using System;
using System.Threading;
using System.Threading.Tasks;
using GitDiffFolderCreator.Models;

namespace GitDiffFolderCreator.Services
{
    /// <summary>How reliably the base/modified direction was determined.</summary>
    public enum RangeConfidence
    {
        /// <summary>One commit is a verified ancestor of the other.</summary>
        Ancestor,

        /// <summary>Inferred from commit dates because neither commit is an ancestor of the other.</summary>
        DateHeuristic,
    }

    /// <summary>Two commits ordered so that the first is the older one.</summary>
    public sealed class CommitRange
    {
        public CommitRange(GitCommit baseCommit, GitCommit modified, RangeConfidence confidence)
        {
            Base = baseCommit;
            Modified = modified;
            Confidence = confidence;
        }

        public GitCommit Base { get; private set; }

        public GitCommit Modified { get; private set; }

        public RangeConfidence Confidence { get; private set; }

        public string ConfidenceNote
        {
            get
            {
                return Confidence == RangeConfidence.Ancestor
                    ? string.Empty
                    : "These commits are not on the same branch line, so the direction was inferred from commit dates. Check the file list before trusting the result.";
            }
        }
    }

    /// <summary>Works out which of two selected commits is the base and which is the modified one.</summary>
    public static class CommitRangeResolver
    {
        /// <summary>
        /// Orders two commits. Ancestry is checked first because <c>git log</c> is date-ordered rather
        /// than ancestry-ordered, so relying on list position silently inverted the export for merged
        /// or rebased history.
        /// </summary>
        public static async Task<CommitRange> ResolveAsync(
            GitService git,
            GitCommit first,
            GitCommit second,
            CancellationToken cancellationToken)
        {
            if (git == null)
            {
                throw new ArgumentNullException("git");
            }

            if (first == null)
            {
                throw new ArgumentNullException("first");
            }

            if (second == null)
            {
                throw new ArgumentNullException("second");
            }

            if (string.Equals(first.Hash, second.Hash, StringComparison.Ordinal))
            {
                throw new ArgumentException("The same commit was selected twice.", "second");
            }

            if (await git.IsAncestorAsync(first.Hash, second.Hash, cancellationToken).ConfigureAwait(false))
            {
                return new CommitRange(first, second, RangeConfidence.Ancestor);
            }

            if (await git.IsAncestorAsync(second.Hash, first.Hash, cancellationToken).ConfigureAwait(false))
            {
                return new CommitRange(second, first, RangeConfidence.Ancestor);
            }

            return OrderByDate(first, second);
        }

        private static CommitRange OrderByDate(GitCommit first, GitCommit second)
        {
            DateTimeOffset left = first.CommitDate.HasValue ? first.CommitDate.Value : DateTimeOffset.MinValue;
            DateTimeOffset right = second.CommitDate.HasValue ? second.CommitDate.Value : DateTimeOffset.MinValue;

            if (left == right)
            {
                // Fall back to the hash so the result is at least deterministic.
                return string.CompareOrdinal(first.Hash, second.Hash) <= 0
                    ? new CommitRange(first, second, RangeConfidence.DateHeuristic)
                    : new CommitRange(second, first, RangeConfidence.DateHeuristic);
            }

            return left <= right
                ? new CommitRange(first, second, RangeConfidence.DateHeuristic)
                : new CommitRange(second, first, RangeConfidence.DateHeuristic);
        }
    }
}