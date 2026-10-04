using System.Globalization;
using System.IO;
using GitDiffFolderCreator.Models;

namespace GitDiffFolderCreator.Services
{
    /// <summary>
    /// What came of trying to update the remote-tracking refs.
    /// </summary>
    /// <remarks>
    /// Three answers rather than one, because "the branch list was not updated" is two very different
    /// situations wearing the same face. A repository with no remote has nothing to be stale about and
    /// nothing to retry. A repository whose remote could not be reached is showing branches as of the
    /// last fetch that worked, which is worth saying out loud: the list looks complete and authoritative,
    /// and a branch somebody pushed an hour ago is missing from it.
    /// </remarks>
    public enum FetchOutcome
    {
        /// <summary>The refs were updated, so the branch list reflects the remote as it is now.</summary>
        Updated,

        /// <summary>The repository has no remote configured, so there was nothing to update from.</summary>
        NoRemote,

        /// <summary>
        /// A remote is configured but could not be reached. The branches on disk are as of the last
        /// fetch that worked, so they may be behind what the remote holds.
        /// </summary>
        Failed,
    }

    /// <summary>Outcome of exporting a set of files from one commit.</summary>
    public sealed class FileExportResult
    {
        public FileExportResult(IList<string> extractedPaths, IList<string> missingPaths)
            : this(extractedPaths, missingPaths, new List<string>(), new List<string>())
        {
        }

        public FileExportResult(
            IList<string> extractedPaths,
            IList<string> missingPaths,
            IList<string> conflictingPaths)
            : this(extractedPaths, missingPaths, conflictingPaths, new List<string>())
        {
        }

        public FileExportResult(
            IList<string> extractedPaths,
            IList<string> missingPaths,
            IList<string> conflictingPaths,
            IList<string> omittedPaths)
        {
            ExtractedPaths = extractedPaths;
            MissingPaths = missingPaths;
            ConflictingPaths = conflictingPaths;
            OmittedPaths = omittedPaths;
        }

        public IList<string> ExtractedPaths { get; private set; }

        public IList<string> MissingPaths { get; private set; }

        /// <summary>
        /// Paths that could not be written because another path already occupied the same file on
        /// this filesystem. Empty unless the repository holds two paths that differ only in case.
        /// </summary>
        public IList<string> ConflictingPaths { get; private set; }

        /// <summary>
        /// Paths Git matched but did not put in the archive, so nothing was written for them even
        /// though the command succeeded. Non-empty means a file is missing from the result for a
        /// reason that is not "it was never in this commit".
        /// </summary>
        public IList<string> OmittedPaths { get; private set; }
    }

    /// <summary>
    /// Every git interaction in the application. Owns process launching, exit-code checking and
    /// output parsing; the view model and the exporter never see raw git output.
    /// </summary>
    public sealed class GitService
    {
        /// <summary>Number of commits fetched by default.</summary>
        public const int DefaultLogLimit = 1000;

        /// <summary>The only object type that is a file's content.</summary>
        private const string BlobType = "blob";

        private readonly string repositoryPath;

        public GitService(string repositoryPath)
        {
            if (repositoryPath == null)
            {
                throw new ArgumentNullException("repositoryPath");
            }

            this.repositoryPath = repositoryPath;
        }

        /// <summary>
        /// The folder this service reads, as given to the constructor rather than the repository root.
        /// </summary>
        /// <remarks>
        /// Other services need to run git in the same working folder this one does. Handing out the
        /// root instead would work for most repositories but not for one entered through a subfolder
        /// that is a linked worktree or a submodule, where the root and the readable path differ.
        /// </remarks>
        public string RepositoryPath
        {
            get { return repositoryPath; }
        }

        /// <summary>Validates that the configured path is a git working tree and returns its root.</summary>
        public async Task<string> GetRepositoryRootAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            {
                throw new GitCommandException(string.Format("'{0}' is not an existing directory.", repositoryPath));
            }

            List<string> arguments = GitServiceConstants.CommonArguments;
            arguments.Add("rev-parse");
            arguments.Add("--show-toplevel");

            string root = (await GitProcessRunner.RunForOutputAsync(repositoryPath, arguments, cancellationToken).ConfigureAwait(false)).Trim();

            return root.Length == 0 ? repositoryPath : root;
        }

        /// <summary>
        /// Reads the current branch, how far it differs from its upstream, and how much uncommitted
        /// work the working tree holds.
        /// </summary>
        /// <remarks>
        /// Uses <c>status --porcelain=v2 --branch</c>, whose header lines are machine readable and
        /// stable, rather than the human-readable <c>git status</c> whose wording varies by locale.
        /// The call does not touch the index or the working tree.
        /// </remarks>
        public async Task<GitRepositoryStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            List<string> arguments = GitServiceConstants.CommonArguments;

            // A git-level option, so it must precede the subcommand; it stops status from refreshing
            // the index behind the user's back.
            arguments.Add("--no-optional-locks");
            arguments.Add("status");
            arguments.Add("--porcelain=v2");
            arguments.Add("--branch");

            string output = await GitProcessRunner.RunForOutputAsync(
                repositoryPath, arguments, cancellationToken).ConfigureAwait(false);

            return ParseStatus(output);
        }

        internal static GitRepositoryStatus ParseStatus(string output)
        {
            GitRepositoryStatus status = new GitRepositoryStatus();
            bool sawBranchLine = false;

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    sawBranchLine = true;
                    ParseBranchHeader(line.Substring(2), status);
                    continue;
                }

                // Ordinary entries: "1 XY ... path", "2 XY ...", "? path" or "u XY ...".
                if (line[0] == '?')
                {
                    status.UntrackedFileCount++;
                }
                else if (line[0] == 'u')
                {
                    status.ConflictedFileCount++;
                }
                else if (line[0] == '1' || line[0] == '2')
                {
                    status.ChangedFileCount++;
                }
            }

            if (!sawBranchLine && status.BranchName.Length == 0)
            {
                // No header at all means git did not recognise the directory as a repository.
                status.BranchName = "not a git repository";
            }

            return status;
        }

        private static void ParseBranchHeader(string line, GitRepositoryStatus status)
        {
            int space = line.IndexOf(' ');
            if (space < 0)
            {
                return;
            }

            string key = line.Substring(0, space);
            string value = line.Substring(space + 1).Trim();

            switch (key)
            {
                case "branch.head":
                    // "(detached)" is git's wording for a HEAD that is not on a branch.
                    if (value == "(detached)")
                    {
                        status.IsDetachedHead = true;
                        status.BranchName = "detached HEAD";
                    }
                    else if (value == "(unknown)")
                    {
                        // An unborn branch: a repository with no commits yet.
                        status.BranchName = "no commits yet";
                    }
                    else
                    {
                        status.BranchName = value;
                    }

                    break;

                case "branch.upstream":
                    status.UpstreamName = value;
                    break;

                case "branch.ab":
                    foreach (string part in value.Split(' '))
                    {
                        if (part.StartsWith("+", StringComparison.Ordinal)
                            && TryParseCount(part.Substring(1), out int ahead))
                        {
                            status.AheadCount = ahead;
                        }
                        else if (part.StartsWith("-", StringComparison.Ordinal)
                            && TryParseCount(part.Substring(1), out int behind))
                        {
                            status.BehindCount = behind;
                        }
                    }

                    break;
            }
        }

        private static bool TryParseCount(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Reads the commit log, newest first.</summary>
        public async Task<IList<GitCommit>> GetLogAsync(int maxCount, CancellationToken cancellationToken) =>
            await GetLogAsync(maxCount, null, cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// Reads the commit log of one ref, newest first, without changing what is checked out.
        /// </summary>
        /// <remarks>
        /// Any revision git accepts works here - a branch, a remote-tracking branch, a tag, a bare
        /// hash - because <c>git log</c> only ever reads the object database. Passing
        /// <c>null</c> leaves git to use HEAD, which is what the unfiltered caller wants.
        /// </remarks>
        public async Task<IList<GitCommit>> GetLogAsync(int maxCount, string? revision, CancellationToken cancellationToken)
        {
            if (maxCount <= 0)
            {
                maxCount = DefaultLogLimit;
            }

            List<string> arguments = GitServiceConstants.CommonArguments;
            arguments.Add("log");
            arguments.Add("--no-color");
            arguments.Add("-n");
            arguments.Add(maxCount.ToString(CultureInfo.InvariantCulture));
            arguments.Add(GitServiceConstants.LogFormat);

            if (!string.IsNullOrWhiteSpace(revision))
            {
                // '--' would end revision parsing, so the ref goes in first and nothing follows it.
                arguments.Add(revision!);
            }

            string output = await GitProcessRunner.RunForOutputAsync(repositoryPath, arguments, cancellationToken).ConfigureAwait(false);

            List<GitCommit> commits = new List<GitCommit>();
            foreach (string record in output.Split(GitServiceConstants.RecordSeparator))
            {
                GitCommit? commit = GitCommit.TryParse(record.Trim('\r', '\n', ' '));
                if (commit != null)
                {
                    commits.Add(commit);
                }
            }

            return commits;
        }

        /// <summary>
        /// Lists every local and remote-tracking branch, most recently committed first, without
        /// contacting the network or changing what is checked out.
        /// </summary>
        /// <remarks>
        /// <c>for-each-ref</c> reads refs straight from the repository, so it needs no checkout and
        /// no fetch: a branch added by someone else simply will not be listed until a fetch happens,
        /// which is the same as what every other tool shows.
        /// <para>
        /// Sorting is by <c>--sort=-committerdate</c> rather than alphabetically, because the
        /// question the list answers is "what has been worked on lately".
        /// </para>
        /// </remarks>
        public async Task<IList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken)
        {
            List<string> arguments = GitServiceConstants.CommonArguments;
            arguments.Add("for-each-ref");
            arguments.Add("--sort=-committerdate");
            arguments.Add(GitServiceConstants.BranchFormat);

            // Restricting to the two ref namespaces keeps tags out of a list of branches.
            arguments.Add("refs/heads");
            arguments.Add("refs/remotes");

            string output = await GitProcessRunner.RunForOutputAsync(repositoryPath, arguments, cancellationToken).ConfigureAwait(false);

            return ParseBranches(output);
        }

        internal static IList<GitBranch> ParseBranches(string output)
        {
            List<GitBranch> branches = new List<GitBranch>();

            foreach (string line in output.Split('\n'))
            {
                GitBranch? branch = GitBranch.TryParse(line.TrimEnd('\r'));
                if (branch != null)
                {
                    branches.Add(branch);
                }
            }

            return branches;
        }

        /// <summary>
        /// Updates the remote-tracking refs so the branch list reflects what the remote actually has.
        /// </summary>
        /// <returns>
        /// What came of it: the refs were updated, there is no remote to update them from, or the remote
        /// could not be reached. The last is the answer the caller has to be able to tell apart from the
        /// other two, because it is the only one that means the branches on screen may be behind.
        /// </returns>
        /// <remarks>
        /// This is the one operation here that writes to the repository, so it is confined to updating
        /// remote-tracking refs. It never merges, never checks anything out, and never touches a local
        /// branch. <c>GIT_TERMINAL_PROMPT=0</c> stops git blocking on a credential prompt: this window
        /// has no console to answer one, and a hung prompt looks like a hung application.
        /// </remarks>
        public async Task<FetchOutcome> FetchAsync(CancellationToken cancellationToken)
        {
            if (!await HasRemoteAsync(cancellationToken).ConfigureAwait(false))
            {
                // A local-only repository has nothing to fetch, and nothing that can be out of date
                // either, so it is reported as its own case rather than as a fetch that did not work.
                return FetchOutcome.NoRemote;
            }

            List<string> arguments = GitServiceConstants.CommonArguments;
            arguments.Add("fetch");
            arguments.Add("--quiet");

            GitProcessResult result = await GitProcessRunner.RunAsync(
                repositoryPath,
                arguments,
                new Dictionary<string, string>
                {
                    ["GIT_TERMINAL_PROMPT"] = "0",
                },
                cancellationToken).ConfigureAwait(false);

            return result.Succeeded ? FetchOutcome.Updated : FetchOutcome.Failed;
        }

        /// <summary>True when the repository has at least one remote configured.</summary>
        public async Task<bool> HasRemoteAsync(CancellationToken cancellationToken)
        {
            List<string> arguments = GitServiceConstants.CommonArguments;
            arguments.Add("remote");

            GitProcessResult result = await GitProcessRunner.RunAsync(
                repositoryPath, arguments, null, cancellationToken).ConfigureAwait(false);

            return result.Succeeded && result.StandardOutput.Trim().Length > 0;
        }

        /// <summary>Files changed between two commits, with rename detection enabled.</summary>
        public async Task<IList<GitFileChange>> GetChangesAsync(string olderHash, string newerHash, CancellationToken cancellationToken)
        {
            ValidateHash(olderHash, "olderHash");
            ValidateHash(newerHash, "newerHash");

            string range = string.Format("{0}..{1}", olderHash, newerHash);

            // Two commands rather than one --numstat: rename detection is decided independently by
            // each, and the counts are joined onto the --name-status result afterwards so the two
            // outputs can never disagree about which files exist.
            IList<GitFileChange> changes = await RunNameStatusAsync(
                diffArguments(range),
                cancellationToken).ConfigureAwait(false);

            IDictionary<string, GitFileChange> counts = await RunNumStatAsync(
                diffArguments(range),
                cancellationToken).ConfigureAwait(false);

            ApplyCounts(changes, counts);
            return changes;
        }

        private static List<string> diffArguments(string range)
        {
            // -z makes git emit NUL-terminated, unquoted fields, so spaces and non-ASCII characters
            // in pathnames need no special handling on our side.
            List<string> arguments = GitServiceConstants.CommonArguments;
            arguments.Add("diff");
            arguments.Add("-z");
            arguments.Add("--no-color");
            arguments.Add("-M");
            arguments.Add(range);
            return arguments;
        }

        /// <summary>
        /// Reads everything about one commit that the log row does not show: the full message, the
        /// author and committer, and the parents.
        /// </summary>
        /// <remarks>
        /// A separate call from <see cref="GetChangesForCommitAsync"/> on purpose. The log can hold a
        /// thousand commits, and reading a full message for every one of them up front to show ten of
        /// them later would make opening the window do far more work than it needs to.
        /// <para>
        /// <c>show</c> is given <c>-s</c> so that no diff is produced; the file list comes from the
        /// other call, which already enables rename detection.
        /// </para>
        /// </remarks>
        public async Task<GitCommitDetail> GetCommitDetailAsync(string hash, CancellationToken cancellationToken)
        {
            ValidateHash(hash, "hash");

            List<string> arguments = GitServiceConstants.CommonArguments;
            arguments.Add("show");

            // -s suppresses the diff that 'show' would otherwise print. --no-patch says the same
            // thing in the long form, so a configuration file that turns the diff back on cannot
            // quietly turn this into a call that also dumps the whole patch.
            arguments.Add("-s");
            arguments.Add("--no-patch");
            arguments.Add("--no-color");
            arguments.Add(GitCommitDetailFormat.Format);
            arguments.Add(hash);

            string output = await GitProcessRunner.RunForOutputAsync(
                repositoryPath, arguments, cancellationToken).ConfigureAwait(false);

            foreach (string record in output.Split(GitServiceConstants.RecordSeparator))
            {
                GitCommitDetail? detail = GitCommitDetail.TryParse(record.Trim('\r', '\n', ' '));
                if (detail != null)
                {
                    return detail;
                }
            }

            throw new GitCommandException(
                string.Format("Git did not report anything about commit {0}.", Short(hash)),
                string.Format("git show -s {0}", hash),
                output.Trim());
        }

        /// <summary>Files touched by a single commit, excluding the commit message itself.</summary>
        public async Task<IList<GitFileChange>> GetChangesForCommitAsync(string hash, CancellationToken cancellationToken)
        {
            ValidateHash(hash, "hash");

            IList<GitFileChange> changes = await RunNameStatusAsync(
                showArguments(hash),
                cancellationToken).ConfigureAwait(false);

            IDictionary<string, GitFileChange> counts = await RunNumStatAsync(
                showArguments(hash),
                cancellationToken).ConfigureAwait(false);

            ApplyCounts(changes, counts);
            return changes;
        }

        private static List<string> showArguments(string hash)
        {
            List<string> arguments = GitServiceConstants.CommonArguments;
            arguments.Add("show");
            arguments.Add("-z");
            arguments.Add("--no-color");
            arguments.Add("-M");
            arguments.Add("--format=");
            arguments.Add(hash);
            return arguments;
        }

        private static List<string> nameStatusArguments(List<string> arguments)
        {
            arguments.Add("--name-status");
            return arguments;
        }

        private static List<string> numStatArguments(List<string> arguments)
        {
            arguments.Add("--numstat");
            return arguments;
        }

        private async Task<IList<GitFileChange>> RunNameStatusAsync(
            List<string> arguments,
            CancellationToken cancellationToken)
        {
            string output = await GitProcessRunner.RunForOutputAsync(
                repositoryPath,
                nameStatusArguments(arguments),
                cancellationToken).ConfigureAwait(false);

            return ParseNameStatus(output);
        }

        private async Task<IDictionary<string, GitFileChange>> RunNumStatAsync(
            List<string> arguments,
            CancellationToken cancellationToken)
        {
            string output = await GitProcessRunner.RunForOutputAsync(
                repositoryPath,
                numStatArguments(arguments),
                cancellationToken).ConfigureAwait(false);

            return ParseNumStat(output);
        }

        /// <summary>
        /// Copies the counts from a parallel <c>--numstat</c> result onto the <c>--name-status</c>
        /// result. Files without a matching count are left with no counts shown rather than zero, so
        /// "unknown" is never displayed as "no lines changed".
        /// </summary>
        private static void ApplyCounts(
            IList<GitFileChange> changes,
            IDictionary<string, GitFileChange> counts)
        {
            foreach (GitFileChange change in changes)
            {
                if (counts.TryGetValue(change.Path, out GitFileChange? counted))
                {
                    change.AddedLines = counted.AddedLines;
                    change.DeletedLines = counted.DeletedLines;
                    change.IsBinary = counted.IsBinary;
                }
            }
        }

        /// <summary>
        /// Determines whether <paramref name="candidate"/> is an ancestor of <paramref name="other"/>.
        /// git exits 0 for yes, 1 for no and 128 for an error, so the two outcomes stay
        /// distinguishable.
        /// </summary>
        public async Task<bool> IsAncestorAsync(string candidate, string other, CancellationToken cancellationToken)
        {
            ValidateHash(candidate, "candidate");
            ValidateHash(other, "other");

            List<string> arguments = GitServiceConstants.CommonArguments;
            arguments.Add("merge-base");
            arguments.Add("--is-ancestor");
            arguments.Add(candidate);
            arguments.Add(other);

            GitProcessResult result = await GitProcessRunner.RunAsync(repositoryPath, arguments, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                return true;
            }

            if (result.ExitCode == 1)
            {
                return false;
            }

            throw new GitCommandException(
                string.Format("Could not compare {0} with {1}.", Short(candidate), Short(other)),
                string.Format("git merge-base --is-ancestor {0} {1}", candidate, other),
                result.StandardError.Trim());
        }

        /// <summary>
        /// Copies the given files from a commit into <paramref name="destinationDirectory"/>,
        /// recreating the repository-relative directory structure.
        /// </summary>
        /// <remarks>
        /// Every path is asked for through one <c>git cat-file --batch</c> process. Git answers each
        /// request individually, so a path that does not exist at that commit is an answer rather than
        /// a failure, and one bad pathname cannot discard the rest - which is what the previous
        /// archive-and-retry arrangement existed to achieve.
        /// </remarks>
        /// <param name="onPathHandled">
        /// Called once for every path answered, whatever the outcome, so a caller can count work
        /// finished. <c>null</c> when the caller has no interest in the running total.
        /// </param>
        public async Task<FileExportResult> ExportFilesAsync(
            string hash,
            IList<string> paths,
            string destinationDirectory,
            Action<string>? onPathHandled,
            CancellationToken cancellationToken)
        {
            ValidateHash(hash, "hash");

            if (paths == null)
            {
                throw new ArgumentNullException("paths");
            }

            if (paths.Count == 0)
            {
                return new FileExportResult(new List<string>(), new List<string>());
            }

            Directory.CreateDirectory(destinationDirectory);

            var sink = new ExportSink(destinationDirectory, onPathHandled);

            // ':path' rather than a separate -- separator, matching how the diff tool asks for one
            // file, so a path beginning with a dash is read as a path.
            List<string> requests = new List<string>(paths.Count);
            foreach (string path in paths)
            {
                requests.Add(hash + ":" + path);
            }

            await GitProcessRunner.RunCatFileBatchAsync(
                repositoryPath,
                requests,
                entry => WriteEntryAsync(entry, sink, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            return new FileExportResult(
                sink.Extracted, sink.Missing, sink.Conflicting, sink.Omitted);
        }

        /// <summary>
        /// Where one export's answers are recorded as they arrive.
        /// </summary>
        /// <remarks>
        /// A class rather than four parameters carried through two call frames, and the place the
        /// per-path callback lives so that <see cref="WriteEntryAsync"/> reports exactly once per
        /// path whatever it decides.
        /// </remarks>
        private sealed class ExportSink
        {
            public ExportSink(string destinationDirectory, Action<string>? onPathHandled)
            {
                DestinationDirectory = destinationDirectory;
                OnPathHandled = onPathHandled;
            }

            public string DestinationDirectory { get; }

            private Action<string>? OnPathHandled { get; }

            public List<string> Extracted { get; } = new List<string>();

            public List<string> Missing { get; } = new List<string>();

            public List<string> Conflicting { get; } = new List<string>();

            public List<string> Omitted { get; } = new List<string>();

            /// <summary>
            /// Keyed by the file each path resolved to, valued by the git path that claimed it, so a
            /// clash can name both sides of it.
            /// </summary>
            public Dictionary<string, string> Written { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public void Handled(string path) => OnPathHandled?.Invoke(path);
        }

        /// <summary>
        /// Writes one answer from git to its place in the destination, or records why it was not
        /// written.
        /// </summary>
        /// <remarks>
        /// The four outcomes are all ordinary, and none of them stops the export: written, absent at
        /// this commit, present but not a file, and present but already occupied by a path Windows
        /// cannot tell apart from it. Every one of them counts as a path handled, because from the
        /// caller's point of view the work is done either way.
        /// </remarks>
        private static async Task WriteEntryAsync(
            CatFileEntry entry, ExportSink sink, CancellationToken cancellationToken)
        {
            string path = entry.Request.Substring(entry.Request.IndexOf(':') + 1);

            try
            {
                if (!entry.Exists)
                {
                    sink.Missing.Add(path);
                    return;
                }

                if (entry.ObjectType != BlobType)
                {
                    // A submodule is a commit object and a directory is a tree, so there is no file
                    // content to write. Anything but a blob means git has no file at this path.
                    sink.Omitted.Add(path);
                    return;
                }

                string target = Path.Combine(
                    sink.DestinationDirectory, path.Replace('/', Path.DirectorySeparatorChar));

                // Git's own path validation rejects '.' and '..' components, so this cannot be reached
                // from a repository. It is checked anyway: the destination is built from data, and a
                // path that escaped it would write outside the run folder.
                if (!IsInside(sink.DestinationDirectory, target))
                {
                    throw new GitCommandException(string.Format(
                        "Refusing to write '{0}' outside the destination folder.", path));
                }

                // Two repository paths that are one file here - "Notes.txt" and "notes.txt" - would
                // otherwise leave the second silently replacing the first, with nothing in the result
                // showing that a file had been lost.
                if (!sink.Written.ContainsKey(target))
                {
                    sink.Written[target] = path;
                }
                else
                {
                    sink.Conflicting.Add(string.Format(
                        "'{0}' and '{1}' are the same file on this filesystem; only the first was written.",
                        sink.Written[target],
                        path));
                    return;
                }

                string parent = Path.GetDirectoryName(target);

                if (parent != null)
                {
                    Directory.CreateDirectory(parent);
                }

                // FileMode.Create, so a half-written file from an interrupted run cannot be mistaken
                // for this one's content.
                using (FileStream file = new FileStream(
                    target,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    entry.Content!.CopyExactly(file, entry.Size, cancellationToken);
                }

                sink.Extracted.Add(path);
            }
            finally
            {
                sink.Handled(path);
            }
        }

        /// <summary>Guards against a path escaping the destination folder.</summary>
        private static bool IsInside(string root, string candidate)
        {
            char separator = Path.DirectorySeparatorChar;
            string fullRoot = Path.GetFullPath(root).TrimEnd(separator) + separator;
            return Path.GetFullPath(candidate).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }


        internal static IList<GitFileChange> ParseNameStatus(string output)
        {
            List<GitFileChange> changes = new List<GitFileChange>();

            if (output.Length == 0)
            {
                return changes;
            }

            string[] tokens = output.Split('\0');
            int index = 0;

            while (index < tokens.Length)
            {
                GitFileChange? change = GitFileChange.TryParse(tokens, ref index);
                if (change != null)
                {
                    changes.Add(change);
                }
                else if (index < tokens.Length && tokens[index].Length == 0)
                {
                    index++;
                }
            }

            return changes;
        }

        /// <summary>
        /// Parses <c>git diff --numstat</c> output into per-path line counts.
        /// </summary>
        /// <remarks>
        /// Results are keyed by the new path. For a rename git emits the counts and then the old and
        /// new path as separate NUL-terminated fields, so the new path is the second of the two and is
        /// the key <see cref="GitFileChange.Path"/> is matched against.
        /// </remarks>
        internal static IDictionary<string, GitFileChange> ParseNumStat(string output)
        {
            Dictionary<string, GitFileChange> counts = new Dictionary<string, GitFileChange>(StringComparer.Ordinal);

            if (output.Length == 0)
            {
                return counts;
            }

            string[] tokens = output.Split('\0');
            int index = 0;

            while (index < tokens.Length)
            {
                if (tokens[index].Length == 0)
                {
                    index++;
                    continue;
                }

                string? path = ReadNumStatPath(tokens[index]);

                GitFileChange change = new GitFileChange();
                GitFileChange.ApplyNumStat(tokens, ref index, change);

                if (path == null)
                {
                    // Rename or copy: the path itself is the next two fields.
                    if (index < tokens.Length && tokens[index].Length > 0)
                    {
                        index++;
                    }

                    if (index >= tokens.Length || tokens[index].Length == 0)
                    {
                        break;
                    }

                    path = tokens[index];
                    index++;
                }

                counts[path] = change;
            }

            return counts;
        }

        private static string? ReadNumStatPath(string record)
        {
            int firstTab = record.IndexOf('\t');
            if (firstTab < 0)
            {
                return null;
            }

            int secondTab = record.IndexOf('\t', firstTab + 1);
            if (secondTab < 0)
            {
                return null;
            }

            string path = record.Substring(secondTab + 1);
            return path.Length > 0 ? path : null;
        }

        private static void ValidateHash(string hash, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                throw new ArgumentException("Commit hash must not be empty.", parameterName);
            }
        }

        private static string Short(string hash)
        {
            return hash.Length <= 8 ? hash : hash.Substring(0, 8);
        }
    }
}
