using System.Windows;

namespace GitDiffFolderCreator.Behaviors;

/// <summary>
/// Publishes whether a row is under the pointer, so controls inside it can appear on hover.
/// </summary>
/// <remarks>
/// <para>
/// <c>IsMouseOver</c> is not a dependency property, so a control inside a row cannot bind to it on the
/// row's behalf. This carries the same answer across as something that can be bound, which is what lets
/// the commit row's details button stay out of the way until the row is pointed at.
/// </para>
/// <para>
/// The keyboard sets it too. A button that only appears under the pointer cannot be reached without one:
/// it is collapsed exactly when nobody is pointing at it, so tabbing would walk straight past. Focus
/// counts as hovering for as long as the row holds it, which keeps the row the keyboard is on fully
/// usable.
/// </para>
/// </remarks>
public static class RowHover
{
    public static readonly DependencyProperty IsHoveredProperty =
        DependencyProperty.RegisterAttached(
            "IsHovered",
            typeof(bool),
            typeof(RowHover),
            new PropertyMetadata(false));

    public static void SetIsHovered(DependencyObject element, bool value) =>
        element.SetValue(IsHoveredProperty, value);

    public static bool GetIsHovered(DependencyObject element) =>
        (bool)element.GetValue(IsHoveredProperty);
}
