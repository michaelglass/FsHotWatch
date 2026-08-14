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

        test <@ trustStoredRows freshness (not stored.IsEmpty) = EverySymbolIsNew @>

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
        test <@ trustStoredRows freshness (not stored.IsEmpty) = DiffAgainstStored @>

        // An unchanged file diffs to nothing — the cheap path is intact.
        let (changes, _) = detectChanges [ libFoo ] stored
        test <@ List.isEmpty changes @>)

// -----------------------------------------------------------------------------
// `trustStoredRows` — the decision table, without a database.
// -----------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``trustStoredRows: Clean over rows diffs; Clean over nothing widens`` () =
    test <@ trustStoredRows Clean true = DiffAgainstStored @>
    test <@ trustStoredRows Clean false = EverySymbolIsNew @>

[<Fact(Timeout = 5000)>]
let ``trustStoredRows: Unknown diffs only when rows exist (ADR-010 seeded DB)`` () =
    test <@ trustStoredRows Unknown true = DiffAgainstStored @>
    test <@ trustStoredRows Unknown false = NoDiff @>

[<Fact(Timeout = 5000)>]
let ``trustStoredRows: Dirty never diffs, rows or no rows`` () =
    test <@ trustStoredRows Dirty true = NoDiff @>
    test <@ trustStoredRows Dirty false = NoDiff @>

[<Fact(Timeout = 5000)>]
let ``trustStoredRows: a Clean stamp can never buy the NARROW answer`` () =
    // The polarity guard. `NoDiff` contributes no changed symbols for the file, so
    // routing a Clean stamp there is the UNDER-testing direction — the one
    // `PendingVerification.fs`'s header forbids and the one AUTOMATION-275's sibling
    // bug actually took. Collapsing `Clean` into `Unknown`'s "only when rows exist"
    // rule is exactly how that flip would arrive, and it lands here.
    test <@ trustStoredRows Clean true <> NoDiff @>
    test <@ trustStoredRows Clean false <> NoDiff @>

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
