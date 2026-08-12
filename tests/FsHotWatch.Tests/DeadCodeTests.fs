module FsHotWatch.Tests.DeadCodeTests

open System.Collections.Generic
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DeadCode
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Ports

// These tests use injectable `fileExists` / `readVersion` / `print` / `eprint`
// and a seeded temp SQLite DB, so they touch NO process-global state (no Console
// redirect, no logging mutables) and stay parallel-safe without joining the
// LogGlobal / FileWatch DisableParallelization collections.

// --- arg resolution: defaults vs explicit --entry ---

[<Fact(Timeout = 15000)>]
let ``resolveEntryPatterns with no --entry yields the four defaults`` () =
    test <@ resolveEntryPatterns [] = [ "*.main"; "*.Program.*"; "*.Routes.*"; "*.Scheduler.*" ] @>

[<Fact(Timeout = 15000)>]
let ``resolveEntryPatterns with explicit --entry replaces the defaults`` () =
    let explicit = [ "*.Cli.*"; "*.Worker.*" ]
    test <@ resolveEntryPatterns explicit = explicit @>

// --- schema compatibility predicate ---

[<Fact(Timeout = 15000)>]
let ``isCompatibleSchema accepts the expected, newer, and pre-versioning marker`` () =
    test <@ isCompatibleSchema ExpectedSchemaVersion @>
    test <@ isCompatibleSchema (ExpectedSchemaVersion + 1) @>
    test <@ isCompatibleSchema 0 @>

[<Fact(Timeout = 15000)>]
let ``isCompatibleSchema rejects an older schema`` () =
    test <@ not (isCompatibleSchema (ExpectedSchemaVersion - 1)) @>

// --- missing-DB error path ---

[<Fact(Timeout = 15000)>]
let ``run with a missing DB exits 1 and tells the user to start the daemon`` () =
    let errs = List<string>()

    let exitCode =
        run
            (fun _ -> false) // DB does not exist
            (fun _ -> failwith "readVersion must not run when the DB is missing")
            (fun s -> failwith $"print must not run on the missing-DB path: %s{s}")
            errs.Add
            "/repo"
            { EntryPatterns = defaultEntryPatterns
              IncludeTests = false
              Verbose = false }

    test <@ exitCode = 1 @>
    test <@ errs.Count = 1 @>
    test <@ errs.[0].Contains(DbRelativePath) @>
    test <@ errs.[0].Contains("dotnet fshw start") @>

// --- incompatible-schema error path (must NOT recreate the DB) ---

[<Fact(Timeout = 15000)>]
let ``run with an older schema exits 1, names the versions, and never opens the DB`` () =
    let errs = List<string>()

    let exitCode =
        run
            (fun _ -> true) // DB exists
            (fun _ -> ExpectedSchemaVersion - 1) // but is too old
            (fun s -> failwith $"print must not run on the incompatible-schema path: %s{s}")
            errs.Add
            "/repo"
            { EntryPatterns = defaultEntryPatterns
              IncludeTests = false
              Verbose = false }

    test <@ exitCode = 1 @>
    test <@ errs.Count = 1 @>
    test <@ errs.[0].Contains("Refusing to touch it") @>
    test <@ errs.[0].Contains(string (ExpectedSchemaVersion - 1)) @>

// --- end-to-end over a seeded DB ---

/// Seed a temp SQLite DB at `<repoRoot>/.fshw/test-impact.db` with:
///   - an entry-point symbol (`App.Program.run`, matches `*.Program.*`)
///   - a reachable symbol (`App.Lib.used`, depended on by the entry point)
///   - an unreachable symbol (`App.Lib.orphan`, no incoming edges)
/// Returns the SymbolStore over the seeded DB.
let private seedDb (repoRoot: string) : SymbolStore =
    let fshwDir = Path.Combine(repoRoot, ".fshw")
    Directory.CreateDirectory(fshwDir) |> ignore
    let dbPath = Path.Combine(fshwDir, "test-impact.db")
    let db = Database.create dbPath

    let sym (name: string) (line: int) : SymbolInfo =
        { FullName = name
          Kind = SymbolKind.Value
          SourceFile = "src/Lib.fs"
          LineStart = line
          LineEnd = line
          ContentHash = "h"
          IsExtern = false }

    let entry = sym "App.Program.run" 10
    let used = sym "App.Lib.used" 20
    let orphan = sym "App.Lib.orphan" 30

    let result =
        AnalysisResult.Create(
            [ entry; used; orphan ],
            // entry depends on used → used is reachable; orphan has no incoming edge.
            [ { FromSymbol = "App.Program.run"
                ToSymbol = "App.Lib.used"
                Kind = DependencyKind.Calls
                Source = "core" } ],
            []
        )

    db.RebuildProjects([ result ])
    toSymbolStore db

[<Fact(Timeout = 30000)>]
let ``analyze flags the orphan and not the reachable symbol`` () =
    withTempDir "deadcode-e2e" (fun repoRoot ->
        let store = seedDb repoRoot

        let result =
            analyze
                store
                { EntryPatterns = defaultEntryPatterns
                  IncludeTests = false
                  Verbose = false }

        let unreachableNames =
            result.UnreachableSymbols |> List.map (fun u -> u.Symbol.FullName)

        test <@ result.Base.TotalSymbols = 3 @>
        // entry + used are reachable (entry matches *.Program.*, used via the edge).
        test <@ result.Base.ReachableSymbols = 2 @>
        test <@ List.contains "App.Lib.orphan" unreachableNames @>
        test <@ not (List.contains "App.Lib.used" unreachableNames) @>
        test <@ not (List.contains "App.Program.run" unreachableNames) @>)

[<Fact(Timeout = 30000)>]
let ``run over a seeded DB prints the report and exits 0`` () =
    withTempDir "deadcode-run" (fun repoRoot ->
        seedDb repoRoot |> ignore
        let outs = List<string>()
        let errs = List<string>()

        let exitCode =
            run
                File.Exists
                (fun _ -> ExpectedSchemaVersion) // compatible
                outs.Add
                errs.Add
                repoRoot
                { EntryPatterns = defaultEntryPatterns
                  IncludeTests = false
                  Verbose = false }

        let joined = System.String.Join("\n", outs)
        test <@ exitCode = 0 @>
        test <@ errs.Count = 0 @>
        test <@ joined.Contains("Dead code analysis:") @>
        test <@ joined.Contains("Total symbols: 3") @>
        test <@ joined.Contains("Reachable: 2") @>
        test <@ joined.Contains("App.Lib.orphan") @>
        test <@ not (joined.Contains("App.Lib.used")) @>)

// --- formatReport shape ---

[<Fact(Timeout = 30000)>]
let ``verbose run annotates each unreachable symbol with a reason`` () =
    withTempDir "deadcode-verbose" (fun repoRoot ->
        let store = seedDb repoRoot

        let result =
            analyze
                store
                { EntryPatterns = defaultEntryPatterns
                  IncludeTests = false
                  Verbose = true }

        let lines = formatReport true result
        let joined = System.String.Join("\n", lines)

        test <@ joined.Contains("App.Lib.orphan") @>
        test <@ joined.Contains("no incoming edges") @>)

[<Fact(Timeout = 15000)>]
let ``formatReport with no unreachable symbols prints only the summary`` () =
    let empty: TestPrune.DeadCode.VerboseDeadCodeResult =
        { Base =
            { TotalSymbols = 5
              ReachableSymbols = 5
              UnreachableSymbols = [] }
          UnreachableSymbols = [] }

    let lines = formatReport false empty

    test
        <@
            lines = [ "Dead code analysis:"
                      "  Total symbols: 5"
                      "  Reachable: 5"
                      "  Potentially unreachable: 0" ]
        @>
