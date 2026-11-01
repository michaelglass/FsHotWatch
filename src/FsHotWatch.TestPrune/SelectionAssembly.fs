/// Assembling a run's selection: classes that always run, check-skip at launch, the sources that selected each project, and what a run would have run.
module FsHotWatch.TestPrune.SelectionAssembly

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

/// `alwaysClasses`: the classes a project runs in every impact-filtered launch.
///
/// Some tests read the source tree as DATA: a guard that greps every source file for a
/// banned call, a census of test attributes. Nothing they police is a symbol they call,
/// so no edge leads to them, and impact selection never picks them however the tree
/// they read changes. A project declares them, and every filtered launch carries them.
module internal AlwaysClasses =

    /// Each configured project's declared classes; projects with none are absent.
    let only (configs: TestConfig list) : Map<string, string list> =
        configs
        |> List.choose (fun c ->
            if List.isEmpty c.AlwaysClasses then
                None
            else
                Some(c.Project, c.AlwaysClasses))
        |> Map.ofList

    /// `selected` with every project's always-classes added. An EMPTY selection already
    /// means "every project in full", and a project present with `[]` already runs in
    /// full, so neither is touched: widening must never narrow a run.
    let widen (configs: TestConfig list) (selected: Map<string, string list>) : Map<string, string list> =
        if Map.isEmpty selected then
            selected
        else
            only configs
            |> Map.fold
                (fun acc project always ->
                    match Map.tryFind project acc with
                    | Some [] -> acc
                    | Some classes -> Map.add project (classes @ always |> List.distinct) acc
                    | None -> Map.add project always acc)
                selected

/// The launch selection `executeTests` will actually honour, from the per-project class
/// map. ONE derivation, so the run's real selection and the one projects
/// through cannot read the same map differently.
///
/// An EMPTY map means "no selection" → every project runs in full; a project present with
/// `[]` runs in full; present with classes runs filtered; ABSENT is skipped entirely.
let internal selectionOf
    (configs: TestConfig list)
    (byProject: Map<string, string list>)
    : Map<string, ProjectSelection> =
    if Map.isEmpty byProject then
        configs |> List.map (fun c -> c.Project, ProjectInFull) |> Map.ofList
    else
        configs
        |> List.choose (fun c ->
            match Map.tryFind c.Project byProject with
            | None -> None
            | Some [] -> Some(c.Project, ProjectInFull)
            | Some classes -> Some(c.Project, ProjectClasses(Set.ofList classes)))
        |> Map.ofList

/// `checkSkipClasses` at launch: which projects the run takes whole, why, and so which of
/// their declared classes it leaves out (`CheckSkip`).
module internal CheckSkipLaunch =

    /// Every widening of one launch that can take a project whole, kept apart so each
    /// reason a project runs whole can be named.
    type Widenings =
        {
            /// `confirm`: every project, every class.
            FullSuiteRequested: bool
            LedgerUnreadable: bool
            /// No valid full-suite baseline. Only a run of every class earns one, so a
            /// skip here would leave `check` owing it forever.
            NoFullSuiteBaseline: bool
            /// The impact graph has a hole: an unanalysable or unindexed file, a failed
            /// extension.
            CoarseGaps: bool
            EvidenceGap: Set<string>
            DependencyFanout: Set<string>
            RuntimeCoverage: Set<string>
            /// Projects an outstanding red that names no class puts in the run whole.
            ProjectLevelReds: Set<string>
            /// `Some` when the selection was EMPTY, which runs every project whole: every
            /// reason the zero-affected skip was refused (`zeroAffectedWidening`).
            EmptySelection: ZeroAffectedWidening list option
        }

    /// Every reason `project` runs whole under `widenings`.
    let causesOf (widenings: Widenings) (project: string) : WholeCause list =
        [ if widenings.FullSuiteRequested then
              WholeCause.Unskippable "confirm runs every class"
          if widenings.LedgerUnreadable then
              WholeCause.Unskippable "the pending-verification ledger could not be read"
          if widenings.NoFullSuiteBaseline then
              WholeCause.Unskippable "no full-suite baseline, which only a run of every class earns"
          if widenings.CoarseGaps then
              WholeCause.Unskippable "the impact graph has a hole"
          if Set.contains project widenings.EvidenceGap then
              WholeCause.Unskippable "no whole-project run under the current project model"
          if Set.contains project widenings.DependencyFanout then
              WholeCause.Skippable SkipWidening.DependencyFanout
          if Set.contains project widenings.RuntimeCoverage then
              WholeCause.Skippable SkipWidening.RuntimeCoverageDebt
          if Set.contains project widenings.ProjectLevelReds then
              WholeCause.Unskippable "an outstanding red that names no class"
          match widenings.EmptySelection with
          | None -> ()
          | Some [] -> WholeCause.Unskippable "an empty selection nothing owed explains"
          | Some refusals ->
              for refusal in refusals do
                  match refusal with
                  | ZeroAffectedWidening.NoSessionBaseline -> WholeCause.Skippable SkipWidening.NoSessionBaseline
                  | ZeroAffectedWidening.RuntimeCoverageDebt _ -> WholeCause.Skippable SkipWidening.RuntimeCoverageDebt
                  | other -> WholeCause.Unskippable(ZeroAffectedWidening.describe other) ]

    /// The classes each project of `selection` that runs whole leaves out, and every
    /// project that declared some with the reasons it runs whole. `named` is each
    /// project's classes the run selected by name, which it never leaves out.
    let decide
        (configs: TestConfig list)
        (widenings: Widenings)
        (named: Map<string, Set<string>>)
        (selection: Map<string, ProjectSelection>)
        : Map<string, string list * WholeCause list> =
        configs
        |> List.choose (fun config ->
            match config.CheckSkip, Map.tryFind config.Project selection with
            | Some skip, Some ProjectInFull ->
                let causes = causesOf widenings config.Project

                let excluded =
                    CheckSkip.excluded skip causes (Map.tryFind config.Project named |> Option.defaultValue Set.empty)

                Some(config.Project, (excluded, causes))
            | _ -> None)
        |> Map.ofList

    /// The classes each project leaves out; projects that leave out none are absent.
    let excludedOf (decisions: Map<string, string list * WholeCause list>) : Map<string, string list> =
        decisions
        |> Map.map (fun _ (excluded, _) -> excluded)
        |> Map.filter (fun _ excluded -> not (List.isEmpty excluded))

    /// `selection` with every project that leaves out classes launched under its exclude
    /// filter instead of in full.
    let narrow
        (excluded: Map<string, string list>)
        (selection: Map<string, ProjectSelection>)
        : Map<string, ProjectSelection> =
        selection
        |> Map.map (fun project chosen ->
            match chosen, Map.tryFind project excluded with
            | ProjectInFull, Some classes -> ProjectExcept(Set.ofList classes)
            | _ -> chosen)

    /// `attribution` with the classes each project left out.
    let attribute
        (excluded: Map<string, string list>)
        (attribution: ProjectAttribution list)
        : ProjectAttribution list =
        attribution
        |> List.map (fun entry ->
            { entry with
                SkippedClasses = Map.tryFind entry.Project excluded |> Option.defaultValue [] })

    /// One line per project that declared `checkSkipClasses` and runs whole: what it left
    /// out and why, or why it runs every class.
    let describe (decisions: Map<string, string list * WholeCause list>) : string list =
        decisions
        |> Map.toList
        |> List.map (fun (project, (excluded, causes)) ->
            let because =
                match causes with
                | [] -> "no widening it can name"
                | causes -> causes |> List.map CheckSkip.describeCause |> String.concat ", "

            match excluded with
            | [] -> $"checkSkipClasses: %s{project} runs every class — it runs whole for %s{because}"
            | classes ->
                $"""checkSkipClasses: %s{project} runs whole for %s{because}, WITHOUT %s{String.concat ", " classes} — `confirm` runs them""")

/// The sources a launch's selection is the union of, each as the projects it put in the
/// run. Kept apart until the launch is attributed, because once unioned no one can tell
/// a wide selection that is honest from one that is a defect.
type SelectionSources =
    {
        /// Every project, when `confirm`, an unreadable ledger or a missing full-suite
        /// baseline forced the whole suite.
        FullSuite: Set<string>
        /// Every project, when the impact graph had a hole.
        CoarseFallback: Set<string>
        /// The projects with no whole-project run under the current project model.
        EvidenceGap: Set<string>
        /// The projects whose dependency fingerprint changed.
        DependencyFanout: Set<string>
        /// The projects runtime coverage attributes a changed file to.
        RuntimeCoverage: Set<string>
        /// The projects an outstanding red added classes to, or put in the run.
        OutstandingFailure: Set<string>
        /// The projects whose classes cover a changed or still-owed symbol.
        ChangedSymbols: Set<string>
        /// The projects whose classes cover a symbol-free edit's file.
        SymbolFreeEdit: Set<string>
        /// The projects whose declared always-classes the launch carried.
        AlwaysClasses: Set<string>
    }

module SelectionSources =
    /// No source selected anything.
    let none: SelectionSources =
        { FullSuite = Set.empty
          CoarseFallback = Set.empty
          EvidenceGap = Set.empty
          DependencyFanout = Set.empty
          RuntimeCoverage = Set.empty
          OutstandingFailure = Set.empty
          ChangedSymbols = Set.empty
          SymbolFreeEdit = Set.empty
          AlwaysClasses = Set.empty }

module SelectionAttribution =
    /// Every project of `selection` with each source that selected it, widest first.
    ///
    /// Attributes the LAUNCHED selection, never the sources alone: a source may name a
    /// project the run then left out (it is not configured), and that is no cause of
    /// anything. An empty `byProject` that still launched ran every project in full
    /// because it was empty, so that is the one cause it names.
    let attribute
        (sources: SelectionSources)
        (byProject: Map<string, string list>)
        (selection: Map<string, ProjectSelection>)
        : ProjectAttribution list =
        let named =
            [ SelectionCause.FullSuite, sources.FullSuite
              SelectionCause.CoarseFallback, sources.CoarseFallback
              SelectionCause.EvidenceGap, sources.EvidenceGap
              SelectionCause.DependencyFanout, sources.DependencyFanout
              SelectionCause.RuntimeCoverage, sources.RuntimeCoverage
              SelectionCause.OutstandingFailure, sources.OutstandingFailure
              SelectionCause.ChangedSymbols, sources.ChangedSymbols
              SelectionCause.SymbolFreeEdit, sources.SymbolFreeEdit
              SelectionCause.AlwaysClasses, sources.AlwaysClasses ]

        let causesOf project =
            if Map.isEmpty byProject then
                [ SelectionCause.EmptySelection ]
            else
                named
                |> List.filter (fun (_, projects) -> Set.contains project projects)
                |> List.map fst

        selection
        |> Map.keys
        |> Seq.map (fun project ->
            { Project = project
              Causes = causesOf project
              SkippedClasses = [] })
        |> List.ofSeq

/// What `check` would have launched over this tree — the run's own
/// selection with the FULL-SUITE widening taken back out, and only that one.
///
/// The other widenings stay, and that is the whole care in this function. The coarse
/// fallback for unanalysable files and the unreadable-ledger fallback
/// fire in the inner loop too, so a projection that dropped them would
/// model a `check` narrower than the one that actually runs — and would then report
/// misses the selector never made. Only `set-scope full`, which is `confirm`'s own doing,
/// is removed.
let internal wouldHaveRunSelection
    (configs: TestConfig list)
    (symbolAffectedByProject: Map<string, string list>)
    (coarseWidened: Set<string>)
    (runtimeCoverageProjects: Set<string>)
    (ledgerUnreadable: bool)
    : Map<string, ProjectSelection> =
    let impactWidened = Set.union coarseWidened runtimeCoverageProjects

    let checkForceRunProjects =
        if ledgerUnreadable then
            Set.union impactWidened (fullSuiteProjects configs)
        else
            impactWidened

    checkForceRunProjects
    |> Set.fold (fun acc proj -> Map.add proj [] acc) symbolAffectedByProject
    |> AlwaysClasses.widen configs
    |> selectionOf configs

/// The same `ScopeReport`, for a selection that was never LAUNCHED — the
/// one `check` would have used before `confirm` widened it.
///
/// Reads the REQUEST, where `scopeOf` reads the receipt, and that is the whole difference
/// between them: there is no run to produce evidence for a run that did not happen. It is
/// used only to describe the projected reading's scope in the verdict file, never to
/// grant coverage — `RunCoverage` still comes from what executed.
let internal scopeOfSelection (projects: string list) (selection: Map<string, ProjectSelection>) : ScopeReport =
    let total = List.length projects
    let selected = projects |> List.filter (fun p -> Map.containsKey p selection)

    if List.isEmpty selected then
        ScopeNone total
    elif
        List.length selected = total
        && selected |> List.forall (fun p -> selection.[p] = ProjectInFull)
    then
        ScopeFull total
    else
        ScopeFiltered(List.length selected, total)
