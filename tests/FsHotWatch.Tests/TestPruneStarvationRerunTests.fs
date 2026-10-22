/// The plugin end of `Starvation`: a project that declared "re-run alone" and timed out
/// on a starved box is re-run once after the rest of the run, its second result is the
/// one reported, and the run directory says what happened.
///
/// The box's real load is whatever it is when this runs, so these tests pin the
/// threshold instead: `loadPerCore = 0` makes every sample starved (a load average is
/// never negative), and a threshold no box reaches makes none of them starved.
module FsHotWatch.Tests.TestPruneStarvationRerunTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// A project whose first launch hangs past its 2 s timeout and whose second exits 0:
/// the marker file is the only state the two launches share.
let private hangsOnceThenPasses (marker: string) (starvation: Starvation.Policy option) : TestConfig =
    { Project = "Slow.Tests"
      Command = "sh"
      Args =
        $"-c \"if [ -f '%s{marker}' ]; then echo second; exit 0; else touch '%s{marker}'; echo first; exec sleep 60; fi\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = Some 2
      ReportVerificationFormat = Disabled
      Starvation = starvation
      AlwaysClasses = [] }

let private alwaysStarved: Starvation.Policy =
    { Starvation.Policy.rerunAlone [] with
        LoadPerCore = 0.0 }

let private neverStarved: Starvation.Policy =
    { Starvation.Policy.rerunAlone [] with
        LoadPerCore = 1_000_000.0 }

/// Run the suite once through `run-tests`; the project's status and the run directory.
let private runOnce (config: TestConfig) : string * string =
    let root = isolatedRoot ()
    let host = PluginHost.create (Unchecked.defaultof<_>) root
    let handler = create ":memory:" root (Some [ config ]) None None None None []
    host.RegisterHandler(handler)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsSome @>

    use doc = JsonDocument.Parse(result.Value)

    let status =
        doc.RootElement.GetProperty("projects").[0].GetProperty("status").GetString()

    let runDir =
        Directory.GetDirectories(FsHotWatch.Ctrf.reportsDir root) |> Array.exactlyOne

    status, runDir

let private markerIn () =
    Path.Combine(Path.GetTempPath(), $"fshw-starved-marker-{Guid.NewGuid():N}")

[<Fact(Timeout = 120_000)>]
let ``a declared project that timed out on a starved box is re-run alone, and its second result is reported`` () =
    let marker = markerIn ()

    try
        let status, runDir = runOnce (hangsOnceThenPasses marker (Some alwaysStarved))

        test <@ status = "passed" @>

        // The verdict-facing record says it was starved, then passed alone.
        match Starvation.tryRead (Starvation.pathIn runDir "Slow.Tests") with
        | Some stored ->
            test <@ stored.PassedAlone @>
            test <@ stored.Summary.Contains "starved, then passed alone" @>
            test <@ stored.FirstAttempt.Contains "timed out after 2s" @>
        | None -> failwith $"no starvation record in %s{runDir}"

        // The first attempt is kept, out of the run's report set; the run's own log is
        // the re-run's.
        let attempt = Path.Combine(runDir, Starvation.AttemptDirName)
        test <@ File.Exists(Path.Combine(attempt, "Slow.Tests" + TimeoutMarker.Suffix)) @>
        test <@ File.ReadAllText(Path.Combine(attempt, "Slow.Tests.output.log")).Contains "first" @>
        test <@ File.ReadAllText(Path.Combine(runDir, "Slow.Tests.output.log")).Contains "second" @>
        test <@ not (File.Exists(Path.Combine(runDir, "Slow.Tests" + TimeoutMarker.Suffix))) @>
    finally
        File.Delete marker

[<Fact(Timeout = 120_000)>]
let ``a declared project that timed out on a quiet box is reported timed out, and not re-run`` () =
    let marker = markerIn ()

    try
        let status, runDir = runOnce (hangsOnceThenPasses marker (Some neverStarved))

        test <@ status = "timed-out" @>
        test <@ not (File.Exists(Starvation.pathIn runDir "Slow.Tests")) @>
        test <@ not (Directory.Exists(Path.Combine(runDir, Starvation.AttemptDirName))) @>
    finally
        File.Delete marker

[<Fact(Timeout = 120_000)>]
let ``a project that declared nothing is never re-run, however starved the box`` () =
    let marker = markerIn ()

    try
        let status, runDir = runOnce (hangsOnceThenPasses marker None)

        test <@ status = "timed-out" @>
        test <@ not (File.Exists(Starvation.pathIn runDir "Slow.Tests")) @>
    finally
        File.Delete marker

[<Fact(Timeout = 120_000)>]
let ``a red that is not a timeout is never re-run, even when declared and starved`` () =
    let marker = markerIn ()

    let failsOnceThenPasses =
        { hangsOnceThenPasses marker (Some alwaysStarved) with
            Args = $"-c \"if [ -f '%s{marker}' ]; then exit 0; else touch '%s{marker}'; exit 1; fi\"" }

    try
        let status, runDir = runOnce failsOnceThenPasses

        test <@ status = "failed" @>
        test <@ not (File.Exists(Starvation.pathIn runDir "Slow.Tests")) @>
    finally
        File.Delete marker
