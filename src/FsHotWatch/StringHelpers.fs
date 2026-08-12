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

/// Sample size for `describeMany`. Deliberately equal to `%A`'s own
/// 100-element cap, so swapping a `%A` for `describeMany` can never print LESS
/// than it did before — the exact count is pure gain.
[<Literal>]
let DefaultLogSample = 100

/// Render a collection for a diagnostic log without lying about its size.
///
/// `%A` silently truncates a sequence at 100 elements (FOUR for a bare `seq`) and
/// hard-wraps at 80 columns, so it renders a 1,500-element list and a 101-element one
/// identically, and the wrapping smears one log record across many lines, breaking the
/// `[tag] ts msg` framing.
///
/// This leads with the EXACT count and follows with a bounded, single-line sample:
///     `0 []`
///     `3 [a; b; c]`
///     `1500 [a; b; …; +1400 more]`
///
/// Empty renders as `0 []` rather than being omitted — an empty collection is often
/// meaningful (TestPrune reads "project present, no affected classes" as "run this
/// project in full", which must stay distinguishable from "project absent").
let describeManyWith (maxSample: int) (items: string seq) : string =
    let all = items |> Seq.toArray
    let separator = "; "

    if all.Length <= maxSample then
        let body = String.concat separator all
        $"%d{all.Length} [%s{body}]"
    else
        let sample = all |> Array.take maxSample |> String.concat separator
        $"%d{all.Length} [%s{sample}; … +%d{all.Length - maxSample} more]"

/// `describeManyWith` at the default sample size.
let describeMany (items: string seq) : string = describeManyWith DefaultLogSample items

/// Like `describeMany`, but never truncates — for diagnostics whose point is the FULL
/// membership, e.g. an impact query's seed set, where the entry being hunted is
/// exactly the one a sample might exclude.
let describeAll (items: string seq) : string =
    describeManyWith System.Int32.MaxValue items
