/// No xUnit cap in the suites is below the floor `docs/wall-clock-bounds.md` sets for a
/// hang cap (rule 2): 5 s, even over in-process work.
///
/// A cap below it is a timing assertion in disguise. A pure XML parse under a 2 s cap was
/// cancelled in a loaded full gate (2026-11-02) while taking milliseconds alone: the cap
/// measured the box, not the code. This refuses any `Fact`/`Theory` whose `Timeout` is an
/// integer literal under the floor; named caps (`SyncHangBackstopMs` and the rest in
/// `TestHelpers`) are the way to state a bound.
module FsHotWatch.Tests.HangCapFloorTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

/// Rule 2's floor for a hang cap, in milliseconds.
let private floorMs = 5000

/// A `Fact`/`Theory` attribute whose `Timeout` is a bare integer literal.
let private literalCap =
    Regex(@"^\s*\[<\s*(Fact|Theory)\s*\(.*\bTimeout\s*=\s*([0-9_]+)\s*[,)]")

/// The literal cap a line declares, if any.
let internal literalCapOf (line: string) : int option =
    let m = literalCap.Match line

    if m.Success then
        Some(int (m.Groups[2].Value.Replace("_", "")))
    else
        None

/// Every attribute under the floor in `tests/`, as `file:line: code`.
let private capsUnderFloor (root: string) =
    FsHotWatch.Tests.TestHelpers.filesUnder FsHotWatch.SafeWalk.SourceExcludedDirs "*.fs" (Path.Combine(root, "tests"))
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), path)
    |> Seq.filter (fun (relative, _) -> not (relative.Contains "/obj/" || relative.Contains "/bin/"))
    |> Seq.collect (fun (relative, path) ->
        File.ReadAllLines path
        |> Seq.mapi (fun i line -> i + 1, line)
        |> Seq.filter (fun (_, line) ->
            match literalCapOf line with
            | Some ms -> ms < floorMs
            | None -> false)
        |> Seq.map (fun (n, line) -> $"%s{relative}:%d{n}: %s{line.Trim()}"))
    |> List.ofSeq

[<Fact>]
let ``no xUnit cap in the suites is under the hang-cap floor`` () =
    let under = capsUnderFloor (repoRoot ())

    if not (List.isEmpty under) then
        Assert.Fail(
            $"xUnit caps under %d{floorMs} ms report the box's load, not a hang (docs/wall-clock-bounds.md, rule 2). \
              Use a named cap from TestHelpers (SyncHangBackstopMs for in-process work):\n"
            + String.Join("\n", under)
        )

[<Theory>]
[<InlineData("[<Fact(Timeout = 2000)>]", 2000)>]
[<InlineData("[<Fact(Timeout = 60_000)>]", 60000)>]
[<InlineData("[<Theory(Timeout = 4999, Skip = \"x\")>]", 4999)>]
let ``a literal cap is read from its attribute`` (line: string) (expected: int) =
    test <@ literalCapOf line = Some expected @>

[<Theory>]
[<InlineData("[<Fact(Timeout = SyncHangBackstopMs)>]")>]
[<InlineData("[<Fact(Timeout = 2 * HangWaitMs)>]")>]
[<InlineData("let timeout = 2000")>]
let ``a named or computed cap is not a literal`` (line: string) = test <@ literalCapOf line = None @>

// ---------------------------------------------------------------------------
// A test that moves or reads the process working directory waits, on entry, for every
// in-process MSBuild evaluation in flight (`Daemon.whileNoEvaluationRuns`). Evaluation has
// no product bound and runs one at a time per process, so in a full run that wait queues
// behind other tests' daemons: rule 2 caps such a test at `InProcessEvaluationTestCapMs`.
//
// A cap under it is worse than a red test (AUTOMATION-1461). xUnit abandons a timed-out
// test without stopping it, so its body, still queued for the evaluations, later stands the
// process in a directory and deletes it while the NEXT test runs: a 15 s cap went red at
// load 72, and the test after it failed in a deleted working directory.
// ---------------------------------------------------------------------------

/// The `Timeout` an xUnit attribute line declares, as written.
let private capExpression =
    Regex(@"^\s*\[<\s*(Fact|Theory)\s*\(.*\bTimeout\s*=\s*([^,)]+)")

/// The helpers that wait for in-flight evaluations before touching the working directory,
/// spelled in halves so this file is not itself a reader of it to
/// `TestHelpersTests`' serialized-collection scan.
let private standsIn = "with" + "ProcessWorkingDirectory"

let private readsAt = "at" + "SettledWorkingDirectory"

let private evaluationWaiters = Regex($@"\b(%s{standsIn}|%s{readsAt})\b")

/// Whether a cap, as written, is at least `InProcessEvaluationTestCapMs`.
let internal coversEvaluationQueue (cap: string) =
    cap.Contains "InProcessEvaluationTestCapMs"
    || (match Int32.TryParse(cap.Trim().Replace("_", "")) with
        | true, ms -> ms >= FsHotWatch.Tests.TestHelpers.InProcessEvaluationTestCapMs
        | false, _ -> false)

/// Each test in `lines` that waits for the evaluation queue, as (line, its cap). A test's
/// body runs from its attribute to the next top-level binding; a helper defined between
/// tests is not attributed to the test before it.
let internal evaluationWaitingTests (lines: string[]) =
    let mutable cap = None
    let mutable previous = ""

    [ for i in 0 .. lines.Length - 1 do
          let line = lines[i]
          let trimmed = line.TrimStart()
          let m = capExpression.Match line

          if m.Success then
              cap <- Some m.Groups[2].Value
          elif line.StartsWith "let " && not (previous.TrimStart().StartsWith "[<") then
              cap <- None

          if not (trimmed.StartsWith "//") && evaluationWaiters.IsMatch line then
              match cap with
              | Some c -> yield i + 1, c
              | None -> ()

          if not (String.IsNullOrWhiteSpace line) then
              previous <- line ]

[<Fact>]
let ``a test that waits for in-flight evaluations is capped above the evaluation queue`` () =
    let root = repoRoot ()

    let under =
        FsHotWatch.Tests.TestHelpers.filesUnder
            FsHotWatch.SafeWalk.SourceExcludedDirs
            "*.fs"
            (Path.Combine(root, "tests"))
        |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), path)
        |> Seq.filter (fun (relative, _) -> not (relative.Contains "/obj/" || relative.Contains "/bin/"))
        |> Seq.filter (fun (relative, _) -> Path.GetFileName relative <> "HangCapFloorTests.fs")
        |> Seq.collect (fun (relative, path) ->
            evaluationWaitingTests (File.ReadAllLines path)
            |> Seq.filter (snd >> coversEvaluationQueue >> not)
            |> Seq.map (fun (n, cap) -> $"%s{relative}:%d{n}: Timeout = %s{cap.Trim()}"))
        |> List.ofSeq

    if not (List.isEmpty under) then
        Assert.Fail(
            "These tests wait for in-flight MSBuild evaluations, which have no product bound, under a cap below \
             InProcessEvaluationTestCapMs (docs/wall-clock-bounds.md, rule 2):\n"
            + String.Join("\n", under)
        )

[<Fact>]
let ``a test's evaluation wait is read with the cap of that test, not of a helper before it`` () =
    let lines =
        [| "[<Fact(Timeout = 15000)>]"
           "let ``short`` () ="
           $"    %s{standsIn} None ignore"
           ""
           $"let private helper () = %s{readsAt} ignore"
           ""
           "[<Fact(Timeout = InProcessEvaluationTestCapMs)>]"
           "let ``long`` () ="
           $"    // %s{standsIn} in a comment"
           $"    %s{readsAt} ignore" |]

    test <@ evaluationWaitingTests lines = [ 3, "15000"; 10, "InProcessEvaluationTestCapMs" ] @>
    test <@ not (coversEvaluationQueue "15000") && coversEvaluationQueue "300_000" @>
