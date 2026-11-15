# ADR-076: Build output is a replay precondition, not a key

An options record names its projects' build output by path and requires it to exist. It does not
key it. AUTOMATION-1726.

Status: Accepted (2026-11-15)

## Context

The options key (ADR-059 §18) folded in every input an evaluation read, probed or listed. A build
writes into each project's `bin/` and `obj/<Configuration>/` while the evaluation reads them, so
the filer saw those inputs move and refused to file (AUTOMATION-1592). pin150-gate, e5-150 and
canary-fshotwatch-cli on alpha.150 each filed no record that way, and their next start evaluated
again.

Classified on intelligence, a record's inputs under `bin/` or `obj/` are:

- 75 reads of restore's outputs (assets, nuget.g props/targets);
- 64 reads of generated compile items;
- 50 probes of restore state;
- 100 probes of build-output directories (`obj/Debug/`, `bin/`, `bin/Debug/`, `…/publish/`);
- 25 listings of each `obj/` root.

The options name a generated compile item only by path; its bytes never change the options. The
build-output directories' existence never changes them either. On intelligence, built against
clean, every project kind (library, test, web SDK, Fable) gives 0 differing option lines. The
design-time build creates those directories before any target-time `Exists` reads them, and every
`Exists($(OutDir|OutputPath|IntermediateOutputPath)…)` in SDK 10.0.401 is inside a target. A
planted evaluation-time condition on the output path is the one shape that would differ, and the
measurement detects it.

## Decision

1. `FiledInputs.ofEvaluation` parses an evaluation's inputs once, against each loaded project's
   `bin/` and `obj/<Configuration>/`:
   - **Generated**: the projects' compile items under that build output. A replay requires each
     to exist; a missing one is a miss, and the evaluation regenerates it. Their bytes are not
     keyed.
   - **Dropped**: probes of that build output.
   - **Keyed**: everything else, including restore's outputs in `obj/` itself and every listing.
2. The filer watches only what is keyed, and refuses to file options naming a generated item that
   is not there.
3. The index record carries the generated items (`fshw-options-inputs-v2`), and the key schema
   moves to `fshw-options-semantics-v2`. This is a key change, shipped in one release with
   AUTOMATION-1725's.

## Rejected

- **Order the daemon's start** (evaluate, then build). It was proposed at +16 s per cold start.
  The stray start build had a precise cause, a re-run broadcast (ADR-074's regression note), and
  that fix costs nothing. Ordering would not cover a build that runs beside a later evaluation.
- **Re-evaluate once the racing build settles.** That pays a second evaluation on the check path,
  and it would still key build output that never changes the options.

## Deferred

- Listings under build output stay keyed. None were measured; dropping them would need the same
  built-against-clean evidence.
- Relocating SDK and NuGet paths, so a record moves between machines whose SDKs sit in different
  places, belongs to AUTOMATION-1684's flip release, not here.
