module FsHotWatch.Cli.IpcOutput

open System
open System.Globalization
open System.Text.Json
open System.Threading
open CommandTree
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

/// Fully parsed per-plugin status including subtasks, activity tail, and last-run history.
type ParsedPluginStatus =
    { Status: PluginStatus
      Subtasks: Subtask list
      ActivityTail: string list
      LastRun: RunRecord option }

/// Parse a status string from IPC into PluginStatus.
let parseStatus (s: string) : PluginStatus =
    let tryParseUtc (s: string) =
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)

    if s = "Idle" then
        Idle
    elif s.StartsWith("Running since ") then
        match tryParseUtc (s.Substring("Running since ".Length)) with
        | true, dt -> Running dt
        | false, _ -> Idle
    elif s.StartsWith("Completed at ") then
        match tryParseUtc (s.Substring("Completed at ".Length)) with
        | true, dt -> Completed dt
        | false, _ -> Completed DateTime.UtcNow
    elif s.StartsWith("Failed at ") then
        let rest = s.Substring("Failed at ".Length)

        match rest.IndexOf(": ") with
        | -1 ->
            match tryParseUtc rest with
            | true, dt -> Failed("", dt)
            | false, _ -> Failed(rest, DateTime.UtcNow)
        | idx ->
            let dtStr = rest.Substring(0, idx)
            let msg = rest.Substring(idx + 2)

            match tryParseUtc dtStr with
            | true, dt -> Failed(msg, dt)
            | false, _ -> Failed(msg, DateTime.UtcNow)
    else
        Idle

/// Parse a map of status strings into PluginStatus values.
let parseStatusMap (statuses: Map<string, string>) : Map<string, PluginStatus> =
    statuses |> Map.map (fun _ s -> parseStatus s)

/// Parse a single structured plugin-status JSON element.
let parsePluginStatusElement (el: JsonElement) : ParsedPluginStatus =
    let status =
        match el.TryGetProperty("status") with
        | true, s -> parseStatus (s.GetString())
        | false, _ -> Idle

    let tryParseUtc (s: string) =
        let ok, v =
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)

        if ok then v else DateTime.UtcNow

    let subtasks =
        match el.TryGetProperty("subtasks") with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            [ for item in arr.EnumerateArray() ->
                  { Key = item.GetProperty("key").GetString()
                    Label = item.GetProperty("label").GetString()
                    StartedAt = tryParseUtc (item.GetProperty("startedAt").GetString()) } ]
        | _ -> []

    let activityTail =
        match el.TryGetProperty("activityTail") with
        | true, arr when arr.ValueKind = JsonValueKind.Array -> [ for item in arr.EnumerateArray() -> item.GetString() ]
        | _ -> []

    let lastRun =
        match el.TryGetProperty("lastRun") with
        | true, r when r.ValueKind = JsonValueKind.Object ->
            let startedAt = tryParseUtc (r.GetProperty("startedAt").GetString())
            let elapsedMs = r.GetProperty("elapsedMs").GetInt64()
            let outcomeStr = r.GetProperty("outcome").GetString()

            let error =
                match r.TryGetProperty("error") with
                | true, e when e.ValueKind <> JsonValueKind.Null -> e.GetString()
                | _ -> ""

            let outcome =
                match outcomeStr with
                | "Failed" -> FailedRun error
                | _ -> CompletedRun

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

/// Parsed GetDiagnostics response.
type DiagnosticsResponse =
    { Count: int
      Files: Map<string, DiagnosticEntry list>
      Statuses: Map<string, ParsedPluginStatus> }

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

/// Render all plugin statuses as a multi-line progress display.
let renderProgress (statuses: Map<string, PluginStatus>) : string =
    statuses
    |> Map.toList
    |> List.map (fun (name, status) -> RunOnceOutput.formatStepResult name status)
    |> String.concat "\n"

/// Format the full errors response with colored status lines and error details.
let formatDiagnosticsResponse (resp: DiagnosticsResponse) : string =
    let sb = System.Text.StringBuilder()

    // Status summary
    let parsedStatuses = statusOnly resp.Statuses
    let summary = RunOnceOutput.formatSummary parsedStatuses

    if summary <> "" then
        sb.AppendLine(summary) |> ignore
        sb.AppendLine() |> ignore

    // Convert DiagnosticEntry to (pluginName * ErrorEntry) for shared formatting
    let errorMap =
        resp.Files
        |> Map.map (fun _ entries ->
            entries
            |> List.map (fun d ->
                d.Plugin,
                { Message = d.Message
                  Severity = d.Severity
                  Line = d.Line
                  Column = d.Column
                  Detail = d.Detail }))

    sb.Append(RunOnceOutput.formatErrors errorMap) |> ignore
    sb.ToString().TrimEnd('\n', '\r')

/// Determine exit code from a DiagnosticsResponse.
/// When noWarnFail is true, only errors (not warnings) cause a non-zero exit code.
let exitCodeFromResponse (noWarnFail: bool) (resp: DiagnosticsResponse) : int =
    let isFailure (e: DiagnosticEntry) =
        match e.Severity with
        | Error -> true
        | Warning -> not noWarnFail
        | Info
        | Hint -> false

    let failCount =
        resp.Files |> Map.toSeq |> Seq.collect snd |> Seq.filter isFailure |> Seq.length

    if failCount > 0 then 1 else 0

/// Parse a JSON object into a string-to-string map (for status responses).
/// Used for legacy flat-map status payloads. New structured payloads use parsePluginStatuses.
let parseStatusJson (json: string) : Map<string, string> =
    try
        use doc = JsonDocument.Parse(json)

        [ for prop in doc.RootElement.EnumerateObject() do
              if prop.Value.ValueKind = JsonValueKind.String then
                  prop.Name, prop.Value.GetString() ]
        |> Map.ofList
    with _ ->
        Map.empty

/// Render a generic IPC result (status JSON or plain text).
/// Dispatches on JSON shape: GetDiagnostics format (has "count"), error/status fields, status map, or plain text.
let renderIpcResult (noWarnFail: bool) (result: string) : int =
    let doc =
        try
            Some(JsonDocument.Parse(result))
        with _ ->
            None

    match doc with
    | None ->
        eprintfn "%s" result
        0
    | Some doc ->
        use doc = doc
        let root = doc.RootElement

        match root.TryGetProperty("count") with
        | true, _ ->
            let resp = parseDiagnosticsResponse result
            let output = formatDiagnosticsResponse resp
            eprintfn "%s" output
            exitCodeFromResponse noWarnFail resp
        | false, _ ->

            match root.TryGetProperty("error") with
            | true, e ->
                UI.fail (e.GetString())
                1
            | false, _ ->

                match root.TryGetProperty("status") with
                | true, v when v.ValueKind = JsonValueKind.String && v.GetString() = "failed" ->
                    UI.fail "Failed"
                    1
                | true, v when v.ValueKind = JsonValueKind.String && v.GetString() = "passed" ->
                    UI.success "Passed"
                    0
                | _ ->

                    match root.TryGetProperty("projects") with
                    | true, projects when projects.ValueKind = JsonValueKind.Array ->
                        let hasFailed =
                            projects.EnumerateArray()
                            |> Seq.exists (fun p ->
                                match p.TryGetProperty("status") with
                                | true, s -> s.GetString() = "failed"
                                | false, _ -> false)

                        if hasFailed then
                            UI.fail "Tests failed"
                            1
                        else
                            UI.success "Tests passed"
                            0
                    | _ ->

                        let parsed = parsePluginStatuses result
                        let plain = statusOnly parsed
                        let output = renderProgress plain
                        eprintfn "%s" output

                        let hasFailed =
                            plain
                            |> Map.exists (fun _ s ->
                                match s with
                                | Failed _ -> true
                                | _ -> false)

                        if hasFailed then 1 else 0

/// Poll daemon status, render live progress, then format final errors.
/// Returns exit code (0 = no errors, 1 = errors).
let pollAndRender
    (noWarnFail: bool)
    (waitForScan: unit -> string)
    (getStatus: unit -> string)
    (getErrors: unit -> string)
    : int =
    // Phase 1: Wait for scan
    if UI.isInteractive then
        UI.withSpinnerQuiet "Scanning" (fun () -> waitForScan () |> ignore)
    else
        eprintfn "  Scanning..."
        waitForScan () |> ignore

    // Phase 2: Poll status until all terminal
    let mutable prevLineCount = 0
    let mutable allDone = false

    while not allDone do
        let statusJson = getStatus ()
        let parsed = parsePluginStatuses statusJson
        let plain = statusOnly parsed

        if UI.isInteractive then
            // Move cursor up to overwrite previous lines
            if prevLineCount > 0 then
                for _ in 1..prevLineCount do
                    Console.Error.Write("\x1b[A\x1b[2K")

            // Render current state
            let progress = renderProgress plain
            eprintfn "%s" progress
            prevLineCount <- plain.Count

        allDone <- isAllTerminal plain

        if not allDone then
            Thread.Sleep(200)

    // Phase 3: Clear interactive progress
    if UI.isInteractive && prevLineCount > 0 then
        for _ in 1..prevLineCount do
            Console.Error.Write("\x1b[A\x1b[2K")

    // Phase 4: Get errors and render final output
    let errorsJson = getErrors ()
    let resp = parseDiagnosticsResponse errorsJson
    let output = formatDiagnosticsResponse resp
    eprintfn "%s" output
    exitCodeFromResponse noWarnFail resp
