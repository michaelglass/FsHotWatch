module FsHotWatch.Tests.OutcomeContractTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.IpcOutput
open FsHotWatch.Tests.TestHelpers

// These exercise the real IPC parser and verdict publisher/readback. The supplied
// suite report is a fixture for policy, not evidence that this test ran a suite.
let private statuses outcome replayField summary =
    let json =
        sprintf
            """{"coverage-probe":{"status":{"tag":"completed","at":"2026-09-09T12:00:00Z"},"lastRun":{"startedAt":"2026-09-09T11:59:59Z","elapsedMs":1000,"outcome":%s,"summary":%s,"activityTail":[]%s}}}"""
            outcome
            (JsonSerializer.Serialize<string> summary)
            replayField

    parseStatuses json

let private publish root mode noWarnFail parsed =
    let code =
        publishVerdict
            root
            []
            mode
            FsHotWatch.Cli.CheckVerdict.VerificationCompleteness.NotRecorded
            noWarnFail
            (BaselineFixtures.reportOf (FullSuite 1))
            Verdict.NoReading
            parsed
            []
            (SettledTree.capture root [])
            (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline)

    match Verdict.read root with
    | Verdict.Reading.Found verdict -> code, verdict
    | other -> failwithf "expected readable published verdict, got %A" other

let private provenance token =
    match token with
    | "observed" -> RunProvenance.Observed
    | "replayed" -> RunProvenance.Replayed
    | "unknown" -> RunProvenance.Unknown
    | other -> failwithf "unknown test expectation %s" other

[<Theory(Timeout = 15000)>]
[<InlineData("", "unknown")>]
[<InlineData(",\"replayed\":null", "unknown")>]
[<InlineData(",\"replayed\":\"false\"", "unknown")>]
[<InlineData(",\"replayed\":0", "unknown")>]
[<InlineData(",\"replayed\":{}", "unknown")>]
[<InlineData(",\"replayed\":false", "observed")>]
[<InlineData(",\"replayed\":true", "replayed")>]
let ``IPC provenance survives published verdict without trusting summary prose`` replayField expected =
    withTempDir "provenance-contract" (fun root ->
        // Deliberately misleading: false must override this marker, while absent
        // metadata must remain unknown even when the prose looks informative.
        let parsed = statuses """{"tag":"completed"}""" replayField "command passed (cached)"
        let wanted = provenance expected
        test <@ parsed["coverage-probe"].LastRun.Value.Provenance = wanted @>

        let _, verdict = publish root CheckVerdict.InnerLoop false parsed
        let plugin = Assert.Single verdict.Plugins
        test <@ plugin.Provenance = wanted @>

        use document = JsonDocument.Parse(File.ReadAllText(Verdict.path root))
        let written = Assert.Single(document.RootElement.GetProperty("plugins").EnumerateArray())
        let field = written.GetProperty("replayed")

        match wanted with
        | RunProvenance.Unknown -> Assert.Equal(JsonValueKind.Null, field.ValueKind)
        | RunProvenance.Observed -> Assert.False(field.GetBoolean())
        | RunProvenance.Replayed -> Assert.True(field.GetBoolean()))

[<Theory(Timeout = 15000)>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
let ``declined evaluation cannot publish green even with full tests and warnings disabled`` confirm noWarnFail =
    withTempDir "decline-contract" (fun root ->
        let reason = "coverage inputs were not regenerated for this tree"
        let outcome = JsonSerializer.Serialize {| tag = "notEvaluated"; reason = reason |}
        let parsed = statuses outcome ",\"replayed\":false" reason
        let mode = if confirm then CheckVerdict.Confirmation else CheckVerdict.InnerLoop
        let code, verdict = publish root mode noWarnFail parsed
        let plugin = Assert.Single verdict.Plugins

        test <@ code <> 0 @>
        test <@ verdict.ExitCode = code @>
        test <@ plugin.Outcome = Verdict.PluginOutcome.NotEvaluated @>
        test <@ plugin.Provenance = RunProvenance.Observed @>
        test <@ not (Verdict.isFullSuiteGreen verdict) @>
        test <@ Verdict.priorConfirmation root [] = Verdict.PriorConfirmation.MustEarn @>

        match verdict.Outcome with
        | Verdict.Incomplete detail -> test <@ detail.Contains "coverage-probe" @>
        | other -> failwithf "decline is unmeasured, not a failed check or a pass: %A" other)

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``a measured plugin still permits a full confirmation with explicit cache provenance`` replayed =
    withTempDir "measured-control" (fun root ->
        let field = if replayed then ",\"replayed\":true" else ",\"replayed\":false"
        let parsed = statuses """{"tag":"completed"}""" field "command checked its inputs"
        let code, verdict = publish root CheckVerdict.Confirmation false parsed
        let plugin = Assert.Single verdict.Plugins
        test <@ code = 0 @>
        test <@ Verdict.isFullSuiteGreen verdict @>
        test <@ plugin.Outcome = Verdict.PluginOutcome.Ok @>
        let expected = if replayed then RunProvenance.Replayed else RunProvenance.Observed
        test <@ plugin.Provenance = expected @>)

[<Fact>]
let ``marking replay preserves the verified-nothing distinction`` () =
    let original = RunVerdict.verifiedNothing "no test was selected" (TimeSpan.FromMilliseconds 17.0)
    let replay = RunVerdict.asReplayed original
    test <@ replay.Provenance = RunProvenance.Replayed @>
    test <@ replay.NothingVerified = original.NothingVerified @>
    test <@ replay.Elapsed = original.Elapsed @>

[<Theory(Timeout = 60000)>]
[<InlineData(3, 2, false)>]
[<InlineData(0, 1, true)>]
[<InlineData(7, 1, true)>]
let ``a declined command runs again while measured success and failure retain useful caching`` exitCode executions replayed =
    withTempDir "decline-cache" (fun root ->
        let input = Path.Combine(root, "input.txt")
        let marker = Path.Combine(root, "execution.txt")
        File.WriteAllText(input, "unchanged")
        File.WriteAllText(Path.Combine(root, "probe.sh"), sprintf "printf x >> execution.txt\nexit %d\n" exitCode)
        let cache = FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache
        let host = FsHotWatch.PluginHost.PluginHost(Unchecked.defaultof<_>, root, taskCache = cache)
        let name = "cache-policy-probe"

        let trigger: FsHotWatch.FileCommand.FileCommandPlugin.CommandTrigger =
            { FilePattern = Some(fun _ -> true)
              AfterTests = None }

        let handler =
            FsHotWatch.FileCommand.FileCommandPlugin.create
                (FsHotWatch.PluginFramework.PluginName.create name)
                trigger
                "sh"
                "probe.sh"
                root
                (Some 5)
                (Some 3)

        host.RegisterHandler handler

        for _ in 1..2 do
            let terminal = beginAwaitNextTerminal host name
            host.EmitFileChanged(SourceChanged [ "input.txt" ])
            Assert.True(terminal.Wait(15000), "each dispatch must produce its own terminal")
            Assert.True(waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 5000, "cache handling must settle")

        Assert.Equal(String.replicate executions "x", File.ReadAllText marker)
        let parsed = FsHotWatch.Cli.RunOnceOutput.snapshotHost host (host.GetAllStatuses())
        let plugins = Verdict.pluginVerdicts true DateTime.UtcNow parsed
        let plugin = Assert.Single plugins
        let expected = if replayed then RunProvenance.Replayed else RunProvenance.Observed
        test <@ plugin.Provenance = expected @>
        let expectedOutcome =
            match exitCode with
            | 0 -> Verdict.PluginOutcome.Ok
            | 3 -> Verdict.PluginOutcome.NotEvaluated
            | _ -> Verdict.PluginOutcome.Fail

        test <@ plugin.Outcome = expectedOutcome @>)

[<Theory>]
[<InlineData("0")>]
[<InlineData("-1")>]
[<InlineData("256")>]
[<InlineData("false")>]
[<InlineData("\"3\"")>]
let ``invalid configured decline code fails at config parsing`` token =
    let json =
        sprintf
            """{"fileCommands":[{"name":"decline","pattern":"*.txt","command":"echo","notEvaluatedExitCode":%s}]}"""
            token

    Assert.Throws<DaemonConfig.ConfigError>(fun () -> DaemonConfig.parseConfig json (defaultTestConfig ()) |> ignore)
    |> ignore
