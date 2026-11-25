/// AUTOMATION-1900 step 3: SHADOW mode for the additive project-model narrowing. Where a launch
/// refuses "no whole-project run under the current project model" (`ImpactLaunch.widenLaunch`'s
/// evidence gap), this computes what `AdditiveModelChange.judge` WOULD decide and logs it as one
/// `additive-model:` line. It changes nothing: the refusal and the whole-project runs stay
/// exactly as they are, and the land shadow reads the line from the daemon log.
///
/// The baseline is the retained box tree a box restart would diff from (`BoxBaseline.choose`,
/// the tree whose manifest differs least from the working files), read whether or not
/// `tests.restartFromBoxBaseline` is on:
///   * BEFORE: T's compile model, filed with its `test-baseline` record (`CompileModel`);
///   * AFTER: each project's shape under the current model (`ObservedShapes`);
///   * the DIFF: T's manifest against the working tree;
///   * uses NOW: the impact index's core edges for the file;
///   * uses AT T: the analysis record T pinned for the file;
///   * the BLIND SPOTS (`BlindSpots`, log fields only): dropped edges in the relied-on files'
///     analyses at T, and whether one uses a member any analysis at T defines more than once.
module FsHotWatch.TestPrune.AdditiveShadow

open FsHotWatch
open FsHotWatch.TestPrune.AdditiveModelChange
open FsHotWatch.TestPrune.InputTrees

/// Why the shadow could not judge.
[<RequireQualifiedAccess>]
type NotJudged =
    | NoStore
    | NoTree
    | NoRetainedBaseline
    /// T's record was filed before compile models were recorded, or its model did not read.
    | NoCompileModelAtT of tree: string
    | Failed of reason: string

/// A judgement against T: what `judge` decides, and the blind spots of the files it relied on
/// (log fields only).
type Judgement =
    { Tree: string
      Verdict: Verdict
      BlindSpots: BlindSpots }

/// What the shadow concluded for one launch.
[<RequireQualifiedAccess>]
type Outcome =
    | Judged of Judgement
    | NotJudged of NotJudged

/// What the shadow reads.
type Env =
    {
        Store: OutcomeRecords.IOutcomeStore option
        Root: string
        /// The working tree now.
        Current: ReceiptTree option
        ShapesNow: Map<string, ProjectShape>
        /// A project's transitive dependents under the current model, by name.
        Dependents: string -> Set<string>
        /// The impact index's analysis identity per repo-relative path (`file_keys`).
        FileKeys: Map<string, string>
        /// The impact index's uses of a file now.
        UsesNow: string -> Set<SymbolUse>
    }

/// `project`'s transitive dependents under `graph`, by name: none for a project it does not name.
let dependentsIn (graph: FsHotWatch.PluginFramework.ProjectGraphAccessor) (project: string) : Set<string> =
    match Map.tryFind project (FsHotWatch.TestPrune.ReceiptTransitions.ObservedModel.projectFiles graph) with
    | Some fsproj ->
        graph.GetTransitiveDependentProjects fsproj
        |> List.map System.IO.Path.GetFileNameWithoutExtension
        |> Set.ofList
    | None -> Set.empty

/// The uses among an index's edges for a file that the compiler resolved: the core analyser's.
/// An analysis record holds only those, and an extension's edges are no use the compiler made.
let coreUses (dependencies: TestPrune.AstAnalyzer.Dependency list) : Set<SymbolUse> =
    dependencies
    |> List.filter (fun d -> d.Source = TestPrune.AstAnalyzer.CoreSource)
    |> SymbolUses.ofDependencies

/// The paths whose content differs between T's manifest and the working tree, either way.
let private diffOf (manifest: (string * string) list) (current: (string * string) list) : Set<string> =
    let atT = Map.ofList manifest
    let now = Map.ofList current

    Seq.append
        (manifest |> Seq.filter (fun (path, hash) -> Map.tryFind path now <> Some hash))
        (current |> Seq.filter (fun (path, _) -> not (atT.ContainsKey path)))
    |> Seq.map fst
    |> Set.ofSeq

/// The analysis T pinned for `path`, or `None` when T pinned none or its record no longer reads.
let private analysisAtT
    (env: Env)
    (store: OutcomeRecords.IOutcomeStore)
    (record: TestBaseline.CheckoutRecord)
    (path: string)
    : TestPrune.AstAnalyzer.AnalysisResult option =
    match Map.tryFind path record.Analyses with
    | Some(TestBaseline.RowAnalysis.Pinned(identity, project, pinned)) ->
        let key = AnalysisRecords.keyOf identity path project

        if key.Value.Text <> pinned then
            None
        else
            AnalysisRecords.lookup store env.Root key
    | Some(TestBaseline.RowAnalysis.Unpinned _)
    | None -> None

/// What `judge` decides for the model change between T and now, and the blind spots of the
/// files it relied on.
let observe (env: Env) : Outcome =
    match env.Store, env.Current with
    | None, _ -> Outcome.NotJudged NotJudged.NoStore
    | Some _, None -> Outcome.NotJudged NotJudged.NoTree
    | Some store, Some current ->
        match BoxBaseline.choose (BoxBaseline.retained store) current with
        | None -> Outcome.NotJudged NotJudged.NoRetainedBaseline
        | Some(record, _) when record.CompileModel.IsEmpty -> Outcome.NotJudged(NotJudged.NoCompileModelAtT record.Tree)
        | Some(record, _) ->
            let diff = diffOf record.Entries current.Entries

            // Every compiled file's analysis at T: the uses the comparison reads, and the
            // definitions an overload is detected from, in any project of T.
            let analysesAtT =
                record.CompileModel
                |> Map.values
                |> Seq.collect _.CompileItems
                |> Seq.distinct
                |> Seq.choose (fun path -> analysisAtT env store record path |> Option.map (fun a -> path, a))
                |> Map.ofSeq

            let compared =
                record.CompileModel
                |> Map.values
                |> Seq.collect _.CompileItems
                |> Seq.filter (fun path -> not (diff.Contains path))
                |> Set.ofSeq

            let now =
                compared
                |> Seq.filter env.FileKeys.ContainsKey
                |> Seq.map (fun path -> path, env.UsesNow path)
                |> Map.ofSeq

            let atT =
                analysesAtT
                |> Map.filter (fun path _ -> compared.Contains path)
                |> Map.map (fun _ analysis -> SymbolUses.ofAnalysis analysis)

            let projects =
                Seq.append (Map.keys record.CompileModel) (Map.keys env.ShapesNow)
                |> Seq.distinct

            let change =
                { Before = record.CompileModel
                  After = env.ShapesNow
                  Dependents = projects |> Seq.map (fun p -> p, env.Dependents p) |> Map.ofSeq
                  UsesAtT = atT
                  UsesNow = now
                  Diff = diff }

            Outcome.Judged
                { Tree = record.Tree
                  Verdict = judge change
                  BlindSpots = BlindSpots.ofAnalyses (BlindSpots.reliedOn change) analysesAtT }

/// The outcome's one log line: `AdditiveModelChange.describe`'s for a verdict, with the tree it
/// was judged against and the blind-spot fields (`BlindSpots.describe`), or
/// `additive-model: not-judged reason=<token>`.
let describe (outcome: Outcome) : string =
    match outcome with
    | Outcome.Judged judged ->
        $"%s{AdditiveModelChange.describe judged.Verdict} tree=%s{judged.Tree} %s{BlindSpots.describe judged.BlindSpots}"
    | Outcome.NotJudged why ->
        let reason =
            match why with
            | NotJudged.NoStore -> "no-store"
            | NotJudged.NoTree -> "no-tree"
            | NotJudged.NoRetainedBaseline -> "no-retained-baseline"
            | NotJudged.NoCompileModelAtT tree -> $"no-compile-model-at-t tree=%s{tree}"
            | NotJudged.Failed message -> $"error — %s{message}"

        $"additive-model: not-judged reason=%s{reason}"

/// The line `observer` concludes over `env`, never throwing: a shadow that fails is a line
/// saying so, not a failed launch.
let lineWith (observer: Env -> Outcome) (env: unit -> Env) : string =
    let outcome =
        try
            observer (env ())
        with ex ->
            Outcome.NotJudged(NotJudged.Failed $"%s{ex.GetType().Name}: %s{ex.Message}")

    describe outcome
