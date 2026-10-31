/// Trace selection in shadow: computed at a run's launch next to the static selection,
/// logged, and stored with the run's verified-failure misses at completion. It never
/// changes what runs.
module FsHotWatch.Tests.TraceShadowTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.TraceStore
open FsHotWatch.TestPrune

let private test' cls meth : TestMethodInfo =
    { SymbolFullName = $"%s{cls}.%s{meth}"
      TestProject = "P"
      TestClass = cls
      TestMethod = meth }

let private settings select : TraceSettings =
    { Record = RecordEveryRun
      DbPath = "traces.db"
      WeaveTests = WeaveTestSites
      FingerprintInputs = []
      FingerprintEnv = []
      VerifyTimeoutSec = 60
      Select = select }

/// A repository root holding a trace store with one recorded run of project P: test
/// `C.a` executed `N.f`, test `C.b` executed `N.g`.
let private withStore (f: string -> unit) =
    let root = Directory.CreateTempSubdirectory("fshw-trace-shadow-").FullName

    try
        do
            use store = Store.Open(Path.Combine(root, "traces.db"))

            let scope key syms : ScopeContent =
                { Key = key
                  Symbols = syms
                  Inputs = [] }

            let passed key scopes : TestTrace =
                { TestKey = key
                  Status = Some Passed
                  Reasons = []
                  ScopeKeys = scopes }

            store.RecordRun(
                { RunId = "r1"
                  TestProject = "P"
                  TreeHash = "tree"
                  EnvFingerprint = "E"
                  RecordedAt = DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero)
                  Kind = FullRun
                  Status = Recorded
                  Reason = ""
                  StatsJson = "{}" },
                [ scope "T:a" [ "N.f", "h1" ]; scope "T:b" [ "N.g", "h2" ] ],
                [ passed "P|C|a" [ "T:a" ]; passed "P|C|b" [ "T:b" ] ]
            )

        f root
    finally
        Directory.Delete(root, true)

let private universe = [ test' "C" "a"; test' "C" "b"; test' "C" "c" ]

let private versions = Map [ "N.f", "h1"; "N.g", "changed" ]

let private launch root select (lines: ResizeArray<string>) =
    TraceShadow.atLaunch
        (settings select)
        root
        [ "P"; "Q" ]
        (fun p -> if p = "P" then universe else [])
        (lazy versions)
        [ test' "C" "a" ]
        lines.Add

[<Fact>]
let ``at launch, shadow compares the trace selection with the static one and logs it per traced project`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines

        let r = readings |> List.exactlyOne
        test <@ r.Fingerprint = "E" @>

        test
            <@
                r.Selection.Selected = Map
                    [ "P|C|b", [ TraceSelect.SymbolChanged "N.g" ]
                      "P|C|c", [ TraceSelect.NoTrace ] ]
            @>

        test <@ r.Comparison.StaticOnly = set [ "P|C|a" ] @>
        // Q has no recorded run: nothing to select from, so no reading and no line.
        test
            <@
                lines.Count = 1
                && lines.[0].StartsWith "trace-shadow: P static 1/1c, trace 2/1c"
            @>)

[<Fact>]
let ``with select off nothing is read, logged or stored`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        test <@ List.isEmpty (launch root SelectOff lines) && lines.Count = 0 @>)

[<Fact>]
let ``an unreadable store is one log line and no reading, never an exception`` () =
    let root = Directory.CreateTempSubdirectory("fshw-trace-shadow-bad-").FullName

    try
        File.WriteAllText(Path.Combine(root, "traces.db"), "not a database")
        let lines = ResizeArray<string>()
        test <@ List.isEmpty (launch root SelectShadow lines) @>
        test <@ lines.Count = 1 && lines.[0].StartsWith "trace-shadow: skipped" @>
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``at completion the rows are stored under the run, and a failed test the trace left out is a miss`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines

        TraceShadow.atCompletion (settings SelectShadow) root readings "run-7" [ "P|C|a"; "P|C|b" ] lines.Add

        use store = Store.Open(Path.Combine(root, "traces.db"))
        let rows = store.ShadowRows "P"
        test <@ rows |> List.map _.RunId = [ "run-7" ] @>
        test <@ rows.[0].TraceOnly = [ "P|C|b"; "P|C|c" ] @>
        test <@ store.ShadowMisses "P" = [ "run-7", "P|C|a" ] @>

        test
            <@
                lines
                |> Seq.exists (fun l -> l.Contains "verified-failure miss" && l.Contains "P|C|a")
            @>)

[<Fact>]
let ``a failure's key is its project, class and method`` () =
    test <@ TraceShadow.failureKey "P" "Ns.C+Inner" "a test" = "P|Ns.C+Inner|a test" @>

[<Fact>]
let ``with no reading, completion opens nothing and stores nothing`` () =
    let root = Directory.CreateTempSubdirectory("fshw-trace-shadow-none-").FullName

    try
        let lines = ResizeArray<string>()
        TraceShadow.atCompletion (settings SelectShadow) root [] "run-1" [ "P|C|a" ] lines.Add
        test <@ lines.Count = 0 && not (File.Exists(Path.Combine(root, "traces.db"))) @>
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a run with no failed test stores its rows and no miss`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines
        TraceShadow.atCompletion (settings SelectShadow) root readings "run-2" [] lines.Add

        use store = Store.Open(Path.Combine(root, "traces.db"))
        test <@ store.ShadowRows "P" |> List.map _.RunId = [ "run-2" ] @>
        test <@ List.isEmpty (store.ShadowMisses "P") && lines.Count = 1 @>)

[<Fact>]
let ``a store that cannot be written at completion is one log line, never an exception`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines
        File.Delete(Path.Combine(root, "traces.db"))
        File.WriteAllText(Path.Combine(root, "traces.db"), "not a database")

        TraceShadow.atCompletion (settings SelectShadow) root readings "run-3" [ "P|C|a" ] lines.Add

        test <@ lines |> Seq.last |> (fun l -> l.StartsWith "trace-shadow: not stored") @>)
