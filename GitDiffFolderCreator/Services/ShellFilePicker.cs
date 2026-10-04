using System;
using System.IO;
using System.Windows.Forms;

namespace GitDiffFolderCreator.Services
{
    /// <summary>
    /// Picks an executable, for the "any program" option in the diff tool picker.
    /// </summary>
    /// <remarks>
    /// The stock shell open dialog again, rather than a custom dialog: it filters to executables and
    /// offers the address bar and search that make a program findable. A tool outside the known list
    /// has to be reachable somehow, and typing its path is not it.
    /// </remarks>
    public static class ShellFilePicker
    {
        /// <summary>
        /// Shows the dialog and returns the chosen program, or <c>null</c> when cancelled.
        /// </summary>
        public static string? PickExecutable(string? initialDirectory)
        {
            string? start = Directory.Exists(initialDirectory) ? initialDirectory : null;

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select the comparison program";
                dialog.CheckFileExists = true;
                dialog.CheckPathExists = true;
                dialog.Multiselect = false;
                dialog.RestoreDirectory = true;
                dialog.AutoUpgradeEnabled = true;
                dialog.Filter = "Programs (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*";

                if (start != null)
                {
                    dialog.InitialDirectory = start;
                }

                return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            }
        }
    }
}