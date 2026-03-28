module FsHotWatch.ErrorLedger

/// A single diagnostic entry from a plugin.
type ErrorEntry =
    { Message: string
      Severity: string
      Line: int
      Column: int }

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
    | HasErrors of AsyncReplyChannel<bool>
    | GetCount of AsyncReplyChannel<int>

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
type ErrorLedger() =
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
                                        { state' with
                                            Errors = Map.remove key state'.Errors }
                                    else
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
                                    { state' with
                                        Errors = Map.remove key state'.Errors }
                                else
                                    state'

                            | ClearPlugin plugin ->
                                let newErrors = state.Errors |> Map.filter (fun (struct (p, _)) _ -> p <> plugin)

                                { state with Errors = newErrors }

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

                            | HasErrors rc ->
                                rc.Reply(not state.Errors.IsEmpty)
                                state

                            | GetCount rc ->
                                let count = state.Errors |> Map.values |> Seq.sumBy List.length
                                rc.Reply(count)
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

    /// True if any errors exist.
    member _.HasErrors() =
        agent.PostAndReply(fun rc -> HasErrors rc)

    /// Total error count across all plugins and files.
    member _.Count() =
        agent.PostAndReply(fun rc -> GetCount rc)
