# ADR-079: Reuse is judged against the nightly, on its own tree

The references key's output-level shadow compares what reuse would have handed each plugin with
what the nightly's fresh full-suite `confirm` produced for the same tree. It captures nothing at
land time. AUTOMATION-1816 step 2b.

Status: Accepted (2026-11-21)

## Context

Step 2 runs the references key in shadow on every land. It compares the compiler's verdict, and
for analyzers, lint and test-prune only the inputs those plugins key on (`fcsSignature`,
`usedSignatures`): a cold daemon cannot re-run a plugin on a cached check result. Before the key
may serve (step 3), reused analyzer and lint findings must equal the real ones, and no test that
a reused test-prune selection would have skipped may fail or change outcome.

## Decision

1. **The nightly is the oracle, on its own tree.** For each file its cold scan finds the
   references key would have served, look up each plugin's output under the reused key: the
   stored check's `fcsSignature` and `usedSignatures` from the step-2 record, with the file's
   bytes, options and toolchain. Compare that output with the nightly's real output for the file.
   Both sides describe one tree, so no comparison crosses trees.
2. **The records are box-wide.** The alternative-upstreams records move from each workspace's
   `.fshw/cache/alternative-keys/` into the outcome store as kind `alternative-upstreams`.
   - The nightly runs in another workspace than the lands that file them.
   - A record not rewritten within 14 days is dropped by the store's GC.
3. **Record and compare only.** Nothing here changes what a gate runs, selects or serves. The
   report (`.fshw/references-shadow/<runId>.json`, one line in `.fshw/references-shadow.jsonl`)
   carries the counts the go criterion is judged from; it judges nothing itself.
4. **The land host only.** The records are per host. On any other host the report says
   `hostMismatch`, with no per-file counts, rather than reading every file as unverifiable.

## Rejected: capture plugin output at land time

Each land would file what its plugins produced for every servable file, for the nightly to
compare. Rejected:

- Plugin output arrives after the settle that writes the record, so the record cannot hold it.
- The output that matters is what the plugin's own store would serve under the reused key. Each
  plugin's store already keeps that, and reading it at nightly time is the real path, not a copy
  of it.

## Consequences

- **Blind spot: intermediate lands.** Only the nightly's tree is judged. A reuse error on a land
  tree that never becomes a nightly tree, or that a later land overwrites, is never seen.
- When the reused key equals the fresh key, the analyzer and lint comparison checks only that the
  plugin's cache is sound for that key.
- An evicted entry or a changed plugin config gives no answer. The report counts these as
  unverifiable, per plugin.
- Test-prune sees only tests the nightly ran, and a stale dependency edge that matters only for a
  change set other than the nightly's is invisible.
