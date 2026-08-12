/// The streamed per-project run log — the child's raw stdout+stderr, written to
/// `.fshw/test-runs/<runId>/<Project>.output.log` as it arrives.
///
/// It sits beside that run's `<Project>.ctrf.json` because it answers what CTRF
/// cannot: a suite SIGKILLed at its timeout produces no report at all, and the
/// console summary only ever shows a fixed TAIL. A real timeout's cause ("test shard
/// pool is already in use by PID 18024") was printed in the first seconds of the run,
/// at the head, where no tail reaches.
///
/// Stream, do not buffer: a run that died has no final capture — the kill truncates it
/// (`ProcessOutput.DrainTimedOut`) and a daemon crash loses it entirely. Written
/// through as bytes land, the file holds everything the child said up to the kill.
module FsHotWatch.RunLog

open System
open System.IO

/// The suffix every streamed run log is named with. Distinct from
/// `Ctrf.ReportSuffix`, and NOT `.log` — the bare `.log` extension names the dead
/// flat layout that `Ctrf.tidyRunsDir` purges from the top of `test-runs/`.
[<Literal>]
let Suffix = ".output.log"

/// Where a project's run log lives inside a run's directory.
let pathIn (runDir: string) (project: string) : string = Path.Combine(runDir, project + Suffix)

/// Whether a project's output was saved, and where — the value a diagnostic message
/// may quote a path out of.
///
/// A DU rather than a `string option` so a message cannot name a log no code wrote:
/// `Written` is only constructed by the code holding the open handle, so a quoted path
/// exists, and `Unavailable` carries a reason instead of degrading to silence.
[<RequireQualifiedAccess>]
type Ref =
    /// The run log was opened and streamed to `path`.
    | Written of path: string
    /// No log was saved. `reason` is the operator-facing why.
    | Unavailable of reason: string

/// An open run log: the sink to hand `ProcessHelper.runProcessTo`, the path to
/// quote, and the close.
[<NoComparison; NoEquality>]
type Handle =
    {
        /// Where it is being written — for the diagnostic that points at it.
        Ref: Ref
        /// Chunk sink. Handed straight to `runProcessTo`.
        Write: string -> unit
        /// Idempotent. Safe to call on a handle that never opened.
        Close: unit -> unit
    }

/// A handle that saves nothing and says so. Every failure to open lands here, so the
/// caller's diagnostic can never quote a path that isn't there.
let private disabled (reason: string) : Handle =
    { Ref = Ref.Unavailable reason
      Write = ignore
      Close = ignore }

/// Which failures to open or close a run log are the filesystem refusing — a full or
/// read-only volume, a missing permission, a path or name the OS won't take. A test
/// run must survive those: we lose the evidence, not the run.
///
/// Anything else propagates. A `NullReferenceException` out of the open is an fshw
/// bug, and swallowing it into "no output log was saved (…)" would report it as a disk
/// problem. Same shape as `ProcessHelper.isExpectedDrainException`.
///
/// Pure, so every arm can be driven directly in a test — conjuring a real
/// `NotSupportedException` out of a `FileStream` on demand is platform-dependent.
let internal isExpectedFileFailure (ex: exn) : bool =
    match ex with
    | :? IOException
    | :? UnauthorizedAccessException
    | :? NotSupportedException
    | :? ArgumentException -> true
    | _ -> false

/// Open a project's run log for streaming.
///
/// `AutoFlush` is the streaming guarantee and is not optional: without it the
/// child's output sits in a 1 KB `StreamWriter` buffer that a killed run never
/// flushes — which is the buffer-then-write bug wearing a smaller buffer.
/// `FileShare.Read` so `tail -f` works on a suite that is still running.
///
/// Never throws: failing to open a place to write evidence must not fail the run. The
/// caller gets a `Ref.Unavailable` carrying the reason, which its diagnostic states
/// instead of naming a file.
let openFor (runDir: string) (project: string) : Handle =
    let path = pathIn runDir project

    try
        Directory.CreateDirectory(runDir) |> ignore

        let stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)

        let writer = new StreamWriter(stream)
        writer.AutoFlush <- true

        let closed = ref false
        let gate = obj ()

        { Ref = Ref.Written path
          Write = (fun chunk -> writer.Write(chunk: string))
          Close =
            fun () ->
                lock gate (fun () ->
                    if not closed.Value then
                        closed.Value <- true

                        // A final flush that fails has nothing left to save it, and
                        // this close runs from a `finally` on the plugin's failure
                        // path — throwing here would replace the run's real
                        // diagnostic with a complaint about the log.
                        try
                            writer.Dispose()
                        with ex when isExpectedFileFailure ex ->
                            ()) }
    with ex when isExpectedFileFailure ex ->
        disabled $"could not open %s{path}: %s{ex.Message}"

/// Write an fshw-authored line into an otherwise verbatim log — a separator, a note
/// that the runner was relaunched. Prefixed `[fshw]`, so everything unprefixed in the
/// file is exactly what the child said. Best-effort: a note about a run may never fail
/// the run.
let note (handle: Handle) (line: string) : unit =
    try
        handle.Write($"%s{Environment.NewLine}[fshw] %s{line}%s{Environment.NewLine}")
    with _ ->
        ()
