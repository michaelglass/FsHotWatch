/// CPU overhead of tracing: interleaved runs of the original (untraced) app and the woven
/// one, each measured as its process tree's user + system CPU from `getrusage`, never wall
/// time. The bar compares medians.
module TestPrune.Trace.Overhead

open System
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
        line $"  sample %s{kind}  %s{msOf s.Cpu} ms  exit %d{s.ExitCode}"

    sb.ToString()

/// Environment that keeps an untraced launch untraced even when this process runs traced.
let internal untracedEnv =
    [ Contract.OutEnv, ""
      Contract.IdsEnv, ""
      Contract.RepoRootEnv, ""
      Contract.ParentScopeEnv, "" ]

/// One launch measured by `getrusage` before and after.
let internal measure traced exe args env workDir timeout : Sample =
    let struct (before, _) = Rusage.children ()
    let code, _ = Launch.run exe args env workDir timeout
    let struct (after, rss) = Rusage.children ()

    { Traced = traced
      Cpu = after - before
      MaxRssBytes = rss
      ExitCode = code }

/// Prepare the project, then `reps` times run the original app from `bin/Debug/<tfm>/`
/// untraced and the woven app traced, interleaved, both from the repository root.
/// `Error` when `reps` is below 1 or the project cannot be prepared. Children of this
/// process that finish during a launch are counted in it: run nothing else meanwhile.
let run
    (req: TraceSession.PrepareRequest)
    (appArgs: string list)
    (reps: int)
    (timeout: TimeSpan)
    : Result<OverheadReport, string> =
    if reps < 1 then
        Error $"reps must be at least 1, got %d{reps}"
    else
        TraceSession.prepareProject req
        |> Result.map (fun launch ->
            let original =
                Path.Combine(
                    req.ProjectDir,
                    "bin",
                    "Debug",
                    Path.GetFileName launch.Shadow.Dir,
                    Path.GetFileName launch.Apphost
                )

            List.init reps (fun _ ->
                [ measure false original appArgs untracedEnv req.RepoRoot timeout
                  measure true launch.Apphost appArgs launch.Env req.RepoRoot timeout ])
            |> List.concat
            |> summarize)
