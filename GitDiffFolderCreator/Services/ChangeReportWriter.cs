using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GitDiffFolderCreator.Services
{
    /// <summary>
    /// Writes the findings out as a file, so the list can be worked through somewhere other than the
    /// window it was found in.
    /// </summary>
    /// <remarks>
    /// The findings are the part of this tool a reader wants to act on somewhere else — pasted into a
    /// ticket, ticked off in a spreadsheet, sent to whoever wrote the document. Re-typing them from a
    /// window is where the transcription errors come from.
    /// </remarks>
    public static class ChangeReportWriter
    {
        private static readonly string[] CsvHeader =
        {
            "Kind", "Path", "Documented path", "Actual path", "What is wrong",
        };

        /// <summary>
        /// The findings as CSV, with a heading row.
        /// </summary>
        /// <remarks>
        /// Quoting follows RFC 4180: a field containing a comma, a quote or a newline is wrapped in
        /// quotes and its own quotes are doubled. Paths and sentences both routinely contain commas,
        /// so a file written without this is a file Excel splits in the wrong places.
        /// </remarks>
        public static string ToCsv(ChangeVerificationResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            var text = new StringBuilder();

            for (int i = 0; i < CsvHeader.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(CsvHeader[i]);
            }

            text.Append("\r\n");

            foreach (ChangeFinding finding in result.Findings)
            {
                text.Append(Field(ChangeFinding.Describe(finding.Kind))).Append(',')
                    .Append(Field(finding.DisplayPath)).Append(',')
                    .Append(Field(finding.DocumentedPath)).Append(',')
                    .Append(Field(finding.ActualPath)).Append(',')
                    .Append(Field(finding.Detail))
                    .Append("\r\n");
            }

            return text.ToString();
        }

        /// <summary>
        /// The findings as plain text: a heading per finding, then its paths.
        /// </summary>
        /// <remarks>
        /// The shape a person reads rather than a shape a spreadsheet reads. A path is on its own line
        /// so it can be selected and pasted whole, which is the thing being done with it.
        /// </remarks>
        public static string ToText(ChangeVerificationResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            var text = new StringBuilder();

            text.Append("Change document check").Append("\r\n");
            text.Append("=====================").Append("\r\n");
            text.Append(result.Agrees
                ? "The document matches the source."
                : Count(result.Findings.Count, "problem") + " found.")
                .Append("\r\n\r\n");

            text.Append(Count(result.DocumentedCount, "path") + " documented, ")
                .Append(Count(result.ActualDifferenceCount, "difference") + ", ")
                .Append(result.Matches.ToString(CultureInfo.CurrentCulture) + " confirmed.")
                .Append("\r\n");

            text.Append(Count(result.FilesCompared, "file") + " present in both folders were compared")
                .Append(result.FilesIgnored > 0
                    ? "; " + Count(result.FilesIgnored, "file") + " were left out."
                    : ".")
                .Append("\r\n\r\n");

            if (result.Findings.Count == 0)
            {
                return text.ToString();
            }

            int number = 1;

            foreach (ChangeFinding finding in result.Findings)
            {
                text.Append(number.ToString(CultureInfo.CurrentCulture) + ". ")
                    .Append(ChangeFinding.Describe(finding.Kind))
                    .Append("\r\n");

                text.Append("   Documented: ")
                    .Append(finding.DocumentedPath ?? "(not mentioned)")
                    .Append("\r\n");

                text.Append("   Actual:     ")
                    .Append(finding.ActualPath ?? "(no such file)")
                    .Append("\r\n");

                text.Append("   ").Append(finding.Detail).Append("\r\n\r\n");

                number++;
            }

            return text.ToString();
        }

        /// <summary>
        /// Writes the findings to the given path, choosing the format from the extension.
        /// </summary>
        /// <remarks>
        /// A byte-order mark is written, unlike everywhere else in this application. This file is going
        /// into a spreadsheet, and Excel reads a UTF-8 file without a mark as the system's legacy code
        /// page, which turns any accented character in a path into mojibake.
        /// </remarks>
        public static void Write(ChangeVerificationResult result, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A file name is required.", nameof(path));
            }

            bool text = string.Equals(
                Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(
                path,
                text ? ToText(result) : ToCsv(result),
                new UTF8Encoding(true));
        }

        private static string Field(string? value)
        {
            if (value is not { Length: > 0 })
            {
                return string.Empty;
            }

            bool needsQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;

            return needsQuotes
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        private static string Count(int number, string noun) =>
            number.ToString(CultureInfo.CurrentCulture)
                + " "
                + noun
                + (number == 1 ? string.Empty : "s");
    }
}