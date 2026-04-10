module FsHotWatch.Tests.WatcherTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Watcher
open FsHotWatch.Tests.TestHelpers

// === Unit tests for isRelevantFile ===

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
let ``isRelevantFile accepts .slnx files`` () =
    test <@ isRelevantFile "/repo/App.slnx" @>

[<Fact>]
let ``isRelevantFile accepts .props files`` () =
    test <@ isRelevantFile "/repo/Directory.Build.props" @>

[<Fact>]
let ``isRelevantFile rejects files in obj directory`` () =
    test <@ not (isRelevantFile "/repo/src/obj/Debug/Generated.fs") @>

[<Fact>]
let ``isRelevantFile rejects files in bin directory`` () =
    test <@ not (isRelevantFile "/repo/src/bin/Debug/App.fs") @>

[<Fact>]
let ``isRelevantFile rejects .cs files`` () =
    test <@ not (isRelevantFile "/repo/src/Program.cs") @>

[<Fact>]
let ``isRelevantFile rejects .txt files`` () =
    test <@ not (isRelevantFile "/repo/src/notes.txt") @>

[<Fact>]
let ``isRelevantFile rejects unrelated extensions`` () =
    test <@ not (isRelevantFile "/repo/src/readme.md") @>
    test <@ not (isRelevantFile "/repo/src/data.json") @>

[<Fact>]
let ``isRelevantFile normalizes backslash paths for obj exclusion`` () =
    test <@ not (isRelevantFile @"C:\repo\src\obj\Debug\Generated.fs") @>

[<Fact>]
let ``isRelevantFile normalizes backslash paths for bin exclusion`` () =
    test <@ not (isRelevantFile @"C:\repo\src\bin\Release\App.fs") @>

// === Unit tests for classifyChange ===

[<Fact>]
let ``classifyChange maps .fs to SourceChanged`` () =
    test <@ classifyChange "/repo/src/Lib.fs" = SourceChanged [ "/repo/src/Lib.fs" ] @>

[<Fact>]
let ``classifyChange maps .fsx to SourceChanged`` () =
    test <@ classifyChange "/repo/src/Script.fsx" = SourceChanged [ "/repo/src/Script.fsx" ] @>

[<Fact>]
let ``classifyChange maps .fsproj to ProjectChanged`` () =
    test <@ classifyChange "/repo/src/App.fsproj" = ProjectChanged [ "/repo/src/App.fsproj" ] @>

[<Fact>]
let ``classifyChange maps .props to ProjectChanged`` () =
    test
        <@ classifyChange "/repo/Directory.Build.props" = ProjectChanged [ "/repo/Directory.Build.props" ] @>

[<Fact>]
let ``classifyChange maps .sln to SolutionChanged`` () =
    test <@ classifyChange "/repo/App.sln" = SolutionChanged "/repo/App.sln" @>

[<Fact>]
let ``classifyChange maps .slnx to SolutionChanged`` () =
    test <@ classifyChange "/repo/App.slnx" = SolutionChanged "/repo/App.slnx" @>

// === Unit tests for hasContentChanged ===

[<Fact>]
let ``hasContentChanged returns true for file that does not exist`` () =
    let fakePath =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-nonexistent-{Guid.NewGuid():N}.fs")

    test <@ hasContentChanged fakePath = true @>

[<Fact>]
let ``hasContentChanged returns true on first check of existing file`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-first-{Guid.NewGuid():N}.fs")

    try
        File.WriteAllText(tmpFile, "let x = 1")
        test <@ hasContentChanged tmpFile = true @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``hasContentChanged returns false when content is unchanged`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-same-{Guid.NewGuid():N}.fs")

    try
        File.WriteAllText(tmpFile, "let x = 1")
        hasContentChanged tmpFile |> ignore // first call stores hash
        test <@ hasContentChanged tmpFile = false @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``hasContentChanged returns true when content changes`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-changed-{Guid.NewGuid():N}.fs")

    try
        File.WriteAllText(tmpFile, "let x = 1")
        hasContentChanged tmpFile |> ignore // first call stores hash
        File.WriteAllText(tmpFile, "let x = 2")
        test <@ hasContentChanged tmpFile = true @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``hasContentChanged returns true and removes from cache when file is deleted`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-deleted-{Guid.NewGuid():N}.fs")

    File.WriteAllText(tmpFile, "let x = 1")
    hasContentChanged tmpFile |> ignore // stores hash
    File.Delete(tmpFile)
    // Now file doesn't exist - should return true and remove from cache
    test <@ hasContentChanged tmpFile = true @>

[<Fact>]
let ``hasContentChanged returns true on IOException`` () =
    // Use a path that will cause IOException (directory, not a file)
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-ioerr-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        // Reading a directory as a file causes IOException on most platforms
        // If it doesn't throw, the test still passes (it's a best-effort test)
        let result = hasContentChanged tmpDir
        test <@ result = true @>
    finally
        Directory.Delete(tmpDir, true)

// === FileWatcher.create non-macOS code path ===

[<Fact>]
let ``FileWatcher.create with isMacOS=false watches src and tests dirs`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshotwatch-fsw-{Guid.NewGuid():N}")
    let srcDir = Path.Combine(tmpDir, "src")
    let testsDir = Path.Combine(tmpDir, "tests")
    Directory.CreateDirectory(srcDir) |> ignore
    Directory.CreateDirectory(testsDir) |> ignore

    let mutable changes: FileChangeKind list = []
    let onChange change = changes <- change :: changes

    try
        use watcher = FileWatcher.create tmpDir onChange (Some false)

        // Write a file and wait for the event
        probeUntilEvent srcDir (fun () -> changes.Length >= 1) 10000
        test <@ changes.Length >= 1 @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``FileWatcher.create with isMacOS=false when neither src nor tests exist`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-nosrc-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    let mutable changes: FileChangeKind list = []
    let onChange change = changes <- change :: changes

    try
        // Should not throw even without src/ or tests/
        use watcher = FileWatcher.create tmpDir onChange (Some false)
        // Only sln watcher should be present
        test <@ watcher.Disposables.Length = 1 @>
    finally
        Directory.Delete(tmpDir, true)

// === Integration test: verify FileWatcher.create produces a working watcher (default OS path) ===

[<Fact>]
let ``watcher detects file changes in src directory`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshotwatch-test-{Guid.NewGuid():N}")
    let srcDir = Path.Combine(tmpDir, "src")
    Directory.CreateDirectory(srcDir) |> ignore
    let mutable changes = []
    let onChange change = changes <- change :: changes

    use watcher = FileWatcher.create tmpDir onChange None

    // Probe until the watcher is delivering events (FSEvents cold-start can be 4-20s).
    probeUntilEvent srcDir (fun () -> changes.Length >= 1) 60000
    test <@ changes.Length >= 1 @>
    Directory.Delete(tmpDir, true)
