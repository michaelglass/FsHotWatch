/// Shared string helpers used across plugins.
module FsHotWatch.StringHelpers

/// Split a command string into (command, args) at the first space.
let splitCommand (commandLine: string) =
    let parts = commandLine.Split(' ', 2)
    (parts.[0], if parts.Length > 1 then parts.[1] else "")

/// Sanitize a string for use as a filename by replacing path separators and angle brackets.
let sanitizeFileName (s: string) =
    s.Replace('/', '-').Replace('\\', '-').Replace('<', '_').Replace('>', '_')

/// Truncate a string to the last `maxLines` lines.
/// Returns the original string if it has fewer lines than the limit.
let truncateOutput (maxLines: int) (output: string) =
    let lines = output.Split('\n')

    if lines.Length <= maxLines then
        output
    else
        lines |> Array.skip (lines.Length - maxLines) |> String.concat "\n"
