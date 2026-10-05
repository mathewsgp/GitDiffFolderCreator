# 6. User Manual

**GitDiffFolderCreator** — produces two mirrored folder trees, `base/` and `modified/`, showing
both sides of a change between two commits, for use with any diff tool.

---

## 6.1 Requirements

| | |
| --- | --- |
| **Operating system** | Windows 10 or later |
| **Git** | Installed and on `PATH` |
| **Runtime** | .NET Framework 4.6.1 or later (included with Windows 10+) |
| **Privileges** | None. The application never needs administrator rights. |

Git is invoked as an external program. If Git is not on `PATH`, loading a repository will fail
with a message saying so.

---

## 6.2 What it does

Pick two commits → see which files changed → export the base and modified version of each into
one folder. Then open that folder in your diff tool, or zip it and send it to someone.

It does not show diffs itself. It hands two folders to whatever tool you already use.

There is also a separate, experimental tool that has nothing to do with commits: give it two source
folders and a Word document listing the changed files, and it reports where the document and the
source disagree. See §6.7.

---

## 6.3 The window at a glance

```
┌──────────────────────────────────────────────────────────────────────┐
│ REPOSITORY [C:\src\my-project   ] [Browse…] SHOW LAST  [500] commits │
│ BRANCH    [main              ▾]  [Refresh]  3 files changed          │
├────────────────────────────────┬─────────────────────────────────────┤
│ COMMITS            324 loaded │ CHANGED FILES  base → modified       │
│ [search commits…] [3 of 324]  │      [Swap] [Diff tool] [Verify…]    │
│ [Select] [All] [None]           │ [filter files…] [All ▾]             │
│  8e4d2f2  Cleanup: Remove du…  │ ☑ M  drivers/context_resolver.py    │
│          mathewsgp · 10:14 ·HEAD│ ☑ A  docs/plan.md         +82  −0  │
│        [BASE]              [ ⋯ ]│ ☑ D  framework/legacy.cs             │
│                                │                                     │
│  9c44dab  Phase 3: Multi-App…   │                                     │
│          mathewsgp · 09:59      │                                     │
├────────────────────────────────┴─────────────────────────────────────┤
│ OUTPUT                                                               │
│  [C:\Users\me\Desktop\Review    ] [Browse...] [Open last result]     │
│  ☐ Open the folder when finished                                     │
│  2 of 40 file(s) · +340 −56   [Verify change document…]              │
│  324 commits loaded from C:/src/my-project · branches updated          │
│                             [Stop]  [ Create Folders ]               │
├──────────────────────────────────────────────────────────────────────┤
│ ▾ Run Log                                                          ▲ │
│   Result:   C:\Users\me\Desktop\Review\diff_8e4d2f2_9c44dab_...     │
│   Exporting base files…                                            │
└──────────────────────────────────────────────────────────────────────┘
```

The top card is one row: **Repository**, **Branch**, **Show last**, then **Refresh**. The uncommitted-work
count sits at the right of that row and is the one item there that may be shortened — the repository path is
the only field whose text can be arbitrarily long, so it is the one that gives ground when the window is narrow.

---

## 6.4 Getting started

### 6.4.1 Choose a repository

Type or paste a path into the **Repository** box, or click **Browse…**. You may point at any
directory inside a working tree — the repository root is found automatically.

The first load runs `git fetch` to pick up remote-tracking branches. This is the only step that
touches the network, and it is skipped when the repository has not changed since last time.

If the fetch cannot reach the remote, the branch list is still shown — it is the right list, just not
a current one — and the branch picker says so:

```
⚠ Could not reach the remote. These branches are the ones on disk, as of the last fetch that worked.  [Retry]
```

**Retry** tries again. Press it once the network or the credentials are sorted out. The same sentence
appears in the status line and in the branch box's tooltip, so you do not have to open the picker to
find out. A repository with **no remote at all** gets none of this: there is nothing that can be out
of date, so nothing is said.

### 6.4.2 Choose a branch (optional)

Click the **Branch** box to see local and remote branches. Pick one to limit the commit list to
that branch, or clear it to see everything. Branches marked **CHECKED OUT** are shown in green
with a tick; **REMOTE** groups the remote-tracking ones.

If your branch is behind its upstream, the branch box says so. Pull from your Git client, then
click **Refresh**.

### 6.4.3 Set how many commits to load

**Show last** is in the **COMMITS** panel header, beside **Refresh**, because both act on that list
and on nothing else.

**Show last** defaults to 500. Raise it for a long history, lower it for speed. Only the commits
needed to pick two from are loaded — this is not a history browser. Click **Refresh** to re-read
the list after changing it, after changing the branch, or to pick up new commits.

### 6.4.4 Pick two commits

Click a commit, then another. The list accepts exactly two.

The application works out the direction by asking Git which commit is an ancestor of the other —
not by list order. Both commits are labelled in the list:

- **BASE** — the older side
- **MODIFIED** — the newer side

If the two commits are on diverged branches and neither is an ancestor of the other, the order
is taken from their dates and the file-list heading says so. That comparison is legitimate but
not verified by ancestry; use **Swap** if you want the other direction.

### 6.4.5 Review the changed files

The right-hand pane lists every differing file, with:

| | |
| --- | --- |
| Badge | `A` added, `M` modified, `D` deleted, `R` renamed |
| Path | file name in bold, folder muted |
| `+n −n` | lines added and deleted |

Hover a badge for the full status text.

Narrow the list with the **filter** box (matches any part of the path, ignoring case) or the
status dropdown. The count beside them always reads "shown of total", so a short list is never
mistaken for a small change.

**Double-click any file** to open its two versions in your diff tool. See §6.6.

### 6.4.6 Export

Set the **OUTPUT** folder, then click **Create Folders**.

You get a new folder named `diff_<base>_<modified>_<timestamp>` containing:

```
diff_8e4d2f2_9c44dab_20261002-142955\
    base\
        drivers\context_resolver.py        ← the old version
        docs\plan.md                       ← empty; this file was added
    modified\
        drivers\context_resolver.py        ← the new version
        framework\legacy.cs                ← empty; this file was deleted
    DeletedFiles.txt
    RenamedFiles.txt
    ChangedFiles.txt
    ExportedFiles.txt
```

Both sides mirror the repository layout, so a folder-level diff tool compares them correctly
with no configuration.

Each of the first three files opens with a line saying how many entries follow, so you never have to
scroll to the end to know whether a list is short or the file failed to write:

```
# 3 rename(s)
src/Old.cs
src/New.cs
vendor/lib.js
```

A list with nothing in it is still that one line — `# 0 rename(s)` — rather than an empty file, which
would read the same whether there was nothing to list or nothing was written.

`ExportedFiles.txt` is a different thing, and the difference is why it is there. The other three
describe *the comparison*; this one describes *what was written*, which is what your diff tool is
about to look at. They differ whenever something was legitimately not written — an addition has no
base version, and a pair of paths differing only in case keeps one file rather than two on Windows —
and a diff tool cannot tell that apart from a real omission:

```
# base: 2 file(s)
drivers/context_resolver.py
docs/plan.md

# modified: 2 file(s)
drivers/context_resolver.py
framework/legacy.cs
```

Tick **Open the folder when finished** to have Explorer open it for you. Otherwise use **Open**, which
says which folder it means: **Open output folder** before the first export of a session, and **Open
last result** afterwards, pointing at the folder that export produced.

---

## 6.5 Exporting only some files

The per-file checkboxes are hidden by default, because exporting the whole range is the common
case and a column of ticks is something you have to look past on every row.

To choose a subset:

1. Click **Choose files**. A check box appears beside each file. Every file starts ticked.
2. Untick what you do not want, or:
   - **All** — tick everything
   - **None** — untick everything, then tick only the few you want

3. Clicking **Choose files** again hides the checkboxes. **Your choices are kept** — hiding them does
   not quietly put the excluded files back.

The summary line updates as you go: `12 of 40 file(s) · +340 −56`. Untick every file and
**Create Folders** disables itself, because there would be nothing to write.

Files left out are reported in the run log, so a partial export is never mistaken for a
complete one.

---

## 6.6 Comparing one file in your diff tool

**Double-click** a file in the changed-files list. The application extracts both versions to
temporary files and launches your diff tool on them.

Added and deleted files work too: the missing side is created as an empty file, so the tool
always gets a real pair to compare.

Temporary files are cleaned up afterwards. A tool that is still holding a file open when the
application closes is left alone — cleanup never fails noisily over a lock.

The button showing your current tool sits next to the file-list heading, so you can see what is
configured before you try. Click it to change.

### Choosing a diff tool

Click the tool button and pick one. These are detected automatically if installed:

Beyond Compare · WinMerge · KDiff3 · Meld · DiffMerge · TortoiseMerge · Visual Studio Code · Notepad++

Or choose **Custom…** and give a full command line, for example:

```
"C:\Program Files\Foo\foo.exe" "%1" "%2"
```

A tool that is installed somewhere other than the places checked will not appear. Both per-user and
machine-wide installs are searched for each, because installers differ: Visual Studio Code, for
instance, is at `%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe` when installed for the current
user and `%ProgramFiles%\Microsoft VS Code\Code.exe` when installed for all users, and it is only
the machine-wide copy that appears directly under `%ProgramFiles%`. **Custom…** reaches anything the
list misses.

Your choice is remembered.

If no tool is configured, double-clicking says so rather than doing nothing.

---

## 6.7 Verifying a change document (experimental)

A separate tool, unrelated to the repository the window happens to have open. It answers one
question: **does this document list the files that actually changed?**

Click **Verify change document…** in the OUTPUT section, beside **Create Folders**. It is
experimental and unrelated to an export; the full description is on its tooltip. Give it three
things:

| | |
| --- | --- |
| **Base source** | The source tree *before* the changes |
| **Modified source** | The source tree *containing* the changes |
| **Change document** | The Word `.docx` that lists the changed files |

Two boxes below those name the headings the list is read between, and are remembered between sessions.
They only need changing if your document does not use `[Modified Files]` and `[Status]` — see *If your
document names its sections differently* below.

Press **Verify**. You get a verdict, the counts, and a list of everything the two disagree about:

| Finding | What it means |
| --- | --- |
| Listed twice | The same path appears twice **in one section** — a duplicated entry |
| Wrong path | The file was **moved** rather than edited |
| Listed, not present | The document names a file the modified source does not have |
| Listed, not changed | The document claims an edit that is not there: both sources are identical |
| Changed, not listed | A real change the document never mentions |
| Added / Deleted, not listed | A file only in one of the two sources, unmentioned |
| Not compared | The path is under `bin`, `obj` or similar and the check skipped it |

### How the document has to be written

The list is read from one section, bounded by two headings:

```
[Modified Files]:
 1.      \Src\Module1\View2\Source\View2\Frames\2222.xaml.cs
 2.      \Src\Module1\View2\Source\View2\Frames\24dsds.xaml.cs
 3.      \Src\Module1\View2\Source\View2\Frames\xdsf.xaml.cs
 4.      \Src\Infrastructure\32fws\wrwe.cs
 5.      \Src\Infrastructure\w322\wewer.xaml.cs

[Status]
 Passed
```

With the default markers the headings are `[Modified Files]` and `[Status]`. Everything between them is
read as the list; everything outside it is ignored, so the "Root cause" and "Fix" paragraphs — and any
example list earlier in the document — cannot turn into findings.

### If your document names its sections differently

Change documents are written by hand, so the two headings are not standardised. Two boxes under the
document path name them:

| Box | Default | What it means |
| --- | --- | --- |
| **CHANGED FILES START MARKER** | `[Modified Files]` | the heading the list starts at |
| **CHANGED FILES END MARKER** | `[Status]` | the heading the list ends at |

Type what your document actually writes. **The marker is used exactly as you typed it** — nothing is
taken off it except the whitespace around it, so the brackets and the colon are matched as characters
and come back unchanged the next time you open the window. Case is ignored, and a heading only has to
be at the *start* of the line, so `[Status] - all green` and `[Modified Files] (Module 2):` are
recognised while "the Modified Files section below lists…" is still not a heading. **Defaults** puts both
back.

One thing to know before you type: a heading is matched as written, not as a spelling. A document that
writes `Modified Files` — no brackets — is a different heading from `[Modified Files]`, so it needs the
marker set to `Modified Files`. There is no second reading of a marker to catch both, because a marker
that matches two different headings cannot say which section it meant.

**The section may appear more than once.** Every section the two markers bound is read, so a document
written as one list per module or per phase is checked in full rather than only in its first part.
Whatever sits *between* two sections belongs to neither — a summary table, or a list of files planned
for a later phase, is not read.

**A file named in two sections is not a problem.** If two of a document's lists name the same file —
because a shared file belongs to both modules, or one section writes `src/Parser.cs` and the next
writes `Parser.cs` — that is the document being correct, and it is not reported. **Listed twice** means
the same path appears twice *within one section*, which is a duplicated entry. A file claimed in two
sections is still counted once in the totals, because it is one change.

Two details worth knowing:

- **A blank box means the default**, not "no marker". Clearing the box is treated as "I did not mean to
  change this", because a document read with no opening marker lists nothing and the verdict then
  reports every real change as undeclared.
- **The markers are remembered** between sessions, so a document that uses `## Files Changed` only has to
  be described once. The two are saved together, because half a pair is a list read in the wrong place.

Inside the section:

- **Numbering and bullets are optional.** `1.`, `2)`, `(3)`, `-` and `•` are stripped, and a path is a
  path without them.
- **Blank lines and sentences in between are skipped, not treated as the end of the list.** The list
  carries on until `[Status]` or the end of the document.
- **Two paths may share a line**, separated by a tab or by two or more spaces.
- **Every path must contain a `/` or a `\`.** A bare `Parser.cs` or `Dockerfile` is not read as a
  path — write the folder it is in. A space inside a *folder* name is fine
  (`TestAuto Layer/api/Reader.py`); a space inside the file name is not.
- **`\` and `/` are the same separator**, and a leading `\` or `/` and a leading `./` are dropped.
  So `\Src\Parser.cs`, `/Src/Parser.cs` and `./Src/Parser.cs` all name `Src/Parser.cs`.

A document without a `[Modified Files]` section — and so does one whose start marker is not in it — lists
nothing, and the verdict then reports every real change as undeclared rather than agreeing with a list
the document never made.

### Paths need not start at the repository root

Each path is matched against the real paths from the right, on whole folders: a path in the document
counts as a match when it is the whole of a real path or the tail of one. So all of these name
`Src/Infrastructure/32fws/wrwe.cs`:

| In the document | Also matches |
| --- | --- |
| `Src/Infrastructure/32fws/wrwe.cs` | the exact path |
| `Infrastructure/32fws/wrwe.cs` | written from the module down |
| `32fws/wrwe.cs` | written from the folder down |

The tail has to land on a folder boundary, so `eReader.cs` does **not** match `LegacyReader.cs`. If two
different files end with the same path — two folders each carrying a copy, which real trees do — the
claim names the first of them by name, so the same document and the same tree always give the same
answer. The other file is then reported as a change the document did not list, which is a statement
about the document rather than a guess about which file was meant.

**Build output is ignored by default.** `bin`, `obj`, `.vs`, `.git` and the like are skipped,
because a compiled assembly differs on every build and would drown the real changes. Clear the
checkbox to include them. A claim about a file under a skipped folder is reported as **Not
compared** rather than as missing — the tool did not look, and it will not pretend otherwise. The
line under the verdict always says how many files were compared and how many were left out, so a
pass is never a claim about more than was examined.

**Paths differing only in case are not a mismatch.** Windows cannot hold two such files, so a
document spelling the path differently is not making a different claim.

### Taking the findings elsewhere

Two buttons sit beside **Verify**:

- **Copy CSV** — the whole grid to the clipboard, ready to paste into a spreadsheet or a ticket.
- **Export…** — writes it to a file. The format follows the extension: `.csv` or `.txt`.

Both are greyed out until a check has run.

---

## 6.8 Searching the commit log

A **search box** sits between the commit list's toolbar and the list itself, with the count of matches
beside it. `Ctrl+F` puts the caret there with the text selected.

It searches the commits already loaded — it does not re-read the log, so a keystroke is instant and
costs nothing. The needle is matched, ignoring case, anywhere inside a single one of:

| Field | Why |
| --- | --- |
| **Message** | The reason you are usually looking |
| **Hash**, long or short | What gets pasted in from a review, a chat or another tool |
| **Author** | A busy log is often scanned by who wrote it |
| **Refs** | "Which commit did I put on the release branch?" |

The count beside the box is what makes a search that matches nothing readable — otherwise an empty list
is indistinguishable from a repository with no commits in it. It reads `N of M commits match` while the
search is narrowing things down, and `M commit(s)` when it is not.

Two things worth knowing:

- **A commit the search hides is dropped from your selection.** You compare two commits, so a
  selection left pointing at a row that is no longer listed would compare something you can no longer
  see and cannot re-pick.
- **The search is cleared when you change repository.** A search left over from the last repository
  would hide the commits of this one and look as though they had gone missing.

---

## 6.9 Reading a whole commit

Every commit row has a **⋯** button at its right edge, which appears when you point at the row or when
it has keyboard focus. It opens a window with:

- The **full subject** — the list row truncates it to one line
- **Author** and date, and **Committed by** and date *only when they differ* (a rebased or cherry-picked commit)
- **Parents** — both of them for a merge; a root commit has none
- **Refs** — branches and tags
- The **full 40-character hash**, so it can be pasted back into a Git command
- The **full message**, including blank lines, if there is more than a subject
- **Files changed**, with a summary and a filter box

The window is modeless, so you can leave it open and pick another commit. It does not modify
anything. The button is hidden until you reach the row with the pointer or the keyboard, so it costs a
committed row no vertical space and no permanent caption — point at the row, or arrow to it, and the
⋯ appears.

### Seeing one file's change

**Double-click a file** in the **Files changed** list and it opens in your diff tool, exactly as
double-clicking a file in the main window's list does. The two versions it shows are the file as it
was at this commit's **first parent** and as it is at this commit — the same pair the list itself is a
summary of, so what you see is what the list is telling you about.

A merge has two parents and is compared against the first, which is the conventional "what did this
branch do here" and what `git show` reports. A **root commit** has no parent, so there is nothing to
compare it against: the file list is then a list of files rather than a list of changes, and no diff
opens.

Whatever the last hand-off said is shown under the list, including the case where no diff tool is
configured — so a double-click that cannot open anything says so rather than appearing to do nothing.
The gesture is also in the list's tooltip, which is worth knowing the first time: the list is
read-only, so the double-click is the only thing it does.

---

## 6.10 The Run Log

A collapsible panel below everything else, shown once a run has produced something in it.

It records each stage, any warnings, and where the output went. It stays readable while the
export runs, so you can see what is happening without waiting.

---

## 6.11 Status line

At the bottom of the output panel. It tells you how many commits were loaded, from which
repository, and whether branches were updated from the remote. Errors appear here in red.

---

## 6.12 Stopping an export

Click **Stop**. The partial output is discarded and no run folder is left behind — a run folder
that exists is always complete.

## 6.12.1 Watching an export

A bar appears beside **Stop** for the length of the run, and the status line says what it is doing.

It slides for the first step — reading the list of changed files — because the size of that step is
not known until it finishes. After that the bar is a true count: it fills as files are written, and
the status line gives the exact figure, for example `Exporting file 64 of 128`. It covers both
sides of the comparison, so it counts to twice the number of changed files.

A path that cannot be produced — a file that was deleted, or a submodule, which Git holds as a
pointer to another repository rather than as content — still counts. The work is finished, so the
bar reaches the end and any such path is named in the warnings.

The bar always ends full. If it stops part-way, the export stopped with it.

---

## 6.13 What gets remembered

| Setting | Behaviour |
| --- | --- |
| Repository path | Restored on next launch |
| Output folder | Restored |
| Log size (Show last) | Restored |
| Open folder when finished | Restored |
| Diff tool | Restored |
| Collapsed panels | Restored |
| Whether file checkboxes are showing | **Not** remembered — it describes this session, not a preference |

---

## 6.14 Reading the run log warnings

| Warning | Meaning |
|---|---|
| `N of M path(s) were excluded by selection.` | You unticked some files. The export is partial by choice. |
| `modified: N path(s) in the base commit are not present in the modified commit…` | Those files were deleted between the commits, so the `modified` side has empty stand-ins for them. Expected for a deletion. |
| `base: N path(s) … are the same file on this filesystem…` | Two files in the commit differ only in case, or only in some other way Windows cannot tell apart — for example `Notes.txt` and `notes.txt`. Windows keeps one file, so one of the pair was not written. The warning names both. |
| `base: N path(s) are in the commit but are not files…` | The path is not a file at that commit — usually a submodule, which Git records as a pointer to a commit in another repository. There is no content to write, so no file was produced for it. The rest of the export is unaffected. |
| `N changed path(s) were not written and nothing accounts for it…` | A file the comparison asked for is missing from the folder and no other warning explains it. This is the last line of defence, and seeing it means a file is genuinely absent rather than accounted for elsewhere. |
| `N file(s) are in the folder but are not in the change list…` | A file is present that the comparison did not ask for. Usually a stale folder from an earlier export, or a tool that wrote into the result. |

Every export also reports what the cross-check found, whether or not it found anything to complain
about: `Verified: 12 file(s) in base, 11 in modified, from 14 changed path(s).` The two counts differ
whenever something was legitimately unwritable — an addition has no base version, a deleted file has
no modified one — and they match the folders, not the change list, because that is what the folders
actually hold.

Files that were **added** between the commits produce no warning. The `base` side simply has
an empty file for them, which is what adding a file means.

### Binary files

Binary files are copied exactly, byte for byte, into both folders — nothing is decoded or
re-encoded on the way, so what you compare is what Git stored. The file list marks them as
`binary` rather than showing a line count, because a count of zero for a changed image is
misleading rather than informative. A diff tool opened on one shows the two versions as files and
is free to treat it as binary itself; Git's own text conversion is deliberately not applied
(`--no-textconv`), so the bytes are the ones in the commit.

### Paths Windows cannot hold

A Git repository can contain two paths that Windows treats as the same file — `Notes.txt` and
`notes.txt` is the usual case, and repositories moved over from Linux often have them. Windows
keeps one file, so the export writes the first and reports the clash in the run log rather than
letting the second quietly replace it. Nothing else is renamed or dropped, and the rest of the
export is unaffected.

### Files Git marks as export-ignore

A path can be marked `export-ignore` in `.gitattributes`, which is how a repository says "do not
put this in a tarball". The export still writes it: the file is in the commit you asked to
compare, so it belongs in the comparison, and the attribute is about tarballs rather than about
what this tool is for. A file marked `export-subst` is likewise written unchanged, so the
`$Format:…$` placeholders it contains are not rewritten.

---

## 6.15 Troubleshooting

**"Not an existing directory" or a Git error when loading**
Check the repository path. On Windows, a path pasted from a terminal may be quoted — remove the quotes.

**"git" is not recognised**
Git is not on `PATH`. Install it, or add it to `PATH` and restart the application.

**The commit list is short**
Bounded by **Show last**. Raise it and click **Refresh**.

**Branches I pushed are missing**
`git fetch` runs only when the repository changed since last load. Click **Refresh**.

**Double-clicking a file does nothing**
The diff tool may not be configured, or may not be at the path given. Click the tool button and
check the caption, which shows what is configured.

**Double-clicking opens a diff when I meant to tick the file**
Tick the file with the checkbox; the double-click is deliberately ignored on a checkbox so the
two actions stay separate.

**The export says "no file differences"**
The two commits have no differing paths. Check the direction — try **Swap**.

**Files are missing from the modified folder**
Files deleted between the commits have empty stand-ins. The run log says how many and which.

**The window is very tall / sections are collapsed**
Panels remember their collapsed state. Reopen them from their headings.

**The checker says "Listed, not present" for a file that exists**
It is looking in the *modified* source folder. A file that exists only in the base tree is a
deletion rather than an addition, and the document has to describe it as one.

**The checker reports almost every file as changed**
Build output is being compared. It is excluded by default; if you cleared that box, tick it again.
The line under the verdict always says how many files were left out.

**A file is reported "Not compared"**
It sits under `bin`, `obj`, `.git` or similar and the check skipped it on purpose. Tick the ignore
box off to include it. It is never reported as missing, because the tool did not look for it.

**A real file is reported as "Listed, not present"**
Check how the document spells it. A path missing its leading folders is fine, but a path that is
missing part of a *name* is a different file — and if the name contains a space, the checker can only
go on what is in front of it.

**"The file could not be opened as a Word document"**
The file is a `.doc` carrying a `.docx` name, or it is a different format. Save it from Word as
`.docx`.

---

## 6.16 Keyboard

| Key | Action |
|---|---|
| `Ctrl+F` | Go to the commit search box, with its text selected |
| `Tab` / `Shift+Tab` | Move between controls |
| `Space` | Toggle the focused checkbox or toggle button |
| `Enter` | Activate the focused button |
| `Esc` | Close the commit details or tool picker window |

The whole primary workflow — choose a repository, pick two commits, export — is reachable from
the keyboard, and the ⋯ button on a commit row appears for the row that has focus so it can be
reached without a mouse.