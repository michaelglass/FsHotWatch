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
    let root = Path.Combine(Path.GetTempPath(), "proc-" + Guid.NewGuid().ToString "N")
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
let ``terminate stops nothing when nothing it is given is alive`` () = test <@ Leftovers.terminate [] = 0 @>
