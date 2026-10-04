# 4. Architecture Design

**Project:** GitDiffFolderCreator
**Applies to:** `GitDiffFolderCreator` (net461) and `GitDiffFolderCreator.Tests` (net472)

---

## 4.1 Architectural style

A layered **single-process desktop application** in the standard WPF presentation-framework
style, with one important deviation called out in §4.6: the *view* is deliberately thin, and
window-shaping concerns live in small view models that a window owns.

There is no service host, no dependency-injection container, and no plugin boundary. The
application runs one window and opens two satellite windows. The size does not justify the
indirection.

## 4.2 Layering

```
┌──────────────────────────────────────────────────────────────────┐
│  View layer                                                      │
│  MainWindow · DiffToolPickerWindow · CommitDetailWindow          │
│  Controls.xaml (resource dictionary) · CollapsibleGroupBox       │
│  Behaviors (DoubleClickBehavior · SelectedItemsBehavior · RowHover)   │
│  Converters (StringToVisibility · CommitRoleToVisibility · …)    │
├──────────────────────────────────────────────────────────────────┤
│  Presentation layer                                               │
│  GitViewModel · DiffToolPickerViewModel · CommitDetailViewModel  │
│  BindableBase · RelayCommand · AsyncRelayCommand                 │
├──────────────────────────────────────────────────────────────────┤
│  Service layer                                                    │
│  GitService · DiffExporter · DiffToolLauncher · DiffToolCatalog  │
│  CommitRangeResolver · GitProcessRunner · AppSettingsStore       │
│  WindowsArgumentString · ShellFolderPicker · ShellFolderOpener   │
│  GitCommandException                                              │
├──────────────────────────────────────────────────────────────────┤
│  Model layer                                                      │
│  GitCommit · GitCommitDetail · GitBranch · GitFileChange         │
│  GitRepositoryStatus · CommitRange · GitCommitRole · GitChangeStatus│
│  ObservableModel (INotifyPropertyChanged base)                   │
└──────────────────────────────────────────────────────────────────┘
                                │
                    ┌───────────▼────────────┐
                    │  git.exe  (external)   │
                    └────────────────────────┘
```

**The single rule that keeps this honest: the dependency arrow points down only.** Views bind
to view models. View models call services. Services produce models. Nothing calls upwards.

### 4.2.1 Consequences of the rule

- A view never builds a Git command line. `DiffToolButton_Click` in `MainWindow.xaml.cs`
  constructs a `GitService` and hands it to a view model; it does not ask Git anything itself.
- Raw Git output never reaches a view. Every service method parses before returning.
- A view model is testable without a window. `CommitDetailViewModel` is constructed with a
  `GitService` and a `GitCommit` and is exercised directly in tests.

## 4.3 Assembly layout

| Assembly | Target | Contents |
| --- | --- | --- |
| `GitDiffFolderCreator.exe` | net461, WinExe, UseWPF | Everything in the four layers above. |
| `GitDiffFolderCreator.Tests.dll`           | net472, xUnit | 360 tests. References the app assembly. `InternalsVisibleTo` grants access to `internal` seams. |

The test project targets a *higher* framework than the application. This is intentional: the
application must run where only the .NET Framework is present, while the current xUnit packages
require 4.7.2 or later.

## 4.4 Dependency inversion at the process boundary

`git.exe` is outside the application's control and is treated as an unreliable collaborator.

```
GitViewModel / DiffExporter / GitService
        │
        ▼
GitProcessRunner ──── Process.Start ────▶ git.exe
        │                                  │
        │  captures stdout, stderr, exit   │
        ◀──────────────────────────────────┘
```

Every Git access goes through `GitProcessRunner`, which is the only place in the application
that starts a process for Git. It:

- builds an argument list (never a command string),
- sets the working directory to the repository root,
- redirects all three streams,
- drains stderr concurrently with stdout, so a chatty command cannot deadlock on a full pipe,
- supports `StandardOutputEncoding = null` for binary reads, which is how file extraction avoids
  UTF-8 corruption of arbitrary bytes,
- exposes the raw exit code so callers can distinguish "not found" from "failed".

## 4.5 View models

| View model | Backs | Notes |
| --- | --- | --- |
| `GitViewModel` | `MainWindow` | The application shell. Owns repository state, selection, filtering, export orchestration and status. Around 2,000 lines; the largest type in the codebase. |
| `DiffToolPickerViewModel` | `DiffToolPickerWindow` | Owns the diff tool selection state and the custom-command draft. |
| `CommitDetailViewModel` | `CommitDetailWindow` | Loads one commit on demand and owns the file filter and totals. |

Commands are `RelayCommand` and `AsyncRelayCommand` (both in `BindableBase.cs`). Each command
carries a `CanExecute` predicate and is re-evaluated through a single `UpdateCommandStates`
entry point, so that command enablement cannot drift between controls.

## 4.6 The deviation: views stay thin, but windows own their view models

`DiffToolPickerWindow` and `CommitDetailWindow` do **not** take a view model as a constructor
parameter. Each exposes a static factory that constructs its own view model, starts the load,
and returns the window:

```csharp
public static CommitDetailWindow Open(
    Window? owner, GitService git, GitCommit commit, CancellationToken cancellationToken = default)
```

**Why:** these two windows are opened from a click handler in the main window. Routing them
through `MainWindow`'s view model would give `GitViewModel` responsibility for the *content* of
dialogs it does not own, and would put `CommitDetailViewModel`'s properties into the main
window's binding surface for no benefit. The factory keeps the dependency pointing one way
while letting each window own exactly one view model.

**The cost, accepted deliberately:** a window is now harder to test in isolation than a
`Window(viewModel)` would be, because construction also starts I/O. The tests compensate by
injecting a real `GitService` over a real temporary repository and asserting on the view model
after the load resolves.

## 4.7 Data flow

### Loading a repository

```
user edits path
   │
   ├─► RefreshLogAsync (async, cancellable)
   │      ├─ GitService.GetRepositoryRootAsync
   │      ├─ FetchIfNewRepositoryAsync        ← only network stage; failure is survivable
   │      ├─ ReadBranchesAsync
   │      ├─ GetLogAsync(LogLimit, ActiveRef)
   │      └─ ReadStatusAsync
   │
   └─► single dispatcher callback repopulates Commits / Branches / Changes / status
```

Branches, log and status are read **concurrently**. A failure in one is reported without
preventing the others from populating, which satisfies FR-04. Only the dispatcher callback
touches observable collections, so no binding is ever updated off the UI thread.

### Exporting

```
CreateFoldersCommand
   │
└─► DiffExporter.ExportAsync(request, progress, ct)
          ├─ GetChangesAsync(base, modified)          → the file list
          ├─ apply ExcludedPaths                      → warning if anything dropped
├─ ExportFilesAsync(base)    → staging\base      (one cat-file --batch; missing ignored, clashes/omissions warned)
           ├─ ExportFilesAsync(modified) → staging\modified  (missing → warning, clashes/omissions warned)
           ├─ VerifyFolder(base, modified)                  → read the staged folders back, warn on anything unaccounted for
           ├─ WriteManifests(staging)
           └─ publish staging → diff_<a>_<b>_<ts>       ← atomic move

ExportProgress{ Message, Completed, Total } is reported throughout: total 0 while the file list is
being read (the bar animates), then one report per path answered on either side, so the bar is
determinate for the rest of the run.
```

The base export's result is intentionally unused: its missing paths are files that were
**added**, which is the expected outcome of a comparison and not something to warn about
(FR-44). The modified export's missing paths are files that were **deleted**, and are worth
reporting (FR-43).

`VerifyFolder` runs after both sides are written and before the manifests, while the staging
directory still exists to be corrected. Everything above it works from what Git said; this works
from what is on disk, which is the only evidence that separates "Git supplied the file" from "the
file landed" (FR-87). It reports only discrepancies nothing else explains — a folder comparison
legitimately produces folders of different sizes, so treating every difference as a fault would
bury the real one. Its comparisons ignore case, because the destination filesystem does (FR-88).

### The diff tool catalog lists install locations, and those go stale

`DiffToolCatalog` finds a tool by trying a list of paths under the three install roots
(`Program Files`, `Program Files (x86)`, `LocalApplicationData`), then `PATH`. Those roots are fixed;
the paths under them are what each installer happens to choose, and they change between versions.

The failure mode is silent and total. A location that is not searched produces no tool at all — no
error, no empty entry — so the tool is simply missing from the picker on exactly the machines that
have it, while every test still passes. This is not hypothetical: VS Code's per-user installer writes
to `%LOCALAPPDATA%\Programs\Microsoft VS Code`, not straight into `%LOCALAPPDATA%`, so a
catalog searching only the latter never found a user-installed VS Code — which is how most people
have it — while the existing test passed, because it only checked the shape of the entries the
machine happened to return.

Two consequences are built into the code rather than left to review:

- **`Discover(IEnumerable<string> installRoots)` is the seam.** The public `Discover()` supplies the
  real roots; the tests supply a tree built for the purpose and assert that a tool at the documented
  per-user location is found. Asserting against the real machine could only ever prove the machine
  running the tests is configured the way it already is.
- **Only executables are listed, never batch files.** `DiffToolLauncher` starts the program directly
  with `UseShellExecute` off, so a `.cmd` shim cannot be run at all. VS Code's `bin\code.cmd` sat in
  the list, and had it ever matched it would have produced a picker entry that failed on use — which
  is why the `PATH` fallback, which matches on exact file name, also never matched it.

The `PATH` fallback exists for portable installs. It matches whole file names, so it finds `Code.exe`
where it is on `PATH` and not `code.cmd` beside it.

## 4.8 The change document checker (experimental)

A separate, self-contained feature with no repository in it: given two source folders and a Word
document listing the changed files, it reports where the document and the source disagree. It exists
because those three artefacts are kept by hand, and a document states its list in a section while the
folders only say what is on disk — neither can check the other.

```
ChangeDocumentWindow  ──►  ChangeDocumentViewModel
                                │
                    DocxTextReader ──► ChangeDocumentParser ──► documented paths
                                │
                    FolderComparer ──► FolderComparison ─────┐
                                                             ├──► ChangeDocumentVerifier ──► findings
                    ChangeDocumentVerifier ─────────────────┘
```

The parser is deliberately not a prose reader. It looks for the `[Modified Files]` heading, reads to
`[Status]`, and takes every run carrying a slash in between — which is the whole of the rule, and the
reason a URL, a version range or half a sentence elsewhere in the document cannot become a finding
about a file that does not exist. Matching is the mirror image of that: a claim is normalised and
compared against the real paths from the right, on whole folders, so a path written from part way down
the tree still names its file. See §5.8.6.

It reuses one idea from the export and nothing else: §5.3.3a's rule that a check must report only
what nothing else explains, and its case-insensitive comparison. It shares no code with `DiffExporter`,
`GitService` or `GitViewModel` — the two features have nothing in common but that rule, and coupling
them would mean a change to the export could break a tool that never touches git.

`System.IO.Packaging` (from `WindowsBase`, already referenced) reads the `.docx`, which is a zip of
XML. The alternative — a zip reader plus hand-rolled part naming — is what OPC exists to avoid.

### 4.8.1 Why the window-loads test lives in `MainWindowTests`

`WindowHost`'s constructor creates the one `Application` the process is allowed, on its own STA
thread. A second `WindowHost` — which is what a test class gets if it declares its own — throws. So
every window in this application is constructed by `MainWindowTests`, including this one, even though
its tests are otherwise elsewhere.

## 4.9 Cross-cutting decisions

### Parse, don't re-query

Model properties that are expensive or fickle to derive are computed once at parse time and
carried on the model — `GitFileChange.FileName`, `DirectoryText`, `SizeText`, `DisplayText`;
`GitCommit.CommiterDateText`. Deriving them in XAML would repeat the work for every visible row.

### Model properties are read-only on purpose

`GitFileChange.IsIncluded` is the one writable state on a model; it is the user's decision and
the list binds to it `TwoWay`. Everything else on a model is computed. This is why the commit
`Details` dialog binds its read-only text `Mode=OneWay` (SR-49).

### Cancellation is per-operation, not per-window

Each long operation creates its own `CancellationTokenSource` via `BeginOperation`, which
cancels and disposes the previous one. Starting a refresh while a refresh is running therefore
cancels the first, rather than running both.

Two rules follow from that, and both were wrong before they were written down. A superseded
operation must not clear the loading state on its way out — the newer one owns it — so the
`finally` that clears it checks its own token first. And an operation that returns early must
still clear it, so every early return from `RefreshLogAsync` sits **inside** the `try` the
`finally` belongs to. A refresh that cancelled another and then found the path invalid used to
satisfy neither rule at once, and left the ring turning on an idle window.

The same rule governs the pipe: every read of `git cat-file --batch` goes through `Fill`, which
checks the token and treats end of stream as an error. Reading the pipe directly, as skipping an
unwanted blob did, is what turns a killed git into a loop that never exits — a read returning 0
looks exactly like "nothing buffered yet".

### Three outcomes for one fetch, because two of them mean opposite things

`GitService.FetchAsync` returns `Updated`, `NoRemote` or `Failed`, not a bool. A repository with
no remote has nothing to be out of date; a repository whose remote could not be reached is showing
branches as of the last fetch that worked. Collapsed into "not fetched", the second case shows a
complete-looking branch list with nothing on screen saying otherwise, which is the one state a
reader cannot detect for themselves. `GitViewModel` turns `Failed` into the notice in the branch
picker and a clause in the status line; the other two say nothing (FR-104, FR-105, SR-57).

### A temporary folder the tool may still be reading is swept later, not sooner

A successful diff hand-off leaves its two extracted versions in `%TEMP%\gdfc-compare-<id>`. The
tool may still be reading them, and it may have been started by a launcher process that has
already exited, so there is no moment at which the launcher knows they are finished. Windows does
not remove them either, which is why repeated comparisons — of large files in particular — fill
the disk.

So the policy is age-based rather than event-based: a later launch deletes the folders carrying this
class's own prefix that are older than a day (FR-106). A day is far longer than any comparison
lasts, so nothing can be in use; and a folder that cannot be deleted is skipped and offered again
next time, because a lock by somebody's diff window is the expected case, not a failure (SR-30).

### Controls are styled implicitly, not per instance

Every `TextBox`, `Button`, `ToggleButton` and `ComboBox` draws itself from an implicit style in
`Controls.xaml`, and they share one `ControlRadius` resource for their corner radius. Two reasons.

The first is consistency: the controls are one family, and a control that asks for a keyed style
for a special reason — the accent-filled export, the red interrupt — can drift away from the rest
without anything noticing. An implicit style is applied to anything that does not override it, so a
new button is already in the family.

The second is that rounding a control means replacing its template, and a replaced template is
where input is lost: the caret, the selection and the scroller are all attached to the
`PART_ContentHost` that the platform's template declares. The styles here declare it, and
`A_restyled_text_box_still_takes_input` and `Every_box_and_button_carries_the_same_corner_radius`
guard both that and the shared radius.

Two controls are deliberately outside the family and are not measured by that test: a `CheckBox`
is a tick rather than a box, and the branch badge's button is the hover tint *inside* the field
shell drawn around it, so its own border is a shade tighter than the shell.

A third variant, `SearchBox`, adds a drawn magnifying glass to a text box. It exists because the
three filter boxes were indistinguishable from the path and count fields beside them, and what they
do is narrow a list rather than set a value. The glass is geometry rather than a glyph so it does
not change with the platform's font, and it is drawn inside the box rather than beside it so the
box keeps its width. Its padding is wider than the shared one because the icon has to fit inside
it — an icon laid over an unchanged padding draws correctly and puts the first character underneath
it, which `A_filter_box_carries_a_search_glass_clear_of_its_text` checks by comparing the rendered
positions rather than reading the markup.

The glass is drawn as two arcs, and that is worth stating because getting it wrong is silent: two
arcs close into a circle only when their endpoints are exactly two radii apart, so endpoints any
closer bow the same way twice and produce a lens — a curved blob beside the handle line, which
reads as two separate marks. `The_search_glass_is_a_ring_and_not_a_lens` samples the flattened
outline and checks the radius holds, because the markup parses to a `StreamGeometry` that will not
give up its arc endpoints to be compared directly.

The style is shared by all three filter boxes — the changed-files filter, the branch filter inside
the popup, and the commit detail window's own file filter — but the binding on each is its own, and
they are not checked together by accident. The glass is measured in the window each box actually
belongs to, because a style that draws correctly in one window says nothing about another, and each
box's binding is asserted as well: a box that kept the glass and lost its binding would pass every
geometric check above while filtering nothing. The two in `MainWindow` also differ in when they are
realised — the branch filter exists only once its popup has opened, which is why the search of the
main window's visual tree has to happen after the branch list is opened rather than at load.

## 4.10 Deployment

- Single Win32 executable plus its config; no installer, no service, no elevation.
- Requires Git on `PATH`.
- Settings live in a single JSON file under the user's local application data.
- The application never writes to the user's repository (SR-24).

## 4.11 Known architectural debt

| Item | Assessment |
| --- | --- |
| `GitViewModel` at ~2,000 lines | Acceptable but at the limit. The natural split is log state, selection state, and export orchestration into three collaborators. Not yet worth the churn. |
| Windows construct their own view models (§4.6) | Accepted trade; documented above. |
| Windows cannot be constructed without starting I/O | Makes window tests slower than a plain constructor would, but keeps them honest. |
| No commit filtering in the log | A missing feature, not an architectural one. |
| `DiffExporter` reports progress per stage only | Resolved: `ExportFilesAsync` takes a per-path callback, so the bar is determinate once the file list is known. |