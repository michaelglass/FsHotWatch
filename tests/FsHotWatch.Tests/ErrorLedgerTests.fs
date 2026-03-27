module FsHotWatch.Tests.ErrorLedgerTests

open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger

let private entry msg sev line =
    { Message = msg
      Severity = sev
      Line = line
      Column = 0 }

[<Fact>]
let ``Report adds errors and GetAll returns them grouped by file`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "bad" "warning" 1 ])
    let all = ledger.GetAll()
    test <@ all.ContainsKey "/src/A.fs" @>
    test <@ all.["/src/A.fs"].Length = 1 @>

[<Fact>]
let ``Clear removes errors for plugin and file`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "bad" "warning" 1 ])
    ledger.Clear("lint", "/src/A.fs")
    test <@ not (ledger.HasErrors()) @>

[<Fact>]
let ``Report with empty list clears errors`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "bad" "warning" 1 ])
    ledger.Report("lint", "/src/A.fs", [])
    test <@ not (ledger.HasErrors()) @>

[<Fact>]
let ``GetByPlugin filters to specific plugin`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "lint-warn" "warning" 1 ])
    ledger.Report("analyzers", "/src/A.fs", [ entry "analyzer-err" "error" 2 ])
    let lintOnly = ledger.GetByPlugin("lint")
    test <@ lintOnly.Count = 1 @>
    test <@ lintOnly.["/src/A.fs"].[0].Message = "lint-warn" @>

[<Fact>]
let ``Multiple plugins for same file accumulate independently`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "lint" "warning" 1 ])
    ledger.Report("analyzers", "/src/A.fs", [ entry "analyze" "error" 2 ])
    let all = ledger.GetAll()
    test <@ all.["/src/A.fs"].Length = 2 @>
    ledger.Clear("lint", "/src/A.fs")
    let all2 = ledger.GetAll()
    test <@ all2.["/src/A.fs"].Length = 1 @>

[<Fact>]
let ``ClearPlugin removes all errors for a plugin`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "a" "warning" 1 ])
    ledger.Report("lint", "/src/B.fs", [ entry "b" "warning" 2 ])
    ledger.Report("analyzers", "/src/A.fs", [ entry "c" "error" 3 ])
    ledger.ClearPlugin("lint")
    test <@ ledger.Count() = 1 @>
    test <@ ledger.GetByPlugin("lint").IsEmpty @>

[<Fact>]
let ``Count returns total across all plugins and files`` () =
    let ledger = ErrorLedger()
    ledger.Report("lint", "/src/A.fs", [ entry "a" "warning" 1; entry "b" "warning" 2 ])
    ledger.Report("analyzers", "/src/A.fs", [ entry "c" "error" 3 ])
    test <@ ledger.Count() = 3 @>

[<Fact>]
let ``Report ignores stale version`` () =
    let ledger = ErrorLedger()

    let newEntry =
        { Message = "new"
          Severity = "error"
          Line = 1
          Column = 0 }

    let staleEntry =
        { Message = "stale"
          Severity = "error"
          Line = 2
          Column = 0 }

    ledger.Report("fcs", "/tmp/Lib.fs", [ newEntry ], version = 2L)
    ledger.Report("fcs", "/tmp/Lib.fs", [ staleEntry ], version = 1L)

    let errors = ledger.GetAll()
    let fileErrors = errors |> Map.tryFind "/tmp/Lib.fs" |> Option.defaultValue []
    test <@ fileErrors.Length = 1 @>
    test <@ (snd fileErrors.[0]).Message = "new" @>

[<Fact>]
let ``Clear ignores stale version`` () =
    let ledger = ErrorLedger()

    let e =
        { Message = "error"
          Severity = "error"
          Line = 1
          Column = 0 }

    ledger.Report("fcs", "/tmp/Lib.fs", [ e ], version = 2L)
    ledger.Clear("fcs", "/tmp/Lib.fs", version = 1L)

    test <@ ledger.HasErrors() @>

[<Fact>]
let ``Report without version always updates`` () =
    let ledger = ErrorLedger()

    let entry1 =
        { Message = "first"
          Severity = "error"
          Line = 1
          Column = 0 }

    let entry2 =
        { Message = "second"
          Severity = "error"
          Line = 2
          Column = 0 }

    ledger.Report("fcs", "/tmp/Lib.fs", [ entry1 ], version = 5L)
    ledger.Report("fcs", "/tmp/Lib.fs", [ entry2 ])

    let errors = ledger.GetAll()
    let fileErrors = errors |> Map.tryFind "/tmp/Lib.fs" |> Option.defaultValue []
    test <@ (snd fileErrors.[0]).Message = "second" @>
