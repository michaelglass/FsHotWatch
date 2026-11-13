/// Every scripted fixture in `tests/` builds its options through `scriptProjectOptions`
/// (AUTOMATION-1654).
///
/// FCS's `GetProjectOptionsFromScript` defaults to `assumeDotNetFramework = true`. A fixture
/// that left the argument out checked its scratch file against the .NET Framework, which
/// gave it a real FCS error whenever the test ran alone, and siblings in the same process
/// hid that by warming the shared checker (AUTOMATION-1653). Thirty more call sites had the
/// same omission. Each test assembly now has one helper that always passes the argument,
/// and this guard keeps every other direct call out of the suites.
module FsHotWatch.Tests.ScriptOptionsGuardTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.RepoTasks

/// The helpers, one per test assembly that builds script options (neither assembly can
/// see the other's): the only files allowed to call FCS directly.
let private helpers =
    set
        [ "tests/FsHotWatch.Tests/TestHelpers.fs"
          "tests/TestPrune.Tests/TestHelpers.fs" ]

let private directCall = Regex(@"\.GetProjectOptionsFromScript\s*\(")

/// Whether `line` calls `GetProjectOptionsFromScript` directly. A comment is not code.
let internal callsScriptOptionsDirectly (line: string) =
    not (line.TrimStart().StartsWith "//") && directCall.IsMatch line

/// Every direct call in `tests/`, as `(relative file, "file:line: code")`. This file is
/// left out: its cases spell the shape it looks for.
let private directCalls (root: string) =
    filesUnder FsHotWatch.SafeWalk.SourceExcludedDirs "*.fs" (Path.Combine(root, "tests"))
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), path)
    |> Seq.filter (fun (relative, _) -> not (relative.Contains "/obj/" || relative.Contains "/bin/"))
    |> Seq.filter (fun (relative, _) -> Path.GetFileName relative <> "ScriptOptionsGuardTests.fs")
    |> Seq.collect (fun (relative, path) ->
        File.ReadAllLines path
        |> Seq.mapi (fun i line -> i + 1, line)
        |> Seq.filter (snd >> callsScriptOptionsDirectly)
        |> Seq.map (fun (n, line) -> relative, $"%s{relative}:%d{n}: %s{line.Trim()}"))
    |> List.ofSeq

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``no test outside the helpers calls GetProjectOptionsFromScript directly`` () =
    let found =
        directCalls (repoRoot ())
        |> List.filter (fun (relative, _) -> not (Set.contains relative helpers))
        |> List.map snd

    if not (List.isEmpty found) then
        Assert.Fail(
            "These fixtures build script options without the helper, so nothing makes them pass \
             assumeDotNetFramework = false. Use TestHelpers.scriptProjectOptions:\n"
            + String.Join("\n", found)
        )

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the scan finds the helpers' own calls`` () =
    // Positive control: each helper calls FCS directly, so a scan that finds no call in
    // them is blind, and its empty answer above would mean nothing.
    let inHelpers =
        directCalls (repoRoot ())
        |> List.map fst
        |> List.filter (fun relative -> Set.contains relative helpers)
        |> Set.ofList

    test <@ inHelpers = helpers @>

[<Theory>]
[<InlineData("            checker.GetProjectOptionsFromScript(")>]
[<InlineData("        sharedChecker.Value.GetProjectOptionsFromScript(file, source, assumeDotNetFramework = false)")>]
[<InlineData("    let o, _ = checker.GetProjectOptionsFromScript (path, text) |> Async.RunSynchronously")>]
let ``a direct call is found`` (line: string) =
    test <@ callsScriptOptionsDirectly line @>

[<Theory>]
[<InlineData("            scriptProjectOptions (checker, path, text)")>]
[<InlineData("        // Force-include absSource: GetProjectOptionsFromScript's SourceFiles")>]
[<InlineData("/// calls `GetProjectOptionsFromScript`, whose `assumeDotNetFramework` defaults to `true`")>]
let ``a helper call or a mention is not a direct call`` (line: string) =
    test <@ not (callsScriptOptionsDirectly line) @>
