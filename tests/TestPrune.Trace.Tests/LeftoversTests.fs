/// How a launch finds what its run left running (AUTOMATION-1734), on either kind of process
/// table, on any machine: the `ps` and `lsof` listings are parsed from text, and a `/proc` tree is
/// a directory of real files and symbolic links, so the Linux code runs on macOS and the macOS
/// parsing on Linux.
module TestPrune.Trace.Tests.LeftoversTests

open System
open System.IO
open System.IO.Pipes
open Xunit
open Swensen.Unquote
open TestPrune.Trace

let private needle = $"%s{Leftovers.LaunchIdEnv}=L1"

[<Fact>]
let ``ps lists the processes whose environment names the launch, and no other`` () =
    let listing =
        String.concat
            "\n"
            [ $"  101 /bin/sh HOME=/h %s{needle} PATH=/bin"
              "  102 /bin/sh HOME=/h"
              $"  103 /bin/sh %s{Leftovers.LaunchIdEnv}=L10"
              $"  abc /bin/sh %s{needle}"
              "lonely"
              "" ]

    test <@ Leftovers.psCarrying needle listing = [ 101 ] @>

[<Fact>]
let ``lsof names our read ends' peers, and naming none is an error`` () =
    test <@ Leftovers.lsofPeers "p1\nf3\nn->0xA\nf4\nn->0xB\n" = Ok(Set.ofList [ "0xA"; "0xB" ]) @>
    test <@ Leftovers.lsofPeers "p1\nf3\nn/dev/null\n" = Error "lsof named no peer for the output pipes" @>

[<Fact>]
let ``lsof holders are the processes with a descriptor on a named write end, this one aside`` () =
    let self = 500

    let listing =
        String.concat
            "\n"
            [ "d0xA" // before any process: owned by none
              "p100"
              "f3"
              "d0xA" // 100 holds it
              "p200"
              "d0xC" // 200 holds another pipe
              "p500"
              "d0xA" // this process: aside
              "pxyz"
              "d0xA" ] // an unreadable pid: owned by none

    test <@ Leftovers.lsofHolders (Set.ofList [ "0xA" ]) self listing = [ 100 ] @>

/// A fake `/proc` tree under a fresh directory.
let private withProc (body: string -> unit) =
    let root = FsHotWatch.TestRoots.TestRoot.freshPath "proc"
    Directory.CreateDirectory root |> ignore

    try
        body root
    finally
        Directory.Delete(root, true)

let private dir (root: string) (parts: string list) =
    let path = Path.Combine(Array.ofList (root :: parts))
    Directory.CreateDirectory path |> ignore
    path

[<Fact>]
let ``a proc tree lists the processes whose environ names the launch`` () =
    withProc (fun root ->
        File.WriteAllText(Path.Combine(dir root [ "11" ], "environ"), $"HOME=/h\000%s{needle}\000")
        File.WriteAllText(Path.Combine(dir root [ "12" ], "environ"), "HOME=/h\000")
        dir root [ "13" ] |> ignore // exited since the listing: no environ
        dir root [ "self" ] |> ignore // not a pid
        test <@ Leftovers.carrying (Leftovers.Proc root) "L1" = Ok [ 11 ] @>)

[<Fact>]
let ``a proc tree's holders are the processes with a descriptor on our pipe, this one aside`` () =
    withProc (fun root ->
        use server = new AnonymousPipeServerStream(PipeDirection.In)
        let fd = int (server.SafePipeHandle.DangerousGetHandle())
        let self = Environment.ProcessId

        let link (pid: string) (n: int) (target: string) =
            File.CreateSymbolicLink(Path.Combine(dir root [ pid; "fd" ], string n), target)
            |> ignore

        link (string self) fd "pipe:[777]"
        link "21" 4 "pipe:[777]" // holds our pipe
        link "22" 4 "pipe:[888]" // holds another
        dir root [ "23" ] |> ignore // no fd directory to read
        dir root [ "net" ] |> ignore // not a pid
        test <@ Leftovers.holders (Leftovers.Proc root) [ server ] = Ok [ 21 ] @>)

[<Fact>]
let ``a stream that is a file names no pipe, and one that is neither is refused`` () =
    withProc (fun root ->
        let path = Path.Combine(root, "out.txt")
        use file = new FileStream(path, FileMode.Create)
        test <@ Leftovers.holders (Leftovers.Proc root) [ file ] = Ok [] @>
        use memory = new MemoryStream()

        test
            <@ Leftovers.holders (Leftovers.Proc root) [ memory ] = Error "the output is a MemoryStream, not a pipe" @>)

[<Fact>]
let ``a table whose tools cannot run says why, and lsof naming no peer is an error`` () =
    test
        <@
            match Leftovers.carrying (Leftovers.Tools("/nonexistent/ps", "/nonexistent/lsof")) "L1" with
            | Error why -> why.StartsWith "/nonexistent/ps could not be run"
            | Ok _ -> false
        @>

    // /bin/echo stands in for lsof: it runs, and prints no `n->` peer.
    use server = new AnonymousPipeServerStream(PipeDirection.In)

    test
        <@
            Leftovers.holders (Leftovers.Tools("/bin/ps", "/bin/echo")) [ server ] = Error
                "lsof named no peer for the output pipes"
        @>

[<Fact>]
let ``terminate stops nothing when nothing it is given is alive`` () =
    test <@ Leftovers.terminate Leftovers.current [] = Leftovers.Termination.Stopped 0 @>

/// A process as `terminateWith` reaches it: it ignores TERM, and KILL takes effect only after
/// `checksToDie` more checks of whether it runs, as a kernel finishes a kill (and a reaper
/// collects it) some time after the signal is sent.
type private DyingProcess(pid: int, checksToDie: int) =
    let mutable killed = false
    let mutable checksSinceKill = 0

    /// Whether it still runs, without counting as a check.
    member _.Running = not killed || checksSinceKill < checksToDie

    member this.Signals: Leftovers.Signals =
        { IsRunning =
            fun p ->
                let running = p = pid && this.Running

                if killed then
                    checksSinceKill <- checksSinceKill + 1

                running
          Send =
            fun p signal ->
                if p = pid && signal = 9 then
                    killed <- true }

// GitHub run 44083201746 (Linux x64): LaunchTests found the orphan it had just seen terminated still
// alive. `terminate` sent KILL and returned at once, so a caller told "stopped" could still see the
// process running.
[<Fact>]
let ``terminate returns only once a killed process is gone`` () =
    let dying = DyingProcess(4242, 3)
    test <@ Leftovers.terminateWith dying.Signals [ 4242 ] = Leftovers.Termination.Stopped 1 @>
    test <@ not dying.Running @>

[<Fact>]
let ``a process still running after KILL and its grace is reported as a survivor, not as stopped`` () =
    let unkillable = DyingProcess(4242, Int32.MaxValue)
    let other = DyingProcess(4343, 0)

    let signals: Leftovers.Signals =
        { IsRunning = fun p -> unkillable.Signals.IsRunning p || other.Signals.IsRunning p
          Send =
            fun p signal ->
                unkillable.Signals.Send p signal
                other.Signals.Send p signal }

    test <@ Leftovers.terminateWith signals [ 4242; 4343 ] = Leftovers.Termination.StillRunning(1, 4242, []) @>

[<Fact>]
let ``a termination names how many it stopped and what still runs`` () =
    test <@ Leftovers.Termination.stopped (Leftovers.Termination.Stopped 2) = 2 @>
    test <@ Leftovers.Termination.survivorsNote (Leftovers.Termination.Stopped 2) = "" @>

    let survived = Leftovers.Termination.StillRunning(1, 12, [ 13 ])
    test <@ Leftovers.Termination.stopped survived = 1 @>
    test <@ Leftovers.Termination.survivorsNote survived = "; still running after KILL: 12, 13" @>

[<Fact>]
let ``lsof's holders are the processes it lists on the peer of our read end`` () =
    // A stand-in lsof that answers both questions: our read end's peer, then who holds it.
    let dir = FsHotWatch.TestRoots.TestRoot.freshPath "lsof"
    Directory.CreateDirectory dir |> ignore
    let script = Path.Combine(dir, "lsof")

    File.WriteAllText(
        script,
        "#!/bin/sh\ncase \"$*\" in\n  *\"-F n\") printf 'p1\\nn->0xFAKE\\n' ;;\n  *) printf 'p4242\\nd0xFAKE\\np4343\\nd0xOTHER\\n' ;;\nesac\n"
    )

    File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    try
        use server = new AnonymousPipeServerStream(PipeDirection.In)
        test <@ Leftovers.holders (Leftovers.Tools("/bin/ps", script)) [ server ] = Ok [ 4242 ] @>
    finally
        Directory.Delete(dir, true)

/// A `/proc` tree holding one process's `stat`, under a fresh directory.
let private procWithStat (pid: int) (stat: string) =
    let root = FsHotWatch.TestRoots.TestRoot.freshPath "proc"
    Directory.CreateDirectory(Path.Combine(root, string pid)) |> ignore
    File.WriteAllText(Path.Combine(root, string pid, "stat"), stat)
    root

// A process KILLed after re-parenting stays a zombie until its reaper collects it, and signal 0
// still reaches a zombie: read as running, a slow reaper made a stopped process look alive.
[<Fact>]
let ``a zombie is not running, whatever its command name holds`` () =
    let self = Environment.ProcessId
    let zombie = procWithStat self $"%d{self} (a (b) Z) Z 1 1 1"
    let sleeping = procWithStat self $"%d{self} (a (b) Z) S 1 1 1"

    test <@ Leftovers.isZombieIn zombie self @>
    test <@ not (Leftovers.isZombieIn sleeping self) @>
    test <@ not (Leftovers.isZombieIn sleeping (self + 1)) @>
    test <@ not (Leftovers.isRunning (Leftovers.Proc zombie) self) @>
    test <@ Leftovers.isRunning (Leftovers.Proc sleeping) self @>
    test <@ Leftovers.isRunning (Leftovers.Tools("/bin/ps", "/usr/sbin/lsof")) self @>

[<Fact>]
let ``a process that has exited is not running`` () =
    use exited = Diagnostics.Process.Start("/bin/sh", [| "-c"; "exit 0" |])
    exited.WaitForExit()
    test <@ not (Leftovers.isRunning Leftovers.current exited.Id) @>
