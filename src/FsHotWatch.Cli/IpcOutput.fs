module FsHotWatch.Cli.IpcOutput

open System
open System.Globalization
open System.Text.Json
open System.Threading
open CommandTree

/// Parsed status for display purposes (no dependency on FsHotWatch.Events).
type DisplayStatus =
    | DisplayIdle
    | DisplayRunning of since: DateTime
    | DisplayCompleted of at: DateTime
    | DisplayFailed of error: string * at: DateTime

/// A single diagnostic entry parsed from IPC JSON.
type DiagnosticEntry =
    { Plugin: string
      Message: string
      Severity: string
      Line: int
      Column: int
      Detail: string option }

/// Parsed GetDiagnostics response.
type DiagnosticsResponse =
    { Count: int
      Files: Map<string, DiagnosticEntry list>
      Statuses: Map<string, string> }

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
                              Severity = entry.GetProperty("severity").GetString()
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
                  prop.Name, prop.Value.GetString() ]
            |> Map.ofList
        | false, _ -> Map.empty

    { Count = count
      Files = files
      Statuses = statuses }

/// Parse a status string from IPC into DisplayStatus.
let private parseStatus (s: string) : DisplayStatus =
    let tryParseUtc (s: string) =
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)

    if s = "Idle" then
        DisplayIdle
    elif s.StartsWith("Running since ") then
        match tryParseUtc (s.Substring("Running since ".Length)) with
        | true, dt -> DisplayRunning dt
        | false, _ -> DisplayIdle
    elif s.StartsWith("Completed at ") then
        match tryParseUtc (s.Substring("Completed at ".Length)) with
        | true, dt -> DisplayCompleted dt
        | false, _ -> DisplayCompleted DateTime.UtcNow
    elif s.StartsWith("Failed at ") then
        let rest = s.Substring("Failed at ".Length)

        match rest.IndexOf(": ") with
        | -1 ->
            match tryParseUtc rest with
            | true, dt -> DisplayFailed("", dt)
            | false, _ -> DisplayFailed(rest, DateTime.UtcNow)
        | idx ->
            let dtStr = rest.Substring(0, idx)
            let msg = rest.Substring(idx + 2)

            match tryParseUtc dtStr with
            | true, dt -> DisplayFailed(msg, dt)
            | false, _ -> DisplayFailed(msg, DateTime.UtcNow)
    else
        DisplayIdle

/// Parse a map of status strings into DisplayStatus values.
let parseStatusMap (statuses: Map<string, string>) : Map<string, DisplayStatus> =
    statuses |> Map.map (fun _ s -> parseStatus s)

/// Check if all statuses are terminal (Completed or Failed).
/// Returns false for empty maps (no plugins registered yet).
let isAllTerminal (statuses: Map<string, DisplayStatus>) : bool =
    not statuses.IsEmpty
    && statuses
       |> Map.forall (fun _ s ->
           match s with
           | DisplayCompleted _
           | DisplayFailed _ -> true
           | _ -> false)

/// Format a single status line with icon, name, and timing.
let formatStatusLine (name: string) (status: DisplayStatus) : string =
    let paddedName = name.PadRight(24)

    match status with
    | DisplayCompleted _ -> $"  %s{Color.green}\u2713%s{Color.reset} %s{paddedName}"
    | DisplayFailed(error, _) ->
        $"  %s{Color.red}\u2717%s{Color.reset} %s{paddedName} %s{Color.dim}\u2014 %s{error}%s{Color.reset}"
    | DisplayRunning since ->
        let elapsed = DateTime.UtcNow - since
        let timingStr = UI.timing elapsed
        $"  %s{Color.yellow}\u2026%s{Color.reset} %s{paddedName} %s{timingStr}"
    | DisplayIdle -> $"  %s{Color.dim}\u2014 %s{paddedName}%s{Color.reset}"

/// Render all plugin statuses as a multi-line progress display.
let renderProgress (statuses: Map<string, DisplayStatus>) : string =
    statuses
    |> Map.toList
    |> List.map (fun (name, status) -> formatStatusLine name status)
    |> String.concat "\n"

/// Format the full errors response with colored status lines and error details.
let formatDiagnosticsResponse (resp: DiagnosticsResponse) : string =
    let sb = System.Text.StringBuilder()

    // Status summary
    let parsedStatuses = parseStatusMap resp.Statuses

    for KeyValue(name, status) in parsedStatuses do
        sb.AppendLine(formatStatusLine name status) |> ignore

    if not parsedStatuses.IsEmpty then
        sb.AppendLine() |> ignore

    // Error details
    if resp.Count = 0 then
        sb.Append($"%s{Color.green}No errors%s{Color.reset}") |> ignore
    else
        for KeyValue(file, entries) in resp.Files do
            sb.AppendLine() |> ignore
            sb.AppendLine($"%s{Color.bold}%s{file}%s{Color.reset}") |> ignore

            for entry in entries do
                let severityLabel =
                    match entry.Severity with
                    | "error" -> $"%s{Color.red}error%s{Color.reset}: "
                    | "warning" -> $"%s{Color.yellow}warning%s{Color.reset}: "
                    | _ -> ""

                sb.AppendLine(
                    $"  %s{Color.dim}[%s{entry.Plugin}]%s{Color.reset} L%d{entry.Line}: %s{severityLabel}%s{entry.Message}"
                )
                |> ignore

        sb.AppendLine() |> ignore
        let fileCount = resp.Files.Count
        sb.Append($"%d{resp.Count} error(s) in %d{fileCount} file(s)") |> ignore

    sb.ToString().TrimEnd('\n', '\r')

/// Determine exit code from a DiagnosticsResponse.
/// When noWarnFail is true, only errors (not warnings) cause a non-zero exit code.
let exitCodeFromResponse (noWarnFail: bool) (resp: DiagnosticsResponse) : int =
    let isFailure (e: DiagnosticEntry) =
        match e.Severity with
        | "error" -> true
        | "warning" -> not noWarnFail
        | _ -> false

    let failCount =
        resp.Files |> Map.toSeq |> Seq.collect snd |> Seq.filter isFailure |> Seq.length

    if failCount > 0 then 1 else 0

/// Parse a JSON object into a string-to-string map (for status responses).
let parseStatusJson (json: string) : Map<string, string> =
    try
        use doc = JsonDocument.Parse(json)

        [ for prop in doc.RootElement.EnumerateObject() do
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
                | true, v when v.GetString() = "failed" ->
                    UI.fail "Failed"
                    1
                | true, v when v.GetString() = "passed" ->
                    UI.success "Passed"
                    0
                | _ ->

                    let statusMap =
                        [ for prop in root.EnumerateObject() do
                              prop.Name, prop.Value.GetString() ]
                        |> Map.ofList

                    let parsed = parseStatusMap statusMap
                    let output = renderProgress parsed
                    eprintfn "%s" output

                    let hasFailed =
                        parsed
                        |> Map.exists (fun _ s ->
                            match s with
                            | DisplayFailed _ -> true
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
        let parsed = parseStatusMap (parseStatusJson statusJson)

        if UI.isInteractive then
            // Move cursor up to overwrite previous lines
            if prevLineCount > 0 then
                for _ in 1..prevLineCount do
                    Console.Error.Write("\x1b[A\x1b[2K")

            // Render current state
            let progress = renderProgress parsed
            eprintfn "%s" progress
            prevLineCount <- parsed.Count

        allDone <- isAllTerminal parsed

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
