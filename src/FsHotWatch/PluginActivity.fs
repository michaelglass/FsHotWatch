/// Per-plugin concurrent subtasks, activity log ring buffer, and bounded run history.
module FsHotWatch.PluginActivity

open System
open System.Collections.Generic
open FsHotWatch.Events

let private maxTailPerPlugin = 64
let private maxHistoryPerPlugin = 16
let private maxTotalBytes = 2 * 1024 * 1024

[<NoComparison; NoEquality>]
type private PerPlugin =
    { mutable Subtasks: Map<string, Subtask>
      ActivityLog: Queue<string>
      mutable SummaryOverride: string option
      History: Queue<RunRecord> }

/// Bounded, thread-safe per-plugin activity state.
type State() =
    let gate = obj ()
    let plugins = Dictionary<string, PerPlugin>()

    let getOrCreate name =
        match plugins.TryGetValue name with
        | true, p -> p
        | _ ->
            let p =
                { Subtasks = Map.empty
                  ActivityLog = Queue<string>()
                  SummaryOverride = None
                  History = Queue<RunRecord>() }

            plugins.[name] <- p
            p

    let stringBytes (s: string) = if isNull s then 0 else s.Length * 2

    let runRecordBytes (r: RunRecord) =
        let summaryBytes =
            match r.Summary with
            | Some s -> stringBytes s
            | None -> 0

        let errorBytes =
            match r.Outcome with
            | FailedRun e -> stringBytes e
            | CompletedRun -> 0

        summaryBytes + errorBytes + (r.ActivityTail |> List.sumBy stringBytes)

    let subtaskBytes (t: Subtask) = stringBytes t.Key + stringBytes t.Label

    let totalBytes () =
        let mutable total = 0

        for KeyValue(_, p) in plugins do
            for msg in p.ActivityLog do
                total <- total + stringBytes msg

            for r in p.History do
                total <- total + runRecordBytes r

            for KeyValue(_, t) in p.Subtasks do
                total <- total + subtaskBytes t

        total

    let evictOldestHistory () =
        // Find the plugin whose history head has the earliest StartedAt; pop it.
        let mutable candidate: (string * DateTime) option = None

        for KeyValue(name, p) in plugins do
            if p.History.Count > 0 then
                let h = p.History.Peek()

                match candidate with
                | None -> candidate <- Some(name, h.StartedAt)
                | Some(_, t) when h.StartedAt < t -> candidate <- Some(name, h.StartedAt)
                | _ -> ()

        match candidate with
        | Some(name, _) -> plugins.[name].History.Dequeue() |> ignore
        | None -> ()

    let enforceGlobalCap () =
        while totalBytes () > maxTotalBytes
              && (plugins.Values |> Seq.exists (fun p -> p.History.Count > 0)) do
            evictOldestHistory ()

    member _.StartSubtask(plugin: string, key: string, label: string) : unit =
        lock gate (fun () ->
            let p = getOrCreate plugin

            if not (Map.containsKey key p.Subtasks) then
                let t =
                    { Key = key
                      Label = label
                      StartedAt = DateTime.UtcNow }

                p.Subtasks <- Map.add key t p.Subtasks)

    member _.EndSubtask(plugin: string, key: string) : unit =
        lock gate (fun () ->
            let p = getOrCreate plugin
            p.Subtasks <- Map.remove key p.Subtasks)

    member _.Log(plugin: string, message: string) : unit =
        lock gate (fun () ->
            let p = getOrCreate plugin
            p.ActivityLog.Enqueue(message)

            while p.ActivityLog.Count > maxTailPerPlugin do
                p.ActivityLog.Dequeue() |> ignore

            enforceGlobalCap ())

    member _.SetSummary(plugin: string, summary: string) : unit =
        lock gate (fun () ->
            let p = getOrCreate plugin
            p.SummaryOverride <- Some summary)

    member _.RecordTerminal(plugin: string, outcome: RunOutcome, startedAt: DateTime, at: DateTime) : unit =
        lock gate (fun () ->
            let p = getOrCreate plugin
            let tail = p.ActivityLog |> List.ofSeq

            let derivedSummary =
                match p.SummaryOverride with
                | Some s -> Some s
                | None ->
                    match tail with
                    | _ :: _ -> Some(List.last tail)
                    | [] ->
                        if Map.isEmpty p.Subtasks then
                            None
                        else
                            let longest =
                                p.Subtasks |> Map.toSeq |> Seq.map snd |> Seq.minBy (fun t -> t.StartedAt)

                            Some longest.Label

            let record =
                { StartedAt = startedAt
                  Elapsed = at - startedAt
                  Outcome = outcome
                  Summary = derivedSummary
                  ActivityTail = tail }

            p.History.Enqueue(record)

            while p.History.Count > maxHistoryPerPlugin do
                p.History.Dequeue() |> ignore

            // Reset run state
            p.Subtasks <- Map.empty
            p.ActivityLog.Clear()
            p.SummaryOverride <- None
            enforceGlobalCap ())

    member _.ResetRun(plugin: string) : unit =
        lock gate (fun () ->
            let p = getOrCreate plugin
            p.Subtasks <- Map.empty
            p.ActivityLog.Clear()
            p.SummaryOverride <- None)

    member _.GetSubtasks(plugin: string) : Subtask list =
        lock gate (fun () ->
            match plugins.TryGetValue plugin with
            | true, p -> p.Subtasks |> Map.toList |> List.map snd
            | _ -> [])

    member _.GetActivityTail(plugin: string) : string list =
        lock gate (fun () ->
            match plugins.TryGetValue plugin with
            | true, p -> p.ActivityLog |> List.ofSeq
            | _ -> [])

    member _.GetHistory(plugin: string) : RunRecord list =
        lock gate (fun () ->
            match plugins.TryGetValue plugin with
            | true, p -> p.History |> List.ofSeq
            | _ -> [])

    member _.TotalByteSize: int = lock gate (fun () -> totalBytes ())
