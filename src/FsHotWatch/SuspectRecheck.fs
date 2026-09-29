/// Re-check, in this process and before a verdict is graded, the files whose latest
/// check the daemon could not trust.
///
/// A check that declared a type incompatible with ITSELF is not a reading of the code
/// (`FcsDiagnosticFilter`), and its errors are reported under `fcs-internal`, which the
/// verdict counts as a checker fault: alone they give no verdict. Clearing them used to
/// take `fshw stop`, because the suspect answer sat in the in-memory check-result cache
/// and every later scan of the unchanged file replayed it. Stopping the daemon also
/// discards everything else it holds, including the test evidence a completed run
/// earned for this exact tree, so the next check re-ran the suite to re-earn it.
///
/// When a check settles holding such entries, the daemon drops only the suspect answers
/// (each file's cached result and its project's checker generation), scans once, and
/// settles again before the verdict is read. Test evidence is not touched, and the scan
/// re-earns nothing on an unchanged tree. It happens at most once per tree, so an answer
/// that stays suspect is reported, not retried forever.
module FsHotWatch.SuspectRecheck

/// What one re-check did.
[<RequireQualifiedAccess>]
type Outcome =
    /// An exclusive run (a test run, a build) is in flight, held by `holders`. Nothing
    /// was dropped or re-checked: its result folds against the checker state it started
    /// under, and a re-check under it would race that fold.
    | Refused of holders: string list
    /// Every file in `files` had its answer dropped and was checked again. `survivors`
    /// still hold a suspect answer after the re-check.
    | Rechecked of files: string list * survivors: string list

/// The daemon operations a re-check is made of, injected so the order is testable.
[<NoComparison; NoEquality>]
type Seams =
    {
        /// The names holding an exclusive run right now; empty when none does.
        ExclusiveHolders: unit -> string list
        /// Files whose latest check left a failing `fcs-internal` entry.
        SuspectFiles: unit -> string list
        /// Drop one file's cached answer and start a new checker generation for its
        /// project, so the next check of it asks the checker afresh.
        Drop: string -> unit
        /// Check the registered files and report their diagnostics.
        Recheck: unit -> Async<unit>
    }

/// Refuse while an exclusive run is held; otherwise drop every suspect file's answer,
/// re-check once, and report which of those files are still suspect. A file that first
/// turns suspect during the re-check was not dropped here, so it is not reported as
/// this re-check's survivor; the next verdict still sees its entry.
let run (seams: Seams) : Async<Outcome> =
    async {
        match seams.ExclusiveHolders() with
        | _ :: _ as holders -> return Outcome.Refused holders
        | [] ->
            match seams.SuspectFiles() with
            | [] -> return Outcome.Rechecked([], [])
            | files ->
                files |> List.iter seams.Drop
                do! seams.Recheck()
                let still = seams.SuspectFiles() |> Set.ofList
                return Outcome.Rechecked(files, files |> List.filter still.Contains)
    }

/// The trees an automatic re-check has already run for. One per daemon: a tree gets one
/// re-check, whichever check first settled on it, and a check of a different tree gets
/// its own.
type RetriedTrees() =
    let claimed = System.Collections.Concurrent.ConcurrentDictionary<string, unit>()

    /// Claim the one re-check `treeHash` is allowed. `false` when it was already claimed.
    member _.TryClaim(treeHash: string) : bool = claimed.TryAdd(treeHash, ())

/// A re-check that has waited this many times for exclusive runs to finish stops waiting.
/// Each wait is a full settle under its own deadline, so this bounds only a host that
/// keeps starting new exclusive runs.
[<Literal>]
let MaxExclusiveWaits = 20

/// What the automatic re-check needs beyond `Seams`.
[<NoComparison; NoEquality>]
type SettleSeams =
    {
        Recheck: Seams
        /// The content address of the tree being settled; `None` when it cannot be read.
        TreeHash: unit -> string option
        /// Wait until the host owns no work: every exclusive run finished and folded.
        AwaitQuiet: unit -> Async<unit>
        Log: string -> unit
    }

/// The one line the automatic re-check logs.
let logLine (outcome: Outcome) : string =
    match outcome with
    | Outcome.Refused holders ->
        let who = String.concat ", " holders
        $"suspect re-check skipped: %s{who} still held an exclusive run after %d{MaxExclusiveWaits} waits"
    | Outcome.Rechecked(files, survivors) ->
        let named paths = String.concat ", " paths

        let left =
            match survivors with
            | [] -> "no survivor"
            | _ -> $"%d{survivors.Length} survivor(s), still suspect: %s{named survivors}"

        $"re-checked %d{files.Length} suspect file(s) once for this tree: %s{named files}; %s{left}"

/// Before a verdict is graded: when the settled check holds suspect entries and this tree
/// has not been re-checked yet, re-check them once (waiting for exclusive runs to finish,
/// never interrupting one), settle again, and log the outcome. `None` when nothing was
/// re-checked.
let settle (retried: RetriedTrees) (seams: SettleSeams) : Async<Outcome option> =
    async {
        match seams.Recheck.SuspectFiles() with
        | [] -> return None
        | _ ->
            match seams.TreeHash() with
            | None ->
                seams.Log "suspect re-check skipped: the tree could not be read, so its one re-check cannot be counted"
                return None
            | Some tree when not (retried.TryClaim tree) -> return None
            | Some _ ->
                // `run` refuses while an exclusive run is held; each refusal waits for the
                // host to finish its work, so a test run is never interrupted.
                let rec attempt waits =
                    async {
                        match! run seams.Recheck with
                        | Outcome.Refused _ when waits < MaxExclusiveWaits ->
                            do! seams.AwaitQuiet()
                            return! attempt (waits + 1)
                        | outcome -> return outcome
                    }

                let! outcome = attempt 0

                match outcome with
                | Outcome.Rechecked _ -> do! seams.AwaitQuiet()
                | Outcome.Refused _ -> ()

                seams.Log(logLine outcome)
                return Some outcome
    }
