/// Trace-based selection, in shadow: which tests a recorded per-test trace says must run
/// now. A test runs when it has no trace (R1), when a symbol version its trace names no
/// longer matches the tree or is gone (R2), when a file input its trace names hashes
/// differently now (R3), or when its trace is incomplete (R4). A test with a trace only
/// under another environment fingerprint has no trace under this one, so it is R1.
///
/// This module only answers and compares; a host records the answer next to its static
/// selection and still runs the static one. Not yet implemented from the phase-2 plan: R6
/// (static fallback for code the probes cannot see), R7 (changed files no trace claims),
/// fingerprint prediction, and eligibility. Until they are, a trace-only miss is expected
/// where those rules would have fired, which is what the shadow rows are there to measure.
module TestPrune.Trace.TraceSelect

open System
open TestPrune.Trace.TraceStore

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
    }

/// The rule's code, `R1`..`R4`.
let ruleCode =
    function
    | NoTrace -> "R1"
    | SymbolChanged _ -> "R2"
    | InputChanged _ -> "R3"
    | Incomplete _ -> "R4"

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

    { TestProject = testProject
      Universe = Set.ofList keys
      Selected =
        judged
        |> List.filter (fun (_, _, rules) -> not rules.IsEmpty)
        |> List.map (fun (key, _, rules) -> key, rules)
        |> Map.ofList
      CompleteShare =
        if keys.IsEmpty then
            1.0
        else
            float (judged |> List.filter (fun (_, complete, _) -> complete) |> List.length)
            / float keys.Length }

/// Rule code -> how many selected tests that rule selected.
let ruleCounts (s: Selection) : Map<string, int> =
    s.Selected
    |> Map.toList
    |> List.collect (fun (_, rules) -> rules |> List.map ruleCode |> List.distinct)
    |> List.countBy id
    |> Map.ofList

/// The class part of a test key (`<project>|<class>`).
let private classOf (key: string) =
    match key.LastIndexOf '|' with
    | -1 -> key
    | i -> key.Substring(0, i)

/// Static against trace selection, by test key and by class.
type Comparison =
    { StaticTests: Set<string>
      TraceTests: Set<string>
      StaticOnly: Set<string>
      TraceOnly: Set<string>
      StaticClasses: Set<string>
      TraceClasses: Set<string> }

/// Compare the static selection's test keys with the trace selection.
let compare (staticSelection: string list) (s: Selection) : Comparison =
    let st = Set.ofList staticSelection
    let tr = s.Selected |> Map.keys |> Set.ofSeq

    { StaticTests = st
      TraceTests = tr
      StaticOnly = Set.difference st tr
      TraceOnly = Set.difference tr st
      StaticClasses = st |> Set.map classOf
      TraceClasses = tr |> Set.map classOf }

/// The failed tests trace selection left out: each failed while its trace said nothing it
/// depends on changed. Keys outside the selection's universe are not judged.
let verifiedFailureMisses (s: Selection) (failedKeys: string list) : string list =
    failedKeys
    |> List.filter (fun k -> s.Universe.Contains k && not (s.Selected.ContainsKey k))
    |> List.distinct
    |> List.sort

/// One log line per project per run.
let logLine (s: Selection) (c: Comparison) (selectMs: int64) : string =
    let rules =
        ruleCounts s
        |> Map.toList
        |> List.map (fun (code, n) -> $"%s{code} %d{n}")
        |> String.concat " "

    let rules = if rules = "" then "no rule fired" else rules

    $"trace-shadow: %s{s.TestProject} static %d{c.StaticTests.Count}/%d{c.StaticClasses.Count}c, trace %d{c.TraceTests.Count}/%d{c.TraceClasses.Count}c, universe %d{s.Universe.Count}, trace-only %d{c.TraceOnly.Count}, static-only %d{c.StaticOnly.Count}, %s{rules}, complete %.3f{s.CompleteShare}, %d{selectMs} ms"

/// The row a host stores for one comparison.
let row (runId: string) (fingerprint: string) (at: DateTimeOffset) (selectMs: int64) (s: Selection) (c: Comparison) =
    let first200 (xs: Set<string>) = xs |> Set.toList |> List.truncate 200

    { RunId = runId
      TestProject = s.TestProject
      EnvFingerprint = fingerprint
      RecordedAt = at
      Universe = s.Universe.Count
      CompleteShare = s.CompleteShare
      StaticTests = c.StaticTests.Count
      TraceTests = c.TraceTests.Count
      StaticClasses = c.StaticClasses.Count
      TraceClasses = c.TraceClasses.Count
      RuleCounts = ruleCounts s
      StaticOnly = first200 c.StaticOnly
      TraceOnly = first200 c.TraceOnly
      SelectMs = selectMs }
