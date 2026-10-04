using System;
using System.Diagnostics;
using System.IO;

namespace GitDiffFolderCreator.Services;

/// <summary>
/// Reveals a folder in File Explorer.
/// </summary>
/// <remarks>
/// <c>explorer.exe</c> is started directly with no shell involved, so a folder name containing shell
/// metacharacters such as <c>&amp;</c> is passed through as a pathname. Explorer reports a non-zero
/// exit code even on success - it hands the folder to an existing window and exits - so the exit
/// code is deliberately not inspected. Only the start attempt is checked.
/// </remarks>
public static class ShellFolderOpener
{
    /// <summary>
    /// Opens <paramref name="path"/> in a File Explorer window, or selects the file when
    /// <paramref name="path"/> is a file.
    /// </summary>
    /// <returns>False when the path is missing or does not exist, in which case nothing is launched.</returns>
    public static bool Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) && !File.Exists(path))
        {
            return false;
        }

        try
        {
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = WindowsArgumentString.Build(new[] { path! }),
                };

                process.Start();
            }

            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Explorer is missing or refused the launch; the caller reports this as a message rather
            // than throwing, since a failed reveal must not look like a failed export.
            return false;
        }
    }
}
