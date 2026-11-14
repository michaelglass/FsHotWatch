/// AUTOMATION-1577 (ADR-074, "Every whole-tree plugin has a record on every concluded check"): a
/// plugin that declares no inputs is keyed by the whole tree, so only a run on THIS tree answers
/// for it. If such a plugin could sit idle on a check, it would be missing forever, the same bug
/// 1577 fixed for pattern commands. This holds every real configuration to the rule: each
/// required plugin that declares no inputs is either a preprocessor (run over every batch a check
/// delivers) or registered with a subscription to an event a daemon's first scan delivers.
///
/// LIMITS, stated:
/// - A subscription is not an execution. A plugin subscribed to such an event could still filter
///   it out and not run. Execution is covered by ADR-074's measurement instead: across 253
///   daemon-green publications of alpha.140-149 no whole-tree plugin was ever missing, only the
///   two pattern commands.
/// - `TestRunCompleted` counts as delivered because a check of a configuration with tests runs
///   them; a plugin waiting on it (coverage, an `afterTests` command) runs on every check that
///   runs tests.
/// - Declared test exclusions (`tests.excluded`) are dropped: they resolve against the repository's
///   solution, which a scratch checkout has not got, and they decide which tests run, not which
///   plugins register.
/// - The analyzers plugin is not registered here: registering it loads the analyzer assemblies its
///   paths name, which a test checkout does not have. It subscribes to `FileChecked`
///   (`FsHotWatch.Analyzers.AnalyzersPlugin`), which every first scan delivers for every file.
module FsHotWatch.Tests.WholeTreeRunConditionTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers

/// The events a daemon's first scan of a checkout delivers.
let private firstScanEvents =
    set
        [ SubscribeFileChanged
          SubscribeFileChecked
          SubscribeBatchChecked
          SubscribeBuildCompleted
          SubscribeTestRunCompleted ]

/// The required plugins of `json` that declare no inputs and that nothing a first scan delivers
/// would run: each with what it subscribes to. Empty when the rule holds.
let private idleable (json: string) : (string * string) list =
    withTempDir "whole-tree-run-condition" (fun repo ->
        Directory.CreateDirectory(Path.Combine(repo, "src")) |> ignore
        let parsed = DaemonConfig.parseConfig json (defaultTestConfig ())

        // Registered as the configuration says, less what a scratch checkout cannot hold: the
        // analyzers (their assemblies) and the declared test exclusions (they resolve against the
        // repository's solution). Neither decides which plugins are registered or what they hear.
        let config =
            { parsed with
                Analyzers = None
                Tests = parsed.Tests |> Option.map (fun t -> {| t with Excluded = [] |}) }

        let daemon =
            FsHotWatch.Daemon.Daemon.createWith (Unchecked.defaultof<_>) repo oneShotDaemonOptions

        DaemonConfig.registerPlugins daemon repo config

        let subscriptions = daemon.Host.RegisteredSubscriptions()
        let preprocessors = daemon.Host.PreprocessorNames()

        [ for p in DaemonConfig.requiredPlugins config do
              match p.Eligibility with
              | PluginInputs.LookupEligibility.DeclaredInputs _ -> ()
              | PluginInputs.LookupEligibility.NoDeclaredInputs _ when List.contains p.Name preprocessors -> ()
              | PluginInputs.LookupEligibility.NoDeclaredInputs _ ->
                  match Map.tryFind p.Name subscriptions with
                  | Some events when not (Set.isEmpty (Set.intersect events firstScanEvents)) -> ()
                  | Some events -> p.Name, String.Join(", ", events |> Seq.map string)
                  | None -> p.Name, "not registered" ])

/// One configuration of every plugin kind a `.fshw.json` can produce.
let private everyKind =
    """{ "preprocessors": [ { "name": "pre", "command": "true" } ],
         "format": "check",
         "lint": true,
         "build": [ { "command": "true" } ],
         "fileCommands": [ { "name": "gate", "pattern": "*.md", "command": "echo" },
                           { "name": "after", "afterTests": true, "command": "echo" } ],
         "coverage": { "configPath": "coverage-ratchet.json", "searchDir": "." },
         "tests": { "projects": [ { "project": "P", "command": "true", "args": "", "coverage": false, "timeoutSec": 10 } ] } }"""

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``every whole-tree plugin of every kind runs on an event a first scan delivers`` () =
    test <@ idleable everyKind = [] @>
    test <@ idleable """{ "format": "auto" }""" = [] @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``every whole-tree plugin FsHotWatch's own configuration requires runs on a first scan`` () =
    test <@ idleable (File.ReadAllText(Path.Combine(RepoTasks.repoRoot (), ".fshw.json"))) = [] @>

/// A consumer's checkout on a machine that has one (`FSHW_TEST_CONSUMER_CHECKOUT`: intelligence's).
/// Where it is unset (CI, the server's gate) the case is reported SKIPPED with why, never passed:
/// FsHotWatch's own configuration and the every-kind one above carry the guarantee there.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``every whole-tree plugin a consumer's configuration requires runs on a first scan`` () =
    match Environment.GetEnvironmentVariable "FSHW_TEST_CONSUMER_CHECKOUT" with
    | null
    | "" -> Assert.Skip "FSHW_TEST_CONSUMER_CHECKOUT is unset: no consumer configuration on this machine"
    | checkout ->
        let path = Path.Combine(checkout, ".fshw.json")

        if not (File.Exists path) then
            Assert.Skip $"FSHW_TEST_CONSUMER_CHECKOUT names %s{checkout}, which has no .fshw.json"

        test <@ idleable (File.ReadAllText path) = [] @>
