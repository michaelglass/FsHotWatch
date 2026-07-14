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
///
/// That direction binds the READ as well as the write (AUTOMATION-150). A ledger
/// that cannot be read is not an empty ledger: `load` answers `Unreadable`, never
/// `empty`, so an unreadable sidecar WIDENS the next run to the full suite instead
/// of silently absorbing the whole outstanding debt. See `LoadedQueue`.
module FsHotWatch.TestPrune.PendingVerification

open System
open System.IO
open System.Text.Json.Nodes
open FsHotWatch

/// The persisted queue: an unordered set of symbol full-names awaiting a green
/// test run. A `Set` so membership/union/removal are exact and order-free.
type Queue = Set<string>

let empty: Queue = Set.empty

/// The result of READING the sidecar — and the reason a failed read may not be
/// spelled `empty`.
///
/// AUTOMATION-150. `load` used to answer EVERY failure with `empty` (`with _ ->
/// empty`). A corrupt, truncated or unreadable file therefore absorbed the entire
/// outstanding test debt in silence, because the value it handed back was
/// indistinguishable from the one a genuinely-clean queue hands back. "I could not
/// read the ledger" collapsed into "nothing is owed" — UNDER-testing, the one
/// direction this module's header forbids. The code did not obey its own contract.
///
/// So the two facts are now different VALUES, and the compiler makes every caller
/// decide which one it is holding. (The same move as `ProcessOutput.DrainTimedOut`:
/// unrepresentable beats detected; detected beats silent.)
///
/// Note what is deliberately NOT an `Unreadable`: a MISSING file. "The file does
/// not exist" (first run, fresh clone, nothing ever queued) and "the file exists
/// and I could not read it" are DIFFERENT FACTS. The first is a provable, genuine
/// empty; only the second is an unknown. Collapsing them would wedge every fresh
/// clone into a permanent full-suite run — the fail-open traded for a stuck-closed.
[<RequireQualifiedAccess>]
type LoadedQueue =
    /// The ledger was read IN FULL: this is exactly what is owed. The only value
    /// that may be treated as an authoritative queue — including when it is empty,
    /// which then provably means "nothing is owed".
    | Loaded of Queue
    /// The file EXISTS and could not be turned into a queue: a torn/truncated write,
    /// corrupt JSON, an unreadable handle, a non-string entry. The debt it holds is
    /// UNKNOWN — which is emphatically not `empty`. A caller must treat this as
    /// "verify EVERYTHING": a full-suite run is the only scope that can discharge a
    /// debt whose membership cannot be named.
    | Unreadable of reason: string

/// Absolute path to the sidecar JSON for this repo root. Lives under the
/// per-plugin subdir of `.fshw/` so it's clearly fshw-owned (vs the
/// TestPrune.Core-owned `test-impact.db`).
let sidecarPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "pending-verification.json")

/// Read the queue off disk. NEVER throws — but a failure comes back as
/// `Unreadable`, not as an empty queue, so no caller can mistake "I could not read
/// what is owed" for "nothing is owed" (AUTOMATION-150).
///
/// A missing file is `Loaded empty`: nothing has ever been queued here, so nothing
/// is owed. That is the fresh-clone/first-run case and it must stay a fast no-op.
let load (repoRoot: string) : LoadedQueue =
    let path = sidecarPath repoRoot

    if not (File.Exists path) then
        LoadedQueue.Loaded empty
    else
        try
            let json = File.ReadAllText path

            // `save` writes a JSON array through an atomic tmp+rename — `[]` for the
            // empty queue — so it can never leave a zero-byte or whitespace-only file
            // behind. One that exists is a torn write, not an empty queue.
            if String.IsNullOrWhiteSpace json then
                LoadedQueue.Unreadable "the file is empty — `save` always writes at least `[]`, so this is a torn write"
            else
                match JsonNode.Parse(json) with
                | null -> LoadedQueue.Unreadable "the file holds a bare JSON `null`, not an array of symbol names"
                | root ->
                    // `AsArray` throws when the root is not an array (an object, a
                    // number, a bare string) — caught below, as an unreadable ledger.
                    let entries = root.AsArray() |> List.ofSeq

                    // An entry we cannot read is a SYMBOL WE CANNOT NAME. Skipping it
                    // (as the old `Seq.choose` did) drops outstanding debt on the floor
                    // one element at a time — the same bug, retail instead of wholesale.
                    // One bad entry makes the whole ledger unreadable.
                    let readEntry (node: JsonNode) : Result<string, string> =
                        if isNull node then
                            Error "a `null` entry where a symbol name was expected"
                        else
                            try
                                Ok(node.GetValue<string>())
                            with _ ->
                                Error $"a non-string entry (%s{node.ToJsonString()}) where a symbol name was expected"

                    let read = entries |> List.map readEntry

                    match
                        read
                        |> List.tryPick (function
                            | Error reason -> Some reason
                            | Ok _ -> None)
                    with
                    | Some reason -> LoadedQueue.Unreadable reason
                    | None ->
                        read
                        |> List.choose (function
                            | Ok name -> Some name
                            | Error _ -> None)
                        |> Set.ofList
                        |> LoadedQueue.Loaded
        with ex ->
            LoadedQueue.Unreadable $"%s{ex.GetType().Name}: %s{ex.Message}"

/// Persist the queue atomically (write to .tmp, rename over the real file).
/// Sorted so the on-disk form is stable/diffable and a queue-hash is
/// deterministic. Called at the flush chokepoint (once per analysis batch,
/// BEFORE the snapshot advance) and at every commit point — not per
/// FileChecked — so writes scale with batches, not files.
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
