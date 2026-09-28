/// A daemon whose root is deleted shuts down once, and one whose root is there never does.
module FsHotWatch.Tests.VanishedRootTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 15000)>]
let ``a root that goes missing shuts the daemon down exactly once`` () =
    let exists = ref true
    let shutdowns = ref 0
    let count () = Volatile.Read &shutdowns.contents
    let logged = Collections.Concurrent.ConcurrentQueue<string>()

    use _ =
        VanishedRoot.watch
            (TimeSpan.FromMilliseconds 20.0)
            (fun () -> Volatile.Read &exists.contents)
            (fun () -> Interlocked.Increment &shutdowns.contents |> ignore)
            logged.Enqueue

    Thread.Sleep 200
    test <@ count () = 0 @>

    Volatile.Write(&exists.contents, false)
    test <@ waitUntilTrue (fun () -> count () = 1) 10000 @>

    // Later ticks still see no root, and shut down nothing more.
    Thread.Sleep 200
    test <@ count () = 1 @>
    test <@ logged |> Seq.exists (fun l -> l.Contains "is gone") @>

[<Fact(Timeout = 15000)>]
let ``a marked root is present until it is deleted, even when something recreates it`` () =
    withTempDir "vanished" (fun dir ->
        let root = Path.Combine(dir, "r")
        Directory.CreateDirectory root |> ignore
        let present = VanishedRoot.mark root ignore
        test <@ present () @>

        Directory.Delete(root, true)
        test <@ not (present ()) @>

        // The daemon's own next write brings the directory back, with no token in it.
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore
        File.WriteAllText(Path.Combine(FsHwPaths.root root, "scan-metrics.jsonl"), "")
        test <@ not (present ()) @>

        // Nor does another daemon's token make it this one's.
        VanishedRoot.mark root ignore |> ignore
        test <@ not (present ()) @>)

[<Fact(Timeout = 15000)>]
let ``a root that cannot be marked is logged, and always reads as present`` () =
    withTempDir "vanished" (fun dir ->
        let logged = Collections.Concurrent.ConcurrentQueue<string>()
        let missing = Path.Combine(dir, "never-created")
        let present = VanishedRoot.mark missing logged.Enqueue
        test <@ present () @>
        test <@ logged |> Seq.exists (fun l -> l.Contains "cannot mark") @>
        test <@ not (Directory.Exists missing) @>)
