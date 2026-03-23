module TestPrune.Tests.TestRunnerTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.TestRunner

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"test-prune-%A{Guid.NewGuid()}")

    Directory.CreateDirectory(dir) |> ignore

    try
        f dir
    finally
        if Directory.Exists dir then
            Directory.Delete(dir, true)

module ``findTestDll`` =

    [<Fact>]
    let ``constructs correct DLL path from project path`` () =
        let projectPath = "/repo/tests/MyTests/MyTests.fsproj"
        let result = findTestDll projectPath

        let expected =
            Path.Combine("/repo/tests/MyTests", "bin", "Debug", defaultTfm, "MyTests.dll")

        test <@ result = expected @>

module ``discoverTestProjects`` =

    [<Fact>]
    let ``finds fsproj with xunit reference and skips ones without`` () =
        withTempDir (fun root ->
            let testsDir = Path.Combine(root, "tests")
            let projWithXunit = Path.Combine(testsDir, "WithXunit")
            let projWithout = Path.Combine(testsDir, "WithoutXunit")
            Directory.CreateDirectory(projWithXunit) |> ignore
            Directory.CreateDirectory(projWithout) |> ignore

            File.WriteAllText(
                Path.Combine(projWithXunit, "WithXunit.fsproj"),
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="3.2.2" />
  </ItemGroup>
</Project>"""
            )

            File.WriteAllText(
                Path.Combine(projWithout, "WithoutXunit.fsproj"),
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
  </ItemGroup>
</Project>"""
            )

            let result = discoverTestProjects root
            test <@ result.Length = 1 @>
            test <@ result[0].EndsWith("WithXunit.fsproj") @>)

    [<Fact>]
    let ``finds fsproj with case-insensitive xunit reference`` () =
        withTempDir (fun root ->
            let testsDir = Path.Combine(root, "tests", "MyTests")
            Directory.CreateDirectory(testsDir) |> ignore

            File.WriteAllText(
                Path.Combine(testsDir, "MyTests.fsproj"),
                """<Project><ItemGroup><PackageReference Include="XUNIT.v3" /></ItemGroup></Project>"""
            )

            let projects = discoverTestProjects root |> List.map Path.GetFileName
            test <@ projects = [ "MyTests.fsproj" ] @>)

    [<Fact>]
    let ``handles unreadable fsproj gracefully`` () =
        withTempDir (fun root ->
            let testsDir = Path.Combine(root, "tests", "BadProj")
            Directory.CreateDirectory(testsDir) |> ignore
            let fsprojPath = Path.Combine(testsDir, "BadProj.fsproj")

            File.WriteAllText(
                fsprojPath,
                """<Project><ItemGroup><PackageReference Include="xunit" /></ItemGroup></Project>"""
            )
            // Remove read permissions so File.ReadAllText throws
            let psi = System.Diagnostics.ProcessStartInfo("chmod", $"000 \"{fsprojPath}\"")
            psi.UseShellExecute <- false
            let proc = System.Diagnostics.Process.Start(psi)
            proc.WaitForExit()

            try
                let result = discoverTestProjects root
                test <@ result |> List.isEmpty @>
            finally
                // Restore permissions so cleanup can delete the file
                let psi2 = System.Diagnostics.ProcessStartInfo("chmod", $"644 \"{fsprojPath}\"")
                psi2.UseShellExecute <- false
                let proc2 = System.Diagnostics.Process.Start(psi2)
                proc2.WaitForExit())

    [<Fact>]
    let ``returns empty when tests dir does not exist`` () =
        withTempDir (fun root ->
            let result = discoverTestProjects root
            test <@ result |> List.isEmpty @>)

module ``buildFilterArgs`` =

    [<Fact>]
    let ``single class produces one filter-class flag`` () =
        let result = buildFilterArgs [ "MyNamespace.MyClass" ]
        test <@ result = "--filter-class \"MyNamespace.MyClass\"" @>

    [<Fact>]
    let ``multiple classes produces space-separated flags`` () =
        let result = buildFilterArgs [ "Ns.A"; "Ns.B" ]
        test <@ result = "--filter-class \"Ns.A\" --filter-class \"Ns.B\"" @>

    [<Fact>]
    let ``empty list produces empty string`` () =
        let result = buildFilterArgs []
        test <@ result = "" @>

module ``normalizeExitCode`` =

    [<Fact>]
    let ``maps exit code 8 to 0`` () = test <@ normalizeExitCode 8 = 0 @>

    [<Fact>]
    let ``preserves exit code 0`` () = test <@ normalizeExitCode 0 = 0 @>

    [<Fact>]
    let ``preserves exit code 1`` () = test <@ normalizeExitCode 1 = 1 @>

module ``runAllTestsWith`` =

    [<Fact>]
    let ``passes correct arguments to runner`` () =
        let mutable capturedFileName = ""
        let mutable capturedArgs = ""

        let fakeRunner (fileName: string) (args: string) : TestResult =
            capturedFileName <- fileName
            capturedArgs <- args
            { ExitCode = 0; Output = "ok" }

        let result = runAllTestsWith fakeRunner "/path/to/Tests.dll"

        test <@ capturedFileName = "dotnet" @>
        test <@ capturedArgs = "exec \"/path/to/Tests.dll\"" @>
        test <@ result.ExitCode = 0 @>
        test <@ result.Output = "ok" @>

module ``runFilteredTestsWith`` =

    [<Fact>]
    let ``normalizes exit code 8 to 0`` () =
        let fakeRunner (_: string) (_: string) : TestResult =
            { ExitCode = 8
              Output = "no tests matched" }

        let result = runFilteredTestsWith fakeRunner "/path/to/Tests.dll" [ "Ns.MyClass" ]
        test <@ result.ExitCode = 0 @>

    [<Fact>]
    let ``preserves non-8 exit codes`` () =
        let fakeRunner (_: string) (_: string) : TestResult = { ExitCode = 1; Output = "failure" }

        let result = runFilteredTestsWith fakeRunner "/path/to/Tests.dll" [ "Ns.MyClass" ]
        test <@ result.ExitCode = 1 @>

    [<Fact>]
    let ``includes filter args in command`` () =
        let mutable capturedArgs = ""

        let fakeRunner (_: string) (args: string) : TestResult =
            capturedArgs <- args
            { ExitCode = 0; Output = "" }

        runFilteredTestsWith fakeRunner "/path/to/Tests.dll" [ "Ns.A"; "Ns.B" ]
        |> ignore

        test <@ capturedArgs.Contains("--filter-class \"Ns.A\"") @>
        test <@ capturedArgs.Contains("--filter-class \"Ns.B\"") @>
