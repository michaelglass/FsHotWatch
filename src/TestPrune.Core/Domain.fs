module TestPrune.Domain

type AnalysisError =
    | ParseFailed of file: string * errors: string list
    | CheckerAborted of file: string
    | DiffProviderFailed of reason: string
    | ProjectBuildFailed of project: string * exitCode: int
    | DatabaseError of operation: string * exn

module AnalysisError =
    let describe (error: AnalysisError) =
        match error with
        | ParseFailed(file, errors) ->
            let errs = errors |> String.concat "; "
            $"Parse failed for '%s{file}': %s{errs}"
        | CheckerAborted file -> $"Type checker aborted for '%s{file}'"
        | DiffProviderFailed reason -> $"Diff provider failed: %s{reason}"
        | ProjectBuildFailed(project, exitCode) -> $"Project build failed for '%s{project}' (exit code %d{exitCode})"
        | DatabaseError(operation, ex) -> $"Database error during '%s{operation}': %s{ex.Message}"

type ChangeKind =
    | Modified
    | Added
    | Removed

type SelectionReason =
    | SymbolChanged of symbolName: string * change: ChangeKind
    | MultipleChanges of symbolNames: string list
    | TransitiveDependency of chain: string list
    | FsprojChanged of file: string
    | NewFileNotIndexed of file: string
    | AnalysisFailedFallback of file: string
    | FileDependencyChanged of path: string * symbolName: string

module SelectionReason =
    let describe (reason: SelectionReason) =
        match reason with
        | SymbolChanged(symbolName, change) -> $"Symbol '%s{symbolName}' was %A{change}"
        | MultipleChanges symbolNames ->
            let names = symbolNames |> String.concat ", "
            $"Multiple symbols changed: %s{names}"
        | TransitiveDependency chain ->
            let path = chain |> String.concat " -> "
            $"Transitive dependency: %s{path}"
        | FsprojChanged file -> $"Project file changed: '%s{file}'"
        | NewFileNotIndexed file -> $"New file not yet indexed: '%s{file}'"
        | AnalysisFailedFallback file -> $"Analysis failed, selecting as fallback: '%s{file}'"
        | FileDependencyChanged(path, symbolName) ->
            $"File dependency '%s{path}' changed (declared by '%s{symbolName}')"

type AnalysisEvent =
    | FileAnalyzedEvent of file: string * symbolCount: int * depCount: int * testCount: int
    | FileCacheHitEvent of file: string * reason: string
    | FileSkippedEvent of file: string * reason: string
    | ProjectCacheHitEvent of project: string
    | ProjectIndexedEvent of project: string * fileCount: int
    | SymbolChangeDetectedEvent of file: string * symbolName: string * change: ChangeKind
    | TestSelectedEvent of testMethod: string * reason: SelectionReason
    | DiffParsedEvent of changedFiles: string list
    | IndexStartedEvent of projectCount: int
    | IndexCompletedEvent of totalSymbols: int * totalDeps: int * totalTests: int
    | ErrorEvent of AnalysisError
    | DeadCodeFoundEvent of symbolNames: string list

type Timestamped<'a> =
    { Timestamp: System.DateTimeOffset
      Event: 'a }
