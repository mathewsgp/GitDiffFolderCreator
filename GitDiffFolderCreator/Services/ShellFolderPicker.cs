using System;
using System.IO;
using System.Windows.Forms;

namespace GitDiffFolderCreator.Services
{
    /// <summary>
    /// Folder picker built on the standard shell open dialog.
    /// </summary>
    /// <remarks>
    /// <c>FolderBrowserDialog</c> renders a legacy tree view with no address bar and no search,
    /// which makes reaching a deeply nested or off-tree path painful. The regular open dialog is the
    /// same one File Explorer uses - navigation pane, address bar, search, Quick Access - and is
    /// turned into a folder picker by disabling file-name validation and showing a short hint in the
    /// name box, so pressing Open without navigating selects the folder being viewed.
    /// </remarks>
    public static class ShellFolderPicker
    {
        /// <summary>
        /// Hint text shown in the name box when the dialog opens. Pressing Open without touching the
        /// name box selects the folder currently being viewed.
        /// </summary>
        internal const string PlaceholderText = "Select folder";

        /// <summary>Shows the dialog and returns the chosen folder, or null when cancelled.</summary>
        public static string? Pick(string? initialDirectory, string title)
        {
            string? start = Directory.Exists(initialDirectory) ? initialDirectory : null;

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = title;
                dialog.CheckFileExists = false;
                dialog.CheckPathExists = true;
                dialog.ValidateNames = false;
                dialog.Multiselect = false;
                dialog.RestoreDirectory = true;
                dialog.AutoUpgradeEnabled = true;
                dialog.FileName = PlaceholderText;

                if (start != null)
                {
                    dialog.InitialDirectory = start;
                }

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return null;
                }

                return ResolveChosenPath(dialog.FileName, start);
            }
        }

        /// <summary>
        /// Turns the dialog's file-name result into a folder. The dialog returns whatever was in the
        /// name box, which is a folder path rather than a file path in this mode.
        /// </summary>
        internal static string? ResolveChosenPath(string? chosen, string? fallbackDirectory)
        {
            if (string.IsNullOrWhiteSpace(chosen))
            {
                return null;
            }

            // Untouched placeholder: the user pressed Open without navigating anywhere.
            if (string.Equals(chosen!.Trim(), PlaceholderText, StringComparison.OrdinalIgnoreCase))
            {
                return fallbackDirectory;
            }

            if (Directory.Exists(chosen))
            {
                return Path.GetFullPath(chosen);
            }

            // A bare folder name comes back relative to the folder that was opened.
            if (!Path.IsPathRooted(chosen) && fallbackDirectory != null)
            {
                string combined = Path.Combine(fallbackDirectory, chosen);
                if (Directory.Exists(combined))
                {
                    return Path.GetFullPath(combined);
                }
            }

            // The name box held something that is not a folder; fall back to its parent, or to the
            // folder that was opened when there is no usable parent.
            string? parent = Path.GetDirectoryName(chosen);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                return Path.GetFullPath(parent);
            }

            return fallbackDirectory;
        }
    }
}