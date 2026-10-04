using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// The commit details dialog: reading a whole commit on demand, and parsing the record git produces
/// for it.
/// </summary>
/// <remarks>
/// Built on a real repository rather than canned output, because the record this parses is the one
/// the parser was written against; a hand-written sample only proves it can read itself.
/// </remarks>
public sealed class CommitDetailTests
{
    private static GitService ServiceFor(TempRepository repo) => new(repo.Root);

    private static GitCommit RowFor(TempRepository repo, string hash)
    {
        return new GitCommit
        {
            Hash = hash,
            ShortHash = hash.Substring(0, 7),
            Message = "from the log row",
        };
    }

    [Fact]
    public async Task A_commit_detail_reports_the_author_the_dates_and_the_whole_hash()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.Commit("the subject");

        GitService git = ServiceFor(repo);
        GitCommitDetail detail = await git.GetCommitDetailAsync(hash, CancellationToken.None);

        Assert.Equal(hash, detail.Hash);
        Assert.Equal(40, detail.Hash.Length);
        Assert.Equal("Test User", detail.AuthorName);
        Assert.Equal("test@example.com", detail.AuthorEmail);
        Assert.Equal("Test User", detail.CommitterName);
        Assert.Equal("the subject", detail.Subject);

        // Strict ISO-8601, not a locale-formatted date that would reorder under another language.
        Assert.Contains("T", detail.AuthorDateText, StringComparison.Ordinal);
        Assert.StartsWith("20", detail.AuthorDateText, StringComparison.Ordinal);
        Assert.NotEmpty(detail.AuthorDateText);
        Assert.NotEmpty(detail.CommitterDateText);
    }

    [Fact]
    public async Task The_full_hash_is_not_the_abbreviated_one_the_log_row_shows()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.Commit("subject");

        GitService git = ServiceFor(repo);
        GitCommitDetail detail = await git.GetCommitDetailAsync(hash, CancellationToken.None);

        // Pasting the log row's 7 characters back into a git command is ambiguous in a big
        // repository, so the dialog must carry all 40 or none of the point is lost.
        Assert.True(
            detail.Hash.Length > detail.ShortHash.Length,
            "The details dialog would offer no more than the log row already showed.");
    }

    [Fact]
    public async Task A_multi_paragraph_body_survives_intact()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.CommitWithBody("the subject", "first paragraph\nsecond line\n\nthird paragraph");

        GitService git = ServiceFor(repo);
        GitCommitDetail detail = await git.GetCommitDetailAsync(hash, CancellationToken.None);

        // The body is the last field on purpose, and it is rejoined rather than split, so the blank
        // line in the middle has to come through with everything else.
        Assert.Equal("first paragraph\nsecond line\n\nthird paragraph", detail.Body);
        Assert.True(detail.HasBody);
    }

    [Fact]
    public async Task The_body_does_not_repeat_the_subject()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.CommitWithBody("fix the thing", "why it was broken");

        GitService git = ServiceFor(repo);
        GitCommitDetail detail = await git.GetCommitDetailAsync(hash, CancellationToken.None);

        // %B repeats the subject and %b does not. Asking for %B would show the same first line
        // twice: once in the header and again at the top of the message box.
        Assert.DoesNotContain("fix the thing", detail.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_commit_with_no_body_reports_an_empty_one_rather_than_the_subject()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.Commit("only a subject");

        GitService git = ServiceFor(repo);
        GitCommitDetail detail = await git.GetCommitDetailAsync(hash, CancellationToken.None);

        Assert.Equal(string.Empty, detail.Body);
        Assert.False(detail.HasBody);
    }

    [Fact]
    public async Task A_body_containing_the_field_separator_does_not_lose_its_tail()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");

        // The separator the format uses between fields, planted inside the message.
        string marker = GitServiceConstants.FieldSeparator.ToString();
        string hash = repo.CommitWithBody("subject", $"before{marker}after");

        GitService git = ServiceFor(repo);
        GitCommitDetail detail = await git.GetCommitDetailAsync(hash, CancellationToken.None);

        // A naive split would cut the body at the first separator and lose "after".
        Assert.Contains("before", detail.Body, StringComparison.Ordinal);
        Assert.Contains("after", detail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_root_commit_reports_no_parents_and_a_later_one_reports_its_parent()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string first = repo.Commit("first");
        repo.WriteFile("b.txt", "b");
        string second = repo.Commit("second");

        GitService git = ServiceFor(repo);

        Assert.Empty((await git.GetCommitDetailAsync(first, CancellationToken.None)).Parents);
        Assert.Equal(
            first,
            Assert.Single((await git.GetCommitDetailAsync(second, CancellationToken.None)).Parents));
    }

    [Fact]
    public async Task A_merge_commit_reports_both_of_its_parents()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string root = repo.Commit("root");

        repo.Git("checkout", "-q", "-b", "side");
        repo.WriteFile("side.txt", "side");
        string side = repo.Commit("side work");

        repo.Git("checkout", "-q", "main");
        repo.WriteFile("main.txt", "main");
        repo.Commit("main work");

        repo.Git("merge", "-q", "--no-ff", "-m", "merge the side work", "side");
        string merge = repo.RevParse("HEAD");

        GitService git = ServiceFor(repo);
        IList<string> parents = (await git.GetCommitDetailAsync(merge, CancellationToken.None)).Parents;

        Assert.Equal(2, parents.Count);
        Assert.Contains(side, parents);
        Assert.NotNull(root);
    }

    [Fact]
    public async Task A_tag_shows_up_in_the_details_of_the_commit_it_points_at()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.Commit("released");
        repo.Git("tag", "v1.2.3");

        GitService git = ServiceFor(repo);
        GitCommitDetail detail = await git.GetCommitDetailAsync(hash, CancellationToken.None);

        // The user asked whether tags are visible; this is where they are confirmed to survive the
        // round trip into the dialog.
        Assert.Contains("v1.2.3", detail.RefNames, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_hash_is_reported_rather_than_returned_as_an_empty_commit()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("first");

        GitService git = ServiceFor(repo);

        await Assert.ThrowsAnyAsync<Exception>(
            () => git.GetCommitDetailAsync("0123456789abcdef0123456789abcdef01234567", CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_hash_is_refused_before_git_is_run()
    {
        using TempRepository repo = TempRepository.Create();
        GitService git = ServiceFor(repo);

        await Assert.ThrowsAsync<ArgumentException>(
            () => git.GetCommitDetailAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task The_files_a_commit_changed_come_back_with_their_line_counts()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("kept.txt", "one\n");
        string first = repo.Commit("first");

        repo.WriteFile("kept.txt", "one\ntwo\nthree\n");
        repo.WriteFile("added.txt", "new\n");
        string second = repo.Commit("second");

        GitService git = ServiceFor(repo);

        // The second commit only: the file list describes one commit, not the history up to it.
        IList<GitFileChange> changed = await git.GetChangesForCommitAsync(second, CancellationToken.None);

        Assert.Equal(2, changed.Count);

        GitFileChange modified = changed.Single(f => f.Path == "kept.txt");
        GitFileChange added = changed.Single(f => f.Path == "added.txt");

        Assert.Equal(GitChangeStatus.Added, added.Status);
        Assert.Equal(GitChangeStatus.Modified, modified.Status);
        Assert.Equal(2, modified.AddedLines);
        Assert.NotNull(first);
    }

    [Fact]
    public async Task The_view_model_starts_from_the_row_so_the_window_is_never_blank()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.Commit("subject");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));

        // Before any git call returns, the header must already say which commit this is.
        Assert.Equal("from the log row", model.Subject);
        Assert.Equal(hash.Substring(0, 7), model.ShortHash);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Loading_fills_in_everything_the_log_row_left_out()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.CommitWithBody("the subject", "the body");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));
        await model.LoadAsync(CancellationToken.None);

        Assert.False(model.HasError);
        Assert.False(model.IsLoading);

        // The row's placeholder is replaced by git's own value.
        Assert.Equal("the subject", model.Subject);
        Assert.Equal(hash, model.FullHash);
        Assert.Equal("Test User <test@example.com>", model.AuthorLine);
        Assert.Equal("the body", model.Body);
        Assert.True(model.HasBody);
        Assert.Single(model.Files);
        Assert.False(model.IsFiltered);
    }

    [Fact]
    public async Task The_file_totals_add_up_the_lines_git_reported()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "one\n");
        repo.Commit("first");

        repo.WriteFile("a.txt", "one\ntwo\nthree\nfour\n");
        repo.WriteFile("b.txt", "new\n");
        string hash = repo.Commit("second");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));
        await model.LoadAsync(CancellationToken.None);

        Assert.Equal(2, model.TotalFileCount);
        Assert.Equal(4, model.TotalAddedLines);
        Assert.Equal(0, model.TotalDeletedLines);
        Assert.Equal("2 files changed, +4 -0", model.FileSummary);
    }

    [Fact]
    public async Task A_single_changed_file_is_named_in_the_singular()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "one\n");
        repo.Commit("first");

        repo.WriteFile("a.txt", "one\ntwo\n");
        string hash = repo.Commit("second");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));
        await model.LoadAsync(CancellationToken.None);

        Assert.Equal("1 file changed, +1 -0", model.FileSummary);
    }

    [Fact]
    public async Task A_binary_file_is_counted_separately_because_it_has_no_lines()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "one\n");
        repo.Commit("first");

        repo.WriteBinaryFile("blob.bin", new byte[] { 0, 1, 2, 3, 0, 255 });
        string hash = repo.Commit("add a binary");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));
        await model.LoadAsync(CancellationToken.None);

        Assert.Equal(1, model.BinaryFileCount);
        Assert.Equal(0, model.TotalAddedLines);

        // Said outright, because otherwise an absent line count reads as "nothing changed in it".
        Assert.Contains("binary", model.FileSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_commit_that_changed_nothing_says_so_rather_than_saying_zero_files_of_nothing()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("first");

        // An empty commit is the plain case of a commit that touched nothing: the dialog must say
        // so rather than showing an empty list with no explanation for it.
        string hash = repo.CommitEmpty("nothing changed at all");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));
        await model.LoadAsync(CancellationToken.None);

        Assert.Equal("No files changed", model.FileSummary);
    }

    [Fact]
    public async Task The_filter_narrows_the_file_list_but_not_the_totals()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("src/one.cs", "a");
        repo.WriteFile("docs/readme.md", "b");
        repo.WriteFile("src/two.cs", "c");
        string hash = repo.Commit("several files");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));
        await model.LoadAsync(CancellationToken.None);

        Assert.Equal(3, model.Files.Count);

        model.Filter = "src";

        Assert.Equal(2, model.Files.Count);
        Assert.True(model.IsFiltered);

        // The summary describes the commit, not the filtered view, so a short list is never
        // mistaken for a small commit.
        Assert.Equal(3, model.TotalFileCount);
        Assert.Contains("3 files changed", model.FileSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_filter_ignores_case_and_matches_any_part_of_the_path()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("src/Deep/One.cs", "a");
        repo.WriteFile("docs/readme.md", "b");
        string hash = repo.Commit("two files");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));
        await model.LoadAsync(CancellationToken.None);

        model.Filter = "DEEP";

        Assert.Equal("src/Deep/One.cs", Assert.Single(model.Files).Path);
    }

    [Fact]
    public async Task A_rename_is_found_by_the_name_it_had()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("old-name.txt", "a");
        repo.Commit("first");

        repo.Move("old-name.txt", "new-name.txt");
        string hash = repo.Commit("rename it");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));
        await model.LoadAsync(CancellationToken.None);

        model.Filter = "old-name";

        // Searching the path the user remembers, not only the one the commit ended with.
        Assert.Single(model.Files);
    }

    [Fact]
    public async Task Clearing_the_filter_brings_every_file_back()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        repo.WriteFile("b.txt", "b");
        string hash = repo.Commit("two files");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));
        await model.LoadAsync(CancellationToken.None);

        model.Filter = "a";
        Assert.Single(model.Files);

        model.Filter = string.Empty;

        Assert.Equal(2, model.Files.Count);
        Assert.False(model.IsFiltered);
    }

    [Fact]
    public async Task A_bad_hash_becomes_a_message_in_the_window_rather_than_an_exception()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("first");

        var row = new GitCommit { Hash = "0123456789abcdef0123456789abcdef01234567", ShortHash = "0123456" };
        var model = new CommitDetailViewModel(ServiceFor(repo), row);

        // The user opened a window to read a commit. Taking the application down over one row
        // would be a far worse outcome than the message.
        await model.LoadAsync(CancellationToken.None);

        Assert.True(model.HasError);
        Assert.False(string.IsNullOrWhiteSpace(model.ErrorMessage));
        Assert.Empty(model.Files);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task A_repeated_load_does_not_run_twice_over_the_same_commit()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.Commit("first");

        var model = new CommitDetailViewModel(ServiceFor(repo), RowFor(repo, hash));

        await Task.WhenAll(model.LoadAsync(CancellationToken.None), model.LoadAsync(CancellationToken.None));

        // Whichever of the two lost the race must not have started a second pair of git calls.
        Assert.False(model.IsLoading);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task A_commit_with_no_refs_leaves_the_ref_field_empty_rather_than_guessing()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string first = repo.Commit("first");

        // Only then the second file, so the first commit is left behind and no longer the tip.
        repo.WriteFile("b.txt", "b");
        string second = repo.Commit("second");

        GitService git = ServiceFor(repo);

        // Neither commit is a branch tip, so git reports no refs for them.
        Assert.Equal(string.Empty, (await git.GetCommitDetailAsync(first, CancellationToken.None)).RefNames);
        Assert.NotEmpty((await git.GetCommitDetailAsync(second, CancellationToken.None)).RefNames);
    }
}
