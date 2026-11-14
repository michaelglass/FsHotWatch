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

module Class =
    /// The class as a shadow line names it.
    let token =
        function
        | Class.Green -> "green"
        | Class.Red -> "red"
        | Class.Missing -> "missing"

    /// The class a shadow line names; `None` for any other token.
    let tryParse =
        function
        | "green" -> Some Class.Green
        | "red" -> Some Class.Red
        | "missing" -> Some Class.Missing
        | _ -> None

/// How the lookup's class relates to today's.
[<RequireQualifiedAccess>]
type Direction =
    | Agree
    /// The lookup is green and today is not.
    | Looser
    /// Today is green and the lookup is not.
    | Stricter
    /// Neither is green, and they differ.
    | RedVsMissing
    /// No comparison was made: the requirement set could not be read. Never agreement, so it
    /// breaks a consecutive-agreement run as ADR-059 §8 counts one (`select(.agree | not)`).
    | NotCompared

module Direction =
    /// The direction as a shadow line names it.
    let token =
        function
        | Direction.Agree -> "agree"
        | Direction.Looser -> "looser"
        | Direction.Stricter -> "stricter"
        | Direction.RedVsMissing -> "red-vs-missing"
        | Direction.NotCompared -> "not-compared"

    /// The direction a shadow line names; `None` for any other token.
    let tryParse =
        function
        | "agree" -> Some Direction.Agree
        | "looser" -> Some Direction.Looser
        | "stricter" -> Some Direction.Stricter
        | "red-vs-missing" -> Some Direction.RedVsMissing
        | "not-compared" -> Some Direction.NotCompared
        | _ -> None

/// The scope of the run a publication was graded from.
[<RequireQualifiedAccess>]
type GradedScope =
    /// Every configured test project ran unfiltered.
    | FullSuite
    /// A subset ran.
    | ImpactFiltered
    /// No scope was earned: no tests ran, or none could be read.
    | Unscoped

/// One test project's report in one run.
type Suite =
    {
        RunId: string option
        Project: string
        Counts: OutcomeStore.Counts
        /// The suite's own outcome: no failed test, or a starved re-run that passed alone.
        Passed: bool
        /// The names of the failed tests when the report names every failure it counts;
        /// `None` when it does not (a raw throw, a crashed host, an unreadable report).
        FailedTests: string list option
        /// The names of the tests the report lists as passed. Empty when they were not read.
        PassedTests: Set<string>
        /// The CTRF report, by absolute path: what a later publication reads the passed names
        /// from when it has a failure on file to clear (AUTOMATION-1472). `None` when unknown.
        Report: string option
    }

/// The failures a suite names: `names`, the failed rows its report names, when they are every
/// failure it counts and it counts nothing but passes, fails and skips. `None` for a project
/// that `timedOut` in that run: a timeout is about the project, not the tests it named.
let namedFailures (timedOut: bool) (counts: OutcomeStore.Counts) (names: string list option) : string list option =
    if timedOut || counts.Total <> counts.Passed + counts.Failed + counts.Skipped then
        None
    else
        names |> Option.filter (fun f -> List.length f = counts.Failed)

/// What one publication says, reduced to what the shadow needs.
type Observation =
    {
        Command: Verdict.Command
        Tree: string
        Producer: string
        Worktree: string
        Today: Class
        /// Today's outcome tag (`green`, `red`, `incomplete`, `model-unavailable`).
        TodayOutcome: string
        ExitCode: int
        /// Each plugin and its terminal outcome; `None` for one still running.
        Plugins: (string * OutcomeStore.Outcome option) list
        /// A diagnostic the exit code was computed from reddened the run.
        DiagnosticsFailed: bool
        GradedRunId: string option
        /// The full-suite run today's green is relative to (its watermark), when the verdict names one.
        BaselineRunId: string option
        Scope: GradedScope
        Suites: Suite list
        /// The runs the suites came from, OLDEST FIRST, as the check attributed them
        /// (`runOrder`). Empty when nobody said, and then no run is later than another.
        RunOrder: string list
        RecordedAt: DateTimeOffset
        /// Each project's full-run outcome key over this tree, as the daemon computed it
        /// (ADR-059 step 7b): a record the test runner filed under it satisfies the project.
        /// Empty when the daemon did not say.
        OutcomeKeys: Map<string, ActionKey.KeyValue>
    }

/// Today's verdict concluded green or red. Only a concluded publication files records: an
/// incomplete one is a statement that nothing was established. (A read files nothing
/// whatever it concluded: `observeReadWith` never asks `recordsOf`.)
let concluded (o: Observation) = o.Today <> Class.Missing

/// The keys of `o`'s tree, as `Requirements` folds them.
let subjectOf (o: Observation) : Requirements.Subject =
    { Tree = o.Tree
      Producer = o.Producer
      OutcomeKeys = o.OutcomeKeys }

let pluginKey (o: Observation) (name: string) =
    Requirements.pluginKey (subjectOf o) name

let diagnosticsKey (o: Observation) =
    Requirements.diagnosticsKey (subjectOf o)

/// A test project's run over the tree, in `scope` (`full` or `impact`).
let testKey (o: Observation) (project: string) (scope: string) =
    Requirements.testKey (subjectOf o) project scope

/// "This project once passed in full" (`Requirements.baselineKey`).
let baselineKey (project: string) = Requirements.baselineKey project

/// The K_P key a publication named for `project` over this tree and binary (AUTOMATION-1443):
/// a `fshw verdict` read has no daemon to name it, so it reads it back from here.
let outcomeKeyKey (o: Observation) (project: string) =
    ActionKey.create "test-outcome-key" [ "tree", o.Tree; "producer", o.Producer; "project", project ]

[<Literal>]
let private OutcomeKeyField = "outcomeKey"

/// The records naming each K_P key `o` carries, with their outputs.
let outcomeKeyRecords (o: Observation) : (OutcomeStore.Record * string) list =
    [ for KeyValue(project, kp) in o.OutcomeKeys do
          let outputs = JsonObject()
          outputs[OutcomeKeyField] <- kp.Text

          OutcomeStore.Record.ofAction
              (outcomeKeyKey o project)
              project
              OutcomeStore.Outcome.Pass
              o.Producer
              o.Worktree
              o.RecordedAt,
          outputs.ToJsonString() ]

/// The K_P keys publications over `o`'s tree and binary named, by project; a project none
/// named, or whose record does not read back as a key, is absent.
let recordedOutcomeKeys
    (store: OutcomeStore.Store)
    (testProjects: string list)
    (o: Observation)
    : Map<string, ActionKey.KeyValue> =
    let keyIn (outputs: string) =
        try
            match JsonNode.Parse outputs with
            | :? JsonObject as obj ->
                match obj[OutcomeKeyField] with
                | :? JsonValue as v ->
                    match v.TryGetValue<string>() with
                    | true, text -> ActionKey.KeyValue.tryParse text
                    | _ -> None
                | _ -> None
            | _ -> None
        with :? Text.Json.JsonException ->
            None

    testProjects
    |> List.choose (fun project ->
        store.LookupWithOutputs(outcomeKeyKey o project)
        |> Option.bind snd
        |> Option.bind keyIn
        |> Option.map (fun key -> project, key))
    |> Map.ofList

/// `o` with the K_P keys earlier publications named, when it names none itself: a read.
let withRecordedOutcomeKeys (store: OutcomeStore.Store) (testProjects: string list) (o: Observation) =
    if Map.isEmpty o.OutcomeKeys then
        { o with
            OutcomeKeys = recordedOutcomeKeys store testProjects o }
    else
        o

[<Literal>]
let FullScope = Requirements.FullScope

[<Literal>]
let ImpactScope = Requirements.ImpactScope

/// What R(c, T) is computed from: the configuration, never the run (`Requirements.Configured`).
type Configured = Requirements.Configured

/// R(c, T) for `o`'s command and tree (`Requirements.requirements`, in FsHotWatch.Core).
let requirements (o: Observation) (configured: Configured) : LookupVerdict.Requirement list =
    let command =
        match o.Command with
        | Verdict.Check -> Requirements.Command.Check
        | Verdict.Confirm -> Requirements.Command.Confirm

    Requirements.requirements command (subjectOf o) configured

/// The failed suites of `o` that a LATER run of the same check ran again and passed.
///
/// A check runs a project more than once: a run fails a test, the outstanding-failure
/// ledger re-selects it, and the next run passes it. Today's verdict is then green, and
/// rightly so. The shadow used to file both suites at the publication's one instant, where
/// a tie goes to `fail`, so the lookup stayed red on a failure the check had already run
/// again green (AUTOMATION-1428). A failure is superseded only when every failed test is
/// NAMED and each passed in a run that `RunOrder` places after it. A failure nobody can
/// name, a run of unknown position, or a later run that skipped the test leaves it standing.
let supersededFailures (o: Observation) : Suite list =
    let position (s: Suite) =
        s.RunId |> Option.bind (fun id -> List.tryFindIndex ((=) id) o.RunOrder)

    o.Suites
    |> List.filter (fun s ->
        not s.Passed
        && match s.FailedTests, position s with
           | Some(_ :: _ as failed), Some at ->
               let passedLater =
                   o.Suites
                   |> List.filter (fun later ->
                       later.Project = s.Project && position later |> Option.exists (fun p -> p > at))
                   |> List.map _.PassedTests
                   |> Set.unionMany

               failed |> List.forall passedLater.Contains
           | _ -> false)

/// The outcome records a publication files.
///
/// A suite of the graded run under a full-suite scope is a `full` record; every other
/// suite is `impact`. A full pass also records the project's baseline. Under an
/// impact-filtered scope, a configured project with no suite at all is recorded as an
/// `impact` pass of zero tests: today's selector chose nothing in it, and the shadow takes
/// today's selection as given. A failed suite a later run of the check passed again is not
/// filed (`supersededFailures`).
///
/// A publication that did not conclude files nothing about its tree: it is a statement
/// that nothing was established there. It still files the baselines of a full-suite run it
/// was graded from. A baseline names no tree, and today's watermark is earned by that run
/// whatever the publication concluded; filing none left every later check missing
/// (AUTOMATION-1428, the run of 2026-10-30 22:49 on intelligence's land workspace).
let recordsOf (o: Observation) (testProjects: string list) : OutcomeStore.Record list =
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

    let isFull (s: Suite) =
        o.Scope = GradedScope.FullSuite && s.RunId = o.GradedRunId

    // The run today's watermark names earns baselines too when it is not the graded run: a
    // check's own full-suite run, graded relative to by the impact run after it (AUTOMATION-1476).
    // Only baselines: its suites stay `impact` records of the tree, because confirm grades the
    // full suite it ran itself.
    let earnsBaseline (s: Suite) =
        isFull s || (Option.isSome o.BaselineRunId && s.RunId = o.BaselineRunId)

    let baselines =
        [ for s in o.Suites do
              if earnsBaseline s && s.Passed then
                  record (baselineKey s.Project) s.Project OutcomeStore.Outcome.Pass None s.RunId ]

    if not (concluded o) then
        baselines
    else
        let superseded = supersededFailures o

        let suites =
            [ for s in o.Suites do
                  if not (List.contains s superseded) then
                      let scope = if isFull s then FullScope else ImpactScope
                      record (testKey o s.Project scope) s.Project (outcomeOf s.Passed) (Some s.Counts) s.RunId ]

        let ran = o.Suites |> List.map _.Project |> Set.ofList

        let unselected =
            if o.Scope = GradedScope.ImpactFiltered then
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
        @ baselines
        @ unselected

let classOf (answer: LookupVerdict.Answer) : Class =
    match answer with
    | LookupVerdict.Answer.Green -> Class.Green
    | LookupVerdict.Answer.Red _ -> Class.Red
    | LookupVerdict.Answer.Missing _ -> Class.Missing

/// How the lookup's class relates to today's.
let direction (today: Class) (lookup: Class) : Direction =
    match today, lookup with
    | a, b when a = b -> Direction.Agree
    | _, Class.Green -> Direction.Looser
    | Class.Green, _ -> Direction.Stricter
    | _ -> Direction.RedVsMissing

/// A log line: what today said, the `lookup` object, and how the two relate.
let private lineWith (command: string) (o: Observation) (lookup: JsonObject) (dir: Direction) (recorded: int) =
    let today = JsonObject()
    today["outcome"] <- o.TodayOutcome
    today["class"] <- Class.token o.Today
    today["exit"] <- o.ExitCode

    let root = JsonObject()
    root["schema"] <- LogSchema
    root["at"] <- o.RecordedAt.ToString("o")
    root["command"] <- command
    root["tree"] <- o.Tree
    root["producer"] <- o.Producer
    root["today"] <- today
    root["lookup"] <- lookup
    root["agree"] <- (dir = Direction.Agree)
    root["direction"] <- Direction.token dir
    root["recorded"] <- recorded
    root.ToJsonString()

/// The log line for one comparison, under the command label `command`.
let lineAs (command: string) (o: Observation) (answer: LookupVerdict.Answer) (recorded: int) : string =
    let strings (items: string list) =
        let a = JsonArray()

        for i in items do
            a.Add(JsonValue.Create i)

        a

    let lookup = JsonObject()
    lookup["outcome"] <- Class.token (classOf answer)

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

    lineWith command o lookup (direction o.Today (classOf answer)) recorded

/// The log line for a publication or read the shadow could not compare, and why
/// (AUTOMATION-1568 rework). A silently absent line would make a gap in the count look like no
/// land at all. It is not agreement (`agree: false`) and names no disagreement direction.
let notComparedLineAs (command: string) (o: Observation) (reason: string) : string =
    let lookup = JsonObject()
    lookup["outcome"] <- "not-compared"
    lookup["reason"] <- reason
    lineWith command o lookup Direction.NotCompared 0

/// Append one line to `repoRoot`'s shadow log.
let appendLine (repoRoot: string) (text: string) =
    let path = logPath repoRoot
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.AppendAllText(path, text + "\n")

/// The log line for one publication: labelled with its own command.
let line (o: Observation) (answer: LookupVerdict.Answer) (recorded: int) : string =
    lineAs (Verdict.Command.token o.Command) o answer recorded

/// The answer for a key under `command`: the shadow store's, else the K_P store's, where the
/// test runner files. Under `check`, a K_P full fail whose every named failure a later run
/// passed at the same K_P reads as a pass, as today's outstanding-failure ledger clears it
/// (`TestFailureClearance`, AUTOMATION-1444); `confirm` grades the full suite itself.
let private lookupIn
    (command: Verdict.Command)
    (store: OutcomeStore.Store)
    (kpStore: OutcomeStore.Store option)
    (key: ActionKey.KeyValue)
    =
    let read (s: OutcomeStore.Store) =
        match command with
        | Verdict.Check -> TestFailureClearance.lookupForCheck s key
        | Verdict.Confirm -> s.Lookup key |> Option.map _.Outcome

    match read store with
    | Some outcome -> Some outcome
    | None -> kpStore |> Option.bind read

/// File `records`, a publication's, in `store`; returns how many were filed.
///
/// A failed suite's test record carries the failures it names. A passing suite's test record
/// also files the clearance it earns over a named fail that an EARLIER publication filed at
/// this tree's test keys for its project (`TestFailureClearance`, AUTOMATION-1472): check N's
/// full run failed a test, and check N+1 on the same tree ran it again and passed it. Its
/// passed names are read from its report only when there is such a fail to clear.
let private fileRecords (store: OutcomeStore.Store) (o: Observation) (records: OutcomeStore.Record list) : int =
    let suiteOf (r: OutcomeStore.Record) =
        if r.Key.Kind = "test" then
            o.Suites
            |> List.tryFind (fun s -> s.Project = r.Subject && s.RunId = r.Producer.RunId)
        else
            None

    let filed =
        records
        |> List.filter (fun r ->
            match suiteOf r with
            | Some s when r.Outcome = OutcomeStore.Outcome.Fail ->
                let outputs =
                    s.FailedTests
                    |> Option.filter (List.isEmpty >> not)
                    |> Option.map TestFailureClearance.outputsOf

                store.RecordWithOutputs(r, outputs)
            | _ -> store.Record r)

    for r in records do
        match suiteOf r with
        | Some s when r.Outcome = OutcomeStore.Outcome.Pass ->
            let passed =
                lazy
                    (if s.PassedTests.IsEmpty then
                         s.Report
                         |> Option.bind Ctrf.tryReadTestNames
                         |> Option.map _.Passed
                         |> Option.defaultValue Set.empty
                     else
                         s.PassedTests)

            for scope in [ FullScope; ImpactScope ] do
                let key = testKey o s.Project scope

                match store.LookupWithOutputs key with
                | Some(newest, _) as onFile when newest.Outcome = OutcomeStore.Outcome.Fail ->
                    TestFailureClearance.clearanceOf key.Value onFile passed.Value r
                    |> Option.iter (store.Record >> ignore)
                | _ -> ()
        | _ -> ()

    List.length filed

/// File `o`'s records, look the tree up, and append the comparison to the shadow log.
/// Returns the lookup's answer. `configured` is what the configuration requires.
let observeWith
    (repoRoot: string)
    (store: OutcomeStore.Store)
    (kpStore: OutcomeStore.Store option)
    (configured: Configured)
    (o: Observation)
    =
    let records = recordsOf o configured.TestProjects
    let recorded = fileRecords store o records

    // Not counted in `recorded`: they name keys, not outcomes this publication established.
    let named = outcomeKeyRecords o

    for record, outputs in named do
        store.RecordWithOutputs(record, Some outputs) |> ignore

    let required = requirements o configured

    let answer = LookupVerdict.decide required (lookupIn o.Command store kpStore)

    appendLine repoRoot (line o answer recorded)

    let keep =
        required
        |> List.collect (fun r -> List.concat r.Alternatives)
        |> Set.ofList
        |> Set.union (named |> List.map (fun (r, _) -> r.Key.Value) |> Set.ofList)

    match store.GcIfDue(o.RecordedAt, keep, OutcomeStore.defaultGcInterval) with
    | OutcomeStore.GcRun.Ran _
    | OutcomeStore.GcRun.NotDue _ -> ()

    answer

/// The shadow of a `fshw verdict` READ: look the CURRENT tree up and log the comparison with
/// what the read reported, labelled `verdict`. A read files no record and evicts nothing —
/// reading cannot perturb.
let observeReadWith
    (repoRoot: string)
    (store: OutcomeStore.Store)
    (kpStore: OutcomeStore.Store option)
    (configured: Configured)
    (o: Observation)
    =
    let o = withRecordedOutcomeKeys store configured.TestProjects o

    let answer =
        LookupVerdict.decide (requirements o configured) (lookupIn o.Command store kpStore)

    appendLine repoRoot (lineAs "verdict" o answer 0)
    answer

/// The shadow store of `repoRoot`, with the default TTL and byte budget.
let storeAt (repoRoot: string) =
    OutcomeStore.Store(
        { Root = storeRoot repoRoot
          Ttl = OutcomeStore.defaultTtl
          MaxBytes = OutcomeStore.defaultMaxBytes
          Log = Logging.info "shadow" }
    )

/// The box-wide store the test runner files K_P records in, opened only when the daemon named
/// keys to look up: a publication with none has nothing to read there.
let kpStoreFor (repoRoot: string) (o: Observation) : OutcomeStore.Store option =
    if Map.isEmpty o.OutcomeKeys then
        None
    else
        Some(OutcomeStore.openFor repoRoot (Logging.info "shadow"))

/// The configured test projects of `repoRoot`, by the name its reports carry.
let configuredTestProjects (repoRoot: string) : Result<string list, string> =
    TestEvidenceKey.configsOf repoRoot |> Result.map (Map.keys >> List.ofSeq)

/// What `repoRoot`'s configuration requires of a green: its non-test plugins and its test
/// projects. An error when either cannot be read: the shadow never guesses a requirement set.
let configuredOf (repoRoot: string) : Result<Configured, string> =
    configuredTestProjects repoRoot
    |> Result.bind (fun projects ->
        try
            Ok
                { RequiredPlugins = DaemonConfig.configuredPlugins repoRoot
                  TestProjects = projects }
        with DaemonConfig.ConfigErrorException message ->
            Error message)

/// The shadow for one publication. Never throws and never changes anything a gate reads:
/// a failure is logged and the publication goes on exactly as it would have.
let observe (repoRoot: string) (o: Observation) : unit =
    if enabledBy (Environment.GetEnvironmentVariable KillSwitchEnvVar) then
        try
            match configuredOf repoRoot with
            | Error reason ->
                Logging.warn "shadow" $"lookup verdict not computed: %s{reason}"
                appendLine repoRoot (notComparedLineAs (Verdict.Command.token o.Command) o reason)
            | Ok configured ->
                observeWith repoRoot (storeAt repoRoot) (kpStoreFor repoRoot o) configured o
                |> ignore
        with ex ->
            Logging.warn "shadow" $"lookup verdict not computed: %s{ex.GetType().Name}: %s{ex.Message}"


/// The runs of a check, OLDEST FIRST, as `Observation.RunOrder` wants them: the check's own
/// runs (`TestRunReport.CheckRuns`, which `TestRunEvidence.attribute` keeps in the order they
/// completed), after the graded run when the check did not produce it. A graded run the check
/// did not produce is one it reused from before it began (`Verdict.runSuites`), so it
/// predates every run the check did produce.
let runOrder (graded: Guid option) (checkRuns: Guid list) : string list =
    let earlier =
        match graded with
        | Some g when not (List.contains g checkRuns) -> [ g ]
        | _ -> []

    earlier @ checkRuns |> List.map (fun g -> g.ToString "N")

/// The observation a published verdict makes.
/// The run today's full-suite watermark records, for a publication that does not name
/// one. A watermark run with a red project publishes red, and today accepts it anyway:
/// without this its passing projects earned no baseline (AUTOMATION-1536).
let private watermarkRun (repoRoot: string) : string option =
    match FsHotWatch.TestPrune.FullSuiteBaseline.load repoRoot with
    | FsHotWatch.TestPrune.FullSuiteBaseline.LoadedBaseline.Loaded(Some baseline) -> Some(baseline.RunId.ToString "N")
    | FsHotWatch.TestPrune.FullSuiteBaseline.LoadedBaseline.Loaded None
    | FsHotWatch.TestPrune.FullSuiteBaseline.LoadedBaseline.Unreadable _ -> None

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

    { Command = v.Command
      Tree = v.TreeHash
      Producer = DaemonIdentity.BinaryIdentity.render v.Producer
      Worktree = repoRoot
      Today = today
      TodayOutcome = Verdict.Outcome.tag v.Outcome
      ExitCode = v.ExitCode
      Plugins = v.Plugins |> List.map (fun p -> p.Name, pluginOutcome p)
      // The downgrade causes (`tree`, `receipt`) are refusals about the evidence, not
      // diagnostics about the code; a verdict carrying one is incomplete and records nothing.
      DiagnosticsFailed =
        v.RedCauseCount > 0
        && v.RedCauses
           |> List.exists (fun c -> c.Source <> "tree" && c.Source <> "receipt")
      GradedRunId = runToken v.RunId
      BaselineRunId =
        (match v.Outcome with
         | Verdict.Green(CheckVerdict.Baseline.FullSuiteRun r) -> Some(r.RunId.ToString "N")
         | _ -> watermarkRun repoRoot)
      Scope =
        (match v.Scope with
         | IpcParsing.FullSuite _ -> GradedScope.FullSuite
         | IpcParsing.ImpactFiltered _ -> GradedScope.ImpactFiltered
         | _ -> GradedScope.Unscoped)
      Suites =
        // Test names are read only for a project some suite of this verdict failed in:
        // they decide nothing else (`supersededFailures`), and a report can be large.
        let failedSomewhere =
            v.Runs
            |> List.collect _.Suites
            |> List.filter (fun s -> s.Failed > 0)
            |> List.map _.Project
            |> Set.ofList

        // A project that timed out in a run is a fact about the project, not about the tests
        // its report names (`TestReports.failuresOf`): its failures are never named.
        let timedOut =
            v.TimedOut |> List.map (fun p -> Some p.RunId, p.Project) |> Set.ofList

        [ for run in v.Runs do
              for s in run.Suites do
                  let names =
                      if failedSomewhere.Contains s.Project then
                          Ctrf.tryReadTestNames (Path.Combine(repoRoot, s.Ctrf))
                      else
                          None

                  { RunId = runToken run.RunId
                    Project = s.Project
                    Counts =
                      { Total = s.Total
                        Passed = s.Passed
                        Failed = s.Failed
                        Skipped = s.Skipped }
                    Passed = s.Failed = 0 || (s.StarvedRerun |> Option.exists _.PassedAlone)
                    FailedTests =
                      namedFailures
                          (timedOut.Contains(runToken run.RunId, s.Project) || Option.isSome s.StarvedRerun)
                          { Total = s.Total
                            Passed = s.Passed
                            Failed = s.Failed
                            Skipped = s.Skipped }
                          (names |> Option.map _.Failed)
                    PassedTests = names |> Option.map _.Passed |> Option.defaultValue Set.empty
                    Report = Some(Path.Combine(repoRoot, s.Ctrf)) } ]
      RunOrder = []
      RecordedAt = DateTimeOffset(DateTime.SpecifyKind(v.ProducedAt, DateTimeKind.Utc))
      OutcomeKeys = Map.empty }

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
            match currentTree, configuredOf repoRoot with
            | None, _ -> ()
            | Some tree, Error reason ->
                Logging.warn "shadow" $"lookup verdict not computed: %s{reason}"
                let producer = DaemonIdentity.BinaryIdentity.render (Verdict.Producer.current ())

                ofRead repoRoot report tree.Hash producer DateTimeOffset.UtcNow
                |> Option.iter (fun o -> appendLine repoRoot (notComparedLineAs "verdict" o reason))
            | Some tree, Ok configured ->
                let producer = DaemonIdentity.BinaryIdentity.render (Verdict.Producer.current ())

                match ofRead repoRoot report tree.Hash producer DateTimeOffset.UtcNow with
                | None -> ()
                | Some o ->
                    let store = storeAt repoRoot
                    let o = withRecordedOutcomeKeys store configured.TestProjects o
                    observeReadWith repoRoot store (kpStoreFor repoRoot o) configured o |> ignore
        with ex ->
            Logging.warn "shadow" $"lookup verdict not computed: %s{ex.GetType().Name}: %s{ex.Message}"
