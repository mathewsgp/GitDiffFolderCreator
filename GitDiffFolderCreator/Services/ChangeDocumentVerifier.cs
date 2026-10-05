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

        /// <summary>In the base folder and gone from the modified one.</summary>
        OnlyInBase,

        /// <summary>Only in the modified folder.</summary>
        OnlyInModified,

        /// <summary>The document lists the same path twice.</summary>
        ListedTwice,
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

                case ChangeFindingKind.OnlyInBase:
                    return "Deleted, not listed";

                case ChangeFindingKind.OnlyInModified:
                    return "Added, not listed";

                case ChangeFindingKind.ListedTwice:
                    return "Listed twice";

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
            //
            // A file that moved is not paired up with the file it was: it is a deletion at the old path
            // and an addition at the new one, and both are taken at face value. A document that lists
            // only one side of the move has then left the other side undeclared, which is the truth -
            // pairing the two would decide on the reader's behalf which one they meant.
            var changed = new Dictionary<string, FolderDifference>(StringComparer.OrdinalIgnoreCase);

            foreach (FolderDifference difference in comparison.Differences)
            {
                changed[difference.PrimaryPath] = difference;
            }

            int matches = 0;
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Where each real file was first claimed, per section. Keyed by section because a document may
            // repeat the list - one section per module - and the same file in two of them is that
            // document's normal way of writing itself, not an error. Only two claims inside one section
            // are the same claim made twice.
            var matchedIn = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);

            // The same, for claims that name nothing: two sections may each list a file that is in neither
            // source, and neither of them is a finding about the other.
            var claimedIn = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (DocumentedFile file in documented)
            {
                if (!ClaimedInSection(claimedIn, file))
                {
                    findings.Add(new ChangeFinding(
                        ChangeFindingKind.ListedTwice,
                        file.Path,
                        null,
                        "The document lists this path more than once, on lines including "
                            + file.LineNumber.ToString(CultureInfo.InvariantCulture) + "."));

                    continue;
                }

                // The claim as the document spells it. The comparison reads it from the right, so a
                // document that wrote the path from part way down still names the file - what it got
                // wrong at the front is dropped once the file is found.
                string? key = Resolve(file.Path, changed.Keys);

                if (key != null)
                {
                    if (FirstLineIn(matchedIn, key, file.Section) is int firstLine)
                    {
                        // Two spellings of one file inside one section: the document contradicts itself
                        // about where the file is.
                        findings.Add(new ChangeFinding(
                            ChangeFindingKind.ListedTwice,
                            file.Path,
                            key,
                            "This line and line "
                                + firstLine.ToString(CultureInfo.InvariantCulture)
                                + " both name the same file, " + key + "."));

                        continue;
                    }

                    // A file already accounted for in an earlier section is left alone rather than
                    // counted again: it is one change, however many sections name it.
                    if (!matched.Contains(key))
                    {
                        matched.Add(key);
                        matches++;
                    }

                    if (!matchedIn.TryGetValue(key, out Dictionary<int, int>? bySection))
                    {
                        bySection = new Dictionary<int, int>();
                        matchedIn[key] = bySection;
                    }

                    bySection[file.Section] = file.LineNumber;
                }
                else if (!IsAccountedFor(matched, changed, file))
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
        /// Whether this claim names something this section has already named, and records that it has.
        /// </summary>
        /// <remarks>
        /// Scoped to the section on purpose. Two lists of the same file in two sections is a document
        /// written as one list per module, and reporting it would fill the findings with a complaint
        /// about a document that is entirely correct. Two of them in one section is a duplicated entry,
        /// which is worth saying.
        /// </remarks>
        private static bool ClaimedInSection(
            Dictionary<string, HashSet<int>> claimedIn,
            DocumentedFile file)
        {
            if (!claimedIn.TryGetValue(file.Path, out HashSet<int>? sections))
            {
                sections = new HashSet<int>();
                claimedIn[file.Path] = sections;
            }

            return sections.Add(file.Section);
        }

        /// <summary>The line this section first named the file on, or null if it has not named it yet.</summary>
        private static int? FirstLineIn(
            Dictionary<string, Dictionary<int, int>> matchedIn,
            string key,
            int section)
        {
            return matchedIn.TryGetValue(key, out Dictionary<int, int>? bySection)
                && bySection.TryGetValue(section, out int line)
                    ? line
                    : null;
        }

        /// <summary>
        /// Whether a claim names something an earlier line already accounted for.
        /// </summary>
        private static bool IsAccountedFor(
            HashSet<string> matched,
            Dictionary<string, FolderDifference> changed,
            DocumentedFile file)
        {
            string? key = Resolve(file.Path, changed.Keys);

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
        /// Several files can end with the same claim, and that is ordinary: two folders in the tree each
        /// carrying a copy of <c>api/Reader.py</c>, or a document listing one file once per section. The
        /// first by name is taken. An answer that moved about with the order the walk happened to reach
        /// files in would read as a change in the verdict rather than as a tie-break.
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

                if (hit == null || string.Compare(path, hit, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    hit = path;
                }
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
        /// Why a documented path found no difference: it may be identical in both folders, or absent from
        /// the modified one entirely — and those are two different answers.
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

            return new ChangeFinding(
                ChangeFindingKind.DocumentedButAbsent,
                file.Path,
                null,
                "The document lists this file, but there is no such file in the modified source.");
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

                case ChangeFindingKind.DocumentedButAbsent:
                    return 1;

                case ChangeFindingKind.DocumentedButUnchanged:
                    return 2;

                case ChangeFindingKind.MissingFromDocument:
                    return 3;

                case ChangeFindingKind.OnlyInBase:
                case ChangeFindingKind.OnlyInModified:
                    return 4;

                default:
                    return 5;
            }
        }
    }
}
