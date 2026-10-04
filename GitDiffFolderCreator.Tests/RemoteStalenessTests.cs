using System;
using System.IO;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// A branch list that could not be refreshed looks exactly like one that could, so the user has to be
/// told - and the two reasons it can happen are not the same thing.
/// </summary>
/// <remarks>
/// A repository with no remote has nothing to be stale about: its branch list is the whole truth.
/// A repository whose remote could not be reached is showing branches as of the last fetch that
/// worked, which is worth saying, because a branch somebody pushed an hour ago is missing from a list
/// that looks authoritative.
/// </remarks>
public sealed class RemoteStalenessTests
{
    /// <summary>
    /// No remote, so no fetch, and nothing to warn about. Raising the warning here would put a
    /// permanent notice in front of every local-only repository over a condition that cannot affect
    /// the answer.
    /// </summary>
    [Fact]
    public void A_repository_with_no_remote_does_not_warn_about_its_branches()
    {
        using TempRepository repo = RepositoryWithOneCommit();

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.HasActiveBranch, "the branches");

            Assert.False(viewModel.BranchesMayBeStale);
            Assert.Equal(string.Empty, viewModel.BranchStaleNotice);
            Assert.False(viewModel.RetryRemoteCommand.CanExecute(null));

            // And nothing said about the remote in the load summary either, which is where the same
            // fact has to hold for a reader who never opens the picker.
            Assert.DoesNotContain("remote", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// A remote that cannot be reached leaves the branch list behind, and the notice says so in words
    /// rather than as a badge the reader has to interpret.
    /// </summary>
    [Fact]
    public void A_remote_that_cannot_be_reached_is_reported_rather_than_left_to_look_complete()
    {
        using TempRepository repo = RepositoryWithOneCommit();
        repo.Git("remote", "add", "origin", Path.Combine(repo.Root, "not-a-repository"));

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.HasActiveBranch, "the branches");
            WaitFor(() => viewModel.BranchesMayBeStale, "the remote warning");

            Assert.Contains("remote", viewModel.BranchStaleNotice, StringComparison.OrdinalIgnoreCase);
            Assert.True(viewModel.RetryRemoteCommand.CanExecute(null));

            // The branch list is still shown: it is the right list, just not a current one.
            Assert.NotEmpty(viewModel.Branches);

            // Said on the badge too, because every number it shows is measured against an upstream ref
            // that may itself be out of date.
            Assert.Contains(
                viewModel.BranchStaleNotice,
                viewModel.ActiveBranchTooltip,
                StringComparison.Ordinal);

            Assert.Contains("could not reach the remote", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// The retry has to actually retry. The repository is remembered as fetched so that typing in the
    /// path does not refetch on every keystroke, which means a button that only cleared the warning
    /// would look like it worked and leave the branches exactly as stale.
    /// </summary>
    [Fact]
    public void Retrying_after_the_remote_comes_back_clears_the_warning()
    {
        using TempRepository repo = RepositoryWithOneCommit();
        repo.Git("remote", "add", "origin", Path.Combine(repo.Root, "not-a-repository"));

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.HasActiveBranch, "the branches");
            WaitFor(() => viewModel.BranchesMayBeStale, "the remote warning");

            // Point the remote at a real repository, then press the button the notice offers. An empty
            // bare one will do: fetching from it succeeds, which is all this needs.
            repo.Git("init", "-q", "--bare", "upstream.git");
            repo.Git("remote", "set-url", "origin", Path.Combine(repo.Root, "upstream.git"));

            viewModel.RetryRemoteCommand.Execute(null);

            // Waited for on the summary rather than on the warning: pressing the button clears the
            // warning at once, before the fetch has said anything, so the summary is what proves the
            // retry actually ran.
            WaitFor(
                () => viewModel.Status.IndexOf("branches updated", StringComparison.OrdinalIgnoreCase) >= 0,
                "the retry to fetch from the remote");

            Assert.False(viewModel.BranchesMayBeStale);
            Assert.Equal(string.Empty, viewModel.BranchStaleNotice);
            Assert.False(viewModel.RetryRemoteCommand.CanExecute(null));
        });
    }

    /// <summary>A repository with one commit and no remote at all.</summary>
    private static TempRepository RepositoryWithOneCommit()
    {
        TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "content");
        repo.Commit("first");

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
                string settingsPath = Path.Combine(
                    Path.GetTempPath(),
                    "gdfc-settings-" + Guid.NewGuid().ToString("N") + ".json");

                var viewModel = new GitViewModel(
                    new AppSettingsStore(settingsPath),
                    pickFolder: (input, title) => null,
                    dispatcher: System.Windows.Threading.Dispatcher.CurrentDispatcher,
                    openFolder: _ => true);
                body(viewModel);

                try
                {
                    File.Delete(settingsPath);
                }
                catch (IOException)
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
