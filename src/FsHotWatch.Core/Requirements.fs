/// R(c, T): what a green for a command over a tree requires (ADR-059 §3), and the keys each
/// requirement is looked up under. Computed from the CONFIGURATION, never from a run
/// (ADR-059 §7 step 2): a plugin nobody ran on the tree is still required (AUTOMATION-1568).
///
/// Part of the correctness core (L2, re-architecture Phase 1, AUTOMATION-1695), moved here
/// unchanged from the CLI's shadow verdict, which keeps thin wrappers over its observation.
module FsHotWatch.Requirements

/// What the configuration requires of a green: its non-test plugins and its test projects.
[<NoComparison>]
type Configured =
    {
        /// The plugins the configuration requires (`RequiredConfig.ofJson`, the one definition), each
        /// with what its outcome is a function of (AUTOMATION-1577).
        RequiredPlugins: PluginInputs.RequiredPlugin list
        /// The configured test projects, by the name their reports carry.
        TestProjects: string list
    }

/// What a green requires over one tree: the configured plugins, each resolved to what its outcome
/// is keyed by there, and the test projects. Made only by `resolve`, so every configured plugin is
/// in it, and a plugin that declares its inputs carries their key.
[<NoComparison>]
type Resolved =
    private
        { Plugins: PluginInputs.Keyed list
          TestProjects: string list }

    /// The configured plugins, resolved.
    member resolved.RequiredPlugins = resolved.Plugins

    /// The configured test projects.
    member resolved.ConfiguredTestProjects = resolved.TestProjects

/// `configured` over one tree, each plugin resolved by `keyed` (`PluginInputs.keyed` over the
/// checkout and the producer: the caller's IO, as hashing the tree is).
let resolve (keyed: PluginInputs.RequiredPlugin -> PluginInputs.Keyed) (configured: Configured) : Resolved =
    { Plugins = configured.RequiredPlugins |> List.map keyed
      TestProjects = configured.TestProjects }

/// The command a requirement set is for. `Confirm` requires every test project in full;
/// `Check` also accepts an impact run over this tree on top of an earlier full pass.
[<RequireQualifiedAccess>]
type Command =
    | Check
    | Confirm

/// What the keys of one tree's requirements are folded from.
type Subject =
    {
        /// The tree's digest, as the verdict addresses it.
        Tree: string
        /// The fshw binary that grades it.
        Producer: string
        /// Each test project's full-run outcome key over this tree, as the daemon computed it
        /// (ADR-059 step 7b). Empty when nobody said.
        OutcomeKeys: Map<string, ActionKey.KeyValue>
    }

[<Literal>]
let FullScope = "full"

[<Literal>]
let ImpactScope = "impact"

let pluginKey (subject: Subject) (name: string) =
    ActionKey.create "plugin" [ "tree", subject.Tree; "producer", subject.Producer; "plugin", name ]

/// The key resolved plugin `plugin`'s outcome is looked up under over `subject`: its declared-input
/// key when it declares its inputs, else the whole-tree `pluginKey`.
let keyOf (subject: Subject) (plugin: PluginInputs.Keyed) =
    match plugin.Keying with
    | PluginInputs.Keying.DeclaredInputs key -> key
    | PluginInputs.Keying.WholeTree -> pluginKey subject plugin.Name

let diagnosticsKey (subject: Subject) =
    ActionKey.create "diagnostics" [ "tree", subject.Tree; "producer", subject.Producer ]

/// A test project's run over the tree, in `scope` (`full` or `impact`).
let testKey (subject: Subject) (project: string) (scope: string) =
    ActionKey.create
        "test"
        [ "tree", subject.Tree
          "producer", subject.Producer
          "project", project
          "scope", scope ]

/// "This project once passed in full": ADR-021's baseline, read from the store instead of
/// `full-suite-baseline.json`. It names no producer and no tree, as that watermark does not:
/// it vouches for the tests an impact run skips, which the binary that ran them does not
/// change. A baseline pinned to the producer was void after every fshw upgrade while today's
/// watermark still held, so each first check on a new binary was missing every project
/// (AUTOMATION-1428). The impact record beside it stays pinned to the tree and producer.
let baselineKey (project: string) =
    ActionKey.create "test-baseline" [ "project", project ]

/// R(c, T) for `command` over `subject`. Plugins and test projects are both the configured
/// ones. A plugin idle with no run record is left out of today's `plugins[]`, so a
/// requirement set read from the run could not miss a configured gate nobody ran on the tree
/// (AUTOMATION-1568). Each plugin is keyed as it was resolved (`keyOf`). Pure: the IO that
/// resolved the plugins was the caller's.
let requirements (command: Command) (subject: Subject) (resolved: Resolved) : LookupVerdict.Requirement list =
    [ for plugin in resolved.RequiredPlugins do
          LookupVerdict.Requirement.single ("plugin:" + plugin.Name) (keyOf subject plugin)
      LookupVerdict.Requirement.single "diagnostics" (diagnosticsKey subject)
      for project in resolved.ConfiguredTestProjects do
          let full = [ (testKey subject project FullScope).Value ]

          // The record the test runner filed under this project's K_P, when the daemon named
          // the key: the K_P of the tree as it is now, so no other tree's run can answer.
          let byKp =
              match Map.tryFind project subject.OutcomeKeys with
              | Some value -> [ [ value ] ]
              | None -> []

          { LookupVerdict.Requirement.Name = "test:" + project
            LookupVerdict.Requirement.Alternatives =
              match command with
              | Command.Confirm -> [ full ] @ byKp
              | Command.Check ->
                  [ full
                    [ (testKey subject project ImpactScope).Value; (baselineKey project).Value ] ]
                  @ byKp } ]
