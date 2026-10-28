/// check and confirm through a repository host's session (AUTOMATION-681 slice 6). A hosted
/// check converges to the tree as it is when the check settles (ADR-034). A hosted confirm's
/// verdict is pinned to the tree it answers (ADR-013): a green is a green for the bytes on
/// disk, the edits made while it ran included, and once the tree moves after it, `verdict`
/// says stale.
module FsHotWatch.Tests.HostedCheckSemanticsTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open Xunit
open FsHotWatch.Tests.ColdCheckFixture
open FsHotWatch.Tests.TestHelpers

let private dotnetHost () =
    Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"))

let private cliAssembly () =
    let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
    let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

    let assembly =
        Path.Combine(root, "src", "FsHotWatch.Cli", "bin", configuration, "net10.0", "FsHotWatch.Cli.dll")

    Assert.True(File.Exists assembly, $"build the real CLI before running this integration test: {assembly}")
    assembly

/// A CLI process of this test's own repository host.
let private startHosted (stateHome: string) (worktree: string) (args: string list) =
    let info = ProcessStartInfo(dotnetHost ())
    info.WorkingDirectory <- worktree
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
    info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] <- "0"
    // This test's own host: its control state lives under the test's directory.
    info.Environment["FSHW_STATE_HOME"] <- stateHome
    info.Environment["FSHW_REPOSITORY_HOST"] <- "1"

    for arg in cliAssembly () :: args do
        info.ArgumentList.Add arg

    let child = Process.Start info
    child, child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync()

let private finish clock (child: Process, out: Threading.Tasks.Task<string>, err: Threading.Tasks.Task<string>) =
    try
        Assert.True(child.WaitForExit(remaining clock 600000), "the hosted command did not finish")
        Assert.True(out.Wait(remaining clock 10000) && err.Wait(remaining clock 10000), "its output did not close")
        child.ExitCode, out.Result + "\n" + err.Result
    finally
        if not child.HasExited then
            child.Kill true

        child.Dispose()

let private runHosted clock stateHome worktree args =
    finish clock (startHosted stateHome worktree args)

let private prepare clock (root: string) =
    for name in [ "ColdA"; "ColdB" ] do
        let directory = Path.Combine(root, "tests", name)
        Directory.CreateDirectory directory |> ignore
        File.WriteAllText(Path.Combine(directory, name + ".fsproj"), project)
        writeValue directory name "2"
        File.WriteAllText(Path.Combine(directory, "Tests.fs"), testSource name)

    File.WriteAllText(
        Path.Combine(root, "ColdPair.slnx"),
        """<Solution><Project Path="tests/ColdA/ColdA.fsproj" /><Project Path="tests/ColdB/ColdB.fsproj" /></Solution>"""
    )

    File.WriteAllText(
        Path.Combine(root, ".fshw.json"),
        """{
  "repositoryHost": true,
  "build": { "command": "dotnet", "args": "build ColdPair.slnx --disable-build-servers" },
  "format": false,
  "lint": false,
  "tests": {
    "beforeRun": "dotnet build ColdPair.slnx --disable-build-servers",
    "projects": [
      { "project": "ColdA", "command": "dotnet", "args": "run --project tests/ColdA/ColdA.fsproj --no-build --",
        "filterTemplate": "--filter-class {classes}", "classJoin": " " },
      { "project": "ColdB", "command": "dotnet", "args": "run --project tests/ColdB/ColdB.fsproj --no-build --",
        "filterTemplate": "--filter-class {classes}", "classJoin": " " }
    ]
  }
}
"""
    )

    initialize clock root

let private runDirectories (root: string) =
    let runs = Path.Combine(root, ".fshw", "test-runs")

    if Directory.Exists runs then
        Directory.GetDirectories runs |> Set.ofArray
    else
        Set.empty

/// A worktree served by this test's own repository host, stopped however the body ends.
let private withHostedWorktree (prefix: string) (body: Stopwatch -> string -> string -> unit) =
    let clock = budget ()

    withTempDir prefix (fun dir ->
        let root = Path.Combine(dir, "repo")
        let stateHome = Path.Combine(dir, "state")
        Directory.CreateDirectory root |> ignore
        prepare clock root

        try
            body clock stateHome root
        finally
            runHosted clock stateHome root [ "stop"; "--repository" ] |> ignore)

/// Start `verb`, and once its test run has started, make a symbol-free edit that keeps the
/// suite green. Returns the verb's exit code and output.
let private editWhileRunning clock stateHome (root: string) (verb: string list) (expression: string) =
    let before = runDirectories root
    let command = startHosted stateHome root (verb @ [ "--agent" ])
    let child, _, _ = command

    let started =
        waitUntilTrue
            (fun () ->
                child.HasExited
                || not (Set.isEmpty (Set.difference (runDirectories root) before)))
            (remaining clock 300000)

    Assert.True(started && not child.HasExited, "the hosted command must start a test run before it ends")
    writeValue (Path.Combine(root, "tests", "ColdA")) "ColdA" expression
    finish clock command

[<Fact(Timeout = 900000)>]
let ``a hosted check converges to the tree as it is when the check settles`` () =
    withHostedWorktree "hosted-check-converges" (fun clock stateHome root ->
        let code, text = runHosted clock stateHome root [ "check"; "--agent" ]
        Assert.True((code = 0), $"positive control: the hosted baseline check is green:\n{text}")

        // New bytes in ColdA owe its tests, so the check runs them; a second edit lands
        // while they run.
        writeValue (Path.Combine(root, "tests", "ColdA")) "ColdA" "1 + 1"
        let code, text = editWhileRunning clock stateHome root [ "check" ] "2 * 1"
        Assert.True((code = 0), $"a check whose tree moved while it ran converges to a green:\n{text}")

        // The verdict it left describes the tree on disk: the second edit included.
        let verdict, text = runHosted clock stateHome root [ "verdict" ]
        Assert.True((verdict = 0), $"the converged verdict applies to the edited tree:\n{text}"))

[<Fact(Timeout = 900000)>]
let ``a hosted confirm is pinned to the tree it answers: honest when the tree moves under it, stale after`` () =
    withHostedWorktree "hosted-confirm-pinned" (fun clock stateHome root ->
        let code, text = runHosted clock stateHome root [ "check"; "--agent" ]
        Assert.True((code = 0), $"positive control: the hosted baseline check is green:\n{text}")

        let code, text =
            editWhileRunning clock stateHome root [ "confirm"; "--fresh" ] "1 + 1"

        let verdict, verdictText = runHosted clock stateHome root [ "verdict" ]

        // A green it gives is a green for the tree on disk, the edit included; anything
        // else is an honest refusal, never a green for bytes nobody checked.
        if code = 0 then
            Assert.True(
                (verdict = 0),
                $"a green confirm's verdict applies to the tree on disk:\n{text}\n{verdictText}"
            )
        else
            Assert.True((verdict <> 0), $"a refused confirm left no applying verdict:\n{text}\n{verdictText}")

        // Pinned: once the tree moves after it, its verdict no longer applies.
        writeValue (Path.Combine(root, "tests", "ColdA")) "ColdA" "2 + 0"
        let verdict, text = runHosted clock stateHome root [ "verdict" ]
        Assert.True((verdict = 4), $"the verdict is stale once the tree moves:\n{text}"))
