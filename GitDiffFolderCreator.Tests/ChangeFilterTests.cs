using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

public sealed class ChangeFilterTests
{
    [Fact]
    public void The_added_option_filters_on_A_and_is_not_mistaken_for_All()
    {
        char code = GitViewModel.StatusFilterCode("A added");

        Assert.Equal('A', code);
        Assert.NotEqual('\0', code);
    }

    [Theory]
    [InlineData("All", '\0')]
    [InlineData("  All  ", '\0')]
    [InlineData("all", '\0')]
    [InlineData(null, '\0')]
    [InlineData("", '\0')]
    [InlineData("M modified", 'M')]
    [InlineData("D deleted", 'D')]
    [InlineData("R renamed", 'R')]
    [InlineData("C copied", 'C')]
    [InlineData("T type changed", 'T')]
    public void Every_combo_box_option_maps_to_a_distinct_code(string? option, char expected)
    {
        Assert.Equal(expected, GitViewModel.StatusFilterCode(option));
    }

    [Fact]
    public void Every_offered_option_maps_to_a_code_different_from_All()
    {
        foreach (string option in GitViewModel.AllStatusFilterOptions)
        {
            if (option == GitViewModel.AllStatusFilter)
            {
                continue;
            }

            Assert.True(
                GitViewModel.StatusFilterCode(option) != '\0',
                $"'{option}' would behave like 'All' and show every file.");
        }
    }

    [Fact]
    public void A_status_filter_keeps_only_that_status()
    {
        GitFileChange added = Change("A", "new.txt");
        GitFileChange modified = Change("M", "old.txt");

        Assert.True(GitViewModel.Matches(added, string.Empty, 'A'));
        Assert.False(GitViewModel.Matches(modified, string.Empty, 'A'));
    }

    [Fact]
    public void No_status_filter_keeps_everything()
    {
        Assert.True(GitViewModel.Matches(Change("A", "new.txt"), string.Empty, '\0'));
        Assert.True(GitViewModel.Matches(Change("D", "gone.txt"), string.Empty, '\0'));
    }

    [Fact]
    public void The_path_filter_matches_the_new_path_and_the_old_path_of_a_rename()
    {
        GitFileChange renamed = new()
        {
            Status = GitChangeStatus.Renamed,
            StatusCode = "R100",
            OldPath = "before/name.txt",
            Path = "after/name.txt",
        };

        Assert.True(GitViewModel.Matches(renamed, "after", '\0'));
        Assert.True(GitViewModel.Matches(renamed, "BEFORE", '\0'));
        Assert.False(GitViewModel.Matches(renamed, "elsewhere", '\0'));
    }

    [Fact]
    public void The_path_and_status_filters_combine()
    {
        GitFileChange added = Change("A", "src/added.txt");
        GitFileChange otherAdded = Change("A", "docs/added.txt");

        Assert.True(GitViewModel.Matches(added, "src", 'A'));
        Assert.False(GitViewModel.Matches(otherAdded, "src", 'A'));
    }

    private static GitFileChange Change(string statusCode, string path) => new()
    {
        Status = statusCode[0] switch
        {
            'A' => GitChangeStatus.Added,
            'D' => GitChangeStatus.Deleted,
            'M' => GitChangeStatus.Modified,
            _ => GitChangeStatus.Unknown,
        },
        StatusCode = statusCode,
        Path = path,
    };
}
