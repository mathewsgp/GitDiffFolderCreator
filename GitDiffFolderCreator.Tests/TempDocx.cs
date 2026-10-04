using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Xml;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// Builds real <c>.docx</c> files for the reader tests.
/// </summary>
/// <remarks>
/// A real package, written the way Word writes one, rather than a file with a <c>.docx</c> extension
/// and some text in it. The reader goes through <see cref="Package"/> and looks the body part up by
/// URI, so a hand-rolled file with the right bytes but the wrong shape would test the fixture rather
/// than the reader — and the whole point of the reader is that it copes with Word's actual structure.
/// </remarks>
internal static class TempDocx
{
    private const string WordNamespace =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const string DocumentContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    private const string DocumentRelationshipType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    /// <summary>A document of plain paragraphs.</summary>
    public static string Create(string path, IEnumerable<string> paragraphs)
    {
        var body = new StringBuilder();

        foreach (string paragraph in paragraphs)
        {
            // A paragraph whose text is split across two runs, because Word does that routinely and a
            // reader that keeps only the first run would silently lose half of every bolded path.
            int split = paragraph.Length / 2;

            body.Append("<w:p><w:r><w:t xml:space=\"preserve\">")
                .Append(Escape(paragraph.Substring(0, split)))
                .Append("</w:t></w:r><w:r><w:t xml:space=\"preserve\">")
                .Append(Escape(paragraph.Substring(split)))
                .Append("</w:t></w:r></w:p>");
        }

        return Write(path, body.ToString());
    }

    /// <summary>A document holding a table, one entry per row.</summary>
    public static string CreateWithRows(string path, IEnumerable<string[]> rows)
    {
        var body = new StringBuilder("<w:tbl>");

        foreach (string[] row in rows)
        {
            body.Append("<w:tr>");

            foreach (string cell in row)
            {
                body.Append("<w:tc><w:p><w:r><w:t xml:space=\"preserve\">")
                    .Append(Escape(cell))
                    .Append("</w:t></w:r></w:p></w:tc>");
            }

            body.Append("</w:tr>");
        }

        body.Append("</w:tbl>");

        return Write(path, body.ToString());
    }

    /// <summary>An empty document, for the "there is nothing listed" case.</summary>
    public static string CreateEmpty(string path) =>
        Write(path, "<w:p><w:r><w:t xml:space=\"preserve\"></w:t></w:r></w:p>");

    private static string Write(string path, string bodyContent)
    {
        Uri documentUri = PackUriHelper.CreatePartUri(new Uri("/word/document.xml", UriKind.Relative));

        using Package package = Package.Open(path, FileMode.Create, FileAccess.ReadWrite);

        PackagePart part = package.CreatePart(documentUri, DocumentContentType, CompressionOption.Normal);

        using (Stream stream = part.GetStream(FileMode.Create, FileAccess.Write))
        {
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
            };

            using XmlWriter writer = XmlWriter.Create(stream, settings);

            writer.WriteStartDocument();
            writer.WriteStartElement("w", "document", WordNamespace);
            writer.WriteStartElement("w", "body", WordNamespace);
            writer.WriteRaw(bodyContent);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        // Word will not open a package with no relationship to its body, so the fixture is a document
        // rather than a zip that happens to hold one.
        package.CreateRelationship(
            documentUri,
            TargetMode.Internal,
            DocumentRelationshipType);

        package.Flush();

        return path;
    }

    private static string Escape(string text)
    {
        var settings = new XmlWriterSettings { ConformanceLevel = ConformanceLevel.Fragment };
        var output = new StringWriter();

        using (XmlWriter writer = XmlWriter.Create(output, settings))
        {
            writer.WriteString(text);
        }

        return output.ToString();
    }
}