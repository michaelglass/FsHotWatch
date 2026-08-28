module TestPrune.Tests.NuGetPublicationBarrierTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote

type private BarrierResult =
    { ExitCode: int
      Stdout: string
      Stderr: string
      Elapsed: TimeSpan }

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: scripts/wait-for-nuget.fsx is absent from every ancestor"
        elif File.Exists(Path.Combine(directory.FullName, "scripts", "wait-for-nuget.fsx")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

let private writeProject path packageIds versions packAsTool =
    let elements name values =
        values
        |> List.map (fun value -> $"    <%s{name}>%s{value}</%s{name}>")
        |> String.concat "\n"

    File.WriteAllText(
        path,
        $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
%s{elements "PackageId" packageIds}
%s{elements "Version" versions}
%s{if packAsTool then
       "    <PackAsTool>true</PackAsTool>"
   else
       ""}
</PropertyGroup></Project>"""
    )

let private writeFakeDotnet path =
    File.WriteAllText(
        path,
        """#!/bin/sh
set -eu
count=0
if [ -f "$FAKE_COUNT_FILE" ]; then count=$(cat "$FAKE_COUNT_FILE"); fi
count=$((count + 1))
printf '%s' "$count" > "$FAKE_COUNT_FILE"
if [ -n "${FAKE_CAPTURE_DIR:-}" ]; then
  printf '%s\n' "$@" > "$FAKE_CAPTURE_DIR/argv.txt"
  printf '%s' "${NUGET_PACKAGES:-}" > "$FAKE_CAPTURE_DIR/nuget-packages.txt"
  previous=""
  for arg in "$@"; do
    if [ "$previous" = "--configfile" ]; then cp "$arg" "$FAKE_CAPTURE_DIR/NuGet.Config"; fi
    previous="$arg"
  done
  if [ "$1" = "restore" ]; then cp "$2" "$FAKE_CAPTURE_DIR/probe.csproj"; fi
fi
case "${FAKE_MODE:-success}" in
  success) exit 0 ;;
  failure) printf 'synthetic restore failure' >&2; exit 42 ;;
  retry) if [ "$count" -lt "${FAKE_SUCCEED_AT:-2}" ]; then exit 42; else exit 0; fi ;;
  timeout) sleep 30 ;;
esac
"""
    )

    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

let private runBarrier root fakeDotnet probeParent project packageId settings =
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true

    [ "fsi"
      Path.Combine(root, "scripts", "wait-for-nuget.fsx")
      "--"
      packageId
      project ]
    |> List.iter start.ArgumentList.Add

    start.Environment["TESTPRUNE_NUGET_PROBE_DOTNET"] <- fakeDotnet
    start.Environment["TESTPRUNE_NUGET_PROBE_PARENT"] <- probeParent
    start.Environment["TESTPRUNE_NUGET_PROBE_ATTEMPTS"] <- "1"
    start.Environment["TESTPRUNE_NUGET_PROBE_DELAY_MS"] <- "1"
    start.Environment["TESTPRUNE_NUGET_PROBE_PROCESS_TIMEOUT_MS"] <- "1000"
    settings |> List.iter (fun (key, value) -> start.Environment[key] <- value)

    let clock = Stopwatch.StartNew()
    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    clock.Stop()

    { ExitCode = child.ExitCode
      Stdout = stdout.GetAwaiter().GetResult()
      Stderr = stderr.GetAwaiter().GetResult()
      Elapsed = clock.Elapsed }

let private scratch body =
    let temp =
        Path.Combine(Path.GetTempPath(), $"testprune-nuget-barrier-%A{Guid.NewGuid()}")

    Directory.CreateDirectory temp |> ignore

    try
        let project = Path.Combine(temp, "Package.fsproj")
        let fakeDotnet = Path.Combine(temp, "fake-dotnet")
        let probeParent = Path.Combine(temp, "probes")
        let capture = Path.Combine(temp, "capture")
        let countFile = Path.Combine(temp, "count")
        Directory.CreateDirectory capture |> ignore
        writeFakeDotnet fakeDotnet
        body project fakeDotnet probeParent capture countFile
    finally
        if Directory.Exists temp then
            Directory.Delete(temp, true)

let private probeDirectories path =
    if Directory.Exists path then
        Directory.GetDirectories path
    else
        [||]

[<Fact>]
let ``success probes one exact version from only nuget org with a fresh cache`` () =
    scratch (fun project fakeDotnet probeParent capture countFile ->
        writeProject project [ "Example.Package" ] [ "1.2.3-alpha.4" ] false

        let settings =
            [ "FAKE_MODE", "success"
              "FAKE_CAPTURE_DIR", capture
              "FAKE_COUNT_FILE", countFile ]

        let first =
            runBarrier (repoRoot ()) fakeDotnet probeParent project "Example.Package" settings

        let argv = File.ReadAllLines(Path.Combine(capture, "argv.txt"))
        let firstPackages = argv[5]
        let probe = File.ReadAllText(Path.Combine(capture, "probe.csproj"))
        let config = File.ReadAllText(Path.Combine(capture, "NuGet.Config"))
        test <@ first.ExitCode = 0 @>

        test
            <@
                probe.Contains("Include=\"Example.Package\"")
                && probe.Contains("Version=\"[1.2.3-alpha.4]\"")
            @>

        test
            <@
                config.Contains("<clear />")
                && config.Contains("https://api.nuget.org/v3/index.json")
            @>

        test <@ argv[0] = "restore" && argv[2] = "--configfile" && argv[4] = "--packages" @>
        test <@ argv[6..] = [| "--no-cache"; "--force"; "--verbosity"; "quiet" |] @>

        let second =
            runBarrier (repoRoot ()) fakeDotnet probeParent project "Example.Package" settings

        let secondArgv = File.ReadAllLines(Path.Combine(capture, "argv.txt"))
        test <@ second.ExitCode = 0 && secondArgv[5] <> firstPackages @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``tool package uses an exact isolated tool install instead of PackageReference`` () =
    scratch (fun project fakeDotnet probeParent capture countFile ->
        writeProject project [ "Example.Tool" ] [ "2.3.4-alpha.5" ] true

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Tool"
                [ "FAKE_MODE", "success"
                  "FAKE_CAPTURE_DIR", capture
                  "FAKE_COUNT_FILE", countFile ]

        let argv = File.ReadAllLines(Path.Combine(capture, "argv.txt"))
        let packages = File.ReadAllText(Path.Combine(capture, "nuget-packages.txt"))
        let config = File.ReadAllText(Path.Combine(capture, "NuGet.Config"))
        test <@ result.ExitCode = 0 @>
        test <@ argv[0..2] = [| "tool"; "install"; "Example.Tool" |] @>
        test <@ argv |> Array.contains "--version" @>
        test <@ argv |> Array.contains "2.3.4-alpha.5" @>
        test <@ argv |> Array.contains "--tool-path" @>
        test <@ argv |> Array.contains "--no-cache" @>
        test <@ packages.StartsWith(Path.Combine(probeParent, "testprune-nuget-probe-")) @>

        test
            <@
                config.Contains("<clear />")
                && config.Contains("https://api.nuget.org/v3/index.json")
            @>

        test <@ not (File.Exists(Path.Combine(capture, "probe.csproj"))) @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Theory>]
[<InlineData("package-mismatch")>]
[<InlineData("ambiguous-package")>]
[<InlineData("ambiguous-version")>]
[<InlineData("missing-version")>]
let ``invalid identity or version fails before restore`` caseName =
    scratch (fun project fakeDotnet probeParent _ countFile ->
        match caseName with
        | "package-mismatch" -> writeProject project [ "Other.Package" ] [ "1.0.0" ] false
        | "ambiguous-package" -> writeProject project [ "Example.Package"; "Other.Package" ] [ "1.0.0" ] false
        | "ambiguous-version" -> writeProject project [ "Example.Package" ] [ "1.0.0"; "2.0.0" ] false
        | _ -> writeProject project [ "Example.Package" ] [] false

        let result =
            runBarrier (repoRoot ()) fakeDotnet probeParent project "Example.Package" [ "FAKE_COUNT_FILE", countFile ]

        test <@ result.ExitCode = 1 && not (File.Exists countFile) @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``restore failures exhaust the retry bound and clean up`` () =
    scratch (fun project fakeDotnet probeParent _ countFile ->
        writeProject project [ "Example.Package" ] [ "1.0.0" ] false

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "failure"
                  "FAKE_COUNT_FILE", countFile
                  "TESTPRUNE_NUGET_PROBE_ATTEMPTS", "3" ]

        test <@ result.ExitCode = 1 && File.ReadAllText countFile = "3" @>

        test
            <@
                result.Stderr.Contains("after 3 attempts")
                && result.Stderr.Contains("synthetic restore failure")
            @>

        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``a transient failure retries and succeeds`` () =
    scratch (fun project fakeDotnet probeParent _ countFile ->
        writeProject project [ "Example.Package" ] [ "1.0.0" ] false

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "retry"
                  "FAKE_SUCCEED_AT", "2"
                  "FAKE_COUNT_FILE", countFile
                  "TESTPRUNE_NUGET_PROBE_ATTEMPTS", "3" ]

        test <@ result.ExitCode = 0 && File.ReadAllText countFile = "2" @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``a wedged restore is killed within its bound and cleaned up`` () =
    scratch (fun project fakeDotnet probeParent _ countFile ->
        writeProject project [ "Example.Package" ] [ "1.0.0" ] false

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "timeout"
                  "FAKE_COUNT_FILE", countFile
                  "TESTPRUNE_NUGET_PROBE_PROCESS_TIMEOUT_MS", "100" ]

        test <@ result.ExitCode = 1 && result.Elapsed < TimeSpan.FromSeconds 8. @>
        test <@ result.Stderr.Contains("restore timed out") @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)
