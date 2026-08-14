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

/// The `[<TestPrune.CompositionRoot>]` marker, as the reverse-walk sees it.
///
/// A composition root names the whole application (a routing table, a DI
/// registration block) instead of using any part of it. Its edges are real, which
/// is why the walk traverses them and why one edited handler currently reaches
/// every test whose fixture boots the app. The marker says: do not propagate
/// relevance THROUGH this symbol — but a change TO it still propagates, because
/// "the app is wired differently now" is what host-booting tests check.
///
/// Matched by attribute NAME, mirroring how `ImpactAnalysis` resolves
/// `DependsOnFile`: the stored `DisplayName` sheds its namespace, and a consumer
/// that would rather not reference TestPrune.Attributes from production code can
/// declare its own `CompositionRootAttribute`. `Names` is the pair to match
/// against; it is the single source of truth shared by the SQLite walk in
/// `Database.QueryAffectedTests` and the in-memory walk in `InMemoryStore`, which
/// must agree or the soundness harness is checking a different selector than the
/// one that ships.
module CompositionRoot =

    /// Both spellings FCS may store for the marker, with and without the
    /// conventional `Attribute` suffix.
    let Names = [ "CompositionRootAttribute"; "CompositionRoot" ]

    /// Does this stored attribute name mark a composition root?
    let isMarker (attributeName: string) =
        Names |> List.exists (fun n -> n = attributeName)

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
