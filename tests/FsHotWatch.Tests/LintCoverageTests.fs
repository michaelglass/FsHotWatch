/// FSharpLint runs as an analyzer: the FSharpLint shim is one `.fshw.json`
/// `analyzers.paths` entry, and `analyzers.scope` decides which files each path's
/// analyzers see. A scope that leaves a project out leaves it unlinted, and an unlinted
/// project and a clean one look the same from the outside. That is how TestPrune's
/// projects lost FSharpLint coverage when its history was imported here (AUTOMATION-1409):
/// the shim's scope excluded them, and nothing else ran FSharpLint over them.
module FsHotWatch.Tests.LintCoverageTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

/// The project directories `FsHotWatch.slnx` lists, repository-relative.
let private solutionProjectDirs root =
    let slnx = File.ReadAllText(Path.Combine(root, "FsHotWatch.slnx"))

    Regex.Matches(slnx, "<Project Path=\"([^\"]+)\"")
    |> Seq.map (fun m -> Path.GetDirectoryName(m.Groups[1].Value).Replace('\\', '/'))
    |> Seq.distinct
    |> Seq.toList

/// The configured analyzer path that loads the FSharpLint shim.
let private shimPath root =
    match configuredAnalyzerPaths root |> List.filter _.Contains("fsharplint-shim") with
    | [ path ] -> path
    | paths -> failwith $"expected exactly one FSharpLint shim analyzer path, found %A{paths}"

[<Fact>]
let ``the FSharpLint shim runs on every project in the solution`` () =
    let root = repoRoot ()
    let config = FsHotWatch.Cli.DaemonConfig.loadConfig root

    let analyzers =
        config.Analyzers
        |> Option.defaultWith (fun () -> failwith ".fshw.json configures no analyzers")

    let runsOn =
        FsHotWatch.Analyzers.AnalyzersPlugin.runsOn (Some root) analyzers.Scopes

    let shim = shimPath root

    // A source file that would sit in each project: the scope decides by path alone.
    let unlinted =
        solutionProjectDirs root
        |> List.filter (fun dir -> not (runsOn shim (Path.Combine(root, dir, "Probe.fs"))))

    test <@ List.isEmpty unlinted @>

[<Fact>]
let ``every project in the solution has a fsharplint.json in the repository above it`` () =
    // The shim walks up from each file to the nearest fsharplint.json and falls back to
    // FSharpLint's built-in defaults when it finds none, which is a different rule set
    // from the one this repository reviewed.
    let root = repoRoot () |> Path.GetFullPath

    let rec hasConfig (dir: string) =
        if File.Exists(Path.Combine(dir, "fsharplint.json")) then
            true
        elif dir = root then
            false
        else
            hasConfig (Path.GetDirectoryName dir)

    let withoutConfig =
        solutionProjectDirs root
        |> List.filter (fun dir -> not (hasConfig (Path.GetFullPath(Path.Combine(root, dir)))))

    test <@ List.isEmpty withoutConfig @>
