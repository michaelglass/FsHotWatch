/// The test-evidence receipt a run earns, the receipt a session holds, why a tree is unverified, and edits no symbol diff sees.
module FsHotWatch.TestPrune.EvidenceReceipts

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

type TestEvidenceReceipt =
    {
        InputTreeHash: string option
        /// The project model the run was launched AND completed under. A receipt speaks for
        /// one tree under one model; under a later model it speaks only for the projects
        /// whose compile inputs are unchanged (`receiptSpeaksFor`).
        ModelGeneration: int64 option
        /// Each runnable project's `.fsproj` and compile-input identity under that model,
        /// by name (`ProjectModel.ProjectInputs`); `None` when the host published none.
        ProjectInputs: Map<string, string * string> option
        RunId: Guid
        Coverage: RunCoverage
        Seeds: string list
        ZeroSelection: ZeroSelection
        /// Why the run covered every project, when it did — the launch's
        /// `TestRunLaunch.FullSuiteCause`. `test-scope` sends it beside a `full` scope.
        FullSuiteCause: FullSuiteCause
        /// Which cause selected each project — the launch's `TestRunLaunch.SelectedBy`.
        /// `test-scope` sends it beside a `full` or `filtered` scope.
        SelectedBy: ProjectAttribution list
        /// The seeds that alone selected the most tests, or why they were not ranked —
        /// the launch's `TestRunLaunch.SeedFanout`.
        SeedFanout: SeedFanoutReading
    }

/// The test-evidence receipt a session holds, or why it holds none. One value, so a
/// revocation reason never stands beside a receipt, and the tree entries a receipt was
/// earned over never outlive it.
[<RequireQualifiedAccess>]
type HeldReceipt =
    /// No run has earned or revoked a receipt in this session.
    | NoneYet
    /// A run earned it. `manifest` is the tree it was earned on, entries and all, so a
    /// later launch can name what moved since (`SymbolFreeEdit`); `None` when that tree
    /// could not be read.
    | Standing of receipt: TestEvidenceReceipt * manifest: ReceiptTree option
    /// It was taken away, for this reason, and none has been earned or kept since.
    | Revoked of reason: string

/// Why `test-scope` holds no evidence for the tree it is asked about, when the completion
/// behind the reading selected tests and so names no `ZeroSelection`. Every one of these
/// is a "no tests ran" a reader must be able to act on, so none may reach the wire as
/// silence.
[<RequireQualifiedAccess>]
type UnverifiedTree =
    /// No run has completed in this session.
    | NoRunYet
    /// A run earned evidence, on a tree that has since changed, and no run has
    /// verified the current one.
    | TreeMoved
    /// A run earned evidence, but the current tree cannot be read to bind it.
    | TreeUnreadable
    /// The last completion earned no evidence, for this reason.
    | Revoked of reason: string
    /// The evidence is bound to this tree but covers no configured project.
    | CoversNothing

module UnverifiedTree =
    let classify
        (stored: TestEvidenceReceipt option)
        (currentTree: string option)
        (revoked: string option)
        (completedRuns: Guid list)
        : UnverifiedTree =
        match stored, revoked with
        | Some evidence, _ when ReceiptInputTree.matches evidence.InputTreeHash currentTree ->
            UnverifiedTree.CoversNothing
        | Some _, _ when currentTree.IsNone -> UnverifiedTree.TreeUnreadable
        | Some _, _ -> UnverifiedTree.TreeMoved
        | None, Some reason -> UnverifiedTree.Revoked reason
        | None, None when List.isEmpty completedRuns -> UnverifiedTree.NoRunYet
        | None, None -> UnverifiedTree.Revoked "the last completed run recorded no test evidence"

    /// The `noTestsReason` token, and the detail that goes with it.
    let wire =
        function
        | UnverifiedTree.NoRunYet -> "no-run-yet", None
        | UnverifiedTree.TreeMoved -> "tree-moved", None
        | UnverifiedTree.TreeUnreadable -> "tree-unreadable", None
        | UnverifiedTree.Revoked reason -> "evidence-revoked", Some reason
        | UnverifiedTree.CoversNothing -> "covers-nothing", None

/// What an edit the symbol diff saw as changing nothing still owes, once a green's
/// evidence is bound to an earlier tree (`ZeroAffectedWidening.UnverifiedTree`).
///
/// The tests whose outcome a symbol-free edit to `F` can change are the tests that execute
/// code from `F`: a moved line number, a caller-info argument, stack-trace text, a body the
/// diff hashed the same. Each of those covers one of `F`'s symbols, so running every class
/// that covers ANY symbol of every edited file earns evidence for the edited tree, at the
/// standard every symbol edit's own impact selection is held to. Anything that is not
/// exactly that shape keeps the whole-project run.
[<RequireQualifiedAccess>]
type SymbolFreeEdit =
    /// Every edited file is a compiled F# source file the symbol diff has run over, and
    /// these classes, by project, cover its symbols. A file whose symbols no runnable test
    /// covers adds no class: what executes its code is known, and it is nothing, so
    /// `classes` is empty when no edited file is covered.
    | Covering of files: string list * seeds: string list * classes: Map<string, string list>
    /// Why the edit is not narrowed: the run stays every project in full.
    | Refused of reason: string

module internal SymbolFreeEdit =
    /// The impact index, as far as the narrowing reads it.
    type Index =
        {
            /// The full names of the symbols the index holds for a repo-relative file.
            SymbolsIn: string -> string list
            /// The test methods the index holds as declared in a repo-relative file.
            TestsIn: string -> TestMethodInfo list
            /// The tests a set of seed symbols selects (`QueryAffectedTests`).
            Affected: string list -> TestMethodInfo list
        }

    let private isFSharpSource (rel: string) =
        String.Equals(Path.GetExtension rel, ".fs", StringComparison.OrdinalIgnoreCase)

    /// The symbols the index holds for a repo-relative file, and the runnable tests a
    /// symbol-free edit to it runs: those covering its symbols and those declared in it.
    /// ONE derivation for the narrowing (`select`) and its explanation (`explainFile`).
    let private covering (runnable: string -> bool) (index: Index) (rel: string) =
        let seeds = index.SymbolsIn rel

        let tests =
            if List.isEmpty seeds then
                []
            else
                index.Affected seeds @ index.TestsIn rel
                |> List.filter (fun t -> runnable t.TestProject)

        seeds, tests

    /// Classes by project, each project's sorted and distinct.
    let private classesOf (tests: TestMethodInfo list) : Map<string, string list> =
        tests
        |> List.groupBy _.TestProject
        |> List.map (fun (project, tests) -> project, tests |> List.map _.TestClass |> List.distinct |> List.sort)
        |> Map.ofList

    /// Why editing one file selects what it selects (`fshw explain`).
    type FileSelection =
        {
            /// The symbols the index holds for the file, sorted: the seeds of the edit.
            Seeds: string list
            /// The classes a symbol-free edit to it runs, by project.
            Classes: Map<string, string list>
            /// How many indexed test methods cover the file. The class filter runs EVERY
            /// test in those classes, so a run executes at least this many, usually more.
            CoveringTests: int
            /// Its symbols ranked by the tests each alone selects.
            Fanout: SeedFanoutReading
        }

    /// The selection a symbol-free edit to `rel` makes, explained; `None` when the index
    /// holds no symbol for it, which `select` refuses to narrow.
    let explainFile (runnable: string -> bool) (index: Index) (rel: string) : FileSelection option =
        match covering runnable index rel with
        | [], _ -> None
        | seeds, tests ->
            let aloneCount seed =
                index.Affected [ seed ]
                |> List.filter (fun t -> runnable t.TestProject)
                |> List.length

            Some
                { Seeds = seeds |> List.distinct |> List.sort
                  Classes = classesOf tests
                  CoveringTests = tests |> List.distinct |> List.length
                  // Ranked whenever anything is selected: one file's symbols are few, and
                  // the walk runs only when someone asks.
                  Fanout = SeedFanoutReading.ofSelection 0 MaxSeedsToAttribute tests.Length seeds aloneCount }

    /// Decide the narrowing for a launch whose evidence is bound to `evidence` and whose
    /// tree is now `current`. `sealedTree` is the tree the last `BatchChecked` folded
    /// its check results over; `compiled` says whether the current model compiles a
    /// repo-relative path (`None` when no model publishes its files).
    let select
        (evidence: ReceiptTree option)
        (current: ReceiptTree option)
        (sealedTree: string option)
        (compiled: (string -> bool) option)
        (runnable: string -> bool)
        (index: Index)
        : SymbolFreeEdit =
        match evidence, current, compiled with
        | None, _, _ ->
            SymbolFreeEdit.Refused
                "the manifest of the tree the evidence is bound to is not held, so what moved cannot be named"
        | _, None, _ -> SymbolFreeEdit.Refused "the current tree could not be read"
        | _, _, None -> SymbolFreeEdit.Refused "no project model publishes the files it compiles"
        | Some _, Some after, _ when not (ReceiptInputTree.matches sealedTree (Some after.Identity)) ->
            SymbolFreeEdit.Refused "the symbol diff has not folded the current tree"
        | Some before, Some after, Some compiled ->
            let delta = ReceiptInputTree.delta before.Entries after.Entries

            let outside =
                delta.Changed
                |> List.filter (fun rel -> not (isFSharpSource rel && compiled rel))

            if not (List.isEmpty delta.Added && List.isEmpty delta.Removed && List.isEmpty outside) then
                SymbolFreeEdit.Refused(
                    "a path other than an edited F# source file the model compiles moved — "
                    + ReceiptInputTree.describeDelta { delta with Changed = outside }
                )
            elif List.isEmpty delta.Changed then
                SymbolFreeEdit.Refused "no path moved"
            else
                let perFile =
                    delta.Changed
                    |> List.map (fun rel ->
                        let seeds, tests = covering runnable index rel
                        rel, seeds, tests)

                // A file the index holds no symbol for is one it cannot answer for. A
                // file it holds symbols for, with no runnable test covering any, is
                // answered: nothing executes it.
                match perFile |> List.tryFind (fun (_, seeds, _) -> List.isEmpty seeds) with
                | Some(rel, _, _) -> SymbolFreeEdit.Refused $"the impact index holds no symbol for %s{rel}"
                | None ->
                    let classes = perFile |> List.collect (fun (_, _, tests) -> tests) |> classesOf

                    SymbolFreeEdit.Covering(
                        delta.Changed,
                        perFile
                        |> List.collect (fun (_, seeds, _) -> seeds)
                        |> List.distinct
                        |> List.sort,
                        classes
                    )
