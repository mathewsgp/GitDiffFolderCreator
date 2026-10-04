using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GitDiffFolderCreator.Services
{
    /// <summary>One way in which the document and the source folders disagree.</summary>
    public enum ChangeFindingKind
    {
        /// <summary>The document lists it, but the two folders hold the same content.</summary>
        DocumentedButUnchanged,

        /// <summary>The document lists it, but no such file is in the modified folder.</summary>
        DocumentedButAbsent,

        /// <summary>The source differs, and the document does not mention it.</summary>
        MissingFromDocument,

        /// <summary>It differs under a different path than the one documented.</summary>
        PathMismatch,

        /// <summary>In the base folder and gone from the modified one.</summary>
        OnlyInBase,

        /// <summary>Only in the modified folder.</summary>
        OnlyInModified,

        /// <summary>The document lists the same path twice.</summary>
        ListedTwice,

        /// <summary>The path is under a folder the comparison was told to skip.</summary>
        NotCompared,
    }

    /// <summary>One disagreement, in a form that can be shown and acted on.</summary>
    public sealed class ChangeFinding
    {
        public ChangeFinding(
            ChangeFindingKind kind,
            string? documentedPath,
            string? actualPath,
            string detail)
        {
            Kind = kind;
            DocumentedPath = documentedPath;
            ActualPath = actualPath;
            Detail = detail;
        }

        public ChangeFindingKind Kind { get; }

        /// <summary>The path as the document gave it, or null when the document did not mention it.</summary>
        public string? DocumentedPath { get; }

        /// <summary>The path the source actually has, or null when there is none.</summary>
        public string? ActualPath { get; }

        /// <summary>One sentence saying what is wrong, in the reader's terms rather than the tool's.</summary>
        public string Detail { get; }

        /// <summary>The path to show in the list: whichever one there is.</summary>
        public string DisplayPath => DocumentedPath ?? ActualPath ?? string.Empty;

        /// <summary>The short heading this kind of finding gets.</summary>
        public string KindCaption => Describe(Kind);

        public static string Describe(ChangeFindingKind kind)
        {
            switch (kind)
            {
                case ChangeFindingKind.DocumentedButUnchanged:
                    return "Listed, not changed";

                case ChangeFindingKind.DocumentedButAbsent:
                    return "Listed, not present";

                case ChangeFindingKind.MissingFromDocument:
                    return "Changed, not listed";

                case ChangeFindingKind.PathMismatch:
                    return "Wrong path";

                case ChangeFindingKind.OnlyInBase:
                    return "Deleted, not listed";

                case ChangeFindingKind.OnlyInModified:
                    return "Added, not listed";

                case ChangeFindingKind.ListedTwice:
                    return "Listed twice";

                case ChangeFindingKind.NotCompared:
                    return "Not compared";

                default:
                    return kind.ToString();
            }
        }
    }

    /// <summary>Whether the document and the source agree.</summary>
    public enum ChangeVerdict
    {
        /// <summary>Everything the document lists differs, and everything that differs is listed.</summary>
        Agrees,

        /// <summary>They do not agree.</summary>
        Disagrees,
    }

    /// <summary>What the cross-check found.</summary>
    public sealed class ChangeVerificationResult
    {
        public ChangeVerificationResult(
            ChangeVerdict verdict,
            IList<ChangeFinding> findings,
            int documentedCount,
            int actualDifferenceCount,
            int filesCompared,
            int filesIgnored,
            int matches)
        {
            Verdict = verdict;
            Findings = findings;
            DocumentedCount = documentedCount;
            ActualDifferenceCount = actualDifferenceCount;
            FilesCompared = filesCompared;
            FilesIgnored = filesIgnored;
            Matches = matches;
        }

        public ChangeVerdict Verdict { get; }

        public IList<ChangeFinding> Findings { get; }

        /// <summary>How many paths the document claimed.</summary>
        public int DocumentedCount { get; }

        /// <summary>How many paths actually differ between the folders.</summary>
        public int ActualDifferenceCount { get; }

        public int FilesCompared { get; }

        public int FilesIgnored { get; }

        /// <summary>How many documented paths were confirmed against a real difference.</summary>
        public int Matches { get; }

        public bool Agrees => Verdict == ChangeVerdict.Agrees;
    }

    /// <summary>
    /// Cross-checks the file list in a change document against the real difference between two
    /// folders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question is one of correspondence in both directions, and a check that only runs in one
    /// is the mistake this exists to prevent. A document listing a file that was never touched is a
    /// false claim about the work; a file that changed without being listed is an undeclared change.
    /// Either alone is enough to make the document wrong.
    /// </para>
    /// <para>
    /// Paths are matched without regard to case, because the filesystem holding the two folders does
    /// not regard it either. A mismatch over case is therefore not a finding at all — it is the same
    /// file, and reporting it would contradict §5.3.4's rule for the export.
    /// </para>
    /// </remarks>
    public sealed class ChangeDocumentVerifier
    {
        /// <summary>
        /// Compares the document's claims with the folders.
        /// </summary>
        public ChangeVerificationResult Verify(
            IList<DocumentedFile> documented,
            FolderComparison comparison)
        {
            if (documented == null)
            {
                throw new ArgumentNullException(nameof(documented));
            }

            if (comparison == null)
            {
                throw new ArgumentNullException(nameof(comparison));
            }

            var findings = new List<ChangeFinding>();

            // What actually differs, keyed the way the document spells paths, so a claim is matched
            // against the difference it is claiming.
            var changed = new Dictionary<string, FolderDifference>(StringComparer.OrdinalIgnoreCase);
            var movedFrom = new Dictionary<string, FolderMove>(StringComparer.OrdinalIgnoreCase);
            var movedTo = new Dictionary<string, FolderMove>(StringComparer.OrdinalIgnoreCase);
            var movedAway = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (FolderMove move in comparison.Moves())
            {
                movedFrom[move.ToPath] = move;
                movedTo[move.FromPath] = move;
                movedAway.Add(move.FromPath);
            }

            foreach (FolderDifference difference in comparison.Differences)
            {
                // A move is one change under its new name, which is the name a document would use.
                if (difference.Kind == FolderDifferenceKind.Added && movedFrom.ContainsKey(difference.ModifiedPath!))
                {
                    changed[difference.ModifiedPath!] = difference;
                    continue;
                }

                // The old side of a move is not an independent deletion, and reporting it as one
                // would tell the reader to delete a file that still exists.
                if (difference.Kind == FolderDifferenceKind.Removed && movedAway.Contains(difference.BasePath!))
                {
                    continue;
                }

                changed[difference.PrimaryPath] = difference;
            }

            int matches = 0;
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seenInDocument = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DocumentedFile file in documented)
            {
                if (seenInDocument.Contains(file.Path))
                {
                    findings.Add(new ChangeFinding(
                        ChangeFindingKind.ListedTwice,
                        file.Path,
                        null,
                        "The document lists this path more than once, on lines including "
                            + file.LineNumber.ToString(CultureInfo.InvariantCulture) + "."));

                    continue;
                }

                seenInDocument.Add(file.Path);

                // The claim as the document spells it. The comparison reads it from the right, so a
                // document that wrote the path from part way down still names the file - what it got
                // wrong at the front is dropped once the file is found.
                string? key = Resolve(file.Path, changed.Keys) ?? Resolve(file.Path, movedTo.Keys);

                if (key != null)
                {
                    if (matchedLines.TryGetValue(key, out int firstLine))
                    {
                        findings.Add(new ChangeFinding(
                            ChangeFindingKind.ListedTwice,
                            file.Path,
                            key,
                            "This line and line "
                                + firstLine.ToString(CultureInfo.InvariantCulture)
                                + " both name the same file, " + key + "."));
                    }
                    else
                    {
                        matched.Add(key);
                        matchedLines[key] = file.LineNumber;

                        matches++;

                        if (movedFrom.TryGetValue(key, out FolderMove? move))
                        {
                            matched.Add(move.FromPath);

                            findings.Add(new ChangeFinding(
                                ChangeFindingKind.PathMismatch,
                                file.Path,
                                move.ToPath,
                                "The document says this file was modified. It was moved instead: the "
                                    + "same content is at " + move.ToPath + " and no longer at "
                                    + move.FromPath + "."));
                        }
                        else if (movedTo.TryGetValue(key, out FolderMove? otherWay))
                        {
                            matched.Add(otherWay.ToPath);

                            findings.Add(new ChangeFinding(
                                ChangeFindingKind.PathMismatch,
                                file.Path,
                                otherWay.ToPath,
                                "The document says this file was modified. It was moved instead: the "
                                    + "same content is at " + otherWay.ToPath + " and no longer at "
                                    + otherWay.FromPath + "."));
                        }
                    }
                }
                else if (!IsAccountedFor(matched, changed, movedTo, file))
                {
                    findings.Add(DescribeUnmatchedClaim(file, comparison));
                }
            }

            foreach (KeyValuePair<string, FolderDifference> entry in changed)
            {
                if (matched.Contains(entry.Key))
                {
                    continue;
                }

                findings.Add(new ChangeFinding(
                    UndocumentedKind(entry.Value.Kind),
                    null,
                    entry.Value.PrimaryPath,
                    UndocumentedDetail(entry.Value.Kind)));
            }

            findings.Sort(CompareFindings);

            return new ChangeVerificationResult(
                findings.Count == 0 ? ChangeVerdict.Agrees : ChangeVerdict.Disagrees,
                findings,
                documented.Count,
                comparison.Differences.Count,
                comparison.FilesCompared,
                comparison.FilesIgnored,
                matches);
        }

        /// <summary>
        /// Whether a claim names something an earlier line already accounted for.
        /// </summary>
        private static bool IsAccountedFor(
            HashSet<string> matched,
            Dictionary<string, FolderDifference> changed,
            Dictionary<string, FolderMove> movedTo,
            DocumentedFile file)
        {
            string? key = Resolve(file.Path, changed.Keys) ?? Resolve(file.Path, movedTo.Keys);

            return key != null && matched.Contains(key);
        }

        /// <summary>
        /// The real path a claim names: the one real path that ends with the claim.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A document does not have to start from the git root. <c>api/Reader.py</c>,
        /// <c>Layer/api/Reader.py</c> and <c>Src/Module1/Frames/2222.xaml.cs</c> all name files, because
        /// each ends with the segments its file is at — so the claim is read from the right and compared
        /// as a tail. Comparing whole strings would call every shortened path missing; comparing
        /// characters would make <c>Reader.py</c> match <c>LegacyReader.py</c>.
        /// </para>
        /// <para>
        /// The tail has to land on a separator, which is what makes it a path rather than a piece of a
        /// name. Both sides are normalised first — see <see cref="ChangeDocumentParser.Normalize"/> — so
        /// a backslash in the document and a slash in the folder walk are the same separator, and a
        /// leading <c>\</c> is not a folder the source has to have.
        /// </para>
        /// <para>
        /// Two files ending with the same claim is no answer at all: naming either would put a guess in
        /// the output where a fact belongs, and the reader would have no way to tell. It is left
        /// unresolved, and reported as a claim that names nothing.
        /// </para>
        /// </remarks>
        private static string? Resolve(string claim, IEnumerable<string> actual)
        {
            string? documented = ChangeDocumentParser.Normalize(claim);

            if (documented == null)
            {
                return null;
            }

            string? hit = null;

            foreach (string path in actual)
            {
                if (!EndsWith(path!, documented))
                {
                    continue;
                }

                if (hit != null)
                {
                    return null;
                }

                hit = path;
            }

            return hit;
        }

        /// <summary>
        /// Whether a real path is the claim itself, or ends with it at a separator.
        /// </summary>
        /// <remarks>
        /// The real paths arrive already normalised — <c>FolderComparer</c> walks the trees and writes
        /// them relative and slashed — so this normalises them again only so that the answer does not
        /// depend on that. It is the same function the claim went through, which is the point: neither
        /// side gets to be right by accident.
        /// </remarks>
        private static bool EndsWith(string path, string documented)
        {
            string? normalised = ChangeDocumentParser.Normalize(path);

            if (normalised == null)
            {
                return false;
            }

            return string.Equals(normalised, documented, StringComparison.OrdinalIgnoreCase)
                || normalised.EndsWith("/" + documented, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Why a documented path found no difference: it may be identical in both folders, absent from
        /// the modified one entirely, or sitting under a folder the comparison skipped — and those are
        /// three different answers.
        /// </summary>
        private ChangeFinding DescribeUnmatchedClaim(DocumentedFile file, FolderComparison comparison)
        {
            string? present = Resolve(file.Path, comparison.ModifiedPaths);

            if (present != null)
            {
                return new ChangeFinding(
                    ChangeFindingKind.DocumentedButUnchanged,
                    file.Path,
                    present,
                    "The document lists this file as modified, but the two sources hold identical content.");
            }

            if (IsIgnored(file.Path, comparison))
            {
                return new ChangeFinding(
                    ChangeFindingKind.NotCompared,
                    file.Path,
                    null,
                    "This file is under a folder the comparison skipped, so it was not checked. "
                        + "Clear the ignore rule to include it.");
            }

            return new ChangeFinding(
                ChangeFindingKind.DocumentedButAbsent,
                file.Path,
                null,
                "The document lists this file, but there is no such file in the modified source.");
        }

        /// <summary>
        /// Whether the path sits under a folder this comparison left out.
        /// </summary>
        /// <remarks>
        /// Every segment is checked, not just the first, because the claim may have been written from part
        /// way down and the skipped folder can be anywhere in it.
        /// </remarks>
        private static bool IsIgnored(string path, FolderComparison comparison)
        {
            foreach (string segment in path.Split('/'))
            {
                if (comparison.IgnoredFolders.Contains(segment))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The kind of finding for a difference the document did not mention, which depends on how the
        /// file differs: edited, added or deleted are three different omissions.
        /// </summary>
        private static ChangeFindingKind UndocumentedKind(FolderDifferenceKind kind)
        {
            switch (kind)
            {
                case FolderDifferenceKind.Modified:
                    return ChangeFindingKind.MissingFromDocument;

                case FolderDifferenceKind.Added:
                    return ChangeFindingKind.OnlyInModified;

                default:
                    return ChangeFindingKind.OnlyInBase;
            }
        }

        private static string UndocumentedDetail(FolderDifferenceKind kind)
        {
            switch (kind)
            {
                case FolderDifferenceKind.Modified:
                    return "This file's content differs between the two sources. The document does not mention it.";

                case FolderDifferenceKind.Added:
                    return "This file is only in the modified source. The document does not mention it.";

                default:
                    return "This file is in the base source and gone from the modified source. The document does not mention it.";
            }
        }

        /// <summary>
        /// Findings in the order a reader wants to act on them: wrong claims first, then omissions,
        /// then the tidiest.
        /// </summary>
        private static int CompareFindings(ChangeFinding left, ChangeFinding right)
        {
            int byKind = Rank(left.Kind).CompareTo(Rank(right.Kind));

            return byKind != 0
                ? byKind
                : string.CompareOrdinal(left.DisplayPath, right.DisplayPath);
        }

        private static int Rank(ChangeFindingKind kind)
        {
            switch (kind)
            {
                case ChangeFindingKind.ListedTwice:
                    return 0;

                case ChangeFindingKind.NotCompared:
                    return 5;

                case ChangeFindingKind.PathMismatch:
                    return 1;

                case ChangeFindingKind.DocumentedButAbsent:
                    return 2;

                case ChangeFindingKind.DocumentedButUnchanged:
                    return 3;

                case ChangeFindingKind.MissingFromDocument:
                    return 4;

                case ChangeFindingKind.OnlyInBase:
                case ChangeFindingKind.OnlyInModified:
                    return 5;

                default:
                    return 6;
            }
        }
    }
}
