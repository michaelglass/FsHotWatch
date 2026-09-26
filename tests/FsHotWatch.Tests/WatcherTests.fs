[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.FileWatchCollectionName)>]
module FsHotWatch.Tests.WatcherTests

open System
open System.IO
open System.Runtime.InteropServices
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
// The daemon must detect a PackageReference `dotnet restore` materializes, not
// only one the user typed into the .fsproj. `obj/project.assets.json` is written
// atomically when the resolver finishes, so reacting to it avoids the race where
// the .fsproj has the new package but restore has not completed.

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
    // Both must reach processBatch through the same FileChangeKind variant; if they
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
    test <@ hasContentChanged tmpFile = true @>

[<Fact(Timeout = 15000)>]
let ``hasContentChanged returns true on IOException`` () =
    // A directory, not a file: reading it as a file raises IOException on most
    // platforms.
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-ioerr-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let result = hasContentChanged tmpDir
        test <@ result = true @>
    finally
        Directory.Delete(tmpDir, true)

// === FileWatcher.create non-macOS code path ===

// The polling backend exists specifically for the macOS failure path, but its
// contract is deterministic and does not depend on an OS event stream.  If it
// regresses to metadata stamps, the same-length rewrite below is missed.
[<Fact(Timeout = 15000)>]
let ``polling fallback baselines silently then detects create content change and delete`` () =
    withTempDir "watcher-poll-diff" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let existing = Path.Combine(srcDir, "Existing.fs")
        File.WriteAllText(existing, "let value = 1")

        let changes = ResizeArray<FileChangeKind>()
        use watcher = new PollingFileWatcher(tmpDir, changes.Add, [], false, None, None)

        test <@ changes.Count = 0 @>

        let created = Path.Combine(srcDir, "Created.fs")
        File.WriteAllText(created, "let created = 1")
        watcher.Poll()
        test <@ changes |> Seq.contains (SourceChanged [ created ]) @>

        changes.Clear()
        let originalTimestamp = File.GetLastWriteTimeUtc(existing)
        File.WriteAllText(existing, "let value = 2")
        File.SetLastWriteTimeUtc(existing, originalTimestamp)
        watcher.Poll()
        test <@ changes |> Seq.contains (SourceChanged [ existing ]) @>

        changes.Clear()
        watcher.Poll()
        test <@ changes.Count = 0 @>

        changes.Clear()
        File.Delete(existing)
        watcher.Poll()
        test <@ changes |> Seq.contains (SourceChanged [ existing ]) @>)

[<Fact(Timeout = 15000)>]
let ``polling fallback covers top-level solutions and extra patterns across repo`` () =
    withTempDir "watcher-poll-scope" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "config", "nested")
        Directory.CreateDirectory(nested) |> ignore
        let changes = ResizeArray<FileChangeKind>()

        use watcher =
            new PollingFileWatcher(tmpDir, changes.Add, [ FilePattern.parse "*.ratchet.json" ], false, None, None)

        let solution = Path.Combine(tmpDir, "Repo.slnx")
        let extra = Path.Combine(nested, "coverage.ratchet.json")
        File.WriteAllText(solution, "<Solution />")
        File.WriteAllText(extra, "{}")
        watcher.Poll()

        test <@ changes |> Seq.contains SolutionChanged @>
        test <@ changes |> Seq.contains (SourceChanged [ extra ]) @>)

[<Fact(Timeout = 15000)>]
let ``polling fallback includes assets but excludes generated and out-of-root source`` () =
    withTempDir "watcher-poll-builtins" (fun tmpDir ->
        let src = Path.Combine(tmpDir, "src")
        let obj = Path.Combine(src, "App", "obj")
        let bin = Path.Combine(src, "App", "bin")
        Directory.CreateDirectory(obj) |> ignore
        Directory.CreateDirectory(bin) |> ignore
        let changes = ResizeArray<FileChangeKind>()
        use watcher = new PollingFileWatcher(tmpDir, changes.Add, [], false, None, None)

        let source = Path.Combine(src, "App.fs")
        let assets = Path.Combine(obj, "project.assets.json")
        let generated = Path.Combine(obj, "Generated.fs")
        let buildOutput = Path.Combine(bin, "Output.fs")
        let outside = Path.Combine(tmpDir, "Outside.fs")
        File.WriteAllText(source, "let source = 1")
        File.WriteAllText(assets, "{}")
        File.WriteAllText(generated, "let generated = 1")
        File.WriteAllText(buildOutput, "let output = 1")
        File.WriteAllText(outside, "let outside = 1")
        watcher.Poll()

        test <@ changes |> Seq.contains (SourceChanged [ source ]) @>
        test <@ changes |> Seq.contains (ProjectChanged [ assets ]) @>
        test <@ not (changes |> Seq.contains (SourceChanged [ generated ])) @>
        test <@ not (changes |> Seq.contains (SourceChanged [ buildOutput ])) @>
        test <@ not (changes |> Seq.contains (SourceChanged [ outside ])) @>)

[<Fact(Timeout = 15000)>]
let ``polling fallback reports unreadable files and holes conservatively every poll`` () =
    let unreadable = "/repo/src/Unreadable.fs"

    let snapshot: PollingSnapshot =
        { Files = Map.ofList [ unreadable, FsHotWatch.ContentHash.UnhashableContent ]
          UnreadableFiles = Set.singleton unreadable
          Holes = Set.singleton "/repo/src/hidden" }

    let changes = ResizeArray<FileChangeKind>()

    use watcher =
        new PollingFileWatcher("/repo", changes.Add, [], false, Some(fun () -> snapshot), None)

    watcher.Poll()
    watcher.Poll()

    test <@ changes |> Seq.filter ((=) (SourceChanged [ unreadable ])) |> Seq.length = 2 @>
    test <@ changes |> Seq.filter ((=) SolutionChanged) |> Seq.length = 2 @>

[<Fact(Timeout = 15000)>]
let ``polling fallback isolates a failing change callback and continues the batch`` () =
    let first = "/repo/src/First.fs"
    let second = "/repo/src/Second.fs"
    let mutable snapshots = 0

    let snapshot () =
        snapshots <- snapshots + 1

        { Files =
            if snapshots = 1 then
                Map.empty
            else
                Map.ofList [ first, "first"; second, "second" ]
          UnreadableFiles = Set.empty
          Holes = Set.empty }

    let callbacks = ResizeArray<FileChangeKind>()

    let failingCallback change =
        callbacks.Add(change)
        raise (InvalidOperationException("consumer failed"))

    use watcher =
        new PollingFileWatcher("/repo", failingCallback, [], false, Some snapshot, None)

    // One consumer exception must not escape Poll or prevent the next changed
    // path in the same snapshot from being delivered.
    watcher.Poll()

    test <@ callbacks |> Seq.contains (SourceChanged [ first ]) @>
    test <@ callbacks |> Seq.contains (SourceChanged [ second ]) @>
    test <@ callbacks.Count = 2 @>

[<Fact(Timeout = 15000)>]
let ``polling fallback skips overlapping polls and emits nothing after disposal`` () =
    let entered = new ManualResetEventSlim(false)
    let release = new ManualResetEventSlim(false)
    let mutable snapshots = 0

    let snapshot () =
        snapshots <- snapshots + 1

        if snapshots = 2 then
            entered.Set()
            release.Wait()

        ({ Files = Map.empty
           UnreadableFiles = Set.empty
           Holes = Set.empty }
        : PollingSnapshot)

    let changes = ResizeArray<FileChangeKind>()

    let watcher =
        new PollingFileWatcher("/repo", changes.Add, [], false, Some snapshot, None)

    let first = Thread(ThreadStart(fun () -> watcher.Poll()))
    first.Start()
    test <@ entered.Wait(TimeSpan.FromSeconds(10.0)) @>
    watcher.Poll()
    test <@ snapshots = 2 @>
    release.Set()
    first.Join()
    (watcher :> IDisposable).Dispose()
    watcher.Poll()
    test <@ snapshots = 2 @>

[<Fact(Timeout = 15000)>]
let ``polling snapshot uses one source walk plus one assets walk per root and one per extra`` () =
    let calls = ResizeArray<Set<string> * string * string>()

    let walk excluded pattern root : FsHotWatch.SafeWalk.WalkResult =
        calls.Add(excluded, pattern, root)
        { Files = []; Skipped = [] }

    takePollingSnapshotWith
        walk
        (fun _ -> [], Set.empty)
        "/repo"
        [ "/repo/src"; "/repo/tests" ]
        [ FilePattern.parse "*.ratchet.json" ]
    |> ignore

    test <@ calls.Count = 5 @>
    test <@ calls |> Seq.contains (FsHotWatch.SafeWalk.SourceExcludedDirs, "*", "/repo/src") @>

    test
        <@
            calls
            |> Seq.contains (pollingAssetsExcludedDirs, "project.assets.json", "/repo/src")
        @>

    test
        <@
            calls
            |> Seq.contains (FsHotWatch.SafeWalk.SourceExcludedDirs, "*", "/repo/tests")
        @>

    test
        <@
            calls
            |> Seq.contains (pollingAssetsExcludedDirs, "project.assets.json", "/repo/tests")
        @>

    test
        <@
            calls
            |> Seq.contains (FsHotWatch.SafeWalk.SourceExcludedDirs, "*.ratchet.json", "/repo")
        @>

[<Fact(Timeout = 15000)>]
let ``automatic polling rearms after snapshot failure and stops rearming after disposal`` () =
    let mutable callback = ignore
    let arms = ResizeArray<int>()
    let mutable timerDisposed = false

    let timerFactory onTick =
        callback <- onTick

        { Arm = arms.Add
          Dispose = fun () -> timerDisposed <- true }

    let mutable snapshots = 0

    let snapshot () =
        snapshots <- snapshots + 1

        if snapshots = 2 then
            raise (IOException("transient snapshot failure"))

        ({ Files = Map.empty
           UnreadableFiles = Set.empty
           Holes = Set.empty }
        : PollingSnapshot)

    let watcher =
        new PollingFileWatcher("/repo", ignore, [], true, Some snapshot, Some timerFactory)

    test <@ arms |> Seq.toList = [ 1000 ] @>
    callback ()
    test <@ snapshots = 2 @>
    test <@ arms |> Seq.toList = [ 1000; 1000 ] @>
    callback ()
    test <@ snapshots = 3 @>
    test <@ arms |> Seq.toList = [ 1000; 1000; 1000 ] @>
    (watcher :> IDisposable).Dispose()
    callback ()
    test <@ timerDisposed @>
    test <@ snapshots = 3 @>
    test <@ arms |> Seq.toList = [ 1000; 1000; 1000 ] @>

[<Fact(Timeout = 15000)>]
let ``automatic polling default timer performs a poll`` () =
    let mutable snapshots = 0

    let snapshot () =
        Interlocked.Increment(&snapshots) |> ignore

        ({ Files = Map.empty
           UnreadableFiles = Set.empty
           Holes = Set.empty }
        : PollingSnapshot)

    use _watcher =
        new PollingFileWatcher("/repo", ignore, [], true, Some snapshot, None)

    // Construction takes the baseline synchronously; the real one-shot timer must
    // produce the second snapshot without an injected timer seam.
    let polled = waitUntilTrue (fun () -> Volatile.Read(&snapshots) >= 2) 5000
    test <@ polled @>

[<Fact(Timeout = 15000)>]
let ``automatic polling disposal waits for active callback then prevents later callbacks`` () =
    let mutable callback = ignore
    let entered = new ManualResetEventSlim(false)
    let release = new ManualResetEventSlim(false)
    let mutable snapshots = 0

    let timerFactory onTick =
        callback <- onTick
        { Arm = ignore; Dispose = ignore }

    let snapshot () =
        snapshots <- snapshots + 1

        if snapshots = 2 then
            entered.Set()
            release.Wait()

        ({ Files = Map.empty
           UnreadableFiles = Set.empty
           Holes = Set.empty }
        : PollingSnapshot)

    let watcher =
        new PollingFileWatcher("/repo", ignore, [], true, Some snapshot, Some timerFactory)

    // Dedicated threads make this a lock-ordering test, not a ThreadPool
    // availability test. The full suite deliberately runs enough parallel work
    // that a queued Task may not start within an arbitrary two-second window.
    let active = Thread(ThreadStart callback)
    active.Start()
    test <@ entered.Wait(TimeSpan.FromSeconds(10.0)) @>

    let disposeStarted = new ManualResetEventSlim(false)
    let disposeCompleted = new ManualResetEventSlim(false)

    let disposing =
        Thread(
            ThreadStart(fun () ->
                disposeStarted.Set()
                (watcher :> IDisposable).Dispose()
                disposeCompleted.Set())
        )

    disposing.Start()
    test <@ disposeStarted.Wait(TimeSpan.FromSeconds(10.0)) @>
    test <@ not disposeCompleted.IsSet @>
    release.Set()
    active.Join()
    disposing.Join()
    test <@ disposeCompleted.IsSet @>
    callback ()
    test <@ snapshots = 2 @>

[<Fact(Timeout = 15000)>]
let ``macOS native start refused with no retry budget fails closed at once`` () =
    // `NativeStartRetry.none` is a budget of one attempt, so one refusal is a refusal past
    // the budget: no FileSystemWatcher is created, no polling watcher is chosen.
    withTempDir "watcher-native-failure" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let mutable systemCreations = 0
        let mutable pollingCreations = 0

        let failNative _directories _onFile _onCoalesced _latency : IDisposable =
            raise (FsHotWatch.MacFsEvents.StartFailedException())

        let system _handle _spec =
            systemCreations <- systemCreations + 1

            { new IDisposable with
                member _.Dispose() = () }

        let polling _repo _onChange _extras : IDisposable =
            pollingCreations <- pollingCreations + 1
            new PollingFileWatcher(tmpDir, ignore, [], false, None, None) :> IDisposable

        let refused =
            Assert.Throws<NativeStreamRefusedPastBudgetException>(fun () ->
                FileWatcher.createWithFactories
                    tmpDir
                    ignore
                    []
                    0.05
                    FileWatcher.NativeStartRetry.none
                    failNative
                    system
                    polling
                |> ignore)

        test <@ refused.Refusal.Attempts = 1 @>
        test <@ refused.Refusal.BackoffSpentMs = 0 @>
        test <@ systemCreations = 0 @>
        test <@ pollingCreations = 0 @>)

// === a transiently refused native start is retried, not demoted ===

/// A retry budget whose waits are recorded instead of slept.
let private recordingRetry (backoffMs: int list) =
    let sleeps = ResizeArray<int>()

    ({ BackoffMs = backoffMs
       Sleep = sleeps.Add }
    : FileWatcher.NativeStartRetry),
    sleeps

let private inert (_name: string) =
    { new IDisposable with
        member _.Dispose() = () }

/// A native factory that raises `refusal` for the first `refusals` attempts, then succeeds.
let private nativeRefusing (refusals: int) (refusal: unit -> exn) =
    let mutable attempts = 0

    let factory _dirs _onFile _onCoalesced _latency : IDisposable =
        attempts <- attempts + 1

        if attempts <= refusals then
            raise (refusal ())
        else
            inert "native"

    factory, (fun () -> attempts)

let private countingPolling () =
    let mutable created = 0

    let factory _repo _onChange _extras : IDisposable =
        created <- created + 1
        inert "polling"

    factory, (fun () -> created)

let private inertSystem _handle _spec = inert "system"

[<Fact(Timeout = 15000)>]
let ``macOS native start refused once then accepted keeps the native watcher`` () =
    withTempDir "watcher-native-transient" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let retry, sleeps = recordingRetry [ 100; 300; 900 ]

        let native, attempts =
            nativeRefusing 1 (fun () -> FsHotWatch.MacFsEvents.StartFailedException() :> exn)

        let polling, pollingCreated = countingPolling ()

        use watcher =
            FileWatcher.createWithFactories tmpDir ignore [] 0.05 retry native inertSystem polling

        test <@ attempts () = 2 @>
        test <@ sleeps |> Seq.toList = [ 100 ] @>
        test <@ pollingCreated () = 0 @>
        test <@ watcher.Mode = WatcherMode.NativeEvents @>)

[<Fact(Timeout = 15000)>]
let ``macOS native create refusal spends the same retry budget as a start refusal`` () =
    withTempDir "watcher-native-create" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let retry, sleeps = recordingRetry [ 100; 300; 900 ]

        let native, attempts =
            nativeRefusing 2 (fun () -> FsHotWatch.MacFsEvents.CreateFailedException() :> exn)

        let polling, pollingCreated = countingPolling ()

        use watcher =
            FileWatcher.createWithFactories tmpDir ignore [] 0.05 retry native inertSystem polling

        test <@ attempts () = 3 @>
        test <@ sleeps |> Seq.toList = [ 100; 300 ] @>
        test <@ pollingCreated () = 0 @>
        test <@ watcher.Mode = WatcherMode.NativeEvents @>)

[<Fact(Timeout = 15000)>]
let ``macOS native start refused past the retry budget fails closed and names the refusal`` () =
    // Case 2: a PERSISTENT refusal is not a reason to poll. The
    // budget is spent in full, then construction raises a typed failure with no watcher
    // of any kind left behind — not the 1-second polling watcher, not a FileSystemWatcher.
    withTempDir "watcher-native-persistent" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let retry, sleeps = recordingRetry [ 100; 300; 900 ]

        let native, attempts =
            nativeRefusing Int32.MaxValue (fun () -> FsHotWatch.MacFsEvents.StartFailedException() :> exn)

        let polling, pollingCreated = countingPolling ()
        let mutable systemCreated = 0

        let system handle spec =
            systemCreated <- systemCreated + 1
            inertSystem handle spec

        let refused =
            Assert.Throws<NativeStreamRefusedPastBudgetException>(fun () ->
                FileWatcher.createWithFactories tmpDir ignore [] 0.05 retry native system polling
                |> ignore)

        // One attempt per budget entry plus the first: the budget is bounded and fully spent.
        test <@ attempts () = 4 @>
        test <@ sleeps |> Seq.toList = [ 100; 300; 900 ] @>
        test <@ pollingCreated () = 0 @>
        test <@ systemCreated = 0 @>

        test
            <@
                refused.Refusal = { Attempts = 4
                                    BackoffSpentMs = 1300
                                    LastRefusal = FsHotWatch.MacFsEvents.StartFailedException().Message }
            @>

        test <@ refused.Message.Contains("all 4 attempts") @>
        test <@ refused.Message.Contains("1300 ms") @>
        test <@ refused.Message.Contains("FSEventStreamStart") @>)

[<Fact(Timeout = 15000)>]
let ``macOS native start accepted after a refusal past the budget starts the native watcher cleanly`` () =
    // The refusal left nothing behind, so the very next construction in the same
    // repository is an ordinary healthy start: native, no polling, no delay.
    withTempDir "watcher-native-restart" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let retry, sleeps = recordingRetry [ 100; 300; 900 ]

        let refusing, _ =
            nativeRefusing Int32.MaxValue (fun () -> FsHotWatch.MacFsEvents.StartFailedException() :> exn)

        let polling, pollingCreated = countingPolling ()

        Assert.Throws<NativeStreamRefusedPastBudgetException>(fun () ->
            FileWatcher.createWithFactories tmpDir ignore [] 0.05 retry refusing inertSystem polling
            |> ignore)
        |> ignore

        sleeps.Clear()
        let accepting, attempts = nativeRefusing 0 (fun () -> failwith "unreachable")

        use watcher =
            FileWatcher.createWithFactories tmpDir ignore [] 0.05 retry accepting inertSystem polling

        test <@ attempts () = 1 @>
        test <@ sleeps.Count = 0 @>
        test <@ pollingCreated () = 0 @>
        test <@ watcher.Mode = WatcherMode.NativeEvents @>)

[<Fact(Timeout = 15000)>]
let ``macOS non-refusal native fault falls back at once without spending the budget`` () =
    withTempDir "watcher-native-fault" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let retry, sleeps = recordingRetry [ 100; 300; 900 ]

        let native, attempts =
            nativeRefusing Int32.MaxValue (fun () -> InvalidOperationException("callback pin failed") :> exn)

        let polling, pollingCreated = countingPolling ()

        use watcher =
            FileWatcher.createWithFactories tmpDir ignore [] 0.05 retry native inertSystem polling

        test <@ attempts () = 1 @>
        test <@ sleeps.Count = 0 @>
        test <@ pollingCreated () = 1 @>
        test <@ watcher.Mode = WatcherMode.ContentPolling "callback pin failed" @>)

[<Fact(Timeout = 15000)>]
let ``macOS healthy native start pays no retry delay`` () =
    withTempDir "watcher-native-healthy" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let retry, sleeps = recordingRetry [ 100; 300; 900 ]
        let native, attempts = nativeRefusing 0 (fun () -> failwith "unreachable")
        let polling, pollingCreated = countingPolling ()

        use watcher =
            FileWatcher.createWithFactories tmpDir ignore [] 0.05 retry native inertSystem polling

        test <@ attempts () = 1 @>
        test <@ sleeps.Count = 0 @>
        test <@ pollingCreated () = 0 @>
        test <@ watcher.Mode = WatcherMode.NativeEvents @>)

[<Fact(Timeout = 150000)>]
let ``macOS native watcher recovered from one refused start delivers real file events`` () =
    if not (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) then
        Assert.Skip("macOS only")
    else
        withTempDir "watcher-native-recovered" (fun tmpDir ->
            let srcDir = Path.Combine(tmpDir, "src")
            Directory.CreateDirectory(srcDir) |> ignore
            let retry, sleeps = recordingRetry [ 100 ]
            let mutable attempts = 0

            // First attempt: the refusal seen on 2026-08-21. Second: the real stream.
            let native dirs onFile onCoalesced latency : IDisposable =
                attempts <- attempts + 1

                if attempts = 1 then
                    raise (FsHotWatch.MacFsEvents.StartFailedException())
                else
                    FsHotWatch.MacFsEvents.createWithCoalesced dirs onFile onCoalesced latency :> IDisposable

            let changes = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()

            use watcher =
                FileWatcher.createWithNativeStream tmpDir changes.Add [] 0.05 retry native

            test <@ attempts = 2 @>
            test <@ sleeps |> Seq.toList = [ 100 ] @>
            test <@ watcher.Mode = WatcherMode.NativeEvents @>

            // Recovery is only real if the stream that finally started delivers.
            probeUntilEvent srcDir (fun () -> changes.Count >= 1) 60000
            test <@ changes.Count >= 1 @>)

[<Fact(Timeout = 15000)>]
let ``macOS setup creates native first and rolls every partial watcher back`` () =
    withTempDir "watcher-transaction" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let order = ResizeArray<string>()
        let disposed = ResizeArray<string>()

        let tracked name =
            { new IDisposable with
                member _.Dispose() = disposed.Add(name) }

        let native _dirs _onFile _onCoalesced _latency =
            order.Add("native")
            tracked "native"

        let mutable systemCount = 0

        let system _handle _spec =
            systemCount <- systemCount + 1
            order.Add($"system-%d{systemCount}")

            if systemCount = 2 then
                failwith "second system watcher failed"

            tracked $"system-%d{systemCount}"

        let polling _repo _onChange _extras = tracked "polling"

        use watcher =
            FileWatcher.createWithFactories
                tmpDir
                ignore
                [ FilePattern.parse "*.ratchet.json" ]
                0.05
                FileWatcher.NativeStartRetry.none
                native
                system
                polling

        test <@ order |> Seq.toList = [ "native"; "system-1"; "system-2" ] @>
        test <@ disposed |> Seq.contains "native" @>
        test <@ disposed |> Seq.contains "system-1" @>
        test <@ watcher.Disposables.Length = 1 @>)

// === a native notification is not evidence that the bytes changed ===
// FSEvents is advisory. It fires for a touch, for an open-for-write that wrote
// nothing, for a rewrite with identical bytes, and for every path named in a
// coalesced batch. Emitting on the notification alone floods the daemon with
// changes for untouched files, and each flood invalidates the project model the
// in-flight test round is producing evidence about.

/// Drives the macOS native path without a real FSEvents stream: runs `body` with
/// the per-file callback the watcher installed in its native factory, and the list
/// of changes the watcher has emitted so far.
let private withNativeNotifications (tmpDir: string) (body: (string -> unit) -> ResizeArray<FileChangeKind> -> unit) =
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let changes = ResizeArray<FileChangeKind>()
    let mutable captured: (string -> unit) option = None

    let native _dirs onFile _onCoalesced _latency : IDisposable =
        captured <- Some onFile
        inert "native"

    use watcher =
        FileWatcher.createWithFactories
            tmpDir
            changes.Add
            []
            0.05
            FileWatcher.NativeStartRetry.none
            native
            inertSystem
            (fun _repo _onChange _extras -> inert "polling")

    test <@ watcher.Mode = WatcherMode.NativeEvents @>

    match captured with
    | Some handle -> body handle changes
    | None -> failwith "the macOS watcher never installed a per-file native callback"

[<Fact(Timeout = 15000)>]
let ``a repeated native notification for unchanged bytes emits nothing`` () =
    withTempDir "watcher-content-unchanged" (fun tmpDir ->
        let file = Path.Combine(tmpDir, "src", "Lib.fs")

        withNativeNotifications tmpDir (fun handle changes ->
            File.WriteAllText(file, "let x = 1")
            handle file
            test <@ changes |> Seq.toList = [ SourceChanged [ file ] ] @>

            changes.Clear()
            handle file
            handle file
            test <@ changes |> Seq.toList |> List.isEmpty @>))

[<Fact(Timeout = 15000)>]
let ``a native notification after a real edit emits`` () =
    withTempDir "watcher-content-edited" (fun tmpDir ->
        let file = Path.Combine(tmpDir, "src", "Lib.fs")

        withNativeNotifications tmpDir (fun handle changes ->
            File.WriteAllText(file, "let x = 1")
            handle file
            changes.Clear()

            File.WriteAllText(file, "let x = 2")
            handle file
            test <@ changes |> Seq.toList = [ SourceChanged [ file ] ] @>))

[<Fact(Timeout = 15000)>]
let ``a native notification for a newly created file emits`` () =
    // Absent-then-present is a real change and must not need a hash to compare against.
    withTempDir "watcher-content-created" (fun tmpDir ->
        let seen = Path.Combine(tmpDir, "src", "Lib.fs")
        let created = Path.Combine(tmpDir, "src", "New.fs")

        withNativeNotifications tmpDir (fun handle changes ->
            File.WriteAllText(seen, "let x = 1")
            handle seen
            changes.Clear()

            File.WriteAllText(created, "let y = 2")
            handle created
            test <@ changes |> Seq.toList = [ SourceChanged [ created ] ] @>))

[<Fact(Timeout = 15000)>]
let ``a native notification for a deleted file emits`` () =
    // Present-then-absent is a real change, and there is nothing left to hash.
    withTempDir "watcher-content-deleted" (fun tmpDir ->
        let file = Path.Combine(tmpDir, "src", "Lib.fs")

        withNativeNotifications tmpDir (fun handle changes ->
            File.WriteAllText(file, "let x = 1")
            handle file
            changes.Clear()

            File.Delete(file)
            handle file
            test <@ changes |> Seq.toList = [ SourceChanged [ file ] ] @>))

[<Fact(Timeout = 15000)>]
let ``a native notification for a file the watcher does not track emits nothing`` () =
    // FSEvents reports every file under a watched root, not only F# sources. The
    // relevance filter must drop the rest before the ledger ever hashes them, or an
    // editor's swap file or a stray note becomes a source change.
    withTempDir "watcher-content-untracked" (fun tmpDir ->
        let note = Path.Combine(tmpDir, "src", "notes.txt")

        withNativeNotifications tmpDir (fun handle changes ->
            File.WriteAllText(note, "not F#")
            handle note
            test <@ changes |> Seq.toList |> List.isEmpty @>))

[<Fact(Timeout = 15000)>]
let ``a native notification for a file that is still gone emits nothing`` () =
    // A removal is a change exactly once; a later batch naming the same removed
    // path describes nothing new.
    withTempDir "watcher-content-still-gone" (fun tmpDir ->
        let file = Path.Combine(tmpDir, "src", "Lib.fs")

        withNativeNotifications tmpDir (fun handle changes ->
            File.WriteAllText(file, "let x = 1")
            handle file
            File.Delete(file)
            handle file
            changes.Clear()

            handle file
            test <@ changes |> Seq.toList |> List.isEmpty @>))

[<Fact(Timeout = 15000)>]
let ``a native notification for an unreadable file always emits`` () =
    // Fail closed, as `ContentHash` requires: a file nobody could read is never
    // evidence that nothing happened, so its sentinel must not match itself.
    withTempDir "watcher-content-unreadable" (fun tmpDir ->
        let file = Path.Combine(tmpDir, "src", "Lib.fs")

        withNativeNotifications tmpDir (fun handle changes ->
            File.WriteAllText(file, "let x = 1")
            handle file
            File.SetUnixFileMode(file, UnixFileMode.None)

            try
                changes.Clear()
                handle file
                handle file

                test <@ changes |> Seq.toList = [ SourceChanged [ file ]; SourceChanged [ file ] ] @>
            finally
                File.SetUnixFileMode(file, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)))


[<Collection(FileWatchCollectionName)>]
type RealFileWatcherTests() =

    [<Fact(Timeout = 150000)>]
    member _.``FileWatcher.create with isMacOS=false watches src and tests dirs``() =
        withTempDir "watcher-fsw" (fun tmpDir ->
            let srcDir = Path.Combine(tmpDir, "src")
            let testsDir = Path.Combine(tmpDir, "tests")
            Directory.CreateDirectory(srcDir) |> ignore
            Directory.CreateDirectory(testsDir) |> ignore

            let changes = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()
            let onChange change = changes.Add(change)

            use watcher = FileWatcher.create tmpDir onChange (Some false) [] 0.05

            // File watcher startup can stall under full-suite load. Keep probing
            // until an event arrives instead of treating 10 seconds as a readiness signal.
            probeUntilEvent srcDir (fun () -> changes.Count >= 1) 60000
            test <@ changes.Count >= 1 @>)

    [<Fact(Timeout = 150000)>]
    member _.``watcher detects file changes in src directory``() =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"fshotwatch-test-{Guid.NewGuid():N}")
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let changes = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()
        let onChange change = changes.Add(change)

        use watcher = FileWatcher.create tmpDir onChange None [] 0.05

        // Probe until the watcher is delivering events (setup lag can be an unbounded window).
        probeUntilEvent srcDir (fun () -> changes.Count >= 1) 60000
        test <@ changes.Count >= 1 @>
        Directory.Delete(tmpDir, true)

[<Fact(Timeout = 60000)>]
let ``FileWatcher fallback delivers every built-in recursive filter through one root watcher`` () =
    withTempDir "watcher-fsw-filters" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        let objDir = Path.Combine(srcDir, "obj")
        Directory.CreateDirectory(objDir) |> ignore
        let received = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()

        use watcher = FileWatcher.create tmpDir received.Add (Some false) [] 0.05

        let cases =
            [ Path.Combine(srcDir, "BuiltIn.fs"), SourceChanged []
              Path.Combine(srcDir, "BuiltIn.fsx"), SourceChanged []
              Path.Combine(srcDir, "BuiltIn.fsproj"), ProjectChanged []
              Path.Combine(srcDir, "BuiltIn.props"), ProjectChanged []
              Path.Combine(objDir, "project.assets.json"), ProjectChanged [] ]

        for path, expectedKind in cases do
            let hasPathWithKind () =
                received
                |> Seq.exists (fun change ->
                    match change, expectedKind with
                    | SourceChanged paths, SourceChanged _
                    | ProjectChanged paths, ProjectChanged _ -> paths |> List.contains path
                    | _ -> false)

            probeLoop (fun n -> File.WriteAllText(path, string n)) hasPathWithKind 10000
            test <@ hasPathWithKind () @>

        // The broad subscription is still one owned native resource: disposing
        // the wrapper must stop every built-in kind, not leave a per-filter tail.
        Thread.Sleep(500)
        (watcher :> IDisposable).Dispose()
        let countAfterDispose = received.Count
        File.WriteAllText(Path.Combine(srcDir, "AfterDispose.fsproj"), "<Project />")
        Thread.Sleep(500)
        test <@ received.Count = countAfterDispose @>)

[<Fact(Timeout = 15000)>]
let ``FileWatcher.create with isMacOS=false when neither src nor tests exist`` () =
    withTempDir "watcher-nosrc" (fun tmpDir ->
        let mutable changes: FileChangeKind list = []
        let onChange change = changes <- change :: changes

        use watcher = FileWatcher.create tmpDir onChange (Some false) [] 0.05
        test <@ watcher.Disposables.Length = 1 @>)

// === Unit tests for syntheticPath ===

// A rerun emits a synthetic path so only the target plugin re-fires. Relative
// paths are resolved against the PROCESS working directory downstream, and that
// directory can be deleted out from under a long-lived daemon — after which a
// rerun fails on the vanished cwd rather than on anything to do with the plugin.
// Rooting removes the daemon's dependence on its own cwd for this path.

[<Fact(Timeout = 15000)>]
let ``syntheticPath is absolute, so a rerun never resolves against the process cwd`` () =
    let wildcard =
        FilePattern.syntheticPath "/repo" (FilePattern.parse "*.ratchet.json")

    let literal =
        FilePattern.syntheticPath "/repo" (FilePattern.parse "coverage-ratchet.json")

    test <@ Path.IsPathFullyQualified wildcard @>
    test <@ Path.IsPathFullyQualified literal @>

[<Fact(Timeout = 15000)>]
let ``syntheticPath still matches the pattern it was built from`` () =
    // Rooting must not cost the thing the path exists for: a literal compares by
    // file name and a wildcard by suffix, so both survive an absolute prefix.
    let wildcard = FilePattern.parse "*.ratchet.json"
    let literal = FilePattern.parse "coverage-ratchet.json"

    test <@ FilePattern.matches wildcard (FilePattern.syntheticPath "/repo" wildcard) @>
    test <@ FilePattern.matches literal (FilePattern.syntheticPath "/repo" literal) @>

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

[<Collection(FileWatchCollectionName)>]
type ExtraPatternFileWatcherTests() =

    [<Fact(Timeout = 60000)>]
    member _.``FileWatcher with wildcard pattern fires SourceChanged for matching file``() =
        withTempDir "watcher-extra-wild" (fun tmpDir ->
            let received = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()
            let onChange change = received.Add(change)

            use _watcher =
                FileWatcher.create tmpDir onChange (Some false) [ FilePattern.parse "*.ratchet.json" ] 0.05
                :> IDisposable

            // Rewrite until an event lands: the macOS backend has cold-start latency.
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
    member _.``FileWatcher with literal filename pattern fires only for matching file``() =
        withTempDir "watcher-extra-literal" (fun tmpDir ->
            let received = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()
            let onChange change = received.Add(change)

            use _watcher =
                FileWatcher.create tmpDir onChange (Some false) [ FilePattern.parse "coverage-ratchet.json" ] 0.05
                :> IDisposable

            let configPath = Path.Combine(tmpDir, "coverage-ratchet.json")

            let hasMatch () =
                received
                |> Seq.exists (fun c ->
                    match c with
                    | SourceChanged files ->
                        files |> List.exists (fun f -> Path.GetFileName(f) = "coverage-ratchet.json")
                    | _ -> false)

            probeLoop (fun n -> File.WriteAllText(configPath, $"{{\"probe\": {n}}}")) hasMatch 30000

            test <@ hasMatch () @>)

// === Cross-instance dedup isolation (ContentDedup.Tracker) ===
// The hash store must be scoped per daemon instance, not process-globally. Two
// daemons in one process (parallel test daemons, or a restart over the same repo
// root) watch overlapping absolute paths, and the keys ARE absolute paths — so a
// hash written by daemon A would exactly collide with daemon B's.

[<Fact(Timeout = 15000)>]
let ``Tracker first observation reports changed even when another tracker already saw the file`` () =
    let tmpFile =
        Path.Combine(Path.GetTempPath(), $"fshotwatch-xinstance-{Guid.NewGuid():N}.fs")

    try
        File.WriteAllText(tmpFile, "let x = 1")

        let trackerA = Tracker()
        let trackerB = Tracker()

        test <@ trackerA.HasContentChanged tmpFile = true @>
        // B's first ever observation of that path must still report changed. With a
        // shared global store this returns false and the genuine event is suppressed.
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

// === FilePattern.parse must reject globs it cannot match consistently ===
//
// An embedded (non-leading) `*` diverges between the two matchers: the raw pattern
// goes to FileSystemWatcher.Filter, which GLOBS it (so the OS watcher fires for
// schema.users.sql), while FilePattern.matches treats it as a literal basename or
// suffix and never matches. The event fires and is silently dropped, so the user's
// file command never runs and never errors — parse must reject it at config load.

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

// === project.assets.json: restore metadata is not a package-graph change ===
// A restore run by another tool (Fable's project cracker is the observed one) rewrites
// `obj/project.assets.json` with a different `project.restore` block — Fable records
// `restoreLockProperties` naming its own lock file — while the resolved package graph
// (targets, libraries, packageFolders, frameworks) is byte-for-byte what the build's
// restore wrote. Reported as a change, it re-evaluated MSBuild and re-checked the
// project and every dependent right after a cold scan.

let private assetsJson (restoreExtra: string) (dependencyVersion: string) =
    $$"""{
  "version": 3,
  "targets": { "net10.0": { "FSharp.Core/{{dependencyVersion}}": { "type": "package" } } },
  "libraries": { "FSharp.Core/{{dependencyVersion}}": { "type": "package", "path": "fsharp.core/{{dependencyVersion}}" } },
  "packageFolders": { "/home/u/.nuget/packages/": {} },
  "project": {
    "version": "1.0.0",
    "restore": { "projectName": "App", "projectStyle": "PackageReference"{{restoreExtra}} },
    "frameworks": { "net10.0": { "dependencies": { "FSharp.Core": { "version": "[{{dependencyVersion}}, )" } } } }
  }
}"""

let private withAssetsFile (body: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"fshw-assets-{Guid.NewGuid():N}", "obj")
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "project.assets.json")

    try
        body path
    finally
        Directory.Delete(Path.GetDirectoryName dir, true)

[<Fact(Timeout = 15000)>]
let ``an assets rewrite that changes only restore metadata is not a content change`` () =
    withAssetsFile (fun path ->
        File.WriteAllText(path, assetsJson "" "10.1.401")
        let tracker = Tracker()
        tracker.Observe path

        File.WriteAllText(
            path,
            assetsJson
                """, "restoreLockProperties": { "restorePackagesWithLockFile": "false", "nuGetLockFilePath": "Fable.lock" }"""
                "10.1.401"
        )

        test <@ not (tracker.HasContentChanged path) @>)

[<Fact(Timeout = 15000)>]
let ``an assets rewrite that changes the resolved package graph is a content change`` () =
    withAssetsFile (fun path ->
        File.WriteAllText(path, assetsJson "" "10.1.401")
        let tracker = Tracker()
        tracker.Observe path
        File.WriteAllText(path, assetsJson "" "10.1.402")

        test <@ tracker.HasContentChanged path @>)

[<Fact(Timeout = 15000)>]
let ``an assets file that is not valid JSON is still compared by its bytes`` () =
    withAssetsFile (fun path ->
        File.WriteAllText(path, "{ not json")
        let tracker = Tracker()
        tracker.Observe path
        File.WriteAllText(path, "{ still not json")

        test <@ tracker.HasContentChanged path @>)
