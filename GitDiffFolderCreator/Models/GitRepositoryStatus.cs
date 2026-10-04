using System.Globalization;

namespace GitDiffFolderCreator.Models
{
    /// <summary>
    /// Current branch and its relationship to the upstream, as reported by
    /// <c>git status --porcelain=v2 --branch</c>.
    /// </summary>
    public sealed class GitRepositoryStatus
    {
        public GitRepositoryStatus()
        {
            BranchName = string.Empty;
            UpstreamName = string.Empty;
        }

        /// <summary>Branch name, or a short explanation when HEAD is not on a branch.</summary>
        public string BranchName { get; set; }

        /// <summary>Tracked remote branch, or empty when the branch has no upstream.</summary>
        public string UpstreamName { get; set; }

        /// <summary>Commits on the branch that the upstream does not have.</summary>
        public int AheadCount { get; set; }

        /// <summary>Commits on the upstream that the branch does not have.</summary>
        public int BehindCount { get; set; }

        /// <summary>True when HEAD is detached and no branch is checked out.</summary>
        public bool IsDetachedHead { get; set; }

        /// <summary>Staged or unstaged tracked changes.</summary>
        public int ChangedFileCount { get; set; }

        /// <summary>Untracked files.</summary>
        public int UntrackedFileCount { get; set; }

        /// <summary>Unmerged paths left over from an interrupted merge or rebase.</summary>
        public int ConflictedFileCount { get; set; }

        /// <summary>True when local commits are missing from or ahead of the upstream.</summary>
        public bool IsOutOfSync => AheadCount > 0 || BehindCount > 0;

        /// <summary>True when the working tree has changes that are not yet committed.</summary>
        public bool HasUncommittedChanges =>
            ChangedFileCount > 0 || UntrackedFileCount > 0 || ConflictedFileCount > 0;

        /// <summary>
        /// The label shown in the window: branch name, plus the sync counts when they are not zero.
        /// </summary>
        public string DisplayText
        {
            get
            {
                if (BranchName.Length == 0)
                {
                    return "no repository";
                }

                if (!IsOutOfSync)
                {
                    return BranchName;
                }

                List<string> parts = new List<string>();

                if (AheadCount > 0)
                {
                    parts.Add(string.Format("{0} ahead", AheadCount.ToString(CultureInfo.CurrentCulture)));
                }

                if (BehindCount > 0)
                {
                    parts.Add(string.Format("{0} behind", BehindCount.ToString(CultureInfo.CurrentCulture)));
                }

                return string.Format("{0} ({1})", BranchName, string.Join(", ", parts.ToArray()));
            }
        }

        /// <summary>Describes the uncommitted work, or an empty string when the tree is clean.</summary>
        public string WorkingTreeText
        {
            get
            {
                if (!HasUncommittedChanges)
                {
                    return string.Empty;
                }

                List<string> parts = new List<string>();

                if (ConflictedFileCount > 0)
                {
                    parts.Add(string.Format("{0} conflicted", ConflictedFileCount.ToString(CultureInfo.CurrentCulture)));
                }

                if (ChangedFileCount > 0)
                {
                    parts.Add(string.Format("{0} changed", ChangedFileCount.ToString(CultureInfo.CurrentCulture)));
                }

                if (UntrackedFileCount > 0)
                {
                    parts.Add(string.Format("{0} untracked", UntrackedFileCount.ToString(CultureInfo.CurrentCulture)));
                }

                return string.Join(", ", parts.ToArray());
            }
        }
    }
}
