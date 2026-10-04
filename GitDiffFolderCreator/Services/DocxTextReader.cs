using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Xml;

namespace GitDiffFolderCreator.Services
{
    /// <summary>
    /// Reads the text out of a Word document, keeping one entry per paragraph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>.docx</c> is a zip holding XML, and the prose lives in <c>word/document.xml</c> as
    /// paragraphs of runs of text. Reading it means opening the package and walking that XML, which
    /// is why <see cref="System.IO.Packaging"/> is used rather than a zip reader directly: it is the
    /// same API Office itself uses, so part naming and content-type handling come for free, and it is
    /// already referenced by WPF.
    /// </para>
    /// <para>
    /// Line structure is the point. A document written for this purpose puts one file per bullet or
    /// per table row, so the paragraph and the table row have to survive as separate entries for the
    /// list to be recoverable. A reader that flattened the file into one string would turn a list of
    /// forty paths into forty sentences with no boundaries.
    /// </para>
    /// <para>
    /// Not handled: headers and footers, footnotes, comments, and text in embedded objects. Those are
    /// in other parts of the package, and a change document puts its file list in the body.
    /// </para>
    /// </remarks>
    public static class DocxTextReader
    {
        private const string WordNamespace =
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        /// <summary>
        /// The document's body text, one entry per paragraph or table row.
        /// </summary>
        /// <exception cref="ChangeDocumentException">The file is missing, is not a Word
        /// document, or cannot be read.</exception>
        public static IList<string> ReadLines(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ChangeDocumentException("No document was chosen.");
            }

            if (!File.Exists(path))
            {
                throw new ChangeDocumentException(
                    "The document was not found: " + path);
            }

            Package package;

            try
            {
                package = Package.Open(path, FileMode.Open, FileAccess.Read);
            }
            catch (Exception ex) when (IsUnreadable(ex))
            {
                // Not a package at all, or a package this build cannot open - an old binary .doc
                // saved under a .docx name, or a file still open in Word.
                throw new ChangeDocumentException(
                    "The file could not be opened as a Word document. Save it as .docx, not .doc."
                        + Environment.NewLine + ex.Message);
            }

            using (package)
            {
                PackagePart? part = FindBody(package);

                if (part == null)
                {
                    throw new ChangeDocumentException(
                        "The file is not a Word document: it has no word/document.xml. A .doc saved "
                        + "with a .docx name looks like this.");
                }

                try
                {
                    using Stream stream = part.GetStream(FileMode.Open, FileAccess.Read);
                    return ReadBody(stream);
                }
                catch (XmlException ex)
                {
                    throw new ChangeDocumentException(
                        "The document's markup could not be read: " + ex.Message);
                }
            }
        }

        /// <summary>The body part, or null when the package has none.</summary>
        private static PackagePart? FindBody(Package package)
        {
            Uri? uri = PackUriHelper.CreatePartUri(new Uri("/word/document.xml", UriKind.Relative));

            return package.PartExists(uri) ? package.GetPart(uri) : null;
        }

        /// <summary>
        /// Walks the body markup and yields one string per paragraph or table row.
        /// </summary>
        /// <remarks>
        /// Table cells are joined with a tab rather than concatenated, so a row of
        /// <c>path | description</c> stays one entry and the path stays separable from the prose
        /// beside it.
        /// </remarks>
        private static IList<string> ReadBody(Stream stream)
        {
            var lines = new List<string>();
            var current = new StringBuilder();

            // How deep the reader is inside tables. A paragraph inside a cell must not end an entry:
            // the row is the entry, and flushing per paragraph turns a two-column row into two lines.
            int tableDepth = 0;

            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                IgnoreWhitespace = false,
                IgnoreComments = true,
                DtdProcessing = DtdProcessing.Prohibit,

                // A document from the internet can carry an external entity reference; resolving it
                // would read files this application has no business reading.
                XmlResolver = null,
            });

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "t":
                            current.Append(reader.ReadElementContentAsString());
                            break;

                        case "tab":
                            current.Append('\t');
                            break;

                        case "br":
                        case "cr":
                            current.Append('\n');
                            break;

                        case "tbl":
                            tableDepth++;
                            break;

                        case "tc":
                            // A cell boundary, so the path in one cell does not run into the
                            // description in the next.
                            if (current.Length > 0 && current[current.Length - 1] != '\t')
                            {
                                current.Append('\t');
                            }

                            break;

                        case "p":
                            if (tableDepth == 0)
                            {
                                Flush(lines, current);
                            }

                            break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    switch (reader.LocalName)
                    {
                        case "tbl":
                            tableDepth--;
                            break;

                        case "p":
                            // The start tag already flushed; Word writes <w:p/> for an empty paragraph,
                            // and an empty list item is a real entry that must not be dropped.
                            if (tableDepth == 0 && current.Length > 0)
                            {
                                Flush(lines, current);
                            }

                            break;

                        case "tr":
                            Flush(lines, current);
                            break;
                    }
                }
            }

            Flush(lines, current);

            return lines;
        }

        /// <summary>
        /// Moves the accumulated text into the result as one entry.
        /// </summary>
        /// <remarks>
        /// Each entry is split on any embedded newline as well, because a Word line break inside one
        /// paragraph is a line to the person who wrote it even though it is not a line to Word.
        /// </remarks>
        private static void Flush(IList<string> lines, StringBuilder current)
        {
            if (current.Length == 0)
            {
                return;
            }

            string text = current.ToString();
            current.Length = 0;

            foreach (string piece in text.Split('\n'))
            {
                lines.Add(piece.TrimEnd('\r', '\t', ' '));
            }
        }

        /// <summary>Whether an exception means "this file is not what it claims to be".</summary>
        private static bool IsUnreadable(Exception ex) =>
            ex is IOException
            || ex is UnauthorizedAccessException
            || ex is InvalidDataException
            || ex is ArgumentException
            || ex is FormatException;
    }

    /// <summary>
    /// A problem with the change document itself, as opposed to a mismatch found between the document
    /// and the source folders.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DiffToolLauncher"/>'s failure type and from a verification result,
    /// because "the document could not be opened" and "the document disagrees with the source" are
    /// different answers and the window has to be able to say which happened.
    /// </remarks>
    public sealed class ChangeDocumentException : Exception
    {
        public ChangeDocumentException(string message)
            : base(message)
        {
        }

        public ChangeDocumentException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}