using GitDiffFolderCreator.Controls;
using Xunit;

namespace GitDiffFolderCreator.Tests;

public sealed class CollapsibleGroupBoxTests
{
    /// <summary>
    /// The panel defaults are what every existing panel relies on, so a change to them would silently
    /// strip the headers off the rest of the window.
    /// </summary>
    [Fact]
    public void A_panel_has_a_foldable_header_unless_it_says_otherwise()
    {
        Sta.Run(() =>
        {
            var panel = new CollapsibleGroupBox();

            Assert.True(panel.ShowHeader);
            Assert.True(panel.IsCollapsible);
            Assert.True(panel.IsExpanded);
        });
    }

    [Fact]
    public void A_headerless_fixed_panel_is_still_open()
    {
        Sta.Run(() =>
        {
            var panel = new CollapsibleGroupBox { ShowHeader = false, IsCollapsible = false };

            Assert.False(panel.ShowHeader);
            Assert.False(panel.IsCollapsible);

            // Nothing can fold it, so folding state is meaningless rather than sticky-off.
            Assert.True(panel.IsExpanded);
        });
    }

    [Theory]
    [InlineData(true, 90.0)]
    [InlineData(false, 0.0)]
    public void The_chevron_points_down_while_open_and_right_while_folded(bool expanded, double expectedAngle)
    {
        Sta.Run(() =>
        {
            var panel = new CollapsibleGroupBox { IsExpanded = expanded };

            Assert.Equal(expectedAngle, CollapsibleGroupBox.GetChevronAngle(panel));
        });
    }
}
