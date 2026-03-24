module FsHotWatch.Tests.DaemonTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Daemon
open FsHotWatch.Events
open FsHotWatch.Plugin

/// A null checker is fine for tests that don't perform actual compilation.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

[<Fact>]
let ``daemon starts and stops without error`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let cts = new CancellationTokenSource()

    try
        let daemon = Daemon.createWith nullChecker tmpDir
        let task = Async.StartAsTask(daemon.Run(cts.Token))
        Thread.Sleep(500)
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``daemon dispatches file change events to plugins`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let mutable receivedChanges = []
    let cts = new CancellationTokenSource()

    try
        let daemon = Daemon.createWith nullChecker tmpDir

        let plugin =
            { new IFsHotWatchPlugin with
                member _.Name = "test-recorder"

                member _.Initialize(ctx) =
                    ctx.OnFileChanged.Add(fun change -> receivedChanges <- change :: receivedChanges)

                member _.Dispose() = () }

        daemon.Register(plugin)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        Thread.Sleep(500)

        // Write a file to trigger the watcher
        File.WriteAllText(Path.Combine(tmpDir, "src", "New.fs"), "module New")
        Thread.Sleep(1500)

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ receivedChanges.Length >= 1 @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)
