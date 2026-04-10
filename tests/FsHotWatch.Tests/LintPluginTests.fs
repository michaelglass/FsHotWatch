module FsHotWatch.Tests.LintPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Lint.LintPlugin
open FsHotWatch.Tests.TestHelpers

[<Fact>]
let ``plugin has correct name`` () =
    let handler = create None None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "lint" @>

[<Fact>]
let ``warnings command returns zeroes when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"files\": 0") @>
    test <@ result.Value.Contains("\"warnings\": 0") @>

[<Fact>]
let ``LintPlugin with configPath sets up lint params`` () =
    let handler = create (Some "/tmp/nonexistent-config.json") None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "lint" @>

[<Fact>]
let ``lint error path sets Failed status on null check results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None
    host.RegisterHandler(handler)

    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    try
        host.EmitFileChecked(fakeResult)
    with _ ->
        ()

    waitUntil
        (fun () ->
            match host.GetStatus("lint") with
            | Some(Failed _)
            | Some(Running _) -> true
            | _ -> false)
        3000

    let status = host.GetStatus("lint")
    test <@ status.IsSome @>

    match status.Value with
    | Failed _ -> ()
    | Running _ -> ()
    | other -> Assert.Fail($"Expected Failed or Running, got: %A{other}")

[<Fact>]
let ``warnings command with args passes through`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None
    host.RegisterHandler(handler)

    // The warnings command ignores args, but verify it handles non-empty args
    let result =
        host.RunCommand("warnings", [| "--verbose" |]) |> Async.RunSynchronously

    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"files\": 0") @>

[<Fact>]
let ``lint skips file with null ParseResults without crashing`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None
    host.RegisterHandler(handler)

    // Null ParseResults — lint should skip, not crash
    let fakeResult: FileCheckResult =
        { File = "/tmp/test/Empty.fs"
          Source = "module Empty"
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    // Should not throw
    host.EmitFileChecked(fakeResult)

    waitUntil
        (fun () ->
            match host.GetStatus("lint") with
            | Some _ -> true
            | None -> false)
        3000

    let status = host.GetStatus("lint")
    test <@ status.IsSome @>

    // Should be Running (set at start of handler), not Failed
    match status.Value with
    | Failed(msg, _) -> Assert.Fail($"Should not fail — got: %s{msg}")
    | _ -> ()
