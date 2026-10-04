using GitDiffFolderCreator.Services;
using Xunit;

namespace GitDiffFolderCreator.Tests;

public sealed class WindowsArgumentStringTests
{
    [Fact]
    public void A_plain_argument_is_left_alone()
    {
        Assert.Equal("git status", WindowsArgumentString.Build(new[] { "git", "status" }));
    }

    [Fact]
    public void A_space_produces_a_quoted_argument()
    {
        Assert.Equal(
            "\"my folder/my file.txt\"",
            WindowsArgumentString.Build(new[] { "my folder/my file.txt" }));
    }

    [Fact]
    public void Shell_metacharacters_are_not_treated_as_syntax()
    {
        // Nothing is expanded here because no shell is involved, so '&' and friends need no
        // escaping at all. Quoting them would be harmless but would make the command line wrong to
        // read, and a "fix" that added escaping here would encode characters git must see raw.
        Assert.Equal(
            "a&b.exe",
            WindowsArgumentString.Build(new[] { "a&b.exe" }));

        Assert.Equal(
            "\"C:\\out dir/a&b.txt\"",
            WindowsArgumentString.Build(new[] { "C:\\out dir/a&b.txt" }));

        Assert.Equal(
            "a|b>c<d^e%f%PATH%!g",
            WindowsArgumentString.Build(new[] { "a|b>c<d^e%f%PATH%!g" }));
    }

    [Fact]
    public void Trailing_backslashes_before_the_closing_quote_are_doubled()
    {
        // Without doubling, a backslash immediately before " escapes the quote and the argument
        // runs into the next one.
        Assert.Equal(
            "\"C:\\out dir\\\\\"",
            WindowsArgumentString.Build(new[] { "C:\\out dir\\" }));
    }

    [Fact]
    public void An_embedded_quote_is_escaped()
    {
        Assert.Equal(
            "\"say \\\"hi\\\".txt\"",
            WindowsArgumentString.Build(new[] { "say \"hi\".txt" }));
    }

    [Fact]
    public void An_empty_argument_stays_separate_from_its_neighbours()
    {
        Assert.Equal(
            "a \"\" b",
            WindowsArgumentString.Build(new[] { "a", string.Empty, "b" }));
    }

    [Fact]
    public void Arguments_are_separated_by_exactly_one_space()
    {
        Assert.Equal(
            "-c core.quotepath=false diff",
            WindowsArgumentString.Build(new[] { "-c", "core.quotepath=false", "diff" }));
    }
}
