/// Failures that stay owed until a run executes them: quarantine into later selections, and when a run retires them.
module FsHotWatch.TestPrune.OutstandingFailures

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
open FsHotWatch.TestPrune.RuntimeCoverageSelection
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.ExternalInputs
open FsHotWatch.TestPrune.RunResults
open FsHotWatch.TestPrune.ReceiptTransitions
open FsHotWatch.TestPrune.SelectionAssembly

module internal OutstandingFailure =

    /// Prior reds are verification debt, not merely verdict decoration. Add every
    /// configured red scope to the next ordinary impact selection so a failing test
    /// cannot disappear just because the next edit reaches a different part of the
    /// graph. A failure without a class is project-scoped and therefore promotes the
    /// whole project to an unfiltered run.
    let quarantine
        (configured: Set<string>)
        (failures: OutstandingFailure list)
        (affectedByProject: Map<string, string list>)
        : Map<string, string list> =
        failures
        |> List.filter (fun failure -> Set.contains failure.Project configured)
        |> List.groupBy (fun failure -> failure.Project)
        |> List.fold
            (fun selected (project, projectFailures) ->
                if projectFailures |> List.exists (fun failure -> Option.isNone failure.Class) then
                    Map.add project [] selected
                else
                    let quarantinedClasses =
                        projectFailures |> List.choose (fun failure -> failure.Class)

                    match Map.tryFind project selected with
                    | Some [] -> selected
                    | Some affectedClasses ->
                        Map.add project (affectedClasses @ quarantinedClasses |> List.distinct) selected
                    | None -> Map.add project (quarantinedClasses |> List.distinct) selected)
            affectedByProject

    /// Identity for de-duplication: the same class failing the same way twice is one
    /// red, not two. (`Entry` is compared by message only — the detail is the full
    /// runner output, which differs by timing/ordering between otherwise identical
    /// failures.)
    let private identity (f: OutstandingFailure) =
        f.Project, f.Class, f.Method, f.File, f.Entry.Message

    /// Split `prior` into the reds that name a class the project does NOT have, and the
    /// rest. Such a red can never be retired by a pass, because no run will ever execute
    /// it; left alone it is carried forever, and nothing can re-run it.
    ///
    /// A red is ABSENT only on this run's own evidence, all three at once:
    ///   * the run SELECTED its class (`coverage` holds it, or the whole project);
    ///   * the project PASSED with a complete CTRF report (`classesRun` holds it — see
    ///     `classesRunOfPassedProjects`);
    ///   * that report has no row of the class at all, in any outcome.
    /// A skipped row is a row: the class exists, and its red waits for a pass. A
    /// project-level red names no class and stays the whole-project run's to clear.
    let partitionAbsent
        (coverage: RunCoverage)
        (classesRun: Map<string, Set<string>>)
        (prior: OutstandingFailure list)
        : OutstandingFailure list * OutstandingFailure list =
        prior
        |> List.partition (fun failure ->
            match failure.Class, Map.tryFind failure.Project classesRun with
            | Some cls, Some ran ->
                RunCoverage.covers failure.Project (Some cls) coverage
                && not (Set.contains cls ran)
            | _ -> false)

    /// The reds a run CARRIES: prior failures it did not cover, and so cannot speak
    /// for. A filtered class selection covers an exact prior method only when this
    /// run's complete CTRF receipt contains that method passing; a sibling green is
    /// not contradictory evidence. These deny an otherwise-passing run its verdict.
    ///
    /// `configured` prunes reds for projects the daemon no longer runs (a project
    /// removed from `tests.projects` could otherwise never be covered again, and its
    /// red would wedge the verdict forever — the unconfigured-project stuck-red, rebuilt). Empty
    /// ⇒ analysis-only, nothing to prune by.
    let carriedOver
        (configured: Set<string>)
        (coverage: RunCoverage)
        (passedTests: Map<string, Set<string * string>>)
        (prior: OutstandingFailure list)
        : OutstandingFailure list =
        let stillConfigured (f: OutstandingFailure) =
            Set.isEmpty configured || Set.contains f.Project configured

        let covered (failure: OutstandingFailure) =
            match failure.Class, failure.Method, Map.tryFind failure.Project coverage with
            | _, _, Some CoveredWholeProject -> true
            | Some cls, Some methodName, Some(CoveredClasses classes) ->
                Set.contains cls classes
                && (passedTests
                    |> Map.tryFind failure.Project
                    |> Option.exists (Set.contains (cls, methodName)))
            | _, None, _ -> RunCoverage.covers failure.Project failure.Class coverage
            | _ -> false

        prior |> List.filter (fun f -> stillConfigured f && not (covered f))

    /// The outstanding set after a run: what it carried, plus what it found. A red the
    /// run COVERED is not carried — if it failed again, `found` re-adds it from THIS
    /// run's evidence; if it passed, it is gone, which is the whole point (no permanent
    /// stuck-red). Defined in terms of `carriedOver` so the status verdict and the
    /// ledger can never disagree about what is still red.
    let carry
        (configured: Set<string>)
        (coverage: RunCoverage)
        (passedTests: Map<string, Set<string * string>>)
        (found: OutstandingFailure list)
        (prior: OutstandingFailure list)
        : OutstandingFailure list =
        carriedOver configured coverage passedTests prior @ found
        |> List.distinctBy identity

    /// Human-readable "what is still red that this run did not look at", for the
    /// status verdict. Names the projects, not every class — the ledger has the detail.
    let summarize (failures: OutstandingFailure list) : string =
        failures
        |> List.map (fun f -> f.Project)
        |> List.distinct
        |> List.sort
        |> String.concat ", "

    // -----------------------------------------------------------------------
    // The reds are DURABLE.
    //
    // They used to be session-scoped, on the argument that a restarted daemon has no
    // `LastResults` and so runs the full suite, which re-finds any red. That argument
    // holds only when the durable pending queue is EMPTY at restart. With symbols
    // queued — the ordinary case after a red run, because a failing project blocks the
    // commit of every symbol it covers — the restarted daemon's first run is
    // impact-FILTERED, `hasCachedResults` is then true, and a red from the previous
    // session that the filter does not reach is never selected again: the
    // never-selected-red shape, reproduced by a restart. Quarantine is memory; memory that
    // does not survive the process is not memory.
    //
    // Same rules as `PendingVerification`: a missing file is an honest empty; a file
    // that exists and cannot be read is `Unreadable`, and the caller treats that as
    // debt of unknown membership (the unreadable-ledger recovery: widen to the full suite,
    // which re-executes every test and rebuilds this list from evidence).
    //
    // `Entry.Detail` is deliberately NOT persisted. It is the captured runner output
    // — unbounded, and already on disk under `.fshw/test-runs/<runId>/` — and a
    // quarantined red is re-executed by the very next run, which re-derives it.
    // -----------------------------------------------------------------------

    [<RequireQualifiedAccess>]
    type LoadedFailures =
        | Loaded of OutstandingFailure list
        | Unreadable of reason: string

    let sidecarPath (repoRoot: string) : string =
        Path.Combine(FsHwPaths.root repoRoot, "test-prune", "outstanding-failures.json")

    let private toJson (f: OutstandingFailure) : Nodes.JsonObject =
        let obj = Nodes.JsonObject()
        obj.["project"] <- Nodes.JsonValue.Create(f.Project)

        obj.["class"] <-
            (match f.Class with
             | Some c -> Nodes.JsonValue.Create(c)
             | None -> null)

        obj.["method"] <-
            (match f.Method with
             | Some m -> Nodes.JsonValue.Create(m)
             | None -> null)

        obj.["file"] <- Nodes.JsonValue.Create(f.File)
        obj.["message"] <- Nodes.JsonValue.Create(f.Entry.Message)
        obj.["severity"] <- Nodes.JsonValue.Create(ErrorLedger.DiagnosticSeverity.toString f.Entry.Severity)
        obj.["line"] <- Nodes.JsonValue.Create(f.Entry.Line)
        obj.["column"] <- Nodes.JsonValue.Create(f.Entry.Column)
        obj

    let private ofJson (node: Nodes.JsonNode) : Result<OutstandingFailure, string> =
        if isNull node then
            Error "a `null` entry where a failure was expected"
        else
            try
                let obj = node.AsObject()

                let str (name: string) =
                    match obj.[name] with
                    | null -> None
                    | v -> Some(v.GetValue<string>())

                let int (name: string) =
                    match obj.[name] with
                    | null -> 0
                    | v -> v.GetValue<int>()

                match str "project", str "file", str "message", str "severity" with
                | Some project, Some file, Some message, Some severity ->
                    match ErrorLedger.DiagnosticSeverity.fromString severity with
                    | None -> Error $"unknown severity '%s{severity}'"
                    | Some severity ->
                        Ok
                            { Project = project
                              Class = str "class"
                              Method = str "method"
                              File = file
                              Entry =
                                { Message = message
                                  Severity = severity
                                  Line = int "line"
                                  Column = int "column"
                                  Code = None
                                  Detail = None } }
                | _ -> Error $"an entry missing `project`, `file`, `message` or `severity` (%s{node.ToJsonString()})"
            with ex ->
                Error $"%s{ex.GetType().Name}: %s{ex.Message}"

    let load (repoRoot: string) : LoadedFailures =
        let path = sidecarPath repoRoot

        if not (File.Exists path) then
            LoadedFailures.Loaded []
        else
            try
                let json = File.ReadAllText path

                if String.IsNullOrWhiteSpace json then
                    LoadedFailures.Unreadable
                        "the file is empty — `save` always writes at least `[]`, so this is a torn write"
                else
                    match Nodes.JsonNode.Parse(json) with
                    | null -> LoadedFailures.Unreadable "the file holds a bare JSON `null`, not an array of failures"
                    | root ->
                        let read = root.AsArray() |> Seq.map ofJson |> List.ofSeq

                        match
                            read
                            |> List.tryPick (function
                                | Error reason -> Some reason
                                | Ok _ -> None)
                        with
                        | Some reason -> LoadedFailures.Unreadable reason
                        | None ->
                            read
                            |> List.choose (function
                                | Ok f -> Some f
                                | Error _ -> None)
                            |> LoadedFailures.Loaded
            with ex ->
                LoadedFailures.Unreadable $"%s{ex.GetType().Name}: %s{ex.Message}"

    let save (repoRoot: string) (failures: OutstandingFailure list) : unit =
        let arr = Nodes.JsonArray()

        for f in failures do
            arr.Add(toJson f)

        FsHwPaths.atomicWriteAllText (sidecarPath repoRoot) (arr.ToJsonString())
