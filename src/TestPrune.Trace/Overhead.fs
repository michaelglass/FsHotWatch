/// CPU overhead of tracing: interleaved runs of the original (untraced) app and the woven
/// one, each measured as its process tree's user + system CPU from `getrusage`, never wall
/// time. The bar compares medians.
module TestPrune.Trace.Overhead

open System
open System.Diagnostics
open System.IO
open System.Text
open TestPrune.Trace.Recorder

/// One launch.
type Sample =
    {
        Traced: bool
        /// User + system CPU of the launch's process tree.
        Cpu: TimeSpan
        /// The largest peak RSS of any child so far (`getrusage` keeps a maximum, not a
        /// per-launch value): an upper bound on this launch's peak.
        MaxRssBytes: int64
        ExitCode: int
        /// The launch's output (the tail `Launch.run` keeps) when it exited nonzero, so the
        /// report can say why; empty for a launch that exited 0.
        Output: string
        /// Wall time of the launch.
        Wall: TimeSpan
        /// Live processes descending from this one when the launch started: a process an
        /// earlier launch left behind burns CPU that `getrusage` charges to a later launch,
        /// the one during which it is reaped.
        LiveDescendants: int
        /// Bytes of the recorder dumps the launch wrote, and how many dumps; 0 for an untraced
        /// launch. Every traced launch starts from an empty dump directory, so this is its own
        /// recorded work, which should not grow from one repetition to the next.
        DumpBytes: int64
        DumpFiles: int
    }

/// The medians, their spread and their ratio.
type OverheadReport =
    {
        Samples: Sample list
        BaseMedianCpu: TimeSpan
        TracedMedianCpu: TimeSpan
        BaseMinCpu: TimeSpan
        BaseMaxCpu: TimeSpan
        TracedMinCpu: TimeSpan
        TracedMaxCpu: TimeSpan
        /// Traced median / untraced median.
        Ratio: float
        /// The largest peak RSS seen after a traced launch.
        TracedMaxRssBytes: int64
    }

/// The bar: traced median CPU at most this multiple of the untraced median.
[<Literal>]
let RatioBar = 1.15

/// The middle value; the mean of the middle two for an even count.
let median (xs: TimeSpan list) : TimeSpan =
    let sorted = List.sort xs |> Array.ofList
    let n = sorted.Length
    (sorted.[(n - 1) / 2] + sorted.[n / 2]) / 2.0

/// The report over samples holding at least one untraced and one traced launch.
let summarize (samples: Sample list) : OverheadReport =
    let cpu traced =
        samples |> List.filter (fun s -> s.Traced = traced) |> List.map (fun s -> s.Cpu)

    let baseCpu, tracedCpu = cpu false, cpu true
    let baseMedian, tracedMedian = median baseCpu, median tracedCpu

    { Samples = samples
      BaseMedianCpu = baseMedian
      TracedMedianCpu = tracedMedian
      BaseMinCpu = List.min baseCpu
      BaseMaxCpu = List.max baseCpu
      TracedMinCpu = List.min tracedCpu
      TracedMaxCpu = List.max tracedCpu
      Ratio = tracedMedian / baseMedian
      TracedMaxRssBytes =
        samples
        |> List.filter (fun s -> s.Traced)
        |> List.map (fun s -> s.MaxRssBytes)
        |> List.max }

/// The bar, and tracing must not change how the app exits: an overhead measured over a
/// traced run that failed where the untraced one passed measures something else.
let passes (r: OverheadReport) =
    r.Ratio <= RatioBar
    && (r.Samples |> List.map (fun s -> s.ExitCode) |> List.distinct |> List.length) = 1

/// The human report.
let render (r: OverheadReport) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore
    let msOf (t: TimeSpan) = $"%.0f{t.TotalMilliseconds}"
    let verdict = if passes r then "PASS" else "FAIL"
    line $"overhead  %s{verdict}  ratio %.3f{r.Ratio} (bar <= %g{RatioBar})"

    line $"  untraced  median %s{msOf r.BaseMedianCpu} ms  range %s{msOf r.BaseMinCpu}-%s{msOf r.BaseMaxCpu} ms"

    line
        $"  traced    median %s{msOf r.TracedMedianCpu} ms  range %s{msOf r.TracedMinCpu}-%s{msOf r.TracedMaxCpu} ms  peak rss %.1f{float r.TracedMaxRssBytes / 1048576.0} MiB"

    for s in r.Samples do
        let kind = if s.Traced then "traced  " else "untraced"

        let dumps =
            if s.DumpFiles > 0 then
                $"  dumps %.1f{float s.DumpBytes / 1024.0} KiB in %d{s.DumpFiles}"
            else
                ""

        line
            $"  sample %s{kind}  %s{msOf s.Cpu} ms  wall %s{msOf s.Wall} ms  live %d{s.LiveDescendants}  exit %d{s.ExitCode}%s{dumps}"

        if s.ExitCode <> 0 then
            line "    output ends:"

            for l in Launch.outputLines s.Output do
                line $"      %s{l}"

    sb.ToString()

/// Environment that keeps an untraced launch untraced even when this process runs traced.
let internal untracedEnv =
    [ Contract.OutEnv, ""
      Contract.IdsEnv, ""
      Contract.RepoRootEnv, ""
      Contract.ParentScopeEnv, "" ]

/// The `Error` for a machine whose CPU time cannot be read.
let internal cannotMeasure (why: string) = $"cannot measure CPU overhead: %s{why}"

/// `read` (`Rusage.children`) with any exception it raises as the cannot-measure `Error`.
let internal readCpu (read: unit -> struct (TimeSpan * int64)) : Result<struct (TimeSpan * int64), string> =
    try
        Ok(read ())
    with e ->
        Error(cannotMeasure $"getrusage failed: %s{e.Message}")

/// One launch measured by `read` (`getrusage`) before and after; `Error`, launching
/// nothing, when the first read fails.
let internal measure read traced exe args env workDir timeout : Result<Sample, string> =
    readCpu read
    |> Result.bind (fun (struct (before, _)) ->
        let clock = Stopwatch.StartNew()
        let code, output = Launch.run exe args env workDir timeout
        let wall = clock.Elapsed

        readCpu read
        |> Result.map (fun (struct (after, rss)) ->
            { Traced = traced
              Cpu = after - before
              MaxRssBytes = rss
              ExitCode = code
              Output = if code = 0 then "" else output
              Wall = wall
              LiveDescendants = 0
              DumpBytes = 0L
              DumpFiles = 0 }))

/// Empty `dir`, so the dumps in it afterwards are the next launch's alone.
let internal clearDumps (dir: string) =
    Directory.Delete(dir, true)
    Directory.CreateDirectory dir |> ignore

/// The processes descending from `root` among `(pid, parent pid)` pairs, `root` excluded.
let internal descendantsOf (root: int) (table: (int * int) list) : Set<int> =
    let rec grow (found: Set<int>) =
        let next =
            table
            |> List.filter (fun (pid, parent) -> pid <> root && (parent = root || found.Contains parent))
            |> List.map fst
            |> Set.ofList

        if next = found then found else grow next

    grow Set.empty

/// `(pid, parent pid)` of every line of `ps -o pid= -o ppid=` output but the one for `self`
/// (the `ps` itself); a line that is not two numbers is skipped.
let internal parseProcessTable (self: int) (output: string) : (int * int) list =
    output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Seq.choose (fun l ->
        let fields = l.Split(' ', StringSplitOptions.RemoveEmptyEntries)

        if fields.Length <> 2 then
            None
        else
            match Int32.TryParse fields.[0], Int32.TryParse fields.[1] with
            | (true, p), (true, pp) when p <> self -> Some(p, pp)
            | _ -> None)
    |> Seq.toList

/// `(pid, parent pid)` of every process, from `ps` (macOS and Linux), without the `ps` itself.
let internal processTable () : (int * int) list =
    let psi =
        ProcessStartInfo("ps", "-A -o pid= -o ppid=", RedirectStandardOutput = true, UseShellExecute = false)

    using (Process.Start psi) (fun p ->
        let out = p.StandardOutput.ReadToEnd()
        p.WaitForExit()
        parseProcessTable p.Id out)

/// Live processes descending from this one.
let internal liveDescendants () =
    (descendantsOf Environment.ProcessId (processTable ())).Count

/// `run` on a given platform and CPU reader: `Error` on Windows, where there is no
/// `getrusage`, before anything is prepared or launched.
let internal runWith
    (isWindows: bool)
    (read: unit -> struct (TimeSpan * int64))
    (live: unit -> int)
    (req: TraceSession.PrepareRequest)
    (appArgs: string list)
    (reps: int)
    (timeout: TimeSpan)
    : Result<OverheadReport, string> =
    if reps < 1 then
        Error $"reps must be at least 1, got %d{reps}"
    elif isWindows then
        Error(cannotMeasure "getrusage is macOS/Linux only")
    else
        TraceSession.prepareProject req
        |> Result.bind (fun launch ->
            let original =
                Path.Combine(
                    req.ProjectDir,
                    "bin",
                    "Debug",
                    Path.GetFileName launch.Shadow.Dir,
                    Path.GetFileName launch.Apphost
                )

            // Counted before the launch's first CPU read, so the count's own `ps` is not
            // charged to the launch.
            let launched traced exe env =
                let alive = live ()

                measure read traced exe appArgs env req.RepoRoot timeout
                |> Result.map (fun s -> { s with LiveDescendants = alive })

            let rec go i acc =
                if i = reps then
                    Ok(summarize (List.rev acc))
                else
                    launched false original untracedEnv
                    |> Result.bind (fun untraced ->
                        clearDumps launch.DumpDir

                        launched true launch.Apphost launch.Env
                        |> Result.bind (fun traced ->
                            let dumps = Directory.GetFiles launch.DumpDir

                            let traced =
                                { traced with
                                    DumpBytes = dumps |> Seq.sumBy (fun f -> FileInfo(f).Length)
                                    DumpFiles = dumps.Length }

                            go (i + 1) (traced :: untraced :: acc)))

            go 0 [])

/// Prepare the project, then `reps` times run the original app from `bin/Debug/<tfm>/`
/// untraced and the woven app traced, interleaved, both from the repository root. Each
/// traced launch starts from an empty dump directory and reports what it dumped; every
/// launch reports its wall time and the live descendants of this process when it started.
/// `Error` when `reps` is below 1, on Windows (no `getrusage`), when `getrusage` fails or
/// the project cannot be prepared. Children of this process that finish during a launch
/// are counted in it: run nothing else meanwhile.
let run req appArgs reps timeout : Result<OverheadReport, string> =
    runWith (OperatingSystem.IsWindows()) Rusage.children liveDescendants req appArgs reps timeout
