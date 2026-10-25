/// Agent mode is machine-only: one compact JSON line on stdout (the agent envelope,
/// `AgentEnvelope.Contract`), nothing on stderr, and the human output kept on disk in
/// `.fshw/agent-output.log` where the envelope points.
///
/// Every "absent" assertion here has a positive control beside it: an envelope that
/// leaves out the human block also leaves it out when the command printed nothing.
///
/// Serialized with the other process-global mutators: `capture` and the end-to-end
/// cases swap `Console.Out` / `Console.Error`.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.AgentEnvelopeTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.Program
open FsHotWatch.Tests.TestHelpers

/// The envelope parsed back, with the root element.
let parse (line: string) : JsonElement =
    use doc = JsonDocument.Parse line
    doc.RootElement.Clone()

let str (e: JsonElement) (name: string) : string = e.GetProperty(name).GetString()

/// Struct-receiver calls cannot appear inside a quotation over a captured local (FS3155),
/// so these take the element by value.
let prop (e: JsonElement) (name: string) : JsonElement = e.GetProperty name

let num (e: JsonElement) (name: string) : int = e.GetProperty(name).GetInt32()

let has (e: JsonElement) (name: string) : bool =
    let mutable found = Unchecked.defaultof<JsonElement>
    e.TryGetProperty(name, &found)

let strings (e: JsonElement) (name: string) : string list =
    e.GetProperty(name).EnumerateArray()
    |> Seq.map (fun x -> x.GetString())
    |> Seq.toList

let facts (outcome: string) (scope: TestScope option) (reasons: string list) : AgentEnvelope.Facts =
    { Outcome = outcome
      Scope = scope
      Reasons = reasons
      ReasonsOmitted = 0
      FromVerdictFile = true }

/// What a human run of `verdict` writes on a stale tree: the block the ticket names.
let humanBlock =
    [ "✗ stale: the verdict describes a different tree (verdict sha256:a, current sha256:b)"
      "    verdict tree  sha256:a"
      "  Re-run `fshw check` (or `fshw confirm` for unfiltered full-suite evidence). Never reuse it." ]

// --- cleanLines ---

[<Fact(Timeout = 15000)>]
let ``cleanLines strips ANSI, keeps what a carriage return left on screen, and drops blank lines`` () =
    let raw =
        [ "\u001b[31m✗\u001b[0m red"
          "spinner 1\rspinner 2\rdone"
          ""
          "   "
          "kept  " ]

    test <@ AgentEnvelope.cleanLines raw = [ "✗ red"; "done"; "kept" ] @>

// --- render: the contract ---

[<Fact(Timeout = 15000)>]
let ``an envelope is ONE line of JSON naming its contract, command and exit code`` () =
    let line =
        AgentEnvelope.render
            "check"
            0
            (AgentEnvelope.Shape.VerdictBacked(Some(facts "green" (Some(FullSuite(3, None))) [])))
            (Some [])

    test <@ not (line.Contains "\n") @>
    let e = parse line
    test <@ str e "fshw" = AgentEnvelope.Contract @>
    test <@ str e "command" = "check" @>
    test <@ num e "exit" = 0 @>
    test <@ str e "outcome" = "green" @>
    test <@ str (prop e "scope") "kind" = "full" @>
    test <@ num (prop e "scope") "ran" = 3 @>
    test <@ str e "verdict" = Verdict.RelativePath @>
    test <@ str e "output" = AgentEnvelope.TranscriptRelativePath @>
    test <@ List.isEmpty (strings e "reasons") @>

[<Fact(Timeout = 15000)>]
let ``a red envelope carries its reasons and NOT the human block`` () =
    let line =
        AgentEnvelope.render
            "check"
            1
            (AgentEnvelope.Shape.VerdictBacked(Some(facts "red" (Some(FullSuite(1, None))) [ "lint src/A.fs: boom" ])))
            (Some humanBlock)

    let e = parse line
    test <@ str e "outcome" = "red" @>
    test <@ strings e "reasons" = [ "lint src/A.fs: boom" ] @>
    test <@ not (has e "reasonsFrom") @>
    // The human block is absent from the envelope...
    test <@ not (line.Contains "✗") @>
    test <@ not (line.Contains "Re-run") @>
    // ...and present in what the command printed (positive control).
    test <@ humanBlock |> List.exists (fun l -> l.Contains "✗") @>
    test <@ humanBlock |> List.exists (fun l -> l.Contains "Re-run") @>

[<Fact(Timeout = 15000)>]
let ``a filtered green says how little it covered`` () =
    let e =
        parse (
            AgentEnvelope.render
                "check"
                0
                (AgentEnvelope.Shape.VerdictBacked(Some(facts "green" (Some(ImpactFiltered(1, 6))) [])))
                (Some [])
        )

    test <@ str (prop e "scope") "kind" = "filtered" @>
    test <@ num (prop e "scope") "ran" = 1 @>
    test <@ num (prop e "scope") "total" = 6 @>
    test <@ (str e "caveat").Contains "1 of 6" @>

[<Fact(Timeout = 15000)>]
let ``a full-suite green carries no caveat`` () =
    let e =
        parse (
            AgentEnvelope.render
                "confirm"
                0
                (AgentEnvelope.Shape.VerdictBacked(Some(facts "green" (Some(FullSuite(6, None))) [])))
                (Some [])
        )

    test <@ not (has e "caveat") @>

[<Fact(Timeout = 15000)>]
let ``a failure with no verdict to explain it takes its reasons from the output tail`` () =
    let transcript = [ for i in 1..15 -> $"line %d{i}" ]

    let e =
        parse (AgentEnvelope.render "check" 2 (AgentEnvelope.Shape.VerdictBacked None) (Some transcript))

    test <@ str e "outcome" = "incomplete" @>
    test <@ strings e "reasons" = [ for i in 6..15 -> $"line %d{i}" ] @>
    test <@ str e "reasonsFrom" = "output" @>
    test <@ not (has e "verdict") @>

[<Fact(Timeout = 15000)>]
let ``a success with no verdict carries no output tail`` () =
    let e =
        parse (AgentEnvelope.render "check" 0 (AgentEnvelope.Shape.VerdictBacked None) (Some [ "chatter" ]))

    test <@ str e "outcome" = "ok" @>
    test <@ List.isEmpty (strings e "reasons") @>
    test <@ not (has e "reasonsFrom") @>

[<Fact(Timeout = 15000)>]
let ``exit codes without a verdict map to outcomes`` () =
    let outcomeOf code =
        str (parse (AgentEnvelope.render "check" code (AgentEnvelope.Shape.VerdictBacked None) None)) "outcome"

    test <@ outcomeOf 1 = "red" @>
    test <@ outcomeOf 2 = "incomplete" @>
    test <@ outcomeOf 3 = "no-verdict" @>
    test <@ outcomeOf 4 = "stale" @>
    test <@ outcomeOf 5 = "no-verdict" @>
    test <@ outcomeOf 6 = "in-flight" @>
    test <@ outcomeOf 130 = "signalled" @>
    test <@ outcomeOf 42 = "fail" @>

[<Fact(Timeout = 15000)>]
let ``a long or multi-line reason is collapsed to one bounded line`` () =
    let long = String('x', 1000)

    let e =
        parse (
            AgentEnvelope.render
                "check"
                1
                (AgentEnvelope.Shape.VerdictBacked(Some(facts "red" None [ "first\nsecond"; long ])))
                None
        )

    let reasons = strings e "reasons"
    test <@ reasons.[0] = "first second" @>
    test <@ reasons.[1].Length <= AgentEnvelope.MaxReasonChars @>

[<Fact(Timeout = 15000)>]
let ``omitted reasons are counted`` () =
    let e =
        parse (
            AgentEnvelope.render
                "check"
                1
                (AgentEnvelope.Shape.VerdictBacked(
                    Some
                        { facts "red" None [ "a" ] with
                            ReasonsOmitted = 4 }
                ))
                None
        )

    test <@ num e "reasonsOmitted" = 4 @>

[<Fact(Timeout = 15000)>]
let ``an output-backed verb carries its own lines, bounded, and no reasons`` () =
    let transcript = [ for i in 1 .. AgentEnvelope.MaxLines + 5 -> $"build: ok %d{i}" ]

    let e =
        parse (AgentEnvelope.render "status" 0 AgentEnvelope.Shape.OutputBacked (Some transcript))

    test <@ str e "outcome" = "ok" @>
    test <@ strings e "lines" = List.truncate AgentEnvelope.MaxLines transcript @>
    test <@ num e "linesOmitted" = 5 @>
    test <@ not (has e "reasons") @>
    test <@ not (has e "scope") @>

[<Fact(Timeout = 15000)>]
let ``an output-backed failure is fail`` () =
    let e =
        parse (AgentEnvelope.render "scan" 1 AgentEnvelope.Shape.OutputBacked (Some [ "oops" ]))

    test <@ str e "outcome" = "fail" @>
    test <@ strings e "lines" = [ "oops" ] @>

[<Fact(Timeout = 15000)>]
let ``an envelope with no transcript names no output file`` () =
    let e =
        parse (AgentEnvelope.render "verdict" 5 (AgentEnvelope.Shape.VerdictBacked None) None)

    test <@ not (has e "output") @>

// --- factsOf: where the facts come from ---

[<Fact(Timeout = 15000)>]
let ``the verdict verb reports a missing verdict as no-verdict with its reason`` () =
    let f =
        AgentEnvelope.factsOf (AgentEnvelope.Evidence.ByVerdictVerb(Verdict.Report.NoVerdict "no verdict on disk")) 5

    test <@ f |> Option.map _.Outcome = Some "no-verdict" @>
    test <@ f |> Option.map _.Reasons = Some [ "no verdict on disk" ] @>
    test <@ f |> Option.map _.FromVerdictFile = Some false @>

[<Fact(Timeout = 15000)>]
let ``after a run, a missing verdict yields no facts`` () =
    test <@ AgentEnvelope.factsOf (AgentEnvelope.Evidence.AfterRun(Verdict.Report.NoVerdict "none")) 2 = None @>

/// A lint red cause in `file`.
let lintCause (file: string) : Verdict.RedCause =
    { Source = "lint"
      File = file
      Severity = "error"
      Message = Verdict.RedCauseMessage.ofLedger "logs/daemon.log" "lint" file "boom"
      Kind = Verdict.AboutThisTree }

/// A `check` verdict read against `model`, with the given outcome, exit code, plugins and causes.
let verdictIn
    (model: ProjectModelReading)
    (outcome: Verdict.Outcome)
    (exitCode: int)
    (plugins: Verdict.PluginVerdict list)
    (causes: Verdict.RedCause list)
    : Verdict.Verdict =
    Verdict.create
        Verdict.Check
        (BaselineFixtures.reportOf (FullSuite(1, None)))
        ({ Hash = "sha256:abc"
           FileCount = 1
           SkippedCount = 0
           DeclaredCount = 0
           AbsentDeclarationCount = 0 }
        : TreeHash.Tree)
        (Some [])
        outcome
        exitCode
        plugins
        []
        Verdict.CheckComparison.notRecorded
        causes
        model

/// A `check` verdict against an available project model.
let verdictOf = verdictIn ProjectModelFixtures.available

let failingPlugin (name: string) (summary: string option) : Verdict.PluginVerdict =
    { Name = name
      Outcome = Verdict.PluginOutcome.Fail
      ElapsedMs = Some 10L
      Summary = summary }

/// A red `check` verdict: lint failed, with one red cause.
let redVerdict () : Verdict.Verdict =
    verdictOf Verdict.Red 1 [ failingPlugin "lint" (Some "1 error") ] [ lintCause "src/A.fs" ]

[<Fact(Timeout = 15000)>]
let ``a red verdict's facts name the failing plugin and the cause`` () =
    let f =
        AgentEnvelope.factsOf (AgentEnvelope.Evidence.ByVerdictVerb(Verdict.Report.Applies(redVerdict ()))) 1

    test <@ f |> Option.map _.Outcome = Some "red" @>
    let reasons = f |> Option.map _.Reasons |> Option.defaultValue []
    test <@ reasons |> List.exists (fun r -> r.StartsWith "lint: fail") @>
    test <@ reasons |> List.exists (fun r -> r.Contains "src/A.fs" && r.Contains "boom") @>
    test <@ f |> Option.map _.Scope = Some(Some(FullSuite(1, None))) @>

[<Fact(Timeout = 15000)>]
let ``after a run, a verdict that disagrees with the exit code is not this run's answer`` () =
    let report = Verdict.Report.Applies(redVerdict ())
    test <@ AgentEnvelope.factsOf (AgentEnvelope.Evidence.AfterRun report) 2 = None @>
    // Positive control: the same verdict with the exit code it records is used.
    test <@ (AgentEnvelope.factsOf (AgentEnvelope.Evidence.AfterRun report) 1).IsSome @>

[<Fact(Timeout = 15000)>]
let ``the verdict verb reports a stale verdict as stale, with its reason`` () =
    let f =
        AgentEnvelope.factsOf
            (AgentEnvelope.Evidence.ByVerdictVerb(Verdict.Report.Stale(redVerdict (), "stale: different tree")))
            4

    test <@ f |> Option.map _.Outcome = Some "stale" @>
    test <@ f |> Option.map _.Reasons = Some [ "stale: different tree" ] @>

[<Fact(Timeout = 15000)>]
let ``the verdict verb reports an in-flight verdict as in-flight, with its reason`` () =
    let f =
        AgentEnvelope.factsOf
            (AgentEnvelope.Evidence.ByVerdictVerb(Verdict.Report.InFlight(redVerdict (), "a check is running")))
            6

    test <@ f |> Option.map _.Outcome = Some "in-flight" @>
    test <@ f |> Option.map _.Reasons = Some [ "a check is running" ] @>

[<Fact(Timeout = 15000)>]
let ``after a run, a stale verdict on disk is not this run's answer`` () =
    let stale = Verdict.Report.Stale(redVerdict (), "stale")
    test <@ AgentEnvelope.factsOf (AgentEnvelope.Evidence.AfterRun stale) 1 = None @>
    // Positive control: the same verdict, applying, is this run's answer.
    test <@ (AgentEnvelope.factsOf (AgentEnvelope.Evidence.AfterRun(Verdict.Report.Applies(redVerdict ()))) 1).IsSome @>

[<Fact(Timeout = 15000)>]
let ``an incomplete or model-unavailable verdict leads its reasons with why`` () =
    let incomplete =
        AgentEnvelope.ofVerdict (verdictOf (Verdict.Incomplete "coverage unconfirmed") 2 [] [])

    test <@ incomplete.Outcome = "incomplete" @>
    test <@ List.tryHead incomplete.Reasons = Some "coverage unconfirmed" @>

    let unavailable =
        AgentEnvelope.ofVerdict (
            verdictIn
                (ProjectModelReading.NotReported "the daemon did not report its project model")
                (Verdict.ModelUnavailable "no project model")
                2
                []
                []
        )

    test <@ unavailable.Outcome = "model-unavailable" @>
    test <@ List.tryHead unavailable.Reasons = Some "no project model" @>

[<Fact(Timeout = 15000)>]
let ``a failing plugin with no summary, or a blank one, is named alone`` () =
    let f =
        AgentEnvelope.ofVerdict (
            verdictOf
                Verdict.Red
                1
                [ failingPlugin "build" None
                  failingPlugin "format" (Some "   ")
                  failingPlugin "lint" (Some "2 errors") ]
                [ lintCause "src/A.fs" ]
        )

    test <@ f.Reasons |> List.contains "build: fail" @>
    test <@ f.Reasons |> List.contains "format: fail" @>
    // Positive control: a summary that says something is kept.
    test <@ f.Reasons |> List.contains "lint: fail — 2 errors" @>

[<Fact(Timeout = 15000)>]
let ``red causes the verdict truncated are counted as omitted`` () =
    let causes =
        [ for i in 1 .. Verdict.MaxRedCauses + 3 -> lintCause $"src/F%d{i}.fs" ]

    let f = AgentEnvelope.ofVerdict (verdictOf Verdict.Red 1 [] causes)
    test <@ f.ReasonsOmitted = 3 @>
    test <@ f.Reasons |> List.filter (fun r -> r.StartsWith "lint src/F") |> List.length = Verdict.MaxRedCauses @>
    // Positive control: a verdict listing every cause omits none.
    test <@ (AgentEnvelope.ofVerdict (redVerdict ())).ReasonsOmitted = 0 @>

[<Fact(Timeout = 15000)>]
let ``a scope with no counts reports only its kind`` () =
    let kindOf scope =
        let e =
            parse (
                AgentEnvelope.render
                    "check"
                    3
                    (AgentEnvelope.Shape.VerdictBacked(Some(facts "no-verdict" (Some scope) [ "why" ])))
                    None
            )

        str (prop e "scope") "kind", has (prop e "scope") "ran"

    test <@ kindOf (NoTestsRun NoTestsReason.NoRunYet) = ("none", false) @>
    test <@ kindOf ScopeUnknown = ("unknown", false) @>
    test <@ kindOf (ScopeUnreadable "torn") = ("unreadable", false) @>
    // Positive control: a counted scope carries its counts.
    test <@ kindOf (FullSuite(2, None)) = ("full", true) @>

// --- capture: the human output goes to the transcript, not the terminal ---

[<Fact(Timeout = 15000)>]
let ``capture sends stdout and stderr to the transcript and restores the console`` () =
    withTempDir "agent-capture" (fun root ->
        let priorOut = Console.Out
        let priorErr = Console.Error

        let exitCode, lines =
            AgentEnvelope.capture root (fun () ->
                printfn "to stdout"
                eprintfn "\u001b[31mto stderr\u001b[0m"
                3)

        test <@ exitCode = 3 @>
        test <@ lines = [ "to stdout"; "to stderr" ] @>
        test <@ obj.ReferenceEquals(Console.Out, priorOut) @>
        test <@ obj.ReferenceEquals(Console.Error, priorErr) @>

        let onDisk =
            File.ReadAllText(Path.Combine(root, AgentEnvelope.TranscriptRelativePath))

        test <@ onDisk.Contains "to stderr" @>)

[<Fact(Timeout = 15000)>]
let ``capture restores the console when the action throws`` () =
    withTempDir "agent-capture-throw" (fun root ->
        let priorErr = Console.Error

        raises<InvalidOperationException> <@ AgentEnvelope.capture root (fun () -> invalidOp "boom") @>

        test <@ obj.ReferenceEquals(Console.Error, priorErr) @>)

[<Fact(Timeout = 15000)>]
let ``a scan progress line goes to the stderr of the moment, so capture receives it`` () =
    withTempDir "agent-capture-heartbeat" (fun root ->
        let priorErr = Console.Error
        use earlier = new StringWriter()
        // Print once BEFORE the capture, so a function that bound whichever stderr it
        // first saw would be pinned to `earlier` and miss the transcript.
        Console.SetError earlier

        try
            ScanHeartbeat.emitLine "scanning 1/4"
        finally
            Console.SetError priorErr

        let _, lines =
            AgentEnvelope.capture root (fun () ->
                ScanHeartbeat.emitLine "scanning 3/4"
                0)

        test <@ lines |> List.exists (fun l -> l.Contains "scanning 3/4") @>
        // Positive control: the earlier line went to the earlier writer.
        test <@ earlier.ToString().Contains "scanning 1/4" @>)


// --- which verbs are gated ---

[<Fact(Timeout = 15000)>]
let ``agentGateFor routes each verb`` () =
    test <@ agentGateFor (Command.Check []) = AgentGate.VerdictAfterRun @>
    test <@ agentGateFor (Command.Confirm []) = AgentGate.VerdictAfterRun @>
    test <@ agentGateFor Command.Verdict = AgentGate.VerdictRead @>
    test <@ agentGateFor (Command.Status(None, [])) = AgentGate.OwnOutput @>
    test <@ agentGateFor Command.Scan = AgentGate.OwnOutput @>
    // The daemon's own process, and verbs whose output IS the product, are never gated.
    test <@ agentGateFor Command.Start = AgentGate.Ungated @>
    test <@ agentGateFor Command.Init = AgentGate.Ungated @>
    test <@ agentGateFor (Command.DeadCode []) = AgentGate.Ungated @>

[<Fact(Timeout = 15000)>]
let ``commandToken names verbs as the CLI spells them`` () =
    test <@ commandToken (Command.Check []) = "check" @>
    test <@ commandToken (Command.TestRerun []) = "test-rerun" @>
    test <@ commandToken (Command.DeadCode []) = "dead-code" @>

// --- end to end: `verdict` in agent mode vs without ---

let fakeConfig: DaemonConfiguration =
    { defaultTestConfig () with
        Build = None
        Format = Off
        Lint = false
        Cache = CacheBackendConfig.NoCache }

let fakeIpc () : IpcOps =
    { Shutdown = fun _ -> async { return "shutting down" }
      Scan = fun _ _ -> async { return "scan started" }
      ScanStatus = fun _ -> async { return "idle" }
      GetStatus = fun _ -> async { return "{}" }
      GetPluginStatus = fun _ _ -> async { return "{}" }
      RunCommand = fun _ name _ -> async { return FsHotWatch.Ipc.unknownCommandReply name }
      RunCommandFor = fun _ _ name _ -> async { return FsHotWatch.Ipc.unknownCommandReply name }
      GetDiagnostics = fun _ _ -> async { return """{"count": 0, "files": {}}""" }
      WaitForScan = fun _ _ -> async { return "idle" }
      WaitForComplete = fun _ _ -> async { return "{}" }
      WaitForRest = fun _ _ -> async { return "rest" }
      TriggerBuild = fun _ -> async { return "{}" }
      FormatAll = fun _ -> async { return "formatted 0 files" }
      RerunPlugin = fun _ _ -> async { return "{}" }
      Invalidate = fun _ -> async { return "invalidated" }
      IsRunning = fun _ -> true
      LaunchDaemon = fun _ _ _ -> () }

/// Run `verdict` in `root` with `agent`, returning (stdout, stderr, exit code).
let runVerdict (root: string) (agent: bool) : string * string * int =
    let priorOut = Console.Out
    let priorErr = Console.Error
    use out = new StringWriter()
    use err = new StringWriter()
    Console.SetOut out
    Console.SetError err

    let exitCode =
        try
            executeCommand
                ""
                (fun _ -> Unchecked.defaultof<_>)
                (fakeIpc ())
                root
                "pipe-agent-envelope"
                Command.Verdict
                { defaultGlobalOptions with
                    AgentMode = Some agent }
                fakeConfig
                5.0
        finally
            Console.SetOut priorOut
            Console.SetError priorErr

    out.ToString(), err.ToString(), exitCode

[<Fact(Timeout = 15000)>]
let ``verdict in agent mode prints only the envelope: one stdout line, empty stderr`` () =
    withTempDir "agent-verdict" (fun root ->
        let stdout, stderr, exitCode = runVerdict root true
        test <@ exitCode = 5 @>
        test <@ stderr = "" @>
        let lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        test <@ lines.Length = 1 @>
        let e = parse lines.[0]
        test <@ str e "fshw" = AgentEnvelope.Contract @>
        test <@ str e "command" = "verdict" @>
        test <@ str e "outcome" = "no-verdict" @>
        test <@ num e "exit" = 5 @>
        test <@ not (List.isEmpty (strings e "reasons")) @>)

[<Fact(Timeout = 15000)>]
let ``verdict without agent mode still prints the human block (positive control)`` () =
    withTempDir "human-verdict" (fun root ->
        let stdout, stderr, exitCode = runVerdict root false
        test <@ exitCode = 5 @>
        test <@ stderr.Contains "✗" @>
        test <@ not (stdout.Contains AgentEnvelope.Contract) @>)
