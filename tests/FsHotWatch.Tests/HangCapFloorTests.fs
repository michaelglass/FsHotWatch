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
