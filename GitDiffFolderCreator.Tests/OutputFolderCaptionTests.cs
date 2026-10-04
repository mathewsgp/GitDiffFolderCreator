using System;
using System.IO;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// The OUTPUT card's Open button opens two different folders: the configured output directory before
/// anything has run, and the folder the last export produced afterwards.
/// </summary>
/// <remarks>
/// One caption for both meant the reader had to work out which from whether the run log had anything in
/// it. The caption therefore names the folder, and this drives a real export to prove it changes - the
/// interesting half, since the change happens when the run finishes and not when the window opens.
/// </remarks>
public sealed class OutputFolderCaptionTests
{
    /// <summary>
    /// Before a run, the button opens the output folder; after one, it opens the result. Checked as the
    /// caption and the tooltip, because the two are read together and a caption that changed alone would
    /// leave the tooltip describing the wrong folder.
    /// </summary>
    [Fact]
    public void The_open_button_names_the_folder_it_will_open()
    {
        using TempRepository repo = RepositoryWithOneChangedFile();
        using var output = new TempDirectory();

        Run(viewModel =>
        {
            // Nothing has run, so there is no result to open and the caption says so.
            Assert.Equal("Open output folder", viewModel.OpenOutputCaption);
            Assert.Contains("No export has run yet", viewModel.OpenOutputTooltip, StringComparison.Ordinal);

            viewModel.GitDirectory = repo.Root;
            WaitFor(() => viewModel.Commits.Count > 0, "the commits");

            foreach (GitCommit commit in viewModel.Commits)
            {
                viewModel.SelectedCommits.Add(commit);
            }

            WaitFor(() => viewModel.Changes.Count > 0, "the comparison");

            viewModel.OutputDirectory = output.Path;
            WaitFor(() => viewModel.CanCreateFolders, "the export to become available");

            viewModel.CreateFoldersCommand.Execute(null);
            WaitFor(
                () => viewModel.OpenOutputCaption == "Open last result",
                "the export to finish and the caption to change");

            Assert.Contains("last export", viewModel.OpenOutputTooltip, StringComparison.Ordinal);

            // The run folder is where the files went, and it is a folder that exists: a caption
            // promising a result that is not there would be worse than no caption.
            Assert.NotEmpty(viewModel.OutputLog);
            Assert.Contains(
                Path.GetFileName(output.Path),
                viewModel.OutputLog,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>A repository holding two commits and one file changed between them.</summary>
    private static TempRepository RepositoryWithOneChangedFile()
    {
        TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.cs", "class A { }");
        repo.Commit("first");

        repo.WriteFile("a.cs", "class A { int id; }");
        repo.Commit("second");

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
