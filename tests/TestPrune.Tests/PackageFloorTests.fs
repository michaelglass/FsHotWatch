module TestPrune.Tests.PackageFloorTests

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open Xunit
open Swensen.Unquote

// scripts/check-package-floors.fsx guards the packed nuspec floors (AUTOMATION-1358): a floated
// FSharp.Core let TestPrune.Trace 0.7.5 ship `FSharp.Core >= 10.1.403`, which every consumer pinned
// at [10.1.402] refused with NU1605. These tests drive the script over synthetic packages so the
// check is shown able to FAIL, and pin its wiring into `mise run ci` and GitHub CI.

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: scripts/check-package-floors.fsx is absent from every ancestor"
        elif File.Exists(Path.Combine(directory.FullName, "scripts", "check-package-floors.fsx")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

let private writeProps directory (fsharpCore: string) =
    let path = Path.Combine(directory, "Directory.Packages.props")

    File.WriteAllText(
        path,
        $"""<Project><ItemGroup>
    <PackageVersion Include="FSharp.Core" Version="%s{fsharpCore}" />
    <PackageVersion Include="FSharp.Compiler.Service" Version="43.12.402" />
</ItemGroup></Project>"""
    )

    path

let private writePackage directory (id: string) (dependencies: (string * string) list) =
    let dependencyXml =
        dependencies
        |> List.map (fun (dependency, version) -> $"""<dependency id="%s{dependency}" version="%s{version}" />""")
        |> String.concat ""

    let nupkg = Path.Combine(directory, $"%s{id}.1.0.0.nupkg")
    use archive = ZipFile.Open(nupkg, ZipArchiveMode.Create)
    let entry = archive.CreateEntry($"%s{id}.nuspec")
    use writer = new StreamWriter(entry.Open())

    writer.Write(
        $"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
<id>%s{id}</id><version>1.0.0</version>
<dependencies><group targetFramework="net10.0">%s{dependencyXml}</group></dependencies>
</metadata></package>"""
    )

let private runCheck propsPath packageDir =
    let root = repoRoot ()
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true

    [ "fsi"
      Path.Combine(root, "scripts", "check-package-floors.fsx")
      "--"
      "check"
      propsPath
      packageDir ]
    |> List.iter start.ArgumentList.Add

    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult()

let private scratch body =
    let temp =
        Path.Combine(Path.GetTempPath(), $"testprune-package-floors-%A{Guid.NewGuid()}")

    let packages = Path.Combine(temp, "packages")
    Directory.CreateDirectory packages |> ignore

    try
        body temp packages
    finally
        Directory.Delete(temp, true)

[<Fact>]
let ``a packed FSharp.Core floor above the pin fails the check`` () =
    scratch (fun temp packages ->
        let props = writeProps temp "10.1.402"
        writePackage packages "TestPrune.Trace" [ "FSharp.Core", "10.1.403" ]

        let exitCode, _, stderr = runCheck props packages

        test <@ exitCode = 1 @>
        test <@ stderr.Contains "TestPrune.Trace.1.0.0 declares FSharp.Core >= 10.1.403, above the pinned 10.1.402" @>)

[<Fact>]
let ``packed floors at or below the pin pass the check`` () =
    scratch (fun temp packages ->
        let props = writeProps temp "10.1.402"

        writePackage
            packages
            "TestPrune.Trace"
            [ "FSharp.Core", "[10.1.402, )"; "FSharp.Compiler.Service", "43.12.402" ]

        writePackage packages "TestPrune.Trace.Recorder" [ "FSharp.Core", "8.0.403" ]

        let exitCode, stdout, stderr = runCheck props packages

        test <@ (exitCode, stderr) = (0, "") @>
        test <@ stdout.Contains "TestPrune.Trace.Recorder.1.0.0: FSharp.Core >= 8.0.403" @>)

[<Fact>]
let ``a floated FSharp.Core pin fails the check`` () =
    scratch (fun temp packages ->
        let props = writeProps temp "10.1.*"
        writePackage packages "TestPrune.Trace" [ "FSharp.Core", "10.1.402" ]

        let exitCode, _, stderr = runCheck props packages

        test <@ exitCode = 1 @>
        test <@ stderr.Contains "FSharp.Core is not pinned" @>)

[<Fact>]
let ``no packages is a failure rather than a vacuous pass`` () =
    scratch (fun temp packages ->
        let props = writeProps temp "10.1.402"

        let exitCode, _, stderr = runCheck props packages

        test <@ exitCode = 1 @>
        test <@ stderr.Contains "no .nupkg files" @>)

[<Fact>]
let ``the package floor check runs in mise ci and GitHub CI`` () =
    let root = repoRoot ()
    let mise = File.ReadAllText(Path.Combine(root, "mise.toml"))

    let workflow =
        File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))

    let command = "dotnet fsi scripts/check-package-floors.fsx -- pack"
    let miseRunsCheck = mise.Contains command
    let workflowRunsCheck = workflow.Contains command

    let ciDependsOnCheck =
        mise.Split('\n')
        |> Array.skipWhile (fun line -> line.Trim() <> "[tasks.ci]")
        |> Array.tryFind (fun line -> line.TrimStart().StartsWith "depends")
        |> Option.exists (fun line -> line.Contains "\"package-floors\"")

    test <@ (miseRunsCheck, workflowRunsCheck, ciDependsOnCheck) = (true, true, true) @>
