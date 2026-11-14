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
let TestPrunePluginName = RequiredConfig.TestPrunePluginName

/// The analyzers plugin's registered name, and therefore its LEDGER KEY. Named here for
/// the same reason as `TestPrunePluginName`: the verdict asks of an analyzer entry
/// whether it was computed from a check that is itself a checker fault, and the only
/// way to ask is by ledger source.
[<Literal>]
let AnalyzersPluginName = RequiredConfig.AnalyzersPluginName

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

/// One live recording's share of `maxTotalBytes` for its activity log. Past it, the
/// recording drops its oldest lines. Live recordings are bounded here, on their own,
/// and never by the history budget: history is the only thing the global cap evicts.
let private maxLogBytesPerRecording = maxTotalBytes / 8

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

/// State held while a plugin run is in progress. Only the `Recording` phase
/// has these fields — once `RecordTerminal` commits the run to history, the
/// phase flips back to `Idle` and this state is released.
[<NoComparison; NoEquality>]
type private RecordingState =
    {
        Subtasks: Dictionary<string, Subtask>
        ActivityLog: Queue<string>
        /// The bytes of the lines in `ActivityLog`, kept equal to their sum.
        mutable LogBytes: int
        mutable Trim: ActivityTrim
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
          LogBytes = 0
          Trim = Untrimmed
          SummaryOverride = None
          SummaryBytes = 0
          OutcomeOverride = None }

/// Every byte a recording holds, as the live counter counted it.
let private recordingBytes (r: RecordingState) =
    (r.Subtasks.Values |> Seq.sumBy subtaskBytes) + r.LogBytes + r.SummaryBytes

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
      mutable Phase: ActivityPhase }

/// A committed run, with the bytes the history counter holds for it.
[<NoComparison; NoEquality>]
type private HistoryEntry =
    { Plugin: string
      Record: RunRecord
      Bytes: int }

/// Snapshot of a plugin's current activity state — taken under one lock.
type Snapshot =
    { Subtasks: Subtask list
      ActivityTail: string list
      LastRun: RunRecord option }

/// Lock order: a plugin's `Gate`, then `historyGate`; `pluginsGate` is never held
/// with either.
type State() =
    let pluginsGate = System.Threading.Lock()
    let plugins = Dictionary<string, PerPlugin>()

    /// Bytes held by live recordings. Each plugin changes it under its own gate.
    let mutable liveBytes = 0

    let historyGate = System.Threading.Lock()

    /// Every plugin's committed runs, oldest-committed first, under `historyGate`.
    let history = LinkedList<HistoryEntry>()

    /// The bytes of the entries in `history`, kept equal to their sum, under `historyGate`.
    let mutable historyBytes = 0

    let getOrCreate name =
        Locking.locked pluginsGate (fun () ->
            match plugins.TryGetValue name with
            | true, p -> p
            | _ ->
                let p =
                    { Gate = System.Threading.Lock()
                      Phase = Idle }

                plugins.[name] <- p
                p)

    let addLive (n: int) =
        System.Threading.Interlocked.Add(&liveBytes, n) |> ignore

    // Must be called while holding historyGate.
    let removeHistory (node: LinkedListNode<HistoryEntry>) =
        history.Remove node
        historyBytes <- historyBytes - node.Value.Bytes

    // Must be called while holding historyGate. History may use what live recordings
    // leave of the cap, and never less than nothing. Past that, the oldest-committed
    // run is evicted. `historyBytes` is the sum of `history`'s entries, so while it
    // exceeds a budget of at least 0 there is an entry to evict.
    let evictOverBudget () =
        let budget () =
            max 0 (maxTotalBytes - System.Threading.Volatile.Read(&liveBytes))

        while historyBytes > budget () do
            removeHistory history.First

    let enforceHistoryBudget () =
        Locking.locked historyGate evictOverBudget

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

    // Must be called while holding the recording's plugin gate.
    let dropOldestLine (r: RecordingState) =
        let dropped = stringBytes (r.ActivityLog.Dequeue())
        r.LogBytes <- r.LogBytes - dropped
        addLive (-dropped)

    // Must be called while holding the recording's plugin gate. `LogBytes` is the sum
    // of `ActivityLog`'s lines, so while it exceeds the share there is a line to drop.
    let trimToShare (r: RecordingState) =
        while r.LogBytes > maxLogBytesPerRecording do
            dropOldestLine r

            r.Trim <-
                match r.Trim with
                | Untrimmed -> Trimmed 1
                | Trimmed n -> Trimmed(n + 1)

    // Must be called while holding historyGate.
    let historyOf (plugin: string) : RunRecord list =
        [ for e in history do
              if e.Plugin = plugin then
                  yield e.Record ]

    // Must be called while holding historyGate, when the plugin holds more runs than
    // the per-plugin limit, so it has an entry to find.
    let rec oldestOf (plugin: string) (node: LinkedListNode<HistoryEntry>) =
        if node.Value.Plugin = plugin then
            node
        else
            oldestOf plugin node.Next

    let overCap () =
        System.Threading.Volatile.Read(&liveBytes)
        + System.Threading.Volatile.Read(&historyBytes) > maxTotalBytes

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
                addLive (subtaskBytes t))

        if overCap () then
            enforceHistoryBudget ()

    member _.UpdateSubtask(plugin: string, key: string, label: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            match p.Phase with
            | Recording r ->
                match r.Subtasks.TryGetValue key with
                | true, t ->
                    let updated = { t with Label = label }
                    r.Subtasks.[key] <- updated
                    addLive (subtaskBytes updated - subtaskBytes t)
                | _ -> ()
            | Idle -> ())

        if overCap () then
            enforceHistoryBudget ()

    member _.EndSubtask(plugin: string, key: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            match p.Phase with
            | Recording r ->
                match r.Subtasks.TryGetValue key with
                | true, t ->
                    r.Subtasks.Remove key |> ignore
                    addLive (-(subtaskBytes t))
                | _ -> ()
            | Idle -> ())

    member _.Log(plugin: string, message: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            let r = ensureRecording p
            r.ActivityLog.Enqueue(message)
            let added = stringBytes message
            r.LogBytes <- r.LogBytes + added
            addLive added

            if r.ActivityLog.Count > maxTailPerPlugin then
                dropOldestLine r

            trimToShare r)

        if overCap () then
            enforceHistoryBudget ()

    member _.SetSummary(plugin: string, summary: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            let r = ensureRecording p
            let previous = r.SummaryBytes
            r.SummaryOverride <- Some summary
            r.SummaryBytes <- stringBytes summary
            addLive (r.SummaryBytes - previous))

        if overCap () then
            enforceHistoryBudget ()

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
                    List.ofSeq r.ActivityLog, r.SummaryOverride, r.Trim, recordingBytes r, r.OutcomeOverride
                | Idle -> [], None, Untrimmed, 0, None

            let record =
                { StartedAt = startedAt
                  Elapsed = at - startedAt
                  Outcome = outcomeOverride |> Option.defaultValue outcome
                  Summary = derivedSummary
                  ActivityTail = tail
                  ActivityTrim = trim }

            // Bytes held by the prior Recording state are all discarded.
            addLive (-discarded)
            p.Phase <- Idle

            // Under the plugin's gate too, so a snapshot never sees the run in neither
            // the recording nor the history.
            Locking.locked historyGate (fun () ->
                let bytes = runRecordBytes record

                history.AddLast(
                    { Plugin = plugin
                      Record = record
                      Bytes = bytes }
                )
                |> ignore

                historyBytes <- historyBytes + bytes

                // One entry was added, so at most one is over the per-plugin limit.
                if history |> Seq.filter (fun e -> e.Plugin = plugin) |> Seq.length > maxHistoryPerPlugin then
                    removeHistory (oldestOf plugin history.First)

                evictOverBudget ()))

    member _.ResetRun(plugin: string) : unit =
        let p = getOrCreate plugin

        Locking.locked p.Gate (fun () ->
            match p.Phase with
            | Recording r ->
                addLive (-(recordingBytes r))
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
                    | Recording r -> r.Subtasks.Values |> Seq.toList, r.ActivityLog |> List.ofSeq
                    | Idle -> [], []

                let lastRun =
                    Locking.locked historyGate (fun () -> historyOf plugin |> List.tryLast)

                { Subtasks = subtasks
                  ActivityTail = tail
                  LastRun = lastRun })

    member this.GetSubtasks(plugin: string) : Subtask list = this.GetSnapshot(plugin).Subtasks

    member this.GetActivityTail(plugin: string) : string list = this.GetSnapshot(plugin).ActivityTail

    member _.GetHistory(plugin: string) : RunRecord list =
        Locking.locked historyGate (fun () -> historyOf plugin)

    member _.TotalByteSize: int =
        System.Threading.Volatile.Read(&liveBytes)
        + System.Threading.Volatile.Read(&historyBytes)
