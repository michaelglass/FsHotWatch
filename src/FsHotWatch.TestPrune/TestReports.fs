/// Reading what a run reported: failed and passed tests from console output and CTRF reports, and the failures of a run.
module FsHotWatch.TestPrune.TestReports

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
open FsHotWatch.TestPrune.TestFilters
open FsHotWatch.TestPrune.FailureReports
open FsHotWatch.TestPrune.RunnerArgs

/// Split a runner's fully-qualified test name into (class, method) against `classes`,
/// the test classes the runner's report declares and the index stores.
///
/// The runner prints compiled names, and a backticked method or class keeps every `.`
/// written between its backticks (`Ns.Tests+Parsing.parses v1.5 input`), so where the
/// class ends is not visible in the name alone. The class is therefore the LONGEST
/// prefix ending before a `.` that is one of `classes`; the method is the rest, theory
/// arguments included. A name no known class prefixes falls back to the last dotted
/// segment as the method and the COMPLETE prefix as the class. A name with no dot is its
/// own class. Keeping the full prefix is load-bearing: launch selections use fully
/// qualified class names, so truncating `A.B.Tests.method` to `Tests` makes a test run
/// and pass while its retained red remains permanently "not covered".
///
/// ONE derivation, used by BOTH sides of the ledger: the class a red is FILED under
/// (`parseFailedTestsAgainst`, off the runner's `failed <name>` console lines, and the
/// CTRF rows that name a red the console did not) and the class a run's report
/// VINDICATES (`passedClassesOfReport`, off the CTRF `name` field) — and the class a
/// recall sample asks the retained selection about (`failedTestsOfReport`). All of them
/// join the index's classes, so all of them split here.
let internal splitTestName (classes: Set<string>) (name: string) : string * string =
    let rec declared (before: int) =
        match name.LastIndexOf('.', before) with
        | separator when separator > 0 ->
            if Set.contains (name.Substring(0, separator)) classes then
                Some separator
            else
                declared (separator - 1)
        | _ -> None

    let separator =
        if Set.isEmpty classes || name.Length < 2 then
            None
        else
            declared (name.Length - 2)
        |> Option.defaultWith (fun () -> name.LastIndexOf('.'))

    if separator > 0 && separator < name.Length - 1 then
        name.Substring(0, separator), name.Substring(separator + 1)
    else
        name, name

/// Parse "failed Namespace.Class.Method (Xms)" lines from test output, colour removed —
/// `failed (canceled) <name> (Xms)` and a multi-unit duration `(10s 112ms)` included.
/// Returns (className, methodName, fullLine) tuples; `fullLine` is the line as words.
/// Each name is split against `classes` (`splitTestName`).
/// Only a line in the runner's result shape is read (`isFailedTestLine`): prose that opens
/// with `failed` names no test.
let internal parseFailedTestsAgainst (classes: Set<string>) (output: string) : (string * string * string) list =
    ConsoleText.lines output
    |> Array.choose (fun line ->
        let trimmed = line.Trim()

        if isFailedTestLine trimmed then
            // Strip the "failed " prefix, a parenthesised qualifier such as
            // "(canceled)" that MTP puts before the name, and the trailing duration.
            let rest =
                let afterVerb = trimmed.Substring(7).Trim()

                if afterVerb.StartsWith("(") then
                    match afterVerb.IndexOf(')') with
                    | -1 -> afterVerb
                    | close -> afterVerb.Substring(close + 1).Trim()
                else
                    afterVerb

            let name =
                match rest.LastIndexOf(" (") with
                | -1 -> rest
                | i -> rest.Substring(0, i)

            let className, methodName = splitTestName classes name
            Some(className, methodName, trimmed)
        else
            None)
    |> Array.toList

/// `parseFailedTestsAgainst` with no known classes: each name splits at its last `.`.
let parseFailedTests (output: string) : (string * string * string) list =
    parseFailedTestsAgainst Set.empty output

/// The classes ONE project's CTRF report proves RAN AND PASSED in this run
/// — the receipt `RunCoverage.ofRun` reads for a raw `--filter`
/// passthrough, whose launch REQUEST claims nothing.
///
/// A class is claimed only when the report holds at least one PASSED test for it and
/// NO failed/other test for it. Everything else fails CLOSED — an empty set, which
/// leaves every red exactly as it was:
///
///   * no parseable SUMMARY block → the report was truncated or never flushed;
///   * per-test array shorter (or longer) than the summary's total → the array is
///     INCOMPLETE. A real report omits per-test entries for tests that threw a raw
///     (non-assertion) exception while still counting them in the summary, so a class
///     could look all-green here while one of its tests exploded. Counting is the only
///     way to see the omission, so an array that does not account for every test in the
///     summary is not evidence about ANY class in it;
///   * a class with a failed/other entry, or with no passed entry at all (all skipped)
///     → not claimed.
///
/// Skips are neutral rather than disqualifying, because the unfiltered arm they must
/// agree with is: a full run whose report contains skips still returns
/// `CoveredWholeProject` and clears everything. A rule that let a skip block a class
/// here would be stricter than the whole-project path it is a refinement of.
let internal passedTestsOfReport (json: string) : Set<string * string> =
    match Ctrf.trySummary json with
    | None -> Set.empty
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        if records.IsEmpty || records.Length <> summary.Total then
            Set.empty
        else
            let classes = Flakiness.parseCtrfDeclaredClasses json

            records
            |> List.groupBy (fun r -> splitTestName classes r.Name)
            |> List.choose (fun (testIdentity, forTest) ->
                let anyPassed = forTest |> List.exists (fun r -> r.Outcome = Flakiness.Passed)

                let anyUnvindicated =
                    forTest
                    |> List.exists (fun r ->
                        match r.Outcome with
                        | Flakiness.Failed
                        | Flakiness.Other -> true
                        | Flakiness.Passed
                        | Flakiness.Skipped -> false)

                if anyPassed && not anyUnvindicated then
                    Some testIdentity
                else
                    None)
            |> Set.ofList

let internal passedClassesOfReport (json: string) : Set<string> =
    match Ctrf.trySummary json with
    | None -> Set.empty
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        if records.IsEmpty || records.Length <> summary.Total then
            Set.empty
        else
            let classes = Flakiness.parseCtrfDeclaredClasses json

            records
            |> List.groupBy (fun r -> fst (splitTestName classes r.Name))
            |> List.choose (fun (cls, forClass) ->
                let anyPassed = forClass |> List.exists (fun r -> r.Outcome = Flakiness.Passed)

                let anyUnvindicated =
                    forClass
                    |> List.exists (fun r ->
                        match r.Outcome with
                        | Flakiness.Failed
                        | Flakiness.Other -> true
                        | Flakiness.Passed
                        | Flakiness.Skipped -> false)

                if anyPassed && not anyUnvindicated then Some cls else None)
            |> Set.ofList

/// Every class ONE project's CTRF report has a row for, in any outcome — skipped
/// included — when the report is complete: a parseable summary and a per-test array that
/// accounts for every test in it. `None` otherwise, because an incomplete report cannot
/// say a class is ABSENT, only that some rows are missing.
let internal classesRunOfReport (json: string) : Set<string> option =
    match Ctrf.trySummary json with
    | None -> None
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        if records.IsEmpty || records.Length <> summary.Total then
            None
        else
            let classes = Flakiness.parseCtrfDeclaredClasses json

            records
            |> List.map (fun r -> fst (splitTestName classes r.Name))
            |> Set.ofList
            |> Some

/// Per project that PASSED in this run, the classes its complete CTRF report ran
/// (`classesRunOfReport`). A project that failed, timed out, matched nothing, or wrote
/// no complete report is absent: nothing is known about which classes it has.
let internal classesRunOfPassedProjects
    (repoRoot: string)
    (runId: Guid)
    (results: Map<string, TestResult>)
    : Map<string, Set<string>> =
    Ctrf.reportsForRun repoRoot runId
    |> List.choose (fun report ->
        match Map.tryFind report.Project results with
        | Some(TestsPassed _) ->
            try
                classesRunOfReport (File.ReadAllText report.Path)
                |> Option.map (fun classes -> report.Project, classes)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None
        | _ -> None)
    |> Map.ofList

/// The per-project passed-class evidence for a completed run, read back from the CTRF
/// reports THAT RUN wrote (`.fshw/test-runs/<runId>/<Project>.ctrf.json`).
///
/// Scoped by RUN DIRECTORY, so a previous run's report can never be mistaken for this
/// one's: the directory IS the run. A project with no readable report contributes no
/// entry, and a project absent from the map claims nothing — the absence of evidence is
/// never evidence of a pass.
let internal passedClassesOfRun (repoRoot: string) (runId: Guid) : Map<string, Set<string>> =
    Ctrf.reportsForRun repoRoot runId
    |> List.choose (fun report ->
        let json =
            try
                Some(File.ReadAllText report.Path)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None

        match json |> Option.map passedClassesOfReport with
        | Some classes when not (Set.isEmpty classes) -> Some(report.Project, classes)
        | _ -> None)
    |> Map.ofList

let internal passedTestsOfRun (repoRoot: string) (runId: Guid) : Map<string, Set<string * string>> =
    Ctrf.reportsForRun repoRoot runId
    |> List.choose (fun report ->
        let json =
            try
                Some(File.ReadAllText report.Path)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None

        match json |> Option.map passedTestsOfReport with
        | Some tests when not (Set.isEmpty tests) -> Some(report.Project, tests)
        | _ -> None)
    |> Map.ofList

/// Exact failed rows from a COMPLETE CTRF report. The summary's failed count is the
/// denominator authority; if the per-test array omits even one failure, this refuses
/// the sample rather than shrinking recall's denominator to the rows that happened to
/// parse.
let internal failedTestsOfReport (json: string) : (string * string) list option =
    match Ctrf.trySummary json with
    | None -> None
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        let failed =
            records |> List.filter (fun record -> record.Outcome = Flakiness.Failed)

        if records.Length <> summary.Total || failed.Length <> summary.Failed then
            None
        else
            let classes = Flakiness.parseCtrfDeclaredClasses json
            failed |> List.map (fun record -> splitTestName classes record.Name) |> Some

/// Every failed row of a CTRF report, by fully-qualified name, whether or not the rows
/// reconcile to the summary. Naming is not counting: a report that omitted a raw-throw
/// row still names the assertions that failed, and each name is a red the console did
/// not spell out.
let internal failedRowsOfReport (json: string) : string list =
    Flakiness.parseCtrfTests json
    |> List.filter (fun record -> record.Outcome = Flakiness.Failed)
    |> List.map (fun record -> record.Name)

/// What one project's CTRF report contributes to filing its reds: the failed rows it
/// names, and the classes it declares. A red is split against those classes whether the
/// console or the report named it, so it is filed under the class the report's passing
/// rows later vindicate.
type internal ReportedFailures =
    {
        Names: string list
        /// The rows reconcile to the summary: every failed test is named, so the report,
        /// not the console, says which tests failed.
        Complete: bool
        Classes: Set<string>
    }

module internal ReportedFailures =
    /// No report: the console is all a red can be named from.
    let none =
        { Names = []
          Complete = false
          Classes = Set.empty }

    let ofReport (json: string) =
        { Names = failedRowsOfReport json
          Complete = (failedTestsOfReport json).IsSome
          Classes = Flakiness.parseCtrfDeclaredClasses json }

let internal sharedInfrastructureFailureOfReport (json: string) : string option =
    try
        use document = JsonDocument.Parse json
        let mutable results = Unchecked.defaultof<JsonElement>
        let mutable tests = Unchecked.defaultof<JsonElement>

        if
            document.RootElement.TryGetProperty("results", &results)
            && results.TryGetProperty("tests", &tests)
            && tests.ValueKind = JsonValueKind.Array
        then
            let failedRows =
                tests.EnumerateArray()
                |> Seq.filter (fun row ->
                    let mutable status = Unchecked.defaultof<JsonElement>
                    row.TryGetProperty("status", &status) && status.GetString() = "failed")
                |> Seq.toList

            let rec stringDescendants (element: JsonElement) =
                seq {
                    match element.ValueKind with
                    | JsonValueKind.String -> yield element.GetString()
                    | JsonValueKind.Object ->
                        for property in element.EnumerateObject() do
                            yield! stringDescendants property.Value
                    | JsonValueKind.Array ->
                        for item in element.EnumerateArray() do
                            yield! stringDescendants item
                    | _ -> ()
                }

            let exceptionTexts (row: JsonElement) =
                seq {
                    for property in row.EnumerateObject() do
                        if
                            property.Name.Equals("trace", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Equals("stack", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Equals("exception", StringComparison.OrdinalIgnoreCase)
                        then
                            yield! stringDescendants property.Value
                }

            let containsTypeIdentity (identity: string) (text: string) =
                let isIdentifierCharacter c =
                    Char.IsLetterOrDigit c || c = '_' || c = '.' || c = '`'

                let rec search start =
                    let index = text.IndexOf(identity, start, StringComparison.Ordinal)

                    if index < 0 then
                        false
                    else
                        let beforeIsBoundary = index = 0 || not (isIdentifierCharacter text[index - 1])
                        let after = index + identity.Length
                        let afterIsBoundary = after = text.Length || not (isIdentifierCharacter text[after])

                        if beforeIsBoundary && afterIsBoundary then
                            true
                        else
                            search (index + 1)

                search 0

            let causesOf (row: JsonElement) =
                exceptionTexts row
                |> Seq.collect (fun text ->
                    [ if containsTypeIdentity "Npgsql.NpgsqlException" text then
                          "NpgsqlException"
                      if containsTypeIdentity "System.Net.Sockets.SocketException" text then
                          "SocketException" ])
                |> Seq.toList

            match failedRows with
            | [] -> None
            | rows ->
                match rows |> List.collect causesOf |> List.distinct with
                | [] -> None
                | causes -> Some(String.concat "/" causes)
        else
            None
    with
    | :? JsonException
    | :? InvalidOperationException -> None

let internal failedTestsOfRun
    (repoRoot: string)
    (runId: Guid)
    (results: TestResults)
    : Result<OutstandingFailure list, string> =
    let reports =
        Ctrf.reportsForRun repoRoot runId
        |> List.map (fun report -> report.Project, report)
        |> Map.ofList

    results.Results
    |> Map.toList
    |> List.fold
        (fun state (project, result) ->
            state
            |> Result.bind (fun accumulated ->
                if TestResult.isTimedOut result then
                    Error $"%s{project} timed out, so no complete failing-test denominator exists"
                else
                    match result, Map.tryFind project reports with
                    | TestsErrored reason, _ ->
                        Error $"%s{project} did not produce an executable test result: %s{reason}"
                    | TestsDeferred reason, _ -> Error $"%s{project} tests were deferred: %s{reason}"
                    | TestsNoMatch _, _ -> Error $"%s{project} executed no tests"
                    | TestsPassed _, _ -> Ok accumulated
                    | (TestsFailed _ | TestsTimedOut _), None ->
                        Error $"%s{project} failed but wrote no current-run CTRF report"
                    | _, Some report ->
                        try
                            let json = File.ReadAllText report.Path

                            match sharedInfrastructureFailureOfReport json, failedTestsOfReport json with
                            | Some cause, _ -> Error $"%s{project} failed through shared infrastructure (%s{cause})"
                            | None, None -> Error $"%s{project}'s CTRF failed rows do not reconcile to its summary"
                            | None, Some identities ->
                                identities
                                |> List.map (fun (className, methodName) ->
                                    { Project = project
                                      Class = Some className
                                      Method = Some methodName
                                      File = report.Path
                                      Entry = ErrorLedger.ErrorEntry.error "observed full-run failure" })
                                |> List.append accumulated
                                |> Ok
                        with
                        | :? IOException
                        | :? UnauthorizedAccessException -> Error $"%s{project}'s CTRF report could not be read"))
        (Ok [])

/// The reds a completed run FOUND, as outstanding failures. Pure:
/// the same run always yields the same set, so the ledger projection is a function
/// of the evidence and nothing else.
///
/// A red is attributed to a CLASS only when the runner named one AND the result is a
/// genuine test failure. A TIMEOUT is deliberately project-level (`Class = None`)
/// even when its output names tests: a project killed for being stuck is a fact
/// about the PROJECT, and a later class-filtered green must not be allowed to
/// vindicate it. Deferred/errored projects are project-level for the same reason —
/// nothing about them was verified.
///
/// `runLogOf` names the run log a project's output was streamed to: a
/// project-level red carries the head of its output AND the path, so the verdict says
/// what the tool knows and where the rest is.
let internal failuresOf
    (runLogOf: string -> RunLog.Ref)
    (reportOf: string -> ReportedFailures)
    (classFiles: Map<string, string>)
    (results: TestResults)
    : OutstandingFailure list =
    let synthetic (project: string) = $"<tests/%s{project}>"

    results.Results
    |> Map.toList
    |> List.collect (fun (project, result) ->
        let projectLevel (entry: ErrorLedger.ErrorEntry) =
            { Project = project
              Class = None
              Method = None
              File = synthetic project
              Entry = entry }

        match result with
        | TestsFailed(output, _, _)
        | TestsTimedOut(output, _, _, _) ->
            // A timeout is never attributable to one class (see above).
            let isTimeout = TestResult.isTimedOut result

            // A complete CTRF report names the reds. Without one the console does, and
            // when the console names none the report's failed rows still do. The report
            // row is the same fully-qualified name the console prints, so a red filed from it
            // is retired by the same passing row. Either way the name splits against the
            // classes the report declares and the index stores.
            let report = reportOf project

            let classes = classFiles |> Map.keys |> Set.ofSeq |> Set.union report.Classes

            let parsed =
                let fromConsole = parseFailedTestsAgainst classes output

                let ofReportRow (name: string) =
                    let className, methodName = splitTestName classes name

                    let line =
                        fromConsole
                        |> List.tryPick (fun (c, m, consoleLine) ->
                            if c = className && m = methodName then
                                Some consoleLine
                            else
                                None)
                        |> Option.defaultValue
                            $"failed %s{name} — named by the run's CTRF report; the console printed no line the parser recognised"

                    className, methodName, line

                // A complete report is the runner's own record of what failed, so the
                // console cannot add a name to it: anything else there that merely looks
                // like a result line is prose. A timeout's report is not complete by
                // construction of the run, and its reds stay the console's.
                if report.Complete && not isTimeout then
                    report.Names |> List.map ofReportRow
                else
                    match fromConsole with
                    | [] -> report.Names |> List.map ofReportRow
                    | named -> named

            if parsed.IsEmpty then
                // ONE entry for the project, so the whole captured output is carried
                // ONCE. This is the only arm where that text is the entry's own
                // subject: no test was named, so the run itself is what the reader has
                // to go on.
                //
                // The MESSAGE carries the head of that output and the
                // log path — the verdict records only `Message`, and "Tests failed in X"
                // with the cause hidden in a `Detail` no surface prints sent a reader to
                // reap daemons over a shard-pool refusal stated on line 1.
                [ projectLevel (
                      ErrorLedger.ErrorEntry.errorWithDetail
                          (FailureCause.render (
                              FailureCause.ofOutput (RedRunEnding.ofResult result) project (runLogOf project) output
                          ))
                          output
                  ) ]
            else
                parsed
                |> List.map (fun (className, methodName, line) ->
                    let file =
                        classFiles |> Map.tryFind className |> Option.defaultValue (synthetic project)

                    { Project = project
                      Class = (if isTimeout then None else Some className)
                      Method = (if isTimeout then None else Some methodName)
                      File = file
                      // The failing LINE, and no detail.
                      //
                      // This used to attach `output` — the whole captured project run —
                      // to every parsed failure, which made the ledger's size the
                      // PRODUCT of two unbounded numbers rather than their sum. 753
                      // failing tests against a 48 MB capture is 36 GB once a mirror of
                      // the ledger writes each entry's copy out, and the merge gate died
                      // building exactly that reply after the daemon had already
                      // finished the run.
                      //
                      // Nothing is lost by dropping it, because it was never this
                      // entry's fact: it was the same project-wide string repeated per
                      // test, no renderer has ever printed a ledger `Detail`, and the
                      // untruncated capture is on disk at
                      // `.fshw/test-runs/<runId>/<project>.output.log`. The transport
                      // bound in `ErrorLedger.Transport` still stands behind this — a
                      // plugin must not be able to do it again — but the bound is a
                      // backstop, and this is the defect.
                      //
                      // A killed run's line keeps the timeout: the verdict records only
                      // `Message`, so a reader would otherwise see test failures from a
                      // project whose host was killed.
                      Entry =
                        ErrorLedger.ErrorEntry.error (
                            match RedRunEnding.ofResult result with
                            | RedRunEnding.Exited -> line
                            | RedRunEnding.TimedOut _ as ending ->
                                $"%s{project}: %s{RedRunEnding.describe ending}; before the kill it reported: %s{line}"
                        ) })
        | TestsDeferred reason ->
            // NOT a test failure — surface an honest "waiting on build / did not
            // run" diagnostic at `Deferred` severity so the verdict is NON-green
            // (nothing was verified) yet NOT a red: the CLI routes any
            // `Deferred`-severity entry to `Incomplete`/exit 2, distinct from the
            // exit 1 a real failure earns. This entry still joins the Outstanding
            // failure list, so cache participation stays refused (a deferred run is
            // never replayed as a green) — the severity governs the VERDICT, the
            // outstanding LIST governs the CACHE, and the two are decoupled.
            // The DETAIL used to assert one cause for every defer: "its build artifact
            // (apphost) was not produced … a build-ordering issue". That is false for a
            // stale-artifact refusal, where the artifact WAS produced and holds bytes
            // that do not match its sources — and it is false in the expensive
            // direction, because a build-ordering race settles on its own while stale
            // output does not. A reader told to wait it out re-runs and gets the
            // identical refusal, which is the defect the stale-artifact preflight exists to delete.
            // So the detail is derived from the reason, not asserted over it.
            let detail =
                if StaleArtifactPreflight.isStaleOutputDeferral reason then
                    $"The %s{project} test project did not run because its build OUTPUT is stale: the artifact \
                      exists, but its bytes do not match the sources it was built from, so running the suite would \
                      test code you did not write. This is NOT a build-ordering race and re-running will not settle \
                      it — the reason above names the file and the command that repairs it."
                else
                    $"The %s{project} test project did not run because its build artifact (apphost) was not \
                      produced. Tests were NOT executed, so this cycle cannot be reported as passing. This is a \
                      build-ordering issue, not a test failure."

            [ projectLevel (
                  ErrorLedger.ErrorEntry.deferredWithDetail $"%s{project}: waiting on build — %s{reason}" detail
              ) ]
        | TestsErrored reason ->
            // NOT a test failure (no test was shown to fail) and NOT a pass
            // (nothing was verified) — an honest "aborted" diagnostic so the
            // verdict is non-green without the misleading "Tests failed in X".
            //
            // It is reported at `HostAborted` SEVERITY, which is what makes that honesty
            // reach the verdict. At `Error` it was counted by `failingDiagnostics`,
            // and every surface downstream — the exit code, `verdict.json`'s outcome,
            // the terminal — then said "failures found" about a run in which nothing
            // failed. The severity routes it to `CheckOutcome.RunnerAborted`/exit 2
            // instead, exactly as `Deferred` routes a defer to `WaitingOnBuild`.
            //
            // It still joins the OUTSTANDING list, so cache participation stays
            // refused and an abort is never replayed as a green: the severity governs
            // the VERDICT, the outstanding list governs the CACHE.
            [ projectLevel (
                  ErrorLedger.ErrorEntry.abortedWithDetail
                      $"%s{project}: aborted — %s{reason}"
                      $"The %s{project} test host did not finish, so NO pass/fail verdict could be derived — nothing was verified. This is NOT a reported test failure and NOT a pass. Any per-test lines in the captured output are a TRANSCRIPT of a killed run, not findings: a test the host never reached is written out the same way as one that ran, which is why an abort must never be counted as failures. Re-run on a machine with headroom (e.g. `dotnet fshw test-rerun`). A run that only goes green on retry is itself a real failure, so this stays non-green."
              ) ]
        // No ledger entry, same as a pass. A filter matching nothing in THIS project is
        // not this project's error — the run-level verdict is where a workspace-wide
        // zero match is refused, and duplicating it here would put a
        // red on every project an ordinary impact selection happened not to name.
        | TestsNoMatch _
        | TestsPassed _ -> [])
