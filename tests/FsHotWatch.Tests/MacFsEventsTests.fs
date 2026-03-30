module FsHotWatch.Tests.MacFsEventsTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers

// Force sequential execution — FSEvents startup latency (4-10s for cold dirs) causes
// spurious failures when multiple streams compete for fseventsd resources in parallel.
type MacFsEventsFixture() = class end

[<CollectionDefinition("MacFsEvents", DisableParallelization = true)>]
type MacFsEventsCollection() =
    interface ICollectionFixture<MacFsEventsFixture>

[<Collection("MacFsEvents")>]
type MacFsEventsTests() =
    let isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)

    [<Fact>]
    member _.``FsEventStream create and dispose without crash``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            withTempDir "fsevents" (fun tmpDir ->
                use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] ignore
                test <@ _stream.IsRunning @>)

    [<Fact>]
    member _.``FsEventStream detects file creation``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            withTempDir "fsevents" (fun tmpDir ->
                let mutable detectedPaths: string list = []
                let lockObj = obj ()

                let onFile path =
                    lock lockObj (fun () -> detectedPaths <- path :: detectedPaths)

                use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] onFile

                // Phase 1: probe until stream is live (FSEvents cold-start can be 4-20s for new dirs).
                probeUntilEvent tmpDir (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 60000

                // Phase 2: probe-write Test.fs until its event fires.
                // fseventsd may batch subsequent events for 15-30s after a large cold-start batch.
                let testFile = Path.Combine(tmpDir, "Test.fs")

                probeLoop
                    (fun _ -> File.WriteAllText(testFile, "module Test\nlet x = 1\n"))
                    (fun () -> lock lockObj (fun () -> detectedPaths |> List.exists (fun p -> p.EndsWith("Test.fs"))))
                    60000

                let paths = lock lockObj (fun () -> detectedPaths)
                test <@ paths |> List.exists (fun p -> p.EndsWith("Test.fs")) @>)

    [<Fact>]
    member _.``FsEventStream detects file modification``() =
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

                // Phase 1: probe until stream is live.
                probeUntilEvent tmpDir (fun () -> lock lockObj (fun () -> detectedCount > 0)) 60000

                // Let in-flight probe events drain before capturing baseline.
                Thread.Sleep(200)
                let countAfterProbe = lock lockObj (fun () -> detectedCount)

                // Phase 2: probe-write Lib.fs until a modification event fires.
                probeLoop
                    (fun n -> File.WriteAllText(testFile, $"module Lib\nlet x = {n + 2}\n"))
                    (fun () -> lock lockObj (fun () -> detectedCount > countAfterProbe))
                    60000

                test <@ lock lockObj (fun () -> detectedCount > countAfterProbe) @>)

    [<Fact>]
    member _.``FsEventStream detects file deletion``() =
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

                // Phase 1: probe until stream is live.
                probeUntilEvent tmpDir (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 60000

                // Phase 2: delete Del.fs, retrying if fseventsd batches the event.
                // Each probeLoop iteration deletes (and if needed re-creates) Del.fs.
                probeLoop
                    (fun _ ->
                        if not (File.Exists testFile) then
                            File.WriteAllText(testFile, "module Del")

                        File.Delete(testFile))
                    (fun () -> lock lockObj (fun () -> detectedPaths |> List.exists (fun p -> p.EndsWith("Del.fs"))))
                    60000

                let paths = lock lockObj (fun () -> detectedPaths)
                test <@ paths |> List.exists (fun p -> p.EndsWith("Del.fs")) @>)

    [<Fact>]
    member _.``FsEventStream watches multiple directories``() =
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

                // Phase 1: probe until stream is live.
                probeUntilEvent srcDir (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 60000

                // Phase 2: probe-write A.fs and B.fs until events fire for both.
                probeLoop
                    (fun n ->
                        File.WriteAllText(Path.Combine(srcDir, "A.fs"), $"module A // v{n}")
                        File.WriteAllText(Path.Combine(testDir, "B.fs"), $"module B // v{n}"))
                    (fun () ->
                        lock lockObj (fun () ->
                            detectedPaths |> List.exists (fun p -> p.EndsWith("A.fs"))
                            && detectedPaths |> List.exists (fun p -> p.EndsWith("B.fs"))))
                    60000

                let paths = lock lockObj (fun () -> detectedPaths)
                test <@ paths |> List.exists (fun p -> p.EndsWith("A.fs")) @>
                test <@ paths |> List.exists (fun p -> p.EndsWith("B.fs")) @>
            finally
                Directory.Delete(tmpRoot, true)
