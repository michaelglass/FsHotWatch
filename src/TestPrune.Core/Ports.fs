module TestPrune.Ports

open Microsoft.Data.Sqlite
open TestPrune.AstAnalyzer
open TestPrune.Database

/// Port for reading symbol data from storage.
type SymbolStore =
    { GetSymbolsInFile: string -> SymbolInfo list
      GetDependenciesFromFile: string -> Dependency list
      GetParentLinksInFile: string -> SymbolParentLink list
      GetTestMethodsInFile: string -> TestMethodInfo list
      GetFileKey: string -> string option
      GetProjectKey: string -> string option
      QueryAffectedTests: string list -> TestMethodInfo list
      GetRuntimeCoverageProjects: string list -> string list
      GetTestMethodsInProjects: string list -> TestMethodInfo list
      GetAllSymbols: unit -> SymbolInfo list
      GetAllSymbolNames: unit -> Set<string>
      GetReachableSymbols: string list -> Set<string>
      GetTestMethodSymbolNames: unit -> Set<string>
      GetIncomingEdgesBatch: string list -> Map<string, string list>
      GetAttributesForSymbol: string -> (string * string) list
      GetAllAttributes: unit -> Map<string, (string * string) list> }

/// Cache keys written atomically with an analysis result. FileKeys map source file
/// paths to content hashes; ProjectKeys map project file paths to their hashes.
type CacheKeys =
    { FileKeys: (string * string) list
      ProjectKeys: (string * string) list }

    static member Empty = { FileKeys = []; ProjectKeys = [] }

/// Port for writing symbol data to storage.
type SymbolSink =
    { RebuildProjects: AnalysisResult list -> CacheKeys -> unit }

/// Create a SymbolStore from a Database instance.
let toSymbolStore (db: Database) : SymbolStore =
    { GetSymbolsInFile = db.GetSymbolsInFile
      GetDependenciesFromFile = db.GetDependenciesFromFile
      GetParentLinksInFile = db.GetParentLinksInFile
      GetTestMethodsInFile = db.GetTestMethodsInFile
      GetFileKey = db.GetFileKey
      GetProjectKey = db.GetProjectKey
      QueryAffectedTests = db.QueryAffectedTests
      GetRuntimeCoverageProjects = db.GetRuntimeCoverageProjects
      GetTestMethodsInProjects = db.GetTestMethodsInProjects
      GetAllSymbols = db.GetAllSymbols
      GetAllSymbolNames = fun () -> db.GetAllSymbolNames()
      GetReachableSymbols = db.GetReachableSymbols
      GetTestMethodSymbolNames = db.GetTestMethodSymbolNames
      GetIncomingEdgesBatch = db.GetIncomingEdgesBatch
      GetAttributesForSymbol = db.GetAttributesForSymbol
      GetAllAttributes = db.GetAllAttributes }

/// Port for an extension that owns storage of its own inside TestPrune's cache database.
///
/// Most extensions need none of this: they derive their facts from the symbol graph
/// (`SymbolStore`) at analysis time, and core never has to know what they mean. This seam
/// exists for the other kind — an extension whose facts are seeded from OUTSIDE the AST
/// (TestPrune.Falco's HTTP routes live in a DU plus runtime wiring no F# symbol reveals),
/// so they must be written in one process and read back in another. Rather than teach core
/// what a route is, core hands out a connection to its cache DB and the extension owns its
/// tables end to end.
///
/// The contract, in both directions:
///
/// * Core owns the FILE. Opening it (`Database.create`) checks `PRAGMA user_version` and,
///   on a file OLDER than its `SchemaVersion`, DELETES and recreates it — dropping every
///   plugin table with it, because core cannot migrate a table it knows nothing about. A
///   file NEWER than its `SchemaVersion` it refuses to open at all; `pluginStoreAt` is
///   how a plugin-only process reaches its table regardless.
/// * A plugin therefore owns its tables but may never ASSUME they exist. Run idempotent
///   `CREATE TABLE IF NOT EXISTS` DDL before every read and write, and store only what can
///   be re-derived (Falco re-seeds its routes each run) — never the sole copy of anything.
/// * A plugin must not touch core's tables. It has the connection; nothing enforces this
///   but the boundary.
type PluginStore =
    {
        /// Open a fresh connection to the cache database. The caller disposes it.
        OpenConnection: unit -> SqliteConnection
    }

/// Create a PluginStore from a Database instance. Taking a live `Database` is what makes
/// the seam safe: the schema-version check (and any delete+recreate it triggers) has
/// already run by the time a plugin gets a connection, so a plugin table created through
/// this store cannot be silently dropped a moment later by core's own open path.
let toPluginStore (db: Database) : PluginStore = { OpenConnection = db.OpenConnection }

/// Create a PluginStore over the cache file at `dbPath` without opening it as a `Database`:
/// no schema-version check, no core DDL, no version stamp. For a process that only seeds
/// or reads its plugin table (TestPrune.Falco's route seeding) and never touches core's
/// tables, so core schema skew between that process and the one that indexes is not its
/// concern: it opens a file of any core schema version, including one newer than the
/// TestPrune.Core it links, which `Database.create` refuses. The plugin contract above is
/// unchanged, and it is what makes this safe: the table is created `IF NOT EXISTS` before
/// every use and holds nothing that cannot be re-seeded, because core still deletes the
/// file on its own next `SchemaVersion` bump.
let pluginStoreAt (dbPath: string) : PluginStore =
    { OpenConnection = fun () -> openPluginConnection dbPath }

let toSymbolSink (db: Database) : SymbolSink =
    { RebuildProjects =
        fun results keys -> db.RebuildProjects(results, fileKeys = keys.FileKeys, projectKeys = keys.ProjectKeys) }
