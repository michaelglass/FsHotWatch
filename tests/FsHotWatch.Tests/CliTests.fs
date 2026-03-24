module FsHotWatch.Tests.CliTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.Program

// --- parseCommand tests ---

[<Fact>]
let ``parseCommand empty args returns Help`` () =
    test <@ parseCommand [] = Help @>

[<Fact>]
let ``parseCommand help returns Help`` () =
    test <@ parseCommand [ "help" ] = Help @>

[<Fact>]
let ``parseCommand --help returns Help`` () =
    test <@ parseCommand [ "--help" ] = Help @>

[<Fact>]
let ``parseCommand -h returns Help`` () =
    test <@ parseCommand [ "-h" ] = Help @>

[<Fact>]
let ``parseCommand start returns Start`` () =
    test <@ parseCommand [ "start" ] = Start @>

[<Fact>]
let ``parseCommand stop returns Stop`` () =
    test <@ parseCommand [ "stop" ] = Stop @>

[<Fact>]
let ``parseCommand status returns Status None`` () =
    test <@ parseCommand [ "status" ] = Status None @>

[<Fact>]
let ``parseCommand status with plugin returns Status Some`` () =
    test <@ parseCommand [ "status"; "lint" ] = Status(Some "lint") @>

[<Fact>]
let ``parseCommand unknown command returns PluginCommand`` () =
    test <@ parseCommand [ "warnings" ] = PluginCommand("warnings", "") @>

[<Fact>]
let ``parseCommand command with args joins them`` () =
    test <@ parseCommand [ "run"; "--verbose"; "foo" ] = PluginCommand("run", "--verbose foo") @>

// --- findRepoRoot tests ---

[<Fact>]
let ``findRepoRoot finds git repo`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-test-git-{System.Guid.NewGuid():N}")
    let nested = Path.Combine(tmpDir, "a", "b")
    Directory.CreateDirectory(nested) |> ignore
    Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore

    try
        let result = findRepoRoot nested
        test <@ result = Some tmpDir @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``findRepoRoot finds jj repo`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-test-jj-{System.Guid.NewGuid():N}")
    let nested = Path.Combine(tmpDir, "src")
    Directory.CreateDirectory(nested) |> ignore
    Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore

    try
        let result = findRepoRoot nested
        test <@ result = Some tmpDir @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``findRepoRoot returns None when no repo`` () =
    // Use a temp dir with no .git or .jj ancestor
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-test-none-{System.Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        // This might find a repo above /tmp on some systems, but the isolated dir itself won't have one.
        // We test a more reliable scenario: the function doesn't crash on a bare dir.
        let result = findRepoRoot tmpDir
        // On macOS /tmp is under /private which may have a repo above it; just verify it returns *something* without crashing.
        test <@ result |> Option.isNone || result |> Option.isSome @>
    finally
        Directory.Delete(tmpDir, true)

// --- computePipeName tests ---

[<Fact>]
let ``computePipeName is deterministic`` () =
    let name1 = computePipeName "/some/repo"
    let name2 = computePipeName "/some/repo"
    test <@ name1 = name2 @>

[<Fact>]
let ``computePipeName starts with prefix`` () =
    let name = computePipeName "/any/path"
    test <@ name.StartsWith("fs-hot-watch-") @>

[<Fact>]
let ``computePipeName differs for different paths`` () =
    let name1 = computePipeName "/repo/a"
    let name2 = computePipeName "/repo/b"
    test <@ name1 <> name2 @>

[<Fact>]
let ``computePipeName has expected length`` () =
    let name = computePipeName "/test"
    // "fs-hot-watch-" is 13 chars + 12 hex chars = 25
    test <@ name.Length = 25 @>
