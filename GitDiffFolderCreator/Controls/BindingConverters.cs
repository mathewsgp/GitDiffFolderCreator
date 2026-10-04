using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using GitDiffFolderCreator.Models;

namespace GitDiffFolderCreator.Controls
{
    /// <summary>
    /// The inverse of the stock boolean-to-visibility rule.
    /// </summary>
    /// <remarks>
    /// Several mutually exclusive parts of a badge are shown by their own triggers rather than by
    /// swapping one control's content, so each state needs both its own look and its own visibility
    /// rule. Inverting here keeps that symmetric without a second style per state.
    /// </remarks>
    public sealed class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture) =>
            value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Shows an element only when the bound text has content, so an empty ref name does not leave a
    /// chip-sized gap behind.
    /// </summary>
    public sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture) =>
            string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Shows an element only when a commit's resolved role matches the converter parameter.
    /// </summary>
    /// <remarks>
    /// Comparing against a parameter rather than writing one trigger per role keeps the base and
    /// modified pills to a single style that the same markup drives for both.
    /// </remarks>
    public sealed class CommitRoleToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture) =>
            value is GitCommitRole role && string.Equals(role.ToString(), parameter as string, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public object ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Turns a fraction of 1 into a star column width, so a grid column can be sized by a proportion
    /// of its container.
    /// </summary>
    /// <remarks>
    /// The progress bar's fill is two star columns - the filled one and the remainder - rather than a
    /// single column with a percentage width, because a star column tracks the track when the window
    /// is resized while a fixed pixel width does not. Binding a <see cref="ColumnDefinition"/>'s
    /// <c>Width</c> to a plain number would give absolute pixels; the fraction only means anything to
    /// the layout as a star, so the conversion happens here.
    /// <para>
    /// The value is clamped, and a negative or unparseable one becomes zero, so a bad report collapses
    /// the fill rather than throwing from inside a layout pass - where the exception would be lost and
    /// the bar left in whatever state it last drew.
    /// </para>
    /// </remarks>
    public sealed class FractionToStarConverter : IValueConverter
    {
        public object Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        {
            double fraction = 0d;

            if (value is double number && !double.IsNaN(number))
            {
                fraction = Math.Max(0d, Math.Min(1d, number));
            }

            return new GridLength(fraction, GridUnitType.Star);
        }

        public object ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture) =>
            throw new NotSupportedException();
    }
}
