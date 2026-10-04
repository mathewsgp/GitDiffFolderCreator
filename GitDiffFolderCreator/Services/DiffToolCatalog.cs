using System;
using System.Collections.Generic;
using System.IO;

namespace GitDiffFolderCreator.Services;

/// <summary>One diff tool that could be offered to the user.</summary>
public sealed class DiffToolChoice
{
    public DiffToolChoice(string id, string displayName, string command, string detail)
    {
        Id = id;
        DisplayName = displayName;
        Command = command;
        Detail = detail;
    }

    /// <summary>Stable identifier, used to remember the choice.</summary>
    public string Id { get; }

    /// <summary>The name shown in the picker.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// The command line to run, with <c>$LOCAL</c> and <c>$REMOTE</c> standing in for the two files.
    /// </summary>
    public string Command { get; }

    /// <summary>Where the executable was found, shown under the name.</summary>
    public string Detail { get; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// Finds the comparison tools installed on this machine and remembers the one chosen.
/// </summary>
/// <remarks>
/// <para>
/// This exists so choosing a tool is a matter of picking a name rather than typing a git command.
/// The tools themselves are located the way a shell would - from the usual install locations and
/// <c>PATH</c> - and nothing is registered with git: choosing here writes to this application's own
/// settings, leaving the user's git configuration untouched.
/// </para>
/// <para>
/// The known list is deliberately short and covers the tools people actually install. Anything not
/// on it can still be reached by choosing a file directly, which is why the picker offers that too -
/// an unlisted tool is not a reason to fall back to a command line.
/// </para>
/// </remarks>
public sealed class DiffToolCatalog
{
    /// <summary>A well-known tool: a name, an executable to look for, and where it usually lives.</summary>
    private sealed class KnownTool
    {
        public KnownTool(string id, string displayName, string executable, string[] relativePaths)
        {
            Id = id;
            DisplayName = displayName;
            Executable = executable;
            RelativePaths = relativePaths;
        }

        public string Id { get; }

        public string DisplayName { get; }

        /// <summary>File name, looked for under each install root.</summary>
        public string Executable { get; }

        /// <summary>
        /// Paths relative to an install root. Several versions are listed because a machine may hold
        /// any of them, and the newest is tried first.
        /// </summary>
        public string[] RelativePaths { get; }
    }

    private static readonly KnownTool[] Known =
    {
        new KnownTool(
            "beyondcompare",
            "Beyond Compare",
            "BCompare.exe",
            new[]
            {
                @"Beyond Compare 4\BCompare.exe",
                @"Beyond Compare 3\BCompare.exe",
                @"Beyond Compare 4\bcomp.exe",
            }),

        new KnownTool(
            "winmerge",
            "WinMerge",
            "WinMergeU.exe",
            new[]
            {
                @"WinMerge\WinMergeU.exe",
                @"WinMerge\WinMerge.exe",
            }),

        new KnownTool(
            "kdiff3",
            "KDiff3",
            "kdiff3.exe",
            new[]
            {
                @"KDiff3\kdiff3.exe",
                @"kdiff3\bin\kdiff3.exe",
            }),

        new KnownTool(
            "meld",
            "Meld",
            "meld.exe",
            new[]
            {
                @"Meld\meld.exe",
                @"Meld\meld-3\bin\meld.exe",
            }),

        new KnownTool(
            "diffmerge",
            "DiffMerge",
            "DiffMerge.exe",
            new[]
            {
                @"DiffMerge\DiffMerge.exe",
                @"DiffMerge\DiffMergeGUI.exe",
            }),

        new KnownTool(
            "tortoisemerge",
            "TortoiseMerge",
            "TortoiseMerge.exe",
            new[]
            {
                @"TortoiseSVN\bin\TortoiseMerge.exe",
                @"TortoiseGit\bin\TortoiseGitMerge.exe",
            }),

        new KnownTool(
            "vscode",
            "Visual Studio Code",
            "Code.exe",
            new[]
            {
                // The per-user installer writes under Programs, not straight into LocalApplicationData.
                // A machine with no machine-wide install has VS Code here and nowhere else, which is
                // why listing only the other two made it the one tool that never appeared.
                @"Programs\Microsoft VS Code\Code.exe",

                // The machine-wide installer.
                @"Microsoft VS Code\Code.exe",

                // Only Code.exe, never bin\code.cmd: the launcher starts the program directly with
                // UseShellExecute off, and a batch file needs a shell to run at all. Offering one
                // would put an entry in the list that cannot be launched.
            }),

        new KnownTool(
            "notepadplusplus",
            "Notepad++",
            "notepad++.exe",
            new[]
            {
                @"Notepad++\notepad++.exe",
            }),
    };

    /// <summary>
    /// A tool that needs arguments beyond the two paths.
    /// </summary>
    /// <remarks>
    /// A plain two-argument diff is the default because that is what most tools accept, and passing
    /// something else to an unknown tool is as likely to break it as to help it. These three are
    /// known to need more.
    /// </remarks>
    private static string CommandFor(string id, string executable)
    {
        switch (id)
        {
            // Code opens a diff editor rather than two windows, and --wait keeps the process alive so
            // the launcher reports the tool as started rather than immediately gone.
            case "vscode":
                return Quote(executable) + " --diff --wait \"$LOCAL\" \"$REMOTE\"";

            // Beyond Compare's own switches for a read-only comparison in each pane.
            case "beyondcompare":
                return Quote(executable) + " /ro1 \"$LOCAL\" /ro2 \"$REMOTE\"";

            case "winmerge":
                return Quote(executable) + " \"$LOCAL\" \"$REMOTE\"";

            default:
                return Quote(executable) + " \"$LOCAL\" \"$REMOTE\"";
        }
    }

    private static string Quote(string path) => "\"" + path + "\"";

    /// <summary>
    /// The installed tools, in a stable order with the best-known first.
    /// </summary>
    public IList<DiffToolChoice> Discover() => Discover(InstallRoots());

    /// <summary>
    /// The same search against a given set of install roots.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="Discover"/> so the search can be pointed at a directory tree built
    /// for the purpose. The locations themselves are the part most likely to be wrong — they are
    /// what each installer happens to choose, they change between versions, and a machine missing
    /// one simply omits the tool rather than failing. Asserting against the real machine would
    /// therefore either fail on most machines or pass on none.
    /// </remarks>
    internal IList<DiffToolChoice> Discover(IEnumerable<string> installRoots)
    {
        var found = new List<DiffToolChoice>();

        foreach (KnownTool tool in Known)
        {
            string? path = Locate(tool, installRoots);

            if (path != null)
            {
                found.Add(new DiffToolChoice(
                    tool.Id,
                    tool.DisplayName,
                    CommandFor(tool.Id, path),
                    path));
            }
        }

        return found;
    }

    /// <summary>
    /// Finds one tool's executable, or <c>null</c> when it is not installed here.
    /// </summary>
    private static string? Locate(KnownTool tool, IEnumerable<string> installRoots)
    {
        foreach (string root in installRoots)
        {
            foreach (string relative in tool.RelativePaths)
            {
                string candidate = Path.Combine(root, relative);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // Last resort: the executable somewhere on PATH, which covers a portable install.
        return OnPath(tool.Executable);
    }

    private static IEnumerable<string> InstallRoots()
    {
        var roots = new List<string>();

        foreach (Environment.SpecialFolder folder in new[]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData,
        })
        {
            string? path = SafeFolder(folder);

            if (!string.IsNullOrEmpty(path))
            {
                roots.Add(path!);
            }
        }

        return roots;
    }

    private static string? SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // A folder that cannot be read is simply not a place to look.
            return null;
        }
    }

    /// <summary>
    /// Searches <c>PATH</c> for the executable, without starting a shell to do it.
    /// </summary>
    private static string? OnPath(string executable)
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (string folder in pathVariable!.Split(Path.PathSeparator))
        {
            string trimmed = folder.Trim().Trim('"');

            if (trimmed.Length == 0)
            {
                continue;
            }

            string candidate;

            try
            {
                candidate = Path.Combine(trimmed, executable);
            }
            catch (ArgumentException)
            {
                // An entry that cannot be combined with a file name is not a usable folder.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The list entry for a tool the user picked by hand, so a choice made here still round-trips
    /// through the same list as a discovered one.
    /// </summary>
    public static DiffToolChoice ForCustomTool(string executable)
    {
        string name = Path.GetFileNameWithoutExtension(executable);

        return new DiffToolChoice(
            "custom:" + executable,
            name,
            Quote(executable) + " \"$LOCAL\" \"$REMOTE\"",
            executable);
    }
}