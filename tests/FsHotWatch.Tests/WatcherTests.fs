module FsHotWatch.Tests.WatcherTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Watcher

// === Unit tests for classification/filtering (no FileSystemWatcher needed) ===

[<Fact>]
let ``isRelevantFile accepts .fs files`` () =
    test <@ isRelevantFile "/repo/src/Lib.fs" @>

[<Fact>]
let ``isRelevantFile accepts .fsx files`` () =
    test <@ isRelevantFile "/repo/src/Script.fsx" @>

[<Fact>]
let ``isRelevantFile accepts .fsproj files`` () =
    test <@ isRelevantFile "/repo/src/App.fsproj" @>

[<Fact>]
let ``isRelevantFile accepts .sln files`` () =
    test <@ isRelevantFile "/repo/App.sln" @>

[<Fact>]
let ``isRelevantFile rejects files in obj directory`` () =
    test <@ not (isRelevantFile "/repo/src/obj/Debug/Generated.fs") @>

[<Fact>]
let ``isRelevantFile rejects files in bin directory`` () =
    test <@ not (isRelevantFile "/repo/src/bin/Debug/App.fs") @>

[<Fact>]
let ``isRelevantFile rejects project.assets.json`` () =
    test <@ not (isRelevantFile "/repo/src/obj/project.assets.json") @>

[<Fact>]
let ``isRelevantFile accepts .props files`` () =
    test <@ isRelevantFile "/repo/Directory.Build.props" @>

[<Fact>]
let ``isRelevantFile rejects files in obj`` () =
    test <@ not (isRelevantFile "/repo/src/obj/project.assets.json") @>
    test <@ not (isRelevantFile "/repo/src/obj/Debug/net10.0/App.fs") @>
    test <@ not (isRelevantFile "/repo/src/obj/NuGet.props") @>

[<Fact>]
let ``isRelevantFile rejects unrelated extensions`` () =
    test <@ not (isRelevantFile "/repo/src/readme.md") @>
    test <@ not (isRelevantFile "/repo/src/data.json") @>

[<Fact>]
let ``classifyChange maps .fs to SourceChanged`` () =
    match classifyChange "/repo/src/Lib.fs" with
    | SourceChanged _ -> ()
    | other -> Assert.Fail($"Expected SourceChanged, got %A{other}")

[<Fact>]
let ``classifyChange maps .fsproj to ProjectChanged`` () =
    match classifyChange "/repo/src/App.fsproj" with
    | ProjectChanged _ -> ()
    | other -> Assert.Fail($"Expected ProjectChanged, got %A{other}")

[<Fact>]
let ``classifyChange maps .sln to SolutionChanged`` () =
    test <@ classifyChange "/repo/App.sln" = SolutionChanged @>

[<Fact>]
let ``classifyChange maps .props to ProjectChanged`` () =
    match classifyChange "/repo/Directory.Build.props" with
    | ProjectChanged _ -> ()
    | other -> Assert.Fail($"Expected ProjectChanged, got %A{other}")

[<Fact>]
let ``classifyChange maps project.assets.json to SourceChanged`` () =
    // project.assets.json is no longer specially handled — it's just another json file
    // that gets rejected by isRelevantFile before classifyChange is called
    match classifyChange "/repo/src/obj/project.assets.json" with
    | SourceChanged _ -> ()
    | other -> Assert.Fail($"Expected SourceChanged (fallthrough), got %A{other}")

// === Integration test: verify FileWatcher.create produces a working watcher ===
// Uses polling watcher on macOS for reliability.

do
    if
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)
    then
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1")

/// Poll until condition is true or timeout.
let private waitUntil (condition: unit -> bool) (timeoutMs: int) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep(50)

[<Fact>]
let ``watcher detects file changes in src directory`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshotwatch-test-{Guid.NewGuid():N}")
    let srcDir = Path.Combine(tmpDir, "src")
    Directory.CreateDirectory(srcDir) |> ignore
    let mutable changes = []
    let onChange change = changes <- change :: changes

    use watcher = FileWatcher.create tmpDir onChange

    let testFile = Path.Combine(srcDir, "Test.fs")
    File.WriteAllText(testFile, "module Test")
    File.SetLastWriteTimeUtc(testFile, DateTime.UtcNow.AddSeconds(1.0))
    waitUntil (fun () -> changes.Length >= 1) 30000
    test <@ changes.Length >= 1 @>
    Directory.Delete(tmpDir, true)
