/// AUTOMATION-1680, at the level of the GATE: the run-once check driver, its verdict and its
/// exit code, over a repository holding a misformatted file.
///
/// `.fshw.json` `"format": true` registers the format-on-save preprocessor. The scan every
/// check forces ran it, it rewrote the misformatted file in place, and the check went green
/// over the REWRITTEN tree while the commit still held the old bytes: CI's
/// `dotnet fantomas --check` went red on main three times over trees the local gates had
/// passed. A scan that has to rewrite a file now fails the check, naming the file; the next
/// check is about the rewritten bytes and is green. The rewrite's own watcher echo, a change
/// batch over the rewritten bytes, must not erase that failure.
module FsHotWatch.Tests.ScanRewriteGateTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers
open Ionide.ProjInfo

/// One project, loaded the same way every time: no MSBuild in the loop.
type private FixedWorkspaceLoader(project: Types.ProjectOptions) =
    let notifications = Event<Types.WorkspaceProjectState>()

    interface IWorkspaceLoader with
        member _.LoadProjects(_projectPaths) = Seq.singleton project
        member _.LoadProjects(_projectPaths, _customProperties, _binaryLog) = Seq.singleton project
        member _.LoadSln(_solutionPath) = Seq.singleton project
        member _.LoadSln(_solutionPath, _customProperties, _binaryLog) = Seq.singleton project

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

let private loadedProject (projectPath: string) (sourcePath: string) : Types.ProjectOptions =
    { ProjectId = None
      ProjectFileName = projectPath
      TargetFramework = "net10.0"
      SourceFiles = [ sourcePath ]
      OtherOptions = []
      ReferencedProjects = []
      PackageReferences = []
      LoadTime = DateTime.UtcNow
      TargetPath = Path.ChangeExtension(projectPath, ".dll")
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

let private unformatted = "module Fmt\nlet value = 1\n"

/// The pinned Fantomas, as the tool behaves: it rewrites a file that is not formatted (here:
/// one without the trailing blank line) and leaves one that is.
let private formatted = unformatted + "\n"

type private Repo =
    {
        Root: string
        Source: string
        /// A host for one run-once check, with the format preprocessor registered over a
        /// runner that behaves like the pinned tool.
        CreateDaemon: string -> FsHotWatch.Daemon.Daemon
    }

let private withMisformattedRepo (name: string) (body: Repo -> unit) =
    withTempDir name (fun root ->
        let srcDir = Path.Combine(root, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let projectPath = Path.Combine(srcDir, "Fmt.fsproj")
        let sourcePath = Path.Combine(srcDir, "Fmt.fs")

        File.WriteAllText(
            projectPath,
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Fmt.fs" />
  </ItemGroup>
</Project>"""
        )

        File.WriteAllText(sourcePath, unformatted)
        Directory.CreateDirectory(Path.Combine(srcDir, "obj")) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "obj", "project.assets.json"), "{}")
        Directory.CreateDirectory(Path.Combine(root, ".config")) |> ignore

        File.WriteAllText(
            Path.Combine(root, ".config", "dotnet-tools.json"),
            """{ "version": 1, "isRoot": true, "tools": { "fantomas": { "version": "7.0.5", "commands": ["fantomas"] } } }"""
        )

        let checker = sharedChecker.Value

        let fcsOptions =
            let scriptOptions, _ =
                scriptProjectOptions (checker, sourcePath, FSharp.Compiler.Text.SourceText.ofString unformatted)
                |> Async.RunSynchronously

            { scriptOptions with
                ProjectFileName = projectPath
                SourceFiles = [| sourcePath |] }

        let rewritingFantomas: FsHotWatch.Fantomas.FantomasTool.Runner =
            fun _pin args _workDir _timeout ->
                if args.Contains sourcePath && File.ReadAllText sourcePath = unformatted then
                    File.WriteAllText(sourcePath, formatted)

                ProcessHelper.Succeeded(ProcessHelper.ProcessOutput.Drained "")

        let createDaemon (repoRoot: string) =
            let daemon =
                Daemon.createWithWorkspaceLoaderAndWatcher
                    checker
                    repoRoot
                    { oneShotDaemonOptions with
                        RunMode = Daemon.RunMode.OneShot }
                    (FixedWorkspaceLoader(loadedProject projectPath sourcePath))
                    (fun projects -> projects |> List.map (fun _ -> fcsOptions))
                    (fun _ _ _ _ _ -> failwith "--run-once constructs no watcher")

            daemon.RegisterPreprocessor(
                FsHotWatch.Fantomas.FormatCheckPlugin.FormatPreprocessor(runner = rewritingFantomas)
            )

            daemon

        body
            { Root = root
              Source = sourcePath
              CreateDaemon = createDaemon })

/// Nothing but the preprocessor above: no build, no lint, no tests. `Format = Off`, because
/// the preprocessor is registered by `CreateDaemon` over the fake tool.
let private config () : DaemonConfig.DaemonConfiguration =
    { defaultTestConfig () with
        Build = None
        Format = DaemonConfig.Off
        Lint = false
        Tests = None
        Cache = DaemonConfig.NoCache }

/// The format plugin's entry in the verdict the run just published.
let private formatEntry (root: string) =
    match Verdict.read root with
    | Verdict.Reading.Found verdict -> verdict, verdict.Plugins |> List.tryFind (fun p -> p.Name = "format")
    | other -> failwith $"the run published no readable verdict: %A{other}"

let private scanOnly (daemon: FsHotWatch.Daemon.Daemon) =
    daemon.RunOnce() |> Async.RunSynchronously |> ignore

[<Fact(Timeout = InProcessEvaluationTestCapMs)>]
let ``a check over a misformatted file is red naming it, the next is green over the rewritten tree`` () =
    withMisformattedRepo "scan-rewrite-gate" (fun repo ->
        let first =
            RunOnceCheck.runOnceAndVerdict
                (fun _ -> "")
                CheckVerdict.InnerLoop
                false
                repo.CreateDaemon
                repo.Root
                (config ())

        test <@ File.ReadAllText repo.Source = formatted @>
        test <@ first = 1 @>

        let redVerdict, redFormat = formatEntry repo.Root
        test <@ redVerdict.ExitCode = 1 @>

        test
            <@
                match redFormat with
                | Some entry ->
                    entry.Outcome = Verdict.PluginOutcome.Fail
                    && entry.Summary
                       |> Option.exists (fun s ->
                           s.Contains "src/Fmt.fs"
                           && s.Contains "include them in the change and run the check again")
                | None -> false
            @>

        let second =
            RunOnceCheck.runOnceAndVerdict
                (fun _ -> "")
                CheckVerdict.InnerLoop
                false
                repo.CreateDaemon
                repo.Root
                (config ())

        test <@ second = 0 @>

        // The green is about the bytes on disk now: the rewritten tree.
        let greenVerdict, greenFormat = formatEntry repo.Root
        test <@ greenVerdict.ExitCode = 0 @>
        test <@ greenVerdict.TreeHash = (TreeHash.compute repo.Root []).Hash @>

        test
            <@
                match greenFormat with
                | Some entry -> entry.Outcome = Verdict.PluginOutcome.Ok
                | None -> false
            @>)

[<Fact(Timeout = InProcessEvaluationTestCapMs)>]
let ``the rewrite's watcher echo does not turn a scan-time failure green`` () =
    withMisformattedRepo "scan-rewrite-echo" (fun repo ->
        // The scan the check ran, then the change batch its rewrite's watcher echo starts:
        // a pass over the now-formatted bytes, which rewrites nothing. In the reproduction at
        // c12ac32f that pass's "rewrote 0 of 1" replaced the scan's status and the check
        // went green.
        let scanThenEcho (daemon: FsHotWatch.Daemon.Daemon) =
            scanOnly daemon
            let echo = daemon.Host.RunPreprocessors [ repo.Source ]
            test <@ List.isEmpty echo.Modified @>
            daemon.Host.GetAllStatuses()

        let exitCode =
            RunOnceCheck.runOnceAndVerdictWith
                scanThenEcho
                (fun _ -> "")
                CheckVerdict.InnerLoop
                false
                repo.CreateDaemon
                repo.Root
                (config ())

        test <@ exitCode = 1 @>

        let _, format = formatEntry repo.Root

        test
            <@
                match format with
                | Some entry -> entry.Outcome = Verdict.PluginOutcome.Fail
                | None -> false
            @>)
