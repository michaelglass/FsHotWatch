module FsHotWatch.Tests.ErrorLedgerTests

open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Tests.TestHelpers

let private entry msg sev line = { errorEntry msg sev with Line = line }

[<Fact(Timeout = 5000)>]
let ``Report adds errors and GetAll returns them grouped by file`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "bad" DiagnosticSeverity.Warning 1 ])
    let all = ledger.GetAll()
    test <@ all.ContainsKey "/src/A.fs" @>
    test <@ all.["/src/A.fs"].Length = 1 @>

[<Fact(Timeout = 5000)>]
let ``Clear removes errors for plugin and file`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "bad" DiagnosticSeverity.Warning 1 ])
    ledger.Clear("lint", "/src/A.fs")
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 5000)>]
let ``Report with empty list clears errors`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "bad" DiagnosticSeverity.Warning 1 ])
    ledger.Report("lint", "/src/A.fs", [])
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 5000)>]
let ``GetByPlugin filters to specific plugin`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "lint-warn" DiagnosticSeverity.Warning 1 ])
    ledger.Report("analyzers", "/src/A.fs", [ entry "analyzer-err" DiagnosticSeverity.Error 2 ])
    let lintOnly = ledger.GetByPlugin("lint")
    test <@ lintOnly.Count = 1 @>
    test <@ lintOnly.["/src/A.fs"].[0].Message = "lint-warn" @>

[<Fact(Timeout = 5000)>]
let ``Multiple plugins for same file accumulate independently`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "lint" DiagnosticSeverity.Warning 1 ])
    ledger.Report("analyzers", "/src/A.fs", [ entry "analyze" DiagnosticSeverity.Error 2 ])
    let all = ledger.GetAll()
    test <@ all.["/src/A.fs"].Length = 2 @>
    ledger.Clear("lint", "/src/A.fs")
    let all2 = ledger.GetAll()
    test <@ all2.["/src/A.fs"].Length = 1 @>

[<Fact(Timeout = 5000)>]
let ``ClearPlugin removes all errors for a plugin`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "a" DiagnosticSeverity.Warning 1 ])
    ledger.Report("lint", "/src/B.fs", [ entry "b" DiagnosticSeverity.Warning 2 ])
    ledger.Report("analyzers", "/src/A.fs", [ entry "c" DiagnosticSeverity.Error 3 ])
    ledger.ClearPlugin("lint")
    test <@ ledger.GetAll() |> Map.values |> Seq.sumBy List.length = 1 @>
    test <@ ledger.GetByPlugin("lint").IsEmpty @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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
    // This hits the update factory branch where v >= last (2 >= 1)
    ledger.Report("fcs", "/tmp/Lib.fs", [ entry2 ], version = 2L)

    let errors = ledger.GetAll()
    let fileErrors = errors |> Map.tryFind "/tmp/Lib.fs" |> Option.defaultValue []
    test <@ fileErrors.Length = 1 @>
    test <@ (snd fileErrors.[0]).Message = "updated" @>

[<Fact(Timeout = 5000)>]
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
    // Same version should still be accepted (v >= last, 3 >= 3)
    ledger.Report("fcs", "/tmp/Lib.fs", [ entry2 ], version = 3L)

    let errors = ledger.GetAll()
    let fileErrors = errors |> Map.tryFind "/tmp/Lib.fs" |> Option.defaultValue []
    test <@ fileErrors.Length = 1 @>
    test <@ (snd fileErrors.[0]).Message = "same-version-update" @>

[<Fact(Timeout = 5000)>]
let ``Clear accepts newer version after initial versioned report`` () =
    let ledger = ErrorLedger()

    let entry1 =
        { Message = "error"
          Severity = DiagnosticSeverity.Error
          Line = 1
          Column = 0
          Detail = None }

    ledger.Report("fcs", "/tmp/Lib.fs", [ entry1 ], version = 1L)
    // Clear with higher version should succeed (hits update branch where v >= last)
    ledger.Clear("fcs", "/tmp/Lib.fs", version = 2L)
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``FailingReasons returns errors and warnings when warningsAreFailures is true`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "warn" DiagnosticSeverity.Warning 1 ])
    ledger.Report("fcs", "/src/A.fs", [ entry "err" DiagnosticSeverity.Error 2 ])
    ledger.Report("lint", "/src/B.fs", [ entry "info" DiagnosticSeverity.Info 3 ])
    let failing = ledger.FailingReasons(warningsAreFailures = true)
    test <@ failing.Count = 1 @>
    test <@ failing.ContainsKey "/src/A.fs" @>
    test <@ failing.["/src/A.fs"].Length = 2 @>

[<Fact(Timeout = 5000)>]
let ``HasFailingReasons returns false when only info and hint entries exist`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "info" DiagnosticSeverity.Info 1 ])
    ledger.Report("fcs", "/src/B.fs", [ entry "hint" DiagnosticSeverity.Hint 2 ])
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = false)) @>
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 5000)>]
let ``HasFailingReasons with warningsAreFailures false ignores warnings`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "warn" DiagnosticSeverity.Warning 1 ])
    test <@ not (ledger.HasFailingReasons(warningsAreFailures = false)) @>
    test <@ ledger.HasFailingReasons(warningsAreFailures = true) @>

[<Fact(Timeout = 5000)>]
let ``FailingReasons returns empty map when no failing entries`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "info" DiagnosticSeverity.Info 1 ])
    let failing = ledger.FailingReasons(warningsAreFailures = true)
    test <@ failing.IsEmpty @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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
