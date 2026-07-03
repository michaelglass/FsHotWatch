/// Tests for FsHotWatch.TestPrune.FileFreshness — fshw-owned per-file
/// "FCS check was clean" sidecar. Survives daemon restarts; gates the
/// `detectChanges` call site so cross-restart Phase B replay only trusts
/// rows that ended their last session FCS-clean. Path D in the design
/// (sidecar JSON), keeps TestPrune.Core schema unchanged.
module FsHotWatch.Tests.FileFreshnessTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune.FileFreshness
open FsHotWatch.Tests.TestHelpers

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
// markUnverified — Item 3 (BuildCompleted-gated stamping). Used in place of
// markDirty when the plugin can't promote a file to clean (because no
// BuildCompleted has fired yet, or the FCS check itself reported errors).
//
// The Item 3 invariant: once a file has been stamped clean, a later
// transient-dirty event must NOT erase that record. The next genuine clean
// stamp will refresh it; until then the prior clean record holds.
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
    // Item 3 invariant: cold-start reliability over correctness on the
    // user-broke-their-code edge case. The next genuine markClean will
    // refresh the timestamp; until then the prior clean record holds.
    let earlier = DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)

    let store =
        Map.empty
        |> Map.add
            "src/Foo.fs"
            { FcsClean = true
              LastCleanCheckAt = Some earlier }

    let updated = markUnverified "src/Foo.fs" store

    // Identity: the prior clean record survives unchanged.
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
// classify — three-way trust (Clean / Dirty / Unknown) consumed by the
// detectChanges call site. The Unknown-vs-Dirty split is what lets a seeded
// test-impact.db (ADR-010) whose sidecar didn't travel be diffed (Unknown)
// while explicitly-poisoned rows stay bypassed (Dirty). AUTOMATION-67.
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
    // The seeded-workspace under-selection root cause: a copied test-impact.db
    // has rows but no matching sidecar record, so every seeded file classifies
    // Unknown (NOT Dirty). The call site diffs Unknown-over-nonempty rows.
    test <@ classify "never-seen.fs" Map.empty = Unknown @>

[<Fact(Timeout = 5000)>]
let ``classify: Unknown is distinct from Dirty — the load-bearing polarity split`` () =
    // Regression guard: if this collapses (e.g. classify maps None->Dirty), the
    // seeded-DB fix silently reverts to under-selection.
    test <@ classify "absent" Map.empty <> Dirty @>

[<Fact(Timeout = 5000)>]
let ``save uses atomic write — partial state never visible at sidecar path`` () =
    // Indirect check: after save the .tmp file should not exist alongside,
    // and the sidecar file must parse cleanly. (This is a smoke test against
    // the implementation forgetting to rename / delete the .tmp.)
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
    // The sidecar is derivative — losing it means we just over-mark files
    // dirty for one cycle. Crashing the plugin on a corrupt sidecar would
    // be a worse trade.
    withTempDir "ff-corrupt" (fun tmpDir ->
        let p = sidecarPath tmpDir
        let dir = Path.GetDirectoryName p

        if not (String.IsNullOrEmpty dir) then
            Directory.CreateDirectory dir |> ignore

        File.WriteAllText(p, "{ this is not json")

        let loaded = load tmpDir
        test <@ Map.isEmpty loaded @>)
