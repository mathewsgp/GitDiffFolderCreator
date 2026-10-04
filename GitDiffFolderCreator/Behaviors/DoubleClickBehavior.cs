using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace GitDiffFolderCreator.Behaviors;

/// <summary>
/// Raises <see cref="Command"/> when a row in a list is double-clicked.
/// </summary>
/// <remarks>
/// An attached property rather than a handler in the window, so the list needs no code-behind and the
/// command can be bound like any other. Only rows are acted on: a double-click on empty space below
/// the last row has no item under the pointer and is left alone.
/// <para>
/// This listens for a genuine double-click rather than for the first press. An earlier version hooked
/// the first mouse-down, which meant every single click on a row ran the command - including clicks
/// on a check box inside the row, where the file was being ticked and the diff was opened in the same
/// gesture.
/// </para>
/// </remarks>
public static class DoubleClickBehavior
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command",
            typeof(ICommand),
            typeof(DoubleClickBehavior),
            new PropertyMetadata(null, OnCommandChanged));

    public static void SetCommand(DependencyObject element, ICommand value) =>
        element.SetValue(CommandProperty, value);

    public static ICommand? GetCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(CommandProperty);

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.RegisterAttached(
            "CommandParameter",
            typeof(object),
            typeof(DoubleClickBehavior),
            new PropertyMetadata(null, OnCommandChanged));

    public static void SetCommandParameter(DependencyObject element, object value) =>
        element.SetValue(CommandParameterProperty, value);

    public static object? GetCommandParameter(DependencyObject element) =>
        element.GetValue(CommandParameterProperty);

    /// <summary>
    /// Controls whose own click already means something. A double-click that lands on one of these is
    /// left to it.
    /// </summary>
    /// <remarks>
    /// A check box and a row are two different intentions sharing one gesture: one says "include this
    /// file", the other says "show me this file". Only the second should open a diff, or the user
    /// cannot tick a file without also launching a tool over it.
    /// </remarks>
    private static bool IsInteractiveControl(object element) =>
        element is CheckBox or ButtonBase or TextBoxBase;

    private static void OnCommandChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        // Reassigned rather than removed first: WPF raises this even when the value goes back to
        // null, and removing a handler that was never added is harmless, so this stays simple.
        control.MouseDoubleClick -= OnDoubleClick;
        control.MouseDoubleClick += OnDoubleClick;
    }

    private static void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl control || GetCommand(control) is not ICommand command)
        {
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (!TryResolveRow(control, e.OriginalSource, out object? item))
        {
            return;
        }

        object? parameter = GetCommandParameter(control) ?? item;

        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }

        // Consumed so the row underneath does not also start editing or toggling.
        e.Handled = true;
    }

    /// <summary>
    /// Works out whether a hit at <paramref name="originalSource"/> is a double-click on a row that
    /// should run the command, and which item that row holds.
    /// </summary>
    /// <returns>
    /// False when the hit was outside any row, or landed on a control inside the row that handles its
    /// own clicks.
    /// </returns>
    /// <remarks>
    /// Separated from the event handler so the decision can be tested directly. Synthesising a real
    /// double-click needs a mouse device and a live input queue, whereas building the tree and asking
    /// this question about it is ordinary work.
    /// <para>
    /// The row is resolved from the pointer rather than from the selection: a double-click lands on a
    /// row and both should agree, but resolving from the pointer is what makes a click on an
    /// already-selected row in a filtered list behave predictably.
    /// </para>
    /// </remarks>
    internal static bool TryResolveRow(ItemsControl control, object? originalSource, out object? item)
    {
        item = null;

        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        DependencyObject? row = ItemsControl.ContainerFromElement(control, source);

        while (row != null && ItemsControl.ItemsControlFromItemContainer(row) != control)
        {
            row = VisualTreeHelperParent(row);
        }

        if (row is null)
        {
            return false;
        }

        item = ItemsControl.ItemsControlFromItemContainer(row) is ItemsControl owner
            ? owner.ItemContainerGenerator.ItemFromContainer(row)
            : null;

        if (item is null)
        {
            return false;
        }

        // Walked before returning, so a double-click on a check box does nothing at all rather than
        // opening a diff and letting the check box toggle afterwards.
        if (IsInteractiveHit(control, source))
        {
            // Cleared as well, so that a false result really does mean "no row": a caller that
            // ignored the return value would otherwise act on the row the pointer happened to be
            // over, which is the very thing being avoided.
            item = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the double-click landed on a control inside the row that handles its own clicks.
    /// </summary>
    private static bool IsInteractiveHit(ItemsControl control, object? originalSource)
    {
        DependencyObject? current = originalSource as DependencyObject;

        while (current != null && current != control)
        {
            if (IsInteractiveControl(current))
            {
                return true;
            }

            current = VisualTreeHelperParent(current);
        }

        return false;
    }

    private static DependencyObject? VisualTreeHelperParent(DependencyObject child)
    {
        return child is Visual || child is Visual3D
            ? VisualTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child);
    }
}
