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
    | FailingReasons of warningsAreFailures: bool * AsyncReplyChannel<Map<string, (string * ErrorEntry) list>>
    | HasFailingReasons of warningsAreFailures: bool * AsyncReplyChannel<bool>

let private isFailing warningsAreFailures e =
    ErrorEntry.isFailing warningsAreFailures e

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
type ErrorLedger(?reporters: IErrorReporter list) =
    let reporters = defaultArg reporters []

    let notifyReporters action =
        for r in reporters do
            try
                action r
            with ex ->
                Logging.error "error-ledger" $"Reporter failed: %s{ex.Message}"

    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop (state: LedgerState) =
                async {
                    let! msg = inbox.Receive()

                    let newState =
                        try
                            match msg with
                            | Report(plugin, file, entries, version) ->
                                let key = struct (plugin, file)

                                let accepted, state' =
                                    match version with
                                    | Some v -> tryAcceptVersion key v state
                                    | None -> true, state

                                if accepted then
                                    if entries.IsEmpty then
                                        notifyReporters (fun r -> r.Clear plugin file)

                                        { state' with
                                            Errors = Map.remove key state'.Errors }
                                    else
                                        notifyReporters (fun r -> r.Report plugin file entries)

                                        { state' with
                                            Errors = Map.add key entries state'.Errors }
                                else
                                    state'

                            | Clear(plugin, file, version) ->
                                let key = struct (plugin, file)

                                let accepted, state' =
                                    match version with
                                    | Some v -> tryAcceptVersion key v state
                                    | None -> true, state

                                if accepted then
                                    notifyReporters (fun r -> r.Clear plugin file)

                                    { state' with
                                        Errors = Map.remove key state'.Errors }
                                else
                                    state'

                            | ClearPlugin plugin ->
                                let newErrors = state.Errors |> Map.filter (fun (struct (p, _)) _ -> p <> plugin)

                                let newVersions =
                                    state.Versions |> Map.filter (fun (struct (p, _)) _ -> p <> plugin)

                                notifyReporters (fun r -> r.ClearPlugin plugin)

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
                        with ex ->
                            Logging.error "error-ledger" $"Agent failed: %s{ex.ToString()}"
                            state

                    return! loop newState
                }

            loop
                { Errors = Map.empty
                  Versions = Map.empty })

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

    /// Get all failing entries grouped by file path, filtered by severity.
    /// When warningsAreFailures is true, both Error and Warning entries are included.
    /// When false, only Error entries are included.
    member _.FailingReasons(warningsAreFailures: bool) : Map<string, (string * ErrorEntry) list> =
        agent.PostAndReply(fun rc -> FailingReasons(warningsAreFailures, rc))

    /// True if any failing entries exist (Error, or Warning when warningsAreFailures=true).
    member _.HasFailingReasons(warningsAreFailures: bool) =
        agent.PostAndReply(fun rc -> HasFailingReasons(warningsAreFailures, rc))
