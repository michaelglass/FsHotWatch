module FsHotWatch.Tests.MacFsEventsTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers

let private isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)

[<Fact>]
let ``FsEventStream create and dispose without crash`` () =
    if not isMacOS then
        Assert.Skip("macOS only")
    else
        withTempDir "fsevents" (fun tmpDir ->
            use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] ignore
            test <@ _stream.IsRunning @>)

[<Fact>]
let ``FsEventStream detects file creation`` () =
    if not isMacOS then
        Assert.Skip("macOS only")
    else
        withTempDir "fsevents" (fun tmpDir ->
            let mutable detectedPaths: string list = []
            let lockObj = obj ()

            let onFile path =
                lock lockObj (fun () -> detectedPaths <- path :: detectedPaths)

            use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] onFile

            // Give FSEvents a moment to start receiving events
            Thread.Sleep(200)

            let testFile = Path.Combine(tmpDir, "Test.fs")
            File.WriteAllText(testFile, "module Test\nlet x = 1\n")

            waitUntil (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 5000

            let paths = lock lockObj (fun () -> detectedPaths)
            test <@ paths |> List.exists (fun p -> p.EndsWith("Test.fs")) @>)

[<Fact>]
let ``FsEventStream detects file modification`` () =
    if not isMacOS then
        Assert.Skip("macOS only")
    else
        withTempDir "fsevents" (fun tmpDir ->
            let testFile = Path.Combine(tmpDir, "Lib.fs")
            File.WriteAllText(testFile, "module Lib\nlet x = 1\n")

            let mutable detectedPaths: string list = []
            let lockObj = obj ()

            let onFile path =
                lock lockObj (fun () -> detectedPaths <- path :: detectedPaths)

            use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] onFile
            Thread.Sleep(200)

            // Clear any initial events from the create
            lock lockObj (fun () -> detectedPaths <- [])

            File.WriteAllText(testFile, "module Lib\nlet x = 2\n")
            waitUntil (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 5000

            let paths = lock lockObj (fun () -> detectedPaths)
            test <@ paths |> List.exists (fun p -> p.EndsWith("Lib.fs")) @>)

[<Fact>]
let ``FsEventStream detects file deletion`` () =
    if not isMacOS then
        Assert.Skip("macOS only")
    else
        withTempDir "fsevents" (fun tmpDir ->
            let testFile = Path.Combine(tmpDir, "Del.fs")
            File.WriteAllText(testFile, "module Del")

            let mutable detectedPaths: string list = []
            let lockObj = obj ()

            let onFile path =
                lock lockObj (fun () -> detectedPaths <- path :: detectedPaths)

            use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] onFile
            Thread.Sleep(200)
            lock lockObj (fun () -> detectedPaths <- [])

            File.Delete(testFile)
            waitUntil (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 5000

            let paths = lock lockObj (fun () -> detectedPaths)
            test <@ paths |> List.exists (fun p -> p.EndsWith("Del.fs")) @>)

[<Fact>]
let ``FsEventStream watches multiple directories`` () =
    if not isMacOS then
        Assert.Skip("macOS only")
    else
        let tmpRoot =
            Path.Combine(Path.GetTempPath(), $"fshw-fsevents-multi-{Guid.NewGuid():N}")

        let srcDir = Path.Combine(tmpRoot, "src")
        let testDir = Path.Combine(tmpRoot, "tests")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(testDir) |> ignore

        let mutable detectedPaths: string list = []
        let lockObj = obj ()

        let onFile path =
            lock lockObj (fun () -> detectedPaths <- path :: detectedPaths)

        try
            use _stream = FsHotWatch.MacFsEvents.create [ srcDir; testDir ] onFile
            Thread.Sleep(200)

            File.WriteAllText(Path.Combine(srcDir, "A.fs"), "module A")
            File.WriteAllText(Path.Combine(testDir, "B.fs"), "module B")

            waitUntil (fun () -> lock lockObj (fun () -> detectedPaths.Length >= 2)) 5000

            let paths = lock lockObj (fun () -> detectedPaths)
            test <@ paths |> List.exists (fun p -> p.EndsWith("A.fs")) @>
            test <@ paths |> List.exists (fun p -> p.EndsWith("B.fs")) @>
        finally
            Directory.Delete(tmpRoot, true)
