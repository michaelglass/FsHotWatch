/// `HangEvidence` captures what a stuck child tree was doing, and a capture that cannot
/// do part of its job still returns and says so.
module FsHotWatch.Tests.HangEvidenceTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 5000)>]
let ``the child tree is every descendant of the root, and nothing beside it`` () =
    let rows =
        HangEvidence.parsePs
            "  10     1  00:05 S  /bin/zsh\n  11    10  00:04 S  dotnet run\n  12    11  00:03 S  dotnet Lib.Tests.dll\n  13     1  00:02 S  other\n garbage\n"

    test <@ HangEvidence.descendants 10 rows |> List.map fst = [ 11; 12 ] @>
    test <@ snd (List.head (HangEvidence.descendants 10 rows)) = "11    10  00:04 S  dotnet run" @>
    test <@ List.isEmpty (HangEvidence.descendants 13 rows) @>

let private alive (pid: int) =
    try
        use p = Process.GetProcessById pid
        not p.HasExited
    with :? ArgumentException ->
        false

let private realRun: HangEvidence.Runner =
    fun command args ->
        match
            ProcessHelper.runProcess
                command
                args
                (Path.GetTempPath())
                []
                (ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 20.0))
        with
        | ProcessHelper.Succeeded output -> Ok(ProcessHelper.ProcessOutput.text output)
        | other -> Error $"%A{other}"

[<Fact(Timeout = 60000)>]
let ``a capture lists a live child, takes its stacks, and keeps the named logs`` () =
    withTempDir "hang-evidence" (fun dir ->
        let logs = Path.Combine(dir, "w", ".fshw", "test-runs", "r1")
        Directory.CreateDirectory logs |> ignore
        File.WriteAllText(Path.Combine(logs, "Lib.Tests.output.log"), "add adds: passed")

        use child = Process.Start(ProcessStartInfo("sleep", "30", UseShellExecute = false))

        try
            let out = Path.Combine(dir, "evidence")

            let errors =
                HangEvidence.captureTo realRun alive out Environment.ProcessId [ Path.Combine(dir, "w") ]

            let tree = File.ReadAllText(Path.Combine(out, "tree.txt"))
            let kept = Directory.GetFiles out |> Array.map Path.GetFileName

            test <@ tree.Contains $"%d{child.Id} " && tree.Contains "sleep 30" @>

            test
                <@
                    kept
                    |> Array.exists (fun f -> f.EndsWith("Lib.Tests.output.log", StringComparison.Ordinal))
                @>

            if RuntimeInformation.IsOSPlatform OSPlatform.OSX then
                test <@ File.Exists(Path.Combine(out, $"sample-%d{child.Id}.txt")) @>

            // Only this test's own child is asserted on: in a parallel suite the tree also
            // holds other tests' children, which come and go during the capture.
            test <@ errors |> List.forall (fun e -> not (e.Contains $" %d{child.Id}:")) @>
        finally
            child.Kill()
            child.WaitForExit())

[<Fact(Timeout = 30000)>]
let ``a child that exits between the listing and its stacks is a skip, not an error`` () =
    // In a parallel suite the tree holds other tests' short-lived children: one listed by
    // `ps` can be gone by the time `sample` or `createdump` reaches it. That is a race the
    // capture expects, not a failure of it.
    // Both platforms' stack steps, whichever this one is: `sample` runs only on macOS, so a
    // test that leaned on it held there and failed on Linux.
    for onMac in [ true; false ] do
        withTempDir "hang-evidence-gone" (fun dir ->
            let gone = 4242

            let run: HangEvidence.Runner =
                fun command _ ->
                    match command with
                    | "ps" -> Ok $"%d{gone} %d{Environment.ProcessId} 00:01 S dotnet Lib.Tests.dll\n"
                    | other -> Error $"%s{other}: no longer appears to be running"

            let out = Path.Combine(dir, "evidence")

            let errors =
                HangEvidence.captureToOn onMac run (fun pid -> pid <> gone) out Environment.ProcessId []

            test <@ List.isEmpty errors @>
            test <@ not (File.Exists(Path.Combine(out, "capture-errors.txt"))) @>
            test <@ File.ReadAllText(Path.Combine(out, "capture-skips.txt")).Contains $"%d{gone} exited" @>

            // A child still alive whose stacks could not be taken is a real failure.
            let stillThere =
                HangEvidence.captureToOn
                    onMac
                    run
                    (fun _ -> true)
                    (Path.Combine(dir, "again"))
                    Environment.ProcessId
                    []

            // Every platform dumps the .NET child; only macOS also samples it.
            let failedSteps =
                stillThere |> List.map (fun e -> e.Substring(0, e.IndexOf ':')) |> List.sort

            let expected =
                if onMac then
                    [ $"createdump %d{gone}"; $"sample %d{gone}" ]
                else
                    [ $"createdump %d{gone}" ]

            test <@ failedSteps = expected @>)

[<Fact(Timeout = 30000)>]
let ``a capture whose process listing fails still returns, and records why`` () =
    withTempDir "hang-evidence-fails" (fun dir ->
        let failing: HangEvidence.Runner =
            fun command _ -> Error $"%s{command} is unavailable"

        let out = Path.Combine(dir, "evidence")

        let errors =
            HangEvidence.captureTo failing (fun _ -> true) out Environment.ProcessId [ Path.Combine(dir, "missing") ]

        test <@ errors = [ "ps: ps is unavailable" ] @>
        test <@ File.ReadAllText(Path.Combine(out, "capture-errors.txt")).Contains "ps is unavailable" @>
        test <@ File.ReadAllText(Path.Combine(out, "tree.txt")) = "" @>)

[<Fact(Timeout = 30000)>]
let ``a timeout is rethrown naming its evidence; any other failure passes through untouched`` () =
    let captured = ResizeArray<string * string list>()

    let fakeCapture label keep =
        captured.Add((label, keep))
        "/evidence/here"

    let timedOut =
        Assert.Throws<TimeoutException>(
            Action(fun () ->
                HangEvidence.onExpiryWith fakeCapture "settle" (fun () -> [ "w" ]) (fun () ->
                    raise (AggregateException(TimeoutException "WaitForComplete timed out"))))
        )

    test <@ timedOut.Message.Contains "WaitForComplete timed out" @>
    test <@ timedOut.Message.EndsWith("evidence captured in /evidence/here", StringComparison.Ordinal) @>
    test <@ List.ofSeq captured = [ "settle", [ "w" ] ] @>

    Assert.Throws<InvalidOperationException>(
        Action(fun () ->
            HangEvidence.onExpiryWith fakeCapture "settle" (fun () -> []) (fun () ->
                raise (InvalidOperationException "not a timeout")))
    )
    |> ignore

    test <@ captured.Count = 1 @>
    test <@ HangEvidence.onExpiryWith fakeCapture "settle" (fun () -> []) (fun () -> 42) = 42 @>
