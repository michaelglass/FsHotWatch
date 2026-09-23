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
