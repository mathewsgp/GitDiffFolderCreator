using System.Text;
using System.Windows.Threading;
using GitDiffFolderCreator.Services;
using GitDiffFolderCreator.ViewModels;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// The two headings the changed-file list is read between, as a setting rather than as fixed text.
/// </summary>
/// <remarks>
/// Change documents are written by hand, so the headings are not standardised: one team writes
/// <c>[Modified Files]</c>, the next writes <c>Files changed:</c>, and a tool that only ever knows the
/// first reads the second as a document with no list in it — which it reports as agreement, having found
/// nothing to disagree with. These tests cover the markers being honoured, being kept, and still meaning
/// the documented thing when the settings file says nothing at all.
/// </remarks>
public sealed class ChangeDocumentMarkerTests
{
    private const string Bullet = "•";

    // ------------------------------------------------------------------ the defaults

    /// <summary>
    /// What the markers are when nothing has been configured.
    /// </summary>
    /// <remarks>
    /// Spelled with the brackets the documents this was written against use, which is also what the
    /// settings file and the two boxes show. The comparison strips them either way, so this is the
    /// readable form rather than a stricter one.
    /// </remarks>
    [Fact]
    public void The_default_markers_are_the_documented_ones()
    {
        Assert.Equal("[Modified Files]", ChangeDocumentParser.DefaultStartMarker);
        Assert.Equal("[Status]", ChangeDocumentParser.DefaultEndMarker);
    }

    [Fact]
    public void A_settings_file_with_no_markers_in_it_reads_back_the_defaults()
    {
        using TempDirectory folder = new();
        string path = folder.PathFor("settings.json");

        // Written by a version that predates the setting, which is what every existing user's file is.
        File.WriteAllText(path, @"{""gitDirectory"":""C:\repo"",""logLimit"":500}");

        AppSettings settings = new AppSettingsStore(path).Load();

        // Null rather than empty: a reader who never asked for a marker gets the one that works, not one
        // that opens no section at all.
        Assert.Equal("[Modified Files]", settings.ChangedFilesStartMarker);
        Assert.Equal("[Status]", settings.ChangedFilesEndMarker);
    }

    [Fact]
    public void A_marker_hand_emptied_in_the_settings_file_reads_back_as_the_default()
    {
        using TempDirectory folder = new();
        string path = folder.PathFor("settings.json");

        File.WriteAllText(
            path,
            @"{""changedFilesStartMarker"":""  "",""changedFilesEndMarker"":null}");

        AppSettings settings = new AppSettingsStore(path).Load();

        Assert.Equal("[Modified Files]", settings.ChangedFilesStartMarker);
        Assert.Equal("[Status]", settings.ChangedFilesEndMarker);
    }

    [Fact]
    public void The_markers_survive_a_write_and_a_read()
    {
        using TempDirectory folder = new();
        AppSettingsStore store = new(folder.PathFor("settings.json"));

        store.Save(new AppSettings
        {
            ChangedFilesStartMarker = "## Files Changed",
            ChangedFilesEndMarker = "## Verification",
        });

        AppSettings loaded = store.Load();

        // Compared in full, brackets and all: a marker stored trimmed of what the reader typed would be a
        // marker they do not recognise when they come back to the box.
        Assert.Equal("## Files Changed", loaded.ChangedFilesStartMarker);
        Assert.Equal("## Verification", loaded.ChangedFilesEndMarker);
    }

    // ------------------------------------------------------------------ the parser

    /// <summary>
    /// The headings the reader named are the headings that bound the list.
    /// </summary>
    /// <remarks>
    /// The document here is written the other way round from the default — different headings entirely, and
    /// no trace of <c>[Modified Files]</c> — which is the case the setting exists for.
    /// </remarks>
    [Fact]
    public void The_markers_the_reader_named_bound_the_section()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(
            new[]
            {
                "Change summary",
                "## Files Changed",
                Bullet + " src/Parser.cs",
                Bullet + " src/Reader.cs",
                "## Verification",
                "Passed",
            },
            startMarker: "## Files Changed",
            endMarker: "## Verification");

        Assert.Equal(
            new[] { "src/Parser.cs", "src/Reader.cs" },
            documented.Select(file => file.Path));
    }

    [Fact]
    public void The_same_document_read_with_the_defaults_holds_no_list_at_all()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "## Files Changed",
            Bullet + " src/Parser.cs",
            "## Verification",
            "Passed",
        });

        // Which is the trap this setting exists to close: no list found is not the same answer as the list
        // agreeing with the folders, and only the reader knows which headings their document uses.
        Assert.Empty(documented);
    }

    /// <summary>
    /// A marker matches however the heading brackets and cases it.
    /// </summary>
    /// <remarks>
    /// The marker is typed once, in settings, and the document is typed by hand every time it is written,
    /// so the two will not agree on the brackets, the trailing colon or the capital letters. Requiring an
    /// exact match would make the setting a spelling test rather than a pointer to a heading.
    /// </remarks>
    [Theory]
    [InlineData("[Files Changed]", "[files changed]")]
    [InlineData("[Files Changed]", "[Files Changed]:")]
    [InlineData("Files Changed", "  [Files Changed]  ")]
    [InlineData("FILES CHANGED", "[Files Changed]")]
    public void A_marker_matches_the_heading_however_it_brackets_and_cases_it(string marker, string heading)
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(
            new[] { heading, Bullet + " src/Parser.cs", "[Status]", "Passed" },
            startMarker: marker,
            endMarker: "[Status]");

        Assert.Equal(new[] { "src/Parser.cs" }, documented.Select(file => file.Path));
    }

    [Fact]
    public void A_marker_named_by_half_a_sentence_still_does_not_open_the_section()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(
            new[]
            {
                "The Files Changed section below lists one path.",
                "src/Parser.cs",
                "[Status]",
            },
            startMarker: "Files Changed",
            endMarker: "[Status]");

        // The rule the defaults were written under holds for a configured marker too: the whole line is
        // compared, so a sentence that happens to contain the words is not the heading.
        Assert.Empty(documented);
    }

    [Fact]
    public void A_blank_closing_marker_reads_the_list_to_the_end_of_the_document()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(
            new[] { "[Modified Files]", Bullet + " src/Parser.cs", "[Status]", "Passed" },
            startMarker: "[Modified Files]",
            endMarker: null);

        // An answer rather than a failure: a document with nothing after the list still has a list in it,
        // and a marker that names no heading cannot close one.
        Assert.Equal(new[] { "src/Parser.cs" }, documented.Select(file => file.Path));
    }

    [Fact]
    public void A_marker_that_is_nothing_but_brackets_and_a_colon_names_no_heading()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(
            new[] { "[]:", "src/Parser.cs", "[Status]", "Passed" },
            startMarker: "[]",
            endMarker: "[Status]");

        // Stripped to nothing, this marker would otherwise match any line that is only punctuation and
        // open the section in the wrong place.
        Assert.Empty(documented);
    }

    // ------------------------------------------------------------------ more than one section

    /// <summary>
    /// A document may repeat the section, and every one of them is part of the list.
    /// </summary>
    /// <remarks>
    /// Per module, per phase, per developer: a long change gets a section each time rather than one
    /// section the length of a page. Reading only the first would report every file in the second as a
    /// real change the document never mentioned — the checker's noisiest possible failure, and one that
    /// looks like the document being wrong.
    /// </remarks>
    [Fact]
    public void Every_section_the_markers_bound_is_read()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            Bullet + " src/Parser.cs",
            "[Status]",
            "Passed",
            "[Modified Files]",
            Bullet + " src/Reader.cs",
            Bullet + " src/Writer.cs",
            "[Status]",
            "Passed",
        });

        Assert.Equal(
            new[] { "src/Parser.cs", "src/Reader.cs", "src/Writer.cs" },
            documented.Select(file => file.Path));
    }

    [Fact]
    public void The_lines_in_a_later_section_report_their_own_line_numbers()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            Bullet + " src/Parser.cs",
            "[Status]",
            "[Modified Files]",
            Bullet + " src/Reader.cs",
            "[Status]",
        });

        // A finding quotes the line the claim came from, and line 5 is not line 2 however they were found.
        Assert.Equal(5, documented.Single(file => file.Path == "src/Reader.cs").LineNumber);
    }

    /// <summary>
    /// What sits between two sections belongs to neither of them.
    /// </summary>
    /// <remarks>
    /// The closing heading is the end of one section, not the end of the document: prose, a summary table
    /// and a list of files for a later phase all sit between two sections in a real document, and none of
    /// them is part of the list.
    /// </remarks>
    [Fact]
    public void Content_between_two_sections_is_not_part_of_either()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            Bullet + " src/Parser.cs",
            "[Status]",
            "Files planned for the next phase:",
            "src/NotYetTouched.cs",
            "[Modified Files]",
            Bullet + " src/Reader.cs",
            "[Status]",
        });

        Assert.Equal(
            new[] { "src/Parser.cs", "src/Reader.cs" },
            documented.Select(file => file.Path));
    }

    // ------------------------------------------------------------------ headings that say more

    /// <summary>
    /// The heading may be followed by whatever the author wrote after it.
    /// </summary>
    /// <remarks>
    /// A heading in a real document is rarely a bare word: it carries a colon, a count, the module or
    /// phase it belongs to. Requiring the line to end at the marker would read all of those documents as
    /// having no section at all, and "no section" is reported as agreement — the tool would pass a
    /// document it never read.
    /// <para>
    /// The marker still has to be at the start of the line, which is what keeps the existing rule whole: a
    /// sentence that mentions the heading half way along is not a heading.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_marker_line_may_carry_more_than_the_marker()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files] - 3 files changed:",
            Bullet + " src/Parser.cs",
            "[Status] - 2 of 2 passed",
            "[Modified Files] (Module 2)",
            Bullet + " src/Reader.cs",
            "[Status]: green",
        });

        Assert.Equal(
            new[] { "src/Parser.cs", "src/Reader.cs" },
            documented.Select(file => file.Path));
    }

    [Fact]
    public void A_closing_marker_with_more_after_it_still_closes_the_section()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(new[]
        {
            "[Modified Files]",
            Bullet + " src/Parser.cs",
            "[Status] - all green",
            "Files touched in a later phase:",
            "src/NotInThisPhase.cs",
        });

        // Otherwise the list would run on past the heading and take the next phase's files with it.
        Assert.Equal("src/Parser.cs", Assert.Single(documented).Path);
    }

    [Fact]
    public void A_marker_named_mid_sentence_is_still_not_a_heading_however_much_follows_it()
    {
        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(
            new[]
            {
                "The Modified Files section below lists one path, and nothing else does.",
                "src/NotTheList.cs",
                "[Status]",
            },
            startMarker: "Modified Files",
            endMarker: "[Status]");

        // The rule that was already there, and the reason the test above can exist: what is allowed to
        // follow the marker is anything, but the marker itself has to come first.
        Assert.Empty(documented);
    }

    [Fact]
    public async Task The_check_reads_every_section_and_every_extra_word_on_a_heading()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(modifiedFolder, "src/Edited.cs", "after");
        Write(modifiedFolder, "src/Added.cs", "new");

        // Both behaviours of the heading at once, against a real package: two sections, neither heading
        // written bare, and a paragraph between them that is not part of either.
        ChangeDocumentViewModel model = New(root.PathFor("settings.json"));
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[]
            {
                "Change summary",
                "[Modified Files] - Module 1:",
                Bullet + " src/Edited.cs",
                "[Status] - 1 of 1 passed",
                "Planned for the next phase:",
                "src/SomethingElse.cs",
                "[Modified Files] - Module 2:",
                Bullet + " src/Added.cs",
                "[Status]: green",
            });

        await model.VerifyAsync();

        // Every real change is accounted for across both sections. Reading only the first would report
        // src/Added.cs as undocumented, and the planned file would be read as a claim about a file that
        // does not exist.
        Assert.True(model.VerdictIsGood);
        Assert.Empty(model.Findings);
    }

    // ------------------------------------------------------------------ the checker

    /// <summary>
    /// A check against a document whose headings are not the defaults.
    /// </summary>
    /// <remarks>
    /// The same tree and the same document read twice: once with the markers that match it and once
    /// without. The pair is the whole claim — that the markers are what decide which lines are the list,
    /// rather than the checker merely carrying them around.
    /// </remarks>
    [Fact]
    public async Task The_check_reads_the_markers_it_was_given()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(modifiedFolder, "src/Edited.cs", "after");

        string document = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[]
            {
                "Change summary",
                "## Files Changed",
                Bullet + " src/Edited.cs",
                "## Verification",
                "Passed",
            });

        ChangeDocumentViewModel model = New(root.PathFor("settings.json"));
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = document;

        // With the default markers the headings are not found, the document yields no paths, and the one
        // real change comes back as undocumented.
        await model.VerifyAsync();
        Assert.False(model.VerdictIsGood);

        model.ChangedFilesStartMarker = "## Files Changed";
        model.ChangedFilesEndMarker = "## Verification";

        await model.VerifyAsync();

        Assert.True(model.VerdictIsGood);
        Assert.Empty(model.Findings);
    }

    [Fact]
    public async Task An_emptied_marker_box_reads_the_document_with_the_default_headings()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = Tree(root);

        Write(modifiedFolder, "src/Edited.cs", "after");

        ChangeDocumentViewModel model = New(root.PathFor("settings.json"));
        model.BaseFolder = baseFolder;
        model.ModifiedFolder = modifiedFolder;
        model.DocumentPath = TempDocx.Create(
            root.PathFor("changes.docx"),
            new[] { "[Modified Files]", Bullet + " src/Edited.cs", "[Status]", "Passed" });

        // A reader who clears a box has not asked for a document that cannot be read.
        model.ChangedFilesStartMarker = "   ";
        model.ChangedFilesEndMarker = string.Empty;

        await model.VerifyAsync();

        Assert.True(model.VerdictIsGood);

        // And what is stored is the default rather than the emptiness, so the box is not left holding
        // something that only means something to the parser.
        AppSettings settings = new AppSettingsStore(root.PathFor("settings.json")).Load();
        Assert.Equal("[Modified Files]", settings.ChangedFilesStartMarker);
        Assert.Equal("[Status]", settings.ChangedFilesEndMarker);
    }

    [Fact]
    public void The_markers_are_read_from_the_settings_the_first_time_the_window_opens()
    {
        using TempDirectory folder = new();
        string settingsPath = folder.PathFor("settings.json");

        new AppSettingsStore(settingsPath).Save(new AppSettings
        {
            ChangedFilesStartMarker = "## Files Changed",
            ChangedFilesEndMarker = "## Verification",
        });

        ChangeDocumentViewModel model = New(settingsPath);

        // Nobody should have to name their own headings twice.
        Assert.Equal("## Files Changed", model.ChangedFilesStartMarker);
        Assert.Equal("## Verification", model.ChangedFilesEndMarker);
    }

    [Fact]
    public void A_marker_edited_in_one_window_is_there_the_next_time_the_window_opens()
    {
        using TempDirectory folder = new();
        string settingsPath = folder.PathFor("settings.json");

        ChangeDocumentViewModel first = New(settingsPath);
        first.ChangedFilesStartMarker = "## Files Changed";

        ChangeDocumentViewModel second = New(settingsPath);

        // Written out as the box loses focus rather than when the window closes, so a crash between the
        // two does not lose it.
        Assert.Equal("## Files Changed", second.ChangedFilesStartMarker);

        // The closing marker is saved with it, because half a pair is a list read to the wrong place.
        Assert.Equal("[Status]", second.ChangedFilesEndMarker);
    }

    [Fact]
    public void Putting_the_markers_back_is_saved_like_any_other_edit()
    {
        using TempDirectory folder = new();
        string settingsPath = folder.PathFor("settings.json");

        new AppSettingsStore(settingsPath).Save(new AppSettings
        {
            ChangedFilesStartMarker = "## Files Changed",
            ChangedFilesEndMarker = "## Verification",
        });

        ChangeDocumentViewModel model = New(settingsPath);
        model.RestoreDefaultMarkersCommand.Execute(null);

        Assert.Equal("[Modified Files]", model.ChangedFilesStartMarker);
        Assert.Equal("[Status]", model.ChangedFilesEndMarker);

        AppSettings settings = new AppSettingsStore(settingsPath).Load();
        Assert.Equal("[Modified Files]", settings.ChangedFilesStartMarker);
        Assert.Equal("[Status]", settings.ChangedFilesEndMarker);
    }

    /// <summary>
    /// Two windows write the same settings file, and neither may lose the other's fields.
    /// </summary>
    /// <remarks>
    /// The main window saves on close and rebuilds most of the file from its own properties; the checker
    /// window saves the markers. Written from scratch, the main window's save drops the two headings on
    /// every close, and the reader is asked for them again each time they open the checker.
    /// </remarks>
    [Fact]
    public void Closing_the_main_window_does_not_throw_the_markers_away()
    {
        using TempDirectory folder = new();
        string settingsPath = folder.PathFor("settings.json");

        AppSettingsStore store = new(settingsPath);
        store.Save(new AppSettings
        {
            ChangedFilesStartMarker = "## Files Changed",
            ChangedFilesEndMarker = "## Verification",
            LogLimit = 250,
        });

        GitViewModel viewModel = new GitViewModel(
            store,
            pickFolder: (input, title) => null,
            dispatcher: Dispatcher.CurrentDispatcher,
            openFolder: _ => true);

        viewModel.SaveSettings();

        AppSettings loaded = store.Load();

        Assert.Equal("## Files Changed", loaded.ChangedFilesStartMarker);
        Assert.Equal("## Verification", loaded.ChangedFilesEndMarker);

        // The settings the main window does own are still written, which is the other half of it.
        Assert.Equal(250, loaded.LogLimit);
    }

    // ------------------------------------------------------------------ helpers

    private static ChangeDocumentViewModel New(string settingsPath)
    {
        return new ChangeDocumentViewModel(
            pickFolder: (_, _) => null,
            pickDocument: _ => null,
            settingsStore: new AppSettingsStore(settingsPath));
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

    private static void Write(string folder, string relativePath, string content)
    {
        string full = Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }
}