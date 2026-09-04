/// Where a plugin's cache entries may live, and the store that honours it.
///
/// A content-addressed entry is portable only if its key names EVERY input that
/// could change the answer. That is a per-plugin property, not a property of the
/// cache, so it is decided here — once, in a table, with each entry's justification
/// beside it — rather than being implied by whichever directory a store happens to
/// be constructed with.
///
/// The table is an ALLOWLIST and it fails closed: a plugin nobody has reasoned about
/// is workspace-local, so the cost of forgetting to classify one is a cold start, not
/// a verdict replayed into a checkout that never earned it.
module FsHotWatch.CacheResidency

open FsHotWatch.TaskCache

/// Whether a plugin's entries may be replayed in a DIFFERENT checkout of the same
/// repository.
[<RequireQualifiedAccess>]
type Residency =
    /// The plugin's verdict is a pure function of inputs its key names, all of which
    /// are content (or a tool version / config hash). Safe to share box-wide.
    | SharedAcrossCheckouts
    /// The verdict asserts something about THIS checkout that its key does not
    /// capture — compiled artifacts on disk, a test process that ran, a command whose
    /// effects are unmodelled. `reason` is surfaced in the daemon's startup log.
    | WorkspaceLocal of reason: string

/// The classification of every plugin this tool ships, and the reason for each.
///
/// Shared:
///
/// • `format-check` — the verdict is `fantomas --check` over one file's bytes. Its
///   key names the PINNED tool version, every `.editorconfig` above the file, the
///   file's repo-relative path and its full source. Formatting is a pure function of
///   exactly those, and the verdict is diagnostics only: nothing on disk has to exist
///   for a replay to be true. This is also the plugin the ticket's timing data
///   indicts — a 34.9 s median against a 566 s maximum, the maximum being the
///   whole-tree scan a fresh workspace pays.
///
/// • `lint` — FSharpLint over one file, keyed on the tool version, a hash of the lint
///   configuration file's contents, the repo-relative path, the source, and the FCS
///   check signature. Diagnostics only, same as format.
///
/// • `analyzers` — keyed on the CONTENT hashes of the analyzer assemblies (not merely
///   their paths), the repo-relative path, the source and the FCS signature.
///   Diagnostics only.
///
/// Workspace-local:
///
/// • `build` — a cached "built N projects" is a claim about `bin/`, and its key is a
///   merkle over SOURCES, structurally blind to outputs. The plugin already refuses to
///   replay when the artifacts are missing or stale (`replayBlockers`), so a fresh
///   workspace would miss anyway; sharing would add risk and buy nothing.
///
/// • `test-prune` — a cached green is a claim that a test PROCESS ran and passed. Its
///   key names the changed symbols, the project structure and the build outcome, but
///   not the compiled assemblies the run executed, and the impact database it reasons
///   from is per-workspace by design (AUTOMATION-564 keeps it that way). Replaying one
///   workspace's green into another would assert a run that never happened there.
///
/// • `coverage` — declares no cache key at all today; classified so the table is
///   exhaustive over what ships.
///
/// • `file-command` — runs an arbitrary user-configured shell command. Its effects are
///   unmodelled by construction, so no key can be complete.
let private table =
    [ "format-check", Residency.SharedAcrossCheckouts
      "lint", Residency.SharedAcrossCheckouts
      "analyzers", Residency.SharedAcrossCheckouts
      "build", Residency.WorkspaceLocal "a cached build asserts artifacts this checkout has not produced"
      "test-prune", Residency.WorkspaceLocal "a cached green asserts a test run this checkout has not made"
      "coverage", Residency.WorkspaceLocal "no cache key is declared"
      "file-command", Residency.WorkspaceLocal "an arbitrary shell command's effects are not modelled by any key" ]
    |> Map.ofList

/// The residency of `plugin`. Unknown plugins — anything registered by a host that
/// this table has not reasoned about — are workspace-local.
let of_ (plugin: string) =
    match Map.tryFind plugin table with
    | Some residency -> residency
    | None -> Residency.WorkspaceLocal "not classified: an unrecognised plugin is never shared"

/// The plugin names classified as shareable, sorted. For logging and for the test
/// that pins the table.
let sharedPlugins =
    table
    |> Map.toList
    |> List.filter (fun (_, residency) -> residency = Residency.SharedAcrossCheckouts)
    |> List.map fst
    |> List.sort

/// Routes each plugin's entries to exactly ONE of two stores according to
/// `residencyOf`. Not a read-through tier: an entry has one home, so there is no
/// double write, no way for the two stores to disagree, and no way for a `Clear` to
/// be undone by a copy that survived in the other one.
type RoutedTaskCache(local: ITaskCache, shared: ITaskCache, residencyOf: string -> Residency) =

    let storeFor (plugin: string) =
        match residencyOf plugin with
        | Residency.SharedAcrossCheckouts -> shared
        | Residency.WorkspaceLocal _ -> local

    /// Operations that name no plugin (`Clear`, `ClearFile`) reach BOTH stores: they
    /// are hygiene, and applying them to only one leaves the caller's request half done.
    let both = [ local; shared ]

    /// The store a composite key's entries live in.
    member _.StoreFor(plugin: string) = storeFor plugin

    interface ITaskCache with
        member _.TryGet compositeKey cacheKey =
            (storeFor compositeKey.Plugin).TryGet compositeKey cacheKey

        member _.Lookup compositeKey cacheKey =
            (storeFor compositeKey.Plugin).Lookup compositeKey cacheKey

        member _.Set compositeKey cacheKey result =
            (storeFor compositeKey.Plugin).Set compositeKey cacheKey result

        member _.Clear() =
            for store in both do
                store.Clear()

        member _.ClearPlugin plugin = (storeFor plugin).ClearPlugin plugin

        member _.ClearFile file =
            for store in both do
                store.ClearFile file

        member _.ClearPluginFile plugin file =
            (storeFor plugin).ClearPluginFile plugin file
