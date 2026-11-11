/// A run's filter and exclude arguments, when a filtered run matched no test, and the ledger entries for unanalysable files and failed extensions.
module FsHotWatch.TestPrune.TestFilters

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
open FsHotWatch.TestPrune.OutstandingFailures

/// The ledger diagnostic for a file the symbol analyser could not read. A WARNING keyed
/// to the file itself, so it surfaces in `fshw check` output and — under the default
/// warn-fail policy — denies the check a green verdict. A log line would not: the
/// plugin's status is overwritten by the very next file's `Completed`.
let internal unanalyzableFileDiagnostic (relPath: string) (reason: string) : ErrorLedger.ErrorEntry =
    ErrorLedger.ErrorEntry.warningWithDetail
        $"%s{relPath}: symbol analysis failed — %s{reason}"
        $"TestPrune could not extract symbols from this file, so it is INVISIBLE to the impact graph: a change to it \
           has no symbols to diff and would select no tests on its own. Every test project is being run in full for \
           this cycle (safe over-selection) until the file analyses cleanly. Fix the reported parse/check error — a \
           misplaced `///` doc comment (FS3520) is the usual cause."

/// The ledger key an extension's refresh failure is reported under. Not a file: the
/// `<...>` form other plugins use for a non-file source, so it can never collide with one.
let internal extensionLedgerKey (extensionName: string) : string = $"<extension:%s{extensionName}>"

/// The ledger diagnostic for an extension whose `AnalyzeEdges` threw. An ERROR, not a log
/// line: TestPrune keeps the extension's previously stored edges, which describe an older
/// tree, so selection over them can silently miss a test the current tree couples in.
let internal extensionFailedDiagnostic (extensionName: string) (reason: string) : ErrorLedger.ErrorEntry =
    ErrorLedger.ErrorEntry.errorWithDetail
        $"test-impact extension '%s{extensionName}' failed: %s{reason}"
        "The extension could not compute its dependency edges for the current tree, so the edges it stored on its \
         last successful refresh describe an older one. Until it answers, every test project runs in full (safe \
         over-selection) rather than trusting a selection made over those edges. It is retried on the next flush; \
         this entry clears once it answers."

/// Build the filter arg string for a config given affected classes. Each class name is
/// quoted with `ProcessHelper.quoteArg` before the join, so a name containing spaces
/// (a backticked sentence-style test module) reaches the runner as ONE argument instead
/// of word-splitting into several that match nothing. An unspaced
/// name is unchanged.
let internal buildFilterArgs (config: TestConfig) (classesByProject: Map<string, string list>) : string option =
    let classes =
        classesByProject |> Map.tryFind config.Project |> Option.defaultValue []

    match classes, config.FilterTemplate with
    | [], _ -> None
    | _, None ->
        Logging.debug "test-prune" $"No filterTemplate configured — running all tests for %s{config.Project}"
        None
    | classes, Some template ->
        let joined =
            classes |> List.map ProcessHelper.quoteArg |> String.concat config.ClassJoin

        let result = template.Replace("{classes}", joined)
        Logging.info "test-prune" $"Filter: %s{result}"
        Some result

/// The exclude-filter arg string for a project a `check` runs whole without `excluded`
/// (`checkSkipClasses`), through its `excludeFilterTemplate`. Quoted and joined exactly as
/// `buildFilterArgs` does. `None` when it leaves out nothing.
let internal buildExcludeArgs (config: TestConfig) (excluded: string list) : string option =
    match excluded, config.CheckSkip with
    | [], _
    | _, None -> None
    | classes, Some skip ->
        let joined =
            classes |> List.map ProcessHelper.quoteArg |> String.concat config.ClassJoin

        let result = skip.ExcludeTemplate.Replace("{classes}", joined)
        Logging.info "test-prune" $"Exclude filter (checkSkipClasses): %s{result}"
        Some result

/// Microsoft.Testing.Platform exit code for "no tests matched / zero tests ran".
[<Literal>]
let internal ZeroTestsExitCode = 8

/// MTP's red run summary: "Test run summary: Failed!", or a `failed: N` count above
/// zero on a line of its own.
let private failedSummaryLine =
    Text.RegularExpressions.Regex(
        @"Test run summary:\s*Failed!|^\s*failed:\s*[1-9]",
        Text.RegularExpressions.RegexOptions.Multiline
        ||| Text.RegularExpressions.RegexOptions.IgnoreCase
    )

/// True when a test host's output reports at least one failed test in its summary.
let internal outputReportsFailure (text: string) : bool = failedSummaryLine.IsMatch text

/// True when a *filtered* run matched no tests in this project. An explicit
/// `--filter-*` passthrough (run-tests / test-rerun) is fanned out to EVERY test
/// project; a project that has no test matching the filter runs zero tests and
/// the runner exits non-zero (MTP uses exit code 8, `ZeroTests`). That is NOT a
/// test failure — the tests simply don't exist for this filter — so it must be
/// treated like an impact-skip (passed/filtered, contributing no coverage),
/// exactly as a template-filtered project with no affected classes already is.
///
/// Gated on `wasFiltered`: an UNFILTERED project that runs zero tests is a real
/// problem (misconfigured runner, empty suite) and must still surface, so this
/// returns false for it. Detection is structural (the canonical exit code) with
/// a text fallback for runners that exit non-zero without emitting code 8 but
/// still print MTP's zero-tests summary line.
///
/// The text fallback never outranks a reported failure (AUTOMATION-1569): a failing
/// test whose assertion message contains the phrase (Unquote prints the quoted
/// expression) prints "Zero tests ran" too, but its summary says the run failed, so
/// tests matched and one of them is red. A red CTRF report outranks both signals; see
/// `FailureReports.isZeroMatchRun`.
let internal isZeroTestsUnderFilter (wasFiltered: bool) (outcome: ProcessOutcome) : bool =
    wasFiltered
    && match outcome with
       | ProcessOutcome.Failed(code, _) when code = ZeroTestsExitCode -> true
       | ProcessOutcome.Failed(_, output) ->
           // A text SEARCH for a marker: a capture cut short by an unfinished drain
           // can only cost us the hit (falling back to the exit code above), never
           // invent one. Sound to search the untagged text.
           let text = ProcessOutput.text output

           text.Contains("Zero tests ran", StringComparison.OrdinalIgnoreCase)
           && not (outputReportsFailure text)
       | _ -> false
