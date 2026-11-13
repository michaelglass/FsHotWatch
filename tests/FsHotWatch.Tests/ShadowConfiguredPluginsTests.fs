/// ADR-059 §3 and §7 step 2 (AUTOMATION-1568): `plugin:<name>` is required once per CONFIGURED
/// non-test plugin, computed from the configuration and not from the run. A plugin that is idle
/// with no run record is left out of today's `plugins[]` (`Verdict.pluginVerdicts`), so a
/// requirement set read from the verdict cannot miss what the verdict omits: the lookup agreed
/// green over a configured gate nobody ran on the tree. Test projects already come from the
/// configuration (`ShadowVerdictTests`); these pin the plugins.
module FsHotWatch.Tests.ShadowConfiguredPluginsTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Cli.ShadowVerdict
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero)

/// A green check over T1 that reported `plugins` and ran no tests (none are configured).
let private checkReporting (plugins: (string * OutcomeStore.Outcome option) list) : Observation =
    { Command = Verdict.Check
      Tree = "sha256:t1"
      Producer = "fshw 1"
      Worktree = "/w"
      Today = Class.Green
      TodayOutcome = "green"
      ExitCode = 0
      Plugins = plugins
      DiagnosticsFailed = false
      GradedRunId = None
      BaselineRunId = None
      Scope = GradedScope.Unscoped
      Suites = []
      RunOrder = []
      RecordedAt = t0
      OutcomeKeys = Map.empty }

let private pass = Some OutcomeStore.Outcome.Pass

/// Run the production shadow (`observe`) in a repository whose `.fshw.json` is `config`, and
/// return the line it logged.
let private shadowLineFor (config: string) (o: Observation) =
    withTempDir "shadow-plugins" (fun repo ->
        File.WriteAllText(FsHwPaths.configFile repo, config)
        observe repo o
        let line = File.ReadAllLines(logPath repo) |> Array.exactlyOne
        Nodes.JsonNode.Parse line)

/// Build (the default), lint, no formatter.
let private lintOnly = """{ "lint": true, "format": false }"""

/// Build (the default) alone.
let private buildOnly = """{ "lint": false, "format": false }"""

/// Build (the default) and one test project.
let private withTests =
    """{ "lint": false, "format": false, "tests": { "projects": [ { "project": "A.Tests", "command": "dotnet", "args": "test" } ] } }"""

let private at (path: string list) (n: Nodes.JsonNode) =
    path |> List.fold (fun (x: Nodes.JsonNode) (k: string) -> x[k]) n

let private str path n = (at path n).GetValue<string>()
let private flag path n = (at path n).GetValue<bool>()

let private missing (n: Nodes.JsonNode) =
    (at [ "lookup"; "missing" ] n).AsArray()
    |> Seq.map (fun x -> x.GetValue<string>())
    |> List.ofSeq

// The ticket's positive control: `.fshw.json` configures lint, lint never reported, and the
// store holds a pass for everything that did. Today is green; per ADR-059 §3 the lookup is
// missing `plugin:lint`, i.e. stricter. Before the fix the requirement set was read from the
// verdict, so lint was not required and the line said `agree`.
[<Fact>]
let ``a configured plugin the verdict omits is required and missing, not agreed green`` () =
    let line = shadowLineFor lintOnly (checkReporting [ "build", pass ])

    test <@ (str [ "lookup"; "outcome" ] line) = "missing" @>
    test <@ missing line = [ "plugin:lint" ] @>
    test <@ (str [ "direction" ] line) = "stricter" @>
    test <@ not (flag [ "agree" ] line) @>

// Converse: the same configuration, every configured plugin reported and passing, is green and
// agrees. The fix must not make a fully-reported run look incomplete.
[<Fact>]
let ``every configured plugin reported passing is green and agrees`` () =
    let line = shadowLineFor lintOnly (checkReporting [ "build", pass; "lint", pass ])

    test <@ (str [ "lookup"; "outcome" ] line) = "green" @>
    test <@ (str [ "direction" ] line) = "agree" @>

// Converse: no `.fshw.json` at all is the daemon's default configuration (build, format, lint),
// exactly as `DaemonConfig.loadConfig` registers it, not "no plugins".
[<Fact>]
let ``without a config file the default plugins are required`` () =
    withTempDir "shadow-plugins" (fun repo ->
        observe repo (checkReporting [ "build", pass ])

        let line =
            File.ReadAllLines(logPath repo) |> Array.exactlyOne |> Nodes.JsonNode.Parse

        test <@ missing line = [ "plugin:format"; "plugin:lint" ] @>)

// The test plugin is required beside its configured projects (rework of AUTOMATION-1568). A
// test-prune failure no project carries (an index flush that failed, an impact DB that would
// not open, a wedged plugin) reddens today's verdict through the plugin alone; with only
// `test:<project>` required, the passing records an earlier publication of the same tree filed
// would answer green over it.
[<Fact>]
let ``the test plugin is required beside its configured projects`` () =
    let line =
        shadowLineFor withTests (checkReporting [ "build", pass; FsHotWatch.PluginActivity.TestPrunePluginName, None ])

    test <@ missing line = [ "plugin:test-prune"; "test:A.Tests" ] @>

[<Fact>]
let ``a test-prune failure no project carries is red, even over the tree's passing test records`` () =
    withTempDir "shadow-plugins" (fun repo ->
        File.WriteAllText(FsHwPaths.configFile repo, withTests)
        let testPrune = FsHotWatch.PluginActivity.TestPrunePluginName

        // A green full-suite publication over T1 files A.Tests' passing `full` record.
        observe
            repo
            { checkReporting [ "build", pass; testPrune, pass ] with
                Command = Verdict.Confirm
                GradedRunId = Some "r1"
                Scope = GradedScope.FullSuite
                Suites =
                    [ { RunId = Some "r1"
                        Project = "A.Tests"
                        Counts =
                          { Total = 1
                            Passed = 1
                            Failed = 0
                            Skipped = 0 }
                        Passed = true
                        FailedTests = Some []
                        PassedTests = Set.empty
                        Report = None } ] }

        // The next publication over the same tree: test-prune failed at plugin level, no suite.
        observe
            repo
            { checkReporting [ "build", pass; testPrune, Some OutcomeStore.Outcome.Fail ] with
                Command = Verdict.Confirm
                Today = Class.Red
                TodayOutcome = "red"
                ExitCode = 1
                RecordedAt = t0.AddMinutes 1.0 }

        let line = File.ReadAllLines(logPath repo) |> Array.last |> Nodes.JsonNode.Parse
        test <@ (str [ "lookup"; "outcome" ] line) = "red" @>
        test <@ (str [ "direction" ] line) = "agree" @>)

// A configuration the shadow cannot read is not a silent gap: the publication logs a line that
// says it was not compared and why. It is neither agree nor a disagreement direction, and it
// carries `agree: false`, so ADR-059 §8's counter (`map(select(.agree | not))`) counts it and a
// consecutive-agreement run cannot pass over it.
[<Fact>]
let ``an unreadable configuration logs a not-compared line with its reason`` () =
    withTempDir "shadow-plugins" (fun repo ->
        File.WriteAllText(FsHwPaths.configFile repo, """{ "format": "sideways" }""")
        observe repo (checkReporting [ "build", pass ])
        let text = File.ReadAllLines(logPath repo) |> Array.exactlyOne
        let line = Nodes.JsonNode.Parse text
        test <@ (str [ "direction" ] line) = "not-compared" @>
        test <@ (str [ "lookup"; "outcome" ] line) = "not-compared" @>
        test <@ (str [ "lookup"; "reason" ] line).Contains "sideways" @>
        test <@ not (flag [ "agree" ] line) @>
        test <@ (str [ "today"; "outcome" ] line) = "green" @>
        // `fshw analytics` reads it as a line, not as an unreadable one.
        test
            <@
                OutcomeAnalytics.parseShadowLine text
                |> Option.map (fun l -> ShadowVerdict.Direction.token l.Direction) = Some "not-compared"
            @>)

[<Fact>]
let ``a read over an unreadable configuration logs a not-compared verdict line`` () =
    withTempDir "shadow-plugins" (fun repo ->
        File.WriteAllText(FsHwPaths.configFile repo, "{ not json")

        let tree: TreeHash.Tree =
            { Hash = "sha256:now"
              FileCount = 1
              SkippedCount = 0
              DeclaredCount = 0
              AbsentDeclarationCount = 0 }

        let published =
            Verdict.create
                Verdict.Check
                (BaselineFixtures.reportOf (IpcParsing.FullSuite(2, None)))
                tree
                (Some [])
                (Verdict.Green BaselineFixtures.baseline)
                0
                []
                []
                Verdict.CheckComparison.notRecorded
                []
                ProjectModelFixtures.available
                (fun _ -> Map.empty)

        observeRead repo (Verdict.Report.Stale(published, "moved")) (Some tree)

        let line =
            File.ReadAllLines(logPath repo) |> Array.exactlyOne |> Nodes.JsonNode.Parse

        test <@ (str [ "command" ] line) = "verdict" @>
        test <@ (str [ "direction" ] line) = "not-compared" @>
        test <@ not (flag [ "agree" ] line) @>)

// ADR-059 §8 measures the switch as "0 disagreements over 50 consecutive" lines with
// `jq '... | .[-50:] | map(select(.agree | not)) | length'`, and "A disagreement resets the
// count." A not-compared line between two agreeing ones breaks the run exactly as that counter
// reads it.
[<Fact>]
let ``a not-compared line breaks a run of agreeing lines as ADR-059 section 8 counts it`` () =
    withTempDir "shadow-plugins" (fun repo ->
        let config = FsHwPaths.configFile repo
        File.WriteAllText(config, buildOnly)
        observe repo (checkReporting [ "build", pass ])
        File.WriteAllText(config, "{ not json")

        observe
            repo
            { checkReporting [ "build", pass ] with
                Tree = "sha256:t2" }

        File.WriteAllText(config, buildOnly)

        observe
            repo
            { checkReporting [ "build", pass ] with
                Tree = "sha256:t3" }

        let lines =
            File.ReadAllLines(logPath repo)
            |> Array.map Nodes.JsonNode.Parse
            |> List.ofArray

        // §8's counter, line for line: non-verdict lines, the last 50, those that do not agree.
        let notAgreeing =
            lines
            |> List.filter (fun l -> str [ "command" ] l <> "verdict")
            |> List.rev
            |> List.truncate 50
            |> List.filter (fun l -> not (flag [ "agree" ] l))

        test <@ lines |> List.map (str [ "direction" ]) = [ "agree"; "not-compared"; "agree" ] @>
        test <@ notAgreeing.Length = 1 @>)

// A plugin the verdict reports but the configuration does not register is not required: the
// requirement set is the configuration's, in both directions.
[<Fact>]
let ``a reported plugin the configuration does not register is not required`` () =
    let line = shadowLineFor buildOnly (checkReporting [ "build", pass; "lint", None ])

    test <@ (str [ "lookup"; "outcome" ] line) = "green" @>

// The names must be the ones the daemon's plugins report their status under, or every
// requirement would be missing by spelling. This registers a configuration of every plugin
// kind but analyzers (which need built analyzer DLLs; its name is the plugin's own constant)
// on a real daemon and compares: what registers, test plugin included, is what is required.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the required plugins are exactly the ones the daemon registers`` () =
    withTempDir "shadow-plugins-register" (fun repo ->
        Directory.CreateDirectory(Path.Combine(repo, "src")) |> ignore

        let config =
            DaemonConfig.parseConfig
                """{ "preprocessors": [ { "name": "pre", "command": "true" } ],
                     "format": "check",
                     "lint": true,
                     "fileCommands": [ { "name": "gate", "pattern": "*.md", "command": "echo" } ],
                     "coverage": { "configPath": "coverage-ratchet.json", "searchDir": "." },
                     "tests": { "projects": [ { "project": "P", "command": "true", "args": "", "coverage": false, "timeoutSec": 10 } ] } }"""
                (defaultTestConfig ())

        let daemon =
            FsHotWatch.Daemon.Daemon.createWith (Unchecked.defaultof<_>) repo oneShotDaemonOptions

        DaemonConfig.registerPlugins daemon repo config

        let registered = daemon.Host.GetAllStatuses() |> Map.keys |> Set.ofSeq

        test <@ registered = Set.ofList (DaemonConfig.requiredPluginNames config) @>
        test <@ registered.Contains FsHotWatch.PluginActivity.TestPrunePluginName @>
        test <@ registered.Count = 7 @>)

[<Fact>]
let ``the formatter, analyzers and coverage are named as their plugins name themselves`` () =
    let config =
        { DaemonConfig.parseConfig """{ "lint": false, "format": "auto", "build": [] }""" (defaultTestConfig ()) with
            Analyzers =
                Some
                    {| Paths = [ "analyzers/" ]
                       FailOnSeverity = FsHotWatch.ErrorLedger.DiagnosticSeverity.Warning
                       BootstrapHints = Map.empty
                       Scopes = Map.empty |} }

    // An empty build list registers no build plugin, so none is required.
    test <@ DaemonConfig.nonTestPluginNames config = [ "format"; FsHotWatch.PluginActivity.AnalyzersPluginName ] @>
