/// `bench/two-session/run.sh` starts real daemons over a throwaway repository. Without a
/// scratch home, each of them writes its shared task store (one namespace per synthetic
/// repo) into the developer's real `~/.cache/fshw`, and its repository-host control state
/// into the real `~/.local/state/fshw`. That is the leak the test suite had
/// (`tests/FsHotWatch.Tests/UserHomeIsolation.fs`), in the one harness it did not cover.
///
/// The script is text, and so is this check: it pins that both homes are EXPORTED, point
/// inside the run's own directory, and are set before the first line that runs fshw.
/// Everything the script starts inherits them, so that ordering is the whole guarantee.
module FsHotWatch.Bench.Tests.TwoSessionHarnessTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: FsHotWatch.slnx is absent from every ancestor"
        elif File.Exists(Path.Combine(directory.FullName, "FsHotWatch.slnx")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

let private scriptLines () =
    File.ReadAllLines(Path.Combine(repoRoot (), "bench", "two-session", "run.sh"))
    |> Array.toList

/// Lines that run the frozen fshw binary — every `"$fshw"` that is not its assignment.
let private fshwCalls (lines: string list) =
    lines
    |> List.indexed
    |> List.filter (fun (_, line) ->
        let code = line.TrimStart()
        not (code.StartsWith('#')) && code.Contains "\"$fshw\"")
    |> List.map fst

/// The line index of `export NAME="$run_dir/..."`, or None when it is absent.
let private scratchExport (name: string) (lines: string list) =
    let pattern = $"^\\s*export\\s+%s{name}=\"\\$run_dir/[^\"]+\"\\s*$"
    lines |> List.tryFindIndex (fun line -> Regex.IsMatch(line, pattern))

[<Theory>]
[<InlineData("FSHW_CACHE_HOME")>]
[<InlineData("FSHW_STATE_HOME")>]
let ``the two-session harness exports a scratch fshw home before it runs fshw`` (name: string) =
    let lines = scriptLines ()
    let calls = fshwCalls lines
    // Guards the guard: a script this check could not find fshw in proves nothing.
    test <@ not (List.isEmpty calls) @>
    let export = scratchExport name lines
    test <@ export.IsSome @>
    test <@ export.Value < List.min calls @>
    // Nothing later points it back at the real home.
    let resets =
        lines
        |> List.indexed
        |> List.filter (fun (i, line) -> i <> export.Value && line.Contains $"%s{name}=")
        |> List.map snd

    test <@ resets = [] @>
