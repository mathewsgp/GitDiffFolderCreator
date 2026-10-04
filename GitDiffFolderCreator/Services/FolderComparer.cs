using System;
using System.Collections.Generic;
using System.IO;

namespace GitDiffFolderCreator.Services
{
    /// <summary>How one path differs between the two folders.</summary>
    public enum FolderDifferenceKind
    {
        /// <summary>In both folders, with different content.</summary>
        Modified,

        /// <summary>Only in the modified folder.</summary>
        Added,

        /// <summary>Only in the base folder.</summary>
        Removed,
    }

    /// <summary>One path that differs between two folders.</summary>
    public sealed class FolderDifference
    {
        public FolderDifference(FolderDifferenceKind kind, string? basePath, string? modifiedPath)
        {
            Kind = kind;
            BasePath = basePath;
            ModifiedPath = modifiedPath;
        }

        public FolderDifferenceKind Kind { get; }

        /// <summary>
        /// The path in the base folder, relative to the base folder, or null when the file is only in
        /// the modified one.
        /// </summary>
        /// <remarks>
        /// Relative, because this is what gets compared against a document and what a reader needs to
        /// see. The two source trees sit at different absolute paths by definition, so an absolute path
        /// here would match nothing and be unreadable in a finding besides.
        /// </remarks>
        public string? BasePath { get; }

        /// <summary>
        /// The path in the modified folder, relative to the modified folder, or null when the file is
        /// only in the base one.
        /// </summary>
        public string? ModifiedPath { get; }

        /// <summary>
        /// The path to call the difference by. A file that moved has two, and the one on the modified
        /// side is the one a change document would list.
        /// </summary>
        public string PrimaryPath => ModifiedPath ?? BasePath ?? string.Empty;
    }

    /// <summary>What comparing two folders found.</summary>
    public sealed class FolderComparison
    {
        public FolderComparison(
            IList<FolderDifference> differences,
            string baseRoot,
            string modifiedRoot,
            ISet<string> modifiedPaths,
            int filesCompared,
            int filesIgnored,
            bool ignoredByRule,
            IEnumerable<string>? ignoredFolders = null)
        {
            Differences = differences;
            BaseRoot = baseRoot;
            ModifiedRoot = modifiedRoot;
            ModifiedPaths = modifiedPaths;
            FilesCompared = filesCompared;
            FilesIgnored = filesIgnored;
            IgnoredByRule = ignoredByRule;
            IgnoredFolders = new HashSet<string>(
                ignoredFolders ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        public IList<FolderDifference> Differences { get; }

        /// <summary>
        /// The folder names left out of the comparison.
        /// </summary>
        /// <remarks>
        /// Carried so that a documented path sitting under one of them can be answered as "not
        /// compared" rather than as "no such file". Without it, clearing the ignore rule is the only
        /// way to find out that the tool never looked.
        /// </remarks>
        public ISet<string> IgnoredFolders { get; }

        /// <summary>
        /// Every file in the modified folder, whether or not it differs.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Differences"/> because "the file is missing" and "the file is there
        /// and unchanged" are different answers, and only the first of them is a difference. Asking
        /// <see cref="Differences"/> would answer "missing" for every unchanged file.
        /// </remarks>
        public ISet<string> ModifiedPaths { get; }

        /// <summary>
        /// The folder the base-side paths are relative to, kept so a reported path can be opened.
        /// </summary>
        public string BaseRoot { get; }

        /// <summary>The folder the modified-side paths are relative to.</summary>
        public string ModifiedRoot { get; }

        /// <summary>Files present in both folders, whether or not they differ.</summary>
        public int FilesCompared { get; }

        /// <summary>Files left out because they sit under an ignored folder.</summary>
        public int FilesIgnored { get; }

        /// <summary>Whether any folder was ignored at all, so the count above is worth showing.</summary>
        public bool IgnoredByRule { get; }

        /// <summary>
        /// Differences that pair up by content: the same bytes at a different path, which is a move
        /// rather than a delete and an add.
        /// </summary>
        /// <remarks>
        /// Grouped on length and content hash rather than name, because a move has no name in common by
        /// definition. A file that was both moved and edited has no match, and is correctly reported as
        /// an addition and a removal rather than guessed at.
        /// </remarks>
        public IList<FolderMove> Moves()
        {
            var removals = new List<FolderDifference>();
            var additions = new List<FolderDifference>();

            foreach (FolderDifference difference in Differences)
            {
                if (difference.Kind == FolderDifferenceKind.Removed)
                {
                    removals.Add(difference);
                }
                else if (difference.Kind == FolderDifferenceKind.Added)
                {
                    additions.Add(difference);
                }
            }

            var moves = new List<FolderMove>();

            // Only a move between two files at the same length can be identified without reading both
            // in full, so the pairing is by length and then confirmed by content.
            foreach (FolderDifference removed in removals)
            {
                string from = Path.Combine(BaseRoot, removed.BasePath!);

                foreach (FolderDifference added in additions)
                {
                    if (moves.Any(move => ReferenceEquals(move.Added, added)))
                    {
                        continue;
                    }

                    string to = Path.Combine(ModifiedRoot, added.ModifiedPath!);

                    if (!FolderComparer.SameLength(from, to, out long length))
                    {
                        continue;
                    }

                    if (!FolderComparer.SameContent(from, to, length))
                    {
                        continue;
                    }

                    moves.Add(new FolderMove(removed, added));
                }
            }

            return moves;
        }
    }

    /// <summary>A file that is at one path in the base folder and another in the modified one.</summary>
    public sealed class FolderMove
    {
        public FolderMove(FolderDifference removed, FolderDifference added)
        {
            Removed = removed;
            Added = added;
        }

        public FolderDifference Removed { get; }

        public FolderDifference Added { get; }

        /// <summary>Where it was.</summary>
        public string FromPath => Removed.BasePath!;

        /// <summary>Where it is now, which is the path a change document would list.</summary>
        public string ToPath => Added.ModifiedPath!;
    }

    /// <summary>
    /// Compares two folders as they are on disk, with no repository involved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the export's cross-check applied to two arbitrary folders instead of two commits. The
    /// question is the same one either way: what is actually different between these two trees?
    /// </para>
    /// <para>
    /// Paths are matched without regard to case, because the destination filesystem does not regard
    /// it: a tree holding both <c>Notes.txt</c> and <c>notes.txt</c> is one file on Windows, and
    /// treating them as two would report a difference that cannot exist.
    /// </para>
    /// <para>
    /// Content is compared byte for byte, and a length check comes first so a file that cannot match
    /// is not read. Build output is excluded by default because these are source folders and a
    /// compiled assembly differs on every build, which would bury the changes the tool is looking for.
    /// </para>
    /// </remarks>
    public sealed class FolderComparer
    {
        /// <summary>
        /// Folder names skipped by default. Build and version-control metadata, which change on their
        /// own rather than because a file was edited.
        /// </summary>
        internal static readonly string[] DefaultIgnoredFolders =
        {
            "bin",
            "obj",
            ".vs",
            ".git",
            ".svn",
            ".hg",
            "node_modules",
            "packages",
            "TestResults",
        };

        private readonly HashSet<string> _ignoredFolders;

        public FolderComparer(IEnumerable<string>? ignoredFolders = null)
        {
            _ignoredFolders = new HashSet<string>(
                ignoredFolders ?? DefaultIgnoredFolders,
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The folder names this comparer skips, so a claim about a file under one of them can be
        /// answered rather than reported as a file that does not exist.
        /// </summary>
        internal IReadOnlyCollection<string> IgnoredFolders => _ignoredFolders;

        /// <summary>
        /// Compares the two folders.
        /// </summary>
        /// <exception cref="DirectoryNotFoundException">Either folder does not exist.</exception>
        public FolderComparison Compare(string baseFolder, string modifiedFolder)
        {
            if (!Directory.Exists(baseFolder))
            {
                throw new DirectoryNotFoundException("The base source folder does not exist: " + baseFolder);
            }

            if (!Directory.Exists(modifiedFolder))
            {
                throw new DirectoryNotFoundException("The modified source folder does not exist: " + modifiedFolder);
            }

            Dictionary<string, string> baseFiles = Enumerate(baseFolder, out int baseIgnored);
            Dictionary<string, string> modifiedFiles = Enumerate(modifiedFolder, out int modifiedIgnored);

            var differences = new List<FolderDifference>();

            foreach (KeyValuePair<string, string> entry in modifiedFiles)
            {
                if (!baseFiles.TryGetValue(entry.Key, out string? baseFile))
                {
                    // The relative key, not the full path: this is what a document lists and what a
                    // finding has to be able to show.
                    differences.Add(new FolderDifference(FolderDifferenceKind.Added, null, entry.Key));
                    continue;
                }

                if (Differs(baseFile, entry.Value))
                {
                    differences.Add(new FolderDifference(
                        FolderDifferenceKind.Modified, entry.Key, entry.Key));
                }
            }

            foreach (KeyValuePair<string, string> entry in baseFiles)
            {
                if (!modifiedFiles.ContainsKey(entry.Key))
                {
                    differences.Add(new FolderDifference(FolderDifferenceKind.Removed, entry.Key, null));
                }
            }

            differences.Sort((left, right) => string.CompareOrdinal(left.PrimaryPath, right.PrimaryPath));

            // Files present on both sides, whether or not they differ. This is the denominator the
            // "how much was actually compared" figure needs, so it is counted rather than derived
            // from the differences: an addition and a removal offset each other in arithmetic and not
            // in meaning.
            int shared = modifiedFiles.Keys.Count(key => baseFiles.ContainsKey(key));

            return new FolderComparison(
                differences,
                baseFolder,
                modifiedFolder,
                new HashSet<string>(modifiedFiles.Keys, StringComparer.OrdinalIgnoreCase),
                shared,
                baseIgnored + modifiedIgnored,
                baseIgnored + modifiedIgnored > 0,
                _ignoredFolders);
        }

        /// <summary>Whether two files that both exist hold different content.</summary>
        private static bool Differs(string left, string right)
        {
            if (!SameLength(left, right, out long length))
            {
                // One of them is unreadable, which is a difference worth reporting rather than
                // silently calling them the same.
                return true;
            }

            return !SameContent(left, right, length);
        }

        /// <summary>
        /// Whether both paths name a readable file of the same length, reporting that length so the
        /// content comparison can be skipped for a file that cannot possibly match.
        /// </summary>
        internal static bool SameLength(string left, string right, out long length)
        {
            length = -1;

            try
            {
                var leftInfo = new FileInfo(left);
                var rightInfo = new FileInfo(right);

                if (!leftInfo.Exists || !rightInfo.Exists)
                {
                    return false;
                }

                length = leftInfo.Length;

                return leftInfo.Length == rightInfo.Length;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Whether two files hold exactly the same bytes.</summary>
        internal static bool SameContent(string left, string right, long length)
        {
            const int BufferSize = 64 * 1024;

            try
            {
                var leftBuffer = new byte[BufferSize];
                var rightBuffer = new byte[BufferSize];

                using FileStream leftStream = new(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize);
                using FileStream rightStream = new(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize);

                long remaining = length;

                while (remaining > 0)
                {
                    int want = (int)Math.Min(BufferSize, remaining);

                    int readLeft = ReadFully(leftStream, leftBuffer, want);
                    int readRight = ReadFully(rightStream, rightBuffer, want);

                    if (readLeft != readRight)
                    {
                        return false;
                    }

                    for (int i = 0; i < readLeft; i++)
                    {
                        if (leftBuffer[i] != rightBuffer[i])
                        {
                            return false;
                        }
                    }

                    remaining -= readLeft;
                }

                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Fills the buffer as far as the stream allows, for files that change under a read.</summary>
        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            int total = 0;

            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        /// <summary>
        /// Every file under a folder, keyed for case-insensitive lookup, with the path spelled the way
        /// this tool spells paths.
        /// </summary>
        private Dictionary<string, string> Enumerate(string root, out int ignored)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ignored = 0;

            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string folder = pending.Pop();

                string[] children;

                try
                {
                    children = Directory.GetDirectories(folder);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // A folder that cannot be listed is not a folder the tool can say anything about.
                    continue;
                }

                foreach (string child in children)
                {
                    if (_ignoredFolders.Contains(Path.GetFileName(child)))
                    {
                        ignored += CountFiles(child);
                        continue;
                    }

                    pending.Push(child);
                }

                string[] here;

                try
                {
                    here = Directory.GetFiles(folder);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string file in here)
                {
                    string key = RelativeTo(root, file);

                    // First one wins on a case-only clash, which is what the filesystem would keep.
                    if (!files.ContainsKey(key))
                    {
                        files[key] = file;
                    }
                }
            }

            return files;
        }

        /// <summary>Counts the files under a folder that is being skipped, for the "left out" count.</summary>
        private int CountFiles(string root)
        {
            int count = 0;

            try
            {
                count = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // An unreadable subtree counts as nothing rather than failing the comparison.
            }

            return count;
        }

        /// <summary>A path relative to the folder, in the slash form this tool compares in.</summary>
        private static string RelativeTo(string root, string file)
        {
            string relative = file.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length);

            if (relative.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || relative.StartsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                relative = relative.Substring(1);
            }

            return relative.Replace('\\', '/');
        }
    }
}