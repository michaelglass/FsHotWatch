# ADR-015: A compile item is not an edit — the freshness source set excludes build output

Status: Accepted (2026-08-23)

Relates to: [ADR-008](adr-008-mtime-is-not-a-content-oracle.md) (mtime is never a
content oracle). ADR-008 says *do not use mtime where content is the question*. This
one is about the case where the clock genuinely **is** the question — "did an edit
land after the compile?" — and about which files may be asked it.

## Context

`BuildPlugin`'s artifact gate compares each project's canonical DLL against
`ProjectGraph.GetMaxSourceMtime`, and reddens (or bypasses the build cache) when the
DLL is older. It exists because MSBuild's incremental cache can silently skip
relinking an artifact a real edit should have rebuilt, and because a stale DLL fails
*silently*: it runs the previous code and passes the previous tests. Observed live on
2026-08-20 in a consuming repo — a source was edited, the DLL was not rebuilt, and two
subsequent targeted test runs reported green against pre-edit bytes. The
grep-the-report-for-your-test-names defence does not cover it: the tests were present
and passing. They ran against the wrong bytes.

The gate had never examined an artifact in production (AUTOMATION-368): the canonical
path needed a `<TargetFramework>` that only `RegisterFromFsproj`'s XML parse supplied,
and nothing in `src/` calls it. Recording MSBuild's own `TargetPath` at discovery made
it reachable, and it shipped **report-only** rather than reddening, because switching
on a build-reddening predicate that has never run against a real repository fails in
the worst direction: not "no protection", but every build in every consuming repo red
at once on a false reading.

That caution was correct, and the observation window is what proved it.

`GetMaxSourceMtime` folded over the project's registered source files — which, on the
live path, are **MSBuild's compile items**. That list is not a list of things a human
edited. Every SDK project compiles
`obj/<config>/<tfm>/<Project>.AssemblyInfo.fs`, and every design-time evaluation
regenerates it. Project discovery *is* a design-time evaluation. So every discovery
pass restamped every project's newest "source" to a moment strictly after the outputs
the last build had just produced, and the gate read a tree nobody had touched as
universally stale.

Measured from the report-only logs of ~40 workspaces of a consuming repo
(2026-08-18..23):

| | |
|---|---|
| `DllOlderThanSources` findings | **2090** |
| …within 90 s of an `MSBuild evaluation` pass in the same log | **1911 (91%)** |
| `DllMissing` findings | 800 |
| …before any completed build in that daemon session (cache-lookup path, correct) | 779 |

Promoting the gate to reddening against that reading would have failed essentially
every build in every workspace, on the first discovery after each one.

TestPrune's independent `ArtifactFreshness` never had this bug. It walks the disk
under `SafeWalk.SourceExcludedDirs`, whose doc comment already named the trap — "a
regenerated `obj/` file would otherwise read as a newer source and pin every test
project permanently stale". The graph-based gate simply never applied the same rule.
That difference *is* the BuildPlugin/TestPrune disagreement the promotion criteria
asked us to reconcile.

## Decision

1. **`GetMaxSourceMtime` excludes build output.** Compile items under the project's
   own `bin/`/`obj/` are outputs of the build, never inputs to it, and a freshness
   check that compares an output against an output is comparing the build to itself.

2. **The rule is one fact, shared.** `SafeWalk.BuildOutputDirs` is the set;
   `SafeWalk.SourceExcludedDirs` is defined in terms of it (for walks) and
   `SafeWalk.isBuildOutput` answers it for one path already in hand (for lookups). A
   walk and a lookup that disagree about what a source is, is how this degraded
   silently for two releases.

3. **The question is asked RELATIVE to the project directory.** Matching `bin`/`obj`
   as segments of the absolute path would classify every file of a repo checked out
   under such a directory as build output — nothing would ever be newer than the DLL
   and the gate would answer FRESH forever. Same silence, arrived at by being *more*
   thorough. This project's own `.workspaces/` convention (jj workspaces are full
   checkouts nested under an excluded name) is the live instance of the shape.

4. **Fixtures register the way the daemon registers.** `RegisterProject` +
   `RegisterProjectOutput`, from what MSBuild reported — and they carry the generated
   `obj/` compile item a real project has. A suite that only exercises a path
   production cannot reach will report health indefinitely; that, not the specific
   bug, is the finding worth keeping.

5. **The gate stays report-only.** The corrected reading has not run against a real
   repository either. Promoting it in the change that fixes it is precisely the
   mistake the flag was created to prevent.

## Rejected

- **A tolerance window on the mtime comparison** (e.g. ignore skews under ~30 s).
  Proposed because the observed skews were small. It would have suppressed the one
  true positive we have: the 2026-08-20 stale DLL was **20 s** behind its source. A
  tolerance tuned to hide the false positives is tuned to hide that. Fix the input
  set, not the threshold.

- **Returning `[]` from `verifyArtifactsFresh` unconditionally** while the promotion
  was pending. Tried first, and it failed eight tests — the ones asserting the gate
  *does* redden. It would have deleted the only coverage the gate's logic has, leaving
  a future promotion nothing to stand on: a silent, permanent loss of protection
  wearing the costume of caution. A flag keeps the logic exercised.

- **Excluding the whole of `SourceExcludedDirs`** (which adds `.git`, `node_modules`,
  `.workspaces`, …) rather than just build output. Over-broad for the question, and
  `.workspaces` in particular would misfire on the absolute path of any workspace
  checkout. The question here is exactly "is this a build output?".

- **Promoting `artifactGateReddens` in this change.** See decision 5.

## Consequences

- The gate can now be believed about a quiescent tree. What remains to be measured on
  the corrected detector, over one observation window, before promotion:
  - the `DllOlderThanSources` rate, and specifically whether it still correlates with
    discovery (it must not);
  - **post-build `DllMissing`** — the residual redden-risk class this change does not
    touch. It is correct at cache-replay (779 of 800 observed findings were there, and
    bypassing the cache to build is the right answer), but a project in the graph that
    the configured build command does not build would redden a build forever.
  - agreement with TestPrune's `ArtifactFreshness` on the same trees.

- **A sanctioned bounded rebuild primitive is still missing, and it gates promotion.**
  `fshw rerun <plugin>` refuses for the build plugin ("no registered file pattern" —
  it routes to `RerunFileCommandPlugin`), and `force-rebuild` is a daemon IPC command
  with no CLI verb, issued only internally by `confirm`. So when the gate fires there
  is no supported way to act on it, and an agent hitting this in August 2026 resorted
  to appending a throwaway marker line to bust the content hash. Refusing a run is
  only right if there is a supported way to fix it; otherwise every occurrence becomes
  a hand-rolled cache-busting edit.

## Amendment: enforcement promoted (AUTOMATION-358 Case 1, 2026-08-31)

The corrected detector is now enforced. Generated `bin/` and `obj/` compile items stay
outside `GetMaxSourceMtime`, while an authored source newer than its canonical DLL is
attributable stale output. BuildPlugin refuses a cache replay over that evidence and
demotes a nominally successful subprocess that leaves it unresolved. Missing canonical
outputs and byte-divergent dependency copies follow the same rule; metadata-only copy
drift is content-checked and remains benign.

This supersedes the temporary report-only decision and the promotion gate above. The
supported recovery is the build the cache bypass launches; if that build does not repair
the named artifact it stays red rather than licensing an old success. A build that does
repair it returns immediately to the normal cached path, which is covered by paired
positive controls.
