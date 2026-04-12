module TestPrune.Ports

open TestPrune.AstAnalyzer
open TestPrune.Database

/// Port for reading symbol data from storage.
type SymbolStore =
    { GetSymbolsInFile: string -> SymbolInfo list
      GetDependenciesFromFile: string -> Dependency list
      GetTestMethodsInFile: string -> TestMethodInfo list
      GetFileKey: string -> string option
      GetProjectKey: string -> string option
      QueryAffectedTests: string list -> TestMethodInfo list
      GetAllSymbols: unit -> SymbolInfo list
      GetAllSymbolNames: unit -> Set<string>
      GetReachableSymbols: string list -> Set<string>
      GetTestMethodSymbolNames: unit -> Set<string>
      GetIncomingEdgesBatch: string list -> Map<string, string list>
      GetAttributesForSymbol: string -> (string * string) list }

/// Port for writing symbol data to storage.
type SymbolSink =
    { RebuildProjects: AnalysisResult list -> (string * string) list -> (string * string) list -> unit }

/// Create a SymbolStore from a Database instance.
let toSymbolStore (db: Database) : SymbolStore =
    { GetSymbolsInFile = db.GetSymbolsInFile
      GetDependenciesFromFile = db.GetDependenciesFromFile
      GetTestMethodsInFile = db.GetTestMethodsInFile
      GetFileKey = db.GetFileKey
      GetProjectKey = db.GetProjectKey
      QueryAffectedTests = db.QueryAffectedTests
      GetAllSymbols = db.GetAllSymbols
      GetAllSymbolNames = fun () -> db.GetAllSymbolNames()
      GetReachableSymbols = db.GetReachableSymbols
      GetTestMethodSymbolNames = db.GetTestMethodSymbolNames
      GetIncomingEdgesBatch = db.GetIncomingEdgesBatch
      GetAttributesForSymbol = db.GetAttributesForSymbol }

/// Port for reading route handler data from storage.
type RouteStore =
    { GetAllHandlerSourceFiles: unit -> Set<string>
      GetUrlPatternsForSourceFile: string -> string list }

/// Create a RouteStore from a Database instance.
let toRouteStore (db: Database) : RouteStore =
    { GetAllHandlerSourceFiles = db.GetAllHandlerSourceFiles
      GetUrlPatternsForSourceFile = db.GetUrlPatternsForSourceFile }

let toSymbolSink (db: Database) : SymbolSink =
    { RebuildProjects =
        fun results fileKeys projectKeys -> db.RebuildProjects(results, fileKeys = fileKeys, projectKeys = projectKeys) }
