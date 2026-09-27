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
/// declare its own `CompositionRootAttribute`.
///
/// This module is where the rule lives, so that the SQLite walk in
/// `Database.QueryAffectedTests` and the in-memory walk in `InMemoryStore` cannot
/// disagree about it. The two engines share the marker `Names` and the per-seed
/// fail-safe (`emptiedProjects`) outright; only the traversal is written twice.
/// `InMemoryStore` states the rule literally, walking each seed on its own;
/// `Database` answers every seed from one loaded region (`SeedCoverage`). The two are
/// held together by `CompositionRootSelectionTests` and `SeedCoverageTests`, which
/// assert both stores return the same rows — the randomised soundness harness
/// exercises the in-memory walk only, so it is those assertions, not the harness,
/// that keep them honest.
module CompositionRoot =

    /// Both spellings FCS may store for the marker, with and without the
    /// conventional `Attribute` suffix. A list rather than a set because
    /// `Database` pairs it positionally with `@cr0`/`@cr1` SQL parameters.
    let internal Names = [ "CompositionRootAttribute"; "CompositionRoot" ]

    /// Does this stored attribute name mark a composition root?
    let internal isMarker (attributeName: string) = Names |> List.contains attributeName

    /// FAIL-SAFE: a barrier may NARROW a test project's selection, never empty it.
    ///
    /// Barriering is only sound while some OTHER attribution still reaches the tests
    /// covering the changed symbol — TestPrune.Falco's route→test edges, in the case
    /// this was built for. That attribution is not total, so a barriered walk can
    /// answer "no tests affected" for a change that really does need testing: a green
    /// gate that verified nothing, which is far worse than the over-selection being
    /// fixed.
    ///
    /// A global "is the answer empty?" guard does NOT cover it. A change can keep a
    /// handful of unit tests while every integration test vanishes, and the non-empty
    /// total masks it. So the unit of the guard is the test PROJECT: a project the
    /// unbarriered walk covers and the barriered one does not is a project whose
    /// markers cannot be trusted for this change, and its unbarriered rows come back
    /// wholesale.
    ///
    /// The guard is judged PER SEED. A batch selects the union of what each of its
    /// seeds selects on its own, fail-safe applied to each seed separately. Judged over
    /// the batch at once, a project kept non-empty by one seed's direct edge would hide
    /// another seed that reached it only through a composition root, and adding a seed
    /// could then drop tests the smaller batch selected. Per seed, selection is
    /// monotone: a batch selects at least what any part of it selects.
    ///
    /// For one seed, `barriered` is a subset of `unbarriered` (identical start set,
    /// strictly more restrictive expansion), so the projects present in `barriered` are
    /// exactly the ones the barrier did not empty, and this set is precisely the ones
    /// it did.
    ///
    /// NOT a completeness guarantee, and its granularity is the CONSUMER'S: a repo
    /// whose unit and integration tests share one test project gets "never select
    /// nothing" rather than a per-project bound. Even with separate projects, a route
    /// with one test that names its URL and another that only clicks through the UI
    /// keeps the project non-empty and still drops the second. This bounds the blast
    /// radius; closing it needs the route attribution to cover click-driven navigation.
    let internal emptiedProjects (barriered: Set<string>) (unbarriered: Set<string>) : Set<string> =
        Set.difference unbarriered barriered

    /// One seed's selection: its barriered rows, plus its unbarriered rows in every
    /// project its barrier emptied (see `emptiedProjects`). Appending cannot duplicate
    /// a row, because a restored row's project has no barriered row.
    let internal restoreEmptiedProjects
        (testProjectOf: 'test -> string)
        (barriered: 'test list)
        (unbarriered: 'test list)
        : 'test list =
        let projectsOf (rows: 'test list) =
            rows |> List.map testProjectOf |> Set.ofList

        let emptied = emptiedProjects (projectsOf barriered) (projectsOf unbarriered)

        barriered
        @ (unbarriered |> List.filter (fun t -> Set.contains (testProjectOf t) emptied))

type ChangeKind =
    | Modified
    | Added
    | Removed

/// Whether a configured test project has a usable complete runtime-coverage
/// baseline. Missing and stale are distinct diagnostics even though both must
/// conservatively widen selection.
type RuntimeCoverageAvailability =
    | Current
    | Missing
    | Stale of observedAt: System.DateTimeOffset

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
    | ProjectSelectedByRuntimeCoverageEvent of testProject: string * changedFile: string
    | DiffParsedEvent of changedFiles: string list
    | IndexStartedEvent of projectCount: int
    | IndexCompletedEvent of totalSymbols: int * totalDeps: int * totalTests: int
    | ErrorEvent of AnalysisError
    | DeadCodeFoundEvent of symbolNames: string list

type Timestamped<'a> =
    { Timestamp: System.DateTimeOffset
      Event: 'a }
