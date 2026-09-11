module FsHotWatch.Tests.A481OutcomeContractTests

open System
open System.IO
open System.Text.Json
open Xunit
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.IpcOutput
open FsHotWatch.Tests.TestHelpers

let private statuses outcome replayField =
    sprintf
        """{"coverage-probe":{"status":{"tag":"completed","at":"2026-09-09T12:00:00Z"},"lastRun":{"startedAt":"2026-09-09T11:59:59Z","elapsedMs":1000,"outcome":%s,"summary":"deliberately misleading success (cached)","activityTail":[]%s}}}"""
        outcome
        replayField
    |> parseStatuses

let private publish root mode scope parsed =
    publishVerdict
        (modelEvidence [ BaselineFixtures.runId ])
        root
        []
        mode
        true
        (BaselineFixtures.reportOf scope)
        Verdict.NoReading
        parsed
        []
        (SettledTree.capture root [])
        (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline)

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``measured confirmation retains machine readable provenance independent of summary`` replayed =
    withTempDir "a481-measured-contract" (fun root ->
        let field =
            if replayed then
                ",\"replayed\":true"
            else
                ",\"replayed\":false"

        let code =
            publish root CheckVerdict.Confirmation (FullSuite 1) (statuses """{"tag":"completed"}""" field)

        Assert.Equal(0, code)
        use document = JsonDocument.Parse(File.ReadAllText(Verdict.path root))

        let plugin =
            Assert.Single(document.RootElement.GetProperty("plugins").EnumerateArray())

        Assert.Equal("ok", plugin.GetProperty("outcome").GetString())
        Assert.Equal(replayed, plugin.GetProperty("replayed").GetBoolean()))

[<Theory(Timeout = 15000)>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``decline is explicit and only filtered verification can remain green`` full confirm =
    withTempDir "a481-declined-contract" (fun root ->
        let parsed =
            statuses """{"tag":"notEvaluated","reason":"reports were not regenerated"}""" ",\"replayed\":false"

        let scope = if full then FullSuite 1 else ImpactFiltered(1, 2)

        let mode =
            if confirm then
                CheckVerdict.Confirmation
            else
                CheckVerdict.InnerLoop

        let code = publish root mode scope parsed
        Assert.Equal((if full || confirm then 2 else 0), code)
        use document = JsonDocument.Parse(File.ReadAllText(Verdict.path root))

        let plugin =
            Assert.Single(document.RootElement.GetProperty("plugins").EnumerateArray())

        Assert.Equal("not-evaluated", plugin.GetProperty("outcome").GetString())
        Assert.Equal(code, document.RootElement.GetProperty("exitCode").GetInt32()))

[<Theory(Timeout = 15000)>]
[<InlineData("")>]
[<InlineData(",\"replayed\":null")>]
[<InlineData(",\"replayed\":\"false\"")>]
[<InlineData(",\"replayed\":0")>]
[<InlineData(",\"replayed\":{}")>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``missing or malformed provenance remains unknown despite cached summary`` field =
    withTempDir "a481-unknown-contract" (fun root ->
        publish root CheckVerdict.InnerLoop (FullSuite 1) (statuses """{"tag":"completed"}""" field)
        |> ignore

        use document = JsonDocument.Parse(File.ReadAllText(Verdict.path root))

        let plugin =
            Assert.Single(document.RootElement.GetProperty("plugins").EnumerateArray())

        Assert.Equal(JsonValueKind.Null, plugin.GetProperty("replayed").ValueKind))

[<Theory>]
[<InlineData("0")>]
[<InlineData("-1")>]
[<InlineData("256")>]
[<InlineData("false")>]
[<InlineData("\"3\"")>]
[<InlineData("1.5")>]
[<InlineData("null")>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``invalid configured decline code fails at config parsing`` token =
    let json =
        sprintf
            """{"fileCommands":[{"name":"decline","pattern":"*.txt","command":"echo","notEvaluatedExitCode":%s}]}"""
            token

    Assert.Throws<DaemonConfig.ConfigError>(fun () -> DaemonConfig.parseConfig json (defaultTestConfig ()) |> ignore)
    |> ignore

[<Fact(Timeout = 15000)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``actual cache replay preserves verified nothing evidence`` () =
    withTempDir "a481-empty-replay" (fun root ->
        let name = "empty-probe"
        let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
        let key = ContentHash.create "unchanged-inputs"

        let original =
            RunVerdict.verifiedNothing "no test was selected" (TimeSpan.FromMilliseconds 17.0)

        cache.Set
            { Plugin = name; File = None }
            key
            { CacheKey = key
              Errors = []
              Status = TaskCache.CachedRunCompleted original
              EmittedEvents = [] }

        let host = PluginHost.PluginHost(Unchecked.defaultof<_>, root, taskCache = cache)

        let handler: PluginHandler<unit, unit> =
            { Name = PluginName.create name
              Init = ()
              Update = fun _ _ _ -> async { return failwith "cache positive control: update must not execute" }
              Commands = []
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = Some(fun _ _ -> Some key)
              PrepareCommit = None
              Teardown = None }

        host.RegisterHandler handler
        let terminal = beginAwaitNextTerminal host name
        host.EmitFileChanged(SourceChanged [ "input.txt" ])
        Assert.True(terminal.Wait(5000), "cache replay must reach a terminal")
        Assert.True(waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 5000, "cache replay must settle")

        match host.GetStatus(name) with
        | Some(Completed(_, verdict)) ->
            Assert.Equal(original.NothingVerified, verdict.NothingVerified)
            Assert.Equal(original.Elapsed, verdict.Elapsed)
        | other -> failwithf "expected replayed completion, got %A" other)

let private rpcStatuses host =
    let config: Ipc.DaemonRpcConfig =
        { Host = host
          RequestShutdown = ignore
          RequestScan = ignore
          GetScanStatus = fun () -> "idle"
          GetScanGeneration = fun () -> 0L
          TriggerBuild = fun () -> async.Return()
          FormatAll = fun () -> async.Return "unused"
          WaitForScanGeneration = fun _ -> System.Threading.Tasks.Task.FromResult(())
          WaitForAllTerminal = fun _ -> System.Threading.Tasks.Task.FromResult(())
          RerunPlugin = fun _ -> async.Return(Result.Ok())
          InvalidateCache = fun () -> System.Threading.Tasks.Task.FromResult(())
          GetUncheckedCount = fun () -> 0 }

    Ipc.DaemonRpcTarget(config).GetStatus() |> parseStatuses

[<Theory(Timeout = 30000)>]
[<InlineData(3, true, false)>]
[<InlineData(3, false, true)>]
[<InlineData(0, true, true)>]
[<InlineData(7, true, true)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``real commands distinguish decline from success and failure across cache and input changes``
    code
    configured
    cacheable
    =
    withTempDir "a481-command-evidence" (fun root ->
        let input = Path.Combine(root, "input.txt")
        let counter = Path.Combine(root, "counter")
        File.WriteAllText(input, "first measurement")
        File.WriteAllText(Path.Combine(root, "probe.sh"), sprintf "cat \"$1\"\nprintf x >> counter\nexit %d\n" code)

        let host =
            PluginHost.PluginHost(
                Unchecked.defaultof<_>,
                root,
                taskCache = (TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache)
            )

        let name = "command-probe"

        let trigger: FileCommand.FileCommandPlugin.CommandTrigger =
            { FilePattern = Some(fun _ -> true)
              AfterTests = None }

        let handler =
            FileCommand.FileCommandPlugin.create
                (PluginName.create name)
                trigger
                "sh"
                "probe.sh input.txt"
                root
                (Some 5)
                (if configured then Some 3 else None)

        host.RegisterHandler handler

        let run expectedExecutions expectedProvenance =
            let terminal = beginAwaitNextTerminal host name
            host.EmitFileChanged(SourceChanged [ "input.txt" ])
            Assert.True(terminal.Wait(10000), "the owned command must reach its terminal")

            Assert.True(
                waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 5000,
                "the command and cache publication must settle"
            )

            Assert.Equal(String.replicate expectedExecutions "x", File.ReadAllText counter)
            let direct = RunOnceOutput.snapshotHost host (host.GetAllStatuses())
            let throughIpc = rpcStatuses host

            for parsed in [ direct; throughIpc ] do
                let plugin = Verdict.pluginVerdicts true DateTime.UtcNow parsed |> Assert.Single
                Assert.Equal(expectedProvenance, plugin.Provenance)

                match code, configured with
                | 3, true -> Assert.Equal(Verdict.PluginOutcome.NotEvaluated(File.ReadAllText input), plugin.Outcome)
                | 0, _ -> Assert.Equal(Verdict.PluginOutcome.Ok, plugin.Outcome)
                | _ -> Assert.Equal(Verdict.PluginOutcome.Fail, plugin.Outcome)

            let commandStatus =
                host.RunCommand(name + "-status", [||]) |> Async.RunSynchronously

            use statusDocument = JsonDocument.Parse(commandStatus.Value)

            if code = 3 && configured then
                Assert.Equal("not-evaluated", statusDocument.RootElement.GetProperty("status").GetString())
                Assert.Equal(File.ReadAllText input, statusDocument.RootElement.GetProperty("reason").GetString())
            else
                Assert.Equal((code = 0), statusDocument.RootElement.GetProperty("passed").GetBoolean())

        run 1 RunProvenance.Observed
        let repeated = if cacheable then 1 else 2

        run
            repeated
            (if cacheable then
                 RunProvenance.Replayed
             else
                 RunProvenance.Observed)

        File.WriteAllText(input, "changed measurement")
        run (repeated + 1) RunProvenance.Observed)

[<Fact(Timeout = 15000)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``publication failure cannot turn required decline back into green`` () =
    withTempDir "a481-publication-refusal" (fun root ->
        // A regular file prevents the verdict directory from being created.
        File.WriteAllText(Path.Combine(root, ".fshw"), "not a directory")

        let parsed =
            statuses """{"tag":"notEvaluated","reason":"measurement unavailable"}""" ",\"replayed\":false"

        Assert.Equal(2, publish root CheckVerdict.Confirmation (FullSuite 1) parsed))

[<Theory>]
[<InlineData(1)>]
[<InlineData(3)>]
[<InlineData(255)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``valid decline codes are preserved in configuration`` code =
    let json =
        sprintf
            """{"fileCommands":[{"name":"decline","pattern":"*.txt","command":"echo","notEvaluatedExitCode":%d}]}"""
            code

    let config = DaemonConfig.parseConfig json (defaultTestConfig ())
    Assert.Equal(Some code, (Assert.Single config.FileCommands).NotEvaluatedExitCode)

[<Fact(Timeout = 15000)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``a new required decline replaces prior confirmation instead of preserving its green`` () =
    withTempDir "a481-prior-refusal" (fun root ->
        let runDir = Ctrf.runDir root BaselineFixtures.runId
        Directory.CreateDirectory runDir |> ignore

        let report =
            """{"reportFormat":"CTRF","specVersion":"0.0.0","reportId":"a481-policy-fixture","results":{"tool":{"name":"xUnit.net v3"},"summary":{"tests":1,"passed":1,"failed":0,"pending":0,"skipped":0,"other":0,"suites":1,"start":1,"stop":2},"tests":[{"name":"Fixture.measured","status":"passed"}]}}"""

        File.WriteAllText(Path.Combine(runDir, "Fixture" + Ctrf.ReportSuffix), report)

        Assert.Equal(
            0,
            publish
                root
                CheckVerdict.Confirmation
                (FullSuite 1)
                (statuses """{"tag":"completed"}""" ",\"replayed\":false")
        )

        match Verdict.priorConfirmation root [] with
        | Verdict.PriorConfirmation.StillApplies previous -> Assert.Single(previous.Suites) |> ignore
        | other -> failwithf "positive control must earn reusable full evidence: %A" other

        let parsed =
            statuses
                """{"tag":"notEvaluated","reason":"reports cannot describe the current run"}"""
                ",\"replayed\":false"

        let code =
            publishVerdict
                (modelEvidence [ BaselineFixtures.runId ])
                root
                []
                CheckVerdict.Confirmation
                true
                (BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified))
                Verdict.NoReading
                parsed
                []
                (SettledTree.capture root [])
                (CheckVerdict.CheckOutcome.UnearnedScope(NoTestsRun NoTestsReason.AlreadyVerified))

        Assert.Equal(2, code)
        Assert.Equal(Verdict.PriorConfirmation.MustEarn, Verdict.priorConfirmation root [])

        match Verdict.read root with
        | Verdict.Reading.Found current ->
            Assert.Equal(2, current.ExitCode)
            Assert.False(Verdict.isFullSuiteGreen current)
        | other -> failwithf "declined evidence should be readable: %A" other)

[<Fact(Timeout = 15000)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``verdict read refuses a full green paired with a declined gate`` () =
    withTempDir "a481-hostile-full-green" (fun root ->
        Assert.Equal(
            0,
            publish
                root
                CheckVerdict.Confirmation
                (FullSuite 1)
                (statuses """{"tag":"completed"}""" ",\"replayed\":false")
        )

        let json =
            System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Verdict.path root))

        json.["plugins"].[0].["outcome"] <- System.Text.Json.Nodes.JsonValue.Create("not-evaluated")
        json.["plugins"].[0].["reason"] <- System.Text.Json.Nodes.JsonValue.Create("measurement declined")
        File.WriteAllText(Verdict.path root, json.ToJsonString())

        match Verdict.read root with
        | Verdict.Reading.Unreadable reason -> Assert.Contains("declined", reason)
        | other -> failwithf "contradictory full green must be refused: %A" other)

[<Fact(Timeout = 15000)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``required decline returns refusal when an older green survives a failed publication`` () =
    withTempDir "a481-prior-write-failure" (fun root ->
        Assert.Equal(
            0,
            publish
                root
                CheckVerdict.Confirmation
                (FullSuite 1)
                (statuses """{"tag":"completed"}""" ",\"replayed\":false")
        )

        match Verdict.priorConfirmation root [] with
        | Verdict.PriorConfirmation.StillApplies _ -> ()
        | other -> failwithf "positive control must have an applicable prior green: %A" other

        let priorBytes = File.ReadAllBytes(Verdict.path root)
        let lockPath = Path.Combine(root, ".fshw", "verdict.write.lock")
        File.Delete lockPath
        Directory.CreateDirectory lockPath |> ignore

        let parsed =
            statuses """{"tag":"notEvaluated","reason":"measurement declined"}""" ",\"replayed\":false"

        Assert.Equal(2, publish root CheckVerdict.Confirmation (FullSuite 1) parsed)
        // Publication could not replace these bytes; the current invocation must
        // refuse instead of returning their older green as its own answer.
        Assert.Equal<byte>(priorBytes, File.ReadAllBytes(Verdict.path root)))

[<Fact>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``changing decline policy invalidates a command cache identity`` () =
    let trigger: FileCommand.FileCommandPlugin.CommandTrigger =
        { FilePattern = Some(fun _ -> true)
          AfterTests = None }

    let key policy =
        let handler =
            FileCommand.FileCommandPlugin.create (PluginName.create "policy") trigger "echo" "input" "/tmp" (Some 5) policy

        handler.CacheKey.Value handler.Init (FileChanged(SourceChanged [ "input.txt" ]))

    let ordinary = key None
    Assert.True(ordinary.IsSome)
    Assert.Equal(ordinary, key None)
    Assert.NotEqual(ordinary, key (Some 3))
    Assert.NotEqual(key (Some 3), key (Some 7))

[<Theory(Timeout = 15000)>]
[<InlineData("")>]
[<InlineData("   ")>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``a silent decline receives an explicit reason`` (output: string) =
    withTempDir "a481-silent-decline" (fun root ->
        File.WriteAllText(Path.Combine(root, "probe.sh"), "cat reason.txt\nexit 3\n")
        File.WriteAllText(Path.Combine(root, "reason.txt"), output)
        let host = PluginHost.PluginHost(Unchecked.defaultof<_>, root)

        let trigger: FileCommand.FileCommandPlugin.CommandTrigger =
            { FilePattern = Some(fun _ -> true)
              AfterTests = None }

        host.RegisterHandler(
            FileCommand.FileCommandPlugin.create
                (PluginName.create "silent")
                trigger
                "sh"
                "probe.sh"
                root
                (Some 5)
                (Some 3)
        )

        let terminal = beginAwaitNextTerminal host "silent"
        host.EmitFileChanged(SourceChanged [ "input.txt" ])
        Assert.True(terminal.Wait(10000))
        Assert.True(waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 5000)

        let plugin =
            rpcStatuses host |> Verdict.pluginVerdicts true DateTime.UtcNow |> Assert.Single

        match plugin.Outcome with
        | Verdict.PluginOutcome.NotEvaluated reason -> Assert.Contains("evaluation declined", reason)
        | other -> failwithf "blank output must still retain the decline: %A" other)

[<Theory(Timeout = 15000)>]
[<InlineData("missing")>]
[<InlineData("null")>]
[<InlineData("\"false\"")>]
[<InlineData("0")>]
[<InlineData("{}")>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``verdict read never infers unknown provenance from a passing outcome`` token =
    withTempDir "a481-read-provenance" (fun root ->
        Assert.Equal(
            0,
            publish
                root
                CheckVerdict.Confirmation
                (FullSuite 1)
                (statuses """{"tag":"completed"}""" ",\"replayed\":false")
        )

        let json =
            System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Verdict.path root))

        let plugin = (json["plugins"][0]).AsObject()

        if token = "missing" then
            plugin.Remove("replayed") |> ignore
        else
            plugin["replayed"] <- System.Text.Json.Nodes.JsonNode.Parse(token)

        File.WriteAllText(Verdict.path root, json.ToJsonString())

        match Verdict.read root with
        | Verdict.Reading.Found verdict ->
            Assert.Equal(RunProvenance.Unknown, (Assert.Single verdict.Plugins).Provenance)
        | other -> failwithf "unknown provenance should remain explicit and readable: %A" other)
