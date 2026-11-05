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
    for status in
        [ BuildStatus.Passed "Build succeeded."
          BuildStatus.Failed "error FS0001: boom" ] do
        rendersAsSniffed "build-status" BuildStatusSchema (BuildStatus.serialize status)

/// AUTOMATION-1466: a build that has not run printed an empty status display and exited 0. It
/// is no verdict.
[<Fact(Timeout = 15000)>]
let ``a build that has not run is no verdict`` () =
    let exitCode, text =
        renderedTyped "build-status" BuildStatusSchema (BuildStatus.serialize BuildStatus.NotRun)

    test <@ exitCode = NoVerdictExitCode @>
    test <@ text.Contains "the build has not run yet" @>

[<Fact(Timeout = 15000)>]
let ``force-rebuild says the next build is forced`` () =
    // AUTOMATION-1466: this printed an empty status display.
    let exitCode, text =
        renderedTyped "force-rebuild" ForceRebuildSchema (ForceRebuild.serialize ForceRebuild.Forced)

    test <@ exitCode = 0 @>
    test <@ text.Contains "the next build runs for real, not from the cache" @>

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
let ``a warnings reply says how many there are, and fails when there are any`` () =
    // AUTOMATION-1466: this printed an empty status display and exited 0. Lint warnings fail
    // unless warnings are excused, as they do in `fshw check`.
    let render noWarnFail (files, warnings) =
        let payload =
            FsHotWatch.Lint.LintReplies.Warnings.serialize { Files = files; Warnings = warnings }

        match
            rendered
                noWarnFail
                (FsHotWatch.Ipc.declaredOutputReply
                    "warnings"
                    (FsHotWatch.Plugin.ReplySchema.Typed FsHotWatch.Lint.LintReplies.WarningsSchema)
                    payload)
        with
        | Ok rendering -> rendering
        | Error(kind, message) -> failwith $"rendering %s{payload} threw %s{kind}: %s{message}"

    let clean = render false (0, 0)
    test <@ fst clean = 0 @>
    test <@ (snd clean).Contains "lint: no warnings" @>

    let dirty = render false (3, 11)
    test <@ fst dirty = 1 @>
    test <@ (snd dirty).Contains "lint: 11 warning(s) in 3 file(s)" @>

    test <@ fst (render true (3, 11)) = 0 @>

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
let ``a file command status says how the command's last run ended`` () =
    // AUTOMATION-1466: every status printed an empty status display and exited 0, a failed
    // command included.
    let expected =
        [ FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.Passed, 0, "Passed"
          FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.Failed, 1, "Failed"
          FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.NotRun, NoVerdictExitCode, "has not run yet" ]

    for status, code, says in expected do
        let exitCode, text =
            renderedTyped
                "lint-docs-status"
                FsHotWatch.FileCommand.FileCommandReplies.StatusSchema
                (FsHotWatch.FileCommand.FileCommandReplies.CommandStatus.serialize status)

        test <@ exitCode = code @>
        test <@ text.Contains says @>

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
    // AUTOMATION-1466: this printed an empty status display. It counts findings of every
    // severity, so it reports and does not decide: `fshw check` does.
    let exitCode, text =
        renderedTyped
            "diagnostics"
            FsHotWatch.Analyzers.AnalyzerReplies.DiagnosticsSchema
            (FsHotWatch.Analyzers.AnalyzerReplies.Diagnostics.serialize
                { Analyzers = 2
                  Files = 1
                  Diagnostics = 4 })

    test <@ exitCode = 0 @>
    test <@ text.Contains "analyzers: 2 loaded, 4 diagnostic(s) in 1 file(s)" @>

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
        match reply with
        | TestPrune.R.Running
        | TestPrune.R.Settling
        | TestPrune.R.NotRun -> ()
        | _ ->
            let payload = FsHotWatch.TestPrune.TestPruneReplies.RunReply.serialize reply
            rendersAsSniffed "run-tests" replies payload
            rendersAsSniffed "test-results" replies payload

    rendersAsSniffed
        "set-scope"
        FsHotWatch.TestPrune.TestPruneReplies.SetScopeSchema
        (FsHotWatch.TestPrune.TestPruneReplies.ScopeReply.serialize (TestPrune.S.ScopeRefused "no"))

/// AUTOMATION-1466: these printed an empty status display.
[<Fact(Timeout = 15000)>]
let ``set-scope and flaky-tests say what they answer`` () =
    let scope =
        renderedTyped
            "set-scope"
            FsHotWatch.TestPrune.TestPruneReplies.SetScopeSchema
            (FsHotWatch.TestPrune.TestPruneReplies.ScopeReply.serialize (TestPrune.S.ScopeSet "full"))

    test <@ scope = (0, "test scope set to full\n") @>

    let flaky =
        renderedTyped
            "flaky-tests"
            FsHotWatch.TestPrune.TestPruneReplies.FlakyTestsSchema
            """{"tests":[{"flakiness":0.25,"name":"Tests.A.works","runs":8}]}"""

    test <@ flaky = (0, "1 flaky test(s):\n  · Tests.A.works: 25% flaky over 8 run(s)\n") @>

    let noneFlaky =
        renderedTyped "flaky-tests" FsHotWatch.TestPrune.TestPruneReplies.FlakyTestsSchema """{"tests":[]}"""

    test <@ noneFlaky = (0, "no flaky tests recorded\n") @>

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

/// AUTOMATION-1466: these printed an empty status display and exited 0. A run still in
/// progress or still being committed is in flight; no completed run is no verdict.
[<Fact(Timeout = 15000)>]
let ``test-results without a committed result is in flight or no verdict`` () =
    let expected =
        [ TestPrune.R.Running, RerunTimedOutExitCode, "a test run is in progress"
          TestPrune.R.Settling, RerunTimedOutExitCode, "being committed"
          TestPrune.R.NotRun, NoVerdictExitCode, "no test run has completed" ]

    for reply, code, says in expected do
        let exitCode, text =
            renderedTyped
                "test-results"
                FsHotWatch.TestPrune.TestPruneReplies.RunSchema
                (FsHotWatch.TestPrune.TestPruneReplies.RunReply.serialize reply)

        test <@ exitCode = code @>
        test <@ text.Contains says @>

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

// AUTOMATION-1466 follow-up: the last three commands the CLI passthrough rendered as an empty
// status display with exit 0. Each now says what it answered, with the exit code of the verb
// that owns the same question.

[<Fact(Timeout = 15000)>]
let ``test-scope says what the last run covered, and an unknown scope is no verdict`` () =
    let full =
        renderedTyped
            "test-scope"
            FsHotWatch.TestPrune.TestPruneReplies.TestScopeSchema
            """{"kind":"full","ranProjects":3,"totalProjects":3,"runId":"7843b74bf301422bbaf3b482d1d97834"}"""

    test <@ fst full = 0 @>
    test <@ (snd full).Contains "test scope: full suite (3/3 projects, unfiltered)" @>
    test <@ (snd full).Contains "run 7843b74bf301422bbaf3b482d1d97834" @>

    let running =
        renderedTyped
            "test-scope"
            FsHotWatch.TestPrune.TestPruneReplies.TestScopeSchema
            """{"kind":"running","runId":null}"""

    test <@ fst running = NoVerdictExitCode @>
    test <@ (snd running).Contains "the daemon did not report a test scope" @>

let private reach (body: string) =
    renderedTyped
        "check-reach"
        FsHotWatch.TestPrune.TestPruneReplies.CheckReachSchema
        ("""{"recorded":true,"runId":"5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e",""" + body + "}")

[<Fact(Timeout = 15000)>]
let ``check-reach says what check would have reached, and an undecided reach is no verdict`` () =
    let reached =
        reach
            """"kind":"filtered","ranProjects":2,"totalProjects":6,"reach":"reached-a-failure",
               "failingSuites":["Lib.Tests"],"reason":null,
               "conditionalFailureRecall":{"measured":true,"reached":3,"total":4,"threshold":1.0,
                                           "acceptable":false,"reason":null}"""

    test <@ fst reached = 0 @>

    test
        <@
            (snd reached).Contains
                "check reach for run 5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e: check would have run impact-filtered (2/6 projects)"
        @>

    test <@ (snd reached).Contains "it reaches a failing test: Lib.Tests" @>
    test <@ (snd reached).Contains "failure recall: 3 of 4 reached (threshold 100%, below it)" @>

    let missed =
        reach
            """"kind":"filtered","ranProjects":1,"totalProjects":2,"reach":"reached-no-failure",
               "failingSuites":[],
               "missed":[{"project":"Jobs.Tests","class":"Jobs.ReflectionGuard","cause":"project-not-selected"}]"""

    test <@ fst missed = 0 @>
    test <@ (snd missed).Contains "it reaches none of the failing tests" @>
    test <@ (snd missed).Contains "missed: Jobs.Tests :: Jobs.ReflectionGuard — project was not selected" @>

    let causes =
        reach
            """"kind":"filtered","ranProjects":1,"totalProjects":2,"reach":"reached-no-failure",
               "failingSuites":[],
               "missed":[{"project":"Api.Tests","class":"Api.ContractTests","cause":"class-not-in-filter"},
                         {"project":"Web.Tests","class":"Web.Pages","cause":"from-the-future"}],
               "conditionalFailureRecall":{"measured":false,"reached":0,"total":0,"threshold":1.0,
                                           "acceptable":null,"reason":"no failures reached"}"""

    test <@ (snd causes).Contains "missed: Api.Tests :: Api.ContractTests — class was not in the generated filter" @>
    test <@ (snd causes).Contains "missed: Web.Tests :: Web.Pages — cause 'from-the-future'" @>
    test <@ (snd causes).Contains "failure recall not measurable: no failures reached" @>

    let acceptable =
        reach
            """"kind":"filtered","ranProjects":2,"totalProjects":6,"reach":"reached-a-failure",
               "failingSuites":["Lib.Tests"],
               "conditionalFailureRecall":{"measured":true,"reached":4,"total":4,"threshold":1.0,
                                           "acceptable":true,"reason":null}"""

    test <@ (snd acceptable).Contains "failure recall: 4 of 4 reached (threshold 100%, at or above it)" @>

    let nothing =
        reach """"kind":"full","ranProjects":6,"totalProjects":6,"reach":"no-failures-to-reach","failingSuites":[]"""

    test <@ fst nothing = 0 @>
    test <@ (snd nothing).Contains "no test failed, so there was nothing to reach" @>

    let unknown =
        reach
            """"kind":"full","ranProjects":6,"totalProjects":6,"reach":"unknown","reason":"a project-level red",
               "failingSuites":[]"""

    test <@ fst unknown = NoVerdictExitCode @>
    test <@ (snd unknown).Contains "a project-level red" @>

    let unrecorded =
        renderedTyped
            "check-reach"
            FsHotWatch.TestPrune.TestPruneReplies.CheckReachSchema
            """{"recorded":false,"reason":"no run has completed"}"""

    test <@ fst unrecorded = NoVerdictExitCode @>
    test <@ (snd unrecorded).Contains "no run has completed" @>

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``analyzer-findings answers as fshw findings does, with its exit codes`` () =
    FsHotWatch.Tests.TestHelpers.withTempDir "pa-findings" (fun root ->
        let replyWith expected =
            FsHotWatch.Analyzers.AnalyzerFindings.serializeReply
                { findingsReply with
                    ScopeRoot = Some root
                    Expected = expected }

        let clean =
            renderedTyped
                FsHotWatch.Analyzers.AnalyzerFindings.CommandName
                FsHotWatch.Analyzers.AnalyzerFindings.ReplySchema
                (replyWith [])

        test <@ fst clean = FsHotWatch.Cli.Findings.Outcome.exitCode FsHotWatch.Cli.Findings.Clean @>
        test <@ (snd clean).Contains "findings: clean" @>

        let incomplete =
            renderedTyped
                FsHotWatch.Analyzers.AnalyzerFindings.CommandName
                FsHotWatch.Analyzers.AnalyzerFindings.ReplySchema
                (replyWith [ System.IO.Path.Combine(root, "src", "A.fs") ])

        test <@ fst incomplete = FsHotWatch.Cli.Findings.Outcome.exitCode FsHotWatch.Cli.Findings.Incomplete @>
        test <@ (snd incomplete).Contains "findings: incomplete" @>
        test <@ (snd incomplete).Contains "missing: src/A.fs" @>)
