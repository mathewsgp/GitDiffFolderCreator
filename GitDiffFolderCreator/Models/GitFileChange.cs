using System.Globalization;

namespace GitDiffFolderCreator.Models
{
    /// <summary>How a file changed between the two compared commits.</summary>
    public enum GitChangeStatus
    {
        Unknown,
        Added,
        Modified,
        Deleted,
        Renamed,
        Copied,
        TypeChanged,
        Unmerged,
    }

    /// <summary>One entry of a <c>git diff --name-status</c> result.</summary>
    public sealed class GitFileChange : ObservableModel
    {
        private bool _isIncluded = true;

        public GitChangeStatus Status { get; set; }

        /// <summary>Single-letter status as reported by git, e.g. <c>A</c>, <c>M</c>, <c>R100</c>.</summary>
        public string StatusCode { get; set; } = string.Empty;

        /// <summary>Path in the newer commit. For deletions this is the path that was removed.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Previous path for renames and copies; otherwise <c>null</c>.</summary>
        public string? OldPath { get; set; }

        /// <summary>
        /// Lines added between the commits, or <c>null</c> when git reports the file as binary or
        /// the count is otherwise unavailable.
        /// </summary>
        public int? AddedLines { get; set; }

        /// <summary>Lines removed between the commits; see <see cref="AddedLines"/>.</summary>
        public int? DeletedLines { get; set; }

        /// <summary>True when git reported <c>-</c> for both counts, i.e. the file is binary.</summary>
        public bool IsBinary { get; set; }

        /// <summary>
        /// Whether the user has left this file in the export. Unticking a file excludes it from both
        /// folders without hiding it, so the decision stays visible and reversible.
        /// </summary>
        public bool IsIncluded
        {
            get { return _isIncluded; }
            set { SetField(ref _isIncluded, value); }
        }

        /// <summary>The single status letter, for a badge whose colour is driven by the status.</summary>
        public string StatusBadge =>
            StatusCode.Length > 0 ? StatusCode.Substring(0, 1) : string.Empty;

        /// <summary>Human wording for the badge's tooltip, so the letters need no legend.</summary>
        public string StatusLabel
        {
            get
            {
                switch (Status)
                {
                    case GitChangeStatus.Added: return "Added";
                    case GitChangeStatus.Modified: return "Modified";
                    case GitChangeStatus.Deleted: return "Deleted";
                    case GitChangeStatus.Renamed: return "Renamed";
                    case GitChangeStatus.Copied: return "Copied";
                    case GitChangeStatus.TypeChanged: return "Type changed";
                    case GitChangeStatus.Unmerged: return "Unmerged";
                    default: return "Changed";
                }
            }
        }

        /// <summary>
        /// The last segment of the path, which is the part that identifies the file.
        /// </summary>
        public string FileName
        {
            get
            {
                int cut = Path.LastIndexOfAny(new[] { '/', '\\' });
                return cut < 0 ? Path : Path.Substring(cut + 1);
            }
        }

        /// <summary>
        /// The folder part of the path, with a trailing separator, or empty for a file at the
        /// repository root.
        /// </summary>
        public string DirectoryText
        {
            get
            {
                int cut = Path.LastIndexOfAny(new[] { '/', '\\' });
                return cut < 0 ? string.Empty : Path.Substring(0, cut + 1);
            }
        }

        /// <summary>True when at least one of the two line counts is known, so the column is worth showing.</summary>
        public bool HasLineCounts => IsBinary || AddedLines.HasValue || DeletedLines.HasValue;

        /// <summary>Lines added, ready to display, or <c>binary</c>.</summary>
        public string AddedText =>
            IsBinary ? "binary"
                : AddedLines.HasValue ? "+" + AddedLines.Value.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;

        /// <summary>Lines removed, ready to display, or empty when git reported no count.</summary>
        public string DeletedText =>
            IsBinary ? string.Empty
                : DeletedLines.HasValue ? "-" + DeletedLines.Value.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;

        private bool HasOldPath
        {
            get { return (Status == GitChangeStatus.Renamed || Status == GitChangeStatus.Copied) && OldPath != null; }
        }

        /// <summary>Path to read from the older commit when exporting the base folder.</summary>
        public string PathInBaseCommit
        {
            get { return HasOldPath ? OldPath! : Path; }
        }

        /// <summary>Path to read from the newer commit when exporting the modified folder.</summary>
        public string PathInModifiedCommit
        {
            get { return Path; }
        }

        /// <summary>
        /// Reads the <c>added TAB deleted TAB path</c> record form of <c>git diff --numstat</c>.
        /// </summary>
        /// <remarks>
        /// Counts are tab separated and git substitutes <c>-</c> for binary files. A path may
        /// contain tabs, so only the first two separators are consumed.
        /// </remarks>
        internal static void ApplyNumStat(string[] tokens, ref int index, GitFileChange change)
        {
            string record = tokens[index++];
            int firstTab = record.IndexOf('\t');
            if (firstTab < 0)
            {
                return;
            }

            int secondTab = record.IndexOf('\t', firstTab + 1);
            if (secondTab < 0)
            {
                return;
            }

            string added = record.Substring(0, firstTab);
            string deleted = record.Substring(firstTab + 1, secondTab - firstTab - 1);

            if (added == "-" && deleted == "-")
            {
                change.IsBinary = true;
                return;
            }

            if (int.TryParse(added, NumberStyles.None, CultureInfo.InvariantCulture, out int addedLines))
            {
                change.AddedLines = addedLines;
            }

            if (int.TryParse(deleted, NumberStyles.None, CultureInfo.InvariantCulture, out int deletedLines))
            {
                change.DeletedLines = deletedLines;
            }
        }

        /// <summary>
        /// The change size as shown next to the path, e.g. <c>+12 -3</c>, or <c>binary</c>. Empty
        /// when git reported no counts, so callers can omit the column entirely.
        /// </summary>
        public string SizeText
        {
            get
            {
                if (IsBinary)
                {
                    return "binary";
                }

                if (!AddedLines.HasValue && !DeletedLines.HasValue)
                {
                    return string.Empty;
                }

                return string.Format(
                    "+{0} -{1}",
                    AddedLines.HasValue ? AddedLines.Value.ToString(CultureInfo.InvariantCulture) : "?",
                    DeletedLines.HasValue ? DeletedLines.Value.ToString(CultureInfo.InvariantCulture) : "?");
            }
        }

        public string DisplayText
        {
            get
            {
                string path = HasOldPath
                    ? string.Format("{0}  ->  {1}", OldPath, Path)
                    : Path;

                return SizeText.Length > 0
                    ? string.Format("{0}  {1}  ({2})", StatusCode, path, SizeText)
                    : string.Format("{0}  {1}", StatusCode, path);
            }
        }

        internal static GitFileChange? TryParse(string[] tokens, ref int index)
        {
            if (index >= tokens.Length)
            {
                return null;
            }

            string statusCode = tokens[index++];
            if (statusCode.Length == 0)
            {
                return null;
            }

            char code = statusCode[0];
            bool hasTwoPaths = code == 'R' || code == 'C';

            if (index >= tokens.Length)
            {
                return null;
            }

            string first = tokens[index++];
            if (first.Length == 0)
            {
                return null;
            }

            if (!hasTwoPaths)
            {
                return new GitFileChange
                {
                    Status = MapStatus(code),
                    StatusCode = statusCode,
                    Path = first,
                };
            }

            if (index >= tokens.Length)
            {
                return null;
            }

            return new GitFileChange
            {
                Status = MapStatus(code),
                StatusCode = statusCode,
                OldPath = first,
                Path = tokens[index++],
            };
        }

        private static GitChangeStatus MapStatus(char code)
        {
            switch (code)
            {
                case 'A': return GitChangeStatus.Added;
                case 'M': return GitChangeStatus.Modified;
                case 'D': return GitChangeStatus.Deleted;
                case 'R': return GitChangeStatus.Renamed;
                case 'C': return GitChangeStatus.Copied;
                case 'T': return GitChangeStatus.TypeChanged;
                case 'U': return GitChangeStatus.Unmerged;
                default: return GitChangeStatus.Unknown;
            }
        }
    }
}