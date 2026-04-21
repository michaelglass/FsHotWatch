module FsHotWatch.Tests.MacFsEventsTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.MacFsEvents

// ─── Pure function tests (run on all platforms) ────────────────────

module ``classifyEvent pure tests`` =

    [<Fact(Timeout = 5000)>]
    let ``file created is FileChange`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemCreated
        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 5000)>]
    let ``file removed is FileChange`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemRemoved
        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 5000)>]
    let ``file renamed is FileChange`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemRenamed
        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 5000)>]
    let ``file modified is FileChange`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemModified
        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 5000)>]
    let ``file with multiple change flags is FileChange`` () =
        let flags =
            EventFlags.ItemIsFile ||| EventFlags.ItemCreated ||| EventFlags.ItemModified

        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 5000)>]
    let ``all four change types with ItemIsFile are FileChange`` () =
        let flags =
            EventFlags.ItemIsFile
            ||| EventFlags.ItemCreated
            ||| EventFlags.ItemRemoved
            ||| EventFlags.ItemRenamed
            ||| EventFlags.ItemModified

        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 5000)>]
    let ``MustScanSubDirs alone is CoalescedScan`` () =
        let flags = EventFlags.MustScanSubDirs
        test <@ classifyEvent flags = EventClassification.CoalescedScan @>

    [<Fact(Timeout = 5000)>]
    let ``MustScanSubDirs with other non-file flags is CoalescedScan`` () =
        // Some extra bits set but not ItemIsFile + change
        let flags = EventFlags.MustScanSubDirs ||| 0x00000004u
        test <@ classifyEvent flags = EventClassification.CoalescedScan @>

    [<Fact(Timeout = 5000)>]
    let ``directory created without ItemIsFile is Ignored`` () =
        let flags = EventFlags.ItemCreated
        test <@ classifyEvent flags = EventClassification.Ignored @>

    [<Fact(Timeout = 5000)>]
    let ``directory modified without ItemIsFile is Ignored`` () =
        let flags = EventFlags.ItemModified
        test <@ classifyEvent flags = EventClassification.Ignored @>

    [<Fact(Timeout = 5000)>]
    let ``ItemIsFile without change flags is Ignored`` () =
        let flags = EventFlags.ItemIsFile
        test <@ classifyEvent flags = EventClassification.Ignored @>

    [<Fact(Timeout = 5000)>]
    let ``zero flags is Ignored`` () =
        test <@ classifyEvent 0u = EventClassification.Ignored @>

    [<Fact(Timeout = 5000)>]
    let ``ItemIsFile with unrelated high bits but no change flags is Ignored`` () =
        let flags = EventFlags.ItemIsFile ||| 0x00100000u
        test <@ classifyEvent flags = EventClassification.Ignored @>

    [<Fact(Timeout = 5000)>]
    let ``MustScanSubDirs takes priority when no file change flags present`` () =
        // MustScanSubDirs + ItemIsFile but no change type => CoalescedScan wins via ordering
        // Actually: ItemIsFile without change flags fails the first branch, falls to MustScanSubDirs
        let flags = EventFlags.MustScanSubDirs ||| EventFlags.ItemIsFile
        test <@ classifyEvent flags = EventClassification.CoalescedScan @>

    [<Fact(Timeout = 5000)>]
    let ``file change takes priority over MustScanSubDirs when both present`` () =
        // If both ItemIsFile+change AND MustScanSubDirs are set, FileChange wins
        let flags =
            EventFlags.ItemIsFile ||| EventFlags.ItemCreated ||| EventFlags.MustScanSubDirs

        test <@ classifyEvent flags = EventClassification.FileChange @>

module ``isFileChangeEvent pure tests`` =

    [<Fact(Timeout = 5000)>]
    let ``returns true for file create`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemCreated
        test <@ isFileChangeEvent flags = true @>

    [<Fact(Timeout = 5000)>]
    let ``returns true for file modify`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemModified
        test <@ isFileChangeEvent flags = true @>

    [<Fact(Timeout = 5000)>]
    let ``returns false for directory create`` () =
        let flags = EventFlags.ItemCreated
        test <@ isFileChangeEvent flags = false @>

    [<Fact(Timeout = 5000)>]
    let ``returns false for MustScanSubDirs`` () =
        let flags = EventFlags.MustScanSubDirs
        test <@ isFileChangeEvent flags = false @>

    [<Fact(Timeout = 5000)>]
    let ``returns false for zero`` () = test <@ isFileChangeEvent 0u = false @>

    [<Fact(Timeout = 5000)>]
    let ``returns false for ItemIsFile alone`` () =
        test <@ isFileChangeEvent EventFlags.ItemIsFile = false @>

module ``isMustScanEvent pure tests`` =

    [<Fact(Timeout = 5000)>]
    let ``returns true for MustScanSubDirs`` () =
        test <@ isMustScanEvent EventFlags.MustScanSubDirs = true @>

    [<Fact(Timeout = 5000)>]
    let ``returns true for MustScanSubDirs with extra bits`` () =
        let flags = EventFlags.MustScanSubDirs ||| 0x00000080u
        test <@ isMustScanEvent flags = true @>

    [<Fact(Timeout = 5000)>]
    let ``returns false for file change`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemModified
        test <@ isMustScanEvent flags = false @>

    [<Fact(Timeout = 5000)>]
    let ``returns false for zero`` () = test <@ isMustScanEvent 0u = false @>

    [<Fact(Timeout = 5000)>]
    let ``returns false when file change and MustScanSubDirs both present`` () =
        // FileChange takes priority
        let flags =
            EventFlags.ItemIsFile ||| EventFlags.ItemRemoved ||| EventFlags.MustScanSubDirs

        test <@ isMustScanEvent flags = false @>

// ─── Integration tests (macOS only) ─────────────────────────��─────

// Force sequential execution — FSEvents startup latency (4-10s for cold dirs) causes
// spurious failures when multiple streams compete for fseventsd resources in parallel.
type MacFsEventsFixture() = class end

[<CollectionDefinition("MacFsEvents", DisableParallelization = true)>]
type MacFsEventsCollection() =
    interface ICollectionFixture<MacFsEventsFixture>

[<Collection("MacFsEvents")>]
type MacFsEventsTests() =
    let isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)

    [<Fact(Timeout = 5000)>]
    member _.``FsEventStream create and dispose without crash``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            withTempDir "fsevents" (fun tmpDir ->
                use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] ignore
                test <@ _stream.IsRunning @>)

    [<Fact(Timeout = 150000)>]
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

    [<Fact(Timeout = 150000)>]
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

    [<Fact(Timeout = 150000)>]
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

    [<Fact(Timeout = 150000)>]
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
