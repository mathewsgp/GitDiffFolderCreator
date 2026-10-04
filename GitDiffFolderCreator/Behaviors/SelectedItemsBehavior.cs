using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GitDiffFolderCreator.Behaviors
{
    /// <summary>
    /// Exposes <see cref="ListBox.SelectedItems"/> as a bindable property so the view model can
    /// react to a multiple selection without an event handler in the code-behind, and caps the
    /// selection at a fixed number of items.
    /// </summary>
    /// <remarks>
    /// Both directions are synchronised: writing to the attached property updates the list, and
    /// changing the list updates the bound collection in place. Handlers are detached while
    /// synchronising so the two directions cannot drive each other in a loop.
    /// <para>
    /// When <see cref="MaxSelectionProperty"/> is set and the user adds an item past the cap, the
    /// previously selected items are dropped and the newest selection wins. That is the behaviour
    /// people expect from "pick two commits": the third click replaces the pair rather than leaving
    /// three highlighted and the view model refusing to act.
    /// </para>
    /// </remarks>
    public static class SelectedItemsBehavior
    {
        public static readonly DependencyProperty SelectedItemsProperty =
            DependencyProperty.RegisterAttached(
                "SelectedItems",
                typeof(IList),
                typeof(SelectedItemsBehavior),
                new PropertyMetadata(null, OnSelectedItemsChanged));

        /// <summary>Largest selection the user can make. Zero or less means unlimited.</summary>
        public static readonly DependencyProperty MaxSelectionProperty =
            DependencyProperty.RegisterAttached(
                "MaxSelection",
                typeof(int),
                typeof(SelectedItemsBehavior),
                new PropertyMetadata(0));

        public static readonly DependencyProperty TruncateSelectionProperty =
            DependencyProperty.RegisterAttached(
                "TruncateSelection",
                typeof(bool),
                typeof(SelectedItemsBehavior),
                new PropertyMetadata(false));

        private static readonly DependencyProperty IsHookedProperty =
            DependencyProperty.RegisterAttached(
                "IsHooked",
                typeof(bool),
                typeof(SelectedItemsBehavior),
                new PropertyMetadata(false));

        private static readonly DependencyProperty IsSyncingProperty =
            DependencyProperty.RegisterAttached(
                "IsSyncing",
                typeof(bool),
                typeof(SelectedItemsBehavior),
                new PropertyMetadata(false));

        /// <summary>
        /// Items in the order the user picked them, so an over-long selection can drop the oldest.
        /// </summary>
        private static readonly DependencyProperty ClickOrderProperty =
            DependencyProperty.RegisterAttached(
                "ClickOrder",
                typeof(List<object>),
                typeof(SelectedItemsBehavior),
                new PropertyMetadata(null));

        public static IList? GetSelectedItems(DependencyObject element)
        {
            return element.GetValue(SelectedItemsProperty) as IList;
        }

        public static void SetSelectedItems(DependencyObject element, IList? value)
        {
            element.SetValue(SelectedItemsProperty, value);
        }

        public static int GetMaxSelection(DependencyObject element)
        {
            return (int)element.GetValue(MaxSelectionProperty);
        }

        public static void SetMaxSelection(DependencyObject element, int value)
        {
            element.SetValue(MaxSelectionProperty, value);
        }

        public static bool GetTruncateSelection(DependencyObject element)
        {
            return (bool)element.GetValue(TruncateSelectionProperty);
        }

        public static void SetTruncateSelection(DependencyObject element, bool value)
        {
            element.SetValue(TruncateSelectionProperty, value);
        }

        private static void OnSelectedItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ListBox listBox))
            {
                return;
            }

            Hook(listBox);

            IList? target = e.NewValue as IList;
            if (target == null)
            {
                return;
            }

            Sync(listBox, () =>
            {
                listBox.SelectedItems.Clear();
                foreach (object item in target)
                {
                    listBox.SelectedItems.Add(item);
                }
            });
        }

        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is ListBox listBox))
            {
                return;
            }

            IList? target = GetSelectedItems(listBox);
            if (target == null)
            {
                return;
            }

            int max = GetMaxSelection(listBox);

            if (max > 0 && listBox.SelectedItems.Count > max && GetTruncateSelection(listBox))
            {
                Truncate(listBox, max);
            }

            Sync(listBox, () =>
            {
                target.Clear();
                foreach (object item in listBox.SelectedItems)
                {
                    target.Add(item);
                }
            });

            listBox.SetValue(
                ClickOrderProperty,
                new List<object>(listBox.SelectedItems.Cast<object>()));
        }

        /// <summary>
        /// Reduces an over-long selection to <paramref name="max"/> items, keeping the ones most
        /// recently involved rather than the first ones in list order.
        /// </summary>
        /// <remarks>
        /// <see cref="ListBox.SelectedItems"/> is in list order, so "keep the last two" would drop the
        /// wrong items for a user who ctrl-clicked a third commit: the item they just clicked would be
        /// discarded instead of the oldest. The previous selection is compared against the current one
        /// to recover the click sequence - the items that are new come last, and within the already
        /// selected the previously reported order is the order the user picked them.
        /// </remarks>
        private static void Truncate(ListBox listBox, int max)
        {
            List<object> current = new List<object>(listBox.SelectedItems.Cast<object>());
            List<object>? previous = listBox.GetValue(ClickOrderProperty) as List<object>;

            List<object> keep = SelectToKeep(current, previous, max);

            Sync(listBox, () =>
            {
                listBox.SelectedItems.Clear();
                foreach (object item in keep)
                {
                    listBox.SelectedItems.Add(item);
                }
            });
        }

        /// <summary>
        /// Chooses which items survive an over-long selection, most recently picked first. Extracted
        /// from the WPF plumbing so the rule can be tested without a dispatcher thread.
        /// </summary>
        internal static List<object> SelectToKeep(IList<object> current, IList<object>? previous, int max)
        {
            if (max <= 0 || current.Count <= max)
            {
                return new List<object>(current);
            }

            if (previous == null || previous.Count == 0 || previous.Count >= current.Count)
            {
                // No usable click history: fall back to list order, most recent row first.
                return current.Skip(current.Count - max).Take(max).Reverse().ToList();
            }

            // Newly selected items are ordered most-recent-first so a shift-click range keeps the end
            // of the range rather than its start; the carried-over items then fill the remaining slots.
            List<object> keep = current.Where(item => !previous.Contains(item)).Reverse().Take(max).ToList();

            foreach (object item in previous.Reverse<object>())
            {
                if (keep.Count == max)
                {
                    break;
                }

                if (!keep.Contains(item))
                {
                    keep.Insert(keep.Count, item);
                }
            }

            return keep;
        }

        /// <summary>Runs a mutation with the selection handlers detached so it cannot re-enter.</summary>
        private static void Sync(ListBox listBox, Action action)
        {
            if ((bool)listBox.GetValue(IsSyncingProperty))
            {
                return;
            }

            Detach(listBox);
            listBox.SetValue(IsSyncingProperty, true);
            try
            {
                action();
            }
            finally
            {
                listBox.SetValue(IsSyncingProperty, false);
                Attach(listBox);
            }
        }

        private static void Hook(ListBox listBox)
        {
            if ((bool)listBox.GetValue(IsHookedProperty))
            {
                return;
            }

            listBox.SetValue(IsHookedProperty, true);
            Attach(listBox);
        }

        private static void Attach(ListBox listBox)
        {
            listBox.SelectionChanged += OnSelectionChanged;
        }

        private static void Detach(ListBox listBox)
        {
            listBox.SelectionChanged -= OnSelectionChanged;
        }
    }
}
