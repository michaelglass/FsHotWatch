/// Per-file FCS-cleanliness sidecar (Path D in the 0.10 fix-forward design).
///
/// FsHotWatch owns this state — it tracks "the last time fshw checked file F,
/// did FCS report any (un-suppressed) Error-severity diagnostics?" Standalone
/// TestPrune.Core has no notion of cold-vs-warm scans and shouldn't carry
/// this concept in its persisted symbol schema. Keeping the freshness flag
/// out-of-band leaves TestPrune.Core untouched.
///
/// On-disk shape: a single JSON file at `.fshw/test-prune/file-freshness.json`,
/// dictionary keyed by repo-relative path. Atomic write (tmp + rename) so a
/// crash mid-flush leaves the prior file intact.
///
/// Read-on-plugin-start, write incrementally as FileChecked events fire.
/// The map fits comfortably in memory at fshw's scale (hundreds-to-low-thousands
/// of files; rows are ~80 bytes each).
///
/// Consumed by `TestPrunePlugin`'s detectChanges call site via the three-way
/// `classify` (Clean / Dirty / Unknown). Explicitly-`Dirty` rows are bypassed
/// (untrusted for diff). An `Unknown` (absent) record over a NON-EMPTY stored
/// row set is a seeded `test-impact.db` (ADR-010) whose sidecar didn't travel
/// with it — those rows ARE diffed, so a seeded workspace selects the tests a
/// real edit affects rather than silently skipping them.
module FsHotWatch.TestPrune.FileFreshness

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch

/// Per-file freshness record. `LastCleanCheckAt` is preserved across dirty
/// transitions so a future "stale-clean" eviction policy has the timestamp
/// to act on without needing to track it separately.
type FileState =
    { FcsClean: bool
      LastCleanCheckAt: DateTime option }

type Store = Map<string, FileState>

// Manual JSON via JsonNode — chosen over System.Text.Json's reflection-based
// (de)serialization because the latter requires a public DTO class and
// behaves surprisingly with F# records / `internal` types. Hand-rolled is
// simpler and the schema is two fields wide.

let private serializeState (s: FileState) : JsonObject =
    let o = JsonObject()
    o.["fcsClean"] <- JsonValue.Create(s.FcsClean)

    match s.LastCleanCheckAt with
    | Some d ->
        let iso = d.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
        o.["lastCleanCheckAt"] <- JsonValue.Create(iso)
    | None -> o.["lastCleanCheckAt"] <- null

    o

let private deserializeState (node: JsonNode) : FileState =
    let obj = node.AsObject()

    let getNode (key: string) : JsonNode option =
        if obj.ContainsKey key then
            let n = obj.[key]
            if n = null then None else Some n
        else
            None

    let clean =
        match getNode "fcsClean" with
        | Some n -> n.GetValue<bool>()
        | None -> false

    let lastClean =
        match getNode "lastCleanCheckAt" with
        | Some n ->
            let s: string = n.GetValue<string>()
            let mutable parsed = DateTime.MinValue

            if DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, &parsed) then
                Some(parsed.ToUniversalTime())
            else
                None
        | None -> None

    { FcsClean = clean
      LastCleanCheckAt = lastClean }

/// Absolute path to the sidecar JSON for this repo root. Lives under the
/// per-plugin subdir of `.fshw/` so it's clearly fshw-owned (vs the
/// TestPrune.Core-owned `test-impact.db`).
let sidecarPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "file-freshness.json")

/// Load the sidecar. Returns an empty map if the file is missing or
/// unreadable/unparseable — the freshness data is derivative and a fresh
/// daemon can rebuild it on the next clean check. Crashing on a corrupt
/// sidecar would be a worse trade than over-marking files dirty for one
/// cycle.
let load (repoRoot: string) : Store =
    let path = sidecarPath repoRoot

    if not (File.Exists path) then
        Map.empty
    else
        try
            let json = File.ReadAllText path

            if String.IsNullOrWhiteSpace json then
                Map.empty
            else
                let root = JsonNode.Parse(json)

                if root = null then
                    Map.empty
                else
                    let obj = root.AsObject()

                    obj
                    |> Seq.choose (fun kv ->
                        if kv.Value = null then
                            None
                        else
                            try
                                Some(kv.Key, deserializeState kv.Value)
                            with _ ->
                                None)
                    |> Map.ofSeq
        with _ ->
            Map.empty

/// Persist the store atomically (write to .tmp, rename over the real file).
/// Cheap enough to call after each FileChecked update at fshw's scale.
let save (repoRoot: string) (store: Store) : unit =
    let path = sidecarPath repoRoot
    let root = JsonObject()

    for KeyValue(k, v) in store do
        root.[k] <- serializeState v

    let json = root.ToJsonString(JsonSerializerOptions(WriteIndented = false))
    FsHwPaths.atomicWriteAllText path json

/// Stamp `relPath` as having ended its last FCS check clean at `now`.
let markClean (now: DateTime) (relPath: string) (store: Store) : Store =
    Map.add
        relPath
        { FcsClean = true
          LastCleanCheckAt = Some now }
        store

/// Stamp `relPath` as having ended its last FCS check dirty. Preserves the
/// prior `LastCleanCheckAt` if any so the timestamp survives clean→dirty
/// transitions. Use this when an explicit demote-to-dirty is intended (e.g.
/// from unit tests). The plugin uses `markUnverified` instead — see below.
let markDirty (relPath: string) (store: Store) : Store =
    let prior =
        match Map.tryFind relPath store with
        | Some s -> s.LastCleanCheckAt
        | None -> None

    Map.add
        relPath
        { FcsClean = false
          LastCleanCheckAt = prior }
        store

/// Item 3 (BuildCompleted-gated stamping) variant of `markDirty`: refuses to
/// downgrade a previously-clean entry. Used when the plugin can't promote a
/// file to clean (no BuildCompleted has fired yet, or the FCS check itself
/// reported errors) but doesn't want to erase a clean record from earlier
/// in this session or from a prior session loaded off disk.
///
/// Trade-off: the plugin may carry a stale "clean" record across a
/// user-broke-their-code edit until the next genuine clean check refreshes
/// it. Per the user's priority "cold-start reliability over 100%
/// correctness," this is the chosen direction. The next `markClean` call
/// overwrites with a fresh timestamp.
let markUnverified (relPath: string) (store: Store) : Store =
    match Map.tryFind relPath store with
    | Some s when s.FcsClean ->
        // Prior clean entry — preserve it as-is.
        store
    | Some s ->
        // Prior dirty entry — stay dirty, preserve LastCleanCheckAt.
        Map.add
            relPath
            { FcsClean = false
              LastCleanCheckAt = s.LastCleanCheckAt }
            store
    | None ->
        // Absent — insert a dirty placeholder.
        Map.add
            relPath
            { FcsClean = false
              LastCleanCheckAt = None }
            store

/// Three-way trust classification for a file's stored symbol rows, consumed by
/// the `detectChanges` call site to decide whether those rows can be diffed
/// against. The distinction between `Unknown` and `Dirty` is load-bearing (it
/// guards the seeded-DB under-selection class):
///
///   - `Clean`   — an explicit "ended its last check FCS-clean" record. Safe to
///                 diff: the stored rows are a complete, trustworthy extraction.
///   - `Dirty`   — an explicit `fcsClean = false` record. The stored rows were
///                 written while FCS reported errors and may be PARTIAL; diffing
///                 against them yields a phantom "all symbols changed" delta (the
///                 4921-affected-tests Phase B regression). Untrusted for diff.
///   - `Unknown` — NO record at all. We have no information. The dominant cause
///                 is a seeded `test-impact.db` (ADR-010) whose fshw-owned
///                 freshness sidecar did not travel with it into a fresh
///                 workspace: the stored rows are a real prior DB, safe to diff.
///                 Treating `Unknown` like `Dirty` (bypass → no-diff) silently
///                 UNDER-selects, violating ADR-010's "a seeded DB over-indexes
///                 but never serves a stale verdict" guarantee. The call site
///                 therefore trusts `Unknown` for diff *when stored rows exist*
///                 (a genuinely empty DB / cold scan has no rows to diff and is
///                 handled separately).
type Freshness =
    | Clean
    | Dirty
    | Unknown

/// Classify `relPath`'s stored-row trust level. See `Freshness`.
let classify (relPath: string) (store: Store) : Freshness =
    match Map.tryFind relPath store with
    | Some s when s.FcsClean -> Clean
    | Some _ -> Dirty
    | None -> Unknown

/// True iff the sidecar has an explicit "ended clean" record for `relPath`.
/// Absent entries return false — unknown == not-explicitly-clean, the
/// conservative default for the `markClean` gate. NOTE: this predicate must NOT
/// be used to gate `detectChanges` — that call site needs the three-way
/// `classify` so it can tell an absent (seeded-DB) record from an explicit dirty
/// one. See `Freshness`.
let isClean (relPath: string) (store: Store) : bool =
    match classify relPath store with
    | Clean -> true
    | Dirty
    | Unknown -> false
