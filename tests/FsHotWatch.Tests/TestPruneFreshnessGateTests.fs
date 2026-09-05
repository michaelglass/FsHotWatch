/// The freshness gates: the FCS cache-poisoning gate around `hasFcsErrors`, the per-file
/// freshness sidecar that decides what a change may be diffed against, and
/// `ArtifactFreshness` — the build-output staleness gate that refuses a run reading old
/// bits.
///
/// Split out of `TestPrunePluginTests`; shared harness in `TestPrunePluginTestSupport`.
module FsHotWatch.Tests.TestPruneFreshnessGateTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.SymbolDiff
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestPrunePluginTestSupport

// =============================================================================
// FCS cache-poisoning gate. Cold-start FCS sometimes returns "expected type X but here
// has type X" for files that compile cleanly once warm, and flushing those poisoned
// symbols overwrites the prior good DB snapshot, breaking cache replay on the next boot.
// =============================================================================

/// A real FCS FileCheckResult — full type-check, real diagnostics — so the gate tests
/// below see realistic Error / Warning / clean diagnostic shapes.
let private checkSourceForReal (tmpDir: string) (fileName: string) (source: string) =
    async {
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let filePath = Path.Combine(tmpDir, fileName)
        File.WriteAllText(filePath, source)
        let! projOptions = getScriptOptions checker filePath source
        pipeline.RegisterProject(filePath, projOptions)
        let! result = pipeline.CheckFile(AbsFilePath.create filePath)
        return result
    }

[<Fact(Timeout = 30000)>]
let ``a cached helper-file analysis cannot relabel a freshly executed test failure as cached`` () =
    withTempDir "tp-fresh-red-provenance" (fun tmpDir ->
        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let checkedFile =
            checkSourceForReal tmpDir "TestHelper.fsx" "module TestHelper\nlet value = 42\n"
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        let configs =
            [ { Project = "Failing.Tests"
                Command = "sh"
                Args = "-c \"echo 'failed Failing.Tests.Example.still_fails (1ms)'; exit 1\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = Disabled } ]

        let host = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cache)
        host.RegisterHandler(create (Path.Combine(tmpDir, "test.db")) tmpDir (Some configs) None None None None [])

        host.EmitFileChecked checkedFile
        waitForPluginTerminal host "test-prune" 12.0

        // Positive control: the same helper analysis remains cacheable while healthy.
        host.EmitFileChecked checkedFile
        test <@ waitForCachedReplay host "test-prune" 10000 @>

        host.EmitBuildCompleted BuildSucceeded

        let freshRed =
            waitUntilTrue
                (fun () ->
                    not (host.GetErrorsByPlugin("test-prune") |> Map.isEmpty)
                    && (terminalSummaryOf host "test-prune").Contains "failed")
                15000

        test <@ freshRed @>
        let fresh = terminalSummaryOf host "test-prune"
        let freshStatus = host.GetStatus("test-prune")
        let freshLedger = host.GetErrorsByPlugin("test-prune")
        test <@ not (fresh.Contains "(cached)") @>

        host.EmitFileChecked checkedFile
        waitForQuiescent host 10000

        test <@ terminalSummaryOf host "test-prune" = fresh @>
        test <@ host.GetStatus("test-prune") = freshStatus @>
        test <@ host.GetErrorsByPlugin("test-prune") = freshLedger @>)

[<Fact(Timeout = 30000)>]
let ``FileChecked analyzes its existing FCS payload without re-entering the checker`` () =
    withTempDir "tp-no-checker-reentry" (fun tmpDir ->
        let source = "module AlreadyChecked\nlet value = 42\n"

        let result =
            checkSourceForReal tmpDir "AlreadyChecked.fsx" source
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // The event already contains both parse and full-check results. Making the
        // host checker unusable proves TestPrune consumes that payload instead of
        // starting a second ParseAndCheckFileInProject pass.
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        host.RegisterHandler(create (Path.Combine(tmpDir, "test.db")) tmpDir None None None None None [])
        host.EmitFileChecked(result)
        waitForPluginTerminal host "test-prune" 12.0

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"Expected payload-only analysis to complete, got: %A{other}"))

[<Fact(Timeout = 15000)>]
let ``hasFcsErrors returns false for ParseOnly`` () =
    test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty "" ParseOnly) @>

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors returns true for source with type error`` () =
    withTempDir "tp-poisoning-err" (fun tmpDir ->
        let brokenSource =
            """module Broken
let x : int = "not an int"
"""

        let result =
            checkSourceForReal tmpDir "Broken.fsx" brokenSource
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        test <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults @>)

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors returns false for clean source`` () =
    withTempDir "tp-poisoning-clean" (fun tmpDir ->
        let cleanSource =
            """module Clean
let answer = 42
"""

        let result =
            checkSourceForReal tmpDir "Clean.fsx" cleanSource
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>)

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors returns false for warning-only source`` () =
    withTempDir "tp-poisoning-warn" (fun tmpDir ->
        // An incomplete pattern match: FCS reports FS0025 at Warning severity.
        let warnSource =
            """module Warn
let f x =
    match x with
    | 1 -> "one"
"""

        let result =
            checkSourceForReal tmpDir "Warn.fsx" warnSource
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // Sanity: the source really does carry warning diagnostics.
        let diagnostics =
            match result.CheckResults with
            | FullCheck cr -> cr.Diagnostics
            | ParseOnly -> [||]

        test <@ diagnostics.Length > 0 @>

        test
            <@
                diagnostics
                |> Array.forall (fun d -> d.Severity <> FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
            @>

        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>)

// =============================================================================
// `hasFcsErrors` must apply the same suppression filter (parseNowarnCodes plus
// FcsSuppressedCodes) that `Daemon.reportFcsDiagnostics` applies to the user-visible
// error stream. Without it the gate trips on codes the user has already silenced — e.g.
// FS1182 promoted to Error by `<TreatWarningsAsErrors>` alongside `#nowarn` — killing
// cache replay across daemon restarts on cold scans.
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors respects per-file #nowarn directives`` () =
    withTempDir "tp-poisoning-nowarn" (fun tmpDir ->
        let source =
            """#nowarn "1"
module Test
let x : int = "not-an-int"
"""

        let result =
            checkSourceForReal tmpDir "NoWarn.fsx" source
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // `#nowarn` does not suppress upstream FCS errors — FCS still reports FS0001 at
        // Severity = Error, and the gate's own suppression filter is what must drop it.
        let hasErrorDiagnostic =
            match result.CheckResults with
            | FullCheck cr ->
                cr.Diagnostics
                |> Array.exists (fun d ->
                    d.ErrorNumber = 1
                    && d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
            | ParseOnly -> false

        test <@ hasErrorDiagnostic @>

        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>)

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors respects configured FcsSuppressedCodes`` () =
    withTempDir "tp-poisoning-config" (fun tmpDir ->
        // No `#nowarn` in source: the caller passes the set instead, which is how daemons
        // silence cold-scan-only noise codes (`fcsSuppressedCodes` in DaemonConfig).
        let source =
            """module Test
let x : int = "not-an-int"
"""

        let result =
            checkSourceForReal tmpDir "Config.fsx" source
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        let hasErrorDiagnostic =
            match result.CheckResults with
            | FullCheck cr -> cr.Diagnostics |> Array.exists (fun d -> d.ErrorNumber = 1)
            | ParseOnly -> false

        test <@ hasErrorDiagnostic @>

        test
            <@
                not (
                    FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors
                        (Set.singleton 1)
                        result.Source
                        result.CheckResults
                )
            @>)

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors still trips on real error not covered by suppression`` () =
    // The positive control for the two suppression tests above: they would both pass
    // against a gate that had simply stopped firing.
    withTempDir "tp-poisoning-loadbearing" (fun tmpDir ->
        let source =
            """module Test
let x : int = "not-an-int"
"""

        let result =
            checkSourceForReal tmpDir "Real.fsx" source
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        test <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults @>

        // Suppressing an unrelated code must NOT mask the real FS0001.
        test
            <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors (Set.singleton 9999) result.Source result.CheckResults @>)

[<Fact(Timeout = 30000)>]
let ``FileChecked with FCS errors persists symbols to DB and stamps sidecar dirty`` () =
    // Dirty FCS results do NOT block the symbol-DB write. The protection against Phase B
    // seeing "0 stored" lives in the freshness sidecar, which marks the file
    // `fcsClean = false` so detectChanges bypasses the diff rather than computing a
    // phantom "all symbols changed" delta against an empty stored row set.
    withTempDir "tp-poisoning-persist-dirty" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let testConfigs =
            [ { Project = "Broken"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
        host.RegisterHandler(handler)

        let brokenSource =
            """module Broken
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let brokenTest () = ()

let badTypeUse : int = "not-an-int"
"""

        let brokenFile = Path.Combine(tmpDir, "Broken.fsx")
        File.WriteAllText(brokenFile, brokenSource)

        let projOptions =
            getScriptOptions checker brokenFile brokenSource |> Async.RunSynchronously

        pipeline.RegisterProject(brokenFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create brokenFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // Sanity: the result really is poisoned.
        test <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults @>

        host.EmitFileChecked(result)
        waitForPluginTerminal host "test-prune" 10.0

        emitBuildAndWaitTerminal host

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        let freshDb = Database.create dbPath
        let symbols = freshDb.GetSymbolsInFile "Broken.fsx"
        test <@ not symbols.IsEmpty @>

        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ not (FsHotWatch.TestPrune.FileFreshness.isClean "Broken.fsx" freshness) @>)

[<Fact(Timeout = 30000)>]
let ``FileChecked without FCS errors flushes symbols to DB (gate doesn't break clean path)`` () =
    withTempDir "tp-poisoning-cleanflush" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let testConfigs =
            [ { Project = "Clean"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
        host.RegisterHandler(handler)

        let cleanSource =
            """module Clean
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let cleanTest () = ()
"""

        let cleanFile = Path.Combine(tmpDir, "Clean.fsx")
        File.WriteAllText(cleanFile, cleanSource)

        let projOptions =
            getScriptOptions checker cleanFile cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(cleanFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create cleanFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>

        emitBuildAndWaitTerminal host

        emitFileAndQuiesce host result
        emitBatchAndQuiesce host [ cleanFile ]

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

        let mutable testMethods: TestMethodInfo list = []

        waitUntil
            (fun () ->
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                let freshDb = Database.create dbPath
                testMethods <- freshDb.GetTestMethodsInFile "Clean.fsx"
                testMethods.Length >= 1)
            5000

        test <@ testMethods.Length >= 1 @>
        test <@ testMethods |> List.exists (fun t -> t.TestMethod = "cleanTest") @>

        // A clean check arriving after BuildCompleted stamps `fcsClean = true`, so Phase B
        // detectChanges trusts the stored rows for this file.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean "Clean.fsx" freshness @>)

[<Fact(Timeout = 30000)>]
let ``BatchChecked persists accumulated symbols to DB without a follow-up BuildCompleted`` () =
    // On a cold scan `performScan` awaits BuildPlugin terminal BEFORE the FCS tier checks,
    // so BuildCompleted reaches the mailbox before any FileChecked and flushes an empty
    // PendingAnalysis. The N FileCheckeds then populate it, and BatchChecked is the only
    // remaining signal that can flush them — otherwise the symbol DB stays empty and every
    // subsequent cold scan perpetuates that.
    withTempDir "tp-batchchecked-flush" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        // No testConfigs, so BuildCompleted is unsubscribed and only FileChecked and
        // BatchChecked can drive the flush — the BatchChecked subscription is
        // unconditional.
        let handler = create dbPath tmpDir None None None None None []
        host.RegisterHandler(handler)

        let cleanSource =
            """module Clean
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let cleanTest () = ()
"""

        let cleanFile = Path.Combine(tmpDir, "Clean.fsx")
        File.WriteAllText(cleanFile, cleanSource)

        let projOptions =
            getScriptOptions checker cleanFile cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(cleanFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create cleanFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        host.EmitFileChecked(result)
        waitForPluginTerminal host "test-prune" 10.0

        // No BuildCompleted ever fires.
        emitBatchAndQuiesce host [ cleanFile ]

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

        let mutable testMethods: TestMethodInfo list = []

        waitUntil
            (fun () ->
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                let freshDb = Database.create dbPath
                testMethods <- freshDb.GetTestMethodsInFile "Clean.fsx"
                testMethods.Length >= 1)
            5000

        test <@ testMethods.Length >= 1 @>
        test <@ testMethods |> List.exists (fun t -> t.TestMethod = "cleanTest") @>)

[<Fact(Timeout = 60000)>]
let ``cold-boot regression: dirty FCS leaves sidecar dirty so detectChanges falls back`` () =
    // Dirty FCS may overwrite rows — persistence is unconditional — but the freshness
    // sidecar marks the file dirty so a later `detectChanges` against those
    // potentially-poisoned rows is bypassed. What must be prevented is the spurious large
    // diff (the 4921-affected-tests Phase B regression), and the sidecar, not the
    // symbol-DB write decision, is what prevents it.
    withTempDir "tp-poisoning-coldboot" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        let testConfigs =
            [ { Project = "CB"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        // Phase 1: clean check, flush populates DB.
        let host1 = PluginHost.create checker tmpDir
        let handler1 = create dbPath tmpDir (Some testConfigs) None None None None []
        host1.RegisterHandler(handler1)

        let cleanSource =
            """module CB
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let coldBootTest () = ()
"""

        let file = Path.Combine(tmpDir, "CB.fsx")
        File.WriteAllText(file, cleanSource)

        let projOptions =
            getScriptOptions checker file cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(file, projOptions)

        let cleanResult =
            pipeline.CheckFile(AbsFilePath.create file)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None (clean)")

        emitBuildAndWaitTerminal host1

        emitFileAndQuiesce host1 cleanResult
        emitBatchAndQuiesce host1 [ file ]

        let mutable phase1Tests: TestMethodInfo list = []

        waitUntil
            (fun () ->
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                let db = Database.create dbPath
                phase1Tests <- db.GetTestMethodsInFile "CB.fsx"
                phase1Tests.Length >= 1)
            5000

        test <@ phase1Tests.Length >= 1 @>

        // Phase 2: cold-boot poisoning — a fresh plugin instance reading the prior DB,
        // with the same file now carrying Error-severity diagnostics.
        let brokenSource =
            """module CB
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let coldBootTest () = ()

let badTypeUse : int = "wrong-type"
"""

        File.WriteAllText(file, brokenSource)

        let projOptionsBroken =
            getScriptOptions checker file brokenSource |> Async.RunSynchronously

        let pipeline2 = CheckPipeline(checker)
        pipeline2.RegisterProject(file, projOptionsBroken)

        let brokenResult =
            pipeline2.CheckFile(AbsFilePath.create file)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None (broken)")

        test
            <@
                FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors
                    Set.empty
                    brokenResult.Source
                    brokenResult.CheckResults
            @>

        let host2 = PluginHost.create checker tmpDir
        let handler2 = create dbPath tmpDir (Some testConfigs) None None None None []
        host2.RegisterHandler(handler2)

        emitBuildAndWaitTerminal host2

        emitFileAndQuiesce host2 brokenResult

        // `markUnverified` preserves a prior clean record: Phase 1's `fcsClean = true` is
        // NOT downgraded even though the current check has Error-severity diagnostics.
        // The trade-off is deliberate — cold-start reliability over precision on
        // user-broke-their-code transients — and the next genuine clean check refreshes
        // the timestamp.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean "CB.fsx" freshness @>

        // The sidecar still reads clean, but `currentClean` is false for this event, so
        // detectChanges is bypassed regardless of stored state and ChangedFiles gains no
        // phantom entry from the poisoned check.
        let changedAfterDirty =
            host2.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        test <@ changedAfterDirty.Value = "[]" @>)

// =============================================================================
// The per-file freshness sidecar gates the detectChanges call site, so cross-restart
// Phase B replay never DIFFS against rows that ended their last session FCS-dirty:
// those rows may be partial, and diffing a complete extraction against them reports a
// phantom "all symbols changed" delta — the 4921-affected-tests regression.
//
// AUTOMATION-526. "Never diff against them" was always right. "Therefore contribute
// nothing" was not, and that is what this gate used to do. A file whose last check hit
// a transient FCS error had its tests selected by NO impact-filtered run afterwards —
// silently, under a green check, because nothing distinguished "I cannot tell what
// changed in this file" from "nothing changed in this file". The stored rows are not a
// baseline; the CURRENT extraction, which the gate has already established is clean, is
// complete. So the answer is the one `Clean, NoRows` already gives: there is no before,
// and every symbol in the file is new.
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``Phase B replay: stored=dirty, current=clean → the file's tests are SELECTED, not dropped`` () =
    withTempDir "tp-phaseb-bypass" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let relPath = "PhaseB.fsx"
        let absPath = Path.Combine(tmpDir, relPath)

        // A dirty sidecar entry and no DB rows: the prior session ended dirty.
        let earlier = DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)

        let priorSidecar =
            Map.empty
            |> Map.add
                relPath
                { FsHotWatch.TestPrune.FileFreshness.FcsClean = false
                  FsHotWatch.TestPrune.FileFreshness.LastCleanCheckAt = Some earlier }

        FsHotWatch.TestPrune.FileFreshness.save tmpDir priorSidecar

        let cleanSource =
            """module PhaseB
type FactAttribute() = inherit System.Attribute()

let usefulValue = 42
let anotherValue = "hello"

[<Fact>]
let phaseBTest () = ()
"""

        File.WriteAllText(absPath, cleanSource)

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        let projOptions =
            getScriptOptions checker absPath cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(absPath, projOptions)

        let testConfigs =
            [ { Project = "PhaseB"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
        host.RegisterHandler(handler)

        let result =
            pipeline.CheckFile(AbsFilePath.create absPath)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // Sanity: this is a clean check.
        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>

        emitBuildAndWaitTerminal host

        emitFileAndQuiesce host result

        // AUTOMATION-526. This read `= "[]"` before the fix, and that empty list IS the
        // defect: the sidecar said dirty when the FileChecked arrived, so the file was
        // dropped from selection entirely on the very pass that recovered from the FCS
        // error. Nothing warned; the run was green.
        let changedFiles = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        // Exactly this one file. `Contains` alone would also pass a fix that escalated
        // to "everything changed" — the over-widening AUTOMATION-526's positive control
        // forbids — so the whole list is pinned: the widening is per FILE.
        test <@ changedFiles.Value = $"[\"%s{relPath}\"]" @>

        // The clean recheck flips the sidecar dirty → clean, so the NEXT restart's Phase B
        // trusts the rows — which is what bounds the widening to ONE pass per recovery
        // rather than one on every subsequent save.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean relPath freshness @>)

[<Fact(Timeout = 30000)>]
let ``Phase B replay: stored=clean → detectChanges runs as today`` () =
    // The guard against an over-aggressive gate that would mask legitimate changes.
    let initialSource = "module Lib\nlet x = 1\n"
    let astChangedSource = "module Lib\nlet x = 1\nlet y = 2\n"

    withSeededTestEnv "tp-phaseb-realdiff" "Lib.fs" initialSource (fun env ->
        File.WriteAllText(env.FilePath, astChangedSource)

        match
            env.Pipeline.CheckFile(AbsFilePath.create env.FilePath)
            |> Async.RunSynchronously
        with
        | None -> Assert.Fail("FCS failed on AST-changed source")
        | Some r -> env.Host.EmitFileChecked(r)

        waitForTerminalStatus env.Host "test-prune" 30000

        let changed = env.Host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
        test <@ changed.Value.Contains(env.RelPath) @>)

// =============================================================================
// BuildCompleted-gated stamping. `markClean` fires only for FileChecked events arriving
// AFTER a BuildCompleted in the current session; earlier ones stamp `markUnverified`,
// treated as dirty unless a prior clean record exists. This is what the fcs-clean
// predicate alone could not solve: by the time the pipeline emits BuildCompleted, FCS has
// been warmed by the build's reference-graph realization, so subsequent FileChecked
// events extract the same number of symbols a warm Phase B rerun would.
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``Item 3: pre-BuildCompleted clean FileChecked → sidecar stays dirty`` () =
    // `currentClean = true` is necessary but not sufficient; BuildCompleted is what
    // signals warm-enough state.
    withTempDir "tp-item3-pre-build-clean" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let testConfigs =
            [ { Project = "Pre"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
        host.RegisterHandler(handler)

        let cleanSource = "module Pre\nlet n = 1\n"
        let cleanFile = Path.Combine(tmpDir, "Pre.fsx")
        File.WriteAllText(cleanFile, cleanSource)

        let projOptions =
            getScriptOptions checker cleanFile cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(cleanFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create cleanFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>

        // Deliberately NOT emitBuildAndWaitTerminal first.
        host.EmitFileChecked(result)
        waitForPluginTerminal host "test-prune" 10.0

        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ not (FsHotWatch.TestPrune.FileFreshness.isClean "Pre.fsx" freshness) @>)

[<Fact(Timeout = 30000)>]
let ``Item 3: post-BuildCompleted clean FileChecked → sidecar stamped clean`` () =
    // Same harness as the pre-build case, with the ordering reversed.
    withTempDir "tp-item3-post-build-clean" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let testConfigs =
            [ { Project = "Post"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
        host.RegisterHandler(handler)

        let cleanSource = "module Post\nlet n = 1\n"
        let cleanFile = Path.Combine(tmpDir, "Post.fsx")
        File.WriteAllText(cleanFile, cleanSource)

        let projOptions =
            getScriptOptions checker cleanFile cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(cleanFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create cleanFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        emitBuildAndWaitTerminal host

        emitFileAndQuiesce host result

        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean "Post.fsx" freshness @>)

[<Fact(Timeout = 30000)>]
let ``Item 3: clean check after prior dirty, still pre-build → stays dirty`` () =
    // Two FileCheckeds and no BuildCompleted: dirty, then clean. The clean one must not
    // promote the entry, because warm extraction stability is still not guaranteed.
    withTempDir "tp-item3-dirty-then-clean" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let testConfigs =
            [ { Project = "Mixed"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
        host.RegisterHandler(handler)

        let mixedFile = Path.Combine(tmpDir, "Mixed.fsx")

        let dirtySource =
            """module Mixed
let bad : int = "not-an-int"
"""

        File.WriteAllText(mixedFile, dirtySource)

        let dirtyOpts =
            getScriptOptions checker mixedFile dirtySource |> Async.RunSynchronously

        pipeline.RegisterProject(mixedFile, dirtyOpts)

        let dirtyResult =
            pipeline.CheckFile(AbsFilePath.create mixedFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile None (dirty)")

        test
            <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty dirtyResult.Source dirtyResult.CheckResults @>

        emitFileAndQuiesce host dirtyResult

        // A fresh pipeline, so FCS reanalyzes against the new source.
        let cleanSource = "module Mixed\nlet n = 1\n"
        File.WriteAllText(mixedFile, cleanSource)

        let cleanOpts =
            getScriptOptions checker mixedFile cleanSource |> Async.RunSynchronously

        let pipeline2 = CheckPipeline(checker)
        pipeline2.RegisterProject(mixedFile, cleanOpts)

        let cleanResult =
            pipeline2.CheckFile(AbsFilePath.create mixedFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile None (clean)")

        test
            <@
                not (
                    FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors
                        Set.empty
                        cleanResult.Source
                        cleanResult.CheckResults
                )
            @>

        emitFileAndQuiesce host cleanResult

        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ not (FsHotWatch.TestPrune.FileFreshness.isClean "Mixed.fsx" freshness) @>)

// =============================================================================
// detectChanges call site: stored and current must agree on unit. The DB stores externs
// under the synthetic SourceFile "_extern" and `GetSymbolsInFile` filters by
// source_file = relPath, so the stored side is file-local only. Passing unfiltered
// `normalizedSymbols` (file-local + externs) on the current side produced a phantom diff
// equal to the file's extern count on every clean re-check — and externs are ~80% of a
// real file's allSymbols, hence "Phase B always reports 4921 affected tests".
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``detectChanges: re-check of unchanged source with externs reports no changes`` () =
    // `List.length` makes the extractor pull in Microsoft.FSharp.Collections.List.length
    // as an extern symbol.
    let source = "module Lib\nlet xs = List.length []\n"

    withSeededTestEnv "tp-extern-filter" "Lib.fsx" source (fun env ->
        // Both controls: the extracted set contains externs, and the DB read-back does
        // not. Without them the test passes tautologically.
        let externs = env.SeededSymbols |> List.filter (fun s -> s.IsExtern)
        test <@ not externs.IsEmpty @>

        let storedFromDb = env.Db.GetSymbolsInFile(env.RelPath)
        test <@ storedFromDb |> List.forall (fun s -> not s.IsExtern) @>

        // Re-check the IDENTICAL source, no edit.
        match
            env.Pipeline.CheckFile(AbsFilePath.create env.FilePath)
            |> Async.RunSynchronously
        with
        | None -> Assert.Fail("FCS failed on re-check")
        | Some r -> env.Host.EmitFileChecked(r)

        waitForTerminalStatus env.Host "test-prune" 30000

        let changedFiles =
            env.Host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        test <@ changedFiles.Value = "[]" @>)

// =============================================================================
// A cold-start missing apphost must NOT be reported as a FAILED test. `dotnet run
// --no-build` launched before the build plugin produced the apphost fails with "An error
// occurred trying to start process … No such file or directory", which
// `looksLikeApphostMissing` distinguishes from a genuine non-zero test exit.
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``looksLikeApphostMissing detects the start-process launch failure`` () =
    let output =
        "Unhandled exception: System.ComponentModel.Win32Exception (2): An error occurred trying to start process '/repo/tests/Unit/bin/Debug/net10.0/Unit' with working directory '/repo'. No such file or directory"

    test <@ looksLikeApphostMissing output @>

[<Fact(Timeout = 15000)>]
let ``looksLikeApphostMissing is false for a genuine test failure`` () =
    // Misclassifying a real failure as apphost-missing would SILENCE reds — the opposite,
    // and worse, failure mode.
    let output =
        "failed FsHotWatch.Tests.FooTests.bar (3ms)\nTest run summary: Failed!\n  total: 10\n  failed: 1\n  succeeded: 9"

    test <@ not (looksLikeApphostMissing output) @>

[<Fact(Timeout = 15000)>]
let ``looksLikeApphostMissing is false for empty / passing output`` () =
    test <@ not (looksLikeApphostMissing "") @>
    test <@ not (looksLikeApphostMissing "Test run summary: Passed!\n  total: 5\n  succeeded: 5") @>

// Structural apphost detection: `tryApphostPresent` derives the binary path from the
// runner's `--project` arg and File.Exists-checks it, rather than sniffing localized OS
// error text.

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent returns None when args carry no --project`` () =
    // Not derivable, so the caller falls back to the output sniff.
    test <@ tryApphostPresent "/tmp/runner.sh" "/repo" = None @>
    test <@ tryApphostPresent "test" "/repo" = None @>

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent reports false when the bin dir is absent`` () =
    withTempDir "tp-apphost-struct-missing" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        Directory.CreateDirectory(projDir) |> ignore
        test <@ tryApphostPresent $"run --project {projDir} --no-build --" tmpDir = Some false @>)

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent reports false when bin exists but apphost is missing`` () =
    withTempDir "tp-apphost-struct-empty-bin" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        // The DLL landed; the apphost did not.
        File.WriteAllText(Path.Combine(tfmDir, "Unit.dll"), "")
        test <@ tryApphostPresent $"run --project {projDir} --no-build --" tmpDir = Some false @>)

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent reports true when the apphost binary exists`` () =
    withTempDir "tp-apphost-struct-present" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        // The apphost is the extension-less sibling of the canonical DLL.
        File.WriteAllText(Path.Combine(tfmDir, "Unit"), "")
        test <@ tryApphostPresent $"run --project {projDir} --no-build --" tmpDir = Some(true) @>)

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent resolves an fsproj --project to its assembly name`` () =
    withTempDir "tp-apphost-struct-fsproj" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        let fsproj = Path.Combine(projDir, "MyTests.fsproj")
        File.WriteAllText(fsproj, "<Project/>")
        // Apphost name follows the project file base name, not the dir leaf.
        File.WriteAllText(Path.Combine(tfmDir, "MyTests"), "")
        test <@ tryApphostPresent $"run --project {fsproj} --no-build --" tmpDir = Some(true) @>)

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent finds a Windows .exe apphost`` () =
    withTempDir "tp-apphost-struct-exe" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        File.WriteAllText(Path.Combine(tfmDir, "Unit.exe"), "")
        test <@ tryApphostPresent $"run --project {projDir} --no-build --" tmpDir = Some(true) @>)

// `transient` = the apphost-missing failure clears on retry (the cold-start race);
// otherwise it persists every run. The configs run a bare `sh <script>` with no
// `--project`, so `tryApphostPresent` returns None and the plugin falls back to the
// `looksLikeApphostMissing` output sniff — exercising that path end-to-end.
[<Theory(Timeout = 20000)>]
[<InlineData(true)>] // cold-start: fails once with the launch signature, then succeeds
[<InlineData(false)>] // persistent: apphost never appears
let ``apphost-missing cold-start retries green; persistent defers non-green (never FAILED test)`` (transient: bool) =
    withTempDir "tp-apphost" (fun tmpDir ->
        let scriptPath = Path.Combine(tmpDir, "runner.sh")

        // The .NET host's start-process signature with no test-summary block. The retry
        // counter lives in a file under the working dir (repoRoot = tmpDir) to avoid
        // nested shell quoting through the F# arg string.
        let launchFailure =
            "echo \"Unhandled exception: An error occurred trying to start process '/x/bin/Debug/net10.0/Unit' with working directory '/x'. No such file or directory\" 1>&2"

        let script =
            if transient then
                // First run: emit the failure and exit 1. Retry: exit 0.
                "n=$(cat attempts 2>/dev/null || echo 0)\n"
                + "n=$((n+1))\n"
                + "echo $n > attempts\n"
                + "if [ \"$n\" -le 1 ]; then\n"
                + "  "
                + launchFailure
                + "\n"
                + "  exit 1\n"
                + "else\n"
                + "  echo ok\n"
                + "  exit 0\n"
                + "fi\n"
            else
                // Apphost never appears — fails identically every run.
                launchFailure + "\n" + "exit 1\n"

        File.WriteAllText(scriptPath, script)

        let configs =
            [ { Project = "Unit"
                Command = "sh"
                Args = scriptPath
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 15.0

        // In neither case may an apphost-missing launch be a test FAILED: it is an
        // ordering bug, never a real red.
        let failingReasons =
            host.GetErrorsByPlugin("test-prune")
            |> Map.toList
            |> List.collect snd
            |> List.filter (fun e -> e.Severity = FsHotWatch.ErrorLedger.Error)

        test
            <@
                failingReasons
                |> List.forall (fun e -> not (e.Message.ToLowerInvariant().Contains("tests failed")))
            @>

        if transient then
            test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

            match host.GetStatus("test-prune") with
            | Some(Failed _) -> Assert.Fail("transient apphost-missing was reported as FAILED")
            | _ -> ()
        else
            // A persistently-missing apphost means the tests NEVER RAN: deferred, which is
            // NON-GREEN (a CI check must not silent-green it) but not a failure. The
            // diagnostic is `Deferred` severity, which the verdict routes to
            // Incomplete/exit 2, and the status is a non-failing terminal. Older code
            // returned TestsPassed here — a false green.
            test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

            let allEntries =
                host.GetErrorsByPlugin("test-prune") |> Map.toList |> List.collect snd

            let waitingDiagnostic =
                allEntries
                |> List.exists (fun e ->
                    e.Severity = FsHotWatch.ErrorLedger.Deferred
                    && e.Message.ToLowerInvariant().Contains("waiting on build"))

            test <@ waitingDiagnostic @>
            test <@ allEntries |> List.forall (fun e -> e.Severity <> FsHotWatch.ErrorLedger.Error) @>

            match host.GetStatus("test-prune") with
            | Some(Completed(_, v)) -> test <@ v.Summary.ToLowerInvariant().Contains("waiting on build") @>
            | other ->
                Assert.Fail($"expected a non-failing Completed status for a pure deferred project, got %A{other}"))

// =============================================================================
// Freshness gate. `tryApphostPresent`/`detectApphostMissing` only fire on a FAILED launch
// (post-exit), so a PRESENT-but-STALE apphost that exits 0 reported a false GREEN:
// `--no-build` ran OLD bits and "passed". This gate runs PRE-launch, independent of exit
// code — build output that predates its inputs defers as "waiting on build" exactly like
// a missing apphost. Mirrors BuildPlugin.verifyArtifactsFresh (ADR-008).
//
// WHAT it compares is the hard part. Comparing the test DLL against the newest source
// ANYWHERE IN THE REPO condemns every project outside an edit's dependency closure, and
// the accusation cannot be cleared: an incremental `dotnet build` is correctly a no-op for
// an unaffected project, so its DLL never catches the repo-wide watermark and only
// `-t:Rebuild` escapes. Looking at `.fs`/`.cs` alone also misses a changed test FIXTURE
// copied in from a shared project — the run reads the OLD copy out of `bin/` and passes
// (`dsa-scope-4.json`, 2026-07-14: a fake green that left main red for hours).
//
// Both directions are pinned below — an out-of-closure edit is FRESH, an in-closure one
// STALE — and content items are judged by the COPY the run would actually read.
// =============================================================================

// `ArtifactFreshness.Cache` is documented "each project is walked at most ONCE per run"
// and "thread-safe: test groups run in parallel". Both were true separately and neither
// implied the other: `ConcurrentDictionary.GetOrAdd(key, valueFactory)` is thread-SAFE
// (one result is published) without being once-ONLY — it may invoke the factory on
// several threads for one key and discard the losers' work. Test groups do run in
// parallel with heavily-overlapping ProjectReference closures, so the directory walks and
// `XDocument.Load` parses the memo exists to eliminate could still each happen N times.
//
// RED-BEFORE-GREEN: implement `OnceMemo.GetOrAdd` as `entries.GetOrAdd(key, factory)` and
// this counts 16 factory runs, not 1.

[<Fact(Timeout = 30000)>]
let ``OnceMemo runs the value factory exactly ONCE per key under concurrent access`` () =
    let memo = ArtifactFreshness.OnceMemo<string, int>()
    let mutable factoryRuns = 0
    let entrants = 16
    use released = new Barrier(entrants)

    let factory (_: string) =
        Interlocked.Increment(&factoryRuns) |> ignore
        // The real factories walk directories and parse XML. A slow factory widens the
        // window in which a second caller finds the key still absent — precisely the
        // window a plain `GetOrAdd` leaves open.
        Thread.Sleep 100
        42

    let results = Array.zeroCreate<int> entrants

    let threads =
        [| for i in 0 .. entrants - 1 ->
               Thread(fun () ->
                   released.SignalAndWait()
                   results[i] <- memo.GetOrAdd("the-one-key", factory)) |]

    for t in threads do
        t.Start()

    for t in threads do
        t.Join()

    test <@ results |> Array.forall (fun r -> r = 42) @>
    // Not "once was published" — once was RUN.
    test <@ factoryRuns = 1 @>

[<Fact(Timeout = 30000)>]
let ``OnceMemo runs the value factory once per DISTINCT key`` () =
    let memo = ArtifactFreshness.OnceMemo<string, string>()
    let mutable factoryRuns = 0

    let factory (k: string) =
        Interlocked.Increment(&factoryRuns) |> ignore
        k + "!"

    test <@ memo.GetOrAdd("a", factory) = "a!" @>
    test <@ memo.GetOrAdd("b", factory) = "b!" @>
    test <@ memo.GetOrAdd("a", factory) = "a!" @>
    test <@ factoryRuns = 2 @>

/// Derive the target from the runner args exactly as `executeTests` does, then ask the
/// gate. A fresh `Cache` per call, since the memo is per-run in production.
let private staleOf (args: string) (repoRoot: string) : ArtifactFreshness.StaleInput option =
    deriveProjectBin args repoRoot
    |> Option.bind (ArtifactFreshness.stale (ArtifactFreshness.Cache()))

/// A synthetic repo mirroring the real MSBuild output layout:
///
///   Leaf/     — an unrelated project, referenced by nobody: out of closure.
///   Common/   — a library with a content fixture, referenced by Tests.
///   Tests/    — the test project, whose output dir holds COPIES of Common's DLL and of
///               Common's fixture.
///
/// Copies carry their ORIGIN's mtime, because that is what MSBuild's `File.Copy` leaves
/// behind — the property the gate's copy check rests on. Everything is "built" at
/// `builtAt` and sources are older; each test then moves ONE mtime.
type private Synth =
    { Root: string
      TestsDir: string
      TestsSrc: string
      TestsDll: string
      TestsOutDir: string
      CommonSrc: string
      CommonFixture: string
      CommonDll: string
      CommonDllCopy: string
      FixtureCopy: string
      LeafSrc: string
      BuiltAt: DateTime }

let private synth (root: string) : Synth =
    let builtAt = DateTime.UtcNow.AddHours(-1.0)
    let sourcedAt = builtAt.AddMinutes(-10.0)

    let leafDir = p [ root; "Leaf" ]
    let commonDir = p [ root; "Common" ]
    let testsDir = p [ root; "Tests" ]
    let commonOut = p [ commonDir; "bin"; "Debug"; "net10.0" ]
    let testsOut = p [ testsDir; "bin"; "Debug"; "net10.0" ]

    writeAt (p [ leafDir; "Leaf.fsproj" ]) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (p [ leafDir; "Leaf.fs" ]) "module Leaf" sourcedAt

    writeAt (p [ commonDir; "Common.fsproj" ]) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (p [ commonDir; "Common.fs" ]) "module Common" sourcedAt
    writeAt (p [ commonDir; "Fixtures"; "data.json" ]) "{ \"leaves\": 36 }" sourcedAt
    writeAt (p [ commonOut; "Common.dll" ]) "" builtAt
    writeAt (p [ commonOut; "Fixtures"; "data.json" ]) "{ \"leaves\": 36 }" sourcedAt

    writeAt
        (p [ testsDir; "Tests.fsproj" ])
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"../Common/Common.fsproj\" />\n  </ItemGroup>\n</Project>"
        sourcedAt

    writeAt (p [ testsDir; "Tests.fs" ]) "module Tests" sourcedAt
    writeAt (p [ testsOut; "Tests" ]) "" builtAt // apphost
    writeAt (p [ testsOut; "Tests.dll" ]) "" builtAt
    writeAt (p [ testsOut; "Common.dll" ]) "" builtAt // copy: same mtime as origin
    writeAt (p [ testsOut; "Fixtures"; "data.json" ]) "{ \"leaves\": 36 }" sourcedAt // copy: origin's mtime

    { Root = root
      TestsDir = testsDir
      TestsSrc = p [ testsDir; "Tests.fs" ]
      TestsDll = p [ testsOut; "Tests.dll" ]
      TestsOutDir = testsOut
      CommonSrc = p [ commonDir; "Common.fs" ]
      CommonFixture = p [ commonDir; "Fixtures"; "data.json" ]
      CommonDll = p [ commonOut; "Common.dll" ]
      CommonDllCopy = p [ testsOut; "Common.dll" ]
      FixtureCopy = p [ testsOut; "Fixtures"; "data.json" ]
      LeafSrc = p [ leafDir; "Leaf.fs" ]
      BuiltAt = builtAt }

/// The gate, asked about the synthetic repo's Tests project.
let private synthStale (s: Synth) =
    let fsproj = Path.Combine(s.TestsDir, "Tests.fsproj")
    staleOf $"run --project {fsproj} --no-build --" s.Root

[<Fact(Timeout = 15000)>]
let ``freshness is None when args carry no --project`` () =
    test <@ staleOf "/tmp/runner.sh" "/repo" = None @>
    test <@ staleOf "test" "/repo" = None @>

[<Fact(Timeout = 15000)>]
let ``freshness is None when no build output exists`` () =
    withTempDir "tp-stale-nobin" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        Directory.CreateDirectory(projDir) |> ignore
        File.WriteAllText(Path.Combine(projDir, "Foo.fs"), "module Foo")
        // Absence is tryApphostPresent's job, not staleness'.
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

[<Fact(Timeout = 15000)>]
let ``freshness is None when there are no sources to be stale against`` () =
    withTempDir "tp-stale-nosrc" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        File.WriteAllText(Path.Combine(tfmDir, "Unit.dll"), "")
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

[<Fact(Timeout = 15000)>]
let ``freshness is None when the DLL is newer than every source`` () =
    withTempDir "tp-stale-fresh" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        let src = Path.Combine(projDir, "Foo.fs")
        File.WriteAllText(src, "module Foo")
        let dll = Path.Combine(tfmDir, "Unit.dll")
        File.WriteAllText(dll, "")
        let t = DateTime.UtcNow
        File.SetLastWriteTimeUtc(src, t.AddMinutes(-10.0))
        File.SetLastWriteTimeUtc(dll, t)
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

[<Fact(Timeout = 15000)>]
let ``freshness is STALE when the project's own DLL predates its own source`` () =
    withTempDir "tp-stale-own" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        let dll = Path.Combine(tfmDir, "Unit.dll")
        File.WriteAllText(dll, "")
        let src = Path.Combine(projDir, "Tests.fs")
        File.WriteAllText(src, "module Tests")
        let t = DateTime.UtcNow
        File.SetLastWriteTimeUtc(dll, t.AddMinutes(-10.0))
        File.SetLastWriteTimeUtc(src, t)

        match staleOf $"run --project {projDir} --no-build --" tmpDir with
        | Some(ArtifactFreshness.AssemblyOlderThanSource(_, source, _, _)) -> test <@ source = src @>
        | other -> Assert.Fail($"expected AssemblyOlderThanSource naming {src}, got %A{other}"))

// AUTOMATION-122, direction 1 — the false positive, verbatim: a build tool was edited and
// an integration suite that does not reference it was condemned as stale. MSBuild rightly
// refuses to relink that suite, so no plain build could ever clear the accusation. On a
// repo-wide watermark this test FAILS — Leaf.fs is the newest source in the repo and
// Tests.dll predates it.
[<Fact(Timeout = 15000)>]
let ``an edit OUTSIDE the test project's closure leaves it FRESH`` () =
    withTempDir "tp-stale-outside" (fun tmpDir ->
        let s = synth tmpDir

        // Leaf is referenced by nobody, so this is now the newest source in the repo and
        // still irrelevant to this test binary.
        File.SetLastWriteTimeUtc(s.LeafSrc, s.BuiltAt.AddMinutes(30.0))

        test <@ synthStale s = None @>)

/// AUTOMATION-528, direction A — the dangerous one, and the one no other case can see.
///
/// `dotnet restore` rewrites `obj/project.assets.json`; only a BUILD regenerates
/// `bin/<tfm>/<Asm>.deps.json` from it. When a restore moves on without a build — which
/// is exactly what the deps-freshness gate's automatic recovery does — the manifest left
/// behind lists a superseded reference closure. The compile is repaired and the LOAD is
/// not: the host resolves assemblies through the manifest, not through the directory, so
/// a dependency sitting in the output folder and missing from the manifest is a
/// `FileNotFoundException` on the routes that touch it. Green build, red run, specific
/// routes only — indistinguishable from an application bug.
///
/// Reproduced against a real SDK before this test was written: with the manifest of an
/// earlier build restored over a fully-built tree, `dotnet App.dll` died with
/// `Could not load file or assembly 'Lib'` while `Lib.dll` sat beside it in the output
/// folder, every assembly was newer than every source, and every copy was byte-identical
/// to its origin.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-528: a deps manifest older than its restore is STALE though every assembly is current`` () =
    withTempDir "tp-a528-superseded" (fun tmpDir ->
        let projDir = p [ tmpDir; "Unit" ]
        let tfmDir = p [ projDir; "bin"; "Debug"; "net10.0" ]
        let expectedAssets = p [ projDir; "obj"; "project.assets.json" ]
        let expectedManifest = p [ tfmDir; "Unit.deps.json" ]
        let now = DateTime.UtcNow

        // A fully-built, otherwise impeccable tree: the assembly postdates its source and
        // there is nothing copied in to differ from an origin.
        writeAt (p [ projDir; "Foo.fs" ]) "module Foo" (now.AddMinutes(-30.0))
        writeAt (p [ tfmDir; "Unit.dll" ]) "" (now.AddMinutes(-10.0))
        writeAt expectedManifest "{}" (now.AddMinutes(-10.0))
        // ... and a restore that moved on after that build, without one following it.
        writeAt expectedAssets "{}" now

        match staleOf $"run --project {projDir} --no-build --" tmpDir with
        | Some(ArtifactFreshness.DepsManifestOlderThanRestore(project, assets, manifest, _, _)) ->
            test <@ project = "Unit" @>
            test <@ assets = expectedAssets @>
            test <@ manifest = expectedManifest @>
        | other -> Assert.Fail($"expected DepsManifestOlderThanRestore, got %A{other}"))

/// POSITIVE CONTROL, required: the ordinary shape a build leaves — restore first, manifest
/// after it — must NOT report staleness, and neither must one written inside the same tick
/// (a coarse filesystem, or a build fast enough that both land on one timestamp). A
/// detector that fired on every built tree would refuse every run and teach people to
/// ignore it.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-528: a deps manifest at or after its restore is fresh`` () =
    withTempDir "tp-a528-control" (fun tmpDir ->
        let projDir = p [ tmpDir; "Unit" ]
        let tfmDir = p [ projDir; "bin"; "Debug"; "net10.0" ]
        let assets = p [ projDir; "obj"; "project.assets.json" ]
        let manifest = p [ tfmDir; "Unit.deps.json" ]
        let now = DateTime.UtcNow

        writeAt (p [ projDir; "Foo.fs" ]) "module Foo" (now.AddMinutes(-30.0))
        writeAt (p [ tfmDir; "Unit.dll" ]) "" (now.AddMinutes(-10.0))
        writeAt assets "{}" (now.AddMinutes(-10.0))

        // Generated after the restore it came from: the normal case.
        writeAt manifest "{}" (now.AddMinutes(-9.0))
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>

        // Same tick: still the normal case, never stale.
        writeAt manifest "{}" (now.AddMinutes(-10.0))
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

/// Either half of the pair missing means there is nothing to compare, and absence is never
/// staleness in this module. BOTH directions are pinned: a project with no restore output
/// (an old-style project, or one whose `obj/` was cleaned), and one that generates no
/// runtime manifest at all (an ordinary library).
///
/// Pinned because every OTHER freshness test in this file builds a tree with no `obj/`, so
/// a check that read a missing assets file as stale would redden all of them at once and
/// the cause would present as a mass regression rather than as this one decision.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-528: neither half of the pair missing is judged as stale`` () =
    withTempDir "tp-a528-absent" (fun tmpDir ->
        let projDir = p [ tmpDir; "Unit" ]
        let tfmDir = p [ projDir; "bin"; "Debug"; "net10.0" ]
        let assets = p [ projDir; "obj"; "project.assets.json" ]
        let manifest = p [ tfmDir; "Unit.deps.json" ]
        let now = DateTime.UtcNow

        writeAt (p [ projDir; "Foo.fs" ]) "module Foo" (now.AddMinutes(-30.0))
        writeAt (p [ tfmDir; "Unit.dll" ]) "" (now.AddMinutes(-10.0))

        // A manifest with no restore beside it.
        writeAt manifest "{}" (now.AddMinutes(-10.0))
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>

        // ... and a restore, newer than everything, with no manifest generated from it.
        File.Delete manifest
        writeAt assets "{}" now
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

// Direction 2 — the real hole. A dependency's source newer than the dependency's own
// assembly means the build has not run since the edit, so the DLL in the test project's
// output dir is old code and `--no-build` must not run.
[<Fact(Timeout = 15000)>]
let ``an edit to a DEPENDENCY inside the closure is STALE`` () =
    withTempDir "tp-stale-inside" (fun tmpDir ->
        let s = synth tmpDir

        File.SetLastWriteTimeUtc(s.CommonSrc, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some(ArtifactFreshness.AssemblyOlderThanSource(project, source, _, _)) ->
            test <@ project = "Common" @>
            test <@ source = s.CommonSrc @>
        | other -> Assert.Fail($"expected the dependency edit to be STALE, got %A{other}"))

// The same edit once the build HAS run: this is what proves a plain `dotnet build`, not
// `-t:Rebuild`, clears the gate. The test project's own DLL is deliberately NOT restamped
// — a private-only change to a dependency need not relink its consumers (reference
// assemblies exist to avoid exactly that), and demanding it would be the same
// unanswerable accusation in a smaller costume.
[<Fact(Timeout = 15000)>]
let ``a dependency edit followed by a plain rebuild of that dependency is FRESH`` () =
    withTempDir "tp-stale-rebuilt" (fun tmpDir ->
        let s = synth tmpDir
        let editedAt = s.BuiltAt.AddMinutes(30.0)
        let rebuiltAt = s.BuiltAt.AddMinutes(31.0)

        File.SetLastWriteTimeUtc(s.CommonSrc, editedAt)
        // The build relinks Common and re-copies it into the consumer's output
        // (File.Copy preserves the origin's mtime — hence the same stamp).
        File.SetLastWriteTimeUtc(s.CommonDll, rebuiltAt)
        File.SetLastWriteTimeUtc(s.CommonDllCopy, rebuiltAt)

        test <@ synthStale s = None @>)

// The dependency's DLL is fresh in its OWN bin, but the copy the test run would load was
// never refreshed. A rebuild emits new BYTES — moving the origin's mtime alone would
// assert the old `copy < origin` rule rather than the real event.
[<Fact(Timeout = 15000)>]
let ``a dependency DLL rebuilt but not re-copied into the test output is STALE`` () =
    withTempDir "tp-stale-depcopy" (fun tmpDir ->
        let s = synth tmpDir
        File.WriteAllText(s.CommonDll, "rebuilt bits")
        File.SetLastWriteTimeUtc(s.CommonDll, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some(ArtifactFreshness.CopyDiffersFromOrigin(origin, copy)) ->
            test <@ origin = s.CommonDll @>
            test <@ copy = s.CommonDllCopy @>
        | other -> Assert.Fail($"expected the un-refreshed dependency copy to be STALE, got %A{other}"))

// AUTOMATION-122, second half — CONTENT FILES, which let a red main through. A shared
// fixture changed (36 → 40 leaf facts); the consuming test project's output dir still
// held the OLD copy, so the `--no-build` run read the old fixture and PASSED. Only
// `-t:Rebuild` exposed it. A stale copy of a content item must make the run stale exactly
// as a stale apphost does.

[<Fact(Timeout = 15000)>]
let ``a FIXTURE edited but not re-copied into the test output is STALE`` () =
    withTempDir "tp-stale-fixture" (fun tmpDir ->
        let s = synth tmpDir

        // The fixture changes and the copy in the test project's output dir still holds
        // the OLD bytes. Every compiled artifact is untouched, so the apphost/DLL checks
        // alone see nothing wrong and the tests would run green against 36 leaves.
        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some(ArtifactFreshness.CopyDiffersFromOrigin(origin, copy)) ->
            test <@ origin = s.CommonFixture @>
            test <@ copy = s.FixtureCopy @>
        | other -> Assert.Fail($"expected the un-copied fixture to be STALE, got %A{other}"))

// What a plain `dotnet build` does: re-copy the fixture, carrying the origin's mtime
// (verified against real MSBuild, 2026-07-14).
[<Fact(Timeout = 15000)>]
let ``a FIXTURE re-copied by a plain build is FRESH`` () =
    withTempDir "tp-stale-fixture-copied" (fun tmpDir ->
        let s = synth tmpDir
        let editedAt = s.BuiltAt.AddMinutes(30.0)

        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, editedAt)
        File.WriteAllText(s.FixtureCopy, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.FixtureCopy, editedAt) // File.Copy preserves mtime

        test <@ synthStale s = None @>)

// The test project's OWN fixtures count too — the copy is what the run reads,
// whoever owns the origin.
[<Fact(Timeout = 15000)>]
let ``the test project's OWN stale fixture copy is STALE`` () =
    withTempDir "tp-stale-own-fixture" (fun tmpDir ->
        let s = synth tmpDir
        let ownFixture = Path.Combine(s.TestsDir, "Fixtures", "own.json")
        let ownCopy = Path.Combine(s.TestsOutDir, "Fixtures", "own.json")
        writeAt ownCopy "{ \"v\": 1 }" s.BuiltAt
        writeAt ownFixture "{ \"v\": 2 }" (s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some(ArtifactFreshness.CopyDiffersFromOrigin(origin, copy)) ->
            test <@ origin = ownFixture @>
            test <@ copy = ownCopy @>
        | other -> Assert.Fail($"expected the test project's own stale fixture to be STALE, got %A{other}"))

// SHADOWING. Two projects in one closure can hold a file at the SAME relative path —
// `xunit.runner.json` sits in five projects of the repo this gate was fixed against.
// MSBuild copies both to one destination, last writer wins, so exactly one survives.
// Judging the survivor against only ONE claimant condemns the other for being shadowed,
// and no build can answer that. A CONTENT comparison would make that accusation PERMANENT
// where an mtime one only fired when the shadowed file happened to be newer — so a copy
// is checked against every claimant and is current if it matches ANY of them.
[<Fact(Timeout = 15000)>]
let ``a fixture SHADOWED by another project's file at the same path is not stale`` () =
    withTempDir "tp-stale-shadowed" (fun tmpDir ->
        let s = synth tmpDir
        let editedAt = s.BuiltAt.AddMinutes(30.0)

        // Tests has its OWN Fixtures/data.json — same relative path as Common's, different
        // bytes — and its copy is the one that survives in the output dir. Common's
        // fixture is now shadowed: its bytes appear nowhere in the output, and a build
        // would change nothing.
        let testsFixture = p [ s.TestsDir; "Fixtures"; "data.json" ]
        writeAt testsFixture "{ \"leaves\": 99 }" editedAt
        writeAt s.FixtureCopy "{ \"leaves\": 99 }" editedAt

        test <@ synthStale s = None @>

        // Shadowing is not a licence to stop checking.
        File.WriteAllText(s.FixtureCopy, "{ \"leaves\": 0 }")

        match synthStale s with
        | Some(ArtifactFreshness.CopyDiffersFromOrigin(_, copy)) -> test <@ copy = s.FixtureCopy @>
        | other -> Assert.Fail($"a copy matching no claimant must still be STALE, got %A{other}"))

// The other half of "keyed on the copy": a file the build does NOT copy has no
// destination in the output dir, so editing it can never fire the gate. Otherwise the
// content check becomes a new wolf-cry, every README and .fsproj edit condemning a
// project no build would ever clear.
[<Fact(Timeout = 15000)>]
let ``a file the build never copies cannot make the run stale`` () =
    withTempDir "tp-stale-uncopied" (fun tmpDir ->
        let s = synth tmpDir
        // Newer than everything, copied nowhere.
        writeAt (Path.Combine(tmpDir, "Common", "README.md")) "# notes" (s.BuiltAt.AddMinutes(30.0))

        test <@ synthStale s = None @>)

// A guard that cries "something, somewhere is stale" is a guard people learn to bypass.
[<Fact(Timeout = 15000)>]
let ``the stale reason names the offending file`` () =
    withTempDir "tp-stale-describe" (fun tmpDir ->
        let s = synth tmpDir
        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some stale ->
            let described = ArtifactFreshness.describe stale
            test <@ described.Contains "data.json" @>
        | None -> Assert.Fail "expected a stale verdict")

// =============================================================================
// FAIL CLOSED. A gate that answers "up to date" because it COULD NOT LOOK is the original
// bug reborn inside its own fix. If the closure cannot be determined — an unparseable
// project file, a `ProjectReference` resolving to nothing — the run is REFUSED and the
// build, which will choke on the same file loudly, reports the real error. Swallowing
// these into "no references" would shrink the closure to nothing and let a stale
// dependency sail through as fresh.
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``an unparseable project file is REFUSED, not called fresh`` () =
    withTempDir "tp-stale-badxml" (fun tmpDir ->
        let s = synth tmpDir
        File.WriteAllText(p [ s.TestsDir; "Tests.fsproj" ], "<Project><ItemGroup><ProjectReference </Project>")

        match synthStale s with
        | Some(ArtifactFreshness.InputsUndeterminable(project, _)) -> test <@ project = "Tests" @>
        | other -> Assert.Fail($"an unreadable project file must fail CLOSED, got %A{other}"))

[<Fact(Timeout = 15000)>]
let ``a ProjectReference without an Include is REFUSED, not ignored`` () =
    withTempDir "tp-stale-noinclude" (fun tmpDir ->
        let s = synth tmpDir

        File.WriteAllText(
            p [ s.TestsDir; "Tests.fsproj" ],
            "<Project>\n  <ItemGroup>\n    <ProjectReference />\n  </ItemGroup>\n</Project>"
        )

        match synthStale s with
        | Some(ArtifactFreshness.InputsUndeterminable _) -> ()
        | other -> Assert.Fail($"an unresolvable reference must fail CLOSED, got %A{other}"))

// The same ignorance: we cannot know a missing project's sources, so we cannot certify
// this run.
[<Fact(Timeout = 15000)>]
let ``a ProjectReference to a missing project is REFUSED, not called fresh`` () =
    withTempDir "tp-stale-missingref" (fun tmpDir ->
        let s = synth tmpDir

        File.WriteAllText(
            p [ s.TestsDir; "Tests.fsproj" ],
            "<Project>\n  <ItemGroup>\n    <ProjectReference Include=\"../Ghost/Ghost.fsproj\" />\n  </ItemGroup>\n</Project>"
        )

        match synthStale s with
        | Some(ArtifactFreshness.InputsUndeterminable(_, reason)) -> test <@ reason.Contains "Ghost" @>
        | other -> Assert.Fail($"a reference to a missing project must fail CLOSED, got %A{other}"))

// Ignorance ANYWHERE in the closure fails closed, not just at its root.
[<Fact(Timeout = 15000)>]
let ``an unparseable project file DEEP in the closure is REFUSED`` () =
    withTempDir "tp-stale-badxml-deep" (fun tmpDir ->
        let s = synth tmpDir
        File.WriteAllText(p [ tmpDir; "Common"; "Common.fsproj" ], "<Project> <<< not xml")

        match synthStale s with
        | Some(ArtifactFreshness.InputsUndeterminable _) -> ()
        | other -> Assert.Fail($"an unreadable DEPENDENCY project file must fail CLOSED, got %A{other}"))

// AUTOMATION-164. The closure-parse channel was fail-closed from the start; the WALK
// channel was not. `SafeWalk` returned `[||]` for a directory it could not enumerate,
// so an unreadable source dir produced no sources, nothing was newer than the assembly,
// and the gate certified a `--no-build` run over bits it had never looked at. The walk
// now reports its holes and the gate refuses on them.
[<Fact(Timeout = 15000)>]
let ``an UNREADABLE SOURCE DIRECTORY in the closure is REFUSED, not called fresh`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "tp-stale-unreadable-src" (fun tmpDir ->
            let s = synth tmpDir

            // POSITIVE CONTROL: this exact tree is FRESH while it is readable. Without
            // it, a gate that answered InputsUndeterminable for everything would pass.
            test <@ synthStale s = None @>

            let sealed' = p [ tmpDir; "Common"; "Internal" ]
            Directory.CreateDirectory sealed' |> ignore
            File.WriteAllText(p [ sealed'; "Deep.fs" ], "module Deep")
            File.SetUnixFileMode(sealed', UnixFileMode.None)

            try
                match synthStale s with
                | Some(ArtifactFreshness.InputsUndeterminable(project, reason)) ->
                    test <@ project = "Common" @>
                    test <@ reason.Contains "Internal" @>
                | other -> Assert.Fail($"an unreadable source directory must fail CLOSED, got %A{other}")
            finally
                File.SetUnixFileMode(
                    sealed',
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

// The same hole on the OTHER side of the gate. `outputs` is read as "did the build put
// something here?", and a copy the walk never saw is a destination nobody checks — so a
// stale copy under an unreadable output subdirectory used to sail through as fresh.
[<Fact(Timeout = 15000)>]
let ``an UNREADABLE OUTPUT DIRECTORY is REFUSED, not called fresh`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "tp-stale-unreadable-out" (fun tmpDir ->
            let s = synth tmpDir
            test <@ synthStale s = None @>

            let sealed' = p [ s.TestsOutDir; "runtimes" ]
            Directory.CreateDirectory sealed' |> ignore
            File.WriteAllText(p [ sealed'; "native.dylib" ], "")
            File.SetUnixFileMode(sealed', UnixFileMode.None)

            try
                match synthStale s with
                | Some(ArtifactFreshness.InputsUndeterminable(project, reason)) ->
                    test <@ project = "Tests" @>
                    test <@ reason.Contains "runtimes" @>
                | other -> Assert.Fail($"an unreadable output directory must fail CLOSED, got %A{other}")
            finally
                File.SetUnixFileMode(
                    sealed',
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

// A reference cycle is MSBuild's error to report; the closure walk must terminate rather
// than hang on it.
[<Fact(Timeout = 15000)>]
let ``a project reference cycle terminates`` () =
    withTempDir "tp-stale-cycle" (fun tmpDir ->
        let s = synth tmpDir
        // Common references Tests, which already references Common.
        File.WriteAllText(
            p [ tmpDir; "Common"; "Common.fsproj" ],
            "<Project>\n  <ItemGroup>\n    <ProjectReference Include=\"../Tests/Tests.fsproj\" />\n  </ItemGroup>\n</Project>"
        )

        test <@ synthStale s = None @>

        File.SetLastWriteTimeUtc(s.CommonSrc, s.BuiltAt.AddMinutes(30.0))
        test <@ (synthStale s).IsSome @>)

// A dependency never built at all is the build's business, not staleness': there is no
// out-of-date artifact to refuse, and a build in flight may still land it. Same for a
// dependency assembly not yet copied into the test output.
[<Fact(Timeout = 15000)>]
let ``an unbuilt dependency is not reported stale`` () =
    withTempDir "tp-stale-unbuilt-dep" (fun tmpDir ->
        let s = synth tmpDir
        Directory.Delete(p [ tmpDir; "Common"; "bin" ], true)

        test <@ synthStale s = None @>)

[<Fact(Timeout = 15000)>]
let ``a dependency assembly not yet copied into the test output is not reported stale`` () =
    withTempDir "tp-stale-uncopied-dep" (fun tmpDir ->
        let s = synth tmpDir
        File.Delete s.CommonDllCopy

        test <@ synthStale s = None @>)

// =============================================================================
// AUTOMATION-169 — the same wolf-cry through a different door: not the wrong project this
// time, the wrong TARGET FRAMEWORK.
//
// A multi-targeted dependency (netstandard2.0/net8.0/net9.0/net10.0) consumed by a
// net10.0 test project. MSBuild copies the net10.0 output, but the gate resolved the
// ORIGIN to whichever TFM output was NEWEST — net8.0, built nine minutes later — so it
// compared a net10.0 copy against a net8.0 origin, found it "older", and condemned it.
// Every consumer's copy was byte-identical to net10.0's digest, and a plain `dotnet build`
// could not answer the accusation because a correct rebuild re-copies net10.0 and it
// re-fires. 4 of 6 test projects refused to run.
//
// An mtime comparison ACROSS TFMs is meaningless by construction; these tests pin that it
// is no longer expressible.
// =============================================================================

/// A dependency multi-targeting net8.0 and net10.0, consumed by a net10.0 test project.
/// The two TFM outputs carry DIFFERENT BYTES (real per-TFM builds differ at minimum in
/// `TargetFrameworkAttribute`) and DIFFERENT MTIMES, with net8.0 the NEWER: exactly the
/// shape that makes "newest output dir wins" pick the framework nobody consumes.
type private MultiTfm =
    { Root: string
      TestsDir: string
      DepNet8Dll: string
      DepNet10Dll: string
      DepDllCopy: string
      BuiltAt: DateTime }

let private multiTfmSynth (root: string) : MultiTfm =
    let builtAt = DateTime.UtcNow.AddHours(-1.0)
    let sourcedAt = builtAt.AddMinutes(-10.0)

    let depDir = p [ root; "Dep" ]
    let testsDir = p [ root; "Tests" ]
    let depNet8 = p [ depDir; "bin"; "Debug"; "net8.0" ]
    let depNet10 = p [ depDir; "bin"; "Debug"; "net10.0" ]
    let testsOut = p [ testsDir; "bin"; "Debug"; "net10.0" ]

    writeAt (p [ depDir; "Dep.fsproj" ]) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (p [ depDir; "Dep.fs" ]) "module Dep" sourcedAt

    // Different bytes, and net8.0 built nine minutes later than net10.0 — so net8.0 is the
    // newest, and a "newest wins" gate resolves the origin to it.
    writeAt (p [ depNet10; "Dep.dll" ]) "net10.0 bits" builtAt
    writeAt (p [ depNet8; "Dep.dll" ]) "net8.0 bits" (builtAt.AddMinutes(9.0))

    writeAt
        (p [ testsDir; "Tests.fsproj" ])
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"../Dep/Dep.fsproj\" />\n  </ItemGroup>\n</Project>"
        sourcedAt

    writeAt (p [ testsDir; "Tests.fs" ]) "module Tests" sourcedAt
    writeAt (p [ testsOut; "Tests" ]) "" builtAt // apphost
    writeAt (p [ testsOut; "Tests.dll" ]) "" builtAt
    // The copy: MSBuild took the net10.0 output — the TFM this consumer targets —
    // preserving its mtime. It is perfect and current.
    writeAt (p [ testsOut; "Dep.dll" ]) "net10.0 bits" builtAt

    { Root = root
      TestsDir = testsDir
      DepNet8Dll = p [ depNet8; "Dep.dll" ]
      DepNet10Dll = p [ depNet10; "Dep.dll" ]
      DepDllCopy = p [ testsOut; "Dep.dll" ]
      BuiltAt = builtAt }

let private multiTfmStale (m: MultiTfm) =
    let fsproj = Path.Combine(m.TestsDir, "Tests.fsproj")
    staleOf $"run --project {fsproj} --no-build --" m.Root

// Nothing is stale: the copy is byte-identical to the net10.0 output it came from. A
// newest-mtime gate calls it nine minutes "older" than net8.0 and cries stale.
[<Fact(Timeout = 15000)>]
let ``a copy of a multi-TFM dependency is FRESH even when a SIBLING TFM is newer`` () =
    withTempDir "tp-stale-tfm-sibling" (fun tmpDir ->
        let m = multiTfmSynth tmpDir

        test <@ multiTfmStale m = None @>)

// The converse, and why this is not a weakening: a copy whose bytes match NO current
// output of its origin is still caught. Its MTIME here EQUALS its origin's, so the
// `copy < origin` rule calls it fresh and runs the stale bits — this is the jj/git
// working-copy restamp, the coarse-timestamp filesystem, and "rebuilt within the same
// second", all at once.
[<Fact(Timeout = 15000)>]
let ``a copy whose bytes match NO output of its origin is STALE, even at an equal mtime`` () =
    withTempDir "tp-stale-tfm-realstale" (fun tmpDir ->
        let m = multiTfmSynth tmpDir

        // Both TFM outputs carry new bytes; the copy was never refreshed and holds the old
        // bits. Every mtime is identical, so no mtime comparison can see it.
        File.WriteAllText(m.DepNet10Dll, "net10.0 bits v2")
        File.WriteAllText(m.DepNet8Dll, "net8.0 bits v2")
        File.SetLastWriteTimeUtc(m.DepNet10Dll, m.BuiltAt)
        File.SetLastWriteTimeUtc(m.DepNet8Dll, m.BuiltAt)
        File.SetLastWriteTimeUtc(m.DepDllCopy, m.BuiltAt)

        match multiTfmStale m with
        | Some(ArtifactFreshness.CopyDiffersFromOrigin(origin, copy)) ->
            test <@ origin = m.DepNet10Dll @> // named for the TFM the consumer consumes
            test <@ copy = m.DepDllCopy @>
        | other -> Assert.Fail($"a copy holding OLD bytes must be STALE whatever the mtimes say, got %A{other}"))

// Fail closed at the copy check too: a file we cannot read is not one we may certify, so
// `ContentHash` hands back `UnhashableContent` and that is `InputsUndeterminable`, never
// "fresh". An exclusive lock is how a real build in flight holds a file mid-write.
[<Fact(Timeout = 15000)>]
let ``an UNREADABLE copy is REFUSED, not called fresh`` () =
    withTempDir "tp-stale-unreadable-copy" (fun tmpDir ->
        let m = multiTfmSynth tmpDir

        use _lock =
            new FileStream(m.DepDllCopy, FileMode.Open, FileAccess.Read, FileShare.None)

        match multiTfmStale m with
        | Some(ArtifactFreshness.InputsUndeterminable _) -> ()
        | other -> Assert.Fail($"an unreadable copy must fail CLOSED, got %A{other}"))

// The subtler side. The copy reads fine but an ORIGIN it must be checked against does
// not, so "it matches none of them" is a conclusion we did not earn: an unhashable origin
// cannot match anything, and calling that a MISMATCH manufactures a stale verdict out of
// a permissions error.
[<Fact(Timeout = 15000)>]
let ``an UNREADABLE origin is REFUSED, not called stale`` () =
    withTempDir "tp-stale-unreadable-origin" (fun tmpDir ->
        let m = multiTfmSynth tmpDir

        // No readable candidate matches: net8.0 holds different bytes by construction, and
        // net10.0 — the one it does match — cannot be read.
        use _lock =
            new FileStream(m.DepNet10Dll, FileMode.Open, FileAccess.Read, FileShare.None)

        match multiTfmStale m with
        | Some(ArtifactFreshness.InputsUndeterminable(_, reason)) -> test <@ reason.Contains "net10.0" @>
        | other -> Assert.Fail($"an unreadable ORIGIN must fail CLOSED, not read as stale, got %A{other}"))

// A multi-targeted project is stale only when EVERY per-TFM output dir is: which TFM
// `dotnet run` selects is not knowable here, so one fresh output dir means there is a
// fresh way to run.
[<Fact(Timeout = 15000)>]
let ``a multi-TFM project with one FRESH output dir is not stale`` () =
    withTempDir "tp-stale-multitfm" (fun tmpDir ->
        let s = synth tmpDir
        let editedAt = s.BuiltAt.AddMinutes(30.0)

        // net10.0's copy still holds the OLD bytes, so it is stale ...
        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, editedAt)
        test <@ (synthStale s).IsSome @>

        // ... but a second TFM's output dir carries the up-to-date copy.
        let net9 = p [ s.TestsDir; "bin"; "Debug"; "net9.0" ]
        writeAt (p [ net9; "Tests.dll" ]) "" s.BuiltAt
        writeAt (p [ net9; "Common.dll" ]) "" s.BuiltAt
        writeAt (p [ net9; "Fixtures"; "data.json" ]) "{ \"leaves\": 40 }" editedAt

        // A dependency TFM dir holding no assembly — only one of its frameworks was built
        // — is not a candidate, and must not be mistaken for a missing build.
        Directory.CreateDirectory(p [ tmpDir; "Common"; "bin"; "Debug"; "net9.0" ])
        |> ignore

        test <@ synthStale s = None @>)

// A partial or interrupted build leaves behind a TFM output dir of the TEST PROJECT with
// no assembly in it. Judging it would mean walking an empty output tree, finding no copy
// of anything, and — since every project in the closure then contributes no finding —
// quietly calling the run fresh on the strength of a directory containing nothing.
[<Fact(Timeout = 15000)>]
let ``a TFM dir of the test project holding no assembly is not a candidate`` () =
    withTempDir "tp-stale-empty-tfmdir" (fun tmpDir ->
        let s = synth tmpDir

        Directory.CreateDirectory(p [ s.TestsDir; "bin"; "Debug"; "net9.0" ]) |> ignore

        test <@ synthStale s = None @>

        // The real output dir still decides: break it and the gate must fire rather than
        // be placated by the empty sibling.
        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, s.BuiltAt.AddMinutes(30.0))
        test <@ (synthStale s).IsSome @>)

// The freshness walk must TERMINATE through symlink cycles. Production trigger:
// `.devenv/profile` links into /nix/store, where ncurses-6.6-dev/include holds two
// self-loop symlinks (`ncurses -> .`, `ncursesw -> .`) — branching factor 2 per level,
// bounded only by ENAMETOOLONG, so ~2^90 paths. A symlink-following walk wedged every
// `fshw check` forever (observed 8h36m, silent) and trips this test's Timeout.
[<Fact(Timeout = 15000)>]
let ``freshness terminates despite self-loop symlink cycles`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "tp-nsm-cycle" (fun tmpDir ->
            let s = synth tmpDir

            // Two self-loops in one directory: the /nix/store shape exactly.
            let cycleDir = Path.Combine(s.TestsDir, "cycle")
            Directory.CreateDirectory cycleDir |> ignore
            Directory.CreateSymbolicLink(Path.Combine(cycleDir, "loop"), ".") |> ignore
            Directory.CreateSymbolicLink(Path.Combine(cycleDir, "loop2"), ".") |> ignore

            test <@ synthStale s = None @>)

// The same wedge, other half: a symlinked directory is a portal OUT of the tree
// (`.devenv/profile` → the nix store). Freshness is computed from the REAL tree only — a
// newer file behind a symlinked dir is not an input to this project, and following it is
// how the walk left the repo in the first place.
[<Fact(Timeout = 15000)>]
let ``freshness does not follow a symlinked directory out of the project`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "tp-nsm-outside" (fun tmpDir ->
            let s = synth tmpDir
            let outside = Path.Combine(tmpDir, "outside")
            Directory.CreateDirectory outside |> ignore
            writeAt (Path.Combine(outside, "Newer.fs")) "module Newer" (s.BuiltAt.AddMinutes(30.0))

            Directory.CreateSymbolicLink(Path.Combine(s.TestsDir, "portal"), outside)
            |> ignore

            test <@ synthStale s = None @>)

// `.devenv`/`.direnv` are excluded by NAME as well: even a regular, non-symlinked file
// under them must not count as an input.
[<Fact(Timeout = 15000)>]
let ``freshness ignores sources under .devenv and .direnv`` () =
    withTempDir "tp-nsm-devenv" (fun tmpDir ->
        let s = synth tmpDir
        writeAt (Path.Combine(s.TestsDir, ".devenv", "gen", "Tool.fs")) "module Tool" (s.BuiltAt.AddMinutes(30.0))

        test <@ synthStale s = None @>)

// The gate end-to-end through the plugin. Without it the runner exits 0, yielding
// TestsPassed and a false green on stale bits.
[<Fact(Timeout = 20000)>]
let ``a present-but-stale apphost defers as 'waiting on build' instead of passing on stale bits`` () =
    withTempDir "tp-stale-defer" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore

        // Apphost and canonical DLL present, so the missing-apphost path does NOT fire ...
        File.WriteAllText(Path.Combine(tfmDir, "Unit"), "")
        let dll = Path.Combine(tfmDir, "Unit.dll")
        File.WriteAllText(dll, "")

        // ... but a source was edited after the build.
        let src = Path.Combine(projDir, "Tests.fs")
        File.WriteAllText(src, "module Tests")
        let now = DateTime.UtcNow
        File.SetLastWriteTimeUtc(dll, now.AddMinutes(-10.0))
        File.SetLastWriteTimeUtc(src, now)

        // The runner exits 0 — a "pass" on stale bits — if it is ever launched. `--project`
        // makes the project derivable, so the gate engages pre-launch.
        let configs =
            [ { Project = "Unit"
                Command = "sh"
                Args = $"-c \"exit 0\" --project {projDir} --no-build --"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 15.0

        let allEntries =
            host.GetErrorsByPlugin("test-prune") |> Map.toList |> List.collect snd

        // A defer is `Deferred` severity, which the verdict routes to Incomplete/exit 2 —
        // not a failing `Error` — so it does not register as a failing reason.
        test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

        let waitingDiagnostic =
            allEntries
            |> List.exists (fun e ->
                e.Severity = FsHotWatch.ErrorLedger.Deferred
                && e.Message.ToLowerInvariant().Contains("waiting on build"))

        test <@ waitingDiagnostic @>

        // A non-failing terminal whose summary still says "waiting on build" — never
        // `Failed`, never a silent green.
        match host.GetStatus("test-prune") with
        | Some(Completed(_, v)) -> test <@ v.Summary.ToLowerInvariant().Contains("waiting on build") @>
        | other -> Assert.Fail($"expected a non-failing Completed status for the stale-artifact defer, got %A{other}")

        test <@ allEntries |> List.forall (fun e -> e.Severity <> FsHotWatch.ErrorLedger.Error) @>

        test
            <@
                allEntries
                |> List.forall (fun e -> not (e.Message.ToLowerInvariant().Contains("tests failed")))
            @>)

[<Fact(Timeout = 20000)>]
let ``stale failures from a prior cycle are cleared when the next cycle supersedes them`` () =
    // Cycle 1 reds ProjA; cycle 2 passes ProjA and reds ProjB, so only ProjB may remain.
    // The Custom(TestsFinished) handler used to clear only on the all-pass branch, so
    // `fshw errors` showed a stale red the fresh cycle had already disproved.
    //
    // Driven via `run-tests` rather than BuildCompleted so each cycle deterministically
    // re-runs: the impact path would skip a warm cycle with no changed symbols.
    withTempDir "tp-stale-clear" (fun tmpDir ->
        let flagA = Path.Combine(tmpDir, "failA")
        let flagB = Path.Combine(tmpDir, "failB")

        let mk (proj: string) (flag: string) =
            { Project = proj
              Command = "sh"
              Args = $"-c \"if [ -f {flag} ]; then exit 1; else exit 0; fi\""
              Group = "default"
              Environment = []
              FilterTemplate = None
              ClassJoin = " "
              TimeoutSec = None
              ReportVerificationFormat = AutoDetect }

        let configs = [ mk "ProjA" flagA; mk "ProjB" flagB ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let ledgerFiles () =
            host.GetErrorsByPlugin("test-prune") |> Map.toList |> List.map fst

        let hasFileFor (substr: string) () =
            ledgerFiles () |> List.exists (fun f -> f.Contains(substr))

        // `run-tests` runs executeTests synchronously then posts TestsFinished, so the
        // ledger lags the call — wait for it.
        File.WriteAllText(flagA, "")
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        waitUntil (hasFileFor "ProjA") 12000

        let cycle1Files = ledgerFiles ()
        test <@ cycle1Files |> List.exists (fun f -> f.Contains("ProjA")) @>
        test <@ not (cycle1Files |> List.exists (fun f -> f.Contains("ProjB"))) @>

        // The ProjB red only appears after this cycle's clear-then-report has run.
        File.Delete(flagA)
        File.WriteAllText(flagB, "")
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        waitUntil (hasFileFor "ProjB") 12000

        let cycle2Files = ledgerFiles ()

        test <@ cycle2Files |> List.exists (fun f -> f.Contains("ProjB")) @>
        test <@ not (cycle2Files |> List.exists (fun f -> f.Contains("ProjA"))) @>)
