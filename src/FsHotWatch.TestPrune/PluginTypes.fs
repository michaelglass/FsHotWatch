/// The plugin's limits and the types its configuration and selection are written in: a test project's configuration, a project's selection, what a run covered, and what it missed.
module FsHotWatch.TestPrune.PluginTypes

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

/// Above this many selected tests the query is effectively a full run, so the per-seed
/// attribution below is worth paying for. Under it the breakdown is noise and is never
/// computed.
[<Literal>]
let WideSelectionTests = 500

/// Attribution re-queries each seed ALONE, so its cost is linear in seed count.
/// Past this many seeds the breakdown is skipped — and said to be skipped, so an
/// absent breakdown is never misread as "no single seed dominated".
[<Literal>]
let MaxSeedsToAttribute = 200

/// The bound `flushAndQueryAffected` declares over itself.
///
/// Impact selection is one long unit of work inside ONE event fold: it finishes no
/// plugin event while it runs, and in a workspace with no impact database it does its
/// largest possible version of that — every symbol in the tree is a seed and the
/// selection is the whole suite. To the daemon's stall detector that is byte-for-byte
/// the signature of a handler that never returned, and an undeclared fold is named
/// WEDGED after five minutes. A cold attribution can legitimately exceed that, and did:
/// a check was failed at five minutes on work that completed seventeen seconds later.
///
/// So the fold declares itself instead, and this is the price of the declaration. It is
/// generous on purpose — roughly twice the whole observed cold-start window, of which
/// selection was only a part — because failing a green tree costs more than waiting.
/// It is still FINITE, and well inside the hour-long `WaitForComplete` timeout, so a
/// selection that genuinely hangs is reported by name rather than swallowed.
let ImpactSelectionDeadline = System.TimeSpan.FromMinutes 20.0

/// How many CONSECUTIVE flush cycles a symbol must sit in the
/// needs-testing queue before its persistence is itself evidence of a problem.
///
/// One cycle is ordinary; two is explicable (an aborted run, a red project mid-fix); by
/// three the symbol has outlived several complete verify attempts while still dragging in
/// a quarter of the suite. Deliberately late — this drives a human-visible warning, and
/// one that cries wolf during an ordinary red-to-green cycle gets tuned out.
[<Literal>]
let PoisonSeedRuns = 3

/// The share of a run's selected tests one seed must account for before it reads as
/// a graph hub or a mis-qualified symbol rather than an ordinary dependency. Matches
/// the existing per-seed attribution threshold (`affected.Length / 4`) so the two
/// diagnostics agree about what "dominant" means.
[<Literal>]
let PoisonSeedSharePercent = 25

/// Is this queued symbol behaving like the poisoned seed — pinned
/// across runs AND selecting a large fraction of the suite every time?
///
/// A symbol only leaves the queue when every runnable project covering it passes, so ONE
/// persistently-red project pins it forever, and while pinned it re-seeds its whole
/// selection on every subsequent run. A single mis-qualified symbol (`name`, `kind`) plus
/// one red project is therefore a permanent, silent, near-full suite that looks exactly
/// like ordinary impact analysis from the outside.
///
/// A conjunction because neither half is sufficient: a pinned symbol selecting three
/// tests is just a slow fix, and a genuine graph hub selecting half the suite for one run
/// is just an expensive edit. Integer arithmetic is deliberate — `alone * 100 >= affected
/// * share` avoids the rounding that would let a seed sit just under the line forever.
let isPoisonSuspect (consecutiveRuns: int) (affectedCount: int) (aloneCount: int) : bool =
    consecutiveRuns >= PoisonSeedRuns
    && affectedCount > 0
    && aloneCount * 100 >= affectedCount * PoisonSeedSharePercent

/// Advance the consecutive-appearance counters for the symbols seeding THIS cycle.
///
/// Rebuilt from `Map.empty` rather than updated in place, so a symbol absent from
/// this cycle's seeds loses its history entirely. That is what makes the count
/// CONSECUTIVE: a symbol that clears and is later re-queued starts again at one, and
/// cannot accumulate its way to a false accusation across unrelated edits.
let bumpSeedAges (previous: Map<string, int>) (seeds: string list) : Map<string, int> =
    seeds
    |> List.fold (fun acc s -> Map.add s ((previous |> Map.tryFind s |> Option.defaultValue 0) + 1) acc) Map.empty

/// How many completed runs the session ledger keeps, and therefore how
/// many `test-scope` declares.
///
/// Generous against the worst check anyone has recorded (four batches) so the bound
/// never truncates a real one, and small enough that the reply stays a reply: 64 ids is
/// ~2 KB on a path that earns verdicts, and a growing-without-bound diagnostic in that
/// path is a new failure mode.
[<Literal>]
let SessionRunLedger = 64


/// How fshw obtains the structured pass/fail report a test verdict is derived
/// from. The report (CTRF) — not the process exit code — is authoritative, but
/// only a runner that actually emits a parseable report can be trusted that
/// way, and an UNSUPPORTED `--report-*` flag is fatal (the runner exits
/// "invalid command line" and runs nothing). So injection of the report flag is
/// scoped by this setting.
type ReportVerificationFormat =
    /// Inject the matching CTRF switches iff the restored project graph resolves
    /// to a supported xUnit 3 or 4 runner. The default.
    | AutoDetect
    /// Always inject CTRF switches (force-on for a capable custom runner the
    /// detector misses). Unknown custom runners retain the xUnit 3 switch family.
    | Ctrf
    /// Never inject a report flag — the process exit code is authoritative
    /// (force-off; e.g. a custom runner that would error on `--report-ctrf`).
    | Disabled

// ─────────────────────────────────────────────────────────────────────────────
// a run may clear ONLY what it covered.
//
// "No failures reported by THIS run" is not "no failures". A full run failed project X;
// a queued impact-filtered re-run then executed a NARROWER selection, passed, and — via
// `ClearAllErrors` + last-cycle-wins — superseded X's red. X never re-ran, yet the check
// went green.
//
// So: a run carries the SELECTION it was launched against, a completed run's COVERAGE is
// that selection intersected with what actually executed, and clearing is a total
// function over that coverage. A filtered run cannot express "clear everything". A red
// survives every run that did not execute it, and dies the moment one that did executes
// it green.
// ─────────────────────────────────────────────────────────────────────────────

/// What a run was LAUNCHED against, per test project — captured at dispatch
/// (`TestRunLaunch.Selection`), never re-derived at completion, because by then the
/// selection inputs have moved on. A project ABSENT from the selection map was not
/// launched at all: impact analysis skipped it (and `executeTests` records the skip
/// as `TestsPassed("", filtered, 0)`, a pass that proves precisely nothing).
type ProjectSelection =
    /// Launched with no class filter — every test in the project was asked to run.
    | ProjectInFull
    /// Launched under a class filter — only these classes were asked to run.
    | ProjectClasses of Set<string>
    /// Launched under an EXCLUDE filter (`checkSkipClasses`): every class but these was
    /// asked to run. A filtered launch: it covers only the classes its report shows ran.
    | ProjectExcept of Set<string>

/// A launched selection as `SelectionPrice` prices it.
let internal askedOf (selection: Map<string, ProjectSelection>) : (string * SelectionPrice.Asked) list =
    selection
    |> Map.toList
    |> List.map (fun (project, s) ->
        project,
        match s with
        | ProjectInFull -> SelectionPrice.Asked.Whole
        | ProjectClasses classes -> SelectionPrice.Asked.Classes classes
        | ProjectExcept classes -> SelectionPrice.Asked.AllBut classes)

/// Changed symbols that DO have covering tests — in a test project
/// `tests.projects` does not list, so this daemon can never run them. Keyed by symbol,
/// valued by the unlisted projects holding its only covering tests.
///
/// Dropped from the pending queue by the same rule as a symbol with no test at all
/// (retaining it wedges the queue forever), but never SILENTLY: the
/// obligation is written off here, and the write-off is what the verdict has to say.
/// A reader who sees "no covering test" for a symbol that has one in an unlisted
/// project concludes the analyzer is broken; a reader who sees the project name can
/// decide whether to list it or declare it excluded.
type UnrunnableCoverage = Map<string, Set<string>>

module UnrunnableCoverage =
    let projects (unrunnable: UnrunnableCoverage) : Set<string> =
        unrunnable |> Map.values |> Set.unionMany

[<RequireQualifiedAccess>]
type UncoveredChanges =
    | No
    /// Every symbol was dropped: `symbols` names them all, `unrunnable` the subset
    /// that had covering tests this daemon cannot run.
    | AllUncovered of symbols: string list * unrunnable: UnrunnableCoverage

module UncoveredChanges =
    let isAll =
        function
        | UncoveredChanges.No -> false
        | UncoveredChanges.AllUncovered _ -> true

[<RequireQualifiedAccess>]
type ZeroSelection =
    | NotAZero
    | AlreadyVerified
    | ChangesUncovered of symbols: string list * unrunnable: UnrunnableCoverage

module ZeroSelection =
    let token =
        function
        | ZeroSelection.NotAZero -> None
        | ZeroSelection.AlreadyVerified -> Some "already-verified"
        | ZeroSelection.ChangesUncovered _ -> Some "changes-uncovered"

    let symbols =
        function
        | ZeroSelection.ChangesUncovered(symbols, _) -> symbols
        | ZeroSelection.NotAZero
        | ZeroSelection.AlreadyVerified -> []

    let unrunnable =
        function
        | ZeroSelection.ChangesUncovered(_, unrunnable) -> unrunnable
        | ZeroSelection.NotAZero
        | ZeroSelection.AlreadyVerified -> Map.empty

[<RequireQualifiedAccess>]
type MissCause =
    | ProjectNotSelected
    | ClassNotInFilter

type MissedFailure =
    { Project: string
      Class: string
      Cause: MissCause }

/// Would the impact selection `check` WOULD have used have EXECUTED a
/// test this run saw fail?
///
/// The question `confirm` destroys by construction. `confirm` sends `set-scope full`
/// BEFORE the scan that provokes the run, so the run is unfiltered and the impact
/// selection — still computed, still correct — is discarded at the widening. Retaining
/// it and asking this one question of the run's OWN failures turns every confirm into a
/// same-tree, same-daemon, same-instant sample of the question impact selection rests on: does
/// the selector choose the test that fails?
///
/// It answers ONLY about REACH. A test that fails here failed in a full suite; whether it
/// would also have failed alone — order, isolation, a shared fixture — is not observable
/// from one run and is never claimed. That is why the wire records the sample's BASIS.
type CheckReach =
    /// At least one test this run saw fail is inside the retained selection. `check`
    /// would have executed it, so `check` would have been red too.
    | ReachedAFailure of projects: string list
    /// This run saw tests fail and the retained selection reaches NONE of them. The
    /// selector did not choose a failing test — the case the whole record exists for.
    | ReachedNoFailure of missed: MissedFailure list
    /// This run saw no test fail at all, so there was no failure for a selection to
    /// reach. NOT the same as `ReachedNoFailure`: nothing was missed, because nothing
    /// was there.
    | NoFailuresToReach
    /// The reach could not be decided. A project-level red (a timeout, an errored host,
    /// unparseable failure output) names no class, and a class-FILTERED selection cannot
    /// be asked whether it reaches a failure with no class — so it is refused rather
    /// than guessed. Every unknown must stay an unknown: a guess in the reaching
    /// direction manufactures an agreement that was never compared.
    | ReachUnknown of reason: string

/// Exact recall among failures observed by one full `confirm` run. This is deliberately
/// not called general test-selection recall: passing tests provide no oracle for whether
/// they were behaviorally relevant. The correctness threshold is 100% because one
/// observed full-suite failure outside `check`'s retained selection is a stale green.
type FailureRecall =
    | RecallMeasured of reached: int * total: int * threshold: float * acceptable: bool
    | RecallNotMeasurable of reason: string

/// What a completed run may VINDICATE in one project — the honest reach of the
/// evidence it produced. Absent from `RunCoverage` ⇒ this run says nothing at all
/// about the project (it was skipped, it never ran, or it ran under a filter whose
/// reach we cannot know).
type ProjectCoverage =
    /// The project executed with NO filter: its green speaks for every test in it,
    /// so it may clear any red the project holds.
    | CoveredWholeProject
    /// The project executed only these classes: its green speaks for them and
    /// nothing else.
    | CoveredClasses of Set<string>

/// What a completed run is entitled to clear, keyed by test project.
type RunCoverage = Map<string, ProjectCoverage>

/// A file the symbol analyser could not read, retained until it
/// analyses cleanly. Carries everything its ledger entry needs, because the entry has to
/// be RE-REPORTED after every test run: the run's ledger rewrite clears this plugin's
/// whole slice, and a warning erased by a cycle that never addressed it is the same
/// defect as a red erased by a run that never executed it.
type UnanalyzableFile =
    {
        /// Repo-relative path — what the diagnostic names.
        RelPath: string
        /// Absolute path — the ledger key.
        File: string
        /// Why analysis failed (the FCS/parse error), carried into the diagnostic.
        Reason: string
    }

/// Drop the unanalysable-file entries whose file is no longer on disk.
///
/// An entry leaves `UnanalyzableFiles` when the file analyses
/// CLEANLY — and a DELETED file never analyses again, because no `FileChecked` will
/// ever arrive for a path that is gone. So one deleted file left its warning in the
/// ledger for the rest of the daemon's life, and under the default warn-fail policy
/// that warning denied every subsequent check its green while ALSO widening every run
/// to the whole suite (the unanalysable-file coarse fallback). Deleting a file is not a
/// defect in the tree, and there is nothing left to fix.
///
/// This is the ONLY other way out, and it is deliberately narrow: the condition
/// "TestPrune cannot see this file's symbols" is discharged by the file ceasing to
/// exist, not merely by time passing. An entry whose file is still there survives
/// untouched, however old.
///
/// `exists` is a parameter so the rule is testable without a filesystem, and so the
/// production caller names the one predicate it uses.
let internal pruneDeletedUnanalyzable
    (exists: string -> bool)
    (files: Map<string, UnanalyzableFile>)
    : Map<string, UnanalyzableFile> =
    files |> Map.filter (fun _ u -> exists u.File)

/// A red this plugin still OWES the user: a test failure (or a project that produced
/// no verdict at all) that no run COVERING it has passed since.
///
/// The shared `ErrorLedger` is a pure projection of the outstanding list — the
/// `TestsFinished` handler rewrites it wholesale each cycle (`ClearAllErrors` +
/// re-report the whole set) — so `fshw errors` shows exactly what is outstanding:
/// never a superseded red, and never a laundered one.
type OutstandingFailure =
    {
        /// The test project the red belongs to.
        Project: string
        /// The failing test CLASS when the runner named one; `None` for a
        /// project-level red (unparseable failure output, timeout, errored,
        /// deferred). A project-level red is only clearable by a run that executed
        /// the project in FULL.
        Class: string option
        /// Exact failing method from the runner receipt. Filtered reconciliation
        /// requires this same method to appear as passed in a complete CTRF report;
        /// a different passing method in the class cannot contradict this red.
        Method: string option
        /// The ledger key: the class's source file, or the synthetic
        /// `<tests/Project>` bucket when no source file is known.
        File: string
        Entry: ErrorLedger.ErrorEntry
    }

/// Configuration for a test project to run.
type TestConfig =
    {
        Project: string
        Command: string
        Args: string
        Group: string
        Environment: (string * string) list
        /// Template for class-based test filtering. {classes} is replaced with
        /// the joined class names. Example: "-- --filter-class {classes}"
        FilterTemplate: string option
        /// Separator for joining class names in the filter. Default: " "
        /// Example: "|ClassName=" for dotnet test --filter "ClassName=A|ClassName=B"
        ClassJoin: string
        /// Per-project timeout in seconds. None → use top-level default.
        TimeoutSec: int option
        /// How to obtain the structured test report for the verdict. Default
        /// `AutoDetect`. Override via `.fshw.json` `reportVerificationFormat`.
        ReportVerificationFormat: ReportVerificationFormat
        /// Test classes every impact-filtered run of this project includes, whatever
        /// changed (`.fshw.json` `alwaysClasses`). For tests that read the source
        /// tree as data, which no symbol edge leads to. See `AlwaysClasses`.
        AlwaysClasses: string list
        /// `starvation`: what a timeout under measured CPU starvation means for this project
        /// (`Starvation.Policy`). `None`, the default, reports every timeout as it is.
        Starvation: Starvation.Policy option
        /// `checkSkipClasses`: the classes a `check` leaves out when it widens this project
        /// to whole-project for a reason the change did not cause (`CheckSkip`). `None`,
        /// the default, runs every class on every widening.
        CheckSkip: CheckSkip option
        /// `nameHungTests`: ask xUnit for long-running reports (`--long-running` and a
        /// diagnostics `--config-file`), so a host killed at its timeout names the test it was
        /// running. Off by default: xUnit's long-running monitor holds one thread-pool thread
        /// for the whole run, and a diagnostic must not change what a host runs with
        /// (AUTOMATION-1353). A project whose `args` set either switch keeps its own.
        NameHungTests: bool
        /// `weight`: how heavily this project's test host is charged for its process slot,
        /// relative to an undeclared project's 1 (AUTOMATION-1299, ADR-053). A group is
        /// charged the largest weight among its projects.
        Weight: float
    }

/// A test project's configuration as one canonical JSON document: every field, in a fixed
/// order, written out in full (`%A` elides long lists, and a key over an elided list
/// cannot tell two configurations apart). The test-evidence key hashes it.
let internal canonicalTestConfig (c: TestConfig) : string =
    let o = System.Text.Json.Nodes.JsonObject()
    o["project"] <- c.Project
    o["command"] <- c.Command
    o["args"] <- c.Args
    o["group"] <- c.Group
    let environment = System.Text.Json.Nodes.JsonArray()

    for (name, value) in c.Environment do
        environment.Add(
            System.Text.Json.Nodes.JsonArray(
                System.Text.Json.Nodes.JsonValue.Create name,
                System.Text.Json.Nodes.JsonValue.Create value
            )
        )

    o["environment"] <- environment

    o["filterTemplate"] <-
        (c.FilterTemplate
         |> Option.map System.Text.Json.Nodes.JsonValue.Create
         |> Option.toObj)

    o["classJoin"] <- c.ClassJoin

    o["timeoutSec"] <-
        (c.TimeoutSec
         |> Option.map System.Text.Json.Nodes.JsonValue.Create
         |> Option.toObj)

    o["reportVerificationFormat"] <- sprintf "%A" c.ReportVerificationFormat
    // `AlwaysClasses` is deliberately absent: it changes which classes a FILTERED run
    // selects, never what a project's full green means, and only full greens are reused.
    // So is `CheckSkip`: a run that skipped classes is filtered, and is never published.
    // `Weight` is absent too: it decides when a host is admitted, never what it runs or reports.
    // `NameHungTests` changes the host's thread pool, so a green earned under it is a different
    // claim; written only when on, so every key earned without it is unchanged.
    if c.NameHungTests then
        o["nameHungTests"] <- System.Text.Json.Nodes.JsonValue.Create true

    o.ToJsonString()
