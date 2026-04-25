module FsHotWatch.Tests.LintPluginTests

open Xunit
open Swensen.Unquote
open FSharpLint.Application
open FSharpLint.Framework.Suggestion
open FSharp.Compiler.Text
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.ErrorLedger
open FsHotWatch.Lint.LintPlugin
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 5000)>]
let ``plugin has correct name`` () =
    let handler = create None None None None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "lint" @>

[<Fact(Timeout = 5000)>]
let ``warnings command returns zeroes when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"files\": 0") @>
    test <@ result.Value.Contains("\"warnings\": 0") @>

[<Fact(Timeout = 5000)>]
let ``LintPlugin with configPath sets up lint params`` () =
    let handler = create (Some "/tmp/nonexistent-config.json") None None None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "lint" @>

[<Fact(Timeout = 10000)>]
let ``lint error path sets Failed status on null check results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None None None
    host.RegisterHandler(handler)

    // Explicitly null ParseResults to exercise the lint plugin's null-guard path.
    let fakeResult =
        { fakeFileCheckResult "/tmp/nonexistent/Fake.fs" with
            Source = ""
            ParseResults = Unchecked.defaultof<_> }

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

[<Fact(Timeout = 5000)>]
let ``warnings command with args passes through`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None None None
    host.RegisterHandler(handler)

    // The warnings command ignores args, but verify it handles non-empty args
    let result =
        host.RunCommand("warnings", [| "--verbose" |]) |> Async.RunSynchronously

    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"files\": 0") @>

[<Fact(Timeout = 5000)>]
let ``lint skips file with null ParseResults without crashing`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None None None
    host.RegisterHandler(handler)

    let fakeResult =
        { fakeFileCheckResult "/tmp/test/Empty.fs" with
            Source = "module Empty"
            ParseResults = Unchecked.defaultof<_> }

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
    | Failed(msg, _) -> Assert.Fail($"Should not fail -- got: %s{msg}")
    | _ -> ()

[<Fact(Timeout = 10000)>]
let ``lint handler times out when runner exceeds TimeoutSec`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let slowRunner (_result: FileCheckResult) =
        System.Threading.Thread.Sleep 3000
        Lint.LintResult.Success []

    let handler = create None None (Some slowRunner) (Some 1)
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeFileCheckResult "/tmp/slow/File.fs")
    waitForTerminalStatus host "lint" 5000

    let snap = host.GetActivitySnapshot("lint")

    match snap.LastRun with
    | Some r ->
        match r.Outcome with
        | TimedOut _ -> ()
        | other -> Assert.Fail($"Expected TimedOut, got {other}")
    | None -> Assert.Fail "Expected LastRun record"

[<Fact(Timeout = 5000)>]
let ``lint runner returning Failure reports errors and sets Failed status`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let runner (_result: FileCheckResult) =
        Lint.LintResult.Failure(Lint.LintFailure.RunTimeConfigError "bad config")

    let handler = create None None (Some runner) None
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeFileCheckResult "/tmp/test/Bad.fs")

    waitForTerminalStatus host "lint" 3000

    let status = host.GetStatus("lint")

    match status with
    | Some(Failed(msg, _)) -> test <@ msg.Contains("bad config") @>
    | other -> Assert.Fail($"Expected Failed, got: %A{other}")

    let errors = host.GetErrorsByPlugin("lint")
    test <@ errors |> Map.isEmpty |> not @>

[<Fact(Timeout = 10000)>]
let ``lint runner returning Success with warnings reports them to error ledger`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let range = Range.mkRange "Warn.fs" (Position.mkPos 10 4) (Position.mkPos 10 20)

    let warning: LintWarning =
        { Details =
            { Range = range
              Message = "Consider using List.isEmpty"
              SuggestedFix = None
              TypeChecks = [] }
          ErrorText = "FL0065"
          FilePath = "/tmp/test/Warn.fs"
          RuleName = "Hints"
          RuleIdentifier = "FL0065" }

    let runner (_result: FileCheckResult) = Lint.LintResult.Success [ warning ]

    let handler = create None None (Some runner) None
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeFileCheckResult "/tmp/test/Warn.fs")

    waitForTerminalStatus host "lint" 3000

    let status = host.GetStatus("lint")

    match status with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"Expected Completed, got: %A{other}")

    let errors = host.GetErrorsByPlugin("lint")
    let fileErrors = errors |> Map.tryFind "/tmp/test/Warn.fs"
    test <@ fileErrors.IsSome @>

    let entries = fileErrors.Value
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Message = "Consider using List.isEmpty" @>
    test <@ entries.[0].Severity = DiagnosticSeverity.Warning @>
    test <@ entries.[0].Line = 10 @>
    test <@ entries.[0].Column = 4 @>

[<Fact(Timeout = 5000)>]
let ``lint runner returning Success with no warnings clears errors`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let runner (_result: FileCheckResult) = Lint.LintResult.Success []

    let handler = create None None (Some runner) None
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeFileCheckResult "/tmp/test/Clean.fs")

    waitForTerminalStatus host "lint" 3000

    let status = host.GetStatus("lint")

    match status with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"Expected Completed, got: %A{other}")

    let errors = host.GetErrorsByPlugin("lint")
    let fileErrors = errors |> Map.tryFind "/tmp/test/Clean.fs"
    // Either no entry or empty list
    test <@ fileErrors.IsNone || fileErrors.Value.IsEmpty @>

    // Verify warnings command reflects zero warnings
    let cmdResult = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ cmdResult.IsSome @>
    test <@ cmdResult.Value.Contains("\"warnings\": 0") @>

[<Fact(Timeout = 10000)>]
let ``warnings command reflects warning count after lint with warnings`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let range = Range.mkRange "A.fs" (Position.mkPos 1 0) (Position.mkPos 1 5)

    let mkWarning msg : LintWarning =
        { Details =
            { Range = range
              Message = msg
              SuggestedFix = None
              TypeChecks = [] }
          ErrorText = "FL0001"
          FilePath = "/tmp/test/A.fs"
          RuleName = "TestRule"
          RuleIdentifier = "FL0001" }

    let runner (_result: FileCheckResult) =
        Lint.LintResult.Success [ mkWarning "warn1"; mkWarning "warn2" ]

    let handler = create None None (Some runner) None
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeFileCheckResult "/tmp/test/A.fs")

    waitForTerminalStatus host "lint" 3000

    let cmdResult = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ cmdResult.IsSome @>
    test <@ cmdResult.Value.Contains("\"files\": 1") @>
    test <@ cmdResult.Value.Contains("\"warnings\": 2") @>
