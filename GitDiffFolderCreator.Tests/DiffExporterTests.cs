using System.Globalization;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using Xunit;

namespace GitDiffFolderCreator.Tests;

public sealed class DiffExporterTests
{
    [Fact]
    public async Task ExportAsync_does_not_warn_about_files_that_were_only_added()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("kept.cs", "alpha");
        string older = repo.Commit("older");

        // Added between the two commits, so it is in the modified commit and not in the base one.
        repo.WriteFile("brand-new.cs", "bravo");
        string newer = repo.Commit("newer");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // The new file is exported from the modified side, as it should be...
        Assert.True(File.Exists(Path.Combine(result.ModifiedFolderPath, "brand-new.cs")));

        // ...and the base side simply has no version of it, which is what adding a file means. That
        // is the expected result rather than something to be told about, so it is not a warning.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("brand-new.cs"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("not present at that commit"));
    }

    /// <summary>
    /// The export is meant to be a faithful copy, so binary content has to arrive as the bytes git
    /// stored. It travels as a zip entry, which copies bytes, and the text-reading path is the one
    /// that would corrupt them.
    /// </summary>
    [Fact]
    public async Task ExportAsync_writes_binary_files_byte_for_byte()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        // Not valid UTF-8 anywhere, so any decoding on the way through replaces bytes with
        // U+FFFD and the comparison the user then runs is against a file git never held.
        byte[] baseBytes = { 0x00, 0x01, 0xFF, 0xFE, 0x80, 0x7F, 0x00, 0x42 };
        byte[] modifiedBytes = { 0x00, 0x01, 0xFF, 0xFE, 0x90, 0x7F, 0x00, 0x43, 0xC3 };

        repo.WriteBinaryFile("Assets/logo.bin", baseBytes);
        string older = repo.Commit("older");

        repo.WriteBinaryFile("Assets/logo.bin", modifiedBytes);
        string newer = repo.Commit("newer");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        Assert.Equal(
            baseBytes,
            File.ReadAllBytes(Path.Combine(result.BaseFolderPath, "Assets", "logo.bin")));

        Assert.Equal(
            modifiedBytes,
            File.ReadAllBytes(Path.Combine(result.ModifiedFolderPath, "Assets", "logo.bin")));
    }

    /// <summary>
    /// A repository can hold two paths that differ only in case - git is case-sensitive, and
    /// repositories carried over from Linux routinely do. Windows is not, so both land on the same
    /// file, and whichever is written second quietly replaces the first. The export has to say so
    /// instead of handing back a folder that looks complete and is not.
    /// </summary>
    [Fact]
    public async Task ExportAsync_reports_two_paths_that_collide_on_this_filesystem()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        var olderFiles = new Dictionary<string, string>
        {
            ["Notes.txt"] = "the capitalised one",
            ["notes.txt"] = "the lower case one",
        };

        // Built without the working tree, because Windows cannot hold both names at once.
        string older = repo.CommitTreeOf(olderFiles, "older", null);

        var newerFiles = new Dictionary<string, string>
        {
            ["Notes.txt"] = "the capitalised one, changed",
            ["notes.txt"] = "the lower case one, changed",
        };

        string newer = repo.CommitTreeOf(newerFiles, "newer", older);

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // The collision is reported, by name, on both sides - losing a file silently is the one
        // outcome a folder comparison cannot detect for itself.
        Assert.Contains(result.Warnings, w => w.Contains("Notes.txt") && w.Contains("notes.txt"));

        // Reported once per side and only once: the cross-check must not name the same collision a
        // second time as an unexplained omission.
        Assert.Equal(2, result.Warnings.Count);

        // Both commits are exported, and exactly one file survives on disk, so the count has to
        // match the file rather than the path.
        Assert.Single(Directory.GetFiles(result.ModifiedFolderPath));
    }

    /// <summary>
    /// The bar is only worth showing if it is true. It has to start unknown — the file list has not
    /// come back yet — become determinate once the number of paths is known, never go backwards, and
    /// finish at exactly full.
    /// </summary>
    [Fact]
    public async Task ExportAsync_reports_progress_that_is_unknown_then_rises_to_full()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        const int Files = 5;

        for (int i = 0; i < Files; i++)
        {
            repo.WriteFile("file" + i + ".txt", "base-" + i);
        }

        string older = repo.Commit("older");

        for (int i = 0; i < Files; i++)
        {
            repo.WriteFile("file" + i + ".txt", "modified-" + i);
        }

        string newer = repo.Commit("newer");

        var reports = new List<ExportProgress>();

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            new Progress<ExportProgress>(reports.Add),
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // Unknown first: the git diff that produces the list has no figure, and reporting one would
        // be a guess dressed as a measurement.
        Assert.False(reports.First().IsDeterminate);
        Assert.Equal(0d, reports.First().Fraction);

        // Then determinate, with one fixed total - two sides of Files paths - and rising without ever
        // retreating.
        List<ExportProgress> determinate = reports.Where(r => r.IsDeterminate).ToList();
        Assert.NotEmpty(determinate);
        Assert.All(determinate, r => Assert.Equal(2 * Files, r.Total));

        for (int i = 1; i < determinate.Count; i++)
        {
            Assert.True(
                determinate[i].Completed >= determinate[i - 1].Completed,
                $"The bar went backwards: {determinate[i - 1].Completed} then {determinate[i].Completed}.");
        }

        // And it ends at full rather than short, because every path handled is counted whether or not
        // a file was written for it.
        ExportProgress last = reports[reports.Count - 1];
        Assert.Equal(2 * Files, last.Completed);
        Assert.Equal(1d, last.Fraction);

        // Every report describes itself as a stage or a count, never as an empty line.
        Assert.All(reports, r => Assert.False(string.IsNullOrWhiteSpace(r.Message)));
    }

    /// <summary>
    /// A path git cannot supply is still work that got done. Counting only the files that landed
    /// would leave the bar short of full on exactly the exports that went wrong, which is the one
    /// time the user most wants to see it complete.
    /// </summary>
    [Fact]
    public async Task ExportAsync_reaches_full_even_when_some_paths_produce_no_file()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("kept.cs", "alpha");
        string older = repo.Commit("older");

        // Added after the base commit, so the base side has nothing to write for it.
        repo.WriteFile("added.cs", "bravo");
        string newer = repo.Commit("newer");

        var reports = new List<ExportProgress>();

        DiffExporter exporter = new();
        await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            new Progress<ExportProgress>(reports.Add),
            CancellationToken.None);

        Assert.Equal(1d, reports[reports.Count - 1].Fraction);
    }

    /// <summary>
    /// A path can be marked 'export-ignore' in .gitattributes, which tells Git not to put it in a
    /// tarball. The old transport used 'git archive', honoured that, and reported success with the
    /// file simply absent - the run folder was short a file with nothing said about it. Asking git
    /// for the object instead of asking for an archive means the attribute is irrelevant: the user
    /// asked for this file at this commit, and that is what is written.
    /// </summary>
    [Fact]
    public async Task ExportAsync_writes_a_path_that_git_would_have_excluded_from_an_archive()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("wanted.cs", "alpha");
        repo.WriteFile("hidden.cs", "bravo");
        repo.WriteFile(".gitattributes", "hidden.cs export-ignore\n");
        string older = repo.Commit("older");

        repo.WriteFile("wanted.cs", "alpha changed");
        repo.WriteFile("hidden.cs", "bravo changed");
        string newer = repo.Commit("newer");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // Both files, on both sides, because the commit has both and the export is a copy of it.
        Assert.Equal("bravo", File.ReadAllText(Path.Combine(result.BaseFolderPath, "hidden.cs")));
        Assert.Equal(
            "bravo changed", File.ReadAllText(Path.Combine(result.ModifiedFolderPath, "hidden.cs")));
        Assert.Equal("alpha changed", File.ReadAllText(Path.Combine(result.ModifiedFolderPath, "wanted.cs")));

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// A path that resolves to something other than a file - a submodule is a commit object, a
    /// directory is a tree - has no content to write. Git says so in its answer rather than by
    /// omitting the entry, so it is reported instead of vanishing.
    /// </summary>
    [Fact]
    public async Task ExportAsync_reports_a_path_that_is_not_a_file()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("a.txt", "alpha");
        string root = repo.Commit("root");

        // A gitlink: the tree names a submodule at 'sub', which is a commit rather than a blob. It is
        // built through a throwaway index because a Windows working tree cannot hold a submodule
        // entry that was never cloned.
        var olderFiles = new Dictionary<string, string>
        {
            ["a.txt"] = "alpha",

            // For a gitlink the value is the commit it points at, not content.
            ["sub"] = root,
        };

        var olderModes = new Dictionary<string, string> { ["sub"] = "160000" };

        string older = repo.CommitTreeOf(olderFiles, "older", root, olderModes);

        // The same commit with 'sub' now a blob, so the gitlink became a real file: one added
        // file, one path that has no content.
        var newerFiles = new Dictionary<string, string>
        {
            ["a.txt"] = "alpha changed",
            ["sub"] = "now a file",
        };

        string newer = repo.CommitTreeOf(newerFiles, "newer", older);

        // The change list is what the export is driven from, so the gitlink is asked for as though
        // it were a file - which is what git itself would report as changed.
        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // On the base side 'sub' is a commit object, so nothing is written for it and it is named.
        Assert.False(Directory.Exists(Path.Combine(result.BaseFolderPath, "sub")));
        Assert.Contains(result.Warnings, w => w.Contains("are not files") && w.Contains("sub"));

        // On the modified side it is an ordinary added file, and is written like one.
        Assert.Equal("now a file", File.ReadAllText(Path.Combine(result.ModifiedFolderPath, "sub")));
    }

    [Fact]
    public async Task ExportAsync_does_not_mistake_an_addition_or_a_clash_for_a_filtered_path()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("kept.cs", "alpha");
        string older = repo.Commit("older");

        repo.WriteFile("kept.cs", "bravo");

        // Added, so the base archive cannot contain it: that is the expected result of comparing two
        // commits, not something Git filtered out.
        repo.WriteFile("added.cs", "gamma");
        string newer = repo.Commit("newer");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // The addition produces the same shape of absence as a path that is not a file, and
        // must not be reported as one.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("are not files"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("added.cs"));
    }

    /// <summary>
    /// A path that was dropped here rather than by Git is still absent from the result, so the
    /// cross-check would otherwise report it a second time as a filtered path. One failure, one
    /// warning: the two reports would each be half the story.
    /// </summary>
    [Fact]
    public async Task ExportAsync_reports_a_colliding_path_as_a_clash_and_not_also_as_a_filtered_one()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        var olderFiles = new Dictionary<string, string>
        {
            ["Notes.txt"] = "the capitalised one",
            ["notes.txt"] = "the lower case one",
        };

        string older = repo.CommitTreeOf(olderFiles, "older", null);

        var newerFiles = new Dictionary<string, string>
        {
            ["Notes.txt"] = "the capitalised one, changed",
            ["notes.txt"] = "the lower case one, changed",
        };

        string newer = repo.CommitTreeOf(newerFiles, "newer", older);

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        Assert.Contains(result.Warnings, w => w.Contains("same file on this filesystem"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("are not files"));
    }

    [Fact]
    public async Task ExportAsync_warns_about_files_that_are_in_the_base_commit_but_not_the_modified_one()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("kept.cs", "alpha");
        repo.WriteFile("gone.cs", "bravo");
        string older = repo.Commit("older");

        // Removed between the two commits, so it is in the base commit and not in the modified one.
        repo.DeleteFile("gone.cs");
        string newer = repo.Commit("newer");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // The base folder keeps its copy; the modified folder cannot have one.
        Assert.True(File.Exists(Path.Combine(result.BaseFolderPath, "gone.cs")));
        Assert.False(File.Exists(Path.Combine(result.ModifiedFolderPath, "gone.cs")));

        // This one is worth saying out loud: the two folders will not match up the way a caller
        // comparing them expects, and nothing on screen would otherwise explain why.
        Assert.Contains(result.Warnings, w => w.Contains("gone.cs"));
    }

    [Fact]
    public async Task ExportAsync_does_not_warn_when_a_file_exists_on_both_sides()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("a.cs", "alpha");
        string older = repo.Commit("older");

        repo.WriteFile("a.cs", "bravo");
        string newer = repo.Commit("newer");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ExportAsync_leaves_unticked_files_out_of_both_folders_and_says_so()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("keep.cs", "alpha alpha");
        repo.WriteFile("skip.cs", "bravo bravo");
        repo.WriteFile("also-skip.cs", "charlie charlie");
        string older = repo.Commit("older");

        repo.WriteFile("keep.cs", "delta");
        repo.WriteFile("skip.cs", "echo");
        repo.WriteFile("also-skip.cs", "foxtrot");
        string newer = repo.Commit("newer");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
                ExcludedPaths = new List<string> { "skip.cs", "also-skip.cs" },
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // The ticked file is exported from both sides.
        Assert.Equal("delta", File.ReadAllText(Path.Combine(result.ModifiedFolderPath, "keep.cs")));
        Assert.Equal("alpha alpha", File.ReadAllText(Path.Combine(result.BaseFolderPath, "keep.cs")));

        // The unticked ones are absent from both, not merely from the newer one.
        Assert.False(File.Exists(Path.Combine(result.ModifiedFolderPath, "skip.cs")));
        Assert.False(File.Exists(Path.Combine(result.BaseFolderPath, "skip.cs")));
        Assert.False(File.Exists(Path.Combine(result.ModifiedFolderPath, "also-skip.cs")));

        // A silent exclusion would look like a bug, so it is reported as a warning.
        Assert.Contains(result.Warnings, w => w.Contains("excluded by selection"));
    }

    [Fact]
    public async Task ExportAsync_reports_an_empty_result_when_every_file_is_unticked()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("only.cs", "alpha");
        string older = repo.Commit("older");
        repo.WriteFile("only.cs", "bravo");
        string newer = repo.Commit("newer");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
                ExcludedPaths = new List<string> { "only.cs" },
            },
            progress: null,
            CancellationToken.None);

        // Nothing to export is not a failure, but it must not quietly fall back to exporting all.
        Assert.False(result.Success);
        Assert.Contains("no file differences", result.FailureReason ?? string.Empty);
    }

    [Fact]
    public async Task ExportAsync_ignores_an_exclusion_for_a_path_that_did_not_change()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("a.cs", "alpha");
        string older = repo.Commit("older");
        repo.WriteFile("a.cs", "bravo");
        string newer = repo.Commit("newer");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
                ExcludedPaths = new List<string> { "never-changed.cs" },
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("bravo", File.ReadAllText(Path.Combine(result.ModifiedFolderPath, "a.cs")));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ExportAsync_writes_the_base_and_modified_version_of_every_changed_file()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("src/modify.cs", "alpha alpha alpha alpha");
        repo.WriteFile("src/delete.cs", "bravo bravo");
        repo.WriteFile("src/rename-old.cs", "charlie charlie charlie");
        repo.WriteFile("untouched.cs", "delta");
        string older = repo.Commit("older");

        repo.WriteFile("src/modify.cs", "delta delta");
        repo.DeleteFile("src/delete.cs");
        repo.Move("src/rename-old.cs", "src/rename-new.cs");
        repo.WriteFile("src/added.cs", "echo echo echo");
        string newer = repo.Commit("newer");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // Base folder: only the files that exist at the base commit.
        Assert.Equal("alpha alpha alpha alpha", File.ReadAllText(Path.Combine(result.BaseFolderPath, "src", "modify.cs")));
        Assert.Equal("bravo bravo", File.ReadAllText(Path.Combine(result.BaseFolderPath, "src", "delete.cs")));
        Assert.Equal("charlie charlie charlie", File.ReadAllText(Path.Combine(result.BaseFolderPath, "src", "rename-old.cs")));
        Assert.False(File.Exists(Path.Combine(result.BaseFolderPath, "src", "added.cs")));

        // Modified folder: the same set at the modified commit.
        Assert.Equal("delta delta", File.ReadAllText(Path.Combine(result.ModifiedFolderPath, "src", "modify.cs")));
        Assert.Equal("charlie charlie charlie", File.ReadAllText(Path.Combine(result.ModifiedFolderPath, "src", "rename-new.cs")));
        Assert.Equal("echo echo echo", File.ReadAllText(Path.Combine(result.ModifiedFolderPath, "src", "added.cs")));
        Assert.False(File.Exists(Path.Combine(result.ModifiedFolderPath, "src", "delete.cs")));
        Assert.False(File.Exists(Path.Combine(result.ModifiedFolderPath, "src", "rename-old.cs")));

        // Unchanged files are not relevant to the comparison.
        Assert.False(File.Exists(Path.Combine(result.BaseFolderPath, "untouched.cs")));
        Assert.False(File.Exists(Path.Combine(result.ModifiedFolderPath, "untouched.cs")));
    }

    [Fact]
    public async Task ExportAsync_creates_one_result_folder_named_for_both_hashes_and_the_time()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("src/a.cs", "alpha alpha alpha");
        string older = repo.Commit("older");
        repo.WriteFile("src/a.cs", "bravo bravo");
        string newer = repo.Commit("newer");

        DateTime before = DateTime.Now.AddSeconds(-2);
        ExportResult result = await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);
        DateTime after = DateTime.Now.AddSeconds(2);

        Assert.True(result.Success, result.FailureReason);

        string name = Path.GetFileName(result.RootFolderPath);
        string shortOlder = older.Substring(0, 8);
        string shortNewer = newer.Substring(0, 8);

        Assert.StartsWith("diff_", name, StringComparison.Ordinal);
        Assert.Contains(shortOlder, name, StringComparison.Ordinal);
        Assert.Contains(shortNewer, name, StringComparison.Ordinal);

        // The trailing component is the timestamp, yyyyMMdd-HHmmss.
        string timestamp = name.Substring(("diff_" + shortOlder + "_" + shortNewer + "_").Length);
        Assert.True(timestamp.Length == 15, $"Unexpected timestamp component '{timestamp}'.");
        DateTime parsed = DateTime.ParseExact(
            timestamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None);
        Assert.InRange(parsed, before, after);

        // The result folder sits directly under the output directory.
        Assert.Equal(output.Path, Path.GetFullPath(Path.GetDirectoryName(result.RootFolderPath)!).TrimEnd('\\'));

        // Both file sets live under the one folder, alongside the manifests.
        Assert.Equal("base", Path.GetFileName(result.BaseFolderPath));
        Assert.Equal("modified", Path.GetFileName(result.ModifiedFolderPath));
        Assert.Equal(result.RootFolderPath, Path.GetDirectoryName(result.BaseFolderPath));
        Assert.Equal(result.RootFolderPath, Path.GetDirectoryName(result.ModifiedFolderPath));

        Assert.True(File.Exists(Path.Combine(result.RootFolderPath, "DeletedFiles.txt")));
        Assert.True(File.Exists(Path.Combine(result.RootFolderPath, "RenamedFiles.txt")));
        Assert.True(File.Exists(Path.Combine(result.RootFolderPath, "ChangedFiles.txt")));
    }

    [Fact]
    public async Task ExportAsync_writes_manifests_in_the_result_folder_not_in_the_subfolders()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("deleted.txt", "bravo bravo");
        repo.WriteFile("old-name.txt", "charlie charlie charlie");
        repo.WriteFile("changed.txt", "delta delta delta");
        string older = repo.Commit("older");

        repo.DeleteFile("deleted.txt");
        repo.Move("old-name.txt", "new-name.txt");
        repo.WriteFile("changed.txt", "echo echo");
        string newer = repo.Commit("newer");

        ExportResult result = await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        string[] deleted = File.ReadAllLines(Path.Combine(result.RootFolderPath, "DeletedFiles.txt"));
        Assert.Contains("deleted.txt", deleted);
        Assert.Contains("old-name.txt", deleted);

        string[] renamed = File.ReadAllLines(Path.Combine(result.RootFolderPath, "RenamedFiles.txt"));
        Assert.Contains("old-name.txt  ->  new-name.txt", renamed);

        Assert.NotEmpty(File.ReadAllLines(Path.Combine(result.RootFolderPath, "ChangedFiles.txt")));

        // Manifests belong to the run as a whole, so they must not be buried in either file set, and
        // must not sit in the shared output root where two runs would overwrite each other.
        Assert.False(File.Exists(Path.Combine(result.ModifiedFolderPath, "DeletedFiles.txt")));
        Assert.False(File.Exists(Path.Combine(result.BaseFolderPath, "DeletedFiles.txt")));
        Assert.False(File.Exists(Path.Combine(output.Path, "DeletedFiles.txt")));
    }

    /// <summary>
    /// Each manifest says how many entries it holds, on a line marked as a comment.
    /// </summary>
    /// <remarks>
    /// The count is marked with a leading '#' rather than being a bare first line, because these files
    /// are read line by line: a bare number would be counted as a path by anything that does not know
    /// the convention, and a script would report one more rename than actually happened.
    /// </remarks>
    [Fact]
    public async Task Every_manifest_says_how_many_entries_it_lists()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("deleted.txt", "bravo");
        repo.WriteFile("old-name.txt", "charlie");
        repo.WriteFile("changed.txt", "delta");
        string older = repo.Commit("older");

        repo.DeleteFile("deleted.txt");
        repo.Move("old-name.txt", "new-name.txt");
        repo.WriteFile("changed.txt", "echo");
        string newer = repo.Commit("newer");

        ExportResult result = await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // A deletion and the path a rename moved away from, so two entries, not one.
        AssertManifestCounts(result.RootFolderPath, "DeletedFiles.txt", 2);
        AssertManifestCounts(result.RootFolderPath, "RenamedFiles.txt", 1);

        // Both sides of the rename and the edit: three, not two.
        AssertManifestCounts(result.RootFolderPath, "ChangedFiles.txt", 3);
    }

    /// <summary>
    /// A manifest with nothing in it says so, rather than being an empty file.
    /// </summary>
    /// <remarks>
    /// Zero bytes reads the same whether there were no renames or the renames file never arrived, and
    /// the second of those is exactly the case a reader needs to notice.
    /// </remarks>
    [Fact]
    public async Task An_empty_manifest_still_says_that_it_is_empty()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("a.txt", "alpha");
        string older = repo.Commit("older");
        repo.WriteFile("a.txt", "bravo");
        string newer = repo.Commit("newer");

        ExportResult result = await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // Nothing was renamed and nothing was deleted, and the files say so rather than being blank.
        AssertManifestCounts(result.RootFolderPath, "RenamedFiles.txt", 0);
        AssertManifestCounts(result.RootFolderPath, "DeletedFiles.txt", 0);
    }

    /// <summary>
    /// The manifest of what the folders hold, as opposed to what the comparison was.
    /// </summary>
    /// <remarks>
    /// These are different lists and the difference is the reason this file exists. A file that was
    /// added is in the modified folder and has no base version, so the two folders are legitimately
    /// different sizes, and the other manifests describe neither of them.
    /// </remarks>
    [Fact]
    public async Task A_manifest_lists_the_files_that_landed_in_each_folder()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("src/shared.cs", "one");
        repo.WriteFile("src/removed.cs", "two");
        string older = repo.Commit("older");

        repo.WriteFile("src/added.cs", "three");
        repo.WriteFile("src/shared.cs", "one changed");
        repo.DeleteFile("src/removed.cs");
        string newer = repo.Commit("newer");

        ExportResult result = await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        string[] manifest = File.ReadAllLines(Path.Combine(result.RootFolderPath, "ExportedFiles.txt"));

        List<string> baseSection = Section(manifest, "# base:");
        List<string> modifiedSection = Section(manifest, "# modified:");

        // Base has the modified file and the deleted one, and not the addition.
        Assert.Equal(new[] { "src/removed.cs", "src/shared.cs" }, baseSection);
        Assert.DoesNotContain("src/added.cs", baseSection);

        // Modified has the addition and the modified file, and not the deletion.
        Assert.Equal(new[] { "src/added.cs", "src/shared.cs" }, modifiedSection);
        Assert.DoesNotContain("src/removed.cs", modifiedSection);

        // And the lists are what is really on disk, counted from the folders themselves rather than
        // taken on trust.
        Assert.Equal(baseSection.Count, FilesIn(result.BaseFolderPath).Count);
        Assert.Equal(modifiedSection.Count, FilesIn(result.ModifiedFolderPath).Count);
        Assert.Equal(
            FilesIn(result.BaseFolderPath).OrderBy(p => p, StringComparer.Ordinal),
            baseSection.OrderBy(p => p, StringComparer.Ordinal));

        // The headers carry the counts, so a reader gets them without counting lines.
        Assert.Contains(manifest, line => line == "# base: 2 file(s)");
        Assert.Contains(manifest, line => line == "# modified: 2 file(s)");
    }

    /// <summary>
    /// The export cross-checks what it wrote against the change list, and says the numbers even when
    /// they agree.
    /// </summary>
    [Fact]
    public async Task ExportAsync_cross_checks_the_folders_against_the_change_list()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("a.txt", "alpha");
        repo.WriteFile("gone.txt", "bravo");
        string older = repo.Commit("older");

        repo.WriteFile("a.txt", "alpha two");
        repo.WriteFile("added.txt", "charlie");
        repo.DeleteFile("gone.txt");
        string newer = repo.Commit("newer");

        ExportResult result = await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // A deletion is missing from the modified folder and an addition from the base folder. Both
        // are the expected shape of a comparison, so neither is a discrepancy to warn about.
        Assert.Empty(Describe(result.Warnings, "nothing accounts for it"));
        Assert.Empty(Describe(result.Warnings, "not in the change list"));

        // The counts are reported anyway, so agreement is visible rather than inferred from silence.
        Assert.Contains(
            result.Messages,
            message => message.Contains("Verified: 2 file(s) in base, 2 in modified, from 3 changed path(s)."));
    }

    /// <summary>
    /// A path that Git cannot supply is missing from the folder, but the export already says so from
    /// Git's own answer. Reporting it a second time as a discrepancy would be the same fact twice.
    /// </summary>
    [Fact]
    public async Task A_path_git_could_not_supply_is_not_reported_twice()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("real.txt", "alpha");
        string root = repo.Commit("root");

        // A gitlink: the tree names a submodule at 'vendor/lib', which is a commit rather than a
        // blob, so there is no content to write and git reports it as omitted. Built through a
        // throwaway index, because a Windows working tree cannot hold a submodule entry that was
        // never cloned.
        string older = repo.CommitTreeOf(
            new Dictionary<string, string>
            {
                ["real.txt"] = "alpha",

                // For a gitlink the value is the commit it points at, not content.
                ["vendor/lib"] = root,
            },
            "older",
            root,
            new Dictionary<string, string> { ["vendor/lib"] = "160000" });

        // The newer side drops the submodule, so the export asks for it and cannot have it.
        string newer = repo.CommitTreeOf(
            new Dictionary<string, string> { ["real.txt"] = "alpha changed" },
            "newer",
            older);

        ExportResult result = await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);

        // Git's own explanation is there.
        Assert.Contains(result.Warnings, warning => warning.Contains("are not files"));

        // And the cross-check does not repeat it as something unaccounted for.
        Assert.Empty(Describe(result.Warnings, "nothing accounts for it"));
    }

    private static void AssertManifestCounts(string root, string name, int expectedDataLines)
    {
        string[] lines = File.ReadAllLines(Path.Combine(root, name));

        Assert.True(
            lines.Length > 0,
            name + " is empty, so it cannot say whether it holds nothing or was never written.");

        // Marked as a comment, so a reader taking the file line by line does not count the header as
        // a path.
        Assert.StartsWith("# ", lines[0]);
        Assert.Contains("# " + expectedDataLines + " ", lines[0]);

        // The header declares the count and the rest of the file is the list, so the two cannot
        // disagree: this is the check that the declared number is the real one.
        Assert.Equal(expectedDataLines, lines.Length - 1);
    }

    /// <summary>The data lines under a header, up to the next header or a blank line.</summary>
    private static List<string> Section(IList<string> lines, string headerPrefix)
    {
        List<string> section = new List<string>();
        bool inside = false;

        foreach (string line in lines)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = line.StartsWith(headerPrefix, StringComparison.Ordinal);
                continue;
            }

            if (line.Length == 0)
            {
                if (inside)
                {
                    break;
                }

                continue;
            }

            if (inside)
            {
                section.Add(line);
            }
        }

        return section;
    }

    private static List<string> FilesIn(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => file.Substring(folder.TrimEnd(Path.DirectorySeparatorChar).Length + 1)
                .Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> Describe(IList<string> warnings, string fragment) =>
        warnings.Where(warning => warning.IndexOf(fragment, StringComparison.Ordinal) >= 0);

    [Fact]
    public async Task ExportAsync_can_skip_the_manifests()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("a.txt", "alpha");
        string older = repo.Commit("older");
        repo.WriteFile("a.txt", "bravo");
        string newer = repo.Commit("newer");

        ExportResult result = await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
                WriteManifests = false,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.False(File.Exists(Path.Combine(result.RootFolderPath, "ChangedFiles.txt")));
        Assert.True(File.Exists(Path.Combine(result.BaseFolderPath, "a.txt")));
    }

    [Fact]
    public async Task ExportAsync_reports_paths_missing_from_one_commit_without_aborting()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("a.txt", "a");
        string older = repo.Commit("older");
        repo.WriteFile("b.txt", "b");
        string newer = repo.Commit("newer");

        GitService git = new(repo.Root);

        var handled = new List<string>();

        FileExportResult export = await git.ExportFilesAsync(
            newer,
            ["a.txt", "b.txt", "does-not-exist.txt"],
            output.Path,
            handled.Add,
            CancellationToken.None);

        // The absent path is reported as handled too: the work is done even though no file was
        // written, which is what lets a progress bar finish rather than stalling at 2 of 3.
        Assert.Equal(3, handled.Count);

        // One bad pathname previously made git archive fail, and the whole batch was lost.
        Assert.Equal(2, export.ExtractedPaths.Count);
        Assert.Equal("does-not-exist.txt", Assert.Single(export.MissingPaths));
        Assert.True(File.Exists(Path.Combine(output.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(output.Path, "b.txt")));
    }

    [Fact]
    public async Task ExportAsync_never_overwrites_an_existing_folder()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("a.txt", "a");
        string older = repo.Commit("older");
        repo.WriteFile("a.txt", "b");
        string newer = repo.Commit("newer");

        GitService git = new(repo.Root);
        DiffExporter exporter = new();
        ExportRequest request = new()
        {
            Git = git,
            BaseHash = older,
            ModifiedHash = newer,
            OutputRoot = output.Path,
        };

        ExportResult first = await exporter.ExportAsync(request, progress: null, CancellationToken.None);
        ExportResult second = await exporter.ExportAsync(request, progress: null, CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotEqual(first.RootFolderPath, second.RootFolderPath);
        Assert.True(Directory.Exists(first.RootFolderPath));
        Assert.True(Directory.Exists(second.RootFolderPath));
    }

    [Fact]
    public async Task ExportAsync_leaves_no_staging_folder_behind()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("a.txt", "a");
        string older = repo.Commit("older");
        repo.WriteFile("a.txt", "b");
        string newer = repo.Commit("newer");

        await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.Empty(Directory.GetDirectories(output.Path, ".gdfc-staging-*"));

        // Only the single result folder is left behind.
        Assert.Single(Directory.GetDirectories(output.Path));
    }

    [Fact]
    public async Task ExportAsync_rejects_an_empty_output_directory()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string older = repo.Commit("older");
        repo.WriteFile("a.txt", "b");
        string newer = repo.Commit("newer");

        ExportResult result = await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = older,
                ModifiedHash = newer,
                OutputRoot = "   ",
            },
            progress: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task ExportAsync_fails_visibly_when_the_commits_identical()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("a.txt", "a");
        string hash = repo.Commit("only");

        ExportResult result = await new DiffExporter().ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = hash,
                ModifiedHash = hash,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task ExportAsync_creates_the_output_root_when_missing()
    {
        using TempRepository repo = TempRepository.Create();
        string parent = Path.Combine(Path.GetTempPath(), $"gdfc-out-{Guid.NewGuid():N}");
        string output = Path.Combine(parent, "nested", "result");

        try
        {
            repo.WriteFile("a.txt", "a");
            string older = repo.Commit("older");
            repo.WriteFile("a.txt", "b");
            string newer = repo.Commit("newer");

            ExportResult result = await new DiffExporter().ExportAsync(
                new ExportRequest
                {
                    Git = new GitService(repo.Root),
                    BaseHash = older,
                    ModifiedHash = newer,
                    OutputRoot = output,
                },
                progress: null,
                CancellationToken.None);

            Assert.True(result.Success, result.FailureReason);
            Assert.True(Directory.Exists(result.ModifiedFolderPath));
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }
}

public sealed class CommitRangeResolverTests
{
    [Fact]
    public async Task ResolveAsync_orders_by_ancestry_not_by_selection_order()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string older = repo.Commit("older");
        repo.WriteFile("a.txt", "b");
        string newer = repo.Commit("newer");

        GitService git = new(repo.Root);
        GitCommit olderCommit = (await git.GetLogAsync(10, CancellationToken.None))[1];
        GitCommit newerCommit = (await git.GetLogAsync(10, CancellationToken.None))[0];

        CommitRange forward = await CommitRangeResolver.ResolveAsync(git, olderCommit, newerCommit, CancellationToken.None);
        CommitRange reverse = await CommitRangeResolver.ResolveAsync(git, newerCommit, olderCommit, CancellationToken.None);

        Assert.Equal(older, forward.Base.Hash);
        Assert.Equal(newer, forward.Modified.Hash);
        Assert.Equal(RangeConfidence.Ancestor, forward.Confidence);

        // Whichever order the user clicks in, the direction is the same.
        Assert.Equal(older, reverse.Base.Hash);
        Assert.Equal(newer, reverse.Modified.Hash);
        Assert.Equal(RangeConfidence.Ancestor, reverse.Confidence);
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_dates_and_flags_it_for_divergent_branches()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("main-line");
        repo.Git("checkout", "-q", "-b", "feature");
        repo.WriteFile("feature.txt", "feature branch content");
        string featureCommit = repo.Commit("feature work");
        repo.Git("checkout", "-q", "main");
        repo.WriteFile("main.txt", "main branch content");
        string mainCommit = repo.Commit("main work");

        GitService git = new(repo.Root);

        // The feature commit is not reachable from main, so it is absent from the log; build the
        // two entries directly with a known age ordering.
        GitCommit feature = new()
        {
            Hash = featureCommit,
            ShortHash = featureCommit.Substring(0, 8),
            CommitDateText = "2026-01-01T00:00:00+00:00",
            CommitDate = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
            Author = "Test User",
            Message = "feature work",
        };

        GitCommit main = new()
        {
            Hash = mainCommit,
            ShortHash = mainCommit.Substring(0, 8),
            CommitDateText = "2026-02-01T00:00:00+00:00",
            CommitDate = DateTimeOffset.Parse("2026-02-01T00:00:00+00:00"),
            Author = "Test User",
            Message = "main work",
        };

        CommitRange forward = await CommitRangeResolver.ResolveAsync(git, feature, main, CancellationToken.None);
        CommitRange reverse = await CommitRangeResolver.ResolveAsync(git, main, feature, CancellationToken.None);

        // Neither is an ancestor of the other, so the direction is a guess and must say so.
        Assert.Equal(RangeConfidence.DateHeuristic, forward.Confidence);
        Assert.NotEmpty(forward.ConfidenceNote);
        Assert.Equal(featureCommit, forward.Base.Hash);
        Assert.Equal(mainCommit, forward.Modified.Hash);
        Assert.Equal(featureCommit, reverse.Base.Hash);
    }

    [Fact]
    public async Task ResolveAsync_rejects_selecting_the_same_commit_twice()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("only");

        GitService git = new(repo.Root);
        GitCommit commit = (await git.GetLogAsync(10, CancellationToken.None))[0];

        await Assert.ThrowsAsync<ArgumentException>(
            () => CommitRangeResolver.ResolveAsync(git, commit, commit, CancellationToken.None));
    }
}

public sealed class AppSettingsStoreTests
{
    [Fact]
    public void Save_and_load_round_trip()
    {
        using TempDirectory directory = new();
        AppSettingsStore store = new(Path.Combine(directory.Path, "settings.json"));

        store.Save(new AppSettings
        {
            GitDirectory = @"C:\repos\demo",
            OutputDirectory = @"C:\out",
            LogLimit = 250,
            OpenOutputWhenFinished = false,
        });

        AppSettings loaded = store.Load();

        Assert.Equal(@"C:\repos\demo", loaded.GitDirectory);
        Assert.Equal(@"C:\out", loaded.OutputDirectory);
        Assert.Equal(250, loaded.LogLimit);
        Assert.False(loaded.OpenOutputWhenFinished);
    }

    [Fact]
    public void Load_returns_defaults_when_the_file_is_missing_or_corrupt()
    {
        using TempDirectory directory = new();

        AppSettingsStore missing = new(Path.Combine(directory.Path, "absent.json"));
        AppSettings defaults = missing.Load();
        Assert.NotNull(defaults);
        Assert.Equal(string.Empty, defaults.GitDirectory);

        string corrupt = Path.Combine(directory.Path, "corrupt.json");
        File.WriteAllText(corrupt, "{ not json");
        Assert.NotNull(new AppSettingsStore(corrupt).Load());
    }

    [Fact]
    public void A_settings_file_written_before_the_open_option_defaults_to_on()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");

        File.WriteAllText(
            path,
            @"{""gitDirectory"":""C:\repos\demo"",""outputDirectory"":""C:\out"",""logLimit"":250}");

        AppSettings loaded = new AppSettingsStore(path).Load();

        // A serializer that skipped the constructor would read this as false, silently turning the
        // feature off for existing users.
        Assert.True(loaded.OpenOutputWhenFinished);
    }

    [Fact]
    public void Collapsed_panels_survive_a_save_and_load()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");

        var store = new AppSettingsStore(path);
        store.Save(new AppSettings { CollapsedSections = "commitLog,result" });

        Assert.Equal("commitLog,result", store.Load().CollapsedSections);
    }

    [Fact]
    public void A_settings_file_written_before_the_panels_existed_opens_them_all()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");

        File.WriteAllText(path, @"{""gitDirectory"":""C:\repos\demo"",""logLimit"":250}");

        AppSettings loaded = new AppSettingsStore(path).Load();

        // Empty means "nothing folded", which is what the window treats as all panels open.
        Assert.True(string.IsNullOrEmpty(loaded.CollapsedSections));
    }
}

/// <summary>Disposable scratch directory.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"gdfc-out-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>
    /// Writes a file inside the scratch directory, creating parent folders, and returns its full path.
    /// </summary>
    /// <remarks>
    /// Written without a byte-order mark. A document that claims to list source files is read back as
    /// text, and a mark left in front of the first line becomes part of the first path.
    /// </remarks>
    public string WriteFile(string relativePath, string content)
    {
        string full = PathFor(relativePath);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));

        return full;
    }

    /// <summary>The full path of something inside the scratch directory, whether or not it exists.</summary>
    public string PathFor(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}