using System.Windows;
using GitDiffFolderCreator.Controls;
using GitDiffFolderCreator.Models;
using Xunit;

namespace GitDiffFolderCreator.Tests;

public sealed class BindingConverterTests
{
    private readonly CommitRoleToVisibilityConverter _roles = new CommitRoleToVisibilityConverter();
    private readonly StringToVisibilityConverter _text = new StringToVisibilityConverter();

    [Theory]
    [InlineData(GitCommitRole.Base, "Base", true)]
    [InlineData(GitCommitRole.Modified, "Modified", true)]
    // A pill must never show on a role it does not name, or every row would wear both badges.
    [InlineData(GitCommitRole.Base, "Modified", false)]
    [InlineData(GitCommitRole.Modified, "Base", false)]
    [InlineData(GitCommitRole.None, "Base", false)]
    [InlineData(GitCommitRole.None, "Modified", false)]
    public void A_role_pill_shows_only_for_the_role_it_names(
        GitCommitRole role, string pill, bool expectedVisible)
    {
        Assert.Equal(
            expectedVisible ? Visibility.Visible : Visibility.Collapsed,
            _roles.Convert(role, typeof(Visibility), pill, null));
    }

    [Fact]
    public void A_missing_or_unusable_parameter_hides_the_pill_rather_than_guessing()
    {
        Assert.Equal(Visibility.Collapsed, _roles.Convert(GitCommitRole.Base, typeof(Visibility), null, null));
        Assert.Equal(Visibility.Collapsed, _roles.Convert(GitCommitRole.Base, typeof(Visibility), "base", null));
        Assert.Equal(Visibility.Collapsed, _roles.Convert(null, typeof(Visibility), "Base", null));
    }

    [Theory]
    [InlineData("origin/main", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_chip_shows_only_when_there_is_text_to_put_in_it(string? text, bool expectedVisible)
    {
        Assert.Equal(
            expectedVisible ? Visibility.Visible : Visibility.Collapsed,
            _text.Convert(text, typeof(Visibility), null, null));
    }
}
