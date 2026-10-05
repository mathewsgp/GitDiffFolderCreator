using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDiffFolderCreator.Services;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// Writing the findings out as a file.
/// </summary>
/// <remarks>
/// The findings are the part of this tool a reader takes somewhere else — into a ticket, a
/// spreadsheet, a mail. That only works if the file opens cleanly where it lands, which is what the
/// quoting here is for: paths and sentences both contain commas, and a CSV written without quoting is
/// a CSV that Excel splits in the wrong places.
/// </remarks>
public sealed class ChangeReportWriterTests
{
    [Fact]
    public void The_csv_has_a_heading_row_and_one_row_per_finding()
    {
        ChangeVerificationResult result = Result(
            new ChangeFinding(ChangeFindingKind.DocumentedButAbsent, "src/Gone.cs", null, "No such file."),
            new ChangeFinding(ChangeFindingKind.MissingFromDocument, null, "src/New.cs", "Undeclared."));

        string[] lines = Split(ChangeReportWriter.ToCsv(result));

        Assert.Equal("Kind,Path,Documented path,Actual path,What is wrong", lines[0]);
        Assert.Equal(3, lines.Length);

        // The caption carries a comma, so it is quoted like any other field that does.
        Assert.Equal(
            "\"Listed, not present\",src/Gone.cs,src/Gone.cs,,No such file.",
            lines[1]);
    }

    [Fact]
    public void A_comma_in_a_sentence_does_not_move_it_into_the_next_column()
    {
        ChangeVerificationResult result = Result(
            new ChangeFinding(
                ChangeFindingKind.DocumentedButUnchanged,
                "src/a.cs",
                null,
                "The document lists this file, but the two sources hold identical content, as they always did."));

        string[] lines = Split(ChangeReportWriter.ToCsv(result));

        Assert.Equal(2, lines.Length);
        Assert.Contains("\"The document lists this file, but the two sources hold identical content, "
            + "as they always did.\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_quote_in_the_text_is_doubled_rather_than_ending_the_field()
    {
        ChangeVerificationResult result = Result(
            new ChangeFinding(
                ChangeFindingKind.DocumentedButAbsent,
                "src/a.cs",
                "src/b.cs",
                "It was called \"Old\" before."));

        string[] lines = Split(ChangeReportWriter.ToCsv(result));

        Assert.Contains("\"It was called \"\"Old\"\" before.\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_path_is_an_empty_field_rather_than_the_word_none()
    {
        ChangeVerificationResult result = Result(
            new ChangeFinding(ChangeFindingKind.DocumentedButAbsent, "src/Gone.cs", null, "No such file."));

        // A spreadsheet reading the word "none" gets a string where a gap should be, and a filter on
        // that column stops behaving.
        Assert.Contains(",src/Gone.cs,,No such file.", ChangeReportWriter.ToCsv(result), StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_report_leads_with_the_counts_and_then_each_finding_in_full()
    {
        ChangeVerificationResult result = Result(
            new ChangeFinding(ChangeFindingKind.DocumentedButAbsent, "src/Gone.cs", null, "No such file."));

        string text = ChangeReportWriter.ToText(result);

        Assert.Contains("1 problem found.", text, StringComparison.Ordinal);
        Assert.Contains("1. Listed, not present", text, StringComparison.Ordinal);
        Assert.Contains("Documented: src/Gone.cs", text, StringComparison.Ordinal);
        Assert.Contains("Actual:     (no such file)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agreeing_document_still_produces_a_report_saying_so()
    {
        ChangeVerificationResult result = new ChangeVerificationResult(
            ChangeVerdict.Agrees,
            new List<ChangeFinding>(),
            12,
            12,
            40,
            3,
            12);

        string text = ChangeReportWriter.ToText(result);

        // The reader has to be able to keep the evidence, and "nothing found" with no counts is not
        // evidence of anything.
        Assert.Contains("The document matches the source.", text, StringComparison.Ordinal);
        Assert.Contains("12 paths documented, 12 differences, 12 confirmed.", text, StringComparison.Ordinal);
        Assert.Contains("3 files were left out.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_extension_chooses_the_format()
    {
        using TempDirectory root = new();
        ChangeVerificationResult result = Result(
            new ChangeFinding(ChangeFindingKind.DocumentedButAbsent, "src/Gone.cs", null, "No such file."));

        string csv = root.PathFor("report.csv");
        ChangeReportWriter.Write(result, csv);

        string txt = root.PathFor("report.txt");
        ChangeReportWriter.Write(result, txt);

        Assert.StartsWith("Kind,Path,", File.ReadAllText(csv), StringComparison.Ordinal);
        Assert.Contains("Change document check", File.ReadAllText(txt), StringComparison.Ordinal);
    }

    [Fact]
    public void The_csv_carries_a_byte_order_mark_so_excel_reads_accents()
    {
        using TempDirectory root = new();
        ChangeVerificationResult result = Result(
            new ChangeFinding(
                ChangeFindingKind.DocumentedButAbsent,
                "src/Prüfung.cs",
                null,
                "No such file."));

        string csv = root.PathFor("report.csv");
        ChangeReportWriter.Write(result, csv);

        byte[] bytes = File.ReadAllBytes(csv);

        // Excel reads a UTF-8 file with no mark as the system's legacy code page, and every accented
        // character in a path becomes mojibake. Everywhere else in this application a mark is avoided;
        // this file exists to be opened in a spreadsheet, which is the one place it matters.
        Assert.True(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "The CSV should start with a UTF-8 byte order mark.");

        Assert.Contains("Prüfung.cs", File.ReadAllText(csv), StringComparison.Ordinal);
    }

    [Fact]
    public void A_written_report_is_utf8_and_readable_back()
    {
        using TempDirectory root = new();
        ChangeVerificationResult result = Result(
            new ChangeFinding(
                ChangeFindingKind.DocumentedButAbsent,
                "src/Ünicode.cs",
                null,
                "No such file."));

        string csv = root.PathFor("report.csv");
        ChangeReportWriter.Write(result, csv);

        Assert.Contains("Ünicode.cs", File.ReadAllText(csv, System.Text.Encoding.UTF8), StringComparison.Ordinal);
    }

    [Fact]
    public void No_file_name_is_refused_rather_than_writing_somewhere_unexpected()
    {
        ChangeVerificationResult result = Result();

        Assert.Throws<ArgumentException>(() => ChangeReportWriter.Write(result, "  "));
        Assert.Throws<ArgumentNullException>(() => ChangeReportWriter.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => ChangeReportWriter.ToText(null!));
    }

    private static ChangeVerificationResult Result(params ChangeFinding[] findings) =>
        new ChangeVerificationResult(
            findings.Length == 0 ? ChangeVerdict.Agrees : ChangeVerdict.Disagrees,
            findings,
            1,
            findings.Length,
            5,
            0,
            0);

    private static string[] Split(string csv) =>
        csv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
}