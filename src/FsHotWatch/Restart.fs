/// What a restarted daemon's first check would select (AUTOMATION-1623, ADR-069).
///
/// The test plugin knows whether a restart would diff from a full pass of this checkout and
/// select its tests, or would have no evidence to diff from and run the full suite. Idle-exit,
/// in the core, decides how long an idle daemon is worth keeping, and that answer is the
/// price of letting it go. The plugin publishes the answer as a file beside the daemon's
/// state, every time one of the facts it rests on can change; idle-exit reads it on every
/// tick. Core cannot reference the plugin, and a file is what a restarted daemon would read
/// anyway.
///
/// Anything that is not a readable `SelectsCorrectly` is `WouldRunFullSuite`: an absent,
/// empty or malformed file never shortens a window.
module FsHotWatch.Restart

open System.IO

/// What a restarted daemon's first check would select.
[<RequireQualifiedAccess>]
type Selection =
    /// A restart diffs from this checkout's full pass of `tree` and selects from the diff.
    | SelectsCorrectly of tree: string
    /// A restart has no full pass to diff from, and its first check runs every project in
    /// full. `reason` is the condition that failed, as the plugin's log names it.
    | WouldRunFullSuite of reason: string

/// The published selection's file, under the daemon's state directory.
let pathOf (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "restart-selection")

[<Literal>]
let private SelectsTag = "selects-correctly"

[<Literal>]
let private FullSuiteTag = "would-run-full-suite"

let private render (selection: Selection) : string =
    match selection with
    | Selection.SelectsCorrectly tree -> $"%s{SelectsTag} %s{tree}"
    | Selection.WouldRunFullSuite reason -> $"%s{FullSuiteTag} %s{reason}"

let private parse (text: string) : Selection =
    let line = text.Trim()

    match line.IndexOf ' ' with
    | -1 -> Selection.WouldRunFullSuite $"the published restart selection is unreadable: %s{line}"
    | space ->
        let tag = line.Substring(0, space)
        let value = line.Substring(space + 1).Trim()

        match tag with
        | SelectsTag when value <> "" -> Selection.SelectsCorrectly value
        | FullSuiteTag -> Selection.WouldRunFullSuite value
        | _ -> Selection.WouldRunFullSuite $"the published restart selection is unreadable: %s{line}"

/// Publish `selection`, replacing any earlier one atomically. Never throws: a selection that
/// cannot be written removes the old one, so a stale `SelectsCorrectly` cannot outlive it.
let publish (log: string -> unit) (repoRoot: string) (selection: Selection) : unit =
    let path = pathOf repoRoot

    try
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        FsHwPaths.atomicWriteAllText path (render selection)
    with ex ->
        log $"restart selection %s{path} could not be written: %s{ex.GetType().Name}: %s{ex.Message}"

        try
            if File.Exists path then
                File.Delete path
        with ex ->
            log $"restart selection %s{path} could not be cleared either: %s{ex.Message}"

/// The published selection; `WouldRunFullSuite` when none is published or it cannot be read.
let read (repoRoot: string) : Selection =
    let path = pathOf repoRoot

    try
        if File.Exists path then
            parse (File.ReadAllText path)
        else
            Selection.WouldRunFullSuite "no restart selection has been published"
    with ex ->
        Selection.WouldRunFullSuite $"the published restart selection could not be read: %s{ex.Message}"
