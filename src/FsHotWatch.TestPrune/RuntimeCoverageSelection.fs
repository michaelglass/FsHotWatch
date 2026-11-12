/// Selecting by recorded runtime coverage, the obligations that coverage leaves owed, and when a selection that reached no test is widened.
module FsHotWatch.TestPrune.RuntimeCoverageSelection

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Xml.Linq
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsSuppression
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Domain
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.PluginTypes

/// Why runtime evidence could not safely exclude a configured project.
type RuntimeCoverageWideningReason =
    | MissingBaseline
    | StaleBaseline of observedAt: DateTimeOffset

type RuntimeCoverageSelection =
    { ProjectsByFile: Map<string, Set<string>>
      Widenings: (string * RuntimeCoverageWideningReason) list }

[<CLIMutable>]
type RuntimeCoverageObligationProjectDto = { Project: string; Generation: int64 }

[<CLIMutable>]
type RuntimeCoverageObligationDto =
    { SourceFile: string
      Projects: RuntimeCoverageObligationProjectDto array }

type RuntimeCoverageObligations = Map<string, Map<string, int64>>

let internal runtimeCoverageObligationsPath repoRoot =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "runtime-coverage-obligations.json")

let internal runtimeCoverageRecoveryPath repoRoot =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "runtime-coverage-obligations.recovery")

let internal loadRuntimeCoverageObligations repoRoot : Result<RuntimeCoverageObligations, string> =
    let path = runtimeCoverageObligationsPath repoRoot

    if File.Exists(runtimeCoverageRecoveryPath repoRoot) then
        Error "a prior runtime obligation write did not complete; the debt is unknown"
    elif not (File.Exists path) then
        Ok Map.empty
    else
        try
            let rows =
                JsonSerializer.Deserialize<RuntimeCoverageObligationDto array>(File.ReadAllText path)

            if isNull rows then
                Error "the runtime coverage obligation ledger is JSON null"
            else
                rows
                |> Array.map (fun row ->
                    row.SourceFile,
                    (row.Projects
                     |> Array.map (fun project -> project.Project, project.Generation)
                     |> Map.ofArray))
                |> Map.ofArray
                |> Ok
        with ex ->
            Error $"%s{ex.GetType().Name}: %s{ex.Message}"

let internal saveRuntimeCoverageObligations repoRoot (obligations: RuntimeCoverageObligations) =
    let rows =
        obligations
        |> Map.toArray
        |> Array.map (fun (file, projects) ->
            { SourceFile = file
              Projects =
                projects
                |> Map.toArray
                |> Array.sortBy fst
                |> Array.map (fun (project, generation) ->
                    { Project = project
                      Generation = generation }) })

    FsHwPaths.atomicWriteAllText (runtimeCoverageObligationsPath repoRoot) (JsonSerializer.Serialize rows)

let internal persistRuntimeCoverageObligationsWith
    (writeRecoveryMarker: unit -> unit)
    (save: unit -> unit)
    (clearRecoveryMarker: unit -> unit)
    : Result<unit, exn> =
    try
        writeRecoveryMarker ()
        save ()
        clearRecoveryMarker ()
        Ok()
    with ex ->
        Error ex

let internal persistRuntimeCoverageTransitionWith
    (writeRecoveryMarker: unit -> unit)
    (save: RuntimeCoverageObligations -> unit)
    (clearRecoveryMarker: unit -> unit)
    (current: RuntimeCoverageObligations)
    (transition: RuntimeCoverageObligations -> RuntimeCoverageObligations)
    : Result<RuntimeCoverageObligations, RuntimeCoverageObligations * exn> =
    try
        // The recovery signal must be durable before the transition is even
        // computed, let alone accepted in memory. If phase one fails, restart
        // may only know the prior ledger, so the prior state is the only state
        // this daemon is allowed to retain.
        writeRecoveryMarker ()
        let next = transition current
        save next
        clearRecoveryMarker ()
        Ok next
    with ex ->
        Error(current, ex)

/// An obligation that names NO project is not a debt, and admitting one
/// is the silent full-suite escalation this guard closes.
///
/// `selectByRuntimeCoverage` maps EVERY changed file — `Set.union current
/// widenedProjects` is empty whenever nothing attributes runtime coverage to that file
/// and every configured project's baseline is current, which is the ordinary case for
/// an edit to a file no runtime tracer ever reached. The caller merges the selection
/// whenever the MAP is non-empty, so those files used to enter the ledger as
/// `file -> map []`. The result satisfies neither of the two questions asked of it:
///
///   * `nothingOwed` asks `Map.isEmpty`, and reads a non-empty map — so for the rest of
///     the session the zero-affected green skip and the stale-rerun guard are both
///     refused, and every cycle that selects nothing runs tests anyway;
///   * `runtimeForceProjects` asks for the projects the ledger NAMES, and gets none — so
///     the run it forces has an EMPTY selection, and an empty selection means every
///     configured project, in full.
///
/// A debt nothing can select and nothing can discharge, costing a whole suite. The
/// observation that a file changed is not itself an obligation: only a file with at
/// least one obligated project may enter the ledger, and only the run that covered it
/// may take it out again.
let internal mergeRuntimeCoverageObligations
    (existing: RuntimeCoverageObligations)
    (incoming: Map<string, Set<string>>)
    =
    (existing, incoming)
    ||> Map.fold (fun acc file projects ->
        if Set.isEmpty projects then
            // Nothing to owe for this file. Leave any PRIOR obligation for it exactly
            // as it stands: a real debt is discharged by the run that covers it
            // (`retireRuntimeCoverageObligations`), never by a later cycle observing
            // the same file and finding nothing to add.
            acc
        else
            let prior = Map.tryFind file acc |> Option.defaultValue Map.empty

            let next =
                (prior, projects)
                ||> Set.fold (fun obligations project ->
                    let generation = Map.tryFind project obligations |> Option.defaultValue 0L
                    Map.add project (generation + 1L) obligations)

            Map.add file next acc)

let internal retireRuntimeCoverageObligations
    (current: RuntimeCoverageObligations)
    (launched: RuntimeCoverageObligations)
    (projectPassed: string -> bool)
    =
    (current, launched)
    ||> Map.fold (fun obligations file launchedProjects ->
        match Map.tryFind file obligations with
        | None -> obligations
        | Some currentProjects ->
            let remaining =
                (currentProjects, launchedProjects)
                ||> Map.fold (fun projects project launchedGeneration ->
                    match Map.tryFind project projects with
                    | Some currentGeneration when currentGeneration = launchedGeneration && projectPassed project ->
                        Map.remove project projects
                    | _ -> projects)

            if Map.isEmpty remaining then
                Map.remove file obligations
            else
                Map.add file remaining obligations)

let internal pruneRuntimeCoverageObligations (allowedProjects: Set<string>) (obligations: RuntimeCoverageObligations) =
    obligations
    |> Map.map (fun _ projects -> projects |> Map.filter (fun project _ -> Set.contains project allowedProjects))
    |> Map.filter (fun _ projects -> not (Map.isEmpty projects))

/// A complete runtime baseline remains useful across ordinary incremental runs,
/// but not indefinitely. Missing or older evidence widens to the whole project.
let internal RuntimeCoverageMaxAge = TimeSpan.FromDays 7.0

let internal selectByRuntimeCoverage
    (db: Database)
    (expectedProjects: string list)
    (changedFiles: string list)
    (staleBefore: DateTimeOffset)
    : RuntimeCoverageSelection =
    let widenings =
        db.GetRuntimeCoverageAvailability(expectedProjects, staleBefore)
        |> List.choose (fun (project, availability) ->
            match availability with
            | RuntimeCoverageAvailability.Current -> None
            | RuntimeCoverageAvailability.Missing -> Some(project, MissingBaseline)
            | RuntimeCoverageAvailability.Stale observedAt -> Some(project, StaleBaseline observedAt))

    let expectedProjectSet = Set.ofList expectedProjects
    let widenedProjects = widenings |> List.map fst |> Set.ofList

    let currentByFile =
        db.GetRuntimeCoverageAttributions(changedFiles)
        |> List.filter (fun (_, project) -> Set.contains project expectedProjectSet)
        |> List.groupBy fst
        |> List.map (fun (file, edges) -> file, edges |> List.map snd |> Set.ofList)
        |> Map.ofList

    let projectsByFile =
        changedFiles
        |> List.distinct
        |> List.map (fun file ->
            let current = Map.tryFind file currentByFile |> Option.defaultValue Set.empty
            file, Set.union current widenedProjects)
        |> Map.ofList

    { ProjectsByFile = projectsByFile
      Widenings = widenings }

/// What runtime coverage selected, split by each project's `runtimeSelection`.
type internal RuntimeSelectionSplit =
    { Decided: Map<string, Set<string>>
      Shadowed: Map<string, Set<string>> }

/// Split what runtime coverage selected by each project's `runtimeSelection`. Each side
/// names only the files with at least one of its projects: a file naming no project is
/// not an obligation (`mergeRuntimeCoverageObligations`).
let internal splitRuntimeSelection (isShadow: string -> bool) (projectsByFile: Map<string, Set<string>>) =
    let side keep =
        projectsByFile
        |> Map.map (fun _ projects -> Set.filter keep projects)
        |> Map.filter (fun _ projects -> not (Set.isEmpty projects))

    { Decided = side (isShadow >> not)
      Shadowed = side isShadow }

/// The one line a launch logs when a project's `runtimeSelection` is shadow: the static
/// selection the run took, what runtime coverage would have added, and the counts, as
/// JSON after a fixed prefix. A later full run over the same `tree` joins to it: a test
/// that fails there in a project `beyondStatic` names, which static selection did not
/// run, is one catch only runtime selection would have made.
module internal RuntimeShadowLine =
    open System.Text.Json.Nodes

    [<Literal>]
    let Prefix = "runtime-selection shadow: "

    /// Relaxed escaping: a nested class's `+` stays a `+`, so the line greps as written.
    let private options =
        JsonSerializerOptions(Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private str (value: string) : JsonNode = JsonValue.Create value
    let private num (value: int) : JsonNode = JsonValue.Create value

    let private sorted (values: string seq) : JsonNode =
        JsonArray(values |> Seq.sort |> Seq.map str |> Seq.toArray)

    let private objectOf (properties: (string * JsonNode) seq) : JsonNode =
        JsonObject(properties |> Seq.map System.Collections.Generic.KeyValuePair.Create)

    let private scopeOf =
        function
        | ProjectInFull -> "whole", Set.empty
        | ProjectClasses classes -> "classes", classes
        | ProjectExcept classes -> "all-but", classes

    let render (tree: string option) (selection: Map<string, ProjectSelection>) (wouldAdd: RuntimeCoverageObligations) =
        let filesByProject =
            wouldAdd
            |> Map.toSeq
            |> Seq.collect (fun (file, projects) -> projects |> Map.keys |> Seq.map (fun p -> p, file))
            |> Seq.groupBy fst
            |> Seq.map (fun (project, pairs) -> project, pairs |> Seq.map snd |> Set.ofSeq)
            |> Map.ofSeq

        let named =
            selection
            |> Map.toSeq
            |> Seq.sumBy (fun (_, s) ->
                match s with
                | ProjectClasses classes -> Set.count classes
                | _ -> 0)

        let staticNode =
            objectOf
                [ "projects", num (Map.count selection)
                  "wholeProjects", num (selection |> Map.filter (fun _ s -> s = ProjectInFull) |> Map.count)
                  "classes", num named
                  "byProject",
                  objectOf (
                      selection
                      |> Map.toSeq
                      |> Seq.map (fun (project, s) ->
                          let scope, classes = scopeOf s
                          project, objectOf [ "scope", str scope; "classes", sorted classes ])
                  ) ]

        let wouldAddNode =
            objectOf
                [ "granularity", str "project"
                  "projects", num (Map.count filesByProject)
                  "files", num (wouldAdd |> Map.filter (fun _ ps -> not (Map.isEmpty ps)) |> Map.count)
                  "beyondStatic",
                  sorted (
                      filesByProject
                      |> Map.keys
                      |> Seq.filter (fun project -> Map.tryFind project selection <> Some ProjectInFull)
                  )
                  "byProject",
                  objectOf (
                      filesByProject
                      |> Map.toSeq
                      |> Seq.map (fun (project, files) -> project, sorted files)
                  ) ]

        let line =
            objectOf
                [ "v", num 1
                  "tree", (tree |> Option.map str |> Option.toObj)
                  "static", staticNode
                  "wouldAdd", wouldAddNode ]

        Prefix + line.ToJsonString options

let internal reportRuntimeCoverageWidenings
    (warn: string -> unit)
    (isShadow: string -> bool)
    (selection: RuntimeCoverageSelection)
    =
    for project, reason in selection.Widenings do
        let widening =
            if isShadow project then
                "shadow: would widen to every test in that configured project; not selected"
            else
                "widening to every test in that configured project"

        match reason with
        | MissingBaseline -> warn $"runtime coverage: project '%s{project}' has no complete baseline; %s{widening}"
        | StaleBaseline observedAt ->
            warn
                $"runtime coverage: project '%s{project}' complete baseline from %O{observedAt} is older than %O{RuntimeCoverageMaxAge}; %s{widening}"

/// The causes of unknown verification debt (`VerificationDebt.UnknownDebt`), in one clause.
let internal describeUnknownDebt (causes: string list) = String.concat "; " causes

/// Why a cycle that found ZERO symbol-affected classes is running tests
/// anyway — one arm per debt that can refuse the zero-affected green skip.
///
/// The single sentence this replaces ("No affected classes (cold start / pending queue)
/// — running all tests") named two causes for five, was printed for runs that were
/// neither, and claimed "all tests" for runs that were a handful of force-run projects.
/// A widening nobody can attribute is a widening nobody measures: 404 of 1,221 launches
/// in one measured log sample took that line, and the line is the only record any of
/// them left.
[<RequireQualifiedAccess>]
type internal ZeroAffectedWidening =
    /// No run has completed in this session, so there is nothing for this tree to be
    /// test-equivalent TO. Expected exactly once per session — the baseline run.
    | NoSessionBaseline
    /// Symbols are still awaiting a green covering run.
    | QueuedSymbols of count: int
    /// Runtime-coverage obligations are outstanding over `files` file(s), naming
    /// `projects` project(s) between them.
    | RuntimeCoverageDebt of files: int * projects: int
    /// These projects' runtime coverage did not ingest, so each owes a whole-project run.
    | CoverageNotIngested of projects: Set<string>
    /// What is owed is unknown — for each of `causes` (a ledger that could not be read, a
    /// debt record that did not finish being written) — and only a full suite can prove it.
    | UnknownDebt of causes: string list
    /// A prior red is outstanding and must be re-executed before anything goes green.
    | OutstandingFailures of count: int
    /// No full-suite baseline vouches for what a filtered run would
    /// skip — none recorded, or it never executed a project configured since.
    | NoFullSuiteBaseline of reason: string
    /// These projects have no whole-project run under the current project model, so the
    /// evidence a run earns refuses them unless it runs them in full.
    | NoCurrentModelEvidence of projects: Set<string>
    /// Nothing is owed, but no test evidence is bound to the tree as it is now: it changed
    /// since the last run that earned any, in a way the symbol diff does not see (a
    /// comment, whitespace, a non-source file). Skipping would verify nothing on it.
    | UnverifiedTree

module internal ZeroAffectedWidening =
    let describe (cause: ZeroAffectedWidening) =
        match cause with
        | ZeroAffectedWidening.NoSessionBaseline ->
            "no run has completed in this session yet (no baseline to be equivalent to)"
        | ZeroAffectedWidening.NoFullSuiteBaseline reason -> reason
        | ZeroAffectedWidening.NoCurrentModelEvidence projects ->
            let names = projects |> Set.toList |> String.concat ", "
            $"no whole-project run under the current project model for %s{names}"
        | ZeroAffectedWidening.QueuedSymbols count -> $"%d{count} symbol(s) still awaiting a green covering run"
        | ZeroAffectedWidening.RuntimeCoverageDebt(files, projects) ->
            $"runtime-coverage debt over %d{files} file(s) naming %d{projects} project(s)"
        | ZeroAffectedWidening.CoverageNotIngested projects ->
            let names = projects |> Set.toList |> String.concat ", "
            $"runtime coverage did not ingest for %s{names}"
        | ZeroAffectedWidening.UnknownDebt causes -> $"what is owed is UNKNOWN (%s{describeUnknownDebt causes})"
        | ZeroAffectedWidening.OutstandingFailures count -> $"%d{count} outstanding test failure(s) from a prior run"
        | ZeroAffectedWidening.UnverifiedTree ->
            "no test evidence is bound to the tree as it is now — it changed since the last run that earned any, \
             in a way no symbol diff sees (a comment, whitespace, a non-source file)"

    let describeMany (causes: ZeroAffectedWidening list) =
        causes |> List.map describe |> String.concat "; "

/// Every reason this run is not taking the zero-affected skip, named and counted.
///
/// Returns EVERY applicable cause rather than the first: two debts outstanding at once
/// is the case where a reader picks the wrong one and goes looking for a bug in the
/// selector. An EMPTY list is the finding that matters most — nothing is owed and a
/// baseline exists, so the skip should have fired and did not. The caller says that out
/// loud instead of running a whole suite quietly, which is how the phantom
/// obligation survived 12 full-suite reruns without leaving a single attributable line.
let internal zeroAffectedWidening
    (hasSessionBaseline: bool)
    (unknownDebt: string list)
    (queuedSymbols: int)
    (runtimeObligations: RuntimeCoverageObligations)
    (coverageNotIngested: Set<string>)
    (outstandingFailures: int)
    (fullSuiteBaselineInvalid: string option)
    (evidenceGap: Set<string>)
    (treeUnverified: bool)
    : ZeroAffectedWidening list =
    [ if not hasSessionBaseline then
          ZeroAffectedWidening.NoSessionBaseline
      if not (List.isEmpty unknownDebt) then
          ZeroAffectedWidening.UnknownDebt unknownDebt
      // Unknown debt already invalidates the baseline; naming it twice would report two
      // debts for one cause.
      match fullSuiteBaselineInvalid with
      | Some reason when List.isEmpty unknownDebt -> ZeroAffectedWidening.NoFullSuiteBaseline reason
      | _ -> ()
      if queuedSymbols > 0 then
          ZeroAffectedWidening.QueuedSymbols queuedSymbols
      // Counted through the projects it NAMES, never through `Map.isEmpty`: a file
      // entry naming no project selects nothing, so reporting it as debt would restate
      // the very confusion this function exists to end.
      let namedProjects =
          runtimeObligations |> Map.values |> Seq.collect Map.keys |> Set.ofSeq

      if not (Set.isEmpty namedProjects) then
          let files =
              runtimeObligations |> Map.filter (fun _ ps -> not (Map.isEmpty ps)) |> Map.count

          ZeroAffectedWidening.RuntimeCoverageDebt(files, Set.count namedProjects)

      if not (Set.isEmpty coverageNotIngested) then
          ZeroAffectedWidening.CoverageNotIngested coverageNotIngested

      if outstandingFailures > 0 then
          ZeroAffectedWidening.OutstandingFailures outstandingFailures

      if not (Set.isEmpty evidenceGap) then
          ZeroAffectedWidening.NoCurrentModelEvidence evidenceGap
      // Only once a session baseline exists: before it, the baseline run is owed anyway
      // and naming this too would report two causes for one run.
      if treeUnverified && hasSessionBaseline then
          ZeroAffectedWidening.UnverifiedTree ]
