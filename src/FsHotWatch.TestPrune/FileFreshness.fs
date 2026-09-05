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

/// What the freshness sidecar RECORDS about a file's last check. This type is the
/// evidence, not the decision — `trustStoredRows` below owns what may be done with
/// it, and is the only thing that should be consulted for that. `Unknown` and
/// `Dirty` must stay distinct: collapsing them is the seeded-DB under-selection bug.
///
///   - `Clean`   — an explicit "ended its last check FCS-clean" record: the stored
///                 rows were a complete extraction at the time they were written.
///   - `Dirty`   — an explicit `fcsClean = false` record. The stored rows were
///                 written while FCS reported errors and may be PARTIAL, so they are
///                 not a BASELINE. That is a fact about the rows, not about the file:
///                 see `trustStoredRows`, where AUTOMATION-526 turned it from "this
///                 file contributed nothing" into "this file has no before".
///   - `Unknown` — NO record at all. Dominant cause is a seeded `test-impact.db`
///                 (ADR-010) whose fshw-owned sidecar did not travel with it into
///                 a fresh workspace, so the stored rows are a real prior DB, worth
///                 diffing. Collapsing it into `Dirty` — which widens rather than
///                 diffing — costs a whole-suite selection on every cold start;
///                 collapsing it into the `NoRows` cold-scan arm silently
///                 UNDER-selects, violating ADR-010's "a seeded DB over-indexes but
///                 never serves a stale verdict".
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
    /// There is no usable BEFORE for this file — the index holds no rows for it, or
    /// holds rows that may not be diffed against. Diffing against nothing is the widest
    /// possible answer — every symbol currently in the file reads as added — and that is
    /// the intended outcome, not a side effect. See `trustStoredRows`.
    | EverySymbolIsNew
    /// Contribute no changed symbols for this file. THE NARROWEST ANSWER THERE IS, and
    /// the only one that can hide a change, so it is reachable from exactly ONE pair —
    /// `Unknown, NoRows`, the ordinary cold scan whose full-suite baseline runs anyway.
    ///
    /// AUTOMATION-526. `Dirty, _` used to land here too, which made a file whose last
    /// check hit a transient FCS error contribute NOTHING on the pass that recovered —
    /// no warning, no selection, a green that had tested none of it. "I cannot tell what
    /// changed" and "nothing changed" are different answers; this arm is only ever the
    /// second one. `trustStoredRows: NoDiff is reachable from exactly one pair` is the
    /// test that keeps it that way.
    | NoDiff

/// What the index holds for one file, and WHEN it came to hold it.
///
/// AUTOMATION-228. This replaced a `storedRowsExist: bool`, which was true both for
/// "a genuine prior extraction" and for "rows this run wrote seconds ago" — two facts
/// that mean OPPOSITE things. A fresh or recreated index's first scan indexes the
/// CURRENT, already-edited tree and writes it as the baseline; `detectChanges` then
/// diffs current-against-stored, finds them identical, and selects zero projects.
/// Neither the selector nor freshness was broken. There was never a "before", and the
/// predicate could not say so.
///
/// AUTOMATION-277's rule — "ask the index what it HOLDS, never how it came to be that
/// way" — is kept, and this is the missing half of it: HOLDS needs a clock. Note what
/// is still NOT an input: `Database.WasRecreated`, a fact about this session's open,
/// answers neither "does the index hold rows for THIS file" nor "were they there
/// before this session started". The distinction below is per file, which is the
/// granularity the question actually has.
///
///   - `NoRows`           — the index holds nothing for this file.
///   - `RowsFromPriorRun` — rows that were already in the index when this session
///                          first looked. A real baseline: the tree may have moved
///                          since, so a diff against them is meaningful.
///   - `RowsFromThisRun`  — rows this session wrote itself, from the tree as it is
///                          NOW. Diffing against them compares the file to itself and
///                          can only ever report "nothing changed".
type StoredRows =
    | NoRows
    | RowsFromPriorRun
    | RowsFromThisRun

/// Decide what the stored rows are good for, from the sidecar's verdict plus the one
/// structural fact the sidecar cannot know — what the index holds for this file, and
/// since when.
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
/// `Unknown` keeps AUTOMATION-67's asymmetry on purpose: with PRIOR rows it is a seeded
/// `test-impact.db` (ADR-010) whose sidecar did not travel into a fresh workspace, and
/// those rows are a real prior extraction worth diffing; with none it is an ordinary
/// cold scan whose full-suite baseline runs anyway, so widening would buy nothing and
/// cost a whole-suite selection on every cold start.
/// `RowsFromThisRun` is the AUTOMATION-228 arm. It resolves exactly as `NoRows` does,
/// and for the same reason: in both cases the index knew NOTHING about this file
/// before this run, so every symbol currently in it is new to the index. Routing it to
/// `DiffAgainstStored` — which is what a bare `storedRowsExist = true` bought — is a
/// self-comparison, and a self-comparison always reports zero changes. That is how a
/// diff adding brand-new `[<Fact>]` tests selected zero test projects while the daemon
/// log named every one of those tests: they were in the baseline the diff ran against.
let trustStoredRows (freshness: Freshness) (rows: StoredRows) : StoredRowTrust =
    match freshness, rows with
    | Clean, RowsFromPriorRun -> DiffAgainstStored
    | Clean, (NoRows | RowsFromThisRun) -> EverySymbolIsNew
    | Unknown, RowsFromPriorRun -> DiffAgainstStored
    // No sidecar record AND no baseline. `NoRows` stays `NoDiff` — an ordinary cold
    // scan, whose full-suite baseline runs anyway, so widening buys nothing and costs a
    // whole-suite selection on every cold start. `RowsFromThisRun` is NOT that case: the
    // rows are this run's own, so the alternative is not "a cheap cold scan" but "a diff
    // against itself", and `PendingVerification.fs`'s rule applies — widen, never wipe.
    | Unknown, RowsFromThisRun -> EverySymbolIsNew
    | Unknown, NoRows -> NoDiff
    // AUTOMATION-526. `Dirty` says the stored rows are not a BASELINE — they were
    // written while FCS reported errors and may be partial. It says NOTHING about the
    // current extraction, which the call site has already established is FCS-clean and
    // therefore complete. So this is the same fact `Clean, NoRows` states: there is no
    // usable before, and every symbol currently in the file is new to the index.
    //
    // It used to be `NoDiff`, to avoid the phantom "all symbols changed" delta that
    // diffing a complete extraction against partial rows produces. That avoided the
    // cost by paying with the answer: on the pass that RECOVERED from a transient FCS
    // error, the file contributed nothing and its tests were not selected — silently,
    // under a green check. A new four-test guard was invisible to three consecutive
    // impact-filtered runs that way.
    //
    // Widening is per FILE, not per project: it selects what this file's symbols
    // actually reach, which is the answer the `Dirty` stamp leaves us entitled to.
    // It is also bounded — the call site marks the baseline established once this
    // extraction is consumed, and the next clean check stamps the sidecar `Clean`,
    // so a recovery costs ONE widening, not one on every subsequent save.
    | Dirty, _ -> EverySymbolIsNew

/// The complete answer for ONE look at ONE file: `trustStoredRows`'s verdict about the
/// stored rows, joined with the one fact only the call site holds — whether the
/// extraction it is holding is itself FCS-clean.
///
/// AUTOMATION-526. The call site carried this as a bare `bool` that it then `ignore`d,
/// which made the two ways of contributing nothing indistinguishable: an ordinary cold
/// scan, which hides nothing, and a file whose changes were genuinely dropped. Both
/// logged at `info`, both selected nothing, and from outside a run that skipped a file
/// and a run that had nothing to skip produce the same green.
///
/// It also carries WHAT to diff against, rather than leaving the call site to work that
/// out from the `StoredRowTrust` a second time. AUTOMATION-526 found that call site
/// passing the stored rows for BOTH diffable arms, which made `EverySymbolIsNew`
/// behave exactly like `DiffAgainstStored` — so AUTOMATION-228's widening existed in
/// the type and in this module's tests, and nowhere in the running daemon. Whenever the
/// rows were this run's own, that "widening" was the self-comparison it was introduced
/// to replace, and a self-comparison reports zero changes every time.
type DiffBaseline =
    /// Diff against the rows the index holds for this file: a real BEFORE.
    | AgainstStoredRows
    /// Diff against NOTHING. There is no usable before, so every symbol currently in the
    /// file reads as added. Deliberately not "list the current symbols" — `detectChanges`
    /// filters externs internally, and an extern has no body to have changed.
    | AgainstNothing

type LookOutcome =
    /// Diff the current extraction, against the baseline named here.
    | Diffable of DiffBaseline
    /// No changed symbols for this file, and nothing is hidden by that: the ordinary
    /// cold scan (`Unknown, NoRows`), whose full-suite baseline runs anyway. There is
    /// no narrower answer being taken here, because there is no wider one available.
    | NothingHidden
    /// No changed symbols for this file, and this file's changes ARE hidden by that.
    /// FCS reported errors for the file on this very check, so the symbols just
    /// extracted may be PARTIAL — widening from them would enqueue names that may not
    /// exist in the tree. Nothing about this file was verified by this look.
    ///
    /// Transient by construction, and only since AUTOMATION-526: the rows are still
    /// persisted and the sidecar records `fcsClean = false`, so the next look classifies
    /// `Dirty` and `trustStoredRows` widens it. Before that fix the next look was
    /// `Dirty -> NoDiff` and the drop became permanent for the session — which is the
    /// defect. Reported at warn, never at info: a look that verified nothing about a
    /// file it was handed is not routine progress.
    | FileUnverified

/// Join the two halves of the decision. Total over the pair, so no caller can add a
/// third silent way to contribute nothing without adding a case here first — and the
/// baseline comes out of the same match that chose the arm, so the two cannot disagree.
let planLook (currentClean: bool) (trust: StoredRowTrust) : LookOutcome =
    match currentClean, trust with
    | true, DiffAgainstStored -> Diffable AgainstStoredRows
    | true, EverySymbolIsNew -> Diffable AgainstNothing
    | true, NoDiff -> NothingHidden
    | false, _ -> FileUnverified

/// The rows to diff the current extraction against, given the baseline the plan chose.
///
/// A function rather than a `match` at the call site, because that match is where
/// AUTOMATION-526's second half went wrong: the plugin passed the stored rows for BOTH
/// diffable arms, so `AgainstNothing` — the widening — silently became a diff against
/// whatever the index held, including rows this very run had written. Generic in the row
/// type so it lives here, beside the decision, instead of in the consumer that already
/// got it wrong once.
let baselineRows (baseline: DiffBaseline) (storedRows: 'row list) : 'row list =
    match baseline with
    | AgainstStoredRows -> storedRows
    | AgainstNothing -> []

/// The clock `StoredRows` needs, kept per file for the life of one plugin session.
///
/// AUTOMATION-228. The index alone cannot answer "was there a before?": after a scan
/// has indexed the already-edited tree, "this file has not changed" and "this index
/// has never had a version of this file to compare against" produce byte-identical
/// rows. Only WHEN the rows arrived separates them, and nothing was recording that.
///
/// Two operations, deliberately split:
///   * `Classify` is a pure read. It answers from the FIRST look at each file, which
///     is necessarily taken before this session has written anything for it.
///   * `MarkBaselineEstablished` is the write, and the caller performs it only where a
///     diff (or a widening) was actually CONSUMED. That is what keeps the widening to
///     one per file per session instead of forever: once a trustworthy extraction has
///     been taken and consumed, the rows the next look sees genuinely predate whatever
///     edit provokes it, and the cheap narrow diff is correct again. A discarded
///     extraction — an FCS-dirty one, or a `NoDiff` arm — marks nothing, so the next
///     look still widens.
///
/// Thread-safe by construction: the mailbox and the cache intercept run on different
/// threads, the same reason the refs beside it in `TestPrunePlugin` are `Volatile`.
type PriorRowLedger() =
    let baselineFor = System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

    /// What the index holds for `relPath`, and whether it is a baseline.
    /// `currentRowsExist` is "does the index hold rows for this file right now".
    member _.Classify(relPath: string, currentRowsExist: bool) : StoredRows =
        let hasBaseline = baselineFor.GetOrAdd(relPath, (fun _ -> currentRowsExist))

        if not currentRowsExist then NoRows
        elif hasBaseline then RowsFromPriorRun
        else RowsFromThisRun

    /// Record that a trustworthy extraction of `relPath` has been taken AND consumed,
    /// so the rows it leaves behind are a real "before" for the next look.
    member _.MarkBaselineEstablished(relPath: string) : unit = baselineFor.[relPath] <- true

/// True iff the sidecar has an explicit "ended clean" record for `relPath`.
/// Absent entries return false — the conservative default for the `markClean` gate.
/// Must NOT be used to gate `detectChanges`: that call site needs the three-way
/// `classify` to tell an absent (seeded-DB) record from an explicit dirty one.
let isClean (relPath: string) (store: Store) : bool =
    match classify relPath store with
    | Clean -> true
    | Dirty
    | Unknown -> false
