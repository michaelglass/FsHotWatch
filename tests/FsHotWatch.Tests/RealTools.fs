/// Tests that run the real `git` and `jj`: a bound on each command, and what a test does
/// when one is missing from PATH (skip off CI, fail on CI).
module FsHotWatch.Tests.RealTools

open System
open System.Diagnostics
open System.IO
open Xunit
open FsHotWatch.Tests.TestHelpers

let onPath (tool: string) =
    (Environment.GetEnvironmentVariable "PATH").Split(Path.PathSeparator)
    |> Array.exists (fun d -> File.Exists(Path.Combine(d, tool)))

let run (cwd: string) (exe: string) (args: string list) =
    let psi = ProcessStartInfo(exe, args, WorkingDirectory = cwd)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.Environment["GIT_CONFIG_GLOBAL"] <- "/dev/null"
    psi.Environment["JJ_CONFIG"] <- "/dev/null"
    psi.Environment["JJ_USER"] <- "t"
    psi.Environment["JJ_EMAIL"] <- "t@example.com"
    use p = Process.Start psi
    let stdout = p.StandardOutput.ReadToEndAsync()
    let stderr = p.StandardError.ReadToEndAsync()

    if not (p.WaitForExit RealCommandMs) then
        p.Kill true
        failwith $"%s{exe} %A{args} timed out"

    if p.ExitCode <> 0 then
        failwith $"%s{exe} %A{args} exited %d{p.ExitCode}: %s{stdout.Result}%s{stderr.Result}"

/// What a test needing real tools does when some are missing from PATH.
type ToolGate =
    | Run
    | SkipMissing of reason: string
    | FailMissing of reason: string

/// The gate for a test whose `missing` tools are not on PATH. CI installs them (git on the
/// runner image, jj by `scripts/install-jj.sh`), so on CI a missing one fails: a skip there
/// would read as a pass.
let toolGate (onCi: bool) (missing: string list) : ToolGate =
    match missing with
    | [] -> Run
    | _ when onCi -> FailMissing $"""%s{String.Join(", ", missing)} not on PATH on CI, which installs it"""
    | _ -> SkipMissing $"""%s{String.Join(", ", missing)} not on PATH"""

/// Whether this process runs under CI (`CI` or `GITHUB_ACTIONS` set).
let onCi () =
    [ "CI"; "GITHUB_ACTIONS" ]
    |> List.exists (Environment.GetEnvironmentVariable >> String.IsNullOrEmpty >> not)

/// Run only if every one of `tools` is on PATH, skipping or failing as `toolGate` says.
let requireTools (tools: string list) =
    match toolGate (onCi ()) (tools |> List.filter (onPath >> not)) with
    | Run -> ()
    | SkipMissing reason -> Assert.Skip reason
    | FailMissing reason -> Assert.Fail reason
