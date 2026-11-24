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
    FsHotWatch.TestRoots.RepositoryTree.root ()

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
    let temp = FsHotWatch.TestRoots.TestRoot.freshPath "testprune-package-floors"

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
let ``an exact one-version range is read as its floor`` () =
    // FsHotWatch's packages pin with `[x]` (the shape NuGet packs an exact PackageReference
    // range as), which the check must read rather than reject as unreadable.
    scratch (fun temp packages ->
        let props = writeProps temp "10.1.402"

        writePackage packages "FsHotWatch" [ "FSharp.Core", "[10.1.402]"; "FSharp.Compiler.Service", "[43.12.402]" ]

        let exitCode, stdout, stderr = runCheck props packages

        test <@ (exitCode, stderr) = (0, "") @>
        test <@ stdout.Contains "FsHotWatch.1.0.0: FSharp.Core >= 10.1.402" @>)

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
    let workflowRunsCheck = workflow.Contains command

    // `mise run ci`'s command is CI's lint step plus the integration suite (CiParityTests), so the
    // check rides in that command rather than in `depends`.
    let ciTask =
        mise.Split('\n')
        |> Array.skipWhile (fun line -> line.Trim() <> "[tasks.ci]")
        |> Array.skip 1
        |> Array.takeWhile (fun line -> not (line.StartsWith "[tasks."))

    let ciRunsCheck =
        ciTask
        |> Array.exists (fun line -> line.TrimStart().StartsWith "run" && line.Contains command)

    test <@ (ciRunsCheck, workflowRunsCheck) = (true, true) @>

// AUTOMATION-1896: mise.toml's [env] pins the SDK's implicit FSharp.Core for every project a
// build in this repository reaches (the optional Falco.UnionRoutes sibling takes the implicit
// one). It must name exactly the central pin, or restore fails with NU1109 again.
[<Fact>]
let ``mise.toml pins the implicit FSharp.Core to exactly the central pin`` () =
    let root = repoRoot ()

    let versionIn (path: string) (pattern: string) =
        let m =
            Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(root, path)), pattern)

        if m.Success then Some m.Groups.[1].Value else None

    let pinned =
        versionIn "Directory.Packages.props" "Include=\"FSharp\\.Core\"\\s+Version=\"([^\"]+)\""

    let implicitVersion =
        versionIn "mise.toml" "(?m)^FSharpCoreImplicitPackageVersion\\s*=\\s*\"([^\"]+)\""

    test <@ pinned.IsSome @>
    test <@ implicitVersion = pinned @>
