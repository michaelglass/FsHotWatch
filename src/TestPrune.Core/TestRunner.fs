module TestPrune.TestRunner

open System
open System.Diagnostics
open System.IO

type TestResult = { ExitCode: int; Output: string }

/// Default target framework moniker — update when upgrading .NET SDK.
let defaultTfm = "net10.0"

/// Find the test DLL path for a given test project.
let findTestDll (projectPath: string) : string =
    let projDir = Path.GetDirectoryName(projectPath)
    let projName = Path.GetFileNameWithoutExtension(projectPath)
    Path.Combine(projDir, "bin", "Debug", defaultTfm, $"%s{projName}.dll")

let private runProcess (fileName: string) (arguments: string) : TestResult =
    let psi = ProcessStartInfo(fileName, arguments)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    use proc = Process.Start(psi)
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

    { ExitCode = proc.ExitCode
      Output = stdout + stderr }

/// Run all tests in a project.
let runAllTests (projectDll: string) : TestResult =
    runProcess "dotnet" $"exec \"%s{projectDll}\""

/// Run only tests in the specified classes.
/// Uses multiple --filter-class flags (ORed by xUnit v3 MTP).
let runFilteredTests (projectDll: string) (testClasses: string list) : TestResult =
    let filterArgs =
        testClasses
        |> List.map (fun cls -> $"--filter-class \"%s{cls}\"")
        |> String.concat " "

    let result = runProcess "dotnet" $"exec \"%s{projectDll}\" %s{filterArgs}"
    // xUnit v3 returns exit code 8 when zero tests match the filter — treat as success
    let exitCode = if result.ExitCode = 8 then 0 else result.ExitCode
    { result with ExitCode = exitCode }

/// Discover test projects by scanning for .fsproj files with xunit references.
/// Only scans tests/ directory to avoid .devenv/ symlink issues.
let discoverTestProjects (repoRoot: string) : string list =
    let testsDir = Path.Combine(repoRoot, "tests")

    if not (Directory.Exists(testsDir)) then
        []
    else
        Directory.GetFiles(testsDir, "*.fsproj", SearchOption.AllDirectories)
        |> Array.filter (fun path ->
            try
                let content = File.ReadAllText(path)
                content.Contains("xunit", StringComparison.OrdinalIgnoreCase)
            with _ ->
                false)
        |> Array.toList
        |> List.sort
