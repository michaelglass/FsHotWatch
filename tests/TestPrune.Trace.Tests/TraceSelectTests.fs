/// Trace selection (shadow): which tests a recorded trace says must run now, and how that
/// compares with the static selection.
module TestPrune.Trace.Tests.TraceSelectTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.TraceStore

let private trace key complete reasons (symbols: (string * string) list) (inputs: (string * string * string) list) =
    { TestKey = key
      EnvFingerprint = "E"
      RunId = "r1"
      Complete = complete
      Reasons = reasons
      Symbols = Set.ofList symbols
      Inputs = Set.ofList inputs }

let private stored =
    Map
        [ "P|C|unchanged", trace "P|C|unchanged" true [] [ "N.f", "h1" ] [ "read", "a.txt", "ha" ]
          "P|C|symbol", trace "P|C|symbol" true [] [ "N.f", "h1"; "N.g", "old" ] []
          "P|C|gone", trace "P|C|gone" true [] [ "N.removed", "h9" ] []
          "P|C|input", trace "P|C|input" true [] [] [ "read", "b.txt", "old" ]
          "P|C|incomplete", trace "P|C|incomplete" false [ "no-outcome" ] [ "N.f", "h1" ] [] ]

let private versions = Map [ "N.f", "h1"; "N.g", "new" ]

let private inputs = Map [ ("read", "a.txt"), "ha"; ("read", "b.txt"), "new" ]

let private selectAll universe =
    TraceSelect.select "P" universe stored.TryFind versions.TryFind inputs.TryFind

[<Fact>]
let ``a test is selected when it has no trace, its trace changed, or its trace is incomplete`` () =
    let s =
        selectAll
            [ "P|C|unchanged"
              "P|C|symbol"
              "P|C|gone"
              "P|C|input"
              "P|C|incomplete"
              "P|C|new" ]

    test
        <@
            s.Selected = Map
                [ "P|C|symbol", [ TraceSelect.SymbolChanged "N.g" ]
                  "P|C|gone", [ TraceSelect.SymbolChanged "N.removed" ]
                  "P|C|input", [ TraceSelect.InputChanged("read", "b.txt") ]
                  "P|C|incomplete", [ TraceSelect.Incomplete [ "no-outcome" ] ]
                  "P|C|new", [ TraceSelect.NoTrace ] ]
        @>

    test <@ s.Universe.Count = 6 @>
    test <@ abs (s.CompleteShare - 4.0 / 6.0) < 1e-9 @>

[<Fact>]
let ``rule codes count each selected test once per rule`` () =
    let s = selectAll [ "P|C|symbol"; "P|C|new"; "P|C|unchanged" ]
    test <@ TraceSelect.ruleCounts s = Map [ "R1", 1; "R2", 1 ] @>

[<Fact>]
let ``the comparison splits static extras from trace additions, by test and by class`` () =
    let s = selectAll [ "P|C|unchanged"; "P|C|symbol"; "P|D|x"; "P|C|new" ]
    let c = TraceSelect.compare [ "P|C|unchanged"; "P|D|x" ] s

    test <@ c.StaticOnly = set [ "P|C|unchanged" ] @>
    test <@ c.TraceOnly = set [ "P|C|symbol"; "P|C|new" ] @>
    test <@ c.StaticClasses = set [ "P|C"; "P|D" ] && c.TraceClasses = set [ "P|C"; "P|D" ] @>

[<Fact>]
let ``a failed test the trace selection left out is a verified-failure miss`` () =
    let s = selectAll [ "P|C|unchanged"; "P|C|symbol" ]

    test
        <@ TraceSelect.verifiedFailureMisses s [ "P|C|unchanged"; "P|C|symbol"; "P|X|unknown" ] = [ "P|C|unchanged" ] @>

[<Fact>]
let ``the log line names the project, both selections, the rules and the misses`` () =
    let s = selectAll [ "P|C|unchanged"; "P|C|symbol"; "P|C|new" ]
    let c = TraceSelect.compare [ "P|C|unchanged" ] s

    test
        <@
            TraceSelect.logLine s c 12L = "trace-shadow: P static 1/1c, trace 2/1c, universe 3, trace-only 2, static-only 1, R1 1 R2 1, complete 0.667, 12 ms"
        @>

[<Fact>]
let ``a shadow row round-trips through the store, and so do its misses`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "tp-shadow-" + Guid.NewGuid().ToString "N" + ".db")

    try
        use store = Store.Open path
        let s = selectAll [ "P|C|unchanged"; "P|C|symbol" ]
        let c = TraceSelect.compare [ "P|C|unchanged" ] s
        let at = DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero)
        let row = TraceSelect.row "run-1" "E" at 5L s c
        store.RecordShadow(row)
        store.RecordShadowMisses("run-1", "P", at, [ "P|C|unchanged" ])

        test <@ store.ShadowRows "P" = [ row ] @>
        test <@ store.ShadowMisses "P" = [ "run-1", "P|C|unchanged" ] @>
    finally
        File.Delete path

[<Fact>]
let ``an input's hash now is the hash ingestion stores for it`` () =
    let root = Directory.CreateTempSubdirectory("tp-input-now-").FullName

    try
        File.WriteAllText(Path.Combine(root, "a.txt"), "x")
        Directory.CreateDirectory(Path.Combine(root, "d")) |> ignore

        for kind in
            [ Model.FileRead
              Model.ExistenceProbe
              Model.DirectoryListing
              Model.DeepDirectoryListing
              Model.MetadataRead ] do
            for path in [ "a.txt"; "d"; "missing" ] do
                let kindCode, key, hash =
                    TraceIngest.inputOf
                        root
                        { Kind = kind
                          Path = Path.Combine(root, path) }
                    |> Option.get

                test <@ TraceIngest.currentInputHash root (kindCode, key) = Some hash @>

        test <@ TraceIngest.currentInputHash root ("weird", "a.txt") = None @>
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``every rule has its code`` () =
    let s = selectAll [ "P|C|new"; "P|C|symbol"; "P|C|input"; "P|C|incomplete" ]

    test <@ TraceSelect.ruleCounts s = Map [ "R1", 1; "R2", 1; "R3", 1; "R4", 1 ] @>

[<Fact>]
let ``an empty universe is wholly complete, and a run where no rule fired says so`` () =
    let s = selectAll []
    test <@ s.CompleteShare = 1.0 @>
    let c = TraceSelect.compare [ "unkeyed" ] s
    test <@ c.StaticClasses = set [ "unkeyed" ] @>
    test <@ (TraceSelect.logLine s c 0L).Contains ", no rule fired, " @>
