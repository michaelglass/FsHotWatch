module FsHotWatch.Cli.IpcOutput

open System
open System.Globalization
open System.Text.Json
open CommandTree

/// Parsed status for display purposes (no dependency on FsHotWatch.Events).
type DisplayStatus =
    | DisplayIdle
    | DisplayRunning of since: DateTime
    | DisplayCompleted of at: DateTime
    | DisplayFailed of error: string * at: DateTime

/// A single error entry parsed from IPC JSON.
type ErrorEntry =
    { Plugin: string
      Message: string
      Severity: string
      Line: int
      Column: int
      Detail: string option }

/// Parsed GetErrors response.
type ErrorsResponse =
    { Count: int
      Files: Map<string, ErrorEntry list>
      Statuses: Map<string, string> }

/// Parse the JSON response from GetErrors RPC.
let parseErrorsResponse (json: string) : ErrorsResponse =
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

/// Format the full errors response with colored status lines and error details.
let formatErrorsResponse (resp: ErrorsResponse) : string =
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

/// Determine exit code from an ErrorsResponse.
let exitCodeFromResponse (resp: ErrorsResponse) : int = if resp.Count > 0 then 1 else 0
