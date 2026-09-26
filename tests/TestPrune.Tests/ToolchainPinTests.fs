module TestPrune.Tests.ToolchainPinTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: no .config/dotnet-tools.json in any ancestor"
        elif File.Exists(Path.Combine(directory.FullName, ".config", "dotnet-tools.json")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

[<Theory>]
// Change this one parameter only after the behaviour this repo's gate depends on
// has a real CLI release. alpha.45 was the first pin whose bundled TestPrune plugin
// selects the CTRF report flag by runner family, which is what lets the gate drive
// an xunit.v3 4.0.0 runner; the xunit-3-only flag made a 4.0.0 host exit 5 and
// report zero tests rather than fail.
[<InlineData("0.14.0-alpha.67")>]
let ``FsHotWatch gate uses the reviewed released pin`` (expectedReleasedVersion: string) =
    let root = repoRoot ()

    use manifest =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json")))

    let tool = manifest.RootElement.GetProperty("tools").GetProperty("fshotwatch.cli")
    let pinnedVersion = tool.GetProperty("version").GetString()
    let rollsForward = tool.GetProperty("rollForward").GetBoolean()
    test <@ pinnedVersion = expectedReleasedVersion @>
    test <@ rollsForward = false @>

    let commands =
        tool.GetProperty("commands").EnumerateArray()
        |> Seq.map (fun command -> command.GetString())
        |> Seq.toList

    test <@ commands = [ "fshw" ] @>

    let mise = File.ReadAllText(Path.Combine(root, "mise.toml"))

    let workflow =
        File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))

    test <@ mise.Contains("dotnet fshw check --run-once") @>
    test <@ mise.Contains("dotnet fshw confirm --run-once") @>
    test <@ workflow.Contains("dotnet fshw confirm --run-once") @>
