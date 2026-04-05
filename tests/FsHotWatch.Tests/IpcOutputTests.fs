module FsHotWatch.Tests.IpcOutputTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.IpcOutput

[<Fact>]
let ``parseErrorsResponse extracts count`` () =
    let json = """{"count":2,"files":{},"statuses":{}}"""
    let result = parseErrorsResponse json
    test <@ result.Count = 2 @>

[<Fact>]
let ``parseErrorsResponse extracts files with entries`` () =
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad name","severity":"warning","line":17,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseErrorsResponse json
    test <@ result.Files.ContainsKey("src/Foo.fs") @>
    let entries = result.Files["src/Foo.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries[0].Plugin = "lint" @>
    test <@ entries[0].Message = "bad name" @>
    test <@ entries[0].Severity = "warning" @>
    test <@ entries[0].Line = 17 @>

[<Fact>]
let ``parseErrorsResponse extracts statuses`` () =
    let json =
        """{"count":0,"files":{},"statuses":{"build":"Completed at 2026-04-05T12:00:00.0000000Z","lint":"Idle"}}"""

    let result = parseErrorsResponse json
    test <@ result.Statuses.ContainsKey("build") @>
    test <@ result.Statuses["build"].Contains("Completed") @>

[<Fact>]
let ``parseStatusMap parses completed status`` () =
    let statuses = Map.ofList [ "build", "Completed at 2026-04-05T12:00:00.0000000Z" ]
    let parsed = parseStatusMap statuses
    test <@ parsed.ContainsKey("build") @>

    match parsed["build"] with
    | DisplayCompleted _ -> ()
    | other -> failwith $"Expected DisplayCompleted, got %A{other}"

[<Fact>]
let ``parseStatusMap parses running status`` () =
    let statuses = Map.ofList [ "lint", "Running since 2026-04-05T12:00:00.0000000Z" ]
    let parsed = parseStatusMap statuses
    test <@ parsed.ContainsKey("lint") @>

    match parsed["lint"] with
    | DisplayRunning _ -> ()
    | other -> failwith $"Expected DisplayRunning, got %A{other}"

[<Fact>]
let ``parseStatusMap parses failed status`` () =
    let statuses =
        Map.ofList [ "build", "Failed at 2026-04-05T12:00:00.0000000Z: compile error" ]

    let parsed = parseStatusMap statuses

    match parsed["build"] with
    | DisplayFailed(msg, _) -> test <@ msg = "compile error" @>
    | other -> failwith $"Expected DisplayFailed, got %A{other}"

[<Fact>]
let ``parseStatusMap parses idle status`` () =
    let statuses = Map.ofList [ "format", "Idle" ]
    let parsed = parseStatusMap statuses

    match parsed["format"] with
    | DisplayIdle -> ()
    | other -> failwith $"Expected DisplayIdle, got %A{other}"

[<Fact>]
let ``formatErrorsResponse with no errors shows clean message`` () =
    let json =
        """{"count":0,"files":{},"statuses":{"build":"Completed at 2026-04-05T12:00:00.0000000Z"}}"""

    let result = parseErrorsResponse json
    let output = formatErrorsResponse result
    test <@ output.Contains("No errors") @>

[<Fact>]
let ``formatErrorsResponse with errors shows file and message`` () =
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad name","severity":"warning","line":17,"column":0,"detail":null}]},"statuses":{"lint":"Completed at 2026-04-05T12:00:00.0000000Z"}}"""

    let result = parseErrorsResponse json
    let output = formatErrorsResponse result
    test <@ output.Contains("src/Foo.fs") @>
    test <@ output.Contains("[lint]") @>
    test <@ output.Contains("L17") @>
    test <@ output.Contains("bad name") @>

[<Fact>]
let ``formatErrorsResponse with errors shows count summary`` () =
    let json =
        """{"count":2,"files":{"src/A.fs":[{"plugin":"lint","message":"x","severity":"warning","line":1,"column":0,"detail":null}],"src/B.fs":[{"plugin":"build","message":"y","severity":"error","line":2,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseErrorsResponse json
    let output = formatErrorsResponse result
    test <@ output.Contains("2 error(s) in 2 file(s)") @>

[<Fact>]
let ``formatStatusLine shows checkmark for completed`` () =
    let line = formatStatusLine "build" (DisplayCompleted(System.DateTime.UtcNow))
    test <@ line.Contains("\u2713") @>
    test <@ line.Contains("build") @>

[<Fact>]
let ``formatStatusLine shows X for failed`` () =
    let line =
        formatStatusLine "build" (DisplayFailed("compile error", System.DateTime.UtcNow))

    test <@ line.Contains("\u2717") @>
    test <@ line.Contains("compile error") @>

[<Fact>]
let ``isAllTerminal returns true when all completed or failed`` () =
    let statuses =
        Map.ofList
            [ "build", DisplayCompleted System.DateTime.UtcNow
              "lint", DisplayFailed("x", System.DateTime.UtcNow) ]

    test <@ isAllTerminal statuses @>

[<Fact>]
let ``isAllTerminal returns false when any running`` () =
    let statuses =
        Map.ofList
            [ "build", DisplayCompleted System.DateTime.UtcNow
              "lint", DisplayRunning System.DateTime.UtcNow ]

    test <@ not (isAllTerminal statuses) @>

[<Fact>]
let ``exitCodeFromResponse returns 0 for count 0`` () =
    let resp =
        { Count = 0
          Files = Map.empty
          Statuses = Map.empty }

    test <@ exitCodeFromResponse resp = 0 @>

[<Fact>]
let ``exitCodeFromResponse returns 1 for count > 0`` () =
    let resp =
        { Count = 3
          Files = Map.empty
          Statuses = Map.empty }

    test <@ exitCodeFromResponse resp = 1 @>
