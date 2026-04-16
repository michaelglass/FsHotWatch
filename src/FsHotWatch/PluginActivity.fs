/// Per-plugin concurrent subtasks, activity log ring buffer, and bounded run history.
module FsHotWatch.PluginActivity

open FsHotWatch.Events

/// Bounded, thread-safe per-plugin activity state. All methods no-op gracefully on unknown plugin names.
type State() =
    member _.StartSubtask(_plugin: string, _key: string, _label: string) : unit = failwith "nyi"
    member _.EndSubtask(_plugin: string, _key: string) : unit = failwith "nyi"
    member _.Log(_plugin: string, _message: string) : unit = failwith "nyi"
    member _.SetSummary(_plugin: string, _summary: string) : unit = failwith "nyi"

    member _.RecordTerminal
        (_plugin: string, _outcome: RunOutcome, _startedAt: System.DateTime, _at: System.DateTime)
        : unit =
        failwith "nyi"

    member _.ResetRun(_plugin: string) : unit = failwith "nyi"
    member _.GetSubtasks(_plugin: string) : Subtask list = failwith "nyi"
    member _.GetActivityTail(_plugin: string) : string list = failwith "nyi"
    member _.GetHistory(_plugin: string) : RunRecord list = failwith "nyi"
    member _.TotalByteSize: int = failwith "nyi"
