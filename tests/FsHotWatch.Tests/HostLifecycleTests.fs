/// The repository host's lifecycle record: what attached, what was refused and why,
/// what ended and how, and whether the host before this one exited without saying so.
/// It is what a rollout reads to decide whether the host is healthy enough to keep.
module FsHotWatch.Tests.HostLifecycleTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.AttachHandshake
open FsHotWatch.HostLifecycle
open FsHotWatch.RepositoryIdentity
open FsHotWatch.SessionRegistry
open FsHotWatch.Tests.TestHelpers

let private at = DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc)

let private repository =
    (RepositoryId.tryParse "0123456789abcdef0123456789abcdef").Value

let private sessionId () =
    { Repository = repository
      Worktree = (WorktreeId.tryParse "fedcba9876543210fedcba9876543210").Value
      Incarnation = SessionIncarnation.mint () }

let private lines (path: string) =
    File.ReadAllLines path |> Array.filter (fun l -> l <> "") |> List.ofArray

let private field (line: string) (name: string) =
    use doc = JsonDocument.Parse line
    doc.RootElement.GetProperty(name).ToString()

[<Fact(Timeout = 5000)>]
let ``every event is one JSON line naming when, which host, and what happened`` () =
    withTempDir "lifecycle" (fun dir ->
        let path = Path.Combine(dir, "host-events.jsonl")
        let log = EventLog(Some path, 41, (fun () -> at))
        let id = sessionId ()
        log.Start()
        log.Record(HostEvent.Attached(id, "/r", AttachDisposition.NewSession))
        log.Record(HostEvent.Refused("binary-mismatch", "/r/.workspaces/b"))
        log.Record(HostEvent.SessionEnded(id, "/r", SessionEnd.Faulted "boom"))
        log.Record(HostEvent.Undelivered id)
        log.Record(HostEvent.HostStopped StopReason.Idle)

        let written = lines path

        let names = written |> List.map (fun l -> field l "event")

        test
            <@
                names = [ "hostStarted"
                          "attached"
                          "refused"
                          "sessionEnded"
                          "undelivered"
                          "hostStopped" ]
            @>

        test <@ written |> List.forall (fun l -> field l "hostPid" = "41") @>

        test
            <@
                written
                |> List.forall (fun l -> DateTime.Parse(field l "at").ToUniversalTime() = at)
            @>

        let attached = written[1]
        test <@ field attached "session" = SessionId.render id @>
        test <@ field attached "root" = "/r" @>
        test <@ field attached "disposition" = "new" @>
        test <@ field written[2] "kind" = "binary-mismatch" @>
        test <@ field written[2] "root" = "/r/.workspaces/b" @>
        test <@ field written[3] "outcome" = "faulted" @>
        test <@ field written[3] "detail" = "boom" @>
        test <@ field written[5] "reason" = "idle" @>)

[<Fact(Timeout = 5000)>]
let ``the tally counts attaches by disposition, refusals by kind, and ends by outcome`` () =
    let log = EventLog(None, 1, (fun () -> at))
    let id = sessionId ()
    log.Start()
    log.Record(HostEvent.Attached(id, "/r", AttachDisposition.NewSession))
    log.Record(HostEvent.Attached(id, "/r", AttachDisposition.NewSession))
    log.Record(HostEvent.Attached(id, "/r", AttachDisposition.Rejoined))
    log.Record(HostEvent.Attached(id, "/r", AttachDisposition.RejoinedConfigChanged))
    log.Record(HostEvent.Refused("binary-mismatch", "/r"))
    log.Record(HostEvent.Refused("binary-mismatch", "/r"))
    log.Record(HostEvent.Refused("toolchain-mismatch", "/r"))
    log.Record(HostEvent.SessionEnded(id, "/r", SessionEnd.Stopped))
    log.Record(HostEvent.SessionEnded(id, "/r", SessionEnd.Faulted "x"))
    log.Record(HostEvent.Undelivered id)

    test
        <@
            log.Tally = { Attached = 2L
                          Rejoined = 1L
                          Reloaded = 1L
                          Refused = Map.ofList [ "binary-mismatch", 2L; "toolchain-mismatch", 1L ]
                          Stopped = 1L
                          Faulted = 1L
                          Undelivered = 1L
                          PreviousUnclean = None }
        @>

[<Fact(Timeout = 5000)>]
let ``a host started after one that recorded its stop finds the previous exit clean`` () =
    withTempDir "lifecycle" (fun dir ->
        let path = Path.Combine(dir, "host-events.jsonl")
        let first = EventLog(Some path, 10, (fun () -> at))
        first.Start()
        first.Record(HostEvent.HostStopped StopReason.Requested)

        let second = EventLog(Some path, 11, (fun () -> at))
        second.Start()
        test <@ second.Tally.PreviousUnclean = None @>
        test <@ lines path |> List.map (fun l -> field l "event") |> List.last = "hostStarted" @>)

[<Fact(Timeout = 5000)>]
let ``a host started after one that never recorded its stop records that, naming the previous pid`` () =
    withTempDir "lifecycle" (fun dir ->
        let path = Path.Combine(dir, "host-events.jsonl")
        let first = EventLog(Some path, 10, (fun () -> at))
        first.Start()
        first.Record(HostEvent.Attached(sessionId (), "/r", AttachDisposition.NewSession))
        // No stop: the process was killed, or ran out of memory.

        let second = EventLog(Some path, 11, (fun () -> at))
        second.Start()
        test <@ second.Tally.PreviousUnclean = Some(Some 10) @>
        let written = lines path

        let unclean =
            written |> List.find (fun l -> field l "event" = "previousHostUnclean")

        test <@ field unclean "previousPid" = "10" @>
        test <@ field unclean "hostPid" = "11" @>
        test <@ field (List.last written) "event" = "hostStarted" @>)

[<Fact(Timeout = 5000)>]
let ``a torn last line does not hide which host exited uncleanly`` () =
    withTempDir "lifecycle" (fun dir ->
        let path = Path.Combine(dir, "host-events.jsonl")
        File.WriteAllText(path, "{\"event\":\"hostStarted\",\"hostPid\":10}\n{\"event\":\"att")
        let log = EventLog(Some path, 11, (fun () -> at))
        log.Start()
        test <@ log.Tally.PreviousUnclean = Some(Some 10) @>)

[<Fact(Timeout = 5000)>]
let ``a session end recorded after its host's stop leaves the exit clean`` () =
    withTempDir "lifecycle" (fun dir ->
        let path = Path.Combine(dir, "host-events.jsonl")
        let first = EventLog(Some path, 10, (fun () -> at))
        first.Start()
        first.Record(HostEvent.HostStopped StopReason.Idle)
        first.Record(HostEvent.SessionEnded(sessionId (), "/r", SessionEnd.Stopped))

        let second = EventLog(Some path, 11, (fun () -> at))
        second.Start()
        test <@ second.Tally.PreviousUnclean = None @>)

[<Fact(Timeout = 5000)>]
let ``a log with lines but no host mark is an unclean exit of an unknown host`` () =
    withTempDir "lifecycle" (fun dir ->
        let path = Path.Combine(dir, "host-events.jsonl")
        File.WriteAllText(path, "not json\n{\"event\":\"attached\"}\n")
        let log = EventLog(Some path, 11, (fun () -> at))
        log.Start()
        test <@ log.Tally.PreviousUnclean = Some None @>)

[<Fact(Timeout = 5000)>]
let ``the first host of a repository has no previous exit`` () =
    withTempDir "lifecycle" (fun dir ->
        let log = EventLog(Some(Path.Combine(dir, "host-events.jsonl")), 11, (fun () -> at))
        log.Start()
        test <@ log.Tally.PreviousUnclean = None @>)

[<Fact(Timeout = 5000)>]
let ``a log past its bound is rotated at start, keeping one previous generation`` () =
    withTempDir "lifecycle" (fun dir ->
        let path = Path.Combine(dir, "host-events.jsonl")
        let old = String('x', 64) + "\n"
        File.WriteAllText(path, String.replicate 20 old + "{\"event\":\"hostStopped\",\"hostPid\":10}\n")
        let log = EventLog(Some path, 11, (fun () -> at), 1024L)
        log.Start()
        test <@ File.Exists(path + ".1") @>
        test <@ (File.ReadAllText(path + ".1")).StartsWith old @>
        test <@ lines path |> List.map (fun l -> field l "event") = [ "hostStarted" ] @>
        // Read before rotating: the previous host's stop still counts.
        test <@ log.Tally.PreviousUnclean = None @>)

[<Fact(Timeout = 5000)>]
let ``a log that cannot be written still counts, and never raises`` () =
    withTempDir "lifecycle" (fun dir ->
        // A directory where the file should be: every append fails.
        let path = Path.Combine(dir, "host-events.jsonl")
        Directory.CreateDirectory path |> ignore
        let log = EventLog(Some path, 11, (fun () -> at))
        log.Start()
        log.Record(HostEvent.Attached(sessionId (), "/r", AttachDisposition.NewSession))
        test <@ log.Tally.Attached = 1L @>)

[<Fact(Timeout = 5000)>]
let ``the tally renders as JSON for status --repository`` () =
    let tally =
        { Attached = 2L
          Rejoined = 1L
          Reloaded = 0L
          Refused = Map.ofList [ "binary-mismatch", 3L ]
          Stopped = 1L
          Faulted = 1L
          Undelivered = 0L
          PreviousUnclean = Some(Some 9) }

    let json = (Tally.toJson "/state/host-events.jsonl" tally).ToJsonString()

    test
        <@
            json = """{"attached":2,"rejoined":1,"reloaded":0,"refused":{"binary-mismatch":3},"ended":{"stopped":1,"faulted":1},"undelivered":0,"previousHostUnclean":true,"previousHostPid":9,"eventLog":"/state/host-events.jsonl"}"""
        @>

    let clean = (Tally.toJson "/l" { tally with PreviousUnclean = None }).ToJsonString()

    test
        <@
            clean.Contains "\"previousHostUnclean\":false"
            && not (clean.Contains "previousHostPid")
        @>

    let unknown =
        (Tally.toJson
            "/l"
            { tally with
                PreviousUnclean = Some None })
            .ToJsonString()

    test
        <@
            unknown.Contains "\"previousHostUnclean\":true"
            && not (unknown.Contains "previousHostPid")
        @>

[<Fact(Timeout = 5000)>]
let ``the event log lives in the repository's control directory`` () =
    let control = repositoryControlPaths "/state" repository
    test <@ eventLogPath control = Path.Combine(control.Directory, "host-events.jsonl") @>

[<Fact(Timeout = 5000)>]
let ``a log that cannot be read says nothing of the previous host, and the host still starts`` () =
    if OperatingSystem.IsWindows() then
        Assert.Skip "POSIX permissions"

    withTempDir "lifecycle" (fun dir ->
        let path = Path.Combine(dir, "host-events.jsonl")
        File.WriteAllText(path, "{\"event\":\"hostStarted\",\"hostPid\":10}\n")
        File.SetUnixFileMode(path, UnixFileMode.None)

        let readable =
            try
                File.ReadAllText path |> ignore
                true
            with :? UnauthorizedAccessException ->
                false

        if readable then
            Assert.Skip "this process reads any file (running as root)"

        let log = EventLog(Some path, 11, (fun () -> at))
        log.Start()
        test <@ log.Tally.PreviousUnclean = None @>)
