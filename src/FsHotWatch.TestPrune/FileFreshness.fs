/// Per-file FCS-cleanliness sidecar.
///
/// FsHotWatch owns this state — it tracks "the last time fshw checked file F,
/// did FCS report any (un-suppressed) Error-severity diagnostics?" Standalone
/// TestPrune.Core has no notion of cold-vs-warm scans and shouldn't carry this
/// concept in its persisted symbol schema, so keeping the flag out-of-band leaves
/// TestPrune.Core untouched.
///
/// On-disk shape: a single JSON file at `.fshw/test-prune/file-freshness.json`,
/// dictionary keyed by repo-relative path. Atomic write (tmp + rename) so a
/// crash mid-flush leaves the prior file intact. Read on plugin start, written
/// incrementally as FileChecked events fire; the map fits comfortably in memory at
/// fshw's scale (hundreds-to-low-thousands of files, rows ~80 bytes each).
///
/// Consumed by `TestPrunePlugin`'s detectChanges call site via the three-way
/// `classify` — see `Freshness`.
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

/// Variant of `markDirty` that refuses to downgrade a previously-clean entry.
/// Used when the plugin can't promote a file to clean (no BuildCompleted has fired
/// yet, or the FCS check itself reported errors) but doesn't want to erase a clean
/// record from earlier in this session or from a prior session loaded off disk.
///
/// Trade-off, chosen deliberately for cold-start reliability over 100%
/// correctness: a stale "clean" record may be carried across a
/// user-broke-their-code edit until the next genuine clean check refreshes it.
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
/// against. `Unknown` and `Dirty` must stay distinct — collapsing them is the
/// seeded-DB under-selection bug:
///
///   - `Clean`   — an explicit "ended its last check FCS-clean" record. Safe to
///                 diff: the stored rows are a complete extraction.
///   - `Dirty`   — an explicit `fcsClean = false` record. The stored rows were
///                 written while FCS reported errors and may be PARTIAL; diffing
///                 against them yields a phantom "all symbols changed" delta
///                 (4921 affected tests, observed). Untrusted for diff.
///   - `Unknown` — NO record at all. Dominant cause is a seeded `test-impact.db`
///                 (ADR-010) whose fshw-owned sidecar did not travel with it into
///                 a fresh workspace: the stored rows are a real prior DB, safe to
///                 diff. Treating it like `Dirty` (bypass → no-diff) silently
///                 UNDER-selects, violating ADR-010's "a seeded DB over-indexes but
///                 never serves a stale verdict". The call site therefore trusts
///                 `Unknown` for diff *when stored rows exist* (a genuinely empty
///                 DB / cold scan has no rows to diff and is handled separately).
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

/// What may be done with the symbol rows `test-impact.db` currently holds for one
/// file. The `detectChanges` call site in `TestPrunePlugin` matches on this instead
/// of on a bare bool, so every arm has to be named — and so the arm that is only
/// reachable after the database threw its rows away cannot be reached by accident.
type StoredRowTrust =
    /// The stored rows are a usable baseline: diff the current extraction against them.
    | DiffAgainstStored
    /// The index holds NO rows for this file, but the sidecar vouches for the check
    /// that produced them. Diffing against nothing is the widest possible answer —
    /// every symbol currently in the file reads as added — and that is the intended
    /// outcome, not a side effect. See `trustStoredRows`.
    | EverySymbolIsNew
    /// The stored rows may not be diffed against, and there is nothing to widen to
    /// either: contribute no changed symbols for this file.
    | NoDiff

/// Decide what the stored rows are good for, from the sidecar's verdict plus the one
/// structural fact the sidecar cannot know — whether the index actually holds rows
/// for this file right now.
///
/// AUTOMATION-277. `file-freshness.json` carries no schema version and lives beside a
/// `test-impact.db` that DELETES AND RECREATES itself on a `SchemaVersion` bump. The
/// sidecar survives that, so a `Clean` stamp can outlive the rows it was a statement
/// about. AUTOMATION-275 is the same shape one file over, and there it discharged real
/// test debt as a green that ran zero tests.
///
/// The fix follows AUTOMATION-275's landed shape rather than its first draft: ask the
/// index what it HOLDS, never how it came to be that way. `Database.WasRecreated` is
/// deliberately NOT an input here —
///   * it is also true for a first-ever creation (`TestPrune.Database`:
///     "the file did not exist, or an incompatible schema forced a delete+recreate"),
///     so it cannot distinguish the two, and
///   * it is a fact about this session's open, while the question is about one file.
///     A seeded database missing rows for one file needs the same answer and has no
///     recreate to point at.
/// The row count answers the real question in every one of those cases at once.
///
/// The polarity is the load-bearing part, and it is not merely the safe direction — it
/// is the CORRECT one. `Clean` over an empty row set is the honest reading "this index
/// has never seen this file", and every symbol in it therefore is new. That also covers
/// the case that has nothing to do with a recreate: a clean-checked file that genuinely
/// held no symbols and has just gained some. Routing either to `NoDiff` would drop the
/// file's changes silently — under-testing, which `PendingVerification.fs`'s header
/// forbids ("widen, never wipe"). The `Clean` arm is therefore load-bearing for
/// correctness, not an optimisation; `trustStoredRows: a Clean stamp can never buy the
/// NARROW answer` is the test that says so.
///
/// `Unknown` keeps AUTOMATION-67's asymmetry on purpose: with rows it is a seeded
/// `test-impact.db` (ADR-010) whose sidecar did not travel into a fresh workspace, and
/// those rows are a real prior extraction worth diffing; with none it is an ordinary
/// cold scan whose full-suite baseline runs anyway, so widening would buy nothing and
/// cost a whole-suite selection on every cold start.
let trustStoredRows (freshness: Freshness) (storedRowsExist: bool) : StoredRowTrust =
    match freshness, storedRowsExist with
    | Clean, true -> DiffAgainstStored
    | Clean, false -> EverySymbolIsNew
    | Unknown, true -> DiffAgainstStored
    | Unknown, false -> NoDiff
    | Dirty, _ -> NoDiff

/// True iff the sidecar has an explicit "ended clean" record for `relPath`.
/// Absent entries return false — the conservative default for the `markClean` gate.
/// Must NOT be used to gate `detectChanges`: that call site needs the three-way
/// `classify` to tell an absent (seeded-DB) record from an explicit dirty one.
let isClean (relPath: string) (store: Store) : bool =
    match classify relPath store with
    | Clean -> true
    | Dirty
    | Unknown -> false
