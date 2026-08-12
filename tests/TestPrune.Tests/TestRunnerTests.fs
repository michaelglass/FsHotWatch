module TestPrune.Tests.TestRunnerTests

open System
open System.IO
open System.Threading.Tasks
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

module ``awaitOutput`` =

    [<Fact>]
    let ``returns the value of a completed task`` () =
        test <@ awaitOutput (Task.FromResult "captured output") = "captured output" @>

    // Regression: unwrapping a faulted output-read with .Result rethrows
    // AggregateException, masking the real IO failure from callers and logs.
    // The original exception type must surface unchanged.
    [<Fact>]
    let ``faulted task surfaces the original exception, not AggregateException`` () =
        let faulted = Task.FromException<string>(IOException "pipe broke")

        raises<IOException> <@ awaitOutput faulted @>

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

            { ExitCode = 0
              Stdout = "ok"
              Stderr = "" }

        let result = runAllTestsWith fakeRunner "/path/to/Tests.dll"

        test <@ capturedFileName = "dotnet" @>
        test <@ capturedArgs = "exec \"/path/to/Tests.dll\"" @>
        test <@ result.ExitCode = 0 @>
        test <@ result.Stdout = "ok" @>

module ``runFilteredTestsWith`` =

    [<Fact>]
    let ``normalizes exit code 8 to 0`` () =
        let fakeRunner (_: string) (_: string) : TestResult =
            { ExitCode = 8
              Stdout = "no tests matched"
              Stderr = "" }

        let result = runFilteredTestsWith fakeRunner "/path/to/Tests.dll" [ "Ns.MyClass" ]
        test <@ result.ExitCode = 0 @>

    [<Fact>]
    let ``preserves non-8 exit codes`` () =
        let fakeRunner (_: string) (_: string) : TestResult =
            { ExitCode = 1
              Stdout = "failure"
              Stderr = "" }

        let result = runFilteredTestsWith fakeRunner "/path/to/Tests.dll" [ "Ns.MyClass" ]
        test <@ result.ExitCode = 1 @>

    [<Fact>]
    let ``includes filter args in command`` () =
        let mutable capturedArgs = ""

        let fakeRunner (_: string) (args: string) : TestResult =
            capturedArgs <- args

            { ExitCode = 0
              Stdout = ""
              Stderr = "" }

        runFilteredTestsWith fakeRunner "/path/to/Tests.dll" [ "Ns.A"; "Ns.B" ]
        |> ignore

        test <@ capturedArgs.Contains("--filter-class \"Ns.A\"") @>
        test <@ capturedArgs.Contains("--filter-class \"Ns.B\"") @>

module ``runProcessWith`` =

    // The wait is a hang detector: a wedged `dotnet exec <testdll>` under an unbounded
    // `WaitForExit` hangs the CLI forever with no diagnostic, so a process that outlives
    // the (injected, short) timeout must be killed and reported as a timeout, NOT silently
    // waited out. Revert `runProcessWith` to `proc.WaitForExit()` and this test blocks for
    // the child's full sleep, then returns ExitCode 0 with no "wedged" diagnostic.
    [<Fact>]
    let ``bounds a wedged process: kills it and returns a timeout result`` () =
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let result = runProcessWith 200 "sleep" "30"
        sw.Stop()

        // Returned far inside the child's 30s sleep => it was killed, not waited out.
        test <@ sw.Elapsed.TotalSeconds < 10.0 @>
        test <@ result.ExitCode = timeoutExitCode @>
        test <@ result.Stderr.Contains "wedged" @>

    // A process that completes inside the bound behaves exactly as an unbounded wait
    // would: real exit code, no timeout signalling.
    [<Fact>]
    let ``runs a process that completes within the bound to a normal result`` () =
        let result = runProcessWith 30_000 "sleep" "0"
        test <@ result.ExitCode = 0 @>
        test <@ result.ExitCode <> timeoutExitCode @>

module ``drainOutputWithin`` =

    open System.Diagnostics

    // `WaitForExit` returns when the DIRECT child exits, but an unbounded
    // read of its redirected stdout blocks until every grandchild that inherited
    // the write handle closes it. This spawns exactly that shape without MSBuild: a shell
    // that backgrounds a long `sleep` (which inherits stdout) and then exits, so the stdout
    // read task cannot complete until the sleep dies. `sleep`'s stderr is sent to /dev/null
    // so the STDERR pipe DOES reach EOF on shell exit — exercising both the
    // "captured-so-far" (stdout: still running) and the completed (stderr: empty) arms of
    // the give-up branch.
    //
    // The drain is run off-thread and given far longer (10s) than its own 500ms bound: a
    // correctly-bounded drain returns almost immediately, so `.Wait(10s)` is true. An
    // unbounded drain blocks on the 30s grandchild sleep and never returns inside 10s.
    [<Fact>]
    let ``bounds a drain wedged by a grandchild holding the stdout pipe open`` () =
        let psi = ProcessStartInfo("/bin/sh")
        psi.ArgumentList.Add("-c")
        psi.ArgumentList.Add("sleep 30 2>/dev/null & echo done")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        use proc = Process.Start(psi)
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()

        // The direct shell exits after `echo done`; the backgrounded `sleep 30` keeps the
        // inherited stdout pipe open, so an unbounded stdout read would block ~30s.
        proc.WaitForExit() |> ignore

        let drainTask =
            Task.Run(fun () ->
                drainOutputWithin 500 "/bin/sh -c 'sleep 30 2>/dev/null & echo done'" stdoutTask stderrTask)

        let finishedWithinTestBound = drainTask.Wait(10_000)

        // The bounded drain gave up on its 500ms bound rather than waiting out the 30s
        // grandchild sleep; the unbounded baseline never gets here inside 10s.
        test <@ finishedWithinTestBound @>

module ``resolveTestRunTimeoutMs`` =

    let private envVar = "TESTPRUNE_TEST_RUN_TIMEOUT_MS"

    [<Fact>]
    let ``falls back to the generous default when the env var is unset`` () =
        let prior = Environment.GetEnvironmentVariable envVar
        Environment.SetEnvironmentVariable(envVar, null)

        try
            test <@ resolveTestRunTimeoutMs () = defaultTestRunTimeoutMs @>
        finally
            Environment.SetEnvironmentVariable(envVar, prior)

    [<Fact>]
    let ``honours a positive integer env override`` () =
        let prior = Environment.GetEnvironmentVariable envVar
        Environment.SetEnvironmentVariable(envVar, "1234")

        try
            test <@ resolveTestRunTimeoutMs () = 1234 @>
        finally
            Environment.SetEnvironmentVariable(envVar, prior)

    [<Fact>]
    let ``ignores a non-positive or malformed override`` () =
        let prior = Environment.GetEnvironmentVariable envVar
        Environment.SetEnvironmentVariable(envVar, "not-a-number")

        try
            test <@ resolveTestRunTimeoutMs () = defaultTestRunTimeoutMs @>
        finally
            Environment.SetEnvironmentVariable(envVar, prior)
