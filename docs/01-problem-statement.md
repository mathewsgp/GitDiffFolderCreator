# 1. Problem Statement and Requirement Summary

**Project:** GitDiffFolderCreator
**Product type:** Single-user Windows desktop utility (WPF)
**Document status:** Living document — derived from the implementation, not a pre-implementation specification.

---

## 1.1 Problem statement

A developer who has two commits in a Git repository and wants to understand what changed
between them has no first-class way to get a usable, two-sided copy of those changes.

The available options all fail in the same way:

| Option | Why it does not solve the problem |
| --- | --- |
| `git diff` in a terminal | Output is transient, not a artefact you can keep, zip, or hand to a colleague. |
| Git hosting web UI | Only shows text. Cannot produce a folder tree you can open in an IDE, a diff tool, or a zip. |
| TortoiseGit / SourceTree | Feature-rich but heavyweight, and geared to browsing history rather than producing a *pair* of trees for a specific pair of commits. |
| Manual `git show <hash> > file` per path | Dozens of git invocations, no rename handling, silently wrong for added and deleted files. |

The concrete pain is this: **you cannot hand someone a folder that shows both sides of a
change.** A reviewer wants to open `base/` and `modified/` side by side in WinMerge, KDiff3
or Beyond Compare. Reviewers who are not using your IDE need a zip. A build or QA step needs a
stable, repeatable path. Today, producing that means hand-assembling two directory trees and
hoping you got the renames right.

## 1.2 Goal

Produce, from any two commits in a local repository, a single output folder containing two
mirrored directory trees — `base/` and `modified/` — holding the base and modified version of
every changed file, with rename detection, binary handling, and the changed paths preserved.

## 1.3 In scope

1. Read a local Git repository: its branches, its commit log, and the working-tree status.
2. Let the user pick two commits and resolve which is the base by ancestry rather than guesswork.
3. List the files that differ, with status and line counts, and let the user narrow the set.
4. Export the two versions of the selected files into a run folder.
5. Hand any single file's two versions to the user's own diff tool.
6. Show the whole of one commit — message, author, parents, refs, files — on demand.
7. Remember the handful of choices the user does not want to repeat.

## 1.4 Out of scope

These are deliberate exclusions, not omissions:

- **Not a repository browser.** It reads the repository it is pointed at; it does not clone, create, commit, merge, or push.
- **Not a diff viewer.** It hands two files to the user's diff tool. It does not render diffs itself.
- **Not a history explorer.** It shows a bounded number of commits to choose two from. There is no graph, no blame, no search over all history.
- **Not multi-user or multi-repository.** One window, one repository at a time.
- **No network access beyond `git fetch`.** It never contacts a hosting service directly.

## 1.5 Users

| Persona | Need |
| --- | --- |
| Reviewer / colleague | Receive a folder or zip they can inspect without your tooling. |
| Developer | Compare two commits in their preferred diff tool, fast, without leaving the app to run git. |
| Build / QA engineer | Produce a deterministic, repeatable artefact for a known pair of commits. |

All three are served by the same two-tree output. The developer persona is the one that
motivates the diff-tool integration and the commit log; the other two motivate the export
being the primary, reliable path.

## 1.6 Requirement summary

The complete functional requirement set is in
[02 — Functional Requirements](02-functional-requirements.md). In outline, the system must:

1. Load a repository's root, status, branches and a bounded commit log.
2. Resolve the base/modified direction of two selected commits by ancestry, with a stated fallback.
3. List the differing files with status, line counts and rename pairing.
4. Filter that list by path text and by status code.
5. Include or exclude individual files, individually or in bulk.
6. Export the selected files' base and modified versions into a new run folder, atomically.
7. Hand a single file's two versions to a configured diff tool, creating the missing side for added and deleted files.
8. Display the full details of one commit on demand.
9. Let the user choose the diff tool, from a catalog or an arbitrary command line.
10. Persist the user's repository, output folder, log size, and diff tool.

## 1.7 Quality goals in one line each

| Goal | Statement |
| --- | --- |
| Correctness | The two exported trees must match what Git considers the base and modified versions, including renames and binaries. |
| Honesty | Anything incomplete — an excluded file, a missing commit, no diff tool configured — is surfaced, never silent. |
| Safety | A failed or cancelled export leaves no partial run folder behind. |
| Speed | A large repository loads and an export completes without the window appearing frozen. |
| Familiarity | Windows conventions throughout; no new concepts for a Git user to learn. |

## 1.8 Document set

| # | Document |
| --- | --- |
| 1 | Problem statement and requirement summary — *this document* |
| 2 | [Functional requirement specification](02-functional-requirements.md) |
| 3 | [Software requirement specification](03-software-requirements.md) |
| 4 | [Architecture design](04-architecture-design.md) |
| 5 | [Detailed design](05-detailed-design.md) |
| 6 | [User manual](06-user-manual.md) |