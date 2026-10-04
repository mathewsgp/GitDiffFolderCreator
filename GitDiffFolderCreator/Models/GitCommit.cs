using System.Globalization;

namespace GitDiffFolderCreator.Models
{
    /// <summary>Which side of the compared range a commit turned out to be.</summary>
    public enum GitCommitRole
    {
        /// <summary>Not one of the two selected commits, or nothing is selected yet.</summary>
        None,

        /// <summary>The older commit; its content goes into the <c>base</c> folder.</summary>
        Base,

        /// <summary>The newer commit; its content goes into the <c>modified</c> folder.</summary>
        Modified,
    }

    /// <summary>A single commit as displayed in the log list.</summary>
    public sealed class GitCommit : ObservableModel
    {
        private GitCommitRole _role;

        /// <summary>Full 40-character object name. Always use this for git operations - abbreviated
        /// names can be ambiguous in large repositories.</summary>
        public string Hash { get; set; } = string.Empty;

        /// <summary>Abbreviated hash, for display and folder naming only.</summary>
        public string ShortHash { get; set; } = string.Empty;

        /// <summary>ISO-8601 commit date exactly as git reported it.</summary>
        public string CommitDateText { get; set; } = string.Empty;

        /// <summary>Parsed commit date, or <c>null</c> when git produced an unparsable value.</summary>
        public DateTimeOffset? CommitDate { get; set; }

        public string Author { get; set; } = string.Empty;

        /// <summary>Refs pointing at this commit, e.g. <c>HEAD -&gt; main, origin/main</c>.</summary>
        public string RefNames { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Which side of the compared range this commit is. Ancestry decides it, so it is only known
        /// once two commits are picked - which is why the badge appears next to the row the user is
        /// looking at rather than in a heading elsewhere on the window.
        /// </summary>
        public GitCommitRole Role
        {
            get { return _role; }
            set { SetField(ref _role, value); }
        }

        public static GitCommit? TryParse(string record)
        {
            if (string.IsNullOrWhiteSpace(record))
            {
                return null;
            }

            string[] fields = record.Split(GitServiceConstants.FieldSeparator);
            if (fields.Length < GitServiceConstants.LogFieldCount)
            {
                return null;
            }

            DateTimeOffset parsed;
            DateTimeOffset? commitDate = DateTimeOffset.TryParse(
                fields[2],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out parsed)
                ? parsed
                : (DateTimeOffset?)null;

            // A commit message may legitimately contain the field separator, so the remainder is
            // rejoined rather than truncated.
            return new GitCommit
            {
                Hash = fields[0],
                ShortHash = fields[1],
                CommitDateText = fields[2],
                CommitDate = commitDate,
                Author = fields[3],
                RefNames = fields[4].Trim(),
                Message = string.Join(GitServiceConstants.FieldSeparator.ToString(), GetTail(fields)).Trim(),
            };
        }

        private static string[] GetTail(string[] fields)
        {
            string[] tail = new string[fields.Length - 5];
            Array.Copy(fields, 5, tail, 0, tail.Length);
            return tail;
        }

        public override string ToString()
        {
            return string.Format("{0} {1} {2} {3} {4}", ShortHash, CommitDateText, Author, RefNames, Message).TrimEnd();
        }
    }

    /// <summary>Separators and format strings shared by <see cref="Services.GitService"/> and the models.</summary>
    public static class GitServiceConstants
    {
        /// <summary>Unit separator between fields of one commit record.</summary>
        public const char FieldSeparator = '\u001F';

        /// <summary>Record separator placed before every commit record by the pretty format.</summary>
        public const char RecordSeparator = '\u001E';

        public const int LogFieldCount = 6;

        /// <summary>
        /// <c>for-each-ref</c> format listing one branch per line: full ref, short name, tip hash,
        /// commit date, upstream, divergence, and a <c>*</c> when the branch is the one the working
        /// tree is on.
        /// </summary>
        /// <remarks>
        /// The upstream and track atoms give each branch's sync state in the same pass, so the badge
        /// can describe any branch rather than only the checked-out one, with no extra git call.
        /// <para>
        /// The separator is written <c>%1f</c>, not <c>%x1f</c>: <c>%x1f</c> is the <c>pretty</c>
        /// format's escape, which <c>for-each-ref</c> does not share. Its atom syntax takes a raw hex
        /// byte instead, and passing <c>%x1f</c> there emits those four characters literally, leaving
        /// every field unseparated.
        /// </para>
        /// </remarks>
        public const string BranchFormat =
            "--format=%(refname)%1f%(refname:short)%1f%(objectname)%1f%(committerdate:iso8601-strict)"
            + "%1f%(upstream:short)%1f%(upstream:track)%1f%(HEAD)";

        /// <summary>
        /// Log format that emits every field separated by control characters. Commit messages
        /// routinely contain punctuation such as <c>;;;</c> or <c>|||</c>, which made the previous
        /// text-based split ambiguous.
        /// </summary>
        public const string LogFormat = "--format=%x1e%H%x1f%h%x1f%cI%x1f%an%x1f%D%x1f%s";

        /// <summary>Global options applied to every git invocation.</summary>
        public static List<string> CommonArguments
        {
            get
            {
                // Stops git from escaping non-ASCII characters in paths as octal \303\251 sequences.
                return new List<string> { "-c", "core.quotepath=false" };
            }
        }
    }
}