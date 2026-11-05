/// Trace-based selection, in shadow: which tests a recorded per-test trace says must run
/// now. A test runs when it has no trace (R1), when a symbol version its trace names no
/// longer matches the tree or is gone (R2), when a file input its trace names hashes
/// differently now (R3), or when its trace is incomplete (R4). A test with a trace only
/// under another environment fingerprint has no trace under this one, so it is R1.
///
/// `withFallbacks` then adds R6 (a changed seed the probes cannot represent: the static walk
/// from it) and R7 (a changed file no trace input claims: the static file rules). Both only
/// add tests.
///
/// This module only answers and compares; a host records the answer next to its static
/// selection and still runs the static one. A project is eligible when at least
/// `EligibilityThreshold` of its tests have a complete trace.
module TestPrune.Trace.TraceSelect

open System
open TestPrune.Trace.TraceStore

/// Why a changed symbol cannot be verified by traces, so R6 walks it statically.
type FallReason =
    /// No probe of the latest weave joins to it: test-assembly code woven at call sites only,
    /// abstract members, record fields, code added since the weave.
    | NotProbed
    /// Marked `inline`: its probe never fires where it was inlined.
    | Inline
    /// A `[<Literal>]`: it has no code at all.
    | Literal
    /// It ran only in a type initializer no test inherited, so no trace names it.
    | StaticInitOnly
    /// It appeared in or vanished from a trace while nothing the test ran or read changed.
    | OrderUnstable

/// Why trace selection selects a test.
type Rule =
    /// R1: no trace under the fingerprint read.
    | NoTrace
    /// R2: a symbol the trace names changed or no longer exists.
    | SymbolChanged of symbol: string
    /// R3: a file input the trace names hashes differently now.
    | InputChanged of kind: string * key: string
    /// R4: the trace is incomplete, with its reason codes.
    | Incomplete of reasons: string list
    /// R6: the static walk from a changed symbol traces cannot verify reaches the test.
    | StaticFallback of reason: FallReason
    /// R7: a changed file no trace input claims, which the static file rules select the
    /// test for (the first such file is named).
    | UntracedInput of file: string

/// One project's trace selection.
type Selection =
    {
        TestProject: string
        /// Test keys considered.
        Universe: Set<string>
        /// Selected test key -> the rules that selected it, in rule order (only the first
        /// changed symbol and input are named).
        Selected: Map<string, Rule list>
        /// Share of the universe with a complete trace.
        CompleteShare: float
        /// At least `EligibilityThreshold` of the universe has a complete trace. Only an
        /// eligible project could ever be selected by trace; an ineligible one keeps its
        /// static selection, so its misses do not count against the promotion bar.
        Eligible: bool
        /// The seeds R6 walked statically, and why each could not be verified.
        FallbackSeeds: Map<string, FallReason>
    }

/// The complete-trace share at which a project is eligible (phase 1's census bar).
[<Literal>]
let EligibilityThreshold = 0.99

/// The rule's code, `R1`..`R7` (R5, a trace only under another fingerprint, is read as R1).
let ruleCode =
    function
    | NoTrace -> "R1"
    | SymbolChanged _ -> "R2"
    | InputChanged _ -> "R3"
    | Incomplete _ -> "R4"
    | StaticFallback _ -> "R6"
    | UntracedInput _ -> "R7"

/// A fall reason's code.
let fallCode =
    function
    | NotProbed -> "not-probed"
    | Inline -> "inline"
    | Literal -> "literal"
    | StaticInitOnly -> "static-init"
    | OrderUnstable -> "order-unstable"

/// Select from traces. `traceOf` reads a test's trace under the fingerprint in use;
/// `currentVersion` is a symbol's content hash in the tree now (`None` when it is gone);
/// `currentInput` is a (kind, key) input's hash now (`None` when it cannot be computed,
/// which counts as changed). Each distinct symbol and input is looked up once.
let select
    (testProject: string)
    (universe: string list)
    (traceOf: string -> StoredTrace option)
    (currentVersion: string -> string option)
    (currentInput: string * string -> string option)
    : Selection =
    let versionNow = Collections.Generic.Dictionary<string, string option>()
    let inputNow = Collections.Generic.Dictionary<string * string, string option>()

    let memo (cache: Collections.Generic.Dictionary<'k, 'v>) (f: 'k -> 'v) (k: 'k) =
        match cache.TryGetValue k with
        | true, v -> v
        | _ ->
            let v = f k
            cache.[k] <- v
            v

    let keys = universe |> List.distinct

    let judged =
        keys
        |> List.map (fun key ->
            match traceOf key with
            | None -> key, false, [ NoTrace ]
            | Some t ->
                let changedSymbol =
                    t.Symbols
                    |> Seq.tryFind (fun (name, hash) -> memo versionNow currentVersion name <> Some hash)
                    |> Option.map (fst >> SymbolChanged)

                let changedInput =
                    t.Inputs
                    |> Seq.tryFind (fun (kind, k, hash) -> memo inputNow currentInput (kind, k) <> Some hash)
                    |> Option.map (fun (kind, k, _) -> InputChanged(kind, k))

                let incomplete = if t.Complete then None else Some(Incomplete t.Reasons)

                key, t.Complete, List.choose id [ changedSymbol; changedInput; incomplete ])

    let completeShare =
        if keys.IsEmpty then
            1.0
        else
            float (judged |> List.filter (fun (_, complete, _) -> complete) |> List.length)
            / float keys.Length

    { TestProject = testProject
      Universe = Set.ofList keys
      Selected =
        judged
        |> List.filter (fun (_, _, rules) -> not rules.IsEmpty)
        |> List.map (fun (key, _, rules) -> key, rules)
        |> Map.ofList
      CompleteShare = completeShare
      Eligible = completeShare >= EligibilityThreshold
      FallbackSeeds = Map.empty }

/// Rule code -> how many selected tests that rule selected.
let ruleCounts (s: Selection) : Map<string, int> =
    s.Selected
    |> Map.toList
    |> List.collect (fun (_, rules) -> rules |> List.map ruleCode |> List.distinct)
    |> List.countBy id
    |> Map.ofList

/// Why a changed symbol cannot be verified by traces, if it cannot. A measured fact about
/// the symbol first, then what the index says, then the absence of a probe.
let fallReason (facts: SymbolFacts) (attributesOf: string -> string list) (symbol: string) : FallReason option =
    let attrs = attributesOf symbol

    if facts.Unstable.Contains symbol then
        Some OrderUnstable
    elif facts.StaticInit.Contains symbol then
        Some StaticInitOnly
    elif attrs |> List.exists (fun a -> a = "LiteralAttribute" || a = "Literal") then
        Some Literal
    elif attrs |> List.contains TestPrune.AstAnalyzer.InlineAttributeName then
        Some Inline
    elif not (facts.Probed.Contains symbol) then
        Some NotProbed
    else
        None

/// What R6 and R7 read beside the traces: the run's seeds and changed files, the project's
/// symbol facts and claimed inputs, and the static rules they fall back to.
type Fallbacks =
    {
        /// The changed symbols the static selection walked from.
        Seeds: string list
        /// Repo-relative, '/'-separated files changed since the last run.
        ChangedFiles: string list
        Facts: SymbolFacts
        /// (kind, key) of every input the project's traces hold (`Store.ClaimedInputs`).
        ClaimedInputs: (string * string) list
        /// Whether a file is an indexed source, whose changes are symbols.
        IsIndexed: string -> bool
        /// A symbol's attribute names in the index.
        AttributesOf: string -> string list
        /// The static walk from seeds: the test keys it reaches, in any project.
        StaticWalk: string list -> string list
        /// The static file rules over changed files: the test keys they select, or `None`
        /// when they select every test (a project file changed).
        FileFallback: string list -> string list option
    }

/// A changed file is claimed when a trace input names it, or a listing names its directory
/// (`list`) or any directory above it (`list-deep`).
let private claimedBy (claims: (string * string) list) =
    let keys = claims |> List.map snd |> Set.ofList

    let listed kind =
        claims
        |> List.choose (fun (k, key) -> if k = kind then Some key else None)
        |> Set.ofList

    let shallow, deep = listed "list", listed "list-deep"

    // Every directory above `file`, outermost first.
    let parents (file: string) =
        let parts = file.Split '/'
        [ for i in 1 .. parts.Length - 1 -> String.Join("/", parts, 0, i) ]

    fun (file: string) ->
        let ps = parents file

        keys.Contains file
        || (not ps.IsEmpty && shallow.Contains(List.last ps))
        || ps |> List.exists deep.Contains

/// R6 and R7 over a selection: each changed seed traces cannot verify (`fallReason`) adds
/// the tests the static walk from it reaches; each changed file that is neither an indexed
/// source nor claimed by a trace input adds what the static file rules select. Only this
/// project's universe is ever added to, and nothing selected is removed.
let withFallbacks (f: Fallbacks) (s: Selection) : Selection =
    let selected = Collections.Generic.Dictionary<string, Rule list>(s.Selected)

    let add (rule: Rule) (key: string) =
        if s.Universe.Contains key then
            selected.[key] <-
                match selected.TryGetValue key with
                | true, rules when List.contains rule rules -> rules
                | true, rules -> rules @ [ rule ]
                | _ -> [ rule ]

    let fallbacks =
        f.Seeds
        |> List.distinct
        |> List.choose (fun seed -> fallReason f.Facts f.AttributesOf seed |> Option.map (fun r -> seed, r))

    for reason, group in fallbacks |> List.groupBy snd do
        f.StaticWalk(List.map fst group) |> List.iter (add (StaticFallback reason))

    let claimed = claimedBy f.ClaimedInputs

    let unclaimed =
        f.ChangedFiles
        |> List.distinct
        |> List.filter (fun file -> not (f.IsIndexed file || claimed file))

    match unclaimed with
    | [] -> ()
    | first :: _ ->
        match f.FileFallback unclaimed with
        | None -> s.Universe |> Seq.iter (add (UntracedInput first))
        | Some keys -> keys |> List.iter (add (UntracedInput first))

    { s with
        Selected = selected |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
        FallbackSeeds = Map.ofList fallbacks }

/// The class part of a test key (`<project>|<class>`).
let private classOf (key: string) =
    match key.LastIndexOf '|' with
    | -1 -> key
    | i -> key.Substring(0, i)

/// Static against trace selection, by test key and by class.
type Comparison =
    {
        StaticTests: Set<string>
        TraceTests: Set<string>
        StaticOnly: Set<string>
        TraceOnly: Set<string>
        StaticClasses: Set<string>
        TraceClasses: Set<string>
        /// The universe's tests in the static side's classes: what it launches, since a host
        /// launches by class.
        StaticClassTests: int
        /// The universe's tests in the trace side's classes.
        TraceClassTests: int
    }

/// Compare the static selection's test keys with the trace selection.
let compare (staticSelection: string list) (s: Selection) : Comparison =
    let st = Set.ofList staticSelection
    let tr = s.Selected |> Map.keys |> Set.ofSeq
    let staticClasses = st |> Set.map classOf
    let traceClasses = tr |> Set.map classOf

    let launched (classes: Set<string>) =
        s.Universe |> Seq.filter (classOf >> classes.Contains) |> Seq.length

    { StaticTests = st
      TraceTests = tr
      StaticOnly = Set.difference st tr
      TraceOnly = Set.difference tr st
      StaticClasses = staticClasses
      TraceClasses = traceClasses
      StaticClassTests = launched staticClasses
      TraceClassTests = launched traceClasses }

/// The universe key a failed row's key names: the key itself, or, for a theory row, whose
/// reported method carries its arguments (`m(x: 1, y: "a")`), the key before that
/// argument list. Only an existing universe key is ever returned, so a method whose own
/// name holds parentheses still matches exactly. `None` when no universe key is named.
let universeKeyOf (universe: Set<string>) (failedKey: string) : string option =
    if universe.Contains failedKey then
        Some failedKey
    elif failedKey.EndsWith(")", StringComparison.Ordinal) then
        failedKey
        |> Seq.mapi (fun i c -> i, c)
        |> Seq.filter (fun (_, c) -> c = '(')
        |> Seq.map (fun (i, _) -> failedKey.Substring(0, i))
        |> Seq.tryFind universe.Contains
    else
        None

/// The failed tests trace selection left out, by universe key: each failed while its trace
/// said nothing it depends on changed. A key no universe key names is not judged here;
/// `unknownFailures` lists those.
let verifiedFailureMisses (s: Selection) (failedKeys: string list) : string list =
    failedKeys
    |> List.choose (universeKeyOf s.Universe)
    |> List.filter (fun k -> not (s.Selected.ContainsKey k))
    |> List.distinct
    |> List.sort

/// The failed keys of `s`'s project that name no key of its universe, so no miss can be
/// judged for them. A caller says these out loud: dropping them silently would hide a miss
/// behind a key mismatch.
let unknownFailures (s: Selection) (failedKeys: string list) : string list =
    failedKeys
    |> List.filter (fun k -> k.StartsWith(s.TestProject + "|", StringComparison.Ordinal))
    |> List.filter (universeKeyOf s.Universe >> Option.isNone)
    |> List.distinct
    |> List.sort


/// On a run where every test ran: of the failed tests the universe knows, how many the
/// trace selection selected, as `r/t`. Recall 1.0 (`t/t`, including `0/0`) is the bar.
let traceRecall (s: Selection) (failedKeys: string list) : string =
    let failed = failedKeys |> List.choose (universeKeyOf s.Universe) |> List.distinct
    let reached = failed |> List.filter s.Selected.ContainsKey
    $"%d{reached.Length}/%d{failed.Length}"

/// Whether shadow read the right environment's traces. `predicted` is the fingerprint it
/// read, `actual` the one the run recorded under (`None`: the run recorded no traces), and
/// `predictedHadTraces` whether anything was stored under `predicted`.
let fingerprintOutcome (predicted: string) (actual: string option) (predictedHadTraces: bool) : FingerprintOutcome =
    match actual with
    | None -> NotTraced
    | Some a when a = predicted -> PredictionHit
    | Some _ when predictedHadTraces -> UnsafeMiss
    | Some _ -> SafeMiss

/// Selected tests whose only rule is an incomplete trace, and whose every incomplete reason
/// is an untraced child process: what a trusted-executable allowlist would release.
let childUntracedTests (s: Selection) : int =
    s.Selected
    |> Map.filter (fun _ rules ->
        rules
        |> List.forall (function
            | Incomplete reasons ->
                not reasons.IsEmpty
                && reasons
                   |> List.forall (fun r -> r.StartsWith("child-process-untraced:", StringComparison.Ordinal))
            | _ -> false))
    |> Map.count

/// One log line per project per run.
let logLine (s: Selection) (c: Comparison) (selectMs: int64) : string =
    let rules =
        ruleCounts s
        |> Map.toList
        |> List.map (fun (code, n) -> $"%s{code} %d{n}")
        |> String.concat " "

    let rules = if rules = "" then "no rule fired" else rules

    $"trace-shadow: %s{s.TestProject} static %d{c.StaticTests.Count}/%d{c.StaticClasses.Count}c, trace %d{c.TraceTests.Count}/%d{c.TraceClasses.Count}c, universe %d{s.Universe.Count}, trace-only %d{c.TraceOnly.Count}, static-only %d{c.StaticOnly.Count}, %s{rules}, complete %.3f{s.CompleteShare}, %d{selectMs} ms"

/// What a host knows about one run, beside the selection and the comparison, when it
/// stores the row.
type RowFacts =
    {
        RunId: string
        Mode: ShadowRunMode
        /// `tests.traces.record` as written: "full-runs" or "every-run".
        RecordPolicy: string
        /// The fingerprint whose traces the selection read.
        Fingerprint: string
        /// The fingerprint the run recorded the project's traces under, if any.
        ActualFingerprint: string option
        /// Whether any trace was stored under `Fingerprint`.
        PredictedHadTraces: bool
        RecordedAt: DateTimeOffset
        SelectMs: int64
        /// `traceRecall` on a full run, else `n/a: <why>`.
        TraceRecall: string
    }

/// The row a host stores for one comparison.
let row (f: RowFacts) (s: Selection) (c: Comparison) : ShadowRow =
    let first200 (xs: Set<string>) = xs |> Set.toList |> List.truncate 200

    { RunId = f.RunId
      TestProject = s.TestProject
      EnvFingerprint = f.Fingerprint
      RecordedAt = f.RecordedAt
      Universe = s.Universe.Count
      CompleteShare = s.CompleteShare
      StaticTests = c.StaticTests.Count
      TraceTests = c.TraceTests.Count
      StaticClasses = c.StaticClasses.Count
      TraceClasses = c.TraceClasses.Count
      RuleCounts = ruleCounts s
      StaticOnly = first200 c.StaticOnly
      TraceOnly = first200 c.TraceOnly
      SelectMs = f.SelectMs
      Mode = f.Mode
      RecordPolicy = f.RecordPolicy
      Eligible = Some s.Eligible
      ActualFingerprint = f.ActualFingerprint
      FingerprintOutcome = fingerprintOutcome f.Fingerprint f.ActualFingerprint f.PredictedHadTraces
      TraceRecall = f.TraceRecall
      StaticClassTests = c.StaticClassTests
      TraceClassTests = c.TraceClassTests
      ChildUntracedTests = childUntracedTests s
      FallbackSeeds = s.FallbackSeeds |> Map.map (fun _ r -> fallCode r) }
