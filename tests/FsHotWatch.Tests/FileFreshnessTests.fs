/// FileFreshness is an fshw-owned per-file "FCS check was clean" sidecar that
/// survives daemon restarts. It gates the `detectChanges` call site so cross-restart
/// Phase B replay only trusts rows that ended their last session FCS-clean.
module FsHotWatch.Tests.FileFreshnessTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune.FileFreshness
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.SymbolDiff

[<Fact(Timeout = 5000)>]
let ``empty store created when sidecar file does not exist`` () =
    withTempDir "ff-empty-load" (fun tmpDir ->
        let store = load tmpDir
        test <@ Map.isEmpty store @>)

[<Fact(Timeout = 5000)>]
let ``save then load round-trips a known dict`` () =
    withTempDir "ff-roundtrip" (fun tmpDir ->
        let now = DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc)

        let store =
            Map.empty
            |> Map.add
                "src/Foo.fs"
                { FcsClean = true
                  LastCleanCheckAt = Some now }
            |> Map.add
                "src/Bar.fs"
                { FcsClean = false
                  LastCleanCheckAt = None }

        save tmpDir store

        let loaded = load tmpDir
        test <@ loaded = store @>)

[<Fact(Timeout = 5000)>]
let ``markClean sets fcsClean=true and stamps timestamp`` () =
    let store = Map.empty
    let now = DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc)
    let updated = markClean now "src/Foo.fs" store

    test
        <@
            Map.tryFind "src/Foo.fs" updated = Some
                { FcsClean = true
                  LastCleanCheckAt = Some now }
        @>

[<Fact(Timeout = 5000)>]
let ``markDirty sets fcsClean=false but preserves prior LastCleanCheckAt`` () =
    let earlier = DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)

    let store =
        Map.empty
        |> Map.add
            "src/Foo.fs"
            { FcsClean = true
              LastCleanCheckAt = Some earlier }

    let updated = markDirty "src/Foo.fs" store

    test
        <@
            Map.tryFind "src/Foo.fs" updated = Some
                { FcsClean = false
                  LastCleanCheckAt = Some earlier }
        @>

[<Fact(Timeout = 5000)>]
let ``markDirty on absent file inserts entry with no LastCleanCheckAt`` () =
    let store = Map.empty
    let updated = markDirty "src/Foo.fs" store

    test
        <@
            Map.tryFind "src/Foo.fs" updated = Some
                { FcsClean = false
                  LastCleanCheckAt = None }
        @>

// =============================================================================
// markUnverified — used in place of markDirty when the plugin cannot promote a
// file to clean: no BuildCompleted has fired yet, or the FCS check reported
// errors. The invariant: once a file has been stamped clean, a later
// transient-dirty event must NOT erase that record. The next genuine clean stamp
// refreshes it; until then the prior clean record holds.
// =============================================================================

[<Fact(Timeout = 5000)>]
let ``markUnverified on absent file inserts fcsClean=false`` () =
    let store = Map.empty
    let updated = markUnverified "src/Foo.fs" store

    test
        <@
            Map.tryFind "src/Foo.fs" updated = Some
                { FcsClean = false
                  LastCleanCheckAt = None }
        @>

[<Fact(Timeout = 5000)>]
let ``markUnverified preserves prior clean state — does NOT downgrade clean to dirty`` () =
    // The deliberate trade: cold-start reliability over correctness on the
    // user-broke-their-code edge case.
    let earlier = DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)

    let store =
        Map.empty
        |> Map.add
            "src/Foo.fs"
            { FcsClean = true
              LastCleanCheckAt = Some earlier }

    let updated = markUnverified "src/Foo.fs" store

    test
        <@
            Map.tryFind "src/Foo.fs" updated = Some
                { FcsClean = true
                  LastCleanCheckAt = Some earlier }
        @>

[<Fact(Timeout = 5000)>]
let ``markUnverified on prior-dirty file leaves dirty — preserves LastCleanCheckAt if any`` () =
    let earlier = DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)

    let store =
        Map.empty
        |> Map.add
            "src/Foo.fs"
            { FcsClean = false
              LastCleanCheckAt = Some earlier }

    let updated = markUnverified "src/Foo.fs" store

    test
        <@
            Map.tryFind "src/Foo.fs" updated = Some
                { FcsClean = false
                  LastCleanCheckAt = Some earlier }
        @>

[<Fact(Timeout = 5000)>]
let ``isClean returns true only when entry is present and fcsClean = true`` () =
    let now = DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc)

    let store =
        Map.empty
        |> Map.add
            "clean.fs"
            { FcsClean = true
              LastCleanCheckAt = Some now }
        |> Map.add
            "dirty.fs"
            { FcsClean = false
              LastCleanCheckAt = None }

    test <@ isClean "clean.fs" store @>
    test <@ not (isClean "dirty.fs" store) @>
    test <@ not (isClean "absent.fs" store) @>

// =============================================================================
// classify — three-way trust (Clean / Dirty / Unknown) consumed by detectChanges.
// The Unknown-vs-Dirty split lets a seeded test-impact.db (ADR-010) whose sidecar
// didn't travel still be diffed, while explicitly-poisoned rows stay bypassed.
// AUTOMATION-67.
// =============================================================================

[<Fact(Timeout = 5000)>]
let ``classify: explicit clean entry -> Clean`` () =
    let now = DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc)

    let store =
        Map.empty
        |> Map.add
            "clean.fs"
            { FcsClean = true
              LastCleanCheckAt = Some now }

    test <@ classify "clean.fs" store = Clean @>

[<Fact(Timeout = 5000)>]
let ``classify: explicit dirty entry -> Dirty (poisoned rows, stays bypassed)`` () =
    let store =
        Map.empty
        |> Map.add
            "dirty.fs"
            { FcsClean = false
              LastCleanCheckAt = None }

    test <@ classify "dirty.fs" store = Dirty @>

[<Fact(Timeout = 5000)>]
let ``classify: absent entry -> Unknown (seeded-DB case, diffable when rows exist)`` () =
    // The seeded-workspace under-selection root cause: a copied test-impact.db has
    // rows but no sidecar record, so every seeded file classifies Unknown, and the
    // call site diffs Unknown-over-nonempty rows.
    test <@ classify "never-seen.fs" Map.empty = Unknown @>

[<Fact(Timeout = 5000)>]
let ``classify: Unknown is distinct from Dirty — the load-bearing polarity split`` () =
    // Regression guard: if this collapses (e.g. classify maps None->Dirty), the
    // seeded-DB fix silently reverts to under-selection.
    test <@ classify "absent" Map.empty <> Dirty @>

// =============================================================================
// AUTOMATION-277 — the sidecar's relationship to `test-impact.db`.
//
// `file-freshness.json` carries no schema version and sits BESIDE a database that
// deletes and recreates itself on a `SchemaVersion` bump. The sidecar survives that.
// AUTOMATION-275 established that the identically-shaped `pending-verification.json`
// could then discharge real test debt as a zero-test green.
//
// Everything below is measured against a REAL recreate, never a simulated one:
// `PRAGMA user_version` is stamped stale and `Database.create` performs its own
// delete-and-recreate. Deleting the file by hand does NOT reproduce it —
// `Microsoft.Data.Sqlite` pools connections and hands a later open the deleted inode
// with its rows intact, so the test passes against a database that was never
// recreated. That vacuous pass already fooled this investigation once (AUTOMATION-275).
// =============================================================================

/// A symbol row for `src/Lib.fs`, the file every recreate test below stamps clean.
let private libFoo: SymbolInfo =
    { FullName = "Lib.foo"
      Kind = SymbolKind.Value
      SourceFile = "src/Lib.fs"
      LineStart = 1
      LineEnd = 1
      ContentHash = "hash-v1"
      IsExtern = false }

/// Index `libFoo`, stamp `src/Lib.fs` clean in the sidecar, then force a REAL
/// schema recreate. Returns the reopened database and the sidecar as the plugin
/// would load it on the next start.
///
/// `Guard`: asserts the recreate actually happened (`WasRecreated`) and that the
/// index really lost its rows, so nothing downstream of this helper can pass
/// vacuously against a database that was never recreated.
let private afterFaithfulRecreate (tmpDir: string) : Database * Store =
    let dbPath = Path.Combine(tmpDir, "tp.db")

    let db = Database.create dbPath
    db.RebuildProjects [ AnalysisResult.Create([ libFoo ], [], []) ]

    // PositiveControl: the index holds the row BEFORE the recreate. Without this the
    // "no rows afterwards" assertion below would also pass against a fixture that
    // never indexed anything.
    test <@ (db.GetSymbolsInFile "src/Lib.fs").Length = 1 @>

    let now = DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc)
    save tmpDir (markClean now "src/Lib.fs" Map.empty)

    // The schema bump an older TestPrune.Core would have left behind.
    do
        use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{dbPath}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA user_version = 1;"
        cmd.ExecuteNonQuery() |> ignore

    let reopened = Database.create dbPath

    // Guard: the recreate is the premise of every assertion below it.
    test <@ reopened.WasRecreated @>
    test <@ (reopened.GetSymbolsInFile "src/Lib.fs").IsEmpty @>

    (reopened, load tmpDir)

[<Fact(Timeout = 15000)>]
let ``a schema recreate empties the index but the freshness sidecar survives saying Clean`` () =
    // The premise of the whole ticket, measured rather than assumed: the two files
    // DO drift apart, and the surviving sidecar keeps making a claim about rows the
    // index no longer holds.
    withTempDir "ff-recreate-survives" (fun tmpDir ->
        let (_, store) = afterFaithfulRecreate tmpDir
        test <@ classify "src/Lib.fs" store = Clean @>)

[<Fact(Timeout = 15000)>]
let ``a Clean stamp over an emptied index must still WIDEN — every current symbol reads as new`` () =
    // THE ASSERTION AUTOMATION-277 EXISTS FOR. The sidecar says "the stored rows for
    // this file are a complete extraction, safe to diff". After a recreate there are
    // no stored rows for that claim to be about, and the only sound reading is "this
    // index has never seen this file" — so every symbol currently in it is new and
    // must be re-verified.
    //
    // Nothing asserted this before. The widening was a by-product of diffing against
    // an empty list, and `trustStoredRows` is what makes it a decision instead.
    withTempDir "ff-recreate-widens" (fun tmpDir ->
        let (db, store) = afterFaithfulRecreate tmpDir

        let stored = db.GetSymbolsInFile "src/Lib.fs"
        let freshness = classify "src/Lib.fs" store

        test <@ trustStoredRows freshness NoRows = EverySymbolIsNew @>
        test <@ List.isEmpty stored @>

        // …and the widening is real, not just a label: the file's symbols come back
        // out as changed, which is what re-queues them for verification.
        let (changes, _) = detectChanges [ libFoo ] stored
        test <@ not (List.isEmpty changes) @>)

[<Fact(Timeout = 15000)>]
let ``PositiveControl: a Clean stamp over an index that still HAS the rows stays a real diff`` () =
    // The mirror of the test above, and the reason it is not vacuous. "Is invalidated
    // after a recreate" passes trivially if the mechanism invalidates everything; this
    // pins the case where the sidecar is genuinely current and must keep buying the
    // cheap path.
    withTempDir "ff-no-recreate" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        db.RebuildProjects [ AnalysisResult.Create([ libFoo ], [], []) ]

        let now = DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc)
        save tmpDir (markClean now "src/Lib.fs" Map.empty)

        // No schema stamp, so the reopen is compatible and nothing is recreated.
        let reopened = Database.create dbPath
        let stored = reopened.GetSymbolsInFile "src/Lib.fs"

        // Guard: this fixture's whole point is that the rows SURVIVED.
        test <@ not (List.isEmpty stored) @>

        let freshness = classify "src/Lib.fs" (load tmpDir)
        test <@ freshness = Clean @>
        test <@ trustStoredRows freshness RowsFromPriorRun = DiffAgainstStored @>

        // An unchanged file diffs to nothing — the cheap path is intact.
        let (changes, _) = detectChanges [ libFoo ] stored
        test <@ List.isEmpty changes @>)

// -----------------------------------------------------------------------------
// `trustStoredRows` — the decision table, without a database.
// -----------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``trustStoredRows: Clean over PRIOR rows diffs; Clean over nothing widens`` () =
    test <@ trustStoredRows Clean RowsFromPriorRun = DiffAgainstStored @>
    test <@ trustStoredRows Clean NoRows = EverySymbolIsNew @>

[<Fact(Timeout = 5000)>]
let ``trustStoredRows: Unknown diffs only against PRIOR rows (ADR-010 seeded DB)`` () =
    test <@ trustStoredRows Unknown RowsFromPriorRun = DiffAgainstStored @>
    test <@ trustStoredRows Unknown NoRows = NoDiff @>

[<Fact(Timeout = 5000)>]
let ``trustStoredRows: Dirty never diffs, whatever the index holds — it WIDENS instead`` () =
    // AUTOMATION-526. "Never diffs" was always right; "contributes nothing" was the
    // mistake. The `Dirty` stamp is a statement about the stored ROWS — written while
    // FCS reported errors, possibly partial — and says nothing about the current
    // extraction, which the call site has already established is clean and complete.
    // So it is the same fact `Clean, NoRows` states: there is no usable before.
    test <@ trustStoredRows Dirty RowsFromPriorRun = EverySymbolIsNew @>
    test <@ trustStoredRows Dirty RowsFromThisRun = EverySymbolIsNew @>
    test <@ trustStoredRows Dirty NoRows = EverySymbolIsNew @>

// -----------------------------------------------------------------------------
// AUTOMATION-228 — rows this run wrote are not a baseline.
//
// A fresh or recreated index's first scan indexes the CURRENT, already-edited tree and
// writes it as the baseline. `detectChanges` then diffs current-against-stored, finds
// them identical, and selects zero test projects — on a diff that ADDS brand-new
// `[<Fact>]` tests, which the daemon log names one by one. Nothing was broken: there
// was never a "before", and `storedRowsExist: bool` had no way to say so. It was true
// for "a genuine prior extraction" and for "rows this run just created", which mean
// opposite things.
// -----------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-228: rows THIS RUN wrote are not a baseline — every symbol reads as new`` () =
    // The arm the bug needed and did not have. It resolves exactly as `NoRows` does,
    // because it IS the same fact: the index knew nothing about this file before this
    // run started.
    test <@ trustStoredRows Clean RowsFromThisRun = EverySymbolIsNew @>
    test <@ trustStoredRows Clean NoRows = EverySymbolIsNew @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-228: no sidecar and no baseline still widens rather than diffing against itself`` () =
    // The fresh-workspace shape: `.fshw/` did not travel, so there is no sidecar
    // record, and the only rows in the index are the ones this scan wrote. `NoDiff`
    // (the pre-fix answer for `Unknown` + no rows) is right for an ordinary cold scan,
    // whose full-suite baseline runs anyway — it is NOT right here, where the
    // alternative was a diff against itself. Widen, never wipe.
    test <@ trustStoredRows Unknown RowsFromThisRun = EverySymbolIsNew @>

[<Fact(Timeout = 5000)>]
let ``PositiveControl AUTOMATION-228: PRIOR rows still buy the cheap diff — the fix is not "widen everything"`` () =
    // Without this the fix would be indistinguishable from deleting impact filtering.
    // A warm daemon, or any index that held rows for the file BEFORE this session
    // looked, must keep the narrow answer — that is the whole value of the feature.
    test <@ trustStoredRows Clean RowsFromPriorRun = DiffAgainstStored @>
    test <@ trustStoredRows Unknown RowsFromPriorRun = DiffAgainstStored @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-228: a dirty extraction is still never diffed against, whoever wrote the rows`` () =
    // The clock does not outrank the sidecar's explicit "these rows may be PARTIAL" —
    // the answer is never `DiffAgainstStored`. AUTOMATION-526 changed WHICH non-diff
    // answer it is, not this claim.
    test <@ trustStoredRows Dirty RowsFromThisRun <> DiffAgainstStored @>
    test <@ trustStoredRows Dirty RowsFromPriorRun <> DiffAgainstStored @>

// -----------------------------------------------------------------------------
// AUTOMATION-526 — a recovery that cannot determine a diff must not select NOTHING.
//
// A test file whose symbol analysis hit a transient FCS error is stamped
// `fcsClean = false`. On the pass that RECOVERS — FCS clean again, extraction
// complete — `Dirty, _ -> NoDiff` made the file contribute no changed symbols at all.
// Not "the file was unchanged": the file was never asked. A four-test guard was
// invisible to three consecutive impact-filtered runs that way, and the runs were
// green, because a blind check and a genuinely green check are the same green check
// from outside.
// -----------------------------------------------------------------------------

/// The name a `SymbolChange` carries, whichever kind it is. These assertions are about
/// WHICH symbols reached the selector, not about how each one changed.
let private changedName (change: SymbolChange) : string =
    match change with
    | Added n
    | Removed n
    | Modified n -> n

/// A symbol row for a test file, used to build the recovery scenario below.
let private guardTest (name: string) (hash: string) : SymbolInfo =
    { FullName = $"Heartbeat.%s{name}"
      Kind = SymbolKind.Value
      SourceFile = "tests/HeartbeatAlarmWiringTests.fs"
      LineStart = 1
      LineEnd = 1
      ContentHash = hash
      IsExtern = false }

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-526: the pass that RECOVERS from a transient FCS error selects the file's tests`` () =
    // The reproduction, as the plugin runs it: sidecar → classify → trustStoredRows →
    // planLook → detectChanges. Every step is the real function.
    let sidecar = markDirty "tests/HeartbeatAlarmWiringTests.fs" Map.empty
    let freshness = classify "tests/HeartbeatAlarmWiringTests.fs" sidecar

    // Guard: the fixture really is in the recovery state, or everything below is vacuous.
    test <@ freshness = Dirty @>

    // The rows the DIRTY check left behind: partial, only the one test that parsed.
    let storedDuringDirtyCheck = [ guardTest "alarmWiringA" "h1" ]

    // The recovery pass: FCS clean again, so the extraction is COMPLETE.
    let currentAfterRecovery =
        [ guardTest "alarmWiringA" "h1"
          guardTest "alarmWiringB" "h2"
          guardTest "alarmWiringC" "h3"
          guardTest "alarmWiringD" "h4" ]

    // `currentClean = true` — that is what makes this the recovery pass and not the
    // failure itself.
    let plan = planLook true (trustStoredRows freshness RowsFromPriorRun)

    // It must be a diff, and it must be against NOTHING: the partial rows are not a
    // baseline, so every symbol currently in the file is new to the index.
    test <@ plan = Diffable AgainstNothing @>

    // `baselineRows` is the plugin's own call — the seam, not a re-derivation of it.
    let priorSymbols =
        match plan with
        | Diffable baseline -> baselineRows baseline storedDuringDirtyCheck
        | NothingHidden
        | FileUnverified -> []

    let (changes, _) = detectChanges currentAfterRecovery priorSymbols
    let selected = changes |> List.map changedName

    // All four, BY NAME. "not empty" would pass on a fix that selected only the three
    // the partial rows happened to be missing.
    test
        <@
            List.sort selected = [ "Heartbeat.alarmWiringA"
                                   "Heartbeat.alarmWiringB"
                                   "Heartbeat.alarmWiringC"
                                   "Heartbeat.alarmWiringD" ]
        @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-526: the recovery pass selected NOTHING when the partial rows were this run's own`` () =
    // The silent-zero shape, shown rather than described. The pre-fix answer was
    // `NoDiff` for every `Dirty` pair; and even routed to `EverySymbolIsNew`, a call
    // site that diffs against `storedSymbols` anyway gets zero when those rows are the
    // ones this run just wrote. Both roads led to the same empty selection.
    let identicalRows = [ guardTest "alarmWiringA" "h1"; guardTest "alarmWiringB" "h2" ]

    let (selfDiff, _) = detectChanges identicalRows identicalRows
    test <@ List.isEmpty selfDiff @>

    // The plan refuses that baseline, and `baselineRows` is what enforces it. Asserting
    // only `plan = Diffable AgainstNothing` would pass against the defect this fix
    // found: a call site that named the widening and then diffed against the stored
    // rows anyway, which is how AUTOMATION-228's widening came to exist in the type and
    // in these tests but never in the running daemon.
    let plan = planLook true (trustStoredRows Dirty RowsFromThisRun)
    test <@ plan = Diffable AgainstNothing @>

    let priorSymbols =
        match plan with
        | Diffable baseline -> baselineRows baseline identicalRows
        | NothingHidden
        | FileUnverified -> identicalRows

    test <@ List.isEmpty priorSymbols @>

    let (widened, _) = detectChanges identicalRows priorSymbols
    test <@ (widened |> List.map changedName |> List.sort) = [ "Heartbeat.alarmWiringA"; "Heartbeat.alarmWiringB" ] @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-526: baselineRows is what makes AgainstNothing mean nothing`` () =
    // The seam on its own. `AgainstNothing` is a widening only if the rows are actually
    // withheld; a consumer that names it and hands over the stored rows regardless gets
    // a self-comparison and reports zero changes, which is indistinguishable from
    // "unchanged".
    let rows = [ guardTest "alarmWiringA" "h1" ]

    test <@ List.isEmpty (baselineRows AgainstNothing rows) @>

    // PositiveControl: it is not "always empty". The narrow arm must still hand the real
    // baseline over, or impact filtering is deleted rather than corrected.
    test <@ baselineRows AgainstStoredRows rows = rows @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-526: NoDiff is reachable from exactly ONE pair — the detector-went-blind guard`` () =
    // THE test for the failure mode itself rather than for one instance of it.
    //
    // `NoDiff` is the only `StoredRowTrust` that can hide a change: it contributes no
    // symbols, so nothing selects tests, so the run is green having verified nothing
    // about the file. Asserting "the Dirty arm no longer returns NoDiff" would not
    // catch the NEXT arm to drift into it. This enumerates the whole 3x3 product and
    // names the single pair entitled to that answer — the ordinary cold scan, whose
    // full-suite baseline runs anyway, so nothing is being declined.
    //
    // If a future edit routes any other pair to `NoDiff`, this fails by name.
    let allPairs =
        [ for freshness in [ Clean; Dirty; Unknown ] do
              for rows in [ NoRows; RowsFromPriorRun; RowsFromThisRun ] -> freshness, rows ]

    // Guard: the product really is the whole table, so a shrunk list cannot pass this
    // vacuously.
    test <@ List.length allPairs = 9 @>

    let blind = allPairs |> List.filter (fun (f, r) -> trustStoredRows f r = NoDiff)

    test <@ blind = [ (Unknown, NoRows) ] @>

[<Fact(Timeout = 5000)>]
let ``PositiveControl AUTOMATION-526: an ordinary run still PRUNES — the fix is not "select everything"`` () =
    // Required by the ticket, and the reason the guard above is not satisfied by
    // deleting impact filtering. A warm daemon with a clean sidecar and real prior
    // rows must still take the narrow answer, and an unchanged file must still select
    // NOTHING — that is the entire value of the feature.
    let unchanged = [ guardTest "alarmWiringA" "h1"; guardTest "alarmWiringB" "h2" ]

    let plan = planLook true (trustStoredRows Clean RowsFromPriorRun)
    test <@ plan = Diffable AgainstStoredRows @>

    let (changes, _) = detectChanges unchanged unchanged
    test <@ List.isEmpty changes @>

    // …and a real edit to one of them still selects exactly that one, so the narrow
    // answer is narrow because it is CORRECT, not because it is empty.
    let edited = [ guardTest "alarmWiringA" "h1-edited"; guardTest "alarmWiringB" "h2" ]
    let (afterEdit, _) = detectChanges edited unchanged
    test <@ (afterEdit |> List.map changedName) = [ "Heartbeat.alarmWiringA" ] @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-526: planLook separates the two ways of contributing nothing`` () =
    // The loudness half. The call site used to compute a `suppressedDiff: bool` and
    // then `ignore` it, so "an ordinary cold scan, nothing hidden" and "this file's
    // changes were DROPPED" were the same value and the same info-level log line.
    // They need opposite reactions, so they are separate cases.
    test <@ planLook true NoDiff = NothingHidden @>
    test <@ planLook false DiffAgainstStored = FileUnverified @>
    test <@ planLook false EverySymbolIsNew = FileUnverified @>
    test <@ planLook false NoDiff = FileUnverified @>

    // An FCS-dirty CURRENT extraction is never diffable, whatever the stored rows are
    // worth: widening from symbols that may themselves be partial would enqueue names
    // that need not exist in the tree.
    for trust in [ DiffAgainstStored; EverySymbolIsNew; NoDiff ] do
        test <@ planLook false trust = FileUnverified @>

// -----------------------------------------------------------------------------
// `PriorRowLedger` — the clock itself.
// -----------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-228 ledger: an index that was EMPTY at the first look never calls its own rows a baseline`` () =
    // The fresh-workspace shape, step by step. The scan indexes the already-edited
    // tree (look 1: no rows), flushes, and something re-checks the file (look 2: rows
    // exist — this session's). Before the ledger, look 2 read `storedRowsExist = true`
    // and diffed the file against itself.
    let ledger = PriorRowLedger()

    test <@ ledger.Classify("src/Lib.fs", false) = NoRows @>
    test <@ ledger.Classify("src/Lib.fs", true) = RowsFromThisRun @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-228 ledger: an index that ALREADY held rows keeps the cheap narrow answer`` () =
    // The warm case, and the reason the ledger is not just "always widen".
    let ledger = PriorRowLedger()

    test <@ ledger.Classify("src/Lib.fs", true) = RowsFromPriorRun @>
    test <@ ledger.Classify("src/Lib.fs", true) = RowsFromPriorRun @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-228 ledger: a CONSUMED extraction establishes the baseline — the widening costs one look, not every look``
    ()
    =
    // Without this the fix would widen on every save for the whole session in a fresh
    // workspace, which is a real cost: an interactive loop would re-select every test
    // touching any symbol in the file it just saved.
    let ledger = PriorRowLedger()

    test <@ ledger.Classify("src/Lib.fs", false) = NoRows @>
    test <@ ledger.Classify("src/Lib.fs", true) = RowsFromThisRun @>

    ledger.MarkBaselineEstablished "src/Lib.fs"

    test <@ ledger.Classify("src/Lib.fs", true) = RowsFromPriorRun @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-228 ledger: an UNCONSUMED extraction establishes nothing — the next look still widens`` () =
    // The positive control for the mark. An FCS-dirty extraction, or one that landed on
    // a `NoDiff` arm, is discarded; a baseline it never established must not be claimed
    // on its behalf, or the widening is skipped and the change is lost for good.
    let ledger = PriorRowLedger()

    test <@ ledger.Classify("src/Lib.fs", false) = NoRows @>
    test <@ ledger.Classify("src/Lib.fs", true) = RowsFromThisRun @>
    test <@ ledger.Classify("src/Lib.fs", true) = RowsFromThisRun @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-228 ledger: files are independent — one file's baseline is not another's`` () =
    let ledger = PriorRowLedger()

    test <@ ledger.Classify("src/Warm.fs", true) = RowsFromPriorRun @>
    test <@ ledger.Classify("src/Cold.fs", false) = NoRows @>
    test <@ ledger.Classify("src/Cold.fs", true) = RowsFromThisRun @>
    test <@ ledger.Classify("src/Warm.fs", true) = RowsFromPriorRun @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-228 end to end: the fresh-workspace second look SELECTS, where a self-diff selected nothing`` () =
    // The whole chain, without a database: ledger → trustStoredRows → detectChanges.
    // The rows and the current extraction are IDENTICAL, which is exactly the state a
    // scan of an already-edited tree leaves behind — and the state in which a diff can
    // only ever say "nothing changed".
    let ledger = PriorRowLedger()

    let sidecar =
        markClean (DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc)) "src/Lib.fs" Map.empty

    // Look 1: the scan indexes the already-edited tree. Nothing stored yet.
    test <@ ledger.Classify("src/Lib.fs", false) = NoRows @>

    // Look 2: the rows now in the index are this session's own, identical to what is
    // on disk.
    let stored = [ libFoo ]
    let current = [ libFoo ]

    let rows = ledger.Classify("src/Lib.fs", not stored.IsEmpty)
    test <@ rows = RowsFromThisRun @>

    let trust = trustStoredRows (classify "src/Lib.fs" sidecar) rows
    test <@ trust = EverySymbolIsNew @>

    // The pre-fix answer, shown rather than described: `RowsFromPriorRun` would have
    // bought `DiffAgainstStored`, and diffing identical rows selects NOTHING.
    test <@ trustStoredRows Clean RowsFromPriorRun = DiffAgainstStored @>
    let (selfDiff, _) = detectChanges current stored
    test <@ List.isEmpty selfDiff @>

    // `EverySymbolIsNew` diffs against the EMPTY stored set instead, and that is what
    // puts the file's symbols back in front of the selector.
    let (widened, _) = detectChanges current []
    test <@ not (List.isEmpty widened) @>

[<Fact(Timeout = 5000)>]
let ``trustStoredRows: a Clean stamp can never buy the NARROW answer`` () =
    // The polarity guard. `NoDiff` contributes no changed symbols for the file, so
    // routing a Clean stamp there is the UNDER-testing direction — the one
    // `PendingVerification.fs`'s header forbids and the one AUTOMATION-275's sibling
    // bug actually took. Collapsing `Clean` into `Unknown`'s "only when rows exist"
    // rule is exactly how that flip would arrive, and it lands here.
    test <@ trustStoredRows Clean RowsFromPriorRun <> NoDiff @>
    test <@ trustStoredRows Clean RowsFromThisRun <> NoDiff @>
    test <@ trustStoredRows Clean NoRows <> NoDiff @>

[<Fact(Timeout = 5000)>]
let ``save uses atomic write — partial state never visible at sidecar path`` () =
    // Indirect: a leftover .tmp alongside the sidecar means the rename never
    // happened, so partial state was writable at the real path.
    withTempDir "ff-atomic" (fun tmpDir ->
        let store =
            Map.empty
            |> Map.add
                "f.fs"
                { FcsClean = true
                  LastCleanCheckAt = Some(DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) }

        save tmpDir store
        let p = sidecarPath tmpDir
        test <@ File.Exists p @>
        test <@ not (File.Exists(p + ".tmp")) @>)

[<Fact(Timeout = 5000)>]
let ``corrupt sidecar JSON falls back to empty store rather than throwing`` () =
    // The sidecar is derivative: losing it over-marks files dirty for one cycle,
    // which is a better trade than crashing the plugin.
    withTempDir "ff-corrupt" (fun tmpDir ->
        let p = sidecarPath tmpDir
        let dir = Path.GetDirectoryName p

        if not (String.IsNullOrEmpty dir) then
            Directory.CreateDirectory dir |> ignore

        File.WriteAllText(p, "{ this is not json")

        let loaded = load tmpDir
        test <@ Map.isEmpty loaded @>)
