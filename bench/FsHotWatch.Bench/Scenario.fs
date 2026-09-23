/// The scripted scenarios: N concurrent worktree sessions on one repository, served either
/// by N legacy per-worktree daemons (`--mode legacy`) or by ONE repository host
/// (`--mode host`), measured at fixed phases, repeated.
///
/// Host sessions attach with `FSHW_REPOSITORY_HOST=1 fshw start`, exactly as legacy
/// daemons start. In host mode the one host process is sampled once per phase as session 0 (its
/// footprint IS the N-session total); sessions 1..N still get records for scan validity
/// and settle latency.
///
/// One repetition of an N-session scenario:
///
/// 1. `cold-scan` — start N daemons at once on N identical worktrees (fresh caches), wait
///    for every first scan; sample each at its scan's end. `physFootprintPeak` there is
///    the kernel's lifetime high-water mark, so the scan peak cannot fall between samples.
/// 2. `settled` — after a quiet interval, sample without forcing anything.
/// 3. `post-gc` — walk each heap (inducing a full blocking collection) and sample the
///    footprint straight after: live set, GC heap, GC committed, and the native rest.
/// 4. `edit` (with `--edits K --edit-file <path>`) — K rounds of a one-line edit to the
///    same file in every worktree at once; each session's `[check] settled … after=Nms`
///    line is its settle latency. The file is restored afterwards.
/// 5. `after-tests` (with `--tests`) — run `fshw check` in every worktree concurrently,
///    then repeat the post-GC reading and count the tests the last complete cycle ran.
///
/// Validity is checked, not assumed: every scan must report `unchecked 0` and no
/// uncovered files, its log window must belong to the pid that was measured, the log's
/// scan counts must agree with `scan-metrics.jsonl`, and every session of a repetition
/// must have checked the same number of files. A variant that "wins" by doing less work
/// is marked invalid (ADR-003 / ADR-004 lesson).
module FsHotWatch.Bench.Scenario

open System
open System.Diagnostics
open System.IO
open System.Threading
open FsHotWatch

/// Who serves the sessions.
[<RequireQualifiedAccess>]
type RunMode =
    /// One `fshw start` daemon per worktree (today).
    | Legacy
    /// One repository host for all worktrees (`FSHW_REPOSITORY_HOST=1`).
    | Host

let modeName (mode: RunMode) =
    match mode with
    | RunMode.Legacy -> "legacy"
    | RunMode.Host -> "host"

/// Harness settings.
type Config =
    {
        Repo: string
        Rev: string
        Sessions: int list
        Reps: int
        Out: string
        Label: string
        /// The FsHotWatch CLI to run: a `.dll` (run as `dotnet <dll>`, which needs no
        /// `DOTNET_ROOT`) or an executable. Daemons are `<Cli> start`.
        Cli: string
        /// Shell command run once in each new worktree before any daemon starts
        /// (warm before gating: no build may rewrite project state under a scan).
        Prepare: string
        /// Top-level `.fshw.json` keys removed in the (throwaway) bench worktrees.
        Strip: string list
        /// `--set` overrides applied after `Strip`, in order.
        Set: ConfigOverride.Set list
        Tests: bool
        Heap: bool
        /// Keep caches between repetitions instead of forcing a cold scan each time.
        WarmCache: bool
        SettleSec: int
        SampleSec: int
        ScanTimeout: TimeSpan
        TestTimeout: TimeSpan
        KeepWorktrees: bool
        AllowContended: bool
        /// Keep each walk's raw trace in this directory, for offline graph analysis.
        KeepTraces: string option
        /// Partition each walked heap by import reachability (see `Retention`).
        Retention: bool
        Mode: RunMode
        /// Edit rounds for the settle-latency phase (0 = no edit phase).
        Edits: int
        /// Worktree-relative file the edit phase appends a marker line to.
        EditFile: string option
        /// How long one edit round waits for every session's settle line.
        SettleTimeout: TimeSpan
    }

let private log (msg: string) =
    eprintfn "[bench] %s %s" (DateTime.Now.ToString("HH:mm:ss")) msg

let private sh (cmd: string) (workDir: string) (timeout: TimeSpan) =
    Instruments.run "/bin/sh" $"-c %s{ProcessHelper.quoteArg cmd}" workDir timeout

/// `(command, argument prefix)` that runs the configured CLI. A `.dll` goes through the
/// `dotnet` muxer: an apphost under a mise- or nix-installed SDK cannot find the runtime
/// without `DOTNET_ROOT`, which is the same failure that stopped the global
/// `dotnet-gcdump` tool from starting at all.
let cliInvocation (cli: string) : string * string =
    if cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
        "dotnet", ProcessHelper.quoteArg cli + " "
    else
        cli, ""

let private runCli (cfg: Config) (args: string) (workDir: string) (timeout: TimeSpan) =
    let exe, prefix = cliInvocation cfg.Cli
    Instruments.run exe (prefix + args) workDir timeout

let private isJj (repo: string) =
    Directory.Exists(Path.Combine(repo, ".jj"))

/// Create a worktree of `repo` at `rev`.
let private addWorktree (cfg: Config) (name: string) (path: string) =
    let result =
        if isJj cfg.Repo then
            Instruments.run
                "jj"
                $"workspace add --name %s{name} -r %s{ProcessHelper.quoteArg cfg.Rev} %s{ProcessHelper.quoteArg path}"
                cfg.Repo
                (TimeSpan.FromMinutes 5.0)
        else
            Instruments.run
                "git"
                $"worktree add --detach %s{ProcessHelper.quoteArg path} %s{ProcessHelper.quoteArg cfg.Rev}"
                cfg.Repo
                (TimeSpan.FromMinutes 5.0)

    match result with
    | Ok _ -> ()
    | Error e -> failwith $"could not create worktree %s{path}: %s{e}"

let private removeWorktree (cfg: Config) (name: string) (path: string) =
    let _ =
        if isJj cfg.Repo then
            Instruments.run "jj" $"workspace forget %s{name}" cfg.Repo (TimeSpan.FromMinutes 2.0)
        else
            Instruments.run
                "git"
                $"worktree remove --force %s{ProcessHelper.quoteArg path}"
                cfg.Repo
                (TimeSpan.FromMinutes 2.0)

    try
        Directory.Delete(path, true)
    with _ ->
        ()

/// Apply `--strip` then `--set` to a worktree's `.fshw.json`.
let private overrideConfig (worktree: string) (strip: string list) (sets: ConfigOverride.Set list) =
    if not (List.isEmpty strip && List.isEmpty sets) then
        let file = Path.Combine(worktree, ".fshw.json")

        match ConfigOverride.apply strip sets (File.ReadAllText file) with
        | Ok text -> File.WriteAllText(file, text)
        | Error e -> failwith e

/// A daemon the harness started.
type Daemon =
    { Session: int
      Worktree: string
      Pid: int
      Port: string
      Started: DateTime }

let private start (cfg: Config) (runDir: string) (cacheHome: string) (session: int) (worktree: string) =
    let port = Path.Combine(runDir, $"d%d{session}.sock")

    if File.Exists port then
        File.Delete port

    let logFile = Path.Combine(worktree, "logs", "daemon.log")
    Directory.CreateDirectory(Path.GetDirectoryName logFile) |> ignore
    let noCache = if cfg.WarmCache then "" else "--no-cache "
    // `exec`, so the pid we hold IS the daemon; output appended exactly as the CLI's own
    // detached launch does, so the log windowing reads the same shape.
    let script =
        let exe, prefix = cliInvocation cfg.Cli
        $"exec %s{ProcessHelper.quoteArg exe} %s{prefix}%s{noCache}start >> %s{ProcessHelper.quoteArg logFile} 2>&1"

    let psi = ProcessStartInfo("/bin/sh", [| "-c"; script |])
    psi.WorkingDirectory <- worktree
    psi.UseShellExecute <- false
    psi.Environment.["DOTNET_DiagnosticPorts"] <- $"%s{port},listen,nosuspend"
    psi.Environment.[FsHwPaths.CacheHomeEnvVar] <- cacheHome
    let started = DateTime.UtcNow
    use proc = Process.Start psi

    { Session = session
      Worktree = worktree
      Pid = proc.Id
      Port = port
      Started = started }

/// The first scan sample this daemon wrote (sampled after it started).
let scanSampleSince (worktree: string) (since: DateTime) : ScanMetrics.ScanSample option =
    ScanMetrics.readSeries (ScanMetrics.recordPath worktree)
    |> List.filter (fun s -> s.SampledAt.ToUniversalTime() >= since)
    |> List.tryHead

let private readLog (worktree: string) =
    let file = Path.Combine(worktree, "logs", "daemon.log")

    if File.Exists file then
        File.ReadAllLines file |> Array.toList |> DaemonLog.sinceLastStart
    else
        []

/// Which process a session's log window must belong to.
[<RequireQualifiedAccess>]
type Owner =
    /// A legacy per-worktree daemon: its log announces its own pid.
    | Process of pid: int
    /// A repository host: the session's log names the host it attached to.
    | Host of pid: int

/// Validity problems of one session's scan against its own log window.
let scanProblems (owner: Owner) (sample: ScanMetrics.ScanSample option) (window: string list) : string list =
    [ match sample with
      | None -> "no scan-metrics sample for this daemon"
      | Some s ->
          if s.FilesUnchecked > 0 then
              $"scan left %d{s.FilesUnchecked} file(s) unchecked"

          if s.FilesUncovered > 0 then
              $"scan left %d{s.FilesUncovered} file(s) uncovered"

          if s.FilesDepsGated > 0 then
              $"scan deps-gated %d{s.FilesDepsGated} file(s)"

          match window |> DaemonLog.scanCycles |> List.tryPick (fun c -> DaemonLog.scanCounts c) with
          | None -> "daemon.log window has no complete scan cycle"
          | Some counts when counts.Checked <> s.FilesChecked ->
              $"log scan checked %d{counts.Checked} but scan-metrics says %d{s.FilesChecked}"
          | Some _ -> ()

      match owner with
      | Owner.Process pid ->
          match DaemonLog.announcedPid window with
          | Some p when p <> pid -> $"daemon.log window belongs to pid %d{p}, not the measured %d{pid}"
          | None -> "daemon.log window announces no pid"
          | Some _ -> ()
      | Owner.Host pid ->
          match DaemonLog.attachedHost window with
          | Some(p, _) when p <> pid -> $"session attached to host pid %d{p}, not the measured host %d{pid}"
          | None -> "daemon.log window shows no attach to a repository host"
          | Some _ -> () ]

/// In host mode every session must have attached, each under its own session id.
let hostSessionProblems (sessions: (int * string option) list) : string list =
    let missing =
        [ for s, id in sessions do
              if id.IsNone then
                  yield $"s%d{s} never attached to the repository host" ]

    let ids = sessions |> List.choose snd

    let shared =
        if List.length (List.distinct ids) < List.length ids then
            let detail =
                sessions
                |> List.choose (fun (s, id) -> id |> Option.map (fun i -> $"s%d{s}=%s{i}"))
                |> String.concat " "

            [ $"sessions share a host session id: %s{detail}" ]
        else
            []

    missing @ shared

/// The settle an edit produced, from the settle lines that appeared after it: one edit
/// can be checked in several cohorts under one epoch (a high fan-out edit re-checks its
/// dependents), and the daemon writes a line per cohort, so the edit is settled at the
/// LAST line of the first new epoch.
let editSettle (fresh: DaemonLog.Settle list) : DaemonLog.Settle option =
    match fresh with
    | [] -> None
    | first :: _ -> fresh |> List.filter (fun x -> x.Epoch = first.Epoch) |> List.tryLast

/// Whether the first new epoch is finished: a later epoch has appeared, or no new line
/// has arrived for `quiet`.
let editEpochDone (fresh: DaemonLog.Settle list) (sinceLastLine: TimeSpan) (quiet: TimeSpan) : bool =
    match fresh with
    | [] -> false
    | first :: _ -> fresh |> List.exists (fun x -> x.Epoch > first.Epoch) || sinceLastLine >= quiet

/// The CLI arguments that attach a session to the repository host: `start`, as a legacy
/// daemon starts. `scan` would attach AND force a second full scan on top of the attach's
/// cold one (measured: 62.7 s cold, then 21.6 s forced), churn legacy `start` never does,
/// right before the settled sample and the first edit.
let hostAttachArgs (warmCache: bool) : string =
    (if warmCache then "" else "--no-cache ") + "start"

/// A source file's content with edit `i`'s marker appended: a real content change (so
/// the file is re-checked) that never changes what the file means.
let editedContent (original: string) (i: int) : string =
    let sep = if original.EndsWith("\n") then "" else "\n"
    $"%s{original}%s{sep}// fshw-bench edit %d{i}\n"

/// Parity across the sessions of one repetition: all must have checked the same files.
let parityProblems (checkedBySession: (int * int) list) : string list =
    match checkedBySession |> List.map snd |> List.distinct with
    | []
    | [ _ ] -> []
    | _ ->
        let detail =
            checkedBySession
            |> List.map (fun (s, n) -> $"s%d{s}=%d{n}")
            |> String.concat " "

        [ $"sessions checked different file counts: %s{detail}" ]

/// Test totals of the last complete test cycle in the window, summed over projects.
let testTotals (window: string list) : DaemonLog.TestTotals option =
    window
    |> DaemonLog.testCycles
    |> DaemonLog.lastComplete
    |> Option.bind (fun cycle ->
        let totals =
            DaemonLog.outputLogs cycle
            |> List.map (fun p ->
                if File.Exists p then
                    DaemonLog.parseTestTotals (File.ReadAllText p)
                else
                    None)

        if List.isEmpty totals || totals |> List.exists Option.isNone then
            None
        else
            let ts = totals |> List.choose id

            Some
                { Total = ts |> List.sumBy _.Total
                  Failed = ts |> List.sumBy _.Failed
                  Succeeded = ts |> List.sumBy _.Succeeded
                  Skipped = ts |> List.sumBy _.Skipped })

/// Everything needed to write one record.
type Sample =
    {
        Phase: string
        Footprint: Footprint.Reading option
        FootprintBeforeWalk: Footprint.Reading option
        Walk: Instruments.HeapWalk option
        Scan: ScanMetrics.ScanSample option
        Tests: DaemonLog.TestTotals option
        PhaseMs: float option
        SettleFiles: int option
        Invalid: string list
        /// The box, the daemon's liveness and the sleep gap AT SAMPLE TIME, for records written later
        /// (a deferred retention analysis runs after the daemons stop). `None` = read now.
        Stamp: (Load.Snapshot * bool * TimeSpan) option
        /// The walk's kept raw trace, for the retention analysis.
        Trace: string option
        Retention: Retention.Reading option
        /// The daemon's config echo, on records that read its log window.
        Echo: (string * string) list option
    }

/// Take one sample of a daemon: footprint, and with `heap` a heap walk first (the
/// footprint is then read straight after the walk's collection).
let measure
    (heap: bool)
    (pid: int)
    (port: string option)
    (keepTrace: string option)
    : Footprint.Reading option * Footprint.Reading option * Instruments.HeapWalk option * string list =
    let before =
        if heap then
            Instruments.footprint pid |> Result.toOption
        else
            None

    let walk, walkProblems =
        if heap then
            match Instruments.diagnosticPort pid port with
            | Error e -> None, [ e ]
            | Ok p ->
                match Instruments.walkHeap p (TimeSpan.FromMinutes 10.0) false keepTrace with
                | Ok w -> Some w, (w.Gc.WalkProblem |> Option.toList)
                | Error e -> None, [ e ]
        else
            None, []

    match Instruments.footprint pid with
    | Ok fp -> Some fp, before, walk, walkProblems
    | Error e -> None, before, walk, e :: walkProblems

/// Build and append a record.
let write
    (out: string)
    (runId: string)
    (label: string)
    (mode: string)
    (position: Record.Position)
    (worktree: string)
    (pid: int)
    (ours: int list)
    (preflight: string list)
    (provenance: Record.ConfigProvenance)
    (sleepWindow: Sleep.Window)
    (s: Sample)
    =
    let load, alive, sleepGap =
        match s.Stamp with
        | Some stamp -> stamp
        | None -> Instruments.loadSnapshot ours, Instruments.isAlive pid, Sleep.gap Sleep.system sleepWindow

    // A record whose window spans a sleep is invalid: the daemon was frozen for part of
    // it. The clock gap decides; pmset's log, read only when there is a gap, names it.
    let sleepProblem =
        if sleepGap <= Sleep.Tolerance then
            []
        else
            let events =
                Instruments.run "pmset" "-g log" "/" (TimeSpan.FromSeconds 60.0)
                |> Result.map Sleep.parsePowerLog
                |> Result.defaultValue []

            Sleep.problem sleepGap sleepWindow events |> Option.toList

    let s =
        { s with
            Invalid = s.Invalid @ sleepProblem }
    // At sample time only a FOREIGN daemon, or battery power, counts as contention: load and memory are
    // recorded, but this harness's own daemons are what is being measured, so judging
    // them here would call every cold scan contended. The box itself was judged by
    // `preflight`, before any daemon started.
    let foreignOnly: Load.QuietBar =
        { MaxLoadPerCpu = Double.PositiveInfinity
          MinMemFreePercent = 0 }

    let contended = preflight @ Load.contention foreignOnly load

    let record: Record.BenchRecord =
        { RunId = runId
          RecordedAt = DateTime.UtcNow
          Label = label
          Mode = mode
          Position = position
          Worktree = worktree
          Pid = pid
          Alive = alive
          SleepGapMs = sleepGap.TotalMilliseconds
          Load = load
          Contended = contended
          Footprint = s.Footprint
          FootprintBeforeWalk = s.FootprintBeforeWalk
          Gc = s.Walk |> Option.map _.Gc
          Heap =
            s.Walk
            |> Option.map (fun w -> HeapHistogram.estimate w.Stats, HeapHistogram.top 40 w.Stats)
          Retention = s.Retention
          Config = { provenance with Echo = s.Echo }
          Scan = s.Scan
          Tests = s.Tests
          PhaseMs = s.PhaseMs
          SettleFiles = s.SettleFiles
          Invalid = s.Invalid }

    Record.append out record

    let mb (v: int64) = v / 1048576L

    let fpText =
        match s.Footprint with
        | Some fp ->
            $"footprint %d{mb fp.PhysFootprint} MB (peak %d{mb fp.PhysFootprintPeak}, swapped %d{mb fp.Swapped})"
        | None -> "footprint -"

    let gcText =
        match s.Walk with
        | Some w ->
            $" live %d{mb w.Gc.LiveBytes} MB heap %d{mb w.Gc.HeapSizeAfterGc} MB committed %A{w.Gc.CommittedBytes |> Option.map mb}"
        | None -> ""

    let bad =
        if List.isEmpty s.Invalid then
            ""
        else
            " INVALID: " + String.concat "; " s.Invalid

    let retentionText =
        match s.Retention with
        | Some r when List.isEmpty r.Problems ->
            $" shareable %.1f{r.Narrowed.High * 100.0}%% (typed tree %.1f{r.Narrowed.TypedTreeHigh * 100.0}%%)"
        | Some r -> " retention UNTRUSTED: " + String.concat "; " r.Problems
        | None -> ""

    log
        $"N=%d{position.Sessions} rep %d{position.Rep} s%d{position.Session} %s{position.Phase}: %s{fpText}%s{gcText}%s{retentionText}%s{bad}"

/// Attach the heap-graph retention reading to a sample from its kept trace, then delete
/// the trace unless `keep`. Runs with the daemons already stopped: rebuilding a 40M-node
/// graph took ~9.6 GB and ~30 s in the harness on this repository's own daemon, and that
/// must not compete with the processes being measured.
let withRetention (keep: bool) (s: Sample) : Sample =
    match s.Trace, s.Walk with
    | Some path, Some walk when File.Exists path ->
        let reading, problem =
            match Instruments.readTrace path true with
            | Ok { Graph = Some(g, report) } ->
                Some(Retention.evaluate g report walk.Gc.LiveBytes Retention.importedAssemblies), []
            | Ok _ -> None, [ "retention: the kept trace held no graph" ]
            | Error e -> None, [ $"retention: %s{e}" ]

        if not keep then
            try
                File.Delete path
            with _ ->
                ()

        // A retention failure does not invalidate the MEMORY sample: it is logged, and
        // the record simply carries no retention reading.
        if not (List.isEmpty problem) then
            log (String.concat "; " problem)

        { s with Retention = reading }
    | _ -> s

/// One worktree session of a repetition.
type SessionRun =
    { Session: int
      Worktree: string
      Started: DateTime
      Owner: Owner }

/// A process the harness samples: a legacy daemon (its session's index) or the repository
/// host (session 0).
type Measured =
    { Session: int
      Worktree: string
      Pid: int
      Port: string }

/// Start a CLI command in the background, not waited for, output appended to `outFile`.
let private launch (cfg: Config) (env: (string * string) list) (worktree: string) (args: string) (outFile: string) =
    let exe, prefix = cliInvocation cfg.Cli

    let script =
        $"exec %s{ProcessHelper.quoteArg exe} %s{prefix}%s{args} >> %s{ProcessHelper.quoteArg outFile} 2>&1"

    let psi = ProcessStartInfo("/bin/sh", [| "-c"; script |])
    psi.WorkingDirectory <- worktree
    psi.UseShellExecute <- false

    for k, v in env do
        psi.Environment.[k] <- v

    Process.Start psi

/// Poll `probe` until it answers or `timeout` passes.
let private waitFor (timeout: TimeSpan) (probe: unit -> 'a option) : 'a option =
    let deadline = DateTime.UtcNow + timeout
    let mutable found = probe ()

    while found.IsNone && DateTime.UtcNow < deadline do
        Thread.Sleep 250
        found <- probe ()

    found

/// The host pid a repository host wrote under `stateHome`.
let private hostPidIn (stateHome: string) : int option =
    let dir = Path.Combine(stateHome, "repositories")

    if not (Directory.Exists dir) then
        None
    else
        Directory.GetFiles(dir, "host.pid", SearchOption.AllDirectories)
        |> Array.tryPick (fun f ->
            match Int32.TryParse((File.ReadAllText f).Trim()) with
            | true, pid when Instruments.isAlive pid -> Some pid
            | _ -> None)

let private stopPid (pid: int) =
    let deadline = DateTime.UtcNow.AddSeconds 60.0

    while Instruments.isAlive pid && DateTime.UtcNow < deadline do
        Thread.Sleep 500

    if Instruments.isAlive pid then
        log $"pid %d{pid} survived stop; SIGKILL"
        Instruments.run "kill" $"-9 %d{pid}" "/" (TimeSpan.FromSeconds 10.0) |> ignore

/// The edit phase: `cfg.Edits` rounds of a marker-line edit to `cfg.EditFile` in every
/// session at once. Each session's first NEW `[check] settled` line after the edit gives
/// its settle latency (the daemon measures it from the watcher's first report). The file
/// is restored afterwards, and the restore's own settle awaited but not recorded.
let private editPhase
    (cfg: Config)
    (sessions: SessionRun list)
    (record: SessionRun -> DaemonLog.Settle option -> string list -> unit)
    =
    match cfg.EditFile with
    | Some rel when cfg.Edits > 0 ->
        let file (s: SessionRun) = Path.Combine(s.Worktree, rel)
        let originals = sessions |> List.map (fun s -> s, File.ReadAllText(file s))

        let settleCount (s: SessionRun) =
            readLog s.Worktree |> DaemonLog.settled |> List.length

        let fresh (s: SessionRun) (n: int) =
            readLog s.Worktree |> DaemonLog.settled |> List.skip n

        // Quiet this long after an epoch's last line means the epoch is finished.
        let quiet = TimeSpan.FromSeconds 3.0

        let awaitSettles (before: (SessionRun * int) list) =
            let deadline = DateTime.UtcNow + cfg.SettleTimeout
            let lastChange = Collections.Generic.Dictionary<int, int * DateTime>()

            let isDone (s: SessionRun, n: int) =
                let lines = fresh s n
                let count = List.length lines

                let since =
                    match lastChange.TryGetValue s.Session with
                    | true, (c, at) when c = count -> DateTime.UtcNow - at
                    | _ ->
                        lastChange.[s.Session] <- (count, DateTime.UtcNow)
                        TimeSpan.Zero

                editEpochDone lines since quiet

            let mutable pending = before

            while not (List.isEmpty pending) && DateTime.UtcNow < deadline do
                Thread.Sleep 250
                pending <- pending |> List.filter (isDone >> not)

            before |> List.map (fun (s, n) -> s, editSettle (fresh s n))

        try
            for i in 1 .. cfg.Edits do
                let before = sessions |> List.map (fun s -> s, settleCount s)

                for s, original in originals do
                    File.WriteAllText(file s, editedContent original i)

                for s, settle in awaitSettles before do
                    match settle with
                    | Some st -> record s (Some st) []
                    | None ->
                        record
                            s
                            None
                            [ $"no [check] settled line within %.0f{cfg.SettleTimeout.TotalSeconds} s of edit %d{i}" ]

                Thread.Sleep 1000
        finally
            let before = sessions |> List.map (fun s -> s, settleCount s)

            for s, original in originals do
                File.WriteAllText(file s, original)

            awaitSettles before |> ignore
    | _ -> ()

/// Run the whole matrix.
let runMatrix (cfg: Config) : int =
    let runId =
        DateTime.UtcNow.ToString("yyyyMMddTHHmmss")
        + "-"
        + Guid.NewGuid().ToString("N").Substring(0, 6)
    // Short and TMPDIR-independent: unix socket paths are capped at 104 bytes on macOS.
    let runDir = Path.Combine("/tmp", $"fshw-bench-%s{runId}")
    Directory.CreateDirectory runDir |> ignore
    let mode = modeName cfg.Mode

    let preflight = Load.contention Load.defaultBar (Instruments.loadSnapshot [])

    if not (List.isEmpty preflight) then
        log ("box is CONTENDED: " + String.concat "; " preflight)

        if not cfg.AllowContended then
            log "refusing to measure (pass --allow-contended to record contended samples anyway)"
            exit 3

    let provenance: Record.ConfigProvenance =
        { Strip = cfg.Strip
          Set = cfg.Set |> List.map (fun s -> ConfigOverride.pathText s, s.Json)
          Echo = None }

    let maxSessions = List.max cfg.Sessions
    let wtRoot = Path.Combine(cfg.Repo, ".workspaces")
    Directory.CreateDirectory wtRoot |> ignore

    let worktrees =
        [ for i in 1..maxSessions ->
              let name = $"bench-%s{runId}-%d{i}"
              name, Path.Combine(wtRoot, name) ]

    try
        for i, (name, path) in List.indexed worktrees do
            log $"worktree %d{i + 1}: %s{path} at %s{cfg.Rev}"
            addWorktree cfg name path
            overrideConfig path cfg.Strip cfg.Set

            if not (String.IsNullOrWhiteSpace cfg.Prepare) then
                log $"prepare: %s{cfg.Prepare}"

                match sh cfg.Prepare path (TimeSpan.FromMinutes 30.0) with
                | Ok _ -> ()
                | Error e -> failwith $"prepare failed in %s{path}: %s{e}"

        for sessions in cfg.Sessions do
            for rep in 1 .. cfg.Reps do
                let cacheHome = Path.Combine(runDir, $"cache-n%d{sessions}-r%d{rep}")
                let active = worktrees |> List.truncate sessions |> List.map snd

                if not cfg.WarmCache then
                    for wt in active do
                        try
                            Directory.Delete(Path.Combine(wt, ".fshw", "cache"), true)
                        with _ ->
                            ()

                // Opened before anything starts: every record of this repetition is
                // judged on whether the machine slept at any point since.
                let sleepWindow = Sleep.openWindow Sleep.system
                log $"N=%d{sessions} rep %d{rep} (%s{mode}): starting"

                // Host mode: one state home per repetition, so each starts a fresh host.
                let stateHome = Path.Combine(runDir, $"state-n%d{sessions}-r%d{rep}")

                // No DOTNET_DiagnosticPorts here: the CLI that launches the host is a
                // .NET process with the same environment, so it binds that listen path
                // first and removes it on exit, and the host is left without it
                // (observed: host alive, env set, no socket at the path). The host's own
                // default socket is found from its pid instead (`lsof -U`).
                let hostEnv =
                    [ "FSHW_REPOSITORY_HOST", "1"
                      "FSHW_STATE_HOME", stateHome
                      FsHwPaths.CacheHomeEnvVar, cacheHome ]

                let launched = ResizeArray<Process>()

                let sessionRuns, measured =
                    match cfg.Mode with
                    | RunMode.Legacy ->
                        let daemons =
                            active |> List.mapi (fun i wt -> start cfg runDir cacheHome (i + 1) wt)

                        daemons
                        |> List.map (fun d ->
                            { Session = d.Session
                              Worktree = d.Worktree
                              Started = d.Started
                              Owner = Owner.Process d.Pid }),
                        daemons
                        |> List.map (fun d ->
                            { Session = d.Session
                              Worktree = d.Worktree
                              Pid = d.Pid
                              Port = d.Port })
                    | RunMode.Host ->
                        // Only the FIRST attach's environment reaches the host (it is
                        // spawned by that CLI), so worktree 1 goes alone and the rest wait
                        // for the host and its diagnostic port.
                        let startSession (i: int) (wt: string) =
                            let started = DateTime.UtcNow
                            let out = Path.Combine(runDir, $"scan-n%d{sessions}-r%d{rep}-s%d{i}.log")
                            launched.Add(launch cfg hostEnv wt (hostAttachArgs cfg.WarmCache) out)
                            i, wt, started

                        // A host start that fails must not leak the host or its CLIs:
                        // the repetition's own cleanup only covers a started repetition.
                        let abandon (why: string) =
                            let exe, prefix = cliInvocation cfg.Cli

                            Instruments.runWith
                                hostEnv
                                exe
                                (prefix + "stop --repository")
                                active.Head
                                (TimeSpan.FromMinutes 2.0)
                            |> ignore

                            hostPidIn stateHome |> Option.iter stopPid

                            for p in launched do
                                try
                                    if not p.HasExited then
                                        p.Kill(true)
                                with _ ->
                                    ()

                            failwith why

                        let first = startSession 1 active.Head

                        let hostPid, hostPort =
                            match waitFor (TimeSpan.FromMinutes 5.0) (fun () -> hostPidIn stateHome) with
                            | None -> abandon $"no repository host appeared under %s{stateHome}"
                            | Some pid ->
                                match
                                    waitFor (TimeSpan.FromSeconds 60.0) (fun () ->
                                        Instruments.diagnosticPort pid None |> Result.toOption)
                                with
                                | Some port -> pid, port
                                | None -> abandon $"host pid %d{pid} has no diagnostic socket"

                        let rest = active |> List.tail |> List.mapi (fun i wt -> startSession (i + 2) wt)

                        (first :: rest)
                        |> List.map (fun (i, wt, started) ->
                            { Session = i
                              Worktree = wt
                              Started = started
                              Owner = Owner.Host hostPid }),
                        [ { Session = 0
                            Worktree = active.Head
                            Pid = hostPid
                            Port = hostPort } ]

                let ours = measured |> List.map _.Pid
                let deferred = ResizeArray<Record.Position * Measured * Sample>()

                let tracePath (session: int) (phase: string) =
                    let file = $"%s{runId}-n%d{sessions}-r%d{rep}-s%d{session}-%s{phase}.nettrace"

                    match cfg.KeepTraces with
                    | Some dir -> Some(Path.Combine(dir, file))
                    | None when cfg.Retention -> Some(Path.Combine(runDir, file))
                    | None -> None

                let pos session phase : Record.Position =
                    { Sessions = sessions
                      Rep = rep
                      Session = session
                      Phase = phase }

                let emit (worktree: string) (pid: int) (position: Record.Position) (sample: Sample) =
                    write
                        cfg.Out
                        runId
                        cfg.Label
                        mode
                        position
                        worktree
                        pid
                        ours
                        preflight
                        provenance
                        sleepWindow
                        sample

                let blank (phase: string) : Sample =
                    { Phase = phase
                      Footprint = None
                      FootprintBeforeWalk = None
                      Walk = None
                      Scan = None
                      Tests = None
                      PhaseMs = None
                      SettleFiles = None
                      Invalid = []
                      Stamp = None
                      Trace = None
                      Retention = None
                      Echo = None }

                let sessionScanProblems (s: SessionRun) =
                    let window = readLog s.Worktree
                    let scan = scanSampleSince s.Worktree s.Started

                    scan,
                    window,
                    scanProblems s.Owner scan window
                    @ ConfigOverride.echoProblems cfg.Set (ConfigOverride.echo window)

                try
                    // Phase 1: cold scan, every session concurrently.
                    let deadline = DateTime.UtcNow + cfg.ScanTimeout
                    let mutable pending = sessionRuns
                    let sessionProblems = Collections.Generic.Dictionary<int, string list>()

                    while not (List.isEmpty pending) && DateTime.UtcNow < deadline do
                        Thread.Sleep(TimeSpan.FromSeconds(float cfg.SampleSec))

                        let finished, still =
                            pending
                            |> List.partition (fun s -> (scanSampleSince s.Worktree s.Started).IsSome)

                        for s in finished do
                            let scan, window, problems = sessionScanProblems s
                            sessionProblems.[s.Session] <- problems

                            // Legacy: the session's own daemon is sampled at its scan's end.
                            // Host: the session record carries validity only; the host is
                            // sampled once, when the last session finishes.
                            let fp, fpProblems =
                                match s.Owner with
                                | Owner.Process pid ->
                                    let fp, _, _, p = measure false pid None None
                                    fp, p
                                | Owner.Host _ -> None, []

                            emit
                                s.Worktree
                                (match s.Owner with
                                 | Owner.Process pid
                                 | Owner.Host pid -> pid)
                                (pos s.Session "cold-scan")
                                { blank "cold-scan" with
                                    Footprint = fp
                                    Scan = scan
                                    PhaseMs = scan |> Option.map _.DurationMs
                                    Invalid = fpProblems @ problems
                                    Echo = Some(ConfigOverride.echo window) }

                        for m in measured do
                            if not (Instruments.isAlive m.Pid) then
                                failwith $"pid %d{m.Pid} (session %d{m.Session}) died during the scan"

                        pending <- still

                    if not (List.isEmpty pending) then
                        failwith
                            $"%d{List.length pending} session(s) did not finish their scan within %A{cfg.ScanTimeout}"

                    let parity =
                        sessionRuns
                        |> List.choose (fun s ->
                            scanSampleSince s.Worktree s.Started
                            |> Option.map (fun x -> s.Session, x.FilesChecked))
                        |> parityProblems

                    // Host mode: one host record for the whole scan, invalid if any session is.
                    let hostProblems =
                        match cfg.Mode with
                        | RunMode.Legacy -> []
                        | RunMode.Host ->
                            let attached =
                                sessionRuns
                                |> List.map (fun s ->
                                    s.Session, readLog s.Worktree |> DaemonLog.attachedHost |> Option.map snd)

                            hostSessionProblems attached
                            @ [ for KeyValue(i, ps) in sessionProblems do
                                    for p in ps -> $"s%d{i}: %s{p}" ]

                    match cfg.Mode with
                    | RunMode.Host ->
                        let host = measured.Head
                        let fp, _, _, problems = measure false host.Pid None None

                        emit
                            host.Worktree
                            host.Pid
                            (pos 0 "cold-scan")
                            { blank "cold-scan" with
                                Footprint = fp
                                Invalid = problems @ hostProblems @ parity }
                    | RunMode.Legacy -> ()

                    // Phase 2: settled, nothing forced.
                    Thread.Sleep(TimeSpan.FromSeconds(float cfg.SettleSec))

                    for m in measured do
                        let fp, _, _, problems = measure false m.Pid None None

                        emit
                            m.Worktree
                            m.Pid
                            (pos m.Session "settled")
                            { blank "settled" with
                                Footprint = fp
                                Scan =
                                    scanSampleSince m.Worktree DateTime.MinValue
                                    |> Option.filter (fun _ -> m.Session > 0)
                                Invalid = problems @ parity @ hostProblems }

                    // Phase 3: post-GC, one process at a time so walks do not overlap.
                    if cfg.Heap then
                        for m in measured do
                            let fp, before, walk, problems =
                                measure true m.Pid (Some m.Port) (tracePath m.Session "post-gc")

                            deferred.Add(
                                pos m.Session "post-gc",
                                m,
                                { blank "post-gc" with
                                    Footprint = fp
                                    FootprintBeforeWalk = before
                                    Walk = walk
                                    Invalid = problems @ parity @ hostProblems
                                    Stamp =
                                        Some(
                                            Instruments.loadSnapshot ours,
                                            Instruments.isAlive m.Pid,
                                            Sleep.gap Sleep.system sleepWindow
                                        )
                                    Trace = tracePath m.Session "post-gc" }
                            )

                    // Phase 4: settle latency under concurrency.
                    editPhase cfg sessionRuns (fun s settle problems ->
                        emit
                            s.Worktree
                            (match s.Owner with
                             | Owner.Process pid
                             | Owner.Host pid -> pid)
                            (pos s.Session "edit")
                            { blank "edit" with
                                PhaseMs = settle |> Option.map _.AfterMs
                                SettleFiles = settle |> Option.map _.Files
                                Invalid = problems })

                    // Phase 5: a full check in every worktree at once, then post-GC again.
                    if cfg.Tests then
                        let env =
                            match cfg.Mode with
                            | RunMode.Host -> hostEnv
                            | RunMode.Legacy -> []

                        let checks =
                            sessionRuns
                            |> List.map (fun s ->
                                async {
                                    let sw = Stopwatch.StartNew()
                                    let exe, prefix = cliInvocation cfg.Cli

                                    let result =
                                        Instruments.runWith env exe (prefix + "check") s.Worktree cfg.TestTimeout

                                    return s, sw.Elapsed.TotalMilliseconds, result
                                })
                            |> Async.Parallel
                            |> Async.RunSynchronously
                            |> Array.toList

                        let checkProblems (s: SessionRun, _, result: Result<string, string>) =
                            let totals = testTotals (readLog s.Worktree)

                            [ match result with
                              | Ok _ -> ()
                              | Error e ->
                                  let firstLine = e.Split('\n') |> Array.tryHead |> Option.defaultValue ""
                                  $"s%d{s.Session}: fshw check did not succeed: %s{firstLine}"
                              if totals.IsNone then
                                  $"s%d{s.Session}: no complete test cycle with a summary in its log window" ]

                        for m in measured do
                            // Legacy: this daemon's own session. Host: every session.
                            let mine =
                                checks |> List.filter (fun (s, _, _) -> m.Session = 0 || s.Session = m.Session)

                            let totals = mine |> List.map (fun (s, _, _) -> testTotals (readLog s.Worktree))

                            let summed: DaemonLog.TestTotals option =
                                if totals |> List.forall Option.isSome then
                                    let ts = totals |> List.choose id

                                    Some
                                        { Total = ts |> List.sumBy _.Total
                                          Failed = ts |> List.sumBy _.Failed
                                          Succeeded = ts |> List.sumBy _.Succeeded
                                          Skipped = ts |> List.sumBy _.Skipped }
                                else
                                    None

                            let fp, before, walk, problems =
                                measure cfg.Heap m.Pid (Some m.Port) (tracePath m.Session "after-tests")

                            deferred.Add(
                                pos m.Session "after-tests",
                                m,
                                { blank "after-tests" with
                                    Footprint = fp
                                    FootprintBeforeWalk = before
                                    Walk = walk
                                    Tests = summed
                                    PhaseMs =
                                        match mine |> List.map (fun (_, ms, _) -> ms) with
                                        | [] -> None
                                        | ms -> Some(List.max ms)
                                    Invalid = problems @ (mine |> List.collect checkProblems)
                                    Stamp =
                                        Some(
                                            Instruments.loadSnapshot ours,
                                            Instruments.isAlive m.Pid,
                                            Sleep.gap Sleep.system sleepWindow
                                        )
                                    Trace = tracePath m.Session "after-tests" }
                            )
                finally
                    match cfg.Mode with
                    | RunMode.Legacy ->
                        for m in measured do
                            runCli cfg "stop" m.Worktree (TimeSpan.FromMinutes 2.0) |> ignore
                            stopPid m.Pid
                    | RunMode.Host ->
                        let exe, prefix = cliInvocation cfg.Cli

                        Instruments.runWith
                            hostEnv
                            exe
                            (prefix + "stop --repository")
                            active.Head
                            (TimeSpan.FromMinutes 2.0)
                        |> ignore

                        for m in measured do
                            stopPid m.Pid

                    for p in launched do
                        try
                            if not p.HasExited then
                                p.Kill(true)
                        with _ ->
                            ()

                    // Walk records are written now, with the processes gone, so the graph
                    // analysis never competes with what it measures.
                    for position, m, sample in deferred do
                        let enriched =
                            if cfg.Retention then
                                withRetention cfg.KeepTraces.IsSome sample
                            else
                                sample

                        emit m.Worktree m.Pid position enriched

        0
    finally
        if not cfg.KeepWorktrees then
            for name, path in worktrees do
                removeWorktree cfg name path

/// One sample of an arbitrary running daemon (phase `probe`).
let probe
    (out: string)
    (label: string)
    (pid: int)
    (port: string option)
    (worktree: string option)
    (heap: bool)
    (retention: bool)
    : int =
    if not (Instruments.isAlive pid) then
        eprintfn "pid %d is not alive" pid
        2
    else
        let runId = "probe-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss")
        let sleepWindow = Sleep.openWindow Sleep.system

        let trace =
            if heap && retention then
                Some(Path.Combine(Path.GetTempPath(), $"fshw-bench-probe-%d{pid}-%s{runId}.nettrace"))
            else
                None

        let fp, before, walk, problems = measure heap pid port trace

        let scan, window =
            match worktree with
            | Some wt -> ScanMetrics.readSeries (ScanMetrics.recordPath wt) |> List.tryLast, readLog wt
            | None -> None, []

        let pidProblem =
            match worktree, DaemonLog.announcedPid window with
            | Some _, Some p when p <> pid -> [ $"daemon.log window belongs to pid %d{p}, not %d{pid}" ]
            | _ -> []

        let sample =
            withRetention
                false
                { Phase = "probe"
                  Footprint = fp
                  FootprintBeforeWalk = before
                  Walk = walk
                  Scan = scan
                  Tests = if Option.isSome worktree then testTotals window else None
                  PhaseMs = None
                  SettleFiles = None
                  Invalid = problems @ pidProblem
                  Stamp = Some(Instruments.loadSnapshot [ pid ], true, Sleep.gap Sleep.system sleepWindow)
                  Trace = trace
                  Retention = None
                  Echo = None }

        write
            out
            runId
            label
            "legacy"
            { Sessions = 1
              Rep = 1
              Session = 1
              Phase = "probe" }
            (worktree |> Option.defaultValue "")
            pid
            [ pid ]
            []
            Record.noConfig
            sleepWindow
            sample

        if List.isEmpty problems then 0 else 1
