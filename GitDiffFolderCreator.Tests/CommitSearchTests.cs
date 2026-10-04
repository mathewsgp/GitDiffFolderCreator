using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// Searching the commit log.
/// </summary>
/// <remarks>
/// The log can hold hundreds of commits, and scrolling is how you find one only if you already know
/// roughly where it is. These check the three things that make a search usable rather than merely
/// present: it matches the fields a reader actually searches by, it says how much it hid, and it cannot
/// leave the window describing commits the list is no longer showing.
/// </remarks>
public class CommitSearchTests
{
    /// <summary>
    /// A search matches what a reader remembers about a commit, which is rarely only its subject.
    /// </summary>
    /// <remarks>
    /// The hash is there because a hash is what gets pasted in from a review or a chat, the author
    /// because a busy log is often scanned by who wrote it, and the refs because "which commit did I put
    /// on the release branch" is a question about the chip on the row. Case-insensitive throughout,
    /// because nobody remembers the capitalisation of a hash.
    /// </remarks>
    [Fact]
    public void The_search_matches_the_message_the_hash_the_author_and_the_refs()
    {
        Run(viewModel =>
        {
            Seed(viewModel);

            // Only one commit is about the retry budget, which is what makes this a search on the
            // message rather than on a word that happens to be common.
            Assert.Equal("Fix the retry budget in the diff loader", Assert.Single(Messages(viewModel, "retry")));

            Assert.Contains("Add a search box to the log", Messages(viewModel, "search"));

            // By the long hash and by the short one, because either may be the one to hand.
            Assert.Single(Matches(viewModel, "aaaaaaa1"));
            Assert.Single(Matches(viewModel, "AAAAAAA1"));
            Assert.Single(Matches(viewModel, "0000002"));
            // Two of the three are by the same hand, which is what makes this a search on the author
            // rather than on the one row that happens to name them.
            Assert.Equal(2, Matches(viewModel, "A Developer").Count);

            Assert.Single(Matches(viewModel, "release"));

            // Surrounding whitespace from a stray paste must not stop it matching.
            Assert.Single(Matches(viewModel, "  Add  "));
        });
    }

    /// <summary>
    /// A term has to sit inside one field, or a search for a hash matches every commit that mentions it.
    /// </summary>
    [Fact]
    public void A_term_does_not_match_across_the_row()
    {
        Run(viewModel =>
        {
            Seed(viewModel);

            // "Fix the" spans the message and nothing else, so it matches - but a term spanning two
            // fields does not, because no single field carries it.
            Assert.NotEmpty(Matches(viewModel, "Fix the"));

            // The message does not contain the author's name, so the two terms together cannot both sit
            // in the one field.
            Assert.Empty(Matches(viewModel, "Fix the A Developer"));
        });
    }

    /// <summary>
    /// Widening the search restores what it hid rather than leaving the list half-built.
    /// </summary>
    [Fact]
    public void Clearing_the_search_brings_every_commit_back()
    {
        Run(viewModel =>
        {
            Seed(viewModel);

            viewModel.CommitFilter = "retry";
            Assert.Single(viewModel.FilteredCommits);

            viewModel.CommitFilter = string.Empty;

            Assert.Equal(viewModel.Commits.Count, viewModel.FilteredCommits.Count);
        });
    }

    /// <summary>
    /// An empty list is indistinguishable from an empty repository unless the log says how much it hid.
    /// </summary>
    [Fact]
    public void The_search_reports_how_much_of_the_log_it_is_showing()
    {
        Run(viewModel =>
        {
            Seed(viewModel);

            Assert.Equal("3 commit(s)", viewModel.CommitFilterSummary);

            viewModel.CommitFilter = "retry";
            Assert.Equal("1 of 3 commits match", viewModel.CommitFilterSummary);

            // Matching nothing is the case that needs saying out loud.
            viewModel.CommitFilter = "no-such-commit";
            Assert.Empty(viewModel.FilteredCommits);
            Assert.Equal("0 of 3 commits match", viewModel.CommitFilterSummary);
        });
    }

    /// <summary>
    /// A commit the search has hidden cannot stay selected.
    /// </summary>
    /// <remarks>
    /// Two commits are chosen to compare, so a selection left pointing at a commit that is no longer
    /// listed would compare something the user can no longer see and cannot re-pick. The two the search
    /// still shows are kept, because the pair is only meaningful while both halves of it are visible.
    /// </remarks>
    [Fact]
    public void A_commit_the_search_hides_is_dropped_from_the_selection()
    {
        Run(viewModel =>
        {
            Seed(viewModel);

            GitCommit kept = viewModel.Commits[0];
            GitCommit hidden = viewModel.Commits[1];

            viewModel.SelectedCommits.Add(kept);
            viewModel.SelectedCommits.Add(hidden);

            // Narrowed to the kept commit's own subject, so one half of the pair survives the search and
            // one does not.
            viewModel.CommitFilter = "Fix the retry budget";

            Assert.Equal("Fix the retry budget in the diff loader", Assert.Single(viewModel.FilteredCommits).Message);
            Assert.Equal(
                "Fix the retry budget in the diff loader",
                Assert.Single(viewModel.SelectedCommits).Message);
            Assert.DoesNotContain(hidden, viewModel.SelectedCommits);
            Assert.Contains(kept, viewModel.SelectedCommits);
        });
    }

    /// <summary>
    /// A search left over from the last repository would hide the commits of this one.
    /// </summary>
    /// <remarks>
    /// Against real repositories rather than a seeded list, because the clearing happens on the way
    /// into a repository and the interesting part is the change of directory, not the absence of one.
    /// </remarks>
    [Fact]
    public void A_search_is_cleared_when_the_repository_is()
    {
        using TempRepository first = TempRepository.Create();
        first.WriteFile("a.txt", "content");
        first.Commit("the first commit");

        using TempRepository second = TempRepository.Create();
        second.WriteFile("b.txt", "content");
        second.Commit("a completely different commit");

        Run(viewModel =>
        {
            viewModel.GitDirectory = first.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Commits.Count > 0, "the first log");

            viewModel.CommitFilter = "first";
            Assert.Single(viewModel.FilteredCommits);

            // A filter naming the previous repository's only commit would hide this one's only commit,
            // and the log would look empty for a reason that has nothing to do with this repository.
            viewModel.GitDirectory = second.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Commits.Count > 0, "the second log");

            Assert.Equal(string.Empty, viewModel.CommitFilter);
            Assert.Equal(viewModel.Commits.Count, viewModel.FilteredCommits.Count);
            Assert.Contains(
                "a completely different commit",
                viewModel.FilteredCommits.Select(c => c.Message));
        });
    }

    /// <summary>The rule, on its own, away from a view model and a window.</summary>
    [Fact]
    public void The_match_rule_is_case_insensitive_and_ignores_empty_fields()
    {
        var commit = new GitCommit
        {
            Message = "Fix the retry budget",
            Hash = "0123456789ABCDEF0123456789abcdef01234567",
            ShortHash = "0123456",
            Author = "A Developer",
            RefNames = string.Empty,
        };

        Assert.True(GitViewModel.CommitMatches(commit, "RETRY"));
        Assert.True(GitViewModel.CommitMatches(commit, "0123456"));
        Assert.True(GitViewModel.CommitMatches(commit, "developer"));

        // The refs are empty, which is most commits: an absent field must not throw or match.
        Assert.False(GitViewModel.CommitMatches(commit, "main"));
        Assert.False(GitViewModel.CommitMatches(commit, "no-such-text"));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Three commits with something to be found by each of the searchable fields.</summary>
    private static void Seed(GitViewModel viewModel)
    {
        viewModel.Commits.Add(new GitCommit
        {
            Hash = "1111111111111111111111111111111111111111",
            ShortHash = "aaaaaaa1",
            Message = "Fix the retry budget in the diff loader",
            Author = "A Developer",
            RefNames = "HEAD -> main",
        });

        viewModel.Commits.Add(new GitCommit
        {
            Hash = "2222222222222222222222222222222222222222",
            ShortHash = "bbbbbbb2",
            Message = "Add a search box to the log",
            Author = "A Developer",
            RefNames = string.Empty,
        });

        viewModel.Commits.Add(new GitCommit
        {
            Hash = "3333333333333333333333333333333333333333",
            ShortHash = "0000002",
            Message = "Bump the pinned package version",
            Author = "Someone Else",
            RefNames = "tag: release",
        });

        // Applied through the setter rather than by adding to the filtered list, because that is the
        // only route the loaded-log path takes.
        viewModel.CommitFilter = "x";
        viewModel.CommitFilter = string.Empty;
    }

    private static List<GitCommit> Matches(GitViewModel viewModel, string needle)
    {
        viewModel.CommitFilter = needle;

        return viewModel.FilteredCommits.ToList();
    }

    private static List<string> Messages(GitViewModel viewModel, string needle) =>
        Matches(viewModel, needle).Select(c => c.Message).ToList();

    /// <summary>
    /// Lets the dispatcher run so the background git work can land, and gives up saying what was
    /// still missing.
    /// </summary>
    /// <remarks>
    /// A poll through the dispatcher rather than a sleep. The view model posts its work back to the
    /// dispatcher this thread owns, so blocking the thread would stop the very thing being waited for.
    /// </remarks>
    private static void WaitFor(Func<bool> until, string what)
    {
        System.Windows.Threading.Dispatcher? dispatcher =
            System.Windows.Threading.Dispatcher.CurrentDispatcher;

        for (int attempt = 0; attempt < 1200; attempt++)
        {
            dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            if (until())
            {
                return;
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException("Timed out waiting for " + what + ".");
    }

    /// <summary>
    /// On the shared STA host, like every other view model test here: WPF refuses to construct anything
    /// UI on a thread-pool thread, and this one is built against a dispatcher.
    /// </summary>
    private static void Run(Action<GitViewModel> body)
    {
        Exception? failure = null;

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                string settingsPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "gdfc-settings-" + Guid.NewGuid().ToString("N") + ".json");

                var viewModel = new GitViewModel(
                    new AppSettingsStore(settingsPath),
                    pickFolder: (input, title) => null,
                    openFolder: _ => true,
                    dispatcher: System.Windows.Threading.Dispatcher.CurrentDispatcher);

                body(viewModel);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
        {
            throw failure;
        }
    }
}
