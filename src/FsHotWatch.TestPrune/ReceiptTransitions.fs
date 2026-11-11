/// How a completed run changes the evidence receipt: the project model it was earned under, what carries across a model change, and why a receipt is kept, narrowed, earned or revoked.
module FsHotWatch.TestPrune.ReceiptTransitions

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

/// What ONE completed run does to the stored `EvidenceReceipt`.
///
/// The store used to have a single replace arm: every completion that was not the
/// exact AlreadyVerified no-op wrote a fresh receipt, and a zero-test completion on an
/// unchanged tree whose launch happened to be UNBOUND (`ReceiptInputTree.read` returned
/// `None` — a skipped entry, an unreadable file, the aborted-launch path) replaced an
/// earned, servable receipt with one that can never match any tree. The evidence the
/// earlier attempt had produced was gone, and `test-scope` then served no run id at all.
///
/// A transition names what the run establishes, and the fold below is total over it:
/// there is no arm that constructs a receipt from a run that verified nothing, so
/// "replaced by an unbound receipt" is unrepresentable rather than guarded against.
[<RequireQualifiedAccess>]
type ReceiptTransition =
    /// Executed at least one project to a verdict, `Outcome = Normal`, and the input
    /// tree it launched against is the tree read at completion. The only case that
    /// constructs a receipt.
    | Earned of TestEvidenceReceipt
    /// A quiet completion that adds nothing and takes nothing away: the selector chose
    /// zero tests because the change was already verified, the run covered nothing and
    /// produced no results, and the tree the previous receipt was earned on is the tree
    /// read now.
    ///
    /// Deliberately blind to the outstanding-failure ledger: failures are the LEDGER's
    /// claim (they keep the plugin red on their own), and a retained receipt cannot
    /// mask them — `IpcOutputTests` proves retained coverage under a red plugin exits 1.
    | Noop
    /// A narrower executed run on the tree AND model the previous receipt already covers
    /// in FULL, where that receipt's run earned a CLEAN evidence: it says nothing the
    /// receipt does not, so the receipt stands — and so does the model evidence that
    /// receipt's run earned. `test-scope` grades the receipt's run, and the verdict refuses
    /// a green unless that same run holds evidence for the current model, so the evidence
    /// must name the run the receipt names.
    ///
    /// Not after a REFUSING receipt (a project failed or timed out, obligations pending):
    /// a narrower run that reruns what failed says exactly what that receipt lacks, and
    /// keeping it graded a frozen refusal no impact-filtered check could ever replace. Such
    /// a run is `Earned`, its evidence taken from the failure ledger and the pending queue
    /// as they stand, which still hold anything it did not rerun.
    | Narrower
    /// The stored receipt no longer describes the tree, or this run could not stand
    /// behind one: aborted, launched unbound, the tree or the project model changed
    /// between launch and completion, or zero tests ran for a reason that is not "already verified".
    | Revoked of reason: string

module internal ObservedModel =
    /// The generation of the model the host currently publishes, when it is available.
    let generation (graph: ProjectGraphAccessor) =
        match graph.ObserveModel() with
        | FsHotWatch.ProjectModel.Observation.Available model -> Some model.Generation
        | FsHotWatch.ProjectModel.Observation.Unobserved
        | FsHotWatch.ProjectModel.Observation.Rediscovering _
        | FsHotWatch.ProjectModel.Observation.Unavailable _ -> None

    /// Each project's compile-input identity under the model the host publishes now, by
    /// `.fsproj`. `None` when no model is available or it was published without them.
    let inputs (graph: ProjectGraphAccessor) : Map<string, string> option =
        match graph.ObserveProjectInputs(), generation graph with
        | Some(published, identities), Some current when published = current -> Some identities
        | _ -> None

    /// Every registered project's `.fsproj`, by project name.
    let projectFiles (graph: ProjectGraphAccessor) : Map<string, string> =
        graph.GetAllProjects()
        |> List.map (fun fsproj -> Path.GetFileNameWithoutExtension fsproj, fsproj)
        |> Map.ofList

    /// The `.fsproj` and current compile-input identity of each named test project whose
    /// identity is known. `None` when the host publishes no identities.
    let testInputs (graph: ProjectGraphAccessor) (projects: string seq) : Map<string, string * string> option =
        inputs graph
        |> Option.map (fun now ->
            let files = projectFiles graph

            projects
            |> Seq.choose (fun project ->
                Map.tryFind project files
                |> Option.bind (fun fsproj ->
                    Map.tryFind fsproj now
                    |> Option.map (fun identity -> project, (fsproj, identity))))
            |> Map.ofSeq)

    /// Is `project`'s entry in `before` still its identity under `now`? A project missing
    /// from either side cannot be shown unchanged.
    let unchanged (before: Map<string, string * string>) (now: Map<string, string>) (project: string) =
        match Map.tryFind project before with
        | Some(fsproj, identity) -> Map.tryFind fsproj now = Some identity
        | None -> false

    /// How long a completion waits for a re-discovery to settle before it is folded
    /// against whatever the host publishes then.
    let settleBound = TimeSpan.FromMinutes 5.0

    /// Wait, bounded by `settleBound`, while the host re-discovers its project model, for
    /// a completion launched under an available model. Returns how long it waited.
    let awaitSettled (graph: ProjectGraphAccessor) (launchedUnder: int64 option) : Async<TimeSpan> =
        let started = Diagnostics.Stopwatch.StartNew()

        let rec wait () =
            async {
                match graph.ObserveModel() with
                | FsHotWatch.ProjectModel.Observation.Rediscovering _ when
                    launchedUnder.IsSome && started.Elapsed < settleBound
                    ->
                    do! Async.Sleep 100
                    return! wait ()
                | _ -> return started.Elapsed
            }

        async {
            let! elapsed = wait ()

            return
                if elapsed < TimeSpan.FromMilliseconds 100.0 then
                    TimeSpan.Zero
                else
                    elapsed
        }

    /// Wait, bounded by `settleBound`, while the host discovers its project model, before
    /// a run is launched. A launch reads the model's generation, and during a discovery
    /// that reading is `None`: a run launched under `None` that completes under a real
    /// model cannot show which projects the model changed, so all of it is revoked.
    ///
    /// Waits on `Rediscovering` only. `Unobserved` is also a host that never discovers
    /// (analysis-only, or a test host), so waiting on it would only spend the bound.
    /// Returns how long it waited and whether the discovery finished within `bound`.
    let internal awaitDiscoveryWithin (bound: TimeSpan) (graph: ProjectGraphAccessor) : Async<TimeSpan * bool> =
        let started = Diagnostics.Stopwatch.StartNew()

        let rec wait (slept: bool) =
            async {
                match graph.ObserveModel() with
                | FsHotWatch.ProjectModel.Observation.Rediscovering _ when started.Elapsed < bound ->
                    do! Async.Sleep 100
                    return! wait true
                | FsHotWatch.ProjectModel.Observation.Rediscovering _ -> return started.Elapsed, false
                | _ when slept -> return started.Elapsed, true
                | _ -> return TimeSpan.Zero, true
            }

        wait false

    /// `awaitDiscoveryWithin settleBound`.
    let awaitDiscovery (graph: ProjectGraphAccessor) : Async<TimeSpan * bool> = awaitDiscoveryWithin settleBound graph

    /// Report what a launch's wait for discovery came to: a warning when discovery was
    /// still running at the bound, a note when the launch waited, nothing when it did not.
    let internal reportDiscoveryWait (warn: string -> unit) (info: string -> unit) (waited: TimeSpan, settled: bool) =
        if not settled then
            warn
                $"project discovery was still running after %.0f{waited.TotalSeconds}s; launching without a model, so the run is revoked if the model it completes under differs"
        elif waited > TimeSpan.Zero then
            info $"waited %.1f{waited.TotalSeconds}s for project discovery to finish before launching"

    /// A generation for a log line; `None` is a model that was not available.
    let describe (generation: int64 option) =
        match generation with
        | Some generation -> string generation
        | None -> "none (no available model)"

/// Whether a receipt earned under one project model still speaks for the model the host
/// publishes now: always under the model it was earned under; under a later one only when
/// every project it covered compiles from the same inputs under both. A re-discovery that
/// changed only an unrelated project does not take a receipt away.
let internal receiptSpeaksFor
    (currentGeneration: int64 option)
    (currentInputs: Map<string, string> option)
    (receipt: TestEvidenceReceipt)
    : bool =
    receipt.ModelGeneration = currentGeneration
    || match receipt.ModelGeneration, currentGeneration, receipt.ProjectInputs, currentInputs with
       | Some _, Some _, Some before, Some now ->
           let covered = RunCoverage.coveredProjects receipt.Coverage

           not covered.IsEmpty
           && covered |> Set.forall (ObservedModel.unchanged before now)
       | _ -> false

/// What a completion launched under one project model still vouches for once the host
/// publishes another. A re-discovery that changed only a project no test project
/// compiles from must not throw a finished run away: on a daemon whose model is
/// re-evaluated more often than a whole-project run takes, nothing would ever complete.
[<RequireQualifiedAccess>]
type ModelCarry =
    /// Launched and completed under one model: nothing to decide.
    | SameModel
    /// Every project with a result compiles from the same inputs under both models: the
    /// run stands, as if launched under the current one.
    | Carried
    /// These projects' compile inputs changed, or cannot be shown unchanged: their results
    /// are revoked and the rest stand.
    | Revoked of changed: Set<string>
    /// No model is available now, or the host published no identities: nothing can be
    /// shown unchanged, and the run is left as launched, to be revoked whole.
    | Unprovable

/// Decide `ModelCarry` for a completion and return the completion and launch the fold
/// proceeds with: re-stamped to the current model when anything is carried, with every
/// revoked project's result and selection removed.
let internal acrossModelChange
    (currentGeneration: int64 option)
    (currentInputs: Map<string, string> option)
    (completed: TestRunCompleted)
    (launch: TestRunLaunch)
    : ModelCarry * TestRunCompleted * TestRunLaunch =
    match launch.ModelGeneration, currentGeneration, launch.ProjectInputs, currentInputs with
    | launched, current, _, _ when launched = current -> ModelCarry.SameModel, completed, launch
    | Some _, Some _, Some before, Some now ->
        let changed =
            completed.Results
            |> Map.keys
            |> Seq.filter (fun project -> not (ObservedModel.unchanged before now project))
            |> Set.ofSeq

        let keep (project: string) _ = not (Set.contains project changed)

        let carried =
            { launch with
                ModelGeneration = currentGeneration
                ProjectInputs = Some(Map.filter keep before)
                Selection = Map.filter keep launch.Selection }

        let results = Map.filter keep completed.Results

        if Set.isEmpty changed then
            ModelCarry.Carried, completed, carried
        elif Map.isEmpty results then
            // Nothing stands: left as launched, the run earns nothing, and the host keeps
            // waiting for evidence about this model instead of settling on a refusal.
            ModelCarry.Revoked changed, completed, launch
        else

            let verification =
                match RunVerification.ofResults results with
                | Ran _ -> Ran RunScope.Partial
                | other -> other

            ModelCarry.Revoked changed,
            { completed with
                Results = results
                Verification = verification },
            carried
    | _ -> ModelCarry.Unprovable, completed, launch

/// The projects whose results a model change took back (see `acrossModelChange`): the
/// changed ones when it could tell, every project with a result when it could not.
let internal revokedProjects (carry: ModelCarry) (completed: TestRunCompleted) : Set<string> =
    match carry with
    | ModelCarry.SameModel
    | ModelCarry.Carried -> Set.empty
    | ModelCarry.Revoked changed -> changed
    | ModelCarry.Unprovable -> completed.Results |> Map.keys |> Set.ofSeq

module ReceiptTransition =
    let private allResultsCompleted (completed: TestRunCompleted) =
        completed.Results
        |> Map.forall (fun _ result ->
            match result with
            | TestsPassed _
            | TestsFailed _
            | TestsNoMatch _ -> true
            | _ -> false)

    /// Classify a completion. `previous` is consulted ONLY to decide whether a quiet
    /// completion is a `Noop` or an executed one `Narrower` (it must have something to
    /// keep, on the same tree) — never to construct a receipt.
    ///
    /// `previousEvidence` is the evidence the state holds for `previous`'s run under the
    /// current model (`EarnedEvidence.retainedForRun`). A receipt stands only WITH it:
    /// `Noop` and `Narrower` keep the receipt and publish that evidence, so a receipt
    /// whose run's evidence is not held cannot be kept. Kept anyway, `test-scope` would
    /// grade a run that no evidence the daemon publishes names, and the verdict would
    /// refuse the green with "nothing vouches for this green".
    let internal classify
        (runnableProjects: string list)
        (previous: TestEvidenceReceipt option)
        (previousEvidence: EarnedEvidence option)
        (currentInputTree: string option)
        (currentModelGeneration: int64 option)
        (currentInputs: Map<string, string> option)
        (writtenByTestRun: ReceiptInputTree.RunWrites option)
        (launch: TestRunLaunch)
        (completed: TestRunCompleted)
        (coverage: RunCoverage)
        : ReceiptTransition =
        // Bound to the tree AND the model: a receipt earned under a replaced model says
        // nothing about the current one, however still the tree held. And, under a model,
        // bound to the evidence its run earned, which is what the verdict grades it by.
        // With no model there is no evidence for any run to hold, and no verdict grades
        // one: the verdict refuses for want of the model first.
        let previousBoundToCurrent =
            match previous with
            | Some prior ->
                ReceiptInputTree.matches prior.InputTreeHash currentInputTree
                && receiptSpeaksFor currentModelGeneration currentInputs prior
                && (currentModelGeneration.IsNone
                    || previousEvidence |> Option.exists (fun evidence -> evidence.RunId = prior.RunId))
            | None -> false

        // Whether the standing receipt's own run earned an evidence that refuses a green:
        // a narrower run keeps only a clean one.
        let previousRefused =
            previousEvidence
            |> Option.exists (fun evidence -> not (List.isEmpty evidence.FailureReasons))

        let executed =
            completed.Results
            |> Map.exists (fun _ result -> TestResult.executedTests result)

        let quietAlreadyVerified =
            launch.ZeroSelection = ZeroSelection.AlreadyVerified
            && Map.isEmpty coverage
            && Map.isEmpty completed.Results

        match completed.Outcome with
        | Aborted reason -> ReceiptTransition.Revoked $"the run aborted: %s{reason}"
        | Normal when launch.ModelGeneration <> currentModelGeneration ->
            ReceiptTransition.Revoked "the project model was replaced between launch and completion"
        | Normal when quietAlreadyVerified ->
            if previousBoundToCurrent then
                ReceiptTransition.Noop
            else
                ReceiptTransition.Revoked
                    "already verified, but no receipt whose run's evidence is held is bound to the current tree and project model"
        | Normal when not executed -> ReceiptTransition.Revoked "the run executed no project to a verdict"
        | Normal when not (ReceiptInputTree.matches launch.InputTreeHash currentInputTree) ->
            ReceiptInputTree.classifyMismatch launch.InputTreeHash currentInputTree writtenByTestRun
            |> ReceiptInputTree.describeMismatch
            |> ReceiptTransition.Revoked
        | Normal ->
            let narrowerThanPrevious =
                previousBoundToCurrent
                && not previousRefused
                && allResultsCompleted completed
                && (previous
                    |> Option.exists (fun prior -> RunCoverage.coversWholeSuite runnableProjects prior.Coverage))
                && not (RunCoverage.coversWholeSuite runnableProjects coverage)

            if narrowerThanPrevious then
                ReceiptTransition.Narrower
            else
                ReceiptTransition.Earned
                    { InputTreeHash = currentInputTree
                      ModelGeneration = currentModelGeneration
                      ProjectInputs = launch.ProjectInputs
                      RunId = completed.RunId
                      Coverage = coverage
                      Seeds = launch.Seeds
                      ZeroSelection = launch.ZeroSelection
                      FullSuiteCause = launch.FullSuiteCause
                      SelectedBy = launch.SelectedBy
                      SeedFanout = launch.SeedFanout
                      Price = launch.Price }

    /// Fold one transition into the store. Total, and the only place the store changes
    /// on a completion.
    let apply (previous: TestEvidenceReceipt option) (transition: ReceiptTransition) : TestEvidenceReceipt option =
        match transition with
        | ReceiptTransition.Earned receipt -> Some receipt
        | ReceiptTransition.Noop
        | ReceiptTransition.Narrower -> previous
        | ReceiptTransition.Revoked _ -> None

    /// The held receipt after a completion's `transition`: an earned receipt stands with
    /// the tree it was earned on (`currentTree`), a revocation names its reason, a narrower
    /// run keeps the receipt that stands, and a no-op keeps everything.
    let hold (held: HeldReceipt) (transition: ReceiptTransition) (currentTree: ReceiptTree option) : HeldReceipt =
        match transition, held with
        | ReceiptTransition.Earned receipt, _ -> HeldReceipt.Standing(receipt, currentTree)
        | ReceiptTransition.Revoked reason, _ -> HeldReceipt.Revoked reason
        | ReceiptTransition.Noop, _
        | ReceiptTransition.Narrower, HeldReceipt.Standing _ -> held
        // `classify` names no narrower run without a standing receipt; were it to, nothing
        // stands and no reason is held.
        | ReceiptTransition.Narrower, (HeldReceipt.NoneYet | HeldReceipt.Revoked _) -> HeldReceipt.NoneYet

/// One line per completion that answers, from the log alone, what a receipt refusal asks:
/// which model the run was launched and completed under, which run the receipt now names
/// (the run `test-scope` grades), and which run and generation the model evidence names.
let internal describeCompletionEvidence
    (runId: Guid)
    (launchGeneration: int64 option)
    (currentGeneration: int64 option)
    (transition: ReceiptTransition)
    (receipt: TestEvidenceReceipt option)
    (earned: EarnedEvidence option)
    : string =
    let idOf (id: Guid) = id.ToString("N")

    let receiptText =
        let graded =
            match receipt with
            | Some receipt -> $"now grades run %s{idOf receipt.RunId}"
            | None -> "now grades no run"

        match transition with
        | ReceiptTransition.Earned _ -> $"earned; %s{graded}"
        | ReceiptTransition.Noop -> $"kept; %s{graded}"
        | ReceiptTransition.Narrower -> $"kept over a narrower run; %s{graded}"
        | ReceiptTransition.Revoked reason -> $"revoked (%s{reason}); %s{graded}"

    let evidenceText =
        match earned with
        | Some evidence when List.isEmpty evidence.FailureReasons ->
            $"run %s{idOf evidence.RunId} at generation %d{evidence.Generation}, no refusals"
        | Some evidence ->
            let refusals = String.concat "; " evidence.FailureReasons
            $"run %s{idOf evidence.RunId} at generation %d{evidence.Generation}, refusing: %s{refusals}"
        | None -> "none"

    $"Run %s{idOf runId} completed: launched under project model generation %s{ObservedModel.describe launchGeneration}, completed under %s{ObservedModel.describe currentGeneration}; receipt %s{receiptText}; model evidence: %s{evidenceText}"

/// The warning for a completion that leaves the receipt and the model evidence naming
/// different runs: `test-scope` would grade one run while the evidence the verdict looks
/// it up by names another, and every check would refuse its green with "nothing vouches
/// for this green". `None` when they name one run, when there is no receipt to grade, or
/// when there is no model: then no run holds evidence, and no verdict grades one.
let internal receiptEvidenceMismatch
    (currentGeneration: int64 option)
    (receipt: TestEvidenceReceipt option)
    (earned: EarnedEvidence option)
    : string option =
    let idOf (id: Guid) = id.ToString("N")

    match currentGeneration, receipt, earned with
    | None, _, _
    | _, None, _ -> None
    | Some _, Some receipt, Some evidence when evidence.RunId = receipt.RunId -> None
    | Some _, Some receipt, held ->
        let heldText =
            match held with
            | Some evidence -> $"names run %s{idOf evidence.RunId}"
            | None -> "names no run"

        Some
            $"the receipt grades run %s{idOf receipt.RunId} but the model evidence %s{heldText}; a check graded from this state refuses its green"
