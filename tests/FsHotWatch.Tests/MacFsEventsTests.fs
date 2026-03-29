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

            // Write sentinel to prove stream is receiving events
            let sentinel = Path.Combine(tmpDir, "_sentinel.fs")
            File.WriteAllText(sentinel, "module Sentinel")
            waitUntil (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 5000

            let testFile = Path.Combine(tmpDir, "Test.fs")
            File.WriteAllText(testFile, "module Test\nlet x = 1\n")

            waitUntil
                (fun () -> lock lockObj (fun () -> detectedPaths |> List.exists (fun p -> p.EndsWith("Test.fs"))))
                5000

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

            let mutable detectedCount = 0
            let lockObj = obj ()

            let onFile _path =
                lock lockObj (fun () -> detectedCount <- detectedCount + 1)

            use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] onFile

            // Write sentinel to prove stream is receiving events
            let sentinel = Path.Combine(tmpDir, "_sentinel.fs")
            File.WriteAllText(sentinel, "module Sentinel")
            waitUntil (fun () -> lock lockObj (fun () -> detectedCount > 0)) 5000

            // Record current count; the next event must be from our modification
            let countAfterSentinel = lock lockObj (fun () -> detectedCount)

            File.WriteAllText(testFile, "module Lib\nlet x = 2\n")

            waitUntil (fun () -> lock lockObj (fun () -> detectedCount > countAfterSentinel)) 5000

            test <@ lock lockObj (fun () -> detectedCount > countAfterSentinel) @>)

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

            // Write sentinel to prove stream is receiving events
            let sentinel = Path.Combine(tmpDir, "_sentinel.fs")
            File.WriteAllText(sentinel, "module Sentinel")
            waitUntil (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 5000

            File.Delete(testFile)

            waitUntil
                (fun () -> lock lockObj (fun () -> detectedPaths |> List.exists (fun p -> p.EndsWith("Del.fs"))))
                5000

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

            // Write sentinel to prove stream is receiving events
            let sentinel = Path.Combine(srcDir, "_sentinel.fs")
            File.WriteAllText(sentinel, "module Sentinel")
            waitUntil (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 5000

            File.WriteAllText(Path.Combine(srcDir, "A.fs"), "module A")
            File.WriteAllText(Path.Combine(testDir, "B.fs"), "module B")

            waitUntil
                (fun () ->
                    lock lockObj (fun () ->
                        detectedPaths |> List.exists (fun p -> p.EndsWith("A.fs"))
                        && detectedPaths |> List.exists (fun p -> p.EndsWith("B.fs"))))
                5000

            let paths = lock lockObj (fun () -> detectedPaths)
            test <@ paths |> List.exists (fun p -> p.EndsWith("A.fs")) @>
            test <@ paths |> List.exists (fun p -> p.EndsWith("B.fs")) @>
        finally
            Directory.Delete(tmpRoot, true)
