# ADR-061: a one-file edit pays for its own project's compile; the build is not narrowed

Status: Accepted (2026-10-30). AUTOMATION-943.

## Context

AUTOMATION-943 reported that a one-file edit took about 15 s to settle "because it
waits on a 19-project `dotnet build`". It asked two questions: does the FCS check
of the edited file wait for the build, and should a one-file edit build only the
projects whose inputs changed?

The original 15 s was a full re-typecheck: CommandTree stamped jj's `@` into the
CLI's informational version, so every build rewrote that DLL and moved every
snapshot. That was fixed in CommandTree (stamp `@-`) and the stable-stamp
alternative was rejected in ADR-037. This ADR records the measurements on the
current tree and the decision on build scope that the ticket left open.

## Measurements (FsHotWatch at main 732dc757, 2026-10-30, 12-core 32 GB laptop)

The box was shared with other agents' gates (1-minute load 20 to 55 during the
runs), so absolute times are inflated. The comparisons between runs taken
back to back are what matter.

**MSBuild, no daemon.** One comment line was appended to a file, then built. The
working copy was held dirty throughout, so CommandTree's `.dirty` marker did not
flip (see "Costs that remain").

| Edit | `dotnet build` (solution, 38 projects) | `dotnet build <test project>` | Fsc |
|---|---|---|---|
| none (no-op) | 2.1 s | 1.45 s | none |
| `tests/TestPrune.Trace.Tests/ReadmeSnippets.fs` (small leaf) | 3.66 s | 2.91 s | 1 call, 2.0 s |
| `tests/FsHotWatch.Tests/LoggingTests.fs` (large leaf) | 17.6 s | 16.0 s | 1 call, 15.5 s |
| `src/FsHotWatch/Logging.fs` (deep; the ref assembly is unchanged) | 8.4 s | 9.1 s | 1 call, 6.6 s |

Building only the edited project's closure saves 0.7 to 1.6 s. Fsc on the one
edited project is 55 to 90% of the build. MSBuild already stops at the edited
project when its reference assembly is unchanged: the deep edit ran one Fsc
call, not one per dependent.

**The daemon, one leaf edit** (`LoggingTests.fs`, steady state, load about 54):

| Step | Time |
|---|---|
| write to `[fcs] checked` (the FCS check itself took 116 ms) | 1.72 s |
| `[check] settled` | 1.78 s |
| build plugin `dotnet build` ("rebuilt 1 of 38 projects") | 24.2 s |
| test-prune selection | 1.1 s |
| `tests.beforeRun` `dotnet build`, the same command again | 4.4 s |
| test host (the edited file's classes) and coverage | 8.3 s |
| **edit to tests done** | **39.8 s** |

Over four edits the FCS check landed 1.5 to 1.7 s after the write and settled
1.35 to 1.78 s after the first change event. That was always before the build
finished, and was unaffected when an edit arrived mid-build. On intelligence,
2026-10-14, the same check landed 0.44 to 0.78 s after "Checking" (4 of 5
edits), 8 to 18 s before its build ("rebuilt 1 of 24 projects") finished.

## Decision

1. **The FCS check of an edited file never waits on a build.**
   `FileCheckedDeliveryTests` "an edit's FileChecked is published while a build
   is still running" holds a build in Running and asserts that the edit's
   `FileChecked` lands first. With a wait for the build inserted before the
   changed-file cohort, it fails.

2. **The build plugin keeps building with the configured whole command.** A
   narrower build saves under 2 s against a cost that is Fsc on the edited
   project. Choosing the projects to build would also be a new "which inputs
   matter" decision, outside the ActionKey that ADR-059 makes the only one. A
   consumer that wants roots-only builds already has `buildTemplate`.

3. **FsHotWatch's own `.fshw.json` drops `tests.beforeRun: dotnet build`.** It
   repeated `build.command` before every test run (3.4 to 6.9 s measured).
   `CiParityTests` pins that no `tests.beforeRun` step repeats `build.command`.

## Costs that remain (next steps, in measured order)

- **Fsc of the edited project** dominates: 15 to 24 s for `FsHotWatch.Tests`
  (270 files). Only incremental compilation, or a smaller project, reduces it.
  Restart-proof incremental checking is the open ADR-059 direction.
- **A symbol-free edit to a core file runs every class covering it.** A comment
  in `src/FsHotWatch/Logging.fs` selected 188 classes, and that run took 5.5
  minutes on the shared box (ADR-039). This is the largest cost for a deep edit,
  and it is a selection question, not a build one.
- **CommandTree's `.dirty` marker flips clean↔dirty** on the first edit after a
  commit, and again on commit. The informational version is in the CLI's
  reference assembly, so every consumer of `FsHotWatch.Cli` recompiles:
  `FsHotWatch.Tests` 23 s and `FsHotWatch.IntegrationTests` 11.6 s, measured on a
  small-leaf edit whose own compile was 2 s. A stamp that does not move the
  reference assembly, such as a resource or a generated non-public value, would
  remove it.
- **An edit during a test run defers its build until the test host exits**
  ("Deferring file change until the active test host completes"), because the
  build rewrites the host's binaries. Building into a separate output directory
  would lift this. That is a larger change.
- **About 1.4 s from the write to the batch** is spent in the watcher and debounce
  (500 ms source debounce plus FSEvents latency) before the check.
