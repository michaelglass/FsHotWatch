/// Shared string helpers used across plugins.
module FsHotWatch.StringHelpers

/// Truncate a string to the last `maxLines` lines.
/// Returns the original string if it has fewer lines than the limit.
let truncateOutput (maxLines: int) (output: string) =
    let lines = output.Split('\n')

    if lines.Length <= maxLines then
        output
    else
        lines |> Array.skip (lines.Length - maxLines) |> String.concat "\n"
