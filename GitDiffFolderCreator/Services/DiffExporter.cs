using System.Text;
using GitDiffFolderCreator.Models;

namespace GitDiffFolderCreator.Services
{
    /// <summary>Inputs for a single base/modified folder export.</summary>
    public sealed class ExportRequest
    {
        /// <summary>
        /// The git to read through, or <c>null</c> for one built from <see cref="OutputRoot"/>'s
        /// repository. Supplied by the window, which already has a service for the open repository.
        /// </summary>
        public GitService? Git { get; set; }

        /// <summary>Ancestor commit; its version of every changed file goes to the base folder.</summary>
        public string BaseHash { get; set; }

        /// <summary>Descendant commit; its version of every changed file goes to the modified folder.</summary>
        public string ModifiedHash { get; set; }

        /// <summary>Folder that will contain the two generated folders.</summary>
        public string OutputRoot { get; set; }

        /// <summary>Write the manifests into the modified folder.</summary>
        public bool WriteManifests { get; set; }

        /// <summary>
        /// Paths the user has unticked in the file list. Matching is by the newer path, which is what
        /// the list shows; a rename is therefore excluded by its destination.
        /// </summary>
        public IList<string> ExcludedPaths { get; set; }

        public ExportRequest()
        {
            Git = null;
            BaseHash = string.Empty;
            ModifiedHash = string.Empty;
            OutputRoot = string.Empty;
            WriteManifests = true;
            ExcludedPaths = new List<string>();
        }
    }

    /// <summary>
    /// One report from a running export: what it is doing, and how much of the work is done.
    /// </summary>
    /// <remarks>
    /// A count rather than a time estimate, because the number of files is known exactly and their
    /// sizes are not. Weighting by bytes would need a second git call to obtain, and would make the
    /// bar jump backwards whenever one large file turned up after several small ones.
    /// </remarks>
    public sealed class ExportProgress
    {
        public ExportProgress(string message, int completed, int total)
        {
            Message = message;
            Completed = completed < 0 ? 0 : completed;
            Total = total < 0 ? 0 : total;
        }

        /// <summary>What the export is doing, for the status line and the log.</summary>
        public string Message { get; }

        /// <summary>Items finished. Counts every path handled, written or not, so it always completes.</summary>
        public int Completed { get; }

        /// <summary>Items in all, or zero when the total is not yet known.</summary>
        public int Total { get; }

        /// <summary>
        /// False before the file list is known. The bar has nothing honest to show then, so it
        /// animates rather than claiming a percentage.
        /// </summary>
        public bool IsDeterminate => Total > 0;

        /// <summary>How far along, from 0 to 1, clamped so no report can overrun the track.</summary>
        public double Fraction =>
            Total > 0 ? Math.Min(1d, (double)Completed / Total) : 0d;
    }

    /// <summary>Outcome of an export attempt.</summary>
    public sealed class ExportResult
    {
        /// <summary>Whether both folders were written. Read before the paths, which are empty when false.</summary>
        public bool Success { get; set; }

        /// <summary>Folder holding the result, the two file sets and the manifests.</summary>
        public string RootFolderPath { get; set; }

        public string BaseFolderPath { get; set; }

        public string ModifiedFolderPath { get; set; }

        public IList<string> Messages { get; set; }

        public IList<string> Warnings { get; set; }

        /// <summary>Set when <see cref="Success"/> is false.</summary>
        public string? FailureReason { get; set; }

        public ExportResult()
        {
            RootFolderPath = string.Empty;
            BaseFolderPath = string.Empty;
            ModifiedFolderPath = string.Empty;
            Messages = new List<string>();
            Warnings = new List<string>();
            FailureReason = null;
        }
    }

    /// <summary>
    /// Builds two folders holding the relevant files only: the state of every changed file at the
    /// base commit, and the same set at the modified commit.
    /// </summary>
    public sealed class DiffExporter
    {
        private const string DeletedFilesManifest = "DeletedFiles.txt";
        private const string RenamedFilesManifest = "RenamedFiles.txt";
        private const string ChangedFilesManifest = "ChangedFiles.txt";

        /// <summary>
        /// What each folder ended up holding, as opposed to what the comparison was.
        /// </summary>
        /// <remarks>
        /// The other three describe the change list. This one describes the disk, and the difference
        /// matters: a diff tool pointed at the two folders has no way to tell a file that was
        /// legitimately not written from one that was lost.
        /// </remarks>
        private const string FolderContentsManifest = "ExportedFiles.txt";

        private const string StagingPrefix = ".gdfc-staging-";

        /// <summary>Prefix of the folder that holds one comparison and everything it produced.</summary>
        private const string RunFolderPrefix = "diff_";

        /// <summary>Folder holding the base-commit version of every changed file.</summary>
        public const string BaseFolderName = "base";

        /// <summary>Folder holding the modified-commit version of every changed file.</summary>
        public const string ModifiedFolderName = "modified";

        private static readonly UTF8Encoding ManifestEncoding = new UTF8Encoding(false);

        public async Task<ExportResult> ExportAsync(
            ExportRequest request,
            IProgress<ExportProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (request.Git == null)
            {
                throw new ArgumentException("A GitService is required.", "request");
            }

            if (string.IsNullOrWhiteSpace(request.BaseHash) || string.IsNullOrWhiteSpace(request.ModifiedHash))
            {
                throw new ArgumentException("A base and a modified commit are required.", "request");
            }

            cancellationToken.ThrowIfCancellationRequested();

            List<string> warnings = new List<string>();
            List<string> messages = new List<string>();

            GitService git = request.Git;
            string baseHash = request.BaseHash;
            string modifiedHash = request.ModifiedHash;

            string outputRoot;
            try
            {
                outputRoot = ValidateOutputRoot(request.OutputRoot);
            }
            catch (ArgumentException ex)
            {
                return new ExportResult { Success = false, FailureReason = ex.Message };
            }

            // Nothing is known about the size of the work yet, so this report is deliberately not
            // determinate: a bar that showed 0% for the length of the git diff would be a bar that
            // looks stuck.
            Report(progress, string.Format("Comparing {0}..{1}", Short(baseHash), Short(modifiedHash)), 0, 0);
            IList<GitFileChange> changes =
                await git.GetChangesAsync(baseHash, modifiedHash, cancellationToken).ConfigureAwait(false);

            Report(progress, string.Format("{0} changed path(s)", changes.Count), 0, 0);

            // Applied before the empty check, so unticking every file is reported as an empty result
            // rather than silently exporting everything.
            if (request.ExcludedPaths != null && request.ExcludedPaths.Count > 0)
            {
                HashSet<string> excluded = new HashSet<string>(request.ExcludedPaths, StringComparer.Ordinal);

                List<GitFileChange> kept = new List<GitFileChange>();
                foreach (GitFileChange change in changes)
                {
                    if (!excluded.Contains(change.Path))
                    {
                        kept.Add(change);
                    }
                }

                int dropped = changes.Count - kept.Count;
                if (dropped > 0)
                {
                    Report(
                        progress,
                        string.Format("{0} path(s) excluded by selection", dropped),
                        0,
                        0);
                    warnings.Add(string.Format("{0} of {1} path(s) were excluded by selection.", dropped, changes.Count));
                }

                changes = kept;
            }


            if (changes.Count == 0)
            {
                return new ExportResult
                {
                    Success = false,
                    FailureReason = "The selected commits have no file differences.",
                };
            }

            List<string> basePaths = new List<string>();
            List<string> modifiedPaths = new List<string>();

            foreach (GitFileChange change in changes)
            {
                basePaths.Add(change.PathInBaseCommit);
                modifiedPaths.Add(change.PathInModifiedCommit);
            }


            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            // Everything for one comparison lives in a single folder, so the two file sets and the
            // manifests that describe them stay together and can be moved or deleted as a unit.
            string runFolderName = string.Format(
                "{0}{1}_{2}_{3}", RunFolderPrefix, Short(baseHash), Short(modifiedHash), timestamp);

            string stagingRoot = Path.Combine(outputRoot, StagingPrefix + Guid.NewGuid().ToString("N"));
            string stagedBase = Path.Combine(stagingRoot, BaseFolderName);
            string stagedModified = Path.Combine(stagingRoot, ModifiedFolderName);
            string finalRoot = UniquePath(Path.Combine(outputRoot, runFolderName));
            bool published = false;

            // The work is now exactly known: one request per path, on each side. Counting files
            // rather than bytes is deliberate - a byte total would need a second git call to obtain,
            // and would make the bar jump backwards when a large file turned up after small ones.
            int total = basePaths.Count + modifiedPaths.Count;
            var completed = new Counter();

            try
            {
                Report(
                    progress,
                    string.Format("Exporting base files to {0}", runFolderName + "\\" + BaseFolderName),
                    completed.Value,
                    total);

                // The base side's result is used for one thing only: two paths that collide on this
                // filesystem are worth a warning whichever side they are on. Its MissingPaths are
                // paths that were added between the two commits, which is what most commits to a new
                // file look like, and warning about those filled the output with notices about the
                // expected result.
                FileExportResult baseExport = await git.ExportFilesAsync(
                    baseHash, basePaths, stagedBase, Done(progress, completed, total),
                    cancellationToken).ConfigureAwait(false);

                warnings.AddRange(DescribePathConflicts(baseExport, BaseFolderName));
                warnings.AddRange(DescribeOmittedPaths(baseExport, BaseFolderName));

                Report(
                    progress,
                    string.Format("Exporting modified files to {0}", runFolderName + "\\" + ModifiedFolderName),
                    completed.Value,
                    total);

                FileExportResult modifiedExport = await git.ExportFilesAsync(
                    modifiedHash, modifiedPaths, stagedModified, Done(progress, completed, total),
                    cancellationToken).ConfigureAwait(false);

                // The one worth reporting: these were in the base commit and are gone from the
                // modified one, so the modified folder is short of them and the two folders will not
                // match up the way the caller is expecting.
                warnings.AddRange(DescribeRemovedFiles(modifiedExport));
                warnings.AddRange(DescribePathConflicts(modifiedExport, ModifiedFolderName));
                warnings.AddRange(DescribeOmittedPaths(modifiedExport, ModifiedFolderName));

                if (request.WriteManifests)
                {
                    // Cross-checked before the manifests are written, so the folder manifest and the
                    // warnings are both describing the same view of what landed. After the move would
                    // be too late to react to a disagreement.
                    FolderVerification baseVerification = VerifyFolder(
                        BaseFolderName,
                        stagedBase,
                        basePaths,
                        PathsAbsentFromBase(changes),
                        baseExport);

                    FolderVerification modifiedVerification = VerifyFolder(
                        ModifiedFolderName,
                        stagedModified,
                        modifiedPaths,
                        PathsAbsentFromModified(changes),
                        modifiedExport);

                    warnings.AddRange(DescribeVerification(baseVerification));
                    warnings.AddRange(DescribeVerification(modifiedVerification));

                    WriteManifests(stagingRoot, changes);
                    WriteFolderContentsManifest(
                        Path.Combine(stagingRoot, FolderContentsManifest),
                        baseVerification.Written,
                        modifiedVerification.Written);

                    messages.Add(string.Format(
                        "Verified: {0} file(s) in {1}, {2} in {3}, from {4} changed path(s).",
                        baseVerification.Written.Count,
                        BaseFolderName,
                        modifiedVerification.Written.Count,
                        ModifiedFolderName,
                        changes.Count));
                }

                cancellationToken.ThrowIfCancellationRequested();

                // The files are all written, so the bar is full; what is left is the single move that
                // publishes them, and the report says so rather than leaving the bar to look idle.
                Report(progress, "Publishing the result folder", completed.Value, total);

                // A single move publishes the whole result; there is no window in which one of the
                // two folders exists without the other.
                Publish(stagingRoot, finalRoot);
                published = true;

                messages.Add(string.Format("{0} changed path(s) exported.", changes.Count));
                messages.Add("Result:   " + finalRoot);
                messages.Add("Base:     " + Path.Combine(finalRoot, BaseFolderName));
                messages.Add("Modified: " + Path.Combine(finalRoot, ModifiedFolderName));

                if (request.WriteManifests)
                {
                    messages.Add(string.Format(
                        "Manifests: {0}, {1}, {2}, {3} in the result folder.",
                        DeletedFilesManifest,
                        RenamedFilesManifest,
                        ChangedFilesManifest,
                        FolderContentsManifest));
                }

                return new ExportResult
                {
                    Success = true,
                    RootFolderPath = finalRoot,
                    BaseFolderPath = Path.Combine(finalRoot, BaseFolderName),
                    ModifiedFolderPath = Path.Combine(finalRoot, ModifiedFolderName),
                    Messages = messages,
                    Warnings = warnings,
                };
            }
            finally
            {
                // The staging tree is only meaningful while the export is in flight; once it has been
                // moved into place there is nothing left to clean up.
                if (!published)
                {
                    TryDeleteDirectory(stagingRoot);
                }
            }
        }

        private static string ValidateOutputRoot(string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                throw new ArgumentException("Output directory must be set.");
            }

            string full = Path.GetFullPath(outputRoot);
            string root = Path.GetPathRoot(full);

            // An unset or whitespace value would otherwise resolve to the drive root.
            if (root == null || full.Length <= root.Length)
            {
                throw new ArgumentException(string.Format("'{0}' is not a usable output folder.", outputRoot));
            }

            Directory.CreateDirectory(full);
            return full;
        }

        /// <summary>
        /// Warns about files that exist in the base commit and not in the modified one.
        /// </summary>
        /// <remarks>
        /// Only ever called for the modified side, and that is the whole point: a path missing from
        /// the base commit is a file that was added, which is the expected outcome of comparing two
        /// commits and not something worth a warning.
        /// </remarks>
        private static IEnumerable<string> DescribeRemovedFiles(FileExportResult modifiedExport)
        {
            if (modifiedExport.MissingPaths.Count == 0)
            {
                return new string[0];
            }

            List<string> preview = new List<string>();
            for (int i = 0; i < modifiedExport.MissingPaths.Count && i < 5; i++)
            {
                preview.Add(modifiedExport.MissingPaths[i]);
            }

            return new[]
            {
                string.Format(
                    "{0}: {1} path(s) in the base commit are not present in the modified commit, "
                    + "so they are missing from the modified folder: {2}{3}",
                    ModifiedFolderName,
                    modifiedExport.MissingPaths.Count,
                    string.Join(", ", preview.ToArray()),
                    modifiedExport.MissingPaths.Count > 5 ? ", ..." : string.Empty),
            };
        }

        /// <summary>
        /// Warns about two repository paths that the destination filesystem cannot hold separately.
        /// </summary>
        /// <remarks>
        /// This is a genuine data loss in the result folder, and it is invisible from inside: a
        /// comparison of two complete-looking folders finds no difference in the file that survived.
        /// Only the first path is written, so the warning has to name both.
        /// </remarks>
        private static IEnumerable<string> DescribePathConflicts(FileExportResult export, string folderName)
        {
            if (export.ConflictingPaths.Count == 0)
            {
                return new string[0];
            }

            List<string> preview = new List<string>();
            for (int i = 0; i < export.ConflictingPaths.Count && i < 5; i++)
            {
                preview.Add(export.ConflictingPaths[i]);
            }

            return new[]
            {
                string.Format(
                    "{0}: {1} path(s) in the commit are the same file on this filesystem, so one of each "
                    + "pair could not be written: {2}{3}",
                    folderName,
                    export.ConflictingPaths.Count,
                    string.Join(", ", preview.ToArray()),
                    export.ConflictingPaths.Count > 5 ? ", ..." : string.Empty),
            };
        }

        /// <summary>
        /// Warns about paths that are in the commit but were not written, because Git matched them
        /// and then filtered them out of the archive.
        /// </summary>
        /// <remarks>
        /// A zero exit code says only that the archive was built; it does not say every requested path
        /// is in it. An <c>export-ignore</c> entry in <c>.gitattributes</c> and a submodule are the two
        /// ordinary causes, and both are silent - which is why this is reported on both sides rather
        /// than treated as the expected result of a comparison.
        /// </remarks>
        private static IEnumerable<string> DescribeOmittedPaths(FileExportResult export, string folderName)
        {
            if (export.OmittedPaths.Count == 0)
            {
                return new string[0];
            }

            List<string> preview = new List<string>();
            for (int i = 0; i < export.OmittedPaths.Count && i < 5; i++)
            {
                preview.Add(export.OmittedPaths[i]);
            }

            return new[]
            {
                string.Format(
                    "{0}: {1} path(s) are in the commit but are not files, so there was nothing to write "
                    + "for them - usually a submodule, which Git records as a pointer to a commit in "
                    + "another repository: {2}{3}",
                    folderName,
                    export.OmittedPaths.Count,
                    string.Join(", ", preview.ToArray()),
                    export.OmittedPaths.Count > 5 ? ", ..." : string.Empty),
            };
        }

        /// <summary>
        /// Changed paths that cannot appear in the base folder, because they did not exist at the base
        /// commit. An addition is the ordinary case, and is the whole reason the two folders differ in
        /// size.
        /// </summary>
        private static IEnumerable<string> PathsAbsentFromBase(IEnumerable<GitFileChange> changes) =>
            changes
                .Where(change => change.Status == GitChangeStatus.Added)
                .Select(change => change.PathInBaseCommit);

        /// <summary>
        /// Changed paths that cannot appear in the modified folder, because the base commit was the
        /// last place they existed.
        /// </summary>
        private static IEnumerable<string> PathsAbsentFromModified(IEnumerable<GitFileChange> changes) =>
            changes
                .Where(change => change.Status == GitChangeStatus.Deleted)
                .Select(change => change.PathInModifiedCommit);

        /// <summary>
        /// What one exported folder turned out to hold, and how that compares with what it was asked
        /// to hold.
        /// </summary>
        /// <remarks>
        /// The two are not the same thing, and conflating them is how a missing file goes unnoticed:
        /// the export asked for a path, git supplied an object or it did not, and a file on disk is the
        /// only evidence of which happened. Anything present that was not asked for, or asked for and
        /// not present, is read off the disk rather than inferred from the request.
        /// <para>
        /// The membership tests below ignore case, because the disk they describe does. Two repository
        /// paths differing only in case are one file on Windows, the collision warning has already
        /// named both, and an ordinal test would then call the losing path unaccounted for and report
        /// the same fact a second time as an unexplained omission. The lists keep the repository's own
        /// spelling, so a warning still quotes the path as git knows it.
        /// </para>
        /// </remarks>
        private sealed class FolderVerification
        {
            public FolderVerification(
                string folderName,
                IList<string> written,
                IList<string> expected,
                IList<string> expectedAbsent,
                IList<string> accountedFor)
            {
                FolderName = folderName;
                Written = written;
                Expected = expected;
                ExpectedAbsent = expectedAbsent;
                AccountedFor = accountedFor;

                writtenSet = new HashSet<string>(written, StringComparer.OrdinalIgnoreCase);
                expectedSet = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
                expectedAbsentSet = new HashSet<string>(expectedAbsent, StringComparer.OrdinalIgnoreCase);
                accountedForSet = new HashSet<string>(accountedFor, StringComparer.OrdinalIgnoreCase);
            }

            private readonly HashSet<string> writtenSet;
            private readonly HashSet<string> expectedSet;
            private readonly HashSet<string> expectedAbsentSet;
            private readonly HashSet<string> accountedForSet;

            public string FolderName { get; }

            /// <summary>Paths found on disk, relative to the folder, in Git's slash form.</summary>
            public IList<string> Written { get; }

            /// <summary>Paths the change list says belong in this folder.</summary>
            public IList<string> Expected { get; }

            /// <summary>
            /// Paths in <see cref="Expected"/> that cannot be here: an added file has no base version,
            /// a deleted one has no modified version.
            /// </summary>
            public IList<string> ExpectedAbsent { get; }

            /// <summary>
            /// Paths whose absence or collision has already been reported from git's own answer, so
            /// re-reporting them would be the same fact twice.
            /// </summary>
            public IList<string> AccountedFor { get; }

            /// <summary>Wanted, not written, and with no explanation already given for it.</summary>
            public IList<string> Missing =>
                Expected
                    .Where(path =>
                        !writtenSet.Contains(path)
                        && !expectedAbsentSet.Contains(path)
                        && !accountedForSet.Contains(path))
                    .ToList();

            /// <summary>Written, but nothing in the change list asked for it.</summary>
            public IList<string> Unexpected =>
                Written.Where(path => !expectedSet.Contains(path)).ToList();
        }

        /// <summary>
        /// Compares what a folder holds against what the change list says it should hold.
        /// </summary>
        private static FolderVerification VerifyFolder(
            string folderName,
            string folderPath,
            IEnumerable<string> expectedPaths,
            IEnumerable<string> expectedAbsentPaths,
            FileExportResult export)
        {
            // Read from the disk, not from the export result: the export result says what git was asked
            // for and what it answered, and the question here is what actually landed.
            var written = new List<string>();
            var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(folderPath))
            {
                foreach (string file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    onDisk.Add(ToGitPath(RelativeTo(folderPath, file)));
                }
            }

            // The expectation is compared case-insensitively, because the disk cannot be: Windows
            // keeps one file for a pair of paths differing only in case, and the surviving name is
            // whichever landed first. Comparing ordinally would call every such file unexpected.
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in expectedPaths)
            {
                wanted.Add(path);
            }

            var accountedFor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in export.MissingPaths)
            {
                accountedFor.Add(path);
            }

            foreach (string path in export.OmittedPaths)
            {
                accountedFor.Add(path);
            }

            foreach (string path in export.ConflictingPaths)
            {
                accountedFor.Add(path);
            }

            foreach (string path in wanted)
            {
                if (onDisk.Contains(path))
                {
                    written.Add(path);
                }
            }

            written.Sort(StringComparer.Ordinal);

            return new FolderVerification(
                folderName,
                written,
                expectedPaths.ToList(),
                expectedAbsentPaths.ToList(),
                accountedFor.ToList());
        }

        /// <summary>
        /// Turns a verification into warnings, and says plainly when there is nothing to report.
        /// </summary>
        /// <remarks>
        /// Only an unexplained discrepancy is worth a warning. A comparison legitimately produces
        /// folders of different sizes - an addition is in one and not the other - so reporting every
        /// difference would bury a real one in the expected ones, which is what makes a warning useless.
        /// The counts are reported either way, because "these two folders agree with the change list"
        /// is the reassurance the cross-check exists to give.
        /// </remarks>
        private static IEnumerable<string> DescribeVerification(FolderVerification verification)
        {
            List<string> warnings = new List<string>();

            if (verification.Missing.Count > 0)
            {
                warnings.Add(string.Format(
                    "{0}: {1} changed path(s) were not written and nothing accounts for it: {2}{3}",
                    verification.FolderName,
                    verification.Missing.Count,
                    Preview(verification.Missing),
                    verification.Missing.Count > 5 ? ", ..." : string.Empty));
            }

            if (verification.Unexpected.Count > 0)
            {
                warnings.Add(string.Format(
                    "{0}: {1} file(s) are in the folder but are not in the change list: {2}{3}",
                    verification.FolderName,
                    verification.Unexpected.Count,
                    Preview(verification.Unexpected),
                    verification.Unexpected.Count > 5 ? ", ..." : string.Empty));
            }

            return warnings;
        }

        private static string Preview(IList<string> paths)
        {
            List<string> preview = new List<string>();
            for (int i = 0; i < paths.Count && i < 5; i++)
            {
                preview.Add(paths[i]);
            }

            return string.Join(", ", preview.ToArray());
        }

        /// <summary>A path relative to a folder, in the slash form Git uses.</summary>
        private static string RelativeTo(string folder, string file) =>
            Path.GetFullPath(file).Substring(
                Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).Length + 1);

        /// <summary>Git writes paths with forward slashes; the disk hands back the platform's.</summary>
        private static string ToGitPath(string path) =>
            path.Replace('\\', '/');

        /// <summary>
        /// Writes the three per-side lists that make the folder readable without git: what was added,
        /// what was removed and what was merely rewritten.
        /// </summary>
        /// <remarks>
        /// One file per list, each line a repository-relative path in the slash form git uses, so a
        /// reader can diff the two folders' manifests directly. A rename is listed on both sides,
        /// because that is what it is: a path that left and a path that arrived.
        /// </remarks>
        private static void WriteManifests(string folder, IList<GitFileChange> changes)
        {
            List<string> deleted = new List<string>();
            List<string> renamed = new List<string>();
            List<string> changed = new List<string>();

            foreach (GitFileChange change in changes)
            {
                if (change.Status == GitChangeStatus.Deleted)
                {
                    deleted.Add(change.Path);
                }
                else if (change.Status == GitChangeStatus.Renamed && change.OldPath != null)
                {
                    deleted.Add(change.OldPath);
                    renamed.Add(string.Format("{0}  ->  {1}", change.OldPath, change.Path));
                }

                changed.Add(change.DisplayText);
            }

            deleted.Sort(StringComparer.Ordinal);
            renamed.Sort(StringComparer.Ordinal);
            changed.Sort(StringComparer.Ordinal);

            WriteCountedLines(Path.Combine(folder, DeletedFilesManifest), "path", deleted);
            WriteCountedLines(Path.Combine(folder, RenamedFilesManifest), "rename", renamed);
            WriteCountedLines(Path.Combine(folder, ChangedFilesManifest), "change", changed);
        }

        /// <summary>
        /// Writes a manifest with a count of what it is about to list, on its first line.
        /// </summary>
        /// <remarks>
        /// The count is the point of the exercise, but it cannot simply precede the data on an existing
        /// manifest: a reader - or a script - taking the file line by line would count the header as a
        /// path. It is marked with a leading '#', the same convention <c>.gitattributes</c> and
        /// <c>.gitignore</c> use for a line that is not data, so a reader has one rule to learn rather
        /// than one per file.
        /// <para>
        /// A manifest with nothing in it is now one header line rather than a zero-byte file. That is
        /// the more useful of the two: "no renames" and "the renames file never arrived" look identical
        /// when the file is empty, and do not once it says so.
        /// </para>
        /// </remarks>
        private static void WriteCountedLines(string path, string noun, IList<string> lines)
        {
            List<string> content = new List<string> { CountHeader(noun, lines.Count) };
            content.AddRange(lines);
            WriteLines(path, content);
        }

        /// <summary>
        /// Writes the manifest of what each folder actually ended up holding.
        /// </summary>
        /// <remarks>
        /// This is a different thing from <c>ChangedFiles.txt</c>, and the difference is the reason it
        /// is worth having. The other manifests describe what the comparison was; this one describes
        /// what was written, which is the thing the user is about to open in a diff tool. The two
        /// differ whenever something was legitimately not written - an addition has no base version, a
        /// path that collides on this filesystem keeps only one of its two files - and a diff tool
        /// cannot tell that apart from a real omission.
        /// <para>
        /// The lists are the files found on disk rather than the paths that were asked for, so the
        /// manifest cannot claim a file the folder does not contain.
        /// </para>
        /// </remarks>
        private static void WriteFolderContentsManifest(
            string path,
            IList<string> baseFiles,
            IList<string> modifiedFiles)
        {
            List<string> content = new List<string>();

            content.Add(SectionHeader(BaseFolderName, baseFiles.Count));
            content.AddRange(baseFiles);

            content.Add(string.Empty);
            content.Add(SectionHeader(ModifiedFolderName, modifiedFiles.Count));
            content.AddRange(modifiedFiles);

            WriteLines(path, content);
        }

        private static string CountHeader(string noun, int count) =>
            string.Format("# {0} {1}(s)", count, noun);

        private static string SectionHeader(string folderName, int count) =>
            string.Format("# {0}: {1} file(s)", folderName, count);

        private static void WriteLines(string path, IList<string> lines) =>
            File.WriteAllLines(path, lines, ManifestEncoding);

        /// <summary>Moves a staged folder to its final name.</summary>
        private static void Publish(string staged, string final)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(final));

            try
            {
                Directory.Move(staged, final);
            }
            catch (IOException)
            {
                // Staging lives under the same root, so this only triggers on exotic failures; copy
                // as a fallback rather than losing the export.
                CopyDirectory(staged, final);
                TryDeleteDirectory(staged);
            }
        }

        /// <summary>Appends a counter until the path is unused, so an existing folder is never overwritten.</summary>
        private static string UniquePath(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                return path;
            }

            for (int suffix = 2; ; suffix++)
            {
                string candidate = string.Format("{0}-{1}", path, suffix);
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            string prefix = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, MakeRelative(prefix, directory)));
            }

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, MakeRelative(prefix, file));
                string targetFolder = Path.GetDirectoryName(target);
                if (targetFolder != null)
                {
                    Directory.CreateDirectory(targetFolder);
                }

                File.Copy(file, target, true);
            }
        }

        /// <summary>Path.GetRelativePath is unavailable on .NET Framework 4.6.1.</summary>
        private static string MakeRelative(string prefix, string fullPath) =>
            Path.GetFullPath(fullPath).Substring(prefix.Length);

        /// <summary>
        /// A per-file reporter for one side of the export, counting into a shared total.
        /// </summary>
        /// <remarks>
        /// The counter is an object rather than a <c>ref</c> parameter because a closure cannot capture
        /// one, and the two sides run one after the other and share a single number: the modified side
        /// continues from where the base side stopped rather than restarting at zero, which is what
        /// makes the bar monotonic across the whole run.
        /// <para>
        /// Every path handled is counted, whether or not a file was written for it. A path Git could
        /// not supply is finished work, and counting only the files that landed would leave the bar
        /// short of full for exactly the exports that went wrong.
        /// </para>
        /// </remarks>
        private static Action<string> Done(IProgress<ExportProgress>? progress, Counter completed, int total)
        {
            return path =>
            {
                Report(
                    progress,
                    string.Format("Exporting file {0} of {1}", completed.Next(), total),
                    completed.Value,
                    total);
            };
        }

        /// <summary>A count the per-file reporter can share between the two sides of the export.</summary>
        private sealed class Counter
        {
            public int Value { get; private set; }

            public int Next() => ++Value;
        }

        private static void Report(IProgress<ExportProgress>? progress, string message, int completed, int total)
        {
            if (progress != null)
            {
                progress.Report(new ExportProgress(message, completed, total));
            }
        }

        /// <summary>
        /// A short form of a hash, for folder names and messages where the full forty characters would
        /// be noise. Anything already short is passed through rather than cut, so a hash that is
        /// missing does not come back as a fragment of nothing.
        /// </summary>
        private static string Short(string hash)
        {
            return string.IsNullOrEmpty(hash) || hash.Length <= 8 ? hash : hash.Substring(0, 8);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}