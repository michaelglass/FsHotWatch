module FsHotWatch.PluginActivity

open System
open System.Collections.Generic
open FsHotWatch.Events

/// Synthetic plugin name used to surface check-pipeline activity in IPC status output.
[<Literal>]
let FcsPluginName = "fcs"

/// Ledger key for faults in fshw's OWN checking process rather than in the code being
/// checked. Distinct from `FcsPluginName` on purpose: a reader scanning the ledger must
/// be able to separate "your code is wrong" from "our checker went stale", and a shared
/// key makes that impossible. Nothing under this key is a finding about the user's
/// code, so nothing here can redden a run: the self-incompatible diagnostics are
/// `Info`, and the other errors of the check that produced one keep their severity
/// but are classified as checker faults by the verdict — alone, they leave it with
/// no verdict, never a green.
[<Literal>]
let FcsInternalPluginName = "fcs-internal"

/// The test-prune plugin's registered name, and therefore its LEDGER KEY.
///
/// Named here beside `FcsPluginName` because the verdict's `NonTestRed` projection has to ask of a
/// red "is this about the TESTS, or about something else this run would have found
/// either way" — and the only way to ask is by ledger source. The plugin registers with
/// this literal; a second copy spelled at the asking site is a second copy that can
/// drift, and a drifted copy answers "not about the tests" for every red there is.
[<Literal>]
let TestPrunePluginName = "test-prune"

/// The analyzers plugin's registered name, and therefore its LEDGER KEY. Named here for
/// the same reason as `TestPrunePluginName`: the verdict asks of an analyzer entry
/// whether it was computed from a check that is itself a checker fault, and the only
/// way to ask is by ledger source.
[<Literal>]
let AnalyzersPluginName = "analyzers"

/// Convention: each plugin emits one long-lived "primary" subtask per run
/// whose label is the user-facing status line. The renderer prefers this
/// label over the recent-activity tail.
[<Literal>]
let PrimarySubtaskKey = "primary"

/// Minimal activity sink — used by in-host subsystems (e.g. the check pipeline)
/// that report activity without being registered plugins.
[<NoComparison; NoEquality>]
type IActivitySink =
    abstract StartSubtask: key: string * label: string -> unit
    abstract UpdateSubtask: key: string * label: string -> unit
    abstract EndSubtask: key: string -> unit
    abstract Log: message: string -> unit
    abstract SetSummary: summary: string -> unit

let private maxTailPerPlugin = 64
let private maxHistoryPerPlugin = 16
let private maxTotalBytes = 2 * 1024 * 1024

let private stringBytes (s: string) = if isNull s then 0 else s.Length * 2

let private runRecordBytes (r: RunRecord) =
    let summaryBytes =
        match r.Summary with
        | Some s -> stringBytes s
        | None -> 0

    let errorBytes =
        match r.Outcome with
        | FailedRun e -> stringBytes e
        | TimedOut r -> stringBytes r
        | VerifiedNothing d -> stringBytes d
        | CompletedRun -> 0

    summaryBytes + errorBytes + (r.ActivityTail |> List.sumBy stringBytes)

let private subtaskBytes (t: Subtask) = stringBytes t.Key + stringBytes t.Label

let private capMarker (count: int) =
    let noun = if count = 1 then "line" else "lines"
    sprintf "… %d earlier %s dropped (activity cap)" count noun

let private capTrimBytes (trim: CapTrim) =
    match trim with
    | CapTrim.Intact -> 0
    | CapTrim.Dropped count -> stringBytes (capMarker count)

/// State held while a plugin run is in progress. Only the `Recording` phase
/// has these fields — once `RecordTerminal` commits the run to history, the
/// phase flips back to `Idle` and this state is released.
[<NoComparison; NoEquality>]
type private RecordingState =
    {
        Subtasks: Dictionary<string, Subtask>
        ActivityLog: Queue<string>
        /// Lines the global cap dropped from the front of `ActivityLog`, rendered
        /// as one marker line ahead of the tail. Lines the 64-line ring drops are
        /// not counted: the tail is a tail by design.
        mutable CapTrim: CapTrim
        mutable SummaryOverride: string option
        mutable SummaryBytes: int
        /// Override applied to the next RecordTerminal call. Plugins set this
        /// (via ctx.CompleteWithTimeout) to flip the terminal outcome to
        /// TimedOut without needing a new PluginStatus variant.
        mutable OutcomeOverride: RunOutcome option
    }

    static member Create() =
        { Subtasks = Dictionary<string, Subtask>()
          ActivityLog = Queue<string>()
          CapTrim = CapTrim.Intact
          SummaryOverride = None
          SummaryBytes = 0
          OutcomeOverride = None }

/// The activity tail as readers see it: the cap marker, if any, then the kept lines.
let private renderedTail (r: RecordingState) : string list =
    let lines = List.ofSeq r.ActivityLog

    match r.CapTrim with
    | CapTrim.Intact -> lines
    | CapTrim.Dropped count -> capMarker count :: lines

/// Every byte a recording holds, as `addBytes` counted it.
let private recordingBytes (r: RecordingState) =
    (r.Subtasks.Values |> Seq.sumBy subtaskBytes)
    + (r.ActivityLog |> Seq.sumBy stringBytes)
    + capTrimBytes r.CapTrim
    + r.SummaryBytes

/// Phase of a plugin's activity tracking. `Idle` carries no in-progress data,
/// so calling `RecordTerminal` on an `Idle` plugin cannot accidentally splice
/// stale subtasks/logs into a new history entry.
[<NoComparison; NoEquality>]
type private ActivityPhase =
    | Idle
    | Recording of RecordingState

[<NoComparison; NoEquality>]
type private PerPlugin =
    { Gate: System.Threading.Lock
      mutable Phase: ActivityPhase
      History: Queue<RunRecord>
      mutable Bytes: int }

/// What the global cap frees next: the oldest history entry across all plugins,
/// else the oldest log line of the live recording holding the most log bytes.
[<NoComparison; NoEquality>]
type private CapVictim =
    | OldestHistory of PerPlugin * startedAt: DateTime
    | OldestLiveLine of PerPlugin * logBytes: int

/// Snapshot of a plugin's current activity state — taken under one lock.
type Snapshot =
    { Subtasks: Subtask list
      ActivityTail: string list
      LastRun: RunRecord option }

type State() =
    let pluginsGate = System.Threading.Lock()
    let plugins = Dictionary<string, PerPlugin>()
    let mutable totalBytes = 0

    let getOrCreate name =
        Locking.locked pluginsGate (fun () ->
            match plugins.TryGetValue name with
            | true, p -> p
            | _ ->
                let p =
                    { Gate = System.Threading.Lock()
                      Phase = Idle
                      History = Queue<RunRecord>()
                      Bytes = 0 }

                plugins.[name] <- p
                p)

    let addBytes (p: PerPlugin) (n: int) =
        p.Bytes <- p.Bytes + n
        System.Threading.Interlocked.Add(&totalBytes, n) |> ignore

    // Must be called while holding p.Gate.
    let victimOf (p: PerPlugin) : CapVictim option =
        if p.History.Count > 0 then
            Some(OldestHistory(p, p.History.Peek().StartedAt))
        else
            match p.Phase with
            | Recording r when r.ActivityLog.Count > 0 ->
                Some(OldestLiveLine(p, r.ActivityLog |> Seq.sumBy stringBytes))
            | Recording _
            | Idle -> None

    let preferred (a: CapVictim) (b: CapVictim) : CapVictim =
        match a, b with
        | OldestHistory(_, ta), OldestHistory(_, tb) -> if tb < ta then b else a
        | OldestHistory _, OldestLiveLine _ -> a
        | OldestLiveLine _, OldestHistory _ -> b
        | OldestLiveLine(_, ba), OldestLiveLine(_, bb) -> if bb > ba then b else a

    let findVictim () : CapVictim option =
        Locking.locked pluginsGate (fun () ->
            plugins.Values
            |> Seq.choose (fun p -> Locking.locked p.Gate (fun () -> victimOf p))
            |> Seq.fold
                (fun best v ->
                    match best with
                    | None -> Some v
                    | Some b -> Some(preferred b v))
                None)

    // Frees one victim. It may have changed since findVictim released its gate;
    // then this frees nothing and the caller looks again.
    let free (victim: CapVictim) =
        match victim with
        | OldestHistory(p, _) ->
            Locking.locked p.Gate (fun () ->
                if p.History.Count > 0 then
                    let evicted = p.History.Dequeue()
                    addBytes p (-(runRecordBytes evicted)))
        | OldestLiveLine(p, _) ->
            Locking.locked p.Gate (fun () ->
                match p.Phase with
                | Recording r when r.ActivityLog.Count > 0 ->
                    let dropped = r.ActivityLog.Dequeue()

                    let trimmed =
                        match r.CapTrim with
                        | CapTrim.Intact -> CapTrim.Dropped 1
                        | CapTrim.Dropped count -> CapTrim.Dropped(count + 1)

                    addBytes p (capTrimBytes trimmed - capTrimBytes r.CapTrim - stringBytes dropped)
                    r.CapTrim <- trimmed
                | Recording _
                | Idle -> ())

    // Called after additions. Frees history oldest-first across all plugins, then
    // the oldest lines of live recordings, until under budget. The loop ends when
    // nothing is left to free (AUTOMATION-1703): subtasks, summaries and cap markers
    // stay, so the total can rest above the cap by what they hold.
    let rec enforceGlobalCap () =
        if totalBytes > maxTotalBytes then
            match findVictim () with
            | None -> ()
            | Some victim ->
                free victim
                enforceGlobalCap ()

    // Must be called while holding p.Gate. Log/SetSummary after RecordTerminal
    // lazily start a fresh Recording so late-arriving output stays observable;
    // that Recording won't be committed without a subsequent RecordTerminal or
    // ResetRun — accepted tradeoff.
    let ensureRecording (p: PerPlugin) : RecordingState =
        match p.Phase with
        | Recording r -> r
        | Idle ->
            let r = RecordingState.Create()
            p.Phase <- Recording r
            r

    member _.StartSubtask(plugin: string, key: string, label: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            let r = ensureRecording p

            if not (r.Subtasks.ContainsKey key) then
                let t =
                    { Key = key
                      Label = label
                      StartedAt = DateTime.UtcNow }

                r.Subtasks.[key] <- t
                addBytes p (subtaskBytes t))

        if totalBytes > maxTotalBytes then
            enforceGlobalCap ()

    member _.UpdateSubtask(plugin: string, key: string, label: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            match p.Phase with
            | Recording r ->
                match r.Subtasks.TryGetValue key with
                | true, t ->
                    addBytes p (-(subtaskBytes t))
                    let updated = { t with Label = label }
                    r.Subtasks.[key] <- updated
                    addBytes p (subtaskBytes updated)
                | _ -> ()
            | Idle -> ())

        if totalBytes > maxTotalBytes then
            enforceGlobalCap ()

    member _.EndSubtask(plugin: string, key: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            match p.Phase with
            | Recording r ->
                match r.Subtasks.TryGetValue key with
                | true, t ->
                    r.Subtasks.Remove key |> ignore
                    addBytes p (-(subtaskBytes t))
                | _ -> ()
            | Idle -> ())

    member _.Log(plugin: string, message: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            let r = ensureRecording p
            r.ActivityLog.Enqueue(message)
            addBytes p (stringBytes message)

            if r.ActivityLog.Count > maxTailPerPlugin then
                let dropped = r.ActivityLog.Dequeue()
                addBytes p (-(stringBytes dropped)))

        if totalBytes > maxTotalBytes then
            enforceGlobalCap ()

    member _.SetSummary(plugin: string, summary: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            let r = ensureRecording p
            addBytes p (-r.SummaryBytes)
            r.SummaryOverride <- Some summary
            r.SummaryBytes <- stringBytes summary
            addBytes p r.SummaryBytes)

        if totalBytes > maxTotalBytes then
            enforceGlobalCap ()

    member _.SetNextTerminalOutcome(plugin: string, outcome: RunOutcome) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            let r = ensureRecording p
            r.OutcomeOverride <- Some outcome)

    member _.RecordTerminal(plugin: string, outcome: RunOutcome, startedAt: DateTime, at: DateTime) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            let tail, derivedSummary, trim, discarded, outcomeOverride =
                match p.Phase with
                | Recording r ->
                    // No fallback; plugins must call SetSummary explicitly
                    // (via ctx.CompleteWithSummary) or the summary is empty.
                    renderedTail r, r.SummaryOverride, r.CapTrim, recordingBytes r, r.OutcomeOverride
                | Idle -> [], None, CapTrim.Intact, 0, None

            let effectiveOutcome = outcomeOverride |> Option.defaultValue outcome

            let record =
                { StartedAt = startedAt
                  Elapsed = at - startedAt
                  Outcome = effectiveOutcome
                  Summary = derivedSummary
                  ActivityTail = tail
                  CapTrim = trim }

            // Bytes held by the prior Recording state are all discarded.
            addBytes p (-discarded)
            p.Phase <- Idle

            p.History.Enqueue(record)
            addBytes p (runRecordBytes record)

            while p.History.Count > maxHistoryPerPlugin do
                let evicted = p.History.Dequeue()
                addBytes p (-(runRecordBytes evicted)))

        if totalBytes > maxTotalBytes then
            enforceGlobalCap ()

    member _.ResetRun(plugin: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            match p.Phase with
            | Recording r ->
                addBytes p (-(recordingBytes r))
                p.Phase <- Idle
            | Idle -> ())

    member _.GetSnapshot(plugin: string) : Snapshot =
        let exists, p = Locking.locked pluginsGate (fun () -> plugins.TryGetValue plugin)

        if not exists then
            { Subtasks = []
              ActivityTail = []
              LastRun = None }
        else
            Locking.locked p.Gate (fun () ->
                let subtasks, tail =
                    match p.Phase with
                    | Recording r -> r.Subtasks.Values |> Seq.toList, renderedTail r
                    | Idle -> [], []

                let lastRun =
                    if p.History.Count = 0 then
                        None
                    else
                        Some(p.History.ToArray().[p.History.Count - 1])

                { Subtasks = subtasks
                  ActivityTail = tail
                  LastRun = lastRun })

    member this.GetSubtasks(plugin: string) : Subtask list = this.GetSnapshot(plugin).Subtasks

    member this.GetActivityTail(plugin: string) : string list = this.GetSnapshot(plugin).ActivityTail

    member _.GetHistory(plugin: string) : RunRecord list =
        let exists, p = Locking.locked pluginsGate (fun () -> plugins.TryGetValue plugin)

        if not exists then
            []
        else
            Locking.locked p.Gate (fun () -> p.History |> List.ofSeq)

    member _.TotalByteSize: int = totalBytes
