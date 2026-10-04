using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Shapes;
using GitDiffFolderCreator.Behaviors;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// The window's own markup. Loading it is the only check that catches a binding or a resource key
/// naming something the view model does not have, because neither error is reported by the build.
/// </summary>
[Collection(nameof(WindowTests))]
public sealed class MainWindowTests
{
    /// <summary>
    /// The window's styles and converters live in the application's merged dictionary, and an
    /// <see cref="Application"/> belongs to the thread that created it. A new STA thread per test would
    /// leave the second and later windows looking for resources owned by a thread that has finished,
    /// so every test here shares one thread and one application.
    /// </summary>
    private static readonly WindowHost Host = new WindowHost();

    [Fact]
    public void The_window_loads_with_its_view_model_in_place()
    {
        Host.Run(window =>
        {
            var viewModel = Assert.IsType<GitViewModel>(window.DataContext);

            // The badge names the branch on screen, so its fallback state has to be drawable before any
            // repository is loaded.
            Assert.False(viewModel.HasActiveBranch);
            Assert.Equal(string.Empty, viewModel.ActiveBranchName);
        });
    }

    /// <summary>
    /// Stop holds the window edge and the progress bar sits to its left. When the bar came after the
    /// button, appearing for an export pushed the button sideways, so the one control a cancelling
    /// user reaches for moved at the moment they needed it.
    /// </summary>
    [Fact]
    public void The_stop_button_holds_the_right_edge_with_the_bar_beside_it()
    {
        Host.Run(window =>
        {
            Button? stop = null;
            FrameworkElement? bar = null;

            foreach (UIElement element in EnumerateDescendants(window))
            {
                if (element is Button button && Equals(button.Content, "Stop"))
                {
                    stop = button;
                }

                if (element is FrameworkElement framework
                    && string.Equals(framework.Name, "ExportTrack", StringComparison.Ordinal))
                {
                    bar = framework;
                }
            }

            Assert.NotNull(stop);
            Assert.NotNull(bar);

            // The bar's column belongs to the grid that holds it, not to the track border itself, so
            // that is what has to be read. The bar is its own grid, so the row is the nearest ancestor
            // that is a column layout rather than the first grid found.
            Grid? barRow = FindColumnLayoutAncestor(bar!);
            Assert.NotNull(barRow);

            // The bar's track is nested one grid deep, so the column to compare is the one its
            // immediate parent occupies in the row.
            FrameworkElement? barPanel = bar!.Parent as FrameworkElement;
            Assert.NotNull(barPanel);

            // Both are placed in the same row, and Stop is in a later column of it.
            Assert.Equal(4, barRow!.ColumnDefinitions.Count);
            Assert.Same(barRow, stop!.Parent);
            Assert.Same(barRow, barPanel.Parent);

            int stopColumn = Grid.GetColumn(stop);
            int barColumn = Grid.GetColumn(barPanel);

            Assert.True(
                stopColumn > barColumn,
                $"Stop sits in column {stopColumn} and the bar in {barColumn}, so the button can still move.");

            // The row is kept at its full height by the button whether or not a run is in progress, so
            // nothing above it shifts when the bar appears.
            Assert.Equal(34d, stop.Height);
        });
    }

    /// <summary>The nearest ancestor laid out in columns, skipping grids that only stack children.</summary>
    private static Grid? FindColumnLayoutAncestor(DependencyObject? start)
    {
        for (DependencyObject? node = start; node != null; node = LogicalTreeHelper.GetParent(node))
        {
            if (node is Grid grid && grid.ColumnDefinitions.Count > 0)
            {
                return grid;
            }
        }

        return null;
    }

    /// <summary>
    /// The fill has to be driven by the reported fraction, or the bar is decoration. Reading the two
    /// columns proves the binding exists and that they are complementary, so the fill and the
    /// remainder together always make one whole track.
    /// </summary>
    [Fact]
    public void The_bar_fill_is_driven_by_the_reported_fraction()
    {
        Host.Run(window =>
        {
            var host = window.FindName("ExportFillHost") as Grid;
            Assert.True(host != null, "There is no grid to hold the determinate fill.");

            Assert.Equal(2, host!.ColumnDefinitions.Count);

            ColumnDefinition fill = host.ColumnDefinitions[0];
            ColumnDefinition remainder = host.ColumnDefinitions[1];

            // Read the binding expression rather than the value: with no data context the column has
            // already resolved to zero, and it is the binding that has to be inspected.
            Assert.Equal("ExportProgressFraction", BoundPath(fill, ColumnDefinition.WidthProperty));
            Assert.Equal("ExportProgressRemainder", BoundPath(remainder, ColumnDefinition.WidthProperty));

            // The fill must be the first column, or it is drawn outside the remainder.
            Assert.Equal(0, Grid.GetColumn((UIElement)window.FindName("ExportFill")));
        });
    }

    /// <summary>The path a target's binding reads, or <c>null</c> when it has none.</summary>
    private static string? BoundPath(DependencyObject target, DependencyProperty property) =>
        (BindingOperations.GetBindingExpression(target, property) as BindingExpression)?.ParentBinding.Path.Path;

    /// <summary>
    /// The one step with no figure is the git diff that produces the file list. An empty bar through
    /// the longest step of the run reads as a hang, so that step slides - and it must slide only
    /// then, or a bar with a real number also has a segment travelling across it.
    /// </summary>
    [Fact]
    public void The_bar_slides_only_while_the_total_is_unknown()
    {
        Host.Run(window =>
        {
            var slide = window.FindName("ExportSlide") as FrameworkElement;
            Assert.True(slide != null, "The sliding segment is missing from the markup.");

            // Read the storyboard by name: a trigger does not hand its action over for inspection,
            // so the markup is named and looked up instead.
            var story = window.FindName("ExportSlideStory") as BeginStoryboard;
            Assert.True(story != null, "No storyboard is attached to the segment.");

            DoubleAnimation? animation = story!.Storyboard.Children.OfType<DoubleAnimation>()
                .FirstOrDefault(a => Storyboard.GetTargetName(a) == "ExportSlideOffset");

            Assert.True(animation != null, "Nothing animates the segment, so it would sit still.");
            Assert.Equal(RepeatBehavior.Forever, story.Storyboard.RepeatBehavior);

            // The travel has to carry the segment clear of the track at both ends, or it is seen
            // popping into existence. The widest track is the bar's MaxWidth, and the segment is the
            // one-star column of a one-to-three split.
            double track = 420d;
            double segment = track / 4d;
            Assert.True(
                (double)animation!.From!.Value <= -segment,
                "The segment starts inside the track, so it pops in.");
            Assert.True(
                (double)animation.To!.Value >= track,
                "The segment stops inside the track, so it pops out.");

            // And it is shown only while the count is unknown - which is a property of the grid holding the
            // segment, since that is what is shown or hidden as a whole.
            Assert.Equal(
                "IsExportProgressKnown",
                BoundPath((DependencyObject)slide!.Parent, UIElement.VisibilityProperty));
        });
    }

    private static string? ReadVisibilityBinding(FrameworkElement element) =>
        BoundPath(element, UIElement.VisibilityProperty);

    /// <summary>
    /// The window is four bands on a grey surface, and they only read as four bands if each one has a
    /// frame, a fill of its own, and room inside it. A band that loses any of the three merges into
    /// its neighbour, which is exactly what happened when the action bar was the only one styled.
    /// </summary>
    [Fact]
    public void Every_band_is_framed_and_padded_so_they_read_as_four()
    {
        Host.Run(window =>
        {
            var root = Assert.IsType<Grid>(window.Content);

            Assert.Equal(4, root.RowDefinitions.Count);

            // GetChildren is the non-generic IEnumerable, and the grid's row definitions come back as
            // logical children too, so the bands are the elements among them, in declaration order.
            List<FrameworkElement> bands = LogicalTreeHelper.GetChildren(root)
                .Cast<object>()
                .OfType<FrameworkElement>()
                .ToList();

            Assert.Equal(4, bands.Count);

            // The lists row holds two cards side by side rather than being one itself, so the cards
            // are counted, not the bands: every band must contribute at least one, and the lists row
            // must contribute exactly two.
            List<FrameworkElement> cards = bands.SelectMany(CardsIn).ToList();

            Assert.Equal(5, cards.Count);

            Brush? expectedBorder = null;

            for (int i = 0; i < cards.Count; i++)
            {
                FrameworkElement card = cards[i];

                // Border derives from Decorator and a collapsible panel from Control, so the four
                // properties that make a card read as one are read through whichever this is.
                Brush? background;
                Brush? borderBrush;
                Thickness borderThickness;
                Thickness padding;

                switch (card)
                {
                    case Border borderElement:
                        background = borderElement.Background;
                        borderBrush = borderElement.BorderBrush;
                        borderThickness = borderElement.BorderThickness;
                        padding = borderElement.Padding;
                        break;

                    case Control control:
                        background = control.Background;
                        borderBrush = control.BorderBrush;
                        borderThickness = control.BorderThickness;
                        padding = control.Padding;
                        break;

                    default:
                        Assert.Fail("Card " + i + " is a " + card.GetType().Name + ".");
                        return;
                }

                Assert.True(
                    borderThickness == new Thickness(1),
                    "Card " + i + " has a border of " + borderThickness + ", so it has no visible edge.");

                // A fill of its own, or the grey window surface shows through and the card vanishes.
                Assert.True(
                    background is SolidColorBrush,
                    "Card " + i + " has no fill, so it is the same colour as the window behind it.");

                Assert.True(
                    padding.Left > 0 && padding.Top > 0,
                    "Card " + i + " is not inset, so its content touches the frame.");

                // The same frame on all of them, or they read as unrelated things rather than one
                // layout.
                var border = Assert.IsType<SolidColorBrush>(borderBrush);
                if (expectedBorder == null)
                {
                    expectedBorder = border;
                }
                else
                {
                    Assert.Equal(expectedBorder, border);
                }
            }
        });
    }

    /// <summary>
    /// The repository card is one row of fields, each with its label above the control it names, in
    /// the order the design lays them out. That is the whole point of the arrangement: the previous
    /// two rows put the second row's labels beside controls with nothing above them, so the pairs
    /// had to be inferred.
    /// </summary>
    [Fact]
    public void The_repository_card_puts_each_label_above_its_own_control()
    {
        Host.Run(window =>
        {
            var card = FindRepositoryCard(window);

            var columns = LogicalTreeHelper.GetChildren(card)
                .Cast<object>()
                .OfType<Grid>()
                .Single();

            // Four columns of controls and the trailing state, in the order the design lays them out.
            // The commit count and Refresh were moved back onto this card for that reason.
            Assert.Equal(5, columns.ColumnDefinitions.Count);

            // Only the repository path is flexible. Everything else is sized to its own content, so
            // the controls keep their natural spacing at any width and the path - the one field whose
            // text can be arbitrarily long - takes what is left.
            for (int i = 1; i < columns.ColumnDefinitions.Count; i++)
            {
                Assert.True(
                    columns.ColumnDefinitions[i].Width.GridUnitType == GridUnitType.Auto,
                    "Column " + i + " is not sized to its content, so the controls beside it are pushed around.");
            }

            Assert.Equal(
                new GridLength(1, GridUnitType.Star),
                columns.ColumnDefinitions[0].Width);

            // Three named fields, then the button, then the state. Walking to the deepest controls
            // would find the same TextBlocks either way, so each label has to be checked where it is:
            // as the first thing in its own field, before the control it names.
            var fields = LogicalTreeHelper.GetChildren(columns)
                .Cast<object>()
                .OfType<FrameworkElement>()
                .Where(element => Grid.GetColumn(element) < 3)
                .ToList();

            Assert.Equal(3, fields.Count);

            foreach (FrameworkElement field in fields)
            {
                var children = LogicalTreeHelper.GetChildren(field).Cast<object>().ToList();

                Assert.True(
                    children.OfType<TextBlock>().Any(),
                    "A field has no label above its control.");

                Assert.True(
                    children.Skip(1).Any(),
                    "A field has a label but nothing under it.");
            }

            // The design's order: repository, branch, how many commits, then Refresh. Asserted as
            // positions rather than as markup, because the arrangement is the point and the markup is
            // only how it is written down.
            Assert.Equal(
                new[] { 0, 1, 2 },
                fields.Select(field => Grid.GetColumn(field)).ToArray());

            // The last one is the state, which the design puts outside the field arrangement: it is
            // text rather than a control, so it has no label above it.
            Assert.IsType<TextBlock>(
                LogicalTreeHelper.GetChildren(columns)
                    .Cast<object>()
                    .OfType<FrameworkElement>()
                    .Single(element => Grid.GetColumn(element) == 4));
        });
    }

    /// <summary>
    /// The commit count and Refresh sit on the repository card, in the design's order: after the two
    /// fields that choose what is read, and before the trailing state. They are about reading the
    /// branch, so they read as part of the same strip as the repository and branch rather than as
    /// controls stranded on the list they act on.
    /// </summary>
    [Fact]
    public void The_commit_count_and_refresh_sit_beside_the_fields_they_read()
    {
        Host.Run(window =>
        {
            FrameworkElement card = FindRepositoryCard(window);

            // Both controls have to be on this card, in one row with the two fields: being somewhere
            // on the window was never the arrangement in question.
            TextBox limit = EnumerateDescendants(card)
                .OfType<TextBox>()
                .Single(box => BoundPath(box, TextBox.TextProperty) == "LogLimit");

            Button refresh = EnumerateDescendants(card)
                .OfType<Button>()
                .Single(button => button.Content as string == "Refresh");

            // And still wired to what they were: a control that moved and lost its binding still sits
            // in the right place while doing nothing.
            Assert.Equal("LogLimit", BoundPath(limit, TextBox.TextProperty));
            Assert.Equal("RefreshLogCommand", BoundPath(refresh, ButtonBase.CommandProperty));

            // The header is a row rather than a stack, so the controls are positioned along it and a
            // control that was merely dropped into the card would fail these.
            Assert.True(
                FindColumnLayoutAncestor(limit!) == FindColumnLayoutAncestor(refresh!),
                "The commit count and Refresh are not on the same row, so they are not one strip.");

            // The design's widths, which are what make the row read as the design: the count is a
            // fixed 70 because it is a number, and Refresh follows it.
            Assert.Equal(70d, limit!.Width);
            Assert.True(
                Grid.GetColumn(refresh) > Grid.GetColumn(limit),
                "Refresh is not to the right of the commit count.");

            // The count is labelled by the field it sits in, and keeps its unit. The unit used to sit
            // between the box and the button, where it read as a label for the button. The field is
            // the card row's own child that contains the box, because the box's parent alone is the
            // inner stack and holds only the unit, and because Grid.GetColumn would answer for the
            // inner stack's grid rather than for the card's row.
            var columns = LogicalTreeHelper.GetChildren(card)
                .Cast<object>()
                .OfType<Grid>()
                .Single();

            var field = LogicalTreeHelper.GetChildren(columns)
                .Cast<object>()
                .OfType<FrameworkElement>()
                .Single(element => EnumerateDescendants(element).Contains(limit));

            Assert.Contains(
                EnumerateDescendants(field).OfType<TextBox>(),
                box => ReferenceEquals(box, limit));

            var texts = EnumerateDescendants(field).OfType<TextBlock>().Select(text => text.Text).ToList();

            Assert.Contains("Show last", texts);
            Assert.Contains("commits", texts);
        });
    }

    /// <summary>
    /// The controls on the repository row share one bottom edge.
    /// </summary>
    /// <remarks>
    /// This is the whole point of bottom-aligning the row, and it is invisible in the markup. The row
    /// is as tall as a labelled field — label above control — so every control in it is a 34-tall band
    /// with a caption above it. A control that is merely centred or stretched in that row sits a
    /// label's height too high or too low, and reads as belonging to the label beside it rather than
    /// to the row.
    /// </remarks>
    [Fact]
    public void The_controls_on_the_repository_row_share_one_bottom_edge()
    {
        Host.Run(window =>
        {
            window.Show();
            window.UpdateLayout();
            Drain();

            var columns = LogicalTreeHelper.GetChildren(FindRepositoryCard(window))
                .Cast<object>()
                .OfType<Grid>()
                .Single();

            // The rendered bottom edge of each control, in the row's own coordinates, so the
            // comparison is against the layout that is actually drawn rather than against what the
            // alignment properties claim.
            double BottomOf(FrameworkElement element) =>
                element.TransformToAncestor(columns)
                    .Transform(new Point(0, element.ActualHeight))
                    .Y;

            List<FrameworkElement> controls = new List<FrameworkElement>
            {
                // Repository path, the count, and Refresh: the three controls at three different
                // depths of nesting in the row, and the ones that can drift apart.
                EnumerateDescendants(columns).OfType<TextBox>()
                    .Single(box => BoundPath(box, TextBox.TextProperty) == "GitDirectory"),
                EnumerateDescendants(columns).OfType<TextBox>()
                    .Single(box => BoundPath(box, TextBox.TextProperty) == "LogLimit"),
                EnumerateDescendants(columns).OfType<Button>()
                    .Single(button => button.Content as string == "Refresh"),
            };

            foreach (FrameworkElement control in controls)
            {
                Assert.True(control.ActualHeight > 0, "A control on the repository row was never laid out.");
            }

            double baseline = BottomOf(controls[0]);

            for (int i = 1; i < controls.Count; i++)
            {
                Assert.True(
                    Math.Abs(BottomOf(controls[i]) - baseline) < 0.5,
                    "A control on the repository row ends at "
                        + BottomOf(controls[i]) + " rather than " + baseline
                        + ", so it does not line up with the rest.");
            }
        });
    }

    /// <summary>The repository card: the first band of the window.</summary>
    private static FrameworkElement FindRepositoryCard(MainWindow window)
    {
        var root = Assert.IsType<Grid>(window.Content);

        return LogicalTreeHelper.GetChildren(root)
            .Cast<object>()
            .OfType<FrameworkElement>()
            .Single(element => Grid.GetRow(element) == 0);
    }

    /// <summary>
    /// The commit card's heading row carries the heading, the stage text and the progress ring, and
    /// nothing else. The ring is beside the text rather than pinned to the right edge, because an
    /// element pinned to the right slides everything beside it every time a load starts.
    /// </summary>
    [Fact]
    public void The_commit_heading_row_carries_the_heading_the_stage_and_the_ring()
    {
        Host.Run(window =>
        {
            var listsRow = LogicalTreeHelper.GetChildren(Assert.IsType<Grid>(window.Content))
                .Cast<object>()
                .OfType<FrameworkElement>()
                .Single(element => Grid.GetRow(element) == 1);

            FrameworkElement commitsCard = CardsIn(listsRow).First();

            FrameworkElement heading = EnumerateDescendants(commitsCard)
                .OfType<TextBlock>()
                .Single(text => text.Text == "COMMITS");

            FrameworkElement ring = EnumerateDescendants(commitsCard)
                .OfType<FrameworkElement>()
                .Single(element => ReferenceEquals(element.FindName("LoadingRingRotation"), element.RenderTransform));

            // One row, so the three travel together rather than the ring sitting on its own line.
            Assert.Same(
                FindColumnLayoutAncestor(heading),
                FindColumnLayoutAncestor(ring));

            // The ring is only meaningful while a load is running, so the binding is the other half of
            // the arrangement: a ring drawn unconditionally is a spinner that never stops.
            Assert.Equal("IsLoadingLog", ReadVisibilityBinding(ring));
        });
    }

    /// <summary>
    /// The badge draws one of three states, and each is shown by a binding to a different flag. A path
    /// that names a property the view model does not have fails silently: the binding reports a path
    /// error, never throws, and the target keeps whatever it was declared with. Visibility's declared
    /// value is Visible, so a broken flag draws its badge unconditionally - and because the three sit
    /// in one row, the branch name appears twice on an out-of-sync branch.
    /// </summary>
    [Fact]
    public void Only_the_badge_for_the_branchs_actual_state_is_drawn()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "content");
        repo.Commit("first");
        repo.CreateRemote();

        // A commit the remote gets and this repository does not, so main falls behind its upstream.
        repo.AdvanceRemote();

        Host.Run(window =>
        {
            var viewModel = (GitViewModel)window.DataContext;

            window.Show();
            viewModel.GitDirectory = repo.Root;
            WaitForBranches(viewModel);

            window.UpdateLayout();
            Drain();

            // The setup has to be the out-of-sync case, or the test would pass with the wrong badge
            // drawn.
            Assert.True(viewModel.IsActiveBranchOutOfSync);
            Assert.Equal("main", viewModel.ActiveBranchName);

            // The three flags the badge is built from, named rather than pattern-matched: a path that
            // has drifted from the view model is the very thing being guarded against, so guessing at
            // the name here would hide it.
            string[] statePaths =
            {
                "IsActiveBranchOutOfSync",
                "IsActiveBranchInSync",
                "IsActiveBranchUntracked",
            };

            List<FrameworkElement> badges = EnumerateDescendants(window)
                .OfType<Border>()
                .Where(element => statePaths.Contains(BoundPath(element, UIElement.VisibilityProperty)))
                .Cast<FrameworkElement>()
                .ToList();

            // Three states, and each has to have found its flag.
            Assert.Equal(3, badges.Count);

            foreach (FrameworkElement badge in badges)
            {
                var expression = (BindingExpression)BindingOperations.GetBindingExpression(
                    badge, UIElement.VisibilityProperty)!;

                Assert.True(
                    expression.Status == BindingStatus.Active,
                    "The " + expression.ParentBinding.Path.Path + " badge is in state " + expression.Status
                        + ", so it is drawing itself regardless of the branch.");
            }

            // One name, not two: exactly one badge is showing, and it is the warning.
            FrameworkElement shown = Assert.Single(badges, badge => badge.Visibility == Visibility.Visible);
            Assert.Equal("IsActiveBranchOutOfSync", BoundPath(shown, UIElement.VisibilityProperty));
        });
    }

    /// <summary>
    /// The boxes and buttons are one family, and the thing that makes them one is a shared corner
    /// radius. They are drawn by implicit styles rather than by key, so a control that forgets to ask
    /// for one still looks like the rest; the radius is a resource so the number cannot drift.
    /// </summary>
    [Fact]
    public void Every_box_and_button_carries_the_same_corner_radius()
    {
        Host.Run(window =>
        {
            window.Show();
            window.UpdateLayout();
            WindowHost.Drain(window.Dispatcher);

            // The radius every one of them has to arrive with. Read as a number rather than compared
            // to the resource so a style that stopped using the resource would fail here instead of
            // quietly agreeing with itself.
            var expected = new CornerRadius(4);

            List<Control> controls = EnumerateDescendants(window)
                .OfType<Control>()
                .Where(IsTemplatedField)
                .ToList();

            // The repository path, the commit count, the two filter boxes, the export buttons, the
            // small row buttons and the dropdown. A list this short means the walk missed something.
            Assert.True(
                controls.Count >= 10,
                "Only found " + controls.Count + " fields, so the walk is not reaching the whole window.");

            foreach (Control control in controls)
            {
                var border = (Border)control.Template.FindName("Root", control)!;

                Assert.True(
                    border.CornerRadius == expected,
                    control.GetType().Name + " has a corner radius of " + border.CornerRadius
                        + " rather than " + expected + ".");
            }

            // The dropdown draws its field inside the toggle rather than in its own template, so it
            // has to be asked for separately or it is the one control on the window left unmeasured.
            ComboBox combo = FindDescendant<ComboBox>(window)!;
            var toggle = (ToggleButton)combo.Template.FindName("Toggle", combo)!;
            var comboBorder = (Border)toggle.Template.FindName("Root", toggle)!;

            Assert.Equal(expected, comboBorder.CornerRadius);

            // It is still a working dropdown, not just a rounded rectangle: the template has to show
            // the selection and open a list of the right length.
            Assert.False(string.IsNullOrEmpty(combo.Text));

            combo.IsDropDownOpen = true;
            window.UpdateLayout();
            WindowHost.Drain(window.Dispatcher);

            var popup = (Popup)combo.Template.FindName("PART_Popup", combo)!;
            Assert.True(popup.IsOpen, "The dropdown did not open.");
            Assert.NotNull(popup.Child);

            combo.IsDropDownOpen = false;
        });
    }

    /// <summary>
    /// The text box and the dropdown stand next to each other in the filter row and are read as one
    /// control, so they have to hold their text at the same distance from their border. The dropdown
    /// is the one that drifts: its padding lives on a style setter, and a custom template that forgets
    /// to apply it does not fail - the box simply closes up around its text while the text box beside
    /// it stays inset, and the pair stops reading as one control.
    /// </summary>
    [Fact]
    public void The_dropdown_holds_its_text_where_the_text_boxes_do()
    {
        Host.Run(window =>
        {
            window.Show();
            window.UpdateLayout();
            WindowHost.Drain(window.Dispatcher);

            List<TextBox> boxes = new List<TextBox>();
            CollectRealised(window, boxes);

            Assert.NotEmpty(boxes);

            foreach (TextBox box in boxes)
            {
                // Read what the template actually did with the padding, not what the style declared:
                // the declared value is the same either way, which is why the drift is invisible to
                // everything except this.
                var scroll = (ScrollViewer)box.Template.FindName("PART_ContentHost", box)!;

                Assert.True(
                    box.Padding.Left == scroll.Margin.Left,
                    "A text box declares an inset of " + box.Padding.Left
                        + " but draws its text " + scroll.Margin.Left + " from the edge.");
            }

            ComboBox combo = FindDescendant<ComboBox>(window)!;
            var toggle = (ToggleButton)combo.Template.FindName("Toggle", combo)!;
            ContentPresenter presenter = FindContentPresenter(toggle)!;

            Assert.True(
                presenter.Margin.Left == boxes[0].Padding.Left,
                "The dropdown draws its text " + presenter.Margin.Left
                    + " from the edge where the text boxes draw theirs " + boxes[0].Padding.Left + ".");
        });
    }

    private static ContentPresenter? FindContentPresenter(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is ContentPresenter presenter)
            {
                return presenter;
            }

            ContentPresenter? found = FindContentPresenter(child);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Swap reverses the two hashes printed beside it, so it belongs beside them. It used to sit at the
    /// far end of the row, past the diff-tool button, which made the user read across the panel to
    /// connect the two: the button named an action on the hashes and the hashes were nowhere near it.
    /// </summary>
    [Fact]
    public void Swap_sits_against_the_hashes_it_reverses()
    {
        Host.Run(window =>
        {
            window.Show();
            window.UpdateLayout();
            WindowHost.Drain(window.Dispatcher);

            Button swap = EnumerateDescendants(window)
                .OfType<Button>()
                .Single(button => button.Content as string == "Swap");

            TextBlock caption = EnumerateDescendants(window)
                .OfType<TextBlock>()
                .Single(text => BoundPath(text, TextBlock.TextProperty) == "RangeCaption");

            double captionLeft = caption.TranslatePoint(new Point(0, 0), window).X;
            double captionRight = captionLeft + caption.ActualWidth;
            double swapLeft = swap.TranslatePoint(new Point(0, 0), window).X;

            // After it, and close enough to belong to it. The gap is the button's own margin.
            Assert.True(
                swapLeft >= captionRight,
                "Swap starts at " + swapLeft + " but the hashes end at " + captionRight
                    + ", so it is not after them.");

            Assert.True(
                swapLeft - captionRight <= 16,
                "Swap starts " + (swapLeft - captionRight)
                    + "px after the hashes, so it reads as a separate control rather than as theirs.");

            // The command is what actually reverses the range, so the button moved must not have lost
            // its wiring on the way.
            Assert.Equal("SwapRangeCommand", BoundPath(swap, ButtonBase.CommandProperty));
        });
    }

    /// <summary>
    /// A text box takes no keystrokes, shows no caret and cannot be scrolled unless its template has
    /// the content host the platform attaches all three to. Replacing the template to round the
    /// corners is exactly the change that loses it, and the box still looks correct while doing so.
    /// </summary>
    [Fact]
    public void A_restyled_text_box_still_takes_input()
    {
        Host.Run(window =>
        {
            window.Show();
            window.UpdateLayout();
            WindowHost.Drain(window.Dispatcher);

            // The visual tree rather than the logical one: a template is only applied to something that
            // has been realised, and the logical tree also reports the text box inside the branch
            // picker's popup, which is closed here and so has no template to inspect.
            List<TextBox> boxes = new List<TextBox>();
            CollectRealised(window, boxes);

            Assert.NotEmpty(boxes);

            foreach (TextBox box in boxes)
            {
                Assert.True(
                    box.Template.FindName("PART_ContentHost", box) is ScrollViewer,
                    "A text box has no content host, so it cannot be typed into.");
            }
        });
    }

    /// <summary>Every text box that has actually been realised into the visual tree.</summary>
    private static void CollectRealised(DependencyObject root, List<TextBox> found)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is TextBox box)
            {
                found.Add(box);
            }

            CollectRealised(child, found);
        }
    }

    /// <summary>
    /// A control whose border the shared style draws, and so must wear the shared radius.
    /// <para>
    /// Only the three control types the implicit styles cover. A check box is a tick rather than a box
    /// and the branch badge is a field with its own shell drawn around it - that button's own border is
    /// the hover tint inside the shell, so it is deliberately a shade tighter than the shell and must
    /// not be dragged up to match. Both are told apart by having content that is not a caption: a real
    /// button's content is the text on its face.
    /// </para>
    /// </summary>
    private static bool IsTemplatedField(Control control) =>
        (control is TextBox or ComboBox
            || (control is Button button && button.Content is string))
        && control.Template?.FindName("Root", control) is Border;

    /// <summary>The cards a band presents to the surface. A band is normally its own card, but the lists row
    /// is a plain grid holding two of them, so it has to be asked rather than assumed.
    /// </summary>
    private static IEnumerable<FrameworkElement> CardsIn(FrameworkElement band)
    {
        if (band is Border || band is Control)
        {
            yield return band;
            yield break;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(band))
        {
            // A grid's column definitions are logical children as well, and are not elements.
            if (child is Border card)
            {
                yield return card;
            }
        }
    }

    /// <summary>
    /// A commit row carries the refs pointing at it, and <c>%D</c> packs several together -
    /// <c>HEAD -&gt; main, origin/main, tag: v1.0.0</c>. The chip used to cap them at 150px, about
    /// twenty characters, so anything with a remote or a tag lost exactly the interesting tail to an
    /// ellipsis, and there was no tooltip to read the rest back. Both halves matter: a generous cap
    /// for the common case, and the full text still reachable for the rest.
    /// </summary>
    [Fact]
    public void Commit_ref_names_are_not_cut_off_to_an_unreadable_remainder()
    {
        Host.Run(window =>
        {
            Style chipText = (Style)Application.Current.Resources["RefChipText"];

            double cap = (double)chipText.Setters
                .OfType<Setter>()
                .First(s => s.Property == FrameworkElement.MaxWidthProperty)
                .Value;

            Assert.True(
                cap >= 400,
                "The ref chip caps at " + cap + "px, which cuts a long branch name or a tag off "
                + "mid-word with nothing to recover the full text.");

            // The chip has to carry the whole list as a tooltip, or the ellipsis is the end of it.
            //
            // A ToolTip set through a Binding is not a plain string on the element - it stays a
            // binding until a data context is attached - so the check is on the binding's path
            // rather than on a value. Reading the string would pass even with the wrong property
            // bound, and would fail on a row that simply was not realised.
            DataTemplate? commitTemplate = null;

            foreach (UIElement element in EnumerateDescendants(window))
            {
                if (element is ListBox { ItemTemplate: not null } box && IsCommitTemplate(box.ItemTemplate))
                {
                    commitTemplate = box.ItemTemplate;
                }
            }

            Assert.NotNull(commitTemplate);

            // Realised against a real commit, because the tooltip lives on a Border inside the
            // template and a template that is never applied produces no such element.
            ContentPresenter presenter = new ContentPresenter { ContentTemplate = commitTemplate };
            presenter.ApplyTemplate();
            presenter.Measure(new Size(1400, double.PositiveInfinity));
            presenter.Arrange(new Rect(0, 0, 1400, 60));
            presenter.UpdateLayout();

            Border chip = VisualDescendants(presenter).OfType<Border>()
                .First(b => b.Style is Style s && ReferenceEquals(s, Application.Current.Resources["RefChip"]));

            Assert.Equal(
                "RefNames",
                BindingPath(chip, ToolTip.ToolTipProperty));
        });
    }

    /// <summary>
    /// Whether a list row template is the changed-files one, told apart by the status badge it shows.
    /// </summary>
    private static bool IsFileTemplate(DataTemplate template)
    {
        return template.LoadContent() is FrameworkElement row
            && VisualDescendants(row)
                .OfType<TextBlock>()
                .Any(t => BindingPath(t, TextBlock.TextProperty) == "StatusBadge");
    }

    /// <summary>
    /// Whether a list row template is the commit one, told apart from the changed-files template by
    /// the hash it shows.
    /// </summary>
    /// <remarks>
    /// Matching on the template's own text is what keeps the test honest. Picking "a ListBox" or
    /// "the last ListBox" would quietly land on the changed-files list and pass or fail for reasons
    /// that have nothing to do with commits.
    /// </remarks>
    private static bool IsCommitTemplate(DataTemplate template)
    {
        return template.LoadContent() is FrameworkElement row
            && VisualDescendants(row)
                .OfType<TextBlock>()
                .Any(t => BindingPath(t, TextBlock.TextProperty) == "ShortHash");
    }

    /// <summary>
    /// The splitter is only offered if it looks like something to grab. It was a transparent hit
    /// area between two lists, which on screen is the same as a fixed margin: nothing marked the
    /// boundary as movable. The lists are now two cards, and this holds the rule that marks the join.
    /// </summary>
    [Fact]
    public void The_splitter_between_the_two_list_cards_has_a_visible_grip()
    {
        Host.Run(window =>
        {
            // The splitter's own template, not just the control, because a default template draws
            // nothing: the test has to look at what is actually rendered.
            ControlTemplate? template = null;
            GridSplitter? live = null;

            foreach (UIElement element in EnumerateDescendants(window))
            {
                if (element is GridSplitter splitter
                    && splitter.ResizeDirection == GridResizeDirection.Columns
                    && splitter.ResizeBehavior == GridResizeBehavior.PreviousAndNext)
                {
                    live = splitter;
                    template = splitter.Template;
                    break;
                }
            }

            // The lists row has to keep its splitter, or the two cards have a fixed join.
            Assert.NotNull(live);
            Assert.NotNull(template!);

            // The grip is drawn by the template, and a template is a recipe: it produces nothing
            // until a control of its own type applies it and the result is measured and arranged.
            // Neither happens on its own here - the test host never shows the window - so the walk
            // below would find no children and pass for the wrong reason.
            GridSplitter probe = new GridSplitter { Template = template };
            probe.Measure(new Size(14, 40));
            probe.Arrange(new Rect(0, 0, 14, 40));
            probe.UpdateLayout();

            List<SolidColorBrush> visible = VisualDescendants(probe)
                .OfType<Border>()
                .Select(b => b.Background)
                .OfType<SolidColorBrush>()
                .ToList();

            Assert.True(
                visible.Count > 0,
                "The splitter template paints no filled border, so the splitter is invisible: "
                + template);
        });
    }

    /// <summary>
    /// Every rendered child of an element, however deep. Used where the point is what is on screen
    /// rather than what the markup declares, which is a different tree once a template is involved.
    /// </summary>
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);

            yield return child;

            foreach (DependencyObject descendant in VisualDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Every window must load. A reference to a resource that does not exist is not a compile error
    /// and not a build error: XAML is compiled to BAML, and the lookup happens when the window is
    /// first shown. The diff tool picker shipped a window that threw
    /// <c>StaticResourceExtension</c> on open because it reached for a converter and a style that
    /// only existed in the main window's own dictionary, and a window cannot see another window's
    /// resources. Constructing each one here is the only thing that would have caught it.
    /// <para>
    /// The main window is loaded the same way in every other test in this file, so the guarantee
    /// covers both without needing a separate case for it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_diff_tool_picker_window_loads()
    {
        Host.Run(_ =>
        {
            // A view model with no tools, so the list is empty and only the window's own markup is
            // exercised. The resources under test are the ones the markup itself references.
            DiffToolPickerViewModel model = new DiffToolPickerViewModel(
                new List<GitDiffFolderCreator.Services.DiffToolChoice>(),
                currentCommand: string.Empty,
                pickExecutable: _ => null);

            DiffToolPickerWindow window = new DiffToolPickerWindow { DataContext = model };

            // Constructing the window is the whole test, and measuring it is deliberately avoided.
            //
            // InitializeComponent runs the compiled BAML through XamlReader, which resolves every
            // StaticResource in the markup at that moment - which is precisely where the shipped
            // picker threw, from DiffToolPickerWindow.Choose. So an unresolved key fails here rather
            // than on first use.
            //
            // Measure/Arrange is not called because it is not needed for that, and calling it on a
            // Window that has never been shown blocks: a Window drives its own layout through an
            // HwndSource that does not exist yet, so the call waits for a presentation source that
            // only Show() would create. Closing it afterwards is likewise unnecessary, since it was
            // never shown and the host shuts down explicitly.

            Assert.NotNull(window.Content);
        });
    }

    /// <summary>
    /// The change document checker's window must load, for the same reason as the two above: a
    /// <c>StaticResource</c> its own dictionary does not hold is resolved when the window is
    /// constructed, not when it is built.
    /// </summary>
    /// <remarks>
    /// It lives here rather than beside the checker's other tests because this class owns the single
    /// <see cref="WindowHost"/>, and that host's constructor creates the one <see cref="Application"/>
    /// the process is allowed. A second host means a second application, which throws — and would take
    /// every window test in this file down with it.
    /// </remarks>
    [Fact]
    public void The_change_document_window_loads()
    {
        Host.Run(_ =>
        {
            ChangeDocumentViewModel model = new ChangeDocumentViewModel(
                pickFolder: (_, _) => null,
                pickDocument: _ => null);

            ChangeDocumentWindow window = new ChangeDocumentWindow { DataContext = model };

            // Constructing it is the whole test, for the reason given on the picker above: BAML is run
            // through XamlReader here and every StaticResource in the markup is resolved at this
            // moment. Measure is not called, because a window that has never been shown blocks on a
            // presentation source that only Show would create.
            Assert.NotNull(window.Content);
        });
    }

    /// <summary>
    /// The commit details window must load for the same reason the diff tool picker had to: a
    /// missing resource key or a binding a property cannot satisfy is not a build error, and it
    /// throws the first time the window is created.
    /// </summary>
    [Fact]
    public void The_commit_detail_window_loads()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "a");
        string hash = repo.Commit("a commit");

        // A row as the log actually supplies one: a subject and a short hash are already on screen
        // before the details button is even clicked.
        var row = new GitCommit
        {
            Hash = hash,
            ShortHash = hash.Substring(0, 7),
            Message = "a commit",
        };

        // On the shared STA host, like every other window test here: WPF refuses to construct
        // anything UI on a thread-pool thread, and a Details window is opened from a button
        // click on the UI thread like any other.
        //
        // No owner is passed: the host never shows its window, and WPF rejects an owner that
        // has not been shown before.
        Host.Run(_ =>
        {
            CommitDetailWindow window = CommitDetailWindow.Open(
                null, new GitService(repo.Root), row);

            // Opened rather than merely constructed, so the load the caller triggers has actually
            // run and a failure inside it shows up here instead of at the click.
            Assert.NotNull(window.Content);
            Assert.NotNull(window.DataContext);

            // The subject from the row must already be there, before the read has been waited for.
            var model = Assert.IsType<CommitDetailViewModel>(window.DataContext);
            Assert.False(string.IsNullOrWhiteSpace(model.Subject));
        });
    }

    /// <summary>
    /// The details window's file list opens a file in the diff tool on a double-click, the same
    /// gesture the main window's list uses.
    /// </summary>
    /// <remarks>
    /// Checked on the markup because the behaviour is attached rather than wired: nothing on the list
    /// says "double-click" except the behaviour, so a list that looks right and is not hooked up would
    /// pass every other test here.
    /// <para>
    /// Read from a window that has been shown. WPF leaves every binding on an unshown window
    /// unattached, so the command would read as null there and a list with nothing attached at all
    /// would look exactly the same.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_commit_details_file_list_opens_a_diff_on_double_click()
    {
        using TempRepository repo = TempRepository.Create();

        // On the shared STA host, because showing a window is UI work - the same reason the other
        // window tests here go through it.
        Host.Run(_ =>
        {
            CommitDetailWindow window = OpenCommitDetail(repo);

            ListBox files = EnumerateDescendants(window)
                .OfType<ListBox>()
                .Single(box => BoundPath(box, ItemsControl.ItemsSourceProperty) == "Files");

            // Attached to the list itself rather than to each row, so the row under the pointer is the
            // item and no selection has to be tracked for it.
            Assert.Same(
                window.DataContext is CommitDetailViewModel model ? model.ShowDifferenceCommand : null,
                DoubleClickBehavior.GetCommand(files));

            // And said out loud: the list is read-only and offers nothing else, so the tooltip is the
            // only place a first-time user is told the gesture exists.
            Assert.False(string.IsNullOrWhiteSpace(files.ToolTip as string));
        });
    }

    /// <summary>
    /// Every commit row carries a Details button, which is the only route to the full message and
    /// to the files a commit touched.
    /// </summary>
    /// <remarks>
    /// Found by its accessible name rather than its caption. It draws three dots now, so the caption is
    /// a shape and not a string - and the name is the thing that has to survive that change anyway,
    /// since a screen reader is the one consumer that cannot infer the button's purpose from three dots.
    /// </remarks>
    [Fact]
    public void Every_commit_row_has_a_details_button()
    {
        Host.Run(window =>
        {
            DataTemplate? commitTemplate = null;

            foreach (UIElement element in EnumerateDescendants(window))
            {
                if (element is ListBox { ItemTemplate: not null } box && IsCommitTemplate(box.ItemTemplate))
                {
                    commitTemplate = box.ItemTemplate;
                }
            }

            Assert.NotNull(commitTemplate);

            var commit = new GitCommit
            {
                Hash = "0000000000000000000000000000000000000000",
                ShortHash = "0000000",
                Message = "the commit under test",
            };

            // Realised with a real commit as the content, so the button's own binding resolves. The
            // resolved value is what matters - that the button knows which commit it belongs to - and
            // reading the binding's path instead would prove nothing about the handler receiving it.
            ContentPresenter presenter = new ContentPresenter
            {
                Content = commit,
                ContentTemplate = commitTemplate,
            };
            presenter.ApplyTemplate();
            presenter.Measure(new Size(1400, double.PositiveInfinity));
            presenter.Arrange(new Rect(0, 0, 1400, 60));
            presenter.UpdateLayout();

            Button? details = VisualDescendants(presenter).OfType<Button>()
                .FirstOrDefault(b => AutomationProperties.GetName(b) == "Show the whole commit");

            Assert.True(
                details != null,
                "A commit row offers no way to open the commit's details.");

            Assert.Same(commit, details!.CommandParameter);

            // Three dots, and drawn rather than typed: a caption of "..." would depend on the font's
            // baseline and weight, which is not what this is being checked for.
            Assert.Equal(
                3,
                VisualDescendants(details).OfType<Ellipse>().Count());

            // And it says what it does, for anyone who cannot read three dots as "more".
            Assert.False(string.IsNullOrWhiteSpace(details.ToolTip as string));
        });
    }

    /// <summary>
    /// The details button appears only while its row is hovered or has keyboard focus.
    /// </summary>
    /// <remarks>
    /// The binding rather than the resolved value, because a row template realised on its own has no
    /// ListBoxItem above it to be hovered. What matters is that the button is driven by the row's hover
    /// state and not left permanently visible, which a hard-coded Visibility would satisfy while looking
    /// identical in the template.
    /// </remarks>
    [Fact]
    public void The_details_button_appears_only_when_the_row_is_hovered()
    {
        Host.Run(window =>
        {
            Button details = DetailsButtonIn(window);

            BindingBase? visibility = BindingOperations.GetBinding(
                details, UIElement.VisibilityProperty);

            var rowHover = visibility as Binding;

            Assert.True(
                rowHover != null,
                "The details button's visibility is not bound, so it is either always shown or never.");

            // The attached property is the hover channel, and it has to be read off the row above rather
            // than off the button: IsMouseOver on the button would only fire over the button itself,
            // which by then is too late to have revealed it.
            Assert.Contains(
                RowHover.IsHoveredProperty.Name,
                rowHover!.Path.Path,
                StringComparison.Ordinal);

            Assert.Equal(RelativeSourceMode.FindAncestor, rowHover.RelativeSource.Mode);
            Assert.Equal(typeof(ListBoxItem), rowHover.RelativeSource.AncestorType);
        });
    }

    /// <summary>
    /// A row the keyboard is on shows its details button, because a control that only appears under the
    /// pointer cannot be tabbed to at all.
    /// </summary>
    /// <remarks>
    /// Hover-only is a real cost, and this is what pays it. Without this, the button would be collapsed
    /// exactly when nobody is pointing at it and the keyboard would walk straight past it every time.
    /// </remarks>
    [Fact]
    public void A_commit_row_shows_its_details_button_while_it_holds_the_keyboard_focus()
    {
        Host.Run(window =>
        {
            Style row = (Style)window.FindResource("CommitRow");

            // Inside the template rather than on the style: the row paints its own background through a
            // named Border, so its triggers have to live where that Border is in scope. Read through a
            // fresh ListBoxItem because Style.Template is the setter, and the setter's own value is
            // what has to be inspected.
            var template = (ControlTemplate)new ListBoxItem { Style = row }.Template;

            List<Trigger> triggers = template.Triggers.OfType<Trigger>().ToList();

            Assert.True(
                triggers.Any(t => t.Property == ListBoxItem.IsMouseOverProperty),
                "The commit row does not report the pointer, so its details button never appears.");

            Assert.True(
                triggers.Any(t => t.Property == UIElement.IsKeyboardFocusWithinProperty),
                "The commit row ignores keyboard focus, so its details button is unreachable without a mouse.");

            foreach (Trigger trigger in triggers.Where(
                t => t.Property == ListBoxItem.IsMouseOverProperty
                    || t.Property == UIElement.IsKeyboardFocusWithinProperty))
            {
                Assert.True(
                    trigger.Setters.OfType<Setter>().Any(
                        s => s.Property == RowHover.IsHoveredProperty),
                    "The commit row reveals nothing when it is hovered or focused.");
            }
        });
    }

    /// <summary>The commit row's details button, found by what it is for.</summary>
    private static Button DetailsButtonIn(MainWindow window)
    {
        DataTemplate? commitTemplate = null;

        foreach (UIElement element in EnumerateDescendants(window))
        {
            if (element is ListBox { ItemTemplate: not null } box && IsCommitTemplate(box.ItemTemplate))
            {
                commitTemplate = box.ItemTemplate;
            }
        }

        Assert.NotNull(commitTemplate);

        var presenter = new ContentPresenter
        {
            Content = new GitCommit { ShortHash = "0000000", Message = "under test" },
            ContentTemplate = commitTemplate,
        };

        presenter.ApplyTemplate();
        presenter.Measure(new Size(1400, double.PositiveInfinity));
        presenter.Arrange(new Rect(0, 0, 1400, 60));
        presenter.UpdateLayout();

        Button? button = VisualDescendants(presenter).OfType<Button>()
            .FirstOrDefault(b => AutomationProperties.GetName(b) == "Show the whole commit");

        Assert.True(button != null, "The commit row has no details button.");

        return button!;
    }

    /// <summary>
    /// The check boxes are only on screen once the Select control is turned on, and the two bulk
    /// buttons exist beside it.
    /// </summary>
    /// <remarks>
    /// Checked against the window's own markup rather than the view model, because the rule is about
    /// what is visible: a view model that defaults to off would still leave a permanently visible
    /// column of boxes if the binding were missing.
    /// </remarks>
    [Fact]
    public void The_file_check_boxes_appear_only_when_the_select_control_is_on()
    {
        Host.Run(window =>
        {
            // The toggle in the file list's toolbar, told apart from the diff tool and Swap buttons
            // that share the row by its caption. "Choose files" rather than "Select", because "Select"
            // beside a list of commits reads as selecting rows; the caption is part of what this test
            // checks, so renaming it is a deliberate act rather than something the test follows.
            ToggleButton select = EnumerateDescendants(window)
                .OfType<ToggleButton>()
                .FirstOrDefault(t => t.Content as string == "Choose files")!;

            Assert.True(select != null, "There is no control that turns the file check boxes on.");
            Assert.Equal("IsSelectionEnabled", BindingPath(select, ToggleButton.IsCheckedProperty));

            // A one-way binding on IsChecked only shows the view model's state; on its own it
            // reports no clicks at all, because there is no setter to push them into. Something has
            // to run on the click, and without it the toggle flips its own face and nothing else
            // happens anywhere.
            Assert.Equal("ToggleSelectionCommand", CommandPath(select));

            // Checked by name rather than by position, so moving a button along the toolbar does not break the
            // test and a button wired to the wrong command does.
            foreach ((string caption, string command) in new[]
            {
                ("All", "SelectAllCommand"),
                ("None", "DeselectAllCommand"),
            })
            {
                Button bulk = EnumerateDescendants(window)
                    .OfType<Button>()
                    .FirstOrDefault(b => b.Content as string == caption)!;

                Assert.True(bulk != null, "The '" + caption + "' button is missing.");
                Assert.Equal(command, CommandPath(bulk));
            }

            // The row's own check box has to be governed by the same switch, or turning it on would
            // reveal nothing.
            DataTemplate? fileRow = null;

            foreach (UIElement element in EnumerateDescendants(window))
            {
                if (element is ListBox { ItemTemplate: not null } box && IsFileTemplate(box.ItemTemplate))
                {
                    fileRow = box.ItemTemplate;
                }
            }

            Assert.NotNull(fileRow);

            ContentPresenter presenter = new ContentPresenter { ContentTemplate = fileRow };
            presenter.ApplyTemplate();
            presenter.Measure(new Size(800, double.PositiveInfinity));
            presenter.Arrange(new Rect(0, 0, 800, 40));
            presenter.UpdateLayout();

            CheckBox rowCheckBox = VisualDescendants(presenter).OfType<CheckBox>().First();

            Assert.Contains(
                "IsSelectionEnabled",
                BindingPath(rowCheckBox, UIElement.VisibilityProperty),
                StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The path a property is bound to, or an empty string when it is not bound at all.
    /// </summary>
    /// <remarks>
    /// Read from the binding rather than from the resolved value, because the markup under test is
    /// not attached to a view model here: what matters is that the property is wired to the right
    /// property, not what that evaluates to without a data context.
    /// </remarks>
    private static string BindingPath(DependencyObject element, DependencyProperty property)
    {
        return (BindingOperations.GetBinding(element, property) as Binding)?.Path.Path ?? string.Empty;
    }

    private static string CommandPath(ButtonBase element) =>
        BindingPath(element, ButtonBase.CommandProperty);

    /// <summary>
    /// The three buttons whose captions say what they do. Checked on the markup because each of them was
    /// a caption that described the wrong thing: one that named two folders as "Open", one that read as
    /// selecting list rows, and one wide enough to be read as a heading.
    /// </summary>
    [Fact]
    public void The_output_row_buttons_are_captions_that_say_what_they_do()
    {
        Host.Run(window =>
        {
            // Open: two different folders, so the caption and the tooltip are bound rather than written
            // once. The behaviour behind them is in OutputFolderCaptionTests; what matters here is that
            // the caption is bound to the view model at all, which a literal string would not be.
            Button open = Buttons(window).Single(
                b => CommandPath(b) == "OpenOutputFolderCommand");

            Assert.Equal("OpenOutputCaption", BindingPath(open, Button.ContentProperty));
            Assert.Equal("OpenOutputTooltip", BindingPath(open, FrameworkElement.ToolTipProperty));

            // Choose files: the toggle that reveals the check boxes. Matched on the command rather than
            // the caption, because the caption is the thing being asserted.
            ToggleButton choose = EnumerateDescendants(window)
                .OfType<ToggleButton>()
                .Single(t => CommandPath(t) == "ToggleSelectionCommand");

            Assert.Equal("Choose files", choose.Content);
            Assert.Equal("ToggleSelectionCommand", CommandPath(choose));

            // The experimental check: short, and it says what it takes rather than what it is called.
            // The full description stays on the tooltip and the accessible name, which is where a
            // screen-reader user gets it.
            Button verify = Buttons(window).Single(b => (b.Content as string) == "Verify change document...");

            Assert.NotNull(verify.ToolTip);
            Assert.Contains("source folders", verify.ToolTip as string, StringComparison.Ordinal);
            Assert.Contains("change document", AutomationProperties.GetName(verify), StringComparison.Ordinal);
        });
    }

    private static IEnumerable<Button> Buttons(Window window) =>
        EnumerateDescendants(window).OfType<Button>();

    /// <summary>
    /// The progress ring beside the COMMITS heading is a rotating transform named in the markup and
    /// animated by a storyboard. A typo in the target name compiles, binds to nothing, and leaves a
    /// static circle, so the link between the two is checked here rather than by eye.
    /// </summary>
    [Fact]
    public void The_log_progress_ring_is_markup_that_actually_animates()
    {
        Host.Run(window =>
        {
            const string RingTransformName = "LoadingRingRotation";

            // The ring is the element whose named transform is the one it renders with. Matching on the
            // name rather than on "has a RotateTransform" keeps this from latching onto some other
            // rotated element in the window.
            FrameworkElement? ring = null;

            foreach (UIElement element in EnumerateDescendants(window))
            {
                if (element is FrameworkElement framework
                    && ReferenceEquals(framework.FindName(RingTransformName), framework.RenderTransform))
                {
                    ring = framework;
                }
            }

            Assert.True(ring != null, "RING-NOT-FOUND");

            // The storyboard is named so it can be read back. A name registered on an element inside a
            // trigger belongs to the root namescope, so it is looked up from the window.
            var spin = window.FindName("RingSpin") as BeginStoryboard;
            Assert.True(spin != null, "RING-SPIN-NOT-FOUND");

            DoubleAnimation? rotation = spin!.Storyboard.Children.OfType<DoubleAnimation>()
                .FirstOrDefault(a => Storyboard.GetTargetName(a) == RingTransformName);

            Assert.True(
                rotation != null,
                "No storyboard animates " + RingTransformName + ", so the ring would sit still.");

            // A full turn, repeated forever, which is what makes it read as spinning rather than as a
            // one-off flourish.
            Assert.Equal(0d, rotation!.From!.Value);
            Assert.Equal(360d, rotation.To!.Value);
            Assert.Equal(RepeatBehavior.Forever, spin.Storyboard.RepeatBehavior);

            // And it turns that transform's angle, so the circle is what moves. PropertyPath has no
            // value equality, so the path is compared as text.
            string path = Storyboard.GetTargetProperty(rotation)!.Path;
            Assert.Contains("Angle", path, StringComparison.Ordinal);

            // That the flag is wired to the ring's visibility is a binding, and the window is never
            // shown here so no binding is evaluated. The flag's own lifecycle is covered against a
            // real repository in RepositorySwitchTests instead.
            Assert.False(Assert.IsType<GitViewModel>(window.DataContext).IsLoadingLog);
        });
    }

    /// <summary>The browser is a popup, so nothing inside it is realised or measured until it opens. Opening it
    /// against a real repository proves the filter box and the list exist and are bound, rather than
    /// the popup opening empty.
    /// </summary>
    [Fact]
    public void Opening_the_branch_browser_shows_the_filtered_list()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "content");
        repo.Commit("first");
        repo.Git("branch", "feature/login");
        repo.Git("branch", "bugfix/crash");

        Host.Run(window =>
        {
            var viewModel = (GitViewModel)window.DataContext;

            window.Show();
            viewModel.GitDirectory = repo.Root;
            WaitForBranches(viewModel);

            viewModel.ToggleBranchListCommand.Execute(null);
            Assert.True(viewModel.IsBranchListOpen);

            // Laid out and drained, so the popup's content has been realised rather than only
            // declared: opening a popup is posted work, not immediate.
            window.UpdateLayout();
            Drain();

            // The browser's own contents, reached through the popup rather than through the window:
            // a popup's visual tree is its own, so nothing inside it is reachable from the window.
            Popup? popup = FindDescendant<Popup>(window);
            TextBox filter = FindDescendant<TextBox>(popup?.Child)!;
            ListBox list = FindDescendant<ListBox>(popup?.Child)!;

            Assert.NotNull(popup);
            Assert.NotNull(filter);
            Assert.NotNull(list);

            // Everything is shown while the filter is empty.
            Assert.Equal(viewModel.Branches.Count, list.Items.Count);

            viewModel.BranchFilter = "feature/";
            window.UpdateLayout();
            Drain();

            // Typing in the box filters, and the list narrows to what survived it.
            Assert.Equal("feature/", filter.Text);
            Assert.Single(viewModel.FilteredBranches);
            Assert.Equal(
                new[] { "feature/login" },
                viewModel.FilteredBranches.Select(b => b.Name));
        });
    }

    /// <summary>
    /// A filter box wears a magnifying glass at its left edge, so it reads as a search rather than as
    /// another field. Two things have to hold together: the glass has to be there, and the text must
    /// start clear of it. The second is the one that is easy to lose - an icon laid over a box whose
    /// padding was left alone draws fine and puts the first character underneath it, and the caret
    /// runs through the glass.
    /// </summary>
    [Fact]
    public void A_filter_box_carries_a_search_glass_clear_of_its_text()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "content");
        repo.Commit("first");
        repo.Git("branch", "feature/login");

        Host.Run(window =>
        {
            var viewModel = (GitViewModel)window.DataContext;

            window.Show();
            viewModel.GitDirectory = repo.Root;
            WaitForBranches(viewModel);

            viewModel.ToggleBranchListCommand.Execute(null);
            window.UpdateLayout();
            Drain();

            // Three boxes in three places: the changed-files filter and the branch filter in the
            // popup, whose contents are only realised once it has opened, and the commit detail
            // window's own file filter. All three draw the glass from the same style, so a change
            // that broke it in one window would break it in the others - but only if the box is
            // measured in the window it actually belongs to.
            List<Control> filters = new List<Control>();

            filters.AddRange(RealisedControls<TextBox>(window)
                .Where(box => BoundPath(box, TextBox.TextProperty) == "ChangeFilter")
                .Cast<Control>());

            filters.AddRange(RealisedControls<TextBox>(FindDescendant<Popup>(window)?.Child!)
                .Where(box => BoundPath(box, TextBox.TextProperty) == "BranchFilter")
                .Cast<Control>());

            filters.AddRange(RealisedControls<TextBox>(OpenCommitDetail(repo))
                .Where(box => BoundPath(box, TextBox.TextProperty) == "Filter")
                .Cast<Control>());

            Assert.Equal(3, filters.Count);

            foreach (Control filter in filters)
            {
                AssertSearchGlass(filter);
            }

            // And each one is still bound to its own view model's property. The glass is drawn by
            // the style, which is shared; the binding is not, and a box that kept the glass and
            // lost its binding would pass every check above while filtering nothing.
            Assert.Equal("Filter", BoundPath(filters[2], TextBox.TextProperty));
        });
    }

    /// <summary>
    /// The commit detail window's own file filter, opened far enough for the visual tree to exist.
    /// `RealisedControls` reads rendered geometry, so a window that was merely constructed would
    /// report no children and the check below would pass on an empty list.
    /// </summary>
    /// <remarks>
    /// Opened through the same factory the application uses rather than by constructing the window,
    /// because that factory is what builds its view model and starts its load. The row is built by
    /// hand rather than read from a log, so the window does not depend on a commit list having
    /// finished loading.
    /// </remarks>
    private static CommitDetailWindow OpenCommitDetail(TempRepository repo)
    {
        // The window's load reads this commit, so it needs files of its own: the branch the test
        // already committed is clean, and an empty commit has nothing for the filter to narrow.
        repo.WriteFile("a.txt", "content");
        repo.WriteFile("b.txt", "more content");

        string hash = repo.Commit("detail");

        var row = new GitCommit
        {
            Hash = hash,
            ShortHash = hash.Substring(0, 7),
            Message = "a commit",
        };

        CommitDetailWindow detail = CommitDetailWindow.Open(null, new GitService(repo.Root), row);

        detail.Show();
        detail.UpdateLayout();

        return detail;
    }

    /// <summary>
    /// The glass is a ring and a handle, and the ring is the part that is easy to get wrong. It is
    /// drawn as two arcs, and two arcs close into a true circle only when their endpoints sit exactly
    /// two radii apart - diametrically opposite. Endpoints any closer bow the same way twice and
    /// produce a lens: a curved blob beside a separate line, which reads as two marks rather than as
    /// one magnifier, and no amount of care about size or colour makes that look right.
    /// <para>
    /// The distinction is measured by sampling the outline. On a circle every point of the ring sits
    /// the same distance from its centre; on a lens the two arcs bow inward off the diagonals, so that
    /// distance varies by a third of the radius. Reading the arc endpoints would be more direct, but
    /// the markup parses to a <see cref="StreamGeometry"/>, which does not expose them - the curves
    /// have to be flattened before they can be looked at, and flattening is what this test measures.
    /// </para>
    /// </summary>
    [Fact]
    public void The_search_glass_is_a_ring_and_not_a_lens()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "content");
        repo.Commit("first");

        Host.Run(window =>
        {
            window.Show();
            window.UpdateLayout();
            WindowHost.Drain(window.Dispatcher);

            var filter = RealisedControls<TextBox>(window)
                .Single(box => BoundPath(box, TextBox.TextProperty) == "ChangeFilter");

            var glass = RealisedVisual<System.Windows.Shapes.Path>(filter).Single();

            // Two figures: the ring, then the handle.
            PathGeometry path = glass.Data.GetFlattenedPathGeometry();
            List<PathFigure> figures = path.Figures.ToList();

            Assert.Equal(2, figures.Count);

            PathFigure ring = figures[0];
            List<Point> outline = OutlineOf(ring);

            // The centre of a circle is the middle of its bounding box.
            var centre = new Point(
                outline.Min(p => p.X) + ((outline.Max(p => p.X) - outline.Min(p => p.X)) / 2),
                outline.Min(p => p.Y) + ((outline.Max(p => p.Y) - outline.Min(p => p.Y)) / 2));

            Assert.True(outline.Count > 8, "The ring has only " + outline.Count + " points to measure.");

            var radii = outline.Select(p => Distance(centre, p)).ToList();
            double smallest = radii.Min();
            double largest = radii.Max();
            double mean = radii.Average();

            // A circle holds its radius to within the flattening error. A lens does not: the same
            // geometry measured this way spans about a third of its radius.
            Assert.True(
                largest - smallest < mean * 0.05,
                "The ring's outline varies from "
                    + smallest.ToString("F2", CultureInfo.InvariantCulture) + " to "
                    + largest.ToString("F2", CultureInfo.InvariantCulture)
                    + " about its centre, so it bows into a lens rather than closing into a circle.");

            // The handle is a straight line, drawn as its own figure so it overlaps the ring cleanly.
            List<Point> handle = OutlineOf(figures[1]);

            Assert.True(
                handle.Count == 2,
                "The handle is not a single straight line, so the glass is more than a ring and a stick.");

            // It starts on the ring rather than beside it.
            double start = Distance(centre, handle[0]);
            double radius = (smallest + largest) / 2;

            Assert.True(
                Math.Abs(start - radius) < 0.1,
                "The handle starts " + start.ToString("F2", CultureInfo.InvariantCulture)
                    + " from the ring's centre where the radius is "
                    + radius.ToString("F2", CultureInfo.InvariantCulture) + ".");

            // And the whole thing fits the box it is drawn in. The previous geometry ran past the
            // right edge, which clipped the handle's tip.
            Rect bounds = glass.Data.Bounds;

            Assert.True(
                bounds.Right <= 13 && bounds.Bottom <= 13,
                "The glass runs to " + bounds.Right.ToString("F2", CultureInfo.InvariantCulture)
                    + " in a 13 wide box, so it is being clipped.");
        });
    }

    /// <summary>Every point on a figure's outline, arcs flattened to curves.</summary>
    private static List<Point> OutlineOf(PathFigure figure)
    {
        var points = new List<Point> { figure.StartPoint };

        foreach (PathSegment segment in figure.Segments)
        {
            switch (segment)
            {
                case LineSegment line:
                    points.Add(line.Point);
                    break;

                case PolyLineSegment polyLine:
                    points.AddRange(polyLine.Points);
                    break;

                case BezierSegment bezier:
                    points.Add(bezier.Point1);
                    points.Add(bezier.Point2);
                    points.Add(bezier.Point3);
                    break;

                case QuadraticBezierSegment quadratic:
                    points.Add(quadratic.Point1);
                    points.Add(quadratic.Point2);
                    break;
            }
        }

        return points;
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>
    /// The glass is drawn rather than a glyph, so it does not change with the platform's font, and it
    /// is inset rather than placed outside, so the box keeps its own width. Read from the rendered
    /// position rather than from the markup: an icon and a padding that agree in the template are the
    /// only case where this is true, and any other arrangement looks correct in the source.
    /// </summary>
    private static void AssertSearchGlass(Control filter)
    {
        var scroll = (ScrollViewer)filter.Template.FindName("PART_ContentHost", filter)!;
        var glass = RealisedVisual<System.Windows.Shapes.Path>(filter).SingleOrDefault();

        Assert.True(glass != null, "A filter box has no search glass in it.");

        double glassRight = glass!.TranslatePoint(new Point(glass.ActualWidth, 0), filter).X;

        Assert.True(
            glassRight <= scroll.Margin.Left,
            "The glass ends at " + glassRight + " and the text starts at " + scroll.Margin.Left
                + ", so the first character sits under it.");

        // And it is visible: a zero-sized or collapsed glass satisfies every check above.
        Assert.True(glass.ActualWidth > 0 && glass.ActualHeight > 0, "The search glass is not drawn.");
    }

    /// <summary>Every element of the given type in the visual tree, which a closed popup does not have.</summary>
    private static List<T> RealisedControls<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        CollectRealised(root, found);
        return found;
    }

    private static void CollectRealised<T>(DependencyObject root, List<T> found)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                found.Add(match);
            }

            CollectRealised(child, found);
        }
    }

    private static List<T> RealisedVisual<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        CollectRealised(root, found);
        return found;
    }

    /// <summary>
    /// Enter takes the highlighted branch and Escape leaves without choosing. Both are handled on the
    /// popup rather than on a control, so that they work with the caret in the filter box.
    /// </summary>
    [Fact]
    public void Enter_and_escape_drive_the_open_browser()
    {
        using TempRepository repo = TempRepository.Create();
        repo.WriteFile("a.txt", "content");
        repo.Commit("first");
        repo.Git("branch", "feature/login");

        Host.Run(window =>
        {
            var viewModel = (GitViewModel)window.DataContext;

            window.Show();
            viewModel.GitDirectory = repo.Root;
            WaitForBranches(viewModel);

            viewModel.ToggleBranchListCommand.Execute(null);
            window.UpdateLayout();
            Drain();

            Popup popup = FindDescendant<Popup>(window)!;
            Assert.NotNull(popup);

            // Escape with the filter typed: the browser closes and what was typed is discarded, so the
            // next visit starts from the whole list.
            viewModel.BranchFilter = "feature/";
            Press(popup, Key.Escape);
            Assert.False(viewModel.IsBranchListOpen);
            Assert.Equal(string.Empty, viewModel.BranchFilter);

            viewModel.ToggleBranchListCommand.Execute(null);
            window.UpdateLayout();
            Drain();

            viewModel.MoveBranchHighlight(1);
            GitBranch highlighted = viewModel.PendingBranch!;

            Press(popup, Key.Enter);
            Assert.False(viewModel.IsBranchListOpen);
            Assert.Same(highlighted, viewModel.SelectedBranch);
        });
    }

    /// <summary>
    /// Raises a key on the popup, the way the keyboard does, so the window's own handler decides what
    /// happens to the browser.
    /// </summary>
    private static void Press(Popup popup, Key key)
    {
        // A key event needs the presentation source it came from. The popup's own content is the
        // visual it is reported against.
        Visual target = (Visual)(popup.Child ?? popup);
        PresentationSource source = PresentationSource.FromVisual(target)!;

        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        };

        popup.RaiseEvent(args);

        Drain();

        Assert.True(args.Handled, "The key was not handled, so it would fall through to the window.");
    }

    /// <summary>
    /// The first descendant of the given type, or <c>null</c> when there is none. Visual rather than
    /// logical, because a popup's content is only in the visual tree.
    /// </summary>
    private static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root == null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                return match;
            }

            T? found = FindDescendant<T>(child);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Waits for the branch read, which runs off the UI thread and lands on the dispatcher. A poll
    /// rather than a wait, because the read is debounced.
    /// </summary>
    private static void WaitForBranches(GitViewModel viewModel)
    {
        for (int i = 0; i < 1200 && (viewModel.IsBusy || viewModel.Branches.Count == 0); i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(25);
        }

        Assert.False(viewModel.IsBusy);
        Assert.NotEmpty(viewModel.Branches);
    }

    /// <summary>
    /// Every element declared below the given one. The logical tree rather than the visual one,
    /// because these tests never show the window, and an unshown window has no visual tree built.
    /// </summary>
    private static IEnumerable<UIElement> EnumerateDescendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (!(child is UIElement element))
            {
                continue;
            }

            yield return element;

            foreach (UIElement descendant in EnumerateDescendants(element))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// The storyboard triggers declared on an element or on its style. A trigger that names a target
    /// that is not in the tree is silently inert, so the animation has to be read off the trigger
    /// rather than off the running clock.
    /// </summary>
    private static void Drain() => WindowHost.Drain(Dispatcher.CurrentDispatcher);
}

/// <summary>
/// Keeps the window tests on one another: two applications in one process cannot both own the
/// application's resources.
/// </summary>
[CollectionDefinition(nameof(WindowTests), DisableParallelization = true)]
public sealed class WindowTests
{
}

/// <summary>
/// One STA thread and one <see cref="Application"/> for every window test, both created on that
/// thread. Reused rather than recreated because <see cref="Application.Current"/> is per process.
/// </summary>
internal sealed class WindowHost
{
    private readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);

    // Not readonly: it is assigned on the thread that starts, once, before Run is ever called.
    private Dispatcher? dispatcher;

    public WindowHost()
    {
        var thread = new Thread(() =>
        {
            // The application has to exist before any window loads, or every StaticResource in the
            // markup fails to resolve.
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "/GitDiffFolderCreator;component/Controls/Controls.xaml",
                    UriKind.Relative),
            });

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(ready.Wait(TimeSpan.FromMinutes(1)), "The window test thread did not start.");
    }

    /// <summary>
    /// Runs the body on the shared thread with a fresh window, and closes the window afterwards so one
    /// test cannot leave a popup open for the next.
    /// </summary>
    public void Run(Action<MainWindow> body)
    {
        Exception? failure = null;

        Dispatcher host = dispatcher!;

        host.Invoke(() =>
        {
            var window = new MainWindow();

            try
            {
                body(window);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window.Close();
            }
        });

        Drain(host);

        if (failure != null)
        {
            throw failure;
        }
    }

    /// <summary>
    /// Empties the dispatcher queue, which is where a popup's own layout and focus work lands. Opening
    /// or closing a popup is posted work rather than something that happens inline.
    /// </summary>
    public static void Drain(Dispatcher dispatcher)
    {
        for (int i = 0; i < 20; i++)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.Input);
        }
    }
}
