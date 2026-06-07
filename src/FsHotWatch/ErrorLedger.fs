module FsHotWatch.ErrorLedger

/// Diagnostic severity levels for error entries.
type DiagnosticSeverity =
    | Error
    | Warning
    | Info
    | Hint

/// A single diagnostic entry from a plugin.
type ErrorEntry =
    {
        Message: string
        Severity: DiagnosticSeverity
        Line: int
        Column: int
        /// Optional full output (e.g. complete test stdout for println debugging).
        Detail: string option
    }

/// Interface for receiving error ledger mutation notifications.
type IErrorReporter =
    abstract Report: plugin: string -> file: string -> entries: ErrorEntry list -> unit
    abstract Clear: plugin: string -> file: string -> unit
    abstract ClearPlugin: plugin: string -> unit
    abstract ClearAll: unit -> unit

module DiagnosticSeverity =
    let toString (severity: DiagnosticSeverity) =
        match severity with
        | Error -> "error"
        | Warning -> "warning"
        | Info -> "info"
        | Hint -> "hint"

    let fromString (s: string) =
        match s with
        | "error" -> Some Error
        | "warning" -> Some Warning
        | "info" -> Some Info
        | "hint" -> Some Hint
        | _ -> None

    let order (severity: DiagnosticSeverity) =
        match severity with
        | Hint -> 0
        | Info -> 1
        | Warning -> 2
        | Error -> 3

module ErrorEntry =
    /// True if the entry counts as a failure given the warningsAreFailures flag.
    let isFailing (warningsAreFailures: bool) (e: ErrorEntry) : bool =
        match e.Severity with
        | Error -> true
        | Warning -> warningsAreFailures
        | Info
        | Hint -> false

    /// Create an Error-severity entry with no source location.
    let error (message: string) : ErrorEntry =
        { Message = message
          Severity = Error
          Line = 0
          Column = 0
          Detail = None }

    /// Create an Error-severity entry with detail (e.g. full test output).
    let errorWithDetail (message: string) (detail: string) : ErrorEntry =
        { Message = message
          Severity = Error
          Line = 0
          Column = 0
          Detail = Some detail }

/// Per-plugin tally of ledger entries by severity — a lightweight projection of the ledger
/// used by the status renderer to decide the "completed-with-issues" glyph without pulling
/// the full entry list across the IPC wire.
type DiagnosticCounts = { Errors: int; Warnings: int }

module DiagnosticCounts =
    let empty = { Errors = 0; Warnings = 0 }

    let private bumpOne (d: DiagnosticCounts) (entry: ErrorEntry) =
        match entry.Severity with
        | Error -> { d with Errors = d.Errors + 1 }
        | Warning -> { d with Warnings = d.Warnings + 1 }
        | _ -> d

    /// Fold a sequence of ledger entries into counts.
    let ofEntries (entries: seq<ErrorEntry>) : DiagnosticCounts = entries |> Seq.fold bumpOne empty

    /// True if these counts should be treated as a failure under the current policy.
    let isFailing (warningsAreFailures: bool) (d: DiagnosticCounts) =
        d.Errors > 0 || (warningsAreFailures && d.Warnings > 0)

    /// Human-readable summary, omitting zero components. Empty string when both zero.
    let summary (d: DiagnosticCounts) =
        match d.Errors, d.Warnings with
        | 0, 0 -> ""
        | e, 0 -> $"%d{e} error(s)"
        | 0, w -> $"%d{w} warning(s)"
        | e, w -> $"%d{e} error(s), %d{w} warning(s)"

type private LedgerState =
    { Errors: Map<struct (string * string), ErrorEntry list>
      Versions: Map<struct (string * string), int64> }

[<NoComparison; NoEquality>]
type private LedgerMsg =
    | Report of plugin: string * file: string * entries: ErrorEntry list * version: int64 option
    | Clear of plugin: string * file: string * version: int64 option
    | ClearPlugin of plugin: string
    | GetAll of AsyncReplyChannel<Map<string, (string * ErrorEntry) list>>
    | GetByPlugin of plugin: string * AsyncReplyChannel<Map<string, ErrorEntry list>>
    | GetCountsByPlugin of AsyncReplyChannel<Map<string, DiagnosticCounts>>
    | FailingReasons of warningsAreFailures: bool * AsyncReplyChannel<Map<string, (string * ErrorEntry) list>>
    | HasFailingReasons of warningsAreFailures: bool * AsyncReplyChannel<bool>
    /// F12 (audit 2026-05-02) test seam: only path that can deterministically
    /// raise inside the typed match. Production messages don't have a natural
    /// failure mode (Map/list/Reply ops don't throw on valid state), so without
    /// this seam the "agent surfaces programming bugs" contract is unobservable.
    /// Posted only by `ErrorLedger.RaiseFaultForTest`, which is itself internal.
    | RaiseFaultForTest of exn

/// Plugin name under which the ledger self-reports its own reporter failures.
/// A reporter that throws while persisting a diagnostic means "we could not
/// record the errors" — which must read as non-clean, never as silence. The
/// synthetic Error entry lands in the same `state.Errors` map that GetAll /
/// FailingReasons / HasFailingReasons (and thus the CLI exit code) consult.
[<Literal>]
let reporterFailurePlugin = "error-ledger"

let private isFailing warningsAreFailures e =
    ErrorEntry.isFailing warningsAreFailures e

/// Build a synthetic Error entry describing reporters that threw while a given
/// plugin's diagnostics were being recorded, naming the failing plugin and the
/// exception(s) so the verdict and the daemon log agree.
let private syntheticReporterFailure (plugin: string) (entryCount: int) (failures: exn list) : ErrorEntry =
    let detail = failures |> List.map (fun ex -> ex.ToString()) |> String.concat "\n\n"

    let exSummary =
        failures
        |> List.map (fun ex -> ex.GetType().Name)
        |> List.distinct
        |> String.concat ", "

    ErrorEntry.errorWithDetail $"failed to record %d{entryCount} diagnostic(s) from %s{plugin}: %s{exSummary}" detail

/// Check version and advance if accepted. Returns (accepted, newState).
let private tryAcceptVersion key (v: int64) (state: LedgerState) =
    match Map.tryFind key state.Versions with
    | Some last when v < last -> false, state
    | _ ->
        true,
        { state with
            Versions = Map.add key v state.Versions }

/// Accumulates per-file errors from plugins. Errors auto-clear when a file
/// is re-checked and passes. Thread-safe via MailboxProcessor agent.
/// Supports optional version-guarded updates: when a version is provided,
/// stale updates (version < last accepted) are silently ignored.
type ErrorLedger(?reporters: IErrorReporter list, ?logError: string -> string -> unit) =
    let reporters = defaultArg reporters []

    // Reporter-failure log sink: defaults to the process-global `Logging.error`
    // (stderr), but is injectable so callers — notably tests asserting the
    // failure is logged — can capture it WITHOUT redirecting process-global
    // `System.Console.Error`. The emission happens on the MailboxProcessor agent
    // thread (during `Report` processing), so a `Console.Error` capture would
    // race any concurrent `Console.SetError`; an injected sink removes that
    // global-state dependency entirely.
    let logError = defaultArg logError Logging.error

    // F11 (audit 2026-05-02): IErrorReporter is a third-party-extension
    // boundary — implementations are user-supplied and may raise anything.
    // The broad catch keeps a misbehaving reporter from taking down the
    // ledger agent; log ex.ToString() (not ex.Message) so the type and
    // stack trace are preserved for diagnosing the offending reporter.
    //
    // 2026-06-01: surviving the crash must NOT also erase the verdict. The
    // logging stays, but callers now also receive the exceptions so a failed
    // *persist* of a diagnostic can be self-reported as a synthetic error the
    // aggregate verdict / exit code observes (see syntheticReporterFailure).
    // Returns the exceptions raised by reporters (empty when all succeeded).
    let notifyReporters action : exn list =
        let mutable failures = []

        for r in reporters do
            try
                action r
            with ex ->
                logError "error-ledger" $"Reporter failed: %s{ex.ToString()}"
                failures <- ex :: failures

        List.rev failures

    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop (state: LedgerState) =
                async {
                    let! msg = inbox.Receive()

                    let newState =
                        // F12 (audit 2026-05-02): no inner try/with here — the body
                        // is a typed match over messages we own and field operations
                        // that don't throw on valid state. Anything that throws is a
                        // programming bug; swallowing it would let the bug recur
                        // forever silently. Unhandled exceptions surface through
                        // `agent.Error` (exposed publicly as `AgentCrashed`).
                        match msg with
                        | Report(plugin, file, entries, version) ->
                            let key = struct (plugin, file)

                            let accepted, state' =
                                match version with
                                | Some v -> tryAcceptVersion key v state
                                | None -> true, state

                            // The synthetic key under which a reporter-failure
                            // for this plugin/file is tracked, so a later clean
                            // re-report of the same file clears the stale alarm.
                            let failureKey = struct (reporterFailurePlugin, file)

                            if accepted then
                                if entries.IsEmpty then
                                    notifyReporters (fun r -> r.Clear plugin file) |> ignore

                                    { state' with
                                        Errors = state'.Errors |> Map.remove key |> Map.remove failureKey }
                                else
                                    let failures = notifyReporters (fun r -> r.Report plugin file entries)

                                    let errors = Map.add key entries state'.Errors

                                    let errors =
                                        if List.isEmpty failures then
                                            // Reporters persisted cleanly: drop any prior failure alarm.
                                            Map.remove failureKey errors
                                        else
                                            // A reporter could not persist these diagnostics. Self-report
                                            // so the aggregate verdict / exit code is non-clean rather than
                                            // falsely green (the diagnostics may be lost on disk).
                                            let synthetic = syntheticReporterFailure plugin entries.Length failures

                                            Map.add failureKey [ synthetic ] errors

                                    { state' with Errors = errors }
                            else
                                state'

                        | Clear(plugin, file, version) ->
                            let key = struct (plugin, file)

                            let accepted, state' =
                                match version with
                                | Some v -> tryAcceptVersion key v state
                                | None -> true, state

                            if accepted then
                                // Symmetry note (2026-06-01): a failed Clear is logged but not
                                // self-reported. Unlike a failed Report, it can only leave a
                                // stale-red on-disk file — never a false-green — and the verdict
                                // reads the in-memory ledger (cleared here) regardless.
                                notifyReporters (fun r -> r.Clear plugin file) |> ignore

                                { state' with
                                    Errors = Map.remove key state'.Errors }
                            else
                                state'

                        | ClearPlugin plugin ->
                            let newErrors = state.Errors |> Map.filter (fun (struct (p, _)) _ -> p <> plugin)

                            let newVersions =
                                state.Versions |> Map.filter (fun (struct (p, _)) _ -> p <> plugin)

                            // See Clear symmetry note: a failed ClearPlugin is stale-red, not
                            // false-green, so it is logged but not self-reported.
                            notifyReporters (fun r -> r.ClearPlugin plugin) |> ignore

                            { Errors = newErrors
                              Versions = newVersions }

                        | GetAll rc ->
                            let result =
                                state.Errors
                                |> Map.toSeq
                                |> Seq.collect (fun (struct (plugin, file), entries) ->
                                    entries |> List.map (fun e -> file, (plugin, e)))
                                |> Seq.groupBy fst
                                |> Seq.map (fun (file, entries) -> file, entries |> Seq.map snd |> Seq.toList)
                                |> Map.ofSeq

                            rc.Reply(result)
                            state

                        | GetByPlugin(pluginName, rc) ->
                            let result =
                                state.Errors
                                |> Map.toSeq
                                |> Seq.choose (fun (struct (p, file), entries) ->
                                    if p = pluginName then Some(file, entries) else None)
                                |> Map.ofSeq

                            rc.Reply(result)
                            state

                        | GetCountsByPlugin rc ->
                            let result =
                                state.Errors
                                |> Map.fold
                                    (fun acc (struct (plugin, _)) entries ->
                                        let prev =
                                            Map.tryFind plugin acc |> Option.defaultValue DiagnosticCounts.empty

                                        let next =
                                            entries
                                            |> List.fold
                                                (fun (d: DiagnosticCounts) e ->
                                                    match e.Severity with
                                                    | Error -> { d with Errors = d.Errors + 1 }
                                                    | Warning -> { d with Warnings = d.Warnings + 1 }
                                                    | _ -> d)
                                                prev

                                        Map.add plugin next acc)
                                    Map.empty

                            rc.Reply(result)
                            state

                        | FailingReasons(warningsAreFailures, rc) ->
                            let result =
                                state.Errors
                                |> Map.toSeq
                                |> Seq.collect (fun (struct (plugin, file), entries) ->
                                    entries
                                    |> List.filter (isFailing warningsAreFailures)
                                    |> List.map (fun e -> file, (plugin, e)))
                                |> Seq.groupBy fst
                                |> Seq.map (fun (file, entries) -> file, entries |> Seq.map snd |> Seq.toList)
                                |> Map.ofSeq

                            rc.Reply(result)
                            state

                        | HasFailingReasons(warningsAreFailures, rc) ->
                            let hasAny =
                                state.Errors
                                |> Map.values
                                |> Seq.exists (List.exists (isFailing warningsAreFailures))

                            rc.Reply(hasAny)
                            state

                        | RaiseFaultForTest ex -> raise ex

                    return! loop newState
                }

            loop
                { Errors = Map.empty
                  Versions = Map.empty })

    do
        agent.Error.Add(fun ex ->
            // F12 (audit 2026-05-02): an unhandled exception inside the agent
            // loop is a programming bug. Log loudly with the full stack trace
            // (ex.ToString(), not ex.Message) so the bug is debuggable; the
            // agent then stops and subsequent posts queue up unconsumed —
            // making the failure visible at the next caller rather than
            // silently dropped as the previous inner try/with did.
            Logging.error "error-ledger" $"Mailbox loop crashed (programming bug, agent stopped): %s{ex.ToString()}")

    /// Set errors for a plugin + file. Replaces previous. Empty list clears.
    /// When version is provided, updates with version < last accepted are ignored.
    member _.Report(pluginName: string, filePath: string, entries: ErrorEntry list, ?version: int64) =
        agent.Post(Report(pluginName, filePath, entries, version))

    /// Clear all errors for a plugin + file.
    /// When version is provided, clears with version < last accepted are ignored.
    member _.Clear(pluginName: string, filePath: string, ?version: int64) =
        agent.Post(Clear(pluginName, filePath, version))

    /// Clear all errors for a plugin.
    member _.ClearPlugin(pluginName: string) = agent.Post(ClearPlugin pluginName)

    /// Get all errors grouped by file path. Each entry includes the plugin name.
    member _.GetAll() : Map<string, (string * ErrorEntry) list> = agent.PostAndReply(fun rc -> GetAll rc)

    /// Get errors for a specific plugin only.
    member _.GetByPlugin(pluginName: string) : Map<string, ErrorEntry list> =
        agent.PostAndReply(fun rc -> GetByPlugin(pluginName, rc))

    /// Get per-plugin error/warning counts in a single agent roundtrip.
    /// Plugins with no ledger entries are absent from the map.
    member _.GetCountsByPlugin() : Map<string, DiagnosticCounts> =
        agent.PostAndReply(fun rc -> GetCountsByPlugin rc)

    /// Get all failing entries grouped by file path, filtered by severity.
    /// When warningsAreFailures is true, both Error and Warning entries are included.
    /// When false, only Error entries are included.
    member _.FailingReasons(warningsAreFailures: bool) : Map<string, (string * ErrorEntry) list> =
        agent.PostAndReply(fun rc -> FailingReasons(warningsAreFailures, rc))

    /// True if any failing entries exist (Error, or Warning when warningsAreFailures=true).
    member _.HasFailingReasons(warningsAreFailures: bool) =
        agent.PostAndReply(fun rc -> HasFailingReasons(warningsAreFailures, rc))

    /// F12 (audit 2026-05-02): unhandled exceptions inside the mailbox loop
    /// surface here. Subscribe to observe programming bugs that the previous
    /// inner try/with would have silently swallowed. The default subscriber
    /// (wired in `do agent.Error.Add ...`) logs with the full stack.
    member _.AgentCrashed: IEvent<exn> = agent.Error

    /// F12 test seam: deterministically raise inside the agent loop. Used by
    /// tests to verify the "agent surfaces programming bugs" contract; the
    /// production messages don't have a natural failure mode (Map/list/Reply
    /// ops don't throw on valid state). Internal — only `FsHotWatch.Tests`
    /// can call this via `InternalsVisibleTo`.
    member internal _.RaiseFaultForTest(ex: exn) = agent.Post(RaiseFaultForTest ex)
