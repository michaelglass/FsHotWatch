# ADR-041: `fshw findings` reads the daemon; it never drives it

Status: Accepted (2026-10-25)

## Context

A consumer's whole-solution lint re-analyzed 2,635 files in 707 s while a warm daemon already
held the same findings: same analyzers, same threshold, same projects. A daemon answers in
about 1 s. But the daemon's surface could not be trusted for that answer. `status analyzers`
answers from what the plugin has folded so far. After an edit to a widely referenced file it
said `analyzers: ok` (exit 0) for 296 s while the file on disk had a finding. The daemon's
ledger entries had no analyzer code, and no verb printed them in a machine-readable form.

## Decision

- `fshw findings` is a reader. It never starts a daemon (exit 5) and never asks one for a
  scan, a build or an analyzer run.
- It waits with a new RPC, `WaitForRest`: the host owns no work. That is the settle half of
  `check`'s wait.
- The analyzers plugin stamps every file's analysis with the inputs it was computed from: the
  source hash, the analyzer set's semantic identity, the config-chain digest, and whether the
  check was typed. A replayed analysis is stamped with what its cache key named. The plugin
  serves the stamps and entries through an `Observe` command, `analyzer-findings`. Observing
  reads the analyzer set on disk; it does not reload it.
- The CLI judges the reply against the disk. A stamp that disagrees is stale (exit 4). An
  expected file of the current model with no complete analysis is incomplete (exit 2). The
  tree hash is taken before the read and after the judgement, and a move between them is
  stale. Only a current answer carries findings. `--wait` retries a stale answer until its
  bound.
- `ErrorEntry` gains `Code`, and the task-cache entry format moves to 6, so a replay keeps
  the code.
- The answer is the versioned `fshw-findings-v1` document.

## Rejected

- **Reading `status`.** It is the stale green above.
- **`WaitForComplete`, the wait `check` grades after.** It also waits for test evidence about
  the current model. Under `tests.runOn: "request"` nothing earns that evidence unasked, so a
  reader would wait out the 60-minute deadline. It also re-checks suspect files once per tree
  (a scan) and prunes the ledger. A reader must not do either.
- **Forcing a scan first, as `check` does.** A scan re-emits every file. That rebuilds and
  re-runs tests, which are the side effects the consumer is trying to avoid. The stamps detect
  an edit the watcher has not delivered without making the daemon re-read anything.
- **Binding on model generation per file.** A model change re-checks only the projects it
  touched. The rest keep the analysis of their unchanged bytes, which the source stamp still
  ties to the disk. Requiring the current generation would refuse every answer after any
  `.fsproj` edit until a full scan.
