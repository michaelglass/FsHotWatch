/// Durable "needs-testing" queue (plugin-owned sidecar).
///
/// FsHotWatch.TestPrune owns this state. It records the set of changed symbol
/// full-names that have NOT yet been proven test-equivalent to the last green
/// run — i.e. symbols a test run that covered them, and PASSED, has not yet
/// removed. The in-memory `TestPruneState.ChangedSymbols` is the hot view; this
/// sidecar is its durable copy so a daemon restart with a non-empty queue
/// re-flags those symbols instead of silently absorbing them.
///
/// Why a sidecar and not a table in TestPrune.Core's `test-impact.db`:
///  - TestPrune.Core lives in a different repo/package; adding a table is out
///    of scope.
///  - The DB's SchemaVersion-mismatch logic deletes+recreates the DB on drift,
///    which would silently wipe the queue and reintroduce the under-testing
///    bug this queue exists to close.
/// Keeping the queue out-of-band (under `.fshw/test-prune/`, next to
/// `file-freshness.json`) leaves TestPrune.Core untouched.
///
/// On-disk shape: a single JSON file at
/// `.fshw/test-prune/pending-verification.json`, a JSON array of symbol
/// full-name strings. Atomic write (tmp + rename) so a crash mid-flush leaves
/// the prior file intact.
///
/// Durability direction: the queue may only err toward OVER-testing, never
/// under-testing. A symbol leaves the queue ONLY when a test run that covered
/// it completed green (or it provably has no covering test). A crash between a
/// queue addition and the analysis flush must leave the symbol QUEUED.
module FsHotWatch.TestPrune.PendingVerification

open System
open System.IO
open System.Text.Json.Nodes
open FsHotWatch

/// The persisted queue: an unordered set of symbol full-names awaiting a green
/// test run. A `Set` so membership/union/removal are exact and order-free.
type Queue = Set<string>

let empty: Queue = Set.empty

/// Absolute path to the sidecar JSON for this repo root. Lives under the
/// per-plugin subdir of `.fshw/` so it's clearly fshw-owned (vs the
/// TestPrune.Core-owned `test-impact.db`).
let sidecarPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "pending-verification.json")

/// Load the queue. Returns an empty set if the file is missing or
/// unreadable/unparseable. Unlike the freshness sidecar, an unreadable queue is
/// a genuine loss of safety information — but crashing the daemon on a corrupt
/// file is the worse trade, and the queue self-heals: the next changed symbol
/// re-enters it, and any symbol whose tests haven't run green is re-flagged the
/// next time it's edited. Treating "can't read" as "empty" errs toward
/// re-testing on the next edit rather than wedging the daemon.
let load (repoRoot: string) : Queue =
    let path = sidecarPath repoRoot

    if not (File.Exists path) then
        empty
    else
        try
            let json = File.ReadAllText path

            if String.IsNullOrWhiteSpace json then
                empty
            else
                match JsonNode.Parse(json) with
                | null -> empty
                | root ->
                    root.AsArray()
                    |> Seq.choose (fun n ->
                        if n = null then
                            None
                        else
                            try
                                Some(n.GetValue<string>())
                            with _ ->
                                None)
                    |> Set.ofSeq
        with _ ->
            empty

/// Persist the queue atomically (write to .tmp, rename over the real file).
/// Sorted so the on-disk form is stable/diffable and a queue-hash is
/// deterministic. Cheap enough to call on each FileChecked update and at every
/// commit point at fshw's scale (hundreds-to-low-thousands of symbols).
let save (repoRoot: string) (queue: Queue) : unit =
    let path = sidecarPath repoRoot
    let arr = JsonArray()

    for s in queue |> Set.toList |> List.sort do
        arr.Add(JsonValue.Create(s))

    FsHwPaths.atomicWriteAllText path (arr.ToJsonString())

/// Stable content hash of the queue (order-independent). Used to fold
/// queue-emptiness/identity into the §2a test cache key so a cached green
/// `TestRunCompleted` can never be replayed for a state whose pending queue
/// differs from the run that produced it.
let hash (queue: Queue) : string =
    queue
    |> Set.toList
    |> List.sort
    |> String.concat "|"
    |> FsHotWatch.CheckCache.sha256Hex
