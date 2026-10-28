/// Pricing a selection before it launches, from the class durations earlier runs recorded.
module FsHotWatch.Tests.SelectionPriceTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.SelectionPrice

let private at = DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc)

let private cls (name: string) (ms: int64) : Insights.ClassRow =
    { Timing =
        { Class = name
          Tests = 1
          DurationMs = ms
          Start = at
          Stop = at.AddMilliseconds(float ms) }
      Contended = None
      LoadPerCoreMean = None }

let private run
    (runId: string)
    (project: string)
    (outcome: string)
    (classes: (string * int64) list)
    : Insights.ProjectRun =
    { RunId = runId
      Project = project
      Outcome = Some outcome
      WallMs = None
      TimeoutSec = None
      LoadPerCoreMean = None
      RunningAtEnd = []
      Children = []
      Classes = classes |> List.map (fun (c, ms) -> cls c ms) }

// Newest first, as `Insights.loadRuns` orders them.
let private history =
    [ "r3", [ run "r3" "P" "passed" [ "P.A", 120L; "P.B", 30L ] ]
      "r2",
      [ run "r2" "P" "passed" [ "P.A", 100L; "P.B", 50L; "P.C", 10L ]
        run "r2" "Q" "passed" [ "Q.X", 7L ] ]
      "r1", [ run "r1" "P" "passed" [ "P.A", 80L ] ] ]

let private book = Book.ofRuns 3 history

[<Fact(Timeout = 10000)>]
let ``a class is priced at the median of its recorded durations`` () =
    test <@ Book.classMs book "P" "P.A" = Some 100.0 @>
    test <@ Book.classMs book "P" "P.B" = Some 40.0 @>
    test <@ Book.classMs book "Q" "Q.X" = Some 7.0 @>
    test <@ Book.classMs book "P" "P.Nope" = None @>

[<Fact(Timeout = 10000)>]
let ``a selection is priced by its classes, its whole projects, and what it leaves out`` () =
    let priced =
        price book [ "P", Asked.Classes(Set.ofList [ "P.A"; "P.Unknown" ]); "Q", Asked.Whole ]

    test <@ priced.EstimateMs = 107.0 @>
    test <@ priced.PricedClasses = 2 @>
    test <@ priced.Unpriced = [ "P", "P.Unknown" ] @>
    test <@ List.isEmpty priced.UnpricedProjects @>

    let allBut = price book [ "P", Asked.AllBut(Set.ofList [ "P.A" ]) ]
    test <@ allBut.EstimateMs = 50.0 @>

[<Fact(Timeout = 10000)>]
let ``a project no run recorded is named, never priced at zero silently`` () =
    let priced = price book [ "R", Asked.Whole ]
    test <@ priced.EstimateMs = 0.0 @>
    test <@ priced.UnpricedProjects = [ "R" ] @>
    test <@ (describe priced).Contains "R" @>

[<Fact(Timeout = 10000)>]
let ``a timed-out run's partial classes and runs past the window do not price`` () =
    let withTimeout = ("r4", [ run "r4" "P" "timed-out" [ "P.A", 9000L ] ]) :: history

    test <@ Book.classMs (Book.ofRuns 3 withTimeout) "P" "P.A" = Some 100.0 @>
    // The window is each project's own newest runs: one for P is r3, one for Q is r2.
    test <@ Book.classMs (Book.ofRuns 1 history) "P" "P.A" = Some 120.0 @>
    test <@ Book.classMs (Book.ofRuns 1 history) "P" "P.C" = None @>
    test <@ Book.classMs (Book.ofRuns 1 history) "Q" "Q.X" = Some 7.0 @>

[<Fact(Timeout = 10000)>]
let ``a backtest prices each run from the runs before it and compares with what it spent`` () =
    let results = backtest 3 history

    // r3 priced from r2 and r1 alone; r1 has nothing before it.
    test <@ results |> List.map _.RunId = [ "r3"; "r2" ] @>
    let r3 = results.Head
    test <@ r3.ActualMs = 150.0 @>
    test <@ r3.EstimateMs = 90.0 + 50.0 @>

[<Fact(Timeout = 10000)>]
let ``a price names the classes no run recorded`` () =
    let priced = price book [ "P", Asked.Classes(Set.ofList [ "P.A"; "P.New" ]) ]
    test <@ (describe priced).Contains "1 class(es) never recorded" @>
    test <@ not ((describe (price book [ "P", Asked.Whole ])).Contains "unpriced") @>

[<Fact(Timeout = 10000)>]
let ``a book over a runs directory reads only its newest runs, and none when it is missing`` () =
    FsHotWatch.Tests.TestHelpers.withTempDir "price-runs" (fun root ->
        test <@ (Book.ofRunsDir 5 (IO.Path.Combine(root, "absent"))).Classes = Map.empty @>

        let write (runId: string) (ms: int64) (stamp: DateTime) =
            let dir = IO.Path.Combine(root, runId)
            IO.Directory.CreateDirectory dir |> ignore

            IO.File.WriteAllText(
                IO.Path.Combine(dir, "P" + FsHotWatch.Ctrf.ReportSuffix),
                $"""{{"results":{{"summary":{{"tests":1,"passed":1,"failed":0,"pending":0,"skipped":0,"other":0}},"tests":[{{"name":"P.A.t","status":"passed","duration":%d{ms},"start":1000,"stop":%d{1000L + ms},"extra":{{"type":"P.A"}}}}]}}}}"""
            )

            IO.Directory.SetLastWriteTimeUtc(dir, stamp)

        write "old" 9000L (DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        write "new" 1000L (DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc))

        test <@ Book.classMs (Book.ofRunsDir 1 root) "P" "P.A" = Some 1000.0 @>
        test <@ Book.classMs (Book.ofRunsDir 2 root) "P" "P.A" = Some 5000.0 @>)

[<Fact(Timeout = 10000)>]
let ``a failed run still prices its classes, a run with no outcome or no class does not`` () =
    let runs =
        [ "r3", [ run "r3" "P" "failed" [ "P.A", 30L ] ]
          "r2",
          [ { run "r2" "P" "passed" [ "P.A", 9000L ] with
                Outcome = None } ]
          "r1", [ run "r1" "P" "passed" [] ] ]

    let priced = Book.ofRuns 3 runs
    test <@ Book.classMs priced "P" "P.A" = Some 30.0 @>
    // r3 has runs before it, but none that priced anything: it is backtested as unpriced.
    test <@ backtest 3 runs |> List.map (fun b -> b.RunId, b.EstimateMs, b.Unpriced) = [ "r3", 0.0, 1 ] @>
