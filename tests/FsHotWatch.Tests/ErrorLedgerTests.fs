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
