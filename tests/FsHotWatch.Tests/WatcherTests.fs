module FsHotWatch.Tests.WatcherTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.ContentDedup
open FsHotWatch.Events
open FsHotWatch.Watcher
open FsHotWatch.Tests.TestHelpers

// === Unit tests for isRelevantFile ===

[<Fact(Timeout = 15000)>]
let ``isRelevantFile accepts .fs files`` () =
    test <@ isRelevantFile "/repo/src/Lib.fs" @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile accepts .fsx files`` () =
    test <@ isRelevantFile "/repo/src/Script.fsx" @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile accepts .fsproj files`` () =
    test <@ isRelevantFile "/repo/src/App.fsproj" @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile accepts .sln files`` () =
    test <@ isRelevantFile "/repo/App.sln" @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile accepts .slnx files`` () =
    test <@ isRelevantFile "/repo/App.slnx" @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile accepts .props files`` () =
    test <@ isRelevantFile "/repo/Directory.Build.props" @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile rejects files in obj directory`` () =
    test <@ not (isRelevantFile "/repo/src/obj/Debug/Generated.fs") @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile rejects files in bin directory`` () =
    test <@ not (isRelevantFile "/repo/src/bin/Debug/App.fs") @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile rejects .cs files`` () =
    test <@ not (isRelevantFile "/repo/src/Program.cs") @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile rejects .txt files`` () =
    test <@ not (isRelevantFile "/repo/src/notes.txt") @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile rejects unrelated extensions`` () =
    test <@ not (isRelevantFile "/repo/src/readme.md") @>
    test <@ not (isRelevantFile "/repo/src/data.json") @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile normalizes backslash paths for obj exclusion`` () =
    test <@ not (isRelevantFile @"C:\repo\src\obj\Debug\Generated.fs") @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile normalizes backslash paths for bin exclusion`` () =
    test <@ not (isRelevantFile @"C:\repo\src\bin\Release\App.fs") @>

// === Unit tests for classifyChange ===

[<Fact(Timeout = 15000)>]
let ``classifyChange maps .fs to SourceChanged`` () =
    test <@ classifyChange "/repo/src/Lib.fs" = SourceChanged [ "/repo/src/Lib.fs" ] @>

[<Fact(Timeout = 15000)>]
let ``classifyChange maps .fsx to SourceChanged`` () =
    test <@ classifyChange "/repo/src/Script.fsx" = SourceChanged [ "/repo/src/Script.fsx" ] @>

[<Fact(Timeout = 15000)>]
let ``classifyChange maps .fsproj to ProjectChanged`` () =
    test <@ classifyChange "/repo/src/App.fsproj" = ProjectChanged [ "/repo/src/App.fsproj" ] @>

[<Fact(Timeout = 15000)>]
let ``classifyChange maps .props to ProjectChanged`` () =
    test <@ classifyChange "/repo/Directory.Build.props" = ProjectChanged [ "/repo/Directory.Build.props" ] @>

[<Fact(Timeout = 15000)>]
let ``classifyChange maps .sln to SolutionChanged`` () =
    test <@ classifyChange "/repo/App.sln" = SolutionChanged @>

[<Fact(Timeout = 15000)>]
let ``classifyChange maps .slnx to SolutionChanged`` () =
    test <@ classifyChange "/repo/App.slnx" = SolutionChanged @>

// === project.assets.json — post-restore package-graph signal ===
// FR docs/fr-auto-refresh-fsproj-changes.md (2026-05-25): the daemon
// must detect when `dotnet restore` materializes a new PackageReference,
// not just when the user types `<PackageReference>` into the .fsproj.
// `obj/project.assets.json` is the canonical post-restore signal — it's
// updated atomically when the resolver finishes, so reacting to it
// avoids the race where the .fsproj has the new package but restore
// hasn't completed yet.

[<Fact(Timeout = 15000)>]
let ``isRelevantFile accepts obj/project.assets.json despite obj/ exclusion`` () =
    test <@ isRelevantFile "/repo/src/MyProj/obj/project.assets.json" @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFile still rejects other files under obj/`` () =
    test <@ not (isRelevantFile "/repo/src/MyProj/obj/Debug/MyProj.AssemblyInfo.fs") @>
    test <@ not (isRelevantFile "/repo/src/MyProj/obj/MyProj.fsproj.nuget.g.props") @>
    test <@ not (isRelevantFile "/repo/src/MyProj/obj/project.nuget.cache") @>

[<Fact(Timeout = 15000)>]
let ``classifyChange maps obj/project.assets.json to ProjectChanged`` () =
    let path = "/repo/src/MyProj/obj/project.assets.json"
    test <@ classifyChange path = ProjectChanged [ path ] @>

[<Fact(Timeout = 15000)>]
let ``classifyChange routes assets.json identically to .fsproj (so processBatch dispatch is shared)`` () =
    // Both paths must arrive at processBatch through the same FileChangeKind
    // variant so the project-tier code path handles them identically. If they
    // diverge, downstream tier-check / invalidation logic has to branch.
    let fsprojChange = classifyChange "/repo/src/MyProj/MyProj.fsproj"
    let assetsChange = classifyChange "/repo/src/MyProj/obj/project.assets.json"

    let isProject =
        function
        | ProjectChanged _ -> true
        | _ -> false

    test <@ isProject fsprojChange && isProject assetsChange @>

// === Unit tests for hasContentChanged ===

[<Fact(Timeout = 15000)>]
let ``hasContentChanged returns true for file that does not exist`` () =
    let fakePath =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-nonexistent-{Guid.NewGuid():N}.fs")

    test <@ hasContentChanged fakePath = true @>

[<Fact(Timeout = 15000)>]
let ``hasContentChanged returns true on first check of existing file`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-first-{Guid.NewGuid():N}.fs")

    try
        File.WriteAllText(tmpFile, "let x = 1")
        test <@ hasContentChanged tmpFile = true @>
    finally
        File.Delete(tmpFile)

[<Fact(Timeout = 15000)>]
let ``hasContentChanged returns false when content is unchanged`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-same-{Guid.NewGuid():N}.fs")

    try
        File.WriteAllText(tmpFile, "let x = 1")
        hasContentChanged tmpFile |> ignore // first call stores hash
        test <@ hasContentChanged tmpFile = false @>
    finally
        File.Delete(tmpFile)

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``hasContentChanged returns true and removes from cache when file is deleted`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-deleted-{Guid.NewGuid():N}.fs")

    File.WriteAllText(tmpFile, "let x = 1")
    hasContentChanged tmpFile |> ignore // stores hash
    File.Delete(tmpFile)
    // Now file doesn't exist - should return true and remove from cache
    test <@ hasContentChanged tmpFile = true @>

[<Fact(Timeout = 15000)>]
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

        use watcher = FileWatcher.create tmpDir onChange (Some false) []

        probeUntilEvent srcDir (fun () -> changes.Length >= 1) 10000
        test <@ changes.Length >= 1 @>)

[<Fact(Timeout = 15000)>]
let ``FileWatcher.create with isMacOS=false when neither src nor tests exist`` () =
    withTempDir "watcher-nosrc" (fun tmpDir ->
        let mutable changes: FileChangeKind list = []
        let onChange change = changes <- change :: changes

        use watcher = FileWatcher.create tmpDir onChange (Some false) []
        test <@ watcher.Disposables.Length = 1 @>)

// === Integration test: verify FileWatcher.create produces a working watcher (default OS path) ===

[<Fact(Timeout = 150000)>]
let ``watcher detects file changes in src directory`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshotwatch-test-{Guid.NewGuid():N}")
    let srcDir = Path.Combine(tmpDir, "src")
    Directory.CreateDirectory(srcDir) |> ignore
    let mutable changes = []
    let onChange change = changes <- change :: changes

    use watcher = FileWatcher.create tmpDir onChange None []

    // Probe until the watcher is delivering events (FSEvents cold-start can be 4-20s).
    probeUntilEvent srcDir (fun () -> changes.Length >= 1) 60000
    test <@ changes.Length >= 1 @>
    Directory.Delete(tmpDir, true)

// === Unit tests for matchesPattern ===

[<Fact(Timeout = 15000)>]
let ``matchesPattern wildcard suffix matches any path ending with suffix`` () =
    test <@ FilePattern.matches (FilePattern.parse "*.ratchet.json") "/repo/coverage.ratchet.json" @>
    test <@ FilePattern.matches (FilePattern.parse "*.ratchet.json") "/repo/nested/my.ratchet.json" @>

[<Fact(Timeout = 15000)>]
let ``matchesPattern wildcard does not match non-matching suffix`` () =
    test <@ not (FilePattern.matches (FilePattern.parse "*.ratchet.json") "/repo/foo.json") @>

[<Fact(Timeout = 15000)>]
let ``matchesPattern literal matches only exact filename`` () =
    test <@ FilePattern.matches (FilePattern.parse "coverage-ratchet.json") "/repo/coverage-ratchet.json" @>
    test <@ FilePattern.matches (FilePattern.parse "coverage-ratchet.json") "/repo/nested/coverage-ratchet.json" @>

[<Fact(Timeout = 15000)>]
let ``matchesPattern literal does not match files that merely end with the name`` () =
    test <@ not (FilePattern.matches (FilePattern.parse "coverage-ratchet.json") "/repo/my-coverage-ratchet.json") @>

// === Unit tests for isRelevantFileOrExtra ===

[<Fact(Timeout = 15000)>]
let ``isRelevantFileOrExtra accepts built-in extensions with no extras`` () =
    test <@ isRelevantFileOrExtra [] "/repo/src/Lib.fs" @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFileOrExtra accepts files matching wildcard pattern`` () =
    test <@ isRelevantFileOrExtra [ FilePattern.parse "*.ratchet.json" ] "/repo/coverage.ratchet.json" @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFileOrExtra accepts files matching literal filename pattern`` () =
    test <@ isRelevantFileOrExtra [ FilePattern.parse "coverage-ratchet.json" ] "/repo/coverage-ratchet.json" @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFileOrExtra rejects files not matching extras or built-ins`` () =
    test <@ not (isRelevantFileOrExtra [ FilePattern.parse "*.ratchet.json" ] "/repo/Program.cs") @>

[<Fact(Timeout = 15000)>]
let ``isRelevantFileOrExtra rejects extra-matching files in obj directory`` () =
    test <@ not (isRelevantFileOrExtra [ FilePattern.parse "*.ratchet.json" ] "/repo/obj/Debug/config.ratchet.json") @>

// === Integration tests: extra-pattern watcher fires for non-source patterns ===

[<Fact(Timeout = 60000)>]
let ``FileWatcher with wildcard pattern fires SourceChanged for matching file`` () =
    withTempDir "watcher-extra-wild" (fun tmpDir ->
        let received = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()
        let onChange change = received.Add(change)

        use _watcher =
            FileWatcher.create tmpDir onChange (Some false) [ FilePattern.parse "*.ratchet.json" ] :> IDisposable

        // Probe by repeatedly rewriting a matching file until the watcher delivers an event.
        // FileSystemWatcher on macOS (kqueue/FSEvents backend) can have cold-start latency.
        let configPath = Path.Combine(tmpDir, "coverage.ratchet.json")

        let hasMatch () =
            received
            |> Seq.exists (fun c ->
                match c with
                | SourceChanged files -> files |> List.exists (fun f -> f.EndsWith(".ratchet.json"))
                | _ -> false)

        probeLoop (fun n -> File.WriteAllText(configPath, $"{{\"probe\": {n}}}")) hasMatch 30000

        test <@ hasMatch () @>)

[<Fact(Timeout = 60000)>]
let ``FileWatcher with literal filename pattern fires only for matching file`` () =
    withTempDir "watcher-extra-literal" (fun tmpDir ->
        let received = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()
        let onChange change = received.Add(change)

        use _watcher =
            FileWatcher.create tmpDir onChange (Some false) [ FilePattern.parse "coverage-ratchet.json" ]
            :> IDisposable

        let configPath = Path.Combine(tmpDir, "coverage-ratchet.json")

        let hasMatch () =
            received
            |> Seq.exists (fun c ->
                match c with
                | SourceChanged files -> files |> List.exists (fun f -> Path.GetFileName(f) = "coverage-ratchet.json")
                | _ -> false)

        probeLoop (fun n -> File.WriteAllText(configPath, $"{{\"probe\": {n}}}")) hasMatch 30000

        test <@ hasMatch () @>)

// === Cross-instance dedup isolation (ContentDedup.Tracker) ===
// The content-dedup hash store must be scoped per daemon instance, not shared
// process-globally. Two daemons in one process (e.g. parallel test daemons, or
// a daemon restarted over the same repo root within a long-lived process) watch
// overlapping absolute paths; a hash written by daemon A must NOT suppress a
// genuine first-observation change event in daemon B. The keys are absolute
// paths, so the collision is exact: same path, two trackers.

[<Fact(Timeout = 15000)>]
let ``Tracker first observation reports changed even when another tracker already saw the file`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-xinstance-{Guid.NewGuid():N}.fs")

    try
        File.WriteAllText(tmpFile, "let x = 1")

        let trackerA = Tracker()
        let trackerB = Tracker()

        // Daemon A observes the file first and stores its hash.
        test <@ trackerA.HasContentChanged tmpFile = true @>
        // Daemon B's FIRST EVER observation of the same path must still be
        // reported as changed — B has never seen this file. With a shared
        // global store this returns false (the genuine event is suppressed).
        test <@ trackerB.HasContentChanged tmpFile = true @>
    finally
        File.Delete(tmpFile)

[<Fact(Timeout = 15000)>]
let ``Tracker tracks its own state independently`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-instance-state-{Guid.NewGuid():N}.fs")

    try
        File.WriteAllText(tmpFile, "let x = 1")
        let tracker = Tracker()
        test <@ tracker.HasContentChanged tmpFile = true @> // first: stores hash
        test <@ tracker.HasContentChanged tmpFile = false @> // unchanged
        File.WriteAllText(tmpFile, "let x = 2")
        test <@ tracker.HasContentChanged tmpFile = true @> // changed
    finally
        File.Delete(tmpFile)

// === Item 6: FilePattern.parse must reject globs it cannot match consistently ===
//
// An embedded (non-leading) `*` diverges between the two matchers: the raw
// pattern string is handed to FileSystemWatcher.Filter (which GLOBS it, so the
// OS watcher fires for e.g. schema.users.sql) while the in-process
// FilePattern.matches treats it as a literal basename / literal suffix (which
// never matches those same files) — the event fires and is then silently
// dropped, so the user's file command never runs and never errors. parse must
// reject such patterns loudly at config-load time instead.

[<Fact(Timeout = 15000)>]
let ``parse rejects literal patterns with embedded wildcard`` () =
    Assert.Throws<ArgumentException>(fun () -> FilePattern.parse "schema.*.sql" |> ignore)
    |> ignore

[<Fact(Timeout = 15000)>]
let ``parse rejects wildcard patterns with additional embedded wildcard`` () =
    Assert.Throws<ArgumentException>(fun () -> FilePattern.parse "*.ratchet.*.json" |> ignore)
    |> ignore

[<Fact(Timeout = 15000)>]
let ``parse rejects empty pattern`` () =
    Assert.Throws<ArgumentException>(fun () -> FilePattern.parse "" |> ignore)
    |> ignore

[<Fact(Timeout = 15000)>]
let ``parse still accepts leading-wildcard and literal patterns`` () =
    test <@ FilePattern.parse "*.ratchet.json" = FilePattern.Wildcard ".ratchet.json" @>
    test <@ FilePattern.parse "coverage-ratchet.json" = FilePattern.Literal "coverage-ratchet.json" @>
    // A bare "*" (match everything) stays supported: both matchers agree on it.
    test <@ FilePattern.parse "*" = FilePattern.Wildcard "" @>
