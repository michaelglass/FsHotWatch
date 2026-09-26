module FsHotWatch.ContentDedup

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography

/// The bytes of a `project.assets.json` that decide a check, as JSON with the
/// `project.restore` block removed; `None` when the text is not a JSON object.
///
/// `project.restore` records how the restore that wrote the file was invoked — its
/// output path, sources, config files, and (from a tool that restores on its own, such
/// as a JavaScript compiler's project cracker) `restoreLockProperties` naming that
/// tool's lock file. None of it is the resolved package graph a check types against:
/// that is `targets`, `libraries`, `packageFolders` and `project.frameworks`, all kept.
/// Two restores of an unchanged project by two different tools write different
/// `project.restore` blocks, and reading that as a project change re-evaluated MSBuild
/// and re-checked the project and every dependent for nothing.
let internal assetsGraphBytes (content: byte[]) : byte[] option =
    try
        match System.Text.Json.Nodes.JsonNode.Parse(ReadOnlySpan content) with
        | :? System.Text.Json.Nodes.JsonObject as root ->
            match root["project"] with
            | :? System.Text.Json.Nodes.JsonObject as project -> project.Remove("restore") |> ignore
            | _ -> ()

            Some(Text.Encoding.UTF8.GetBytes(root.ToJsonString()))
        | _ -> None
    with :? System.Text.Json.JsonException ->
        None

/// The hash a path's content is compared by: `assetsGraphBytes` for a
/// `project.assets.json` that parses, the raw bytes for everything else.
let internal comparableHash (path: string) (content: byte[]) : byte[] =
    let isAssets =
        Path.GetFileName(path).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase)

    let bytes =
        if isAssets then
            assetsGraphBytes content |> Option.defaultValue content
        else
            content

    SHA256.HashData(bytes)

/// Compute the change verdict for `path` against `store`, mutating `store`.
/// Extracted so both the per-instance `Tracker` and the process-global default
/// share one implementation. Returns true if the content actually changed since
/// the store last saw it (and true for new/deleted/unreadable files).
let private evaluate (store: ConcurrentDictionary<string, byte[]>) (path: string) =
    try
        if not (File.Exists(path)) then
            store.TryRemove(path) |> ignore
            true
        else
            let content = File.ReadAllBytes(path)
            let hash = comparableHash path content

            match store.TryGetValue(path) with
            | true, previous when ReadOnlySpan(previous).SequenceEqual(ReadOnlySpan(hash)) -> false
            | _ ->
                store[path] <- hash
                true
    with
    | :? IOException -> true
    | :? UnauthorizedAccessException -> true

/// Per-daemon content-hash store. Scoped per instance (like `ProcessRegistry.Registry`)
/// so a hash written by one daemon never suppresses a genuine first-observation change
/// event in another daemon sharing the process — the key is the absolute file path, so
/// daemon A's stale entry would collide exactly with daemon B's first read.
type Tracker() =
    let fileHashes = ConcurrentDictionary<string, byte[]>()

    /// Returns true if the file content actually changed since this tracker last
    /// checked it. Updates the stored hash on change. Returns true for
    /// new/deleted files.
    member _.HasContentChanged(path: string) = evaluate fileHashes path

    /// Record `path`'s current content as already seen, WITHOUT reporting a verdict.
    ///
    /// `HasContentChanged` answers "changed" for a path it has never seen, which is the
    /// only honest answer for a source file: there is no prior to compare against. For a
    /// file the caller has just READ — a project file the daemon has this moment loaded
    /// its model from — a prior does exist, and the caller can supply it. Observing it
    /// here is how the next watcher echo of those same bytes is answered "unchanged"
    /// instead of provoking work the caller has already done.
    member _.Observe(path: string) = evaluate fileHashes path |> ignore

/// Process-global fallback tracker backing the module-level `hasContentChanged`.
let private defaultTracker = Tracker()

/// Returns true if the file content actually changed since the process-global default
/// tracker last checked it. Returns true for new/deleted files. Daemons do NOT use this
/// path — they hold a per-instance `Tracker` so cross-daemon hashes never collide.
let hasContentChanged (path: string) = defaultTracker.HasContentChanged(path)
