/// A configured command that rewrites files in place BEFORE the build and the checks
/// see them — a generator that regenerates a source file from an input the compiler
/// does not read, say. It is the `preprocessors` entry of `.fshw.json`, run in the same
/// slot as the built-in formatter: inside a change batch and inside a scan, ahead of
/// every plugin, so the tree the plugins read is the one the command produced.
///
/// What the command wrote is attributed to it by CONTENT: each declared write is hashed
/// before and after the run, and only the ones whose bytes changed are `Modified`. The
/// daemon suppresses the watcher echo of exactly those, and dispatches them with the
/// batch. A run that changed nothing suppresses nothing, so a later real edit to a
/// declared file is never swallowed as the command's own echo.
module FsHotWatch.CommandPreprocessor

open System
open System.Diagnostics
open System.IO
open FsHotWatch.Plugin
open FsHotWatch.ProcessHelper
open FsHotWatch.Watcher

/// When a configured preprocessor runs.
[<RequireQualifiedAccess; NoComparison>]
type Trigger =
    /// Before every run that has a changed file at all.
    | Always
    /// Only when a changed file in the batch matches one of the patterns.
    | Matching of FilePattern list

module Trigger =
    /// Whether `changedFiles` asks for the command to run.
    let fires (trigger: Trigger) (changedFiles: string list) : bool =
        match trigger with
        | Trigger.Always -> true
        | Trigger.Matching patterns ->
            changedFiles
            |> List.exists (fun file -> patterns |> List.exists (fun pattern -> FilePattern.matches pattern file))

/// One resolved `preprocessors` entry: every path absolute, every default applied.
[<NoComparison>]
type Spec =
    {
        /// The status line's name; the name a verdict reports the failure under.
        Name: string
        Command: string
        Args: string
        /// The child's working directory.
        WorkDir: string
        Trigger: Trigger
        /// The files the command may rewrite, and so the files whose writes are its own.
        Writes: string list
        /// Total run bound. The child is silent (`ProcessBounds.silent`).
        Timeout: TimeSpan
    }

/// The content of `path`, as a hash, or `None` for a file that is not there.
let private contentOf (path: string) : string option =
    if File.Exists path then
        Some(ContentHash.ofFile path)
    else
        None

/// `path` with every symbolic link among its components followed: the form a native
/// watcher reports (`/private/var/...` for a macOS `/var/...`). A component that does
/// not exist is kept as written.
let rec realPathOf (path: string) : string =
    let full = Path.GetFullPath path
    let root = Path.GetPathRoot full

    full.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
    |> Array.fold
        (fun (current: string) part ->
            let next = Path.Combine(current, part)

            let info: FileSystemInfo =
                if Directory.Exists next then DirectoryInfo next
                elif File.Exists next then FileInfo next
                else null

            match info with
            | null -> next
            | info ->
                match info.ResolveLinkTarget(returnFinalTarget = true) with
                | null -> next
                // A link's target is written as its author wrote it, links among its own
                // ancestors included.
                | target -> realPathOf target.FullName)
        root

/// `path` moved from under `fromRoot` to under `toRoot`; unchanged when not under it.
let private rebase (fromRoot: string) (toRoot: string) (path: string) =
    if path.StartsWith(fromRoot + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
        toRoot + path.Substring fromRoot.Length
    else
        path

/// The declared write paths in the form the batch's watcher reports them. A native
/// watcher reports real paths, and a repository root behind a symbolic link then
/// arrives in a form the configuration never wrote. The daemon suppresses a write's
/// echo by exact path, so a `Modified` path in the wrong form is an echo that
/// re-triggers the command that wrote it — a loop. The batch is the evidence: when it
/// holds paths under the real root, that is the form to report.
let private inWatcherForm (repoRoot: string) (changedFiles: string list) (paths: string list) : string list =
    let configured = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar)
    let real = (realPathOf configured).TrimEnd(Path.DirectorySeparatorChar)

    let batchIsReal =
        real <> configured
        && changedFiles
           |> List.exists (fun f -> f.StartsWith(real + string Path.DirectorySeparatorChar, StringComparison.Ordinal))

    if batchIsReal then
        paths |> List.map (rebase configured real)
    else
        paths

/// The last `count` lines of a child's output, for a refusal.
let private tailOf (count: int) (text: string) : string =
    let lines = text.Split('\n') |> Array.filter (fun l -> l.Trim() <> "")
    let kept = lines |> Array.skip (max 0 (lines.Length - count))
    String.Join("\n", kept)

/// The preprocessor for `spec`. `Ok` carries the declared files whose bytes the run
/// changed; a non-zero exit, a timeout, or a child that could not start is `Error`,
/// which the host records as a failed status — a red run, never a silent skip.
let create (spec: Spec) : IFsHotWatchPreprocessor =
    let invocation = $"%s{spec.Command} %s{spec.Args}".Trim()

    { new IFsHotWatchPreprocessor with
        member _.Name = spec.Name

        member _.Process (changedFiles: string list) (repoRoot: string) =
            if changedFiles.IsEmpty || not (Trigger.fires spec.Trigger changedFiles) then
                Ok
                    { Modified = []
                      Considered = 0
                      Evidence = $"%s{invocation} — not triggered by this batch" }
            else
                let before = spec.Writes |> List.map (fun path -> path, contentOf path)
                let clock = Stopwatch.StartNew()

                let outcome =
                    try
                        Ok(runProcess spec.Command spec.Args spec.WorkDir [] (ProcessBounds.silent spec.Timeout))
                    with :? System.ComponentModel.Win32Exception as ex ->
                        Error $"`%s{invocation}` could not start in %s{spec.WorkDir}: %s{ex.Message}"

                match outcome with
                | Error reason -> Error reason
                | Ok(Succeeded _) ->
                    let modified =
                        before |> List.filter (fun (path, was) -> contentOf path <> was) |> List.map fst

                    Ok
                        { Modified = inWatcherForm repoRoot changedFiles modified
                          Considered = spec.Writes.Length
                          Evidence = $"%s{invocation} exit 0 in %d{int clock.ElapsedMilliseconds}ms" }
                | Ok(Failed(exitCode, output)) ->
                    Error
                        $"`%s{invocation}` exited %d{exitCode} after %d{int clock.ElapsedMilliseconds}ms:\n%s{tailOf 20 (renderOutput output)}"
                | Ok(TimedOut(after, tail, kill)) ->
                    Error
                        $"`%s{invocation}` timed out after %d{int after.TotalSeconds}s%s{renderKillBrief kill}:\n%s{tailOf 20 (renderOutput tail)}"

        member _.Dispose() = () }
