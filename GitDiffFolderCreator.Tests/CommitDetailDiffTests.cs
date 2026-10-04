using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// Opening one of a commit's files in the diff tool from the details window.
/// </summary>
/// <remarks>
/// The file list in the details window is a summary of one commit, and the main window's list was
/// already double-clickable. These check the two things that make the same gesture work here: that the
/// two sides compared are the commit and its parent — the pair the list itself came from — and that a
/// commit with nothing to compare against says so instead of failing silently.
/// </remarks>
public sealed class CommitDetailDiffTests
{
    /// <summary>
    /// A file in a commit is handed over as the version its parent held and the version the commit
    /// holds.
    /// </summary>
    /// <remarks>
    /// Against the parent rather than the previous commit in the log, because that is the pair
    /// <c>git show</c> read the file list from. A list whose rows are a summary of one pair must not
    /// open a different one.
    /// </remarks>
    [Fact]
    public async Task A_file_in_a_commit_is_shown_against_the_version_its_parent_held()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("Models/Widget.cs", "class Widget { }");
        string older = repo.Commit("first");

        repo.WriteFile("Models/Widget.cs", "class Widget { int id; }");
        repo.WriteFile("Models/Other.cs", "class Other { }");
        string newer = repo.Commit("second");

        string script = InstallRecordingTool(out string recordedBase, out string recordedModified);

        try
        {
            var model = new CommitDetailViewModel(
                new GitService(repo.Root),
                RowFor(newer),
                script);

            await model.LoadAsync(CancellationToken.None);

            Assert.Equal(older, model.BaseHash);
            Assert.True(model.CanShowDifference, model.FileSummary);

            GitFileChange widget = Assert.Single(
                model.Files,
                f => f.Path.EndsWith("Widget.cs", StringComparison.Ordinal));

            model.ShowDifferenceCommand.Execute(widget);

            (string baseFile, string modifiedFile) = await WaitForRecordedPathsAsync(
                recordedBase, recordedModified);

            Assert.True(File.Exists(baseFile), "base file missing at '" + baseFile + "'");
            Assert.True(File.Exists(modifiedFile), "modified file missing at '" + modifiedFile + "'");

            // The parent's content on one side and the commit's on the other, which is the whole claim:
            // a tool shown two copies of the same version would render an empty diff and look like it
            // had worked.
            Assert.Equal("class Widget { }", File.ReadAllText(baseFile));
            Assert.Equal("class Widget { int id; }", File.ReadAllText(modifiedFile));

            Assert.False(model.DiffMessageIsError);
            Assert.True(model.HasDiffMessage);
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedBase);
            TryDelete(recordedModified);
        }
    }

    /// <summary>
    /// The tool chosen in the main window's picker is the one used here.
    /// </summary>
    /// <remarks>
    /// Otherwise a user who picked a tool would get a different one depending on which window they
    /// double-clicked in, which is not a choice anybody made.
    /// </remarks>
    [Fact]
    public async Task The_tool_chosen_in_the_main_window_is_the_one_that_is_launched()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "one");
        repo.Commit("first");
        repo.WriteFile("a.txt", "two");
        string newer = repo.Commit("second");

        string script = InstallRecordingTool(out string recordedBase, out string recordedModified);

        try
        {
            var model = new CommitDetailViewModel(new GitService(repo.Root), RowFor(newer), script);

            await model.LoadAsync(CancellationToken.None);

            model.ShowDifferenceCommand.Execute(Assert.Single(model.Files));

            await WaitForRecordedPathsAsync(recordedBase, recordedModified);

            Assert.False(model.DiffMessageIsError, model.DiffMessage);
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedBase);
            TryDelete(recordedModified);
        }
    }

    /// <summary>
    /// A root commit has nothing to compare against, and the gesture is refused rather than left to
    /// fail.
    /// </summary>
    /// <remarks>
    /// Its file list is a list of files that exist, not of changes between two commits, so there is no
    /// second version to show. Saying so is the honest answer; launching a tool on one real file and
    /// one empty one would invent a difference that is not there.
    /// </remarks>
    [Fact]
    public async Task A_root_commit_offers_no_diff_because_it_has_nothing_to_compare_against()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "the only content");
        string root = repo.Commit("first");

        var model = new CommitDetailViewModel(new GitService(repo.Root), RowFor(root));

        await model.LoadAsync(CancellationToken.None);

        Assert.Single(model.Files);
        Assert.Equal(string.Empty, model.BaseHash);
        Assert.False(model.CanShowDifference);
        Assert.False(model.ShowDifferenceCommand.CanExecute(model.Files[0]));

        // And asking anyway does nothing at all, rather than reaching the launcher with no base.
        model.ShowDifferenceFor(model.Files[0]);

        Assert.False(model.HasDiffMessage);
    }

    /// <summary>
    /// The gesture is refused while the commit is still being read.
    /// </summary>
    /// <remarks>
    /// The parents arrive with the read, so until it lands there is no second commit to compare
    /// against. Enabling the command on the seeded row state would offer a diff of nothing.
    /// </remarks>
    [Fact]
    public async Task The_gesture_is_refused_before_the_commit_has_been_read()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "one");
        repo.Commit("first");
        repo.WriteFile("a.txt", "two");
        string newer = repo.Commit("second");

        var model = new CommitDetailViewModel(new GitService(repo.Root), RowFor(newer));

        Assert.False(model.CanShowDifference);
        Assert.False(model.ShowDifferenceCommand.CanExecute(null));

        await model.LoadAsync(CancellationToken.None);

        Assert.True(model.CanShowDifference);
    }

    /// <summary>
    /// A launch that finds no tool is reported in the window, not swallowed.
    /// </summary>
    /// <remarks>
    /// The window already reports its own read failures, and a diff tool that is missing or
    /// misconfigured is exactly as much a failure — but without this the double-click would look
    /// identical to one that never registered at all.
    /// </remarks>
    [Fact]
    public async Task A_launch_with_no_tool_configured_is_reported_in_the_window()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "one");
        repo.Commit("first");
        repo.WriteFile("a.txt", "two");
        string newer = repo.Commit("second");

        // Nothing configured on the command line and none in the repository, so the launcher's own
        // message is what reaches the window.
        var model = new CommitDetailViewModel(new GitService(repo.Root), RowFor(newer));

        await model.LoadAsync(CancellationToken.None);

        model.ShowDifferenceCommand.Execute(Assert.Single(model.Files));

        await WaitUntilAsync(() => model.HasDiffMessage);

        Assert.True(model.DiffMessageIsError);
        Assert.Contains("diff tool", model.DiffMessage!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The command tells the list when it may act, rather than the list asking once and being told no.
    /// </summary>
    [Fact]
    public async Task The_command_announces_that_it_became_available()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "one");
        repo.Commit("first");
        repo.WriteFile("a.txt", "two");
        string newer = repo.Commit("second");

        var model = new CommitDetailViewModel(new GitService(repo.Root), RowFor(newer));

        int announcements = 0;
        model.ShowDifferenceCommand.CanExecuteChanged += (_, _) => announcements++;

        await model.LoadAsync(CancellationToken.None);

        // Bound to a list, so the list asks only when told. A command whose answer changes silently
        // leaves the gesture dead for good.
        Assert.True(announcements > 0);
        Assert.True(model.ShowDifferenceCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------ helpers

    private static GitCommit RowFor(string hash) =>
        new()
        {
            Hash = hash,
            ShortHash = hash.Substring(0, 7),
            Message = "from the log row",
        };

    /// <summary>
    /// A diff tool that records the two paths it was given, so the test can read back what was
    /// handed over without a real comparison tool being installed.
    /// </summary>
    /// <remarks>
    /// A batch file rather than a diff viewer: the point is the two file arguments, not whether
    /// something renders them. Written to the temporary folder rather than into the repository, because
    /// the repository path in one launcher test is deliberately awkward and the batch file could not
    /// redirect to a path containing its own metacharacters.
    /// </remarks>
    private static string InstallRecordingTool(
        out string recordBase,
        out string recordModified)
    {
        string script = Path.Combine(Path.GetTempPath(), $"gdfc-detail-tool-{Guid.NewGuid():N}.cmd");
        recordBase = Path.Combine(Path.GetTempPath(), $"gdfc-detail-{Guid.NewGuid():N}.base");
        recordModified = Path.Combine(Path.GetTempPath(), $"gdfc-detail-{Guid.NewGuid():N}.modified");

        File.WriteAllText(
            script,
            string.Format(
                CultureInfo.InvariantCulture,
                "@echo off\r\necho \"%~1\" > \"{0}\"\r\necho \"%~2\" > \"{1}\"\r\n",
                recordBase,
                recordModified),
            new UTF8Encoding(false));

        return script;
    }

    /// <summary>
    /// The two file paths the tool was given, copied out by the recording tool.
    /// </summary>
    /// <remarks>
    /// Polled for rather than read straight away: the tool is started without being waited for, so its
    /// output lands after the launch returns. <c>%~1</c> strips the quotes the launcher added, so each
    /// path arrives whole whether or not it needed them.
    /// </remarks>
    private static async Task<(string BaseFile, string ModifiedFile)> WaitForRecordedPathsAsync(
        string recordBase,
        string recordModified)
    {
        await WaitUntilAsync(() => File.Exists(recordBase) && File.Exists(recordModified));

        return (Unquote(File.ReadAllText(recordBase)),
                Unquote(File.ReadAllText(recordModified)));
    }

    private static string Unquote(string recorded)
    {
        string trimmed = recorded.Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"'
            ? trimmed.Substring(1, trimmed.Length - 2)
            : trimmed;
    }

    private static async Task WaitUntilAsync(Func<bool> until)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (until())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The recording tool never wrote what it was given.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover in the temporary folder is not worth failing a test over.
        }
    }
}
