using GitDiffFolderCreator.Models;
using Xunit;

namespace GitDiffFolderCreator.Tests;

public sealed class GitFileChangeDisplayTests
{
    [Theory]
    [InlineData("Models/Scenario.cs", "Scenario.cs", "Models/")]
    [InlineData("README.md", "README.md", "")]
    [InlineData("a/b/c/deep/file.txt", "file.txt", "a/b/c/deep/")]
    [InlineData("dir\\windows\\file.cs", "file.cs", "dir\\windows\\")]
    public void The_file_name_is_split_from_its_folder(
        string path, string expectedName, string expectedDirectory)
    {
        var change = new GitFileChange { Path = path };

        Assert.Equal(expectedName, change.FileName);
        Assert.Equal(expectedDirectory, change.DirectoryText);
    }

    [Theory]
    [InlineData(23, 0, "+23", "-0")]
    [InlineData(0, 12, "+0", "-12")]
    [InlineData(1, 1, "+1", "-1")]
    public void The_line_counts_are_shown_with_their_sign(int added, int deleted, string plus, string minus)
    {
        var change = new GitFileChange { AddedLines = added, DeletedLines = deleted };

        Assert.Equal(plus, change.AddedText);
        Assert.Equal(minus, change.DeletedText);
        Assert.True(change.HasLineCounts);
    }

    [Fact]
    public void A_binary_file_says_so_once_instead_of_claiming_no_changes()
    {
        var change = new GitFileChange { IsBinary = true };

        Assert.Equal("binary", change.AddedText);
        Assert.Equal(string.Empty, change.DeletedText);
        Assert.True(change.HasLineCounts);
    }

    [Fact]
    public void A_file_with_no_counts_leaves_the_columns_empty()
    {
        var change = new GitFileChange();

        Assert.Equal(string.Empty, change.AddedText);
        Assert.Equal(string.Empty, change.DeletedText);
        Assert.False(change.HasLineCounts);
    }

    /// <summary>The badge shows one letter, so a similarity score such as R100 must not leak into it.</summary>
    [Fact]
    public void The_badge_is_the_first_letter_of_the_status_code()
    {
        Assert.Equal("R", new GitFileChange { StatusCode = "R100" }.StatusBadge);
        Assert.Equal("M", new GitFileChange { StatusCode = "M" }.StatusBadge);
        Assert.Equal(string.Empty, new GitFileChange { StatusCode = string.Empty }.StatusBadge);
    }

    /// <summary>The badge letters used to need a legend line; the tooltip carries the word instead.</summary>
    [Theory]
    [InlineData(GitChangeStatus.Added, "Added")]
    [InlineData(GitChangeStatus.Modified, "Modified")]
    [InlineData(GitChangeStatus.Deleted, "Deleted")]
    [InlineData(GitChangeStatus.Renamed, "Renamed")]
    [InlineData(GitChangeStatus.Copied, "Copied")]
    [InlineData(GitChangeStatus.TypeChanged, "Type changed")]
    [InlineData(GitChangeStatus.Unmerged, "Unmerged")]
    [InlineData(GitChangeStatus.Unknown, "Changed")]
    public void Every_status_has_a_word_for_its_tooltip(GitChangeStatus status, string expected)
    {
        Assert.Equal(expected, new GitFileChange { Status = status }.StatusLabel);
    }

    [Fact]
    public void A_file_is_included_until_the_user_says_otherwise()
    {
        var change = new GitFileChange();
        Assert.True(change.IsIncluded);

        change.IsIncluded = false;
        Assert.False(change.IsIncluded);
    }

    [Fact]
    public void Unticking_a_file_is_announced_so_the_row_can_redraw()
    {
        var change = new GitFileChange();
        int notifications = 0;
        string? lastProperty = null;

        change.PropertyChanged += (_, e) =>
        {
            notifications++;
            lastProperty = e.PropertyName;
        };

        change.IsIncluded = false;

        Assert.Equal(1, notifications);
        Assert.Equal(nameof(GitFileChange.IsIncluded), lastProperty);
    }

    /// <summary>Setting the value it already has must not repaint every visible row.</summary>
    [Fact]
    public void An_unchanged_value_raises_nothing()
    {
        var change = new GitFileChange();
        int notifications = 0;
        change.PropertyChanged += (_, _) => notifications++;

        change.IsIncluded = true;

        Assert.Equal(0, notifications);
    }
}
