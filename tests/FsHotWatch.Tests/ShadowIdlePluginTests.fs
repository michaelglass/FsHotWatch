/// AUTOMATION-1577 (ADR-059 §3): a configured plugin that sat idle on a tree is answered by its
/// last outcome when, and only when, the plugin declares its own inputs and those inputs are
/// unchanged. A file command runs only when a file its `pattern` matches changes, so on every
/// other tree it files nothing; keyed by the whole tree, its requirement was missing on every
/// such tree, and the lookup never answered green on a repository that configures one.
///
/// Each history below runs real publications over a real checkout (the requirement set is read
/// from its `.fshw.json`, as the daemon reads it), changes files between them, and reads what
/// the lookup answers.
module FsHotWatch.Tests.ShadowIdlePluginTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Cli.ShadowVerdict
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)

/// A check publication over `tree` in `repo`, reporting `plugins`, with no tests configured.
let private check (repo: string) (tree: string) (minutes: float) plugins : Observation =
    { Command = Verdict.Check
      Tree = tree
      Producer = "fshw 1"
      Worktree = repo
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
      RecordedAt = t0.AddMinutes minutes
      OutcomeKeys = Map.empty }

/// A checkout configured with exactly one plugin besides nothing else: the `*.fsx` file
/// command (format, lint and build off; no tests), holding one match and one unrelated file.
let private fsxOnly =
    """{ "format": "off", "lint": false, "build": false,
  "fileCommands": [ { "pattern": "*.fsx", "command": "true" } ] }"""

let private fsx = "file-cmd-*.fsx"

let private write (repo: string) (rel: string) (text: string) =
    let path = Path.Combine(repo, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private withCheckout (config: string) (body: string -> OutcomeStore.Store -> Configured -> unit) =
    withTempDir "shadow-idle" (fun repo ->
        write repo ".fshw.json" config
        write repo "build.fsx" "printfn \"build\""
        write repo "src/A/a.fs" "module A"

        let store =
            OutcomeStore.Store(
                { Root = storeRoot repo
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )

        match configuredOf repo with
        | Ok configured -> body repo store configured
        | Error reason -> failwith reason)

[<Fact>]
let ``an idle file command whose matches are unchanged answers its last pass`` () =
    withCheckout fsxOnly (fun repo store configured ->
        // T0: the `.fsx` changed, so the file command ran and passed.
        observeWith repo store None configured (check repo "sha256:t0" 0.0 [ fsx, Some OutcomeStore.Outcome.Pass ])
        |> ignore

        // T1: only an unrelated file changed; the file command stayed idle and reported nothing.
        write repo "src/A/a.fs" "module A // edited"
        let answer = observeWith repo store None configured (check repo "sha256:t1" 1.0 [])

        test <@ answer = LookupVerdict.Answer.Green @>)

[<Fact>]
let ``an idle file command whose matches are unchanged answers its last fail`` () =
    withCheckout fsxOnly (fun repo store configured ->
        let red =
            { check repo "sha256:t0" 0.0 [ fsx, Some OutcomeStore.Outcome.Fail ] with
                Today = Class.Red
                TodayOutcome = "red"
                ExitCode = 1 }

        observeWith repo store None configured red |> ignore
        write repo "src/A/a.fs" "module A // edited"

        let idle =
            { check repo "sha256:t1" 1.0 [] with
                Today = Class.Red
                TodayOutcome = "red"
                ExitCode = 1 }

        test <@ observeWith repo store None configured idle = LookupVerdict.Answer.Red [ "plugin:" + fsx ] @>)

[<Fact>]
let ``a changed match makes the idle file command missing until it runs again`` () =
    withCheckout fsxOnly (fun repo store configured ->
        observeWith repo store None configured (check repo "sha256:t0" 0.0 [ fsx, Some OutcomeStore.Outcome.Pass ])
        |> ignore

        // The positive control: the `.fsx` itself changed, so the earlier pass says nothing.
        write repo "build.fsx" "printfn \"changed\""

        test
            <@
                observeWith repo store None configured (check repo "sha256:t1" 1.0 []) = LookupVerdict.Answer.Missing
                    [ "plugin:" + fsx ]
            @>

        // Once it runs on the new content, its outcome answers.
        test
            <@
                observeWith
                    repo
                    store
                    None
                    configured
                    (check repo "sha256:t1" 2.0 [ fsx, Some OutcomeStore.Outcome.Pass ]) = LookupVerdict.Answer.Green
            @>)

[<Fact>]
let ``nothing on record is never green: an idle file command that never ran is missing`` () =
    withCheckout fsxOnly (fun repo store configured ->
        test
            <@
                observeWith repo store None configured (check repo "sha256:t0" 0.0 []) = LookupVerdict.Answer.Missing
                    [ "plugin:" + fsx ]
            @>

        // A pattern that matches no file at all is no different: no record, no green.
        File.Delete(Path.Combine(repo, "build.fsx"))

        test
            <@
                observeWith repo store None configured (check repo "sha256:t1" 1.0 []) = LookupVerdict.Answer.Missing
                    [ "plugin:" + fsx ]
            @>)

[<Fact>]
let ``a plugin with no declared inputs that sat idle is missing, and named as such`` () =
    // The formatter reads the whole tree, so it declares no inputs of its own: only a run on this
    // tree answers for it, and the lookup says that is why it is missing.
    withCheckout """{ "format": "auto", "lint": false, "build": false }""" (fun repo store configured ->
        observeWith
            repo
            store
            None
            configured
            (check repo "sha256:t0" 0.0 [ "format", Some OutcomeStore.Outcome.Pass ])
        |> ignore

        write repo "src/A/a.fs" "module A // edited"
        let answer = observeWith repo store None configured (check repo "sha256:t1" 1.0 [])

        test <@ answer = LookupVerdict.Answer.Missing [ "plugin:format" ] @>
        test <@ noDeclaredInputs configured answer = [ "plugin:format", "format reads the whole tree" ] @>

        let logged =
            Text.Json.Nodes.JsonNode.Parse(File.ReadAllLines(logPath repo) |> Array.last)

        let lookup = logged.Item "lookup"
        let named = (lookup.Item "noDeclaredInputs").AsArray() |> Seq.toList

        let field (name: string) =
            named |> List.map (fun n -> n[name].GetValue<string>())

        test <@ field "requirement" = [ "plugin:format" ] @>
        test <@ field "why" = [ "format reads the whole tree" ] @>)

[<Fact>]
let ``a plugin's declared inputs come from its configuration`` () =
    withCheckout fsxOnly (fun _ _ configured ->
        match configured.RequiredPlugins with
        | [ { Name = name
              Eligibility = PluginInputs.LookupEligibility.DeclaredInputs(PluginInputs.InputSpec.Matching(pattern, _),
                                                                          []) } ] ->
            test <@ name = fsx @>
            test <@ Watcher.FilePattern.toString pattern = "*.fsx" @>
        | other -> failwith $"expected one plugin declaring *.fsx, got %A{other}")

[<Fact>]
let ``a declared-input plugin with no record for its current inputs is the one a start runs`` () =
    // ADR-059 §2: a miss means run. A fresh checkout's store holds nothing, and an idle file
    // command never runs by itself, so without this it would stay missing until a match changed.
    withCheckout fsxOnly (fun repo store configured ->
        test <@ unrecordedDeclared repo "fshw 1" store configured = [ fsx ] @>

        observeWith repo store None configured (check repo "sha256:t0" 0.0 [ fsx, Some OutcomeStore.Outcome.Pass ])
        |> ignore

        test <@ unrecordedDeclared repo "fshw 1" store configured = [] @>
        // Another binary has recorded nothing, and neither has the new content of a match.
        test <@ unrecordedDeclared repo "fshw 2" store configured = [ fsx ] @>
        write repo "build.fsx" "printfn \"changed\""
        test <@ unrecordedDeclared repo "fshw 1" store configured = [ fsx ] @>)
