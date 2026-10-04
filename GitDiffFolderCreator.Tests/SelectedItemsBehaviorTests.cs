using System.Collections.Generic;
using System.Linq;
using GitDiffFolderCreator.Behaviors;
using Xunit;

namespace GitDiffFolderCreator.Tests;

public sealed class SelectedItemsBehaviorTests
{
    [Fact]
    public void A_third_click_replaces_the_oldest_and_keeps_the_new_pair()
    {
        // Commits are listed newest first, so list order is the opposite of click order.
        List<object> current = new() { "a", "b", "c" };
        List<object> previous = new() { "a", "b" };

        List<object> keep = SelectedItemsBehavior.SelectToKeep(current, previous, 2);

        Assert.Equal(new object[] { "c", "b" }, keep);
    }

    [Fact]
    public void A_click_on_an_older_commit_is_never_discarded()
    {
        // Picking commits 5, then 3, then 9 must keep 3 and 9 - 3 sorts before 5 in the log.
        List<object> current = new() { "c", "e", "i" };
        List<object> previous = new() { "c", "e" };

        List<object> keep = SelectedItemsBehavior.SelectToKeep(current, previous, 2);

        Assert.Equal(new object[] { "i", "e" }, keep);
    }

    [Fact]
    public void A_third_click_that_sorts_first_still_survives()
    {
        // 9, then 5, then 3: the newest click is the topmost row and must not be dropped.
        List<object> current = new() { "c", "e", "i" };
        List<object> previous = new() { "e", "i" };

        List<object> keep = SelectedItemsBehavior.SelectToKeep(current, previous, 2);

        Assert.Equal(new object[] { "c", "i" }, keep);
    }

    [Fact]
    public void A_shift_range_keeps_the_end_of_the_range()
    {
        List<object> current = new() { "a", "b", "c", "d", "e" };
        List<object> previous = new() { "a" };

        List<object> keep = SelectedItemsBehavior.SelectToKeep(current, previous, 2);

        Assert.Equal(new object[] { "e", "d" }, keep);
    }

    [Fact]
    public void Without_history_the_last_items_in_list_order_win()
    {
        List<object> keep = SelectedItemsBehavior.SelectToKeep(new object[] { "a", "b", "c" }, null, 2);

        Assert.Equal(new object[] { "c", "b" }, keep);
    }

    [Fact]
    public void A_selection_within_the_cap_is_untouched()
    {
        List<object> current = new() { "a", "b" };

        Assert.Equal(current, SelectedItemsBehavior.SelectToKeep(current, new object[] { "a" }, 2));
    }

    [Fact]
    public void Removing_an_item_never_reorders_the_survivors()
    {
        // Ctrl-clicking away from a full selection shrinks it and must be passed through as-is.
        List<object> current = new() { "a", "c" };

        List<object> keep = SelectedItemsBehavior.SelectToKeep(current, new object[] { "a", "b", "c" }, 2);

        Assert.Equal(new object[] { "a", "c" }, keep);
    }

    [Fact]
    public void An_unlimited_selection_is_left_alone()
    {
        List<object> current = new() { "a", "b", "c" };

        Assert.Equal(current, SelectedItemsBehavior.SelectToKeep(current, null, 0));
    }
}
