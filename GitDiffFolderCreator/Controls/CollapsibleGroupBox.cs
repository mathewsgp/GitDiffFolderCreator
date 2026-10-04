using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace GitDiffFolderCreator.Controls
{
    /// <summary>
    /// A titled panel whose header doubles as a toggle, so each section of the window can be folded
    /// out of the way.
    /// </summary>
    /// <remarks>
    /// Built as a <see cref="HeaderedContentControl"/> rather than a themed <see cref="GroupBox"/>
    /// because the stock template has nowhere to put a chevron or a hover state, and restyling it
    /// would mean overriding its internal parts. The header is a real <see cref="ToggleButton"/> so
    /// keyboard users get Space/Enter, tab order, and an automation name for free.
    /// </remarks>
    public class CollapsibleGroupBox : HeaderedContentControl
    {
        public static readonly DependencyProperty IsExpandedProperty =
            DependencyProperty.RegisterAttached(
                "IsExpanded",
                typeof(bool),
                typeof(CollapsibleGroupBox),
                new FrameworkPropertyMetadata(
                    true,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnIsExpandedChanged));

        public static readonly DependencyProperty IsCollapsibleProperty =
            DependencyProperty.Register(
                nameof(IsCollapsible),
                typeof(bool),
                typeof(CollapsibleGroupBox),
                new PropertyMetadata(true));

        /// <summary>
        /// Whether the panel has a header at all. A panel that is both fixed open and headerless is
        /// just a titled block of content, which is what the repository panel wants: its rows carry
        /// their own labels, so a caption above them would only repeat them.
        /// </summary>
        public static readonly DependencyProperty ShowHeaderProperty =
            DependencyProperty.Register(
                nameof(ShowHeader),
                typeof(bool),
                typeof(CollapsibleGroupBox),
                new PropertyMetadata(true));

        /// <summary>
        /// Padding between the rule and the content. There is no frame, so the default is vertical
        /// only: a DevExpress-style card sits flush to the left edge of the surface.
        /// </summary>
        public static readonly DependencyProperty BodyPaddingProperty =
            DependencyProperty.Register(
                nameof(BodyPadding),
                typeof(Thickness),
                typeof(CollapsibleGroupBox),
                new PropertyMetadata(new Thickness(0, 8, 0, 10)));

        public static readonly DependencyProperty ChevronAngleProperty =
            DependencyProperty.RegisterAttached(
                "ChevronAngle",
                typeof(double),
                typeof(CollapsibleGroupBox),
                // 90 is the open state. The change callback below only runs when the value is
                // actually set, so the default here is what a freshly loaded panel would show.
                new PropertyMetadata(90.0));

        static CollapsibleGroupBox()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(CollapsibleGroupBox), new FrameworkPropertyMetadata(typeof(CollapsibleGroupBox)));
        }

        public CollapsibleGroupBox()
        {
            // The header is the toggle target, so a click anywhere in the header row folds the panel
            // without the caller wiring up a command.
            Focusable = false;
        }

        public bool IsExpanded
        {
            get { return (bool)GetValue(IsExpandedProperty); }
            set { SetValue(IsExpandedProperty, value); }
        }

        public bool IsCollapsible
        {
            get { return (bool)GetValue(IsCollapsibleProperty); }
            set { SetValue(IsCollapsibleProperty, value); }
        }

        public bool ShowHeader
        {
            get { return (bool)GetValue(ShowHeaderProperty); }
            set { SetValue(ShowHeaderProperty, value); }
        }

        public Thickness BodyPadding
        {
            get { return (Thickness)GetValue(BodyPaddingProperty); }
            set { SetValue(BodyPaddingProperty, value); }
        }

        public static double GetChevronAngle(DependencyObject element) =>
            (double)element.GetValue(ChevronAngleProperty);

        public static void SetChevronAngle(DependencyObject element, double value) =>
            element.SetValue(ChevronAngleProperty, value);

        private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = (CollapsibleGroupBox)d;
            box.SetValue(ChevronAngleProperty, (bool)e.NewValue ? 90.0 : 0.0);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            // Enter and Space fold the panel when focus is anywhere inside it, which matches what a
            // disclosure widget is expected to do.
            if (e.Key != Key.Enter && e.Key != Key.Space)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            if (IsCollapsible && ShowHeader && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                IsExpanded = !IsExpanded;
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
        }
    }
}
