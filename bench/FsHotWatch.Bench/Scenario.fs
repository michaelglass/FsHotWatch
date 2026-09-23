/// The scripted scenarios: N concurrent worktree daemons on one repository, measured at
/// fixed phases, repeated.
///
/// One repetition of an N-session scenario:
///
/// 1. `cold-scan` — start N daemons at once on N identical worktrees (fresh caches), wait
///    for every first scan; sample each at its scan's end. `physFootprintPeak` there is
///    the kernel's lifetime high-water mark, so the scan peak cannot fall between samples.
/// 2. `settled` — after a quiet interval, sample without forcing anything.
/// 3. `post-gc` — walk each heap (inducing a full blocking collection) and sample the
///    footprint straight after: live set, GC heap, GC committed, and the native rest.
/// 4. `after-tests` (with `--tests`) — run `fshw check` in every worktree concurrently,
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

/// Remove top-level keys from a worktree's `.fshw.json`.
let private stripConfig (worktree: string) (keys: string list) =
    if not (List.isEmpty keys) then
        let file = Path.Combine(worktree, ".fshw.json")
        let node = Text.Json.Nodes.JsonNode.Parse(File.ReadAllText file).AsObject()

        for k in keys do
            node.Remove(k) |> ignore

        File.WriteAllText(file, node.ToJsonString())

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

let private stop (cfg: Config) (d: Daemon) =
    let _ = runCli cfg "stop" d.Worktree (TimeSpan.FromMinutes 2.0)
    let deadline = DateTime.UtcNow.AddSeconds 60.0

    while Instruments.isAlive d.Pid && DateTime.UtcNow < deadline do
        Thread.Sleep 500

    if Instruments.isAlive d.Pid then
        log $"pid %d{d.Pid} survived stop; SIGKILL"
        Instruments.run "kill" $"-9 %d{d.Pid}" "/" (TimeSpan.FromSeconds 10.0) |> ignore

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

/// Validity problems of one session's scan against its own log window.
let scanProblems (pid: int) (sample: ScanMetrics.ScanSample option) (window: string list) : string list =
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

      match DaemonLog.announcedPid window with
      | Some p when p <> pid -> $"daemon.log window belongs to pid %d{p}, not the measured %d{pid}"
      | None -> "daemon.log window announces no pid"
      | Some _ -> () ]

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
    { Phase: string
      Footprint: Footprint.Reading option
      FootprintBeforeWalk: Footprint.Reading option
      Walk: Instruments.HeapWalk option
      Scan: ScanMetrics.ScanSample option
      Tests: DaemonLog.TestTotals option
      PhaseMs: float option
      Invalid: string list }

/// Take one sample of a daemon: footprint, and with `heap` a heap walk first (the
/// footprint is then read straight after the walk's collection).
let measure
    (heap: bool)
    (pid: int)
    (port: string option)
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
                match Instruments.walkHeap p (TimeSpan.FromMinutes 10.0) with
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
    (position: Record.Position)
    (worktree: string)
    (pid: int)
    (ours: int list)
    (preflight: string list)
    (s: Sample)
    =
    let load = Instruments.loadSnapshot ours
    // At sample time only a FOREIGN daemon counts as contention: load and memory are
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
          Position = position
          Worktree = worktree
          Pid = pid
          Alive = Instruments.isAlive pid
          Load = load
          Contended = contended
          Footprint = s.Footprint
          FootprintBeforeWalk = s.FootprintBeforeWalk
          Gc = s.Walk |> Option.map _.Gc
          Heap =
            s.Walk
            |> Option.map (fun w -> HeapHistogram.estimate w.Stats, HeapHistogram.top 40 w.Stats)
          Scan = s.Scan
          Tests = s.Tests
          PhaseMs = s.PhaseMs
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

    log
        $"N=%d{position.Sessions} rep %d{position.Rep} s%d{position.Session} %s{position.Phase}: %s{fpText}%s{gcText}%s{bad}"

/// Run the whole matrix.
let runMatrix (cfg: Config) : int =
    let runId =
        DateTime.UtcNow.ToString("yyyyMMddTHHmmss")
        + "-"
        + Guid.NewGuid().ToString("N").Substring(0, 6)
    // Short and TMPDIR-independent: unix socket paths are capped at 104 bytes on macOS.
    let runDir = Path.Combine("/tmp", $"fshw-bench-%s{runId}")
    Directory.CreateDirectory runDir |> ignore

    let preflight = Load.contention Load.defaultBar (Instruments.loadSnapshot [])

    if not (List.isEmpty preflight) then
        log ("box is CONTENDED: " + String.concat "; " preflight)

        if not cfg.AllowContended then
            log "refusing to measure (pass --allow-contended to record contended samples anyway)"
            exit 3

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
            stripConfig path cfg.Strip

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

                log $"N=%d{sessions} rep %d{rep}: starting %d{sessions} daemon(s)"

                let daemons =
                    active |> List.mapi (fun i wt -> start cfg runDir cacheHome (i + 1) wt)

                let ours = daemons |> List.map _.Pid

                let pos session phase : Record.Position =
                    { Sessions = sessions
                      Rep = rep
                      Session = session
                      Phase = phase }

                try
                    // Phase 1: cold scan, every session concurrently.
                    let deadline = DateTime.UtcNow + cfg.ScanTimeout
                    let mutable pending = daemons

                    while not (List.isEmpty pending) && DateTime.UtcNow < deadline do
                        Thread.Sleep(TimeSpan.FromSeconds(float cfg.SampleSec))

                        let finished, still =
                            pending
                            |> List.partition (fun d -> (scanSampleSince d.Worktree d.Started).IsSome)

                        for d in finished do
                            let scan = scanSampleSince d.Worktree d.Started
                            let window = readLog d.Worktree
                            let fp, before, _, problems = measure false d.Pid None

                            write
                                cfg.Out
                                runId
                                cfg.Label
                                (pos d.Session "cold-scan")
                                d.Worktree
                                d.Pid
                                ours
                                preflight
                                { Phase = "cold-scan"
                                  Footprint = fp
                                  FootprintBeforeWalk = before
                                  Walk = None
                                  Scan = scan
                                  Tests = None
                                  PhaseMs = scan |> Option.map _.DurationMs
                                  Invalid = problems @ scanProblems d.Pid scan window }

                        for d in still do
                            if not (Instruments.isAlive d.Pid) then
                                failwith
                                    $"daemon s%d{d.Session} (pid %d{d.Pid}) died during its scan; see %s{d.Worktree}/logs/daemon.log"

                        pending <- still

                    if not (List.isEmpty pending) then
                        failwith
                            $"%d{List.length pending} daemon(s) did not finish their scan within %A{cfg.ScanTimeout}"

                    let parity =
                        daemons
                        |> List.choose (fun d ->
                            scanSampleSince d.Worktree d.Started
                            |> Option.map (fun s -> d.Session, s.FilesChecked))
                        |> parityProblems

                    // Phase 2: settled, nothing forced.
                    Thread.Sleep(TimeSpan.FromSeconds(float cfg.SettleSec))

                    for d in daemons do
                        let fp, before, _, problems = measure false d.Pid None

                        write
                            cfg.Out
                            runId
                            cfg.Label
                            (pos d.Session "settled")
                            d.Worktree
                            d.Pid
                            ours
                            preflight
                            { Phase = "settled"
                              Footprint = fp
                              FootprintBeforeWalk = before
                              Walk = None
                              Scan = scanSampleSince d.Worktree d.Started
                              Tests = None
                              PhaseMs = None
                              Invalid = problems @ parity }

                    // Phase 3: post-GC, one session at a time so walks do not overlap.
                    if cfg.Heap then
                        for d in daemons do
                            let fp, before, walk, problems = measure true d.Pid (Some d.Port)

                            write
                                cfg.Out
                                runId
                                cfg.Label
                                (pos d.Session "post-gc")
                                d.Worktree
                                d.Pid
                                ours
                                preflight
                                { Phase = "post-gc"
                                  Footprint = fp
                                  FootprintBeforeWalk = before
                                  Walk = walk
                                  Scan = scanSampleSince d.Worktree d.Started
                                  Tests = None
                                  PhaseMs = None
                                  Invalid = problems @ parity }

                    // Phase 4: a full check in every worktree at once, then post-GC again.
                    if cfg.Tests then
                        let checks =
                            daemons
                            |> List.map (fun d ->
                                async {
                                    let sw = Stopwatch.StartNew()
                                    let result = runCli cfg "check" d.Worktree cfg.TestTimeout
                                    return d, sw.Elapsed.TotalMilliseconds, result
                                })
                            |> Async.Parallel
                            |> Async.RunSynchronously

                        for d, ms, result in checks do
                            let window = readLog d.Worktree
                            let fp, before, walk, problems = measure cfg.Heap d.Pid (Some d.Port)
                            let totals = testTotals window

                            let checkProblem =
                                match result with
                                | Ok _ -> []
                                | Error e ->
                                    let firstLine = e.Split('\n') |> Array.tryHead |> Option.defaultValue ""
                                    [ $"fshw check did not succeed: %s{firstLine}" ]

                            let noTests =
                                if totals.IsNone then
                                    [ "no complete test cycle with a summary in this daemon's log window" ]
                                else
                                    []

                            write
                                cfg.Out
                                runId
                                cfg.Label
                                (pos d.Session "after-tests")
                                d.Worktree
                                d.Pid
                                ours
                                preflight
                                { Phase = "after-tests"
                                  Footprint = fp
                                  FootprintBeforeWalk = before
                                  Walk = walk
                                  Scan = scanSampleSince d.Worktree d.Started
                                  Tests = totals
                                  PhaseMs = Some ms
                                  Invalid = problems @ checkProblem @ noTests }
                finally
                    for d in daemons do
                        stop cfg d

        0
    finally
        if not cfg.KeepWorktrees then
            for name, path in worktrees do
                removeWorktree cfg name path

/// One sample of an arbitrary running daemon (phase `probe`).
let probe (out: string) (label: string) (pid: int) (port: string option) (worktree: string option) (heap: bool) : int =
    if not (Instruments.isAlive pid) then
        eprintfn "pid %d is not alive" pid
        2
    else
        let runId = "probe-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss")
        let fp, before, walk, problems = measure heap pid port

        let scan, window =
            match worktree with
            | Some wt -> ScanMetrics.readSeries (ScanMetrics.recordPath wt) |> List.tryLast, readLog wt
            | None -> None, []

        let pidProblem =
            match worktree, DaemonLog.announcedPid window with
            | Some _, Some p when p <> pid -> [ $"daemon.log window belongs to pid %d{p}, not %d{pid}" ]
            | _ -> []

        write
            out
            runId
            label
            { Sessions = 1
              Rep = 1
              Session = 1
              Phase = "probe" }
            (worktree |> Option.defaultValue "")
            pid
            [ pid ]
            []
            { Phase = "probe"
              Footprint = fp
              FootprintBeforeWalk = before
              Walk = walk
              Scan = scan
              Tests = if Option.isSome worktree then testTotals window else None
              PhaseMs = None
              Invalid = problems @ pidProblem }

        if List.isEmpty problems then 0 else 1
