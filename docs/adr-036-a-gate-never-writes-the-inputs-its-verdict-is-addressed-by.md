# ADR-036: A gate never writes the inputs its verdict is addressed by

Status: Accepted (2026-09-21). Builds on the tree-hash rule in `FsHotWatch.TreeHash` and
the declaration mechanism in `FsHotWatch.VerdictInputs`.

## Context

A verdict is a claim about a PARTICULAR tree, so it carries that tree's content address,
and `.fshw.json` `verdictInputs.hashed` names the gate-deciding files the discovery walk
cannot reach. `coverage-ratchet-FsHotWatch.json` is one of them, declared with this `why`:

> The per-file coverage floors. Lower one and a verdict earned under the higher floor must
> stop applying; hashing it is the only thing that makes that true.

`mise run check` — this repository's inner-loop gate — depended on `coverage-ratchet`,
whose whole job is to REWRITE that file. The writer sat inside the reader's own dependency
closure, so the gate edited the bytes its answer is keyed to, in the run that produced the
answer.

Two distinct defects follow, and only the first is about hashes.

**1. The verdict cannot be read back — on every run, with no human edit at all.** Measured
2026-09-21: `mise run check` exited 0, and `fshw verdict` over the tree the run itself left
behind exited 4, "Never reuse it". The chain is the mutation's, end to end. The ratchet
wrote the tracked floors file at 22:08:27, which moved the jj source ref; the daemon's own
`tests.beforeRun: dotnet build` then rebuilt `FsHotWatch.Cli.dll` at 22:08:46, nineteen
seconds later, stamping it with the NEW ref; and the process that published the verdict had
started at 22:08:28 from the binary carrying the OLD one:

```
✗ stale: the verdict was produced by a DIFFERENT fshw binary
  (verdict 0.14.0-alpha.57+5cad1dcf… 74deeaa7ef41e68a,
   current 0.14.0-alpha.57+d13b4b3a….dirty 74deeaa7ef41e68a)
```

The two binary CONTENT hashes are identical — same bytes, same behaviour. Only the stamp
moved, because the gate dirtied the working copy underneath its own build. So the link that
broke first was the PRODUCER link, not the tree link; the tree link is broken too, and is
simply never reached, because `applicability` checks the producer first. Declining the
unrequested edit — the ordinary response to a diff you did not ask for — leaves the verdict
addressed to a tree that now exists nowhere.

**2. The gate repaired its own criterion.** The wrapper ran `coverageratchet ratchet`, read
exit 2 — "a file is BELOW its floor" — and answered it by running `coverageratchet loosen`,
which sets the threshold down to current coverage. A check whose response to its own finding
is to delete the finding cannot fail; its success is unconditional.

This is not a hypothetical. The first `mise run check` of a fresh workspace on 2026-09-21,
over an UNMODIFIED tree, printed:

```
[coverage-ratchet] Coverage below threshold for: CheckPipeline.fs, OperationWatchdog.fs
[coverage-ratchet] Loosen complete: thresholds set to current coverage
```

and rewrote the floors file — lowering `OperationWatchdog.fs` branch 100 → 91 against a
`reason` field in that very entry reading "Do not re-tighten from a single lucky run", and
`CheckPipeline.fs` against one reading "do NOT auto-ratchet to 80 (reintroduces the flake
fixed in xwtkmmqv)". It also RAISED `DepsFreshness.fs` from that single loaded-box
measurement. The file records the cost of that habit: seven floor entries whose entire
justification is "loosened automatically", and a 2026-08-07 repair note carried by 32 of
its 131 entries after "an auto-ratchet pass had pinned this to a single run's measurement
(under 1pp headroom, and two such floors went red on the next run)".

The plugin already had the right shape. `CoveragePlugin` serves `coverage-ratchet` as an
explicit IPC command that takes the check's own exclusive slot, "because a check READS the
config the ratchet REWRITES". It was the task graph that put the writer in the reader's path.

## Decision

- **A gate observes; it does not repair.** `check` depends on `coverage-check`
  (`coverageratchet check`), which reads the floors, fails on a regression, and writes
  nothing.
- **The local gate consults coverage exactly as CI does.** `ci` already depended on
  `coverage-check`. One task now serves both, so a local green and a CI green are the same
  claim rather than two that can drift — the drift being the bug, as `[tasks.ci]` already
  says of its own command.
- **Repair is explicit and reviewable.** `mise run coverage-ratchet` tightens and ONLY
  tightens; `mise run loosen-from-ci` re-baselines from both platforms' CI artifacts;
  `mise run coverage-loosen` lowers. Each writes a hashed verdict input, so each
  invalidates the verdict and forces a re-gate under the floors it just moved. That is the
  declaration working as designed, not a cost.
- **The separation is pinned by tests.** `tests/FsHotWatch.Tests/GatePurityTests.fs` reads
  `mise.toml` and `.fshw.json` and refuses a `coverageratchet` verb that writes anywhere in
  `check`'s or `ci`'s dependency closure, refuses a graph in which no gate consults coverage
  at all, and refuses a `coverage-ratchet` task that can loosen.

## Rejected

Both rejections turn on the same non-obvious fact, so it is stated once, here, plainly:

> **The verdict was not invalidated because the hash covered the floors file. It was
> invalidated because the thing that PRODUCED it got re-stamped underneath it.** The
> ratchet's write moved the jj source ref; the daemon's own `beforeRun: dotnet build`
> then rebuilt `FsHotWatch.Cli.dll` with the new ref while the publishing process still
> carried the old one. The two binary content hashes were IDENTICAL — no code changed,
> nothing was recompiled differently. `applicability` checks the producer before it
> compares trees, so the producer link is the one that breaks, and it breaks for a write
> to ANY tracked file, whether or not `verdictInputs` names it.

Everything below follows from that.

- **Declare the floors file a `notInput`.** IMPOSSIBLE, not merely inferior, and then
  insufficient even if it were possible. Impossible: `notInputs` is validated and never
  filters (`DaemonConfig.validateVerdictInputs` — "A declaration that could REMOVE files
  from the hash would be a supported way to weaken the gate silently"), and declaring a path
  in both `hashed` and `notInputs` is a hard `ConfigError`, so the option is really "delete
  the `hashed` entry". Insufficient: un-hashing the file would not recover the verdict
  anyway, because the link that actually broke is the producer link and it does not consult
  `verdictInputs` at all — the gate would go on writing a tracked file, the ref would go on
  moving, and `fshw verdict` would go on answering 4. What deleting the declaration WOULD
  achieve is making the gate blind to exactly the weakening the auto-loosen performs, while
  leaving defect 2 — the half with the measured damage — completely untouched. It buys
  nothing and pays for it.
- **Move the ratchet to a post-gate step.** This is the one the next person will re-propose,
  because "write after the hash is taken" sounds like it removes the race. It does not. The
  re-stamp is not something the hash protects against: a write after the tree is hashed still
  moves the source ref, and the next build — the gate's own, on the next invocation — still
  re-stamps the binary. So every green is followed by an edit that strands it, and the
  verdict is stale the instant the command returns rather than only sometimes. It converts
  "sometimes wrong" into "always wrong", and again leaves the auto-loosen intact. A post-gate
  step that a PERSON runs is not this option — it is the decision above, and it is fine
  precisely because a person's edit invalidating a verdict is the system working.

## Consequences

- Tightening stops happening on its own. That is the point: the floors file's own reasons
  prescribe "re-baseline from CI on BOTH platforms, not one local run", which an inner-loop
  auto-tighten on one developer's machine structurally cannot honour. Forgetting to tighten
  leaves floors below achieved coverage and lets no regression through; the old behaviour
  had the opposite failure mode.
- A coverage regression now reds the local gate instead of being absorbed. Where that
  regression is load-dependent jitter, the honest fix is the one the file already documents
  — move the flaky test to `FsHotWatch.IntegrationTests` (no coverage), or re-baseline from
  CI — not a silent rewrite in the dev loop.
- `coverage-check` is a mise `depends`, so a coverage failure stops `check` before the
  daemon runs. `ci` has had that shape all along.

## Not in this change

- `mise.toml` is not itself a hashed verdict input. It decides what the SURROUNDING task
  graph runs, while the verdict is a claim about what `fshw check` concluded, and that is
  governed by `.fshw.json`, which is hashed. The invariant above is instead pinned by a test
  that lives under `tests/` and therefore is.
- The coverage finding this change UNCOVERS is not dispositioned here. With the auto-loosen
  removed, `mise run check` on a box at load ~55 exits 1 at `coverage-check` with three
  count-floor line-items: `Daemon.fs` covered lines 1546 < 1547 (-1), and `CheckPipeline.fs`
  covered lines 192 < 194 (-2) and covered branches 33 < 34 (-1). Every one is inside the
  wobble those entries already document ("the daemon timing paths still wobble"; floors are
  "the observed minimum" of several runs). It was never absent — it was being absorbed, on
  every run, by the write this change removes. Settling it means re-baselining from CI or
  from quiet runs (`mise run loosen-from-ci`) with a reason recorded, in its own commit,
  which is exactly the workflow above and deliberately not something a gate does to you.
