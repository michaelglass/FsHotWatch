/// `RequiredConfig.ofJson` (FsHotWatch.Core) is the ONE definition of which plugins a green
/// requires (AUTOMATION-1695). The daemon's parser still decides which plugins it CONFIGURES, from
/// the same JSON. If the two ever disagree, a plugin runs that R(c, T) does not require (a red
/// it cannot see), or R(c, T) requires one that never runs (missing forever). This test holds them
/// together over real configurations and one configuration per branch of each rule.
module FsHotWatch.Tests.RequiredConfigParityTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Tests.TestHelpers

/// The plugins the parsed configuration configures, by the names they register under: the
/// derivation `requiredPluginNames` used before `RequiredConfig` (the oracle, kept here only).
let private configured (config: DaemonConfig.DaemonConfiguration) : Set<string> =
    set
        [ for p in config.Preprocessors do
              p.Name
          match config.Format with
          | DaemonConfig.Auto -> "format"
          | DaemonConfig.Check -> "format-check"
          | DaemonConfig.Off -> ()
          if config.Lint then
              "lint"
          if config.Analyzers.IsSome then
              PluginActivity.AnalyzersPluginName
          match config.Build with
          | Some builds when not builds.IsEmpty -> "build"
          | _ -> ()
          for fc in config.FileCommands do
              fc.PluginName
          if not (List.isEmpty config.Coverage) then
              "coverage"
          if config.Tests.IsSome then
              PluginActivity.TestPrunePluginName ]

/// What plugin `name` of the parsed `config` is a function of: the derivation the shadow used from
/// the parsed configuration before `RequiredConfig` read it from the raw JSON (AUTOMATION-1577; the
/// oracle, kept here only). Only a file command with a `pattern` and no `afterTests` declares its
/// inputs, keyed by its configuration entry as the daemon parsed it.
let private eligibilityOf (config: DaemonConfig.DaemonConfiguration) (name: string) : PluginInputs.LookupEligibility =
    let wholeTree reason =
        PluginInputs.LookupEligibility.NoDeclaredInputs reason

    if config.Preprocessors |> List.exists (fun p -> p.Name = name) then
        wholeTree $"preprocessor %s{name} runs over the files a check delivers"
    else
        match config.FileCommands |> List.tryFind (fun fc -> fc.PluginName = name) with
        | Some fc ->
            match fc.Pattern, fc.AfterTests with
            | Some pattern, None ->
                PluginInputs.LookupEligibility.DeclaredInputs(
                    PluginInputs.InputSpec.Matching(
                        Watcher.FilePattern.parse pattern,
                        PluginInputs.entryDigest name pattern fc.Command fc.Args fc.TimeoutSec
                    ),
                    []
                )
            | _, Some _ -> wholeTree $"file command %s{name} runs after the tests, so its inputs are their outcomes"
            | None, None -> wholeTree $"file command %s{name} declares no pattern"
        | None when name = PluginActivity.TestPrunePluginName ->
            wholeTree "test-prune runs the configured test projects over the whole tree"
        | None -> wholeTree $"%s{name} reads the whole tree"

/// The daemon's parse of `json` with the real defaults, through a `.fshw.json` in a scratch root.
let private parsed (json: string) =
    withTempDir "required-config-parity" (fun dir ->
        File.WriteAllText(Path.Combine(dir, ".fshw.json"), json)
        DaemonConfig.loadConfig dir)

let private holds (json: string) =
    let required = RequiredConfig.ofJson json
    let config = parsed json
    test <@ Set.ofList (required.Plugins |> List.map _.Name) = configured config @>

    // And each one is keyed by what the daemon's parse says it is a function of.
    let eligibility = required.Plugins |> List.map (fun p -> p.Name, p.Eligibility)

    let expected =
        required.Plugins |> List.map (fun p -> p.Name, eligibilityOf config p.Name)

    test <@ eligibility = expected @>

    let projects =
        match config.Tests with
        | Some tests -> tests.Projects |> List.map _.Project
        | None -> []

    test <@ required.TestProjects = projects @>
    // The tree a pure lookup hashes is the daemon's (`Lookup.ofRepo`).
    test <@ required.Exclude = config.Exclude @>

/// One configuration per branch of each rule `RequiredConfig.ofJson` reads.
let private branches =
    [ "{}"
      """{ "format": "check" }"""
      """{ "format": "off" }"""
      """{ "format": false }"""
      """{ "format": true }"""
      """{ "format": "auto" }"""
      """{ "lint": false }"""
      """{ "lint": true }"""
      """{ "build": false }"""
      """{ "build": [] }"""
      """{ "build": { "command": "dotnet", "args": "build" } }"""
      """{ "build": [ { "command": "dotnet" }, { "command": "make" } ] }"""
      """{ "analyzers": { "paths": [ "a/" ] } }"""
      """{ "analyzers": { "paths": [] } }"""
      """{ "analyzers": {} }"""
      """{ "fileCommands": [ { "name": "gate", "pattern": "*.md", "command": "echo" } ] }"""
      """{ "fileCommands": [ { "pattern": "*.fsx", "command": "echo" } ] }"""
      """{ "fileCommands": [ { "name": "after", "afterTests": true, "command": "echo" } ] }"""
      """{ "fileCommands": [ { "name": "g", "pattern": "*.md", "command": "echo", "args": "-n x", "timeoutSec": 30 } ] }"""
      """{ "fileCommands": [ { "name": "g", "pattern": "build.fsx", "command": "dotnet", "args": "fsi build.fsx" } ] }"""
      """{ "fileCommands": [ { "name": "p", "pattern": "*.md", "afterTests": [ "P" ], "command": "echo" } ] }"""
      """{ "fileCommands": [ { "name": "e", "pattern": "*.md", "afterTests": [], "command": "echo" } ] }"""
      """{ "fileCommands": [ { "name": "f", "pattern": "*.md", "afterTests": false, "command": "echo" } ] }"""
      """{ "preprocessors": [ { "name": "gate", "command": "true" } ], "fileCommands": [ { "name": "gate", "pattern": "*.md", "command": "echo" } ] }"""
      """{ "coverage": { "configPath": "c.json", "searchDir": "." } }"""
      """{ "coverage": [ { "configPath": "c.json", "searchDir": "." }, { "configPath": "d.json", "searchDir": "." } ] }"""
      """{ "preprocessors": [ { "name": "pre", "command": "true" }, { "name": "two", "command": "true" } ] }"""
      """{ "tests": { "projects": [ { "project": "P", "command": "true", "args": "" } ] } }"""
      """{ "tests": { "projects": [] } }"""
      """{ "exclude": [ "docs/**", "*.md" ] }"""
      """{ "exclude": [] }""" ]

[<Fact>]
let ``every branch of every rule: the required plugins are the configured ones`` () =
    for json in branches do
        holds json

[<Fact>]
let ``this repository's configuration: the required plugins are the configured ones`` () =
    holds (File.ReadAllText(Path.Combine(RepoTasks.repoRoot (), ".fshw.json")))

/// A consumer's checkout on a machine that has one (`FSHW_TEST_CONSUMER_CHECKOUT`, as in
/// `RequiredConfigGoldenTests`); its configuration is the consumer's, not kept here. Where it is
/// unset (CI, the server's gate) the case is reported SKIPPED with why, never passed.
[<Fact>]
let ``a consumer's configuration: the required plugins are the configured ones`` () =
    match Environment.GetEnvironmentVariable "FSHW_TEST_CONSUMER_CHECKOUT" with
    | null
    | "" -> Assert.Skip "FSHW_TEST_CONSUMER_CHECKOUT is unset: no consumer configuration on this machine"
    | checkout ->
        let path = Path.Combine(checkout, ".fshw.json")

        if not (File.Exists path) then
            Assert.Skip $"FSHW_TEST_CONSUMER_CHECKOUT names %s{checkout}, which has no .fshw.json"

        holds (File.ReadAllText path)
