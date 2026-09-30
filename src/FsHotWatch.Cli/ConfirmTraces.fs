/// After `confirm`: whether the run its verdict graded recorded the traces `tests.traces`
/// asks for. Tracing never decides a verdict, so nothing here changes an exit code; it
/// makes a configured recording that did not happen impossible to miss.
module FsHotWatch.Cli.ConfirmTraces

open System
open System.IO
open FsHotWatch.TestPrune
open TestPrune.Trace

/// The warning for run `runId`, or `None` when nothing is owed. `rows` holds, for each
/// project that takes part in recording, its stored trace runs (of any run). Owed: the
/// policy records, some project takes part, and no project's row for this run is a
/// recording. A stored refusal is named with its reason; a run that stored nothing at
/// all is named as that.
let unrecordedWarning
    (record: TraceRecordPolicy)
    (runId: string)
    (rows: (string * TraceStore.TraceRun list) list)
    : string option =
    let ofRun =
        rows
        |> List.map (fun (project, runs) -> project, runs |> List.filter (fun r -> r.RunId = runId))

    let traced =
        ofRun
        |> List.exists (fun (_, runs) ->
            runs
            |> List.exists (fun r ->
                match r.Status with
                | TraceStore.Recorded
                | TraceStore.TreeMovedDuringRun -> true
                | TraceStore.Refused
                | TraceStore.FailedToRecord -> false))

    match record with
    | RecordOff -> None
    | RecordFullRuns
    | RecordEveryRun when traced || List.isEmpty rows -> None
    | RecordFullRuns
    | RecordEveryRun ->
        let reasons =
            ofRun
            |> List.choose (fun (project, runs) ->
                runs |> List.tryHead |> Option.map (fun r -> $"%s{project}: %s{r.Reason}"))

        let what =
            if List.isEmpty reasons then
                "stored no trace and no reason for any project"
            else
                "traced no project — " + String.concat "; " reasons

        Some $"tests.traces is configured, but run %s{runId}, the run this verdict graded, %s{what}"

/// `unrecordedWarning` over the trace database of `settings`, for the projects in
/// `participating`. A database that does not exist holds no rows; one that cannot be read
/// is named in the warning. Never throws.
let check
    (repoRoot: string)
    (settings: TraceSettings option)
    (participating: string list)
    (runId: Guid option)
    : string option =
    match settings, runId with
    | Some settings, Some runId ->
        let dbPath = Path.Combine(repoRoot, settings.DbPath)

        let rows =
            if not (File.Exists dbPath) then
                Ok(participating |> List.map (fun project -> project, []))
            else
                try
                    let store = TraceStore.Store.Open dbPath

                    try
                        Ok(participating |> List.map (fun project -> project, store.Runs project))
                    finally
                        (store :> IDisposable).Dispose()
                with ex ->
                    Error ex.Message

        match rows with
        | Ok rows -> unrecordedWarning settings.Record (runId.ToString "N") rows
        | Error reason ->
            Some $"tests.traces is configured, but the trace database %s{settings.DbPath} could not be read: %s{reason}"
    | _ -> None
