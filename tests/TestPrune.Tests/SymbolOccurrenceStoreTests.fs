module TestPrune.Tests.SymbolOccurrenceStoreTests

// The store seam for signature/implementation pairs, with hand-built analysis results so
// the storage contract is pinned independently of what the analyzer emits.
//
// Library.fsi and Library.fs declare the SAME canonical names. Each file owns its own
// occurrence (location + hash) and the edges its analysis produced. The 8.2.0
// under-selection was one file's re-index erasing the other's facts: with one row per
// name, the last-indexed file overwrote the signature's hash, so a signature edit no
// longer reached its consumers. These tests re-index each file ALONE, in both
// directions, and require the other file's occurrence, edges and selection to survive.

open System
open System.IO
open Xunit
open Swensen.Unquote
open Microsoft.Data.Sqlite
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.ImpactAnalysis
open TestPrune.InMemoryStore
open TestPrune.Ports
open TestPrune.Tests.TestHelpers

let private symbol name kind file line hash =
    { FullName = name
      Kind = kind
      SourceFile = file
      LineStart = line
      LineEnd = line
      ContentHash = hash
      IsExtern = false }

let private placeholder name kind =
    { symbol name kind ExternSourceFile 0 "" with
        IsExtern = true }

let private edge from target kind =
    { FromSymbol = from
      ToSymbol = target
      Kind = kind
      Source = "core" }

let private result symbols dependencies tests parentLinks =
    { AnalysisResult.Create(symbols, dependencies, tests) with
        ParentLinks = parentLinks }

/// `Library.fsi`: `type Token = { Value: int }` and `val calculate: Token -> int`.
let private signatureResult calculateHash =
    result
        [ symbol "Library" Module "Library.fsi" 1 "sig-module"
          symbol "Library.Token" Type "Library.fsi" 2 "sig-token"
          symbol "Library.Token.Value" Property "Library.fsi" 3 "sig-value"
          symbol "Library.calculate" Function "Library.fsi" 4 calculateHash ]
        [ edge "Library.calculate" "Library.Token" UsesType ]
        []
        [ { Child = "Library.Token.Value"
            Parent = "Library.Token" } ]

/// `Library.fs`: the implementation of the same names.
let private implementationResult =
    result
        [ symbol "Library" Module "Library.fs" 1 "impl-module"
          symbol "Library.Token" Type "Library.fs" 2 "impl-token"
          symbol "Library.Token.Value" Property "Library.fs" 2 "impl-value"
          symbol "Library.calculate" Function "Library.fs" 3 "impl-calculate"
          placeholder "Microsoft.FSharp.Core.Operators.(+)" ExternRef ]
        [ edge "Library.calculate" "Microsoft.FSharp.Core.Operators.(+)" Calls ]
        []
        [ { Child = "Library.Token.Value"
            Parent = "Library.Token" } ]

/// `Consumer.fs`: a test that calls `Library.calculate`.
let private consumerResult =
    result
        [ symbol "Consumer" Module "Consumer.fs" 1 "consumer-module"
          symbol "Consumer.exercisesLibrary" Function "Consumer.fs" 3 "consumer-test"
          placeholder "Library.calculate" ExternRef ]
        [ edge "Consumer.exercisesLibrary" "Library.calculate" Calls ]
        [ { SymbolFullName = "Consumer.exercisesLibrary"
            TestProject = "Consumer.Tests"
            TestClass = "Consumer"
            TestMethod = "exercisesLibrary" } ]
        []

let private allResults =
    [ signatureResult "sig-calculate"; implementationResult; consumerResult ]

let private occurrence (store: SymbolStore) file name =
    store.GetSymbolsInFile file |> List.filter (fun s -> s.FullName = name)

let private ownsEdge (store: SymbolStore) file from target =
    store.GetDependenciesFromFile file
    |> List.exists (fun d -> d.FromSymbol = from && d.ToSymbol = target)

/// A signature edit — `calculate`'s declaration changed — selected through the real
/// changed-file path, which diffs the edited file against its STORED occurrences.
let private signatureEditSelectsConsumer (store: SymbolStore) =
    let edited = (signatureResult "sig-calculate-EDITED").Symbols

    match
        selectTests store [ "Library.fsi" ] (Map.ofList [ "Library.fsi", edited ])
        |> fst
    with
    | RunSubset selected ->
        selected
        |> List.exists (fun t -> t.SymbolFullName = "Consumer.exercisesLibrary")
    | RunAll _ -> false

module ``Signature under-selection (8_2_0 regression)`` =

    [<Fact>]
    let ``re-indexing the implementation alone keeps the signature's occurrence, edges and selection`` () =
        withDb (fun db ->
            db.RebuildProjects allResults
            db.RebuildProjects [ implementationResult ]
            let store = toSymbolStore db

            test
                <@ (occurrence store "Library.fsi" "Library.calculate" |> List.map _.ContentHash) = [ "sig-calculate" ] @>

            test <@ (occurrence store "Library.fsi" "Library.calculate" |> List.map _.LineStart) = [ 4 ] @>
            test <@ ownsEdge store "Library.fsi" "Library.calculate" "Library.Token" @>
            test <@ signatureEditSelectsConsumer store @>)

    [<Fact>]
    let ``re-indexing the signature alone keeps the implementation's occurrence, edges and selection`` () =
        withDb (fun db ->
            db.RebuildProjects allResults
            db.RebuildProjects [ signatureResult "sig-calculate" ]
            let store = toSymbolStore db

            test
                <@ (occurrence store "Library.fs" "Library.calculate" |> List.map _.ContentHash) = [ "impl-calculate" ] @>

            test <@ (occurrence store "Library.fs" "Library.calculate" |> List.map _.LineStart) = [ 3 ] @>
            test <@ ownsEdge store "Library.fs" "Library.calculate" "Microsoft.FSharp.Core.Operators.(+)" @>

            let implementationEdit =
                implementationResult.Symbols
                |> List.filter (fun s -> not s.IsExtern)
                |> List.map (fun s ->
                    if s.FullName = "Library.calculate" then
                        { s with
                            ContentHash = "impl-calculate-EDITED" }
                    else
                        s)

            match
                selectTests store [ "Library.fs" ] (Map.ofList [ "Library.fs", implementationEdit ])
                |> fst
            with
            | RunSubset selected ->
                test
                    <@
                        selected
                        |> List.exists (fun t -> t.SymbolFullName = "Consumer.exercisesLibrary")
                    @>
            | RunAll reason -> failwith $"implementation lost its baseline: {reason}")

    [<Fact>]
    let ``one node per name, and a symbol survives until its last occurrence is removed`` () =
        withDb (fun db ->
            db.RebuildProjects allResults
            test <@ (db.GetAllSymbols() |> List.filter (fun s -> s.FullName = "Library.calculate")).Length = 2 @>

            // The implementation drops `calculate`; the signature still declares it.
            let withoutCalculate =
                { implementationResult with
                    Symbols =
                        implementationResult.Symbols
                        |> List.filter (fun s -> s.FullName <> "Library.calculate")
                    Dependencies = [] }

            db.RebuildProjects [ withoutCalculate ]
            test <@ db.GetAllSymbolNames().Contains "Library.calculate" @>

            test
                <@
                    db.QueryAffectedTests [ "Library.calculate" ]
                    |> List.exists (fun t -> t.TestMethod = "exercisesLibrary")
                @>

            // Now the signature drops it too: the symbol and its incoming edges go.
            let signatureWithoutCalculate =
                let s = signatureResult "unused"

                { s with
                    Symbols = s.Symbols |> List.filter (fun x -> x.FullName <> "Library.calculate")
                    Dependencies = [] }

            db.RebuildProjects [ signatureWithoutCalculate ]
            test <@ not (db.GetAllSymbolNames().Contains "Library.calculate") @>)

/// Every read on the store port, normalised for order.
type StoreSnapshot =
    { Symbols: (string * SymbolInfo list) list
      Dependencies: (string * Dependency list) list
      Tests: (string * TestMethodInfo list) list
      ParentLinks: (string * SymbolParentLink list) list
      AllSymbols: SymbolInfo list
      Names: string list
      Affected: (string * TestMethodInfo list) list
      Reachable: (string * Set<string>) list
      Incoming: Map<string, string list>
      TestNames: Set<string> }

module ``One result per source file`` =

    /// A project's results merged into one: the signature and the implementation both
    /// declare `Library.calculate`, so the result cannot say which file owns its facts.
    let private merged =
        { AnalysisResult.Create(
              (signatureResult "sig-calculate").Symbols @ implementationResult.Symbols,
              (signatureResult "sig-calculate").Dependencies
              @ implementationResult.Dependencies,
              []
          ) with
            ParentLinks = (signatureResult "sig-calculate").ParentLinks }

    let private rejection (act: unit -> unit) =
        try
            act ()
            None
        with :? ArgumentException as ex ->
            Some ex.Message

    [<Fact>]
    let ``a result declaring one name in two files is rejected by both stores, naming it`` () =
        let fromSqlite =
            let path = tempDbPath ()

            try
                rejection (fun () -> (Database.create path).RebuildProjects [ merged ])
            finally
                SqliteConnection.ClearAllPools()
                cleanupDb path

        let fromMemory = rejection (fun () -> fromAnalysisResults [ merged ] |> ignore)

        for message in [ fromSqlite; fromMemory ] do
            test <@ message.IsSome @>
            test <@ message.Value.Contains "Library.calculate (Library.fsi, Library.fs)" @>
            test <@ message.Value.Contains "one AnalysisResult per source file" @>

    [<Fact>]
    let ``a multi-file result that declares each name once is still accepted`` () =
        // Only the ambiguous shape is rejected: a hand-built result spanning files, with no
        // name declared twice, still has one owner per fact.
        let spanning =
            AnalysisResult.Create(
                implementationResult.Symbols @ consumerResult.Symbols,
                implementationResult.Dependencies @ consumerResult.Dependencies,
                consumerResult.TestMethods
            )

        withDb (fun db ->
            db.RebuildProjects [ spanning ]
            test <@ (db.GetSymbolsInFile "Consumer.fs").Length = 2 @>)

        test
            <@
                (fromAnalysisResults [ spanning ]).GetSymbolsInFile "Library.fs"
                |> List.isEmpty
                |> not
            @>

module ``Store parity`` =

    /// Every read on the port, normalised for order, for the files and names in the fixture.
    let private snapshot (store: SymbolStore) : StoreSnapshot =
        let files = [ "Library.fsi"; "Library.fs"; "Consumer.fs" ]
        let names = store.GetAllSymbolNames() |> Set.toList

        let perFile read =
            files |> List.map (fun f -> f, read f |> List.distinct |> List.sort)

        { Symbols = perFile store.GetSymbolsInFile
          Dependencies = perFile store.GetDependenciesFromFile
          Tests = perFile store.GetTestMethodsInFile
          ParentLinks = perFile store.GetParentLinksInFile
          AllSymbols = store.GetAllSymbols() |> List.sort
          Names = names
          Affected =
            names
            |> List.map (fun n -> n, store.QueryAffectedTests [ n ] |> List.distinct |> List.sort)
          Reachable = names |> List.map (fun n -> n, store.GetReachableSymbols [ n ])
          Incoming =
            store.GetIncomingEdgesBatch names
            |> Map.map (fun _ froms -> froms |> List.distinct |> List.sort)
          TestNames = store.GetTestMethodSymbolNames() }

    [<Fact>]
    let ``SQLite and in-memory stores agree on every read, whatever file was re-indexed last`` () =
        let memory = snapshot (fromAnalysisResults allResults)

        for reindex in
            [ allResults
              [ implementationResult ]
              [ signatureResult "sig-calculate" ]
              [ consumerResult ]
              List.rev allResults ] do
            withDb (fun db ->
                db.RebuildProjects allResults
                db.RebuildProjects reindex
                let persisted = snapshot (toSymbolStore db)
                test <@ persisted = memory @>)

module ``Schema 13 to 14`` =

    /// The v13 schema exactly as TestPrune.Core 9.0.0 created it: one `symbols` row per
    /// name carrying a single source_file/content_hash.
    let private v13Schema =
        """
    CREATE TABLE IF NOT EXISTS symbols (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        full_name TEXT NOT NULL UNIQUE,
        kind TEXT NOT NULL,
        source_file TEXT NOT NULL,
        line_start INTEGER NOT NULL,
        line_end INTEGER NOT NULL,
        content_hash TEXT NOT NULL DEFAULT '',
        is_extern INTEGER NOT NULL DEFAULT 0,
        parent_symbol_id INTEGER REFERENCES symbols(id) ON DELETE SET NULL,
        indexed_at TEXT NOT NULL,
        CONSTRAINT symbols_full_name_is_qualified
            CHECK (kind = 'Module' OR full_name LIKE '%.%')
    );

    CREATE TABLE IF NOT EXISTS dependencies (
        from_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        to_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        dep_kind TEXT NOT NULL,
        source TEXT NOT NULL DEFAULT 'core',
        PRIMARY KEY (from_symbol_id, to_symbol_id, dep_kind)
    );

    CREATE TABLE IF NOT EXISTS test_methods (
        symbol_id INTEGER PRIMARY KEY REFERENCES symbols(id) ON DELETE CASCADE,
        test_project TEXT NOT NULL,
        test_class TEXT NOT NULL,
        test_method TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS project_keys (
        project_name TEXT PRIMARY KEY,
        key TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS file_keys (
        source_file TEXT PRIMARY KEY,
        key TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS index_metadata (
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS analysis_events (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        run_id TEXT NOT NULL,
        timestamp TEXT NOT NULL,
        event_type TEXT NOT NULL,
        event_data TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS symbol_attributes (
        symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        attribute_name TEXT NOT NULL,
        args_json TEXT NOT NULL DEFAULT '[]',
        PRIMARY KEY (symbol_id, attribute_name, args_json)
    );

    CREATE TABLE IF NOT EXISTS coverage_points (
        symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        line_offset INTEGER NOT NULL,
        kind TEXT NOT NULL DEFAULT 'line',
        hits INTEGER NOT NULL DEFAULT 0,
        PRIMARY KEY (symbol_id, line_offset, kind)
    );

    CREATE TABLE IF NOT EXISTS runtime_coverage (
        test_project TEXT NOT NULL,
        source_file TEXT NOT NULL,
        PRIMARY KEY (test_project, source_file)
    );

    CREATE TABLE IF NOT EXISTS runtime_coverage_baselines (
        test_project TEXT PRIMARY KEY,
        run_id TEXT NOT NULL,
        observed_at TEXT NOT NULL
    );

    CREATE INDEX IF NOT EXISTS idx_symbols_by_file ON symbols (source_file);
    CREATE INDEX IF NOT EXISTS idx_symbols_by_parent ON symbols (parent_symbol_id);
    CREATE INDEX IF NOT EXISTS idx_deps_to ON dependencies (to_symbol_id);
    CREATE INDEX IF NOT EXISTS idx_deps_from ON dependencies (from_symbol_id);
    CREATE INDEX IF NOT EXISTS idx_events_run_id ON analysis_events(run_id);
    CREATE INDEX IF NOT EXISTS idx_events_type ON analysis_events(event_type);
    CREATE INDEX IF NOT EXISTS idx_symbol_attrs_by_symbol ON symbol_attributes (symbol_id);
    CREATE INDEX IF NOT EXISTS idx_coverage_by_symbol ON coverage_points (symbol_id);
    CREATE INDEX IF NOT EXISTS idx_runtime_coverage_by_file ON runtime_coverage (source_file);
        """

    [<Fact>]
    let ``a v13 database is recreated as v14 through the schema-version path`` () =
        let path = tempDbPath ()

        try
            do
                use conn = new SqliteConnection($"Data Source=%s{path}")
                conn.Open()
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    v13Schema
                    + """
                    INSERT INTO symbols (full_name, kind, source_file, line_start, line_end, content_hash, is_extern, indexed_at)
                    VALUES ('TestPrune.__Signature__.Library.calculate', 'Function', 'Library.fsi', 4, 4, 'old', 0, '2026-09-01T00:00:00Z');
                    PRAGMA user_version = 13;
                    """

                cmd.ExecuteNonQuery() |> ignore
                SqliteConnection.ClearPool conn

            let db = Database.create path

            // The recreate path ran (the flag FsHotWatch uses to clear its check cache),
            // the file is stamped v14, and nothing of the v13 store survived to mix in.
            test <@ db.WasRecreated @>

            let userVersion, columns =
                use conn = new SqliteConnection($"Data Source=%s{path}")
                conn.Open()
                use version = conn.CreateCommand()
                version.CommandText <- "PRAGMA user_version;"
                let v = version.ExecuteScalar() :?> int64 |> int
                use cols = conn.CreateCommand()
                cols.CommandText <- "SELECT name FROM pragma_table_info('symbols') ORDER BY cid"
                use reader = cols.ExecuteReader()
                let names = ResizeArray<string>()

                while reader.Read() do
                    names.Add(reader.GetString 0)

                SqliteConnection.ClearPool conn
                v, List.ofSeq names

            test <@ userVersion = 14 && SchemaVersion = 14 @>
            test <@ not (columns |> List.contains "source_file") @>
            test <@ db.GetAllSymbolNames() |> Set.isEmpty @>

            // And the recreated store is fully usable at the new schema.
            db.RebuildProjects allResults
            test <@ (occurrence (toSymbolStore db) "Library.fsi" "Library.calculate").Length = 1 @>
            test <@ not (Database.create path).WasRecreated @>
        finally
            SqliteConnection.ClearAllPools()
            cleanupDb path
