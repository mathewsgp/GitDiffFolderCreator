using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// The hand-off to the user's diff tool. What matters is that both versions reach the tool as
/// <em>files</em> holding exactly what git stored, and that the one-sided cases - added, deleted,
/// renamed - still produce two readable files rather than a missing-path error.
/// </summary>
public sealed class DiffToolLauncherTests
{
    /// <summary>
    /// Sets a diff tool that records the paths it was given, so the test can read back what the
    /// launcher actually handed over without a real comparison tool being installed.
    /// </summary>
    /// <remarks>
    /// The tool is a small batch file rather than a diff viewer: the point under test is the two
    /// file arguments, not whether something renders them.
    /// </remarks>
    private static string InstallRecordingTool(
        TempRepository repository,
        out string recordFirst,
        out string recordSecond)
    {
        // The recording files live in the temporary folder rather than in the test repository: the
        // repository path is deliberately awkward in one test, and the batch file would then be
        // asked to redirect to a path whose & and % it cannot carry.
        string recordPath = Path.Combine(Path.GetTempPath(), $"gdfc-tool-{Guid.NewGuid():N}.txt");

        // Written as a .cmd so it can be launched as an ordinary program, but it is started directly
        // rather than through a shell of our own, so no shell quoting rules apply to the paths it
        // receives.
        // <para>
        // Each path is echoed to its own file, and quoted when echoed. The quotes around %~1 strip
        // the ones the launcher added, so the recorded text is a usable path; the quotes around the
        // echo protect the path from the batch file's own parser, which would otherwise read the
        // ampersand and parentheses in a filename such as "a & b (copy) %v%!.cs" as syntax. That is a
        // limitation of this recording shim, not of the launcher, which hands the path to the tool as
        // a single quoted argument - the test that uses such a name is the one that proves it.
        // </para>
        string script = Path.Combine(Path.GetTempPath(), $"gdfc-tool-{Guid.NewGuid():N}.cmd");
        string first = recordPath + ".1";
        string second = recordPath + ".2";

        File.WriteAllText(
            script,
            string.Format(
                CultureInvariant,
                "@echo off\r\necho \"%~1\" > \"{0}\"\r\necho \"%~2\" > \"{1}\"\r\n",
                first,
                second),
            new UTF8Encoding(false));

        repository.Git("config", "diff.tool", script);

        recordFirst = first;
        recordSecond = second;

        return script;
    }

    private static System.Globalization.CultureInfo CultureInvariant =>
        System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// The two file paths the tool was given, copied out by the recording tool.
    /// </summary>
    /// <remarks>
    /// The tool is started without being waited for, so the copies land after the launch returns and
    /// are polled for. <c>%~1</c> strips the quotes the launcher added, so each path arrives whole
    /// whether or not it needed them.
    /// </remarks>
    private static (string Base, string Modified) WaitForRecordedPaths(string first, string second)
    {
        // Generous, because what is being waited for is a batch file starting under whatever load the
        // rest of the suite happens to be applying: it was five seconds, which failed occasionally
        // when the whole suite ran together and passed every time the class ran alone.
for (int attempt = 0; attempt < 600; attempt++)
        {
            if (File.Exists(first) && File.Exists(second))
            {
                try
                {
                    // The shim echoes the path in quotes, both because that is what protects it from
                    // the batch parser and because %~1 needs them present to strip. So the recorded
                    // text carries a surrounding pair and has to be unwrapped before it is a path.
                    string basePath = Unquote(File.ReadAllText(first));
                    string modifiedPath = Unquote(File.ReadAllText(second));

                    if (basePath.Length > 0 && modifiedPath.Length > 0)
                    {
                        return (basePath, modifiedPath);
                    }
                }
                catch (IOException)
                {
                    // The file exists but the writing process still holds it open, which it does for
                    // as long as it takes under load. Existence is therefore not readiness, and
                    // reading it anyway would fail the test for a reason that has nothing to do with
                    // the launcher. Keep waiting.
                }
                catch (UnauthorizedAccessException)
                {
                    // The same race, reported by the file system as access denied rather than as a
                    // sharing violation.
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

    [Fact]
    public async Task A_modified_file_is_handed_over_as_its_two_versions()
    {
        using TempRepository repository = TempRepository.Create();
        repository.WriteFile("Models/Widget.cs", "class Widget { }");
        string older = repository.Commit("first");
        repository.WriteFile("Models/Widget.cs", "class Widget { int id; }");
        string newer = repository.Commit("second");

        string script = InstallRecordingTool(repository, out string recordedFirst, out string recordedSecond);
        try
        {
            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "Models/Widget.cs");

            DiffToolLaunchResult result = await new DiffToolLauncher()
                .LaunchAsync(git, change, older, newer, CancellationToken.None);

            Assert.True(result.Launched, result.Message);
            Assert.False(result.IsError);

            (string baseFile, string modifiedFile) = WaitForRecordedPaths(recordedFirst, recordedSecond);
            Assert.True(File.Exists(baseFile), "base file missing at '" + baseFile + "'");
            Assert.True(File.Exists(modifiedFile), "modified file missing at '" + modifiedFile + "'");
            Assert.Equal("class Widget { }", File.ReadAllText(baseFile));
            Assert.Equal("class Widget { int id; }", File.ReadAllText(modifiedFile));
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst); TryDelete(recordedSecond);
        }
    }

    [Fact]
    public async Task An_added_file_is_compared_against_an_empty_file()
    {
        using TempRepository repository = TempRepository.Create();
        repository.WriteFile("Models/Widget.cs", "class Widget { }");
        string older = repository.Commit("first");
        repository.WriteFile("Models/New.cs", "class New { }");
        string newer = repository.Commit("adds a file");

        string script = InstallRecordingTool(repository, out string recordedFirst, out string recordedSecond);
        try
        {
            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "Models/New.cs");

            Assert.Equal(GitChangeStatus.Added, change.Status);

            DiffToolLaunchResult result = await new DiffToolLauncher()
                .LaunchAsync(git, change, older, newer, CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) = WaitForRecordedPaths(recordedFirst, recordedSecond);

            // The older commit has no such file, so the tool must still be given something to read.
            Assert.Equal(string.Empty, File.ReadAllText(baseFile));
            Assert.Equal("class New { }", File.ReadAllText(modifiedFile));
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst); TryDelete(recordedSecond);
        }
    }

    [Fact]
    public async Task A_deleted_file_is_compared_against_an_empty_file()
    {
        using TempRepository repository = TempRepository.Create();
        repository.WriteFile("Models/Gone.cs", "class Gone { }");
        string older = repository.Commit("first");
        repository.DeleteFile("Models/Gone.cs");
        string newer = repository.Commit("removes a file");

        string script = InstallRecordingTool(repository, out string recordedFirst, out string recordedSecond);
        try
        {
            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "Models/Gone.cs");

            Assert.Equal(GitChangeStatus.Deleted, change.Status);

            DiffToolLaunchResult result = await new DiffToolLauncher()
                .LaunchAsync(git, change, older, newer, CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) = WaitForRecordedPaths(recordedFirst, recordedSecond);
            Assert.Equal("class Gone { }", File.ReadAllText(baseFile));
            Assert.Equal(string.Empty, File.ReadAllText(modifiedFile));
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst); TryDelete(recordedSecond);
        }
    }

    [Fact]
    public async Task A_renamed_file_is_handed_over_under_both_of_its_names()
    {
        using TempRepository repository = TempRepository.Create();
        repository.WriteFile("Models/Old.cs", "class Widget { }");
        string older = repository.Commit("first");
        repository.Move("Models/Old.cs", "Models/New.cs");
        string newer = repository.Commit("renames a file");

        string script = InstallRecordingTool(repository, out string recordedFirst, out string recordedSecond);
        try
        {
            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "Models/New.cs");

            Assert.Equal(GitChangeStatus.Renamed, change.Status);

            DiffToolLaunchResult result = await new DiffToolLauncher()
                .LaunchAsync(git, change, older, newer, CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) = WaitForRecordedPaths(recordedFirst, recordedSecond);

            // Each side is read at the path it had at that commit, not at the newer name for both:
            // asking the older commit for the new path finds nothing.
            Assert.Equal("class Widget { }", File.ReadAllText(baseFile));
            Assert.Contains("Old.cs", baseFile);
            Assert.Contains("New.cs", modifiedFile);
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst); TryDelete(recordedSecond);
        }
    }

    [Fact]
    public async Task A_binary_file_reaches_the_tool_byte_for_byte()
    {
        using TempRepository repository = TempRepository.Create();

        // Bytes that are not valid UTF-8, so any text handling in between would corrupt them.
        byte[] content = { 0x00, 0x01, 0xFF, 0xFE, 0x80, 0x7F, 0x00, 0x42 };

        repository.WriteBinaryFile("Assets/logo.bin", content);
        string older = repository.Commit("first");
        repository.WriteFile("Assets/logo.bin", "replaced with text");
        string newer = repository.Commit("replaces a binary file");

        string script = InstallRecordingTool(repository, out string recordedFirst, out string recordedSecond);
        try
        {
            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "Assets/logo.bin");

            Assert.True(change.IsBinary);

            DiffToolLaunchResult result = await new DiffToolLauncher()
                .LaunchAsync(git, change, older, newer, CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) = WaitForRecordedPaths(recordedFirst, recordedSecond);
            Assert.Equal(content, File.ReadAllBytes(baseFile));
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst); TryDelete(recordedSecond);
        }
    }

    /// <summary>
    /// The ordinary binary case: both versions are binary, which is what a changed image, archive
    /// or database is, and both have to arrive exactly as git stored them. The other binary case -
    /// binary replaced by text - is covered by the test above.
    /// </summary>
    [Fact]
    public async Task Both_sides_of_a_binary_change_reach_the_tool_byte_for_byte()
    {
        using TempRepository repository = TempRepository.Create();

        // Large enough to be split across reads, and holding a byte sequence that no text decoding
        // survives: 0x00, a lone 0x80, and an invalid two-byte sequence.
        byte[] baseContent = CreateUndecodableBytes(64 * 1024, 0x80);
        byte[] modifiedContent = CreateUndecodableBytes(64 * 1024, 0x90);

        repository.WriteBinaryFile("Assets/data.bin", baseContent);
        string older = repository.Commit("first");
        repository.WriteBinaryFile("Assets/data.bin", modifiedContent);
        string newer = repository.Commit("second");

        string script = InstallRecordingTool(repository, out string recordedFirst, out string recordedSecond);
        try
        {
            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "Assets/data.bin");

            DiffToolLaunchResult result = await new DiffToolLauncher()
                .LaunchAsync(git, change, older, newer, CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) = WaitForRecordedPaths(recordedFirst, recordedSecond);
            Assert.Equal(baseContent, File.ReadAllBytes(baseFile));
            Assert.Equal(modifiedContent, File.ReadAllBytes(modifiedFile));
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst); TryDelete(recordedSecond);
        }
    }

    /// <summary>Bytes that contain no valid UTF-8, padded to a length that exercises partial reads.</summary>
    private static byte[] CreateUndecodableBytes(int length, byte marker)
    {
        var content = new byte[length];

        for (int i = 0; i < length; i++)
        {
            content[i] = i % 5 == 0 ? (byte)0x00 : (i % 3 == 0 ? marker : (byte)(i % 256));
        }

        return content;
    }

    /// <summary>
    /// The two temporary names are derived from the repository paths, so paths that differ only in
    /// where the separators are collapse to the same name - "src/a/b.cs" and "a_b.cs" both become
    /// one tag. The prefixes have to keep the two files apart, or the tool is handed the same file
    /// twice and the comparison shows no difference at all.
    /// </summary>
    [Fact]
    public async Task Two_versions_whose_paths_collapse_to_one_name_are_still_handed_over_separately()
    {
        using TempRepository repository = TempRepository.Create();

        repository.WriteFile("src/a/b.cs", "class Widget { }");
        string older = repository.Commit("first");

        // A real rename onto a name that is what the old one reduces to once '/' becomes '_'.
        repository.Move("src/a/b.cs", "a_b.cs");
        string newer = repository.Commit("renames onto a colliding name");

        string script = InstallRecordingTool(repository, out string recordedFirst, out string recordedSecond);
        try
        {
            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "a_b.cs");

            DiffToolLaunchResult result = await new DiffToolLauncher()
                .LaunchAsync(git, change, older, newer, CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) = WaitForRecordedPaths(recordedFirst, recordedSecond);
            Assert.NotEqual(baseFile, modifiedFile);
            Assert.Equal("class Widget { }", File.ReadAllText(baseFile));
            Assert.Equal("class Widget { }", File.ReadAllText(modifiedFile));
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst); TryDelete(recordedSecond);
        }
    }

    [Fact]
    public async Task A_path_with_shell_metacharacters_survives_the_hand_off()
    {
        using TempRepository repository = TempRepository.Create();

        // The whole reason nothing here goes through a shell: this name would otherwise be read as
        // syntax rather than as a pathname.
        const string Awkward = "src/a & b (copy) %v%!.cs";

        repository.WriteFile(Awkward, "class Awkward { }");
        string older = repository.Commit("first");
        repository.WriteFile(Awkward, "class Awkward { int id; }");
        string newer = repository.Commit("second");

        string script = InstallRecordingTool(repository, out string recordedFirst, out string recordedSecond);
        try
        {
            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, Awkward);

            DiffToolLaunchResult result = await new DiffToolLauncher()
                .LaunchAsync(git, change, older, newer, CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) = WaitForRecordedPaths(recordedFirst, recordedSecond);
            Assert.Equal("class Awkward { }", File.ReadAllText(baseFile));
            Assert.Equal("class Awkward { int id; }", File.ReadAllText(modifiedFile));
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst); TryDelete(recordedSecond);
        }
    }

    [Fact]
    public async Task No_configured_diff_tool_is_reported_rather_than_silently_doing_nothing()
    {
        using TempRepository repository = TempRepository.Create();
        repository.WriteFile("a.cs", "class A { }");
        string older = repository.Commit("first");
        repository.WriteFile("a.cs", "class A { int id; }");
        string newer = repository.Commit("second");

        // No diff.tool at all: git has nothing to run, and saying so is better than a silent no-op
        // that looks like a broken double-click.
        GitService git = new GitService(repository.Root);
        GitFileChange change = await SingleChangeAsync(git, older, newer, "a.cs");

        DiffToolLaunchResult result = await new DiffToolLauncher()
            .LaunchAsync(git, change, older, newer, CancellationToken.None);

        // git itself may still start and then fail to find a tool, so what is asserted is that the
        // launcher reports one of the two honest outcomes rather than claiming success blindly.
        if (!result.Launched)
        {
            Assert.True(result.IsError);
            Assert.Contains("diff tool", result.Message);
        }
    }

    /// <summary>
    /// Beyond Compare installs to "C:\Program Files\Beyond Compare 4\BCompare.exe". Reading that as a
    /// command line by splitting on whitespace would make the program "C:\Program", so a tool path
    /// containing spaces has to survive being configured and launched.
    /// </summary>
    [Fact]
    public async Task A_tool_whose_path_contains_spaces_is_launched_as_one_program()
    {
        using TempRepository repository = TempRepository.Create();
        repository.WriteFile("a.cs", "class A { }");
        string older = repository.Commit("first");
        repository.WriteFile("a.cs", "class A { int id; }");
        string newer = repository.Commit("second");

        // The tool is placed in a folder with a space in it, which is what a real installation looks
        // like and what a naive split cannot handle.
        string toolFolder = Path.Combine(Path.GetTempPath(), "gdfc tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(toolFolder);

        string script = WriteRecordingScript(toolFolder, out string recordedFirst, out string recordedSecond);

        // Configured the way a real installation is: the absolute path, quoted, with no arguments.
        repository.Git("config", "diff.tool", "\"" + script + "\"");

        try
        {
            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "a.cs");

            DiffToolLaunchResult result = await new DiffToolLauncher()
                .LaunchAsync(git, change, older, newer, CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) =
                WaitForRecordedPaths(recordedFirst, recordedSecond);

            Assert.Equal("class A { }", File.ReadAllText(baseFile));
            Assert.Equal("class A { int id; }", File.ReadAllText(modifiedFile));
        }
        finally
        {
            TryDelete(script);
            TryDelete(recordedFirst);
            TryDelete(recordedSecond);
            TryDelete(toolFolder);
        }
    }

    /// <summary>
    /// git accepts a bare name in diff.tool that refers to difftool.&lt;name&gt;.cmd, whose command
    /// line uses the $LOCAL and $REMOTE placeholders. A bare name is not a program, so it has to be
    /// resolved to the named command line rather than executed as written.
    /// </summary>
    [Fact]
    public async Task A_named_tool_is_resolved_to_its_command_line_and_placeholders_are_filled_in()
    {
        using TempRepository repository = TempRepository.Create();
        repository.WriteFile("a.cs", "class A { }");
        string older = repository.Commit("first");
        repository.WriteFile("a.cs", "class A { int id; }");
        string newer = repository.Commit("second");

        string script = WriteRecordingScript(Path.GetTempPath(), out string recordedFirst, out string recordedSecond);

        try
        {
            // The two shapes git accepts, in the order a user would set them: a named command
            // line carrying $LOCAL and $REMOTE, and a bare name in diff.tool referring to it.
            repository.Git("config", "difftool.record.cmd", script + " \"$LOCAL\" \"$REMOTE\"");
            repository.Git("config", "diff.tool", "record");

            GitService git = new GitService(repository.Root);
            GitFileChange change = await SingleChangeAsync(git, older, newer, "a.cs");

            DiffToolLaunchResult result = await new DiffToolLauncher()
                .LaunchAsync(git, change, older, newer, CancellationToken.None);

            Assert.True(result.Launched, result.Message);

            (string baseFile, string modifiedFile) =
                WaitForRecordedPaths(recordedFirst, recordedSecond);

            // $LOCAL is the older file and $REMOTE the newer one, so the base side must come first.
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

    /// <summary>
    /// The two versions are left in the temporary folder after a successful hand-off, because the tool
    /// may still be reading them. Windows does not remove arbitrary temporary files, so repeated
    /// comparisons - of large files in particular - would fill the disk unless a later launch clears
    /// what earlier ones abandoned.
    /// </summary>
    /// <remarks>
    /// The age is the safety boundary and is checked from both sides: a folder young enough to belong
    /// to a diff window somebody has open must survive, and one old enough that nobody can still be
    /// reading it must go. The third folder is not ours and has to be left alone whatever its age.
    /// </remarks>
    [Fact]
    public void An_earlier_comparison_folder_is_cleared_once_it_cannot_still_be_in_use()
    {
        string root = Path.Combine(Path.GetTempPath(), $"gdfc-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            string abandoned = MakeFolder(root, "gdfc-compare-abandoned", TimeSpan.FromDays(3));
            string recent = MakeFolder(root, "gdfc-compare-recent", TimeSpan.FromMinutes(5));
            string notOurs = MakeFolder(root, "unrelated-folder", TimeSpan.FromDays(3));

            int removed = DiffToolLauncher.DeleteAbandonedFolders(
                root,
                DiffToolLauncher.AbandonedFolderAge,
                DateTime.UtcNow);

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(abandoned));
            Assert.True(Directory.Exists(recent), "A folder young enough to be in use was deleted.");
            Assert.True(Directory.Exists(notOurs), "A folder that is not this program's was deleted.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>There is nothing to sweep in a temporary folder that does not exist.</summary>
    [Fact]
    public void Sweeping_a_temporary_folder_that_is_not_there_is_not_a_failure()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"gdfc-sweep-{Guid.NewGuid():N}");

        Assert.Equal(0, DiffToolLauncher.DeleteAbandonedFolders(
            missing,
            DiffToolLauncher.AbandonedFolderAge,
            DateTime.UtcNow));
    }

    /// <summary>
    /// A folder of the given name and age, with its contents written before the timestamp is set:
    /// writing into a folder updates its modified time, which would make a young folder look old.
    /// </summary>
    private static string MakeFolder(string root, string name, TimeSpan age)
    {
        string folder = Path.Combine(root, name);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "base-a.cs"), "class A { }");
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - age);

        return folder;
    }

    /// <summary>
    /// Writes the batch file that records the two paths it is given, into the given folder.
    /// </summary>
    private static string WriteRecordingScript(
        string folder,
        out string recordedFirst,
        out string recordedSecond)
    {
        string recordPath = Path.Combine(Path.GetTempPath(), $"gdfc-tool-{Guid.NewGuid():N}.txt");
        recordedFirst = recordPath + ".1";
        recordedSecond = recordPath + ".2";

        string script = Path.Combine(folder, $"gdfc-tool-{Guid.NewGuid():N}.cmd");

        File.WriteAllText(
            script,
            string.Format(
                CultureInvariant,
                "@echo off\r\necho \"%~1\" > \"{0}\"\r\necho \"%~2\" > \"{1}\"\r\n",
                recordedFirst,
                recordedSecond),
            new UTF8Encoding(false));

        return script;
    }
    private static async Task<GitFileChange> SingleChangeAsync(
        GitService git,
        string older,
        string newer,
        string expectedPath)
    {
        System.Collections.Generic.IList<GitFileChange> changes = await git.GetChangesAsync(
            older,
            newer,
            CancellationToken.None);

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