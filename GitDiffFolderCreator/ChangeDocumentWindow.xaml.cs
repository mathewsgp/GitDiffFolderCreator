using System;
using System.Windows;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;

namespace GitDiffFolderCreator
{
    /// <summary>
    /// Cross-checks a change document against the real difference between two source folders.
    /// </summary>
    /// <remarks>
    /// Experimental, and the only part of the application with nothing to do with Git. It exists because
    /// a change document and a pair of source trees can disagree, and neither one can tell that on its
    /// own: the document is prose that happens to contain a list, and the folders only say what is on
    /// disk.
    /// <para>
    /// Holds no logic — the comparison and the reconciliation belong to
    /// <see cref="ChangeDocumentVerifier"/> and <see cref="FolderComparer"/>, and the window supplies
    /// only the two shell pickers, which are the one thing here that needs a dialog.
    /// </para>
    /// <para>
    /// Its own window rather than another band, because it is a separate job with its own inputs and
    /// rather a lot of output, and it has nothing to do with whichever repository happens to be loaded.
    /// </para>
    /// </remarks>
    public partial class ChangeDocumentWindow : Window
    {
        public ChangeDocumentWindow()
        {
            InitializeComponent();
        }

        /// <summary>Shows the checker over the given owner.</summary>
        /// <remarks>
        /// Given a settings store of its own, because this window is the one that owns the two section
        /// markers and it has to read them before anything is shown and write them back after. Saving
        /// again as the window closes covers the one edit the binding never saw: a marker typed and the
        /// window shut without the focus ever leaving the box.
        /// </remarks>
        public static void Show(Window? owner)
        {
            ChangeDocumentViewModel model = new ChangeDocumentViewModel(
                (start, title) => ShellFolderPicker.Pick(start, title),
                PickDocument,
                PickReportFile,
                CopyToClipboard,
                new AppSettingsStore(null));

            ChangeDocumentWindow window = new ChangeDocumentWindow
            {
                DataContext = model,
                Owner = owner,
            };

            window.Closed += (_, _) => model.SaveMarkers();

            window.ShowDialog();
        }

        /// <summary>
        /// Picks the document, filtered to Word documents.
        /// </summary>
        /// <remarks>
        /// Filtered rather than free, because a .doc saved with a .docx name opens as a package and
        /// fails in a way that reads as the tool being broken. The filter makes the intended format the
        /// easy choice; All files stays available so an unsupported file produces the real message
        /// rather than being unreachable.
        /// </remarks>
        private static string? PickDocument(string? initialDirectory)
        {
            string start = initialDirectory ?? string.Empty;

            try
            {
                if (System.IO.Directory.Exists(start))
                {
                    start = System.IO.Path.GetDirectoryName(start) ?? start;
                }
            }
            catch (ArgumentException)
            {
                // A malformed path is not worth a dialog failure; the shell dialog picks its own.
            }

            using System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Select the change document",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
                AutoUpgradeEnabled = true,
                Filter = "Word documents (*.docx)|*.docx|All files (*.*)|*.*",
            };

            if (start.Length > 0)
            {
                dialog.InitialDirectory = start;
            }

            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
        }

        /// <summary>
        /// Asks where to save the findings, offering both formats the writer understands.
        /// </summary>
        /// <remarks>
        /// Starting beside the document, because the report belongs with the thing it is reporting on
        /// rather than in a folder the reader has to go and find.
        /// </remarks>
        private static string? PickReportFile(string suggestedName)
        {
            using System.Windows.Forms.SaveFileDialog dialog = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Save the findings",
                FileName = suggestedName,
                AddExtension = true,
                OverwritePrompt = true,
                AutoUpgradeEnabled = true,
                Filter = "Comma-separated values (*.csv)|*.csv|Text files (*.txt)|*.txt",
            };

            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
        }

        /// <summary>
        /// Puts text on the clipboard, retrying once.
        /// </summary>
        /// <remarks>
        /// The clipboard is owned by whichever process last opened it, so a single call can lose a race
        /// with something else on the machine. One retry turns the usual transient case into a
        /// non-event, and the exception still reaches the view model if both attempts fail.
        /// </remarks>
        private static void CopyToClipboard(string text)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    return;
                }
                catch (System.Runtime.InteropServices.ExternalException) when (attempt == 0)
                {
                }
            }
        }
    }
}