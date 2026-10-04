using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitDiffFolderCreator.Models;

namespace GitDiffFolderCreator.Services;

/// <summary>What happened when a changed file was handed to a diff tool.</summary>
public sealed class DiffToolLaunchResult
{
    public DiffToolLaunchResult(bool launched, string message, bool isError, string? basePath, string? modifiedPath)
    {
        Launched = launched;
        Message = message;
        IsError = isError;
        BasePath = basePath;
        ModifiedPath = modifiedPath;
    }

    /// <summary>True when a tool was actually started.</summary>
    public bool Launched { get; }

    /// <summary>Wording for the status line.</summary>
    public string Message { get; }

    public bool IsError { get; }

    /// <summary>The extracted base file, for the caller to keep or clean up.</summary>
    public string? BasePath { get; }

    /// <summary>The extracted modified file.</summary>
    public string? ModifiedPath { get; }
}

/// <summary>
/// Shows the difference between the two versions of a changed file by handing both to whatever diff
/// tool the user has already configured for git.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a hand-off rather than a built-in viewer. The application exists to prepare
/// two folders for a comparison tool, so the user already has one; reimplementing a diff view would
/// be a second, worse copy of it.
/// </para>
/// <para>
/// Both versions are written to a temporary folder with <c>git show</c> rather than passed as
/// revisions, because a diff tool is handed <em>files</em>. That also gives the added and deleted
/// cases something real to open: for a file that exists at only one of the two commits, the missing
/// side becomes an empty file, since most tools treat a missing path as an error rather than as
/// "everything was added" or "everything was removed".
/// </para>
/// <para>
/// The tool is started without waiting and without redirecting, because the tool owns the user from
/// then on - it stays open until they close it, and this window has to stay usable meanwhile. Its
/// exit code is therefore never observed.
/// </para>
/// </remarks>
public sealed class DiffToolLauncher
{
    /// <summary>Prefix for the temporary folder holding the two extracted versions.</summary>
    private const string WorkingFolderPrefix = "gdfc-compare-";

    /// <summary>
    /// How old an extracted pair has to be before a later launch treats its folder as abandoned.
    /// </summary>
    /// <remarks>
    /// A day, which is far longer than any comparison lasts and far shorter than the point at which a
    /// user would notice the disk. It has to be longer than the life of the tool: a folder younger
    /// than this may still be open in a diff window somebody is reading, and deleting it out from
    /// under them would be worse than leaving it.
    /// </remarks>
    internal static readonly TimeSpan AbandonedFolderAge = TimeSpan.FromDays(1);

    /// <summary>
    /// Set once the sweep has run, so a session that compares fifty files does not walk the
    /// temporary folder fifty times. Interlocked rather than a plain flag because two double-clicks
    /// can be in flight at once.
    /// </summary>
    private static int _sweptThisSession;

    /// <summary>
    /// Both versions of <paramref name="change"/> are extracted and the configured diff tool is
    /// started on them.
    /// </summary>
    public async Task<DiffToolLaunchResult> LaunchAsync(
            GitService git,
            GitFileChange change,
            string baseHash,
            string modifiedHash,
            CancellationToken cancellationToken)
    {
        return await LaunchAsync(git, change, baseHash, modifiedHash, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// As above, but with a tool command line chosen in this application rather than read from git.
    /// </summary>
    /// <param name="configuredCommand">
    /// The command line from the picker, or <c>null</c> to fall back to the repository's git
    /// configuration. An explicit choice wins, because the user made it here; git is only consulted
    /// when nothing has been chosen, so existing git configuration still works with no setup.
    /// </param>
    public async Task<DiffToolLaunchResult> LaunchAsync(
        GitService git,
        GitFileChange change,
        string baseHash,
        string modifiedHash,
        string? configuredCommand,
        CancellationToken cancellationToken)
    {
        string workingFolder = Path.Combine(
            Path.GetTempPath(),
            WorkingFolderPrefix + Guid.NewGuid().ToString("N"));

        try
        {
            // The two sides, in one temporary folder. The names are fixed rather than taken from the
            // repository path: a git path is a legal pathname but not necessarily a legal file name,
            // and a changed file may legitimately be called "a:b.cs" in a tree git imported from
            // elsewhere. Windows would reject the whole hand-off over one such name.
            string basePath = Path.Combine(workingFolder, "base-" + FileNameTag(change.PathInBaseCommit));
            string modifiedPath = Path.Combine(workingFolder, "modified-" + FileNameTag(change.PathInModifiedCommit));

            Directory.CreateDirectory(workingFolder);

            await ExtractAsync(git, baseHash, change.PathInBaseCommit, basePath, cancellationToken).ConfigureAwait(false);
            await ExtractAsync(git, modifiedHash, change.PathInModifiedCommit, modifiedPath, cancellationToken).ConfigureAwait(false);

            DiffToolLaunchResult result = await StartConfiguredToolAsync(git, basePath, modifiedPath, configuredCommand, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Launched)
            {
                // Nothing was handed over, so the extracted copies are dead weight.
                TryDelete(workingFolder);
            }
            else
            {
                // The pair this launch just made stays: the tool may still be reading it, and it may
                // have been started by a launcher process that has already exited. What can be cleared
                // is what earlier launches left behind, which is why the sweep runs here rather than
                // being left to the operating system - it does not remove arbitrary temporary files.
                TrySweepAbandonedFolders();
            }

            return result;
        }
        catch (GitCommandException ex)
        {
            TryDelete(workingFolder);

            return new DiffToolLaunchResult(false, ex.Message, true, null, null);
        }
        catch (Exception ex) when (
            ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
        {
            // Writing the two versions can fail on its own account: a full disk, a temporary folder
            // that cannot be created, or a leftover from an earlier run holding the same name. That
            // is a failed hand-off rather than a fault somewhere above, so it is reported like any
            // other and the folder is not left behind.
            TryDelete(workingFolder);

            return new DiffToolLaunchResult(false, ex.Message, true, null, null);
        }
    }

    /// <summary>
    /// Characters Windows forbids in a file name, replaced so a repository path can seed a temporary
    /// one. Anything not replaced would make the folder unrecreatable and the hand-off fail.
    /// </summary>
    private static string FileNameTag(string repositoryPath)
    {
        char[] invalid = Path.GetInvalidFileNameChars();

        // A git path is separated by '/', which is legal in a directory name but not in a file name,
        // and GetInvalidFileNameChars does not include it. Every separator therefore becomes '_', so
        // the whole path collapses into one name and no subfolder is implied.
        var safe = new StringBuilder(repositoryPath.Length);

        foreach (char c in repositoryPath)
        {
            safe.Append(c == '/' || c == '\\' || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string tag = safe.ToString().TrimEnd('.', ' ');

        // A file name is limited to 255 characters, and the prefix is added on top of this.
        return tag.Length > 120 ? tag.Substring(tag.Length - 120) : tag;
    }

    /// <summary>
    /// Writes one version of the file to disk, leaving an empty file when the path does not exist at
    /// that commit.
    /// </summary>
    /// <remarks>
    /// The added and deleted cases are ordinary, not failures: the file added in the newer commit does
    /// not exist in the older one, and the reverse holds for a deletion. An empty file is what makes
    /// either one legible in a side-by-side tool, and it is why the run is not reported as an error.
    /// </remarks>
    private static async Task ExtractAsync(
        GitService git,
        string hash,
        string path,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        List<string> arguments = GitServiceConstants.CommonArguments;
        arguments.Add("show");
        arguments.Add("--no-textconv");

        // ":path" rather than a separate -- separator: git show takes the revision and the path as
        // one argument, and this keeps a path beginning with a dash from being read as an option.
        arguments.Add(hash + ":" + path);

        GitProcessResult result = await GitProcessRunner.RunToFileAsync(
            git.RepositoryPath,
            arguments,
            destinationPath,
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return;
        }

        // Not an error: the path simply is not in this commit.
        File.WriteAllBytes(destinationPath, new byte[0]);
    }

    /// <summary>
    /// Starts the diff tool the user has configured, on the two given files, without waiting for it.
    /// </summary>
    /// <remarks>
    /// The tool is run directly rather than through <c>git difftool</c>. That looked like the obvious
    /// route and is wrong: <c>difftool</c> reads its path arguments as pathspecs <em>inside the
    /// repository</em>, so two files in a temporary folder produce no diff, no tool launch, and a
    /// zero exit code that looks like success. Handing the two files straight to the configured command
    /// is also what git itself does once it has resolved a tool - the arguments a tool expects are the
    /// two file paths.
    /// </remarks>
    private static async Task<DiffToolLaunchResult> StartConfiguredToolAsync(
        GitService git,
        string basePath,
        string modifiedPath,
        string? configuredCommand,
        CancellationToken cancellationToken)
    {
        string tool;

        string? fromGit = null;

        if (!string.IsNullOrWhiteSpace(configuredCommand))
        {
            // Chosen in this application's picker. Taken as given: it may name a tool git knows
            // nothing about.
            tool = configuredCommand!;
        }
        else
        {
            fromGit = await ReadConfigAsync(git, "diff.tool", cancellationToken).ConfigureAwait(false);

            if (fromGit == null)
            {
                return new DiffToolLaunchResult(
                    false,
                    "No diff tool is set. Choose one from the changed files list, or with "
                        + "'git config --global diff.tool <tool>'.",
                    true,
                    null,
                    null);
            }

            tool = fromGit;
        }

        // git accepts two shapes for diff.tool, and both are common enough to have to work.
        //
        // A bare name - "bcomp" - refers to difftool.bcomp.cmd, which is a full command line with
        // $LOCAL and $REMOTE placeholders. A bare name is not a program, so executing it directly
        // fails with a confusing "not recognised as an internal command".
        //
        // Anything else is the command line to run, which for Beyond Compare and similar is an
        // absolute path *with spaces in it*, plus often its own arguments. Splitting that on
        // whitespace would produce "C:\Program" as the program name, so the quoting in the value has
        // to be honoured instead.
        string program;
        string arguments;

        // Only a value that came from git can be a bare name referring to another key. A command line
        // from the picker is already complete, so looking up difftool.<whole command line>.cmd would
        // ask git about a key that cannot exist.
        string? named = fromGit == null
            ? null
            : await ReadConfigAsync(git, "difftool." + fromGit + ".cmd", cancellationToken)
                .ConfigureAwait(false);

        if (named != null)
        {
            // The placeholders are what this tool's command line is missing, so they are filled in
            // here rather than the two paths being appended after it.
            program = FirstToken(named);
            arguments = Substitute(
                RemainingTokens(named),
                basePath,
                modifiedPath);
        }
        else if (tool.IndexOf(' ') < 0 && tool.IndexOf('"') < 0 && File.Exists(tool))
        {
            program = tool;
            arguments = string.Join(" ", new[] { Quote(basePath), Quote(modifiedPath) });
        }
        else
        {
            program = FirstToken(tool);
            arguments = Substitute(RemainingTokens(tool), basePath, modifiedPath);
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(program)
            {
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = arguments,
            };

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;

                if (!process.Start())
                {
                    return new DiffToolLaunchResult(
                        false,
                        "Could not start '" + tool + "'.",
                        true,
                        null,
                        null);
                }
            }

            return new DiffToolLaunchResult(
                true,
                "Opened both versions of the file in " + tool + ".",
                false,
                basePath,
                modifiedPath);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new DiffToolLaunchResult(
                false,
                "Could not start '" + tool + "'. Check that the configured diff tool exists.",
                true,
                null,
                null);
        }
    }

    /// <summary>
    /// Reads one git configuration value, or <c>null</c> when it is not set.
    /// </summary>
    /// <remarks>
    /// A missing key is not an error: git exits non-zero and prints nothing, which is the ordinary
    /// answer for a tool that has not been configured.
    /// </remarks>
    private static async Task<string?> ReadConfigAsync(
        GitService git,
        string key,
        CancellationToken cancellationToken)
    {
        List<string> arguments = GitServiceConstants.CommonArguments;
        arguments.Add("config");
        arguments.Add("--get");
        arguments.Add(key);

        GitProcessResult result = await GitProcessRunner.RunAsync(
            git.RepositoryPath,
            arguments,
            cancellationToken).ConfigureAwait(false);

        string value = result.StandardOutput.Trim();

        return result.Succeeded && value.Length > 0 ? value : null;
    }

    /// <summary>
    /// Quotes one path for a command line.
    /// </summary>
    /// <remarks>
    /// The same rules <see cref="WindowsArgumentString"/> applies, reused rather than restated: this
    /// is the one place a path is appended to an existing command line rather than built from a
    /// vector.
    /// </remarks>
    private static string Quote(string path) => WindowsArgumentString.Build(new[] { path });

    /// <summary>
    /// Replaces git's <c>$LOCAL</c> and <c>$REMOTE</c> with the two files.
    /// </summary>
    /// <remarks>
    /// A named tool's command line is written with these placeholders rather than with two paths, so
    /// they have to be filled in for the command to mean anything. A tool configured without them
    /// still works: the two paths are appended, which is the convention for a bare executable.
    /// <para>
    /// The substituted paths are quoted here even though the stored value already has quotes around
    /// the placeholders. git does not keep them - it stores <c>difftool.x.cmd</c> as
    /// <c>"tool" $LOCAL $REMOTE</c> with the placeholder's quotes removed - so a name with a space in
    /// it would otherwise arrive as two arguments.
    /// </para>
    /// </remarks>
    private static string Substitute(string arguments, string basePath, string modifiedPath)
    {
        if (arguments.IndexOf("$LOCAL", StringComparison.Ordinal) < 0
            && arguments.IndexOf("$REMOTE", StringComparison.Ordinal) < 0)
        {
            return string.IsNullOrWhiteSpace(arguments)
                ? Quote(basePath) + " " + Quote(modifiedPath)
                : arguments + " " + Quote(basePath) + " " + Quote(modifiedPath);
        }

        return arguments
            .Replace("$LOCAL", Quote(basePath))
            .Replace("$REMOTE", Quote(modifiedPath));
    }

    /// <summary>
    /// Splits a configured command line into its program and the rest, honouring quotes.
    /// </summary>
    /// <remarks>
    /// This is why <c>diff.tool</c> cannot simply be split on whitespace: Beyond Compare installs to
    /// "C:\Program Files\Beyond Compare 4\BCompare.exe", and a naive split yields "C:\Program" as the
    /// program - which fails for the single most common configuration there is. The same
    /// <c>CommandLineToArgvW</c> rules <see cref="WindowsArgumentString"/> implements are applied in
    /// reverse, so a quoted path stays one argument however many spaces it contains.
    /// <para>
    /// The parts come back through out parameters rather than a tuple: <c>ValueTuple</c> does not
    /// exist on the 4.6.1 reference assemblies this application targets.
    /// </para>
    /// </remarks>
    private static string SplitCommandLine(string commandLine, out string arguments)
    {
        string trimmed = commandLine.Trim();
        int index = 0;
        var current = new StringBuilder();
        var program = new StringBuilder();
        bool inQuotes = false;
        bool programDone = false;

        while (index < trimmed.Length)
        {
            char c = trimmed[index];

            if (c == '"')
            {
                // A doubled quote inside a quoted run is a literal quote, per the argv rules.
                if (inQuotes && index + 1 < trimmed.Length && trimmed[index + 1] == '"')
                {
                    Append(programDone ? current : program, '"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                index++;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (!programDone && program.Length > 0)
                {
                    programDone = true;
                }
                else if (programDone)
                {
                    if (current.Length > 0)
                    {
                        current.Append(' ');
                    }
                }

                index++;
                continue;
            }

            Append(programDone ? current : program, c);
            index++;
        }

        arguments = current.ToString();
        return program.ToString();
    }

    private static void Append(StringBuilder builder, char c) => builder.Append(c);

    private static string FirstToken(string commandLine)
    {
        return SplitCommandLine(commandLine, out _);
    }

    private static string RemainingTokens(string commandLine)
    {
        SplitCommandLine(commandLine, out string arguments);
        return arguments;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
        catch (IOException)
        {
            // The files are in the temporary folder and the OS will reclaim them; failing to tidy up
            // must not turn a successful hand-off into an error.
        }
        catch (UnauthorizedAccessException)
        {
            // As above: a leftover temporary folder is not worth reporting.
        }
    }

    /// <summary>
    /// Deletes the extracted pairs earlier launches left behind, once per session.
    /// </summary>
    /// <remarks>
    /// Every failure here is swallowed, including the enumeration itself. A hand-off that succeeded is
    /// not made into an error by housekeeping, and nothing here is worth telling the user about: the
    /// folders are in the temporary directory and they are gone or they are not.
    /// </remarks>
    private static void TrySweepAbandonedFolders()
    {
        if (Interlocked.Exchange(ref _sweptThisSession, 1) != 0)
        {
            return;
        }

        try
        {
            DeleteAbandonedFolders(Path.GetTempPath(), AbandonedFolderAge, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes every extracted-pair folder under <paramref name="tempRoot"/> that is older than
    /// <paramref name="maxAge"/>, and reports how many were removed.
    /// </summary>
    /// <remarks>
    /// Only folders carrying this class's own prefix are touched, and only ones old enough that no
    /// comparison can still be using them. A folder that cannot be deleted - a tool still holding a
    /// file open, or a permission problem - is skipped rather than treated as a failure: it will be
    /// offered again next time, and the alternative is refusing to clean up anything at all.
    /// <para>
    /// The clock and the age are arguments rather than constants so the policy can be tested against a
    /// folder of a known age instead of one that has to be waited for. The comparison is against
    /// UTC, which is what the filesystem reports; a local clock passed in is converted rather than
    /// silently compared as though it were UTC, which would be wrong by the offset.
    /// </para>
    /// </remarks>
    internal static int DeleteAbandonedFolders(string tempRoot, TimeSpan maxAge, DateTime now)
    {
        if (!Directory.Exists(tempRoot))
        {
            return 0;
        }

        DateTime cutoff = (now.Kind == DateTimeKind.Utc ? now : now.ToUniversalTime()) - maxAge;
        int removed = 0;

        foreach (string folder in Directory.EnumerateDirectories(tempRoot, WorkingFolderPrefix + "*"))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(folder) > cutoff)
                {
                    continue;
                }

                Directory.Delete(folder, true);
                removed++;
            }
            catch (IOException)
            {
                // Still in use, or held by something else. Left for a later sweep.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }
        }

        return removed;
    }
}