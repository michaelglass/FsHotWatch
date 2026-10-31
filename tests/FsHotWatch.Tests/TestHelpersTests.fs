module FsHotWatch.Tests.TestHelpersTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers

// Regression guard: `withTempDir`'s `finally` recursively deletes the scratch dir
// on the test thread, while a daemon whose shutdown wait has timed out can keep
// (re)creating files under `.fshw/errors/`. That race threw `IOException
// "Directory not empty"` ON THE TEST THREAD, which the host reported as an
// unattributed "failed: 1" with tests unreported. Cleanup must survive a
// concurrent writer.

/// Hammer the tree under `dir` the way a not-yet-drained daemon does (recreate a
/// nested subdir and write files into it) for a short burst, then stop on its
/// own. The burst is timed to still be in flight when `withTempDir` starts its
/// recursive delete (proving resilience) but to stop quickly so the delete's
/// retry converges within a few attempts.
let private hammerTreeBriefly (dir: string) (burst: TimeSpan) : Thread =
    let run () =
        let nested = Path.Combine(dir, ".fshw", "errors")
        let deadline = DateTime.UtcNow + burst
        let mutable n = 0

        while DateTime.UtcNow < deadline do
            try
                Directory.CreateDirectory(nested) |> ignore
                File.WriteAllText(Path.Combine(nested, $"fcs-{n}.json"), "{}")
                n <- n + 1
            with _ ->
                // The delete may yank the dir mid-write — mirror the daemon's
                // tolerated FileErrorReporter behaviour and keep hammering.
                ()

    let t = Thread(ThreadStart(run))
    t.IsBackground <- true
    t.Start()
    t

[<WallClock("simulated work: the sleep gives fake work a duration, or widens a race window, so events overlap; every outcome is awaited by an event or a named budget, not by the sleep")>]
[<Fact(Timeout = 20000)>]
let ``withTempDir cleanup tolerates a daemon still writing into the dir`` () =
    // Repeat so the cleanup window reliably overlaps a live writer: the bug threw
    // on ~80% of single attempts, so 8 makes a pre-fix regression near-certain.
    for _ in 1..8 do
        let mutable hammer = Unchecked.defaultof<Thread>

        withTempDir "helpers-cleanup-race" (fun tmpDir ->
            hammer <- hammerTreeBriefly tmpDir (TimeSpan.FromMilliseconds(75.0))
            Thread.Sleep(10))

        hammer.Join(TimeSpan.FromSeconds(2.0)) |> ignore

    // Reaching here means every cleanup race returned without throwing.
    test <@ true @>

// ---------------------------------------------------------------------------
// Clearing a SQLite pool must reach ONE database, not the process.
//
// `SqliteConnection.ClearAllPools()` disposes the pooled native handles of every
// database in the process. A class opening a pooled connection at that instant is handed
// a disposed `SQLitePCL.sqlite3` and fails inside its own open — a red on a test that
// touched nothing. `clearSqlitePool` clears by connection string, and the string is a
// per-test temp path, so nothing else can be standing in it.
// ---------------------------------------------------------------------------

/// The `.fs` sources of this test project, read from the tree rather than the build
/// output: `AppContext.BaseDirectory` is `bin/<config>/<tfm>` under the project.
let private testProjectSources () =
    let projectDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../"))

    Directory.GetFiles(projectDir, "*.fs")
    |> Array.map (fun path -> Path.GetFileName path, File.ReadAllLines path)

/// The lines of `file` that CALL `needle`, ignoring the ones that merely name it in a
/// comment — a guard that cannot tell a prohibition from its own explanation is a guard
/// nobody can document.
let private callSites (needle: string) (lines: string[]) =
    lines
    |> Array.filter (fun line -> not ((line.TrimStart()).StartsWith "//") && line.Contains needle)

let private libFooSymbol: TestPrune.AstAnalyzer.SymbolInfo =
    { FullName = "Lib.foo"
      Kind = TestPrune.AstAnalyzer.SymbolKind.Value
      SourceFile = "src/Lib.fs"
      LineStart = 1
      LineEnd = 1
      ContentHash = "hash-v1"
      IsExtern = false }

[<Fact(Timeout = 20000)>]
let ``clearSqlitePool drops the pooled connections of its own database`` () =
    withTempDir "sqlite-pool-clear" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = TestPrune.Database.Database.create dbPath
        db.RebuildProjects [ TestPrune.AstAnalyzer.AnalysisResult.Create([ libFooSymbol ], [], []) ]
        test <@ (db.GetSymbolsInFile "src/Lib.fs").Length = 1 @>

        File.Delete dbPath

        // PositiveControl: pooling is really in play. A later open is handed the pooled
        // connection to the DELETED inode, rows intact — which is why deleting the file
        // is not a way to empty this index, and why the clear below has work to do.
        let stale = TestPrune.Database.Database.create dbPath
        test <@ (stale.GetSymbolsInFile "src/Lib.fs").Length = 1 @>

        clearSqlitePool dbPath

        let reopened = TestPrune.Database.Database.create dbPath
        test <@ (reopened.GetSymbolsInFile "src/Lib.fs").IsEmpty @>)

[<Fact(Timeout = 20000)>]
let ``no test clears the process-wide SQLite pool`` () =
    // One class clearing every pool in the process is how a passing class fails: the
    // clear lands between another class's `Database.create` and its first read.
    // Spelled in halves so this guard is not itself a match for what it forbids.
    let processWideClear = "ClearAll" + "Pools"

    let offenders =
        testProjectSources ()
        |> Array.filter (fun (_, lines) -> callSites processWideClear lines |> Array.isEmpty |> not)
        |> Array.map fst
        |> List.ofArray

    Assert.True(List.isEmpty offenders, $"these sources clear every pool in the process: %A{offenders}")

[<Fact(Timeout = 20000)>]
let ``every source that redirects Console.Error joins the serialized collection`` () =
    // `Console.SetError` is process-wide: two classes redirecting it in parallel restore
    // each other's writers, so one class's output lands in the other's capture and the
    // capturing test asserts against a stranger. The only exclusion that works is the
    // one every redirector honours, so a redirector outside the collection is a defect
    // wherever it sits — this went unnoticed in one file out of twelve.
    //
    // File granularity, deliberately: the attribute may sit on the module or on a type
    // inside it, and a file that redirects anywhere needs the serialization everywhere.
    // Spelled in halves so this guard is not itself a match for what it looks for.
    let redirect = "Console." + "SetError"
    let serialized = "LogGlobal" + "CollectionName"

    let unserialized =
        testProjectSources ()
        |> Array.filter (fun (_, lines) -> callSites redirect lines |> Array.isEmpty |> not)
        |> Array.filter (fun (_, lines) -> callSites serialized lines |> Array.isEmpty)
        |> Array.map fst
        |> List.ofArray

    Assert.True(
        List.isEmpty unserialized,
        $"these sources redirect Console.Error without joining the serialized collection: %A{unserialized}"
    )

// ---------------------------------------------------------------------------
// A test that reads the process working directory runs serialized (AUTOMATION-1411).
//
// In-process MSBuild evaluation stands the PROCESS in the project it evaluates, and a
// daemon in a parallel test evaluates whenever it discovers projects. A test running
// beside it that reads the working directory, or resolves a relative path against it,
// reads that test's scratch project instead of its own: EventTests did, once in 6,704 on
// a Linux runner. The serialized collection runs alone, so a reader in it sees only what
// it set itself.
//
// Scoped by declaration, not by file: the collection may sit on the module (above its
// `module` line) or on a type, and a reader is serialized only inside one that carries it.
// ---------------------------------------------------------------------------

/// What reads the process working directory, spelled in halves so this guard is not
/// itself a match: the runtime's own reads, the TestHelpers that stand in or move it,
/// and a relative path literal resolved against it.
let private workingDirectoryReads =
    [ "Directory." + "GetCurrentDirectory"
      "Environment." + "CurrentDirectory"
      "process" + "WorkingDirectory"
      "withProcess" + "WorkingDirectory"
      "evaluateIn" + "ProjectDirectory"
      "standLike" + "TheHostVerb"
      "moveProcess" + "ToRefuge" ]

let private relativePathResolution =
    Text.RegularExpressions.Regex(
        "(AbsFilePath|AbsProjectPath)\\.create\\s*\\(?\\s*[$@]*\"[^/{\"]|Path\\.GetFullPath\\s*\\(?\\s*[$@]*\"[^/{\"]"
    )

let private readsWorkingDirectory (line: string) =
    let trimmed = line.TrimStart()

    not (trimmed.StartsWith "//")
    && (workingDirectoryReads |> List.exists line.Contains
        || relativePathResolution.IsMatch line)

/// The 1-based lines of `lines` that read the process working directory outside a
/// declaration carrying `[<Collection(LogGlobalCollectionName)>]`.
let private unserializedWorkingDirectoryReads (lines: string[]) : int list =
    let serialized (attributeLine: string) =
        attributeLine.StartsWith "[<"
        && attributeLine.Contains("LogGlobal" + "CollectionName")

    // The attribute lines directly above line `i`, skipping doc comments.
    let attributesAbove (i: int) =
        let rec walk j acc =
            if j < 0 then
                acc
            else
                let line = lines[j]

                if line.StartsWith "[<" then walk (j - 1) (line :: acc)
                elif line.StartsWith "///" then walk (j - 1) acc
                else acc

        walk (i - 1) []

    let isDeclaration (line: string) =
        line.StartsWith "let " || line.StartsWith "type " || line.StartsWith "module "

    let moduleSerialized =
        lines
        |> Array.tryFindIndex (fun line -> line.StartsWith "module ")
        |> Option.exists (fun i -> attributesAbove i |> List.exists serialized)

    if moduleSerialized then
        []
    else
        lines
        |> Array.indexed
        |> Array.filter (fun (_, line) -> readsWorkingDirectory line)
        |> Array.choose (fun (i, _) ->
            // The declaration this line belongs to: the nearest one at column 0 above it,
            // or the line itself when it is one.
            let owner = seq { i .. -1 .. 0 } |> Seq.tryFind (fun j -> isDeclaration lines[j])

            match owner with
            | Some j when lines[j].StartsWith "type " && attributesAbove j |> List.exists serialized -> None
            | _ -> Some(i + 1))
        |> List.ofArray

[<Fact(Timeout = 20000)>]
let ``the working-directory guard tells a serialized reader from one beside the parallel tests`` () =
    // PositiveControl for the guard below: it flags a read in a plain test, or in a type
    // without the collection, and passes one inside a type or module that has it.
    let attribute = "[<Collection(" + "LogGlobal" + "CollectionName)>]"
    let read = "    let cwd = Directory." + "GetCurrentDirectory()"
    let relative = "    let p = AbsFilePath" + ".create \"Lib.fs\""
    let rooted = "    let p = AbsFilePath" + ".create \"/repo/Lib.fs\""

    let parallelModule =
        [| "module M"
           "[<Fact>]"
           "let ``a`` () ="
           read
           "[<Collection(\"Other\")>]"
           "type T() ="
           relative
           "let ``b`` () ="
           rooted |]

    let serializedType =
        [| "module M"
           attribute
           "/// doc"
           "type T() ="
           "    member _.x() ="
           read
           relative |]

    let serializedModule = [| attribute; "module M"; "let ``a`` () ="; read |]

    test <@ unserializedWorkingDirectoryReads parallelModule = [ 4; 7 ] @>
    test <@ List.isEmpty (unserializedWorkingDirectoryReads serializedType) @>
    test <@ List.isEmpty (unserializedWorkingDirectoryReads serializedModule) @>

[<Fact(Timeout = 20000)>]
let ``every test that reads the process working directory runs in the serialized collection`` () =
    // TestHelpers defines the readers (their callers are what is checked). The
    // working-directory guard runs after every test, in every collection, and waits out
    // any evaluation before it moves the process.
    let allowed = set [ "TestHelpers.fs"; "ProcessWorkingDirectoryGuard.fs" ]

    let offenders =
        testProjectSources ()
        |> Array.filter (fun (name, _) -> not (allowed.Contains name))
        |> Array.choose (fun (name, lines) ->
            match unserializedWorkingDirectoryReads lines with
            | [] -> None
            | found -> Some(name, found))
        |> List.ofArray

    Assert.True(
        List.isEmpty offenders,
        $"these sources read the process working directory beside the parallel tests (file, lines): %A{offenders}"
    )

// ---------------------------------------------------------------------------
// A bounded wait must not report an expiry as a success.
//
// A caller that waits and then asserts something NEGATIVE ("no cache entry was written")
// is satisfied identically by "the work ran and wrote nothing" and "the wait gave up
// before the work ran". Only the wait can tell those apart, so an expiry has to reach
// the caller.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``waitUntil throws when its condition never holds`` () =
    // With no floor, so the expiry is reached at the budget given rather than the hang cap.
    let ex =
        Assert.Throws<TimeoutException>(fun () -> waitUntilOrAtLeast 0 "the awaited condition" (fun () -> false) 100)

    test <@ ex.Message.Contains "100 ms" @>

[<Fact(Timeout = 5000)>]
let ``waitUntil returns once its condition holds`` () =
    // PositiveControl: the throw above is the expiry, not a wait that always throws.
    let mutable polls = 0

    waitUntil
        (fun () ->
            polls <- polls + 1
            polls >= 3)
        5000

    test <@ polls = 3 @>

[<Fact(Timeout = 2 * HangWaitMs)>]
let ``waitUntil never gives up on a short budget before the hang cap`` () =
    // A positive wait's budget is a hang cap, never the claim: 105 call sites passed 5-20 s
    // budgets that a box at load 140-190 ran out of (AUTOMATION-1364). The condition holds
    // only on the fifth poll, 200 ms in at the 50 ms poll interval, well past a 1 ms budget.
    let mutable polls = 0

    waitUntil
        (fun () ->
            polls <- polls + 1
            polls >= 5)
        1

    test <@ polls = 5 @>

// ---------------------------------------------------------------------------
// A wait for a daemon started in this process to serve IPC.
//
// Daemon startup (config load, watcher subscription, binding the pipe) has no product
// bound, so a fixed short poll for it measures the box: a 10 s one failed two loaded
// release gates while the daemon was still starting. `awaitServing` waits up to
// `DaemonStartupWaitMs` and ends at once, naming why, when the daemon stopped instead.
// ---------------------------------------------------------------------------

// "At once" is the message, not a clock: a wait that ignored the stopped run would meet
// the hang guard and say the daemon was still starting. The cap sits above that guard.
[<Fact(Timeout = 2 * DaemonStartupWaitMs)>]
let ``awaitServing fails at once, naming why, when the daemon stopped before serving`` () =
    let pipe = $"fshw-never-served-{Guid.NewGuid():N}"
    let stopped = Tasks.Task.FromResult 3

    let ex = Assert.ThrowsAny<exn>(fun () -> awaitServing pipe (daemonStopped stopped))

    test
        <@
            ex.Message.Contains "stopped before it served IPC"
            && ex.Message.Contains "returned 3"
        @>

[<Fact(Timeout = 20000)>]
let ``every wait for a daemon to serve IPC goes through awaitServing`` () =
    // A `waitUntil…` whose condition probes the IPC client is a readiness poll with its
    // own budget. The one exception is a bare `IpcServer` started by its own test, which
    // binds one pipe and loads nothing.
    // Spelled in halves so this guard is not itself a match for what it looks for.
    let probe = "IpcClient" + "."
    let poll = "wait" + "Until"
    let allowed = set [ "TestHelpers.fs"; "IpcTests.fs" ]

    // A wait's condition: its own line through the line before its budget, which the
    // formatter puts on a line of its own (a literal or a `…Ms` name) in a multi-line call.
    let isBudget (line: string) =
        Text.RegularExpressions.Regex.IsMatch(line.Trim(), @"^(\d[\d_]*|[A-Z]\w*Ms)\)*,?$")

    let condition (lines: string[]) i =
        let last = min (lines.Length - 1) (i + 12)

        let budget =
            [ i + 1 .. last ]
            |> List.tryFind (fun j -> isBudget lines[j])
            |> Option.defaultValue (i + 1)

        lines[i .. budget - 1]

    let offenders =
        testProjectSources ()
        |> Array.filter (fun (name, _) -> not (allowed.Contains name))
        |> Array.collect (fun (name, lines) ->
            lines
            |> Array.mapi (fun i line -> i, line)
            |> Array.filter (fun (_, line) -> not ((line.TrimStart()).StartsWith "//") && line.Contains poll)
            |> Array.filter (fun (i, _) -> condition lines i |> Array.exists (fun l -> l.Contains probe))
            |> Array.map (fun (i, _) -> $"%s{name}:%d{i + 1}"))
        |> List.ofArray

    Assert.True(List.isEmpty offenders, $"these waits poll a daemon's IPC with their own budget: %A{offenders}")
