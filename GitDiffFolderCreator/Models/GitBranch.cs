using System.Globalization;
using System.Linq;

namespace GitDiffFolderCreator.Models
{
    /// <summary>A local or remote-tracking branch, as listed for the user to pick from.</summary>
    public sealed class GitBranch : ObservableModel
    {
        /// <summary>Short name, e.g. <c>feature/login</c> or <c>origin/main</c>.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The fully qualified ref, e.g. <c>refs/heads/main</c>. Passing this to git is unambiguous,
        /// whereas a short name can collide with a tag or a directory of the same name.
        /// </summary>
        public string RefName { get; set; } = string.Empty;

        /// <summary>Full 40-character object name the branch points at.</summary>
        public string Hash { get; set; } = string.Empty;

        /// <summary>Commit date of the tip, used to sort the list newest first.</summary>
        public DateTimeOffset? CommitDate { get; set; }

        /// <summary>Date as git reported it, shown when it cannot be parsed.</summary>
        public string CommitDateText { get; set; } = string.Empty;

        /// <summary>True for a remote-tracking branch such as <c>origin/main</c>.</summary>
        public bool IsRemote { get; set; }

        /// <summary>True for the branch the working tree is currently on.</summary>
        public bool IsCurrent { get; set; }

        /// <summary>Short name of the tracked upstream, e.g. <c>origin/main</c>, or empty.</summary>
        public string UpstreamName { get; set; } = string.Empty;

        /// <summary>Commits on this branch that the upstream does not have.</summary>
        public int AheadCount { get; set; }

        /// <summary>Commits on the upstream that this branch does not have.</summary>
        public int BehindCount { get; set; }

        /// <summary>True when the branch tracks an upstream, so a sync state can exist at all.</summary>
        public bool HasUpstream => UpstreamName.Length > 0;

        /// <summary>True when the branch and its upstream disagree in either direction.</summary>
        public bool IsOutOfSync => AheadCount > 0 || BehindCount > 0;

        /// <summary>
        /// The divergence as it is shown beside the name, e.g. <c>+2 -1</c>. Empty when the branch is
        /// level with its upstream, so nothing is drawn for the ordinary case.
        /// </summary>
        public string SyncText
        {
            get
            {
                if (!IsOutOfSync)
                {
                    return string.Empty;
                }

                // Spaced so the two counts cannot run together into something that reads as one number.
                string ahead = AheadCount > 0 ? "+" + AheadCount : string.Empty;
                string behind = BehindCount > 0 ? "-" + BehindCount : string.Empty;
                return string.Join(" ", new[] { ahead, behind }.Where(part => part.Length > 0));
            }
        }

        /// <summary>The name without its remote prefix, for the short display form.</summary>
        public string DisplayName
        {
            get
            {
                if (!IsRemote)
                {
                    return Name;
                }

                int slash = Name.IndexOf('/');
                return slash < 0 ? Name : Name.Substring(slash + 1);
            }
        }

        /// <summary>Short hash, for the list row.</summary>
        public string ShortHash =>
            Hash.Length <= 8 ? Hash : Hash.Substring(0, 8);

        /// <summary>Date formatted for display, or empty when git gave nothing usable.</summary>
        public string DateText =>
            CommitDate.HasValue
                ? CommitDate.Value.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : CommitDateText;

        public override string ToString() => Name;

        /// <summary>
        /// Reads one <c>for-each-ref</c> line in <see cref="GitServiceConstants.BranchFormat"/> order.
        /// </summary>
        internal static GitBranch? TryParse(string record)
        {
            if (string.IsNullOrWhiteSpace(record))
            {
                return null;
            }

            string[] fields = record.Split(GitServiceConstants.FieldSeparator);
            if (fields.Length < 7)
            {
                return null;
            }

            string refName = fields[0].Trim();
            string shortName = fields[1].Trim();
            string hash = fields[2].Trim();
            string dateText = fields[3].Trim();
            string upstream = fields[4].Trim();
            string track = fields[5].Trim();

            if (refName.Length == 0 || shortName.Length == 0)
            {
                return null;
            }

            // %(HEAD) is a bare '*' on the current branch and empty on every other one.
            bool isCurrent = fields[6].Trim() == "*";

            bool isRemote = refName.StartsWith("refs/remotes/", StringComparison.Ordinal);

            // origin/HEAD is a symbolic pointer at the default remote branch, not a branch in its
            // own right; listing it would offer a duplicate of the branch it points at.
            if (shortName.Equals("origin", StringComparison.OrdinalIgnoreCase)
                || shortName.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            DateTimeOffset? commitDate = DateTimeOffset.TryParse(
                dateText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed)
                ? parsed
                : (DateTimeOffset?)null;

            return new GitBranch
            {
                RefName = refName,
                Name = shortName,
                Hash = hash,
                CommitDateText = dateText,
                CommitDate = commitDate,
                UpstreamName = upstream,
                IsRemote = isRemote,
                IsCurrent = isCurrent,
            }.ApplyTrack(track);
        }

        /// <summary>
        /// Reads git's <c>%(upstream:track)</c> summary into the two counts.
        /// </summary>
        /// <remarks>
        /// git wraps it in brackets and names the counts in words: <c>[ahead 2]</c>,
        /// <c>[behind 1]</c>, <c>[ahead 2, behind 1]</c>, and an empty string when the branch is level
        /// with its upstream. The order varies, so both words are looked for rather than assuming a
        /// position.
        /// </remarks>
        internal GitBranch ApplyTrack(string track)
        {
            if (string.IsNullOrWhiteSpace(track))
            {
                return this;
            }

            foreach (string part in track.Trim('[', ']').Split(','))
            {
                string token = part.Trim();
                int space = token.IndexOf(' ');

                if (space < 0)
                {
                    continue;
                }

                string word = token.Substring(0, space);
                string number = token.Substring(space + 1).Trim();

                if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                {
                    continue;
                }

                if (word.Equals("ahead", StringComparison.OrdinalIgnoreCase))
                {
                    AheadCount = count;
                }
                else if (word.Equals("behind", StringComparison.OrdinalIgnoreCase))
                {
                    BehindCount = count;
                }
            }

            return this;
        }
    }
}