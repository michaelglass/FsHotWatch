/// AUTOMATION-448. The daemon refuses to start when a configured analyzer path
/// loads zero analyzers (`DaemonConfig.analyzerPathFailures`, fail-loud on purpose).
/// So every mise task that starts the daemon must first materialize EVERY path in
/// `.fshw.json` `analyzers.paths` — not just the house rules. Before this, `format`
/// in a clean workspace built the rules but not `tools/fsharplint-shim`, and only
/// succeeded on a workspace where some earlier `dotnet build` had happened to
/// populate the shim's bin. These tests pin the task graph to the config so that
/// dependency on workspace history cannot come back.
module FsHotWatch.Tests.AnalyzerMaterializationTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

/// Every mise task that ends in `dotnet run ... -- <daemon verb>`: each one starts
/// the daemon, and the daemon loads the analyzers before any plugin runs.
let daemonBackedTasks = [ "format"; "status"; "check"; "run" ]

let private buildsInClosure mise taskName =
    dependencyClosure mise taskName
    |> Set.toList
    |> List.collect (dotnetBuildTargets mise)

[<Fact>]
let ``every configured analyzer path names exactly one buildable project`` () =
    let root = repoRoot ()
    let paths = configuredAnalyzerPaths root

    test <@ not (List.isEmpty paths) @>

    let projects = paths |> List.map (analyzerProjectFor root)

    test
        <@
            projects
            |> List.forall (fun p -> System.IO.File.Exists(System.IO.Path.Combine(root, p)))
        @>

    test <@ projects |> List.distinct = projects @>

[<Fact>]
let ``each daemon-backed task builds every configured analyzer project exactly once before starting the daemon`` () =
    let root = repoRoot ()
    let mise = miseToml root

    let expected =
        configuredAnalyzerPaths root |> List.map (analyzerProjectFor root) |> Set.ofList

    for taskName in daemonBackedTasks do
        let built = buildsInClosure mise taskName
        let builtAnalyzers = built |> List.filter (fun p -> Set.contains p expected)

        // Present: nothing configured is left to workspace history.
        test <@ Set.ofList builtAnalyzers = expected @>
        // Once: the graph names each analyzer project in one place, so a task that
        // runs it cannot disagree with another about which configuration it built.
        test <@ builtAnalyzers |> List.countBy id |> List.forall (snd >> (=) 1) @>

[<Fact>]
let ``the analyzer build task is a dependency, not a step inside the daemon command`` () =
    // mise runs `depends` to completion before the task's own `run`, so an analyzer
    // project reachable only through `depends` is built strictly before the daemon
    // command starts. A build folded into the daemon task's own `run` would be
    // ordered only by the shell, and would disappear from every OTHER daemon task.
    let root = repoRoot ()
    let mise = miseToml root

    let analyzerProjects =
        configuredAnalyzerPaths root |> List.map (analyzerProjectFor root) |> Set.ofList

    for taskName in daemonBackedTasks do
        let ownBuilds = dotnetBuildTargets mise taskName |> Set.ofList
        test <@ Set.isEmpty (Set.intersect ownBuilds analyzerProjects) @>

        let viaDepends =
            dependencyClosure mise taskName
            |> Set.remove taskName
            |> Set.toList
            |> List.collect (dotnetBuildTargets mise)
            |> Set.ofList

        test <@ Set.isSubset analyzerProjects viaDepends @>

[<Fact>]
let ``daemon-backed task list matches the tasks whose run invokes the CLI`` () =
    // Pins `daemonBackedTasks` to the file, so adding a new `dotnet run ... --` task
    // without listing it here is a test failure rather than a silent audit gap.
    let mise = miseToml (repoRoot ())

    let cliTasks =
        taskNames mise
        |> List.filter (fun name -> (taskBlock mise name).Contains("dotnet run --project src/FsHotWatch.Cli"))
        |> List.filter (fun name -> name <> "ci") // `ci` builds the whole solution via `compile`

    test <@ Set.ofList cliTasks = Set.ofList daemonBackedTasks @>
