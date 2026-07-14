/// The verdict as a FILE — `.fshw/verdict.json`.
///
/// An agent should learn the gate's verdict by READING STATE, not by spawning a
/// CLI, not by grepping a progress display written for a human, and above all not
/// by an act of measurement that can corrupt the thing being measured. That last
/// one is the decisive argument and it is not hypothetical: the `fshw test-rerun`
/// calls an orchestrator made *because the gate looked untrustworthy* were
/// themselves what corrupted the daemon's busy accounting, which is what made the
/// next `check` stamp a content-free green (AUTOMATION-99). The act of measuring
/// created the defect being measured. A file read cannot do that. It also costs
/// nothing — no ~1-3s dotnet spawn per poll.
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
/// ONE TRUTH, TWO SURFACES. The exit code the CLI returns and the `outcome` in
/// this file are computed from the SAME `CheckVerdict.CheckOutcome` value — the
/// human surface and the agent surface cannot disagree, because there is only one
/// of them, rendered twice. There is deliberately NO "agent mode" that changes
/// what a check MEANS: semantics never depend on who the tool thinks is calling
/// (detection is a guess, and guesses fail open). Presentation may adapt to the
/// caller; semantics may not.
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

/// What one plugin contributed to the verdict.
///
/// The SAME value drives the agent-mode status line (`ProgressRenderer`) — a
/// plugin cannot report `ok` on one surface and `fail` on the other.
[<RequireQualifiedAccess>]
type PluginOutcome =
    | Ok
    | Warn
    | Fail
    | TimedOut
    | Running

module PluginOutcome =
    /// The wire token. Total.
    let token (o: PluginOutcome) : string =
        match o with
        | PluginOutcome.Ok -> "ok"
        | PluginOutcome.Warn -> "warn"
        | PluginOutcome.Fail -> "fail"
        | PluginOutcome.TimedOut -> "timed-out"
        | PluginOutcome.Running -> "running"

/// Resolve a plugin's outcome from its parsed status. `None` means "nothing to
/// report" — idle, never run — and such a plugin is omitted rather than being
/// invented as a pass.
let pluginOutcomeOf (warningsAreFailures: bool) (parsed: ParsedPluginStatus) : PluginOutcome option =
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
    | StatusView.Running _ -> Some PluginOutcome.Running
    | StatusView.Failed _ when timedOutLastRun () -> Some PluginOutcome.TimedOut
    | StatusView.Failed _ -> Some PluginOutcome.Fail
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
/// `ElapsedMs` is an OPTION, and that is load-bearing. It was an `int64` defaulting
/// to `0L` when no measurement existed — which is the AUTOMATION-99 signature
/// exactly (`started:` with no `elapsed:`), rebuilt inside the very file that exists
/// to prevent it. **A missing number is not zero.** `0` is a measurement — "this ran
/// instantaneously"; absence is the absence of one, and a reader must be able to
/// tell them apart. Serialized as `null`.
type PluginVerdict =
    { Name: string
      Outcome: PluginOutcome
      ElapsedMs: int64 option
      Summary: string option }

/// A pointer to one test project's CTRF report, plus the counts it carries — so
/// the common questions ("how many ran? how many failed?") are answered without
/// opening a second file, and the deeper ones (which test, what message, what
/// stack) have somewhere to go.
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

/// The verdict itself. `Incomplete` is the honest third answer: nothing is known
/// to be broken, and nothing is known to be sound either — a merge gate that ran
/// impact-filtered tests lands here, and so does a check whose coverage could not
/// be confirmed. It is NEVER laundered into a green.
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
    | CheckVerdict.CheckOutcome.UnearnedScope NoTestsRun ->
        // The emptiest evidence gets the loudest words. "0 projects selected" is an
        // INCOMPLETE check, never a pass, and it must not be renderable as a green on
        // any surface.
        Incomplete "NO TESTS RAN — nothing was verified. This is not a pass; it is an absence of evidence."
    | CheckVerdict.CheckOutcome.UnearnedScope scope ->
        Incomplete
            $"the tests that ran were %s{TestScope.describe scope}, not the full suite — a merge verdict needs the whole suite"

/// The IDENTITY OF THE BINARY that produced a verdict.
///
/// `treeHash` content-addresses the verdict's SUBJECT — a green about a different
/// tree is detectably inapplicable. That closes half of a provenance chain. This
/// closes the other half: a green produced by a DIFFERENT (older, buggier) fshw is
/// equally inapplicable, and without this the check is bypassable at the file layer —
/// a stale daemon writes a verdict for an unchanged tree, `treeHash` matches, and the
/// verdict reads as current.
///
/// Content-address the subject AND the producer, or the chain has a hole in the
/// middle. (AUTOMATION-147 makes the same argument for the daemon's own handshake;
/// this is the file-layer half of it, and both should ultimately share one
/// `ContentHash` policy — they now do.)
type Producer =
    {
        /// File name of the producing assembly (diagnostic; never the identity).
        Binary: string
        /// SHA-256 of its bytes. THIS is the identity.
        Hash: string
    }

module Producer =
    /// The fshw binary running right now.
    ///
    /// Falls back to a NON-MATCHING sentinel if the assembly cannot be located or
    /// read — so an identity we could not establish never silently equals one we
    /// could. Fail closed: an unknown producer is not the producer you want.
    let current () : Producer =
        let asm =
            try
                Reflection.Assembly.GetEntryAssembly()
                |> Option.ofObj
                |> Option.map (fun a -> a.Location)
                |> Option.filter (String.IsNullOrEmpty >> not)
            with _ ->
                None

        match asm with
        | Some path ->
            { Binary = Path.GetFileName path
              Hash = ContentHash.ofFile path }
        | None ->
            { Binary = "(unknown)"
              Hash = ContentHash.UnreadableSentinel }

    /// Do two producers refer to the same binary? An UNREADABLE identity never
    /// matches — not even itself — because two binaries we could not read are not
    /// thereby the same binary.
    let same (a: Producer) (b: Producer) : bool =
        ContentHash.isReadable a.Hash
        && ContentHash.isReadable b.Hash
        && String.Equals(a.Hash, b.Hash, StringComparison.Ordinal)

/// Which verb produced the verdict, and therefore what it is allowed to claim.
/// `check` is the inner loop (impact-scoped, honest that it is); `gate` is the
/// merge gate (unfiltered, evidence-required).
type Command =
    | Check
    | Gate

module Command =
    let token (c: Command) : string =
        match c with
        | Check -> "check"
        | Gate -> "gate"

    let ofCheckMode (mode: CheckVerdict.CheckMode) : Command =
        match mode with
        | CheckVerdict.InnerLoop -> Check
        | CheckVerdict.MergeGate -> Gate

/// The on-disk verdict.
type Verdict =
    {
        ProducedAt: DateTime
        Command: Command
        /// The fshw binary that produced this verdict. A verdict from a different
        /// binary does not apply, however well its `treeHash` matches.
        Producer: Producer
        /// The test run this verdict's suites came from — i.e. the directory they
        /// live in (`.fshw/test-runs/<runId>/`). `None` when NO test run happened,
        /// which is a fact the verdict states rather than a silence for the reader
        /// to decode.
        RunId: Guid option
        TreeHash: string
        TreeHashAlgorithm: string
        TreeFileCount: int
        Scope: TestScope
        Outcome: Outcome
        /// The exit code the producing command returned. Carried so the file and
        /// the process agree in the record, not just by convention.
        ExitCode: int
        Plugins: PluginVerdict list
        Suites: SuiteVerdict list
    }

/// Absolute path to the verdict file.
let path (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "verdict.json")

/// Repo-relative path to the verdict file — what the CLI PRINTS, because a
/// pointer you have to translate before you can use it is a pointer you ignore.
[<Literal>]
let RelativePath = ".fshw/verdict.json"

// ---------------------------------------------------------------------------
// Serialization
// ---------------------------------------------------------------------------

/// Scope on the wire. UNIFORMLY TAGGED (`kind` always present) rather than
/// "sometimes a string, sometimes an object": a consumer that must first
/// discriminate a JSON string from a JSON object before it can read a field is a
/// consumer that will reach for a regex.
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
        {| kind = "none"
           ranProjects = 0
           totalProjects = 0 |}
        :> obj
    | ScopeUnknown -> {| kind = "unknown" |} :> obj

let private outcomeJson (outcome: Outcome) : obj =
    match outcome with
    | Green -> {| kind = "green" |} :> obj
    | Red -> {| kind = "red" |} :> obj
    | Incomplete reason ->
        {| kind = "incomplete"
           reason = reason |}
        :> obj

let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

/// Render the verdict as JSON. Pure, so the contract is testable without a disk.
let serialize (v: Verdict) : string =
    let payload =
        {| schema = Schema
           producedAt = v.ProducedAt.ToString("O")
           command = Command.token v.Command
           producer =
            {| binary = v.Producer.Binary
               hash = v.Producer.Hash |}
           runId =
            (match v.RunId with
             | Some id -> box (id.ToString("N"))
             | None -> null)
           treeHash = v.TreeHash
           treeHashAlgorithm = v.TreeHashAlgorithm
           treeFileCount = v.TreeFileCount
           scope = scopeJson v.Scope
           outcome = outcomeJson v.Outcome
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
                     skipped = s.Skipped |} ] |}

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

/// `JsonElement.TryGetProperty` THROWS on a non-object element rather than
/// returning false — so a verdict whose `scope` is a bare string (an older shape,
/// a hand edit, a future schema) would raise `InvalidOperationException` out of
/// `read`, past its `JsonException` handler, and crash the caller.
///
/// A reader of a state file must not have to reason about exceptions to learn that
/// there is no usable state. Ask a non-object for a field and the answer is simply
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
    | _ -> ScopeUnknown

let private parseOutcome (el: JsonElement) : Outcome option =
    match tryString el "kind" with
    | Some "green" -> Some Green
    | Some "red" -> Some Red
    | Some "incomplete" -> Some(Incomplete(tryString el "reason" |> Option.defaultValue "no reason recorded"))
    | _ -> None

/// A plugin outcome token this build does not recognize is NOT dropped and NOT
/// rounded to `Ok` — it becomes `Fail`. An unknown state is not a passing state.
let private parsePluginOutcome (token: string option) : PluginOutcome =
    match token with
    | Some "ok" -> PluginOutcome.Ok
    | Some "warn" -> PluginOutcome.Warn
    | Some "running" -> PluginOutcome.Running
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
/// count is not a zero: `total: 0, failed: 0` reads as "this suite ran cleanly", and
/// manufacturing that from an absent field is the vacuous green in miniature. So a
/// suite whose numbers cannot be read makes the whole verdict UNREADABLE, exactly as
/// a missing `treeHash` does. Fail closed, or do not bother having a verdict file.
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
                    Reading.Found
                        { ProducedAt =
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
                          Command =
                            (if tryString root "command" = Some "gate" then
                                 Gate
                             else
                                 Check)
                          Producer =
                            (match tryProp root "producer" with
                             | Some el ->
                                 { Binary = tryString el "binary" |> Option.defaultValue "(unknown)"
                                   // An absent producer hash is UNREADABLE, not a
                                   // wildcard — a verdict that cannot say which binary
                                   // made it has not established its provenance.
                                   Hash = tryString el "hash" |> Option.defaultValue ContentHash.UnreadableSentinel }
                             | None ->
                                 { Binary = "(unknown)"
                                   Hash = ContentHash.UnreadableSentinel })
                          RunId =
                            tryString root "runId"
                            |> Option.bind (fun s ->
                                match Guid.TryParse s with
                                | true, g -> Some g
                                | _ -> None)
                          TreeHash = treeHash
                          TreeHashAlgorithm = tryString root "treeHashAlgorithm" |> Option.defaultValue "(none)"
                          TreeFileCount = tryInt root "treeFileCount" |> Option.defaultValue 0
                          Scope = scope
                          Outcome = outcome
                          ExitCode = tryInt root "exitCode" |> Option.defaultValue 2
                          Plugins = plugins
                          Suites = suites }
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
        Applicability.StaleProducer(v.Producer.Hash, currentProducer.Hash)
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
        Report.NoVerdict $"no verdict at %s{RelativePath} — run `fshw check` (or `fshw gate` for a merge)"
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
let pluginVerdicts (warningsAreFailures: bool) (statuses: Map<string, ParsedPluginStatus>) : PluginVerdict list =
    statuses
    |> Map.toList
    |> List.choose (fun (name, parsed) ->
        pluginOutcomeOf warningsAreFailures parsed
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
/// in `.fshw/test-runs/<runId>/`), never inferred from mtimes. That is the whole
/// point: a shared pile with no manifest is unreadable in both directions — you
/// cannot tell which files are yours, and you cannot tell an empty listing apart
/// from a cleaned-up one.
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
