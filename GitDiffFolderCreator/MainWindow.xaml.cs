using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;

namespace GitDiffFolderCreator
{
    /// <summary>Main window. Holds no logic beyond wiring and the folder picker.</summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Created after InitializeComponent so the view model starts on the UI dispatcher and the
            // data context exists before the first property is set.
            DataContext = new GitViewModel(new AppSettingsStore(null), PickFolder, Dispatcher);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (DataContext is GitViewModel viewModel)
            {
                viewModel.SaveSettings();
            }
        }

        /// <summary>
        /// Ctrl+F puts the caret in the log's search box, the way it does everywhere else.
        /// </summary>
        /// <remarks>
        /// Preview, so it wins before the keystroke reaches whatever has the focus - a list, a check box,
        /// the filter box itself. Only Ctrl+F is claimed, so F5, F3 and the arrow keys keep working
        /// everywhere.
        /// </remarks>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                CommitFilterBox.Focus();
                CommitFilterBox.SelectAll();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Puts the caret in the filter box as the picker opens, so typing a branch name starts a
        /// search instead of having to be aimed at the box first.
        /// </summary>
        private void BranchPopup_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true)
            {
                BranchFilterBox.Focus();
                BranchFilterBox.SelectAll();
                BranchList.ScrollIntoView(BranchList.SelectedItem);
            }
        }

        /// <summary>
        /// Enter takes the highlighted branch, Escape leaves without choosing, and the arrows drive the
        /// list from the filter box, which is where the caret already is.
        /// </summary>
        private void BranchPopup_KeyDown(object sender, KeyEventArgs e)
        {
            if (!(DataContext is GitViewModel viewModel))
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Enter:
                    // Enter belongs to the highlighted row, not to whatever the caret sits in, so a
                    // filter typed but not yet applied still confirms the branch under the highlight.
                    viewModel.ConfirmPendingBranch();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    viewModel.CloseBranchList();
                    e.Handled = true;
                    break;

                case Key.Down:
                case Key.Up:
                    // Only intercepted from the filter box: inside the list the arrows already move
                    // the selection and must keep doing it the framework's way.
                    if (Keyboard.FocusedElement == BranchFilterBox)
                    {
                        viewModel.MoveBranchHighlight(e.Key == Key.Down ? 1 : -1);
                        BranchList.ScrollIntoView(BranchList.SelectedItem);
                        e.Handled = true;
                    }

                    break;
            }
        }

        /// <summary>
        /// Opens the diff tool picker and applies whatever is chosen.
        /// </summary>
        /// <remarks>
        /// The window is shown rather than a command bound to a property, because choosing is an
        /// interaction with a list and a preview, not a value that can be typed into a box. Only the
        /// result crosses back into the view model, which then persists it.
        /// </remarks>
        private void DiffToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is GitViewModel viewModel))
            {
                return;
            }

            string? chosen = DiffToolPickerWindow.Choose(this, viewModel.DiffToolCommand);

            if (chosen == null)
            {
                return;
            }

            viewModel.SetDiffToolCommand(chosen);
        }

        /// <summary>
        /// Opens a window with the whole of one commit: the message past its first line, who wrote
        /// and committed it, and the files it touched.
        /// </summary>
        /// <remarks>
        /// Shown rather than opened through a command bound to a property, in the same way the diff
        /// tool picker is: this is a window with its own content and its own filter, not a value that
        /// can be set. The button passes the row it belongs to as its command parameter, so there is
        /// no need to look up which commit was clicked.
        /// <para>
        /// Modeless, and a new window each time, because reading a commit is a detour rather than a
        /// decision - a user comparing two commits may well want both open at once.
        /// </para>
        /// </remarks>
        private void CommitDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is GitViewModel viewModel))
            {
                return;
            }

            if (!(sender is FrameworkElement { DataContext: Models.GitCommit commit }))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(viewModel.GitDirectory))
            {
                // Nothing to read against. The button only exists on rows that came from a loaded
                // repository, so this is a guard rather than a path a user can reach.
                return;
            }

            // The row's own hash, not the abbreviated one: git needs the object name, and an abbreviated
            // name can be ambiguous in a large repository. The diff tool choice comes with it so
            // that double-clicking a file in the details window uses the tool the user picked here,
            // rather than falling back to git's configuration behind their back.
            CommitDetailWindow.Open(
                this,
                new GitService(viewModel.GitDirectory),
                commit,
                viewModel.DiffToolCommand).Show();
        }

        /// <summary>
        /// Opens the change document checker.
        /// </summary>
        /// <remarks>
        /// Experimental and unrelated to the repository this window is showing, which is why it is a
        /// window rather than another band: it has its own inputs, its own output, and no interest in
        /// whichever two commits happen to be selected. Nothing crosses back into the view model.
        /// </remarks>
        private void ChangeDocumentButton_Click(object sender, RoutedEventArgs e)
        {
            ChangeDocumentWindow.Show(this);
        }

        private string? PickFolder(string initialDirectory, string title) =>
            ShellFolderPicker.Pick(initialDirectory, title);
    }
}