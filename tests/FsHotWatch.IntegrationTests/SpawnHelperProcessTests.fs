// The spawn helper through a REAL helper process. The launch and loss paths log, so the
// class shares the serialized logging collection.
//
// Integration suite, not FsHotWatch.Tests (AUTOMATION-1651): the helper runs the
// coverage-instrumented CLI, so what it executed was graded as unit coverage that came and
// went with the helper's timing. The unit suite covers the helper's code in process.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.SpawnHelperProcessTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Xunit
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestHelpers

let private isGone (pid: int) =
    SpinWait.SpinUntil((fun () -> isProcessAlive pid = Liveness.Gone), 10000)

[<Fact(Timeout = 60000)>]
let ``a hook step started through the real helper is the helper's child, not ours`` () =
    let psi = SpawnHelperMode.defaultStartInfo ()
    Assert.True(File.Exists psi.FileName, $"the dotnet host must exist: %s{psi.FileName}")
    Assert.EndsWith("FsHotWatch.Cli.dll", psi.ArgumentList[0])

    match SpawnHelperMode.launch SpawnHelperMode.defaultStartInfo () with
    | Error reason -> failwith $"the helper must start: %s{reason}"
    | Ok running ->
        try
            use _ = SpawnHelper.install running.Connection
            let registry = ProcessRegistry.Registry()
            use _ = ProcessRegistry.install registry

            match
                runProcessObserved
                    ignore
                    "/bin/sh"
                    "-c \"echo $PPID\""
                    "/"
                    []
                    (ProcessBounds.silent (TimeSpan.FromSeconds 30.0))
            with
            | Succeeded(ProcessOutput.Drained parent) ->
                Assert.Equal(string running.Pid, parent)
                Assert.NotEqual<string>(string Environment.ProcessId, parent)
            | other -> failwith $"expected success, got %A{other}"
        finally
            running.Stop()

        Assert.True(isGone running.Pid, "a stopped helper must exit")

[<Fact(Timeout = 60000)>]
let ``a CLI started with SIGINT ignored starts children that SIGINT still stops`` () =
    // A shell's background job starts with SIGINT ignored, and so does the daemon the
    // detached launch backgrounds; an ignored disposition survives fork and exec into
    // every child. The CLI restores the default at entry, so its children can be
    // interrupted whatever its parent did.
    let ignoringParent () =
        let psi = SpawnHelperMode.defaultStartInfo ()
        let wrapped = ProcessStartInfo("/bin/sh")
        wrapped.ArgumentList.Add "-c"
        wrapped.ArgumentList.Add "trap '' INT; exec \"$0\" \"$@\""
        wrapped.ArgumentList.Add psi.FileName

        for arg in psi.ArgumentList do
            wrapped.ArgumentList.Add arg

        wrapped.UseShellExecute <- false
        wrapped.RedirectStandardInput <- true
        wrapped.RedirectStandardOutput <- true

        for KeyValue(key, value) in psi.Environment do
            wrapped.Environment[key] <- value

        wrapped

    match SpawnHelperMode.launch ignoringParent () with
    | Error reason -> failwith $"the helper must start: %s{reason}"
    | Ok running ->
        try
            use _ = SpawnHelper.install running.Connection
            let registry = ProcessRegistry.Registry()
            use _ = ProcessRegistry.install registry
            let mutable signalled = Ok()

            let outcome =
                runProcessObserved
                    (fun pid -> signalled <- sendInterrupt pid)
                    "sleep"
                    "30"
                    "/"
                    []
                    (ProcessBounds.silent (TimeSpan.FromSeconds 15.0))

            Assert.Equal(Ok(), signalled)

            match outcome with
            | TimedOut _ -> failwith "the child ignored SIGINT: it inherited the ignored disposition"
            | _ -> ()
        finally
            running.Stop()
