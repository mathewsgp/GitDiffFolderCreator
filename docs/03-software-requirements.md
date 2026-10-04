# 3. Software Requirements Specification

**Project:** GitDiffFolderCreator
**Applies to:** `GitDiffFolderCreator.exe` (WPF, .NET Framework 4.6.1) and `GitDiffFolderCreator.Tests.dll` (xUnit, .NET Framework 4.7.2)

---

## 3.1 Purpose

The functional requirements in document 2 state *what* the system does. This document states
the constraints it must satisfy to do so acceptably: platform, environment, performance,
reliability, security, usability and maintainability.

Requirement IDs use `SR-nn`. "MUST" levels are release-blocking.

---

## 3.2 Platform and runtime

| ID | Requirement |
| --- | --- |
| SR-01 | The system **MUST** run on Windows 10 or later. |
| SR-02 | The system **MUST** target .NET Framework 4.6.1 or later and **MUST NOT** require a .NET Core/.NET 5+ runtime. Many corporate Windows installs have only the .NET Framework. |
| SR-03 | The system **MUST** be a single-window WPF desktop application; it **MUST NOT** require a local web server, a service, or administrator rights to run. |
| SR-04 | The system **MUST** be a self-contained executable with no third-party runtime dependency. The only external dependency is Git itself. |
| SR-05 | The test suite **MUST** target .NET Framework 4.7.2 or later so that xUnit's current packages are supported. (The application targets a lower framework than the tests; this is deliberate and harmless.) |
| SR-06 | The build **MUST** produce **zero** compiler warnings at `Warning level` default and **MUST** treat warnings as reviewable, not ignorable. |

## 3.3 External dependencies

| ID | Requirement |
| --- | --- |
| SR-07 | The system **MUST** shell out to a Git executable found on `PATH`, resolved once per operation and reported if absent. |
| SR-08 | Git commands **MUST** be executed with the working directory set to the repository root, so path arguments are interpreted correctly. |
| SR-09 | Git **MUST NOT** be trusted to produce UTF-8. The system **MUST** read binary output byte-for-byte rather than through a text decoder, because decoding a binary blob as text corrupts it. |
| SR-10 | Every Git invocation **MUST** capture stdout, stderr and the exit code, and **MUST** surface a non-zero exit as a diagnosable error carrying the command line and stderr. |
| SR-11 | A user-configured external diff tool **MAY** be any executable; the system **MUST** not assume it is a known tool. |

## 3.4 Performance

| ID | Requirement |
| --- | --- |
| SR-12 | The window **MUST** remain responsive throughout repository loading and export. No stage may block the UI thread. |
| SR-13 | Loading a repository with 500 commits **SHOULD** complete within 3 seconds on a local disk, excluding network time for `git fetch`. |
| SR-14 | The commit list and file list **MUST** use list virtualisation with container recycling, so that scrolling a long list does not create one visual tree per item. |
| SR-15 | Any bulk change to a list or to per-file state — installing a comparison, include all, exclude all — **MUST** rebuild derived aggregates **once**, not once per item. A derived list rebuilt per item added is quadratic in the size of the comparison. |
| SR-16 | Filter keystrokes **MUST** be debounced so that typing in a filter box does not run a query per keystroke. |
| SR-17 | Long-running work **MUST** support cancellation, and cancellation **MUST** take effect promptly rather than at the end of the current stage. |

## 3.5 Reliability and data integrity

| ID | Requirement |
| --- | --- |
| SR-18 | A failed export **MUST NOT** leave a run folder that looks complete. Output **MUST** be built in a staging location and published atomically. |
| SR-19 | Staging directories **MUST** be removed on both failure and cancellation. |
| SR-20 | Publishing **MUST NOT** overwrite an existing run folder. |
| SR-21 | A path that Git cannot supply **MUST NOT** discard the paths it can. Each path **MUST** be requested and answered individually, so one bad pathname cannot fail the rest. |
| SR-22 | Any operation scoped to a single row — commit details, diff hand-off — that fails **MUST** degrade to a message in the relevant window and **MUST NOT** terminate the application. |
| SR-23 | Settings files that are absent, older than the current version, or hand-edited to be incomplete **MUST** load without exception. |
| SR-24 | The system **MUST NOT** modify the user's repository. It is a reader; the only Git command that writes is `git fetch`, which only updates remote-tracking refs. |
| SR-25 | Concurrent Git invocations **MUST** not corrupt shared state; each runs in its own process with its own redirected streams. |

## 3.6 Security

| ID | Requirement |
| --- | --- |
| SR-26 | The system **MUST NOT** run with elevated privileges, and **MUST NOT** require elevation. |
| SR-27 | Arguments passed to Git or to an external diff tool **MUST** be constructed as an argument list and quoted for `CreateProcess`, never assembled into a command string and passed to a shell. A path containing spaces, quotes, `&`, `|`, `^` or `%` **MUST** reach the target intact. |
| SR-28 | The system **MUST NOT** use `cmd.exe /c` or PowerShell to launch the diff tool. Arguments containing shell metacharacters would otherwise be reinterpreted. |
| SR-29 | Temporary files created for diff hand-off **MUST** be created under the system temporary directory, **MUST** use names that do not collide between runs, and **MUST** be deleted afterwards. A pair the tool may still be reading **MUST** be left in place for the moment, and **MUST** be removed by a later hand-off once it can no longer be in use, because Windows does not remove arbitrary temporary files on its own. |
| SR-30 | Cleanup of temporary files **MUST NOT** let a cleanup failure surface to the user or crash the application; file locks by a running diff tool are expected. |
| SR-31 | The system **MUST NOT** read or write anything outside the chosen repository, the chosen output folder, the settings file, and its own temporary files. |
| SR-32 | Repository paths supplied for display or comparison **MUST** be reduced to their filename component where they are used to build temporary directory names, so that a path cannot escape the temporary directory. |

## 3.7 Usability

| ID | Requirement |
| --- | --- |
| SR-33 | The application **MUST** follow Windows desktop conventions: standard accelerators, standard control sizing, no modal blocking during long operations. |
| SR-34 | Destructive or cancelling operations — Stop, Close — **MUST** be styled so they are not mistaken for the primary action. |
| SR-35 | Layout **MUST NOT** shift when transient text changes length. Status text that varies in length **MUST NOT** resize the panel containing it. |
| SR-36 | Text that can be long — commit subjects, file paths, ref names, commit messages — **MUST** be elided or wrapped deliberately, and **MUST** retain a way to see the full value (tooltip, selectable field, or wrapping region). |
| SR-37 | State that is derived rather than stored — a spinner shown while loading, the Stop button while idle — **MUST** occupy its space permanently, so that its appearance does not reflow the window. |
| SR-38 | A control whose meaning is not obvious **MUST** carry a tooltip stating what it does. |
| SR-39 | Any incomplete or unexpected state **MUST** be stated in words. An empty list **MUST** say why it is empty. |
| SR-40 | Colour **MUST NOT** be the sole carrier of meaning; status badges carry a letter as well as a colour. |
| SR-41 | The application **MUST** support keyboard-only operation of the primary workflow: choose two commits, export. |
| SR-57 | Data that may be out of date **MUST** say so. A branch list that could not be refreshed from the remote **MUST** be reported as being as of the last fetch that worked, with a way to try again, and a repository with no remote **MUST NOT** be reported that way — nothing about it can be stale. |
| SR-58 | A control whose effect depends on state the user cannot see **MUST** name that state in its caption rather than leaving the reader to infer it, and **MUST NOT** be captioned for a different action that happens to sound similar. |

## 3.8 Maintainability

| ID | Requirement |
| --- | --- |
| SR-42 | Layering **MUST** be enforced by convention and kept acyclic: views → view models → services → models. A view **MUST NOT** call a service. |
| SR-43 | All Git interaction **MUST** be confined to the service layer. No view or view model **MUST** construct a Git command line. |
| SR-44 | Raw Git output **MUST NOT** escape the service layer unparsed. Views and view models receive models only. |
| SR-45 | Public types **MUST** carry XML documentation comments explaining *why* a decision was made, where the reason is not evident from the signature. |
| SR-46 | Logic that can be separated from WPF **MUST** be so separated, so that it is testable without a dispatcher, a message loop, or an STA thread. |
| SR-47 | Test-only seams **MUST** be `internal` and visible to the test assembly via `InternalsVisibleTo`, rather than being made `public`. |
| SR-48 | Resources referenced by more than one window **MUST** live in the application-level resource dictionary. A window **MUST NOT** reference a resource defined only in another window's dictionary, because a window cannot see it and the lookup fails at runtime. |
| SR-49 | A binding to a read-only property **MUST** be declared `Mode=OneWay`. WPF rejects a `TwoWay` binding to a read-only property at runtime, and neither the compiler nor the build catches it. |
| SR-50 | The test suite **MUST** cover the service layer against **real** repositories created in temporary directories, not against canned strings. Parsers written against a format are only proven by that format. |

## 3.9 Testability

| ID | Requirement |
| --- | --- |
| SR-51 | Every Git-facing service method **MUST** be testable against a real temporary repository, and **MUST** accept a `CancellationToken`. |
| SR-52 | Tests that construct WPF objects **MUST** run on a single-threaded-apartment thread; WPF refuses to construct on a thread-pool thread. |
| SR-53 | Tests that need a window **MUST** share one host thread with one `Application` instance, because `Application.Current` is per-process and every `StaticResource` lookup depends on it. |
| SR-54 | A test that asserts on markup **MUST** load the real markup and read the resolved bindings, rather than parsing the XAML as text. Text assertions pass on markup that does not load. |
| SR-55 | A window that is created but never shown **MUST NOT** have `Measure`/`Arrange` called on it: a `Window` drives layout through an `HwndSource` that does not exist until it is shown, and the call blocks. |
| SR-56 | Each requirement in document 2 that can be automated **SHOULD** have at least one test that fails if the requirement is reverted. |

## 3.10 Current conformance

| Metric | Value |
| --- | --- |
| Compiler warnings | 0 |
| Automated tests | 369, all passing |
| Test project | xUnit 2.9.2, .NET Framework 4.7.2 |
| Application framework | .NET Framework 4.6.1 |
| Third-party runtime dependencies | none |

### Known gaps against this specification

| Gap | Impact |
| --- | --- |
| ~~No commit search or filter over the log (contrast with design mockup)~~ | Resolved. The log has a search box, reachable with `Ctrl+F`, matching message, hash, author and refs; see FR-100 – FR-103. |
| ~~A failed fetch and a repository with no remote looked identical~~ | Resolved. The branch picker says when the branches are as of the last fetch that worked and offers a retry; see FR-104, FR-105, SR-57. |
| ~~"Open" named two different folders~~ | Resolved. The caption names the folder it will open and changes after a run; see FR-107, SR-58. |
| Run log has no copy or clear control | Minor friction when reporting a failed run. |
| ~~Export progress is indeterminate~~ | Resolved. The bar is determinate for as much of a run as the work is countable; see FR-79. |
| Status text sits inside the output panel and wraps | Minor violation of SR-35; a wrapping status line resizes its panel. |
| No master tri-state "select all" checkbox | Duplicated by the separate all/none buttons; no partial state is shown. |