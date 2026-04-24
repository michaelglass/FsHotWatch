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

[<Fact(Timeout = 5000)>]
let ``isRelevantFile accepts .fs files`` () =
    test <@ isRelevantFile "/repo/src/Lib.fs" @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile accepts .fsx files`` () =
    test <@ isRelevantFile "/repo/src/Script.fsx" @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile accepts .fsproj files`` () =
    test <@ isRelevantFile "/repo/src/App.fsproj" @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile accepts .sln files`` () =
    test <@ isRelevantFile "/repo/App.sln" @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile accepts .slnx files`` () =
    test <@ isRelevantFile "/repo/App.slnx" @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile accepts .props files`` () =
    test <@ isRelevantFile "/repo/Directory.Build.props" @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile rejects files in obj directory`` () =
    test <@ not (isRelevantFile "/repo/src/obj/Debug/Generated.fs") @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile rejects files in bin directory`` () =
    test <@ not (isRelevantFile "/repo/src/bin/Debug/App.fs") @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile rejects .cs files`` () =
    test <@ not (isRelevantFile "/repo/src/Program.cs") @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile rejects .txt files`` () =
    test <@ not (isRelevantFile "/repo/src/notes.txt") @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile rejects unrelated extensions`` () =
    test <@ not (isRelevantFile "/repo/src/readme.md") @>
    test <@ not (isRelevantFile "/repo/src/data.json") @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile normalizes backslash paths for obj exclusion`` () =
    test <@ not (isRelevantFile @"C:\repo\src\obj\Debug\Generated.fs") @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFile normalizes backslash paths for bin exclusion`` () =
    test <@ not (isRelevantFile @"C:\repo\src\bin\Release\App.fs") @>

// === Unit tests for classifyChange ===

[<Fact(Timeout = 5000)>]
let ``classifyChange maps .fs to SourceChanged`` () =
    test <@ classifyChange "/repo/src/Lib.fs" = SourceChanged [ "/repo/src/Lib.fs" ] @>

[<Fact(Timeout = 5000)>]
let ``classifyChange maps .fsx to SourceChanged`` () =
    test <@ classifyChange "/repo/src/Script.fsx" = SourceChanged [ "/repo/src/Script.fsx" ] @>

[<Fact(Timeout = 5000)>]
let ``classifyChange maps .fsproj to ProjectChanged`` () =
    test <@ classifyChange "/repo/src/App.fsproj" = ProjectChanged [ "/repo/src/App.fsproj" ] @>

[<Fact(Timeout = 5000)>]
let ``classifyChange maps .props to ProjectChanged`` () =
    test <@ classifyChange "/repo/Directory.Build.props" = ProjectChanged [ "/repo/Directory.Build.props" ] @>

[<Fact(Timeout = 5000)>]
let ``classifyChange maps .sln to SolutionChanged`` () =
    test <@ classifyChange "/repo/App.sln" = SolutionChanged "/repo/App.sln" @>

[<Fact(Timeout = 5000)>]
let ``classifyChange maps .slnx to SolutionChanged`` () =
    test <@ classifyChange "/repo/App.slnx" = SolutionChanged "/repo/App.slnx" @>

// === Unit tests for hasContentChanged ===

[<Fact(Timeout = 5000)>]
let ``hasContentChanged returns true for file that does not exist`` () =
    let fakePath =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-nonexistent-{Guid.NewGuid():N}.fs")

    test <@ hasContentChanged fakePath = true @>

[<Fact(Timeout = 5000)>]
let ``hasContentChanged returns true on first check of existing file`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-first-{Guid.NewGuid():N}.fs")

    try
        File.WriteAllText(tmpFile, "let x = 1")
        test <@ hasContentChanged tmpFile = true @>
    finally
        File.Delete(tmpFile)

[<Fact(Timeout = 5000)>]
let ``hasContentChanged returns false when content is unchanged`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-same-{Guid.NewGuid():N}.fs")

    try
        File.WriteAllText(tmpFile, "let x = 1")
        hasContentChanged tmpFile |> ignore // first call stores hash
        test <@ hasContentChanged tmpFile = false @>
    finally
        File.Delete(tmpFile)

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``hasContentChanged returns true and removes from cache when file is deleted`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-deleted-{Guid.NewGuid():N}.fs")

    File.WriteAllText(tmpFile, "let x = 1")
    hasContentChanged tmpFile |> ignore // stores hash
    File.Delete(tmpFile)
    // Now file doesn't exist - should return true and remove from cache
    test <@ hasContentChanged tmpFile = true @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 30000)>]
let ``FileWatcher.create with isMacOS=false watches src and tests dirs`` () =
    withTempDir "watcher-fsw" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        let testsDir = Path.Combine(tmpDir, "tests")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(testsDir) |> ignore

        let mutable changes: FileChangeKind list = []
        let onChange change = changes <- change :: changes

        use watcher = FileWatcher.create tmpDir onChange (Some false)

        probeUntilEvent srcDir (fun () -> changes.Length >= 1) 10000
        test <@ changes.Length >= 1 @>)

[<Fact(Timeout = 5000)>]
let ``FileWatcher.create with isMacOS=false when neither src nor tests exist`` () =
    withTempDir "watcher-nosrc" (fun tmpDir ->
        let mutable changes: FileChangeKind list = []
        let onChange change = changes <- change :: changes

        use watcher = FileWatcher.create tmpDir onChange (Some false)
        test <@ watcher.Disposables.Length = 1 @>)

// === Integration test: verify FileWatcher.create produces a working watcher (default OS path) ===

[<Fact(Timeout = 150000)>]
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

// === Unit tests for isRelevantFileOrExtra ===

[<Fact(Timeout = 5000)>]
let ``isRelevantFileOrExtra accepts built-in extensions with no extras`` () =
    test <@ isRelevantFileOrExtra [] "/repo/src/Lib.fs" @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFileOrExtra accepts files matching extra suffix`` () =
    test <@ isRelevantFileOrExtra [ ".ratchet.json" ] "/repo/coverage.ratchet.json" @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFileOrExtra rejects files not matching extras or built-ins`` () =
    test <@ not (isRelevantFileOrExtra [ ".ratchet.json" ] "/repo/Program.cs") @>

[<Fact(Timeout = 5000)>]
let ``isRelevantFileOrExtra rejects extra-matching files in obj directory`` () =
    test <@ not (isRelevantFileOrExtra [ ".ratchet.json" ] "/repo/obj/Debug/config.ratchet.json") @>
