/// AUTOMATION-1740: a real build beside a real options evaluation does not stop it being filed
/// (AUTOMATION-1726). alpha.150's start built while discovery evaluated, and the filing was
/// refused because ".../bin/Debug/net10.0/publish/ changed while the projects were evaluated".
/// The unit tests write two files by hand; here `dotnet build` and `dotnet publish` write what a
/// build really writes (bin/, obj/<Configuration>/ with its *.cache files and refint/, and
/// publish/) between the evaluation and its filing, through the daemon's own options-records
/// loader over Ionide's MSBuild evaluation. A restart must then replay with no evaluation.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.BuildBesideEvaluationTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Ionide.ProjInfo
open FsHotWatch
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.ColdCheckFixture

let private library =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ProduceReferenceAssembly>true</ProduceReferenceAssembly>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.1.*" />
  </ItemGroup>
</Project>
"""

/// `inner`, running `beside` once each evaluation has returned: what `beside` writes lands after
/// the evaluation started and before the loader around this one files it.
type private BesideLoader(inner: IWorkspaceLoader, beside: unit -> unit) =
    let after (load: unit -> seq<Types.ProjectOptions>) =
        let loaded = load () |> Seq.toList
        beside ()
        Seq.ofList loaded

    interface IWorkspaceLoader with
        member _.LoadProjects(paths) =
            after (fun () -> inner.LoadProjects paths)

        member _.LoadProjects(paths, properties, binaryLog) =
            after (fun () -> inner.LoadProjects(paths, properties, binaryLog))

        member _.LoadSln(solution) =
            after (fun () -> inner.LoadSln solution)

        member _.LoadSln(solution, properties, binaryLog) =
            after (fun () -> inner.LoadSln(solution, properties, binaryLog))

        [<CLIEvent>]
        member _.Notifications = inner.Notifications

/// `inner`, counting the evaluations it runs.
type private CountingLoader(inner: IWorkspaceLoader) =
    let mutable loads = 0
    member _.Loads = loads

    interface IWorkspaceLoader with
        member _.LoadProjects(paths) =
            loads <- loads + 1
            inner.LoadProjects paths

        member _.LoadProjects(paths, properties, binaryLog) =
            loads <- loads + 1
            inner.LoadProjects(paths, properties, binaryLog)

        member _.LoadSln(solution) =
            loads <- loads + 1
            inner.LoadSln solution

        member _.LoadSln(solution, properties, binaryLog) =
            loads <- loads + 1
            inner.LoadSln(solution, properties, binaryLog)

        [<CLIEvent>]
        member _.Notifications = inner.Notifications

let private dotnet clock (root: string) (args: string list) =
    let code, text = run clock root "dotnet" (args @ [ "--disable-build-servers" ])
    let command = String.concat " " args
    Assert.True((code = 0), $"dotnet %s{command}: %s{text}")

[<Fact(Timeout = 900000)>]
let ``a real build and publish beside the options evaluation do not stop it being filed, and a restart replays it`` () =
    let clock = budget ()

    withTempDir "build-beside-evaluation" (fun scratch ->
        // The checkout, and beside it what the test itself writes: a write in the checkout's root
        // while it is evaluated would move the root an absent Directory.Build.props is judged by.
        let root = Path.Combine(scratch, "checkout")
        let aside = Path.Combine(scratch, "aside")
        Directory.CreateDirectory aside |> ignore
        let dir = Path.Combine(root, "Lib")
        Directory.CreateDirectory dir |> ignore
        let project = Path.Combine(dir, "Lib.fsproj")
        File.WriteAllText(project, library)
        File.WriteAllText(Path.Combine(dir, "Lib.fs"), "module Lib\nlet answer = 42\n")
        dotnet clock root [ "restore"; project ]
        // Built, as the incident's checkouts were: the build beside the evaluation rewrites bin/ and
        // obj/Debug/ rather than creating them. (Creating bin/ in a never-built checkout moves the
        // project directory, which judges the absent inputs in it: a separate case, AUTOMATION-1740.)
        dotnet clock root [ "build"; project; "--no-restore" ]
        dotnet clock root [ "publish"; project; "--no-build"; "-c"; "Debug" ]

        // What setup wrote is older than the racy window once the evaluation starts, so only
        // what the build writes beside it can move an input.
        Threading.Thread.Sleep(CheckCache.FileContentHasher.RacyWindow + TimeSpan.FromSeconds 1.0)

        let store =
            OutcomeStore.Store(
                { Root = Path.Combine(aside, ".store")
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        let buildBeside () =
            dotnet clock root [ "build"; project; "--no-restore"; "--no-incremental" ]
            dotnet clock root [ "publish"; project; "--no-build"; "-c"; "Debug" ]

        // Rebuilt in full beside every evaluation, as a start's build went on writing beside a retry.
        let evaluations = ref None

        let first =
            Daemon.Daemon.optionsRecordsLoaderOver
                (fun inner ->
                    let counted = CountingLoader inner
                    evaluations.Value <- Some counted
                    BesideLoader(counted, buildBeside))
                store
                root
                OptionsPortable.Mode.Off

        let optionsLog = Path.Combine(aside, "options.log")

        let loaded =
            use _ = Logging.installSink (Logging.fileSink optionsLog Logging.LogLevel.Info)
            first.LoadProjects [ project ] |> Seq.length

        // What the filer said, so a failure names the input that moved.
        let said =
            File.ReadAllLines optionsLog
            |> Array.filter (fun l -> l.Contains "options:")
            |> List.ofArray

        test <@ loaded = 1 @>
        // Filed on the first evaluation: a refused filing evaluates once more (`CachingLoader`).
        test <@ (evaluations.Value |> Option.map _.Loads, said) = (Some 1, said) @>

        // The build wrote what alpha.150's did, the incident's publish/ included.
        let output = Path.Combine(dir, "bin", "Debug", "net10.0")
        let intermediate = Path.Combine(dir, "obj", "Debug", "net10.0")
        test <@ Directory.Exists(Path.Combine(output, "publish")) @>
        test <@ File.Exists(Path.Combine(intermediate, "refint", "Lib.dll")) @>
        test <@ not (Array.isEmpty (Directory.GetFiles(intermediate, "*.cache"))) @>

        let counting = ref None

        let restarted =
            Daemon.Daemon.optionsRecordsLoaderOver
                (fun inner ->
                    let loader = CountingLoader inner
                    counting.Value <- Some loader
                    loader)
                store
                root
                OptionsPortable.Mode.Off

        test <@ restarted.LoadProjects [ project ] |> Seq.length = 1 @>
        test <@ counting.Value |> Option.map _.Loads = Some 0 @>)
