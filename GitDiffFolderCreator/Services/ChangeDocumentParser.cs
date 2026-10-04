using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GitDiffFolderCreator.Services
{
    /// <summary>One path the document claims was modified, and where the claim was.</summary>
    public sealed class DocumentedFile
    {
        public DocumentedFile(string path, int lineNumber, string originalText, int section = 1)
        {
            Path = path;
            LineNumber = lineNumber;
            OriginalText = originalText;
            Section = section;
        }

        /// <summary>
        /// The path as this tool reads it: forward slashes, and no leading or trailing separator or
        /// whitespace.
        /// </summary>
        /// <remarks>
        /// This is the form <see cref="Normalize"/> produces and the form the comparison matches in, so
        /// what is reported back to the reader is the path they can act on rather than the path they
        /// happened to type.
        /// </remarks>
        public string Path { get; }

        /// <summary>The entry in the document this came from, counting from 1.</summary>
        public int LineNumber { get; }

        /// <summary>The document's own text, kept so a finding can quote what was actually written.</summary>
        public string OriginalText { get; }

        /// <summary>
        /// Which occurrence of the opening heading this claim came from, counting from 1.
        /// </summary>
        /// <remarks>
        /// A document may repeat the section — one list per module, per phase — and the same file
        /// legitimately appears in two of them. This is what tells a claim repeated across two sections,
        /// which says nothing the reader did not already know, from one repeated inside a single section,
        /// which is a duplicated entry.
        /// </remarks>
        public int Section { get; }

        public override string ToString() =>
            Path + " (line " + LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// Pulls the list of modified files out of a change document: everything between the
    /// <c>[Modified Files]</c> heading and the <c>[Status]</c> heading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document is read as what it is — a document with a section in it — rather than as prose to
    /// be mined for anything path-shaped. Two headings bound the section; between them every run
    /// carrying a slash is a path. Blank lines, numbering, bullets and sentences of commentary in
    /// between are not errors: they are skipped, and the section carries on.
    /// </para>
    /// <para>
    /// Which two headings those are is the reader's choice, because change documents are written by
    /// hand and by no two teams alike; <see cref="DefaultStartMarker"/> and <see cref="DefaultEndMarker"/>
    /// are what is used when nothing else is named.
    /// </para>
    /// <para>
    /// This is a deliberate simplification. Reading a whole document for anything path-shaped found
    /// more than it should — a URL, a version range, half a sentence — and every one of those needed a
    /// rule explaining why it was not a path. A section bounded by two headings needs none: a path in
    /// that section is a path, and a path elsewhere in the document is not part of the list.
    /// </para>
    /// <para>
    /// The one rule inside the section is that a path carries at least one slash. A bare word is
    /// therefore not a path here: <c>Parser.cs</c> and <c>Dockerfile</c> are left out, and a list that
    /// means to include them has to write the folder they are in. That is the trade for not having to
    /// guess whether a word is a file name or an English word. The file name may not contain a space
    /// either, which is what keeps a sentence of commentary that happens to include a slash out.
    /// </para>
    /// </remarks>
    public static class ChangeDocumentParser
    {
        /// <summary>
        /// The heading that opens the list, for a document that does not name its own.
        /// </summary>
        /// <remarks>
        /// The brackets are part of how the heading is written rather than part of what it says: the
        /// comparison strips them from both sides, so <c>Modified Files</c> and <c>[Modified Files]:</c>
        /// open the same section. They are written here because that is how the documents this was written
        /// against spell it, so the default reads the same as the document does.
        /// </remarks>
        public const string DefaultStartMarker = "[Modified Files]";

        /// <summary>
        /// The heading that closes it, for a document that does not name its own. Everything after this
        /// belongs to something else.
        /// </summary>
        public const string DefaultEndMarker = "[Status]";

        /// <summary>
        /// Where one path ends and the next begins on a line: a tab, or a run of two or more spaces.
        /// </summary>
        /// <remarks>
        /// Two or more, and not one, because a folder name may contain a single space —
        /// <c>TestAuto Layer/api/Reader.py</c> is one path and stopping at the space would leave
        /// <c>Layer/api/Reader.py</c>, which names a different thing. The layouts this has to read put
        /// real gaps between paths — a numbered list's tab, a table's padding — so the wider gap is
        /// the separator and the narrow one is part of a name.
        /// </remarks>
        private static readonly Regex PathSeparator = new Regex("[ \t]{2,}|\t", RegexOptions.CultureInvariant);

        /// <summary>
        /// A list marker in front of a path: <c>1.</c>, <c>12)</c>, <c>(3)</c>, or a bullet.
        /// </summary>
        /// <remarks>
        /// Stripped rather than merely ignored. A marker followed by a gap is already a separate piece
        /// and has no slash, so it would be rejected anyway; this is for the author who typed
        /// <c>1.\Src\Parser.cs</c> with nothing between, where the number would otherwise become the
        /// first folder of the path.
        /// </remarks>
        private static readonly Regex ListMarker = new Regex(
            @"^(?:\(?\d+[.)]\)?|[\u2022\u2023\u25AA\u2043*+\-])\s*",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// The documented paths, in the order they appear, with duplicates left in place.
        /// </summary>
        /// <remarks>
        /// Duplicates are kept because a document that lists the same file twice is itself worth
        /// reporting; collapsing them here would hide the one thing the reader would want to fix.
        /// </remarks>
        public static IList<DocumentedFile> Parse(IEnumerable<string> lines)
        {
            return Parse(lines, DefaultStartMarker, DefaultEndMarker);
        }

        /// <summary>
        /// The documented paths between the two headings the reader named, in every section they bound.
        /// </summary>
        /// <remarks>
        /// A blank marker names no heading: a blank opening one reads as nothing at all, and a blank
        /// closing one leaves the list running to the end of the document. Both are answers rather than
        /// failures, so nothing here needs to refuse them.
        /// </remarks>
        public static IList<DocumentedFile> Parse(
            IEnumerable<string> lines,
            string? startMarker,
            string? endMarker)
        {
            var found = new List<DocumentedFile>();

            if (lines == null)
            {
                return found;
            }

            string? start = HeadingKey(startMarker);
            string? end = HeadingKey(endMarker);

            bool inSection = false;
            int number = 0;
            int section = 0;

            foreach (string line in lines)
            {
                number++;

                if (start != null && IsHeading(line, start))
                {
                    // Opening, not "the first one". A document may repeat the section — one per module,
                    // one per phase — and the paths in the second are as much part of the list as the
                    // first's; reading only the first would report every file in the second as a change
                    // nobody documented. Each opening is its own section, so the claims in it can be told
                    // apart from the same claim made in another.
                    section++;
                    inSection = true;
                    continue;
                }

                if (inSection && end != null && IsHeading(line, end))
                {
                    // Closing, not finishing: whatever comes between this and the next opening heading is
                    // another section, and the scan carries on to reach it.
                    inSection = false;
                    continue;
                }

                if (!inSection || string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                foreach (string path in PathsOn(line!))
                {
                    found.Add(new DocumentedFile(path, number, line!.Trim(), section));
                }
            }

            return found;
        }

        /// <summary>
        /// Whether a line is the named heading, with or without the brackets and the colon an author may
        /// have typed around it, and with or without whatever they wrote after it.
        /// </summary>
        /// <remarks>
        /// The heading has to be at the <em>start</em> of the line, which is what keeps
        /// "the Modified Files section lists…" — a sentence that happens to contain the words — from
        /// opening the section in the wrong place. What follows the heading is not compared: an author
        /// who writes <c>[Status] - all green</c> has written the heading, and requiring the line to end
        /// there would read their document as having no section in it at all.
        /// </remarks>
        private static bool IsHeading(string? line, string key)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            // Only the leading punctuation is stripped. Trailing brackets belong to whatever the author
            // wrote after the heading — "Modified Files]" is the heading followed by a closing bracket,
            // not the heading written twice.
            string text = line!.Trim().TrimStart('[', ']', ':', ' ').Trim();

            return text.StartsWith(key, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Puts a configured marker into the one form a line is compared against, or answers null for
        /// anything that is not a heading.
        /// </summary>
        /// <remarks>
        /// Both ends are stripped, because the marker is typed once in a box rather than in a document:
        /// <c>[Modified Files]</c> and <c>Modified Files:</c> are one heading written two ways. A marker
        /// of nothing but brackets or a colon is not a heading and is refused here rather than matching
        /// whatever line begins with punctuation.
        /// </remarks>
        private static string? HeadingKey(string? marker)
        {
            if (string.IsNullOrWhiteSpace(marker))
            {
                return null;
            }

            string key = marker!.Trim().Trim('[', ']', ':', ' ').Trim();

            return key.Length == 0 ? null : key;
        }

        /// <summary>Every path on one line, in the order they appear.</summary>
        private static IEnumerable<string> PathsOn(string line)
        {
            foreach (string piece in PathSeparator.Split(line))
            {
                string? path = Normalize(ListMarker.Replace(piece.Trim(), string.Empty).Trim());

                if (path != null)
                {
                    yield return path;
                }
            }
        }

        /// <summary>
        /// Puts a path into the one form everything downstream compares, and answers null for anything
        /// that is not one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Four things happen, and each answers a way the two sides can disagree for no good reason.
        /// Separators are unified, because a document written in Word on Windows spells a path with
        /// backslashes and the folder walk spells it with slashes. Whitespace and surrounding
        /// separators are trimmed, because <c>\Src\Parser.cs</c> and <c>Src\Parser.cs</c> are the same
        /// path written from two different starting points. A leading <c>./</c> is dropped, because it
        /// is a way of saying "here" rather than a folder.
        /// </para>
        /// <para>
        /// What is left has to carry a separator, and its file name may not carry a space. That is the
        /// whole of what a path is here: the first rule keeps a bare word out, the second keeps a
        /// sentence that happens to contain a slash out. A URL is rejected as well — its slashes would
        /// pass both, and it is the one token whose shape really is a path's.
        /// </para>
        /// <para>
        /// Used by both sides of the comparison. The claim is normalised on the way in and the real
        /// path on the way out, so neither side can be right by accident.
        /// </para>
        /// </remarks>
        public static string? Normalize(string? text)
        {
            if (text == null)
            {
                return null;
            }

            string normalised = text.Trim().Replace('\\', '/');

            if (normalised.Length == 0)
            {
                return null;
            }

            if (normalised.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                return null;
            }

            while (normalised.StartsWith("./", StringComparison.Ordinal))
            {
                normalised = normalised.Substring(2);
            }

            normalised = normalised.Trim('/');

            // The rule the whole section rests on: a path has a folder in it. A bare word is a name at
            // best and a comment at worst, and there is no way to tell the two apart from the text -
            // which is why a list that means to name a file at the root has to write more of its path.
            if (normalised.Length == 0 || normalised.IndexOf('/') < 0)
            {
                return null;
            }

            // And the file name itself may not carry a space, which is what keeps a sentence inside the
            // section - "and/or the notes below were changed too" - from being read as one very long
            // path. A space in a *folder* name is still fine, because that is not the last segment.
            int lastSeparator = normalised.LastIndexOf('/');

            for (int i = lastSeparator + 1; i < normalised.Length; i++)
            {
                if (char.IsWhiteSpace(normalised[i]))
                {
                    return null;
                }
            }

            return normalised;
        }
    }
}
