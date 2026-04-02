module TestPrune.TestRunner

open System
open System.Diagnostics
open System.IO

/// Result of running a test process, with stdout and stderr kept separate.
type TestResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

/// Type alias for process runner functions.
type ProcessRunner = string -> string -> TestResult

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
    let sw = Stopwatch.StartNew()

    let stdoutTask = proc.StandardOutput.ReadToEndAsync()
    let stderrTask = proc.StandardError.ReadToEndAsync()
    proc.WaitForExit()
    sw.Stop()

    let stdout = stdoutTask.Result
    let stderr = stderrTask.Result

    eprintfn $"[%s{fileName} %s{arguments}] \u2192 exit %d{proc.ExitCode} in %.1f{sw.Elapsed.TotalSeconds}s"

    { ExitCode = proc.ExitCode
      Stdout = stdout
      Stderr = stderr }

/// Build the filter arguments string from a list of test class names.
let buildFilterArgs (testClasses: string list) : string =
    testClasses
    |> List.map (fun cls -> $"--filter-class \"%s{cls}\"")
    |> String.concat " "

/// Normalize exit codes: xUnit v3 returns 8 when zero tests match — treat as success.
let normalizeExitCode (exitCode: int) : int = if exitCode = 8 then 0 else exitCode

/// Run all tests in a project using the given process runner.
let runAllTestsWith (runner: ProcessRunner) (projectDll: string) : TestResult =
    runner "dotnet" $"exec \"%s{projectDll}\""

/// Run all tests in a project.
let runAllTests (projectDll: string) : TestResult = runAllTestsWith runProcess projectDll

/// Run only tests in the specified classes using the given process runner.
let runFilteredTestsWith (runner: ProcessRunner) (projectDll: string) (testClasses: string list) : TestResult =
    let filterArgs = buildFilterArgs testClasses
    let result = runner "dotnet" $"exec \"%s{projectDll}\" %s{filterArgs}"

    { result with
        ExitCode = normalizeExitCode result.ExitCode }

/// Run only tests in the specified classes.
/// Uses multiple --filter-class flags (ORed by xUnit v3 MTP).
let runFilteredTests (projectDll: string) (testClasses: string list) : TestResult =
    runFilteredTestsWith runProcess projectDll testClasses

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
            with
            | :? System.IO.IOException
            | :? System.UnauthorizedAccessException -> false)
        |> Array.toList
        |> List.sort
