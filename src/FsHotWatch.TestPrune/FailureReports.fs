/// Why a run is red, in words a reader can act on: failure causes, console text, failure and abort reports, and how a test host's outcome is classified.
module FsHotWatch.TestPrune.FailureReports

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

/// THE CAUSE OF A FAILED RUN THAT NAMED NO TEST — never empty.
///
/// A run refused by the shard-pool guard ("Test shard pool '…' is already in use by
/// PID 6862 …") printed its cause as line 1 of a 351-byte output. That line reached
/// the daemon log and the run's `.output.log`; the ledger entry — and so the verdict's
/// `reddenedBy`, the surface agents are told to read — said only "Tests failed in X", and
/// a reader of it concluded a wedged daemon and reaped daemons that were fine. The tool
/// knew the cause and told the reader where to look instead of what it knew.
///
/// So the message CARRIES the cause: the HEAD of the captured output (the first
/// `HeadLines` non-blank lines, bounded by `HeadMaxChars`), then the path of the full
/// log. Content first, pointer second. The head, not the tail, because the head is where
/// a killed, wedged or refused run states its cause — the same claim the daemon-log
/// sentence had always made while printing the tail.
///
/// And it is a TYPE with a private constructor: the only two builders each yield a
/// sentence, so a blank output cannot produce a blank message — it produces
/// `unknownPointing`, which SAYS nothing was captured. "Unexplained" and "explained
/// elsewhere" are different facts, and an empty string states neither.
type FailureCause = private FailureCause of string

/// How a red run ENDED, as far as its failure message is concerned. A host killed at
/// its timeout may already have printed a complete runner summary ("Passed!", total,
/// failed 0) — the summary is then a transcript of tests that finished before the kill,
/// not proof the run did. Every message builder takes this, so none can infer
/// "completed" from the text alone.
[<RequireQualifiedAccess>]
type RedRunEnding =
    /// The host exited on its own, red.
    | Exited
    /// The host was killed at its timeout.
    | TimedOut of after: System.TimeSpan

module RedRunEnding =
    /// The ending of a red `TestResult`: `TimedOut` for a timeout, `Exited` otherwise.
    let ofResult (result: TestResult) : RedRunEnding =
        match result with
        | TestsTimedOut(_, after, _, _) -> RedRunEnding.TimedOut after
        | _ -> RedRunEnding.Exited

    /// What happened, as the subject of a sentence: "run failed" or "run timed out
    /// after Ns and its host was killed".
    let describe (ending: RedRunEnding) : string =
        match ending with
        | RedRunEnding.Exited -> "run failed"
        | RedRunEnding.TimedOut after -> $"run timed out after %d{int after.TotalSeconds}s and its host was killed"

/// The runner's console output as words. A terminal-aware runner (MTP colours `failed`
/// red and a duration grey) prints a failing line whose BYTES start with an escape
/// sequence, not with `failed`; every matcher below reads the line with the colour
/// removed, so what it matches is what a reader sees.
module ConsoleText =
    let private ansiSequence =
        System.Text.RegularExpressions.Regex(
            @"\u001b\[[0-9;?]*[ -/]*[@-~]",
            System.Text.RegularExpressions.RegexOptions.Compiled
        )

    /// `text` with every ANSI CSI sequence (colour, cursor movement, erase) removed.
    let stripAnsi (text: string) : string =
        if isNull text then "" else ansiSequence.Replace(text, "")

    /// The output's lines, colour removed, `\r` dropped.
    let lines (output: string) : string[] =
        (stripAnsi output).Split('\n') |> Array.map (fun l -> l.TrimEnd('\r'))

    /// The runner's summary block — its `Test run summary:` line and the count lines
    /// under it — when the output has one. A run that printed it ran to completion;
    /// a run killed, wedged or refused never reaches it.
    let summaryOf (output: string) : string list =
        let all = lines output

        match
            all
            |> Array.tryFindIndexBack (fun l -> l.TrimStart().StartsWith("Test run summary:"))
        with
        | None -> []
        | Some index ->
            let isCount (l: string) =
                let t = l.TrimStart()

                [ "total:"; "failed:"; "succeeded:"; "skipped:"; "duration:" ]
                |> List.exists t.StartsWith

            all.[index].Trim()
            :: (all.[index + 1 ..]
                |> Array.takeWhile isCount
                |> Array.map (fun l -> l.Trim())
                |> Array.toList)

module FailureCause =
    /// How many non-blank lines of the head are quoted. Twenty: an MTP runner's banner
    /// (version, discovery, the first migrations) is under ten lines, so a cause stated
    /// at the start of the run is inside the excerpt with room to spare, while a verdict
    /// listing `MaxRedCauses` such entries stays a page, not a log.
    [<Literal>]
    let HeadLines = 20

    /// The byte bound on the head, so twenty lines of a verbose runner cannot turn the
    /// verdict file into the run log. 4 KB × ten causes is the most `reddenedBy` grows.
    [<Literal>]
    let HeadMaxChars = 4096

    /// The first `HeadLines` non-blank lines of `output`, trimmed, cut to `HeadMaxChars`
    /// in total. Empty only when the output has no non-blank line.
    let headOf (output: string) : string list =
        let lines =
            (if isNull output then "" else output).Split('\n')
            |> Array.map (fun l -> l.TrimEnd('\r').TrimEnd())
            |> Array.filter (fun l -> not (System.String.IsNullOrWhiteSpace l))
            |> Array.truncate HeadLines
            |> Array.toList

        // Spend the character budget line by line (one char per line for the newline);
        // the line that crosses it is cut and marked, and nothing follows it.
        let rec take (budget: int) (acc: string list) (rest: string list) =
            match rest with
            | [] -> List.rev acc
            | l :: tail when l.Length + 1 <= budget -> take (budget - l.Length - 1) (l :: acc) tail
            | l :: _ when budget > 1 -> List.rev ((l.Substring(0, budget - 1) + "…") :: acc)
            | _ -> List.rev acc

        take HeadMaxChars [] lines

    /// Where the full output is, or why it is nowhere. ONE rendering of the `RunLog.Ref`,
    /// so both builders point the same way.
    let private pointer (runLog: RunLog.Ref) : string =
        match runLog with
        | RunLog.Ref.Written path -> $"see %s{path}"
        | RunLog.Ref.Unavailable reason -> $"no output log was saved (%s{reason})"

    /// The message when the run produced NOTHING to quote. Says so explicitly, and
    /// points at the log (or at the reason there is none).
    let private unknownEnded (ending: RedRunEnding) (project: string) (runLog: RunLog.Ref) : FailureCause =
        FailureCause
            $"%s{project}: %s{RedRunEnding.describe ending}, no per-test 'failed' line was parsed, and no cause \
              captured — the runner produced no output to quote; %s{pointer runLog}"

    let unknownPointing (project: string) (runLog: RunLog.Ref) : FailureCause =
        unknownEnded RedRunEnding.Exited project runLog

    /// Where the full output is, for a message that has just quoted part of it.
    let private fullOutput (runLog: RunLog.Ref) : string =
        match runLog with
        | RunLog.Ref.Written path -> $"full output: %s{path}"
        | RunLog.Ref.Unavailable reason ->
            $"full output was NOT saved (%s{reason}); the lines above are all that was kept"

    /// The message for a run whose output named no failing test, and whose CTRF report
    /// named none either. A run that EXITED after printing its summary ran to
    /// completion, so the summary is quoted: the head of such a run is the daemon's
    /// banner and says nothing about the red. A run that timed out did not complete
    /// whatever it printed — its summary is quoted as what it printed before the kill.
    /// A run with no summary was killed, wedged or refused, and the head is where it
    /// stated its cause. Routes a blank output to `unknownPointing`'s sentence, so this
    /// is the only door from captured text and cannot yield an empty message.
    let ofOutput (ending: RedRunEnding) (project: string) (runLog: RunLog.Ref) (output: string) : FailureCause =
        let quote (excerpt: string list) =
            excerpt |> List.map (fun l -> "  | " + l) |> String.concat "\n"

        match ConsoleText.summaryOf output, ending with
        | (_ :: _ as summary), RedRunEnding.Exited ->
            FailureCause
                $"%s{project}: run failed and ran to completion, but neither the runner's console nor its CTRF report named a failing test. The runner's summary:\n%s{quote summary}\n%s{fullOutput runLog}"
        | (_ :: _ as summary), RedRunEnding.TimedOut _ ->
            FailureCause
                $"%s{project}: %s{RedRunEnding.describe ending}. It printed a runner summary before the kill, which does not make the run complete; the red is the timeout. The summary it printed:\n%s{quote summary}\n%s{fullOutput runLog}"
        | [], _ ->
            match headOf output with
            | [] -> unknownEnded ending project runLog
            | head ->
                FailureCause
                    $"%s{project}: %s{RedRunEnding.describe ending} but no per-test 'failed' line was parsed. The run's output begins (first %d{head.Length} non-blank lines; the head is where a killed, wedged or refused run states its cause):\n%s{quote head}\n%s{fullOutput runLog}"

    /// The sentence. Total; never empty by construction.
    let render (FailureCause s) : string = s

/// The runner's result line for a failing test: `failed <name> (<duration>)`, with an
/// optional parenthesised qualifier before the name (`failed (canceled) <name> (1ms)`)
/// and a duration of one or more units (`(27ms)`, `(10s 112ms)`, `(2m 3s 4ms)`).
let internal failedTestLineShape =
    System.Text.RegularExpressions.Regex(
        @"^failed (\([^)]*\) )?\S.* \((\d+(\.\d+)?(ms|s|m|h|d) ?)+\)$",
        System.Text.RegularExpressions.RegexOptions.Compiled
    )

/// Is `line` the runner reporting a failing test? Only the result-line SHAPE counts.
///
/// The captured output is not the runner's alone: a project's preflight, a gate or a test
/// that prints shares the stream, and prose may open with the word `failed`. A package
/// gate's report once wrapped "…the release confirm / failed twice. Pin the version
/// obj/project.assets.json resolves today, then run": read by its prefix alone, that
/// line filed a red for a test named "twice. Pin the version obj/project.assets", which
/// no run could ever select, so it stayed outstanding forever. The runner always ends a
/// result line with the test's duration; prose does not.
let internal isFailedTestLine (line: string) : bool =
    failedTestLineShape.IsMatch((ConsoleText.stripAnsi line).Trim())

/// Build the human-readable error lines for a FAILED test project run, parsed
/// from the runner's captured `output`. The header line plus the per-test
/// `failed ...` lines plus the MTP summary lines (`total:`/`failed:`/
/// `succeeded:`).
///
/// CRITICAL for CI observability: a red run that only reports "failed: 1" with
/// NO test name is undiagnosable when the on-disk `.fshw/test-runs` log isn't
/// uploaded as an artifact. MTP prints a failing test as a line whose TRIMMED
/// form starts with `failed ` — INCLUDING `failed (canceled) <name> (Nms)` for a
/// test killed by its `[<Fact(Timeout=...)>]` under CI load (the documented
/// daemon-load flake class). We match the trimmed prefix so leading indentation
/// (which varies by MTP version / capture path) never hides the name. As a
/// backstop, when the run failed but NO `failed ` line parsed (a crash, an
/// OOM-kill, or an output shape the matcher doesn't yet recognise), the tail of
/// the captured output is echoed so the failure is ALWAYS visible from the CI
/// console alone — never silently swallowed into "0 test(s) failed".
///
/// The tail is a SUMMARY and stays one. It is also, structurally, the wrong end of
/// the output for a whole class of failure: a suite killed at its timeout printed
/// its cause in the first seconds and its noise for the fifteen minutes since, so
/// forty lines of tail are forty lines of noise. `runLog` is the answer to that —
/// the full, head-included, streamed capture — and this message NAMES it.
///
/// The path comes from a `RunLog.Ref`, never a formatted guess: a path is printed only
/// when something actually opened it, and otherwise the REASON there is no file takes
/// its place. A message that points at a log nobody wrote is worse than none.
///
/// The summary is taken from BOTH ends. The sentence above already said
/// the head is where a killed or wedged run states its cause — and then printed only the
/// tail, so the one thing it claimed to know was the one thing it withheld. A refused run
/// (the shard-pool guard) says everything on line 1; a run killed mid-way says how far it
/// got on the last line. The head is `FailureCause.headOf`, the same excerpt the ledger
/// entry (and so `reddenedBy`) carries, so the daemon log and the verdict agree.
///
/// `ending` says how the run ended: a host killed at its timeout is never called
/// complete, whatever summary it printed first. `ctrfFailedRows` are the failed rows of
/// the run's CTRF report; the report is said to name the tests only when it does.
let internal formatFailureReport
    (ending: RedRunEnding)
    (ctrfFailedRows: string list)
    (projectName: string)
    (runLog: RunLog.Ref)
    (output: string)
    : string list =
    let lines = ConsoleText.lines output

    let failedTests = lines |> Array.filter isFailedTestLine |> Array.toList

    // The runner's own summary lines, by their leading word: a daemon-log line that
    // happens to CONTAIN `failed:` (`P: 0 test(s) failed:`) is not one of them.
    let summaryLines =
        lines
        |> Array.filter (fun l ->
            let t = l.TrimStart()

            t.StartsWith("Test run summary:")
            || t.StartsWith("total:")
            || t.StartsWith("failed:")
            || t.StartsWith("succeeded:"))
        |> Array.toList

    let ctrf =
        match ctrfFailedRows with
        | [] -> "no CTRF report row names a failing test either"
        | rows -> $"the CTRF report beside the output log names the %d{rows.Length} failing test(s)"

    [ match ending with
      | RedRunEnding.Exited -> $"%s{projectName}: %d{failedTests.Length} test(s) failed:"
      | RedRunEnding.TimedOut _ ->
          $"%s{projectName}: %s{RedRunEnding.describe ending}; %d{failedTests.Length} test(s) printed 'failed' before the kill:"
      yield! failedTests |> List.map (fun l -> $"  %s{l.TrimEnd()}")
      yield! summaryLines |> List.map (fun l -> $"  %s{l.TrimEnd()}")
      if
          List.isEmpty failedTests
          && ending = RedRunEnding.Exited
          && not (List.isEmpty (ConsoleText.summaryOf output))
      then
          // The summary above is the runner's own: the run EXITED after it, so its
          // head is a banner and its tail is the summary already printed. A timed-out
          // run never takes this arm: a summary printed before the kill is not a
          // completed run, and its head is where it may have said why it hung.
          match runLog with
          | RunLog.Ref.Written path ->
              $"%s{projectName}: run failed and ran to completion, but no per-test 'failed' line was parsed from \
                its console; %s{ctrf}. Full output: %s{path}"
          | RunLog.Ref.Unavailable reason ->
              $"%s{projectName}: run failed and ran to completion, but no per-test 'failed' line was parsed from \
                its console; %s{ctrf}; and NO output log was saved (%s{reason})"
      elif List.isEmpty failedTests then
          let nonBlank =
              lines |> Array.filter (fun l -> not (System.String.IsNullOrWhiteSpace l))

          let head = FailureCause.headOf output
          let tail = nonBlank.[max head.Length (nonBlank.Length - 40) ..]

          match runLog with
          | RunLog.Ref.Written path ->
              $"%s{projectName}: %s{RedRunEnding.describe ending} but no per-test 'failed' line was parsed. The FULL output was streamed \
                to %s{path}. The first %d{head.Length} of its %d{nonBlank.Length} non-blank lines follow — the \
                HEAD is where a killed, wedged or refused run states its cause:"
          | RunLog.Ref.Unavailable reason ->
              $"%s{projectName}: %s{RedRunEnding.describe ending} but no per-test 'failed' line was parsed, and NO output log was saved \
                (%s{reason}) — so the lines below are ALL there is. The first %d{head.Length} of %d{nonBlank.Length} \
                non-blank lines — the HEAD is where a killed, wedged or refused run states its cause:"

          yield! head |> List.map (fun l -> $"  | %s{l}")

          if not (Array.isEmpty tail) then
              $"%s{projectName}: … and the last %d{tail.Length} lines, where a run killed mid-way says how far it got:"
              yield! tail |> Array.map (fun l -> $"  | %s{l.TrimEnd()}") |> Array.toList ]

/// The human-readable report for a run whose HOST DIED — the counterpart to
/// `formatFailureReport`, and deliberately NOT it.
///
/// `formatFailureReport` counts the `failed ...` lines it can parse and heads them
/// "N test(s) failed". Run over a KILLED host's transcript that header is the whole
/// defect this report exists to fix: a runner cut off mid-suite still has per-test rows
/// in its captured output — rows at 0ms, with no assertion message, for tests that
/// never executed — and printing them under "N test(s) failed" turns a non-result
/// into a definite negative. Every hour that symptom cost was spent investigating
/// code that was fine.
///
/// So an abort gets its OWN words: what killed the host, that NOTHING was verified,
/// and that the lines below are a TRANSCRIPT of a run that was killed rather than a
/// list of findings. The rows are still printed — they are the only evidence of how
/// far the run got — but they are never counted, and never called failures.
let internal formatAbortReport
    (projectName: string)
    (runLog: RunLog.Ref)
    (reason: string)
    (output: string)
    : string list =
    let lines = output.Split('\n')

    let transcript =
        lines
        |> Array.filter (fun l -> not (System.String.IsNullOrWhiteSpace l))
        |> (fun ls -> ls.[max 0 (ls.Length - 40) ..])

    [ $"%s{projectName}: RUN ABORTED — %s{reason}"
      $"%s{projectName}: NOTHING WAS VERIFIED. This is NOT a test failure: no test was shown to fail, and no         test was shown to pass. Do not read the lines below as a regression — a runner killed mid-suite still         has rows in its capture for tests it never executed (0ms, no assertion message). Re-run on a machine         with headroom (e.g. `dotnet fshw test-rerun`); if it aborts again, the host is dying for a reason that         is not load."
      match runLog with
      | RunLog.Ref.Written path ->
          $"%s{projectName}: the FULL output — including the HEAD, which is where a dying host states its             cause — was streamed to %s{path}. READ THAT FIRST; the transcript tail follows only as a summary:"
      | RunLog.Ref.Unavailable why ->
          $"%s{projectName}: NO output log was saved (%s{why}), so the transcript tail below is ALL there is             and the head of the run is gone:"
      yield! transcript |> Array.map (fun l -> $"  | %s{l.TrimEnd()}") |> Array.toList ]

/// What is known about the structured test report for a run — the verdict
/// input. Modelled as a DU rather than a `bool * report option` so the
/// meaningless "no report was requested yet somehow a parsed report exists"
/// state cannot be constructed.
type ReportEvidence =
    /// No report was requested (an unknown / unsupported runner) — the process
    /// exit code is the only pass/fail signal available.
    | NoReportRequested
    /// A report WAS requested from a capable runner. `Ok` carries a report that passed
    /// `Ctrf.tryVerdictReport`; `Error` says why there is none — the file was absent or
    /// unreadable (the host aborted before flushing), or what it held is not evidence (a
    /// truncated report, malformed counters, a clean summary its rows do not account for).
    | ReportRequested of report: Result<Ctrf.VerdictReport, string>

/// The lead sentence for a test host that died of `signal`, on macOS or not.
///
/// SIGABRT is the host aborting ITSELF: on .NET, the runtime ending the process when its
/// exception dispatch fails (`SfiNextWorker` → `TerminateProcess` → `abort`), on a
/// fail-fast or on an assertion, often without writing a word. Calling that "KILLED" sent
/// readers after a killer that did not exist (AUTOMATION-1425). Every other signal here
/// is delivered from outside.
///
/// A process that crashes rather than being killed leaves a macOS crash report, and that
/// report is the only record of where it died: it names the native frames, and the time
/// it was written matches the host's. SIGKILL and SIGTERM leave none.
let internal describeSignalDeath (isMacOS: bool) (signal: TerminatingSignal) : string =
    let exitCode = TerminatingSignal.SignalExitBase + signal.Number

    let lead =
        if signal.Number = 6 then
            $"test host ABORTED itself (%s{signal.Name}, exit %d{exitCode}) — on .NET, the runtime ending the \
              process (a failed exception dispatch, a fail-fast, an assertion), often with nothing written first"
        else
            $"test host was KILLED by %s{signal.Name} (exit %d{exitCode})"

    let crashReport =
        match signal.Number with
        | 4
        | 6
        | 8
        | 11 when isMacOS ->
            " The OS kept a crash report naming where it died: ~/Library/Logs/DiagnosticReports/<host \
             executable>-<time>.ips, moved to its Retired/ folder within hours."
        | _ -> ""

    $"%s{lead} — it never reached its own exit, so NOTHING was verified.%s{crashReport}"

/// Decide a single project's verdict. The structured test report (when present and
/// parseable) is AUTHORITATIVE for pass/fail; the process exit code is only a tie-break
/// when there is no usable report. Exit-code-only produced false REDs: a test host that
/// exits non-zero during a dirty shutdown (the Microsoft.Testing.Platform exit-7 flake)
/// after flushing a clean report reported "Tests failed" with zero named tests, while
/// `test-rerun` came back green.
///
/// Precedence (apphost-missing / zero-match-under-filter are handled by the
/// caller BEFORE this — they are not test outcomes):
///   1. report has any failed/other result → `TestsFailed` (red). Exit irrelevant.
///   2. report is all-clear (no failed/other) AND ran ≥1 test → `TestsPassed`
///      (green) EVEN IF the process exited non-zero — the flake case.
///   3. a report WAS requested from a capable runner and there is no usable one
///      (absent, unreadable, or not coherent evidence) → `TestsErrored` on ANY exit:
///      nothing was verified. Never green, never the misleading "tests failed". A
///      clean exit is not a substitute for the report that was asked for.
///   4. no report was requested (unknown runner) → the exit code is the only signal
///      there is → `TestsPassed` or `TestsFailed`.
///
///   0. AND BEFORE ALL OF THEM: the host was TERMINATED BY A SIGNAL → `TestsErrored`.
///      A killed host did not finish, so nothing it wrote is a result — including a
///      CTRF report it managed to flush on the way down, whose rows for tests that
///      never executed are exactly the mass 0ms "failures" seen under CPU load.
///      This arm is why the report is not consulted there: outcome 1 would read that
///      partial report and call a machine that ran out of CPU a mass regression.
///   A `summary.tests == 0` report that reaches here is an UNFILTERED zero-test
///   run (the filtered case was handled upstream) — a real misconfiguration. It is
///   never green: a non-zero exit keeps it red, and a clean exit verified nothing.
///
/// Outcome 2 deliberately does NOT also require a whitelisted shutdown exit code: the
/// benign codes are runner/version-specific, and a report positively showing zero
/// failures is stronger evidence than the exit number.
///
/// Outcome 0 is the one direction where the exit code OUTRANKS the report, and only
/// because of what that particular exit code means: `TerminatingSignal` recognises
/// codes no runner CHOOSES, so it can only ever fire for a host that was killed. A
/// suite that genuinely goes red exits with a code the runner picked (MTP's are single
/// digits) and still reaches outcome 1 — the assertion this must survive in BOTH
/// directions, because a real mass failure dressed as an abort is the same lie with
/// the sign flipped.
let internal classifyTestOutcome
    (evidence: ReportEvidence)
    (wasFiltered: bool)
    (elapsed: System.TimeSpan)
    (outcome: ProcessOutcome)
    : TestResult =
    match outcome, terminatingSignalOf outcome with
    | ProcessOutcome.TimedOut(after, output, kill), _ ->
        // A timeout KILL is a real "stuck" signal; a partial report it may have flushed
        // must not override it.
        //
        // This arm renders the tail itself rather than going through `outputOf`, so it
        // must append `renderKill` explicitly — otherwise a test-runner tree we FAILED
        // to kill would be reported as a plain "stuck" timeout while it kept running,
        // holding the test DB / the port / the lock that makes the NEXT run fail too.
        TestsTimedOut(renderOutput output + renderKill kill, after, wasFiltered, elapsed)
    | _, Some signal ->
        // Outcome 0. The host was KILLED. `TestsErrored` — `NothingVerified`, never a
        // pass and never a reported failure — because a process that did not reach its
        // own exit did not finish measuring anything.
        //
        // The report state is NAMED rather than consulted: a reader who sees a partial
        // CTRF beside this message needs to be told the rows in it are a transcript,
        // not a verdict, or they will open it and find their "mass regression".
        let reportNote =
            match evidence with
            | ReportRequested(Ok report) ->
                // Counted from the ROWS (AUTOMATION-294): a crashed host never sends the
                // message its summary is written from, so `summary.tests` reads 0 beside a
                // report listing thousands. The summary's own counts follow, named as such.
                let listed = Ctrf.VerdictReport.rows report
                let r = Ctrf.VerdictReport.summary report

                $" It had flushed a PARTIAL report (%d{listed.Rows} row(s), %d{listed.FailedRows} of them marked failed; its summary counts %d{r.Total} test(s), %d{r.Failed} failed) before it died; those rows are a transcript of a killed run, NOT results — a test the host never reached is written out the same way as one that ran."
            | ReportRequested(Error reason) -> $" It wrote no usable report: %s{reason}."
            | NoReportRequested -> " No structured report was requested from this runner."

        TestsErrored(describeSignalDeath (System.OperatingSystem.IsMacOS()) signal + reportNote)
    | _, None ->
        let output = outputOf outcome
        let succeeded = isSucceeded outcome

        match evidence with
        | ReportRequested(Ok report) ->
            let r = Ctrf.VerdictReport.summary report

            if r.Failed > 0 || r.Other > 0 then
                // Outcome 1.
                TestsFailed(output, wasFiltered, elapsed)
            elif r.Total > 0 then
                // Outcome 2 — green even on a non-zero exit (the dirty-shutdown flake).
                TestsPassed(output, wasFiltered, elapsed)
            elif succeeded then
                // An unfiltered zero-test run that exited cleanly. The report is coherent
                // and proves only that the runner wrote it.
                TestsErrored "test host exited cleanly but its report counts zero tests — nothing verified"
            else
                // An unfiltered zero-test run that exited non-zero: an empty suite stays red.
                TestsFailed(output, wasFiltered, elapsed)
        | ReportRequested(Error reason) ->
            // Outcome 3 — the report that was asked for is not evidence, whatever the exit.
            let exit = if succeeded then "exited cleanly" else "exited non-zero"
            TestsErrored $"test host %s{exit} but left no usable report (%s{reason}) — nothing verified"
        | NoReportRequested when succeeded ->
            // Outcome 4. Unknown runner we never asked for a report: the exit code is all there is.
            TestsPassed(output, wasFiltered, elapsed)
        | NoReportRequested -> TestsFailed(output, wasFiltered, elapsed)
