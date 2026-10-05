using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// Reading the text out of a Word document, and pulling the file list out of it.
/// </summary>
/// <remarks>
/// The document is prose with a list inside it, so the reader's job is to keep the list's structure
/// and the parser's is to find the list without taking the prose for it. Both are tested against real
/// packages, because the reader's whole claim is that it understands Word's markup.
/// </remarks>
public sealed class ChangeDocumentReaderTests
{
    // ------------------------------------------------------------------ the reader

    [Fact]
    public void A_document_is_read_one_entry_per_paragraph()
    {
        using TempDirectory folder = new();
        string path = folder.PathFor("changes.docx");

        TempDocx.Create(path, new[]
        {
            "Change summary",
            "The parser was rewritten.",
            "src/Parser.cs",
        });

        IList<string> lines = DocxTextReader.ReadLines(path);

        // The prose is kept, not filtered: deciding what is a path is the parser's job, and a reader
        // that dropped prose would make the parser's job impossible to reason about.
        Assert.Equal(3, lines.Count);
        Assert.Equal("The parser was rewritten.", lines[1]);

        // Word splits a paragraph's text across runs routinely, and a reader that kept only the first
        // run would silently lose half of every line.
        Assert.Equal("src/Parser.cs", lines[2]);
    }

    [Fact]
    public void A_table_row_is_one_entry_with_its_cells_kept_apart()
    {
        using TempDirectory folder = new();
        string path = folder.PathFor("changes.docx");

        TempDocx.CreateWithRows(path, new[]
        {
            new[] { "File", "Change" },
            new[] { "src/Parser.cs", "Rewritten" },
            new[] { "src/Reader.cs", "Added" },
        });

        IList<string> lines = DocxTextReader.ReadLines(path);

        // A row is one entry, or a list of two files becomes four lines and the description beside a
        // path is read as a path of its own.
        Assert.Equal(3, lines.Count);
        Assert.Equal("File\tChange", lines[0]);
        Assert.Equal("src/Parser.cs\tRewritten", lines[1]);
    }

    [Fact]
    public void A_file_that_is_not_a_word_document_says_so_rather_than_failing_obscurely()
    {
        using TempDirectory folder = new();
        string path = folder.WriteFile("notes.txt", "src/Parser.cs");

        ChangeDocumentException error =
            Assert.Throws<ChangeDocumentException>(() => DocxTextReader.ReadLines(path));

        // The message has to name the problem and the fix, because the usual cause is a .doc saved
        // under a .docx name and the user cannot see that from here.
        Assert.Contains("docx", error.Message);
    }

    [Fact]
    public void A_missing_document_says_which_path_was_missing()
    {
        using TempDirectory folder = new();

        ChangeDocumentException error = Assert.Throws<ChangeDocumentException>(
            () => DocxTextReader.ReadLines(folder.PathFor("nowhere.docx")));

        Assert.Contains("nowhere.docx", error.Message);
    }

    [Fact]
    public void An_empty_document_reads_as_nothing_rather_than_as_a_failure()
    {
        using TempDirectory folder = new();
        string path = folder.PathFor("empty.docx");

        TempDocx.CreateEmpty(path);

        Assert.Empty(DocxTextReader.ReadLines(path));
    }

    // ------------------------------------------------------------------ the parser

    /// <summary>
    /// The section is read as a section: everything between the two headings, and nothing outside it.
    /// </summary>
    /// <remarks>
    /// The two layouts below are the ones this has to read, and they are the same list written by hand
    /// and by Word's numbered list. Numbering, brackets and indentation are all incidental: a run
    /// carrying a slash between the headings is a path, whatever is around it.
    /// </remarks>
    [Fact]
    public void A_numbered_list_between_the_headings_is_the_list()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "Change summary",
            string.Empty,
            "[Modified Files]:",
            " 1.      \\Src\\Module1\\View2\\Source\\View2\\Frames\\2222.xaml.cs",
            " 2.      \\Src\\Module1\\View2\\Source\\View2\\Frames\\24dsds.xaml.cs",
            " 3.      \\Src\\Module1\\View2\\Source\\View2\\Frames\\xdsf.xaml.cs",
            " 4.      \\Src\\Infrastructure\\32fws\\wrwe.cs",
            " 5.      \\Src\\Infrastructure\\w322\\wewer.xaml.cs",
            string.Empty,
            "[Status]",
            " 1.      Passed",
        });

        // Separators unified and the leading one dropped, so the paths arrive in the form the comparison
        // matches in and the number never becomes the first folder.
        Assert.Equal(
            new[]
            {
                "Src/Module1/View2/Source/View2/Frames/2222.xaml.cs",
                "Src/Module1/View2/Source/View2/Frames/24dsds.xaml.cs",
                "Src/Module1/View2/Source/View2/Frames/xdsf.xaml.cs",
                "Src/Infrastructure/32fws/wrwe.cs",
                "Src/Infrastructure/w322/wewer.xaml.cs",
            },
            documented.Select(file => file.Path));
    }

    /// <summary>
/// The same list with no numbering, and two paths on one line.
    /// </summary>
    /// <remarks>
    /// A line is not one path. A gap of a tab or of two or more spaces separates two paths, which is how
    /// a document written in a fixed-width editor lays a long list out.
    /// </remarks>
    [Fact]
    public void An_unnumbered_list_between_the_headings_is_the_list_and_two_paths_may_share_a_line()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            "      \\Src\\Module1\\View2\\Source\\View2\\Frames\\2222.xaml.cs",
            "      \\Src\\Module1\\View2\\Source\\View2\\Frames\\24dsds.xaml.cs       \\Src\\Module1\\View2\\Source\\View2\\Frames\\xdsf.xaml.cs",
            "      \\Src\\Infrastructure\\32fws\\wrwe.cs",
            "      \\Src\\Infrastructure\\w322\\wewer.xaml.cs",
            "[Status]",
            "Passed",
        });

        Assert.Equal(
            new[]
            {
                "Src/Module1/View2/Source/View2/Frames/2222.xaml.cs",
                "Src/Module1/View2/Source/View2/Frames/24dsds.xaml.cs",
                "Src/Module1/View2/Source/View2/Frames/xdsf.xaml.cs",
                "Src/Infrastructure/32fws/wrwe.cs",
                "Src/Infrastructure/w322/wewer.xaml.cs",
            },
            documented.Select(file => file.Path));
    }

    /// <summary>
    /// Blank lines and commentary inside the section are skipped, and the list carries on past them.
    /// </summary>
    /// <remarks>
    /// The section is not a run of consecutive path lines: an author leaves a blank line under a
    /// heading and writes a sentence explaining what changed. Ending the list at the first line without
    /// a path would silently drop everything after it, so the only thing that ends it is the closing
    /// heading.
    /// </remarks>
    [Fact]
    public void Blank_lines_and_commentary_inside_the_section_are_skipped_and_the_list_carries_on()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            "src/Parser.cs",
            string.Empty,
            "The parser was rewritten; the reader was not touched.",
            " 1. src/Reader.cs",
            string.Empty,
            string.Empty,
            "and/or the notes below were changed as well",
            "src\\Views\\MainWindow.xaml",
            "[Status]",
        });

        // "and/or" carries slashes and is not a path: it is the one shape that looks like one and is not.
        // It is rejected because the section is the boundary and the run is commentary, not because of
        // how it reads.
        Assert.Equal(
            new[] { "src/Parser.cs", "src/Reader.cs", "src/Views/MainWindow.xaml" },
            documented.Select(file => file.Path));
    }

    /// <summary>Nothing outside the section is part of the list.</summary>
    [Fact]
    public void A_path_before_the_opening_heading_is_not_part_of_the_list()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "The change touches src/Earlier.cs, which is listed below.",
            "[Modified Files]",
            "src/Parser.cs",
        });

        Assert.Equal("src/Parser.cs", Assert.Single(documented).Path);
    }

    /// <summary>
    /// Nothing after the closing heading is part of the list, however path-shaped it is.
    /// </summary>
    /// <remarks>
    /// A document that goes on to list the files of the next phase, or repeats the list in a summary at
    /// the end, would otherwise contribute every one of those paths as a claim about this change.
    /// </remarks>
    [Fact]
    public void Nothing_after_the_closing_heading_is_read()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            "src/Parser.cs",
            "[Status]",
            "Passed",
            "Files touched in this phase:",
            "src/NotInThisPhase.cs",
        });

        Assert.Equal("src/Parser.cs", Assert.Single(documented).Path);
    }

    /// <summary>A document with no section in it lists nothing, rather than everything.</summary>
    [Fact]
    public void A_document_with_no_section_reads_as_nothing_rather_than_as_everything()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "Change summary",
            "src/Parser.cs was rewritten.",
            "Root cause",
            "The diff was compared against the wrong tree.",
        });

        // The opposite failure to reading prose: a document that does not use the section says nothing,
        // and the verdict then reports every real change as undeclared instead of claiming a match the
        // document never made.
        Assert.Empty(documented);
    }

    /// <summary>
    /// The heading is the whole line, so a sentence that happens to contain one is not the heading.
    /// </summary>
    [Fact]
    public void A_sentence_mentioning_the_heading_does_not_open_the_section()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "The Modified Files section below lists one path.",
            "src/NotTheList.cs",
            "[Modified Files]",
            "src/Parser.cs",
        });

        Assert.Equal("src/Parser.cs", Assert.Single(documented).Path);
    }

    /// <summary>
    /// Inside the section, a path is a run carrying a slash. A bare word is not one.
    /// </summary>
    /// <remarks>
    /// This is the rule that replaced a list of exceptions. Nothing here has to decide whether
    /// <c>Parser.cs</c> is a file name and <c>e.g</c> is an abbreviation, because both are simply not
    /// paths under it: a document that means to name a file at the root writes the folder it is in.
    /// </remarks>
    [Fact]
    public void A_run_without_a_slash_is_not_a_path()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            "Parser.cs",
            "Dockerfile",
            ".gitignore",
            "wrwe.cs",
            "e.g. the reader",
        });

        Assert.Empty(documented);
    }

    /// <summary>
    /// The numbering is not part of the path, however it is written.
    /// </summary>
    [Fact]
    public void A_list_marker_is_stripped_from_in_front_of_a_path()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            "1. src/Parser.cs",
            "2) src/Reader.cs",
            "(3) src/Writer.cs",
            "4.\tsrc/Other.cs",
            "1.\\src\\Attached.cs",
            "- src/Dashed.cs",
            "\u2022 src/Bulleted.cs",
        });

        Assert.Equal(
            new[]
            {
                "src/Parser.cs",
                "src/Reader.cs",
                "src/Writer.cs",
                "src/Other.cs",
                "src/Attached.cs",
                "src/Dashed.cs",
                "src/Bulleted.cs",
            },
            documented.Select(file => file.Path));
    }

    /// <summary>
    /// Separators are spelling, not difference, and a path written with backslashes is the path the
    /// folder walk reports with slashes.
    /// </summary>
    [Fact]
    public void Separators_and_surrounding_slashes_are_normalised()
    {
        Assert.Equal("src/Views/MainWindow.xaml", ChangeDocumentParser.Normalize("src\\Views\\MainWindow.xaml"));
        Assert.Equal("src/Parser.cs", ChangeDocumentParser.Normalize("  \\src\\Parser.cs  "));
        Assert.Equal("src/Parser.cs", ChangeDocumentParser.Normalize("/src/Parser.cs/"));
        Assert.Equal("src/Parser.cs", ChangeDocumentParser.Normalize("./src/Parser.cs"));
        Assert.Equal("C:/project/src/Reader.cs", ChangeDocumentParser.Normalize(@"C:\project\src\Reader.cs"));

        // And the answers that are not paths.
        Assert.Null(ChangeDocumentParser.Normalize("Parser.cs"));
        Assert.Null(ChangeDocumentParser.Normalize("   "));
        Assert.Null(ChangeDocumentParser.Normalize("/"));
        Assert.Null(ChangeDocumentParser.Normalize("https://example.com/src/Parser.cs"));
    }

    /// <summary>
    /// A folder name may contain a single space, so a single space does not end a path.
    /// </summary>
    /// <remarks>
    /// Two or more spaces, or a tab, separate two paths; one space is part of a name. Stopping at every
    /// space turns <c>TestAuto Layer/api/Reader.py</c> into <c>Layer/api/Reader.py</c>, which names a
    /// different thing - and the truncated claim would still find its file by suffix, which is exactly
    /// why it has to be prevented rather than tolerated.
    /// </remarks>
    [Fact]
    public void A_folder_name_containing_a_space_is_read_whole()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            "D  TestAuto Layer/api/app_context.py",
            "src/Parser.cs   src/Reader.cs",
        });

        Assert.Equal(
            new[]
            {
                "TestAuto Layer/api/app_context.py",
                "src/Parser.cs",
                "src/Reader.cs",
            },
            documented.Select(file => file.Path));
    }

    /// <summary>
    /// Nothing is excluded: whether a path counts is a question about the comparison, not about what the
    /// document wrote.
    /// </summary>
    [Fact]
    public void A_path_under_an_ignored_folder_is_extracted_like_any_other()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            "bin/Debug/net461/App.dll",
            "obj/Release/App.dll",
            ".git/hooks/pre-commit",
            "node_modules/lib/index.js",
        });

        Assert.Equal(
            new[]
            {
                "bin/Debug/net461/App.dll",
                "obj/Release/App.dll",
                ".git/hooks/pre-commit",
                "node_modules/lib/index.js",
            },
            documented.Select(file => file.Path));
    }

    /// <summary>
    /// The same path twice is kept twice, so the check can say the document listed it twice.
    /// </summary>
    [Fact]
    public void The_same_path_twice_is_kept_twice_so_the_checker_can_say_so()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            "src/Parser.cs",
            "src/Reader.cs",
            "\\src\\Parser.cs",
        });

        // Collapsing duplicates here would hide the one thing the reader would want to fix - and the two
        // spellings above are the same path, so this also covers the section being read twice over.
        Assert.Equal(3, documented.Count);
        Assert.Equal(
            new[] { 2, 3, 4 },
            documented.Select(file => file.LineNumber));
    }

    /// <summary>A finding can point at the line the claim came from.</summary>
    [Fact]
    public void A_finding_can_point_at_the_line_the_claim_came_from()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "Root cause",
            "The wrong tree was compared.",
            "[Modified Files]",
            "\u2022 src/Parser.cs",
        });

        // Without the line number a finding says "src/Parser.cs is wrong" and the reader has to hunt for
        // it in a document they are trying to correct.
        DocumentedFile claim = Assert.Single(documented);

        Assert.Equal(4, claim.LineNumber);
        Assert.Equal("\u2022 src/Parser.cs", claim.OriginalText);
    }


    // ------------------------------------------------------------------ the view model

    /// <summary>
    /// Nothing can be verified until all three inputs are given.
    /// </summary>
    /// <remarks>
    /// Checked through the command rather than through a validation property, because the command is
    /// what the button is bound to: a rule enforced anywhere else is a rule the button does not have.
    /// </remarks>
    [Fact]
    public void The_verify_button_is_live_only_once_all_three_inputs_are_given()
    {
        ChangeDocumentViewModel model = New();

        Assert.False(model.VerifyCommand.CanExecute(null));

        model.BaseFolder = @"C:\work\base";
        Assert.False(model.VerifyCommand.CanExecute(null));

        model.ModifiedFolder = @"C:\work\modified";
        Assert.False(model.VerifyCommand.CanExecute(null));

        model.DocumentPath = @"C:\work\changes.docx";
        Assert.True(model.VerifyCommand.CanExecute(null));

        // Whitespace is not an input. A box holding a space is a box nobody has filled in.
        model.DocumentPath = "   ";
        Assert.False(model.VerifyCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_document_that_cannot_be_opened_is_reported_rather_than_thrown()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        ChangeDocumentViewModel model = New();
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = root.WriteFile("notes.txt", "not a document");

        await model.VerifyAsync();

        Assert.True(model.HasError);
        Assert.Contains("docx", model.Error);

        // And no stale verdict is left behind to be read as the answer to this attempt.
        Assert.False(model.HasResult);
        Assert.Empty(model.Findings);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_reported_by_name()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        ChangeDocumentViewModel model = New();
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = Path.Combine(root.Path, "nowhere");
        model.DocumentPath = TempDocx.Create(root.PathFor("changes.docx"), new[] { "[Modified Files]" });

        await model.VerifyAsync();

        Assert.True(model.HasError);
        Assert.Contains("nowhere", model.Error);
    }

    [Fact]
    public async Task A_verification_that_agrees_says_so_and_shows_what_it_examined()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(modifiedFolder, "src/Edited.cs", "after");

        ChangeDocumentViewModel model = New();
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[] { "[Modified Files]", Bullet + " src/Edited.cs" });

        await model.VerifyAsync();

        Assert.False(model.HasError);
        Assert.True(model.HasResult);
        Assert.True(model.VerdictIsGood);
        Assert.Contains("matches the source", model.VerdictCaption);
        Assert.Contains("1 path(s) documented", model.Summary);
        Assert.Empty(model.Findings);

        // How much was actually compared, so a pass is a statement about the tree rather than an
        // absence of complaints.
        Assert.Contains("compared", model.DetailLine);
    }

    [Fact]
    public async Task A_verification_that_disagrees_lists_what_is_wrong()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(modifiedFolder, "src/Edited.cs", "after");

        // The document claims a file that never changed.
        ChangeDocumentViewModel model = New();
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[] { "[Modified Files]", Bullet + " src/Untouched.cs" });

        await model.VerifyAsync();

        Assert.True(model.HasResult);
        Assert.False(model.VerdictIsGood);
        Assert.Contains("problem(s) found", model.VerdictCaption);

        ChangeFinding finding = Assert.Single(
            model.Findings,
            f => f.Kind == ChangeFindingKind.DocumentedButUnchanged);

        Assert.Equal("src/Untouched.cs", finding.DisplayPath);

        // Both directions of the disagreement: the false claim, and the real change nobody declared.
        Assert.Contains(model.Findings, f => f.Kind == ChangeFindingKind.MissingFromDocument
            && f.ActualPath == "src/Edited.cs");
    }

    /// <summary>
    /// Nothing is left out of the comparison, whatever folder a file sits in.
    /// </summary>
    /// <remarks>
    /// The export skips build output because a compiled assembly differs on every build and would bury
    /// the work. The checker cannot: the two folders are the two sides of one change, and a file the
    /// document says changed has to be looked at wherever it lives — otherwise the check says nothing
    /// about it and reports agreement.
    /// </remarks>
    [Fact]
    public async Task A_changed_file_is_compared_even_when_it_sits_in_an_ordinary_build_folder()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(baseFolder, "bin/App.dll", "old");
        Write(modifiedFolder, "bin/App.dll", "new");

        ChangeDocumentViewModel model = New();
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[] { "[Modified Files]", Bullet + " src/Edited.cs" });

        Write(model.ModifiedFolder, "src/Edited.cs", "after");
        await model.VerifyAsync();

        // The document's own claim is still right, and the compiled output is reported beside it rather
        // than quietly left out.
        ChangeFinding finding = Assert.Single(model.Findings);

        Assert.Equal(ChangeFindingKind.MissingFromDocument, finding.Kind);
        Assert.Equal("bin/App.dll", finding.ActualPath);
        Assert.DoesNotContain("left out", model.DetailLine);
    }

    /// <summary>
    /// A claim about a file under a build folder is answered from the folders, not excused.
    /// </summary>
    /// <remarks>
    /// There is no longer an ignore rule to defer to, so a document naming a file that is there and
    /// unchanged gets the same answer as any other: it claims an edit that is not there.
    /// </remarks>
    [Fact]
    public async Task A_claim_about_a_file_in_a_build_folder_is_answered_rather_than_called_missing()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(baseFolder, "bin/App.dll", "same");
        Write(modifiedFolder, "bin/App.dll", "same");

        ChangeDocumentViewModel model = New();
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[] { "[Modified Files]", Bullet + " bin/App.dll" });

        await model.VerifyAsync();

        ChangeFinding finding = Assert.Single(model.Findings);

        Assert.Equal(ChangeFindingKind.DocumentedButUnchanged, finding.Kind);
        Assert.Equal("bin/App.dll", finding.DocumentedPath);
    }

    [Fact]
    public void A_folder_picker_fills_in_the_field_it_was_opened_from()
    {
        ChangeDocumentViewModel model = New();

        model.BrowseBaseCommand.Execute(null);
        Assert.Equal(@"C:\picked\base", model.BaseFolder);

        model.BrowseModifiedCommand.Execute(null);
        Assert.Equal(@"C:\picked\modified", model.ModifiedFolder);
    }

    [Fact]
    public void A_cancelled_picker_leaves_the_field_as_it_was()
    {
        // A picker that returns nothing, which is what a cancelled shell dialog does.
        ChangeDocumentViewModel model = new ChangeDocumentViewModel(
            pickFolder: (_, _) => null,
            pickDocument: _ => null);

        model.BaseFolder = @"C:\work\base";
        model.BrowseBaseCommand.Execute(null);

        // Overwriting a typed path with nothing because the dialog was dismissed would be worse than
        // not having opened one.
        Assert.Equal(@"C:\work\base", model.BaseFolder);
    }

    // ------------------------------------------------------------------ helpers

    private const string Bullet = "\u2022";

    [Fact]
    public async Task The_findings_can_be_copied_out_as_csv()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(modifiedFolder, "src/Edited.cs", "after");

        string? copied = null;

        ChangeDocumentViewModel model = new ChangeDocumentViewModel(
            pickFolder: (_, _) => null,
            pickDocument: _ => null,
            pickReportFile: _ => null,
            copyText: text => copied = text);

        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[] { "[Modified Files]", Bullet + " src/Untouched.cs" });

        await model.VerifyAsync();

        model.CopyReportCommand.Execute(null);

        // The clipboard is how the list reaches a spreadsheet or a ticket, and a tab-separated copy
        // would paste into the wrong columns because the sentences contain commas.
        Assert.NotNull(copied);
        Assert.Contains("Kind,Path,Documented path,Actual path,What is wrong", copied!, StringComparison.Ordinal);
        Assert.Contains("src/Untouched.cs", copied!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_findings_can_be_written_to_a_chosen_file()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(modifiedFolder, "src/Edited.cs", "after");

        string target = root.PathFor("report.csv");

        ChangeDocumentViewModel model = new ChangeDocumentViewModel(
            pickFolder: (_, _) => null,
            pickDocument: _ => null,
            pickReportFile: suggested =>
            {
                // The name is offered from the document, so the reader recognises the file.
                Assert.Equal("changes-report.csv", suggested);
                return target;
            },
            copyText: _ => { });

        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[] { "[Modified Files]", Bullet + " src/Untouched.cs" });

        await model.VerifyAsync();
        model.ExportReportCommand.Execute(null);

        Assert.True(File.Exists(target));
        Assert.Contains("src/Untouched.cs", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.False(model.HasError);
    }

    /// <summary>
    /// Nothing to report means nothing to copy or export, and a button that is merely able to answer
    /// is not the same as a button that is enabled.
    /// </summary>
    [Fact]
    public void The_export_buttons_are_dead_until_there_is_something_to_export()
    {
        ChangeDocumentViewModel model = New();

        Assert.False(model.CanReport);
        Assert.False(model.CopyReportCommand.CanExecute(null));
        Assert.False(model.ExportReportCommand.CanExecute(null));
    }

    /// <summary>
    /// The buttons have to come alive when the check finishes, not merely be able to answer when asked.
    /// </summary>
    /// <remarks>
    /// A bound button asks its command once when the window loads and waits to be told otherwise, so a
    /// command whose answer changes without announcing it leaves the button greyed out for good even
    /// though the answer is true. Asserting <c>CanExecute</c> alone cannot see this, which is why the
    /// event is watched instead.
    /// </remarks>
    [Fact]
    public async Task Copy_and_export_live_when_a_verification_finishes()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(modifiedFolder, "src/Edited.cs", "after");

        ChangeDocumentViewModel model = New();
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[] { "[Modified Files]", Bullet + " src/Edited.cs" });

        var announced = new List<bool>();
        model.CopyReportCommand.CanExecuteChanged += (_, _) => announced.Add(model.CanReport);

        await model.VerifyAsync();

        Assert.True(model.CanReport);
        Assert.True(model.CopyReportCommand.CanExecute(null));
        Assert.True(model.ExportReportCommand.CanExecute(null));

        // Announced, not merely true: the binding is what the reader sees. Both the result being set and
        // the run finishing announce, because either alone is enough for the button depending on the
        // order the two land in, and the order is not something this test should be relying on.
        Assert.Contains(true, announced);
    }

    [Fact]
    public async Task Copy_and_export_go_dead_again_when_a_later_verification_fails()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(modifiedFolder, "src/Edited.cs", "after");

        ChangeDocumentViewModel model = New();
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[] { "[Modified Files]", Bullet + " src/Edited.cs" });

        await model.VerifyAsync();
        Assert.True(model.CanReport);

        // A failed run clears the result, and a button left live would offer to export the answer to a
        // check that never happened.
        model.DocumentPath = root.WriteFile("notes.txt", "not a document");

        await model.VerifyAsync();

        Assert.False(model.CanReport);
        Assert.False(model.CopyReportCommand.CanExecute(null));
        Assert.False(model.ExportReportCommand.CanExecute(null));
    }

    [Fact]
    public void A_report_cannot_be_produced_before_a_verification_has_run()
    {
        ChangeDocumentViewModel model = New();

        // Not an exception: a window wired up without a clipboard should still verify.
        model.CopyReportCommand.Execute(null);
        model.ExportReportCommand.Execute(null);

        Assert.False(model.HasResult);
    }

    private static ChangeDocumentViewModel New()
    {
        // A different answer for each folder, so a test can tell which field a Browse filled rather
        // than only that something was filled.
        int call = 0;

        return new ChangeDocumentViewModel(
            pickFolder: (_, _) => new[] { @"C:\picked\base", @"C:\picked\modified" }[call++],
            pickDocument: _ => @"C:\picked\changes.docx");
    }

    private static (string Base, string Modified) Tree(TempDirectory root)
    {
        string baseFolder = root.PathFor("base");

        Write(baseFolder, "src/Edited.cs", "before");
        Write(baseFolder, "src/Untouched.cs", "identical");

        string modifiedFolder = root.PathFor("modified");
        Directory.CreateDirectory(modifiedFolder);

        foreach (string file in Directory.GetFiles(baseFolder, "*", SearchOption.AllDirectories))
        {
            string target = file.Replace(baseFolder, modifiedFolder);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return (baseFolder, modifiedFolder);
    }

    private static string Write(string folder, string relativePath, string content)
    {
        string full = Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));

        return full;
    }
}
