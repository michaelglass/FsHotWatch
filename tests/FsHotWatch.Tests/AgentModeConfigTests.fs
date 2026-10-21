/// Agent mode from `.fshw.json`: the `agent` key, the `--no-agent` opt-out, and the
/// precedence between them — flag > env var > config > default.
///
/// The key exists because an env var only applies where it was exported: a repo that
/// declares agent mode in its config gets it on every invocation, whoever started the
/// process and from wherever. These tests pin that a run with the key set and NO flag and
/// NO env var renders for an agent, and that the command line still wins.
///
/// Serialized with the other process-environment mutators (`withEnv`): the env-var
/// precedence cases set `FS_HOT_WATCH_AGENT` / `FS_HOT_WATCH_NO_AGENT`, and the
/// no-env case must prove both are ABSENT, or a leaked variable would satisfy it.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.AgentModeConfigTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Cli.Program
open FsHotWatch.Tests.TestHelpers

let defaults: DaemonConfiguration = defaultTestConfig ()

let agentEnv = "FS_HOT_WATCH_AGENT"
let noAgentEnv = "FS_HOT_WATCH_NO_AGENT"

/// Run `body` with both agent env vars set as given (`None` = unset), restoring both.
let private withAgentEnv (agent: string option) (noAgent: string option) (body: unit -> unit) =
    withEnv agentEnv agent (fun () -> withEnv noAgentEnv noAgent body)

/// The options `main` would build for `argv`: CommandTree parse (which folds in the env
/// vars), then `applyGlobalFlags`.
let optionsFor (argv: string array) : GlobalOptions =
    match globalSpec.Parse argv with
    | Ok(globals, _) -> applyGlobalFlags globals
    | Error e -> failwith $"expected %A{argv} to parse, got %A{e}"

// --- parseConfig: the `agent` key ---

[<Fact(Timeout = 15000)>]
let ``parseConfig agent absent is off`` () =
    test <@ not (parseConfig "{}" defaults).AgentMode @>

[<Fact(Timeout = 15000)>]
let ``parseConfig agent true turns agent mode on`` () =
    test <@ (parseConfig """{"agent": true}""" defaults).AgentMode @>

[<Fact(Timeout = 15000)>]
let ``parseConfig agent false or null is off`` () =
    test <@ not (parseConfig """{"agent": false}""" defaults).AgentMode @>
    test <@ not (parseConfig """{"agent": null}""" defaults).AgentMode @>

[<Theory(Timeout = 15000)>]
[<InlineData("""{"agent": "yes"}""", "yes")>]
[<InlineData("""{"agent": "true"}""", "true")>]
[<InlineData("""{"agent": 1}""", "1")>]
[<InlineData("""{"agent": {}}""", "{}")>]
let ``parseConfig agent that is not a boolean is a ConfigErrorException naming it`` (json: string, bad: string) =
    // A string "true" is refused too: a typo'd mode must fail the load, not quietly
    // render for the wrong reader.
    let ex =
        Assert.Throws<ConfigErrorException>(fun () -> parseConfig json defaults |> ignore)

    let quoted = "'" + bad + "'"
    test <@ ex.message.StartsWith "agent:" @>
    test <@ ex.message.Contains quoted @>
    test <@ ex.message.Contains "expected one of true, false" @>

// --- agentConfigured: the tolerant read for commands that load no config ---

[<Fact(Timeout = 15000)>]
let ``agentConfigured reads the agent key from config text`` () =
    test <@ agentConfigured """{"agent": true}""" @>
    test <@ not (agentConfigured """{"agent": false}""") @>
    test <@ not (agentConfigured "{}") @>

[<Fact(Timeout = 15000)>]
let ``agentConfigured reads missing, malformed or invalid config as off`` () =
    test <@ not (agentConfigured "") @>
    test <@ not (agentConfigured "{") @>
    test <@ not (agentConfigured """{"agent": "yes"}""") @>

// --- resolveAgentMode: flag/env (explicit) > config > default ---

[<Fact(Timeout = 15000)>]
let ``resolveAgentMode defaults to off with nothing said`` () =
    test <@ not (resolveAgentMode None false false) @>

[<Fact(Timeout = 15000)>]
let ``resolveAgentMode takes the config when nothing explicit is said`` () =
    test <@ resolveAgentMode None false true @>

[<Fact(Timeout = 15000)>]
let ``resolveAgentMode explicit choice beats the config`` () =
    // The case a human hits: the repo is configured for agents, they want the pretty output.
    test <@ not (resolveAgentMode (Some false) false true) @>
    test <@ resolveAgentMode (Some true) false false @>

[<Fact(Timeout = 15000)>]
let ``resolveAgentMode an explicit --compact beats the config's agent mode`` () =
    // `-q` asks for a human render on the command line; the config must not override it.
    test <@ not (resolveAgentMode None true true) @>
    // ...but an explicit agent choice still beats --compact, as it always has.
    test <@ resolveAgentMode (Some true) true true @>

// --- applyGlobalFlags: --agent / --no-agent ---

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags says nothing about agent mode without a flag`` () =
    test <@ (applyGlobalFlags []).AgentMode = None @>

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags --agent and --no-agent are explicit choices`` () =
    test <@ (applyGlobalFlags [ GlobalFlag.Agent ]).AgentMode = Some true @>
    test <@ (applyGlobalFlags [ GlobalFlag.NoAgent ]).AgentMode = Some false @>

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags the first agent flag wins, so a command-line flag beats an env var`` () =
    // CommandTree lists command-line flags before env-derived ones.
    test <@ (applyGlobalFlags [ GlobalFlag.NoAgent; GlobalFlag.Agent ]).AgentMode = Some false @>
    test <@ (applyGlobalFlags [ GlobalFlag.Agent; GlobalFlag.NoAgent ]).AgentMode = Some true @>

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags --no-agent is not forwarded to the daemon`` () =
    test <@ (applyGlobalFlags [ GlobalFlag.NoAgent ]).DaemonExtraArgs = "" @>

// --- renderModeFor: what a command renders with ---

[<Fact(Timeout = 15000)>]
let ``renderModeFor renders for an agent when only the config says so`` () =
    test <@ renderModeFor defaultGlobalOptions true = ProgressRenderer.RenderMode.Agent @>
    // Positive control: the same options with the key off keep the human render.
    test <@ renderModeFor defaultGlobalOptions false = ProgressRenderer.RenderMode.Verbose @>

[<Fact(Timeout = 15000)>]
let ``renderModeFor --no-agent gives the human render on a repo configured for agents`` () =
    test <@ renderModeFor (applyGlobalFlags [ GlobalFlag.NoAgent ]) true = ProgressRenderer.RenderMode.Verbose @>

    test
        <@
            renderModeFor (applyGlobalFlags [ GlobalFlag.NoAgent; GlobalFlag.Compact ]) true = ProgressRenderer.RenderMode.Compact
        @>

// --- end to end through the parser: no flag, no env var, config set ---

[<Fact(Timeout = 15000)>]
let ``check with the config key set and NO flag and NO env var renders for an agent`` () =
    withAgentEnv None None (fun () ->
        // Prove the env is clean, or a leaked variable would satisfy the assertion below.
        test <@ isNull (Environment.GetEnvironmentVariable agentEnv) @>
        test <@ isNull (Environment.GetEnvironmentVariable noAgentEnv) @>

        let opts = optionsFor [| "check" |]
        test <@ opts.AgentMode = None @>
        test <@ renderModeFor opts true = ProgressRenderer.RenderMode.Agent @>
        // Positive control: same argv, key off → the human render.
        test <@ renderModeFor opts false = ProgressRenderer.RenderMode.Verbose @>)

[<Fact(Timeout = 15000)>]
let ``--no-agent parses as a global flag and opts out of the config`` () =
    withAgentEnv None None (fun () ->
        let opts = optionsFor [| "--no-agent"; "check" |]
        test <@ opts.AgentMode = Some false @>
        test <@ renderModeFor opts true = ProgressRenderer.RenderMode.Verbose @>)

[<Fact(Timeout = 15000)>]
let ``a command-line flag beats the env var`` () =
    withAgentEnv (Some "1") None (fun () ->
        test <@ (optionsFor [| "--no-agent"; "check" |]).AgentMode = Some false @>)

    withAgentEnv None (Some "1") (fun () -> test <@ (optionsFor [| "--agent"; "check" |]).AgentMode = Some true @>)

[<Fact(Timeout = 15000)>]
let ``the env var beats the config`` () =
    withAgentEnv None (Some "1") (fun () ->
        test <@ renderModeFor (optionsFor [| "check" |]) true = ProgressRenderer.RenderMode.Verbose @>)

    withAgentEnv (Some "1") None (fun () ->
        test <@ renderModeFor (optionsFor [| "check" |]) false = ProgressRenderer.RenderMode.Agent @>)
