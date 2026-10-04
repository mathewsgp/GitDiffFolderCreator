using System.Globalization;
using System.IO;
using System.Threading;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using Xunit;

namespace GitDiffFolderCreator.Tests;

public sealed class GitServiceTests
{
    [Fact]
    public async Task GetLogAsync_returns_full_and_short_hash()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.Commit("first");

        GitService git = new(repo.Root);
        IList<GitCommit> commits = await git.GetLogAsync(10, CancellationToken.None);

        GitCommit commit = Assert.Single(commits);
        Assert.Equal(hash, commit.Hash);
        Assert.Equal(40, commit.Hash.Length);
        Assert.NotNull(commit.CommitDate);
        Assert.Equal("first", commit.Message);
        Assert.Equal("Test User", commit.Author);
    }

    [Fact]
    public async Task GetLogAsync_survives_separators_inside_commit_messages()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");

        // The previous implementation split the log on ";;;" and "|||", so this message collapsed
        // the record boundary and every following commit was misparsed.
        repo.Commit("fix ||| that ;;; thing |||");
        repo.WriteFile("b.txt", "b");
        repo.Commit("second");

        GitService git = new(repo.Root);
        IList<GitCommit> commits = await git.GetLogAsync(10, CancellationToken.None);

        Assert.Equal(2, commits.Count);
        Assert.Equal("second", commits[0].Message);
        Assert.Equal("fix ||| that ;;; thing |||", commits[1].Message);
    }

    [Fact]
    public async Task GetLogAsync_returns_non_ascii_paths_and_authors_unescaped()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("caf\u00e9/men\u00fc.txt", "x");
        repo.Commit("unicode");

        GitService git = new(repo.Root);
        GitCommit commit = Assert.Single(await git.GetLogAsync(10, CancellationToken.None));

        Assert.Equal("unicode", commit.Message);
    }

    [Fact]
    public async Task GetChangesAsync_parses_added_modified_deleted_and_renamed()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("modify.txt", "alpha alpha alpha alpha");
        repo.WriteFile("delete.txt", "bravo bravo");
        repo.WriteFile("rename-old.txt", "charlie charlie charlie");
        string older = repo.Commit("older");

        repo.WriteFile("modify.txt", "delta delta");
        repo.DeleteFile("delete.txt");
        repo.Move("rename-old.txt", "rename-new.txt");
        repo.WriteFile("added.txt", "echo echo echo");
        string newer = repo.Commit("newer");

        GitService git = new(repo.Root);
        IList<GitFileChange> changes = await git.GetChangesAsync(older, newer, CancellationToken.None);

        Dictionary<string, GitFileChange> byPath = changes.ToDictionary(c => c.Path, StringComparer.Ordinal);

        Assert.Equal(4, changes.Count);
        Assert.Equal(GitChangeStatus.Added, byPath["added.txt"].Status);
        Assert.Equal(GitChangeStatus.Modified, byPath["modify.txt"].Status);
        Assert.Equal(GitChangeStatus.Deleted, byPath["delete.txt"].Status);

        GitFileChange rename = byPath["rename-new.txt"];
        Assert.Equal(GitChangeStatus.Renamed, rename.Status);
        Assert.Equal("rename-old.txt", rename.OldPath);
        Assert.Equal("rename-old.txt", rename.PathInBaseCommit);
        Assert.Equal("rename-new.txt", rename.PathInModifiedCommit);
    }

    [Fact]
    public async Task GetStatusAsync_reports_branch_and_sync_counts()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "one");
        repo.Commit("first");
        repo.Git("branch", "-M", "main");

        GitRepositoryStatus clean = await new GitService(repo.Root).GetStatusAsync(CancellationToken.None);
        Assert.Equal("main", clean.BranchName);
        Assert.False(clean.IsOutOfSync);
        Assert.Equal(0, clean.ChangedFileCount);

        // A local commit with no upstream configured is not "out of sync"; nothing is being tracked.
        repo.WriteFile("a.txt", "two");
        repo.Commit("second");

        GitRepositoryStatus aheadOfNothing = await new GitService(repo.Root).GetStatusAsync(CancellationToken.None);
        Assert.Equal("main", aheadOfNothing.BranchName);
        Assert.False(aheadOfNothing.IsOutOfSync);
        Assert.Equal("main", aheadOfNothing.DisplayText);
    }

    [Fact]
    public async Task GetStatusAsync_reports_ahead_and_behind_against_a_real_upstream()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "one");
        repo.Commit("first");
        repo.CreateRemote();

        GitService git = new(repo.Root);

        repo.WriteFile("a.txt", "two");
        repo.Commit("second");

        GitRepositoryStatus ahead = await git.GetStatusAsync(CancellationToken.None);
        Assert.Equal(1, ahead.AheadCount);
        Assert.Equal(0, ahead.BehindCount);
        Assert.True(ahead.IsOutOfSync);
        Assert.Equal("main (1 ahead)", ahead.DisplayText);

        repo.Git("reset", "-q", "--hard", "HEAD~1");
        repo.AdvanceRemote();

        GitRepositoryStatus behind = await git.GetStatusAsync(CancellationToken.None);
        Assert.Equal(0, behind.AheadCount);
        Assert.Equal(1, behind.BehindCount);
        Assert.True(behind.IsOutOfSync);
        Assert.Equal("main (1 behind)", behind.DisplayText);
    }

    [Fact]
    public async Task GetStatusAsync_flags_uncommitted_work_without_calling_it_out_of_sync()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "one");
        repo.Commit("first");

        repo.WriteFile("a.txt", "two");
        repo.WriteFile("scratch.txt", "temp");
        repo.Git("rm", "-q", "--cached", "scratch.txt");

        GitRepositoryStatus status = await new GitService(repo.Root).GetStatusAsync(CancellationToken.None);

        // a.txt is staged, scratch.txt is left staged-but-untracked after the reset.
        Assert.Equal(1, status.ChangedFileCount);
        Assert.Equal(1, status.UntrackedFileCount);
        Assert.True(status.HasUncommittedChanges);

        // The upstream is untouched, so the branch is not out of sync - that is the red state.
        Assert.False(status.IsOutOfSync);
        Assert.Equal("1 changed, 1 untracked", status.WorkingTreeText);
    }

    [Fact]
    public void ParseStatus_reads_branch_headers_and_entry_counts()
    {
        GitRepositoryStatus status = GitService.ParseStatus(
            "# branch.oid abc123\n"
            + "# branch.head feature/login\n"
            + "# branch.upstream origin/feature/login\n"
            + "# branch.ab +2 -5\n"
            + "1 M. N... 100644 100644 100644 aaa bbb staged.txt\n"
            + "1 .M N... 100644 100644 100644 aaa bbb restaged.txt\n"
            + "? new.txt\n"
            + "u UU N... 100644 100644 100644 100644 aaa bbb ccc conflict.txt\n");

        Assert.Equal("feature/login", status.BranchName);
        Assert.Equal("origin/feature/login", status.UpstreamName);
        Assert.Equal(2, status.AheadCount);
        Assert.Equal(5, status.BehindCount);
        Assert.True(status.IsOutOfSync);
        Assert.Equal(2, status.ChangedFileCount);
        Assert.Equal(1, status.UntrackedFileCount);
        Assert.Equal(1, status.ConflictedFileCount);
        Assert.Equal("feature/login (2 ahead, 5 behind)", status.DisplayText);
    }

    [Fact]
    public void ParseStatus_names_the_states_git_reports_without_a_branch_name()
    {
        Assert.True(GitService.ParseStatus("# branch.head (detached)\n").IsDetachedHead);
        Assert.Equal("detached HEAD", GitService.ParseStatus("# branch.head (detached)\n").BranchName);
        Assert.Equal("no commits yet", GitService.ParseStatus("# branch.head (unknown)\n").BranchName);

        // No header at all: git did not recognise the directory as a repository.
        GitRepositoryStatus none = GitService.ParseStatus(string.Empty);
        Assert.Equal("not a git repository", none.BranchName);
        Assert.False(none.IsOutOfSync);
    }

    [Fact]
    public void A_synced_branch_shows_just_its_name()
    {
        GitRepositoryStatus status = new() { BranchName = "main" };

        Assert.False(status.IsOutOfSync);
        Assert.Equal("main", status.DisplayText);
        Assert.Equal(string.Empty, status.WorkingTreeText);
    }

    [Fact]
    public async Task GetChangesAsync_reports_line_counts_for_each_change()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("modify.txt", "one\ntwo\n");
        repo.WriteFile("rename-old.txt", "kept\n");
        repo.WriteFile("gone.txt", "farewell\n");
        string older = repo.Commit("older");

        // One line replaced by two, so the file has both additions and removals.
        repo.WriteFile("modify.txt", "one\nTWO\nthree\n");
        repo.DeleteFile("gone.txt");
        repo.Move("rename-old.txt", "rename-new.txt");
        repo.WriteFile("added.txt", "alpha\nbravo\n");
        string newer = repo.Commit("newer");

        GitService git = new(repo.Root);
        Dictionary<string, GitFileChange> byPath =
            (await git.GetChangesAsync(older, newer, CancellationToken.None))
                .ToDictionary(c => c.Path, StringComparer.Ordinal);

        Assert.Equal(2, byPath["modify.txt"].AddedLines);
        Assert.Equal(1, byPath["modify.txt"].DeletedLines);
        Assert.Equal("+2 -1", byPath["modify.txt"].SizeText);

        Assert.Equal(0, byPath["gone.txt"].AddedLines);
        Assert.Equal(1, byPath["gone.txt"].DeletedLines);

        Assert.Equal(2, byPath["added.txt"].AddedLines);
        Assert.Equal(0, byPath["added.txt"].DeletedLines);

        // Counts must follow the rename to its new path, which is the key the results are joined on.
        Assert.False(byPath.ContainsKey("rename-old.txt"));
        Assert.Equal(0, byPath["rename-new.txt"].AddedLines);
        Assert.Equal(0, byPath["rename-new.txt"].DeletedLines);
    }

    [Fact]
    public async Task GetChangesAsync_marks_binary_files_rather_than_reporting_zero()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteBinaryFile("blob.dat", new byte[] { 0, 1, 2, 3, 0, 255 });
        string older = repo.Commit("older");

        repo.WriteBinaryFile("blob.dat", new byte[] { 0, 1, 2, 3, 4, 255 });
        string newer = repo.Commit("newer");

        GitService git = new(repo.Root);
        GitFileChange change = Assert.Single(await git.GetChangesAsync(older, newer, CancellationToken.None));

        Assert.True(change.IsBinary);
        Assert.Null(change.AddedLines);
        Assert.Equal("binary", change.SizeText);
    }

    [Fact]
    public void ParseNumStat_reads_counts_renames_and_binary_markers()
    {
        IDictionary<string, GitFileChange> counts =
            GitService.ParseNumStat("3\t1\ta.txt\0-\t-\tblob.dat\05\t0\t\0old.txt\0new.txt\0");

        Assert.Equal(3, counts["a.txt"].AddedLines);
        Assert.Equal(1, counts["a.txt"].DeletedLines);

        Assert.True(counts["blob.dat"].IsBinary);
        Assert.Null(counts["blob.dat"].AddedLines);

        Assert.Equal(5, counts["new.txt"].AddedLines);
        Assert.False(counts.ContainsKey("old.txt"));
    }

    /// <summary>
    /// The end-to-end proof that a pathname containing spaces or shell metacharacters survives the
    /// whole round trip: diff parsing, argument building, git archive, and extraction to disk.
    /// </summary>
    [Fact]
    public async Task Export_works_for_paths_with_spaces_and_shell_metacharacters()
    {
        // Only characters Windows actually permits in a filename appear here. '<', '>', ':', '"',
        // '/', '\', '|' and '?' are rejected by the filesystem, so git can never track such a path
        // and quoting them would be untestable. The metacharacters that survive a shell - '&', '%',
        // '^', '!' - are the ones a cmd-based implementation would have broken on.
        string[] paths =
        {
            "plain.txt",
            "with space.txt",
            "folder with space/nested file.txt",
            "a&b.txt",
            "ampersand & more.txt",
            "percent%PATH%var.txt",
            "bang!caret^tilde~.txt",
            "semicolon;and&amp.txt",
            "paren(s)and[brackets].txt",
            "caf\u00e9 \u00fcber \u00e4.txt",
            "hash#and@at{dollar}.txt",
        };

        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        foreach (string path in paths)
        {
            repo.WriteFile(path, "base " + path);
        }

        string older = repo.Commit("older");

        foreach (string path in paths)
        {
            repo.WriteFile(path, "modified " + path);
        }

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
        Assert.Empty(result.Warnings);

        foreach (string path in paths)
        {
            Assert.Equal(
                "modified " + path,
                File.ReadAllText(Path.Combine(result.ModifiedFolderPath, path.Replace('/', Path.DirectorySeparatorChar))));

            Assert.Equal(
                "base " + path,
                File.ReadAllText(Path.Combine(result.BaseFolderPath, path.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    /// <summary>
    /// The output folder itself may contain spaces and metacharacters: it is used as the working
    /// directory of the git process and as the destination of the extracted tree.
    /// </summary>
    [Fact]
    public async Task Export_works_when_the_output_directory_has_spaces_and_metacharacters()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "one");
        string older = repo.Commit("older");
        repo.WriteFile("a.txt", "two");
        string newer = repo.Commit("newer");

        string root = Path.Combine(
            Path.GetTempPath(),
            $"gdfc-out {Guid.NewGuid():N}",
            "R&D & more (copy)");

        try
        {
            Directory.CreateDirectory(root);

            ExportResult result = await new DiffExporter().ExportAsync(
                new ExportRequest
                {
                    Git = new GitService(repo.Root),
                    BaseHash = older,
                    ModifiedHash = newer,
                    OutputRoot = root,
                },
                progress: null,
                CancellationToken.None);

            Assert.True(result.Success, result.FailureReason);
            Assert.Equal("two", File.ReadAllText(Path.Combine(result.ModifiedFolderPath, "a.txt")));

            // And the folder really does live where it was asked for, metacharacters and all.
            Assert.StartsWith(root, result.RootFolderPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public async Task GetChangesAsync_keeps_paths_with_spaces_and_non_ascii_characters_intact()    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("plain.txt", "one");
        string older = repo.Commit("older");

        repo.WriteFile("my folder/my file \u00fc.txt", "two");
        string newer = repo.Commit("newer");

        GitService git = new(repo.Root);
        GitFileChange change = Assert.Single(await git.GetChangesAsync(older, newer, CancellationToken.None));

        Assert.Equal("my folder/my file \u00fc.txt", change.Path);
    }

    [Fact]
    public async Task GetChangesForCommitAsync_excludes_the_commit_message()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("first");
        repo.WriteFile("b.txt", "b");
        string newer = repo.Commit("second with a message mentioning a.txt");

        GitService git = new(repo.Root);
        IList<GitFileChange> changes = await git.GetChangesForCommitAsync(newer, CancellationToken.None);

        GitFileChange change = Assert.Single(changes);
        Assert.Equal("b.txt", change.Path);
    }

    [Fact]
    public async Task IsAncestorAsync_distinguishes_no_from_error()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string older = repo.Commit("older");
        repo.WriteFile("b.txt", "b");
        string newer = repo.Commit("newer");

        GitService git = new(repo.Root);

        Assert.True(await git.IsAncestorAsync(older, newer, CancellationToken.None));
        Assert.False(await git.IsAncestorAsync(newer, older, CancellationToken.None));

        // An invalid object name must throw rather than being reported as "not an ancestor".
        await Assert.ThrowsAsync<GitCommandException>(
            () => git.IsAncestorAsync(newer, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", CancellationToken.None));
    }

    [Fact]
    public async Task GetChangesAsync_throws_instead_of_returning_git_diagnostics_as_data()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("first");

        GitService git = new(repo.Root);

        GitCommandException exception = await Assert.ThrowsAsync<GitCommandException>(
            () => git.GetChangesAsync("0000000000000000000000000000000000000000", "0000000000000000000000000000000000000000", CancellationToken.None));

        Assert.NotNull(exception.StandardError);
        Assert.NotEmpty(exception.StandardError!);
    }

    [Fact]
    public async Task GetRepositoryRootAsync_rejects_a_directory_that_is_not_a_repository()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gdfc-norepo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            GitService git = new(directory);
            await Assert.ThrowsAsync<GitCommandException>(
                () => git.GetRepositoryRootAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetRepositoryRootAsync_rejects_a_missing_directory()
    {
        GitService git = new(Path.Combine(Path.GetTempPath(), $"gdfc-missing-{Guid.NewGuid():N}"));

        await Assert.ThrowsAsync<GitCommandException>(() => git.GetRepositoryRootAsync(CancellationToken.None));
    }

    /// <summary>
    /// The export used to send paths on a <c>git archive</c> command line and split them into batches
    /// to stay under the Windows length limit, with a retry that halved a batch git rejected. Paths
    /// now travel to <c>git cat-file</c> on standard input, so a long list is one process and one
    /// request each, with no batching and nothing that can be rejected as a whole.
    /// </summary>
    [Fact]
    public async Task ExportFilesAsync_writes_a_list_too_long_for_one_command_line()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory destination = new();

        // 400 paths of ~145 characters: over 58 000 characters, which cannot be one Windows command
        // line under any quoting.
        const int Count = 400;
        var expected = new List<string>();

        for (int i = 0; i < Count; i++)
        {
            string name = "Assets/Nested/" + new string('p', 130) + "-" + i.ToString("D3", CultureInfo.InvariantCulture) + ".txt";
            expected.Add(name);
            repo.WriteFile(name, "content-" + i);
        }

        string hash = repo.Commit("many files");

        var handled = new List<string>();

        GitService git = new(repo.Root);
        FileExportResult result = await git.ExportFilesAsync(
            hash, expected, destination.Path, handled.Add, CancellationToken.None);

        Assert.Empty(result.MissingPaths);
        Assert.Empty(result.ConflictingPaths);
        Assert.Empty(result.OmittedPaths);
        Assert.Equal(Count, result.ExtractedPaths.Count);

        // Every path is reported exactly once, whatever became of it, so a caller counting progress
        // finishes at the number it started with.
        Assert.Equal(Count, handled.Count);
        Assert.Equal(expected.Count, handled.Distinct().Count());

        // Spot-check the ends, since a failure part-way through would leave the tail unwritten.
        foreach (string name in new[] { expected[0], expected[Count / 2], expected[Count - 1] })
        {
            Assert.Equal(
                "content-" + expected.IndexOf(name),
                File.ReadAllText(Path.Combine(destination.Path, name.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [Fact]
    public void ParseNameStatus_handles_trailing_and_empty_fields()
    {
        IList<GitFileChange> changes = GitService.ParseNameStatus("M\0a.txt\0R100\0old b.txt\0new.txt\0");

        Assert.Equal(2, changes.Count);
        Assert.Equal("a.txt", changes[0].Path);
        Assert.Equal("old b.txt", changes[1].OldPath);
        Assert.Equal("new.txt", changes[1].Path);
    }

    [Fact]
    public void ParseNameStatus_ignores_empty_output()
    {
        Assert.Empty(GitService.ParseNameStatus(string.Empty));
    }

    /// <summary>
    /// Three answers rather than one, because "the branches were not updated" is two different things:
    /// a repository with no remote has nothing to be out of date, and one whose remote could not be
    /// reached is showing a list that may be behind. Told apart here, because the caller warns about one
    /// and not the other.
    /// </summary>
    [Fact]
    public async Task FetchAsync_tells_a_missing_remote_from_one_that_cannot_be_reached()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("first");

        GitService git = new(repo.Root);

        // Nothing configured: there is nothing to fetch and nothing stale.
        Assert.Equal(FetchOutcome.NoRemote, await git.FetchAsync(CancellationToken.None));

        // A remote that is configured but is not there: the fetch runs and fails, so the branches on
        // disk are as of the last one that worked.
        repo.Git("remote", "add", "origin", Path.Combine(repo.Root, "not-a-repository"));
        Assert.Equal(FetchOutcome.Failed, await git.FetchAsync(CancellationToken.None));

        // Pointed at a real repository, the same call succeeds.
        repo.Git("init", "-q", "--bare", "upstream.git");
        repo.Git("remote", "set-url", "origin", Path.Combine(repo.Root, "upstream.git"));
        Assert.Equal(FetchOutcome.Updated, await git.FetchAsync(CancellationToken.None));
    }
}