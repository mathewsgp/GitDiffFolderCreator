using System.Windows;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;

namespace GitDiffFolderCreator
{
    /// <summary>
    /// Shows everything about one commit that the log row has no room for, including the files it
    /// touched.
    /// </summary>
    /// <remarks>
    /// Holds no logic of its own: the loading, the totals and the file filter belong to
    /// <see cref="CommitDetailViewModel"/>. Shown as a modeless window rather than a dialog because
    /// reading a commit is a detour rather than a decision, and the user may well want to pick a
    /// different commit while this one is still open.
    /// </remarks>
    public partial class CommitDetailWindow : Window
    {
        public CommitDetailWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Opens the details for one commit and returns the window, already loaded.
        /// </summary>
        /// <remarks>
        /// The window is returned rather than shown so the caller decides: a test can load the data
        /// without a window appearing, and the click handler owns whether a second window for a
        /// different commit replaces this one or sits beside it.
        /// <para>
        /// The two calls the view model makes are started before the window is shown, so the loading
        /// state is already true by the time anything renders.
        /// </para>
        /// </remarks>
        /// <param name="diffToolCommand">
        /// The tool chosen in the main window's picker, or <c>null</c> to use whatever git is configured
        /// with. Passed in rather than read from settings here, so this window stays a view over one commit
        /// and knows nothing about where the rest of the application keeps its configuration.
        /// </param>
        public static CommitDetailWindow Open(
            Window? owner,
            GitService git,
            GitCommit commit,
            string? diffToolCommand = null,
            CancellationToken cancellationToken = default)
        {
            var model = new CommitDetailViewModel(git, commit, diffToolCommand);

            var window = new CommitDetailWindow
            {
                DataContext = model,
                Title = string.IsNullOrWhiteSpace(commit.ShortHash)
                    ? "Commit details"
                    : "Commit " + commit.ShortHash,
            };

            // Only when there is one to have: WPF refuses an owner that has never been shown, and a
            // null owner leaves the window with no parent rather than failing to open it at all.
            if (owner != null)
            {
                window.Owner = owner;
            }

            // Fire and forget: the view model records any failure in the window rather than
            // throwing, so there is nothing here to await and nothing left unhandled.
            _ = model.LoadAsync(cancellationToken);

            return window;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
