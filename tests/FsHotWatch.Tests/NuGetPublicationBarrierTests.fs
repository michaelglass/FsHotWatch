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

let private writeToolProject path packageId version =
    File.WriteAllText(
        path,
        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>%s{packageId}</PackageId>
    <Version>%s{version}</Version>
    <PackAsTool>true</PackAsTool>
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
  printf '%s\n' "$@" > "$FAKE_CAPTURE_DIR/argv.txt"
  printf 'NUGET_PACKAGES=%s\nNUGET_HTTP_CACHE_PATH=%s\nNUGET_FALLBACK_PACKAGES=%s\n' "${NUGET_PACKAGES:-}" "${NUGET_HTTP_CACHE_PATH:-}" "${NUGET_FALLBACK_PACKAGES:-}" > "$FAKE_CAPTURE_DIR/env.txt"
  if [ "$1" = restore ]; then
    cp "$2" "$FAKE_CAPTURE_DIR/probe.csproj"
    cp "$4" "$FAKE_CAPTURE_DIR/NuGet.Config"
  else
    while [ "$#" -gt 0 ]; do
      if [ "$1" = --configfile ]; then cp "$2" "$FAKE_CAPTURE_DIR/NuGet.Config"; break; fi
      shift
    done
  fi
fi
case "${FAKE_MODE:-success}" in
  success) exit 0 ;;
  failure) printf 'synthetic restore failure' >&2; exit 42 ;;
  failure_lock_parent) chmod a-w "$FAKE_LOCK_PARENT"; printf 'synthetic primary failure' >&2; exit 42 ;;
  retry) if [ "$count" -lt "${FAKE_SUCCEED_AT:-2}" ]; then exit 42; else exit 0; fi ;;
  timeout) sleep 30 ;;
  nu1212) printf 'NU1212: Invalid project-package combination' >&2; exit 1 ;;
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

let private runBarrierScript root script dotnetExecutable probeParent project packageId settings =
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true

    [ "fsi"; script; "--"; packageId; project ] |> List.iter start.ArgumentList.Add

    start.Environment["FSHW_NUGET_PROBE_DOTNET"] <- dotnetExecutable
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

let private runBarrier root fakeDotnet probeParent project packageId settings =
    runBarrierScript
        root
        (Path.Combine(root, "scripts", "wait-for-nuget.fsx"))
        fakeDotnet
        probeParent
        project
        packageId
        settings

let private runProcess workingDirectory executable arguments =
    let start = ProcessStartInfo(executable)
    start.WorkingDirectory <- workingDirectory
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    arguments |> List.iter start.ArgumentList.Add
    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult()

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
                  "FAKE_COUNT_FILE", countFile
                  "NUGET_FALLBACK_PACKAGES", "/hostile-fallback" ]

        test <@ result.ExitCode = 0 @>
        let probeProject = File.ReadAllText(Path.Combine(capture, "probe.csproj"))
        let nugetConfig = File.ReadAllText(Path.Combine(capture, "NuGet.Config"))
        let environment = File.ReadAllText(Path.Combine(capture, "env.txt"))
        let argv = File.ReadAllLines(Path.Combine(capture, "argv.txt"))
        test <@ probeProject.Contains("Include=\"Example.Package\"") @>
        test <@ probeProject.Contains("Version=\"[1.2.3-alpha.4]\"") @>
        test <@ nugetConfig.Contains("<clear />") @>
        test <@ nugetConfig.Contains("https://api.nuget.org/v3/index.json") @>
        test <@ not (nugetConfig.Contains("<fallbackPackageFolders>")) @>
        test <@ not (nugetConfig.Contains("fshotwatch-local")) @>
        test <@ not (environment.Contains("/hostile-fallback")) @>
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
                  "FAKE_COUNT_FILE", countFile
                  "NUGET_FALLBACK_PACKAGES", "/hostile-fallback" ]

        let secondArgv = File.ReadAllLines(Path.Combine(capture, "argv.txt"))
        test <@ secondResult.ExitCode = 0 @>
        test <@ secondArgv[5] <> firstPackages @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``PackAsTool uses an exact isolated tool install from only nuget org`` () =
    scratch (fun _ project fakeDotnet probeParent capture countFile ->
        writeToolProject project "Example.Tool" "1.2.3-alpha.4"

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Tool"
                [ "FAKE_MODE", "success"
                  "FAKE_CAPTURE_DIR", capture
                  "FAKE_COUNT_FILE", countFile
                  "NUGET_FALLBACK_PACKAGES", "/hostile-fallback" ]

        test <@ result.ExitCode = 0 @>
        let argv = File.ReadAllLines(Path.Combine(capture, "argv.txt"))
        let nugetConfig = File.ReadAllText(Path.Combine(capture, "NuGet.Config"))
        let environment = File.ReadAllText(Path.Combine(capture, "env.txt"))
        test <@ argv.Length = 10 @>
        test <@ argv[0..2] = [| "tool"; "install"; "Example.Tool" |] @>
        test <@ argv[3..4] = [| "--version"; "1.2.3-alpha.4" |] @>
        test <@ argv[5] = "--tool-path" @>
        test <@ argv[6].StartsWith(Path.Combine(probeParent, "fshw-nuget-probe-")) @>
        test <@ argv[6].EndsWith("/tools") @>
        test <@ argv[7] = "--configfile" @>
        test <@ argv[8].EndsWith("/NuGet.Config") @>
        test <@ argv[9] = "--no-cache" @>
        test <@ nugetConfig.Contains("<clear />") @>
        test <@ not (nugetConfig.Contains("<fallbackPackageFolders>")) @>
        test <@ not (nugetConfig.Contains("fshotwatch-local")) @>
        test <@ environment.Contains($"NUGET_PACKAGES=%s{probeParent}") @>
        test <@ environment.Contains("/packages") @>
        test <@ environment.Contains("/http-cache") @>
        test <@ environment.Contains("NUGET_FALLBACK_PACKAGES=") @>
        test <@ not (environment.Contains("/hostile-fallback")) @>
        test <@ result.Stdout.Contains("tool-installable from nuget.org") @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``a packed tool rejects PackageReference but succeeds through the tool probe`` () =
    withTempDir "fshw-nuget-real-tool" (fun temp ->
        let packageRoot = Path.Combine(temp, "package")
        let feed = Path.Combine(temp, "feed")
        let probes = Path.Combine(temp, "probes")
        Directory.CreateDirectory packageRoot |> ignore
        Directory.CreateDirectory feed |> ignore

        File.WriteAllText(
            Path.Combine(packageRoot, "Example.Tool.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>example-tool</ToolCommandName>
    <PackageId>Example.Tool</PackageId>
    <Version>1.2.3</Version>
  </PropertyGroup>
</Project>
"""
        )

        File.WriteAllText(Path.Combine(packageRoot, "Program.cs"), "System.Console.WriteLine(\"ok\");")

        let packExit, _packOut, _packError =
            runProcess packageRoot "dotnet" [ "pack"; "--output"; feed; "--nologo" ]

        test <@ packExit = 0 @>

        let script = Path.Combine(temp, "wait-for-nuget.fsx")

        let source =
            File.ReadAllText(Path.Combine(repoRoot (), "scripts", "wait-for-nuget.fsx"))

        File.WriteAllText(script, source.Replace("https://api.nuget.org/v3/index.json", Uri(feed).AbsoluteUri))

        let declaredProject = Path.Combine(temp, "Declared.fsproj")
        writeProject declaredProject [ "Example.Tool" ] [ "1.2.3" ]

        let packageReferenceResult =
            runBarrierScript
                (repoRoot ())
                script
                "dotnet"
                probes
                declaredProject
                "Example.Tool"
                [ "FSHW_NUGET_PROBE_PROCESS_TIMEOUT_MS", "30000" ]

        test <@ packageReferenceResult.ExitCode = 1 @>
        test <@ packageReferenceResult.Stderr.Contains("NU1212") @>

        writeToolProject declaredProject "Example.Tool" "1.2.3"

        let toolResult =
            runBarrierScript
                (repoRoot ())
                script
                "dotnet"
                probes
                declaredProject
                "Example.Tool"
                [ "FSHW_NUGET_PROBE_PROCESS_TIMEOUT_MS", "30000" ]

        test <@ toolResult.ExitCode = 0 @>
        test <@ toolResult.Stdout.Contains("tool-installable") @>
        test <@ probeDirectories probes |> Array.isEmpty @>)

[<Fact>]
let ``missing tool version retries to the bound then fails and cleans up`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeToolProject project "Missing.Tool" "9.9.9"

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Missing.Tool"
                [ "FAKE_MODE", "failure"
                  "FAKE_COUNT_FILE", countFile
                  "FSHW_NUGET_PROBE_ATTEMPTS", "3" ]

        test <@ result.ExitCode = 1 @>
        test <@ File.ReadAllText countFile = "3" @>
        test <@ result.Stderr.Contains("after 3 attempts") @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``package-kind mismatch is a probe defect and is not retried as unavailable`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeProject project [ "Example.Package" ] [ "1.0.0" ]

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "nu1212"
                  "FAKE_COUNT_FILE", countFile
                  "FSHW_NUGET_PROBE_ATTEMPTS", "3" ]

        test <@ result.ExitCode = 1 @>
        test <@ File.ReadAllText countFile = "1" @>
        test <@ result.Stderr.Contains("probe defect") @>
        test <@ result.Stderr.Contains("NU1212") @>
        test <@ not (result.Stderr.Contains("unavailable")) @>
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
let ``cleanup failure preserves the primary probe failure and reports both`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeProject project [ "Example.Package" ] [ "1.0.0" ]

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "failure_lock_parent"
                  "FAKE_LOCK_PARENT", probeParent
                  "FAKE_COUNT_FILE", countFile ]

        File.SetUnixFileMode(
            probeParent,
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.UserExecute
            ||| UnixFileMode.GroupRead
            ||| UnixFileMode.GroupExecute
        )

        test <@ result.ExitCode = 1 @>
        test <@ result.Stderr.Contains("synthetic primary failure") @>
        test <@ result.Stderr.Contains("cleanup also failed") @>
        test <@ not (result.Stderr.Contains("could not read release project")) @>)

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

[<Fact>]
let ``a wedged tool install is killed at the process timeout and cleanup still runs`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeToolProject project "Example.Tool" "1.0.0"

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Tool"
                [ "FAKE_MODE", "timeout"
                  "FAKE_COUNT_FILE", countFile
                  "FSHW_NUGET_PROBE_PROCESS_TIMEOUT_MS", "100" ]

        test <@ result.ExitCode = 1 @>
        test <@ result.Elapsed < TimeSpan.FromSeconds 8. @>
        test <@ result.Stderr.Contains("tool install timed out") @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)
