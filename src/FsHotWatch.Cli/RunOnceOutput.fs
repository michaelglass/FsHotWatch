module FsHotWatch.Cli.RunOnceOutput

open System
open System.Text
open CommandTree
open FsHotWatch.Events
open FsHotWatch.ErrorLedger

/// CLI-side view of a plugin's status. The wire deliberately carries only the
/// state tag, timestamps, and the failure diagnosis — the verdict (summary +
/// elapsed) travels exclusively in the run record (`lastRun`), the ONE channel
/// the renderer reads. Parsing into the daemon's `PluginStatus` would force
/// the CLI to fabricate a `RunVerdict` from untrusted wire input — the content-free
/// ✓ that `RunVerdict` exists to prevent — so the CLI has its own verdict-free shape
/// instead.
[<RequireQualifiedAccess; NoComparison>]
type StatusView =
    | Idle
    | Running of since: DateTime
    | Completed of at: DateTime
    | Failed of error: string * at: DateTime
    /// The daemon SENT a status and this build could not read it — an unrecognized
    /// tag, a `since` that will not parse, a status that is not even an object.
    ///
    /// Its own case, because the alternatives all fail OPEN. Rounding it to `Idle`
    /// makes the plugin quiescent, clean and INVISIBLE — `Verdict.pluginVerdicts`
    /// omits an idle plugin with no run record, so it vanishes from `plugins[]`
    /// altogether, and a verdict over a clean ledger goes green. Dropping the plugin
    /// from the map is worse still.
    ///
    /// A live cross-version hazard: an "old CLI, new daemon" pairing can carry status
    /// tags the CLI has no name for. Same policy as `Verdict.read` — an unknown state
    /// is not a passing state.
    | Unreadable of reason: string

module StatusView =
    /// Project the daemon's in-process status (run-once path) to the CLI view.
    ///
    /// Total, and note there is no `Unreadable` here: in-process we hold the daemon's
    /// own `PluginStatus` value, not a wire encoding of it. Absence of an answer is not
    /// a possible state, so it is not a representable one.
    let ofPluginStatus (status: PluginStatus) : StatusView =
        match status with
        | Idle -> StatusView.Idle
        | Running since -> StatusView.Running since
        | Completed(at, _) -> StatusView.Completed at
        | Failed(error, at, _) -> StatusView.Failed(error, at)

    // Idle counts as quiescent for status-aggregation callers that query after
    // WaitForScan: Idle there means "not triggered by this scan", not "pending".
    //
    // `Unreadable` is quiescent too: there is nothing to WAIT for — a status we cannot
    // read will not become readable by polling it again, and blocking on one would hang
    // the check instead of failing it. It is terminal AND failing (see
    // `Verdict.pluginOutcomeOf`).
    let isQuiescent (status: StatusView) =
        match status with
        | StatusView.Running _ -> false
        | StatusView.Idle
        | StatusView.Completed _
        | StatusView.Failed _
        | StatusView.Unreadable _ -> true

type ParsedPluginStatus =
    { Status: StatusView
      Subtasks: Subtask list
      ActivityTail: string list
      LastRun: RunRecord option
      Diagnostics: DiagnosticCounts }

module ParsedPluginStatus =
    /// Did the run behind this status VERIFY NOTHING — did it complete having executed
    /// no test at all (AUTOMATION-198)?
    ///
    /// Read off the run record's summary through `RunSummary.saysNothingVerified`, the
    /// one reader for the one writer. THE definition, so the three surfaces that must
    /// refuse a `✓` for it — compact, verbose, and `pluginOutcomeOf` (which drives both
    /// agent mode and `plugins[]` in the verdict file) — cannot disagree about which
    /// runs those are.
    ///
    /// FALSE when there is no run record: that absence is its own fail-closed rule
    /// (`CompletedNoRecordText`), with its own words, and answering "verified nothing"
    /// here would put the wrong sentence on it.
    let verifiedNothing (parsed: ParsedPluginStatus) : bool =
        match parsed.LastRun with
        | Some { Summary = Some s } -> RunSummary.saysNothingVerified s
        | _ -> false

/// Describes a FileCommand-style plugin run for staleness detection: when did
/// it last start, and what input files (relative to repoRoot) does it depend on?
type PluginRunInfo =
    { Name: string
      LastRunStarted: System.DateTime
      RepoRoot: string
      Args: string }

/// For each plugin, return the input files modified after the plugin's last
/// run started. A non-empty list signals that the plugin's reported errors
/// (or absence thereof) may not reflect current input — typically because the
/// daemon has cached output keyed without the changed file.
///
/// Defense-in-depth against cache-key gaps. Even if a plugin's salt covers
/// its inputs (as FileCommandPlugin's now does), this surfaces the same hint
/// for plugins that don't.
let detectStalePluginInputs (plugins: PluginRunInfo list) : (string * string list) list =
    plugins
    |> List.choose (fun p ->
        let stale =
            FsHotWatch.FileCommand.FileCommandPlugin.argsStalerThan p.RepoRoot p.Args p.LastRunStarted

        if stale.IsEmpty then None else Some(p.Name, stale))

/// Returns "" for an empty input so callers can `if s <> "" then eprintfn "%s" s`.
let formatStalenessWarning (stale: (string * string list) list) : string =
    if List.isEmpty stale then
        ""
    else
        let sb = StringBuilder()

        sb.AppendLine($"%s{Color.yellow}warning%s{Color.reset}: cached output may be stale")
        |> ignore

        for (plugin, files) in stale do
            for file in files do
                sb.AppendLine($"  [%s{plugin}] %s{file} modified after last run") |> ignore

            sb.AppendLine($"  → run `fshw rerun %s{plugin}` to refresh") |> ignore

        sb.ToString().TrimEnd('\n', '\r')

/// Format the errors section with colored severity labels.
/// Groups errors by file with colored severity.
let formatErrors (errors: Map<string, (string * ErrorEntry) list>) : string =
    let actionable =
        errors
        |> Map.map (fun _ entries ->
            entries
            |> List.filter (fun (_, e) ->
                match e.Severity with
                | Error
                | Warning
                // A "waiting on build" deferral is actionable context worth
                // showing (it explains a non-green run), so render it too — and an
                // abort all the more so: it is the whole reason the run has no verdict.
                | Deferred
                | HostAborted -> true
                | Info
                | Hint -> false))
        |> Map.filter (fun _ entries -> not entries.IsEmpty)

    if actionable.IsEmpty then
        $"%s{Color.green}No errors%s{Color.reset}"
    else
        let sb = StringBuilder()
        let mutable errorCount = 0
        let mutable warnCount = 0

        for KeyValue(file, entries) in actionable do
            sb.AppendLine() |> ignore
            sb.AppendLine($"%s{Color.bold}%s{file}%s{Color.reset}") |> ignore

            for (pluginName, entry) in entries do
                match entry.Severity with
                | Error -> errorCount <- errorCount + 1
                | Warning -> warnCount <- warnCount + 1
                | _ -> ()

                let severityLabel =
                    match entry.Severity with
                    | Error -> $"%s{Color.red}error%s{Color.reset}: "
                    | Warning -> $"%s{Color.yellow}warning%s{Color.reset}: "
                    | Deferred -> $"%s{Color.yellow}waiting on build%s{Color.reset}: "
                    // Its own label, never `error`: the point of the whole change is
                    // that a reader can tell a dead runner from a broken test at a
                    // glance (AUTOMATION-294).
                    | HostAborted -> $"%s{Color.yellow}ABORTED (nothing verified)%s{Color.reset}: "
                    | Info
                    | Hint -> ""

                sb.AppendLine(
                    $"  %s{Color.dim}[%s{pluginName}]%s{Color.reset} L%d{entry.Line}: %s{severityLabel}%s{entry.Message}"
                )
                |> ignore

        sb.AppendLine() |> ignore
        let fileCount = actionable.Count

        let summary =
            match
                DiagnosticCounts.summary
                    { Errors = errorCount
                      Warnings = warnCount }
            with
            | "" -> "No errors"
            | s -> s

        sb.Append($"%s{summary} in %d{fileCount} file(s)") |> ignore
        sb.ToString().TrimEnd('\n', '\r')

/// Verify at least one discoverable `.fsproj` exists before any expensive
/// daemon work. Returns `Some 2` (config-error exit code) and emits a
/// clear stderr message when no project would be discovered — the caller
/// is expected to exit with that code rather than continue. Returns
/// `None` when at least one project would be picked up.
///
/// Used by both daemon-mode startup (`fshw start`) and the run-once paths
/// (`fshw check --run-once`, `fshw build --run-once`, etc.) so every entry point
/// fails fast on misconfiguration. Zero projects almost always means a wrong working
/// directory or an over-eager `.fshw.json` exclude pattern, and the daemon has no
/// useful behavior to provide in that state.
let failIfNoProjects (repoRoot: string) (excludePatterns: string list) : int option =
    let isExcluded = FsHotWatch.PathFilter.isExcludedPath repoRoot excludePatterns

    // SafeWalk (not `SearchOption.AllDirectories`): symlink-safe + depth-capped,
    // and LAZY — `Seq.exists` stops at the first project instead of materialising
    // every .fsproj in the repo just to answer "is there at least one?".
    let hasProject =
        [ "src"; "tests" ]
        |> List.map (fun d -> System.IO.Path.Combine(repoRoot, d))
        |> List.filter System.IO.Directory.Exists
        |> List.exists (fun dir ->
            FsHotWatch.SafeWalk.bestEffortFilePaths FsHotWatch.SafeWalk.ToolingExcludedDirs "*.fsproj" dir
            |> Seq.exists (fun f -> not (isExcluded f)))

    if hasProject then
        None
    else
        eprintfn
            "fshw: no projects discovered under %s. Check `.fshw.json` exclude patterns or working directory."
            repoRoot

        Some 2

/// Run a daemon's RunOnce with live progress display to stderr.
let runOnceWithProgress (daemon: FsHotWatch.Daemon.Daemon) : Map<string, PluginStatus> =
    if UI.isInteractive then
        UI.withSpinnerQuiet "Running checks" (fun () -> daemon.RunOnce() |> Async.RunSynchronously)
    else
        daemon.RunOnce() |> Async.RunSynchronously

/// Build a parsed-status map from the daemon's host, for use by the progress renderer.
let snapshotHost (host: FsHotWatch.PluginHost.PluginHost) (statuses: Map<string, PluginStatus>) =
    let counts = host.GetDiagnosticCountsByPlugin()

    statuses
    |> Map.map (fun name status ->
        let snap = host.GetActivitySnapshot(name)

        { Status = StatusView.ofPluginStatus status
          Subtasks = snap.Subtasks
          ActivityTail = snap.ActivityTail
          LastRun = snap.LastRun
          Diagnostics = Map.tryFind name counts |> Option.defaultValue DiagnosticCounts.empty })

/// Run a daemon in run-once mode and report results.
let runOnceAndReport
    (renderSummary: Map<string, ParsedPluginStatus> -> string)
    (noWarnFail: bool)
    (createDaemon: string -> FsHotWatch.Daemon.Daemon)
    (repoRoot: string)
    (config: DaemonConfig.DaemonConfiguration)
    (pluginName: string option)
    : int =
    match failIfNoProjects repoRoot config.Exclude with
    | Some exitCode -> exitCode
    | None ->

        let daemon = createDaemon repoRoot
        DaemonConfig.registerPlugins daemon repoRoot config
        let statuses = runOnceWithProgress daemon

        let allErrors =
            match pluginName with
            | Some name ->
                daemon.Host.GetErrorsByPlugin(name)
                |> Map.map (fun _ entries -> entries |> List.map (fun e -> name, e))
            | None -> daemon.Host.GetErrors()

        let failCount =
            allErrors
            |> Map.toList
            |> List.collect snd
            |> List.filter (fun (_, e) -> ErrorEntry.isFailing (not noWarnFail) e)
            |> List.length

        let parsed = snapshotHost daemon.Host statuses
        let summary = renderSummary parsed

        if summary <> "" then
            eprintfn "%s" summary

        eprintfn "%s" (formatErrors allErrors)

        // Defense-in-depth against cache-key gaps — see `detectStalePluginInputs`.
        let staleInputs =
            config.FileCommands
            |> List.choose (fun fc ->
                match Map.tryFind fc.PluginName parsed |> Option.bind (fun p -> p.LastRun) with
                | Some lastRun ->
                    Some
                        { Name = fc.PluginName
                          LastRunStarted = lastRun.StartedAt
                          RepoRoot = repoRoot
                          Args = fc.Args }
                | None -> None)
            |> detectStalePluginInputs

        let stalenessWarning = formatStalenessWarning staleInputs

        if stalenessWarning <> "" then
            eprintfn "%s" stalenessWarning

        if failCount > 0 then 1 else 0
