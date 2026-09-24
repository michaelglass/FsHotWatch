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

/// A PackAsTool project. `ToolCommandName` is not decoration here: it is the command the
/// barrier will demand the packed tool actually ships and runs, so a tool project without
/// one is refused before any probe.
let private writeToolProject path packageId version commandName =
    File.WriteAllText(
        path,
        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>%s{packageId}</PackageId>
    <Version>%s{version}</Version>
    <PackAsTool>true</PackAsTool>
%s{commandName}
  </PropertyGroup>
</Project>
"""
    )

let private toolCommand name =
    $"    <ToolCommandName>%s{name}</ToolCommandName>"

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

tool_path=""
config_file=""
previous=""
for argument in "$@"; do
  if [ "$previous" = "--tool-path" ]; then tool_path="$argument"; fi
  if [ "$previous" = "--configfile" ]; then config_file="$argument"; fi
  previous="$argument"
done

if [ -n "${FAKE_CAPTURE_DIR:-}" ]; then
  printf '%s\n' "$@" > "$FAKE_CAPTURE_DIR/argv.txt"
  printf 'NUGET_PACKAGES=%s\nNUGET_HTTP_CACHE_PATH=%s\n' "${NUGET_PACKAGES:-}" "${NUGET_HTTP_CACHE_PATH:-}" > "$FAKE_CAPTURE_DIR/env.txt"
  [ -n "$config_file" ] && cp "$config_file" "$FAKE_CAPTURE_DIR/NuGet.Config"
  [ "$1" = restore ] && cp "$2" "$FAKE_CAPTURE_DIR/probe.csproj"
fi

# Materialize whatever `dotnet tool install --tool-path` would have left behind. Each
# knob turns off exactly one thing a real tool package supplies, so a test can break one
# and keep the rest honest.
install_tool() {
  packaged_command="${FAKE_TOOL_COMMAND:-probetool}"
  case "${FAKE_TOOL_SETTINGS:-placed}" in
    placed)    settings_dir="$tool_path/.store/pkg/1.0.0/tools/net10.0/any" ;;
    misplaced) settings_dir="$tool_path/.store/pkg/1.0.0/lib/net10.0" ;;
    none)      settings_dir="" ;;
  esac
  if [ -n "$settings_dir" ]; then
    mkdir -p "$settings_dir"
    printf '<DotNetCliTool Version="1"><Commands><Command Name="%s" EntryPoint="Packed.dll" Runner="dotnet" /></Commands></DotNetCliTool>' \
      "$packaged_command" > "$settings_dir/DotnetToolSettings.xml"
    if [ "${FAKE_TOOL_ENTRY:-yes}" = yes ]; then : > "$settings_dir/Packed.dll"; fi
  fi
  if [ "${FAKE_TOOL_SHIM:-yes}" != no ]; then
    mkdir -p "$tool_path"
    printf '#!/bin/sh\nsleep %s\nexit %s\n' "${FAKE_TOOL_RUN_SLEEP:-0}" "${FAKE_TOOL_RUN_EXIT:-0}" > "$tool_path/$packaged_command"
    if [ "${FAKE_TOOL_SHIM:-yes}" = nonexec ]; then
      chmod 644 "$tool_path/$packaged_command"
    else
      chmod 755 "$tool_path/$packaged_command"
    fi
  fi
}

case "${FAKE_MODE:-success}" in
  success) if [ "$1" = tool ]; then install_tool; fi; exit 0 ;;
  failure) printf 'synthetic restore failure' >&2; exit 42 ;;
  retry) if [ "$count" -lt "${FAKE_SUCCEED_AT:-2}" ]; then exit 42; else if [ "$1" = tool ]; then install_tool; fi; exit 0; fi ;;
  timeout) sleep 120 ;;
  nu1212) printf 'NU1212: Invalid project-package combination' >&2; exit 1 ;;
  settings-rejected) printf "The settings file in the tool's NuGet package is invalid: Entry point file 'Packed.dll' was not found in the package." >&2; exit 1 ;;
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

let private barrierStart root fakeDotnet probeParent project packageId settings =
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

    // An EMPTY value means "remove the harness's override and let the script's own
    // default answer". Every default above is set here for speed, which also makes the
    // real defaults — the ones a release actually runs with — unreachable from a test:
    // a budget reverted in the script stays green because no test ever sees it.
    for key, value in settings do
        if value = "" then
            start.Environment.Remove(key: string) |> ignore
        else
            start.Environment[key] <- value

    start

let private runBarrier root fakeDotnet probeParent project packageId settings =
    let start = barrierStart root fakeDotnet probeParent project packageId settings
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

/// Reads the barrier's output as it is produced and returns the first line matching
/// `matching`, killing the child instead of waiting it out.
///
/// This exists for one claim that cannot be made any other way: the DEFAULT delay
/// between attempts. Observing it by running with it means sleeping it, and fifteen
/// seconds of nothing to read one constant is not a trade this suite should make. The
/// retry notice is printed BEFORE the delay it announces, so reading that line proves
/// the value the run was about to sleep without sleeping it.
let private barrierLineMatching root fakeDotnet probeParent project packageId settings matching =
    let start = barrierStart root fakeDotnet probeParent project packageId settings
    use child = Process.Start start
    // Drained, never read: an undrained stderr pipe can fill and wedge the child before
    // it reaches the line under test.
    child.StandardError.ReadToEndAsync() |> ignore

    let rec readUntil () =
        match child.StandardOutput.ReadLine() with
        | null -> None
        | line when matching line -> Some line
        | _ -> readUntil ()

    let found = readUntil ()

    if not child.HasExited then
        child.Kill true

    child.WaitForExit()
    found

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
        // The give-up line reports MEASURED wall-clock, not the configured budget, and
        // says how to read it — index lag after a green Release run resumes from here.
        test <@ result.Stderr.Contains("of measured wall-clock time") @>
        test <@ result.Stderr.Contains("most likely index lag") @>
        test <@ result.Stderr.Contains("re-run `mise run release`") @>
        test <@ result.Stderr.Contains("synthetic restore failure") @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``the attempt budget defaults to the twenty minutes an index lag needs`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeProject project [ "Example.Package" ] [ "1.0.0" ]

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                // The one setting this test does NOT supply. Every other test pins the
                // attempts so it finishes quickly, which left the shipped default — the
                // only value a release ever runs with — asserted by nothing: shrinking it
                // back to the five minutes that failed closed on every ordinary release
                // would have kept the suite green.
                [ "FAKE_MODE", "failure"
                  "FAKE_COUNT_FILE", countFile
                  "FSHW_NUGET_PROBE_ATTEMPTS", "" ]

        test <@ result.ExitCode = 1 @>
        // 80 attempts at the 15s default is 20 minutes, which is what the observed index
        // lag after a green Release run costs. The probe count is the claim; the give-up
        // line is how a human reads it.
        test <@ File.ReadAllText countFile = "80" @>
        test <@ result.Stderr.Contains("after 80 attempts") @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``the delay between attempts defaults to the fifteen seconds that make that budget`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeProject project [ "Example.Package" ] [ "1.0.0" ]

        let line =
            barrierLineMatching
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                // The budget is attempts TIMES delay. Pinning the attempts alone would
                // leave half of the twenty minutes guarded by nothing: a delay quietly
                // cut to a second keeps 80 attempts and buys 80 seconds. Two attempts is
                // the fewest that announces a delay at all.
                [ "FAKE_MODE", "failure"
                  "FAKE_COUNT_FILE", countFile
                  "FSHW_NUGET_PROBE_ATTEMPTS", "2"
                  "FSHW_NUGET_PROBE_DELAY_MS", "" ]
                (fun line -> line.Contains "retrying in")

        test
            <@
                line = Some
                    "NuGet publication barrier: Example.Package 1.0.0 unavailable (attempt 1/2); retrying in 15000ms"
            @>)

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

        // The control: the same script, the same box, moments before. Its elapsed time is
        // almost entirely `dotnet fsi` compiling the barrier, which is the cost the wedged
        // run below shares and the one that moves with machine load.
        let control =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Package"
                [ "FAKE_MODE", "success"; "FAKE_COUNT_FILE", countFile ]

        test <@ control.ExitCode = 0 @>

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
        // The claim is "the wedged child was killed at the timeout, not waited out" — the
        // fake sleeps 120 s and the probe's own budget is 100 ms. Measured against the
        // control rather than the clock: a waited-out child adds the full 120 s over the
        // control whatever the box is doing, while the compile that dominates both runs
        // cancels out. An absolute bound here is a bound on that compile, and goes red on
        // machine load rather than on behaviour.
        test <@ result.Elapsed < control.Elapsed + TimeSpan.FromSeconds 30. @>
        test <@ result.Stderr.Contains("restore timed out") @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

// ---------------------------------------------------------------------------
// PackAsTool packages.
//
// A library is proven published by a restore. A tool is not: the release that
// exposed this shipped FsHotWatch.Cli 0.14.0-alpha.30 to nuget.org, the barrier
// probed it as a PackageReference, NuGet answered NU1212 — "DotnetToolReference
// project style can only contain references of the DotnetTool type" — and the
// barrier retried that twenty times and called a successful publication a failure.
//
// What replaces it is not "install instead of restore". `dotnet tool install`
// exiting 0 proves bytes arrived; it does not prove the command the project
// PROMISES is the command the package ships, that its entry assembly is there, or
// that the thing runs. Every test below breaks exactly one of those and asserts the
// refusal names it — and that a defect is refused ONCE, because retrying a fact that
// cannot change is how the false negative got its twenty attempts.
// ---------------------------------------------------------------------------

[<Fact>]
let ``a tool package is probed by installing the exact version into a private tool path`` () =
    scratch (fun _ project fakeDotnet probeParent capture countFile ->
        writeToolProject project "Example.Tool" "1.2.3-alpha.4" (toolCommand "probetool")

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

        test <@ result.ExitCode = 0 @>
        let argv = File.ReadAllLines(Path.Combine(capture, "argv.txt"))
        let nugetConfig = File.ReadAllText(Path.Combine(capture, "NuGet.Config"))
        let environment = File.ReadAllText(Path.Combine(capture, "env.txt"))
        test <@ argv[0..2] = [| "tool"; "install"; "Example.Tool" |] @>
        test <@ argv[3..4] = [| "--version"; "1.2.3-alpha.4" |] @>
        test <@ argv[5] = "--tool-path" @>
        test <@ argv[6].StartsWith(Path.Combine(probeParent, "fshw-nuget-probe-")) @>
        test <@ argv[7] = "--configfile" @>
        test <@ argv[9] = "--no-cache" @>
        test <@ Path.GetDirectoryName argv[6] = Path.GetDirectoryName argv[8] @>
        // The claim is "resolvable from the PUBLIC feed", so the probe must not be able to
        // pass on a local overlay, an already-warm global package folder, or an HTTP cache.
        test <@ nugetConfig.Contains("<clear />") @>
        test <@ nugetConfig.Contains("https://api.nuget.org/v3/index.json") @>
        test <@ not (nugetConfig.Contains("fshotwatch-local")) @>
        let probePrefix = Path.Combine(probeParent, "fshw-nuget-probe-")
        test <@ environment.Contains($"NUGET_PACKAGES=%s{probePrefix}") @>
        test <@ environment.Contains("/http-cache") @>
        test <@ result.Stdout.Contains("is installable and runnable from") @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``a tool package that ships a different command than it declares is refused by name`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeToolProject project "Example.Tool" "1.0.0" (toolCommand "probetool")

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Tool"
                [ "FAKE_MODE", "success"
                  "FAKE_TOOL_COMMAND", "somethingelse"
                  "FAKE_COUNT_FILE", countFile
                  "FSHW_NUGET_PROBE_ATTEMPTS", "3" ]

        test <@ result.ExitCode = 1 @>
        test <@ result.Stderr.Contains("tool package defect") @>
        test <@ result.Stderr.Contains("no command shim was created at") @>
        test <@ result.Stderr.Contains("probetool") @>
        // Installed-but-wrong is terminal. One attempt, and the words "attempts" never appear.
        test <@ File.ReadAllText countFile = "1" @>
        test <@ not (result.Stderr.Contains("attempts")) @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Theory>]
[<InlineData("shim-not-executable", "is not executable (mode")>]
[<InlineData("no-tool-settings", "contains no DotnetToolSettings.xml anywhere under")>]
[<InlineData("misplaced-tool-settings", "not under tools/<tfm>/any/")>]
[<InlineData("missing-entry-point", "the entry assembly for 'probetool' is absent at")>]
[<InlineData("command-exits-non-zero", "failed — exit 3")>]
[<InlineData("command-hangs", "did not finish within the probe timeout")>]
let ``an installed tool that cannot run is refused with the artifact that was missing`` caseName expected =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeToolProject project "Example.Tool" "1.0.0" (toolCommand "probetool")

        let knobs =
            match caseName with
            | "shim-not-executable" -> [ "FAKE_TOOL_SHIM", "nonexec" ]
            | "no-tool-settings" -> [ "FAKE_TOOL_SETTINGS", "none" ]
            | "misplaced-tool-settings" -> [ "FAKE_TOOL_SETTINGS", "misplaced" ]
            | "missing-entry-point" -> [ "FAKE_TOOL_ENTRY", "no" ]
            | "command-exits-non-zero" -> [ "FAKE_TOOL_RUN_EXIT", "3" ]
            // The budget bounds every child the probe spawns, the tool install included, so
            // it has to leave that install room to finish — a budget tight enough to trip
            // the install turns this into the retryable-unavailable case instead.
            | _ -> [ "FAKE_TOOL_RUN_SLEEP", "120"; "FSHW_NUGET_PROBE_PROCESS_TIMEOUT_MS", "4000" ]

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Tool"
                ([ "FAKE_MODE", "success"
                   "FAKE_COUNT_FILE", countFile
                   "FSHW_NUGET_PROBE_ATTEMPTS", "3" ]
                 @ knobs)

        test <@ result.ExitCode = 1 @>
        test <@ result.Stderr.Contains("tool package defect") @>
        test <@ result.Stderr.Contains(expected: string) @>
        test <@ File.ReadAllText countFile = "1" @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``the SDK refusing a packaged tool's settings is a defect, not a publication delay`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeToolProject project "Example.Tool" "1.0.0" (toolCommand "probetool")

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Tool"
                [ "FAKE_MODE", "settings-rejected"
                  "FAKE_COUNT_FILE", countFile
                  "FSHW_NUGET_PROBE_ATTEMPTS", "3" ]

        test <@ result.ExitCode = 1 @>
        test <@ result.Stderr.Contains("refused its packaged tool settings") @>
        test <@ result.Stderr.Contains("Entry point file") @>
        test <@ File.ReadAllText countFile = "1" @>
        test <@ not (result.Stderr.Contains("still not restorable")) @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``a tool version that is not on the feed retries to the bound then fails`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeToolProject project "Missing.Tool" "9.9.9" (toolCommand "probetool")

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
        test <@ not (result.Stderr.Contains("tool package defect")) @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``a tool project that declares no ToolCommandName is refused before any probe`` () =
    scratch (fun _ project fakeDotnet probeParent _ countFile ->
        writeToolProject project "Example.Tool" "1.0.0" ""

        let result =
            runBarrier
                (repoRoot ())
                fakeDotnet
                probeParent
                project
                "Example.Tool"
                [ "FAKE_MODE", "success"; "FAKE_COUNT_FILE", countFile ]

        test <@ result.ExitCode = 1 @>
        test <@ result.Stderr.Contains("has no readable <ToolCommandName>") @>
        test <@ not (File.Exists countFile) @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)

[<Fact>]
let ``NU1212 is reported as a probe-shape defect rather than retried as unavailability`` () =
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
        test <@ result.Stderr.Contains("probe defect") @>
        test <@ result.Stderr.Contains("NU1212") @>
        test <@ File.ReadAllText countFile = "1" @>
        test <@ not (result.Stderr.Contains("still not restorable")) @>
        test <@ probeDirectories probeParent |> Array.isEmpty @>)
