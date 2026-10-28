/// The plugin end of `Profile`: every project of every run leaves a
/// `<Project>.profile.jsonl` in the run directory — including a project whose host was
/// killed at its timeout and wrote no report (AUTOMATION-1300).
module FsHotWatch.Tests.TestPruneProfileTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestPrunePluginTestSupport

let private project (args: string) (timeoutSec: int option) : TestConfig =
    { Project = "Profiled.Tests"
      Command = "sh"
      Args = args
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = timeoutSec
      ReportVerificationFormat = Disabled
      Starvation = None
      CheckSkip = None
      AlwaysClasses = [] }

let private runOnce (config: TestConfig) : string * Profile.Line list =
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

    status, Profile.read (Profile.pathIn runDir config.Project)

[<Fact(Timeout = 120_000)>]
let ``a project killed at its timeout still leaves a profile with its start, host pid, end and why no classes`` () =
    let status, lines = runOnce (project "-c \"echo started; exec sleep 60\"" (Some 2))

    test <@ status = "timed-out" @>

    match lines with
    | Profile.Start("Profiled.Tests", _, cores, Some 2, _) :: _ -> test <@ cores >= 1 @>
    | other -> failwith $"no start line: %A{other}"

    test
        <@
            lines
            |> List.exists (function
                | Profile.Spawn(_, pid) -> pid > 0
                | _ -> false)
        @>

    let ends =
        lines
        |> List.choose (function
            | Profile.End(_, wall, _, _, outcome) -> Some(wall, outcome)
            | _ -> None)

    match ends with
    | [ (wall, "timed-out") ] -> test <@ wall >= 2000L @>
    | other -> failwith $"unexpected end lines: %A{other}"

    test
        <@
            lines
            |> List.exists (function
                | Profile.ClassesUnavailable _ -> true
                | _ -> false)
        @>

[<Fact(Timeout = 120_000)>]
let ``a passing project's profile ends passed`` () =
    let status, lines = runOnce (project "-c \"exit 0\"" None)

    test <@ status = "passed" @>

    test
        <@
            lines
            |> List.exists (function
                | Profile.End(_, _, _, _, "passed") -> true
                | _ -> false)
        @>
