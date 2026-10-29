/// Shadow mode for the lookup verdict (ADR-059 §7).
///
/// At every verdict publication, the run's results are projected into outcome records and
/// filed in the outcome store; then the verdict the STORE would give for this tree — from
/// the configured requirements, not from the run — is looked up and compared with the one
/// this build published. One line per publication goes to `.fshw/shadow/lookup-verdict.jsonl`.
///
/// That log is a DIAGNOSTIC, not a ledger: nothing reads it to decide anything, deleting it
/// changes no answer, and no failure here may change today's exit code or `verdict.json`.
/// It exists to measure the switch criterion (ADR-059 §8).
///
/// The first shadow keys every action by the verdict's own tree hash and producer — exact
/// parity with what `verdict.json` is addressed to — so an early disagreement points at
/// bookkeeping, not at key design. Re-keying tests on `K_P` is a later step.
///
/// The store lives under the checkout (`.fshw/shadow/outcomes`) for now: tree-keyed records
/// gain nothing from being shared across checkouts, and a box-wide store written by every
/// test that publishes a verdict in a temp repository is how 1100 leaked 98k directories.
module FsHotWatch.Cli.ShadowVerdict

open System
open System.IO
open System.Text.Json.Nodes
open FsHotWatch

/// Shadow work is on unless this is `0`.
[<Literal>]
let KillSwitchEnvVar = "FSHW_OUTCOME_SHADOW"

let enabledBy (value: string) = isNull value || value.Trim() <> "0"

[<Literal>]
let LogSchema = "fshw-shadow-v1"

let directory (repoRoot: string) =
    Path.Combine(FsHwPaths.root repoRoot, "shadow")

let logPath (repoRoot: string) =
    Path.Combine(directory repoRoot, "lookup-verdict.jsonl")

let storeRoot (repoRoot: string) =
    Path.Combine(directory repoRoot, "outcomes")

/// The class two verdicts are compared in.
[<RequireQualifiedAccess>]
type Class =
    | Green
    | Red
    | Missing

let private classToken =
    function
    | Class.Green -> "green"
    | Class.Red -> "red"
    | Class.Missing -> "missing"

/// One test project's report in one run.
type Suite =
    {
        RunId: string option
        Project: string
        Counts: OutcomeStore.Counts
        /// The suite's own outcome: no failed test, or a starved re-run that passed alone.
        Passed: bool
    }

/// What one publication says, reduced to what the shadow needs.
type Observation =
    {
        /// `check` or `confirm`.
        Command: string
        Tree: string
        Producer: string
        Worktree: string
        Today: Class
        /// Today's outcome tag (`green`, `red`, `incomplete`, `model-unavailable`).
        TodayOutcome: string
        ExitCode: int
        /// Today's verdict concluded green or red. Only a concluded publication files
        /// records: an incomplete one is a statement that nothing was established.
        Concluded: bool
        /// Each plugin and its terminal outcome; `None` for one still running.
        Plugins: (string * OutcomeStore.Outcome option) list
        /// A diagnostic the exit code was computed from reddened the run.
        DiagnosticsFailed: bool
        GradedRunId: string option
        /// The graded run's scope was the full suite.
        FullSuite: bool
        /// The graded run's scope was impact-filtered.
        ImpactFiltered: bool
        Suites: Suite list
        RecordedAt: DateTimeOffset
    }

let pluginKey (o: Observation) (name: string) =
    ActionKey.create "plugin" [ "tree", o.Tree; "producer", o.Producer; "plugin", name ]

let diagnosticsKey (o: Observation) =
    ActionKey.create "diagnostics" [ "tree", o.Tree; "producer", o.Producer ]

/// A test project's run over the tree, in `scope` (`full` or `impact`).
let testKey (o: Observation) (project: string) (scope: string) =
    ActionKey.create "test" [ "tree", o.Tree; "producer", o.Producer; "project", project; "scope", scope ]

/// "This project once passed in full under this producer": ADR-021's baseline, read from
/// the store instead of `full-suite-baseline.json`.
let baselineKey (producer: string) (project: string) =
    ActionKey.create "test-baseline" [ "producer", producer; "project", project ]

[<Literal>]
let FullScope = "full"

[<Literal>]
let ImpactScope = "impact"

/// R(c, T): what a green for this command over this tree requires (ADR-059 §3). Plugins
/// are the ones the daemon reported; test projects are the configured ones.
let requirements (o: Observation) (testProjects: string list) : LookupVerdict.Requirement list =
    [ for (name, _) in o.Plugins do
          LookupVerdict.Requirement.single ("plugin:" + name) (pluginKey o name)
      LookupVerdict.Requirement.single "diagnostics" (diagnosticsKey o)
      for project in testProjects do
          let full = [ testKey o project FullScope ]

          { LookupVerdict.Requirement.Name = "test:" + project
            LookupVerdict.Requirement.Alternatives =
              if o.Command = "confirm" then
                  [ full ]
              else
                  [ full; [ testKey o project ImpactScope; baselineKey o.Producer project ] ] } ]

/// The outcome records a publication files. None for one that did not conclude.
///
/// A suite of the graded run under a full-suite scope is a `full` record; every other
/// suite is `impact`. A full pass also records the project's baseline. Under an
/// impact-filtered scope, a configured project with no suite at all is recorded as an
/// `impact` pass of zero tests: today's selector chose nothing in it, and the shadow takes
/// today's selection as given.
let recordsOf (o: Observation) (testProjects: string list) : OutcomeStore.Record list =
    if not o.Concluded then
        []
    else
        let record (key: ActionKey.ActionKey) subject outcome counts runId : OutcomeStore.Record =
            { Key = key
              Subject = subject
              Outcome = outcome
              Counts = counts
              DurationMs = None
              Producer =
                { Binary = o.Producer
                  Worktree = o.Worktree
                  RunId = runId }
              RecordedAt = o.RecordedAt }

        let outcomeOf passed =
            if passed then
                OutcomeStore.Outcome.Pass
            else
                OutcomeStore.Outcome.Fail

        let suites =
            [ for s in o.Suites do
                  let scope =
                      if o.FullSuite && s.RunId = o.GradedRunId then
                          FullScope
                      else
                          ImpactScope

                  record (testKey o s.Project scope) s.Project (outcomeOf s.Passed) (Some s.Counts) s.RunId

                  if scope = FullScope && s.Passed then
                      record (baselineKey o.Producer s.Project) s.Project OutcomeStore.Outcome.Pass None s.RunId ]

        let ran = o.Suites |> List.map _.Project |> Set.ofList

        let unselected =
            if o.ImpactFiltered then
                [ for project in testProjects do
                      if not (Set.contains project ran) then
                          record
                              (testKey o project ImpactScope)
                              project
                              OutcomeStore.Outcome.Pass
                              (Some
                                  { Total = 0
                                    Passed = 0
                                    Failed = 0
                                    Skipped = 0 })
                              o.GradedRunId ]
            else
                []

        [ for (name, outcome) in o.Plugins do
              match outcome with
              | Some outcome -> record (pluginKey o name) name outcome None None
              | None -> ()
          record (diagnosticsKey o) "diagnostics" (outcomeOf (not o.DiagnosticsFailed)) None None ]
        @ suites
        @ unselected

let classOf (answer: LookupVerdict.Answer) : Class =
    match answer with
    | LookupVerdict.Answer.Green -> Class.Green
    | LookupVerdict.Answer.Red _ -> Class.Red
    | LookupVerdict.Answer.Missing _ -> Class.Missing

/// How the lookup's class relates to today's: `agree`; `looser` (the lookup is green and
/// today is not); `stricter` (today is green and the lookup is not); `red-vs-missing`.
let direction (today: Class) (lookup: Class) : string =
    match today, lookup with
    | a, b when a = b -> "agree"
    | _, Class.Green -> "looser"
    | Class.Green, _ -> "stricter"
    | _ -> "red-vs-missing"

/// The log line for one comparison, under the command label `command`.
let lineAs (command: string) (o: Observation) (answer: LookupVerdict.Answer) (recorded: int) : string =
    let strings (items: string list) =
        let a = JsonArray()

        for i in items do
            a.Add(JsonValue.Create i)

        a

    let lookup = JsonObject()
    lookup["outcome"] <- classToken (classOf answer)

    lookup["missing"] <-
        strings (
            match answer with
            | LookupVerdict.Answer.Missing m -> m
            | _ -> []
        )

    lookup["failing"] <-
        strings (
            match answer with
            | LookupVerdict.Answer.Red f -> f
            | _ -> []
        )

    let today = JsonObject()
    today["outcome"] <- o.TodayOutcome
    today["class"] <- classToken o.Today
    today["exit"] <- o.ExitCode

    let dir = direction o.Today (classOf answer)
    let root = JsonObject()
    root["schema"] <- LogSchema
    root["at"] <- o.RecordedAt.ToString("o")
    root["command"] <- command
    root["tree"] <- o.Tree
    root["producer"] <- o.Producer
    root["today"] <- today
    root["lookup"] <- lookup
    root["agree"] <- (dir = "agree")
    root["direction"] <- dir
    root["recorded"] <- recorded
    root.ToJsonString()

/// The log line for one publication: labelled with its own command.
let line (o: Observation) (answer: LookupVerdict.Answer) (recorded: int) : string = lineAs o.Command o answer recorded

/// File `o`'s records, look the tree up, and append the comparison to the shadow log.
/// Returns the lookup's answer. `testProjects` are the configured test projects.
let observeWith (repoRoot: string) (store: OutcomeStore.Store) (testProjects: string list) (o: Observation) =
    let records = recordsOf o testProjects
    let recorded = records |> List.filter store.Record |> List.length
    let required = requirements o testProjects

    let answer =
        LookupVerdict.decide required (fun k -> store.Lookup k |> Option.map _.Outcome)

    let path = logPath repoRoot
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.AppendAllText(path, line o answer recorded + "\n")

    let keep =
        required
        |> List.collect (fun r -> List.concat r.Alternatives)
        |> List.map _.Value
        |> Set.ofList

    store.Gc(o.RecordedAt, keep) |> ignore
    answer

/// The shadow of a `fshw verdict` READ: look the CURRENT tree up and log the comparison with
/// what the read reported, labelled `verdict`. A read files no record and evicts nothing —
/// reading cannot perturb.
let observeReadWith (repoRoot: string) (store: OutcomeStore.Store) (testProjects: string list) (o: Observation) =
    let answer =
        LookupVerdict.decide (requirements o testProjects) (fun k -> store.Lookup k |> Option.map _.Outcome)

    let path = logPath repoRoot
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.AppendAllText(path, lineAs "verdict" o answer 0 + "\n")
    answer

/// The shadow store of `repoRoot`, with the default TTL and byte budget.
let storeAt (repoRoot: string) =
    OutcomeStore.Store(
        { Root = storeRoot repoRoot
          Ttl = OutcomeStore.defaultTtl
          MaxBytes = OutcomeStore.defaultMaxBytes
          Log = Logging.info "shadow" }
    )

/// The configured test projects of `repoRoot`, by the name its reports carry.
let configuredTestProjects (repoRoot: string) : Result<string list, string> =
    TestEvidenceKey.configsOf repoRoot |> Result.map (Map.keys >> List.ofSeq)

/// The shadow for one publication. Never throws and never changes anything a gate reads:
/// a failure is logged and the publication goes on exactly as it would have.
let observe (repoRoot: string) (o: Observation) : unit =
    if enabledBy (Environment.GetEnvironmentVariable KillSwitchEnvVar) then
        try
            match configuredTestProjects repoRoot with
            | Error reason -> Logging.warn "shadow" $"lookup verdict not computed: %s{reason}"
            | Ok projects -> observeWith repoRoot (storeAt repoRoot) projects o |> ignore
        with ex ->
            Logging.warn "shadow" $"lookup verdict not computed: %s{ex.GetType().Name}: %s{ex.Message}"

/// The observation a published verdict makes.
let ofVerdict (repoRoot: string) (v: Verdict.Verdict) : Observation =
    let today =
        match v.Outcome with
        | Verdict.Green _ -> Class.Green
        | Verdict.Red -> Class.Red
        | Verdict.Incomplete _
        | Verdict.ModelUnavailable _ -> Class.Missing

    let pluginOutcome (p: Verdict.PluginVerdict) =
        match p.Outcome with
        | Verdict.PluginOutcome.Ok
        | Verdict.PluginOutcome.Warn -> Some OutcomeStore.Outcome.Pass
        | Verdict.PluginOutcome.Fail
        | Verdict.PluginOutcome.TimedOut
        | Verdict.PluginOutcome.Wedged -> Some OutcomeStore.Outcome.Fail
        | Verdict.PluginOutcome.Running -> None

    let runToken (id: Guid option) =
        id |> Option.map (fun g -> g.ToString "N")

    { Command = Verdict.Command.token v.Command
      Tree = v.TreeHash
      Producer = DaemonIdentity.BinaryIdentity.render v.Producer
      Worktree = repoRoot
      Today = today
      TodayOutcome = Verdict.Outcome.tag v.Outcome
      ExitCode = v.ExitCode
      Concluded = today <> Class.Missing
      Plugins = v.Plugins |> List.map (fun p -> p.Name, pluginOutcome p)
      // The downgrade causes (`tree`, `receipt`) are refusals about the evidence, not
      // diagnostics about the code; a verdict carrying one is incomplete and records nothing.
      DiagnosticsFailed =
        v.RedCauseCount > 0
        && v.RedCauses
           |> List.exists (fun c -> c.Source <> "tree" && c.Source <> "receipt")
      GradedRunId = runToken v.RunId
      FullSuite =
        (match v.Scope with
         | IpcParsing.FullSuite _ -> true
         | _ -> false)
      ImpactFiltered =
        (match v.Scope with
         | IpcParsing.ImpactFiltered _ -> true
         | _ -> false)
      Suites =
        [ for run in v.Runs do
              for s in run.Suites do
                  { RunId = runToken run.RunId
                    Project = s.Project
                    Counts =
                      { Total = s.Total
                        Passed = s.Passed
                        Failed = s.Failed
                        Skipped = s.Skipped }
                    Passed = s.Failed = 0 || (s.StarvedRerun |> Option.exists _.PassedAlone) } ]
      RecordedAt = DateTimeOffset(DateTime.SpecifyKind(v.ProducedAt, DateTimeKind.Utc)) }

/// The observation a `fshw verdict` read makes: the verdict on disk, judged against the
/// CURRENT tree and the CURRENT fshw binary, with today's answer being what the read reported
/// (`Applies` keeps the verdict's own class; `Stale` and `InFlight` are no answer). `None`
/// when there is no verdict to read, so nothing names the plugins or the command to require.
let ofRead
    (repoRoot: string)
    (report: Verdict.Report)
    (currentTree: string)
    (currentProducer: string)
    (now: DateTimeOffset)
    : Observation option =
    let read (v: Verdict.Verdict) (today: Class) (todayOutcome: string) =
        { ofVerdict repoRoot v with
            Tree = currentTree
            Producer = currentProducer
            Today = today
            TodayOutcome = todayOutcome
            ExitCode = Verdict.reportExitCode report
            Concluded = false
            Suites = []
            RecordedAt = now }

    match report with
    | Verdict.Report.Applies v ->
        let concluded = ofVerdict repoRoot v
        Some(read v concluded.Today ("applies:" + concluded.TodayOutcome))
    | Verdict.Report.Stale(v, _) -> Some(read v Class.Missing "stale")
    | Verdict.Report.InFlight(v, _) -> Some(read v Class.Missing "in-flight")
    | Verdict.Report.NoVerdict _ -> None

/// The shadow of one `fshw verdict` read. Never throws, writes no record, and never touches
/// stdout, the exit code or the verdict file.
let observeRead (repoRoot: string) (report: Verdict.Report) (currentTree: TreeHash.Tree option) : unit =
    if enabledBy (Environment.GetEnvironmentVariable KillSwitchEnvVar) then
        try
            match currentTree, configuredTestProjects repoRoot with
            | None, _ -> ()
            | _, Error reason -> Logging.warn "shadow" $"lookup verdict not computed: %s{reason}"
            | Some tree, Ok projects ->
                let producer = DaemonIdentity.BinaryIdentity.render (Verdict.Producer.current ())

                match ofRead repoRoot report tree.Hash producer DateTimeOffset.UtcNow with
                | None -> ()
                | Some o -> observeReadWith repoRoot (storeAt repoRoot) projects o |> ignore
        with ex ->
            Logging.warn "shadow" $"lookup verdict not computed: %s{ex.GetType().Name}: %s{ex.Message}"
