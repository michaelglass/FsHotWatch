/// The STREAMED per-project run log — the child's raw stdout+stderr, written to
/// `.fshw/test-runs/<runId>/<Project>.output.log` as it arrives.
///
/// It sits beside that run's `<Project>.ctrf.json` because it answers the
/// question CTRF cannot: CTRF says which tests failed, and says NOTHING at all
/// when the runner never reached a writer. A suite SIGKILLed at its timeout
/// produces no report, and the console summary only ever shows a fixed TAIL of
/// the output.
///
/// A tail is the wrong end. The incident this module exists for: an integration
/// suite hit its 900s cap on four consecutive runs, and the 40 lines of tail were
/// forty identical copies of startup logging from seven app instances. The actual
/// cause — "test shard pool is already in use by PID 18024" — was printed in the
/// first SECONDS of the run, at the HEAD, where no tail can reach. Five wrong
/// hypotheses were chased before someone ran the suite by hand.
///
/// So: STREAM, do not buffer. A log assembled from the final capture is not a log
/// of the run that died, because a run that died has no final capture — the kill
/// truncates it (`ProcessOutput.DrainTimedOut`) and a daemon crash loses it
/// entirely. Written through as bytes land, the file holds everything the child
/// said up to the instant it was killed.
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

/// Whether a project's output was saved, and WHERE — the value a diagnostic
/// message may quote a path out of.
///
/// This is a DU and not a `string option` because the defect it replaces was a
/// message naming a saved log that no code ever wrote. Every arm here carries its
/// own evidence: `Written` is only ever constructed by the code that has the open
/// handle, so a path in a message is a path that exists. `Unavailable` says why
/// instead of falling back to silence — "I could not save it" and "I saved it
/// there" must never be spelled the same way.
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

/// A handle that saves nothing, and SAYS so. Every failure to open lands here, so
/// the caller's diagnostic can never quote a path that isn't there.
let private disabled (reason: string) : Handle =
    { Ref = Ref.Unavailable reason
      Write = ignore
      Close = ignore }

/// Which failures to open or close a run log are THE FILESYSTEM SAYING NO — a full or
/// read-only volume, a permission we don't have, a path the OS won't take, a name
/// carrying a character it rejects. Those are conditions of the box, and a test
/// run must survive them: we lose the evidence, not the run.
///
/// Anything else PROPAGATES. A `NullReferenceException` out of this open is a bug
/// in fshw, and swallowing it into "no output log was saved (…)" would file a
/// defect in our own code under the heading "your disk". Same reasoning and same
/// shape as `ProcessHelper.isExpectedDrainException`.
///
/// Pure, so every arm is driven directly — conjuring a real `NotSupportedException`
/// out of a `FileStream` on demand is platform-dependent, and an exception filter
/// nobody can reach is an exception filter nobody has checked.
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
/// NEVER throws. A run log is evidence about a test run, and failing to open a
/// place to write evidence may not fail the run itself; the caller gets a
/// `Ref.Unavailable` carrying the reason, which its diagnostic then states
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

/// Write an fshw-AUTHORED line into an otherwise verbatim log — a separator, a
/// note that the runner was relaunched. Prefixed `[fshw]` so a reader can always
/// tell our words from the child's; everything unprefixed in the file is exactly
/// what the child said.
///
/// Best-effort by construction: a note ABOUT a run may never fail the run.
let note (handle: Handle) (line: string) : unit =
    try
        handle.Write($"%s{Environment.NewLine}[fshw] %s{line}%s{Environment.NewLine}")
    with _ ->
        ()
