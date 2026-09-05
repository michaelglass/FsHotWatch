/// AUTOMATION-573 — the run CLAIM, on its own terms.
///
/// `VerdictTests` covers what a claim MEANS to a reader of the verdict. This file
/// covers the claim itself: its wire format, when it is still held, and what happens to
/// one whose process is gone. The liveness policy is driven PURELY (host and probe
/// injected), because the interesting cases — a dead pid, a claim from another machine —
/// cannot be staged by starting real processes.
module FsHotWatch.Tests.RunClaimTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli
open FsHotWatch.Tests.TestHelpers

let private aClaim =
    { RunClaim.InvocationId = "inv-1"
      RunClaim.Pid = 4242
      RunClaim.Host = "some-box"
      RunClaim.Command = "confirm"
      RunClaim.StartedAtUtc = DateTime(2026, 9, 5, 11, 30, 0, DateTimeKind.Utc) }

[<Fact>]
let ``a claim round-trips through its wire format`` () =
    match RunClaim.deserialize (RunClaim.serialize aClaim) with
    | Some c ->
        test <@ c.InvocationId = aClaim.InvocationId @>
        test <@ c.Pid = aClaim.Pid @>
        test <@ c.Host = aClaim.Host @>
        test <@ c.Command = aClaim.Command @>
        test <@ c.StartedAtUtc = aClaim.StartedAtUtc @>
    | None -> failwith "a claim we just serialized must parse"

[<Fact>]
let ``a claim from an unknown schema does not parse`` () =
    // A future shape change must be a DECLARED break, not a misparse that reads some of
    // the fields and invents the rest.
    let json =
        (RunClaim.serialize aClaim).Replace(RunClaim.Schema, "fshw-run-claim-v99")

    test <@ RunClaim.deserialize json = None @>

[<Fact>]
let ``a claim missing a field does not parse`` () =
    test <@ RunClaim.deserialize """{"schema":"fshw-run-claim-v1","invocationId":"x"}""" = None @>
    test <@ RunClaim.deserialize "not json at all" = None @>

[<Fact>]
let ``a claim whose process is provably gone is not held`` () =
    let dead (_: int) = false
    test <@ not (RunClaim.isHeld "some-box" dead aClaim) @>

[<Fact>]
let ``a claim whose process is alive is held`` () =
    let alive (_: int) = true
    test <@ RunClaim.isHeld "some-box" alive aClaim @>

[<Fact>]
let ``a claim from ANOTHER host is held — a pid we cannot probe is not a pid we may release`` () =
    // The unknowns all lean HELD, the same lean `daemonProcessAliveWith` takes and, here,
    // the fail-CLOSED one: a claim wrongly kept costs a re-read, while a claim wrongly
    // released hands back the stale green this whole feature exists to stop.
    let dead (_: int) = false
    test <@ RunClaim.isHeld "this-box" dead aClaim @>

[<Fact>]
let ``host comparison ignores case — the same box under a different spelling is the same box`` () =
    let dead (_: int) = false
    test <@ not (RunClaim.isHeld "SOME-BOX" dead aClaim) @>

[<Fact>]
let ``an unparseable claim file is held, not dropped — unknown WHO, never unknown WHETHER`` () =
    let dead (_: int) = false

    let held, abandoned =
        RunClaim.liveIn "some-box" dead [ "/x/torn.json", """{"schema":"fshw-run-cl""" ]

    test <@ List.isEmpty abandoned @>

    match held with
    | [ c ] ->
        // What a reader is SHOWN names the fault and the file it is in.
        test <@ c.Command.Contains "unreadable claim" @>
        test <@ c.Command.Contains "torn.json" @>
        // And the id can never collide with a real invocation, so the sentinel cannot be
        // mistaken for the run that published the verdict.
        test <@ c.InvocationId.StartsWith "unreadable:" @>
        // The empty host matches no machine, so no liveness probe can release it. Only
        // its owner deleting the file, or a hand, clears it.
        test <@ c.Host = "" @>
    | other -> failwith $"an unreadable claim must survive as one claim, got %A{other}"

[<Fact>]
let ``liveIn separates the held from the provably abandoned, in order`` () =
    let aliveOnly (pid: int) = pid = 1

    let files =
        [ "/x/a.json",
          RunClaim.serialize
              { aClaim with
                  InvocationId = "a"
                  Pid = 1 }
          "/x/b.json",
          RunClaim.serialize
              { aClaim with
                  InvocationId = "b"
                  Pid = 2 }
          "/x/c.json",
          RunClaim.serialize
              { aClaim with
                  InvocationId = "c"
                  Pid = 1 } ]

    let held, abandoned = RunClaim.liveIn "some-box" aliveOnly files

    test <@ held |> List.map (fun c -> c.InvocationId) = [ "a"; "c" ] @>
    test <@ abandoned = [ "/x/b.json" ] @>

[<Fact>]
let ``acquire then release leaves no claim behind`` () =
    withTempDir "run-claim-lifecycle" (fun root ->
        let claim = RunClaim.acquire root "check" "inv-lifecycle"
        test <@ claim.IsSome @>
        test <@ (RunClaim.live root |> List.map (fun c -> c.InvocationId)) = [ "inv-lifecycle" ] @>

        RunClaim.release root claim
        test <@ RunClaim.live root = [] @>

        // Releasing twice is silent — the bracket latches this, and a signal can reach it
        // after the ordinary path already did.
        RunClaim.release root claim
        test <@ RunClaim.live root = [] @>)

[<Fact>]
let ``a release touches only its OWN claim — two concurrent runs cannot clear each other`` () =
    // The reason claims are one file per invocation rather than one shared file: with a
    // shared file the second claimant's release would clear the marker while the first
    // was still running, opening a hole in exactly the window the marker covers.
    withTempDir "run-claim-concurrent" (fun root ->
        let first = RunClaim.acquire root "check" "inv-first"
        let second = RunClaim.acquire root "confirm" "inv-second"
        test <@ (RunClaim.live root |> List.length) = 2 @>

        RunClaim.release root second
        test <@ (RunClaim.live root |> List.map (fun c -> c.InvocationId)) = [ "inv-first" ] @>

        RunClaim.release root first
        test <@ RunClaim.live root = [] @>)

[<Fact>]
let ``no claims directory is no claims — and does not throw`` () =
    withTempDir "run-claim-absent" (fun root ->
        test <@ not (Directory.Exists(RunClaim.dirPath root)) @>
        test <@ RunClaim.live root = [] @>)

[<Fact>]
let ``describe names the run without making a reader open the file`` () =
    let line = RunClaim.describe aClaim
    test <@ line.Contains "confirm" @>
    test <@ line.Contains "4242" @>
    test <@ line.Contains "some-box" @>

[<Fact>]
let ``describe states no pid, host or time for a claim it could not read`` () =
    // An absence dressed up as three facts sends a reader chasing "pid 0 on , started
    // 00:54:00" — a process, a machine and a moment, none of which exist.
    let line = RunClaim.describe (RunClaim.unreadableClaim "torn.json")
    test <@ line.Contains "unreadable claim" @>
    test <@ line.Contains "torn.json" @>
    test <@ not (line.Contains "pid") @>
    test <@ not (line.Contains "started") @>
