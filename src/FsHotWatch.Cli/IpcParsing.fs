module FsHotWatch.Cli.IpcParsing

open System
open System.Globalization
open System.Text.Json
open FsHotWatch.Events
open FsHotWatch.ErrorLedger

/// A single diagnostic entry parsed from IPC JSON.
type DiagnosticEntry =
    { Plugin: string
      Message: string
      Severity: DiagnosticSeverity
      Line: int
      Column: int
      Detail: string option }

type ParsedPluginStatus = RunOnceOutput.ParsedPluginStatus

/// Parsed GetDiagnostics response.
type DiagnosticsResponse =
    { Count: int
      Files: Map<string, DiagnosticEntry list>
      Statuses: Map<string, ParsedPluginStatus> }

let private tryParseUtcOpt (s: string) : DateTime option =
    match DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) with
    | true, dt -> Some dt
    | false, _ -> None

let private tryParseUtcOr (fallback: DateTime) (s: string) : DateTime =
    match tryParseUtcOpt s with
    | Some dt -> dt
    | None -> fallback

/// Parse a legacy status string (e.g. "Running since <iso>") into PluginStatus.
/// Kept for the GetPluginStatus single-plugin RPC, which returns a bare string.
let parseStatus (s: string) : PluginStatus =
    if s = "Idle" then
        Idle
    elif s.StartsWith("Running since ") then
        match tryParseUtcOpt (s.Substring("Running since ".Length)) with
        | Some dt -> Running dt
        | None -> Idle
    elif s.StartsWith("Completed at ") then
        match tryParseUtcOpt (s.Substring("Completed at ".Length)) with
        | Some dt -> Completed dt
        | None -> Completed DateTime.UtcNow
    elif s.StartsWith("Failed at ") then
        let rest = s.Substring("Failed at ".Length)

        match rest.IndexOf(": ") with
        | -1 ->
            match tryParseUtcOpt rest with
            | Some dt -> Failed("", dt)
            | None -> Failed(rest, DateTime.UtcNow)
        | idx ->
            let dtStr = rest.Substring(0, idx)
            let msg = rest.Substring(idx + 2)

            match tryParseUtcOpt dtStr with
            | Some dt -> Failed(msg, dt)
            | None -> Failed(msg, DateTime.UtcNow)
    else
        Idle

/// Parse a map of legacy status strings into PluginStatus values.
let parseStatusMap (statuses: Map<string, string>) : Map<string, PluginStatus> =
    statuses |> Map.map (fun _ s -> parseStatus s)

let private tryGetStringProp (el: JsonElement) (name: string) : string option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// Parse a tagged PluginStatus object, e.g. {"tag":"running","since":"..."}.
/// Returns None if the element isn't a recognizable tagged status.
let parseTaggedStatus (el: JsonElement) : PluginStatus option =
    if el.ValueKind <> JsonValueKind.Object then
        None
    else
        match tryGetStringProp el "tag" with
        | Some "idle" -> Some Idle
        | Some "running" -> tryGetStringProp el "since" |> Option.bind tryParseUtcOpt |> Option.map Running
        | Some "completed" -> tryGetStringProp el "at" |> Option.bind tryParseUtcOpt |> Option.map Completed
        | Some "failed" ->
            let err = tryGetStringProp el "error" |> Option.defaultValue ""
            let at = tryGetStringProp el "at" |> Option.bind tryParseUtcOpt

            match at with
            | Some dt -> Some(Failed(err, dt))
            | None -> Some(Failed(err, DateTime.UtcNow))
        | _ -> None

/// Parse the status field of a plugin-status payload, accepting either
/// the new tagged-object shape or the legacy string shape.
let parseStatusField (el: JsonElement) : PluginStatus =
    match el.ValueKind with
    | JsonValueKind.String -> parseStatus (el.GetString())
    | JsonValueKind.Object ->
        match parseTaggedStatus el with
        | Some s -> s
        | None -> Idle
    | _ -> Idle

/// Parse a tagged RunOutcome object, e.g. {"tag":"failed","error":"..."}.
let parseTaggedOutcome (el: JsonElement) : RunOutcome option =
    if el.ValueKind <> JsonValueKind.Object then
        None
    else
        match tryGetStringProp el "tag" with
        | Some "completed" -> Some CompletedRun
        | Some "failed" ->
            let err = tryGetStringProp el "error" |> Option.defaultValue ""
            Some(FailedRun err)
        | _ -> None

/// Parse a lastRun.outcome field, accepting the new tagged-object shape or
/// the legacy ("Failed"/"Completed" string + separate error field) shape.
let parseOutcomeField (outcomeEl: JsonElement) (legacyErrorEl: JsonElement voption) : RunOutcome =
    match parseTaggedOutcome outcomeEl with
    | Some o -> o
    | None ->
        let outcomeStr =
            match outcomeEl.ValueKind with
            | JsonValueKind.String -> outcomeEl.GetString()
            | _ -> ""

        let legacyError =
            match legacyErrorEl with
            | ValueSome e when e.ValueKind = JsonValueKind.String -> e.GetString()
            | _ -> ""

        match outcomeStr with
        | "Failed" -> FailedRun legacyError
        | _ -> CompletedRun

/// Parse a single structured plugin-status JSON element.
let parsePluginStatusElement (el: JsonElement) : ParsedPluginStatus =
    let status =
        match el.TryGetProperty("status") with
        | true, s -> parseStatusField s
        | false, _ -> Idle

    let subtasks =
        match el.TryGetProperty("subtasks") with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            [ for item in arr.EnumerateArray() ->
                  { Key = item.GetProperty("key").GetString()
                    Label = item.GetProperty("label").GetString()
                    StartedAt = tryParseUtcOr DateTime.UtcNow (item.GetProperty("startedAt").GetString()) } ]
        | _ -> []

    let activityTail =
        match el.TryGetProperty("activityTail") with
        | true, arr when arr.ValueKind = JsonValueKind.Array -> [ for item in arr.EnumerateArray() -> item.GetString() ]
        | _ -> []

    let lastRun =
        match el.TryGetProperty("lastRun") with
        | true, r when r.ValueKind = JsonValueKind.Object ->
            let startedAt =
                tryParseUtcOr DateTime.UtcNow (r.GetProperty("startedAt").GetString())

            let elapsedMs = r.GetProperty("elapsedMs").GetInt64()

            let outcomeEl = r.GetProperty("outcome")

            let legacyErrorEl =
                match r.TryGetProperty("error") with
                | true, e -> ValueSome e
                | false, _ -> ValueNone

            let outcome = parseOutcomeField outcomeEl legacyErrorEl

            let summary =
                match r.TryGetProperty("summary") with
                | true, s when s.ValueKind <> JsonValueKind.Null -> Some(s.GetString())
                | _ -> None

            let tail =
                match r.TryGetProperty("activityTail") with
                | true, arr when arr.ValueKind = JsonValueKind.Array ->
                    [ for item in arr.EnumerateArray() -> item.GetString() ]
                | _ -> []

            Some
                { StartedAt = startedAt
                  Elapsed = TimeSpan.FromMilliseconds(float elapsedMs)
                  Outcome = outcome
                  Summary = summary
                  ActivityTail = tail }
        | _ -> None

    { Status = status
      Subtasks = subtasks
      ActivityTail = activityTail
      LastRun = lastRun }

/// Parse the top-level JSON object returned by GetStatus into structured per-plugin status.
let parsePluginStatuses (json: string) : Map<string, ParsedPluginStatus> =
    try
        use doc = JsonDocument.Parse(json)

        [ for prop in doc.RootElement.EnumerateObject() do
              if prop.Value.ValueKind = JsonValueKind.Object then
                  prop.Name, parsePluginStatusElement prop.Value ]
        |> Map.ofList
    with _ ->
        Map.empty

/// Project a ParsedPluginStatus map to plain PluginStatus values.
let statusOnly (parsed: Map<string, ParsedPluginStatus>) : Map<string, PluginStatus> =
    parsed |> Map.map (fun _ p -> p.Status)

/// Parse the JSON response from GetDiagnostics RPC.
let parseDiagnosticsResponse (json: string) : DiagnosticsResponse =
    use doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    let count =
        match root.TryGetProperty("count") with
        | true, v -> v.GetInt32()
        | false, _ -> 0

    let files =
        match root.TryGetProperty("files") with
        | true, filesEl ->
            [ for prop in filesEl.EnumerateObject() do
                  let entries =
                      [ for entry in prop.Value.EnumerateArray() do
                            { Plugin = entry.GetProperty("plugin").GetString()
                              Message = entry.GetProperty("message").GetString()
                              Severity = DiagnosticSeverity.fromString (entry.GetProperty("severity").GetString())
                              Line = entry.GetProperty("line").GetInt32()
                              Column = entry.GetProperty("column").GetInt32()
                              Detail =
                                match entry.TryGetProperty("detail") with
                                | true, d when d.ValueKind <> JsonValueKind.Null -> Some(d.GetString())
                                | _ -> None } ]

                  prop.Name, entries ]
            |> Map.ofList
        | false, _ -> Map.empty

    let statuses =
        match root.TryGetProperty("statuses") with
        | true, statusEl ->
            [ for prop in statusEl.EnumerateObject() do
                  if prop.Value.ValueKind = JsonValueKind.Object then
                      prop.Name, parsePluginStatusElement prop.Value ]
            |> Map.ofList
        | false, _ -> Map.empty

    { Count = count
      Files = files
      Statuses = statuses }

/// Check if all statuses are terminal (Completed, Failed, or Idle).
/// Returns false for empty maps (no plugins registered yet).
/// Idle is treated as terminal because this is only called after WaitForScan returns,
/// at which point Idle means the plugin was not triggered by this scan cycle.
let isAllTerminal (statuses: Map<string, PluginStatus>) : bool =
    not statuses.IsEmpty
    && statuses
       |> Map.forall (fun _ s ->
           match s with
           | Completed _
           | Failed _
           | Idle -> true
           | _ -> false)
