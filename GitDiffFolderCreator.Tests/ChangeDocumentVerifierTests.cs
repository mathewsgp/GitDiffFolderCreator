using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitDiffFolderCreator.Services;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// Comparing two source folders, and cross-checking a change document against the result.
/// </summary>
/// <remarks>
/// The four cases the check exists for are each a failure in one direction or the other: a file claimed
/// and not changed, a file changed and not claimed, a file at a path other than the one claimed, and
/// anything else the two disagree about. A check that only ran in one direction would pass a document
/// that is wrong in the other, which is why every test here states which direction it is checking.
/// </remarks>
public sealed class ChangeDocumentVerifierTests
{
    /// <summary>
    /// A bullet, written as an escape rather than as the character.
    /// </summary>
    /// <remarks>
    /// The document a real change comes in is written in Word, and the parser's job is to recognise
    /// whatever the author pressed. Typing the character into a source file instead leaves it at the
    /// mercy of how the file was saved and re-saved, and a mangled bullet fails as "no paths found"
    /// rather than as anything pointing at the encoding.
    /// </remarks>
    private const string Bullet = "\u2022";

    // ------------------------------------------------------------------ comparing folders

    [Fact]
    public void The_three_ways_a_file_can_differ_are_all_found()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = MakePair(root);

        Write(modifiedFolder, "src/Edited.cs", "after");
        Write(modifiedFolder, "src/Added.cs", "new");
        File.Delete(Path.Combine(modifiedFolder, "src", "Untouched.cs"));

        FolderComparison comparison = new FolderComparer().Compare(baseFolder, modifiedFolder);

        Assert.Equal(
            new[] { "src/Added.cs", "src/Edited.cs", "src/Untouched.cs" },
            comparison.Differences.Select(d => d.PrimaryPath));

        Assert.Equal(
            FolderDifferenceKind.Added,
            comparison.Differences.Single(d => d.PrimaryPath == "src/Added.cs").Kind);

        Assert.Equal(
            FolderDifferenceKind.Removed,
            comparison.Differences.Single(d => d.PrimaryPath == "src/Untouched.cs").Kind);

        Assert.Equal(
            FolderDifferenceKind.Modified,
            comparison.Differences.Single(d => d.PrimaryPath == "src/Edited.cs").Kind);
    }

    [Fact]
    public void A_file_that_is_identical_on_both_sides_is_not_a_difference()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = MakePair(root);

        FolderComparison comparison = new FolderComparer().Compare(baseFolder, modifiedFolder);

        Assert.Empty(comparison.Differences);

        // Still counted as compared, which is what makes "nothing changed" a finding about the whole
        // tree rather than a vacuous pass.
        Assert.Equal(3, comparison.FilesCompared);
    }

    [Fact]
    public void Build_output_is_left_out_by_default_because_it_differs_on_every_build()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = MakePair(root);

        Write(baseFolder, "bin/App.dll", "old build");
        Write(modifiedFolder, "bin/App.dll", "new build");
        Write(modifiedFolder, "obj/Debug/cache.tmp", "scratch");

        Assert.Empty(new FolderComparer().Compare(baseFolder, modifiedFolder).Differences);

        // And the count says so, rather than a check that quietly examined less than it appears to.
        FolderComparison skipped = new FolderComparer().Compare(baseFolder, modifiedFolder);

        Assert.Equal(3, skipped.FilesIgnored);
        Assert.True(skipped.IgnoredByRule);

        // With nothing skipped, the same two folders really do differ - so the pass above was the rule
        // working rather than the comparison missing them.
        Assert.Equal(2, new FolderComparer(Array.Empty<string>())
            .Compare(baseFolder, modifiedFolder)
            .Differences.Count);
    }

    [Fact]
    public void A_file_moved_unchanged_is_one_change_and_not_a_deletion_plus_an_addition()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = MakePair(root);

        // Moved within the modified copy, not out of the base one. The two trees are separate copies,
        // so a move leaves the base file where it is and puts the new path only on the modified side -
        // moving it across would empty the base side and leave nothing to compare against.
        File.Move(
            Path.Combine(modifiedFolder, "src", "Old.cs"),
            Path.Combine(modifiedFolder, "src", "New.cs"));

        FolderComparison comparison = new FolderComparer().Compare(baseFolder, modifiedFolder);

        FolderMove move = Assert.Single(comparison.Moves());

        Assert.Equal("src/Old.cs", move.FromPath);
        Assert.Equal("src/New.cs", move.ToPath);
    }

    [Fact]
    public void A_folder_that_does_not_exist_says_which_one()
    {
        using TempDirectory root = new();
        (string baseFolder, _) = MakePair(root);

        DirectoryNotFoundException error = Assert.Throws<DirectoryNotFoundException>(
            () => new FolderComparer().Compare(baseFolder, Path.Combine(root.Path, "nowhere")));

        Assert.Contains("nowhere", error.Message);
    }

    // ------------------------------------------------------------------ cross-checking

    [Fact]
    public void A_document_that_lists_exactly_what_changed_agrees_with_the_source()
    {
        using TempDirectory root = new();
        Fixture fixture = Build(root);

        ChangeVerificationResult result = Verify(fixture, "src/Edited.cs", "src/Added.cs");

        Assert.True(result.Agrees);
        Assert.Empty(result.Findings);
        Assert.Equal(2, result.Matches);

        // Both numbers stated, because a pass is only meaningful against what was examined.
        Assert.Equal(2, result.DocumentedCount);
        Assert.Equal(2, result.ActualDifferenceCount);
    }

    [Fact]
    public void A_file_listed_but_never_changed_is_reported()
    {
        using TempDirectory root = new();
        Fixture fixture = Build(root);

        // src/Untouched.cs is identical in both trees, and the two real changes are listed, so the
        // only thing left to report is the false claim.
        ChangeVerificationResult result = Verify(
            fixture, "src/Untouched.cs", "src/Edited.cs", "src/Added.cs");

        ChangeFinding finding = Assert.Single(result.Findings);

        Assert.Equal(ChangeFindingKind.DocumentedButUnchanged, finding.Kind);
        Assert.Equal("src/Untouched.cs", finding.DocumentedPath);
        Assert.Equal("Listed, not changed", finding.KindCaption);
        Assert.Contains("identical content", finding.Detail);
    }

    [Fact]
    public void A_file_that_changed_without_being_listed_is_reported()
    {
        using TempDirectory root = new();
        Fixture fixture = Build(root);

        // The document claims only the edit, but a file was added as well.
        ChangeVerificationResult result = Verify(fixture, "src/Edited.cs");

        ChangeFinding finding = Assert.Single(result.Findings);

        Assert.Equal(ChangeFindingKind.OnlyInModified, finding.Kind);
        Assert.Equal("src/Added.cs", finding.ActualPath);
        Assert.Null(finding.DocumentedPath);
    }

    [Fact]
    public void A_file_edited_but_not_listed_is_reported_as_a_missing_claim_not_as_an_addition()
    {
        using TempDirectory root = new();
        Fixture fixture = Build(root);

        // Both sides differ, and the document mentions neither.
        ChangeVerificationResult result = Verify(fixture);

        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Kind == ChangeFindingKind.MissingFromDocument
            && f.ActualPath == "src/Edited.cs");
        Assert.Contains(result.Findings, f => f.Kind == ChangeFindingKind.OnlyInModified
            && f.ActualPath == "src/Added.cs");
    }

    [Fact]
    public void A_file_that_moved_is_reported_as_a_wrong_path_when_documented_under_its_new_name()
    {
        using TempDirectory root = new();
        Fixture fixture = MovedFile(root);

        ChangeVerificationResult result = Verify(fixture, "src/New.cs");

        ChangeFinding finding = Assert.Single(result.Findings);

        Assert.Equal(ChangeFindingKind.PathMismatch, finding.Kind);
        Assert.Equal("src/New.cs", finding.DocumentedPath);
        Assert.Equal("src/New.cs", finding.ActualPath);
        Assert.Contains("moved", finding.Detail);
    }

    [Fact]
    public void A_moved_file_documented_under_its_old_name_is_told_where_it_went()
    {
        using TempDirectory root = new();
        Fixture fixture = MovedFile(root);

        // What someone writes when they have not noticed the move. Reporting this as "no such file"
        // would be true and useless.
        ChangeVerificationResult result = Verify(fixture, "src/Old.cs");

        ChangeFinding finding = Assert.Single(result.Findings);

        Assert.Equal(ChangeFindingKind.PathMismatch, finding.Kind);
        Assert.Equal("src/Old.cs", finding.DocumentedPath);
        Assert.Equal("src/New.cs", finding.ActualPath);
        Assert.Contains("no longer at src/Old.cs", finding.Detail);
    }

    [Fact]
    public void A_moved_file_is_not_reported_as_a_deletion_and_an_addition_as_well()
    {
        using TempDirectory root = new();
        Fixture fixture = MovedFile(root);

        // One move, one finding. Three findings for one renamed file would bury it.
        ChangeVerificationResult result = Verify(fixture, "src/New.cs");

        Assert.Single(result.Findings);
    }

    [Fact]
    public void A_deleted_file_that_the_document_never_mentions_is_reported_as_such()
    {
        using TempDirectory root = new();
        Fixture fixture = Build(root);

        File.Delete(Path.Combine(fixture.ModifiedFolder, "src", "Untouched.cs"));

        ChangeVerificationResult result = Verify(fixture, "src/Edited.cs", "src/Added.cs");

        ChangeFinding finding = Assert.Single(result.Findings);

        Assert.Equal(ChangeFindingKind.OnlyInBase, finding.Kind);
        Assert.Equal("src/Untouched.cs", finding.ActualPath);
    }

    [Fact]
    public void A_file_listed_that_is_not_in_the_modified_source_is_reported_as_absent()
    {
        using TempDirectory root = new();
        Fixture fixture = Build(root);

        // Distinct from "listed but unchanged": the file is simply not there, which is a different
        // mistake and a different fix.
        ChangeVerificationResult result = Verify(
            fixture, "src/NeverExisted.cs", "src/Edited.cs", "src/Added.cs");

        ChangeFinding finding = Assert.Single(result.Findings);

        Assert.Equal(ChangeFindingKind.DocumentedButAbsent, finding.Kind);
        Assert.Contains("no such file", finding.Detail);
    }

    [Fact]
    public void A_path_listed_twice_is_reported_once_as_a_duplicate_and_not_twice_as_a_claim()
    {
        using TempDirectory root = new();
        Fixture fixture = Build(root);

        ChangeVerificationResult result = Verify(fixture, "src/Edited.cs", "src/Edited.cs", "src/Added.cs");

        ChangeFinding finding = Assert.Single(result.Findings);

        Assert.Equal(ChangeFindingKind.ListedTwice, finding.Kind);

        // One match each for the edit and the addition, and not a third for the repeat: the file does
        // differ, so counting it twice would overstate what the document got right.
        Assert.Equal(2, result.Matches);
    }

    [Fact]
    public void The_worst_problems_are_listed_first_because_that_is_what_a_reader_fixes_first()
    {
        using TempDirectory root = new();
        Fixture fixture = Build(root);

        ChangeVerificationResult result = Verify(
            fixture,
            "src/Untouched.cs",
            "src/NeverExisted.cs",
            "src/Edited.cs",
            "src/Edited.cs",
            "src/Added.cs");

        // Every real change is listed, so the only findings are the three claims that are wrong -
        // and they come out in the order a reader would fix them.
        Assert.Equal(
            new[]
            {
                ChangeFindingKind.ListedTwice,
                ChangeFindingKind.DocumentedButAbsent,
                ChangeFindingKind.DocumentedButUnchanged,
            },
            result.Findings.Select(f => f.Kind));
    }

    [Fact]
    public void A_path_that_differs_only_in_case_is_the_same_file_and_is_not_a_mismatch()
    {
        using TempDirectory root = new();
        Fixture fixture = Build(root);

        // Windows does not hold two files whose names differ only in case, so a document spelling the
        // path differently is not making a different claim. Reporting it would contradict the rule
        // the export's own cross-check follows.
        ChangeVerificationResult result = Verify(fixture, "SRC/EDITED.CS", "src/ADDED.CS");

        Assert.True(result.Agrees);
    }

    [Fact]
    public void A_document_that_lists_nothing_at_all_says_so_rather_than_passing()
    {
        using TempDirectory root = new();
        Fixture fixture = Build(root);

        ChangeVerificationResult result = Verify(fixture);

        // Two real changes and an empty document: every one of them is an undeclared change, and an
        // empty list must not read as agreement.
        Assert.False(result.Agrees);
        Assert.Equal(2, result.Findings.Count);
        Assert.Equal(0, result.DocumentedCount);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Two folders to compare and the text of the document describing them.</summary>
    private sealed class Fixture
    {
        public Fixture(string baseFolder, string modifiedFolder)
        {
            BaseFolder = baseFolder;
            ModifiedFolder = modifiedFolder;
        }

        public string BaseFolder { get; }

        public string ModifiedFolder { get; }
    }

    /// <summary>
    /// A tree where one file was edited, one was added and one is identical, which between them
    /// produces every kind of claim a document can make.
    /// </summary>
    private static Fixture Build(TempDirectory root)
    {
        (string baseFolder, string modifiedFolder) = MakePair(root);

        Write(modifiedFolder, "src/Edited.cs", "after the change");
        Write(modifiedFolder, "src/Added.cs", "brand new");

        return new Fixture(baseFolder, modifiedFolder);
    }

    /// <summary>
    /// The same pair with <c>src/Old.cs</c> moved to <c>src/New.cs</c> and nothing else changed, so a
    /// move is the only difference and nothing else can be mistaken for one.
    /// </summary>
    private static Fixture MovedFile(TempDirectory root)
    {
        (string baseFolder, string modifiedFolder) = MakePair(root);

        // Within the modified copy only: the base tree keeps the file at its old path, which is what
        // makes the pair a move rather than a deletion with nothing left to compare.
        File.Move(
            Path.Combine(modifiedFolder, "src", "Old.cs"),
            Path.Combine(modifiedFolder, "src", "New.cs"));

        return new Fixture(baseFolder, modifiedFolder);
    }

    /// <summary>
    /// Runs the cross-check with the given paths written into the document as a bulleted list, which
    /// is how a change document states one.
    /// </summary>
    private static ChangeVerificationResult Verify(Fixture fixture, params string[] listed)
    {
        IList<string> paragraphs = new[] { ChangeDocumentParser.DefaultStartMarker }
            .Concat(listed.Select(path => Bullet + " " + path))
            .ToList();

        IList<DocumentedFile> documented = ChangeDocumentParser.Parse(paragraphs);

        FolderComparison comparison = new FolderComparer()
            .Compare(fixture.BaseFolder, fixture.ModifiedFolder);

        return new ChangeDocumentVerifier().Verify(documented, comparison);
    }

    /// <summary>
    /// Two identical trees, one named base and one named modified.
    /// </summary>
    /// <remarks>
    /// Returned as a pair rather than as one value because the copy helper answers with its
    /// destination, and binding that to a variable called the base folder compares the modified tree
    /// against itself - which reports nothing and looks like a pass.
    /// </remarks>
    private static (string Base, string Modified) MakePair(TempDirectory root)
    {
        string baseFolder = MakeTree(root, "base");

        return (baseFolder, CopyTree(baseFolder, MakeEmptyFolder(root, "modified")));
    }

    // -------------------------------------------------- matching a claim to a real path

    /// <summary>
    /// A claim is read from the right, so a path written from part way down names the file and is not
    /// reported.
    /// </summary>
    /// <remarks>
    /// The file is not in doubt: the tail of the claim is the whole of what identifies it. A document
    /// that wrote fewer folders than it meant to has not made a false claim about anything, so the
    /// claim is a match and the check stays quiet.
    /// </remarks>
    [Fact]
    public void A_path_written_from_the_middle_names_the_file_and_is_not_reported()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = MakePair(root);

        Write(baseFolder, "TestAuto Layer/api/app_management/_shared.py", "before");
        Write(modifiedFolder, "TestAuto Layer/api/app_management/_shared.py", "after");

        ChangeVerificationResult result = Verify(
            baseFolder,
            modifiedFolder,
            "api/app_management/_shared.py");

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.Matches);
    }

    /// <summary>The same, with only the last two segments written down.</summary>
    [Fact]
    public void A_path_missing_its_root_matches_the_file_it_names()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = MakePair(root);

        Write(baseFolder, "Src/Module1/View2/Frames/wrwe.cs", "before");
        Write(modifiedFolder, "Src/Module1/View2/Frames/wrwe.cs", "after");

        // The shape the section is written in: rooted at the repository rather than at the folder the
        // comparison walks, or written from the innermost folder outwards.
        foreach (string claim in new[]
        {
            "Src/Module1/View2/Frames/wrwe.cs",
            "Module1/View2/Frames/wrwe.cs",
            "View2/Frames/wrwe.cs",
            "Frames/wrwe.cs",
        })
        {
            ChangeVerificationResult result = Verify(baseFolder, modifiedFolder, claim);

            Assert.Empty(result.Findings);
            Assert.Equal(1, result.Matches);
        }
    }

    /// <summary>
    /// How the two sides are put in one form before they are compared.
    /// </summary>
    /// <remarks>
    /// Separators and a leading one are spelling, not difference: a document written on Windows spells
    /// a path with backslashes, a folder walk spells it with slashes, and <c>\Src\Parser.cs</c> names
    /// the same file as <c>src/Parser.cs</c>. Requiring them to be written identically would report
    /// every claim in such a document as missing.
    /// </remarks>
    [Fact]
    public void Separators_and_a_leading_slash_are_spelling_rather_than_difference()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = MakePair(root);

        Write(baseFolder, "Src/Module1/wrwe.cs", "before");
        Write(modifiedFolder, "Src/Module1/wrwe.cs", "after");

        foreach (string claim in new[] { "\\Src\\Module1\\wrwe.cs", "/Src/Module1/wrwe.cs", "Src/Module1/wrwe.cs" })
        {
            ChangeVerificationResult result = Verify(baseFolder, modifiedFolder, claim);

            Assert.Empty(result.Findings);
            Assert.Equal(1, result.Matches);
        }
    }

    /// <summary>
    /// A folder the source does not spell that way names no file at all, and is reported as such.
    /// </summary>
    /// <remarks>
    /// <c>TestAutoLayer</c> and <c>TestAuto Layer</c> are two different folders. There is no partial
    /// answer here: the claim names one segment the source does not have, so it names nothing, and the
    /// reader is told which file was being claimed.
    /// </remarks>
    [Fact]
    public void A_folder_spelled_differently_names_no_file()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = MakePair(root);

        Write(baseFolder, "TestAuto Layer/api/_shared.py", "before");
        Write(modifiedFolder, "TestAuto Layer/api/_shared.py", "after");

        ChangeVerificationResult result = Verify(
            baseFolder,
            modifiedFolder,
            "TestAutoLayer/api/_shared.py");

        // Two findings, and both of them true: the claim names a file that is not there, and the file
        // that did change is not the one it named.
        ChangeFinding absent = Assert.Single(
            result.Findings,
            f => f.Kind == ChangeFindingKind.DocumentedButAbsent);

        Assert.Equal("TestAutoLayer/api/_shared.py", absent.DocumentedPath);

        Assert.Single(result.Findings, f => f.Kind == ChangeFindingKind.MissingFromDocument);
        Assert.Equal(0, result.Matches);
    }

    /// <summary>
    /// Two files ending with the same claim is no answer, so nothing is claimed.
    /// </summary>
    /// <remarks>
    /// Either file could be meant. Naming one would look identical to a fact in the output, and the
    /// reader would have no way to tell it was a coin toss.
    /// </remarks>
    [Fact]
    public void A_path_matching_several_files_is_left_unresolved_rather_than_guessed()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = MakePair(root);

        Write(baseFolder, "old/api/Reader.py", "one before");
        Write(modifiedFolder, "old/api/Reader.py", "one after");
        Write(baseFolder, "new/api/Reader.py", "two before");
        Write(modifiedFolder, "new/api/Reader.py", "two after");

        // "api/Reader.py" is the tail of both, so it names two files and therefore none.
        ChangeVerificationResult result = Verify(baseFolder, modifiedFolder, "api/Reader.py");

        Assert.Equal(0, result.Matches);
        Assert.Single(result.Findings, f => f.Kind == ChangeFindingKind.DocumentedButAbsent);
        Assert.Equal(2, result.Findings.Count(f => f.Kind == ChangeFindingKind.MissingFromDocument));

        // A claim that does settle it still matches: refusing to guess is not refusing to match. What is
        // left is the other file, which this document did not list - a true omission, not a guess.
        ChangeVerificationResult exact = Verify(baseFolder, modifiedFolder, "old/api/Reader.py");

        Assert.Equal(1, exact.Matches);
        Assert.Single(exact.Findings, f => f.Kind == ChangeFindingKind.MissingFromDocument);
    }

    /// <summary>
    /// The tail has to land on a separator, which is what makes it a path rather than a piece of a name.
    /// </summary>
    [Fact]
    public void A_suffix_has_to_land_on_a_separator()
    {
        using TempDirectory root = new();
        (string baseFolder, string modifiedFolder) = MakePair(root);

        Write(baseFolder, "src/LegacyReader.cs", "before");
        Write(modifiedFolder, "src/LegacyReader.cs", "after");

        // "eReader.cs" is the tail of LegacyReader.cs but not at a separator, so it is part of a
        // different name.
        ChangeVerificationResult result = Verify(baseFolder, modifiedFolder, "eReader.cs");

        Assert.Contains(
            result.Findings,
            finding => finding.Kind == ChangeFindingKind.DocumentedButAbsent);
    }

    [Fact]
    public void A_file_under_an_ignored_folder_is_answered_rather_than_called_missing()
    {
        using TempDirectory root = new();
        string baseFolder = MakeTree(root, "base");
        string modifiedFolder = CopyTree(baseFolder, root.PathFor("modified"));

        Write(baseFolder, "bin/App.dll", "before");
        Write(modifiedFolder, "bin/App.dll", "after");

        FolderComparer comparer = new FolderComparer();
        FolderComparison comparison = comparer.Compare(baseFolder, modifiedFolder);

        ChangeVerificationResult result = new ChangeDocumentVerifier().Verify(
            new List<DocumentedFile> { new DocumentedFile("bin/App.dll", 1, "bin/App.dll") },
            comparison);

        ChangeFinding finding = Assert.Single(result.Findings);

        // The comparison skipped it on purpose. Calling that "no such file" would be a wrong answer
        // to a question the tool never actually asked.
        Assert.Equal(ChangeFindingKind.NotCompared, finding.Kind);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Cross-checks a document listing the given paths against two folders.
    /// </summary>
    /// <remarks>
    /// The claims are handed straight to the verifier rather than written into a document and parsed.
    /// What is under test here is the matching, and a path built by the parser has already been
    /// normalised by the time it arrives - so feeding these in raw is also what proves the verifier
    /// normalises what it is given.
    /// </remarks>
    private static ChangeVerificationResult Verify(
        string baseFolder,
        string modifiedFolder,
        params string[] documentedPaths)
    {
        FolderComparison comparison = new FolderComparer().Compare(baseFolder, modifiedFolder);

        var documented = documentedPaths
            .Select(path => new DocumentedFile(path, 1, path))
            .ToList();

        return new ChangeDocumentVerifier().Verify(documented, comparison);
    }

    private static string MakeTree(TempDirectory root, string name)
    {
        string folder = root.PathFor(name);

        Write(folder, "src/Edited.cs", "before the change");
        Write(folder, "src/Untouched.cs", "identical");
        Write(folder, "src/Old.cs", "content that will move");

        return folder;
    }

    /// <summary>
    /// An empty folder for <see cref="CopyTree"/> to fill.
    /// </summary>
    /// <remarks>
    /// Creating it through <see cref="MakeTree"/> would put files in it, and copying onto files that
    /// are already there throws - which reads as a fault in the comparison rather than a mistake in
    /// the fixture.
    /// </remarks>
    private static string MakeEmptyFolder(TempDirectory root, string name)
    {
        string folder = root.PathFor(name);

        Directory.CreateDirectory(folder);

        return folder;
    }

    private static string Write(string folder, string relativePath, string content)
    {
        string full = Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));

        return full;
    }

    /// <summary>Copies a tree so the two folders start identical and only the test's edits differ them.</summary>
    private static string CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (string directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(from, to));
        }

        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = file.Replace(from, to);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return to;
    }
}
