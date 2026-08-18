/// The verdict as a FILE — `.fshw/verdict.json`.
///
/// An agent should learn the verdict by READING STATE, not by spawning a CLI and not
/// by grepping a progress display written for a human. An extra `check`/`test-rerun`
/// spawned to double-check a verdict perturbs the daemon's busy accounting and can
/// produce the next content-free green; a file read cannot, and costs no ~1-3s dotnet
/// spawn per poll.
///
/// THE PROPERTY THAT MAKES IT SAFE. A file that exists can be read when it does
/// not answer the question being asked, and a green from a different tree is still
/// a green. So the verdict is CONTENT-ADDRESSED to the tree it verified (see
/// `FsHotWatch.TreeHash`) and the consumer's rule is total:
///
///     read `.fshw/verdict.json`; if `treeHash` ≠ hash(current tree),
///     THE VERDICT DOES NOT APPLY — never reuse it.
///
/// Stale becomes DETECTABLE instead of silently reusable.
///
/// ONE TRUTH, TWO SURFACES. The exit code and this file's `outcome` are computed from
/// the SAME `CheckVerdict.CheckOutcome`, so they cannot disagree. There is deliberately
/// NO "agent mode" that changes what a check MEANS — detecting the caller is a guess,
/// and guesses fail open. Presentation may adapt to the caller; semantics may not.
module FsHotWatch.Cli.Verdict

open System
open System.IO
open System.Text.Json
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing

/// Identifies the on-disk contract. Consumers depend on this file now; a
/// breaking change to its shape MUST bump this string.
[<Literal>]
let Schema = "fshw-verdict-v1"

/// What one plugin contributed to the verdict. The SAME value drives the agent-mode
/// status line (`ProgressRenderer`), so a plugin cannot report `ok` on one surface
/// and `fail` on the other.
[<RequireQualifiedAccess>]
type PluginOutcome =
    | Ok
    | Warn
    | Fail
    | TimedOut
    | Running
    /// Running past the wedge bound with no completion posted — by definition wedged.
    /// Its OWN token, because a wedge read as mere "running" is how a long silence
    /// gets waited out.
    | Wedged

module PluginOutcome =
    /// The wire token. Total.
    let token (o: PluginOutcome) : string =
        match o with
        | PluginOutcome.Ok -> "ok"
        | PluginOutcome.Warn -> "warn"
        | PluginOutcome.Fail -> "fail"
        | PluginOutcome.TimedOut -> "timed-out"
        | PluginOutcome.Running -> "running"
        | PluginOutcome.Wedged -> "wedged"

    /// Is this plugin's own outcome incompatible with a GREEN verdict? Total, so a new
    /// outcome must be classified here by an explicit edit — it cannot default to
    /// "compatible with green".
    ///
    /// `Running` is not listed, deliberately: a plugin still running has not failed. It
    /// is `Wedged` — running PAST the bound, which is failure by definition — that is.
    let isFailing (o: PluginOutcome) : bool =
        match o with
        | PluginOutcome.Fail
        | PluginOutcome.TimedOut
        | PluginOutcome.Wedged -> true
        | PluginOutcome.Ok
        | PluginOutcome.Warn
        | PluginOutcome.Running -> false

/// Resolve a plugin's outcome from its parsed status. `None` means "nothing to
/// report" — idle, never run — and such a plugin is omitted rather than being
/// invented as a pass.
///
/// THE one implementation: it drives both the agent-mode status line
/// (`ProgressRenderer`) and `plugins[]` in `.fshw/verdict.json`. Two fail-closed
/// rules, which the verdict file therefore inherits:
///
///   * a plugin Running past the wedge bound is `Wedged`, never merely `running`;
///   * a `Completed` carrying NO run record can never token as `ok` — a ✓ with no
///     `elapsed:` is not evidence. No record ⇒ no green;
///   * a `Completed` whose run record says the run VERIFIED NOTHING can never token as
///     `ok` either — a run that executed no test is an absence of evidence, not a pass
///     (AUTOMATION-198). `Warn`, not `Fail`: nothing broke, so this must not redden a
///     verdict that the scope layer is already refusing with its own exit 3.
let pluginOutcomeOf (warningsAreFailures: bool) (now: DateTime) (parsed: ParsedPluginStatus) : PluginOutcome option =
    let okOrDiag () =
        if ErrorLedger.DiagnosticCounts.isFailing warningsAreFailures parsed.Diagnostics then
            if parsed.Diagnostics.Errors > 0 then
                PluginOutcome.Fail
            else
                PluginOutcome.Warn
        else
            PluginOutcome.Ok

    let timedOutLastRun () =
        match parsed.LastRun with
        | Some r ->
            match r.Outcome with
            | TimedOut _ -> true
            | _ -> false
        | None -> false

    match parsed.Status with
    | StatusView.Running since ->
        match PluginWedge.classifyRunning (PluginWedge.ambientBound ()) now since with
        | PluginWedge.RunningHealth.Wedged _ -> Some PluginOutcome.Wedged
        | PluginWedge.RunningHealth.StillRunning _ -> Some PluginOutcome.Running
    // A status this build could not read is `Fail`, and `Some`: the plugin STAYS IN the
    // verdict. Rounding it down to `Idle` would omit it, and a plugin nobody could read
    // vanishing from `plugins[]` is how the verdict goes green over a check that never
    // reported.
    | StatusView.Unreadable _ -> Some PluginOutcome.Fail
    | StatusView.Failed _ when timedOutLastRun () -> Some PluginOutcome.TimedOut
    | StatusView.Failed _ -> Some PluginOutcome.Fail
    // Fail closed on a `Completed` that is not evidence of a pass. Two reasons reach
    // this, and they share one rule rather than one copy of it each:
    //   * NO RUN RECORD at all — nothing to vouch for the completion;
    //   * a run record saying the run VERIFIED NOTHING (AUTOMATION-198) — the summary
    //     says what happened, and an absence of evidence is not a pass.
    // Either way a clean ledger downgrades to `warn`; failing diagnostics still take
    // precedence, so nothing here can hide a red.
    | StatusView.Completed _ when parsed.LastRun.IsNone || ParsedPluginStatus.verifiedNothing parsed ->
        match okOrDiag () with
        | PluginOutcome.Ok -> Some PluginOutcome.Warn
        | other -> Some other
    | StatusView.Completed _ -> Some(okOrDiag ())
    | StatusView.Idle ->
        parsed.LastRun
        |> Option.map (fun r ->
            match r.Outcome with
            | FailedRun _ -> PluginOutcome.Fail
            | TimedOut _ -> PluginOutcome.TimedOut
            | CompletedRun -> okOrDiag ())

/// One plugin's line in the verdict.
///
/// `ElapsedMs` is an OPTION: **a missing number is not zero.** `0` is a measurement
/// ("this ran instantaneously"); absence is the absence of one, and a reader must be
/// able to tell them apart. Serialized as `null`.
type PluginVerdict =
    { Name: string
      Outcome: PluginOutcome
      ElapsedMs: int64 option
      Summary: string option }

/// A pointer to one test project's CTRF report, plus the counts it carries, so "how
/// many ran? how many failed?" is answered without opening a second file while the
/// deeper questions still have somewhere to go.
type SuiteVerdict =
    {
        Project: string
        /// Repo-relative path to the CTRF report.
        Ctrf: string
        Total: int
        Passed: int
        Failed: int
        Skipped: int
    }

/// One diagnostic that CONTRIBUTED to a red, as the verdict records it.
///
/// AUTOMATION-303 case 3. A `confirm` returned exit 1 with all four plugins `ok` and
/// 9,064 passed / 0 failed. The red was real — ~51 FCS diagnostics in the ledger — but
/// FCS is not a plugin: the daemon reports its diagnostics under the pseudo-source
/// `fcs` (`PluginActivity.FcsPluginName`), which has no `PluginStatus` and therefore no
/// line in `plugins[]`. Every surface the verdict offered said "fine", and the exit code
/// said "broken", so the only way to find out which was true was to open the daemon log.
///
/// So the file now names them. `Source` is the LEDGER KEY that reported the entry — a
/// plugin name, or `fcs` — precisely because the interesting case is the one that is not
/// a plugin.
type RedCause =
    {
        Source: string
        /// Repo-relative where the ledger gave one; otherwise as reported.
        File: string
        Severity: string
        Message: string
        /// Is this diagnostic a claim about THE TREE ON DISK — see `RedCauseKind`.
        Kind: RedCauseKind
    }

/// IS THIS RED A CLAIM ABOUT THIS TREE?
///
/// AUTOMATION-303's second failure direction, and the one its own fix left open. The
/// ticket's premise is that a green must be earned; a RED must be earned in exactly the
/// same sense, and two of the four incidents it records were reds that no longer
/// described the tree they were reported against. A daemon that keeps asserting a
/// diagnostic after the tree it came from is gone is making the same error as a cache
/// that replays a green after the tree it ran on is gone — with the sign flipped, and
/// with a worse consequence, because the operator's only escape (`fshw stop`) is exactly
/// the one the tool never mentions.
///
/// Two of these are DECIDABLE from the diagnostic itself, and neither is a heuristic:
///
///   * a diagnostic reported against an ABSOLUTE path that is not on disk cannot be a
///     claim about a tree in which that path does not exist;
///   * an FCS `internal error:` is the CHECKER reporting its own crash. It is not a
///     finding about the code — the check did not complete, so nothing was found.
///
/// Everything else is `AboutThisTree`, deliberately: this classification may only ever
/// move a cause OUT of "your code is broken", so anything it cannot prove stays a red.
and RedCauseKind =
    /// A diagnostic that names a file present on disk, or that names no path at all
    /// (`<build>`, a Cobertura filename). A genuine claim about this tree; the red is
    /// earned. THE DEFAULT — nothing reaches the others without proof.
    | AboutThisTree
    /// The diagnostic names an absolute path that is NOT on disk. The daemon is
    /// describing a tree that no longer exists. Case 4's stale symbol index, generalised
    /// to the whole ledger: `pruneDeletedUnanalyzable` fixed the one map in test-prune
    /// that produced it, and left the general shape — any source that pins a diagnostic
    /// to a path and never re-runs for a file that is gone — intact.
    | VanishedFile
    /// An FCS `internal error:` — the checker faulted. Case 3: ~51 of these reddened a
    /// `confirm` with four plugins `ok` and 9,064 tests passed, against code the session
    /// had not touched and MSBuild compiled cleanly. `fshw scan` (the documented remedy)
    /// did not clear them; `fshw stop` did, completely.
    | CheckerFault

module RedCauseKind =
    /// The wire tag. Total.
    let tag (k: RedCauseKind) : string =
        match k with
        | AboutThisTree -> "about-this-tree"
        | VanishedFile -> "vanished-file"
        | CheckerFault -> "checker-fault"

    let ofTag (s: string) : RedCauseKind option =
        match s with
        | "about-this-tree" -> Some AboutThisTree
        | "vanished-file" -> Some VanishedFile
        | "checker-fault" -> Some CheckerFault
        | _ -> None

    /// Can this cause be a claim about the tree on disk? `false` for exactly the two
    /// proven cases — so "unattributable" is the narrow set, never the residue.
    let isAboutThisTree (k: RedCauseKind) : bool =
        match k with
        | AboutThisTree -> true
        | VanishedFile
        | CheckerFault -> false

module RedCause =
    /// The marker FCS puts on its own crashes. Matched case-insensitively on a trimmed
    /// message, and ONLY for entries the checker itself reported (`fcs`) — a plugin that
    /// happens to quote the phrase is not the compiler crashing.
    [<Literal>]
    let private checkerFaultMarker = "internal error:"

    /// Classify one cause. `exists` is injected rather than called directly so a test can
    /// pin the "the file is gone" branch without deleting anything, and — more to the
    /// point — so the POSITIVE CONTROL (a present file still reddens) is expressible.
    ///
    /// Only ABSOLUTE paths are eligible for `VanishedFile`. The ledger's file key is
    /// whatever the reporting source passed: `fcs` passes `AbsFilePath.value`, but
    /// `BuildPlugin` passes the literal `<build>` and `CoveragePlugin` passes a Cobertura
    /// filename. A relative or synthetic key that does not exist on disk proves nothing,
    /// and treating it as proof would demote real reds.
    let classifyWith (exists: string -> bool) (source: string) (file: string) (message: string) : RedCauseKind =
        let isCheckerFault =
            source = FsHotWatch.PluginActivity.FcsPluginName
            && (message: string).TrimStart().StartsWith(checkerFaultMarker, System.StringComparison.OrdinalIgnoreCase)

        if isCheckerFault then
            CheckerFault
        elif
            not (System.String.IsNullOrWhiteSpace file)
            && System.IO.Path.IsPathRooted file
            && not (exists file)
        then
            VanishedFile
        else
            AboutThisTree

    /// `classifyWith` against the real filesystem.
    let classify (source: string) (file: string) (message: string) : RedCauseKind =
        classifyWith System.IO.File.Exists source file message

/// How many causes a verdict lists before it starts counting instead. A red from a
/// cross-file FCS fault arrives in the dozens, and a verdict file that is mostly one
/// repeated message is not more informative than a verdict file that says so.
[<Literal>]
let MaxRedCauses = 10

/// The verdict itself. `Incomplete` is the third answer: nothing is known to be broken,
/// and nothing is known to be sound either — a `confirm` that ran impact-filtered tests
/// lands here, and so does a check whose coverage could not be confirmed. NEVER a green.
type Outcome =
    | Green
    | Red
    | Incomplete of reason: string

module Outcome =
    /// The wire tag. Total.
    let tag (o: Outcome) : string =
        match o with
        | Green -> "green"
        | Red -> "red"
        | Incomplete _ -> "incomplete"

/// The words a non-green `CheckOutcome` is explained in — ONE copy, read by every
/// surface that has to explain one: this file's structured `Outcome`, the daemon-path
/// terminal (`IpcOutput`) and the daemon-less one (`RunOnceCheck`).
///
/// They lived as three hand-synced copies, and the `waiting on build` text is the
/// reason that mattered: it is the message a WEDGED caller reads, so it is the one
/// message that must never be the stale copy. AUTOMATION-245 had to edit all three to
/// add the same two sentences, and a grep for the prose found only two of them.
///
/// Cause and remedy are separate bindings because the surfaces join them differently —
/// the terminals put the remedy on its own line, the structured payload keeps one
/// string — and that difference is presentation, which may vary. The words may not.
module CheckProse =

    /// What happened. Never a red: nothing failed, and nothing was verified either.
    let waitingOnBuildCause =
        "waiting on build — a test project's build artifact was not produced, so its \
         tests did not run. Nothing was verified (not a pass) and nothing failed (not a \
         red); re-run once the build settles."

    /// What to DO about it — the half that had to be added because the cause alone left
    /// the caller with an instruction that could not work.
    ///
    /// `fshw confirm` is the escape, and restarting the daemon explicitly is NOT one:
    /// the task cache is `FileTaskCache` on disk under `.fshw/`, so it survives `fshw
    /// stop` and a reboot. That folk remedy was advised for months and never cleared
    /// anything by itself; saying so here is the only place a wedged caller will read it.
    let waitingOnBuildRemedy =
        "If an otherwise-unchanged re-run says this again, the build is serving a \
         cached result its outputs no longer support: run `fshw confirm`, which forces \
         a real build. Restarting the daemon does NOT — the task cache is on disk."

    /// AUTOMATION-201. The OTHER cause of "waiting on build", and the reason the pair
    /// above could not stay the only answer: for a stale-artifact refusal every clause
    /// of it is wrong. The artifact WAS produced. The build already ran — the ticket
    /// records `✓ build` in the same run as the refusal — so "the build is serving a
    /// cached result" misnames the mechanism, which is MSBuild's incremental `Copy`
    /// skipping a file whose timestamps compared equal. And both remedies it names cost
    /// a full gate cycle to arrive back at the identical refusal, which is precisely the
    /// "re-run the identical command, get the identical failure" defect this ticket
    /// exists to delete.
    ///
    /// So this one states the cause it actually has, names every affected project by
    /// carrying the per-project deferrals VERBATIM (each already names its project, the
    /// stale file and the command that repairs it — there is no second list to truncate
    /// or to keep in step), and rules out the three remedies that do not work. Same
    /// shape as `staleDaemonState`, for the same reason: the sentence that saves the
    /// cycle is the one naming the remedy that does NOT help.
    let staleBuildOutput (deferrals: string list) =
        let listed = deferrals |> List.map (fun d -> $"\n  · %s{d}") |> String.concat ""

        $"waiting on build — %d{List.length deferrals} test project(s) did NOT run because their build OUTPUT is \
           stale: the artifact exists, but its bytes do not match the sources it was built from, so running the \
           suite would have tested code you did not write. Nothing was verified (not a pass) and nothing failed \
           (not a red).%s{listed}\nRemedy: run `dotnet build`. If it reports success while this persists, the copy \
           is timestamp-inverted and only `dotnet build --no-incremental` re-emits it. Re-running the gate does \
           NOT clear this, and neither does `fshw confirm` nor restarting the daemon — this is bytes on disk, not \
           the task cache."

    /// AUTOMATION-303 AC5. The gate's own answer to "is `fshw stop` still needed?" —
    /// stated by the tool, at the moment it is needed, instead of left in a ticket.
    ///
    /// It names the remedy AND rules out the wrong one. `fshw scan` is what the docs
    /// advised for this class and it has never cleared it: the FCS internal-error storm
    /// of 2026-08-12 survived a scan and vanished on a stop. An operator who tries the
    /// documented remedy first loses another full gate cycle, so the sentence that saves
    /// the cycle is the one that says which remedy does NOT work.
    let staleDaemonState (unattributable: int) =
        $"NO VERDICT — all %d{unattributable} failing diagnostic(s) are ones this run cannot attribute to the tree on \
           disk: an FCS `internal error:` (the checker crashed, so it found nothing) or a diagnostic against a file \
           that is no longer there.\nNothing is reported broken — do NOT go looking for a defect — and nothing is \
           reported sound either. This is stale daemon state: run `fshw stop`, then re-run. `fshw scan` does NOT \
           clear it. See `reddenedBy[].kind` in the verdict for which cause was which."

    /// A scope that could not be READ. Its own words, never `confirm`'s: this is not
    /// "the run was too narrow", it is "we could not see what the run was" — and a
    /// consumer told the former would retry a broken check forever.
    let scopeUnreadable (reason: string) =
        $"NO VERDICT — the test scope could not be read (%s{reason}).\nThat is not the same as \"no tests were \
           needed\": a read that faulted cannot rule out that ZERO tests ran, which is the one thing a green \
           may never mean. Nothing is reported broken, and nothing is reported sound either."

    /// A scope that was read and is too narrow to support a merge claim.
    let scopeTooNarrow (scope: TestScope) =
        $"Confirm: NO VERDICT — the tests that ran were %s{TestScope.describe scope}, \
           not the full suite.\nAn impact-filtered green means \"your change didn't break anything I chose to \
           look at\", which is not the claim a merge needs. Nothing is reported broken, but nothing is \
           reported sound either."

/// Derive the file's outcome from the check's — the SAME value the exit code is
/// derived from, so the two can never disagree.
let outcomeOfCheck (outcome: CheckVerdict.CheckOutcome) : Outcome =
    match outcome with
    | CheckVerdict.CheckOutcome.Clean -> Green
    | CheckVerdict.CheckOutcome.FailuresFound -> Red
    | CheckVerdict.CheckOutcome.Incomplete n ->
        if n > 0 then
            Incomplete $"%d{n} file(s) could not be checked"
        else
            Incomplete "coverage could not be confirmed"
    | CheckVerdict.CheckOutcome.WaitingOnBuild [] ->
        // A DISTINCT `incomplete` reason in `.fshw/verdict.json` — the deploy
        // preflight reads the structured outcome (`incomplete`, exit 2), never the
        // prose, so "waiting on build" is a retry signal, not "tests failed".
        // One string here: this payload is a JSON field, not a terminal.
        Incomplete $"%s{CheckProse.waitingOnBuildCause} %s{CheckProse.waitingOnBuildRemedy}"
    | CheckVerdict.CheckOutcome.WaitingOnBuild stale ->
        // The stale-output cause gets its own reason IN THE FILE, not only at the
        // terminal. A consumer that reads `verdict.json` — the deploy preflight, an
        // autonomous loop — is exactly the reader who would otherwise retry forever:
        // this defer does not settle on its own, and the reason has to say so where the
        // retry decision is actually made.
        Incomplete(CheckProse.staleBuildOutput stale)
    | CheckVerdict.CheckOutcome.UnearnedScope NoTestsRun ->
        // "0 projects selected" is an INCOMPLETE check, never a pass, and must not be
        // renderable as a green on any surface.
        Incomplete "NO TESTS RAN — nothing was verified. This is not a pass; it is an absence of evidence."
    | CheckVerdict.CheckOutcome.UnearnedScope(ScopeUnreadable reason) ->
        // Not "the scope was too narrow" — the scope is UNKNOWN because reading it
        // failed, so this run cannot say whether anything ran at all. Its own reason,
        // because a consumer that sees the generic "not the full suite" would read a
        // broken check as a merely-narrow one and retry forever.
        Incomplete
            $"THE TEST SCOPE COULD NOT BE READ (%s{reason}) — so this run cannot say whether any test ran. Not a pass; \
               an absence of evidence."
    | CheckVerdict.CheckOutcome.UnearnedScope scope ->
        Incomplete
            $"the tests that ran were %s{TestScope.describe scope}, not the full suite — a merge verdict needs the whole suite"
    // AUTOMATION-303. `incomplete`, never `red`: the structured outcome is what a deploy
    // preflight reads, and "the daemon is stale" must route to retry-after-stop, not to
    // "tests failed". The prose is `CheckProse`'s single copy — the same words the two
    // terminals print.
    | CheckVerdict.CheckOutcome.StaleDaemonState n -> Incomplete(CheckProse.staleDaemonState n)

// ---------------------------------------------------------------------------
// AUTOMATION-259 — the check-vs-confirm sample every `confirm` already had
// ---------------------------------------------------------------------------

/// What the IMPACT-SCOPED run concluded — the run `confirm` graded BEFORE it escalated to
/// the full suite.
///
/// `confirm` does not start with a full run. It grades what the daemon already has, sees
/// that it was filtered (`CheckVerdict.confirmNeedsFullRun`), says so, and runs the whole
/// suite to earn a verdict. That first reading existed in-process and survived only as a
/// log line.
///
/// It is also the ONE reading fshw cannot get any other way. `check` and `confirm` are
/// separate invocations, so comparing them means comparing two trees: a full day of
/// running them side by side (2026-08-06) produced not one clean pair, because the tree
/// moved between every invocation. Recorded here, the two readings share a tree, a daemon,
/// a scan generation and an instant — for free, because nothing extra runs.
///
/// Graded in `InnerLoop` mode, deliberately — see `impactScopedRun`.
type ImpactScopedRun =
    {
        Scope: TestScope
        Outcome: Outcome
        /// Projects whose CTRF report recorded at least one failing test IN THAT RUN.
        /// Names only: the counts live in the run's own reports, and a second copy here
        /// would be a second thing to keep in step.
        FailingSuites: string list
    }

/// How the impact-scoped reading compared with the full-suite verdict `confirm` went on to
/// earn.
///
/// NOT a bool. A bool has one way of being false, and there are three different ways of
/// not having an agreement — one of which is an fshw DEFECT and two of which are not a
/// comparison at all. Collapsing them is the same mistake as `Coverage.Unknown` reading as
/// `Complete`: the states that mean "we do not know" must not be spendable as the state
/// that means "we checked".
[<RequireQualifiedAccess>]
type Divergence =
    /// Both readings existed and answered the same — both clean, or both red. THE data
    /// point: same tree, same daemon, same instant.
    | Agreed
    /// The impact-scoped run was GREEN and the full suite was RED: the selector did not
    /// choose a test that fails. Per AUTOMATION-160 this is an fshw DEFECT, not a merge
    /// saved — `check` told someone their change was fine and it was not. The case this
    /// whole record exists to surface.
    | CheckMissedFailures
    /// The impact-scoped run was RED and the full suite was GREEN: a stale red, a flake, or
    /// a test-isolation defect (a test that only passes with company). `check` may well be
    /// the honest one here — what it is not is agreement.
    | CheckOnlyFailures
    /// `confirm` did not escalate: the run it graded was ALREADY full-suite, so there is no
    /// impact-scoped reading beside it to compare with.
    ///
    /// STATED, never left as an absence. `confirm` sends `set-scope full` before it scans
    /// (`Program.ensureAndQueryErrors`, `RunOnceCheck.runOnceAndVerdict`), so any run its
    /// own scan provokes is unfiltered — which is why this is the COMMON case in CI and why
    /// it must not be counted as an agreement: nothing was compared.
    | NoImpactScopedRun
    /// One of the two readings reached no answer to compare. An escalated run that dies on
    /// compile errors produces no full-suite result, and "could not compare" may never
    /// collapse into "agreed".
    | Incomparable of reason: string
    /// NO comparison is on record: the verdict predates AUTOMATION-259, or it is a `check`,
    /// which never escalates and so never makes one.
    ///
    /// This is what an ABSENT field reads as. It is deliberately distinct from every case
    /// above, so "nobody recorded anything" can never be read as "they agreed".
    | NotRecorded

module Divergence =
    /// The wire token. Total.
    let token (d: Divergence) : string =
        match d with
        | Divergence.Agreed -> "agreed"
        | Divergence.CheckMissedFailures -> "check-missed-failures"
        | Divergence.CheckOnlyFailures -> "check-only-failures"
        | Divergence.NoImpactScopedRun -> "no-impact-scoped-run"
        | Divergence.Incomparable _ -> "incomparable"
        | Divergence.NotRecorded -> "not-recorded"

    /// Does this classification CLAIM that two readings were compared — and therefore
    /// require the impact-scoped one to be recorded beside it?
    ///
    /// Total, so a new case must be classified by an explicit edit rather than defaulting
    /// to "needs nothing", which is how a record claiming a comparison it never made gets
    /// written.
    let claimsAComparison (d: Divergence) : bool =
        match d with
        | Divergence.Agreed
        | Divergence.CheckMissedFailures
        | Divergence.CheckOnlyFailures -> true
        | Divergence.NoImpactScopedRun
        | Divergence.Incomparable _
        | Divergence.NotRecorded -> false

    /// Does this classification ASSERT that there was nothing to compare — and therefore
    /// require that no impact-scoped run is recorded beside it?
    ///
    /// `Incomparable` is in NEITHER predicate, deliberately: `confirm` escalated and the
    /// reading is there, or the classification came from a build this one cannot read and
    /// it is not. Both are honest `Incomparable`s.
    let assertsNothingToCompare (d: Divergence) : bool =
        match d with
        | Divergence.NoImpactScopedRun
        | Divergence.NotRecorded -> true
        | Divergence.Agreed
        | Divergence.CheckMissedFailures
        | Divergence.CheckOnlyFailures
        | Divergence.Incomparable _ -> false

/// The AUTOMATION-259 sample as ONE value: the classification, and the reading it
/// classified. They are only meaningful together, so they travel together and `validate`
/// refuses the two pairs that are self-contradictory (see `divergenceAgreesWithRecord`).
type CheckComparison =
    { Divergence: Divergence
      ImpactScoped: ImpactScopedRun option }

module CheckComparison =
    /// What a `check` records, and what a verdict written before AUTOMATION-259 reads as.
    let notRecorded: CheckComparison =
        { Divergence = Divergence.NotRecorded
          ImpactScoped = None }

    /// The one thing the two runs can be compared ON: did it find failures? `Incomplete` is
    /// neither a yes nor a no — it is the ABSENCE of an answer, and folding it into "found
    /// nothing" is exactly how "the escalated run died on compile errors" would come to
    /// read as agreement.
    let private failuresFound (o: Outcome) : bool option =
        match o with
        | Green -> Some false
        | Red -> Some true
        | Incomplete _ -> None

    let private whyNoAnswer (o: Outcome) : string =
        match o with
        | Incomplete reason -> reason
        | Green
        | Red -> Outcome.tag o

    /// THE classification, and the only door into a `CheckComparison` that claims one.
    ///
    /// `earned` is the outcome THIS verdict records — the full-suite result `confirm` went
    /// and got — not the pre-escalation one.
    let ofRun (impactScoped: ImpactScopedRun option) (earned: Outcome) : CheckComparison =
        let divergence =
            match impactScoped with
            | None -> Divergence.NoImpactScopedRun
            | Some pre ->
                match failuresFound pre.Outcome, failuresFound earned with
                | Some checkFoundFailures, Some confirmFoundFailures ->
                    match checkFoundFailures, confirmFoundFailures with
                    | false, true -> Divergence.CheckMissedFailures
                    | true, false -> Divergence.CheckOnlyFailures
                    | _ -> Divergence.Agreed
                // The escalated run is checked FIRST: when neither side answered, the fact
                // worth recording is that the run `confirm` FORCED produced nothing, which
                // is the failure mode the case was named for.
                | _, None ->
                    Divergence.Incomparable
                        $"the escalated full-suite run reached no verdict to compare with (%s{whyNoAnswer earned})"
                | None, _ ->
                    Divergence.Incomparable
                        $"the impact-scoped run reached no verdict of its own (%s{whyNoAnswer pre.Outcome})"

        { Divergence = divergence
          ImpactScoped = impactScoped }

/// The identity of the fshw that produced a verdict — `DaemonIdentity.BinaryIdentity`
/// reused, not a fourth identity type.
///
/// `treeHash` content-addresses the verdict's SUBJECT; this content-addresses its
/// PRODUCER. Both halves are needed, or a stale daemon writes a verdict for an UNCHANGED
/// tree, the `treeHash` matches, and the verdict reads as current.
type Producer = DaemonIdentity.BinaryIdentity

module Producer =
    /// The fshw running right now (version + content hash of its binary).
    let current () : Producer = DaemonIdentity.currentIdentity ()

    /// Do two producers refer to the same binary — as far as a VERDICT is concerned?
    ///
    /// FAIL CLOSED. Deliberately differs from `DaemonIdentity.compareIdentity`, which
    /// treats two UNHASHABLE binaries as a match. Same hash, same sentinel, different
    /// questions: "restart the daemon?" must fail OPEN (refusing on an unhashable binary
    /// would restart it every command and thrash the FCS cache), while "does this claim
    /// apply?" must fail CLOSED (accepting would let a verdict of unestablished
    /// provenance read as current). So an unhashable producer never matches — not even
    /// itself.
    let same (a: Producer) (b: Producer) : bool =
        ContentHash.isReadable a.ContentHash
        && ContentHash.isReadable b.ContentHash
        && String.Equals(a.ContentHash, b.ContentHash, StringComparison.Ordinal)
        && String.Equals(a.Version, b.Version, StringComparison.Ordinal)

/// Which verb produced the verdict, and therefore what it is allowed to claim.
/// `check` is the inner loop (impact-scoped, honest that it is); `confirm` runs the
/// full suite to confirm `check` told the truth (unfiltered, evidence-required).
type Command =
    | Check
    | Confirm

module Command =
    let token (c: Command) : string =
        match c with
        | Check -> "check"
        | Confirm -> "confirm"

    let ofCheckMode (mode: CheckVerdict.CheckMode) : Command =
        match mode with
        | CheckVerdict.InnerLoop -> Check
        | CheckVerdict.Confirmation -> Confirm

/// The on-disk verdict.
///
/// THE REPRESENTATION IS PRIVATE, and the only door in is `Verdict.create`. Without
/// that, `outcome` and `plugins` are assembled from independent sources and nothing
/// forbids the one state this file must never express:
///
///     {"outcome": "green", "plugins": [{"outcome": "fail"}]}
///
/// So `outcome` is not merely CORRELATED with `plugins`; it is CHECKED against them,
/// once, at the only place a `Verdict` can come into existence. A state that is a lie
/// is made unconstructible rather than documented as forbidden.
[<NoComparison>]
type Verdict =
    private
        {
            producedAt: DateTime
            command: Command
            producer: Producer
            runId: Guid option
            treeHash: string
            treeHashAlgorithm: string
            treeFileCount: int
            scope: TestScope
            outcome: Outcome
            exitCode: int
            plugins: PluginVerdict list
            suites: SuiteVerdict list
            comparison: CheckComparison
            /// AUTOMATION-303. The failing ledger diagnostics this verdict's exit code
            /// was computed from, truncated to `MaxRedCauses`. Empty on a green — and
            /// empty on a red means the red came from a failing PLUGIN, which
            /// `plugins[]` already names.
            redCauses: RedCause list
            /// How many failing diagnostics there were before `redCauses` was
            /// truncated. A count, not a length: `redCauses.Length` answers "how many
            /// are printed here", which is a different question and the one nobody
            /// asked.
            redCauseCount: int
            /// The change that SELECTED this run's tests, truncated. Empty when
            /// nothing selected it (an unfiltered run), when no run has happened, and
            /// when the daemon predates the field — all of which must read as silence.
            ///
            /// Persisted because it answers a question only a LATER run asks: when a
            /// check selects nothing, "what was the last change that did trigger
            /// tests?" is answerable solely from the verdict left behind by the run
            /// that did. In memory it dies with the daemon.
            trigger: string list
            /// The true seed count before `trigger` was truncated, so a report can say
            /// "and N more" rather than implying the short list is all of them.
            triggerCount: int
        }

    member this.ProducedAt = this.producedAt
    member this.Command = this.command

    /// The fshw binary that produced this verdict. A verdict from a different
    /// binary does not apply, however well its `treeHash` matches.
    member this.Producer = this.producer

    /// The test run this verdict's suites came from — i.e. the directory they
    /// live in (`.fshw/test-runs/<runId>/`). `None` when NO test run happened,
    /// which is a fact the verdict states rather than a silence for the reader
    /// to decode.
    member this.RunId = this.runId
    member this.TreeHash = this.treeHash
    member this.TreeHashAlgorithm = this.treeHashAlgorithm
    member this.TreeFileCount = this.treeFileCount
    member this.Scope = this.scope

    /// Never `Green` while any plugin below is failing. Guaranteed by `create`.
    member this.Outcome = this.outcome

    /// The exit code the producing command returned. Carried so the file and
    /// the process agree in the record, not just by convention.
    member this.ExitCode = this.exitCode
    member this.Plugins = this.plugins
    member this.Suites = this.suites

    /// AUTOMATION-259. How the impact-scoped reading `confirm` escalated away from compared
    /// with the verdict it then earned. ALWAYS a named case — a verdict that made no
    /// comparison says so (`NotRecorded` / `NoImpactScopedRun`) rather than staying silent.
    member this.Comparison = this.comparison

    /// The classification on its own, for the consumer that only wants to count.
    member this.Divergence = this.comparison.Divergence

    /// What the impact-scoped run concluded, when there was one.
    member this.ImpactScopedRun = this.comparison.ImpactScoped

    /// AUTOMATION-303. The failing ledger diagnostics behind a red exit code — including
    /// the ones no plugin owns (`fcs`). Empty on a green.
    member this.RedCauses = this.redCauses

    /// How many failing diagnostics there were before `RedCauses` was truncated.
    member this.RedCauseCount = this.redCauseCount

    /// The change that selected this run's tests (truncated; see `TriggerCount`).
    member this.Trigger = this.trigger

    /// How many seeds there were before `Trigger` was truncated.
    member this.TriggerCount = this.triggerCount

/// The invariants. THREE, and they are the same lie in three places: a record whose own
/// fields disagree, so that reading any ONE of them cold gives the wrong answer.
///
///   * a GREEN verdict may not contain a failing plugin;
///   * a CONFIRM verdict may not carry an `ImpactFiltered` scope;
///   * a check-vs-confirm classification may not disagree with the reading beside it.
///
/// Both construction sites — `create` (producing) and `read` (rehydrating from disk) —
/// go through this, so a hand-edited or hostile verdict file is refused on the way IN
/// as well as being impossible on the way out.
let private validate (v: Verdict) : Result<Verdict, string> =
    // AUTOMATION-258. `confirm` never accepts a filtered run: it DETECTS one and escalates
    // to the full suite (`CheckVerdict.confirmNeedsFullRun`), and `CheckVerdict.verdict`
    // has no branch that reaches `Clean` without a `FullSuite`. So `command: confirm`
    // beside `scope: {kind: "filtered"}` never records evidence confirm settled for — it
    // records the reading of the run confirm REJECTED, left behind because the forced full
    // run did not complete. A reader took that pair cold, reported that `confirm` was
    // impact-filtering, and only the prose log disproved it.
    let scopeAgreesWithCommand () =
        match v.Command, v.Scope with
        | Confirm, ImpactFiltered(ran, total) ->
            Error
                $"a CONFIRM verdict cannot carry an impact-filtered scope (%d{ran}/%d{total} projects) — \
                   `confirm` escalates a filtered run to the full suite and has no branch that accepts one, so \
                   this is not the evidence it settled for; it is the reading of the run it refused. A record \
                   that says one thing cold and another beside the log has not recorded a verdict."
        | _ -> Ok v

    let outcomeAgreesWithPlugins () =
        match v.Outcome with
        | Red
        | Incomplete _ -> Ok v
        | Green ->
            match v.Plugins |> List.filter (fun p -> PluginOutcome.isFailing p.Outcome) with
            | [] -> Ok v
            | failing ->
                let named =
                    failing
                    |> List.map (fun p -> $"%s{p.Name} (%s{PluginOutcome.token p.Outcome})")
                    |> String.concat ", "

                Error
                    $"a GREEN verdict cannot contain failing plugins — %s{named}. A check that says green on one \
                       surface and fail on another has not checked anything; it has produced two answers."

    // AUTOMATION-259. The classification and the reading it classified are ONE fact split
    // across two fields, and the two pairs below are the ways of stating it wrong:
    // claiming a comparison with nothing to have compared, and recording a reading under a
    // classification that says there was none. Either would let an analysis counting
    // `agreed` count a `confirm` that compared nothing.
    let divergenceAgreesWithRecord () =
        match v.Command with
        // `check` never escalates (`CheckVerdict.confirmNeedsFullRun` is `false` in
        // `InnerLoop`), so it never HAS a second reading to compare against and may not
        // record one. Confirm-only, in the type rather than by convention.
        | Check when v.Divergence <> Divergence.NotRecorded || v.ImpactScopedRun.IsSome ->
            Error
                $"a CHECK verdict cannot carry a check-vs-confirm comparison (%s{Divergence.token v.Divergence}) — \
                   `check` never escalates, so it never ran a second time to compare against. Only `confirm` can \
                   make this claim."
        | Check -> Ok v
        | Confirm ->
            match v.ImpactScopedRun with
            | None when Divergence.claimsAComparison v.Divergence ->
                Error
                    $"a verdict classified '%s{Divergence.token v.Divergence}' records no impact-scoped run — it \
                       claims a comparison with nothing to have compared against."
            | Some _ when Divergence.assertsNothingToCompare v.Divergence ->
                Error
                    $"a verdict classified '%s{Divergence.token v.Divergence}' says there was nothing to compare, \
                       yet records an impact-scoped run. One of the two is wrong and a reader cannot tell which."
            | _ -> Ok v

    scopeAgreesWithCommand ()
    |> Result.bind (fun _ -> outcomeAgreesWithPlugins ())
    |> Result.bind (fun _ -> divergenceAgreesWithRecord ())

/// The scope a `command`'s verdict may RECORD, given what the run actually reported.
///
/// The PRODUCING half of `validate`'s confirm-vs-filtered rule, and it lives next to that
/// rule so the two cannot drift: `validate` says which pair is a lie, this says what to
/// write instead. A producer that skipped it would not write the lie — `create` throws —
/// but a crash is a worse answer for the caller than an honest record, so the rule and its
/// remedy are one edit apart.
///
/// Only `confirm`'s filtered scope is rewritten. `check` KEEPS its `ImpactFiltered`: an
/// impact-filtered green is the answer the inner loop wants, and a `check` that hid its own
/// scope would be the same lie pointed the other way.
/// AUTOMATION-259 took the COUNTS out of this string. They were the one thing here a
/// machine wanted, they had to be re-derived by parsing prose, and they now live in
/// `checkComparison.impactScopedRun.scope` as a typed `ImpactFiltered` — the same filtered
/// reading, in the one place where being filtered is correct rather than a lie. Restating
/// them here would be a second copy of a number, drifting from a DIFFERENT read (this
/// scope is the FINAL read; the sub-record is the PRE-escalation one).
let scopeToRecord (command: Command) (scope: TestScope) : TestScope =
    match command, scope with
    | Confirm, ImpactFiltered _ ->
        ScopeUnreadable
            "confirm forced the full suite and that run did not complete — the only scope on record is an EARLIER \
             impact-filtered run, which is not this verdict's evidence. That run is recorded, with its counts, under \
             `checkComparison.impactScopedRun`"
    | _ -> scope

/// The ONLY constructor. Throws on the contradiction `validate` names, so a verdict
/// that says green while one of its own plugins says fail cannot be written to disk,
/// because it cannot be built.
///
/// `producedAt` and `producer` are stamped HERE, not passed: they are facts about the
/// process doing the constructing, and a caller that could supply them is a caller that
/// could lie about them.
let create
    (command: Command)
    (runReport: TestRunReport)
    (tree: TreeHash.Tree)
    (outcome: Outcome)
    (exitCode: int)
    (plugins: PluginVerdict list)
    (suites: SuiteVerdict list)
    (comparison: CheckComparison)
    // AUTOMATION-303. EVERY failing ledger diagnostic the exit code was computed from,
    // untruncated — `create` does the truncating, so a caller cannot quietly report a
    // short list as the whole story. Required, not optional: a caller that could omit it
    // is a caller that can produce a red naming nothing, which is the defect.
    (redCauses: RedCause list)
    : Verdict =
    let candidate =
        { producedAt = DateTime.UtcNow
          command = command
          producer = Producer.current ()
          runId = runReport.RunId
          treeHash = tree.Hash
          treeHashAlgorithm = TreeHash.Algorithm
          treeFileCount = tree.FileCount
          scope = runReport.Scope
          outcome = outcome
          exitCode = exitCode
          plugins = plugins
          suites = suites
          comparison = comparison
          redCauses = redCauses |> List.truncate MaxRedCauses
          redCauseCount = List.length redCauses
          trigger = runReport.Seeds
          triggerCount = runReport.SeedCount }

    match validate candidate with
    | Ok v -> v
    | Error reason -> invalidArg (nameof outcome) reason

/// Absolute path to the verdict file.
let path (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "verdict.json")

/// Repo-relative path to the verdict file — what the CLI PRINTS, so a reader does not
/// have to translate it before using it.
[<Literal>]
let RelativePath = ".fshw/verdict.json"

// ---------------------------------------------------------------------------
// Serialization
// ---------------------------------------------------------------------------

/// Scope on the wire. UNIFORMLY TAGGED (`kind` always present) rather than
/// "sometimes a string, sometimes an object", so a consumer never has to
/// discriminate a JSON string from a JSON object before it can read a field.
let private scopeJson (scope: TestScope) : obj =
    match scope with
    | FullSuite n ->
        {| kind = "full"
           ranProjects = n
           totalProjects = n |}
        :> obj
    | ImpactFiltered(ran, total) ->
        {| kind = "filtered"
           ranProjects = ran
           totalProjects = total |}
        :> obj
    | NoTestsRun ->
        // NO counts. `TestScope.NoTestsRun` carries none, and `ranProjects: 0,
        // totalProjects: 0` would state, of a repo with six test projects, that it has
        // none. `kind: "none"` already says the only thing that matters: nothing ran.
        {| kind = "none" |} :> obj
    | ScopeUnknown -> {| kind = "unknown" |} :> obj
    // A DISTINCT kind, not folded into "unknown": "the daemon reported no scope" is an
    // ordinary tests-less repo, while "the scope read faulted" is a check that could not
    // see what it was judging, and a consumer must be able to tell them apart.
    | ScopeUnreadable reason ->
        {| kind = "unreadable"
           reason = reason |}
        :> obj

let private outcomeJson (outcome: Outcome) : obj =
    match outcome with
    | Green -> {| kind = "green" |} :> obj
    | Red -> {| kind = "red" |} :> obj
    | Incomplete reason ->
        {| kind = "incomplete"
           reason = reason |}
        :> obj

/// AUTOMATION-259 on the wire. Tagged with `kind` like every other sum in this file, so a
/// consumer reads ONE field and never has to parse prose to learn what happened. `reason`
/// appears only where there is one.
let private divergenceJson (d: Divergence) : obj =
    match d with
    | Divergence.Incomparable reason ->
        {| kind = Divergence.token d
           reason = reason |}
        :> obj
    | Divergence.Agreed
    | Divergence.CheckMissedFailures
    | Divergence.CheckOnlyFailures
    | Divergence.NoImpactScopedRun
    | Divergence.NotRecorded -> {| kind = Divergence.token d |} :> obj

/// The sub-record, nested under `checkComparison` beside its classification. Not spliced
/// into the top level: `scope` and `outcome` up there describe the verdict that was
/// EARNED, and two same-named fields one nesting apart is how a reader lifts the wrong one.
let private checkComparisonJson (c: CheckComparison) : obj =
    {| divergence = divergenceJson c.Divergence
       impactScopedRun =
        (match c.ImpactScoped with
         | Some r ->
             box
                 {| scope = scopeJson r.Scope
                    outcome = outcomeJson r.Outcome
                    failingSuites = r.FailingSuites |}
         | None -> null) |}
    :> obj

let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

/// Render the verdict as JSON. Pure, so the contract is testable without a disk.
let serialize (v: Verdict) : string =
    let payload =
        {| schema = Schema
           producedAt = v.ProducedAt.ToString("O")
           command = Command.token v.Command
           producer =
            {| version = v.Producer.Version
               contentHash = v.Producer.ContentHash |}
           runId =
            (match v.RunId with
             | Some id -> box (id.ToString("N"))
             | None -> null)
           treeHash = v.TreeHash
           treeHashAlgorithm = v.TreeHashAlgorithm
           treeFileCount = v.TreeFileCount
           scope = scopeJson v.Scope
           outcome = outcomeJson v.Outcome
           checkComparison = checkComparisonJson v.Comparison
           exitCode = v.ExitCode
           plugins =
            [ for p in v.Plugins ->
                  {| name = p.Name
                     outcome = PluginOutcome.token p.Outcome
                     elapsedMs =
                      (match p.ElapsedMs with
                       | Some ms -> box ms
                       | None -> null)
                     summary =
                      (match p.Summary with
                       | Some s -> box s
                       | None -> null) |} ]
           suites =
            [ for s in v.Suites ->
                  {| project = s.Project
                     ctrf = s.Ctrf
                     total = s.Total
                     passed = s.Passed
                     failed = s.Failed
                     skipped = s.Skipped |} ]
           // AUTOMATION-303. What reddened this verdict, when the answer is not in
           // `plugins[]`. Always present — an empty array on a green is a statement, and
           // a reader that has to distinguish "no causes" from "this build did not
           // record any" is back where it started.
           reddenedBy =
            [ for c in v.RedCauses ->
                  {| source = c.Source
                     file = c.File
                     severity = c.Severity
                     message = c.Message
                     // AUTOMATION-303. Whether this cause is a claim about the tree on
                     // disk at all. Recorded per cause, not summarised, because the
                     // interesting file is the MIXED one — some real, some not — and a
                     // single flag would force the reader back to guessing which.
                     kind = RedCauseKind.tag c.Kind |} ]
           reddenedByCount = v.RedCauseCount
           trigger = v.Trigger |> List.toArray
           triggerCount = v.TriggerCount |}

    JsonSerializer.Serialize(payload, jsonOptions)

/// Write the verdict ATOMICALLY (temp file + rename). A partial read must be
/// impossible: a consumer polling this file races the daemon by construction, and
/// a half-written verdict that happened to parse would be the worst possible
/// artifact.
let write (repoRoot: string) (v: Verdict) : unit =
    FsHwPaths.atomicWriteAllText (path repoRoot) (serialize v + "\n")

// ---------------------------------------------------------------------------
// Reading it back — the CLI reads the same file it writes. No second truth.
// ---------------------------------------------------------------------------

/// `JsonElement.TryGetProperty` THROWS on a non-object element rather than returning
/// false, so a verdict whose `scope` is a bare string (a hand edit, a future schema)
/// would raise `InvalidOperationException` out of `read`, past its `JsonException`
/// handler, and crash the caller. Here, asking a non-object for a field simply answers
/// "it hasn't got one".
let private tryProp (el: JsonElement) (name: string) : JsonElement option =
    if el.ValueKind <> JsonValueKind.Object then
        None
    else
        match el.TryGetProperty(name) with
        | true, v -> Some v
        | _ -> None

let private tryString (el: JsonElement) (name: string) : string option =
    match tryProp el name with
    | Some v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private tryInt (el: JsonElement) (name: string) : int option =
    match tryProp el name with
    | Some v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt32() with
        | true, n -> Some n
        | _ -> None
    | _ -> None

let private tryInt64 (el: JsonElement) (name: string) : int64 option =
    match tryProp el name with
    | Some v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt64() with
        | true, n -> Some n
        | _ -> None
    | _ -> None

let private parseScope (el: JsonElement) : TestScope =
    let ran = tryInt el "ranProjects"
    let total = tryInt el "totalProjects"

    match tryString el "kind", ran, total with
    | Some "full", Some r, Some t when r > 0 && r = t -> FullSuite t
    | Some "filtered", Some r, Some t -> ImpactFiltered(r, t)
    | Some "none", _, _ -> NoTestsRun
    | Some "unknown", _, _ -> ScopeUnknown
    // Everything else — a kind from another version, a self-contradicting "full",
    // outright garbage. The file said something about its scope and this build cannot
    // read it: `ScopeUnreadable`, round-tripping the reason when there is one. Distinct
    // from `ScopeUnknown` even though both fail closed — see the `ScopeUnreadable` docs.
    | _ ->
        ScopeUnreadable(
            tryString el "reason"
            |> Option.defaultValue "the recorded scope is not a shape this build recognizes"
        )

let private parseOutcome (el: JsonElement) : Outcome option =
    match tryString el "kind" with
    | Some "green" -> Some Green
    | Some "red" -> Some Red
    | Some "incomplete" -> Some(Incomplete(tryString el "reason" |> Option.defaultValue "no reason recorded"))
    | _ -> None

/// AUTOMATION-259, read back. Every way of not getting a classification lands on a case
/// that is NOT `Agreed` — an unreadable comparison is not an agreement, exactly as an
/// unreadable plugin outcome is not a pass.
let private parseDivergence (el: JsonElement) : Divergence =
    match tryString el "kind" with
    | Some "agreed" -> Divergence.Agreed
    | Some "check-missed-failures" -> Divergence.CheckMissedFailures
    | Some "check-only-failures" -> Divergence.CheckOnlyFailures
    | Some "no-impact-scoped-run" -> Divergence.NoImpactScopedRun
    | Some "not-recorded" -> Divergence.NotRecorded
    | Some "incomparable" -> Divergence.Incomparable(tryString el "reason" |> Option.defaultValue "no reason recorded")
    // A token from another version, or none at all. `Incomparable` rather than
    // `NotRecorded`: something WAS recorded here and this build cannot read it, which is a
    // different fact from nobody having recorded anything — and neither is agreement.
    | Some other ->
        Divergence.Incomparable $"the recorded classification is '%s{other}', which this build does not understand"
    | None -> Divergence.Incomparable "the recorded comparison does not say which classification it is"

let private parseImpactScopedRun (el: JsonElement) : ImpactScopedRun option =
    if el.ValueKind <> JsonValueKind.Object then
        None
    else
        let failingSuites =
            match tryProp el "failingSuites" with
            | Some arr when arr.ValueKind = JsonValueKind.Array ->
                arr.EnumerateArray()
                |> Seq.choose (fun e ->
                    if e.ValueKind = JsonValueKind.String then
                        Some(e.GetString())
                    else
                        None)
                |> List.ofSeq
            | _ -> []

        Some
            { Scope = tryProp el "scope" |> Option.map parseScope |> Option.defaultValue ScopeUnknown
              // Fails CLOSED, like every other outcome read in this file: an outcome this
              // build cannot make sense of is an `Incomplete`, never a green, so a
              // malformed sub-record cannot manufacture a "check was clean" reading.
              Outcome =
                tryProp el "outcome"
                |> Option.bind parseOutcome
                |> Option.defaultValue (
                    Incomplete "the recorded impact-scoped outcome is not a shape this build recognizes"
                )
              FailingSuites = failingSuites }

/// The whole `checkComparison` block. An ABSENT block is `NotRecorded` — the verdict
/// predates AUTOMATION-259, which is not corruption and is not agreement.
let private parseCheckComparison (root: JsonElement) : CheckComparison =
    match tryProp root "checkComparison" with
    | Some el when el.ValueKind = JsonValueKind.Object ->
        { Divergence =
            tryProp el "divergence"
            |> Option.map parseDivergence
            |> Option.defaultValue (Divergence.Incomparable "the recorded comparison has no classification")
          ImpactScoped = tryProp el "impactScopedRun" |> Option.bind parseImpactScopedRun }
    | _ -> CheckComparison.notRecorded

/// A plugin outcome token this build does not recognize is NOT dropped and NOT
/// rounded to `Ok` — it becomes `Fail`. An unknown state is not a passing state.
let private parsePluginOutcome (token: string option) : PluginOutcome =
    match token with
    | Some "ok" -> PluginOutcome.Ok
    | Some "warn" -> PluginOutcome.Warn
    | Some "running" -> PluginOutcome.Running
    | Some "wedged" -> PluginOutcome.Wedged
    | Some "timed-out" -> PluginOutcome.TimedOut
    | _ -> PluginOutcome.Fail

let private parsePlugins (root: JsonElement) : Result<PluginVerdict list, string> =
    match tryProp root "plugins" with
    | Some arr when arr.ValueKind = JsonValueKind.Array ->
        arr.EnumerateArray()
        |> Seq.fold
            (fun acc el ->
                match acc with
                | Error e -> Error e
                | Ok xs ->
                    match tryString el "name" with
                    | None -> Error "a plugin entry has no 'name'"
                    | Some name ->
                        Ok(
                            { Name = name
                              Outcome = parsePluginOutcome (tryString el "outcome")
                              // Absent (or `null`) = NOT MEASURED. Never 0.
                              ElapsedMs = tryInt64 el "elapsedMs"
                              Summary = tryString el "summary" }
                            :: xs
                        ))
            (Ok [])
        |> Result.map List.rev
    | _ -> Ok []

/// A suite entry is EVIDENCE — its counts are the whole reason it exists. A missing
/// count is not a zero: `total: 0, failed: 0` reads as "this suite ran cleanly", so
/// manufacturing it from an absent field would be a vacuous green. A suite whose
/// numbers cannot be read makes the whole verdict UNREADABLE, exactly as a missing
/// `treeHash` does.
let private parseSuites (root: JsonElement) : Result<SuiteVerdict list, string> =
    let required (el: JsonElement) (project: string) (name: string) =
        match tryInt el name with
        | Some n -> Ok n
        | None -> Error $"suite '%s{project}' has no '%s{name}' — a count that is absent is not a count of zero"

    match tryProp root "suites" with
    | Some arr when arr.ValueKind = JsonValueKind.Array ->
        arr.EnumerateArray()
        |> Seq.fold
            (fun acc el ->
                match acc with
                | Error e -> Error e
                | Ok xs ->
                    match tryString el "project" with
                    | None -> Error "a suite entry has no 'project'"
                    | Some project ->
                        match
                            required el project "total",
                            required el project "passed",
                            required el project "failed",
                            required el project "skipped"
                        with
                        | Ok total, Ok passed, Ok failed, Ok skipped ->
                            Ok(
                                { Project = project
                                  Ctrf = tryString el "ctrf" |> Option.defaultValue ""
                                  Total = total
                                  Passed = passed
                                  Failed = failed
                                  Skipped = skipped }
                                :: xs
                            )
                        | Error e, _, _, _
                        | _, Error e, _, _
                        | _, _, Error e, _
                        | _, _, _, Error e -> Error e)
            (Ok [])
        |> Result.map List.rev
    | _ -> Ok []

/// What was found at `.fshw/verdict.json`. Total — every way of not having a
/// usable verdict is a NAMED case, so none of them can be quietly rounded to
/// "green".
[<RequireQualifiedAccess>]
type Reading =
    /// No verdict has ever been written here.
    | Missing
    /// A file is present but is not a verdict this build understands (truncated,
    /// hand-edited, or written by a future schema). NEVER treated as green.
    | Unreadable of reason: string
    | Found of Verdict

/// Read the verdict file. Never throws: a consumer of a state file must not have
/// to reason about exceptions to learn that there is no state.
let read (repoRoot: string) : Reading =
    let p = path repoRoot

    if not (File.Exists p) then
        Reading.Missing
    else
        try
            use doc = JsonDocument.Parse(File.ReadAllText p)
            let root = doc.RootElement

            let schema = tryString root "schema" |> Option.defaultValue "(none)"

            if schema <> Schema then
                Reading.Unreadable $"schema is '%s{schema}', this build understands '%s{Schema}'"
            else
                let outcome = tryProp root "outcome" |> Option.bind parseOutcome

                let scope =
                    tryProp root "scope"
                    |> Option.map parseScope
                    |> Option.defaultValue ScopeUnknown

                match tryString root "treeHash", outcome, parsePlugins root, parseSuites root with
                | _, _, Error e, _ -> Reading.Unreadable e
                | _, _, _, Error e -> Reading.Unreadable e
                | Some treeHash, Some outcome, Ok plugins, Ok suites ->
                    // Rehydrated through the SAME invariant `create` enforces (see
                    // `validate`), so a hand-edited green over a failing plugin is
                    // refused on the way in.
                    let rehydrated =
                        { producedAt =
                            tryString root "producedAt"
                            |> Option.bind (fun s ->
                                match
                                    DateTime.TryParse(
                                        s,
                                        Globalization.CultureInfo.InvariantCulture,
                                        Globalization.DateTimeStyles.AdjustToUniversal
                                    )
                                with
                                | true, dt -> Some dt
                                | _ -> None)
                            |> Option.defaultValue DateTime.MinValue
                          command =
                            (if tryString root "command" = Some "confirm" then
                                 Confirm
                             else
                                 Check)
                          producer =
                            (match tryProp root "producer" with
                             | Some el ->
                                 { DaemonIdentity.Version =
                                     tryString el "version" |> Option.defaultValue "unknown-version"
                                   // An absent producer hash is UNHASHABLE, not a wildcard.
                                   // A verdict that cannot say which binary made it has not
                                   // established its provenance, and `Producer.same` refuses it.
                                   DaemonIdentity.ContentHash =
                                     tryString el "contentHash" |> Option.defaultValue ContentHash.UnhashableContent }
                             | None ->
                                 { DaemonIdentity.Version = "unknown-version"
                                   DaemonIdentity.ContentHash = ContentHash.UnhashableContent })
                          runId =
                            tryString root "runId"
                            |> Option.bind (fun s ->
                                match Guid.TryParse s with
                                | true, g -> Some g
                                | _ -> None)
                          treeHash = treeHash
                          treeHashAlgorithm = tryString root "treeHashAlgorithm" |> Option.defaultValue "(none)"
                          treeFileCount = tryInt root "treeFileCount" |> Option.defaultValue 0
                          scope = scope
                          outcome = outcome
                          exitCode = tryInt root "exitCode" |> Option.defaultValue 2
                          plugins = plugins
                          suites = suites
                          comparison = parseCheckComparison root
                          // AUTOMATION-303. Absent in verdicts written before the field
                          // existed, which is silence, not a claim that nothing reddened
                          // them — so it does not make those files Unreadable.
                          redCauses =
                            (match tryProp root "reddenedBy" with
                             | Some arr when arr.ValueKind = JsonValueKind.Array ->
                                 arr.EnumerateArray()
                                 |> Seq.map (fun el ->
                                     { Source = tryString el "source" |> Option.defaultValue "(unnamed source)"
                                       File = tryString el "file" |> Option.defaultValue ""
                                       Severity = tryString el "severity" |> Option.defaultValue "error"
                                       Message = tryString el "message" |> Option.defaultValue ""
                                       // A verdict written before `kind` existed said
                                       // nothing about attribution, and "said nothing"
                                       // must read as the CONSERVATIVE answer — a red is
                                       // about this tree until something proves it is
                                       // not. An unknown tag lands here too.
                                       Kind =
                                         tryString el "kind"
                                         |> Option.bind RedCauseKind.ofTag
                                         |> Option.defaultValue AboutThisTree })
                                 |> Seq.toList
                             | _ -> [])
                          redCauseCount = tryInt root "reddenedByCount" |> Option.defaultValue 0
                          // Absent in every verdict written before this field existed,
                          // so it defaults to empty rather than making those files
                          // Unreadable. A diagnostic may not retroactively invalidate
                          // verdicts that are otherwise perfectly good.
                          trigger =
                            (match tryProp root "trigger" with
                             | Some el when el.ValueKind = JsonValueKind.Array ->
                                 el.EnumerateArray()
                                 |> Seq.choose (fun e ->
                                     if e.ValueKind = JsonValueKind.String then
                                         Some(e.GetString())
                                     else
                                         None)
                                 |> Seq.toList
                             | _ -> [])
                          triggerCount = tryInt root "triggerCount" |> Option.defaultValue 0 }

                    match validate rehydrated with
                    | Ok v -> Reading.Found v
                    | Error reason -> Reading.Unreadable reason
                | None, _, _, _ -> Reading.Unreadable "no treeHash — the verdict does not say which tree it verified"
                | _, None, _, _ -> Reading.Unreadable "no recognizable outcome"
        with
        | :? JsonException as ex -> Reading.Unreadable $"not valid JSON: %s{ex.Message}"
        | :? IOException as ex -> Reading.Unreadable $"could not be read: %s{ex.Message}"
        | :? UnauthorizedAccessException as ex -> Reading.Unreadable $"could not be read: %s{ex.Message}"

/// Does this verdict answer the question the reader is asking?
///
/// The ONE check that keeps a green from a different tree out of a merge decision.
[<RequireQualifiedAccess>]
type Applicability =
    /// The verdict describes the tree that is on disk right now, and was produced by
    /// the fshw that is running right now.
    | Applies
    /// It describes a DIFFERENT tree. Not a green, not a red — not an answer.
    | StaleTree of verdictTree: string * currentTree: string
    /// It was produced by a DIFFERENT fshw. The tree may well match; the claim was
    /// still made by a binary whose behaviour we are not running. A stale daemon's
    /// green about an unchanged tree is exactly the hole this closes.
    | StaleProducer of verdictBinary: string * currentBinary: string

/// Total. Content equality on the SUBJECT and on the PRODUCER — no mtimes, no
/// "close enough", no heuristic that can fail open.
///
/// The producer is checked FIRST: a verdict from a binary we are not running tells us
/// nothing about the tree, whatever its treeHash says.
let applicability (currentProducer: Producer) (currentTreeHash: string) (v: Verdict) : Applicability =
    if not (Producer.same v.Producer currentProducer) then
        Applicability.StaleProducer(
            DaemonIdentity.BinaryIdentity.render v.Producer,
            DaemonIdentity.BinaryIdentity.render currentProducer
        )
    elif String.Equals(v.TreeHash, currentTreeHash, StringComparison.Ordinal) then
        Applicability.Applies
    else
        Applicability.StaleTree(v.TreeHash, currentTreeHash)

/// What `fshw verdict` found. Total, and in one-to-one correspondence with an
/// exit code — the codes 0/1/2/3 are exactly `check`'s, so a verdict READ and a
/// verdict EARNED are reported identically, and 4/5 name the two ways of having
/// no answer at all.
[<RequireQualifiedAccess>]
type Report =
    | Applies of Verdict
    /// The verdict does not apply. ONE exit code (4), because the consequence is
    /// identical — do not reuse it — while `reason` says which provenance link broke:
    /// a different tree, or a different binary.
    | Stale of Verdict * reason: string
    | NoVerdict of reason: string

/// Exit code for `fshw verdict`. Exhaustive: a new case is a compile error here,
/// never a silent fall-through to 0.
///
///   0/1/2/3 — the verdict applies, and these are `check`'s own codes.
///   4       — STALE: a verdict exists but describes a different tree.
///   5       — no usable verdict on disk.
let reportExitCode (r: Report) : int =
    match r with
    | Report.Applies v -> v.ExitCode
    | Report.Stale _ -> 4
    | Report.NoVerdict _ -> 5

/// Read the verdict and decide whether it applies to the tree on disk RIGHT NOW.
///
/// Touches no socket, starts no daemon, triggers no run — reading cannot perturb.
let report (repoRoot: string) (excludePatterns: string list) : Report =
    match read repoRoot with
    | Reading.Missing ->
        Report.NoVerdict $"no verdict at %s{RelativePath} — run `fshw check` (or `fshw confirm` for a merge)"
    | Reading.Unreadable reason -> Report.NoVerdict $"%s{RelativePath} is unusable: %s{reason}"
    | Reading.Found v ->
        let currentTree = TreeHash.compute repoRoot excludePatterns
        let currentProducer = Producer.current ()

        match applicability currentProducer currentTree.Hash v with
        | Applicability.Applies -> Report.Applies v
        | Applicability.StaleTree(verdictTree, current) ->
            Report.Stale(
                v,
                $"stale: the verdict describes a different tree (verdict %s{verdictTree}, current %s{current})"
            )
        | Applicability.StaleProducer(verdictBinary, current) ->
            Report.Stale(
                v,
                $"stale: the verdict was produced by a DIFFERENT fshw binary (verdict %s{verdictBinary}, current %s{current}) — a stale daemon's green about an unchanged tree is still a stale daemon's green"
            )

// ---------------------------------------------------------------------------
// Has `confirm`'s evidence ALREADY been earned?
// ---------------------------------------------------------------------------

/// Is this verdict, ON ITS OWN TERMS, the claim `confirm` makes?
///
/// A GREEN outcome earned by a run that covered the FULL SUITE. Nothing else: an
/// impact-filtered green is not the claim a merge needs, and `NoTestsRun` is not
/// evidence at all.
///
/// Deliberately BLIND to which verb produced it. `check` and `confirm` differ in exactly
/// one thing — whether a non-full scope may reach `Clean` (`CheckVerdict.verdict`) — so a
/// `check` that RECORDS `FullSuite` + `Green` has, by construction, identical evidence.
/// A merge rests on the EVIDENCE, not the name of the command that produced it.
let isFullSuiteGreen (v: Verdict) : bool =
    match v.Outcome with
    | Red
    | Incomplete _ -> false
    | Green -> TestScope.isFullSuite v.Scope

/// What `confirm` finds when it asks "do I already have the answer?" — BEFORE it starts a
/// daemon, sets a scope, or runs a test.
[<RequireQualifiedAccess>]
type PriorConfirmation =
    /// A full-suite green, earned over THIS tree by THIS binary. `confirm` may report it
    /// and exit 0 without running anything.
    | StillApplies of Verdict
    /// Nothing on disk discharges this `confirm`. Go and earn it.
    | MustEarn

/// THE fast path — and the only thing in fshw allowed to carry a green across a process
/// boundary.
///
/// A cached PLUGIN RESULT may not do this (see `TestPrunePlugin.cacheKeyFor`): its key is
/// a merkle of changed symbols and build outcome, which does not pin the tree, so a hit
/// proves nothing about what is on disk. The verdict file is content-addressed to its
/// SUBJECT (`treeHash`) and to its PRODUCER (the binary's content hash), both compared
/// for byte equality, with no mtime, no heuristic, and no way to fail open
/// (`applicability`).
///
/// Everything that is not an exact match is `MustEarn` — a stale tree, a stale producer, a
/// filtered green, a red, an incomplete, an unreadable file, no file. Total, and every
/// one of those roads leads to the same place: run the suite.
let priorConfirmation (repoRoot: string) (excludePatterns: string list) : PriorConfirmation =
    match report repoRoot excludePatterns with
    | Report.Applies v when isFullSuiteGreen v -> PriorConfirmation.StillApplies v
    | Report.Applies _
    | Report.Stale _
    | Report.NoVerdict _ -> PriorConfirmation.MustEarn

/// One line for a human: WHAT still applies, WHEN it was earned, and on WHAT evidence.
///
/// It names the two things that had to match, so the green is auditable rather than
/// taken on trust.
let describeStillApplies (v: Verdict) : string =
    let earnedAt = v.ProducedAt.ToLocalTime().ToString("HH:mm")

    let evidence =
        let suite =
            match v.Scope with
            | FullSuite n when n = 1 -> "full suite, 1 project"
            | FullSuite n -> $"full suite, %d{n} projects"
            // Unreachable: `isFullSuiteGreen` is the only door in. Named, not
            // wildcarded, so a future scope cannot slip through as "full suite".
            | ImpactFiltered _
            | NoTestsRun
            | ScopeUnknown
            | ScopeUnreadable _ -> TestScope.describe v.Scope

        // A count that is absent is NOT a count of zero — say nothing rather than
        // report "0 passed" over a suite whose reports this build could not read.
        match v.Suites with
        | [] -> suite
        | suites ->
            let passed = suites |> List.sumBy (fun s -> s.Passed)
            $"%s{suite}, %d{passed} passed"

    $"the verdict from %s{earnedAt} still applies\n            (treeHash + producer match; %s{evidence})"

/// The machine-readable envelope `fshw verdict` prints on stdout.
///
/// It carries `applies` — it never prints a bare verdict that a reader could
/// mistake for a current one. A stale green must not LOOK like a green.
let serializeReport (r: Report) : string =
    let payload: obj =
        match r with
        | Report.Applies v ->
            {| schema = "fshw-verdict-report-v1"
               applies = true
               verdict = JsonSerializer.Deserialize<JsonElement>(serialize v) |}
            :> obj
        | Report.Stale(v, reason) ->
            {| schema = "fshw-verdict-report-v1"
               applies = false
               reason = reason
               verdict = JsonSerializer.Deserialize<JsonElement>(serialize v) |}
            :> obj
        | Report.NoVerdict reason ->
            {| schema = "fshw-verdict-report-v1"
               applies = false
               reason = reason |}
            :> obj

    JsonSerializer.Serialize(payload, jsonOptions)

// ---------------------------------------------------------------------------
// Building a verdict from what the check observed
// ---------------------------------------------------------------------------

/// Project the daemon's per-plugin statuses into verdict lines. Plugins with
/// nothing to report (idle, never run) are omitted rather than invented as passes.
let pluginVerdicts
    (warningsAreFailures: bool)
    (now: DateTime)
    (statuses: Map<string, ParsedPluginStatus>)
    : PluginVerdict list =
    statuses
    |> Map.toList
    |> List.choose (fun (name, parsed) ->
        pluginOutcomeOf warningsAreFailures now parsed
        |> Option.map (fun outcome ->
            { Name = name
              Outcome = outcome
              // No `LastRun` (a plugin still Running, or a synthetic terminal from a
              // cache replay) means NO MEASUREMENT — not a zero-length run.
              ElapsedMs = parsed.LastRun |> Option.map (fun r -> int64 r.Elapsed.TotalMilliseconds)
              Summary = parsed.LastRun |> Option.bind (fun r -> r.Summary) }))

/// The CTRF reports THIS RUN produced — the files in the run's own directory.
///
/// Membership is DECLARED (the daemon told us the run id; the reports are the files
/// in `.fshw/test-runs/<runId>/`), never inferred from mtimes: in a shared pile with
/// no manifest you cannot tell which files are yours, nor an empty listing apart from
/// a cleaned-up one.
///
/// No run id ⇒ no run happened ⇒ no suites. Not "we couldn't find any".
///
/// The counts are copied INLINE into the verdict. The path is provenance, not the
/// carrier: a number that depends on a second file still being readable is a number
/// that can evaporate.
let suiteVerdicts (repoRoot: string) (runId: Guid option) : SuiteVerdict list =
    match runId with
    | None -> []
    | Some id ->
        Ctrf.reportsForRun repoRoot id
        |> List.map (fun r ->
            { Project = r.Project
              Ctrf = Path.GetRelativePath(repoRoot, r.Path).Replace('\\', '/')
              Total = r.Summary.Total
              Passed = r.Summary.Passed
              Failed = r.Summary.Failed
              Skipped = r.Summary.Skipped })

/// AUTOMATION-259. Turn what a transport observed just BEFORE `confirm` escalated into the
/// sub-record the verdict carries.
///
/// GRADED IN `InnerLoop`, and that is the whole design. `CheckVerdict.verdict` with
/// `Confirmation` routes every non-full scope to `UnearnedScope` — a REFUSAL, not a
/// reading — so a sub-record graded that way would say "no verdict" on every single sample
/// and the comparison would never have two answers to compare. `InnerLoop` over the SAME
/// inputs is, by definition, what `check` computes: same statuses, same ledger, same
/// coverage, same scope.
///
/// Costs one directory listing of a run whose reports are already on disk, on the
/// escalating-`confirm` path only. Nothing is re-run: the reading itself was in memory.
let impactScopedRun (repoRoot: string) (runReport: TestRunReport) (inputs: CheckVerdict.CheckInputs) : ImpactScopedRun =
    { Scope = runReport.Scope
      Outcome = outcomeOfCheck (CheckVerdict.verdict CheckVerdict.InnerLoop inputs)
      FailingSuites =
        suiteVerdicts repoRoot runReport.RunId
        |> List.filter (fun s -> s.Failed > 0)
        |> List.map (fun s -> s.Project) }
