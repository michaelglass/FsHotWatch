module FsHotWatch.Tests.TimeoutMarkerTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.ProcessHelper
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestHelpers

let private spawned = DateTime(2026, 10, 5, 22, 21, 11, DateTimeKind.Utc)

let private stopped exited : TreeInterrupt =
    { Signal = "SIGTERM"
      At = spawned.AddSeconds 900.0
      Signalled = [ 41; 42 ]
      Refused = []
      Waited = TimeSpan.FromSeconds 1.5
      Exited = exited }

let private teardown (interrupt: TreeInterrupt option) survivors : TreeTeardown =
    { RootPid = 41
      Interrupt = interrupt
      Tree = Ok []
      KillTook = TimeSpan.FromMilliseconds 5.0
      Kill = KillOutcome.AlreadyExited
      Survivors = survivors }

let private facts kill teardown : TimeoutMarker.Facts =
    { Project = "Slow.Tests"
      After = TimeSpan.FromSeconds 900.0
      SpawnedAt = spawned
      Kill = kill
      Teardown = teardown }

[<Fact>]
let ``the marker sits beside the project's report and log`` () =
    test <@ TimeoutMarker.pathIn "/r/run" "Slow.Tests" = Path.Combine("/r/run", "Slow.Tests.timed-out.json") @>

[<Fact>]
let ``the message names the budget, the spawn time and a host that stopped on its signal`` () =
    let m =
        TimeoutMarker.message (facts KillOutcome.AlreadyExited (Some(teardown (Some(stopped true)) (Ok []))))

    test
        <@ m = "TIMED OUT after 900s (spawned at 2026-10-05T22:21:11.0000000Z; host sent SIGTERM, exited within 1.5s)" @>

[<Fact>]
let ``the message says when the host outlived its grace and was killed`` () =
    let m =
        TimeoutMarker.message (facts KillOutcome.Killed (Some(teardown (Some(stopped false)) (Ok []))))

    test <@ m.EndsWith "host sent SIGTERM, still running after 1.5s, then killed)" @>

[<Fact>]
let ``the message says a host was killed outright when nothing was signalled`` () =
    test <@ (TimeoutMarker.message (facts KillOutcome.Killed None)).EndsWith "; host killed)" @>

[<Fact>]
let ``the message carries a failed kill`` () =
    let m = TimeoutMarker.message (facts (KillOutcome.KillFailed(exn "EPERM")) None)
    test <@ m.EndsWith "(KILL FAILED — process tree STILL RUNNING)" @>

[<Fact>]
let ``the marker records the stop, the kill and the survivors`` () =
    let json =
        TimeoutMarker.render (facts KillOutcome.Killed (Some(teardown (Some(stopped false)) (Ok []))))

    // `JsonElement` is a struct: read every value out before quoting it (FS3155).
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    let stop = root.GetProperty "stop"
    let project = root.GetProperty("project").GetString()
    let timeoutSec = root.GetProperty("timeoutSec").GetInt32()
    let spawnedAt = root.GetProperty("spawnedAt").GetString()
    let message = root.GetProperty("message").GetString()
    let kill = root.GetProperty("kill").GetString()
    let signal = stop.GetProperty("signal").GetString()
    let signalled = stop.GetProperty("signalled").GetInt32()
    let exited = stop.GetProperty("exited").GetBoolean()
    let waitedMs = stop.GetProperty("waitedMs").GetInt32()
    let survivors = root.GetProperty("survivors").GetArrayLength()
    test <@ project = "Slow.Tests" @>
    test <@ timeoutSec = 900 @>
    test <@ spawnedAt = "2026-10-05T22:21:11.0000000Z" @>
    test <@ message.StartsWith "TIMED OUT after 900s" @>
    test <@ kill = "killed" @>
    test <@ signal = "SIGTERM" @>
    test <@ signalled = 2 @>
    test <@ not exited @>
    test <@ waitedMs = 1500 @>
    test <@ survivors = 0 @>

[<Fact>]
let ``an unaccounted teardown records no stop and unknown survivors`` () =
    let rendered (kill, t) =
        use doc = JsonDocument.Parse(TimeoutMarker.render (facts kill t))

        doc.RootElement.GetProperty("stop").ValueKind,
        doc.RootElement.GetProperty("survivors").ValueKind,
        doc.RootElement.GetProperty("kill").GetString()

    test <@ rendered (KillOutcome.AlreadyExited, None) = (JsonValueKind.Null, JsonValueKind.Null, "already-exited") @>

    test
        <@
            rendered (KillOutcome.KillTimedOut(TimeSpan.FromSeconds 10.0), Some(teardown None (Error "no table"))) = (JsonValueKind.Null,
                                                                                                                      JsonValueKind.Null,
                                                                                                                      "kill-timed-out")
        @>

    test
        <@
            rendered (KillOutcome.KillFailed(exn "x"), None)
            |> fun (_, _, k) -> k = "kill-failed"
        @>

[<Fact>]
let ``write puts the marker in the run directory`` () =
    withTempDir "timeout-marker" (fun dir ->
        let f = facts KillOutcome.Killed None
        TimeoutMarker.write dir f
        test <@ File.ReadAllText(TimeoutMarker.pathIn dir "Slow.Tests") = TimeoutMarker.render f @>)

[<Fact>]
let ``write survives a run directory the filesystem refuses`` () =
    withTempDir "timeout-marker-refused" (fun dir ->
        // A FILE where the run directory should be: creating the directory fails.
        let blocker = Path.Combine(dir, "blocker")
        File.WriteAllText(blocker, "")
        let refused = Path.Combine(blocker, "run")
        TimeoutMarker.write refused (facts KillOutcome.Killed None)
        test <@ not (File.Exists(TimeoutMarker.pathIn refused "Slow.Tests")) @>)
