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
///
/// The same re-check covers any other diagnostic-only red in a file the change did not
/// touch (`diagnosticOnlyReds`), and each one files a `Receipt` in the outcome store.
module FsHotWatch.SuspectRecheck

open System
open System.IO
open System.Text.Json.Nodes

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

/// Why a file was re-checked.
[<RequireQualifiedAccess>]
type Reason =
    /// Its check declared a type incompatible with itself.
    | FcsInternal
    /// It held a failing diagnostic, no other kind of red was on the ledger, and this
    /// daemon saw no edit to it.
    | DiagnosticOnly

module Reason =
    let token =
        function
        | Reason.FcsInternal -> "fcs-internal"
        | Reason.DiagnosticOnly -> "diagnostic-only"

    let tryOfToken =
        function
        | "fcs-internal" -> Some Reason.FcsInternal
        | "diagnostic-only" -> Some Reason.DiagnosticOnly
        | _ -> None

/// The plugins whose ledger entries are statements about a source file. A red from
/// anything else (a build, a test run) is evidence of its own.
let private diagnosticPlugins =
    Set.ofList
        [ PluginActivity.FcsPluginName
          PluginActivity.FcsInternalPluginName
          PluginActivity.AnalyzersPluginName
          "lint" ]

/// The files holding a diagnostic-only red in a file the change did not touch: `failing`
/// is every failing ledger entry by file, `edited` says whether this daemon saw an edit.
///
/// A red beside any other kind of red selects nothing. A real compile error fails the
/// build, so it never reaches this as diagnostics alone, and a test failure means the
/// run is red whatever the diagnostics say. Pass only ERROR-severity entries: a warning
/// is deterministic, and a standing one would pay for a re-check on every tree.
let diagnosticOnlyReds
    (failing: Map<string, (string * ErrorLedger.ErrorEntry) list>)
    (edited: string -> bool)
    : string list =
    let onlyDiagnostics =
        failing
        |> Map.forall (fun _ entries -> entries |> List.forall (fun (plugin, _) -> diagnosticPlugins.Contains plugin))

    if onlyDiagnostics then
        failing
        |> Map.toList
        |> List.map fst
        |> List.filter (fun file -> Path.IsPathRooted file && not (edited file))
    else
        []

/// The daemon operations a re-check is made of, injected so the order is testable.
[<NoComparison; NoEquality>]
type Seams =
    {
        /// The names holding an exclusive run right now; empty when none does.
        ExclusiveHolders: unit -> string list
        /// Files to re-check: those whose latest check left a failing `fcs-internal` entry,
        /// and diagnostic-only reds in files the change did not touch (`diagnosticOnlyReds`).
        SuspectFiles: unit -> string list
        /// Files still holding a failing diagnostic. Read after a re-check, when the rule
        /// `SuspectFiles` selects by no longer applies: a file does not stop being red
        /// because some other kind of red has joined it.
        StillSuspect: unit -> string list
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
                let still = seams.StillSuspect() |> Set.ofList
                return Outcome.Rechecked(files, files |> List.filter still.Contains)
    }

/// The files this daemon's watcher has reported an edit to: what "the change touched" means
/// to a re-check. Monotonic for the daemon's life, so a once-edited file is left alone
/// rather than re-checked on a guess.
type EditedFiles() =
    let files = System.Collections.Concurrent.ConcurrentDictionary<string, unit>()

    member _.Note(changes: Events.FileChangeKind list) =
        for change in changes do
            match change with
            | Events.SourceChanged edited ->
                for file in edited do
                    files[file] <- ()
            | Events.ProjectChanged _
            | Events.SolutionChanged -> ()

    member _.Contains(file: string) : bool = files.ContainsKey file

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

/// The record of one settled re-check.
type Receipt =
    {
        /// The content address of the tree it ran for.
        Tree: string
        /// Every file re-checked, and why.
        Rechecked: (string * Reason) list
        /// The files still red after it.
        Survivors: string list
    }

module Receipt =
    let private keyOf (tree: string) =
        ActionKey.create "suspect-recheck" [ "tree", tree ]

    let toJson (receipt: Receipt) : JsonNode =
        let o = JsonObject()
        o["tree"] <- JsonValue.Create receipt.Tree

        o["rechecked"] <-
            receipt.Rechecked
            |> List.map (fun (file, reason) ->
                JsonObject(
                    dict
                        [ "file", JsonValue.Create file :> JsonNode
                          "why", JsonValue.Create(Reason.token reason) :> JsonNode ]
                )
                :> JsonNode)
            |> Array.ofList
            |> JsonArray

        o["survivors"] <-
            receipt.Survivors
            |> List.map (fun file -> JsonValue.Create file :> JsonNode)
            |> Array.ofList
            |> JsonArray

        o

    /// `None` for anything this build cannot read.
    let tryOfJson (node: JsonNode) : Receipt option =
        try
            let rechecked =
                node["rechecked"].AsArray()
                |> Seq.map (fun e -> e["file"].GetValue<string>(), Reason.tryOfToken (e["why"].GetValue<string>()))
                |> List.ofSeq

            if rechecked |> List.exists (snd >> Option.isNone) then
                None
            else
                Some
                    { Tree = node["tree"].GetValue<string>()
                      Rechecked = rechecked |> List.map (fun (file, reason) -> file, Option.get reason)
                      Survivors = node["survivors"].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq }
        with _ ->
            None

    /// File `receipt` with repo-relative paths: the store is shared across checkouts,
    /// and a key is a content address that speaks for any of them.
    let file (store: OutcomeStore.Store) (binary: string) (repoRoot: string) (receipt: Receipt) : unit =
        let relative (file: string) =
            Path.GetRelativePath(repoRoot, file).Replace('\\', '/')

        let stored =
            { receipt with
                Rechecked = receipt.Rechecked |> List.map (fun (file, reason) -> relative file, reason)
                Survivors = receipt.Survivors |> List.map relative }

        let outcome =
            if List.isEmpty receipt.Survivors then
                OutcomeStore.Outcome.Pass
            else
                OutcomeStore.Outcome.Fail

        store.RecordWithOutputs(
            OutcomeStore.Record.ofAction
                (keyOf receipt.Tree)
                "suspect-recheck"
                outcome
                binary
                repoRoot
                DateTimeOffset.UtcNow,
            Some((toJson stored).ToJsonString())
        )
        |> ignore

    /// The newest receipt filed for `tree`, from any process.
    let read (store: OutcomeStore.Store) (tree: string) : Receipt option =
        match store.LookupWithOutputs(keyOf tree) with
        | Some(_, Some outputs) -> JsonNode.Parse outputs |> tryOfJson
        | _ -> None

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
        /// Why each currently suspect file is suspect; a file absent here is
        /// `Reason.DiagnosticOnly`. Read once, before anything is dropped.
        Reasons: unit -> Map<string, Reason>
        /// File the receipt of a re-check that ran.
        FileReceipt: Receipt -> unit
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
/// never interrupting one), settle again, log the outcome and file its receipt. `None` when nothing was
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
            | Some tree ->
                let reasons = seams.Reasons()

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

                // A plugin's ledger write can land after the scan the re-check waited for, so
                // what survived is read only once the re-check has settled.
                let! outcome =
                    async {
                        match outcome with
                        | Outcome.Rechecked(files, _) ->
                            do! seams.AwaitQuiet()
                            let still = seams.Recheck.StillSuspect() |> Set.ofList
                            return Outcome.Rechecked(files, files |> List.filter still.Contains)
                        | Outcome.Refused _ -> return outcome
                    }

                seams.Log(logLine outcome)

                match outcome with
                | Outcome.Rechecked(files, survivors) when not files.IsEmpty ->
                    seams.FileReceipt
                        { Tree = tree
                          Rechecked =
                            files
                            |> List.map (fun file ->
                                file, reasons |> Map.tryFind file |> Option.defaultValue Reason.DiagnosticOnly)
                          Survivors = survivors }
                | _ -> ()

                return Some outcome
    }
