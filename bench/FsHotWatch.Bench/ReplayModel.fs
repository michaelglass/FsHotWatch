/// The history-replay harness's pure parts (see bench/README.md, "History replay").
///
/// The harness checks out each commit of a fixed range in a dedicated workspace, runs
/// the consumer's check twice (check records on, then forced with them off), and files
/// one ledger line per finished step. Everything here is a function of its inputs, so the
/// plan, the diff, the ledger and the summary are unit-tested; `ReplayRun` does the I/O.
module FsHotWatch.Bench.ReplayModel

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

// ─── JSON helpers ────────────────────────────────────────────────────────────

let private str (node: JsonNode) (key: string) : string =
    match node[key] with
    | null -> ""
    | v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>()
    | _ -> ""

let private int' (node: JsonNode) (key: string) : int option =
    match node[key] with
    | null -> None
    | v when v.GetValueKind() = JsonValueKind.Number -> Some(v.GetValue<int>())
    | _ -> None

let private bool' (node: JsonNode) (key: string) : bool =
    match node[key] with
    | null -> false
    | v when v.GetValueKind() = JsonValueKind.True -> true
    | _ -> false

let private strings (node: JsonNode) (key: string) : string list =
    match node[key] with
    | :? JsonArray as a ->
        [ for x in a do
              if not (isNull x) && x.GetValueKind() = JsonValueKind.String then
                  x.GetValue<string>() ]
    | _ -> []

let private stringArray (xs: string list) : JsonArray =
    JsonArray(xs |> List.map (fun x -> JsonValue.Create x :> JsonNode) |> List.toArray)

let private tryParseNode (text: string) : JsonNode option =
    try
        match JsonNode.Parse text with
        | null -> None
        | n -> Some n
    with :? JsonException ->
        None

// ─── Plan and steps ──────────────────────────────────────────────────────────

/// What a step does at its commit: a clean replay, or one seeded fault.
[<RequireQualifiedAccess>]
type Phase =
    | Clean
    | Fault of kind: string

module Phase =
    let token (phase: Phase) : string =
        match phase with
        | Phase.Clean -> "clean"
        | Phase.Fault kind -> $"fault:%s{kind}"

    let parse (token: string) : Phase option =
        if token = "clean" then
            Some Phase.Clean
        elif token.StartsWith("fault:", StringComparison.Ordinal) && token.Length > 6 then
            Some(Phase.Fault(token.Substring 6))
        else
            None

/// A seeded fault: text appended to one repository-relative file, in a throwaway change.
type FaultSpec =
    { Kind: string
      File: string
      Append: string }

/// The replay's fixed range and fault sampling, chosen once and kept in `plan.json`, so a
/// resumed run replays the same commits even when the branch has moved since.
type Plan =
    {
        Source: string
        /// Oldest first.
        Commits: string list
        /// Seed faults at every `FaultEvery`-th commit (0 = never).
        FaultEvery: int
        /// The index of the first sampled commit.
        FaultOffset: int
        /// Seed every kind at each sampled commit, instead of one kind in rotation.
        AllKinds: bool
        Faults: FaultSpec list
    }

module Plan =
    let Schema = "fshw-replay-plan-v1"

    let toJson (plan: Plan) : string =
        let root = JsonObject()
        root["schema"] <- Schema
        root["source"] <- plan.Source
        root["commits"] <- stringArray plan.Commits
        root["faultEvery"] <- plan.FaultEvery
        root["faultOffset"] <- plan.FaultOffset
        root["allKinds"] <- plan.AllKinds

        root["faults"] <-
            JsonArray(
                plan.Faults
                |> List.map (fun f ->
                    let o = JsonObject()
                    o["kind"] <- f.Kind
                    o["file"] <- f.File
                    o["append"] <- f.Append
                    o :> JsonNode)
                |> List.toArray
            )

        root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    /// Read the faults file a run is given: `[{"kind","file","append"}]`.
    let faultsOfJson (text: string) : Result<FaultSpec list, string> =
        match tryParseNode text with
        | Some(:? JsonArray as a) ->
            Ok
                [ for f in a do
                      if not (isNull f) then
                          { Kind = str f "kind"
                            File = str f "file"
                            Append = str f "append" } ]
        | _ -> Error "a faults file is a JSON array of {kind, file, append}"

    let ofJson (text: string) : Result<Plan, string> =
        match tryParseNode text with
        | Some root when str root "schema" = Schema ->
            let faults =
                match root["faults"] with
                | null -> Ok []
                | n -> faultsOfJson (n.ToJsonString())

            faults
            |> Result.map (fun faults ->
                { Source = str root "source"
                  Commits = strings root "commits"
                  FaultEvery = int' root "faultEvery" |> Option.defaultValue 0
                  FaultOffset = int' root "faultOffset" |> Option.defaultValue 0
                  AllKinds = bool' root "allKinds"
                  Faults = faults })
        | Some _ -> Error $"not a %s{Schema} file"
        | None -> Error "a plan file is not JSON"

type Step =
    { Index: int
      Commit: string
      Phase: Phase }

/// A step's identity in the ledger: its commit and its phase.
let stepKey (step: Step) : string * string = step.Commit, Phase.token step.Phase

/// Every step of a plan in the order it runs: each commit's clean step, then that
/// commit's seeded faults when it is sampled.
let steps (plan: Plan) : Step list =
    let kinds = List.length plan.Faults

    [ for index, commit in List.indexed plan.Commits do
          yield
              { Index = index
                Commit = commit
                Phase = Phase.Clean }

          let k = index - plan.FaultOffset

          if plan.FaultEvery > 0 && kinds > 0 && k >= 0 && k % plan.FaultEvery = 0 then
              let chosen =
                  if plan.AllKinds then
                      plan.Faults
                  else
                      [ plan.Faults[(k / plan.FaultEvery) % kinds] ]

              for f in chosen do
                  yield
                      { Index = index
                        Commit = commit
                        Phase = Phase.Fault f.Kind } ]

/// The steps a resumed run still owes: those the ledger holds no finished line for.
/// Whether a step also runs the forced re-check: every seeded fault, and the clean step of
/// every `every`-th commit (0 = faults only).
let forcedDue (every: int) (step: Step) : bool =
    match step.Phase with
    | Phase.Fault _ -> true
    | Phase.Clean -> every > 0 && step.Index % every = 0

let pending (all: Step list) (completed: Set<string * string>) : Step list =
    all |> List.filter (fun s -> not (completed.Contains(stepKey s)))

// ─── Diagnostics ─────────────────────────────────────────────────────────────

/// One diagnostic or finding, as the two runs are compared on it.
type Diagnostic =
    {
        Plugin: string
        /// Checkout-relative where the source names the checkout; otherwise as recorded.
        File: string
        Line: int
        Column: int
        Severity: string
        /// The rule's code, `""` when the source does not name one.
        Code: string
        Message: string
    }

type Diff =
    { OnlyReplay: Diagnostic list
      OnlyForced: Diagnostic list }

module Diagnostics =
    /// One `.fshw/errors/<plugin>--<sanitized absolute path>.json` file. The file name is
    /// the only place the plugin and the source file are recorded, and the source path is
    /// sanitized (`/` → `-`), so the checkout prefix is removed in its sanitized form.
    let parseErrorFile (checkoutRoot: string) (fileName: string) (content: string) : Diagnostic list =
        let name =
            if fileName.EndsWith(".json", StringComparison.Ordinal) then
                fileName.Substring(0, fileName.Length - 5)
            else
                fileName

        let cut = name.IndexOf("--", StringComparison.Ordinal)

        if cut <= 0 then
            []
        else
            let plugin = name.Substring(0, cut)
            let rest = name.Substring(cut + 2)

            let prefix =
                FsHotWatch.StringHelpers.sanitizeFileName (checkoutRoot.TrimEnd('/', '\\') + "/")

            let file =
                if rest.StartsWith(prefix, StringComparison.Ordinal) then
                    rest.Substring prefix.Length
                else
                    rest

            match tryParseNode content with
            | Some(:? JsonArray as entries) ->
                [ for e in entries do
                      if not (isNull e) then
                          { Plugin = plugin
                            File = file
                            Line = int' e "line" |> Option.defaultValue 0
                            Column = int' e "column" |> Option.defaultValue 0
                            Severity = str e "severity"
                            Code = str e "code"
                            Message = str e "message" } ]
            | _ -> []

    /// `fshw findings --json`: its outcome token and its findings. Anything printed before
    /// the JSON object (config lines on a shared stream) is skipped.
    let parseFindings (text: string) : Result<string * Diagnostic list, string> =
        let first = text.IndexOf '{'
        let last = text.LastIndexOf '}'

        if first < 0 || last < first then
            Error "no findings JSON in the output"
        else
            match tryParseNode (text.Substring(first, last - first + 1)) with
            | Some root ->
                let findings =
                    match root["findings"] with
                    | :? JsonArray as a ->
                        [ for f in a do
                              if not (isNull f) then
                                  { Plugin = "findings"
                                    File = str f "file"
                                    Line = int' f "line" |> Option.defaultValue 0
                                    Column = int' f "column" |> Option.defaultValue 0
                                    Severity = str f "severity"
                                    Code = str f "code"
                                    Message = str f "message" } ]
                    | _ -> []

                Ok(str root "outcome", findings)
            | None -> Error "the findings output is not JSON"

    /// The multiset difference of the two runs over the compared plugins. Order is not a
    /// difference; a diagnostic reported twice by one run and once by the other is.
    let diff (plugins: Set<string>) (replay: Diagnostic list) (forced: Diagnostic list) : Diff =
        let counted xs =
            xs
            |> List.filter (fun d -> plugins.Contains d.Plugin)
            |> List.countBy id
            |> Map.ofList

        let r = counted replay
        let f = counted forced

        let minus (a: Map<Diagnostic, int>) (b: Map<Diagnostic, int>) =
            [ for KeyValue(d, n) in a do
                  let extra = n - (b |> Map.tryFind d |> Option.defaultValue 0)

                  for _ in 1..extra do
                      d ]

        { OnlyReplay = minus r f
          OnlyForced = minus f r }

    /// The test runs a `verdict.json` names, in its order: the CTRF reports a disagreement is
    /// traced through, and which the workspace prunes as later runs land.
    let verdictRunIds (verdictJson: string) : string list =
        match tryParseNode verdictJson with
        | Some root ->
            match root["runs"] with
            | :? JsonArray as runs ->
                [ for r in runs do
                      if not (isNull r) then
                          match str r "runId" with
                          | "" -> ()
                          | id -> id ]
                |> List.distinct
            | _ -> []
        | None -> []

    let agrees (diff: Diff) : bool =
        List.isEmpty diff.OnlyReplay && List.isEmpty diff.OnlyForced

// ─── Shadow lines (ADR-059 §7) ───────────────────────────────────────────────

type ShadowLine =
    { Command: string
      Direction: string
      Agree: bool
      Today: string
      Lookup: string
      Missing: string list
      Failing: string list
      Recorded: int option
      Tree: string }

module Shadow =
    let parse (line: string) : ShadowLine option =
        match tryParseNode line with
        | Some root when not (isNull root["command"]) ->
            let today = root["today"]
            let lookup = root["lookup"]

            Some
                { Command = str root "command"
                  Direction = str root "direction"
                  Agree = bool' root "agree"
                  Today = (if isNull today then "" else str today "outcome")
                  Lookup = (if isNull lookup then "" else str lookup "outcome")
                  Missing = (if isNull lookup then [] else strings lookup "missing")
                  Failing = (if isNull lookup then [] else strings lookup "failing")
                  Recorded = int' root "recorded"
                  Tree = str root "tree" }
        | _ -> None

    /// The kinds of requirement the lookup found no record for (`test`, `plugin`, …).
    let missingKinds (line: ShadowLine) : string list =
        line.Missing
        |> List.map (fun m ->
            match m.IndexOf ':' with
            | -1 -> m
            | i -> m.Substring(0, i))
        |> List.distinct
        |> List.sort

    /// A disagreement's class: the direction, the command, both outcomes, and what the
    /// lookup was missing or found failing. Two lines in one class are one defect until
    /// shown otherwise.
    let classOf (line: ShadowLine) : string =
        let detail =
            [ if not line.Missing.IsEmpty then
                  "missing " + String.Join("+", missingKinds line)
              if not line.Failing.IsEmpty then
                  "failing " + String.Join("+", List.sort line.Failing) ]

        String.Join(
            ", ",
            [ $"%s{line.Direction} %s{line.Command}: today %s{line.Today}"
              $"lookup %s{line.Lookup}" ]
            @ detail
        )

    /// The lines a run appended to a log that held `before` lines when it started. A log
    /// now shorter than that was replaced, and is read whole.
    let since (before: int) (lines: string list) : string list =
        if List.length lines < before then
            lines
        else
            List.skip before lines

// ─── The ledger ──────────────────────────────────────────────────────────────

type VerdictInfo =
    { Command: string
      Outcome: string
      Exit: int
      Tree: string }

/// One run of the consumer's check at a step, and what it left behind.
type RunResult =
    {
        /// `replay` (records on) or `forced` (records off).
        Mode: string
        Exit: int
        TimedOut: bool
        ElapsedMs: int64
        /// The verdict this run published, `None` when it published none.
        Verdict: VerdictInfo option
        Shadow: ShadowLine list
        /// How many diagnostics the error ledger held after the run.
        Diagnostics: int
        /// `fshw findings --json`'s outcome.
        Findings: string option
        /// The daemon's `check-records` / `options-records` log lines from this run.
        RecordsLog: string list
        Artifacts: string
    }

type FaultResult =
    { Kind: string
      File: string
      ReplayRed: bool
      LookupRed: bool
      ForcedRed: bool }

type Entry =
    { Index: int
      Commit: string
      Phase: Phase
      StartedAt: DateTimeOffset
      FinishedAt: DateTimeOffset
      Replay: RunResult
      Forced: RunResult option
      Diff: Diff
      Fault: FaultResult option }

module Ledger =
    let Schema = "fshw-replay-ledger-v1"

    let private diagnosticNode (d: Diagnostic) : JsonNode =
        let o = JsonObject()
        o["plugin"] <- d.Plugin
        o["file"] <- d.File
        o["line"] <- d.Line
        o["column"] <- d.Column
        o["severity"] <- d.Severity
        o["code"] <- d.Code
        o["message"] <- d.Message
        o

    let private diagnosticOf (n: JsonNode) : Diagnostic =
        { Plugin = str n "plugin"
          File = str n "file"
          Line = int' n "line" |> Option.defaultValue 0
          Column = int' n "column" |> Option.defaultValue 0
          Severity = str n "severity"
          Code = str n "code"
          Message = str n "message" }

    let private shadowNode (s: ShadowLine) : JsonNode =
        let o = JsonObject()
        o["command"] <- s.Command
        o["direction"] <- s.Direction
        o["agree"] <- s.Agree
        o["today"] <- s.Today
        o["lookup"] <- s.Lookup
        o["missing"] <- stringArray s.Missing
        o["failing"] <- stringArray s.Failing
        o["recorded"] <- (s.Recorded |> Option.toNullable)
        o["tree"] <- s.Tree
        o

    let private shadowOf (n: JsonNode) : ShadowLine =
        { Command = str n "command"
          Direction = str n "direction"
          Agree = bool' n "agree"
          Today = str n "today"
          Lookup = str n "lookup"
          Missing = strings n "missing"
          Failing = strings n "failing"
          Recorded = int' n "recorded"
          Tree = str n "tree" }

    let private nodes (f: 'a -> JsonNode) (xs: 'a list) =
        JsonArray(xs |> List.map f |> List.toArray)

    let private items (f: JsonNode -> 'a) (node: JsonNode) (key: string) : 'a list =
        match node[key] with
        | :? JsonArray as a ->
            [ for x in a do
                  if not (isNull x) then
                      f x ]
        | _ -> []

    let private runNode (r: RunResult) : JsonNode =
        let o = JsonObject()
        o["mode"] <- r.Mode
        o["exit"] <- r.Exit
        o["timedOut"] <- r.TimedOut
        o["elapsedMs"] <- r.ElapsedMs

        o["verdict"] <-
            match r.Verdict with
            | Some v ->
                let vo = JsonObject()
                vo["command"] <- v.Command
                vo["outcome"] <- v.Outcome
                vo["exit"] <- v.Exit
                vo["tree"] <- v.Tree
                vo :> JsonNode
            | None -> null

        o["shadow"] <- nodes shadowNode r.Shadow
        o["diagnostics"] <- r.Diagnostics
        o["findings"] <- (r.Findings |> Option.toObj)
        o["recordsLog"] <- stringArray r.RecordsLog
        o["artifacts"] <- r.Artifacts
        o

    let private runOf (n: JsonNode) : RunResult =
        { Mode = str n "mode"
          Exit = int' n "exit" |> Option.defaultValue -1
          TimedOut = bool' n "timedOut"
          ElapsedMs =
            match n["elapsedMs"] with
            | null -> 0L
            | v -> v.GetValue<int64>()
          Verdict =
            match n["verdict"] with
            | null -> None
            | v ->
                Some
                    { Command = str v "command"
                      Outcome = str v "outcome"
                      Exit = int' v "exit" |> Option.defaultValue -1
                      Tree = str v "tree" }
          Shadow = items shadowOf n "shadow"
          Diagnostics = int' n "diagnostics" |> Option.defaultValue 0
          Findings =
            match n["findings"] with
            | null -> None
            | _ -> Some(str n "findings")
          RecordsLog = strings n "recordsLog"
          Artifacts = str n "artifacts" }

    let toLine (e: Entry) : string =
        let o = JsonObject()
        o["schema"] <- Schema
        o["index"] <- e.Index
        o["commit"] <- e.Commit
        o["phase"] <- Phase.token e.Phase
        o["startedAt"] <- e.StartedAt.ToString("o")
        o["finishedAt"] <- e.FinishedAt.ToString("o")
        o["replay"] <- runNode e.Replay

        o["forced"] <-
            match e.Forced with
            | Some f -> runNode f
            | None -> null

        let diff = JsonObject()
        diff["onlyReplay"] <- nodes diagnosticNode e.Diff.OnlyReplay
        diff["onlyForced"] <- nodes diagnosticNode e.Diff.OnlyForced
        o["diff"] <- diff

        o["fault"] <-
            match e.Fault with
            | Some f ->
                let fo = JsonObject()
                fo["kind"] <- f.Kind
                fo["file"] <- f.File
                fo["replayRed"] <- f.ReplayRed
                fo["lookupRed"] <- f.LookupRed
                fo["forcedRed"] <- f.ForcedRed
                fo :> JsonNode
            | None -> null

        o.ToJsonString()

    let parseLine (line: string) : Entry option =
        match tryParseNode line with
        | Some n when str n "schema" = Schema && not (isNull n["replay"]) ->
            match Phase.parse (str n "phase") with
            | None -> None
            | Some phase ->
                let diff = n["diff"]

                Some
                    { Index = int' n "index" |> Option.defaultValue -1
                      Commit = str n "commit"
                      Phase = phase
                      StartedAt = DateTimeOffset.Parse(str n "startedAt")
                      FinishedAt = DateTimeOffset.Parse(str n "finishedAt")
                      Replay = runOf n["replay"]
                      Forced =
                        match n["forced"] with
                        | null -> None
                        | f -> Some(runOf f)
                      Diff =
                        if isNull diff then
                            { OnlyReplay = []; OnlyForced = [] }
                        else
                            { OnlyReplay = items diagnosticOf diff "onlyReplay"
                              OnlyForced = items diagnosticOf diff "onlyForced" }
                      Fault =
                        match n["fault"] with
                        | null -> None
                        | f ->
                            Some
                                { Kind = str f "kind"
                                  File = str f "file"
                                  ReplayRed = bool' f "replayRed"
                                  LookupRed = bool' f "lookupRed"
                                  ForcedRed = bool' f "forcedRed" } }
        | _ -> None

    /// Every whole line; a line torn by an interruption is not a finished step.
    let parse (text: string) : Entry list =
        text.Split('\n') |> Array.toList |> List.choose parseLine

    let completed (entries: Entry list) : Set<string * string> =
        entries |> List.map (fun e -> e.Commit, Phase.token e.Phase) |> Set.ofList

    let read (path: string) : Entry list =
        if File.Exists path then
            parse (File.ReadAllText path)
        else
            []

    /// Append one finished step. A torn tail left by an interruption is cut first, so the
    /// new line is never glued onto it.
    let append (path: string) (entry: Entry) : unit =
        if File.Exists path then
            let text = File.ReadAllText path

            if text.Length > 0 && not (text.EndsWith '\n') then
                let keep = text.LastIndexOf '\n' + 1
                File.WriteAllText(path, text.Substring(0, keep))

        use stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
        use writer = new StreamWriter(stream)
        writer.Write(toLine entry + "\n")
        writer.Flush()
        stream.Flush(true)

// ─── Seeded faults ───────────────────────────────────────────────────────────

module Fault =
    /// Red is a failures verdict. No-verdict (3) and incomplete (2) are not detections.
    let isRed (run: RunResult) : bool =
        match run.Verdict with
        | Some v -> v.Outcome = "red"
        | None -> run.Exit = 1

    let judge (spec: FaultSpec) (replay: RunResult) (forced: RunResult option) : FaultResult =
        { Kind = spec.Kind
          File = spec.File
          ReplayRed = isRed replay
          LookupRed =
            replay.Shadow
            |> List.exists (fun s -> s.Command <> "verdict" && s.Lookup = "red")
          ForcedRed = forced |> Option.map isRed |> Option.defaultValue false }

    /// The file's text with the fault appended after a blank line.
    let applyTo (text: string) (append: string) : string =
        text.TrimEnd('\n') + "\n\n" + append.TrimEnd('\n') + "\n"

// ─── Summary ─────────────────────────────────────────────────────────────────

type FaultTally =
    { Kind: string
      Seeded: int
      ReplayRed: int
      LookupRed: int
      ForcedRed: int
      DiagnosticsAgree: int }

type Summary =
    {
        Commits: int
        CleanSteps: int
        FaultSteps: int
        /// ((run mode, command, direction), lines).
        ShadowByDirection: ((string * string * string) * int) list
        /// (run mode + class, lines), for every line whose direction is not `agree`.
        ShadowDisagreements: (string * int) list
        /// Steps whose replayed and forced diagnostics differ.
        DiffSteps: int
        /// ((plugin, replay-only | forced-only), diagnostics).
        DiffByPlugin: ((string * string) * int) list
        /// Clean steps where one run published green and the other red: (commit, replay,
        /// forced). A forced run on a tree the replay already tested may run no tests and
        /// publish no verdict; that is not a disagreement.
        VerdictMismatches: (string * string * string) list
        Faults: FaultTally list
    }

module Summary =
    let summarize (entries: Entry list) : Summary =
        let runs = entries |> List.collect (fun e -> e.Replay :: Option.toList e.Forced)

        let lines =
            runs |> List.collect (fun r -> r.Shadow |> List.map (fun s -> r.Mode, s))

        let clean = entries |> List.filter (fun e -> e.Phase = Phase.Clean)

        { Commits = entries |> List.map _.Commit |> List.distinct |> List.length
          CleanSteps = clean.Length
          FaultSteps = entries.Length - clean.Length
          ShadowByDirection =
            lines
            |> List.countBy (fun (mode, s) -> mode, s.Command, s.Direction)
            |> List.sort
          ShadowDisagreements =
            lines
            |> List.filter (fun (_, s) -> s.Direction <> "agree")
            |> List.countBy (fun (mode, s) -> mode + " " + Shadow.classOf s)
            |> List.sort
          DiffSteps = entries |> List.filter (fun e -> not (Diagnostics.agrees e.Diff)) |> List.length
          DiffByPlugin =
            entries
            |> List.collect (fun e ->
                (e.Diff.OnlyReplay |> List.map (fun d -> d.Plugin, "replay-only"))
                @ (e.Diff.OnlyForced |> List.map (fun d -> d.Plugin, "forced-only")))
            |> List.countBy id
            |> List.sort
          VerdictMismatches =
            [ for e in clean do
                  match e.Replay.Verdict, e.Forced |> Option.bind _.Verdict with
                  | Some r, Some f when set [ r.Outcome; f.Outcome ] = set [ "green"; "red" ] ->
                      e.Commit, r.Outcome, f.Outcome
                  | _ -> () ]
          Faults =
            entries
            |> List.choose (fun e -> e.Fault |> Option.map (fun f -> f, Diagnostics.agrees e.Diff))
            |> List.groupBy (fun (f, _) -> f.Kind)
            |> List.map (fun (kind, xs) ->
                let count p = xs |> List.filter p |> List.length

                { Kind = kind
                  Seeded = xs.Length
                  ReplayRed = count (fun (f, _) -> f.ReplayRed)
                  LookupRed = count (fun (f, _) -> f.LookupRed)
                  ForcedRed = count (fun (f, _) -> f.ForcedRed)
                  DiagnosticsAgree = count snd })
            |> List.sortBy _.Kind }

    let render (s: Summary) : string =
        let b = Text.StringBuilder()
        let line (text: string) = b.AppendLine text |> ignore

        line $"commits replayed: %d{s.Commits} (%d{s.CleanSteps} clean steps, %d{s.FaultSteps} seeded-fault steps)"

        line ""
        line "shadow lines by run, command and direction:"

        for (mode, command, direction), n in s.ShadowByDirection do
            line $"  %-7s{mode} %-8s{command} %-15s{direction} %d{n}"

        line ""
        line $"shadow disagreements by class: %d{s.ShadowDisagreements |> List.sumBy snd}"

        for cls, n in s.ShadowDisagreements do
            line $"  %4d{n}  %s{cls}"

        line ""

        line
            $"replay-vs-forced diagnostics: %d{s.DiffSteps} step(s) disagree, %d{s.DiffByPlugin |> List.sumBy snd} diagnostic(s)"

        for (plugin, side), n in s.DiffByPlugin do
            line $"  %-12s{plugin} %-12s{side} %d{n}"

        line ""
        line $"replay-vs-forced verdict outcomes that differ on a clean tree: %d{s.VerdictMismatches.Length}"

        for commit, r, f in s.VerdictMismatches do
            line $"  %s{commit}: replay %s{r}, forced %s{f}"

        line ""
        line "seeded faults (kind: seeded / replay red / lookup red / forced red / diagnostics agree):"

        for f in s.Faults do
            line
                $"  %-10s{f.Kind} %d{f.Seeded} / %d{f.ReplayRed} / %d{f.LookupRed} / %d{f.ForcedRed} / %d{f.DiagnosticsAgree}"

        b.ToString()
