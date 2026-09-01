module FsHotWatch.Tests.RunOnceOutputTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Daemon
open Ionide.ProjInfo

type private EmptyWorkspaceLoader() =
    let notifications = Event<Types.WorkspaceProjectState>()

    interface IWorkspaceLoader with
        member _.LoadProjects(_projectPaths) = Seq.empty
        member _.LoadProjects(_projectPaths, _customProperties, _binaryLog) = Seq.empty
        member _.LoadSln(_solutionPath) = Seq.empty
        member _.LoadSln(_solutionPath, _customProperties, _binaryLog) = Seq.empty

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

type private ControlledWorkspaceLoader(resultsByAttempt: Types.ProjectOptions list list) =
    let entered =
        resultsByAttempt
        |> List.map (fun _ -> new Threading.ManualResetEventSlim(false))
        |> List.toArray

    let resume =
        resultsByAttempt
        |> List.map (fun _ -> new Threading.ManualResetEventSlim(false))
        |> List.toArray

    let notifications = Event<Types.WorkspaceProjectState>()
    let mutable attempt = -1

    member _.Entered(index: int) = entered[index]
    member _.Resume(index: int) = resume[index].Set()

    member private _.Load() =
        let index = Threading.Interlocked.Increment(&attempt)

        if index >= resultsByAttempt.Length then
            failwithf "unexpected workspace-loader attempt %d" index

        entered[index].Set()
        resume[index].Wait()
        resultsByAttempt[index] :> seq<_>

    interface IWorkspaceLoader with
        member this.LoadProjects(_projectPaths) = this.Load()
        member this.LoadProjects(_projectPaths, _customProperties, _binaryLog) = this.Load()
        member this.LoadSln(_solutionPath) = this.Load()
        member this.LoadSln(_solutionPath, _customProperties, _binaryLog) = this.Load()

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

open FsHotWatch.Tests.TestHelpers

let private minimalWorkspaceProject (projectPath: string) : Types.ProjectOptions =
    { ProjectId = None
      ProjectFileName = projectPath
      TargetFramework = "net10.0"
      SourceFiles = []
      OtherOptions = []
      ReferencedProjects = []
      PackageReferences = []
      LoadTime = DateTime.UtcNow
      TargetPath = System.IO.Path.ChangeExtension(projectPath, ".dll")
      TargetRefPath = None
      ProjectOutputType = Types.ProjectOutputType.Library
      ProjectSdkInfo =
        { IsTestProject = false
          Configuration = "Debug"
          IsPackable = false
          TargetFramework = "net10.0"
          TargetFrameworkIdentifier = ".NETCoreApp"
          TargetFrameworkVersion = "v10.0"
          MSBuildAllProjects = []
          MSBuildToolsVersion = "Current"
          ProjectAssetsFile = ""
          RestoreSuccess = true
          Configurations = [ "Debug" ]
          TargetFrameworks = [ "net10.0" ]
          RunArguments = None
          RunCommand = None
          IsPublishable = None }
      Items = []
      Properties = []
      CustomProperties = []
      AllProperties = Map.empty
      AllItems = Map.empty
      Analyzers = [] }


// --- Staleness warning: detect FileCommand plugin inputs newer than last run ---

[<Fact(Timeout = 15000)>]
let ``detectStalePluginInputs flags plugins whose args are newer than last run`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let cfg = System.IO.Path.Combine(tmpDir, "cfg.json")

    try
        System.IO.File.WriteAllText(cfg, "{}")
        let lastRun = DateTime.UtcNow.AddMinutes(-5.0)
        // ensure mtime is after lastRun
        System.IO.File.SetLastWriteTimeUtc(cfg, DateTime.UtcNow)

        let plugins =
            [ { Name = "ratchet"
                LastRunStarted = lastRun
                RepoRoot = tmpDir
                Args = "--check cfg.json" } ]

        let result = detectStalePluginInputs plugins

        test
            <@
                result
                |> List.exists (fun (n, files) -> n = "ratchet" && List.contains cfg files)
            @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``detectStalePluginInputs omits plugins with no stale files`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let cfg = System.IO.Path.Combine(tmpDir, "cfg.json")

    try
        System.IO.File.WriteAllText(cfg, "{}")
        System.IO.File.SetLastWriteTimeUtc(cfg, DateTime.UtcNow.AddMinutes(-10.0))
        let lastRun = DateTime.UtcNow

        let plugins =
            [ { Name = "ratchet"
                LastRunStarted = lastRun
                RepoRoot = tmpDir
                Args = "--check cfg.json" } ]

        let result = detectStalePluginInputs plugins
        test <@ List.isEmpty result @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``formatStalenessWarning is empty for no stale plugins`` () =
    test <@ formatStalenessWarning [] = "" @>

[<Fact(Timeout = 15000)>]
let ``formatStalenessWarning names the plugin, file, and rerun hint`` () =
    let warning = formatStalenessWarning [ "ratchet", [ "/tmp/cfg.json" ] ]
    test <@ warning.Contains("ratchet") @>
    test <@ warning.Contains("/tmp/cfg.json") @>
    test <@ warning.Contains("rerun") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors groups by file with plugin prefix`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("lint",
                 { Message = "bad name"
                   Severity = Warning
                   Line = 17
                   Column = 0
                   Detail = None })
                ("build",
                 { Message = "type error"
                   Severity = Error
                   Line = 42
                   Column = 5
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("src/Foo.fs") @>
    test <@ result.Contains("[lint]") @>
    test <@ result.Contains("[build]") @>
    test <@ result.Contains("L17") @>
    test <@ result.Contains("L42") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors shows severity labels for error and warning`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("build",
                 { Message = "type error"
                   Severity = Error
                   Line = 42
                   Column = 5
                   Detail = None })
                ("lint",
                 { Message = "bad name"
                   Severity = Warning
                   Line = 17
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("error") @>
    test <@ result.Contains("type error") @>
    test <@ result.Contains("warning") @>
    test <@ result.Contains("bad name") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors shows count summary`` () =
    let errors =
        Map.ofList
            [ "src/A.fs",
              [ ("lint",
                 { Message = "x"
                   Severity = Warning
                   Line = 1
                   Column = 0
                   Detail = None }) ]
              "src/B.fs",
              [ ("build",
                 { Message = "y"
                   Severity = Error
                   Line = 2
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("1 error(s), 1 warning(s) in 2 file(s)") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors with no errors shows clean message`` () =
    let result = formatErrors Map.empty
    test <@ result.Contains("No errors") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors hides info-severity entries from output`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("fcs",
                 { Message = "XML comment is not placed on a valid language element."
                   Severity = Info
                   Line = 3
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ not (result.Contains("XML comment")) @>
    test <@ result.Contains("No errors") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors hides hint-severity entries from output`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("fcs",
                 { Message = "some hint"
                   Severity = Hint
                   Line = 5
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ not (result.Contains("some hint")) @>
    test <@ result.Contains("No errors") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors shows warnings but hides info in same file`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("fcs",
                 { Message = "XML comment is not placed on a valid language element."
                   Severity = Info
                   Line = 3
                   Column = 0
                   Detail = None })
                ("format-check",
                 { Message = "File is not formatted"
                   Severity = Warning
                   Line = 1
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("File is not formatted") @>
    test <@ not (result.Contains("XML comment")) @>
    test <@ result.Contains("1 warning(s) in 1 file(s)") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors excludes files with only info entries from file count`` () =
    let errors =
        Map.ofList
            [ "src/A.fs",
              [ ("fcs",
                 { Message = "XML comment"
                   Severity = Info
                   Line = 3
                   Column = 0
                   Detail = None }) ]
              "src/B.fs",
              [ ("lint",
                 { Message = "bad name"
                   Severity = Warning
                   Line = 1
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("1 warning(s) in 1 file(s)") @>

// --- failIfNoProjects: shared fail-fast helper used by every entry path ---

[<Fact(Timeout = 15000)>]
let ``failIfNoProjects returns Some 2 when no fsproj exists`` () =
    withTempDir "failif-zero-projects" (fun tmpDir ->
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmpDir, "src"))
        |> ignore

        let result = failIfNoProjects tmpDir []
        test <@ result = Some 2 @>)

[<Fact(Timeout = 15000)>]
let ``failIfNoProjects returns None when at least one fsproj exists`` () =
    withTempDir "failif-has-project" (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "MyProject.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )

        let result = failIfNoProjects tmpDir []
        test <@ result = None @>)

[<Fact(Timeout = 15000)>]
let ``failIfNoProjects returns None when fsproj is in tests directory`` () =
    // src/ exists but is empty and the sole fsproj lives under tests/, so both directories
    // must be probed before the helper can declare success.
    withTempDir "failif-tests-only" (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")
        let testsDir = System.IO.Path.Combine(tmpDir, "tests")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore
        System.IO.Directory.CreateDirectory(testsDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(testsDir, "MyTests.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )

        let result = failIfNoProjects tmpDir []
        test <@ result = None @>)

[<Fact(Timeout = 15000)>]
let ``failIfNoProjects returns None when some fsprojs excluded but at least one remains`` () =
    // Drives `Array.exists` past its first element — with only one fsproj the "keep
    // iterating" branch is never exercised.
    withTempDir "failif-mixed-excludes" (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")

        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(srcDir, "vendor"))
        |> ignore

        // Excluded by `vendor/`.
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "vendor", "Vendored.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )
        // Not excluded.
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "MyProject.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )

        let result = failIfNoProjects tmpDir [ "vendor/" ]
        test <@ result = None @>)

[<Fact(Timeout = 15000)>]
let ``failIfNoProjects returns Some 2 when every fsproj is excluded`` () =
    // An fsproj exists on disk but the user's exclude pattern matches it. The pre-check
    // resolves excludes with repo-relative gitignore semantics.
    withTempDir "failif-all-excluded" (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "MyProject.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )

        let result = failIfNoProjects tmpDir [ "src/" ]
        test <@ result = Some 2 @>)

// --- AUTOMATION-290: total discovery failure — files found, none loaded ---
//
// The guard `failIfNoProjects` above cannot answer this question and is not
// meant to: it runs BEFORE any MSBuild evaluation and asks whether there is
// anything to load. In the incident all 18 project files existed, so it passed,
// correctly, and every one of them then threw inside `LoadProject`. These pin the
// distinction it is blind to.

[<Fact(Timeout = 15000)>]
let ``project files discovered and none loaded is a discovery failure`` () =
    // The incident, in numbers: 18 .fsproj on disk, 0 out of MSBuild evaluation.
    test <@ (totalDiscoveryFailure 18 0).IsSome @>

[<Fact(Timeout = 15000)>]
let ``a repository where every project loaded is not a discovery failure`` () =
    test <@ totalDiscoveryFailure 18 18 = None @>

[<Fact(Timeout = 15000)>]
let ``a partial load is not a TOTAL discovery failure`` () =
    // The separator between this guard and a noisier one. Individual projects fail
    // to load for ordinary reasons (an unrestored project, a broken .fsproj), and
    // the daemon already logs each `LoadProject FAILED`. Only the total case is
    // the state that cannot be told apart from success from the outside, so only
    // the total case aborts the run.
    test <@ totalDiscoveryFailure 18 1 = None @>

[<Fact(Timeout = 15000)>]
let ``a tree with no project files at all is not reported as a load failure`` () =
    // Zero found is `failIfNoProjects`' case, and discovery already warns on it.
    // Claiming a load failure here would invent the very thing this ticket is
    // about: a message pointing away from what actually happened.
    test <@ totalDiscoveryFailure 0 0 = None @>

[<Fact(Timeout = 15000)>]
let ``the discovery-failure message names the load failure and the log that carries it`` () =
    // THE POINT OF THE TICKET, and the reason this is asserted rather than left to
    // review. What made the incident expensive was not the failure, it was an hour
    // of wall clock spent on messages that pointed elsewhere: a wedged plugin, a
    // quiescence timeout, `coverage could not be confirmed`. A reader who sees this
    // line must be able to stop reading and go straight to the per-project reason.
    let message =
        match totalDiscoveryFailure 18 0 with
        | Some m -> m
        | None -> failwith "18 project files found and 0 loaded must be reported as a failure"

    // Where the actual reason is, in the words the daemon logs it under.
    test <@ message.Contains("LoadProject FAILED") @>
    test <@ message.Contains("logs/daemon.log") @>
    // The counts, so the sentence stands alone in a log without its neighbours.
    test <@ message.Contains("0 of 18") @>
    // And the explicit disavowal of the three red herrings.
    test <@ message.Contains("NOT a coverage, quiescence or wedged-plugin problem") @>

// --- runOnceAndReport: zero-projects exit code ---

[<Fact(Timeout = 30000)>]
let ``runOnceAndReport returns 2 when no projects are discovered`` () =
    withTempDir "runonce-zero-projects" (fun tmpDir ->
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmpDir, "src"))
        |> ignore

        let nullChecker: FSharp.Compiler.CodeAnalysis.FSharpChecker =
            Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

        let createDaemon (root: string) =
            Daemon.createWith nullChecker root Daemon.DaemonOptions.defaults

        let config: DaemonConfiguration =
            { defaultTestConfig () with
                Build = None
                Format = Off
                Lint = false
                Cache = NoCache }

        let exitCode = runOnceAndReport (fun _ -> "") false createDaemon tmpDir config None

        test <@ exitCode = 2 @>)

// ---------------------------------------------------------------------------
// AUTOMATION-117 — `confirm --run-once`: the merge verdict WITHOUT a daemon.
//
// `confirm` used to exist only on the daemon IPC path, and `--run-once` (what CI uses)
// bypasses the daemon entirely. CI ran the full suite anyway, but only because a checkout
// starts with a COLD impact DB; warm that cache and the same green comes from a subset.
//
// These drive the REAL run-once driver (`RunOnceCheck.runOnceAndVerdict`), not a pure
// helper beside it. A repo with a project but NO test projects has no test-prune plugin, so
// `test-scope` does not exist and the scope reads `ScopeUnknown` — "I could not establish
// what ran". `confirm` must refuse it (exit 3); the inner loop tolerates it (exit 0),
// because a repo with no tests configured has no tests to run.
// ---------------------------------------------------------------------------

/// A repo with one discoverable, loadable project and NO test projects.
let private withProjectOnlyRepo (name: string) (f: string -> 'a) : 'a =
    withTempDir name (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "MyProject.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>"""
        )

        f tmpDir)

/// The cheapest config that still registers a plugin: the read-only format check —
/// in-process (no `dotnet` spawn), and a no-op on a repo with no `.fs` files.
///
/// It has to register SOMETHING. `isAllTerminal` is false for an EMPTY plugin map ("no
/// plugins registered yet" is not "everything finished"), so a genuinely plugin-free daemon
/// never settles and `RunOnce` blocks until its 30-minute timeout.
///
/// No build, no lint, and above all NO TESTS: with no test projects there is no test-prune
/// plugin, hence no `test-scope` command, hence no way to establish what ran. That is the
/// state `confirm` must refuse.
let private noTestProjectsConfig () : DaemonConfiguration =
    { defaultTestConfig () with
        Build = None
        Format = Check
        Lint = false
        Tests = None
        Cache = NoCache }

let private runOnceIn (checkMode: FsHotWatch.Cli.CheckVerdict.CheckMode) (repoRoot: string) : int =
    let createDaemon (root: string) =
        Daemon.createWith
            (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
            root
            Daemon.DaemonOptions.defaults

    FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdict
        (fun _ -> "")
        checkMode
        false
        createDaemon
        repoRoot
        (noTestProjectsConfig ())
        None

let private daemonWithLateVanishedDiagnostic (root: string) =
    let daemon =
        Daemon.createWith
            (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
            root
            Daemon.DaemonOptions.defaults

    daemon.Host.ReportErrors("test-prune", "RenamedAway.fs", [ ErrorEntry.error "late old-path finding" ])
    daemon

[<Fact(Timeout = 60000)>]
[<Trait("Issue", "AUTOMATION-300")>]
let ``generic run-once report prunes a late vanished diagnostic before grading`` () =
    withProjectOnlyRepo "runonce-report-rename" (fun repoRoot ->
        let exitCode =
            runOnceAndReport
                (fun _ -> "")
                false
                daemonWithLateVanishedDiagnostic
                repoRoot
                (noTestProjectsConfig ())
                None

        test <@ exitCode = 0 @>)

[<Fact(Timeout = 60000)>]
[<Trait("Issue", "AUTOMATION-300")>]
let ``check run-once prunes a late vanished diagnostic before grading`` () =
    withProjectOnlyRepo "runonce-check-rename" (fun repoRoot ->
        let exitCode =
            FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdict
                (fun _ -> "")
                FsHotWatch.Cli.CheckVerdict.InnerLoop
                false
                daemonWithLateVanishedDiagnostic
                repoRoot
                (noTestProjectsConfig ())
                None

        test <@ exitCode = 0 @>)

[<Fact(Timeout = 60000)>]
let ``run-once command retains executed evidence across a same-tree quiet convergence read`` () =
    withProjectOnlyRepo "runonce-retained-command" (fun repoRoot ->
        let sourcePath = System.IO.Path.Combine(repoRoot, "src", "Library.fs")
        let pendingPath = System.IO.Path.Combine(repoRoot, "src", "Pending.fs")
        let projectPath = System.IO.Path.Combine(repoRoot, "src", "MyProject.fsproj")
        System.IO.File.WriteAllText(sourcePath, "module Library\n")
        // Keep the disk project consistent with the manual registration below. The
        // daemon's watcher may rediscover during this test; rediscovery must preserve
        // both files, especially the deliberately unchecked Pending.fs.
        System.IO.File.WriteAllText(
            projectPath,
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Library.fs" />
    <Compile Include="Pending.fs" />
  </ItemGroup>
</Project>"""
        )

        let runId = Guid.Parse "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        let runDir = FsHotWatch.Ctrf.runDir repoRoot runId
        System.IO.Directory.CreateDirectory(runDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(runDir, "A.Tests" + FsHotWatch.Ctrf.ReportSuffix),
            """{"reportFormat":"CTRF","specVersion":"0.0.0","reportId":"a","results":{"tool":{"name":"xUnit.net v3"},"summary":{"tests":3,"passed":3,"failed":0,"pending":0,"skipped":0,"other":0,"suites":1,"start":1,"stop":2},"tests":[]}}"""
        )

        let mutable scopeReads = 0

        let createDaemon (root: string) =
            let daemon =
                Daemon.createWith
                    (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
                    root
                    Daemon.DaemonOptions.defaults

            let handler: FsHotWatch.PluginFramework.PluginHandler<unit, unit> =
                { Name = FsHotWatch.PluginFramework.PluginName.create "fake-test-prune"
                  Init = ()
                  Update = fun _ctx state _event -> async { return state }
                  Commands =
                    [ FsHotWatch.Cli.IpcParsing.TestScopeCommand,
                      fun _ctx _state _args ->
                          async {
                              scopeReads <- scopeReads + 1

                              return
                                  if scopeReads = 1 then
                                      $"""{{"scope":"filtered","ranProjects":2,"totalProjects":4,"runId":"%O{runId}"}}"""
                                  else
                                      """{"scope":"none","noTestsReason":"already-verified"}"""
                          } ]
                  Subscriptions = FsHotWatch.PluginFramework.PluginSubscriptions.none
                  CacheKey = None
                  Teardown = None }

            daemon.Host.RegisterHandler(handler)
            daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously
            daemon.RegisterProject(projectPath, makeProjectOptions projectPath [ sourcePath; pendingPath ] [])
            daemon.Host.EmitFileChanged(SourceChanged [ sourcePath; pendingPath ])
            daemon

        let mutable scanCount = 0

        let runScan (daemon: Daemon) =
            scanCount <- scanCount + 1

            if scanCount > 1 then
                for file in [ sourcePath; pendingPath ] do
                    daemon.Host.EmitFileChecked(
                        { File = AbsFilePath.create file
                          Source = ""
                          ParseResults = Unchecked.defaultof<_>
                          CheckResults = FullCheck(Unchecked.defaultof<_>)
                          ProjectOptions = Unchecked.defaultof<_>
                          Version = 0L }
                    )

            daemon.Host.GetAllStatuses()

        let exitCode =
            FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdictWith
                runScan
                (fun _ -> "")
                FsHotWatch.Cli.CheckVerdict.InnerLoop
                false
                createDaemon
                repoRoot
                (noTestProjectsConfig ())
                None

        test <@ exitCode = 0 @>
        test <@ scanCount = 2 @>

        match FsHotWatch.Cli.Verdict.read repoRoot with
        | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
            test <@ verdict.RunId = Some runId @>
            test <@ verdict.Scope = FsHotWatch.Cli.IpcParsing.ImpactFiltered(2, 4) @>
            test <@ verdict.Suites |> List.map (fun suite -> suite.Project) = [ "A.Tests" ] @>
        | other -> failwithf "expected a published run-once verdict, got %A" other)

[<Fact(Timeout = 60000)>]
let ``run-once overwrites a current green before surfacing total discovery failure`` () =
    withProjectOnlyRepo "runonce-total-discovery-failure" (fun repoRoot ->
        // Seed the exact dangerous state: a readable green from an earlier run.
        FsHotWatch.Cli.IpcOutput.publishVerdict
            repoRoot
            []
            FsHotWatch.Cli.CheckVerdict.InnerLoop
            false
            (FsHotWatch.Cli.IpcParsing.TestRunReport.ofScopeOnly (FsHotWatch.Cli.IpcParsing.FullSuite 1))
            FsHotWatch.Cli.Verdict.NoReading
            Map.empty
            []
            (FsHotWatch.Cli.IpcOutput.SettledTree.capture repoRoot [])
            FsHotWatch.Cli.CheckVerdict.CheckOutcome.Clean
        |> ignore

        let createDaemon (root: string) =
            Daemon.createWithWorkspaceLoader
                (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
                root
                Daemon.DaemonOptions.defaults
                (EmptyWorkspaceLoader())
                (fun _ -> [])

        // The real driver after a scan whose loader returned zero projects. The
        // separate daemon snapshot test proves this is loader count, not pipeline
        // registration count.
        let runScan (daemon: Daemon) =
            daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously
            daemon.Host.GetAllStatuses()

        let ex =
            Assert.Throws<ConfigError>(fun () ->
                FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdictWith
                    runScan
                    (fun _ -> "")
                    FsHotWatch.Cli.CheckVerdict.InnerLoop
                    false
                    createDaemon
                    repoRoot
                    (noTestProjectsConfig ())
                    None
                |> ignore)

        test <@ ex.Message.Contains("PROJECT LOADING FAILED") @>

        match FsHotWatch.Cli.Verdict.read repoRoot with
        | FsHotWatch.Cli.Verdict.Reading.Found v ->
            test <@ v.ExitCode = 2 @>

            match v.Outcome with
            | FsHotWatch.Cli.Verdict.Incomplete reason ->
                test <@ reason.Contains("PROJECT LOADING FAILED") @>
                test <@ reason.Contains("0 of 1") @>
            | other -> failwithf "expected incomplete discovery verdict, got %A" other
        | other -> failwithf "expected discovery failure to replace the seeded green, got %A" other)

[<Fact(Timeout = 60000)>]
let ``run-once waits for an initial discovery still inside the real loader`` () =
    withProjectOnlyRepo "runonce-in-progress-discovery" (fun repoRoot ->
        let loader = ControlledWorkspaceLoader([ [] ])
        let mutable discoveryTask: System.Threading.Tasks.Task option = None
        let mutable daemonInstance: Daemon option = None

        let createDaemon (root: string) =
            let daemon =
                Daemon.createWithWorkspaceLoader
                    (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
                    root
                    Daemon.DaemonOptions.defaults
                    loader
                    (fun _ -> [])

            daemonInstance <- Some daemon
            daemon

        let runScan (daemon: Daemon) =
            let running = Async.StartAsTask(daemon.DiscoverAndRegisterProjects())
            discoveryTask <- Some(running :> System.Threading.Tasks.Task)

            if not (loader.Entered(0).Wait(TimeSpan.FromSeconds(10.0))) then
                failwith "workspace loader did not enter"

            daemon.Host.GetAllStatuses()

        let driver =
            System.Threading.Tasks.Task.Run(fun () ->
                FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdictWith
                    runScan
                    (fun _ -> "")
                    FsHotWatch.Cli.CheckVerdict.InnerLoop
                    false
                    createDaemon
                    repoRoot
                    (noTestProjectsConfig ())
                    None)

        try
            test <@ loader.Entered(0).Wait(TimeSpan.FromSeconds(10.0)) @>

            let premature =
                System.Threading.Tasks.Task.WhenAny(driver, System.Threading.Tasks.Task.Delay(1000))

            test <@ not (obj.ReferenceEquals(premature.GetAwaiter().GetResult(), driver)) @>
            loader.Resume(0)

            let ex =
                Assert.Throws<ConfigError>(fun () -> driver.GetAwaiter().GetResult() |> ignore)

            test <@ ex.Message.Contains("PROJECT LOADING FAILED") @>
            discoveryTask |> Option.iter (fun running -> running.GetAwaiter().GetResult())
        finally
            loader.Resume(0)

            discoveryTask
            |> Option.iter (fun running ->
                try
                    running.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
                with _ ->
                    ())

            try
                driver.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
            with _ ->
                ()

            daemonInstance |> Option.iter (fun daemon -> (daemon :> IDisposable).Dispose()))

[<Fact(Timeout = 60000)>]
let ``run-once convergence refuses a zero-load rediscovery`` () =
    withProjectOnlyRepo "runonce-zero-load-rescan" (fun repoRoot ->
        let projectPath = System.IO.Path.Combine(repoRoot, "src", "MyProject.fsproj")
        let sourcePath = System.IO.Path.Combine(repoRoot, "src", "Library.fs")
        System.IO.File.WriteAllText(sourcePath, "module Library")

        let loader =
            ControlledWorkspaceLoader([ [ minimalWorkspaceProject projectPath ]; [] ])

        loader.Resume(0)
        let mutable daemonInstance: Daemon option = None

        let createDaemon (root: string) =
            let daemon =
                Daemon.createWithWorkspaceLoader
                    (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
                    root
                    Daemon.DaemonOptions.defaults
                    loader
                    (fun loaded ->
                        if List.isEmpty loaded then
                            []
                        else
                            [ makeProjectOptions projectPath [ sourcePath ] [] ])

            daemonInstance <- Some daemon
            daemon

        let runScan (daemon: Daemon) =
            daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously
            daemon.Host.GetAllStatuses()

        let driver =
            System.Threading.Tasks.Task.Run(fun () ->
                FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdictWith
                    runScan
                    (fun _ -> "")
                    FsHotWatch.Cli.CheckVerdict.InnerLoop
                    false
                    createDaemon
                    repoRoot
                    (noTestProjectsConfig ())
                    None)

        try
            test <@ loader.Entered(1).Wait(TimeSpan.FromSeconds(10.0)) @>
            test <@ not driver.IsCompleted @>
            loader.Resume(1)

            let ex =
                Assert.Throws<ConfigError>(fun () -> driver.GetAwaiter().GetResult() |> ignore)

            test <@ ex.Message.Contains("PROJECT LOADING FAILED") @>

            match FsHotWatch.Cli.Verdict.read repoRoot with
            | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
                test <@ verdict.ExitCode = 2 @>

                match verdict.Outcome with
                | FsHotWatch.Cli.Verdict.Incomplete reason -> test <@ reason.Contains("PROJECT LOADING FAILED") @>
                | other -> failwithf "expected incomplete discovery verdict, got %A" other
            | other -> failwithf "expected zero-load rescan to publish an incomplete verdict, got %A" other
        finally
            loader.Resume(1)

            try
                driver.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
            with _ ->
                ()

            daemonInstance |> Option.iter (fun daemon -> (daemon :> IDisposable).Dispose()))

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-163: confirm one-shot accepts full evidence from its initial scan without a second scan`` () =
    withProjectOnlyRepo "confirm-runonce-exactly-once" (fun repoRoot ->
        let mutable scanCount = 0

        let createDaemon (root: string) =
            let daemon =
                Daemon.createWith
                    (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
                    root
                    Daemon.DaemonOptions.defaults

            let handler: FsHotWatch.PluginFramework.PluginHandler<unit, unit> =
                { Name = FsHotWatch.PluginFramework.PluginName.create "fake-test-prune"
                  Init = ()
                  Update = fun _ctx state _event -> async { return state }
                  Commands =
                    [ FsHotWatch.Cli.IpcParsing.SetScopeCommand, fun _ctx _state _args -> async { return "" }
                      FsHotWatch.Cli.IpcParsing.TestScopeCommand,
                      fun _ctx _state _args ->
                          async { return """{"scope":"full","ranProjects":1,"totalProjects":1}""" } ]
                  Subscriptions = FsHotWatch.PluginFramework.PluginSubscriptions.none
                  CacheKey = None
                  Teardown = None }

            daemon.Host.RegisterHandler(handler)
            daemon

        let runScan daemon =
            scanCount <- scanCount + 1
            runOnceWithProgress daemon

        let exitCode =
            FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdictWith
                runScan
                (fun _ -> "")
                FsHotWatch.Cli.CheckVerdict.Confirmation
                false
                createDaemon
                repoRoot
                (noTestProjectsConfig ())
                None

        test <@ exitCode = 0 @>
        test <@ scanCount = 1 @>)

[<Fact(Timeout = 60000)>]
let ``confirm --run-once REFUSES a verdict it has no full-suite evidence for`` () =
    // No test-prune plugin ⇒ no `test-scope` command ⇒ `ScopeUnknown`, which is not
    // full-suite and so cannot reach green. Exit 3 = UnearnedScope: nothing reported broken,
    // nothing reported sound either.
    withProjectOnlyRepo "confirm-runonce-refuses" (fun repoRoot ->
        let exitCode = runOnceIn FsHotWatch.Cli.CheckVerdict.Confirmation repoRoot
        test <@ exitCode = 3 @>)

[<Fact(Timeout = 60000)>]
let ``check --run-once tolerates an unknown scope`` () =
    // Same driver, same tree, DIFFERENT mode — this pins that the mode is what decides.
    //
    // It is also the positive control for the faulted-read test below, where the only
    // difference is a throwing `test-scope`; an exit 3 there is therefore the fault's doing.
    withProjectOnlyRepo "check-runonce-tolerates" (fun repoRoot ->
        let exitCode = runOnceIn FsHotWatch.Cli.CheckVerdict.InnerLoop repoRoot
        test <@ exitCode = 0 @>)

/// The REAL run-once driver, on a daemon whose `test-scope` command throws. Identical to
/// `runOnceIn` otherwise, so the only variable is whether the scope read succeeds.
let private runOnceWithFaultingScope (checkMode: FsHotWatch.Cli.CheckVerdict.CheckMode) (repoRoot: string) : int =
    let createDaemon (root: string) =
        let daemon =
            Daemon.createWith
                (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
                root
                Daemon.DaemonOptions.defaults

        let handler: FsHotWatch.PluginFramework.PluginHandler<unit, unit> =
            { Name = FsHotWatch.PluginFramework.PluginName.create "fake-test-prune"
              Init = ()
              Update = fun _ctx state _event -> async { return state }
              Commands =
                [ FsHotWatch.Cli.IpcParsing.TestScopeCommand,
                  fun _ctx _state (_args: string array) -> async { return failwith "SQLITE_BUSY: database is locked" } ]
              Subscriptions = FsHotWatch.PluginFramework.PluginSubscriptions.none
              CacheKey = None
              Teardown = None }

        daemon.Host.RegisterHandler(handler)
        daemon

    FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdict
        (fun _ -> "")
        checkMode
        false
        createDaemon
        repoRoot
        (noTestProjectsConfig ())
        None

[<Fact(Timeout = 60000)>]
let ``check --run-once REFUSES a scope read that faulted — and records WHY on disk`` () =
    // The trap: a throwing scope read used to produce the same `ScopeUnknown` as the test
    // above, which the inner loop tolerates — so `check` exited 0 having verified nothing.
    //
    // The verdict FILE must also carry the reason: a reader of `.fshw/verdict.json` has to
    // tell "this repo has no tests" from "this check could not see what it was judging",
    // because only the second is a broken check.
    withProjectOnlyRepo "check-runonce-faulted-scope" (fun repoRoot ->
        let exitCode =
            runOnceWithFaultingScope FsHotWatch.Cli.CheckVerdict.InnerLoop repoRoot

        test <@ exitCode = 3 @>

        match FsHotWatch.Cli.Verdict.read repoRoot with
        | FsHotWatch.Cli.Verdict.Reading.Found v ->
            test <@ v.ExitCode = exitCode @>
            // Never green, and never a red either: nothing failed, nothing was proven.
            test <@ FsHotWatch.Cli.Verdict.Outcome.tag v.Outcome = "incomplete" @>

            match v.Scope with
            | FsHotWatch.Cli.IpcParsing.ScopeUnreadable reason -> test <@ reason.Contains "SQLITE_BUSY" @>
            | other -> failwithf "expected ScopeUnreadable carrying the fault on disk, got %A" other
        | other -> failwithf "expected a published verdict, got %A" other)

[<Fact(Timeout = 60000)>]
let ``run-once publishes a verdict file that agrees with its exit code`` () =
    // The run-once path once wrote NO verdict at all, so `fshw verdict` after a CI run
    // reported "no verdict on disk" (AUTOMATION-117). File and exit code are two renderings
    // of ONE `CheckOutcome`: a refused confirm must not leave a green on disk.
    withProjectOnlyRepo "runonce-verdict-file" (fun repoRoot ->
        let exitCode = runOnceIn FsHotWatch.Cli.CheckVerdict.Confirmation repoRoot

        match FsHotWatch.Cli.Verdict.read repoRoot with
        | FsHotWatch.Cli.Verdict.Reading.Found v ->
            test <@ v.ExitCode = exitCode @>
            test <@ v.Command = FsHotWatch.Cli.Verdict.Confirm @>
            // An unearned scope is `incomplete`, NEVER green.
            test <@ FsHotWatch.Cli.Verdict.Outcome.tag v.Outcome = "incomplete" @>
            test <@ v.Scope = FsHotWatch.Cli.IpcParsing.ScopeUnknown @>
        | other -> failwithf "expected a published verdict, got %A" other)

// ---------------------------------------------------------------------------
// A CRASHED PLUGIN MUST NOT GREEN CI.
//
// `PluginStatus.Failed` is reachable WITHOUT a single `ErrorEntry` being written:
// PluginFramework's crash-nets force `Failed` and report NO diagnostics, because the
// framework cannot invent a file/line for someone else's stack trace.
//
// The daemon path counts that (`IpcOutput.hasFailures` is `anyPluginFailed ||
// failingDiagnostics`). The `--run-once` path — what CI runs — counted ONLY the
// diagnostics, so a CRASHED plugin exited 0 with `outcome: green` and
// `plugins: [{"outcome":"fail"}]` in the same verdict file.
// ---------------------------------------------------------------------------

/// A plugin whose event handler THROWS — what every unhandled plugin exception looks like
/// from the framework's side. It writes no `ErrorEntry`; it never gets the chance to.
let private crashingHandler () : FsHotWatch.PluginFramework.PluginHandler<unit, unit> =
    { Name = FsHotWatch.PluginFramework.PluginName.create "crash-plugin"
      Init = ()
      Update = fun _ctx _state _event -> async { return failwith "plugin exploded mid-handler" }
      Commands = []
      Subscriptions = Set.ofList [ FsHotWatch.PluginFramework.SubscribeBuildCompleted ]
      CacheKey = None
      Teardown = None }

/// A run-once driver whose daemon carries a plugin handed an event it will crash on. The
/// event is emitted BEFORE `RunOnce`, so the crash happens inside the settle the driver
/// itself waits out — as a real plugin fault would.
let private runOnceWithCrashedPlugin (checkMode: FsHotWatch.Cli.CheckVerdict.CheckMode) (repoRoot: string) : int =
    let createDaemon (root: string) =
        let daemon =
            Daemon.createWith
                (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
                root
                Daemon.DaemonOptions.defaults

        daemon.Host.RegisterHandler(crashingHandler ())
        daemon.Host.EmitBuildCompleted(BuildSucceeded)
        daemon

    FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdict
        (fun _ -> "")
        checkMode
        false
        createDaemon
        repoRoot
        (noTestProjectsConfig ())
        None

[<Fact(Timeout = 60000)>]
let ``check --run-once goes RED on a plugin that FAILED without writing a diagnostic`` () =
    // The ledger is spotless — the plugin never reached `ReportErrors`, it threw. Exit 0
    // here would be a green stamped over a check that did not run.
    withProjectOnlyRepo "runonce-crashed-plugin" (fun repoRoot ->
        let exitCode =
            runOnceWithCrashedPlugin FsHotWatch.Cli.CheckVerdict.InnerLoop repoRoot

        test <@ exitCode = 1 @>)

[<Fact(Timeout = 60000)>]
let ``the verdict file can NEVER say green while a plugin says fail`` () =
    // The contradiction asserted on the artifact itself: `{"outcome":"green",
    // "plugins":[{"outcome":"fail"}]}`. Whatever the exit code, the FILE cannot state both.
    withProjectOnlyRepo "runonce-crashed-verdict-file" (fun repoRoot ->
        runOnceWithCrashedPlugin FsHotWatch.Cli.CheckVerdict.InnerLoop repoRoot
        |> ignore

        match FsHotWatch.Cli.Verdict.read repoRoot with
        | FsHotWatch.Cli.Verdict.Reading.Found v ->
            let crashed =
                v.Plugins |> List.tryFind (fun p -> p.Name = "crash-plugin") |> Option.get

            test <@ crashed.Outcome = FsHotWatch.Cli.Verdict.PluginOutcome.Fail @>
            test <@ FsHotWatch.Cli.Verdict.Outcome.tag v.Outcome <> "green" @>
            test <@ v.ExitCode = 1 @>
        | other -> failwithf "expected a published verdict, got %A" other)

// ---------------------------------------------------------------------------
// RunOnceCheck — `confirm`'s scope commands over the IN-PROCESS transport.
//
// The daemon path reaches `test-scope`/`set-scope` over a socket; run-once reaches the same
// commands on the same plugin host, directly. These pin the transport and the fail-closed
// reading, without a daemon, a build, or a test run.
// ---------------------------------------------------------------------------

/// A host carrying a test-prune-shaped plugin that answers the given commands.
let private hostWith (commands: (string * (string array -> string)) list) : FsHotWatch.PluginHost.PluginHost =
    let host =
        FsHotWatch.PluginHost.PluginHost.create
            (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
            "/tmp/runoncecheck"

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, unit> =
        { Name = FsHotWatch.PluginFramework.PluginName.create "fake-test-prune"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands =
            commands
            |> List.map (fun (name, f) -> name, (fun _ctx _state (args: string array) -> async { return f args }))
          Subscriptions = FsHotWatch.PluginFramework.PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host

[<Fact(Timeout = 15000)>]
let ``readTestRun parses a full-suite reply from the in-process host`` () =
    let host =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.TestScopeCommand,
              fun _ -> """{"scope":"full","ranProjects":3,"totalProjects":3}""" ]

    let report = FsHotWatch.Cli.RunOnceCheck.readTestRun host
    test <@ report.Scope = FsHotWatch.Cli.IpcParsing.FullSuite 3 @>

[<Fact(Timeout = 15000)>]
let ``readTestRun reports an impact-filtered run as filtered, never as full`` () =
    // The whole point of `confirm`. A filtered run must not be readable as evidence.
    let host =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.TestScopeCommand,
              fun _ -> """{"scope":"filtered","ranProjects":1,"totalProjects":3}""" ]

    let report = FsHotWatch.Cli.RunOnceCheck.readTestRun host
    test <@ report.Scope = FsHotWatch.Cli.IpcParsing.ImpactFiltered(1, 3) @>

[<Fact(Timeout = 15000)>]
let ``readTestRun on a host with NO test-scope command is ScopeUnknown, not full-suite`` () =
    // The host returned nothing because the command does not exist; that silence must read
    // as "I could not establish what ran", never round up to full-suite (AUTOMATION-129).
    let report = FsHotWatch.Cli.RunOnceCheck.readTestRun (hostWith [])
    test <@ report.Scope = FsHotWatch.Cli.IpcParsing.ScopeUnknown @>

[<Fact(Timeout = 15000)>]
let ``readTestRun on a THROWING test-scope command is ScopeUnreadable, NOT the tolerated unknown`` () =
    // A throw is "I could not find out", a different fact from the no-test-projects host
    // above ("there is nothing to find out"). The trap: they were once the SAME value, and
    // because the inner loop tolerates the second it tolerated the first — so a throwing
    // read exited 0.
    let host =
        hostWith [ FsHotWatch.Cli.IpcParsing.TestScopeCommand, fun _ -> failwith "SQLITE_BUSY" ]

    let report = FsHotWatch.Cli.RunOnceCheck.readTestRun host

    match report.Scope with
    | FsHotWatch.Cli.IpcParsing.ScopeUnreadable reason -> test <@ reason.Contains "SQLITE_BUSY" @>
    | other -> failwithf "expected ScopeUnreadable carrying the fault, got %A" other

// ---------------------------------------------------------------------------
// A FAULTED READING IS NOT A GOOD READING (AUTOMATION-150's principle, applied to the
// TEST SCOPE).
//
// `CheckVerdict.verdict` refuses `NoTestsRun` in BOTH modes, but that refusal is only
// reached when the scope read SUCCEEDS. Every way of failing to read it — the command
// threw, the reply was garbage, the daemon answered something this build has no name for —
// degraded to the SAME value the "this repo has no tests configured" case uses, which the
// inner loop tolerates. So a fault converted a refusal (exit 3) into a pass (exit 0) on an
// identical daemon state: "I could not read what ran" collapsed into "nothing needed to
// run".
//
// These compose the REAL transport read with the REAL verdict, so the test fails if either
// half stops holding.
// ---------------------------------------------------------------------------

/// The exit code `fshw check` (InnerLoop) would produce for `scope`, with everything else
/// spotless: complete coverage, no failing plugin or diagnostic, no deferred build. The
/// scope is the ONLY variable.
let private innerLoopExitFor (scope: FsHotWatch.Cli.IpcParsing.TestScope) : int =
    let inputs: FsHotWatch.Cli.CheckVerdict.CheckInputs =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = FsHotWatch.Cli.CheckVerdict.BuildWait.NotWaiting
          RunnerAborted = FsHotWatch.Cli.CheckVerdict.RunnerAbort.NoAbort
          Coverage = FsHotWatch.Cli.IpcParsing.Complete
          Scope = scope }

    FsHotWatch.Cli.CheckVerdict.verdict FsHotWatch.Cli.CheckVerdict.InnerLoop inputs
    |> FsHotWatch.Cli.CheckVerdict.exitCode

let private scopeReadFrom (host: FsHotWatch.PluginHost.PluginHost) : FsHotWatch.Cli.IpcParsing.TestScope =
    (FsHotWatch.Cli.RunOnceCheck.readTestRun host).Scope

[<Fact(Timeout = 15000)>]
let ``check refuses a scope read that FAULTED — a fault may not be read as a pass`` () =
    // POSITIVE CONTROL 1: the fixture can observe an exit 0, so a non-zero below is the
    // scope's doing and not a broken harness.
    let ranEverything =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.TestScopeCommand,
              fun _ -> """{"scope":"full","ranProjects":3,"totalProjects":3}""" ]

    test <@ innerLoopExitFor (scopeReadFrom ranEverything) = 0 @>

    // POSITIVE CONTROL 2: the same state READ SUCCESSFULLY is already refused — three test
    // projects, no test evidence, exit 3. That is what the fault below must not undo.
    let ranNothing =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.TestScopeCommand,
              fun _ -> """{"scope":"none","ranProjects":0,"totalProjects":3}""" ]

    test <@ innerLoopExitFor (scopeReadFrom ranNothing) = 3 @>

    // THE DEFECT: the SAME daemon state, but the read throws. Only our ability to find out
    // changed, yet the answer flipped from "no verdict" to "everything is fine".
    let threw =
        hostWith [ FsHotWatch.Cli.IpcParsing.TestScopeCommand, fun _ -> failwith "SQLITE_BUSY: database is locked" ]

    test <@ innerLoopExitFor (scopeReadFrom threw) <> 0 @>

    // ...and the same for a reply this build cannot turn into a scope at all.
    let garbled =
        hostWith [ FsHotWatch.Cli.IpcParsing.TestScopeCommand, fun _ -> "<html>502 Bad Gateway</html>" ]

    test <@ innerLoopExitFor (scopeReadFrom garbled) <> 0 @>

    // ...and for a daemon that contradicts itself: "full", over 2 of 4 projects. The counts
    // are the evidence and they refute the label, so that reads unreadable, not clean.
    let selfContradicting =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.TestScopeCommand,
              fun _ -> """{"scope":"full","ranProjects":2,"totalProjects":4}""" ]

    test <@ innerLoopExitFor (scopeReadFrom selfContradicting) <> 0 @>

[<Fact(Timeout = 15000)>]
let ``check still tolerates a host with NO test-scope command — nothing to read is not a fault`` () =
    // The over-correction guard, and why this is a split rather than a blanket refusal. A
    // host with no `test-scope` command has no test projects configured — a PROVABLE
    // nothing, not a failed read — and turning every tests-less repo's inner loop red would
    // be a worse bug than the one above.
    test <@ innerLoopExitFor (scopeReadFrom (hostWith [])) = 0 @>

    // A run still IN FLIGHT is the same kind of fact: the daemon answered, and its answer
    // is "not yet". The inner loop keeps tolerating it (`confirm` does not — see below).
    let running =
        hostWith [ FsHotWatch.Cli.IpcParsing.TestScopeCommand, fun _ -> """{"scope":"running"}""" ]

    test <@ innerLoopExitFor (scopeReadFrom running) = 0 @>

[<Fact(Timeout = 15000)>]
let ``confirm refuses every scope it did not positively establish — fault or not`` () =
    // The split above may not open a door on the confirmation side. Exit 3 across the board.
    let confirmExit (scope: FsHotWatch.Cli.IpcParsing.TestScope) =
        let inputs: FsHotWatch.Cli.CheckVerdict.CheckInputs =
            { PluginStatuses = Map.empty
              FailingDiagnostics = 0
              UnattributableDiagnostics = 0
              WaitingOnBuild = FsHotWatch.Cli.CheckVerdict.BuildWait.NotWaiting
              RunnerAborted = FsHotWatch.Cli.CheckVerdict.RunnerAbort.NoAbort
              Coverage = FsHotWatch.Cli.IpcParsing.Complete
              Scope = scope }

        FsHotWatch.Cli.CheckVerdict.verdict FsHotWatch.Cli.CheckVerdict.Confirmation inputs
        |> FsHotWatch.Cli.CheckVerdict.exitCode

    // Positive control: a real full suite is the one thing that passes.
    let full =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.TestScopeCommand,
              fun _ -> """{"scope":"full","ranProjects":3,"totalProjects":3}""" ]

    test <@ confirmExit (scopeReadFrom full) = 0 @>

    test <@ confirmExit (scopeReadFrom (hostWith [])) = 3 @>

    let threw =
        hostWith [ FsHotWatch.Cli.IpcParsing.TestScopeCommand, fun _ -> failwith "boom" ]

    test <@ confirmExit (scopeReadFrom threw) = 3 @>

[<Fact(Timeout = 15000)>]
let ``requestFullSuiteScope sends set-scope full to the host`` () =
    // AUTOMATION-129: `confirm` must address the COMMAND (`set-scope`), not the plugin
    // (`test-prune`). It once passed the plugin name in the command slot, so the host
    // resolved nothing and `confirm` could never establish a full-suite scope on any repo.
    // It failed SAFE, which is why nothing caught it.
    let mutable received: string list = []

    let host =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.SetScopeCommand,
              fun args ->
                  received <- List.ofArray args
                  """{"scope":"full"}""" ]

    FsHotWatch.Cli.RunOnceCheck.requestFullSuiteScope host

    test <@ received = [ FsHotWatch.Cli.IpcParsing.FullSuiteScopeArgs ] @>

[<Fact(Timeout = 15000)>]
let ``requestFullSuiteScope survives a host that has no set-scope command`` () =
    // A repo with no test projects has no such command. Not a crash — `readTestRun` refuses
    // it on the evidence instead.
    FsHotWatch.Cli.RunOnceCheck.requestFullSuiteScope (hostWith [])

[<Fact(Timeout = 15000)>]
let ``requestFullRun asks the host to run EVERY project — no filter, no selection`` () =
    // `run-tests` with an empty payload means "all configured projects, unfiltered" — a
    // filter or project list here would be `confirm` narrowing what it exists to widen.
    let mutable received: string list = []

    let host =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.RunTestsCommand,
              fun args ->
                  received <- List.ofArray args
                  """{"status":"ok"}""" ]

    FsHotWatch.Cli.RunOnceCheck.requestFullRun host

    test <@ received = [ "{}" ] @>

[<Fact(Timeout = 15000)>]
let ``requestFullRun survives a host with no run-tests command`` () =
    // Nothing to force. Not a crash — `readTestRun` refuses it on the evidence instead.
    FsHotWatch.Cli.RunOnceCheck.requestFullRun (hostWith [])

[<Fact(Timeout = 15000)>]
let ``requestFullRun survives a THROWING run-tests command`` () =
    // A forced run that blows up has produced no evidence. `confirm` must still reach its
    // verdict and refuse, not die with a stack trace on the way there.
    let host =
        hostWith [ FsHotWatch.Cli.IpcParsing.RunTestsCommand, fun _ -> failwith "runner exploded" ]

    FsHotWatch.Cli.RunOnceCheck.requestFullRun host

// ---------------------------------------------------------------------------
// AUTOMATION-167 — the SECOND transport, over the same defect.
//
// `--run-once` is what CI runs, so this is the transport whose exit code actually gates
// a merge. It publishes through the very same `IpcOutput.publishVerdict`, so it inherits
// the same requirement: the tree it captures at ITS settle boundary (the in-process
// scan's return) is the only thing a publication-time hash can honestly be compared with.
// ---------------------------------------------------------------------------

/// The real `--run-once` driver over a repo carrying one tracked file, rewriting that
/// file from inside `renderSummary` when `moveTree` — i.e. after the in-process scan has
/// settled and before the verdict is published.
///
/// `renderSummary` is the production injection point the CLI already hands its own
/// renderer to; nothing was added to `RunOnceCheck` to make this window reachable from a
/// test.
let private runOnceWithTreeMovedMidCheck (moveTree: bool) (repoRoot: string) : int =
    // `withProjectOnlyRepo` already created `src/`, which is a discovery root — so
    // `TreeHash` walks this file, and rewriting it genuinely moves the tree.
    let tracked = System.IO.Path.Combine(repoRoot, "src", "Tracked.txt")
    System.IO.File.WriteAllText(tracked, "the content the check was run against")

    let mutable moved = false

    let createDaemon (root: string) =
        Daemon.createWith
            (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
            root
            Daemon.DaemonOptions.defaults

    FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdict
        (fun _ ->
            if moveTree && not moved then
                moved <- true
                System.IO.File.WriteAllText(tracked, "an edit that landed while the check was finishing")

            "")
        FsHotWatch.Cli.CheckVerdict.InnerLoop
        false
        createDaemon
        repoRoot
        (noTestProjectsConfig ())
        None

/// The verdict the drive above left on disk, read inside the temp repo's lifetime.
let private verdictOnDisk (repoRoot: string) : FsHotWatch.Cli.Verdict.Verdict =
    match FsHotWatch.Cli.Verdict.read repoRoot with
    | FsHotWatch.Cli.Verdict.Reading.Found v -> v
    | other -> failwithf "expected a published verdict, got %A" other

[<Fact(Timeout = 60000)>]
let ``run-once: a tree that moves mid-check exits 2 AND records incomplete`` () =
    withProjectOnlyRepo "runonce-167-tree-moved" (fun repoRoot ->
        // The code CI gates on...
        let exitCode = runOnceWithTreeMovedMidCheck true repoRoot
        test <@ exitCode = 2 @>

        // ...and the file a deploy preflight gates on. One decision, two renderings.
        let v = verdictOnDisk repoRoot
        test <@ v.ExitCode = 2 @>

        match v.Outcome with
        | FsHotWatch.Cli.Verdict.Incomplete reason -> test <@ reason.Contains "working tree changed" @>
        | other -> failwithf "a tree that moved under the check must record INCOMPLETE, got %A" other)

[<Fact(Timeout = 60000)>]
let ``run-once: the same drive over a tree that HOLDS STILL is green — 0 in both renderings`` () =
    // The control: same driver, same repo, same config — the ONLY variable is whether the
    // tracked file was rewritten inside the window.
    withProjectOnlyRepo "runonce-167-tree-still" (fun repoRoot ->
        let exitCode = runOnceWithTreeMovedMidCheck false repoRoot
        test <@ exitCode = 0 @>

        let v = verdictOnDisk repoRoot
        test <@ v.ExitCode = 0 @>
        test <@ v.Outcome = FsHotWatch.Cli.Verdict.Green @>)
