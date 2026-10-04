# GitDiffFolderCreator

A small WPF tool that produces two folders containing **only the files that differ between two
commits** - one holding those files as they were at the base commit, one holding them as they are
at the modified commit. Point a LOC or code-comparison tool at the two folders and you compare the
change in isolation, without the rest of the repository.

## How it works

1. Set the **Git Directory** and pick **two commits** in the log.
2. The direction is resolved with `git merge-base --is-ancestor`, not from the order you clicked,
   so the base folder is always the older state. When neither commit is an ancestor of the other
   (divergent branches) the direction falls back to commit dates and the status line says so.
3. **Create Folders** writes, under the output directory:

   Only the files still ticked in the changed-file list are exported; unticked paths are left out of
   both folders, counted in the action bar, and reported as a warning in the run log rather than
   dropped silently.

   ```
   diff_<basehash>_<modifiedhash>_<yyyyMMdd-HHmmss>\
       base\
           ... every changed file as of the base commit ...
           (deleted and renamed-from files are included here, because they exist at this commit)

       modified\
           ... the same set as of the modified commit ...
           (added and renamed-to files are included here)

       DeletedFiles.txt     paths removed or renamed away by the modified commit
       RenamedFiles.txt     "old  ->  new" pairs
       ChangedFiles.txt     every changed path with its status code
   ```

Everything for one comparison lands in a single folder, so the two file sets and the manifests that
describe them can be moved, archived or deleted as a unit. Files that did not change are not
copied. Existing folders are never overwritten; a counter is appended to the new name instead.

The whole result is staged in a hidden `.gdfc-staging-*` folder and moved into place with a single
rename, so a failed or cancelled run leaves no partial output and there is never a moment where one
of the two folders exists without the other.

A path that does not exist at the commit it is being read from is reported as a warning rather than
failing the run - that is expected for the added file in `base\` and the deleted file in `modified\`.

## The branch indicator

The branch is shown as a badge at the left of the **Commit Log** row that holds Refresh, and clicking
it opens the branch browser. The badge describes **the branch on screen**, not whatever happens to be
checked out: once another branch is picked from the browser, the badge follows it. A warning about a
branch nobody is looking at would be noise.

When local commits and the upstream disagree it becomes a warning: a red badge with a triangle icon and
the divergence beside the name, `main  +2 -1`. When they agree the same badge is shown in neutral grey
with a tick, so the row does not reflow as the warning appears. A branch with no upstream shows just
its name. Before any repository is loaded neither badge is shown. Hovering spells the state out in words
and says whether the branch on screen is also the checked-out one.

Uncommitted work appears under the header in grey - `3 changed, 1 untracked` - and deliberately does
*not* raise the warning, because a dirty working tree is normal and is not what "out of sync" means.
Conflicted paths are counted separately and listed first.

The uncommitted-work state comes from `git status --porcelain=v2 --branch`, whose header lines are
machine readable and stable across locales, rather than the human-readable `git status`.
`--no-optional-locks` keeps the call from silently refreshing the index. A failure to read the status
is not fatal: the log still loads and the branch shows as `branch unknown`.

The ahead/behind counts come from `for-each-ref` instead, from `%(upstream:short)` and
`%(upstream:track)`. That is the same listing the branch browser is built from, so every branch's sync
state is known at once and the counts are current for the branch on screen without a second git call.

Detached HEAD reads `detached HEAD`, and a repository with no commits yet reads `no commits yet` -
git reports `(detached)` and `(unknown)` for those two cases, which are not the same thing.

## Reading the changed files

The list shows the status letter, the path and the size as `+12 -3` lines added and removed. Sizes come
from a separate `git diff --numstat` run joined onto the `--name-status` result by path, so the two
commands cannot disagree about which files exist. Binary files show `binary` rather than a misleading
zero, and a file with no reported count shows nothing at all - unknown is never rendered as "no lines
changed".

The text box filters on the path (including the old path of a rename) and the combo box filters on
status. The count beside them reads *12 of 300 files* whenever a filter is hiding something, so a
short list is never mistaken for a small diff.

**Choose files** reveals a check box beside every row, for exporting a subset. It says *choose* rather
than *select* because selecting rows of a list is a different gesture with its own control; **All** and
**None** set every box at once, and the choices survive switching the boxes off and on again.

Picking commits is capped at two. A third selection drops the oldest rather than leaving three
highlighted: the item just clicked always survives, including when it sits above the two it replaces,
because the rule works from the selection history rather than from row order.

## When the branches cannot be refreshed

Opening a repository runs `git fetch`, which is the only step that touches the network. It is skipped
when the repository has not changed since last time, and it is skipped entirely for a repository with
no remote - there is nothing to fetch, and nothing that can be out of date.

If a remote *is* configured and cannot be reached, the branch list is still shown and is still correct
for what is on disk, but it may be behind. The branch picker says so, in words, with a **Retry**:

```
⚠ Could not reach the remote. These branches are the ones on disk, as of the last fetch that worked.  [Retry]
```

The same sentence goes into the status line and the branch box's tooltip, because every ahead/behind
count the box shows is measured against an upstream ref that may itself be out of date - a branch can
read "level" here and still be behind the remote. A repository with no remote never gets the notice,
because nothing about it can be stale.

## Finding a commit

The log can hold five hundred entries and scrolling only finds one if you already know roughly where it
is, so the log has its own search box. `Ctrl+F` moves the caret into it with the text selected, which is
the whole point of the accelerator: the reason to reach for it is to type something, not to go and find
the box first.

The text matches, ignoring case, against the four things a reader actually has to hand: the message, the
full or abbreviated hash, the author, and the ref names - so `HEAD`, `origin/main`, `9c44dab` and
`mathewsgp` all find rows. Clearing it restores every commit rather than leaving the list half-built, and
the heading says *14 of 500 commits* whenever the search is hiding something. Nothing else in the window
is allowed to describe a commit the list is no longer showing: a count that survived a filter would be a
count of rows that cannot be clicked.

## Reading a whole commit

Each commit row carries a **⋯** button at its right edge. It appears when the pointer reaches the row or
when the row has keyboard focus, and it draws three dots rather than a caption - a permanent "Details"
on every row would be the loudest thing in a list whose real content is the commits.

It opens a modeless window with the full subject (the row shows one line), the author and date, the
committer and date *only when they differ* - which is what a rebased or cherry-picked commit looks like
- both parents of a merge, the refs pointing at the commit, the full 40-character hash so it can be
pasted back into a git command, the whole message including blank lines, and the files the commit
touched with their own filter and totals. A root commit says it has no parent rather than showing an
empty parents field. A commit that cannot be read says why in the window; it does not take the
application down.

Double-clicking a file in that list opens the same diff hand-off as the main window, for that one file
and that one commit: the version at the commit's **first parent** against the version at the commit,
which is the pair the file list itself is a summary of. A root commit therefore offers nothing to
compare, and says so. What the last launch said is shown beside the list, so a commit opened with no
diff tool configured reports that instead of appearing to do nothing.

## Showing one file's difference

Double-clicking a row in **CHANGED FILES** opens that single file's two versions in whatever diff tool
git is configured to use. Nothing is checked out and no file in the repository is read from disk.

Both versions are extracted with `git show` into a temporary folder and the two files are handed to
the tool. Each side is read at the path it had *at that commit*, so a rename is shown rather than
reported as missing. A file that exists at only one of the two commits - added, or deleted - is
compared against an empty file, because most tools treat a missing path as an error rather than as
"everything was added".

Extraction writes the raw bytes rather than decoding them, so a binary file reaches the tool intact
instead of arriving as replacement characters. The tool is started without waiting for it, since it
stays open until the user closes it and this window has to remain usable.

Both of git's conventions for `diff.tool` are honoured:

- **A command line.** The absolute path to the executable, optionally with its own arguments, quoted
  if it contains spaces. `C:\Program Files\Beyond Compare 4\BCompare.exe` is the common case, and the
  quoting is what stops the path being read as `C:\Program`.
- **A named tool.** `diff.tool` holding a bare name that resolves to `difftool.<name>.cmd`, whose
  `$LOCAL` and `$REMOTE` placeholders are filled in with the two files.

  ```
  git config --global difftool.bcomp.cmd '"C:\Program Files\Beyond Compare 4\BCompare.exe" "$LOCAL" "$REMOTE"'
  git config --global diff.tool bcomp
  ```

With nothing configured, the status line says so and names the command to run, rather than appearing
to do nothing.

### Choosing the tool

The button beside **CHANGED FILES** reads `Diff tool: <name>` and opens the picker. Tools are found by
looking in `Program Files`, `Program Files (x86)` and `%LocalAppData%` for the usual install
locations, and then on `PATH` - Beyond Compare, WinMerge, KDiff3, Meld, DiffMerge, TortoiseMerge,
Visual Studio Code and Notepad++ are recognised by name.

- **Browse** reaches anything not on that list, by choosing any `.exe`, `.cmd` or `.bat`. A tool
  outside the known list is not a reason to fall back to a command line.
- **Use git's** clears the choice, so whatever `diff.tool` says is used instead.
- The tool in use is marked **IN USE** in the list, and the command line that will actually be run is
  shown underneath, so the setting is not a black box.
- A tool chosen previously but no longer on disk stays in the list as *Previously chosen*, rather than
  silently vanishing while the setting still points at it.

The choice is written to this application's own settings file. **It does not modify your git
configuration** - choosing here leaves `git difftool` untouched, so a repository opened in another
tool still behaves as you set it up.

A chosen tool takes precedence over `diff.tool`; git is consulted only when nothing has been chosen
here, so an existing git configuration keeps working with no setup at all.

## Opening the result

**Open** beside the output directory opens the last run folder in File Explorer, falling back to the
configured output directory before any export has run. Because those are two different folders, the
caption says which one it means: **Open output folder** before the first export of a session,
**Open last result** afterwards, and the tooltip changes with it.

The same folder is revealed automatically when an export finishes, unless *Open the folder
automatically when the export finishes* is unticked; the choice is remembered in the settings file.

`explorer.exe` is started directly with no shell in between, so an output path containing shell
metacharacters is passed through as a pathname. Explorer's exit code is not inspected: it hands the
folder to an existing window and exits non-zero even on success.

### What a diff hand-off leaves behind

To give a diff tool two versions of a file, both are extracted into `%TEMP%\gdfc-compare-<id>`. They
are left there after a successful launch, because the tool may still be reading them - and it may
have been started by a launcher that exited immediately while its window is still open, so there is
no moment at which the launcher knows they are finished. Windows does not remove arbitrary temporary
files either.

So the cleanup is by age rather than by event: a later comparison deletes the folders carrying that
prefix which are more than a day old, skipping any it cannot delete - a diff window somebody has open
holds a lock, and that is expected. Nothing outside this program's own prefix is ever touched.

## Checking a change document against two folders (experimental)

**Verify change document…** in the OUTPUT section is a separate tool that does not use the repository
the window has open. It answers one question: **does this document list the files that actually
changed?** Give it the base source tree, the modified source tree and the Word `.docx` that describes
the change.

The list is read from one section of the document, bounded by two headings, so the prose around it -
the "Root cause" and "Fix" paragraphs, any example list earlier on - cannot turn into findings:

```
[Modified Files]:
 1.      \Src\Module1\View2\Source\View2\Frames\2222.xaml.cs
 2.      \Src\Module1\View2\Source\View2\Frames\24dsds.xaml.cs
 3.      \Src\Infrastructure\32fws\wrwe.cs

[Status]
 Passed
```

With the default markers, the headings may be written `[Modified Files]` or plain `Modified Files`, with
or without the colon, and
they only have to be at the start of the line - `[Status] - all green` and `[Modified Files] (Module 2):`
are recognised. Inside the section, numbering and bullets are optional, blank lines and commentary are
skipped without ending the list, two paths may share a line, and every path must contain a `/` or a `\` -
so a bare `Parser.cs` is not read as one. A space in a folder name is fine; a space in the file name is
not. The section may be repeated - one per module or per phase - and every repeat is read; what falls
between two sections belongs to neither.

**CHANGED FILES START MARKER** and **CHANGED FILES END MARKER** name those two headings, so a document
that does not use `Modified Files` and `Status` can still be read - type what your document actually
says. The marker is used exactly as typed, brackets, colon and all, less the whitespace around it; case
is ignored and the heading only has to be at the start of the line. **Defaults** puts `[Modified Files]`
and `[Status]` back. A marker left blank means the default rather than no marker, because a reader who
clears a box has not asked for a document that cannot be read. The two are remembered between sessions,
so nobody has to name their own headings twice.

Both sides of a comparison are normalised the same way: separators unified to `/`, surrounding
whitespace and slashes trimmed, a leading `./` dropped. Matching then reads each claim **from the
right**, on whole folders, and a claim matches when it is the whole of a real path or the tail of one.
So `32fws/wrwe.cs`, `Infrastructure/32fws/wrwe.cs` and the full path all name the same file, while
`eReader.cs` does not match `LegacyReader.cs` and a tail two different files could answer is reported as
naming nothing rather than guessed at.

Every disagreement is reported in both directions - a path listed but not changed, a path changed but not
listed, a path listed twice in one section, a file under a skipped folder (**Not compared**, because the
tool did not look). A path named in two different sections is not a duplicate: a document listing per
module is expected to name a shared file more than once. Build output (`bin`, `obj`, `.git`,
`node_modules` and the like) is ignored by default and the
verdict always states how many files were compared and how many were left out, so a pass is a statement
about the tree rather than an absence of complaints. **Copy CSV** and **Export…** take the findings to a
spreadsheet or a ticket.

---

## Paths with spaces and metacharacters

Tested end to end, not assumed. An export succeeds when filenames contain spaces, `&`, `%`, `^`, `!`,
`;`, parentheses, brackets, `#`, `@`, `{`, `}` and non-ASCII characters, and when the *output*
directory itself is something like `R&D & more (copy)`. Both are covered by tests that read the
extracted files back and compare content.

Three things make it work:

- **No shell anywhere.** `Process.Start` is called with `UseShellExecute = false` and the argument
  vector is handed to git directly. There is no `cmd` in the path, so `&` is just a character in a
  filename. This is why the earlier `cmd /c` approach was removed - it turned `a&b.exe` in a pathname
  into a command to run.
- **`WindowsArgumentString`** quotes only what needs it: space, tab and `"`. It implements the
  `CommandLineToArgvW` rules, including doubling the backslashes that would otherwise escape the
  closing quote. Deliberately it does *not* escape `&`, `%` or `^` - those are shell syntax, and
  there is no shell to interpret them. Escaping them would encode characters git must receive raw.
- **`-z` on every parsing command.** `diff` and `show` emit NUL-terminated, unquoted fields, so
  spaces and non-ASCII characters in pathnames need no unquoting on the way back in.

`git archive` is invoked with an explicit `--` before the pathname list, so a file called `HEAD` or
`master` is not mistaken for a ref. Extraction re-validates each entry against the destination folder
before writing it, so an archive entry cannot escape via `..`.

Characters Windows forbids in filenames - `< > : " / \ | ? *` - cannot appear in a tracked path at
all, so they are out of scope: git cannot store such a file in the first place.

## Layout

Four bands, top to bottom: the repository header, the two lists side by side, the action bar, and a
run log that only exists after a run.

```
Repository  [C:\...\SocketDeviceSimulator   ] [Browse...]
Branch  [! upload-code  +2 −1 ▾]  Show last [500] commits  [Refresh]     clean
──────────────────────────────────────────────────────────────────────────────
COMMITS              324 loaded              │ CHANGED FILES   c87034a → 2b67c7a  [Swap]
[search commits…]  [3 of 324]               │ [filter files…] [All ▾]  [Diff tool] 5
                                       │ ☑ M Scenario.cs        Models/   +23  −0
● 2b67c7a              [MODIFIED]      │ ☑ A Sample.simproj              +52  −0
  Add LoopUntil step…                 │ ☑ M Service.cs                  +75  −0
  OpenHands · Aug 1  [origin/main]     │ ☐ M MainWindow.xaml              +1  −0
                              [ ⋯ ]    │
● c87034a                [BASE]        │  3 of 4 file(s) · +151 −0 · excluded: 1
──────────────────────────────────────────────────────────────────────────────
OUTPUT  [C:\...\New folder  ] [Browse…] [Open last result]  [Verify change document…]
☑ Open the folder when finished  3 of 4 file(s) · +151 −0   [ Stop ]  ▓▓▓░░░
```

**The two lists are side by side** (`3*` / splitter / `4*`). They used to stack, each claiming a
fixed share of a window that was empty to the right; sharing the height and using the width fixes
both halves of that. The splitter drag previews rather than reflowing the lists as it moves.

**The direction is shown where it is decided.** Ancestry picks which of the two commits is the base,
and that used to be visible only in a heading over the file list. Each commit row now wears a `BASE`
or `MODIFIED` pill, so the answer appears on the row the user just clicked, live.

**Commit rows are two lines**: hash in monospace and the message on the first, author, date and refs
on the second. Refs are chips rather than a yellow highlight, so they read as metadata instead of
competing with the message. The row's **⋯** button is hidden until the pointer or the keyboard reaches
the row, and it sits in a fixed-width column, so appearing costs the row no width and moves nothing.

**The file list carries no legend.** Each row starts with a colour-coded status letter whose tooltip
names it (A green, M amber, D red, R blue), the file name in bold with its folder muted beside it, and
green/red line counts in fixed right-aligned columns. Untick a row to leave that file out of both
folders; the row stays visible and dimmed so the choice can be undone, and the excluded count appears
in the action bar.

**The action bar is always visible** and holds everything needed to start, watch and stop a run. The
export is the only control wearing the accent colour. **Stop** is enabled only while a run is in
progress, because a permanently enabled cancel button on an idle window is noise. Its content has no
horizontal padding, so all four bands start on one left edge.

**The log replaces the old empty Result panel.** It is hidden entirely until a run has produced
something, then folds.

`Swap` reverses the direction and rebuilds the list, which is useful when ancestry picks the opposite
end from what was meant.

## Reading another branch

Clicking the branch badge opens a browser of every local and remote-tracking branch, most recently
committed first. Choosing one loads that branch's log and the export reads from it. **Nothing is ever
checked out.**

The browser takes a filter box, because a repository can hold hundreds of branches and scrolling to one
is not finding it. Typing narrows the list by name, case-insensitively and ignoring surrounding spaces,
and the count beside the box says how much is showing - *2 of 43 match* - so an empty list is
distinguishable from a misspelled filter. The filter is cleared whenever the browser opens or closes,
because a filter left over from last time would hide the branch just chosen and look as though it had
vanished.

`Enter` takes the highlighted branch, `Esc` closes without choosing, and the arrow keys move the
highlight from the filter box. Moving the highlight deliberately does *not* read the log - only `Enter`
and clicking a row do - so arrowing down a long list is one pass rather than a dozen reloads. A branch
the filter has hidden loses the highlight for the same reason: it can no longer be chosen, and leaving
it named would describe a row that is not on screen.

Rows carry the divergence of their own branch (`+2 -1`), marked `REMOTE` or `CHECKED OUT` so neither
needs a legend.

That a branch can be read at all works because nothing in the export path needs a working tree. `git log <ref>`,
`git diff <base>..<modified>` and `git archive <ref> -- <paths>` all read the object database, so any
ref will do - a local branch, `origin/main`, a tag, or a bare hash. The comparison can therefore span
two branches that are not on the same line of development, and the working tree is left exactly as it
was found.

The badge names whichever branch is on screen, so the checked-out branch is never mistaken for the one
being read. Its tooltip says which of the two is which.

Two details worth knowing:

- **The list is refreshed from the remote when a repository is opened.** Pointing the window at a
  repository runs `git fetch` first, so a branch somebody else pushed is there on arrival instead of
  after a manual fetch. It happens once per repository, keyed on the repository *root* rather than the
  typed path, so moving from a subfolder to its parent does not fetch again and pressing **Refresh**
  does not fetch again. It is the only operation here that touches the network, and it only ever
  updates remote-tracking refs: no merge, no checkout, no change to a local branch.
  `GIT_TERMINAL_PROMPT=0` is set for it, because a window with no console cannot answer a credential
  prompt and a hung prompt looks like a hung application. A repository with no remote is not an error,
  and neither is being offline: the list is simply built from the refs already on disk.

## Switching repositories

Everything on screen describes one repository, so all of it is dropped the moment the repository path
changes - commits, files, branches and the selected branch - before any git call for the new
repository begins.

This matters because the path is typed. Between the keystroke that changes it and the moment the new
repository finishes loading, the window would otherwise be showing one repository's branches while the
text box names another, and the branch picker would offer refs that do not exist in what is now open.
Clearing first also covers the case where the new path is never a valid repository at all: there is no
later step to replace the list, so anything left behind would simply persist.
- **`origin/HEAD` is left out.** It is a symbolic pointer at the default remote branch rather than a
  branch of its own, so listing it would offer a second entry for a branch already shown.

The list is produced by `for-each-ref`, whose format atoms differ from the `pretty` format's: the field
separator is written `%1f`, not `%x1f`. Passing `%x1f` there emits those four characters literally and
leaves every field unseparated.

## Panels

The remaining band-level container is a `CollapsibleGroupBox`
(`Controls/CollapsibleGroupBox.cs`): a `HeaderedContentControl` with a `ControlTemplate`, rather than
a themed `GroupBox` - the stock template has nowhere to put a chevron or a hover state, and restyling
it means overriding its internals. Only the run log still uses one; the header bar and the two lists
are plain grids, because they have no folding to do.

- Click the heading to fold or unfold. The header is a real `ToggleButton`, so tab order, Space/Enter
  and the automation name come from the platform instead of being reimplemented.
- The chevron rotates a quarter turn when the panel opens, so the state does not depend on colour.
- A panel set to `IsCollapsible="False"` hides its chevron and drops the hand cursor, so it does not
  advertise an affordance it does not have.
- A panel set to `ShowHeader="False"` has no caption and no rule under it (`PanelHeaderless`).
- Folded panels are remembered between sessions, so a window arranged once stays arranged. Names of
  panels that no longer fold are still read from an older settings file and ignored, rather than
  treated as an error.

## Requirements

- Windows with .NET Framework 4.6.1 or newer
- `git` on `PATH`

The application targets `net461`. The test project targets `net472`, because the current VSTest host
requires 4.6.2 or newer; it still runs the same application code.

## Build and test

```powershell
dotnet build GitDiffFolderCreator.sln
dotnet test  GitDiffFolderCreator.Tests\GitDiffFolderCreator.Tests.csproj
```

The test suite drives real git repositories created in a temporary folder, so it exercises the
actual output format of the installed git rather than canned strings.

## Project layout

| Path | Responsibility |
| --- | --- |
| `Services/GitProcessRunner.cs` | Launches git without a shell; separates stdout from stderr, checks exit codes, honours cancellation |
| `Services/WindowsArgumentString.cs` | Quotes the argument vector for `ProcessStartInfo.Arguments`, which is all .NET Framework offers |
| `Services/ShellFolderPicker.cs` | Browse button: the standard shell open dialog in folder-picker mode |
| `Services/ShellFolderOpener.cs` | Reveals a folder in File Explorer |
| `Services/GitService.cs` | The only class that runs git: log, diff, ancestry, branches, fetch, file export |
| `Services/DiffExporter.cs` | Turns a commit range into the two folders and the manifests |
| `Services/DiffToolLauncher.cs` | Hands one changed file's two versions to the configured diff tool |
| `Services/DiffToolCatalog.cs` | Finds the comparison tools installed on this machine |
| `ViewModels/DiffToolPickerViewModel.cs` | The tool list, which one is in use, and the resulting command line |
| `Behaviors/DoubleClickBehavior.cs` | Raises a command when a list row is double-clicked |
| `Services/ShellFilePicker.cs` | Browse button for "choose any program" in the diff tool picker |
| `Services/CommitRangeResolver.cs` | Decides which selected commit is the base |
| `Services/AppSettingsStore.cs` | JSON settings under `%LocalAppData%` |
| `ViewModels/GitViewModel.cs` | UI state, debouncing, error reporting |
| `ViewModels/CommitDetailViewModel.cs` | One commit on demand: its metadata, its files, its totals, and its per-file diff |
| `Services/DocxTextReader.cs` | Reads the text out of a Word `.docx`, one entry per paragraph or table row |
| `Services/ChangeDocumentParser.cs` | Recovers the documented file list from the document's prose |
| `Services/FolderComparer.cs` | Compares two source trees, ignoring build output by default |
| `Services/ChangeDocumentVerifier.cs` | Cross-checks a change document against two folders, in both directions |
| `Services/ChangeReportWriter.cs` | Writes the findings out as CSV or text, quoted and UTF-8 with a BOM |
| `ViewModels/ChangeDocumentViewModel.cs` | The experimental checker's state, its findings and its commands |
| `Behaviors/RowHover.cs` | Publishes whether a row is hovered or holds focus, so a row's own controls can stay out of the way |
| `Controls/CollapsibleGroupBox.cs` | The folding panel: header, chevron, rule and body |
| `Controls/BindingConverters.cs` | Small visibility rules the list rows bind to |
| `Models/GitBranch.cs` | One row of the branch browser: name, date, hash and its upstream divergence |
| `Behaviors/SelectedItemsBehavior.cs` | Bindable `ListBox.SelectedItems`, so there is no view logic in the code-behind |
| `Models/` | `GitCommit`, `GitFileChange` |

## The folder picker

`FolderBrowserDialog` renders a legacy tree view: no address bar, no search, and reaching a nested
or off-tree path means clicking through every level. It is replaced by the standard open dialog -
the same one File Explorer uses, with the navigation pane, address bar, search and Quick Access.

It becomes a folder picker by disabling file-name validation (`ValidateNames = false`,
`CheckFileExists = false`) and pre-filling the name box with the folder that was last used, so
pressing **Open** selects the folder currently being viewed. The dialog opens in the remembered
folder when one is remembered, and in `My Computer` otherwise.

Each Browse button passes its own caption, so the dialog says *Select the git working folder* or
*Select the output folder* rather than a generic string.

The dialog returns whatever was in the name box, which is a folder path rather than a file path in
this mode. `ShellFolderPicker.ResolveChosenPath` turns that into a folder and is unit tested,
including the cases where a bare name is relative to the opened folder and where the name box holds
something that is not a folder at all.

## Notes on the .NET Framework 4.6.1 target

The application stays on 4.6.1, which rules out a few APIs that the rest of the code would otherwise
use. Each is replaced rather than worked around:

| Not available on 4.6.1 | Replacement |
| --- | --- |
| `ProcessStartInfo.ArgumentList` | `WindowsArgumentString` builds a correctly quoted command line |
| `Process.WaitForExitAsync` | `Process.Exited` bridged onto a `TaskCompletionSource<int>` |
| `System.Text.Json` | `DataContractJsonSerializer` |
| `Microsoft.Win32.OpenFolderDialog` | `System.Windows.Forms.FolderBrowserDialog` |
| `ZipFileExtensions.ExtractToFile(…, overwrite)` | Delete the existing file first |
| `Path.GetRelativePath` | Prefix subtraction |
| `record`, `init`, `required` | Classes with constructor-assigned properties |

Nullable reference types are enabled, but the 4.6.1 reference assemblies carry no annotations, so
the compiler cannot narrow after `string.IsNullOrWhiteSpace`; the affected spots use `!` with a
comment.

## Notes on git usage

- **No shell.** Arguments are quoted into a command line and handed straight to git. A pathname such
  as `a&calc.exe` is therefore passed through as a pathname - the previous `cmd /c` approach
  executed it.
- **NUL-delimited output.** `git log` uses `%x1E`/`%x1F` separators and `git diff` uses `-z`, so
  spaces, non-ASCII characters and commit messages containing `;;;` or `|||` parse unambiguously.
- **Full hashes.** Operations use `%H`; the abbreviated `%h` is only ever displayed.
- **Batching.** `git archive` is invoked in batches sized to stay well inside the Windows command
  line limit. A batch that git rejects is split and retried, so one path that does not exist at a
  commit cannot discard the rest of the batch.
- **Symlinks and submodules** are exported as git stores them: a symlink becomes a file containing
  its target, and a submodule becomes a gitlink entry. Git LFS content is exported as the pointer
  text, not as the real blob.