using System.IO;
using GitDiffFolderCreator.Services;
using Xunit;

namespace GitDiffFolderCreator.Tests;

public sealed class ShellFolderPickerTests
{
    [Fact]
    public void A_full_folder_path_is_taken_as_the_result()
    {
        using TempDirectory directory = new();

        string? result = ShellFolderPicker.ResolveChosenPath(directory.Path, null);

        Assert.Equal(Path.GetFullPath(directory.Path), result);
    }

    [Fact]
    public void A_folder_name_typed_relative_to_the_opened_folder_is_resolved()
    {
        using TempDirectory parent = new();
        string child = Path.Combine(parent.Path, "child");
        Directory.CreateDirectory(child);

        string? result = ShellFolderPicker.ResolveChosenPath("child", parent.Path);

        Assert.Equal(Path.GetFullPath(child), result);
    }

    [Fact]
    public void A_name_that_is_not_a_folder_falls_back_to_its_parent()
    {
        using TempDirectory parent = new();
        string file = Path.Combine(parent.Path, "notes.txt");
        File.WriteAllText(file, "x");

        string? result = ShellFolderPicker.ResolveChosenPath(file, null);

        Assert.Equal(Path.GetFullPath(parent.Path), result);
    }

    [Fact]
    public void An_unusable_result_returns_the_folder_that_was_opened()
    {
        using TempDirectory directory = new();

        string? result = ShellFolderPicker.ResolveChosenPath("C:\\definitely\\not\\here", directory.Path);

        Assert.Equal(directory.Path, result);
    }

    [Fact]
    public void An_empty_result_means_the_user_cancelled()
    {
        Assert.Null(ShellFolderPicker.ResolveChosenPath(null, "C:\\anywhere"));
        Assert.Null(ShellFolderPicker.ResolveChosenPath("   ", "C:\\anywhere"));
    }

    [Fact]
    public void The_untouched_placeholder_selects_the_folder_that_was_opened()
    {
        using TempDirectory directory = new();

        Assert.Equal(
            directory.Path,
            ShellFolderPicker.ResolveChosenPath(ShellFolderPicker.PlaceholderText, directory.Path));

        Assert.Equal(
            directory.Path,
            ShellFolderPicker.ResolveChosenPath("  select folder  ", directory.Path));
    }
}

public sealed class ShellFolderOpenerTests
{
    [Fact]
    public void Opening_a_missing_path_launches_nothing()
    {
        using TempDirectory directory = new();

        Assert.False(ShellFolderOpener.Open(null));
        Assert.False(ShellFolderOpener.Open("   "));
        Assert.False(ShellFolderOpener.Open(Path.Combine(directory.Path, "absent")));
    }
}
