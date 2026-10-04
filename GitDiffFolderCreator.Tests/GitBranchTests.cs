using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// The branch list, and reading a branch that is not checked out.
/// </summary>
public sealed class GitBranchTests
{
    [Fact]
    public async Task GetBranchesAsync_lists_local_and_remote_branches_newest_first()
    {
        using TempRepository repo = TempRepository.Create();

        // Distinct dates: commits made in the same second share a committerdate, and the sort under
        // test would then be decided by git's tie-breaking rather than by the date.
        DateTimeOffset oldest = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset middle = oldest.AddDays(1);
        DateTimeOffset newest = oldest.AddDays(2);

        repo.WriteFile("a.txt", "first");
        string first = repo.CommitAt("first", oldest);
        repo.CreateRemote();

        repo.Git("checkout", "-q", "-b", "older-branch");
        repo.WriteFile("b.txt", "second");
        string older = repo.CommitAt("second", middle);

        repo.Git("checkout", "-q", first);
        repo.Git("checkout", "-q", "-b", "newer-branch");
        repo.WriteFile("c.txt", "third");
        string newer = repo.CommitAt("third", newest);

        GitService git = new(repo.Root);
        IList<GitBranch> branches = await git.GetBranchesAsync(CancellationToken.None);

        List<string> names = branches.Select(b => b.Name).ToList();

        Assert.Contains("newer-branch", names);
        Assert.Contains("older-branch", names);
        Assert.Contains("main", names);

        // The newest tip must come first; otherwise the list has not been sorted by date.
        Assert.True(
            names.IndexOf("newer-branch") < names.IndexOf("older-branch"),
            "Expected newer-branch before older-branch but got: " + string.Join(", ", names));

        GitBranch found = branches.First(b => b.Name == "newer-branch");
        Assert.Equal(newer, found.Hash);
        Assert.False(found.IsRemote);
    }

    [Fact]
    public async Task GetBranchesAsync_reports_which_branch_is_checked_out()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "first");
        string first = repo.Commit("first");
        repo.Git("checkout", "-q", "-b", "feature");
        repo.WriteFile("b.txt", "second");
        repo.Commit("second");

        GitService git = new(repo.Root);
        IList<GitBranch> branches = await git.GetBranchesAsync(CancellationToken.None);

        Assert.True(branches.Single(b => b.Name == "feature").IsCurrent);
        Assert.False(branches.Single(b => b.Name == "main").IsCurrent);
        Assert.NotNull(first);
    }

    [Fact]
    public async Task GetBranchesAsync_marks_remote_tracking_branches()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "first");
        repo.Commit("first");
        repo.CreateRemote();
        repo.WriteFile("b.txt", "second");
        repo.Commit("second");

        GitService git = new(repo.Root);
        IList<GitBranch> branches = await git.GetBranchesAsync(CancellationToken.None);

        GitBranch remote = branches.First(b => b.IsRemote);
        Assert.StartsWith("origin/", remote.Name);
        Assert.StartsWith("refs/remotes/", remote.RefName);

        // The remote's name without its remote prefix, which is what the list shows as the label.
        Assert.DoesNotContain("origin/", remote.DisplayName);
    }

    /// <summary>
    /// origin/HEAD is a symbolic pointer at the default remote branch. Listing it would offer the
    /// user a second entry for a branch already in the list.
    /// </summary>
    [Fact]
    public async Task GetBranchesAsync_leaves_out_the_remotes_head_pointer()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "first");
        repo.Commit("first");
        repo.CreateRemote();
        repo.Git("remote", "set-head", "origin", "-a");
        repo.WriteFile("b.txt", "second");
        repo.Commit("second");

        GitService git = new(repo.Root);
        IList<GitBranch> branches = await git.GetBranchesAsync(CancellationToken.None);

        Assert.DoesNotContain(branches, b => b.Name == "origin");
        Assert.DoesNotContain(branches, b => b.Name.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The whole point of naming a ref: the log of a branch can be read while the working tree stays
    /// on something else.
    /// </summary>
    [Fact]
    public async Task GetLogAsync_reads_a_branch_without_checking_it_out()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "first");
        repo.Commit("first");
        repo.Git("checkout", "-q", "-b", "feature");
        repo.WriteFile("b.txt", "on feature");
        repo.Commit("feature commit");
        repo.Git("checkout", "-q", "main");
        repo.WriteFile("c.txt", "on master");
        repo.Commit("master commit");

        string headBefore = repo.RevParse("HEAD");
        string branchBefore = repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

        GitService git = new(repo.Root);
        IList<GitCommit> commits = await git.GetLogAsync(10, "feature", CancellationToken.None);

        Assert.Contains(commits, c => c.Message == "feature commit");
        Assert.DoesNotContain(commits, c => c.Message == "master commit");

        // Reading must have changed nothing about the checkout.
        Assert.Equal(branchBefore, repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.Equal(headBefore, repo.RevParse("HEAD"));
    }

    [Fact]
    public async Task GetLogAsync_without_a_revision_still_reads_head()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "first");
        repo.Commit("first");
        repo.Git("checkout", "-q", "-b", "feature");
        repo.WriteFile("b.txt", "on feature");
        repo.Commit("feature commit");

        GitService git = new(repo.Root);

        IList<GitCommit> fromHead = await git.GetLogAsync(10, CancellationToken.None);
        IList<GitCommit> fromBranch = await git.GetLogAsync(10, "feature", CancellationToken.None);
        IList<GitCommit> fromFullRef = await git.GetLogAsync(10, "refs/heads/feature", CancellationToken.None);

        // HEAD is on feature here, so all three must agree.
        Assert.Equal(fromHead[0].Hash, fromBranch[0].Hash);
        Assert.Equal(fromHead[0].Hash, fromFullRef[0].Hash);
    }

    [Fact]
    public async Task GetLogAsync_accepts_a_bare_hash_and_a_tag()
    {
        using TempRepository repo = TempRepository.Create();

        repo.WriteFile("a.txt", "first");
        string hash = repo.Commit("first");
        repo.Git("tag", "v1");
        repo.WriteFile("b.txt", "second");
        repo.Commit("second");

        GitService git = new(repo.Root);

        IList<GitCommit> byHash = await git.GetLogAsync(10, hash, CancellationToken.None);
        IList<GitCommit> byTag = await git.GetLogAsync(10, "v1", CancellationToken.None);

        Assert.Equal("first", byHash[0].Message);
        Assert.Equal(hash, byHash[0].Hash);

        // A tag is a valid revision, so a released version can be compared like any other ref.
        Assert.Equal("first", byTag[0].Message);
    }

    /// <summary>
    /// Export reads a commit directly out of the object database, so a range spanning two branches
    /// works without either being checked out.
    /// </summary>
    [Fact]
    public async Task ExportAsync_writes_a_comparison_spanning_two_branches_without_checking_out()
    {
        using TempRepository repo = TempRepository.Create();
        using TempDirectory output = new();

        repo.WriteFile("shared.cs", "base version");
        string baseCommit = repo.Commit("base");

        repo.Git("checkout", "-q", "-b", "feature");
        repo.WriteFile("feature.cs", "only on feature");

        // Changed on both sides, so it appears in the base folder and demonstrates that the base
        // side is read from the base commit rather than from the checked-out tree.
        repo.WriteFile("shared.cs", "feature version");
        string featureCommit = repo.Commit("feature work");
        repo.Git("checkout", "-q", "main");

        string headBefore = repo.RevParse("HEAD");

        DiffExporter exporter = new();
        ExportResult result = await exporter.ExportAsync(
            new ExportRequest
            {
                Git = new GitService(repo.Root),
                BaseHash = baseCommit,
                ModifiedHash = featureCommit,
                OutputRoot = output.Path,
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("only on feature", File.ReadAllText(Path.Combine(result.ModifiedFolderPath, "feature.cs")));
        Assert.Equal("base version", File.ReadAllText(Path.Combine(result.BaseFolderPath, "shared.cs")));

        // The export ran entirely from objects; the checkout never moved.
        Assert.Equal(headBefore, repo.RevParse("HEAD"));
        Assert.Equal("main", repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.False(File.Exists(Path.Combine(repo.Root, "feature.cs")));
    }

    [Fact]
    public void ParseBranches_reads_the_for_each_ref_shape()
    {
        const char sep = GitServiceConstants.FieldSeparator;

        string output = string.Join("\n", new[]
        {
            // refname, short name, object name, committerdate, upstream, upstream:track, HEAD.
            string.Join(sep.ToString(), new[]
            {
                "refs/heads/feature", "feature", "abc123", "2026-03-04T10:00:00+00:00", "", "", "*",
            }),
            string.Join(sep.ToString(), new[]
            {
                "refs/remotes/origin/main", "origin/main", "def456", "2026-03-01T09:30:00+00:00",
                "", "", "",
            }),
        });

        IList<GitBranch> branches = GitService.ParseBranches(output);

        Assert.Equal(2, branches.Count);

        GitBranch local = branches[0];
        Assert.Equal("feature", local.Name);
        Assert.True(local.IsCurrent);
        Assert.False(local.IsRemote);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero), local.CommitDate);

        GitBranch remote = branches[1];
        Assert.Equal("origin/main", remote.Name);
        Assert.Equal("main", remote.DisplayName);
        Assert.True(remote.IsRemote);
        Assert.False(remote.IsCurrent);
    }

    [Fact]
    public void ParseBranches_keeps_an_unparsable_date_rather_than_dropping_the_branch()
    {
        const char sep = GitServiceConstants.FieldSeparator;

        string output = string.Join(sep.ToString(), new[]
        {
            string.Join(sep.ToString(), new[]
            {
                "refs/heads/weird", "weird", "abc123", "not-a-date", "", "", "",
            }),
        });

        IList<GitBranch> branches = GitService.ParseBranches(output);

        // Losing the date is survivable; losing the branch would make it unselectable.
        Assert.Single(branches);
        Assert.Null(branches[0].CommitDate);
        Assert.Equal("not-a-date", branches[0].DateText);
    }

    /// <summary>
    /// The counts come out of one <c>for-each-ref</c> line, so the whole sync state of a branch is
    /// parsed without asking git for anything else.
    /// </summary>
    [Fact]
    public void ParseBranches_reads_the_upstream_and_divergence_counts()
    {
        const char sep = GitServiceConstants.FieldSeparator;

        string Line(string refName, string name, string upstream, string track, bool isCurrent) =>
            string.Join(sep.ToString(), new[]
            {
                refName, name, "abc123", "2026-03-04T10:00:00+00:00", upstream, track,
                isCurrent ? "*" : string.Empty,
            });

        string output = string.Join("\n", new[]
        {
            Line("refs/heads/main", "main", "origin/main", "[ahead 2, behind 1]", true),
            Line("refs/heads/feature", "feature", "origin/feature", "[behind 3]", false),
            Line("refs/heads/local-only", "local-only", string.Empty, string.Empty, false),
        });

        IList<GitBranch> branches = GitService.ParseBranches(output);

        GitBranch main = branches[0];
        Assert.Equal("origin/main", main.UpstreamName);
        Assert.True(main.HasUpstream);
        Assert.Equal(2, main.AheadCount);
        Assert.Equal(1, main.BehindCount);
        Assert.True(main.IsOutOfSync);
        Assert.Equal("+2 -1", main.SyncText);

        // Only one of the two directions out of sync is still out of sync, and only that side shows.
        GitBranch feature = branches[1];
        Assert.Equal(3, feature.BehindCount);
        Assert.Equal(0, feature.AheadCount);
        Assert.Equal("-3", feature.SyncText);

        // A branch with no upstream is not out of sync; there is nothing to be out of sync with.
        GitBranch local = branches[2];
        Assert.False(local.HasUpstream);
        Assert.False(local.IsOutOfSync);
        Assert.Equal(string.Empty, local.SyncText);
    }

    /// <summary>
    /// git names the two counts in a fixed order today, but the format is documentation, not a
    /// guarantee, so both words are looked for rather than read from a position.
    /// </summary>
    [Theory]
    [InlineData("[ahead 4, behind 5]", 4, 5)]
    [InlineData("[behind 5, ahead 4]", 4, 5)]
    public void ApplyTrack_reads_the_counts_in_either_order(string track, int ahead, int behind)
    {
        GitBranch branch = new GitBranch { Name = "b", UpstreamName = "origin/b" }.ApplyTrack(track);

        Assert.Equal(ahead, branch.AheadCount);
        Assert.Equal(behind, branch.BehindCount);
    }

    /// <summary>
    /// A branch level with its upstream produces an empty track field. Anything unexpected in it
    /// must leave the counts at zero rather than throwing the listing out.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[gone]")]
    [InlineData("[ahead many]")]
    public void ApplyTrack_leaves_the_counts_at_zero_for_anything_it_cannot_read(string track)
    {
        GitBranch branch = new GitBranch { Name = "b" }.ApplyTrack(track);

        Assert.Equal(0, branch.AheadCount);
        Assert.Equal(0, branch.BehindCount);
        Assert.False(branch.IsOutOfSync);
    }

    [Fact]
    public void ParseBranches_skips_blank_and_truncated_lines()
    {
        Assert.Empty(GitService.ParseBranches(string.Empty));
        Assert.Empty(GitService.ParseBranches("\n\n"));
        Assert.Empty(GitService.ParseBranches("refs/heads/x" + GitServiceConstants.FieldSeparator + "x"));
    }
}