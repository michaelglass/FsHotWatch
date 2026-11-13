/// scripts/packaging-inputs-changed.fsx decides whether the fast landing gate (`land-check`)
/// runs the package-floor step (`check-package-floors.fsx -- pack`), which otherwise runs only
/// in `mise run ci`. On 2026-11-13 a change was green on land-check and broke that pack step
/// (NETSDK1085); only the post-land spot check caught it (AUTOMATION-1654). The step is kept
/// out of land-check when nothing it reads moved, and run when anything did.
///
/// `classify` is the pure decision: paths in, run/skip plus a reason out. `gate --dry-run`
/// adds the base resolution through `jj`, faked here, without packing anything.
module FsHotWatch.Tests.PackagingInputsGateTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

let private script root =
    Path.Combine(root, "scripts", "packaging-inputs-changed.fsx")

let private runScript (pathPrefix: string option) (args: string list) =
    let root = repoRoot ()
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    [ "fsi"; script root; "--" ] @ args |> List.iter start.ArgumentList.Add

    pathPrefix
    |> Option.iter (fun bin -> start.Environment["PATH"] <- bin + ":" + Environment.GetEnvironmentVariable "PATH")

    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()

let private classify (args: string list) = runScript None ("classify" :: args)

let private runs (code: int, output: string) =
    test <@ code = 0 @>
    test <@ output.Contains "package floors: running" @>

let private skips (code: int, output: string) =
    test <@ code = 0 @>
    test <@ output.Contains "package floors: skipped, no packaging input changed since main@origin" @>

[<Fact>]
let ``a changed fsproj runs the step and names it`` () =
    let result = classify [ "src/FsHotWatch/FsHotWatch.fsproj" ]
    runs result
    test <@ (snd result).Contains "src/FsHotWatch/FsHotWatch.fsproj" @>

[<Fact>]
let ``a changed Directory.Build.targets runs the step`` () =
    runs (classify [ "src/FsHotWatch/Foo.fs"; "Directory.Build.targets" ])

[<Fact>]
let ``every declared packaging input runs the step`` () =
    for path in
        [ "tests/X/X.csproj"
          "Directory.Build.props"
          "Directory.Packages.props"
          "build/Pack.targets"
          "src/X/X.props"
          "semantic-tagger.json"
          ".github/workflows/release.yml"
          "scripts/check-package-floors.fsx"
          "mise.toml" ] do
        runs (classify [ path ])

[<Fact>]
let ``source-only changes skip the step`` () =
    skips (classify [ "src/X/Foo.fs"; "README.md"; ".github/workflows/ci.yml" ])

[<Fact>]
let ``no changes skip the step`` () = skips (classify [])

[<Fact>]
let ``an unresolvable base runs the step and says why`` () =
    let code, output = classify [ "--base-unresolved"; "src/X/Foo.fs" ]
    runs (code, output)
    test <@ output.Contains "main@origin could not be resolved" @>

/// A stand-in `jj` first on PATH: prints `stdout` for any call and exits `exitCode`.
let private withFakeJj (exitCode: int) (stdout: string) body =
    let bin = Path.Combine(Path.GetTempPath(), "fshw-fakejj-" + string (Guid.NewGuid()))

    Directory.CreateDirectory bin |> ignore
    let fake = Path.Combine(bin, "jj")
    File.WriteAllText(fake, $"#!/usr/bin/env bash\nprintf '%%b' '%s{stdout}'\nexit %d{exitCode}\n")
    File.SetUnixFileMode(fake, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    try
        body bin
    finally
        Directory.Delete(bin, true)

[<Fact>]
let ``the gate reads the diff from jj and skips a source-only change`` () =
    withFakeJj 0 "src/X/Foo.fs\\n" (fun bin -> skips (runScript (Some bin) [ "gate"; "--dry-run" ]))

[<Fact>]
let ``the gate runs the step when jj reports a packaging input`` () =
    withFakeJj 0 "src/X/Foo.fs\\nsrc/X/X.fsproj\\n" (fun bin -> runs (runScript (Some bin) [ "gate"; "--dry-run" ]))

[<Fact>]
let ``the gate fails safe, running the step, when jj cannot resolve the base`` () =
    withFakeJj 1 "Error: Revision `main@origin` doesn't exist\\n" (fun bin ->
        let code, output = runScript (Some bin) [ "gate"; "--dry-run" ]
        runs (code, output)
        test <@ output.Contains "main@origin could not be resolved" @>)

[<Fact>]
let ``land-check runs the input-keyed package-floor step after compile`` () =
    let mise = miseToml (repoRoot ())
    test <@ dependsOf mise "land-check" |> List.contains "package-floors-if-changed" @>
    test <@ dependsOf mise "package-floors-if-changed" |> List.contains "compile" @>

    test
        <@
            (commandLines mise "package-floors-if-changed").Contains
                "dotnet fsi scripts/packaging-inputs-changed.fsx -- gate"
        @>
