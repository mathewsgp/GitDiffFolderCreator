using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using GitDiffFolderCreator.Behaviors;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// Choosing which changed files to export: the check boxes that do it, the toggle that reveals them,
/// and the two buttons that avoid needing to click every row.
/// </summary>
/// <remarks>
/// The double-click behaviour is covered here too, because it lives in the same list and the two
/// used to fight each other - ticking a check box also opened a diff.
/// </remarks>
public sealed class FileSelectionTests
{
    // ---------------------------------------------------------------- view model

    [Fact]
    public void The_check_boxes_are_off_to_begin_with()
    {
        Run(viewModel =>
        {
            // Most runs export the whole range, and a column of ticked boxes beside every path is
            // something the eye has to skip on every row.
            Assert.False(viewModel.IsSelectionEnabled);
            Assert.False(viewModel.CanSelectFiles);
        });
    }

    [Fact]
    public void Turning_selection_on_shows_the_check_boxes_and_keeps_every_file_ticked()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 3);

            viewModel.ToggleSelectionCommand.Execute(null);

            Assert.True(viewModel.IsSelectionEnabled);
            Assert.True(viewModel.CanSelectFiles);

            // Starting from "nothing ticked" would look like a deliberate choice, and the export
            // would quietly produce an empty folder the first time anyone turned the option on.
            Assert.Equal(0, viewModel.ExcludedCount);
            Assert.All(viewModel.Changes, change => Assert.True(change.IsIncluded));
        });
    }

    [Fact]
    public void Turning_selection_off_leaves_the_ticks_alone()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 3);

            viewModel.ToggleSelectionCommand.Execute(null);
            viewModel.DeselectAllCommand.Execute(null);
            viewModel.ToggleSelectionCommand.Execute(null);

            Assert.False(viewModel.IsSelectionEnabled);

            // Hidden is not the same as forgotten. Turning the option off must not quietly
            // re-include files the user had deliberately excluded.
            Assert.Equal(3, viewModel.ExcludedCount);
        });
    }

    [Fact]
    public void Deselect_all_leaves_nothing_to_export()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 4);
            viewModel.ToggleSelectionCommand.Execute(null);

            viewModel.DeselectAllCommand.Execute(null);

            Assert.Equal(4, viewModel.ExcludedCount);
            Assert.All(viewModel.Changes, change => Assert.False(change.IsIncluded));

            // There is nothing left to write, so the run must not be offered.
            Assert.False(viewModel.CanCreateFolders);
        });
    }

    [Fact]
    public void Select_all_puts_every_file_back_after_deselecting_them()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 4);
            viewModel.ToggleSelectionCommand.Execute(null);
            viewModel.DeselectAllCommand.Execute(null);

            viewModel.SelectAllCommand.Execute(null);

            Assert.Equal(0, viewModel.ExcludedCount);
            Assert.All(viewModel.Changes, change => Assert.True(change.IsIncluded));
        });
    }

    [Fact]
    public void Ticking_a_single_file_by_hand_still_works_while_the_buttons_are_offered()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 3);
            viewModel.ToggleSelectionCommand.Execute(null);

            viewModel.Changes[1].IsIncluded = false;

            Assert.Equal(1, viewModel.ExcludedCount);
            Assert.Contains("2 of 3", viewModel.SelectionSummary, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Select_all_and_deselect_all_rebuild_the_summary_once_rather_than_once_per_file()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 25);
            viewModel.ToggleSelectionCommand.Execute(null);

            int summaries = 0;
            int excludedCounts = 0;

            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "SelectionSummary")
                {
                    summaries++;
                }

                if (e.PropertyName == "ExcludedCount")
                {
                    excludedCounts++;
                }
            };

            viewModel.DeselectAllCommand.Execute(null);

            // Every row raises its own change, and recomputing the summary for each of them makes
            // "Select all" on a large comparison visibly slow. One rebuild at the end is enough.
            Assert.Equal(1, summaries);
            Assert.Equal(1, excludedCounts);
            Assert.Equal(25, viewModel.ExcludedCount);
        });
    }

    [Fact]
    public void The_select_buttons_do_nothing_while_there_are_no_files_to_choose_between()
    {
        Run(viewModel =>
        {
            // Selection was never turned on, so there are no check boxes and the buttons are not
            // offered. Running one anyway must be harmless rather than throwing.
            viewModel.DeselectAllCommand.Execute(null);
            viewModel.SelectAllCommand.Execute(null);

            Assert.Empty(viewModel.Changes);
            Assert.False(viewModel.IsSelectionEnabled);
        });
    }

    [Fact]
    public void The_select_button_is_offered_as_soon_as_there_is_something_to_select()
    {
        Run(viewModel =>
        {
            // Nothing loaded yet, so there is nothing to choose between and the control is not
            // offered.
            Assert.False(viewModel.ToggleSelectionCommand.CanExecute(null));

            LoadFiles(viewModel, 1);

            // The boxes have somewhere to appear, so the toggle becomes available...
            Assert.True(viewModel.ToggleSelectionCommand.CanExecute(null));

            // ...but the two bulk buttons are for use against those boxes, so they wait for the
            // option to be turned on, the same as they are hidden until it is.
            Assert.False(viewModel.SelectAllCommand.CanExecute(null));
            Assert.False(viewModel.DeselectAllCommand.CanExecute(null));

            viewModel.ToggleSelectionCommand.Execute(null);

            Assert.True(viewModel.SelectAllCommand.CanExecute(null));
            Assert.True(viewModel.DeselectAllCommand.CanExecute(null));
        });
    }

    // ---------------------------------------------------------------- behaviour

    [Fact]
    public void A_double_click_on_a_check_box_does_not_open_a_diff()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 2);

            (ListBox list, CheckBox box, ListBoxItem row, Counter runs) = BuildFileList(viewModel);

            bool handled = DoubleClickBehavior.TryResolveRow(list, box, out object? item);

            // The tick means "export this file". Opening a diff on the same gesture makes the file
            // impossible to untick without also launching a tool over it.
            Assert.False(handled);
            Assert.Null(item);
            Assert.Equal(0, runs.Runs);
            Assert.NotNull(row);
        });
    }

    [Fact]
    public void A_double_click_elsewhere_on_a_row_does_open_a_diff_and_reports_that_row()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 2);

            (ListBox list, CheckBox _, ListBoxItem _, Counter _) = BuildFileList(viewModel);

            bool handled = DoubleClickBehavior.TryResolveRow(list, PathOnRow(list), out object? item);

            Assert.True(handled);
            Assert.Same(viewModel.Changes[0], item);
        });
    }

    [Fact]
    public void A_double_click_outside_any_row_is_left_alone()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 2);

            (ListBox list, CheckBox _, ListBoxItem _, Counter runs) = BuildFileList(viewModel);

            // Empty space below the last row has no item under it, so there is nothing to show.
            Assert.False(DoubleClickBehavior.TryResolveRow(list, list, out object? item));
            Assert.Null(item);
            Assert.Equal(0, runs.Runs);
        });
    }

    [Fact]
    public void The_behaviour_waits_for_a_double_click_instead_of_acting_on_the_first_click()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 1);

            (ListBox list, CheckBox _, ListBoxItem _, Counter runs) = BuildFileList(viewModel);

            // The first press of a double-click. An earlier version listened to exactly this and so
            // ran the command on every single click, which is what made ticking a box open a diff.
            list.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                Source = PathOnRow(list),
            });

            Assert.Equal(0, runs.Runs);
        });
    }

    [Fact]
    public void A_real_double_click_on_a_row_runs_the_command()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 2);

            (ListBox list, CheckBox _, ListBoxItem _, Counter runs) = BuildFileList(viewModel);

            list.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
                Source = PathOnRow(list),
            });

            Assert.Equal(1, runs.Runs);
        });
    }

    [Fact]
    public void A_real_double_click_on_a_check_box_still_leaves_the_command_alone()
    {
        Run(viewModel =>
        {
            LoadFiles(viewModel, 2);

            (ListBox list, CheckBox box, ListBoxItem _, Counter runs) = BuildFileList(viewModel);

            list.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
                Source = box,
            });

            Assert.Equal(0, runs.Runs);
        });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Counts how many times the attached command ran.
    /// </summary>
    /// <remarks>
    /// A class rather than an <c>int</c> returned from the helper: the count is only meaningful
    /// after the event has been raised, by which time a value returned alongside the list would
    /// already have been copied.
    /// </remarks>
    private sealed class Counter
    {
        public int Runs;
    }

    /// <summary>
    /// A realised list of changed files, shaped like the real one: a check box on the left and a path
    /// on the right, with the behaviour attached to a command that counts how often it runs.
    /// </summary>
    private static (ListBox list, CheckBox box, ListBoxItem row, Counter runs) BuildFileList(
        GitViewModel viewModel)
    {
        var counter = new Counter();

        var list = new ListBox
        {
            ItemsSource = viewModel.FilteredChanges,
            ItemTemplate = BuildRowTemplate(),
            Width = 400,
            Height = 200,
        };

        DoubleClickBehavior.SetCommand(list, new RelayCommand(_ => counter.Runs++));

        var presenter = new ContentPresenter { Content = list, Width = 400, Height = 200 };
        presenter.Measure(new Size(400, 200));
        presenter.Arrange(new Rect(0, 0, 400, 200));
        presenter.UpdateLayout();

        ListBoxItem row = Descendants(list).OfType<ListBoxItem>().First();
        CheckBox box = Descendants(row).OfType<CheckBox>().First();

        return (list, box, row, counter);
    }

    /// <summary>
    /// A click on a row that is not on a control the row owns, which is how a double-click on a path
    /// arrives.
    /// </summary>
    private static TextBlock PathOnRow(ListBox list) =>
        Descendants(list).OfType<TextBlock>().First(t => t.Name == "FileName");

    private static DataTemplate BuildRowTemplate()
    {
        // Built in code rather than loaded from the window's markup, so the test is not standing on
        // the very file it is checking: what matters here is the shape, not the styling.
        var template = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(Grid)) };

        FrameworkElementFactory grid = (FrameworkElementFactory)template.VisualTree;

        var checkBox = new FrameworkElementFactory(typeof(CheckBox));
        checkBox.SetValue(CheckBox.IsCheckedProperty, new Binding(nameof(GitFileChange.IsIncluded))
        {
            Mode = BindingMode.TwoWay,
        });
        checkBox.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        grid.AppendChild(checkBox);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(FrameworkElement.NameProperty, "FileName");
        text.SetBinding(
            TextBlock.TextProperty,
            new Binding(nameof(GitFileChange.FileName)));
        grid.AppendChild(text);

        return template;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (DependencyObject nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Filling the file list filters it once, not once per file.
    /// </summary>
    /// <remarks>
    /// The filtered list is derived state: every file added to the comparison triggers a rebuild of it,
    /// so a comparison of <em>n</em> files cost <em>n</em> rebuilds of a growing list - quadratic work,
    /// on a comparison between two branches that routinely runs to thousands of files. Counted here as
    /// the collection notifications the filtered list raises, which is both the visible cost (the list
    /// repaints and re-evaluates its containers on each one) and a direct measure of how many times it
    /// was rebuilt.
    /// <para>
    /// One rebuild raises one clear and one add per surviving file, so a list of <em>n</em> files with
    /// nothing filtered out must raise exactly <em>n</em> + 1. A per-file rebuild would raise
    /// <em>n</em> + <em>n</em><sup>2</sup>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Building_the_file_list_filters_it_once_rather_than_once_per_file()
    {
        Run(viewModel =>
        {
            const int Count = 40;
            int notifications = 0;

            viewModel.FilteredChanges.CollectionChanged += (_, __) => notifications++;

            LoadFiles(viewModel, Count);

            Assert.Equal(Count, viewModel.FilteredChanges.Count);
            Assert.Equal(Count + 1, notifications);

            // And a second comparison replaces the list rather than adding to it, so the same bound
            // holds every time the range changes.
            notifications = 0;
            LoadFiles(viewModel, Count);

            Assert.Equal(Count, viewModel.FilteredChanges.Count);
            Assert.Equal(Count + 1, notifications);
        });
    }

    /// <summary>Loads a comparison so the file list has something in it.</summary>
    private static void LoadFiles(GitViewModel viewModel, int count)
    {
        var files = new List<GitFileChange>();

        for (int i = 0; i < count; i++)
        {
            files.Add(new GitFileChange
            {
                Path = string.Format("src/file{0}.cs", i),
                Status = GitChangeStatus.Modified,
                StatusCode = "M",
                AddedLines = 1,
                DeletedLines = 0,
            });
        }

        viewModel.SetChangesForTest(files);
    }

    /// <summary>Drives the view model on its own STA thread.</summary>
    private static void Run(Action<GitViewModel> body)
    {
        Exception? failure = null;

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                string settingsPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "gdfc-settings-" + Guid.NewGuid().ToString("N") + ".json");

                var viewModel = new GitViewModel(
                    new AppSettingsStore(settingsPath),
                    pickFolder: (input, title) => null,
                    dispatcher: System.Windows.Threading.Dispatcher.CurrentDispatcher,
                    openFolder: _ => true);

                try
                {
                    body(viewModel);
                }
                finally
                {
                    try
                    {
                        System.IO.File.Delete(settingsPath);
                    }
                    catch (System.IO.IOException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "The test body did not finish in time.");
        if (failure != null)
        {
            throw failure;
        }
    }
}
