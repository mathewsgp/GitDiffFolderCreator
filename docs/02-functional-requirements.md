# 2. Functional Requirement Specification

**Project:** GitDiffFolderCreator
**Applies to:** `GitDiffFolderCreator.exe` (WPF, .NET Framework 4.6.1)

---

## 2.1 Conventions

- **MUST / SHALL** — mandatory. Absence is a defect.
- **SHOULD** — strongly expected; deviating requires a stated reason.
- **MAY** — optional.
- Each requirement has a stable ID (`FR-nn`) for traceability into design and tests.
- "Run folder" means the single output directory created per export, named `diff_<base>_<modified>_<timestamp>`, containing `base/`, `modified/` and manifest files.

---

## 2.2 Repository loading

| ID | Requirement |
| --- | --- |
| FR-01 | The system **MUST** accept a filesystem path to a Git working tree, entered directly or chosen through a folder picker. |
| FR-02 | The system **MUST** resolve the path to the repository root, so that a subdirectory of a working tree is accepted. |
| FR-03 | On load the system **MUST** read, independently of one another: repository root, working-tree status, local and remote-tracking branches, and the commit log. |
| FR-04 | A failure in one of the reads in FR-03 **MUST NOT** prevent the others from completing; each failure **MUST** be reported separately. |
| FR-05 | The system **MUST** update remote-tracking refs from the remote on load, **SHOULD** do so only when the repository itself changed since the last load, and **MUST** tolerate an unreachable remote without failing the load. |
| FR-06 | The number of commits loaded **MUST** be user-configurable and **MUST** default to 500. |
| FR-07 | The system **MUST** commit only the changes shown by Git for the two selected commits, excluding the commit's own message as a "change". |
| FR-08 | When a repository is switched, all state derived from the previous repository — commits, branches, file list, selection — **MUST** be cleared. |
| FR-09 | The system **MUST** persist the repository path and restore it on next launch. |
| FR-104 | The system **MUST** distinguish a repository that has no remote from one whose remote could not be reached, and **MUST** state, in words, that the branch list is as of the last fetch that worked when the remote could not be reached — a complete-looking list of branches is otherwise indistinguishable from a current one. |
| FR-105 | The stale-branch notice **MUST** offer a retry that re-reads the remote, and **MUST** be cleared by a fetch that succeeds, by a repository with no remote, and by switching repository. |

## 2.3 Branch selection

| ID | Requirement |
| --- | --- |
| FR-10 | The system **MUST** list local branches and remote-tracking branches, each with name, tracking status and last commit date. |
| FR-11 | The system **MUST** indicate the checked-out branch and whether it is behind its upstream. |
| FR-12 | Selecting a branch **MUST** scope the commit log to that branch. |
| FR-13 | When the current branch is behind its upstream and the remote has been updated, the system **MUST** surface that as a distinct, actionable state rather than a generic status message. |
| FR-14 | The user **MUST** be able to clear the branch filter to see the combined history. |
| FR-15 | The system **MAY** display a count of how many branches are hidden by the current branch filter. |

## 2.4 Commit selection and range resolution

| ID | Requirement |
| --- | --- |
| FR-16 | The user **MUST** be able to select exactly two commits from the log. |
| FR-17 | The system **MUST** determine the base and modified direction by testing ancestry with `git merge-base --is-ancestor`, **MUST NOT** infer it from list order, and **MUST** support the Swap control to reverse a correct-but-unwanted direction. |
| FR-18 | Where neither commit is an ancestor of the other, the system **MUST** order them by date, **MUST** record that the ordering is inferred rather than verified, and **MUST** label it as such in the UI. |
| FR-19 | The system **MUST** label both selected commits in the log with their resolved role, and **MUST** name both in the file-list heading, so the direction is stated where the files are. |
| FR-20 | Each log entry **MUST** show the abbreviated hash, subject, author, commit date, and any refs pointing at it — including branch names and tags. |
| FR-21 | Ref names **MUST NOT** be truncated so far that the tag or branch is lost; where the full list cannot fit, the full list **MUST** remain retrievable. |
| FR-100 | The commit log **MUST** be filterable by a text search reachable through a standard accelerator, which **MUST** place the caret in the search box with its text selected. |
| FR-101 | The search **MUST** match, ignoring case, against the commit message, the full or abbreviated hash, the author and the ref names. |
| FR-102 | The system **MUST** state how many commits the search hid out of how many are loaded, and **MUST NOT** describe in its status line any commit the filtered list is no longer showing. |
| FR-103 | Clearing the search **MUST** restore every loaded commit. |

## 2.5 Changed-file listing

| ID | Requirement |
| --- | --- |
| FR-22 | For the resolved range the system **MUST** list every differing path, using rename detection, with status code, status, and added/deleted line counts. |
| FR-23 | A binary file **MUST** be identified as such and **MUST NOT** be given line counts it does not have. |
| FR-24 | The file list **MUST** support filtering by path substring, case-insensitively. |
| FR-25 | The file list **MUST** support filtering by status (all / added / modified / deleted / renamed). |
| FR-26 | The system **MUST** state how many files are shown out of how many total whenever the filter hides any. |
| FR-27 | Unchecking a file **MUST** exclude it from the export **and MUST** from the reported line-count totals. |
| FR-28 | Per-file checkboxes **MUST NOT** be shown by default; they **MUST** appear only when the user turns the selection option on. |
| FR-29 | Turning the selection option on **MUST** leave every file included. Turning it off **MUST NOT** alter which files are included. |
| FR-30 | The user **MUST** be able to include or exclude all files in one action, in either direction. |
| FR-31 | Excluding every file **MUST** disable the export action and **MUST** state that the export would produce nothing, rather than producing an empty run folder. |

## 2.6 Export

| ID | Requirement |
| --- | --- |
| FR-32 | The export **MUST** write the base version of each included file into `base/` and the modified version into `modified/`, recreating each file's repository-relative directory structure in both. |
| FR-33 | For a file added between the two commits, the system **MUST** create an empty base-side file. |
| FR-34 | For a file deleted between the two commits, the system **MUST** create an empty modified-side file. |
| FR-35 | For a renamed file, **base/` **MUST** receive the old path's content and `modified/` the new path's content. |
| FR-36 | Binary files **MUST** be copied byte-for-byte. |
| FR-37 | The system **MUST NOT** leave partial run folders: it **MUST** build into a staging directory and publish atomically, and **MUST** delete the staging directory on failure or cancellation. |
| FR-38 | The run folder **MUST** be named `diff_<base-abbrev>_<modified-abbrev>_<timestamp>`, and a name collision **MUST NOT** overwrite an existing folder. |
| FR-39 | The system **MUST** write manifest files listing deleted, renamed and changed paths. |
| FR-84 | Every manifest **MUST** open with a line declaring how many entries it holds, marked with a leading `#` so a reader taking the file line by line does not count the header as a path. |
| FR-85 | A manifest with no entries **MUST** still be that one header line, and **MUST NOT** be a zero-byte file: an empty file reads the same whether there was nothing to list or the manifest was never written. |
| FR-86 | The system **MUST** write a manifest of what each folder actually ended up holding, as distinct from the manifests describing the comparison, so a diff tool cannot mistake a legitimately unwritten file for an omission. |
| FR-87 | After writing the files, the system **MUST** read the staged folders back from disk and compare them against the change list, and **MUST** warn about any requested path that was not written and that nothing accounts for, and about any file present that nothing asked for. |
| FR-88 | The cross-check's comparisons **MUST** ignore case, because the destination filesystem may not: two paths differing only in case are one file there, and an exact-case comparison would report a collision a warning has already explained as an unexplained omission. |
| FR-89 | **Experimental.** The system **MUST** offer a way to check a change document against two source folders, independent of any repository. |
| FR-90 | **Experimental.** The system **MUST** read the change document's text from a Word `.docx` file, keeping one list entry per paragraph or table row. |
| FR-91 | **Experimental.** The system **MUST** read the documented file list from the section between the document's `[Modified Files]` heading and its `[Status]` heading, and **MUST NOT** read paths from anywhere else in the document. Numbering, bullets, blank lines and commentary inside that section **MUST NOT** end the list or hide the paths beside them. |
| FR-108 | **Experimental.** A documented path **MUST** carry at least one `/` or `\`; a bare file name **MUST NOT** be read as a path. Two paths **MAY** share one line when separated by a tab or by two or more spaces. |
| FR-109 | **Experimental.** The system **MUST** normalise a documented path before matching it — separators unified to `/`, surrounding whitespace and slashes trimmed, a leading `./` dropped — and **MUST** apply the same normalisation to the source path, so that neither side can match by accident. |
| FR-110 | **Experimental.** A documented path **MUST** count as matching a source file when it is that path or a trailing sequence of its folders. The tail **MUST** land on a folder boundary, and a tail that two or more files could answer **MUST** be reported as naming nothing rather than resolved to one of them. |
| FR-111 | **Experimental.** The user **MUST** be able to name the two headings that bound the documented file list, defaulting to `[Modified Files]` and `[Status]`. A configured marker **MUST** be used as typed, less any surrounding whitespace, with no character removed from it and no second reading of it; it **MUST** match at the start of a line, ignoring case and any text following it. |
| FR-112 | **Experimental.** A marker left blank **MUST** mean the default rather than no marker, because clearing the box is not a request for a document that cannot be read. |
| FR-113 | **Experimental.** Where the markers bound more than one section of the document, **MUST** read every such section, and **MUST NOT** read anything that falls between the end of one and the start of the next. A repeated section is as much part of the list as the first, and a document that repeats it per module or per phase **MUST NOT** have its later sections reported as undocumented changes. |
| FR-114 | **Experimental.** A path named in more than one of those sections **MUST NOT** be reported as a duplicate, because a document listing per module is expected to name a shared file more than once — including where two sections spell the same file differently. **MUST** still be reported when the same path appears twice within one section. |
| FR-92 | **Experimental.** The system **MUST** report every disagreement in both directions: a path listed but not changed, a path changed but not listed, and a path that names no file in either source. |
| FR-93 | **Experimental.** The system **MUST** treat a moved file as one change at its new path, and **MUST** tell the reader which path it moved from rather than reporting a deletion and an addition. |
| FR-94 | **Experimental.** The system **MUST** state how many files it compared and how many it left out, so that a pass is a statement about the tree rather than an absence of complaints. |
| FR-95 | **Experimental.** The system **MUST NOT** regard the absence of build output as a difference: these are source trees, and a compiled artifact differs on every build. |
| FR-40 | A path that Git cannot supply **MUST NOT** discard the rest of the export: one unusable pathname **MUST** affect only itself. |
| FR-41 | The user **MUST** be able to cancel a running export, and cancellation **MUST** leave no run folder behind. |
| FR-42 | On completion the system **MUST** report the run folder path, and **MUST** optionally open it in File Explorer. |
| FR-43 | The system **MUST** report a warning when files present in the base commit are absent from the modified commit. |
| FR-44 | The system **MUST NOT** warn about files that were added between the two commits; an addition is the expected outcome of a comparison. |
| FR-45 | Excluding files by selection **MUST** be reported, so a partial export is never mistaken for a complete one. |
| FR-46 | Throughout the export the system **MUST** show which stage is running, **MUST** keep the window responsive, and **MUST** offer a working Stop control. |
| FR-47 | The system **MUST** maintain a run log of stages, warnings and outcome, and the log **MUST** remain readable while the export runs. |
| FR-77 | Where two paths of one commit resolve to the same file on the destination filesystem — paths differing only in case, which Git allows and Windows does not — the system **MUST** write only the first, **MUST NOT** silently overwrite, and **MUST** report the clash by name. |
| FR-78 | A path that is not a file at that commit — a submodule, or a directory — **MUST** be reported rather than silently left out of the result. A path marked `export-ignore` in `.gitattributes` **MUST** still be exported, because that attribute governs tarballs and this is a copy of the commit. |

## 2.7 Diff tool integration

| ID | Requirement |
| --- | --- |
| FR-48 | Double-clicking a changed file **MUST** hand that file's base and modified versions to the user's configured diff tool. |
| FR-49 | For an added or deleted file the system **MUST** create the missing side as an empty file before launching, so the tool receives a real pair. |
| FR-50 | Paths handed to the tool **MUST** be temporary, sanitised, and cleaned up afterwards. |
| FR-106 | A temporary folder left behind by a diff hand-off **MUST** be removed by a later hand-off once it can no longer be in use, since Windows does not remove arbitrary temporary files: the two versions a tool is still reading **MUST** survive, and nothing **MUST** be deleted outside this program's own temporary folders. |
| FR-51 | A path containing spaces or shell metacharacters **MUST** reach the tool intact. |
| FR-52 | If no diff tool is configured, the system **MUST** report that in the window rather than doing nothing silently. |
| FR-53 | If the configured tool cannot be launched, the system **MUST** report the failure and what it tried to run. |
| FR-54 | The user **MUST** be able to choose a diff tool from a catalog of commonly installed tools, detected by inspecting standard install locations. |
| FR-55 | The user **MUST** be able to supply an arbitrary tool command line, which **MUST** take precedence over the catalog. |
| FR-56 | The configured tool **MUST** be shown next to the file list, so it is visible before a hand-off is attempted rather than only after one fails. |
| FR-57 | The chosen tool **MUST** persist across sessions. |
| FR-58 | A double-click on a checkbox, button or text field **MUST NOT** launch the diff tool. |
| FR-59 | The double-click action **MUST** require an actual double-click; a single click **MUST NOT** trigger it. |

## 2.8 Commit details

| ID | Requirement |
| --- | --- |
| FR-60 | Every commit row **MUST** offer a control that opens the whole of that commit. |
| FR-61 | The details window **MUST** show the subject, author, author date, full 40-character hash, parents, refs, and the full commit message body. |
| FR-62 | It **MUST** show the committer and commit date **only when they differ from the author**, and **MUST** hide the body when there is none. |
| FR-63 | It **MUST** list the files that commit changed, with a summary of file count and line totals. |
| FR-64 | Binary files **MUST** be counted separately from the line totals and **MUST** be reported as such, so their absence from the totals is not read as "unchanged". |
| FR-65 | A commit that changed nothing **MUST** be reported as having changed nothing. |
| FR-66 | The file list **MUST** be filterable, and the totals **MUST** continue to describe the whole commit rather than the filtered view. |
| FR-67 | Reading a commit that does not exist or cannot be read **MUST** produce a message in the window, **MUST NOT** terminate the application. |
| FR-68 | A multi-paragraph commit message, including blank lines, **MUST** be displayed intact. |
| FR-96 | The commit row's control for opening the whole commit **MUST** be shown only while that row is hovered or holds keyboard focus, so a row costs no permanent caption, and it **MUST NOT** change the row's width when it appears. |
| FR-97 | Double-clicking a file in the details window **MUST** hand that file's version at the commit's **first parent** and its version at the commit to the user's configured diff tool — the same pair the file list was built from. |
| FR-98 | A commit with no parent **MUST NOT** offer a diff, and **MUST** say that there is nothing to compare it against. |
| FR-99 | The outcome of that hand-off, including the case where no diff tool is configured, **MUST** be stated in the details window rather than appearing to do nothing. |

## 2.9 Settings

| ID | Requirement |
| --- | --- |
| FR-69 | The system **MUST** persist the repository path, output folder, log size, "open when finished" preference, chosen diff tool and change-document section markers. |
| FR-70 | A settings file written by an earlier version, or hand-edited to be incomplete, **MUST** load without error and **MUST** fall back to defaults for any absent member. |
| FR-71 | Collapsed panel state **SHOULD** persist, so a user's layout survives a restart. |
| FR-72 | A transient setting such as whether the selection checkboxes are showing **MUST NOT** persist; it describes the current session, not a preference. |
| FR-73 | A window that writes one part of the settings file **MUST NOT** discard the parts another window owns; each **MUST** read the file, change its own members and write it back. |

## 2.10 Status and feedback

| ID | Requirement |
| --- | --- |
| FR-73 | The window **MUST** display a status line covering, at minimum, how many commits were loaded, from which repository, and whether branches were updated from the remote **or could not be**. |
| FR-74 | An error status **MUST** be visually distinct from a normal status. |
| FR-75 | Long-running work **MUST** show which stage is in progress; a single generic "working" indicator **MUST NOT** be used where the stages are distinguishable. |
| FR-76 | Any control whose action is not currently valid **MUST** be visibly disabled rather than silently inert. |
| FR-107 | A control whose effect depends on state the user cannot see **MUST** name that state in its caption: the Open control **MUST** say whether it opens the configured output folder or the last result, and the control revealing the per-file check boxes **MUST** be captioned for choosing files rather than for selecting rows, which is a different gesture with its own control. |
| FR-79 | The export bar **MUST** be determinate whenever the amount of work is known, **MUST** never move backwards, and **MUST** finish at full. While the total is not yet known it **MUST** indicate activity rather than show a figure. |

## 2.11 Traceability

Requirement groups map to test classes as follows.

| Requirements | Primary test coverage |
| --- | --- |
| FR-02, FR-07, FR-10 – FR-15 | `GitServiceTests`, `GitBranchTests`, `BranchSyncTests` |
| FR-17 – FR-19 | `GitServiceTests`, `MainWindowTests` |
| FR-20, FR-21, FR-100 – FR-103 | `GitServiceTests`, `MainWindowTests`, `CommitSearchTests` |
| FR-22 – FR-31 | `ChangeFilterTests`, `GitFileChangeDisplayTests`, `FileSelectionTests` |
| FR-32 – FR-47, FR-77 – FR-88 | `DiffExporterTests` |
| FR-48 – FR-59, FR-97 – FR-99, FR-106 | `DiffToolLauncherTests`, `DiffToolPickerTests`, `WindowsArgumentStringTests`, `FileSelectionTests`, `CommitDetailDiffTests` |
| FR-60 – FR-68, FR-96 | `CommitDetailTests`, `CommitDetailDiffTests`, `MainWindowTests` |
| FR-104, FR-105 | `GitServiceTests`, `RemoteStalenessTests` |
| FR-107 | `MainWindowTests`, `OutputFolderCaptionTests` |
| FR-69 – FR-72 | `DiffToolPickerTests`, `RepositorySwitchTests` |
| FR-73 – FR-76, FR-79 | `MainWindowTests`, `CollapsibleGroupBoxTests`, `BindingConverterTests`, `DiffExporterTests` |
| UI composition, resources, layout | `MainWindowTests`, `CollapsibleGroupBoxTests` |
| FR-89 – FR-95, FR-108 – FR-110 (experimental) | `ChangeDocumentReaderTests`, `ChangeDocumentVerifierTests` |