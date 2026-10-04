using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// Switching repositories must never leave the previous repository's data on screen.
/// </summary>
public sealed class RepositorySwitchTests
{
    [Fact]
    public void The_branch_list_belongs_to_the_repository_that_is_open_now()
    {
        using TempRepository first = RepositoryWithBranches("alpha-only", "alpha-second");
        using TempRepository second = RepositoryWithBranches("beta-only");

        Run(viewModel =>
        {
            viewModel.GitDirectory = first.Root;
            WaitFor(() => viewModel.Branches.Count > 0, "the first repository's branches");

            Assert.Contains("alpha-only", viewModel.Branches.Select(b => b.Name));
            Assert.DoesNotContain("beta-only", viewModel.Branches.Select(b => b.Name));

            viewModel.GitDirectory = second.Root;
            WaitFor(() => viewModel.Branches.Count > 0, "the second repository's branches");

            List<string> names = viewModel.Branches.Select(b => b.Name).ToList();

            Assert.Contains("beta-only", names);
            Assert.DoesNotContain("alpha-only", names);
            Assert.DoesNotContain("alpha-second", names);
        });
    }

    /// <summary>
    /// The picker must be emptied the moment the path changes, not only once the new repository has
    /// finished loading, or it offers branches of a repository that is no longer open.
    /// </summary>
    [Fact]
    public void The_previous_branches_disappear_before_the_new_repository_loads()
    {
        using TempRepository first = RepositoryWithBranches("alpha-only");
        using TempRepository second = RepositoryWithBranches("beta-only");

        Run(viewModel =>
        {
            viewModel.GitDirectory = first.Root;
            WaitFor(() => viewModel.Branches.Count > 0, "the first repository's branches");

            // A path that is not a repository at all: nothing will ever arrive to replace the list,
            // which is exactly the case that used to leave the old branches on screen.
            viewModel.GitDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gdfc-not-a-repo-" + Guid.NewGuid().ToString("N"));

            Assert.Empty(viewModel.Branches);
            Assert.False(viewModel.HasBranches);

            WaitFor(
                () => viewModel.Status.Contains("not an existing directory"),
                "the invalid path to be reported");

            Assert.Empty(viewModel.Branches);
        });
    }

    [Fact]
    public void Switching_repositories_clears_the_commits_and_files_too()
    {
        using TempRepository first = RepositoryWithBranches("alpha-only");
        using TempRepository second = RepositoryWithBranches("beta-only");

        Run(viewModel =>
        {
            viewModel.GitDirectory = first.Root;
            WaitFor(() => viewModel.Commits.Count > 0, "the first repository's commits");

            viewModel.GitDirectory = second.Root;

            // Cleared synchronously by the path change, before any git call for the new repository.
            Assert.Empty(viewModel.Commits);
            Assert.Empty(viewModel.Changes);
            Assert.Empty(viewModel.Branches);
            Assert.Null(viewModel.SelectedBranch);
            Assert.True(viewModel.IsShowingHead);
        });
    }

    /// <summary>
    /// A branch chosen from the old repository must not stay selected, or the log would keep naming a
    /// ref that does not exist in the new one.
    /// </summary>
    [Fact]
    public void A_branch_chosen_from_the_previous_repository_is_forgotten()
    {
        using TempRepository first = RepositoryWithBranches("alpha-only");
        using TempRepository second = RepositoryWithBranches("beta-only");

        Run(viewModel =>
        {
            viewModel.GitDirectory = first.Root;
            WaitFor(() => viewModel.Branches.Count > 0, "the first repository's branches");

            viewModel.SelectedBranch = viewModel.Branches.First(b => b.Name == "alpha-only");
            Assert.False(viewModel.IsShowingHead);

            viewModel.GitDirectory = second.Root;

            Assert.Null(viewModel.SelectedBranch);
            Assert.Null(viewModel.PendingBranch);
            Assert.True(viewModel.IsShowingHead);
            Assert.Null(viewModel.ActiveRef);
        });
    }

    /// <summary>
    /// Opening a repository updates its remote-tracking refs, so a branch pushed by someone else
    /// appears without the user fetching by hand first.
    /// </summary>
    [Fact]
    public void Opening_a_repository_fetches_a_branch_that_was_pushed_elsewhere()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "content");
        repo.Commit("first");
        repo.CreateRemote();

        // Pushed from a clone, so this repository's refs do not know about it yet.
        repo.PushNewBranchFromClone("pushed-elsewhere");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => viewModel.Branches.Count > 0, "the fetched branches");

            List<string> names = viewModel.Branches.Select(b => b.Name).ToList();

            // This is the whole point of fetching: the branch exists on the remote and is listed
            // because the application asked the remote for it.
            Assert.Contains("origin/pushed-elsewhere", names);
        });
    }

    /// <summary>A local-only repository must not report a fetch failure; there is nothing to fetch.</summary>
    [Fact]
    public void A_repository_with_no_remote_loads_without_complaint()
    {
        using TempRepository repo = RepositoryWithBranches("local-only");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;
            WaitFor(() => viewModel.Branches.Count > 0, "the local branches");

            Assert.Contains("local-only", viewModel.Branches.Select(b => b.Name));

            // Having no remote is ordinary, so it must not be reported as a failure.
            Assert.DoesNotContain("not an existing directory", viewModel.Status);
            Assert.False(viewModel.IsStatusError);
        });
    }

    /// <summary>
    /// WPF asks a command whether it may run once, then keeps that answer until the command raises
    /// CanExecuteChanged. Without that notification the branch badge stays disabled forever, because
    /// the first question is always asked while the branch list is still empty.
    /// </summary>
    [Fact]
    public void Loading_branches_tells_the_picker_command_it_may_now_run()
    {
        using TempRepository repo = RepositoryWithBranches("has-branches");

        Run(viewModel =>
        {
            int raised = 0;
            viewModel.ToggleBranchListCommand.CanExecuteChanged += (_, __) => raised++;

            viewModel.GitDirectory = repo.Root;
            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            // And the other way round: emptying the list must disable it again.
            int before = raised;
            viewModel.GitDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "gdfc-not-a-repo-" + Guid.NewGuid().ToString("N"));

            Assert.True(raised > before, "Clearing the branches did not notify the picker command.");
            Assert.False(viewModel.ToggleBranchListCommand.CanExecute(null));
        });
    }

    /// <summary>
    /// The first load of a repository is several git processes, including a network fetch, so the log
    /// has to say it is working and then say it has stopped. A ring left spinning, or a stage message
    /// left on screen, both read as a hang.
    /// </summary>
    [Fact]
    public void Loading_the_log_shows_progress_and_clears_it_afterwards()
    {
        using TempRepository repo = RepositoryWithBranches("has-branches");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;

            // The indicator has to go up and come back down within the load, not merely end up false.
            bool sawLoading = false;
            bool sawMessage = false;

            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(viewModel.IsLoadingLog) && viewModel.IsLoadingLog)
                {
                    sawLoading = true;
                }

                if (args.PropertyName == nameof(viewModel.LoadingMessage)
                    && !string.IsNullOrEmpty(viewModel.LoadingMessage))
                {
                    sawMessage = true;
                }
            };

            WaitFor(() => !viewModel.IsBusy && viewModel.Branches.Count > 0, "the branches");

            Assert.True(sawLoading, "The log never reported that it was loading.");
            Assert.True(sawMessage, "The log never reported which stage it was on.");

            // Settled: the ring is gone and no stage text is left behind.
            Assert.False(viewModel.IsLoadingLog);
            Assert.Equal(string.Empty, viewModel.LoadingMessage);
            Assert.NotEmpty(viewModel.Commits);
        });
    }

    /// <summary>
    /// A refresh that cancels another one and then finds the path invalid must still finish the job.
    /// </summary>
    /// <remarks>
    /// The cancelled load cannot clear the loading state - the newer refresh owns it - and the invalid
    /// refresh used to return before it had set anything, so between them nothing cleared it. The window
    /// was left spinning on a load that had ended, which also leaves every command that waits on
    /// <c>IsBusy</c> disabled.
    /// <para>
    /// Arranged by watching for the first load to actually start, rather than by typing straight into
    /// the box: typing during the debounce cancels a refresh that never claimed to be loading, and this
    /// test would pass against the broken code.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_path_that_stops_being_a_folder_mid_load_does_not_leave_the_window_busy()
    {
        using TempRepository repo = RepositoryWithBranches("has-branches");

        Run(viewModel =>
        {
            viewModel.GitDirectory = repo.Root;

            // Until it has reported itself busy, nothing is in flight to cancel.
            WaitFor(() => viewModel.IsBusy || viewModel.IsLoadingLog, "the load to start");

            viewModel.GitDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "gdfc-not-a-repo-" + Guid.NewGuid().ToString("N"));

            // Waited for both at once: the status is written on the way in and the loading state is
            // cleared on the way out, so asking for either alone can land between the two.
            WaitFor(
                () => !viewModel.IsBusy && viewModel.Status.Contains("not an existing directory"),
                "the invalid path to be reported and the load to finish");

            // Settled, not stuck: nothing is loading, nothing claims to be, and the commands that wait
            // on that are live again.
            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.IsLoadingLog);
            Assert.Equal(string.Empty, viewModel.LoadingMessage);
            Assert.True(viewModel.RefreshLogCommand.CanExecute(null));
        });
    }

    /// <summary>Builds a repository holding one commit and the named branches pointing at it.</summary>
    private static TempRepository RepositoryWithBranches(params string[] branchNames)
    {
        TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "content");
        string commit = repo.Commit("first");
        repo.Git("checkout", "-q", "-b", branchNames[0]);

        foreach (string name in branchNames.Skip(1))
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

        var thread = new Thread(() =>
        {
            try
            {
                // A settings file per test, so one test's repository cannot leak into the next.
                string settingsPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "gdfc-settings-" + Guid.NewGuid().ToString("N") + ".json");

                var viewModel = new GitViewModel(
                    new AppSettingsStore(settingsPath),
                    pickFolder: (input, title) => null,
                    dispatcher: Dispatcher.CurrentDispatcher,
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

        thread.SetApartmentState(ApartmentState.STA);
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
        Dispatcher? dispatcher = Dispatcher.CurrentDispatcher;

        for (int i = 0; i < 1200; i++)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            if (condition())
            {
                return;
            }

            Thread.Sleep(25);
        }

        throw new InvalidOperationException("Timed out waiting for " + what + ".");
    }
}