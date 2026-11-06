/// The history-replay harness's I/O: workspace moves, the consumer's check, captures.
/// The decisions (plan, diff, ledger, summary) are `ReplayModel`'s.
///
/// It touches the consuming repository only through its dedicated workspace: `jj new`
/// and `jj restore` there, and `jj workspace add` once if the workspace is missing. It
/// never moves a bookmark and never pushes.
module FsHotWatch.Bench.ReplayRun

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch.Bench.ReplayModel

type Options =
    {
        /// The consuming repository (a jj repository).
        Target: string
        /// The dedicated workspace's root and its jj name.
        Workspace: string
        WorkspaceName: string
        /// Where the plan, the ledger, the summary and every step's artifacts go.
        Out: string
        /// The range to replay. Only read when no plan exists yet.
        Revset: string option
        /// Run in the workspace for each run: the consumer's gate.
        CheckCmd: string
        /// The fshw invocation in the workspace (`stop`, `findings`, `status` are appended).
        Fshw: string
        /// Run after each move, before any fault: e.g. pin one fshw for the whole range.
        PrepareCmd: string option
        /// Run before each check: e.g. wait for idle CPU.
        BeforeCheckCmd: string option
        Faults: FaultSpec list
        FaultEvery: int
        FaultOffset: int
        AllKinds: bool
        TimeoutMin: int
        DiffPlugins: Set<string>
        /// Run the forced re-check (records off) at every seeded fault and at the clean step of
        /// every `ForcedEvery`-th commit (1 = every commit, 0 = faults only).
        ForcedEvery: int
        /// The daemon's log, relative to the workspace.
        DaemonLog: string
        /// Stop after this many steps in this invocation (the rest resume later).
        MaxSteps: int option
    }

let private log (text: string) =
    printfn "[replay] %s %s" (DateTimeOffset.UtcNow.ToString("HH:mm:ss")) text

let private quote (s: string) = "'" + s.Replace("'", "'\\''") + "'"

/// Run `cmd` under bash in `cwd`, with `env` added, its output appended to `logFile`.
/// A run past `timeout` is killed with its whole process tree.
let private shell
    (cwd: string)
    (env: (string * string) list)
    (cmd: string)
    (logFile: string)
    (timeout: TimeSpan)
    : int * bool * int64 =
    Directory.CreateDirectory(Path.GetDirectoryName logFile) |> ignore
    let psi = ProcessStartInfo("/bin/bash")
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add $"( %s{cmd} ) >> %s{quote logFile} 2>&1 </dev/null"
    psi.WorkingDirectory <- cwd
    psi.UseShellExecute <- false

    for k, v in env do
        psi.Environment[k] <- v

    let sw = Stopwatch.StartNew()
    use p = Process.Start psi

    if p.WaitForExit(int timeout.TotalMilliseconds) then
        p.ExitCode, false, sw.ElapsedMilliseconds
    else
        (try
            p.Kill true
         with _ ->
             ())

        p.WaitForExit()
        -1, true, sw.ElapsedMilliseconds

/// Run `cmd` under bash in `cwd` and return its exit code and standard output.
let private capture (cwd: string) (cmd: string) : int * string =
    let psi = ProcessStartInfo("/bin/bash")
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add(cmd + " 2>/dev/null </dev/null")
    psi.WorkingDirectory <- cwd
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    p.ExitCode, out

let private jj (cwd: string) (args: string) : int * string = capture cwd ("jj " + args)

/// Run `cmd` under bash in `cwd` and return its standard output; on a non-zero exit, throw
/// with what it printed to stderr, which is where a tool says why it failed.
let internal mustRun (cwd: string) (cmd: string) : string =
    let psi = ProcessStartInfo("/bin/bash")
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add(cmd + " </dev/null")
    psi.WorkingDirectory <- cwd
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Process.Start psi
    let err = p.StandardError.ReadToEndAsync()
    let out = p.StandardOutput.ReadToEnd()
    p.WaitForExit()

    if p.ExitCode = 0 then
        out
    else
        failwith $"%s{cmd} exited %d{p.ExitCode} in %s{cwd}: %s{err.Result.Trim()} %s{out.Trim()}"

let private mustJj (cwd: string) (args: string) : string = mustRun cwd ("jj " + args)

/// The range, oldest first, as full commit ids.
let resolveRange (target: string) (revset: string) : string list =
    mustJj target $"log --ignore-working-copy --no-graph -r {quote revset} -T 'commit_id ++ \"\\n\"'"
    |> fun out -> out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList
    |> List.rev

let private ensureWorkspace (opts: Options) (first: string) =
    if not (Directory.Exists(Path.Combine(opts.Workspace, ".jj"))) then
        log $"creating workspace %s{opts.WorkspaceName} at %s{opts.Workspace}"
        // `jj workspace add` does not create the parent (`.workspaces/` in a fresh clone).
        Directory.CreateDirectory(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator opts.Workspace))
        |> ignore

        mustJj opts.Target $"workspace add {quote opts.Workspace} --name {quote opts.WorkspaceName} -r {first}"
        |> ignore

/// Discard the workspace's own uncommitted changes (a fault, a pin, a formatter's
/// rewrite) and put it on an empty change on top of `commit`.
let private moveTo (opts: Options) (commit: string) =
    // Another workspace's operation can leave this one stale; catching up is harmless.
    jj opts.Workspace "workspace update-stale" |> ignore
    mustJj opts.Workspace "restore" |> ignore

    let parent =
        mustJj opts.Workspace "log --no-graph -r @- -T 'commit_id'" |> fun s -> s.Trim()

    if parent <> commit then
        mustJj opts.Workspace $"new {commit}" |> ignore

let private lineCount (path: string) =
    if File.Exists path then
        File.ReadAllLines(path).Length
    else
        0

let private fileLength (path: string) =
    if File.Exists path then FileInfo(path).Length else 0L

let private readFrom (path: string) (offset: int64) : string list =
    if not (File.Exists path) then
        []
    else
        use s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        let start = if s.Length < offset then 0L else offset
        s.Seek(start, SeekOrigin.Begin) |> ignore
        use r = new StreamReader(s)
        r.ReadToEnd().Split('\n') |> Array.toList

let private verdictSince (workspace: string) (start: DateTime) : VerdictInfo option =
    let path = Path.Combine(workspace, ".fshw", "verdict.json")

    if File.Exists path && File.GetLastWriteTimeUtc path >= start then
        try
            let root = JsonNode.Parse(File.ReadAllText path)
            let outcome = root["outcome"]

            Some
                { Command = root["command"].GetValue<string>()
                  Outcome =
                    if isNull outcome then
                        ""
                    else
                        outcome["kind"].GetValue<string>()
                  Exit = root["exitCode"].GetValue<int>()
                  Tree = root["treeHash"].GetValue<string>() }
        with _ ->
            None
    else
        None

let private readErrorDir (workspace: string) : Diagnostic list =
    let dir = Path.Combine(workspace, ".fshw", "errors")

    if Directory.Exists dir then
        [ for f in Directory.EnumerateFiles(dir, "*.json") |> Seq.sort do
              yield! Diagnostics.parseErrorFile workspace (Path.GetFileName f) (File.ReadAllText f) ]
    else
        []

let private diagnosticsJson (ds: Diagnostic list) : string =
    let arr = JsonArray()

    for d in ds do
        let o = JsonObject()
        o["plugin"] <- d.Plugin
        o["file"] <- d.File
        o["line"] <- d.Line
        o["column"] <- d.Column
        o["severity"] <- d.Severity
        o["code"] <- d.Code
        o["message"] <- d.Message
        arr.Add o

    arr.ToJsonString(JsonSerializerOptions(WriteIndented = true))

/// Copy the CTRF receipts of every test run `verdictJson` names from the workspace's
/// `.fshw/test-runs/<runId>/` into `dir/test-runs/<runId>/`. The workspace prunes them as
/// later runs land, and a flipped test's assertion text lives only there.
let internal keepTestRuns (workspace: string) (verdictJson: string) (dir: string) =
    for runId in Diagnostics.verdictRunIds verdictJson do
        let source = Path.Combine(workspace, ".fshw", "test-runs", runId)

        if Directory.Exists source then
            let target = Path.Combine(dir, "test-runs", runId)
            Directory.CreateDirectory target |> ignore

            for f in Directory.EnumerateFiles(source, "*.ctrf.json") do
                File.Copy(f, Path.Combine(target, Path.GetFileName f), true)

let private stopDaemon (opts: Options) (logFile: string) =
    shell opts.Workspace [] (opts.Fshw + " stop") logFile (TimeSpan.FromMinutes 5.0)
    |> ignore

let private recordsEnv (on: bool) =
    let v = if on then "1" else "0"

    [ "FSHW_CHECK_RECORDS", v
      "FSHW_OPTIONS_RECORDS", v
      "FSHW_OUTCOME_SHADOW", "1" ]

/// One run of the consumer's check, and everything it left behind. The daemon is stopped
/// before (so the record flags take effect) and after (so its tally is logged).
let private runCheck (opts: Options) (stepDir: string) (mode: string) : RunResult * Diagnostic list =
    let dir = Path.Combine(stepDir, mode)
    Directory.CreateDirectory dir |> ignore
    stopDaemon opts (Path.Combine(dir, "stop-before.log"))

    opts.BeforeCheckCmd
    |> Option.iter (fun cmd ->
        shell opts.Workspace [] cmd (Path.Combine(dir, "before-check.log")) (TimeSpan.FromHours 12.0)
        |> ignore)

    let shadowPath =
        Path.Combine(opts.Workspace, ".fshw", "shadow", "lookup-verdict.jsonl")

    let daemonLog = Path.Combine(opts.Workspace, opts.DaemonLog)
    let shadowBefore = lineCount shadowPath
    let logOffset = fileLength daemonLog
    let start = DateTime.UtcNow
    log $"  %s{mode}: %s{opts.CheckCmd}"

    let exit, timedOut, elapsed =
        shell
            opts.Workspace
            (recordsEnv (mode = "replay"))
            opts.CheckCmd
            (Path.Combine(dir, "check.log"))
            (TimeSpan.FromMinutes(float opts.TimeoutMin))

    let verdict = verdictSince opts.Workspace start

    verdict
    |> Option.iter (fun _ ->
        File.Copy(Path.Combine(opts.Workspace, ".fshw", "verdict.json"), Path.Combine(dir, "verdict.json"), true))

    verdict
    |> Option.iter (fun _ -> keepTestRuns opts.Workspace (File.ReadAllText(Path.Combine(dir, "verdict.json"))) dir)

    let newShadow =
        if File.Exists shadowPath then
            Shadow.since shadowBefore (File.ReadAllLines shadowPath |> Array.toList)
        else
            []

    File.WriteAllLines(Path.Combine(dir, "shadow.jsonl"), newShadow)

    let errors = readErrorDir opts.Workspace
    File.WriteAllText(Path.Combine(dir, "errors.json"), diagnosticsJson errors)

    let _, findingsText = capture opts.Workspace (opts.Fshw + " findings --json")
    File.WriteAllText(Path.Combine(dir, "findings.json"), findingsText)

    let findingsOutcome, findings =
        match Diagnostics.parseFindings findingsText with
        | Ok(outcome, fs) -> Some outcome, fs
        | Error _ -> None, []

    let _, status = capture opts.Workspace (opts.Fshw + " status --agent")
    File.WriteAllText(Path.Combine(dir, "status.json"), status)

    stopDaemon opts (Path.Combine(dir, "stop-after.log"))

    let recordsLog =
        readFrom daemonLog logOffset
        |> List.filter (fun l -> l.Contains "[check-records]" || l.Contains "[options-records]")
        |> List.map _.Trim()

    let all = errors @ findings

    let timedOutNote = if timedOut then " (timed out)" else ""

    let outcome = verdict |> Option.map _.Outcome |> Option.defaultValue "none"

    log
        $"  %s{mode}: exit %d{exit}%s{timedOutNote} in %d{elapsed / 1000L}s, verdict %s{outcome}, %d{newShadow.Length} shadow line(s), %d{errors.Length} diagnostic(s)"

    { Mode = mode
      Exit = exit
      TimedOut = timedOut
      ElapsedMs = elapsed
      Verdict = verdict
      Shadow = newShadow |> List.choose Shadow.parse
      Diagnostics = errors.Length
      Findings = findingsOutcome
      RecordsLog = recordsLog
      Artifacts = dir },
    all

let private runStep (opts: Options) (step: Step) : Entry =
    let short = step.Commit.Substring(0, min 12 step.Commit.Length)

    let stepDir =
        Path.Combine(opts.Out, "steps", $"%03d{step.Index}-%s{short}-%s{(Phase.token step.Phase).Replace(':', '-')}")

    Directory.CreateDirectory stepDir |> ignore
    log $"step %d{step.Index} %s{short} %s{Phase.token step.Phase}"
    let startedAt = DateTimeOffset.UtcNow
    stopDaemon opts (Path.Combine(stepDir, "stop-move.log"))
    moveTo opts step.Commit

    opts.PrepareCmd
    |> Option.iter (fun cmd ->
        let code, _, _ =
            shell opts.Workspace [] cmd (Path.Combine(stepDir, "prepare.log")) (TimeSpan.FromMinutes 30.0)

        if code <> 0 then
            failwith $"the prepare command exited %d{code} (see %s{stepDir}/prepare.log)")

    let spec =
        match step.Phase with
        | Phase.Clean -> None
        | Phase.Fault kind ->
            let spec = opts.Faults |> List.find (fun f -> f.Kind = kind)
            let path = Path.Combine(opts.Workspace, spec.File)
            File.WriteAllText(path, Fault.applyTo (File.ReadAllText path) spec.Append)
            log $"  seeded %s{kind} fault in %s{spec.File}"
            Some spec

    try
        let replay, replayDiags = runCheck opts stepDir "replay"

        let forced, forcedDiags =
            if forcedDue opts.ForcedEvery step then
                let f, ds = runCheck opts stepDir "forced"
                Some f, ds
            else
                None, replayDiags

        let diff = Diagnostics.diff opts.DiffPlugins replayDiags forcedDiags

        if not (Diagnostics.agrees diff) then
            log $"  DIFF: %d{diff.OnlyReplay.Length} replay-only, %d{diff.OnlyForced.Length} forced-only"

        { Index = step.Index
          Commit = step.Commit
          Phase = step.Phase
          StartedAt = startedAt
          FinishedAt = DateTimeOffset.UtcNow
          Replay = replay
          Forced = forced
          Diff = diff
          Fault = spec |> Option.map (fun s -> Fault.judge s replay forced) }
    finally
        // A seeded fault never outlives its step.
        if spec.IsSome then
            stopDaemon opts (Path.Combine(stepDir, "stop-revert.log"))
            mustJj opts.Workspace "restore" |> ignore

let private loadOrCreatePlan (opts: Options) : Result<Plan, string> =
    let path = Path.Combine(opts.Out, "plan.json")

    if File.Exists path then
        match Plan.ofJson (File.ReadAllText path) with
        | Ok plan when opts.Revset.IsSome && opts.Revset <> Some plan.Source ->
            Error
                $"%s{path} replays %s{plan.Source}, not %s{opts.Revset.Value}: resume without --revset, or use another --out"
        | other -> other
    else
        match opts.Revset with
        | None -> Error "no plan yet: --revset (or --count) is required for the first run"
        | Some revset ->
            let commits = resolveRange opts.Target revset

            if commits.IsEmpty then
                Error $"%s{revset} resolves to no commits"
            else
                let plan =
                    { Source = revset
                      Commits = commits
                      FaultEvery = opts.FaultEvery
                      FaultOffset = opts.FaultOffset
                      AllKinds = opts.AllKinds
                      Faults = opts.Faults }

                Directory.CreateDirectory opts.Out |> ignore
                File.WriteAllText(path, Plan.toJson plan)
                Ok plan

let writeSummary (out: string) : string =
    let entries = Ledger.read (Path.Combine(out, "ledger.jsonl"))
    let text = Summary.render (Summary.summarize entries)
    File.WriteAllText(Path.Combine(out, "summary.txt"), text)
    text

/// The nightly gate over the ledger in `out` (ADR-059 §14, AUTOMATION-1442): rewrites
/// `summary.txt`, prints the gate's verdict, and returns its exit code (0 agree, 1 a
/// records-vs-forced disagreement, 3 nothing compared).
let gate (out: string) : int =
    writeSummary out |> ignore

    let verdict =
        Summary.gate (Summary.summarize (Ledger.read (Path.Combine(out, "ledger.jsonl"))))

    printfn "%s" (Gate.render verdict)
    Gate.exitCode verdict

/// Run (or resume) a replay. Exit 0 when every planned step is in the ledger, 4 when
/// `MaxSteps` stopped it early, 2 on a usage error.
let run (opts: Options) : int =
    Directory.CreateDirectory opts.Out |> ignore
    let lockPath = Path.Combine(opts.Out, "replay.lock")

    use _lock =
        try
            new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        with :? IOException ->
            failwith $"another replay holds %s{lockPath}"

    match loadOrCreatePlan opts with
    | Error message ->
        eprintfn "%s" message
        2
    | Ok plan ->
        // The plan's faults are the ones it was made with, so a resume seeds the same text.
        let opts = { opts with Faults = plan.Faults }
        let ledger = Path.Combine(opts.Out, "ledger.jsonl")
        let todo = pending (steps plan) (Ledger.completed (Ledger.read ledger))
        log $"%d{plan.Commits.Length} commit(s) in %s{plan.Source}; %d{todo.Length} step(s) to run"
        ensureWorkspace opts plan.Commits.Head

        let budget = opts.MaxSteps |> Option.defaultValue Int32.MaxValue
        let now = todo |> List.truncate budget

        for step in now do
            Ledger.append ledger (runStep opts step)
            writeSummary opts.Out |> ignore

        printfn "%s" (writeSummary opts.Out)
        if now.Length < todo.Length then 4 else 0
