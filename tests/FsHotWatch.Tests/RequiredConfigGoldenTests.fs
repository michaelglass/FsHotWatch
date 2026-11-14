/// What a real configuration requires of a green: its required plugin names and its test projects,
/// as the daemon's parser yields them today (AUTOMATION-1695, Phase 1 step C). Captured BEFORE the
/// reading of these two lists moves into FsHotWatch.Core, and the same goldens must hold after it.
module FsHotWatch.Tests.RequiredConfigGoldenTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli

/// The two lists, read the way R(c, T) reads them (`ShadowVerdict.configuredOf`).
let private requiredOf (repoRoot: string) =
    DaemonConfig.configuredPlugins repoRoot,
    FsHotWatch.TestEvidenceKey.configsOf repoRoot
    |> Result.map (Map.keys >> List.ofSeq >> List.sort)

[<Fact>]
let ``this repository's configuration requires what it required before the move`` () =
    let plugins, projects = requiredOf (FsHotWatch.Tests.RepoTasks.repoRoot ())
    test <@ plugins = [ "format"; "analyzers"; "build"; "coverage"; "test-prune" ] @>

    test
        <@
            projects = Ok
                [ "FsHotWatch.Bench.Tests"
                  "FsHotWatch.Simulation.Tests"
                  "FsHotWatch.Tests"
                  "TestPrune.Tests"
                  "TestPrune.Trace.Tests" ]
        @>

/// A consumer's checkout, on a machine that has one: `FSHW_TEST_CONSUMER_CHECKOUT` names it, and
/// `FSHW_TEST_CONSUMER_REQUIRED` holds what it required before the move, as
/// `plugin,plugin|project,project` (its configuration is the consumer's, not kept here).
[<Fact>]
let ``a consumer's configuration requires what it required before the move`` () =
    match
        Environment.GetEnvironmentVariable "FSHW_TEST_CONSUMER_CHECKOUT",
        Environment.GetEnvironmentVariable "FSHW_TEST_CONSUMER_REQUIRED"
    with
    | (null | ""), _
    | _, (null | "") -> ()
    | checkout, expected when Directory.Exists checkout ->
        let plugins, projects = requiredOf checkout
        let render (items: string list) = String.Join(",", items)

        let actual =
            match projects with
            | Ok projects -> $"%s{render plugins}|%s{render projects}"
            | Error why -> $"%s{render plugins}|ERROR %s{why}"

        test <@ actual = expected @>
    | _ -> ()
