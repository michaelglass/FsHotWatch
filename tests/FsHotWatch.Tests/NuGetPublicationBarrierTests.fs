module FsHotWatch.Tests.NuGetPublicationBarrierTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers

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

let private writeProject path packageIds versions =
    let elements name values =
        values
        |> List.map (fun value -> $"    <%s{name}>%s{value}</%s{name}>")
        |> String.concat "\n"

    File.WriteAllText(
        path,
        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
%s{elements "PackageId" packageIds}
%s{elements "Version" versions}
  </PropertyGroup>
</Project>
"""
    )

let private writeFakeDotnet path =
    File.WriteAllText(
        path,
        """#!/bin/sh
set -eu
count_file="$FAKE_COUNT_FILE"
count=0
if [ -f "$count_file" ]; then count=$(cat "$count_file"); fi
count=$((count + 1))
printf '%s' "$count" > "$count_file"
if [ -n "${FAKE_CAPTURE_DIR:-}" ]; then
  cp "$2" "$FAKE_CAPTURE_DIR/probe.csproj"
  cp "$4" "$FAKE_CAPTURE_DIR/NuGet.Config"
  printf '%s\n' "$@" > "$FAKE_CAPTURE_DIR/argv.txt"
fi
case "${FAKE_MODE:-success}" in
  success) exit 0 ;;
  failure) printf 'synthetic restore failure' >&2; exit 42 ;;
  retry) if [ "$count" -lt "${FAKE_SUCCEED_AT:-2}" ]; then exit 42; else exit 0; fi ;;
  timeout) sleep 30 ;;
  *) exit 99 ;;
esac
"""
    )

    File.SetUnixFileMode(
        path,
        UnixFileMode.UserRead
        ||| UnixFileMode.UserWrite
        ||| UnixFileMode.UserExecute
        ||| UnixFileMode.GroupRead
        ||| UnixFileMode.GroupExecute
    )

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

    start.Environment["FSHW_NUGET_PROBE_DOTNET"] <- fakeDotnet
    start.Environment["FSHW_NUGET_PROBE_PARENT"] <- probeParent
    start.Environment["FSHW_NUGET_PROBE_ATTEMPTS"] <- "1"
    start.Environment["FSHW_NUGET_PROBE_DELAY_MS"] <- "1"
    start.Environment["FSHW_NUGET_PROBE_PROCESS_TIMEOUT_MS"] <- "1000"

    for key, value in settings do
        start.Environment[key] <- value

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
    withTempDir "fshw-nuget-barrier" (fun temp ->
        let project = Path.Combine(temp, "Package.fsproj")
        let fakeDotnet = Path.Combine(temp, "fake-dotnet")
        let probeParent = Path.Combine(temp, "probes")
        let capture = Path.Combine(temp, "capture")
        let countFile = Path.Combine(temp, "count")
        Directory.CreateDirectory capture |> ignore
        writeFakeDotnet fakeDotnet
        body temp project fakeDotnet probeParent capture countFile)

let private probeDirectories probeParent =
    if Directory.Exists probeParent then
        Directory.GetDirectories probeParent
    else
        [||]

[<Fact>]
let ``success restores an exact version from only nuget org and cleans its probe`` () =
    scratch (fun _ project fakeDotnet probeParent capture countFile ->
        writeProject project [ "Example.Package" ] [ "1.2.3-alpha.4" ]

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "success"
                  "FAKE_CAPTURE_DIR", capture
                  "FAKE_COUNT_FILE", countFile ]

        test <@ result.ExitCode = 0 @>
        let probeProject = File.ReadAllText(Path.Combine(capture, "probe.csproj"))
        let nugetConfig = File.ReadAllText(Path.Combine(capture, "NuGet.Config"))
        let argv = File.ReadAllLines(Path.Combine(capture, "argv.txt"))
        test <@ probeProject.Contains("Include=\"Example.Package\"") @>
        test <@ probeProject.Contains("Version=\"[1.2.3-alpha.4]\"") @>
        test <@ nugetConfig.Contains("<clear />") @>
        test <@ nugetConfig.Contains("https://api.nuget.org/v3/index.json") @>
        test <@ not (nugetConfig.Contains("fshotwatch-local")) @>
        test <@ argv.Length = 10 @>
        test <@ argv[0] = "restore" @>
        test <@ argv[1].EndsWith("/probe.csproj") @>
        test <@ argv[2] = "--configfile" @>
        test <@ argv[3].EndsWith("/NuGet.Config") @>
        test <@ argv[4] = "--packages" @>
        test <@ argv[5].EndsWith("/packages") @>
        test <@ argv[5].StartsWith(Path.Combine(probeParent, "fshw-nuget-probe-")) @>
        test <@ argv[6..] = [| "--no-cache"; "--force"; "--verbosity"; "quiet" |] @>
        test <@ Path.GetDirectoryName(argv[1]) = Path.GetDirectoryName(argv[3]) @>
        test <@ Path.GetDirectoryName(argv[1]) = Path.GetDirectoryName(argv[5]) @>

        let firstPackages = argv[5]

        let secondResult =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "success"
                  "FAKE_CAPTURE_DIR", capture
                  "FAKE_COUNT_FILE", countFile ]

        let secondArgv = File.ReadAllLines(Path.Combine(capture, "argv.txt"))
        test <@ secondResult.ExitCode = 0 @>
        test <@ secondArgv[5] <> firstPackages @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Theory>]
[<InlineData("package-mismatch")>]
[<InlineData("ambiguous-package")>]
[<InlineData("ambiguous-version")>]
[<InlineData("missing-version")>]
let ``invalid project identity or version fails before restore and leaves no probe`` caseName =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        match caseName with
        | "package-mismatch" -> writeProject project [ "Other.Package" ] [ "1.0.0" ]
        | "ambiguous-package" -> writeProject project [ "Example.Package"; "Other.Package" ] [ "1.0.0" ]
        | "ambiguous-version" -> writeProject project [ "Example.Package" ] [ "1.0.0"; "2.0.0" ]
        | _ -> writeProject project [ "Example.Package" ] []

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "success"; "FAKE_COUNT_FILE", countFile ]

        test <@ result.ExitCode = 1 @>
        test <@ not (File.Exists countFile) @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``restore failures retry to the bound then fail and clean up`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeProject project [ "Example.Package" ] [ "1.0.0" ]

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "failure"
                  "FAKE_COUNT_FILE", countFile
                  "FSHW_NUGET_PROBE_ATTEMPTS", "3" ]

        test <@ result.ExitCode = 1 @>
        test <@ File.ReadAllText countFile = "3" @>
        test <@ result.Stderr.Contains("after 3 attempts") @>
        test <@ result.Stderr.Contains("synthetic restore failure") @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``a transient restore failure retries and can succeed`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeProject project [ "Example.Package" ] [ "1.0.0" ]

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
                  "FSHW_NUGET_PROBE_ATTEMPTS", "3" ]

        test <@ result.ExitCode = 0 @>
        test <@ File.ReadAllText countFile = "2" @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``a wedged restore is killed at the process timeout and cleanup still runs`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeProject project [ "Example.Package" ] [ "1.0.0" ]

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "timeout"
                  "FAKE_COUNT_FILE", countFile
                  "FSHW_NUGET_PROBE_PROCESS_TIMEOUT_MS", "100" ]

        test <@ result.ExitCode = 1 @>
        test <@ result.Elapsed < TimeSpan.FromSeconds 8. @>
        test <@ result.Stderr.Contains("restore timed out") @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)
