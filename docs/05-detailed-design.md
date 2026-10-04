# 5. Detailed Design

**Project:** GitDiffFolderCreator

This document describes how each component works. It assumes the layering in document 4.

---

## 5.1 `GitProcessRunner` — the process boundary

The only type in the application that starts a Git process.

### 5.1.1 Text output

```
RunForOutputAsync(workDir, arguments, ct) -> string
```

Runs `git <arguments>`, returns stdout as a `string`, and throws
`GitCommandException(command, stderr)` on a non-zero exit. The exception carries the command
line and stderr so the message can say what was run, not just that something failed.

### 5.1.2 Binary output

```
RunToFileAsync(workDir, arguments, destination, ct)
```

For extracting file content. Two details matter:

- **`StandardOutputEncoding = null`.** A binary blob piped through a text decoder is corrupted
  — the decoder replaces invalid sequences with U+FFFD. Binary output is therefore written to
  the destination as raw bytes.
- **Stderr is drained concurrently.** A command that writes more stderr than the pipe buffer
  holds will block forever if stderr is only read after the process exits.

### 5.1.3 Argument construction

Arguments are passed as a list. `ProcessStartInfo.ArgumentList` is unavailable on .NET
Framework 4.6.1, so quoting is done by `WindowsArgumentString.Build`, which implements the
`CommandLineToArgvW` escaping rules: wrap in quotes, escape embedded `"` as `\"`, escape
backslashes that immediately precede a quote. This is what makes FR-51 hold — a path
containing spaces, quotes or `&` reaches the target intact, because no shell is involved.

## 5.2 `GitService` — all Git interaction

Ten public asynchronous methods, every one taking a `CancellationToken`. Repository path is
set at construction.

| Method | Git invocation | Notes |
| --- | --- | --- |
| `GetRepositoryRootAsync` | `rev-parse --show-toplevel` | Accepts a subdirectory. |
| `GetStatusAsync` | `status --porcelain` | Working-tree status. |
| `GetBranchesAsync` | `for-each-ref` | One invocation for all refs. |
| `HasRemoteAsync` | `remote` | Guards the fetch. |
| `FetchAsync` | `fetch --quiet` | The only network stage. Guarded by `HasRemoteAsync`; a local-only repository returns quietly. |
| `GetLogAsync(max, revision)` | `log --no-color -n <max> <format> [revision]` | The revision goes last with nothing after it: `--` would end revision parsing. |
| `GetChangesAsync(old, new)` | `diff --name-status -M` + `diff --numstat` | Two calls, because status and line counts need different formats. |
| `GetChangesForCommitAsync(hash)` | `show --name-status -M` + `show --numstat` | `-M` gives rename pairing. |
| `GetCommitDetailAsync(hash)` | `show -s --no-patch` | Metadata only; see §5.2.1. |
| `IsAncestorAsync(a, b)` | `merge-base --is-ancestor` | See §5.2.2. |
| `ExportFilesAsync(hash, paths, dest)` | `archive` | See §5.2.3. |

### 5.2.1 Record and field separators

The log format is:

```
--format=%x1e%H%x1f%h%x1f%cI%x1f%an%x1f%D%x1f%s
```

- `%x1e` (record separator) marks the start of each commit.
- `%x1f` (field separator) separates fields within it.

Splitting on the record separator first, then the field separator, gives a matrix. This is
more robust than newline-splitting because a commit *subject* may contain anything except a
newline — but it may contain a separator-like control character, so the record split is the
only safe order.

**Field-by-field for the commit detail format** (`GitCommitDetailFormat.Format`):

| Index | Placeholder | Meaning |
| --- | --- | --- |
| 0 | `%H` | Full 40-character hash |
| 1 | `%h` | Abbreviated hash |
| 2–4 | `%an` `%ae` `%aI` | Author name, email, strict ISO-8601 date |
| 5–7 | `%cn` `%ce` `%cI` | Committer name, email, strict ISO-8601 date |
| 8 | `%P` | Parents, space-separated; empty for a root commit |
| 9 | `%D` | Refs, including `tag: v1.2.3` and `HEAD -> main` |
| 10 | `%s` | Subject |
| 11 | `%b` | Body — **always the last field** |

Two decisions here are load-bearing:

- **`%b`, not `%B`.** Both are the unwrapped raw message, but `%B` repeats the subject, which
  is already requested separately by `%s` and already displayed. Using `%B` would show the
  same first line twice.
- **The body is last, and the tail is rejoined rather than split.** The body is the only field
  that can contain the field separator. `GitCommitDetail.TryParse` takes fields `[0..10]` as
  individual values and rejoins everything from index 11 with the separator before trimming.
  A naive split loses everything after the first separator inside the body. This is verified
  by `A_body_containing_the_field_separator_does_not_lose_its_tail`.

### 5.2.2 Ancestry, and why exit codes matter

`git merge-base --is-ancestor A B` communicates through its exit code, not its output:

| Exit | Meaning | `IsAncestorAsync` returns |
| --- | --- | --- |
| 0 | A is an ancestor of B | `true` |
| 1 | Not an ancestor | `false` |
| other | The comparison itself failed | throws `GitCommandException` |

`CommitRangeResolver` tests both directions and produces a `CommitRange` carrying a
`RangeConfidence`:

- `Ancestor` — verified by Git.
- `DateHeuristic` — neither is an ancestor of the other, so they are ordered by date and the
  UI says so. Reporting this honestly (FR-18) matters: a date-ordered pair of divergent
  branches is a legitimate comparison, but it is not a verified one.

### 5.2.3 `ExportFilesAsync` — one `cat-file --batch` process

Every path is asked for as `<commit>:<path>` through a single `git cat-file --batch` process, and
the answer is written straight to disk. Git answers each request individually:

```
<oid> blob <size>      → exactly <size> bytes, then a newline
<input> missing         → no such object at that commit
<oid> commit <size>    → the path is a gitlink; there is no file to write
```

Two properties follow, and both were bought at the price of something:

- **A bad path cannot fail the export.** `missing` is an answer, not an error, so the previous
  all-or-nothing behaviour — and the bisect retry that mitigated it — are gone (SR-21).
- **Paths no longer travel on a command line.** The 16 000-character budget and the batching it
  forced existed only for the 32 767 limit, so a 400-path export is now one process instead of
  several, with no temp archive written and read back.

`RunCatFileBatchAsync` keeps exactly one request outstanding and consumes each answer in full
before writing the next. That ordering is the whole of its deadlock safety: the child blocks once
the pipe is full, so a caller that queued every request up front would block writing while the
child blocked reading. The cost is one round trip per object, which is far cheaper than the
process-per-batch it replaces. `BoundedStream` clamps reads to the announced size and drains the
remainder on dispose, so the stream is aligned for the next answer whether the caller read the
object, ignored it, or was interrupted.

`missing` is interpreted differently per side, which is the whole of §5.5's warning policy.

Every read of the pipe goes through `PipeReader.Fill`, which checks the cancellation token and
treats end of stream as an error. That includes `SkipRemaining`, which discards the part of an
answer the caller did not want — and which used to read the stream directly. A read at end of
stream returns 0, which is indistinguishable from "nothing buffered yet", so a skip loop that
refilled on it never decreased its count and never exited: a git killed part way through a batch,
or one cancelled mid-export, hung the export instead of ending it. Refilling through `Fill` makes
that loop terminate on both counts, and gives cancellation somewhere to be noticed while the
discarded bytes are being thrown away.

### 5.2.4 Destination-side path conflicts

A third outcome, distinct from both "extracted" and "missing", is a path that Git holds but the
destination filesystem cannot hold separately. Git trees are case-sensitive, so a repository
carried over from Linux can contain both `Notes.txt` and `notes.txt`; Windows resolves both to
one file. Writing the second would replace the first, and nothing about the finished folder would
show that anything had been lost — a comparison of two apparently complete folders finds no
difference in the file that survived.

`WriteEntryAsync` therefore tracks the destination file each path claimed, keyed
`OrdinalIgnoreCase`. The first path to arrive keeps the file; each later one is recorded in
`ConflictingPaths` and skipped, and `DiffExporter` turns those into a warning naming both sides of
each clash (FR-77). Reporting is preferred over renaming: a renamed file would land at a path the
repository does not contain, which breaks the mapping the comparison depends on, and over preferred
over failing: one unrepresentable pair should not cost the user the rest of the export.

The destination path is built from the requested path, so it is checked against the destination
root before anything is written. Git's own path validation rejects `.` and `..` components, so
this is unreachable from a repository; it is checked anyway, because the alternative is a
destination derived from data with no bound on it.

### 5.2.5 Omitted paths — and what changed when the transport did

A path that is not a file — a submodule, which is a gitlink to a commit, or a directory, which is
a tree — has no content to write. `cat-file` reports its type in the header, so such a path becomes
`OmittedPaths` and a warning on whichever side it belongs to (FR-78).

This section previously described a different failure, and the difference is the argument for the
current transport:

```
.gitattributes:   hidden.cs export-ignore
git archive --format=zip -o o.zip HEAD -- hidden.cs   →  exit 0, no entry
```

`git archive` filters what it writes *after* pathspec matching, so `export-ignore` produced a
successful archive with the file silently absent — no error, no `MissingPaths` entry, no manifest
row, and nothing a comparison could detect. Recovering from that needed a cross-check between the
request and the archive, and its own false-positive cases to reason about.

Asking for the object instead of the archive makes the question disappear. `export-ignore` is a
statement about tarballs, and this application is not producing one: the user asked for that file
at that commit, and that is what is written. `export-subst` likewise no longer rewrites `$Format:…$`
placeholders in the exported copy, which for a byte-for-byte comparison was always the wrong
answer. `cat-file` has no filters and no pathspec, so a request either resolves to the exact blob
or says it does not exist. The cross-check is therefore gone, not weakened — the guarantee it
existed to provide now holds structurally, which is why nothing replaced it.

## 5.3 `DiffExporter`

### 5.3.1 Output layout

```
<outputRoot>/
  .gdfc-staging-<guid>/        ← built here, then moved
      base/                     ← base version of each included file
      modified/                ← modified version of each included file
      DeletedFiles.txt
      RenamedFiles.txt
      ChangedFiles.txt
      ExportedFiles.txt         ← what each folder actually ended up holding
  diff_<base7>_<mod7>_<ts>/     ← published name
```

Run folder name: `diff_<base-abbrev>_<modified-abbrev>_<timestamp>`. `UniquePath` appends a
suffix rather than overwriting, so a second export in the same second cannot destroy the first
(SR-20).

### 5.3.1a Manifest headers

`DeletedFiles.txt`, `RenamedFiles.txt` and `ChangedFiles.txt` are written by `WriteCountedLines`,
which puts one header line first:

```
# 3 rename(s)
src/old.cs
src/new.cs
vendor/lib.js
```

The count is a comment for two reasons. These files are read line by line, so a bare number on
line 1 would be counted as a path by anything that does not know the convention, and a script
would report one more rename than actually happened. And the marker makes the header skippable by
the same rule `.gitattributes` and `.gitignore` use for a non-data line — one rule to learn, rather
than one per file (FR-84).

A manifest with nothing in it is that one header line, not a zero-byte file. Zero bytes reads the
same whether there were no renames or the renames file never arrived, and the second of those is
exactly the case a reader needs to notice (FR-85).

### 5.3.1b `ExportedFiles.txt`, and why it is a different manifest

The other three manifests describe *what the comparison was*. `ExportedFiles.txt` describes *what
was written*, which is the thing the user is about to open in a diff tool. The two differ whenever
something was legitimately not written — an addition has no base version, a pair colliding on this
filesystem keeps only one of its files — and a diff tool cannot tell that apart from a real
omission. Its two sections are the files found in `base/` and the files found in `modified/`, each
with its own count:

```
# base: 2 file(s)
src/old.cs
vendor/lib.js

# modified: 2 file(s)
src/new.cs
vendor/lib.js
```

The lists are the paths read back off disk rather than the paths that were requested, so the
manifest cannot claim a file the folder does not contain (FR-86).

### 5.3.2 Atomic publish

The staging directory is built in full, then moved to its final name. The move is the
commit point. On any exception the staging directory is deleted in a `finally`. The
consequence: a run folder that exists is complete, and an interrupted export leaves no
evidence of itself beyond the log (SR-18, SR-19).

### 5.3.3 Warning policy

| Side | Meaning of a missing path | Reported? |
| --- | --- | --- |
| `base` | The file was **added** between the commits | **No** — that is the expected result of comparing two commits (FR-44) |
| `modified` | The file was **deleted** between the commits | **Yes** — the two folders will not correspond (FR-43) |

Exclusions by selection are also warned about, so a partial export is never mistaken for a
complete one (FR-45).

| Any | Two paths that are one file on this filesystem | **Yes, on both sides** — a file was not written, and the folder is short of it without looking so (FR-77) |
| Any | Git matched the path and then filtered it out of the archive | **Yes, on both sides** — the path is not a file, so there was nothing to write (FR-78) |

### 5.3.3a The post-export cross-check

Every warning above is derived from what Git said. That is not the same as what landed on disk, so
before the manifests are written — while there is still time to react — `VerifyFolder` reads each
staged folder back and compares it against the change list (FR-87).

It is deliberately narrow. A comparison legitimately produces folders of different sizes: an
addition is in one and not the other. Reporting every difference would bury a real one in the
expected ones, which is what makes a warning useless. So only two things are reported:

| | Meaning |
| --- | --- |
| `Missing` | A path the change list asked for, not on disk, and with no explanation already given for it |
| `Unexpected` | A file on disk that the change list did not ask for |

`MissingPaths`, `OmittedPaths` and `ConflictingPaths` all count as already explained, and each is
already warned about. Re-reporting them would state one fact twice, which is worse than stating it
once, because the second statement arrives with no context attached.

The counts are reported either way — `Verified: 12 file(s) in base, 11 in modified, from 14 changed
path(s).` — because "these two folders agree with the change list" is the reassurance the
cross-check exists to give, and it is worth as much when nothing was found as when something was.

**Case matters here, and in one direction only.** The comparison ignores case, because the disk it
describes does. Two paths differing only in case are one file on Windows, and the surviving name is
whichever landed first; an ordinal comparison would then call the losing path both present — under
the survivor's name — and unaccounted for, reporting one collision as a collision *and* an
unexplained omission (FR-88). `FolderVerification` therefore keeps the repository's spelling for
reporting and tests membership through `OrdinalIgnoreCase` sets.

This is a check on the export, not a substitute for the manifest: `ExportedFiles.txt` is written
from the same read, so the manifest and the warnings always describe the same view of what landed.

### 5.3.4 Progress

`ExportProgress` carries a message and a count: `{ Message, Completed, Total }`, with `Total == 0`
meaning "not yet known".

The total becomes knowable exactly once, when `GetChangesAsync` returns the file list. From there
the work is `basePaths.Count + modifiedPaths.Count` requests, and `ExportFilesAsync` reports one per
path answered. Three decisions are behind that:

- **Counted per path, not per byte.** The file count is free; a byte count needs a second git call,
  and it would make the bar jump backwards whenever a large file turned up after several small
  ones. A bar that retreats is worse than one that is coarse.
- **Every path answered is counted, written or not.** A path Git could not supply is finished work.
  Counting only the files that landed would leave the bar short of full on exactly the exports that
  went wrong — the one time the user most wants to see it complete (FR-79).
- **One shared counter across both sides.** The modified side continues from where the base side
  stopped, which is what makes the bar monotonic across the whole run rather than restarting at zero
  halfway through. It is a small object rather than a `ref` parameter because a closure cannot
  capture one.

Until the total is known the bar has nothing honest to show, so it animates. That phase is the
`git diff`, which is the longest single step; a filled bar sitting at 0% through it reads as a hang
rather than as work.

`GitViewModel.ApplyExportProgress` drops any report that would not move the bar by a whole percent.
An export of a few thousand files reports once per file, and posting each one to the dispatcher would
put more work on the UI thread than the export does — the bar would then lag the work it is
describing. Reports are also ignored once the export has returned, because `Progress<T>` posts its
callbacks and one still queued would otherwise overwrite "Folders created." with a stage message from
work that has already finished.

The markup drives the fill from `ExportProgressFraction` and `ExportProgressRemainder` as two star
columns, converted by `FractionToStarConverter`. A star column tracks the track when the window is
resized mid-export; a pixel width does not, and the fraction only means anything to the layout as a
star.

## 5.4 `DiffToolLauncher`

Hands one file's two versions to the user's configured diff tool.

```
LaunchAsync(repositoryPath, hash, path, toolCommand, ct) -> DiffToolLaunchResult
```

### 5.4.1 Why it does not use `git difftool`

Two dead ends are worth recording so they are not re-attempted:

- `git difftool --no-prompt -- <baseTemp> <modTemp>` — Git interprets the arguments as
  *repository pathspecs*, not paths on disk. It exits successfully without launching anything.
  The failure is silent, which is the worst kind.
- `git difftool --no-prompt <baseCommit> <modifiedCommit> -- <path>` — can hang behind stdin
  prompt behaviour.

`git var GIT_EXTERNAL_DIFF` is not supported by the installed Git version.

The launcher therefore reads `git config --get diff.tool` and **launches the configured
command directly**, through `Process`, with arguments built by `WindowsArgumentString.Build`.

### 5.4.2 Steps

1. Extract both versions with `git show --no-textconv <hash>:<path>`, writing bytes.
2. Sanitise the repository path to a single filename component (`FileNameTag`) so a crafted
   path cannot escape the temporary directory (SR-32).
3. Create a per-run temporary folder prefixed `gdfc-compare-`.
4. Where one side does not exist (added or deleted file), **create an empty file for it**, so
   the tool always receives a real pair (FR-49).
5. Split the configured command into executable + arguments; quote each argument.
6. Launch. A launch that failed deletes its own folder immediately, because nothing was handed
   over. A launch that succeeded **leaves it**, and instead sweeps what earlier launches
   abandoned (below).
7. Cleanup swallows IO and `UnauthorizedAccessException` — a still-running diff tool holds file
   locks, and that is expected, not an error (SR-30).

`DiffToolLaunchResult` carries `Launched`, `Message`, `IsError`, `BasePath`, `ModifiedPath`.
An unconfigured tool is reported rather than silently doing nothing (FR-52).

### 5.4.3 Sweeping what an earlier comparison left behind

Step 6 leaves the pair on disk on purpose. There is no event to clean up on: the tool is started
without being waited for, and it may have been started *by a launcher* that exited immediately
while its window is still open. Windows does not remove arbitrary temporary files either, so
without a policy a session that compares a few hundred files leaves a few hundred folders of
extracted content behind (FR-106, SR-29).

The policy is therefore age-based, and it runs at the moment it is safe to run — after a
successful launch, once per session:

```
DeleteAbandonedFolders(tempRoot, AbandonedFolderAge /* 1 day */, DateTime.UtcNow)
   foreach (folder in tempRoot/gdfc-compare-*)
       if (folder.LastWriteTimeUtc > now - age) continue;      // may still be in use
       try { Directory.Delete(folder, recursive: true); removed++ } catch (IOException) { }
```

Three decisions worth stating:

- **A day, not "the last one".** The only thing the age protects is a folder a tool may still be
  reading. A day is far longer than any comparison lasts and far shorter than the point at which
  the disk matters.
- **Only this class's own prefix.** The sweep runs in the shared `%TEMP%`, so a wildcard that
  matched everything there would be deleting other programs' files.
- **A failure is not a failure.** A locked folder is the expected case, not an error: it is
  skipped and offered again next time. Deleting the ones that can be deleted is worth more than
  refusing to delete any (SR-30).

The clock and the age are arguments rather than constants, so the policy is tested against a
folder of a known age rather than one that has to be waited for.

## 5.5 `GitViewModel`

### 5.5.1 Selection state

```
SelectedCommits : ObservableCollection<GitCommit>   (exactly 0 or 2)
   │ CollectionChanged
   ▼
CommitRangeResolver  ──►  CommitRange { Base, Modified, Confidence }
   │
   ▼
GitService.GetChangesAsync(base.Hash, modified.Hash)  ──►  Changes
```

`Changes` carries the per-file `PropertyChanged` subscription that refreshes `SelectionSummary`
and `ExcludedCount`. `FilteredChanges` is rebuilt by `ApplyFilter` from the path text and
status code.

### 5.5.2 Bulk selection, and why it suppresses notifications

```
SetAllIncluded(bool included)
   _suppressFileChangeNotifications = true
   foreach (change in Changes) change.IsIncluded = included
   _suppressFileChangeNotifications = false
   OnPropertyChanged("SelectionSummary"); OnPropertyChanged("ExcludedCount"); UpdateCommandStates()
```

Each row raises its own change, and each one would otherwise recompute the summary and
re-evaluate every command. On a 500-file comparison that is 500 rebuilds of work whose result
is constant. The rows still repaint individually — that is the binding doing its own job.

Covered by `Select_all_and_deselect_all_rebuild_the_summary_once_rather_than_once_per_file`,
which asserts exactly one `SelectionSummary` notification for 25 files.

The same argument applies one level up, to *installing* the list rather than editing it.
`Changes.CollectionChanged` runs `ApplyFilter`, which re-reads every file and rebuilds
`FilteredChanges` from scratch; adding files one at a time therefore cost a rebuild per file, on
a list that runs to thousands. `ReplaceChanges` sets `_suppressChangeFilter` around the whole
installation and filters once at the end:

```
ReplaceChanges(changes)
   _suppressChangeFilter = true
   try { Changes.Clear(); foreach (change in changes) Changes.Add(change); }
   finally { _suppressChangeFilter = false; }
   ApplyFilter()
```

The collection events still arrive, deliberately: the per-file `PropertyChanged` subscriptions
have to be attached as items come and go, and the rows bind to the collection. Only the derived
work is held back. This is the same suppression as §5.5.2 and the same requirement (SR-15);
`Building_the_file_list_filters_it_once_rather_than_once_per_file` asserts 41 collection
notifications for 40 files, where per-file filtering raised 902.

### 5.5.3 Searching the log

`CommitFilter` narrows `Commits` into `FilteredCommits`, and the list binds to the filtered one. Three
decisions, each forced by something:

- **Narrowing the list, not re-reading the log.** The commits are already in memory, and a search that
  spends a `git log` is a search the user waits on. `ApplyCommitFilter` runs over `Commits`, never over
  `FilteredCommits`, so widening the search restores what it hid rather than leaving the list half-built.
- **Each term must sit inside one field.** `CommitMatches` tests message, hash, short hash, author and
  refs separately. Testing the row as one string would let a hash match every commit whose *message*
  mentions it, which is the opposite of what a pasted hash is for. The refs are empty on most commits,
  so an absent field has to be skipped rather than throw.
- **A hidden commit is dropped from the selection.** `SelectedCommits` is the pair being compared; a
  selection pointing at a row that is no longer listed would compare something the user can no longer
  see and cannot re-pick.

The filter is cleared alongside `BranchFilter` in `ClearRepositoryState`. One left over from the previous
repository would hide this one's commits and look as though they had gone missing.

### 5.5.4 Selection mode

`IsSelectionEnabled` is private-set and defaults to `false`. The toolbar `Choose files` toggle
drives it through `ToggleSelectionCommand`; `IsChecked` is bound `Mode=OneWay` so the toggle's
face is corrected from real state rather than drifting.

Captioned for what it does rather than for what it resembles: `Select` beside a list of commits
reads as selecting rows, which this does not do and which the commit list has its own gesture for.
It chooses *files* out of the comparison, which is what the check boxes it reveals are for
(FR-107, SR-58).

A one-way binding alone reports no clicks — there is no setter to push them into — and the
toggle would flip its own face while nothing else happened. Something must run on the click.

Turning selection **on** ticks everything (starting from nothing ticked would look like a
deliberate choice and quietly produce an empty export). Turning it **off** preserves the ticks
(hidden is not forgotten; otherwise turning it off would silently re-include excluded files).

It is not persisted: it describes the current session, and a window reopening with every file
unticked because the last session ended on "None" would export nothing without saying so
(FR-72).

### 5.5.5 A branch list that could not be refreshed

`FetchIfNewRepositoryAsync` fetches once per repository root and returns a `FetchOutcome`, not a
bool. The distinction is the whole feature:

| Outcome | What it means | What the user is told |
| --- | --- | --- |
| `Updated` | the refs are current | `(branches updated from the remote)` in the status line |
| `NoRemote` | nothing to fetch, nothing that can be stale | nothing |
| `Failed` | the branches on screen are as of the last fetch that worked | the notice in the picker, the clause in the status line |

Collapsed into "not fetched", `NoRemote` and `Failed` produce the same screen: a complete-looking
branch list with nothing saying otherwise. That is the one state a reader cannot detect for
themselves, and every number the badge shows — ahead, behind — is measured against an upstream ref
that may itself be out of date (FR-104, SR-57).

`BranchesMayBeStale` is set from the outcome in the same dispatcher hop that publishes the list,
and cleared by a fetch that works, by a repository with no remote, by the invalid-directory path,
and by `ClearRepositoryState` — a warning about the previous repository's remote would be pointing
at branches that are no longer on screen.

Three details:

- **The notice is in the picker, not the window.** A strip in the window would appear and disappear
  with the fetch, moving the row it sits in each time, which is what SR-35 forbids. The picker is
  where somebody goes to look at branches, and its size is not the window's business.
- **The badge's tooltip carries it too** (`ActiveBranchTooltip`), because a badge this small has
  room for one sentence and nowhere else to put it.
- **Retry forgets the repository, not just the warning.** `_lastFetchedRoot` is why typing in the
  path does not refetch on every keystroke. Clearing the flag without clearing that would leave the
  button pressing and nothing happening.

## 5.6 `DoubleClickBehavior`

An attached property, so a list needs no code-behind and the command binds like any other.

Two decisions, both of which were wrong at some point and are now tested:

**It listens for a real double-click.** An earlier version hooked `PreviewMouseLeftButtonDown` —
the *first press*. Every single click on a row ran the command, including clicks on a checkbox
inside it, which is how ticking a file also launched a diff tool. It now hooks
`Control.MouseDoubleClickEvent`.

**It ignores hits on controls that own their click.** A hit is tested by walking up from the
original source; a `CheckBox`, `ButtonBase` or `TextBoxBase` before the row means the gesture
belongs to that control. A tick means "export this file"; opening a diff on the same gesture
makes the file impossible to untick without also launching a tool over it.

The decision is exposed as `internal static bool TryResolveRow(control, originalSource, out item)`
so it can be tested against a realised list without synthesising a mouse event. Note that on a
false result the `item` is cleared too — a caller ignoring the return value must not act on the
row the pointer happened to be over.

## 5.7 `CommitDetailViewModel`

Seeded from the log row before any I/O, so the window opens with something on screen rather
than an empty frame:

```
constructor : Subject = commit.Message; ShortHash = commit.ShortHash; RefNames = commit.RefNames
LoadAsync   : Task.WhenAll(GetCommitDetailAsync, GetChangesForCommitAsync)
```

The two calls are independent and started together; the window is already visible by the time
they return. A failure is recorded in `ErrorMessage` and shown in the window rather than
thrown — the user opened a window to read a commit, and an exception there would take down the
application over one row (SR-22).

Totals skip files with no count rather than counting them as zero — same answer for the sum,
but stated so the absence is not later mistaken for a bug. Binary files are counted separately
and reported explicitly (FR-64).

### 5.7.1 Diffing one of the commit's own files

The file list carries `DoubleClickBehavior.Command="{Binding ShowDifferenceCommand}"`, the same
attached behaviour the main window's list uses, so the gesture is one users already have. The
parameter is the double-clicked `GitFileChange`, not a selection, so the row under the pointer is
the one that is compared (FR-58, FR-59).

The two sides are `Detail.Parents[0]` and the commit itself:

```
BaseHash  = _detail.Parents.Count > 0 ? _detail.Parents[0] : string.Empty
LaunchAsync(git, change, BaseHash, _commit.Hash, _diffToolCommand, CancellationToken.None)
```

That is deliberately the pair the file list itself was built from (`git show --name-status` against
the first parent), so what the tool opens is what the list is a summary of. Diffing a merge against
its second parent instead would show changes the list never mentioned.

`CanShowDifference` is false for a root commit, for an empty file list, while a read is in flight and
while a launch is in flight. A root commit has no parent, so there is nothing to compare it against,
and saying so beats letting the double-click fail (FR-98).

The launch is fire-and-forget with its outcome recorded in `DiffMessage`, for the reason SR-22 gives:
the user opened a window to read a commit, and a diff tool that is missing or misconfigured is not
worth taking the application down for. Without that message a double-click on a machine with no tool
configured would look like nothing had happened at all (FR-99).

## 5.8 XAML resource strategy

### 5.8.1 One application-level dictionary

Every resource used by more than one window lives in `Controls.xaml`, merged at application
level. **A window cannot see another window's resources**, and the failure is a runtime
`StaticResourceExtension` throw when the window is constructed — not a compile error and not a
build error, because XAML compiles to BAML and the lookup happens on load.

`The_diff_tool_picker_window_loads` exists purely to catch this class of failure. It constructs
the window, which is sufficient: `InitializeComponent` runs the BAML through `XamlReader` and
resolves every `StaticResource` at that moment.

It deliberately does **not** call `Measure`/`Arrange`. A `Window` drives its own layout through
an `HwndSource` that does not exist until the window is shown, so measuring an unshown window
blocks on a presentation source that only `Show()` would create.

### 5.8.2 Binding modes

Read-only properties bind `Mode=OneWay`. WPF's `TextBox.Text` defaults to `TwoWay` and
rejects a read-only property at runtime:

```
System.InvalidOperationException : A TwoWay or OneWayToSource binding cannot work
on the read-only property 'CommandPreview'
```

Neither the compiler nor the build catches this. It was found by a test that constructs a
window, and is now prevented by SR-49 plus the window-construction tests.

### 5.8.3 The repository card row, and why `VerticalAlignment` is not the interesting part

The repository card is one row of five columns: Repository (star), Branch, Show last, Refresh, and
the trailing working-tree state. The first three are fields with a label above a 34-tall control, so
the row is as tall as a label plus a control rather than as tall as a control.

That has one consequence, and it is the reason the row needs saying down. A control in the row must
be **bottom**-aligned, not centred and not stretched:

- Every control is 34 tall, so a control dropped into the middle of a row that is a label taller
  than it sits exactly one label above the others, and reads as belonging to the caption beside it.
- The implicit `Button` style sets `Height` explicitly, so `VerticalAlignment="Stretch"` does not
  stretch it — WPF arranges an element with an explicit height at that height within its slot, which
  for a taller slot means the same thing as `Center`. Stretch reads as "fill the row" and does not do
  that here, which makes it the most misleading of the three.
- The trailing state already uses `Bottom` for the same reason, so anything else in the row would
  disagree with it.

The design bottom-aligns the row (`align-items: flex-end`), which is the arrangement the bottom
alignment reproduces.

`VerticalAlignment` in the markup is not evidence for any of this, which is why the assertion is on
rendered geometry instead. `The_controls_on_the_repository_row_share_one_bottom_edge` shows the
window, lays it out, and compares the transformed bottom edge of the path box, the count and the
Refresh button against one another in the row's own coordinates. It was verified to fail when the
button is set back to `Center`, which is the only way to be sure it is measuring the arrangement
rather than passing on an empty layout.

### 5.8.4 Model-provided derived text

`FileName`, `DirectoryText`, `SizeText`, `AddedText`, `DeletedText` are computed once on
`GitFileChange` at parse time. XAML binds them; it does not derive them. The same computation
per visible row would be repeated on every scroll, on a virtualised list.

### 5.8.5 A control that appears on hover, and the two things that break

The commit row's details button is collapsed until the row is hovered or has keyboard focus. Three
things that are easy to get wrong, and how each is handled:

- **`IsMouseOver` is not a dependency property**, so a control inside a row cannot bind to the row's on
  its behalf — and a style cannot target a descendant. `RowHover.IsHovered` is an attached property
  that carries the same answer across as something bindable; the row's template triggers set it.
- **A collapsed control cannot be reached by keyboard.** It is collapsed exactly when nobody is
  pointing at it, so tabbing walks straight past it every time. `IsKeyboardFocusWithin` sets the same
  property, which keeps the row the keyboard is on fully usable. This is the real cost of hover-only,
  and it is what pays it.
- **An `Auto` column would reflow the row.** The button collapses, so an `Auto` column would take its
  width with it and the subject would jump sideways on every pass of the pointer. The button's column is
  a fixed 34 and the row's width is identical hovered or not.

The dots are three `Ellipse`es rather than a typed `…`: a glyph would render at whatever baseline and
weight the font chose, which is not what the row's own text does. `AutomationProperties.Name` carries the
wording for a screen reader, which is the one consumer that cannot infer the button's purpose from three
dots — and it is also what the tests identify the button by.

### 5.8.6 The change document checker (experimental)

Four pieces: reading the document, finding the paths in it, matching those paths against two folder
trees, and writing the disagreements out.

**Reading the `.docx`.** A `.docx` is a zip holding XML; the body is `word/document.xml` as paragraphs
of text runs. `System.IO.Packaging` opens it, because that is the OPC API Office itself uses and it
is already referenced by WPF. The one thing that matters is that **line structure survives**: a
document lists one file per bullet or per table row, so a reader that flattened the file into one
string would turn forty paths into forty sentences with no boundaries. Paragraphs are the entries, and
a table *row* is the entry — not a cell, and not the paragraph inside a cell. Cells are joined with a
tab, so `path | description` stays one entry and the path stays separable from the prose beside it.
Getting this wrong took three attempts: the first version flushed on every paragraph and turned a
two-column row into two lines.

**Finding the paths.** The document is read as what it is — a document with a section in it — rather
than as prose to be mined for anything path-shaped. Two headings bound the list: everything between
`[Modified Files]` and `[Status]` is the list, and everything outside it is not. The headings are
compared as whole lines with the brackets and a trailing colon stripped, so `[Modified Files]`,
`[Modified Files]:` and `Modified Files` are the same heading while *"the Modified Files section
below lists…"* is a sentence that happens to contain one.

Inside the section, one rule: **a run carrying a slash is a path.** Blank lines and lines of
commentary are skipped and the list carries on, because the section — not the first line without a
path — is the boundary, and ending the list early would silently drop everything after it. A line
may carry more than one path, separated by a tab or by two or more spaces. `1.`, `2)`, `(3)`, `-`
and `•` are stripped from in front of a path, so numbering is incidental.

**Everything inside the section is normalised by one function**, `ChangeDocumentParser.Normalize`,
and the verifier runs both sides through it, so neither side can be right by accident. Separators are
unified, because a document written in Word on Windows spells a path with backslashes and a folder
walk spells it with slashes. Surrounding whitespace and slashes are trimmed, and a leading `./` is
dropped, because `\Src\Parser.cs`, `/Src/Parser.cs` and `./Src/Parser.cs` are one path written from
three starting points. A URL is rejected: its slashes pass every other test here, and it is the one
token whose shape really is a path's.

Two rules keep the section's own commentary out, and each exists because the alternative produced a
finding blaming the document for a reader's punctuation:

- **a path must carry at least one slash**, so `Parser.cs`, `Dockerfile` and `.gitignore` are not
  paths. This is the trade for not having to decide whether a word is a file name or an English word:
  a document that means to name a file at the root writes the folder it is in;
- **the file name may not contain a space**, so `and/or the notes below were changed too` is not one
  very long path. A space in a *folder* name is still fine, which is what keeps
  `TestAuto Layer/api/app_context.py` whole while stopping at two or more spaces, not at one.

Nothing is excluded on grounds of what it names. A path under `bin` or `obj` is extracted like any
other, because whether a file counts as build output is a question about the comparison and not about
what the document said — and the verdict answers it as **Not compared** rather than as missing.

**Matching a claim to a file.** The claim is read **from the right, on whole folders**: it matches
when it is the whole of a real path or the tail of one.

```
Src/Module1/View2/Frames/wrwe.cs   matches Src/Module1/View2/Frames/wrwe.cs   (the whole thing)
Module1/View2/Frames/wrwe.cs       matches the same file                       (a tail of it)
Frames/wrwe.cs                     matches the same file                       (a shorter tail)
32fws/wrwe.cs                      names two files, or none                    (a suffix must land on a separator)
```

Comparing on whole folders rather than on characters is what keeps `eReader.cs` from matching
`LegacyReader.cs`, and comparing a tail rather than the whole string is what lets a document be
written from part way down the tree without every such path being called missing. A document that
wrote fewer folders than it meant to has not made a false claim about anything, so it is a match and
nothing is reported — which is why there is no partial-match finding.

Two files ending with the same claim is **no answer at all**: naming either would put a guess in the
output where a fact belongs, and the reader would have no way to tell. It is left unresolved, and the
claim is reported as not present.

The result is that the extractor needs no knowledge of the folders and the verifier no knowledge of
the prose. Neither has to be trusted about the other's half of the problem, and neither needs a rule
per way a document can be untidy.

**Comparing the folders.** Byte-for-byte, with a length check first so a file that cannot match is
never read. Reported paths are **relative**, because that is what a document lists and what a reader
needs to see; the two trees sit at different absolute paths by definition, so an absolute path here
would name nothing. The roots are kept anyway, because a reported path has to be openable.

A path present on both sides with different bytes is a difference; a path present on one side only is
an addition or a removal. A file whose content is identical but whose path moved is reported as a
**move** rather than as a deletion plus an addition — one change, one finding, and the document's
`old -> new` arrow and the folder walk then agree with each other. Matching is on the content hash,
so a move of a modified file is still a move.

**Build output is skipped by default** (`bin`, `obj`, `.vs`, `.git`, `.svn`, `.hg`, `node_modules`,
`packages`, `TestResults`). These are source folders, and a compiled assembly differs on every build,
so including them would drown the real changes. The count is reported either way — files compared and
files left out — so a pass is a statement about what was examined and never an absence of complaints.
A claim about a file under a skipped folder is answered as **Not compared** rather than as missing:
the tool did not look, and saying "no such file" would be a wrong answer to a question it never asked
(FR-92).

**Findings are ordered by what a reader fixes first**: duplicates, then wrong paths, then claims that
name nothing, then claims of an edit that is not there, then undeclared changes, then the tidiest.
Ties break on the path, so the order is the same on every run and two people reading two copies work
from the same list.

**Getting the findings out.** `ChangeReportWriter` writes them as CSV or as tab-separated text.
Quoting follows RFC 4180 — a field holding a comma, a quote or a newline is wrapped and its own
quotes doubled — because the paths and the sentences both routinely contain commas, and a file
written without it is a file Excel splits in the wrong places. Files are written as UTF-8 **with** a
BOM, which is what makes Excel read a non-ASCII path correctly on a machine whose default codepage
would otherwise mangle it. Both commands are disabled until a check has run, because a button that is
merely able to answer is not the same as a button that is enabled.

The clipboard and the save dialog are injected as delegates, like the folder and document pickers, so
the commands are testable without a window — and both default to doing nothing rather than throwing,
because a caller interested only in verifying should not have to supply a clipboard it will never use.

### 5.8.7 Captions that name what they do

Three captions in the OUTPUT card were each describing something other than their own effect, and all
three are bound to the view model rather than written once, because each depends on state:

```
Open button          Content  = {Binding OpenOutputCaption}     "Open output folder" / "Open last result"
                     ToolTip  = {Binding OpenOutputTooltip}     "…No export has run yet." / "…the last export produced."
Choose files toggle  Content  = "Choose files"
Verify button        Content  = "Verify change document..."
                     AutomationProperties.Name = "Verify change document against two source folders (experimental)"
```

The Open button is two actions wearing one name: before a run it opens the configured output
directory, and afterwards it opens the run folder the last export produced. The reader had no way
to tell which except by checking whether the run log had anything in it, which is exactly the kind
of inference a caption exists to remove (FR-107, SR-58).

The verify button's full name was forty characters wide in the middle of the action bar, and a
caption that long is read as a heading rather than as an action. Shortened to say what it *takes*; the
full description stays on the tooltip and on `AutomationProperties.Name`, which is where a
screen-reader user gets it, so nothing was lost but the width.

## 5.9 Test architecture

369 tests across 29 files, all against the real behaviour.

| Category | Approach |
| --- | --- |
| Git parsing and export | Real temporary repositories created per test (`TempRepository`), committed to with real Git. A hand-written sample would only prove the parser can read itself. |
| Pure logic | Direct unit tests: argument quoting, converter output, filters, totals. |
| View models | Constructed with a real `GitService` over a temporary repository. |
| Windows and markup | A shared STA host with one `Application` instance and the `Controls.xaml` dictionary merged (`WindowHost`); markup is loaded and bindings read, not text-matched. |

`Sta.cs` runs a body on an apartment thread. `WindowHost` exists because `Application.Current`
is per-process and every `StaticResource` lookup depends on it existing before any window loads.

`TempRepository.CommitWithBody` writes its message file to the system temp directory rather
than inside the repository — writing it inside would make the following `git add -A` stage
the message file, so every such commit would report one extra changed file and quietly break
any test that counts them.

`TempDocx` builds real `.docx` packages rather than supplying document text directly. A stub
reader would let the parser tests pass while the actual zip layout failed to open, which is
the part most likely to be wrong and least likely to be exercised by hand.

### 5.9.1 A note on lost stack traces

`WindowHost.Run` captures the exception from the body and rethrows it with `throw failure;`.
That resets the stack trace, so a failure inside a body shows only `WindowHost.Run` and the
test method — the real frame is gone. When bisecting a failure in a window test, wrap the
suspect region and surface the type and message as an assertion failure instead of guessing.
