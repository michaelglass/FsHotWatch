/// scripts/check-commit-messages.sh refuses commit messages that carry private
/// references: agent-session trailers and links, private-network hostnames and
/// issue-tracker URLs. This is a public repository, and nothing used to stop those.
///
/// The guard reads `git log -z --format='%H%n%B'` (or the jj equivalent) on stdin:
/// NUL-separated records, each a commit id on its first line and the message after.
/// Every refusal below is a positive control: a planted leak MUST turn the run red,
/// or a green from the guard would mean nothing.
module FsHotWatch.Tests.CommitMessageGuardTests

open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

let private script root =
    Path.Combine(root, "scripts", "check-commit-messages.sh")

let private runGuard (args: string list) (records: (string * string) list) =
    let root = repoRoot ()
    let start = ProcessStartInfo("bash")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardInput <- true
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add(script root)
    args |> List.iter start.ArgumentList.Add
    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()

    for sha, message in records do
        child.StandardInput.Write($"%s{sha}\n%s{message}\000")

    child.StandardInput.Close()
    child.WaitForExit()
    child.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()

let private clean =
    "aaaa111", "Fix the thing\n\nCo-Authored-By: Someone <someone@example.com>\n"

let private refused (message: string) =
    let code, output = runGuard [] [ clean; "bbbb222", message ]
    test <@ code = 1 @>
    // The refusal names the offending commit, not just "something failed".
    test <@ output.Contains "bbbb222" @>
    test <@ not (output.Contains "aaaa111") @>

[<Fact>]
let ``a clean message passes`` () =
    let code, output = runGuard [] [ clean ]
    test <@ code = 0 @>
    test <@ output.Contains "1 commit" @>

[<Fact>]
let ``no commits to check passes`` () =
    let code, _ = runGuard [] []
    test <@ code = 0 @>

[<Fact>]
let ``an agent session trailer is refused`` () =
    refused "Fix\n\nClaude-Session: https://claude.ai/code/session_0123456789abcdef\n"

[<Fact>]
let ``an agent session link in the body is refused`` () =
    refused "Fix\n\nSee https://claude.ai/code/session_0123abc for the discussion.\n"

[<Fact>]
let ``a private-network hostname is refused`` () =
    refused "Fix\n\nReproduced on build-box.example-tailnet.ts.net.\n"

[<Fact>]
let ``an issue-tracker URL is refused`` () =
    refused "Fix\n\nhttps://app.plane.so/some-workspace/browse/PROJ-12/\n"

[<Fact>]
let ``a bare ticket id in a title passes by default`` () =
    let code, _ = runGuard [] [ "cccc333", "Finish: AUTOMATION-1234 fix the thing\n" ]
    test <@ code = 0 @>

[<Fact>]
let ``with --forbid-ticket-ids a bare internal ticket id is refused`` () =
    for prefix in [ "AUTOMATION"; "NEWS"; "APPLIC" ] do
        let code, output =
            runGuard [ "--forbid-ticket-ids" ] [ "dddd444", $"Finish: %s{prefix}-12 fix\n" ]

        test <@ code = 1 @>
        test <@ output.Contains "dddd444" @>

[<Fact>]
let ``local ci checks the unpushed commits`` () =
    let mise = miseToml (repoRoot ())
    test <@ dependsOf mise "ci" |> List.contains "check-commit-messages" @>

    test
        <@
            (commandLines mise "check-commit-messages").Contains
                "bash scripts/check-commit-messages.sh --jj-revset 'main@origin..@'"
        @>

/// A stand-in `jj` first on PATH: prints `records` for `jj log`, or fails with `exitCode`.
let private runGuardWithFakeJj (exitCode: int) (records: string) =
    let bin =
        Path.Combine(Path.GetTempPath(), "fshw-fakejj-" + string (System.Guid.NewGuid()))

    Directory.CreateDirectory bin |> ignore
    let fake = Path.Combine(bin, "jj")
    File.WriteAllText(fake, $"#!/usr/bin/env bash\nprintf '%%b' '%s{records}'\nexit %d{exitCode}\n")

    File.SetUnixFileMode(fake, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    try
        let root = repoRoot ()
        let start = ProcessStartInfo("bash")
        start.WorkingDirectory <- root
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.ArgumentList.Add(script root)
        start.ArgumentList.Add "--jj-revset"
        start.ArgumentList.Add "main@origin..@"
        start.Environment["PATH"] <- bin + ":" + System.Environment.GetEnvironmentVariable "PATH"
        use child = Process.Start start
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        child.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()
    finally
        Directory.Delete(bin, true)

[<Fact>]
let ``--jj-revset refuses a leak in the commits jj lists`` () =
    let code, output =
        runGuardWithFakeJj 0 "eeee555\\nclean\\0ffff666\\ntip\\n\\nClaude-Session: x\\0"

    test <@ code = 1 @>
    test <@ output.Contains "ffff666" @>

[<Fact>]
let ``--jj-revset fails when jj fails, rather than reading no commits as clean`` () =
    let code, _ = runGuardWithFakeJj 3 ""
    test <@ code <> 0 @>

[<Fact>]
let ``GitHub CI checks the pushed commits`` () =
    let yml =
        File.ReadAllText(Path.Combine(repoRoot (), ".github", "workflows", "ci.yml"))

    test <@ yml.Contains "  commit-messages:" @>
    test <@ yml.Contains "fetch-depth: 0" @>
    test <@ yml.Contains "bash scripts/check-commit-messages.sh --git-base \"$PUSH_BASE\"" @>

// --git-base reads the history itself, the way GitHub CI runs it.

let private git (dir: string) (args: string list) =
    let start = ProcessStartInfo("git")
    start.WorkingDirectory <- dir
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true

    [ "-c"
      "user.name=Test"
      "-c"
      "user.email=test@example.com"
      "-c"
      "commit.gpgsign=false" ]
    @ args
    |> List.iter start.ArgumentList.Add

    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    child.StandardError.ReadToEndAsync() |> ignore
    child.WaitForExit()
    test <@ child.ExitCode = 0 @>
    stdout.GetAwaiter().GetResult().Trim()

let private runGuardIn (dir: string) (args: string list) =
    let start = ProcessStartInfo("bash")
    start.WorkingDirectory <- dir
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add(script (repoRoot ()))
    args |> List.iter start.ArgumentList.Add
    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()

/// A throwaway repository: a base commit, then a clean commit and one carrying `last`.
let private historyEndingWith (last: string) =
    let dir =
        Path.Combine(Path.GetTempPath(), "fshw-commitguard-" + string (System.Guid.NewGuid()))

    Directory.CreateDirectory dir |> ignore
    git dir [ "init"; "-q" ] |> ignore

    git
        dir
        [ "commit"
          "-q"
          "--allow-empty"
          "-m"
          "base\n\nClaude-Session: before the range" ]
    |> ignore

    let baseSha = git dir [ "rev-parse"; "HEAD" ]
    git dir [ "commit"; "-q"; "--allow-empty"; "-m"; "clean" ] |> ignore
    git dir [ "commit"; "-q"; "--allow-empty"; "-m"; last ] |> ignore
    dir, baseSha

[<Fact>]
let ``--git-base refuses a leak in the pushed range, including its last commit`` () =
    let dir, baseSha =
        historyEndingWith "tip\n\nClaude-Session: https://claude.ai/code/session_x"

    try
        let tip = git dir [ "rev-parse"; "HEAD" ]
        let code, output = runGuardIn dir [ "--git-base"; baseSha ]
        test <@ code = 1 @>
        test <@ output.Contains tip @>
        // The base itself is not in the range: its planted trailer must not be reported.
        test <@ not (output.Contains baseSha) @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``--git-base passes a clean pushed range and counts every commit in it`` () =
    let dir, baseSha = historyEndingWith "tip"

    try
        let code, output = runGuardIn dir [ "--git-base"; baseSha ]
        test <@ code = 0 @>
        test <@ output.Contains "2 commits clean" @>
    finally
        Directory.Delete(dir, true)

// A manual dispatch, or a push that creates a branch, has no usable base: the guard
// then checks what is not on origin/main. It must NOT fall back to all of history, whose
// already-published messages are out of scope and would red every such run.
[<Fact>]
let ``--git-base with no usable base checks only the commits not on origin/main`` () =
    let dir, baseSha = historyEndingWith "tip"

    try
        git dir [ "update-ref"; "refs/remotes/origin/main"; baseSha ] |> ignore

        for given in [ ""; "0000000000000000000000000000000000000000"; "deadbeefdeadbeef" ] do
            let code, output = runGuardIn dir [ "--git-base"; given ]
            test <@ code = 0 @>
            test <@ output.Contains "2 commits clean" @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``--git-base with no usable base and no origin/main refuses to guess`` () =
    let dir, _ = historyEndingWith "tip"

    try
        let code, output = runGuardIn dir [ "--git-base"; "" ]
        test <@ code = 2 @>
        test <@ output.Contains "origin/main" @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``--git-base outside a git checkout says SKIPPED rather than passing silently`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fshw-commitguard-" + string (System.Guid.NewGuid()))

    Directory.CreateDirectory dir |> ignore

    try
        let code, output = runGuardIn dir [ "--git-base"; "" ]
        test <@ code = 0 @>
        test <@ output.Contains "SKIPPED" @>
    finally
        Directory.Delete(dir, true)
