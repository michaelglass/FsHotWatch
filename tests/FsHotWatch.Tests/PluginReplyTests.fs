/// Each plugin command's typed reply: written as the JSON the command always sent, read back
/// into the same reply, and rendered by the CLI exactly as its shape was rendered before the
/// reply carried a schema.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.PluginReplyTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcOutput
open FsHotWatch.Build.BuildReplies

/// What the CLI does when it renders `reply`: the exit code and everything written (stdout,
/// stderr and the verdict terminal), or the type and message of the exception it raised. A
/// rendering that throws is compared too, so a typed renderer cannot quietly differ from the
/// shape-read one even where that one fails.
let private rendered (noWarnFail: bool) (reply: string) : Result<int * string, string * string> =
    let priorOut, priorErr = Console.Out, Console.Error
    use writer = new StringWriter()
    Console.SetOut writer
    Console.SetError writer

    try
        try
            use _ = VerdictTerminal.install writer

            let exitCode =
                renderIpcResult ProgressRenderer.Verbose (fun _ -> [ "statuses" ]) noWarnFail reply

            writer.Flush()
            Ok(exitCode, writer.ToString())
        with ex ->
            Error(ex.GetType().FullName, ex.Message)
    finally
        Console.SetOut priorOut
        Console.SetError priorErr

/// The CLI renders `payload` under its declared schema exactly as it renders the same bytes
/// read by their shape.
let private rendersAsSniffed (command: string) (schema: string) (payload: string) =
    let typed =
        FsHotWatch.Ipc.declaredOutputReply command (FsHotWatch.Plugin.ReplySchema.Typed schema) payload

    let sniffed =
        FsHotWatch.Ipc.declaredOutputReply command (FsHotWatch.Plugin.ReplySchema.Raw "read by its shape") payload

    for noWarnFail in [ false; true ] do
        test <@ rendered noWarnFail typed = rendered noWarnFail sniffed @>

/// The exit code and text of rendering `payload` under its declared schema; a throw fails the test.
let private renderedTyped (command: string) (schema: string) (payload: string) : int * string =
    match
        rendered false (FsHotWatch.Ipc.declaredOutputReply command (FsHotWatch.Plugin.ReplySchema.Typed schema) payload)
    with
    | Ok rendering -> rendering
    | Error(kind, message) -> failwith $"rendering %s{payload} threw %s{kind}: %s{message}"

/// A payload its declared schema does not describe is no verdict, naming the schema.
let private refusesUndescribed (command: string) (schema: string) (payload: string) =
    match
        rendered false (FsHotWatch.Ipc.declaredOutputReply command (FsHotWatch.Plugin.ReplySchema.Typed schema) payload)
    with
    | Ok(exitCode, text) ->
        test <@ exitCode = NoVerdictExitCode @>
        test <@ text.Contains schema @>
    | Error(kind, message) -> failwith $"rendering an undescribed payload threw %s{kind}: %s{message}"

let private buildStatuses =
    [ BuildStatus.Passed "Build succeeded."
      BuildStatus.Failed "error FS0001: boom"
      BuildStatus.NotRun ]

[<Fact(Timeout = 15000)>]
let ``build-status is written as the JSON it always was, and read back as itself`` () =
    test <@ BuildStatus.serialize (BuildStatus.Passed "ok") = """{"output":"ok","status":"passed"}""" @>
    test <@ BuildStatus.serialize (BuildStatus.Failed "no") = """{"output":"no","status":"failed"}""" @>
    test <@ BuildStatus.serialize BuildStatus.NotRun = """{"status":"not run"}""" @>

    for status in buildStatuses do
        test <@ BuildStatus.tryParse (BuildStatus.serialize status) = Some status @>

[<Fact(Timeout = 15000)>]
let ``force-rebuild is written as the JSON it always was, and read back as itself`` () =
    test <@ ForceRebuild.serialize ForceRebuild.Forced = """{"forced":true,"status":"ok"}""" @>
    test <@ ForceRebuild.tryParse (ForceRebuild.serialize ForceRebuild.Forced) = Some ForceRebuild.Forced @>

[<Fact(Timeout = 15000)>]
let ``a typed build reply renders exactly as its shape did`` () =
    for status in buildStatuses do
        rendersAsSniffed "build-status" BuildStatusSchema (BuildStatus.serialize status)

    rendersAsSniffed "force-rebuild" ForceRebuildSchema (ForceRebuild.serialize ForceRebuild.Forced)

[<Theory(Timeout = 15000)>]
[<InlineData("""{"status":"passed"}""")>]
[<InlineData("""{"status":"exploded","output":"x"}""")>]
[<InlineData("""{"count":3}""")>]
[<InlineData("not json")>]
let ``a build-status payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed "build-status" BuildStatusSchema payload

[<Theory(Timeout = 15000)>]
[<InlineData("""{"status":"ok"}""")>]
[<InlineData("""{"status":"ok","forced":false}""")>]
let ``a force-rebuild payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed "force-rebuild" ForceRebuildSchema payload

[<Fact(Timeout = 15000)>]
let ``warnings is written as the JSON it always was, and read back as itself`` () =
    let warnings: FsHotWatch.Lint.LintReplies.Warnings = { Files = 2; Warnings = 7 }
    test <@ FsHotWatch.Lint.LintReplies.Warnings.serialize warnings = """{"files": 2, "warnings": 7}""" @>

    test
        <@
            FsHotWatch.Lint.LintReplies.Warnings.tryParse (FsHotWatch.Lint.LintReplies.Warnings.serialize warnings) = Some
                warnings
        @>

[<Fact(Timeout = 15000)>]
let ``a typed warnings reply renders exactly as its shape did`` () =
    for files, warnings in [ 0, 0; 3, 11 ] do
        rendersAsSniffed
            "warnings"
            FsHotWatch.Lint.LintReplies.WarningsSchema
            (FsHotWatch.Lint.LintReplies.Warnings.serialize { Files = files; Warnings = warnings })

[<Theory(Timeout = 15000)>]
[<InlineData("""{"files": 2}""")>]
[<InlineData("""{"files": "2", "warnings": 1}""")>]
let ``a warnings payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed "warnings" FsHotWatch.Lint.LintReplies.WarningsSchema payload

[<Fact(Timeout = 15000)>]
let ``unformatted is written as the JSON it always was, and read back as itself`` () =
    let unformatted: FsHotWatch.Fantomas.FormatReplies.Unformatted =
        { Count = 2
          Files = "src/A.fs, src/B.fs" }

    test
        <@
            FsHotWatch.Fantomas.FormatReplies.Unformatted.serialize unformatted = """{"count": 2, "files": "src/A.fs, src/B.fs"}"""
        @>

    test
        <@
            FsHotWatch.Fantomas.FormatReplies.Unformatted.tryParse (
                FsHotWatch.Fantomas.FormatReplies.Unformatted.serialize unformatted
            ) = Some unformatted
        @>

/// A path holding a quote or a backslash used to be written unescaped, which is not JSON. It is
/// escaped now, and reads back as the same path.
[<Fact(Timeout = 15000)>]
let ``an unformatted file whose path needs escaping reads back as itself`` () =
    let unformatted: FsHotWatch.Fantomas.FormatReplies.Unformatted =
        { Count = 1
          Files = """C:\src\say "hi".fs""" }

    test
        <@
            FsHotWatch.Fantomas.FormatReplies.Unformatted.tryParse (
                FsHotWatch.Fantomas.FormatReplies.Unformatted.serialize unformatted
            ) = Some unformatted
        @>

[<Fact(Timeout = 15000)>]
let ``an unformatted reply says which files are unformatted, and fails when any are`` () =
    // AUTOMATION-1466: this used to throw, because the reply was read as a diagnostics
    // response whose `files` is an object.
    let clean =
        renderedTyped
            "unformatted"
            FsHotWatch.Fantomas.FormatReplies.UnformattedSchema
            (FsHotWatch.Fantomas.FormatReplies.Unformatted.serialize { Count = 0; Files = "" })

    test <@ fst clean = 0 @>
    test <@ (snd clean).Contains "no unformatted files" @>

    let dirty =
        renderedTyped
            "unformatted"
            FsHotWatch.Fantomas.FormatReplies.UnformattedSchema
            (FsHotWatch.Fantomas.FormatReplies.Unformatted.serialize
                { Count = 2
                  Files = "src/A.fs, src/B.fs" })

    test <@ fst dirty = 1 @>
    test <@ (snd dirty).Contains "2 unformatted file(s): src/A.fs, src/B.fs" @>

[<Theory(Timeout = 15000)>]
[<InlineData("""{"count": 2}""")>]
[<InlineData("""{"files": "a.fs"}""")>]
let ``an unformatted payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed "unformatted" FsHotWatch.Fantomas.FormatReplies.UnformattedSchema payload

let private commandStatuses =
    [ FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.Passed
      FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.Failed
      FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.NotRun ]

[<Fact(Timeout = 15000)>]
let ``a file command's status is written as the JSON it always was, and read back as itself`` () =
    let serialize = FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.serialize

    test <@ serialize FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.Passed = """{"passed":true}""" @>
    test <@ serialize FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.Failed = """{"passed":false}""" @>
    test <@ serialize FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.NotRun = """{"status":"not run"}""" @>

    for status in commandStatuses do
        test <@ FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.tryParse (serialize status) = Some status @>

[<Fact(Timeout = 15000)>]
let ``a typed file command status renders exactly as its shape did`` () =
    for status in commandStatuses do
        rendersAsSniffed
            "lint-docs-status"
            FsHotWatch.FileCommand.FileCommandReplies.StatusSchema
            (FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.serialize status)

[<Theory(Timeout = 15000)>]
[<InlineData("""{"passed":"yes"}""")>]
[<InlineData("""{"status":"passed"}""")>]
let ``a file command status payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed "lint-docs-status" FsHotWatch.FileCommand.FileCommandReplies.StatusSchema payload

let private coverageStatuses =
    [ FsHotWatch.Coverage.CoverageReplies.CoverageStatus.NotChecked
      FsHotWatch.Coverage.CoverageReplies.CoverageStatus.Passed
      FsHotWatch.Coverage.CoverageReplies.CoverageStatus.Failed ]

let private ratchetReplies =
    [ FsHotWatch.Coverage.CoverageReplies.RatchetReply.NoReportFound
      FsHotWatch.Coverage.CoverageReplies.RatchetReply.NoNamedReportExists
      FsHotWatch.Coverage.CoverageReplies.RatchetReply.Updated "coverage-ratchet.json"
      FsHotWatch.Coverage.CoverageReplies.RatchetReply.Failed "the report is not Cobertura"
      FsHotWatch.Coverage.CoverageReplies.RatchetReply.TimedOut ]

[<Fact(Timeout = 15000)>]
let ``coverage-status is written as the text it always was, and read back as itself`` () =
    let serialize = FsHotWatch.Coverage.CoverageReplies.CoverageStatus.serialize

    test
        <@
            List.map serialize coverageStatuses = [ "coverage: no check run yet"
                                                    "coverage: OK"
                                                    "coverage: FAILED (run `fshw errors` for details)" ]
        @>

    for status in coverageStatuses do
        test <@ FsHotWatch.Coverage.CoverageReplies.CoverageStatus.tryParse (serialize status) = Some status @>

[<Fact(Timeout = 15000)>]
let ``coverage-ratchet is written as the text it always was, and read back as itself`` () =
    let serialize = FsHotWatch.Coverage.CoverageReplies.RatchetReply.serialize

    test
        <@
            List.map serialize ratchetReplies = [ "coverage-ratchet: no coverage.cobertura.xml found"
                                                  "coverage-ratchet: none of the named coverage reports exists"
                                                  "coverage-ratchet: thresholds updated in coverage-ratchet.json"
                                                  "coverage-ratchet failed: the report is not Cobertura"
                                                  "coverage-ratchet: did not complete within 5 minutes (a coverage check may be holding the slot); retry" ]
        @>

    for reply in ratchetReplies do
        test <@ FsHotWatch.Coverage.CoverageReplies.RatchetReply.tryParse (serialize reply) = Some reply @>

[<Fact(Timeout = 15000)>]
let ``a coverage reply exits by what it says, and prints its line`` () =
    // AUTOMATION-1465: these all exited 0, so a ratchet that failed or a coverage check that
    // failed read as a green. A failure is 1, an answer still being computed is 6 (in
    // flight), and no answer at all is 3 (no verdict).
    let expectations =
        [ "coverage-status",
          FsHotWatch.Coverage.CoverageReplies.StatusSchema,
          [ FsHotWatch.Coverage.CoverageReplies.CoverageStatus.serialize
                FsHotWatch.Coverage.CoverageReplies.CoverageStatus.Passed,
            0
            FsHotWatch.Coverage.CoverageReplies.CoverageStatus.serialize
                FsHotWatch.Coverage.CoverageReplies.CoverageStatus.Failed,
            1
            FsHotWatch.Coverage.CoverageReplies.CoverageStatus.serialize
                FsHotWatch.Coverage.CoverageReplies.CoverageStatus.NotChecked,
            NoVerdictExitCode ]
          "coverage-ratchet",
          FsHotWatch.Coverage.CoverageReplies.RatchetSchema,
          ratchetReplies
          |> List.map (fun reply ->
              FsHotWatch.Coverage.CoverageReplies.RatchetReply.serialize reply,
              match reply with
              | FsHotWatch.Coverage.CoverageReplies.RatchetReply.Updated _ -> 0
              | FsHotWatch.Coverage.CoverageReplies.RatchetReply.TimedOut -> RerunTimedOutExitCode
              | _ -> 1) ]

    for command, schema, cases in expectations do
        for payload, expected in cases do
            match
                rendered
                    false
                    (FsHotWatch.Ipc.declaredOutputReply command (FsHotWatch.Plugin.ReplySchema.Typed schema) payload)
            with
            | Ok(exitCode, text) ->
                test <@ exitCode = expected @>
                test <@ text.Contains payload @>
            | Error(kind, message) -> failwith $"rendering %s{payload} threw %s{kind}: %s{message}"

[<Theory(Timeout = 15000)>]
[<InlineData("coverage: maybe")>]
[<InlineData("""{"status":"passed"}""")>]
let ``a coverage-status payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed "coverage-status" FsHotWatch.Coverage.CoverageReplies.StatusSchema payload

[<Theory(Timeout = 15000)>]
[<InlineData("coverage-ratchet: done")>]
[<InlineData("ratchet failed: x")>]
let ``a coverage-ratchet payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed "coverage-ratchet" FsHotWatch.Coverage.CoverageReplies.RatchetSchema payload

[<Fact(Timeout = 15000)>]
let ``diagnostics is written as the JSON it always was, and read back as itself`` () =
    let diagnostics: FsHotWatch.Analyzers.AnalyzerReplies.Diagnostics =
        { Analyzers = 3
          Files = 2
          Diagnostics = 5 }

    test
        <@
            FsHotWatch.Analyzers.AnalyzerReplies.Diagnostics.serialize diagnostics = """{"analyzers":3,"diagnostics":5,"files":2}"""
        @>

    test
        <@
            FsHotWatch.Analyzers.AnalyzerReplies.Diagnostics.tryParse (
                FsHotWatch.Analyzers.AnalyzerReplies.Diagnostics.serialize diagnostics
            ) = Some diagnostics
        @>

let private findingsReply: FsHotWatch.Analyzers.AnalyzerFindings.Reply =
    { ModelGeneration = Some 4L
      Expected = [ "/repo/src/A.fs" ]
      AnalyzerSet = Ok "set-key"
      FailOnSeverity = FsHotWatch.ErrorLedger.DiagnosticSeverity.Warning
      ScopeRoot = Some "/repo"
      AnalyzerCount = 1
      Files = [] }

[<Fact(Timeout = 15000)>]
let ``a typed analyzers reply renders exactly as its shape did`` () =
    rendersAsSniffed
        "diagnostics"
        FsHotWatch.Analyzers.AnalyzerReplies.DiagnosticsSchema
        (FsHotWatch.Analyzers.AnalyzerReplies.Diagnostics.serialize
            { Analyzers = 1
              Files = 0
              Diagnostics = 0 })

    rendersAsSniffed
        FsHotWatch.Analyzers.AnalyzerFindings.CommandName
        FsHotWatch.Analyzers.AnalyzerFindings.ReplySchema
        (FsHotWatch.Analyzers.AnalyzerFindings.serializeReply findingsReply)

[<Theory(Timeout = 15000)>]
[<InlineData("""{"analyzers":1,"files":0}""")>]
[<InlineData("""{"count":1}""")>]
let ``a diagnostics payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed "diagnostics" FsHotWatch.Analyzers.AnalyzerReplies.DiagnosticsSchema payload

[<Theory(Timeout = 15000)>]
[<InlineData("""{"schema":"fshw-analyzer-findings-reply-v0"}""")>]
[<InlineData("""{"files":[]}""")>]
let ``an analyzer-findings payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed
        FsHotWatch.Analyzers.AnalyzerFindings.CommandName
        FsHotWatch.Analyzers.AnalyzerFindings.ReplySchema
        payload

module private TestPrune =
    type R = FsHotWatch.TestPrune.TestPruneReplies.RunReply
    type S = FsHotWatch.TestPrune.TestPruneReplies.ScopeReply

    let report (projects: string) (extra: string) =
        $"""{{"coverage":"ran","elapsed":"1.0s","filter":null,"noTestsMatched":false%s{extra},"projects":[%s{projects}]}}"""

    let passedProject =
        """{"project":"Tests","status":"passed","output":"ok","elapsedMs":10,"counts":{"total":3,"succeeded":3,"failed":0,"skipped":0,"other":0}}"""

    let failedProject =
        """{"project":"Tests","status":"failed","output":"boom","elapsedMs":10,"counts":null}"""

    let runReplies =
        [ R.Report(report passedProject "")
          R.Report(report failedProject "")
          R.Report(report "" "")
          R.Busy
              "the test run did not produce a result within 600s (still queued or running); retry, or raise --wait-sec"
          R.Refused "no matching test projects"
          R.Running
          R.Settling
          R.NotRun ]

    let scopeReplies =
        [ S.ScopeSet "full"
          S.ScopeRefused "unknown scope 'x' (expected 'full', 'impact' or 'owed')" ]

[<Fact(Timeout = 15000)>]
let ``a run reply is written as the JSON it always was, and read back as itself`` () =
    let serialize = FsHotWatch.TestPrune.TestPruneReplies.RunReply.serialize

    test <@ serialize TestPrune.R.Running = """{"status":"running"}""" @>
    test <@ serialize TestPrune.R.Settling = """{"status":"settling"}""" @>
    test <@ serialize TestPrune.R.NotRun = """{"status":"not run"}""" @>
    test <@ serialize (TestPrune.R.Refused "no") = """{"error":"no"}""" @>
    test <@ serialize (TestPrune.R.Busy "wait") = """{"message":"wait","status":"busy"}""" @>

    for reply in TestPrune.runReplies do
        test <@ FsHotWatch.TestPrune.TestPruneReplies.RunReply.tryParse (serialize reply) = Some reply @>

[<Fact(Timeout = 15000)>]
let ``a scope reply is written as the JSON it always was, and read back as itself`` () =
    let serialize = FsHotWatch.TestPrune.TestPruneReplies.ScopeReply.serialize

    test <@ serialize (TestPrune.S.ScopeSet "owed") = """{"scope":"owed"}""" @>
    test <@ serialize (TestPrune.S.ScopeRefused "no") = """{"error":"no"}""" @>

    for reply in TestPrune.scopeReplies do
        test <@ FsHotWatch.TestPrune.TestPruneReplies.ScopeReply.tryParse (serialize reply) = Some reply @>

[<Fact(Timeout = 15000)>]
let ``the listing replies are written as the JSON they always were, and read back as themselves`` () =
    let tests: FsHotWatch.TestPrune.TestPruneReplies.AffectedTest list =
        [ { Project = "Tests"
            Class = "Tests.A"
            Method = "works" } ]

    let flaky: FsHotWatch.TestPrune.TestPruneReplies.FlakyTest list =
        [ { Name = "Tests.A.works"
            Flakiness = 0.25
            Runs = 8 } ]

    test
        <@
            FsHotWatch.TestPrune.TestPruneReplies.AffectedTests.serialize tests = """[{"class":"Tests.A","method":"works","project":"Tests"}]"""
        @>

    test <@ FsHotWatch.TestPrune.TestPruneReplies.ChangedFiles.serialize [ "src/A.fs" ] = """["src/A.fs"]""" @>

    test
        <@
            FsHotWatch.TestPrune.TestPruneReplies.FlakyTests.serialize flaky = """{"tests":[{"flakiness":0.25,"name":"Tests.A.works","runs":8}]}"""
        @>

    test
        <@
            FsHotWatch.TestPrune.TestPruneReplies.AffectedTests.tryParse (
                FsHotWatch.TestPrune.TestPruneReplies.AffectedTests.serialize tests
            ) = Some tests
        @>

    test
        <@
            FsHotWatch.TestPrune.TestPruneReplies.ChangedFiles.tryParse (
                FsHotWatch.TestPrune.TestPruneReplies.ChangedFiles.serialize [ "src/A.fs" ]
            ) = Some [ "src/A.fs" ]
        @>

    test
        <@
            FsHotWatch.TestPrune.TestPruneReplies.FlakyTests.tryParse (
                FsHotWatch.TestPrune.TestPruneReplies.FlakyTests.serialize flaky
            ) = Some flaky
        @>

[<Fact(Timeout = 15000)>]
let ``a typed test-prune reply renders exactly as its shape did`` () =
    let replies = FsHotWatch.TestPrune.TestPruneReplies.RunSchema

    for reply in TestPrune.runReplies do
        let payload = FsHotWatch.TestPrune.TestPruneReplies.RunReply.serialize reply
        rendersAsSniffed "run-tests" replies payload
        rendersAsSniffed "test-results" replies payload

    for reply in TestPrune.scopeReplies do
        rendersAsSniffed
            "set-scope"
            FsHotWatch.TestPrune.TestPruneReplies.SetScopeSchema
            (FsHotWatch.TestPrune.TestPruneReplies.ScopeReply.serialize reply)

    rendersAsSniffed
        "flaky-tests"
        FsHotWatch.TestPrune.TestPruneReplies.FlakyTestsSchema
        """{"tests":[{"flakiness":0.25,"name":"Tests.A.works","runs":8}]}"""

    rendersAsSniffed
        "check-reach"
        FsHotWatch.TestPrune.TestPruneReplies.CheckReachSchema
        """{"recorded":false,"reason":"no run has completed"}"""

/// AUTOMATION-1466: both answer with a JSON array, which the CLI used to refuse as "not a JSON
/// object" (no verdict). They are listings, so they list, and exit 0.
[<Fact(Timeout = 15000)>]
let ``affected-tests and changed-files list what they answer`` () =
    let affected =
        renderedTyped
            "affected-tests"
            FsHotWatch.TestPrune.TestPruneReplies.AffectedTestsSchema
            """[{"class":"Tests.A","method":"works","project":"Tests"}]"""

    test <@ affected = (0, "1 affected test(s):\n  · Tests: Tests.A.works\n") @>

    let noneAffected =
        renderedTyped "affected-tests" FsHotWatch.TestPrune.TestPruneReplies.AffectedTestsSchema "[]"

    test <@ noneAffected = (0, "no affected tests\n") @>

    let changed =
        renderedTyped
            "changed-files"
            FsHotWatch.TestPrune.TestPruneReplies.ChangedFilesSchema
            """["src/A.fs","src/B.fs"]"""

    test <@ changed = (0, "2 changed file(s):\n  · src/A.fs\n  · src/B.fs\n") @>

    let noneChanged =
        renderedTyped "changed-files" FsHotWatch.TestPrune.TestPruneReplies.ChangedFilesSchema "[]"

    test <@ noneChanged = (0, "no changed files\n") @>

[<Theory(Timeout = 15000)>]
[<InlineData("run-tests", "test-prune/run-v1", """{"status":"exploded"}""")>]
[<InlineData("run-tests", "test-prune/run-v1", """{"count":1,"projects":[]}""")>]
[<InlineData("set-scope", "test-prune/set-scope-v1", """{"scope":3}""")>]
[<InlineData("affected-tests", "test-prune/affected-tests-v1", """{"tests":[]}""")>]
[<InlineData("changed-files", "test-prune/changed-files-v1", """[1]""")>]
[<InlineData("flaky-tests", "test-prune/flaky-tests-v1", """{"tests":[{"name":"x"}]}""")>]
[<InlineData("test-scope", "test-prune/test-scope-v1", "not json")>]
[<InlineData("check-reach", "test-prune/check-reach-v1", """{"reason":"x"}""")>]
[<InlineData("run-tests", "test-prune/run-v1", """{"elapsed":"1.0s"}""")>]
[<InlineData("run-tests", "test-prune/run-v1", """{"projects":3}""")>]
[<InlineData("affected-tests", "test-prune/affected-tests-v1", """[{"project":"Tests"}]""")>]
[<InlineData("changed-files", "test-prune/changed-files-v1", """{"files":[]}""")>]
[<InlineData("flaky-tests", "test-prune/flaky-tests-v1", """{"tests":3}""")>]
let ``a test-prune payload its schema does not describe is no verdict``
    (command: string, schema: string, payload: string)
    =
    refusesUndescribed command schema payload
