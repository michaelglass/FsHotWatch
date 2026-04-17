module FsHotWatch.PluginActivity

open System
open System.Collections.Generic
open FsHotWatch.Events

/// Synthetic plugin name used to surface check-pipeline activity in IPC status output.
[<Literal>]
let FcsPluginName = "fcs"

/// Minimal activity sink — used by in-host subsystems (e.g. the check pipeline)
/// that report activity without being registered plugins.
[<NoComparison; NoEquality>]
type IActivitySink =
    abstract StartSubtask: key: string * label: string -> unit
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
        | CompletedRun -> 0

    summaryBytes + errorBytes + (r.ActivityTail |> List.sumBy stringBytes)

let private subtaskBytes (t: Subtask) = stringBytes t.Key + stringBytes t.Label

[<NoComparison; NoEquality>]
type private PerPlugin =
    { Gate: obj
      Subtasks: Dictionary<string, Subtask>
      ActivityLog: Queue<string>
      mutable SummaryOverride: string option
      mutable SummaryBytes: int
      History: Queue<RunRecord>
      mutable Bytes: int }

/// Snapshot of a plugin's current activity state — taken under one lock.
type Snapshot =
    { Subtasks: Subtask list
      ActivityTail: string list
      LastRun: RunRecord option }

type State() =
    let pluginsGate = obj ()
    let plugins = Dictionary<string, PerPlugin>()
    let mutable totalBytes = 0

    let getOrCreate name =
        lock pluginsGate (fun () ->
            match plugins.TryGetValue name with
            | true, p -> p
            | _ ->
                let p =
                    { Gate = obj ()
                      Subtasks = Dictionary<string, Subtask>()
                      ActivityLog = Queue<string>()
                      SummaryOverride = None
                      SummaryBytes = 0
                      History = Queue<RunRecord>()
                      Bytes = 0 }

                plugins.[name] <- p
                p)

    let enforceGlobalCap () =
        // Called after additions. Evict oldest history entries across all plugins
        // until under budget. Each plugin updates totalBytes + its own Bytes when it
        // evicts, so we only need to find the global oldest each iteration.
        while totalBytes > maxTotalBytes do
            let mutable candidate: (PerPlugin * DateTime) option = None

            lock pluginsGate (fun () ->
                for KeyValue(_, p) in plugins do
                    lock p.Gate (fun () ->
                        if p.History.Count > 0 then
                            let h = p.History.Peek()

                            match candidate with
                            | None -> candidate <- Some(p, h.StartedAt)
                            | Some(_, t) when h.StartedAt < t -> candidate <- Some(p, h.StartedAt)
                            | _ -> ()))

            match candidate with
            | Some(p, _) ->
                lock p.Gate (fun () ->
                    if p.History.Count > 0 then
                        let evicted = p.History.Dequeue()
                        let sz = runRecordBytes evicted
                        p.Bytes <- p.Bytes - sz
                        System.Threading.Interlocked.Add(&totalBytes, -sz) |> ignore)
            | None -> ()

    let addBytes (p: PerPlugin) (n: int) =
        p.Bytes <- p.Bytes + n
        System.Threading.Interlocked.Add(&totalBytes, n) |> ignore

    member _.StartSubtask(plugin: string, key: string, label: string) : unit =
        let p = getOrCreate plugin

        lock p.Gate (fun () ->
            if not (p.Subtasks.ContainsKey key) then
                let t =
                    { Key = key
                      Label = label
                      StartedAt = DateTime.UtcNow }

                p.Subtasks.[key] <- t
                addBytes p (subtaskBytes t))

        if totalBytes > maxTotalBytes then
            enforceGlobalCap ()

    member _.EndSubtask(plugin: string, key: string) : unit =
        let p = getOrCreate plugin

        lock p.Gate (fun () ->
            match p.Subtasks.TryGetValue key with
            | true, t ->
                p.Subtasks.Remove key |> ignore
                addBytes p (-(subtaskBytes t))
            | _ -> ())

    member _.Log(plugin: string, message: string) : unit =
        let p = getOrCreate plugin

        lock p.Gate (fun () ->
            p.ActivityLog.Enqueue(message)
            addBytes p (stringBytes message)

            if p.ActivityLog.Count > maxTailPerPlugin then
                let dropped = p.ActivityLog.Dequeue()
                addBytes p (-(stringBytes dropped)))

        if totalBytes > maxTotalBytes then
            enforceGlobalCap ()

    member _.SetSummary(plugin: string, summary: string) : unit =
        let p = getOrCreate plugin

        lock p.Gate (fun () ->
            addBytes p (-p.SummaryBytes)
            p.SummaryOverride <- Some summary
            p.SummaryBytes <- stringBytes summary
            addBytes p p.SummaryBytes)

        if totalBytes > maxTotalBytes then
            enforceGlobalCap ()

    member _.RecordTerminal(plugin: string, outcome: RunOutcome, startedAt: DateTime, at: DateTime) : unit =
        let p = getOrCreate plugin

        lock p.Gate (fun () ->
            let tail = p.ActivityLog |> List.ofSeq

            let derivedSummary =
                match p.SummaryOverride with
                | Some s -> Some s
                | None ->
                    match tail with
                    | _ :: _ -> Some(List.last tail)
                    | [] when p.Subtasks.Count > 0 ->
                        let longest = p.Subtasks.Values |> Seq.minBy (fun t -> t.StartedAt)
                        Some longest.Label
                    | [] -> None

            let record =
                { StartedAt = startedAt
                  Elapsed = at - startedAt
                  Outcome = outcome
                  Summary = derivedSummary
                  ActivityTail = tail }

            // Bytes held by subtasks + tail + summary override are all discarded.
            let discarded =
                (p.Subtasks.Values |> Seq.sumBy subtaskBytes)
                + (p.ActivityLog |> Seq.sumBy stringBytes)
                + p.SummaryBytes

            addBytes p (-discarded)
            p.Subtasks.Clear()
            p.ActivityLog.Clear()
            p.SummaryOverride <- None
            p.SummaryBytes <- 0

            p.History.Enqueue(record)
            addBytes p (runRecordBytes record)

            while p.History.Count > maxHistoryPerPlugin do
                let evicted = p.History.Dequeue()
                addBytes p (-(runRecordBytes evicted)))

        if totalBytes > maxTotalBytes then
            enforceGlobalCap ()

    member _.ResetRun(plugin: string) : unit =
        let p = getOrCreate plugin

        lock p.Gate (fun () ->
            let discarded =
                (p.Subtasks.Values |> Seq.sumBy subtaskBytes)
                + (p.ActivityLog |> Seq.sumBy stringBytes)
                + p.SummaryBytes

            addBytes p (-discarded)
            p.Subtasks.Clear()
            p.ActivityLog.Clear()
            p.SummaryOverride <- None
            p.SummaryBytes <- 0)

    member _.GetSnapshot(plugin: string) : Snapshot =
        let exists, p = lock pluginsGate (fun () -> plugins.TryGetValue plugin)

        if not exists then
            { Subtasks = []
              ActivityTail = []
              LastRun = None }
        else
            lock p.Gate (fun () ->
                let lastRun =
                    if p.History.Count = 0 then
                        None
                    else
                        Some(p.History.ToArray().[p.History.Count - 1])

                { Subtasks = p.Subtasks.Values |> Seq.toList
                  ActivityTail = p.ActivityLog |> List.ofSeq
                  LastRun = lastRun })

    member this.GetSubtasks(plugin: string) : Subtask list = this.GetSnapshot(plugin).Subtasks

    member this.GetActivityTail(plugin: string) : string list = this.GetSnapshot(plugin).ActivityTail

    member _.GetHistory(plugin: string) : RunRecord list =
        let exists, p = lock pluginsGate (fun () -> plugins.TryGetValue plugin)

        if not exists then
            []
        else
            lock p.Gate (fun () -> p.History |> List.ofSeq)

    member _.TotalByteSize: int = totalBytes
