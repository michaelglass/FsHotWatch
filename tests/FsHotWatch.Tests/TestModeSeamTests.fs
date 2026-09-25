/// `TestMode` is branched on in exactly one place: `src/FsHotWatch.TestPrune/TestMode.fs`.
///
/// `check` and `confirm` share one run lifecycle, and `confirm` subtracts a named set of
/// work from it (`PassThroughSkip`). A branch on the mode anywhere else is a second path:
/// the next feature added beside it lands on one side and is silently skipped in the
/// other mode. So the rest of `src/` asks the seam a question, and this test refuses any
/// line that names a mode case or compares or matches on a mode itself.
module FsHotWatch.Tests.TestModeSeamTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

/// The seam, repo-relative.
let private seam = "src/FsHotWatch.TestPrune/TestMode.fs"

/// A line of code with its comment removed. A `//` preceded by `:` is a URL, not a
/// comment. Text analysis, and it says so: string literals are not parsed, so a string
/// that spells a mode case is reported too, which is the safe direction.
let private codeOf (line: string) =
    let m = Regex.Match(line, @"(?<!:)//")
    if m.Success then line.Substring(0, m.Index) else line

/// Naming a mode case, or comparing or matching on a `.Mode`.
let private modeBranch =
    Regex(@"\b(PassThrough|ImpactSelection)\b|\.Mode\s*(=|<>)(?!=)|\bmatch\b.*\.Mode\s+with\b")

/// Every line outside the seam that branches on the mode, as `file:line: code`.
let private strayBranches (root: string) =
    Directory.EnumerateFiles(Path.Combine(root, "src"), "*.fs", SearchOption.AllDirectories)
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), path)
    |> Seq.filter (fun (relative, _) -> relative <> seam && not (relative.Contains "/obj/"))
    |> Seq.collect (fun (relative, path) ->
        File.ReadAllLines path
        |> Seq.mapi (fun i line -> i + 1, line)
        |> Seq.filter (fun (_, line) -> modeBranch.IsMatch(codeOf line))
        |> Seq.map (fun (n, line) -> $"%s{relative}:%d{n}: %s{line.Trim()}"))
    |> List.ofSeq

[<Fact>]
let ``the seam exists and names every mode case`` () =
    // PRESENT, not merely absent: a guard over a renamed or deleted seam would pass by
    // finding nothing to refuse.
    let source = File.ReadAllText(Path.Combine(repoRoot (), seam))
    test <@ source.Contains "| PassThrough" && source.Contains "| ImpactSelection" @>

[<Fact>]
let ``no code outside the seam branches on TestMode`` () =
    let stray = strayBranches (repoRoot ())

    if not (List.isEmpty stray) then
        Assert.Fail(
            "TestMode is branched on outside "
            + seam
            + ". Ask the seam (`TestMode.skips`, `TestMode.requestsFullSuite`) instead, or add \
               the work to `PassThroughSkip` there:\n"
            + String.Join("\n", stray)
        )

[<Theory>]
[<InlineData("        if state.Mode = PassThrough then")>]
[<InlineData("        match inputs.Mode with")>]
[<InlineData("        | ImpactSelection -> ()")>]
[<InlineData("        && flushedState.Mode <> mode")>]
let ``the scan recognises a branch on the mode`` (line: string) =
    test <@ modeBranch.IsMatch(codeOf line) @>

[<Theory>]
[<InlineData("          Mode = state.Mode")>]
[<InlineData("        if TestMode.skips RerunIntents state.Mode then")>]
[<InlineData("        // under PassThrough every arrival attaches")>]
[<InlineData("        let url = \"https://example.invalid\" // PassThrough")>]
let ``the scan leaves questions to the seam alone`` (line: string) =
    test <@ not (modeBranch.IsMatch(codeOf line)) @>

// --- recordsTraces: the one place that decides when a run records traces ---

[<Theory>]
[<InlineData("off", false, false)>]
[<InlineData("full-runs", false, true)>]
[<InlineData("every-run", true, true)>]
let ``recordsTraces follows the policy per mode`` (policy: string, underCheck: bool, underConfirm: bool) =
    let p = (FsHotWatch.TestPrune.TraceSettings.parseRecord policy).Value
    test <@ FsHotWatch.TestPrune.TestMode.recordsTraces p FsHotWatch.TestPrune.ImpactSelection = underCheck @>
    test <@ FsHotWatch.TestPrune.TestMode.recordsTraces p FsHotWatch.TestPrune.PassThrough = underConfirm @>

[<Theory>]
[<InlineData("off", "full", false)>]
[<InlineData("off", "", false)>]
[<InlineData("full-runs", "full", true)>]
[<InlineData("full-runs", "", false)>]
[<InlineData("full-runs", "affected", false)>]
[<InlineData("every-run", "full", true)>]
[<InlineData("every-run", "", true)>]
let ``recordsTraces follows the policy per trigger scope`` (policy: string, scope: string, records: bool) =
    // `confirm` sets scope "full"; `check` (and any other scope) runs impact selection.
    let p = (FsHotWatch.TestPrune.TraceSettings.parseRecord policy).Value
    let mode = FsHotWatch.TestPrune.TestMode.ofScope scope
    test <@ FsHotWatch.TestPrune.TestMode.recordsTraces p mode = records @>

[<Fact>]
let ``a daemon session starts recording only under every-run`` () =
    let at policy =
        FsHotWatch.TestPrune.TestMode.recordsTraces policy FsHotWatch.TestPrune.TestMode.initial

    test
        <@
            [ FsHotWatch.TestPrune.RecordOff
              FsHotWatch.TestPrune.RecordFullRuns
              FsHotWatch.TestPrune.RecordEveryRun ]
            |> List.map at = [ false; false; true ]
        @>
