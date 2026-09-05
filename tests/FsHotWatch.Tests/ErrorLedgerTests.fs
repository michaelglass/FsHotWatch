[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.ErrorLedgerTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Tests.TestHelpers

let private entry msg sev line = { errorEntry msg sev with Line = line }

/// Every `DiagnosticSeverity` case, enumerated BY REFLECTION rather than by hand.
/// A hand-written list is the exact thing AUTOMATION-294 found already broken: a case
/// added later was simply absent from it, so a test that claimed to walk "every
/// severity" had quietly stopped doing so. Reflection makes the list impossible to
/// forget to update.
let private allSeverities () =
    Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<DiagnosticSeverity>)
    |> Array.map (fun c -> Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(c, [||]) :?> DiagnosticSeverity)
    |> Array.toList

/// The trap: wrapping the agent's mailbox loop in `with ex -> log; loop state`
/// swallows programming bugs silently. Unhandled exceptions must reach the
/// MailboxProcessor `Error` event, published as `AgentCrashed`. The fault is
/// injected through the internal `RaiseFaultForTest` seam because production
/// messages have no natural failure mode inside a typed match.
[<Fact(Timeout = 5000)>]
let ``F12: programming-bug exception in agent loop surfaces via AgentCrashed instead of being swallowed`` () =
    let ledger = ErrorLedger()

    let crashed =
        TaskCompletionSource<exn>(TaskCreationOptions.RunContinuationsAsynchronously)

    use _ = ledger.AgentCrashed.Subscribe(fun ex -> crashed.TrySetResult(ex) |> ignore)

    let bug = InvalidOperationException("simulated programming bug inside agent loop")
    ledger.RaiseFaultForTest(bug)

    let observed = crashed.Task.Wait(TimeSpan.FromSeconds(2.0))
    test <@ observed @>
    test <@ obj.ReferenceEquals(crashed.Task.Result, bug) @>

[<Fact(Timeout = 15000)>]
let ``Report adds errors and GetAll returns them grouped by file`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "bad" DiagnosticSeverity.Warning 1 ])
    let all = ledger.GetAll()
    test <@ all.ContainsKey "/src/A.fs" @>
    test <@ all.["/src/A.fs"].Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``Clear removes errors for plugin and file`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "bad" DiagnosticSeverity.Warning 1 ])
    ledger.Clear("lint", "/src/A.fs")
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 15000)>]
let ``Report with empty list clears errors`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "bad" DiagnosticSeverity.Warning 1 ])
    ledger.Report("lint", "/src/A.fs", [])
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 15000)>]
let ``GetByPlugin filters to specific plugin`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "lint-warn" DiagnosticSeverity.Warning 1 ])
    ledger.Report("analyzers", "/src/A.fs", [ entry "analyzer-err" DiagnosticSeverity.Error 2 ])
    let lintOnly = ledger.GetByPlugin("lint")
    test <@ lintOnly.Count = 1 @>
    test <@ lintOnly.["/src/A.fs"].[0].Message = "lint-warn" @>

[<Fact(Timeout = 15000)>]
let ``Multiple plugins for same file accumulate independently`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "lint" DiagnosticSeverity.Warning 1 ])
    ledger.Report("analyzers", "/src/A.fs", [ entry "analyze" DiagnosticSeverity.Error 2 ])
    let all = ledger.GetAll()
    test <@ all.["/src/A.fs"].Length = 2 @>
    ledger.Clear("lint", "/src/A.fs")
    let all2 = ledger.GetAll()
    test <@ all2.["/src/A.fs"].Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``ClearPlugin removes all errors for a plugin`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "a" DiagnosticSeverity.Warning 1 ])
    ledger.Report("lint", "/src/B.fs", [ entry "b" DiagnosticSeverity.Warning 2 ])
    ledger.Report("analyzers", "/src/A.fs", [ entry "c" DiagnosticSeverity.Error 3 ])
    ledger.ClearPlugin("lint")
    test <@ ledger.GetAll() |> Map.values |> Seq.sumBy List.length = 1 @>
    test <@ ledger.GetByPlugin("lint").IsEmpty @>

[<Fact(Timeout = 15000)>]
let ``Count returns total across all plugins and files`` () =
    let ledger = ErrorLedger()

    ledger.Report(
        "lint",
        "/src/A.fs",
        [ entry "a" DiagnosticSeverity.Warning 1
          entry "b" DiagnosticSeverity.Warning 2 ]
    )

    ledger.Report("analyzers", "/src/A.fs", [ entry "c" DiagnosticSeverity.Error 3 ])
    test <@ ledger.GetAll() |> Map.values |> Seq.sumBy List.length = 3 @>

[<Fact(Timeout = 15000)>]
let ``Report ignores stale version`` () =
    let ledger = ErrorLedger()

    let newEntry =
        { Message = "new"
          Severity = DiagnosticSeverity.Error
          Line = 1
          Column = 0
          Detail = None }

    let staleEntry =
        { Message = "stale"
          Severity = DiagnosticSeverity.Error
          Line = 2
          Column = 0
          Detail = None }

    ledger.Report("fcs", "/tmp/Lib.fs", [ newEntry ], version = 2L)
    ledger.Report("fcs", "/tmp/Lib.fs", [ staleEntry ], version = 1L)

    let errors = ledger.GetAll()
    let fileErrors = errors |> Map.tryFind "/tmp/Lib.fs" |> Option.defaultValue []
    test <@ fileErrors.Length = 1 @>
    test <@ (snd fileErrors.[0]).Message = "new" @>

[<Fact(Timeout = 15000)>]
let ``Clear ignores stale version`` () =
    let ledger = ErrorLedger()

    let e =
        { Message = "error"
          Severity = DiagnosticSeverity.Error
          Line = 1
          Column = 0
          Detail = None }

    ledger.Report("fcs", "/tmp/Lib.fs", [ e ], version = 2L)
    ledger.Clear("fcs", "/tmp/Lib.fs", version = 1L)

    test <@ ledger.HasFailingReasons(warningsAreFailures = true) @>

[<Fact(Timeout = 15000)>]
let ``Report without version always updates`` () =
    let ledger = ErrorLedger()

    let entry1 =
        { Message = "first"
          Severity = DiagnosticSeverity.Error
          Line = 1
          Column = 0
          Detail = None }

    let entry2 =
        { Message = "second"
          Severity = DiagnosticSeverity.Error
          Line = 2
          Column = 0
          Detail = None }

    ledger.Report("fcs", "/tmp/Lib.fs", [ entry1 ], version = 5L)
    ledger.Report("fcs", "/tmp/Lib.fs", [ entry2 ])

    let errors = ledger.GetAll()
    let fileErrors = errors |> Map.tryFind "/tmp/Lib.fs" |> Option.defaultValue []
    test <@ (snd fileErrors.[0]).Message = "second" @>

[<Fact(Timeout = 15000)>]
let ``Report accepts newer version after initial versioned report`` () =
    let ledger = ErrorLedger()

    let entry1 =
        { Message = "first"
          Severity = DiagnosticSeverity.Error
          Line = 1
          Column = 0
          Detail = None }

    let entry2 =
        { Message = "updated"
          Severity = DiagnosticSeverity.Error
          Line = 2
          Column = 0
          Detail = None }

    ledger.Report("fcs", "/tmp/Lib.fs", [ entry1 ], version = 1L)
    ledger.Report("fcs", "/tmp/Lib.fs", [ entry2 ], version = 2L)

    let errors = ledger.GetAll()
    let fileErrors = errors |> Map.tryFind "/tmp/Lib.fs" |> Option.defaultValue []
    test <@ fileErrors.Length = 1 @>
    test <@ (snd fileErrors.[0]).Message = "updated" @>

[<Fact(Timeout = 15000)>]
let ``Report accepts equal version as update`` () =
    let ledger = ErrorLedger()

    let entry1 =
        { Message = "first"
          Severity = DiagnosticSeverity.Error
          Line = 1
          Column = 0
          Detail = None }

    let entry2 =
        { Message = "same-version-update"
          Severity = DiagnosticSeverity.Error
          Line = 2
          Column = 0
          Detail = None }

    ledger.Report("fcs", "/tmp/Lib.fs", [ entry1 ], version = 3L)
    // The guard is v >= last, not v > last: an equal version is still an update.
    ledger.Report("fcs", "/tmp/Lib.fs", [ entry2 ], version = 3L)

    let errors = ledger.GetAll()
    let fileErrors = errors |> Map.tryFind "/tmp/Lib.fs" |> Option.defaultValue []
    test <@ fileErrors.Length = 1 @>
    test <@ (snd fileErrors.[0]).Message = "same-version-update" @>

[<Fact(Timeout = 15000)>]
let ``Clear accepts newer version after initial versioned report`` () =
    let ledger = ErrorLedger()

    let entry1 =
        { Message = "error"
          Severity = DiagnosticSeverity.Error
          Line = 1
          Column = 0
          Detail = None }

    ledger.Report("fcs", "/tmp/Lib.fs", [ entry1 ], version = 1L)
    ledger.Clear("fcs", "/tmp/Lib.fs", version = 2L)
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 15000)>]
let ``FailingReasons returns only errors when warningsAreFailures is false`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "warn" DiagnosticSeverity.Warning 1 ])
    ledger.Report("fcs", "/src/A.fs", [ entry "err" DiagnosticSeverity.Error 2 ])
    ledger.Report("lint", "/src/B.fs", [ entry "info" DiagnosticSeverity.Info 3 ])
    let failing = ledger.FailingReasons(warningsAreFailures = false)
    test <@ failing.Count = 1 @>
    test <@ failing.ContainsKey "/src/A.fs" @>
    test <@ failing.["/src/A.fs"].Length = 1 @>
    test <@ (snd failing.["/src/A.fs"].[0]).Message = "err" @>

[<Fact(Timeout = 15000)>]
let ``FailingReasons returns errors and warnings when warningsAreFailures is true`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "warn" DiagnosticSeverity.Warning 1 ])
    ledger.Report("fcs", "/src/A.fs", [ entry "err" DiagnosticSeverity.Error 2 ])
    ledger.Report("lint", "/src/B.fs", [ entry "info" DiagnosticSeverity.Info 3 ])
    let failing = ledger.FailingReasons(warningsAreFailures = true)
    test <@ failing.Count = 1 @>
    test <@ failing.ContainsKey "/src/A.fs" @>
    test <@ failing.["/src/A.fs"].Length = 2 @>

[<Fact(Timeout = 15000)>]
let ``HasFailingReasons returns false when only info and hint entries exist`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "info" DiagnosticSeverity.Info 1 ])
    ledger.Report("fcs", "/src/B.fs", [ entry "hint" DiagnosticSeverity.Hint 2 ])
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = false)) @>
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 15000)>]
let ``HasFailingReasons with warningsAreFailures false ignores warnings`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "warn" DiagnosticSeverity.Warning 1 ])
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = false)) @>
    test <@ ledger.HasFailingReasons(warningsAreFailures = true) @>

[<Fact(Timeout = 15000)>]
let ``FailingReasons returns empty map when no failing entries`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "info" DiagnosticSeverity.Info 1 ])
    let failing = ledger.FailingReasons(warningsAreFailures = true)
    test <@ failing.IsEmpty @>

[<Fact(Timeout = 15000)>]
let ``ErrorLedger notifies reporters on Report`` () =
    let mutable reported: (string * string * ErrorEntry list) list = []

    let reporter =
        { new IErrorReporter with
            member _.Report plugin file entries =
                reported <- (plugin, file, entries) :: reported

            member _.Clear _ _ = ()
            member _.ClearPlugin _ = ()
            member _.ClearAll() = () }

    let ledger = ErrorLedger([ reporter ])
    ledger.Report("lint", "/src/A.fs", [ entry "bad" DiagnosticSeverity.Warning 1 ])
    ledger.GetAll() |> ignore // sync barrier: ensures all prior Posts have been processed
    test <@ reported.Length = 1 @>
    test <@ let (p, f, _) = reported.[0] in p = "lint" && f = "/src/A.fs" @>

[<Fact(Timeout = 15000)>]
let ``ErrorLedger notifies reporters on Clear`` () =
    let mutable cleared: (string * string) list = []

    let reporter =
        { new IErrorReporter with
            member _.Report _ _ _ = ()
            member _.Clear plugin file = cleared <- (plugin, file) :: cleared
            member _.ClearPlugin _ = ()
            member _.ClearAll() = () }

    let ledger = ErrorLedger([ reporter ])
    ledger.Report("lint", "/src/A.fs", [ entry "bad" DiagnosticSeverity.Warning 1 ])
    ledger.Clear("lint", "/src/A.fs")
    ledger.GetAll() |> ignore
    test <@ cleared.Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``ErrorLedger notifies reporters on ClearPlugin`` () =
    let mutable clearedPlugins: string list = []

    let reporter =
        { new IErrorReporter with
            member _.Report _ _ _ = ()
            member _.Clear _ _ = ()

            member _.ClearPlugin plugin =
                clearedPlugins <- plugin :: clearedPlugins

            member _.ClearAll() = () }

    let ledger = ErrorLedger([ reporter ])
    ledger.Report("lint", "/src/A.fs", [ entry "a" DiagnosticSeverity.Warning 1 ])
    ledger.ClearPlugin("lint")
    ledger.GetAll() |> ignore
    test <@ clearedPlugins = [ "lint" ] @>

[<Fact(Timeout = 15000)>]
let ``ErrorLedger does not notify reporters on stale version`` () =
    let mutable reportCount = 0

    let reporter =
        { new IErrorReporter with
            member _.Report _ _ _ = reportCount <- reportCount + 1
            member _.Clear _ _ = ()
            member _.ClearPlugin _ = ()
            member _.ClearAll() = () }

    let ledger = ErrorLedger([ reporter ])
    ledger.Report("fcs", "/tmp/Lib.fs", [ entry "new" DiagnosticSeverity.Error 1 ], version = 2L)
    ledger.Report("fcs", "/tmp/Lib.fs", [ entry "stale" DiagnosticSeverity.Error 1 ], version = 1L)
    ledger.GetAll() |> ignore
    test <@ reportCount = 1 @>

[<Fact(Timeout = 15000)>]
let ``ErrorLedger logs reporter exception with stack trace (F11)`` () =
    // IErrorReporter is a third-party-shaped boundary, so the broad catch is
    // justified — but logging only `ex.Message` strips the stack trace exactly when
    // a misbehaving reporter needs debugging. The log must carry the exception type
    // and ToString() text, so the reporter is debuggable from logs alone.
    //
    // The ledger emits this on its MailboxProcessor thread, so a `Console.SetError`
    // capture raced any concurrent redirect in the suite and lost the line. Assert on
    // an injected log sink instead: no process-global state, no race.
    let logged = System.Collections.Concurrent.ConcurrentQueue<string * string>()

    let throwingReporter =
        { new IErrorReporter with
            member _.Report _ _ _ =
                raise (System.InvalidOperationException("reporter boom"))

            member _.Clear _ _ = ()
            member _.ClearPlugin _ = ()
            member _.ClearAll() = () }

    let ledger =
        ErrorLedger([ throwingReporter ], logError = (fun tag msg -> logged.Enqueue(tag, msg)))

    ledger.Report("lint", "/src/A.fs", [ entry "bad" DiagnosticSeverity.Warning 1 ])
    ledger.GetAll() |> ignore // sync barrier: the agent has run notifyReporters

    let output =
        logged |> Seq.map (fun (tag, msg) -> $"%s{tag} %s{msg}") |> String.concat "\n"

    test <@ output.Contains("error-ledger") @>
    test <@ output.Contains("InvalidOperationException") @>

[<Fact(Timeout = 15000)>]
let ``ErrorLedger reporter throwing on Report yields non-clean verdict with synthetic error`` () =
    // A reporter that throws while persisting a diagnostic must NOT collapse "there
    // were errors" into a clean pass. The CLI verdict and exit code read
    // GetAll()/FailingReasons, so the failure must land a synthetic Error entry in
    // that same path.
    let throwingReporter =
        { new IErrorReporter with
            member _.Report _ _ _ =
                raise (System.InvalidOperationException("reporter boom"))

            member _.Clear _ _ = ()
            member _.ClearPlugin _ = ()
            member _.ClearAll() = () }

    let ledger = ErrorLedger([ throwingReporter ])
    ledger.Report("lint", "/src/A.fs", [ entry "real error" DiagnosticSeverity.Error 1 ])
    ledger.GetAll() |> ignore // sync barrier

    test <@ ledger.HasFailingReasons(warningsAreFailures = false) @>

    let all = ledger.GetAll()
    let allEntries = all |> Map.toSeq |> Seq.collect snd |> Seq.toList

    // The synthetic error must name the failing plugin, so the daemon log and the
    // verdict agree about which one broke.
    test
        <@
            allEntries
            |> List.exists (fun (plugin, e) ->
                plugin = "error-ledger"
                && e.Severity = DiagnosticSeverity.Error
                && e.Message.Contains("lint")
                && e.Message.Contains("InvalidOperationException"))
        @>

    // FailingReasons is what the exit code is computed from.
    let failing = ledger.FailingReasons(warningsAreFailures = false)
    test <@ not failing.IsEmpty @>

[<Fact>]
let ``DiagnosticSeverity order is hint lt info lt warning lt error`` () =
    test <@ DiagnosticSeverity.order Hint < DiagnosticSeverity.order Info @>
    test <@ DiagnosticSeverity.order Info < DiagnosticSeverity.order Warning @>
    test <@ DiagnosticSeverity.order Warning < DiagnosticSeverity.order Error @>

[<Fact>]
let ``DiagnosticSeverity: every severity round-trips through its wire name, and Deferred ranks between Info and Warning``
    ()
    =
    // The wire name is how a severity survives the IPC hop, so one that does not
    // round-trip is one the CLI silently loses: `Deferred` losing its name routes a
    // "waiting on build" back to a plain failure. Every case, both directions.
    //
    // ENUMERATED BY REFLECTION, not by hand (AUTOMATION-294). This list was written out
    // and a case added later was simply absent from it — a new severity could ship
    // without a wire name and this test would still pass, which is the one thing it
    // exists to prevent. `IpcParsing` defaults an unrecognised tag to `Error`, so the
    // symptom is silent: the severity crosses the wire, comes back a failure, and the
    // exit code reverts to the very 1 the new case was added to stop returning.
    let all = allSeverities ()

    // The reflection is the point, so pin that it actually found them all — an empty or
    // truncated list would make the `forall` below vacuously true.
    test <@ List.length all >= 6 @>
    test <@ all |> List.contains HostAborted @>

    test
        <@
            all
            |> List.forall (fun s -> DiagnosticSeverity.fromString (DiagnosticSeverity.toString s) = Some s)
        @>

    // Never a default: an unrecognised tag from a newer peer must not be read as
    // one of ours.
    test <@ DiagnosticSeverity.fromString "not-a-severity" = None @>

    // Deferred is louder than informational (it denies a green) but is not a defect.
    test <@ DiagnosticSeverity.order Info < DiagnosticSeverity.order Deferred @>
    test <@ DiagnosticSeverity.order Deferred < DiagnosticSeverity.order Warning @>

    // AUTOMATION-294. `HostAborted` sits exactly where `Deferred` does, and for the same
    // reason: both say "this did not run", neither says "this failed".
    test <@ DiagnosticSeverity.toString HostAborted = "aborted" @>
    test <@ DiagnosticSeverity.fromString "aborted" = Some HostAborted @>
    test <@ DiagnosticSeverity.order HostAborted = DiagnosticSeverity.order Deferred @>
    test <@ DiagnosticSeverity.order Info < DiagnosticSeverity.order HostAborted @>
    test <@ DiagnosticSeverity.order HostAborted < DiagnosticSeverity.order Warning @>

/// AUTOMATION-294. `DiagnosticCounts` is the projection the status renderer and the
/// verdict read to decide "completed with issues", and it tallies only `Error` and
/// `Warning`. A severity whose whole meaning is "this DID NOT RUN" — `Deferred`,
/// `HostAborted` — must therefore tally as NEITHER: counted as an error it would render
/// a killed test host as a defect, which is precisely the confusion AUTOMATION-294
/// removed from the verdict, re-introduced one layer up in the renderer.
///
/// Enumerated by reflection (`allSeverities`) for the same reason the wire-name
/// round-trip is: the tally's `| _ -> d` swallows every case nobody named, so a
/// hand-written list here could not tell a deliberate "not a defect" from a case that
/// was never considered at all.
[<Fact>]
let ``DiagnosticCounts.ofEntries tallies only Error and Warning; a severity that did not run is neither`` () =
    let all = allSeverities ()

    // The reflection is the point, so pin that it actually found them all — an empty or
    // truncated list would make the `forall` below vacuously true.
    test <@ List.length all >= 6 @>
    test <@ all |> List.contains Deferred @>
    test <@ all |> List.contains HostAborted @>

    // One entry of EVERY severity at once: exactly one error and exactly one warning.
    let counts =
        all
        |> List.map (fun s -> errorEntry (DiagnosticSeverity.toString s) s)
        |> DiagnosticCounts.ofEntries

    test <@ counts = { Errors = 1; Warnings = 1 } @>

    // Per severity as well, so a miscount cannot cancel out inside that aggregate.
    let tallies =
        all |> List.map (fun s -> s, DiagnosticCounts.ofEntries [ errorEntry "x" s ])

    test <@ tallies |> List.contains (Error, { Errors = 1; Warnings = 0 }) @>
    test <@ tallies |> List.contains (Warning, { Errors = 0; Warnings = 1 }) @>

    let didNotRun = tallies |> List.filter (fun (s, _) -> s <> Error && s <> Warning)

    test <@ List.length didNotRun >= 4 @>
    test <@ didNotRun |> List.forall (fun (_, c) -> c = DiagnosticCounts.empty) @>

    // And the predicate the renderer actually calls agrees: a tally built from nothing
    // but did-not-run severities is not failing, even under warnings-are-failures.
    let didNotRunCounts =
        didNotRun
        |> List.map (fun (s, _) -> errorEntry "x" s)
        |> DiagnosticCounts.ofEntries

    test <@ not (DiagnosticCounts.isFailing true didNotRunCounts) @>

/// `GetCountsByPlugin` is the per-plugin tally the status renderer reads, and it must
/// AGGREGATE across every file a plugin reported: a tally that only ever folded one
/// file per plugin under-reports the moment a second file goes red, and the aggregation
/// step is exactly the branch no test was reaching. Non-defect severities count as
/// neither here for the same reason as in `ofEntries` — a plugin whose only entry says
/// "this did not run" is not a plugin with a defect.
[<Fact(Timeout = 15000)>]
let ``GetCountsByPlugin sums a plugin's counts across its files and tallies did-not-run entries as neither`` () =
    let ledger = ErrorLedger()

    ledger.Report(
        "lint",
        "/src/A.fs",
        [ entry "a-err" DiagnosticSeverity.Error 1
          entry "a-warn" DiagnosticSeverity.Warning 2 ]
    )

    ledger.Report(
        "lint",
        "/src/B.fs",
        [ entry "b-err" DiagnosticSeverity.Error 3
          entry "b-aborted" DiagnosticSeverity.HostAborted 4 ]
    )

    ledger.Report("test-prune", "/src/C.fs", [ entry "c-deferred" DiagnosticSeverity.Deferred 5 ])

    // PostAndReply on the same mailbox is the sync barrier for the posts above.
    let counts = ledger.GetCountsByPlugin()

    // lint spans two files: the tally is their SUM, and the aborted entry is neither.
    test <@ Map.tryFind "lint" counts = Some { Errors = 2; Warnings = 1 } @>

    // test-prune reported a "did not run" entry and nothing else: present, but with
    // zero errors and zero warnings, so nothing downstream renders it as a defect.
    test <@ Map.tryFind "test-prune" counts = Some DiagnosticCounts.empty @>
    test <@ not (DiagnosticCounts.isFailing true counts.["test-prune"]) @>

    // A plugin that reported nothing is ABSENT, not zero — the documented contract.
    test <@ Map.tryFind "format" counts = None @>

// ---------------------------------------------------------------------------
// AUTOMATION-747 — `Transport`: how much of the ledger a mirror of it may carry.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``Transport.truncateField leaves a short field alone and names what it drops`` () =
    test <@ Transport.truncateField "short" = "short" @>
    test <@ isNull (Transport.truncateField null) @>

    let oversized = String.replicate (Transport.MaxFieldChars + 500) "x"
    let trimmed = Transport.truncateField oversized

    test <@ trimmed.Length < oversized.Length @>
    test <@ trimmed.StartsWith(String.replicate 100 "x", StringComparison.Ordinal) @>
    // The count is the point: a field that says only "truncated" cannot tell a reader
    // whether they are missing a line or a million.
    test <@ trimmed.Contains "500 chars" @>

[<Fact(Timeout = 15000)>]
let ``Transport.takeDetail bounds a whole response, and marks the entries it could not carry`` () =
    // The response-wide budget is what the per-field cap alone does not give: 2,414
    // entries each holding a 20,000-char excerpt is still ~48 MB of a field nothing
    // renders.
    let oneDetail = Some(String.replicate 40_000 "y")

    let carried, spent =
        List.replicate 200 oneDetail |> List.mapFold Transport.takeDetail 0

    test <@ carried.Length = 200 @>
    // Never dropped to `None`: absence and elision are different facts.
    test <@ carried |> List.forall Option.isSome @>

    let marker = Transport.DetailBudgetSpentMarker
    let real = carried |> List.filter (fun d -> d <> Some marker)
    let elided = carried |> List.filter (fun d -> d = Some marker)

    test <@ not real.IsEmpty @>
    test <@ not elided.IsEmpty @>
    // Overshoot is at most the one entry that crossed the line.
    test <@ spent <= Transport.MaxDetailCharsPerResponse + Transport.MaxFieldChars + 64 @>

[<Fact(Timeout = 15000)>]
let ``Transport.takeDetail spends nothing on an entry that has no detail`` () =
    // A ledger of detail-free entries — the ordinary case — must not have its budget
    // eaten by entries that were never going to carry anything.
    let carried, spent =
        List.replicate 5_000 None |> List.mapFold Transport.takeDetail 0

    test <@ spent = 0 @>
    test <@ carried |> List.forall Option.isNone @>
