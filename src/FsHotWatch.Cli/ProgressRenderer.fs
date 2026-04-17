module FsHotWatch.Cli.ProgressRenderer

open FsHotWatch.Cli.IpcOutput

/// Rendering mode for the progress block. Verbose is the default;
/// Compact collapses each plugin to a single line.
type RenderMode =
    | Compact
    | Verbose

/// Render a single plugin's status block. Returns zero or more lines.
let renderPlugin (_mode: RenderMode) (_now: System.DateTime) (_name: string) (_parsed: ParsedPluginStatus) : string list =
    failwith "nyi"

/// Render all plugin statuses in the given mode.
/// Callers join the result with newlines and use the line count for cursor-up erase.
let renderAll (_mode: RenderMode) (_now: System.DateTime) (_statuses: Map<string, ParsedPluginStatus>) : string list =
    failwith "nyi"
