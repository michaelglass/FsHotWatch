# ADR-068: a scan that rewrites a file fails the check

Status: Accepted (2026-11-14). AUTOMATION-1680.

## Context

`.fshw.json` `"format": true` registers the format-on-save preprocessor. A preprocessor runs
inside every change batch and inside every scan, and `check` and `confirm` force a scan
before they grade. On a tree holding a misformatted file, the scan rewrote the file in place.
The host recorded "rewrote 1 of N" as Completed. The verdict is captured at settle (ADR-013),
after the rewrite, so the check was green about the rewritten working copy while the commit
it had been asked about still held the old bytes.

GitHub CI runs `dotnet fantomas --check` over the commit, and main went red on it three times
(c12ac32f, d6457cae, 05d7e16c) over trees the local gates had passed. Reproduced at c12ac32f:
`dotnet fantomas src/ tests/ examples/ --check` exits 99 on one test file, while fshw `check`
rewrites that file and exits 0.

There was a second hole. The rewrite's own watcher echo is a change batch over the rewritten
bytes. It finds nothing to rewrite, and its "rewrote 0 of 1" replaced the scan's status, so
nothing the check printed or recorded said a file had been rewritten.

**Measured: how often checks rewrite files.** Read-only, from the daemon logs of 100 jj
workspaces of the largest consumer repository, 2026-10-07 to 2026-11-14:
- 887 scans ran, and another 334 scan requests were answered by a scan already in flight, so
  the number of checks was about 887 to 1,221.
- 11 scans rewrote files: about 1% of checks.
- By preprocessor: 4 were the formatter and 1 was most likely a source generator (a
  `preprocessors` entry). The other 6 could not be attributed, because the log line named no
  preprocessor.

So failing those checks costs a second run for about 1 check in 100.

## Decision

A scan runs the preprocessors as `PreprocessPass.Judge`. A change batch (a save) and
`fshw format` run them as `PreprocessPass.Apply`, unchanged.

- **A rewrite in a scan fails the preprocessor.** Its status is Failed and names each
  rewritten file, repository-relative: "these files were rewritten: …; include them in the
  change and run the check again". The status summary carries that text, so `check` prints it
  and verdict.json records it. The check is red.
- **The failure stands until a scan rewrites nothing.** Only a `Judge` pass writes or clears
  the record of what the scan rewrote. An `Apply` pass, the rewrite's own echo first of all,
  keeps the failure. The next check scans the rewritten bytes, finds nothing to rewrite, and
  earns its green over the tree on disk.
- **Every preprocessor, not only the formatter.** A configured generator that has to
  regenerate a committed file during a scan is the same defect: the check certifies bytes the
  commit does not hold.
- The rewrite itself is kept on disk. The check reports it and does not undo it.
- The scan logs a warning naming the preprocessor and the files, so the next measurement can
  attribute every rewrite.

Pinned by `ScanRewriteGateTests`, which drives the run-once check: the first check is red and
names the file, the second is green, and its verdict tree is the rewritten tree. The
rewrite's echo leaves the failure standing. `DaemonTests` pins the same sequence at the
daemon level.

The repository's own landing gate also runs CI's exact `dotnet fantomas … --check`
(`fantomas-check`, AUTOMATION-1680), so this repository is covered even under `"format":
"check"` or `false`.

## Rejected: judge the formatter only

This would keep generators passing while they rewrite committed files. That is the same
false green, and the host has no way to tell the formatter apart without a per-preprocessor
flag or an interface change.

## Rejected: report the rewrites but stay green ("loud only")

This would name the rewritten files on a passing status and in verdict.json. But a green that
says "the commit does not hold these bytes" still lets a landing proceed on it, and the
incident was a landing. At about 1% of checks, a red costs little and is acted on.

## Rejected: landing runners verify the working copy after the gate

The idea: remote-ci and the land runners would check that `jj diff` against the revision
under test is empty after the gate task, and fail otherwise. This catches any writer, not
only preprocessors. It is redundant once a scan that rewrites fails the check. It would also
protect only runs that go through those runners, while the false green reached every
consumer of `check`.
