# Coverage Push: Test the 0% Files

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Push test coverage on Program.fs, TestRunner.fs, and ProjectLoader.fs from 0% to meaningful levels using DI and temp filesystem fixtures.

**Architecture:** Two phases. Phase A adds tests for already-public pure functions (no code changes needed). Phase B introduces function parameters to replace hardcoded side effects (process execution, jj diff), enabling the orchestration logic to be tested with fakes.

**Tech Stack:** F#, xUnit v3, Unquote, temp directories via `System.IO.Path.GetTempPath()`

---

### Task 1: Test pure functions in Program.fs

These functions are already public and pure. Just add tests.

**Files:**
- Modify: `tests/TestPrune.Tests/ProgramTests.fs`

**Step 1: Add tests for `findRepoRoot`**

```fsharp
module ``findRepoRoot`` =

    [<Fact>]
    let ``finds repo with .jj directory`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        let subDir = Path.Combine(tmpDir, "a", "b")
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        Directory.CreateDirectory(subDir) |> ignore
        try
            test <@ findRepoRoot subDir = Some tmpDir @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``finds repo with .git directory`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        let subDir = Path.Combine(tmpDir, "deep")
        Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore
        Directory.CreateDirectory(subDir) |> ignore
        try
            test <@ findRepoRoot subDir = Some tmpDir @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``returns None when no repo marker exists`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        try
            // Use a deep isolated path with no .jj/.git anywhere above
            test <@ findRepoRoot tmpDir = None @>
        finally
            Directory.Delete(tmpDir, true)
```

**Step 2: Add tests for `findSourceFiles`**

```fsharp
module ``findSourceFiles`` =

    [<Fact>]
    let ``finds .fs files in src and tests, excludes obj and bin`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        let srcDir = Path.Combine(tmpDir, "src", "Lib")
        let testsDir = Path.Combine(tmpDir, "tests", "Lib.Tests")
        let objDir = Path.Combine(tmpDir, "src", "Lib", "obj")
        let binDir = Path.Combine(tmpDir, "src", "Lib", "bin")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(testsDir) |> ignore
        Directory.CreateDirectory(objDir) |> ignore
        Directory.CreateDirectory(binDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Lib.fs"), "module Lib")
        File.WriteAllText(Path.Combine(testsDir, "LibTests.fs"), "module LibTests")
        File.WriteAllText(Path.Combine(objDir, "Generated.fs"), "// generated")
        File.WriteAllText(Path.Combine(binDir, "Copy.fs"), "// copy")
        try
            let files = findSourceFiles tmpDir |> List.map Path.GetFileName
            test <@ files = [ "Lib.fs"; "LibTests.fs" ] @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``returns empty list when src and tests dirs missing`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        try
            test <@ findSourceFiles tmpDir = [] @>
        finally
            Directory.Delete(tmpDir, true)
```

**Step 3: Add tests for `findProjectFiles`**

```fsharp
module ``findProjectFiles`` =

    [<Fact>]
    let ``finds .fsproj files in src and tests, excludes obj`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        let srcDir = Path.Combine(tmpDir, "src", "Lib")
        let objDir = Path.Combine(tmpDir, "src", "Lib", "obj")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(objDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Lib.fsproj"), "<Project/>")
        File.WriteAllText(Path.Combine(objDir, "Bad.fsproj"), "<Project/>")
        try
            let files = findProjectFiles tmpDir |> List.map Path.GetFileName
            test <@ files = [ "Lib.fsproj" ] @>
        finally
            Directory.Delete(tmpDir, true)
```

**Step 4: Run tests, verify pass**

Run: `dotnet build && dotnet exec tests/TestPrune.Tests/bin/Debug/net10.0/TestPrune.Tests.dll`

---

### Task 2: Test pure functions in TestRunner.fs

**Files:**
- Modify: `tests/TestPrune.Tests/TestPrune.Tests.fsproj` (add new test file)
- Create: `tests/TestPrune.Tests/TestRunnerTests.fs`

**Step 1: Add TestRunnerTests.fs to the fsproj**

Add `<Compile Include="TestRunnerTests.fs" />` after the `ProgramTests.fs` line.

**Step 2: Write tests for `findTestDll`**

```fsharp
module TestPrune.Tests.TestRunnerTests

open Xunit
open Swensen.Unquote
open System.IO
open TestPrune.TestRunner

module ``findTestDll`` =

    [<Fact>]
    let ``constructs correct DLL path from project path`` () =
        let projPath = "/repo/tests/MyTests/MyTests.fsproj"
        let result = findTestDll projPath
        let expected = Path.Combine("/repo/tests/MyTests", "bin", "Debug", defaultTfm, "MyTests.dll")
        test <@ result = expected @>
```

**Step 3: Write tests for `discoverTestProjects`**

```fsharp
module ``discoverTestProjects`` =

    [<Fact>]
    let ``finds fsproj files containing xunit reference`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{System.Guid.NewGuid():N}")
        let testsDir = Path.Combine(tmpDir, "tests", "MyTests")
        Directory.CreateDirectory(testsDir) |> ignore
        File.WriteAllText(
            Path.Combine(testsDir, "MyTests.fsproj"),
            """<Project><ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup></Project>""")
        File.WriteAllText(
            Path.Combine(testsDir, "NotATest.fsproj"),
            """<Project><ItemGroup><PackageReference Include="SomeLib" /></ItemGroup></Project>""")
        try
            let projects = discoverTestProjects tmpDir |> List.map Path.GetFileName
            test <@ projects = [ "MyTests.fsproj" ] @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``returns empty list when tests dir does not exist`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{System.Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        try
            test <@ discoverTestProjects tmpDir = [] @>
        finally
            Directory.Delete(tmpDir, true)
```

**Step 4: Run tests, verify pass**

---

### Task 3: Test `parseProjectFile` in ProjectLoader.fs

**Files:**
- Modify: `tests/TestPrune.Tests/TestPrune.Tests.fsproj` (add new test file)
- Create: `tests/TestPrune.Tests/ProjectLoaderTests.fs`

**Step 1: Add ProjectLoaderTests.fs to fsproj**

Add `<Compile Include="ProjectLoaderTests.fs" />` after `TestRunnerTests.fs`.

**Step 2: Write tests**

```fsharp
module TestPrune.Tests.ProjectLoaderTests

open Xunit
open Swensen.Unquote
open System
open System.IO
open TestPrune.ProjectLoader

module ``parseProjectFile`` =

    let writeTempFsproj (content: string) =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let fsprojPath = Path.Combine(tmpDir, "Test.fsproj")
        File.WriteAllText(fsprojPath, content)
        tmpDir, fsprojPath

    [<Fact>]
    let ``extracts compile items in order`` () =
        let tmpDir, fsprojPath = writeTempFsproj """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="A.fs" />
    <Compile Include="B.fs" />
    <Compile Include="Sub/C.fs" />
  </ItemGroup>
</Project>"""
        try
            let compileItems, _ = parseProjectFile fsprojPath
            let names = compileItems |> List.map Path.GetFileName
            test <@ names = [ "A.fs"; "B.fs"; "C.fs" ] @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``extracts project references`` () =
        let tmpDir, fsprojPath = writeTempFsproj """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../Other/Other.fsproj" />
  </ItemGroup>
</Project>"""
        try
            let _, projectRefs = parseProjectFile fsprojPath
            test <@ projectRefs.Length = 1 @>
            test <@ projectRefs.[0].EndsWith("Other.fsproj") @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``handles empty project file`` () =
        let tmpDir, fsprojPath = writeTempFsproj """
<Project Sdk="Microsoft.NET.Sdk">
</Project>"""
        try
            let compileItems, projectRefs = parseProjectFile fsprojPath
            test <@ compileItems = [] @>
            test <@ projectRefs = [] @>
        finally
            Directory.Delete(tmpDir, true)
```

**Step 3: Run tests, verify pass**

---

### Task 4: DI for TestRunner — make `runProcess` injectable

The key insight: `runAllTests` and `runFilteredTests` are thin wrappers around `runProcess`. Make `runProcess` a parameter so the logic (argument construction, exit code mapping) can be tested with a fake.

**Files:**
- Modify: `src/TestPrune.Core/TestRunner.fs`
- Modify: `src/TestPrune/Program.fs` (update call sites)
- Modify: `tests/TestPrune.Tests/TestRunnerTests.fs`

**Step 1: Refactor TestRunner.fs**

Keep `runProcess` as the default, but add versions that accept a runner function:

```fsharp
/// Type alias for process runner functions.
type ProcessRunner = string -> string -> TestResult

let private runProcess : ProcessRunner =
    fun (fileName: string) (arguments: string) ->
        // ... existing implementation unchanged ...

/// Run all tests in a project using the given runner.
let runAllTestsWith (runner: ProcessRunner) (projectDll: string) : TestResult =
    runner "dotnet" $"exec \"%s{projectDll}\""

/// Run all tests in a project.
let runAllTests (projectDll: string) : TestResult =
    runAllTestsWith runProcess projectDll

/// Build filter arguments for xUnit v3 MTP class filtering.
let buildFilterArgs (testClasses: string list) : string =
    testClasses
    |> List.map (fun cls -> $"--filter-class \"%s{cls}\"")
    |> String.concat " "

/// Normalize xUnit v3 exit codes (8 = no matching tests -> 0).
let normalizeExitCode (exitCode: int) : int =
    if exitCode = 8 then 0 else exitCode

/// Run only tests in the specified classes using the given runner.
let runFilteredTestsWith (runner: ProcessRunner) (projectDll: string) (testClasses: string list) : TestResult =
    let filterArgs = buildFilterArgs testClasses
    let result = runner "dotnet" $"exec \"%s{projectDll}\" %s{filterArgs}"
    { result with ExitCode = normalizeExitCode result.ExitCode }

/// Run only tests in the specified classes.
let runFilteredTests (projectDll: string) (testClasses: string list) : TestResult =
    runFilteredTestsWith runProcess projectDll testClasses
```

**Step 2: Add tests for the extracted pure functions and DI versions**

```fsharp
module ``buildFilterArgs`` =

    [<Fact>]
    let ``builds filter args for single class`` () =
        let result = buildFilterArgs [ "MyTests.FooTests" ]
        test <@ result = "--filter-class \"MyTests.FooTests\"" @>

    [<Fact>]
    let ``builds filter args for multiple classes`` () =
        let result = buildFilterArgs [ "A.Tests"; "B.Tests" ]
        test <@ result = "--filter-class \"A.Tests\" --filter-class \"B.Tests\"" @>

    [<Fact>]
    let ``returns empty string for empty list`` () =
        test <@ buildFilterArgs [] = "" @>

module ``normalizeExitCode`` =

    [<Fact>]
    let ``exit code 8 becomes 0`` () =
        test <@ normalizeExitCode 8 = 0 @>

    [<Fact>]
    let ``exit code 0 stays 0`` () =
        test <@ normalizeExitCode 0 = 0 @>

    [<Fact>]
    let ``exit code 1 stays 1`` () =
        test <@ normalizeExitCode 1 = 1 @>

module ``runAllTestsWith fake runner`` =

    [<Fact>]
    let ``passes correct arguments to runner`` () =
        let mutable capturedArgs = ("", "")
        let fakeRunner cmd args =
            capturedArgs <- (cmd, args)
            { ExitCode = 0; Output = "ok" }

        let _ = runAllTestsWith fakeRunner "/path/to/Tests.dll"
        test <@ fst capturedArgs = "dotnet" @>
        test <@ (snd capturedArgs).Contains("Tests.dll") @>

module ``runFilteredTestsWith fake runner`` =

    [<Fact>]
    let ``passes filter args and normalizes exit code 8`` () =
        let fakeRunner _ _ = { ExitCode = 8; Output = "no match" }
        let result = runFilteredTestsWith fakeRunner "/path/to/Tests.dll" [ "NS.MyClass" ]
        test <@ result.ExitCode = 0 @>

    [<Fact>]
    let ``preserves non-8 exit codes`` () =
        let fakeRunner _ _ = { ExitCode = 1; Output = "fail" }
        let result = runFilteredTestsWith fakeRunner "/path/to/Tests.dll" [ "NS.MyClass" ]
        test <@ result.ExitCode = 1 @>
```

**Step 3: Update Program.fs call sites**

No changes needed — `runAllTests` and `runFilteredTests` still exist with the same signatures. The `*With` variants are additive.

**Step 4: Run tests, verify pass**

---

### Task 5: DI for Program.fs — make `getJjDiff` injectable

The `analyzeChanges` function is private and calls `getJjDiff()` directly. Extract the diff-fetching as a parameter so the orchestration logic can be tested.

**Files:**
- Modify: `src/TestPrune/Program.fs`
- Modify: `tests/TestPrune.Tests/ProgramTests.fs`

**Step 1: Refactor `analyzeChanges` to accept a diff provider**

```fsharp
/// Type alias for VCS diff providers.
type DiffProvider = unit -> Result<string, string>

/// The default diff provider: runs `jj diff --git`.
let jjDiffProvider : DiffProvider =
    fun () ->
        // ... existing getJjDiff implementation moved here ...

/// Determine test selection from a diff.
let analyzeChanges
    (getDiff: DiffProvider)
    (repoRoot: string)
    (db: Database)
    (checker: FSharpChecker)
    : Result<TestSelection * string list, string> =
    match getDiff () with
    // ... rest unchanged ...
```

Update `runStatus` and `runRun` to pass `jjDiffProvider`.

**Step 2: Add tests for `analyzeChanges` with fake diff**

```fsharp
module ``analyzeChanges`` =

    [<Fact>]
    let ``diff error returns Error`` () =
        let fakeDiff () = Error "not a repo"
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let dbPath = Path.Combine(tmpDir, "test.db")
        let db = Database.create dbPath
        let checker = FSharpChecker.Create()
        try
            let result = analyzeChanges fakeDiff tmpDir db checker
            test <@ Result.isError result @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``empty diff returns empty subset`` () =
        let fakeDiff () = Ok ""
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let dbPath = Path.Combine(tmpDir, "test.db")
        let db = Database.create dbPath
        let checker = FSharpChecker.Create()
        try
            match analyzeChanges fakeDiff tmpDir db checker with
            | Ok(RunSubset [], _) -> ()
            | other -> failwithf $"Expected Ok(RunSubset [], _) but got %A{other}"
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``fsproj change triggers RunAll`` () =
        let fakeDiff () = Ok "diff --git a/src/Lib/Lib.fsproj b/src/Lib/Lib.fsproj\n--- a/src/Lib/Lib.fsproj\n+++ b/src/Lib/Lib.fsproj\n@@ -1 +1 @@\n-old\n+new\n"
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let dbPath = Path.Combine(tmpDir, "test.db")
        let db = Database.create dbPath
        let checker = FSharpChecker.Create()
        try
            match analyzeChanges fakeDiff tmpDir db checker with
            | Ok(RunAll _, _) -> ()
            | other -> failwithf $"Expected Ok(RunAll _, _) but got %A{other}"
        finally
            Directory.Delete(tmpDir, true)
```

**Step 3: Run tests, verify pass**

---

### Task 6: Ratchet coverage thresholds

**Files:**
- Modify: `scripts/check-coverage.fsx`

**Step 1: Run full coverage and update thresholds**

```bash
dotnet test --coverage --coverage-output-format cobertura --coverage-output "$PWD/coverage/coverage.cobertura.xml"
dotnet fsi scripts/check-coverage.fsx
```

**Step 2: Update overrides to match new actual coverage**

Set each override to the current measured value (rounded down to nearest integer). Leave a comment explaining what remains untestable and why.

**Step 3: Verify coverage check passes**

---

### Task 7: Commit

```bash
jj commit -m "test: push coverage with DI and pure function tests"
```
