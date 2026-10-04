using System;
using System.Linq;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// The badge describes the branch on screen rather than whatever happens to be checked out, and the
/// picker can narrow a repository's branches down to the one being looked for.
/// </summary>
public sealed class BranchSyncTests
{
    /// <summary>
    /// Picking a branch and picking a different one must move the badge with it, or the warning
    /// describes a branch nobody is reading.
    /// </summary>
    [Fact]
    public void The_badge_follows_the_branch_that_is_on_screen()
    {
        using TempRepository repo = RepositoryWithADivergedAndACleanBranch();

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            // The checked-out branch has fallen behind its upstream, so that is what must warn.
            Assert.Equal("main", viewModel.ActiveBranchName);
            Assert.True(viewModel.IsActiveBranchOutOfSync);
            Assert.False(viewModel.IsActiveBranchInSync);
            Assert.Contains("behind", viewModel.ActiveBranchDetail, StringComparison.OrdinalIgnoreCase);

            viewModel.SelectedBranch = viewModel.Branches.First(b => b.Name == "level-feature");

            // Same working tree, same upstream counts, but the branch on screen is level, so the
            // warning has gone and the quiet badge has taken its place.
            Assert.Equal("level-feature", viewModel.ActiveBranchName);
            Assert.False(viewModel.IsActiveBranchOutOfSync);
            Assert.True(viewModel.IsActiveBranchInSync);
            Assert.Equal(string.Empty, viewModel.ActiveBranchSyncText);
            Assert.Contains("not checked out", viewModel.ActiveBranchDetail, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// A branch with no upstream is neither in sync nor out of sync, so it matches neither of the two
    /// badge rules. The fallback has to cover it, or the name is drawn by nothing at all and the branch
    /// on screen is left unnamed.
    /// </summary>
    [Fact]
    public void A_branch_without_an_upstream_still_shows_its_name()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "content");
        repo.Commit("first");

        // A local branch with no upstream at all: no remote, so nothing is tracking it.
        repo.Git("checkout", "-q", "-b", "lonely");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.HasActiveBranch, "the branches");

            viewModel.SelectedBranch = viewModel.Branches.First(b => b.Name == "lonely");

            GitBranch branch = viewModel.Branches.First(b => b.Name == "lonely");
            Assert.False(branch.HasUpstream);
            Assert.False(branch.IsOutOfSync);

            // The name is what the fallback draws, so it must be both present and non-empty.
            Assert.Equal("lonely", viewModel.ActiveBranchName);
            Assert.True(viewModel.IsActiveBranchUntracked);

            // And the two states that need an upstream must stay out of the way, so exactly one badge
            // is showing.
            Assert.False(viewModel.IsActiveBranchInSync);
            Assert.False(viewModel.IsActiveBranchOutOfSync);
            Assert.Equal(string.Empty, viewModel.ActiveBranchSyncText);
        });
    }

    [Fact]
    public void The_badge_falls_back_to_the_checkout_until_a_branch_is_picked()
    {
        using TempRepository repo = RepositoryWithADivergedAndACleanBranch();

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.HasActiveBranch, "the branches");

            Assert.Null(viewModel.SelectedBranch);
            Assert.True(viewModel.HasActiveBranch);

            viewModel.SelectedBranch = viewModel.Branches.First(b => b.Name == "level-feature");
            viewModel.SelectedBranch = null;

            Assert.True(viewModel.IsShowingHead);
            Assert.Equal("main", viewModel.ActiveBranchName);
        });
    }

    /// <summary>
    /// A repository can hold hundreds of branches. The filter narrows by name so finding one does
    /// not mean scrolling past all of them.
    /// </summary>
    [Fact]
    public void The_filter_narrows_the_branch_list_by_name()
    {
        using TempRepository repo = RepositoryWithBranches(
            "feature/login", "feature/logout", "bugfix/crash", "release/1.0", "main");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            viewModel.BranchFilter = "feature/";
            Assert.Equal(
                new[] { "feature/login", "feature/logout" },
                viewModel.FilteredBranches.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal));

            // Case-insensitive, because nobody remembers the capitalisation of a branch name.
            viewModel.BranchFilter = "BUGFIX";
            Assert.Equal("bugfix/crash", Assert.Single(viewModel.FilteredBranches).Name);

            // Surrounding whitespace from a stray paste must not stop it matching.
            viewModel.BranchFilter = "  release  ";
            Assert.Equal("release/1.0", Assert.Single(viewModel.FilteredBranches).Name);

            // Widening again restores what the filter hid, rather than leaving the list half-built.
            viewModel.BranchFilter = string.Empty;
            Assert.Equal(viewModel.Branches.Count, viewModel.FilteredBranches.Count);
        });
    }

    /// <summary>
    /// An empty list is indistinguishable from a misspelled filter unless the picker says how much of
    /// the list it is showing.
    /// </summary>
    [Fact]
    public void The_filter_reports_how_much_of_the_list_it_is_showing()
    {
        using TempRepository repo = RepositoryWithBranches("feature/login", "feature/logout", "main");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            Assert.Equal("3 branch(es)", viewModel.BranchFilterSummary);

            viewModel.BranchFilter = "feature/";
            Assert.Equal("2 of 3 match", viewModel.BranchFilterSummary);

            // A filter matching nothing is the case that needs saying out loud.
            viewModel.BranchFilter = "no-such-branch";
            Assert.Empty(viewModel.FilteredBranches);
            Assert.Equal("0 of 3 match", viewModel.BranchFilterSummary);
        });
    }

    /// <summary>
    /// A branch the filter has hidden cannot stay highlighted, or the picker would describe a branch
    /// its own list is not showing.
    /// </summary>
    [Fact]
    public void A_highlight_hidden_by_the_filter_is_cleared()
    {
        using TempRepository repo = RepositoryWithBranches("feature/login", "feature/logout", "main");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            viewModel.ToggleBranchListCommand.Execute(null);
            viewModel.PendingBranch = viewModel.Branches.First(b => b.Name == "main");
            Assert.Equal("main", viewModel.PendingBranch?.Name);

            viewModel.BranchFilter = "feature/login";
            Assert.Null(viewModel.PendingBranch);

            // Unhiding the branch does not silently reselect it: that would switch branches behind
            // the user's back with nothing to show for it.
            viewModel.BranchFilter = string.Empty;
            Assert.Null(viewModel.PendingBranch);
        });
    }

    /// <summary>
    /// Opening the picker must not inherit the last filter, or the branch just chosen would look as
    /// though it had vanished from the list.
    /// </summary>
    [Fact]
    public void Opening_the_picker_starts_from_a_clean_filter()
    {
        using TempRepository repo = RepositoryWithBranches("feature/login", "feature/logout", "main");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            viewModel.ToggleBranchListCommand.Execute(null);
            viewModel.BranchFilter = "login";
            Assert.Single(viewModel.FilteredBranches);

            viewModel.CloseBranchList();
            Assert.Equal(string.Empty, viewModel.BranchFilter);

            viewModel.ToggleBranchListCommand.Execute(null);

            // The whole list is back, and the row under the highlight is the branch being read.
            Assert.Equal(viewModel.Branches.Count, viewModel.FilteredBranches.Count);
            Assert.True(viewModel.IsBranchListOpen);
        });
    }

    /// <summary>
    /// The arrows move the highlight through the filtered list and Enter takes it. Navigation alone
    /// must not read the log, or arrowing past a dozen branches would reload it a dozen times.
    /// </summary>
    [Fact]
    public void The_arrow_keys_move_the_highlight_and_enter_takes_it()
    {
        using TempRepository repo = RepositoryWithBranches("aaa-first", "bbb-second", "ccc-third", "ddd-fourth");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            viewModel.ToggleBranchListCommand.Execute(null);

            // The list is ordered by commit date, not alphabetically, so the walk is described against
            // whatever order git reported rather than against the names. Opening the picker highlights
            // the branch being read, so the highlight is cleared to start the walk from a known place.
            string[] order = viewModel.FilteredBranches.Select(b => b.Name).ToArray();
            Assert.True(order.Length >= 4, "Expected at least the four branches named for this test.");

            viewModel.PendingBranch = null;
            viewModel.MoveBranchHighlight(1);

            Assert.Equal(order[0], viewModel.PendingBranch?.Name);

            // Arrowing alone has not chosen anything.
            Assert.Null(viewModel.SelectedBranch);
            Assert.True(viewModel.IsBranchListOpen);

            viewModel.MoveBranchHighlight(1);
            Assert.Equal(order[1], viewModel.PendingBranch?.Name);

            viewModel.MoveBranchHighlight(-1);
            Assert.Equal(order[0], viewModel.PendingBranch?.Name);

            viewModel.ConfirmPendingBranch();

            Assert.Equal(order[0], viewModel.SelectedBranch?.Name);
            Assert.False(viewModel.IsBranchListOpen);
        });
    }

    /// <summary>
    /// From nothing highlighted, an upward press belongs at the bottom of the list rather than
    /// leaving the user with no row selected at all.
    /// </summary>
    [Fact]
    public void Arrowing_up_from_no_highlight_starts_at_the_bottom()
    {
        using TempRepository repo = RepositoryWithBranches("aaa-first", "bbb-second", "ccc-third");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            viewModel.ToggleBranchListCommand.Execute(null);
            viewModel.PendingBranch = null;

            viewModel.MoveBranchHighlight(-1);

            Assert.NotNull(viewModel.PendingBranch);
            Assert.Null(viewModel.SelectedBranch);
        });
    }

    /// <summary>
    /// Arrowing has to walk the filtered list, not the whole one, or the highlight lands on a row
    /// that is not there.
    /// </summary>
    [Fact]
    public void Arrowing_walks_the_filtered_list()
    {
        using TempRepository repo = RepositoryWithBranches("feature/login", "feature/logout", "main");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            viewModel.ToggleBranchListCommand.Execute(null);
            viewModel.BranchFilter = "feature/";

            // Arrowing from the bottom of the filtered list, so the walk starts inside it rather than
            // wherever the highlighted branch happens to sit.
            viewModel.PendingBranch = null;
            viewModel.MoveBranchHighlight(-1);

            Assert.NotNull(viewModel.PendingBranch);
            Assert.Contains(viewModel.PendingBranch!.Name, viewModel.FilteredBranches.Select(b => b.Name));

            // One row past the end of the two matches is not reachable, even though the full list has
            // a third branch below them.
            string last = viewModel.PendingBranch.Name;
            viewModel.MoveBranchHighlight(1);
            Assert.Equal(last, viewModel.PendingBranch?.Name);

            // Arrowing alone has not chosen anything.
            Assert.Null(viewModel.SelectedBranch);
            Assert.True(viewModel.IsBranchListOpen);
        });
    }

    /// <summary>
    /// Enter with the picker shut must do nothing, or a stray keypress would change branches behind
    /// a closed popup.
    /// </summary>
    [Fact]
    public void Enter_does_nothing_when_the_picker_is_closed()
    {
        using TempRepository repo = RepositoryWithBranches("aaa-first", "bbb-second");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            viewModel.PendingBranch = viewModel.Branches.First(b => b.Name == "bbb-second");
            viewModel.ConfirmPendingBranch();

            Assert.Null(viewModel.SelectedBranch);
        });
    }

    /// <summary>
    /// Builds a repository whose checked-out branch has fallen behind its upstream while a second
    /// branch is level with its own.
    /// </summary>
    private static TempRepository RepositoryWithADivergedAndACleanBranch()
    {
        TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "content");
        repo.Commit("first");
        repo.CreateRemote();

        repo.Git("checkout", "-q", "-b", "level-feature");
        repo.Git("push", "-q", "-u", "origin", "level-feature");
        repo.Git("checkout", "-q", "main");

        // A commit the remote gets and this repository does not, so main is behind its upstream.
        repo.AdvanceRemote();

        return repo;
    }

    /// <summary>Builds a repository holding one commit and the named branches pointing at it.</summary>
    private static TempRepository RepositoryWithBranches(params string[] branchNames)
    {
        TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "content");
        string commit = repo.Commit("first");

        // 'main' already exists from the initial commit; naming it again would ask git to create a
        // second branch by that name.
        foreach (string name in branchNames.Where(n => n != "main" && n != "master"))
        {
            repo.Git("branch", name, commit);
        }

        return repo;
    }

    /// <summary>
    /// Drives the view model on its own STA thread, because it posts work to a dispatcher and creates
    /// WPF objects.
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
                    dispatcher: System.Windows.Threading.Dispatcher.CurrentDispatcher,
                    openFolder: _ => true);
                body(viewModel);

                try
                {
                    System.IO.File.Delete(settingsPath);
                }
                catch (System.IO.IOException)
                {
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "The test body did not finish in time.");
        if (failure != null)
        {
            throw failure;
        }
    }

    /// <summary>
    /// Lets the dispatcher run so the background git work can land. The view model's own debounce and
    /// asynchronous reads make this a poll rather than a wait.
    /// </summary>
    private static void WaitFor(Func<bool> condition, string what)
    {
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        for (int i = 0; i < 1200; i++)
        {
            dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            if (condition())
            {
                return;
            }

            System.Threading.Thread.Sleep(25);
        }

        throw new InvalidOperationException("Timed out waiting for " + what + ".");
    }
}
