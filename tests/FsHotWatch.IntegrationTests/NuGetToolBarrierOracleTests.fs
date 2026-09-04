module FsHotWatch.Tests.NuGetToolBarrierOracleTests

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open Xunit
open Swensen.Unquote

// The tool-aware publication barrier, proved against a REAL packed tool instead of a
// fake `dotnet` (AUTOMATION-602). The unit suite pins the barrier's decisions given a
// scripted SDK; only this file pins that the decisions are about a `.nupkg` the SDK
// itself produced — that `dotnet pack` of a PackAsTool project yields something the
// barrier accepts, and that a package broken in a way NuGet is perfectly happy to serve
// is refused. A local feed made from the packed output is the only honest oracle for
// that: nuget.org will not host a deliberately broken tool for us to probe.
//
// It lives in the integration suite for the reasons `.fshw.json` gives: it runs a real
// `dotnet pack` and a real `dotnet tool install` as child processes, which is exactly
// the nested-MSBuild load the gated inner loop excludes.

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: scripts/wait-for-nuget.fsx is absent from every ancestor"
        elif File.Exists(Path.Combine(directory.FullName, "scripts", "wait-for-nuget.fsx")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

type private ProcessResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

let private run executable (arguments: string list) (environment: (string * string) list) =
    let start = ProcessStartInfo(executable: string)
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    arguments |> List.iter start.ArgumentList.Add

    for key, value in environment do
        start.Environment[key] <- value

    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()

    { ExitCode = child.ExitCode
      Stdout = stdout.GetAwaiter().GetResult()
      Stderr = stderr.GetAwaiter().GetResult() }

let private toolProject =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>fshwprobetool</ToolCommandName>
    <PackageId>FsHotWatch.ProbeTool</PackageId>
    <Version>1.0.0</Version>
    <Authors>FsHotWatch</Authors>
    <Description>Throwaway tool packed by the publication-barrier oracle test.</Description>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.1.*" />
  </ItemGroup>
</Project>
"""

/// `dotnet pack` is the expensive part, so it happens once for the whole file. `lazy` is
/// thread-safe by default, and the scratch tree is removed when the test host exits.
let private packed =
    lazy
        (let identity = Guid.NewGuid().ToString "N"
         let root = Path.Combine(Path.GetTempPath(), $"fshw-tool-oracle-%s{identity}")

         let source = Path.Combine(root, "src")
         let feed = Path.Combine(root, "feed")
         Directory.CreateDirectory source |> ignore

         AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
             try
                 if Directory.Exists root then
                     Directory.Delete(root, true)
             with _ ->
                 ())

         let project = Path.Combine(source, "FsHotWatch.ProbeTool.fsproj")
         File.WriteAllText(project, toolProject)

         File.WriteAllText(
             Path.Combine(source, "Program.fs"),
             """[<EntryPoint>]
let main _ =
    printfn "fshwprobetool 1.0.0"
    0
"""
         )

         let pack = run "dotnet" [ "pack"; project; "-c"; "Release"; "-o"; feed ] []

         if pack.ExitCode <> 0 then
             failwith $"packing the oracle tool failed (%d{pack.ExitCode}): %s{pack.Stdout} %s{pack.Stderr}"

         root, project, feed)

/// Rebuild the packed `.nupkg` into a sibling feed, passing every entry through
/// `transform` — returning `None` drops it. This is how a package that NuGet will serve
/// happily but that cannot produce a working command gets made: no `dotnet pack` switch
/// produces one, because the SDK is trying not to.
let private variantFeed name transform =
    let root, _, feed = packed.Value
    let variant = Path.Combine(root, name)
    Directory.CreateDirectory variant |> ignore

    let source = Directory.GetFiles(feed, "*.nupkg") |> Array.exactlyOne
    let destination = Path.Combine(variant, Path.GetFileName source)

    if not (File.Exists destination) then
        use input = ZipFile.OpenRead source
        use file = File.Create destination
        use output = new ZipArchive(file, ZipArchiveMode.Create)

        for entry in input.Entries do
            use buffer = new MemoryStream()

            (use stream = entry.Open()
             stream.CopyTo buffer)

            match transform entry.FullName (buffer.ToArray()) with
            | None -> ()
            | Some(entryName, (data: byte array)) ->
                let created = output.CreateEntry entryName
                use target = created.Open()
                target.Write(data, 0, data.Length)

    variant

let private probe feedDirectory =
    let _, project, _ = packed.Value
    let root = repoRoot ()

    let identity = Guid.NewGuid().ToString "N"

    let probeParent =
        Path.Combine(Path.GetTempPath(), $"fshw-tool-oracle-probes-%s{identity}")

    try
        let result =
            run
                "dotnet"
                [ "fsi"
                  Path.Combine(root, "scripts", "wait-for-nuget.fsx")
                  "--"
                  "FsHotWatch.ProbeTool"
                  project ]
                [ "FSHW_NUGET_PROBE_SOURCE", (feedDirectory: string)
                  "FSHW_NUGET_PROBE_PARENT", probeParent
                  "FSHW_NUGET_PROBE_ATTEMPTS", "1"
                  "FSHW_NUGET_PROBE_DELAY_MS", "1" ]

        // Every probe deletes its own scratch tree, whatever it concluded.
        test
            <@
                not (Directory.Exists probeParent)
                || Directory.GetDirectories probeParent |> Array.isEmpty
            @>

        result
    finally
        try
            if Directory.Exists probeParent then
                Directory.Delete(probeParent, true)
        with _ ->
            ()

[<Fact(Timeout = 600000)>]
let ``a genuinely packed tool passes the barrier`` () =
    let _, _, feed = packed.Value
    let result = probe feed

    if result.ExitCode <> 0 then
        Assert.Fail $"the real packed tool was refused: %s{result.Stdout} %s{result.Stderr}"

    test <@ result.Stdout.Contains("FsHotWatch.ProbeTool 1.0.0 is installable and runnable") @>

[<Fact(Timeout = 600000)>]
let ``a tool package whose packaged command was renamed is refused`` () =
    // NuGet serves this without complaint and `dotnet tool install` exits 0 — it just
    // installs a command nobody asked for. Only checking the DECLARED command catches it.
    let feed =
        variantFeed "feed-renamed-command" (fun name data ->
            if name.EndsWith("DotnetToolSettings.xml", StringComparison.Ordinal) then
                let rewritten =
                    Text.Encoding.UTF8
                        .GetString(data: byte array)
                        .Replace("Name=\"fshwprobetool\"", "Name=\"someothercommand\"")

                Some(name, Text.Encoding.UTF8.GetBytes rewritten)
            else
                Some(name, data))

    let result = probe feed

    test <@ result.ExitCode = 1 @>
    test <@ result.Stderr.Contains("tool package defect") @>
    test <@ result.Stderr.Contains("no command shim was created at") @>
    test <@ result.Stderr.Contains("fshwprobetool") @>

[<Fact(Timeout = 600000)>]
let ``a tool package missing its entry assembly is refused as a defect, not as unavailable`` () =
    let feed =
        variantFeed "feed-missing-entry" (fun name data ->
            if name.EndsWith("/FsHotWatch.ProbeTool.dll", StringComparison.Ordinal) then
                None
            else
                Some(name, data))

    let result = probe feed

    test <@ result.ExitCode = 1 @>
    test <@ result.Stderr.Contains("tool package defect") @>
    // The distinction the ticket turns on: the package is THERE and broken. Reporting it
    // as "still not restorable after N attempts" is the false negative being fixed.
    test <@ not (result.Stderr.Contains("still not restorable")) @>
