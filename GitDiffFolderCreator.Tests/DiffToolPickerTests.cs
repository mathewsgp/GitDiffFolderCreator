using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// The diff tool picker: which tools are offered, which one counts as in use, and that a chosen tool
/// is used in preference to whatever git has configured.
/// </summary>
public sealed class DiffToolPickerTests
{
    // ---------------------------------------------------------------- discovery

    [Fact]
    public void A_known_tool_is_found_where_it_is_installed_and_given_its_placeholders()
    {
        // The catalog reads the machine, so this asserts the shape of every entry it returns rather
        // than that any particular tool happens to be installed here - which would fail on most
        // machines and pass on none.
        IList<DiffToolChoice> tools = new DiffToolCatalog().Discover();

        foreach (DiffToolChoice tool in tools)
        {
            Assert.Contains("$LOCAL", tool.Command);
            Assert.Contains("$REMOTE", tool.Command);

            // The path has to be quoted, because every one of these installs under Program Files.
            Assert.StartsWith("\"", tool.Command);
            Assert.Contains(".exe\"", tool.Command);
        }
    }

    /// <summary>
    /// Each installer writes wherever it likes, and the catalog's list of those places is the part
    /// that goes stale: an entry that is not searched produces no tool at all, and no error, so the
    /// tool is simply absent from the picker on the one machine that has it.
    /// </summary>
    /// <remarks>
    /// VS Code is the case that actually happened. Its per-user installer writes under
    /// <c>%LOCALAPPDATA%\Programs</c> rather than straight into <c>%LOCALAPPDATA%</c>, and the
    /// catalog only searched the latter, so a user-installed VS Code — which is how most people have
    /// it — was never found. Asserting against the real machine would not catch that, because the
    /// machine running the test would have to have the tool installed to see it missing.
    /// </remarks>
    [Fact]
    public void A_tool_installed_per_user_is_found_under_the_Program_Folder()
    {
        using TempDirectory programFiles = new();
        using TempDirectory localAppData = new();

        // The machine-wide install: Program Files. The per-user one: LocalApplicationData\Programs.
        CreateFakeExecutable(
            Path.Combine(localAppData.Path, "Programs", "Microsoft VS Code", "Code.exe"));

        IList<DiffToolChoice> tools = new DiffToolCatalog()
            .Discover(new[] { programFiles.Path, localAppData.Path });

        DiffToolChoice vscode = Assert.Single(tools, tool => tool.Id == "vscode");

        Assert.Equal("Visual Studio Code", vscode.DisplayName);

        // And it is runnable: the launcher starts the program directly, so the command has to be the
        // executable itself rather than a shim beside it.
        Assert.StartsWith("\"" + localAppData.Path, vscode.Command);
        Assert.Contains("Code.exe\"", vscode.Command);
        Assert.Contains("--diff", vscode.Command);
        Assert.Contains("--wait", vscode.Command);

        Assert.DoesNotContain(tools, tool => tool.Command.IndexOf(".cmd", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// A machine with both installs finds the machine-wide one, because that is the copy the
    /// machine-wide installer keeps current and the one a user who also has the per-user copy is not
    /// running.
    /// </summary>
    [Fact]
    public void The_machine_wide_install_is_preferred_over_the_per_user_one()
    {
        using TempDirectory programFiles = new();
        using TempDirectory localAppData = new();

        CreateFakeExecutable(
            Path.Combine(programFiles.Path, "Microsoft VS Code", "Code.exe"));

        CreateFakeExecutable(
            Path.Combine(localAppData.Path, "Programs", "Microsoft VS Code", "Code.exe"));

        DiffToolChoice vscode = Assert.Single(
            new DiffToolCatalog().Discover(new[] { programFiles.Path, localAppData.Path }),
            tool => tool.Id == "vscode");

        Assert.StartsWith("\"" + programFiles.Path, vscode.Command);
    }

    /// <summary>
    /// A tool that is not installed contributes nothing, and contributes it silently — the picker
    /// lists what is there, so an absent tool is the ordinary state on a machine without it.
    /// </summary>
    [Fact]
    public void A_tool_that_is_not_installed_is_simply_not_listed()
    {
        using TempDirectory empty = new();

        Assert.Empty(new DiffToolCatalog().Discover(new[] { empty.Path }));
    }

    /// <summary>
    /// Only the file's existence is checked, because nothing is run during discovery; the file needs
    /// no content, and an empty one will do.
    /// </summary>
    private static void CreateFakeExecutable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    [Fact]
    public void A_hand_picked_program_becomes_a_choice_that_carries_its_own_path()
    {
        string executable = @"C:\Program Files\Some Tool\compare.exe";

        DiffToolChoice choice = DiffToolCatalog.ForCustomTool(executable);

        // Named after the executable rather than the folder it sits in: that is what distinguishes
        // one tool from another in a list, and two tools can share an install folder.
        Assert.Equal("compare", choice.DisplayName);
        Assert.Contains(executable, choice.Command);
        Assert.Contains("$LOCAL", choice.Command);
        Assert.Contains("$REMOTE", choice.Command);
    }

    [Fact]
    public void A_tool_with_spaces_in_its_path_is_quoted_rather_than_split()
    {
        DiffToolChoice choice = DiffToolCatalog.ForCustomTool(@"C:\Program Files\Some Tool\compare.exe");

        // The whole path has to survive as one token, so it must be inside one pair of quotes.
        Assert.StartsWith("\"C:\\Program Files\\Some Tool\\compare.exe\"", choice.Command);
    }

    // ---------------------------------------------------------------- picker state

    private static DiffToolPickerViewModel Picker(
        IList<DiffToolChoice> tools,
        string current,
        Func<string?, string?> picker = null!)
    {
        return new DiffToolPickerViewModel(tools, current, picker ?? (_ => null));
    }

    private static DiffToolChoice Choice(string id) =>
        new DiffToolChoice(id, id, "\"" + id + ".exe\" \"$LOCAL\" \"$REMOTE\"", "detail");

    [Fact]
    public void The_tool_already_in_use_is_marked_rather_than_merely_selected()
    {
        DiffToolPickerViewModel model = Picker(
            new[] { Choice("alpha"), Choice("beta") },
            "\"beta.exe\" \"$LOCAL\" \"$REMOTE\"");

        Assert.True(model.Tools.First(t => t.Choice.Id == "beta").IsChosen);
        Assert.False(model.Tools.First(t => t.Choice.Id == "alpha").IsChosen);
        Assert.Same(model.Tools.First(t => t.Choice.Id == "beta"), model.SelectedTool);
    }

    [Fact]
    public void Nothing_is_marked_when_no_tool_has_been_chosen_yet()
    {
        DiffToolPickerViewModel model = Picker(
            new[] { Choice("alpha"), Choice("beta") },
            current: string.Empty);

        Assert.DoesNotContain(model.Tools, t => t.IsChosen);
    }

    [Fact]
    public void Choosing_a_tool_reports_its_command_line_as_the_result()
    {
        DiffToolPickerViewModel model = Picker(new[] { Choice("alpha"), Choice("beta") }, string.Empty);

        model.SelectedTool = model.Tools.First(t => t.Choice.Id == "beta");

        Assert.Contains("beta.exe", model.ResultCommand);
        Assert.Equal(model.ResultCommand, model.CommandPreview);
    }

    [Fact]
    public void OK_is_unavailable_until_a_tool_is_chosen()
    {
        DiffToolPickerViewModel model = Picker(new[] { Choice("alpha") }, string.Empty);

        model.SelectedTool = null;

        Assert.False(model.AcceptCommand.CanExecute(null));
    }

    [Fact]
    public void Choosing_use_gits_clears_the_choice_so_git_is_used_again()
    {
        DiffToolPickerViewModel model = Picker(
            new[] { Choice("alpha") },
            "\"alpha.exe\" \"$LOCAL\" \"$REMOTE\"");

        Assert.NotEmpty(model.ResultCommand);

        model.UseGitConfigCommand.Execute(null);

        // Empty means "no choice made here", which is what sends the launcher back to git's own
        // configuration rather than pinning it to whatever was picked last.
        Assert.Equal(string.Empty, model.ResultCommand);
        Assert.DoesNotContain(model.Tools, t => t.IsChosen);
    }

    [Fact]
    public void A_tool_chosen_by_hand_that_is_no_longer_installed_stays_listed_as_the_one_in_use()
    {
        DiffToolPickerViewModel model = Picker(
            new[] { Choice("alpha") },
            "\"gone.exe\" \"$LOCAL\" \"$REMOTE\"");

        DiffToolRow previous = Assert.Single(model.Tools, t => t.IsChosen);

        // Otherwise the setting would point at a tool that is not in the list, and the user would have
        // no way to see what the window is currently set to.
        Assert.Equal("Previously chosen", previous.DisplayName);
        Assert.Contains("No longer found", previous.Detail);
    }

    [Fact]
    public void Browsing_replaces_an_earlier_hand_picked_tool_rather_than_stacking_them_up()
    {
        // Two different programs, so a second Browse is visibly a replacement rather than the same
        // choice made twice.
        string[] picked = { @"C:\Tools\first.exe", @"C:\Tools\second.exe" };
        int call = 0;

        DiffToolPickerViewModel model = Picker(
            new[] { Choice("alpha") },
            string.Empty,
            _ => picked[call++]);

        model.BrowseCommand.Execute(null);
        model.BrowseCommand.Execute(null);

        List<DiffToolRow> custom = model.Tools
            .Where(t => t.Choice.Id.StartsWith("custom:", StringComparison.Ordinal))
            .ToList();

        // Two browses must leave one hand-picked entry. If they stacked, the list would grow and the
        // window would offer the same tool twice with no way to tell which is in use.
        Assert.Single(custom);
        Assert.Contains("second.exe", custom[0].Command);
        Assert.True(custom[0].IsChosen);
    }

    [Fact]
    public void Browsing_shows_the_list_even_when_nothing_was_discovered()
    {
        DiffToolPickerViewModel model = Picker(
            new List<DiffToolChoice>(),
            string.Empty,
            _ => @"C:\Tools\compare.exe");

        // An empty list with nothing to add would leave a window the user cannot act on.
        Assert.False(model.HasTools);

        model.BrowseCommand.Execute(null);

        Assert.True(model.HasTools);
        Assert.NotNull(model.SelectedTool);
    }

    // ---------------------------------------------------------------- precedence

    [Fact]
    public async Task A_tool_chosen_in_the_picker_wins_over_the_one_git_has_configured()
    {
        using TempRepository repository = TempRepository.Create();
        repository.WriteFile("a.cs", "class A { }");
        string older = repository.Commit("first");
        repository.WriteFile("a.cs", "class A { int id; }");
        string newer = repository.Commit("second");

        // git is pointed at a tool that does not exist, so if the launcher consulted git first the
        // hand-off would fail and the test would say so.
        repository.Git("config", "diff.tool", "definitely-not-a-real-tool-xyz");

        string script = WriteRecordingTool(out string recordedFirst, out string recordedSecond);

        try
        {
            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "a.cs");

            DiffToolLaunchResult result = await new DiffToolLauncher().LaunchAsync(
                git,
                change,
                older,
                newer,
                "\"" + script + "\" \"$LOCAL\" \"$REMOTE\"",
                CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) = WaitForPaths(recordedFirst, recordedSecond);

            Assert.Equal("class A { }", File.ReadAllText(baseFile));
            Assert.Equal("class A { int id; }", File.ReadAllText(modifiedFile));
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst);
            TryDelete(recordedSecond);
        }
    }

    [Fact]
    public async Task An_untouched_picker_setting_still_uses_the_tool_git_has_configured()
    {
        using TempRepository repository = TempRepository.Create();
        repository.WriteFile("a.cs", "class A { }");
        string older = repository.Commit("first");
        repository.WriteFile("a.cs", "class A { int id; }");
        string newer = repository.Commit("second");

        string script = WriteRecordingTool(out string recordedFirst, out string recordedSecond);

        try
        {
            repository.Git("config", "diff.tool", script);

            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "a.cs");

            DiffToolLaunchResult result = await new DiffToolLauncher().LaunchAsync(
                git,
                change,
                older,
                newer,
                configuredCommand: null,
                CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) = WaitForPaths(recordedFirst, recordedSecond);

            Assert.Equal("class A { }", File.ReadAllText(baseFile));
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst);
            TryDelete(recordedSecond);
        }
    }

    [Fact]
    public async Task With_no_tool_chosen_anywhere_the_user_is_told_rather_than_shown_nothing()
    {
        using TempRepository repository = TempRepository.Create();
        repository.WriteFile("a.cs", "class A { }");
        string older = repository.Commit("first");
        repository.WriteFile("a.cs", "class A { int id; }");
        string newer = repository.Commit("second");

        GitService git = new GitService(repository.Root);
        GitFileChange change = await SingleChangeAsync(git, older, newer, "a.cs");

        DiffToolLaunchResult result = await new DiffToolLauncher().LaunchAsync(
            git,
            change,
            older,
            newer,
            configuredCommand: null,
            CancellationToken.None);

        Assert.False(result.Launched);
        Assert.True(result.IsError);
        Assert.Contains("No diff tool is set", result.Message);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void The_chosen_tool_survives_a_settings_round_trip()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"gdfc-settings-{Guid.NewGuid():N}.json");

        try
        {
            AppSettingsStore store = new AppSettingsStore(settingsPath);

            store.Save(new AppSettings { DiffToolCommand = "\"C:\\Tools\\bcomp.exe\" \"$LOCAL\" \"$REMOTE\"" });

            // Compared in full, so this fails if the quotes or the placeholders are lost rather than
            // surviving on the substring alone.
            Assert.Equal(
                "\"C:\\Tools\\bcomp.exe\" \"$LOCAL\" \"$REMOTE\"",
                store.Load().DiffToolCommand);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [Fact]
    public void A_settings_file_written_before_the_picker_existed_reads_as_no_choice()
    {
        // The serializer runs the parameterless constructor, so a missing key has to arrive as the
        // default rather than as null - otherwise every existing user gets a null into the launcher.
        string settingsPath = Path.Combine(Path.GetTempPath(), $"gdfc-settings-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(settingsPath, "{\"gitDirectory\":\"C:\\\\repo\",\"logLimit\":500}");

            AppSettings settings = new AppSettingsStore(settingsPath).Load();

            Assert.Equal(string.Empty, settings.DiffToolCommand);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [Fact]
    public void The_caption_names_the_chosen_tool_and_names_git_when_there_is_none()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"gdfc-settings-{Guid.NewGuid():N}.json");

        try
        {
            AppSettingsStore store = new AppSettingsStore(settingsPath);
            store.Save(new AppSettings { DiffToolCommand = "\"C:\\Program Files\\BC\\BCompare.exe\" \"$LOCAL\"" });

            GitViewModel viewModel = new GitViewModel(
                store,
                pickFolder: (input, title) => null,
                dispatcher: System.Windows.Threading.Dispatcher.CurrentDispatcher,
                openFolder: _ => true);

            Assert.Contains("BCompare", viewModel.DiffToolCaption);
            Assert.DoesNotContain("git's", viewModel.DiffToolCaption);

            viewModel.SetDiffToolCommand(string.Empty);

            Assert.Contains("git's", viewModel.DiffToolCaption);

            // And the clearing has to be written out, not just held in memory.
            Assert.Equal(string.Empty, store.Load().DiffToolCommand);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string WriteRecordingTool(out string recordedFirst, out string recordedSecond)
    {
        string record = Path.Combine(Path.GetTempPath(), $"gdfc-tool-{Guid.NewGuid():N}.txt");
        recordedFirst = record + ".1";
        recordedSecond = record + ".2";

        string script = Path.Combine(Path.GetTempPath(), $"gdfc-tool-{Guid.NewGuid():N}.cmd");

        File.WriteAllText(
            script,
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "@echo off\r\necho \"%~1\" > \"{0}\"\r\necho \"%~2\" > \"{1}\"\r\n",
                recordedFirst,
                recordedSecond),
            new System.Text.UTF8Encoding(false));

        return script;
    }

    private static (string Base, string Modified) WaitForPaths(string first, string second)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(first) && File.Exists(second))
            {
                string a = Unquote(File.ReadAllText(first));
                string b = Unquote(File.ReadAllText(second));

                if (a.Length > 0 && b.Length > 0)
                {
                    return (a, b);
                }
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException("The diff tool never recorded the paths it was given.");
    }

    private static string Unquote(string recorded)
    {
        string trimmed = recorded.Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"'
            ? trimmed.Substring(1, trimmed.Length - 2)
            : trimmed;
    }

    private static async Task<GitFileChange> SingleChangeAsync(
        GitService git,
        string older,
        string newer,
        string expectedPath)
    {
        IList<GitFileChange> changes = await git.GetChangesAsync(older, newer, CancellationToken.None);

        Assert.Single(changes);
        Assert.Equal(expectedPath, changes[0].Path);
        return changes[0];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (path.Length > 0 && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}