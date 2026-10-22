/// AGENT MODE IS MACHINE-ONLY.
///
/// A command run in agent mode (`--agent`, `FS_HOT_WATCH_AGENT`, or `"agent": true` in
/// `.fshw.json`) prints exactly one line on stdout, the ENVELOPE below, and nothing on
/// stderr. Everything the command would have shown a person goes to
/// `.fshw/agent-output.log`, which the envelope names, so the detail is still there for
/// anyone who needs it. Earlier, `--agent` printed a large JSON document and then the
/// human block as well. That was more output than the human mode, not less.
///
/// The envelope (`Contract`), one compact JSON object:
///
///   fshw            the contract string, `fshw-agent-v1`
///   command         the verb: `check`, `confirm`, `verdict`, `status`, ...
///   exit            the process exit code
///   outcome         verdict verbs: `green` `red` `incomplete` `model-unavailable`
///                   `stale` `in-flight` `no-verdict`; with no verdict of this run to
///                   read, the exit code's meaning (`ok` `red` `incomplete` `no-verdict`
///                   `signalled` `fail`). Other verbs: `ok` or `fail`.
///   scope           verdict verbs, when known: `{kind, ran, total}`, kind `full`
///                   `filtered` `none` `unknown` `unreadable`
///   caveat          a green from an impact-filtered run: how little it covered
///   reasons         verdict verbs: why the outcome is what it is, one line each
///   reasonsOmitted  red causes the verdict counted but did not list
///   reasonsFrom     `output` when the reasons are the transcript's last lines, because
///                   no verdict of this run explained a failing exit code
///   lines           other verbs: their own output, cleaned, at most `MaxLines`
///   linesOmitted    lines past `MaxLines`
///   verdict         `.fshw/verdict.json` when the facts came from it
///   output          the transcript, `.fshw/agent-output.log`
///
/// A bump of `Contract` is required for any change that removes a field or changes a
/// field's meaning.
module FsHotWatch.Cli.AgentEnvelope

open System
open System.IO
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open FsHotWatch
open FsHotWatch.Cli.IpcParsing

/// The envelope's contract string.
[<Literal>]
let Contract = "fshw-agent-v1"

/// Where an agent-mode command's human output goes, relative to the repository root.
[<Literal>]
let TranscriptRelativePath = ".fshw/agent-output.log"

/// The most output lines an output-backed verb's envelope carries.
[<Literal>]
let MaxLines = 40

/// How many transcript lines stand in for reasons when nothing else explains a failure.
[<Literal>]
let MaxTailLines = 10

/// The longest one reason may be, in characters.
[<Literal>]
let MaxReasonChars = 300

/// The transcript's absolute path.
let transcriptPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "agent-output.log")

let private ansi = Regex(@"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled)

/// What a terminal would have shown: ANSI escapes stripped, each line cut to what its
/// last carriage return left on screen, trailing space trimmed, blank lines dropped.
let cleanLines (raw: string seq) : string list =
    raw
    |> Seq.map (fun line ->
        let plain = ansi.Replace(line, "")
        let lastReturn = plain.LastIndexOf '\r'

        let shown =
            if lastReturn >= 0 then
                plain.Substring(lastReturn + 1)
            else
                plain

        shown.TrimEnd())
    |> Seq.filter (fun line -> not (String.IsNullOrWhiteSpace line))
    |> Seq.toList

/// One reason as one bounded line.
let private oneLine (text: string) : string =
    let flat = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim()

    if flat.Length <= MaxReasonChars then
        flat
    else
        flat.Substring(0, MaxReasonChars - 1) + "…"

/// What a verdict says, reduced to what a decision needs.
type Facts =
    {
        /// The wire outcome (see the module comment).
        Outcome: string
        Scope: TestScope option
        Reasons: string list
        /// Red causes the verdict counted but did not list.
        ReasonsOmitted: int
        /// Whether the facts were read from `.fshw/verdict.json`.
        FromVerdictFile: bool
    }

/// Where the facts of a verdict verb come from.
[<RequireQualifiedAccess>]
type Evidence =
    /// `fshw verdict`: the report IS the answer, whatever it says.
    | ByVerdictVerb of Verdict.Report
    /// `check` / `confirm`: the verdict on disk after the run. It is this run's answer
    /// only when it applies to the tree on disk and records this run's exit code.
    | AfterRun of Verdict.Report

/// The facts of a verdict that applies to the tree.
let ofVerdict (v: Verdict.Verdict) : Facts =
    let outcomeReason =
        match v.Outcome with
        | Verdict.Incomplete reason
        | Verdict.ModelUnavailable reason -> [ reason ]
        | Verdict.Green _
        | Verdict.Red -> []

    let failingPlugins =
        v.Plugins
        |> List.filter (fun p -> Verdict.PluginOutcome.isFailing p.Outcome)
        |> List.map (fun p ->
            let token = Verdict.PluginOutcome.token p.Outcome

            match p.Summary with
            | Some summary when not (String.IsNullOrWhiteSpace summary) -> $"%s{p.Name}: %s{token} — %s{summary}"
            | _ -> $"%s{p.Name}: %s{token}")

    let causes =
        v.RedCauses
        |> List.map (fun c -> $"%s{c.Source} %s{c.File}: %s{Verdict.RedCauseMessage.value c.Message}")

    { Outcome = Verdict.Outcome.tag v.Outcome
      Scope = Some v.Scope
      Reasons = outcomeReason @ failingPlugins @ causes
      ReasonsOmitted = max 0 (v.RedCauseCount - List.length v.RedCauses)
      FromVerdictFile = true }

/// The facts a verdict verb's envelope reports, or `None` when no verdict answers for
/// this run (the envelope then reports the exit code's meaning).
let factsOf (evidence: Evidence) (exitCode: int) : Facts option =
    let fromReport (outcome: string) (reason: string) (fromFile: bool) =
        Some
            { Outcome = outcome
              Scope = None
              Reasons = [ reason ]
              ReasonsOmitted = 0
              FromVerdictFile = fromFile }

    match evidence with
    | Evidence.ByVerdictVerb(Verdict.Report.Applies v) -> Some(ofVerdict v)
    | Evidence.ByVerdictVerb(Verdict.Report.Stale(_, reason)) -> fromReport "stale" reason true
    | Evidence.ByVerdictVerb(Verdict.Report.InFlight(_, reason)) -> fromReport "in-flight" reason true
    | Evidence.ByVerdictVerb(Verdict.Report.NoVerdict reason) -> fromReport "no-verdict" reason false
    | Evidence.AfterRun(Verdict.Report.Applies v) when v.ExitCode = exitCode -> Some(ofVerdict v)
    | Evidence.AfterRun _ -> None

/// The meaning of an exit code when no verdict of this run says more.
let private exitOutcome (exitCode: int) : string =
    match exitCode with
    | 0 -> "ok"
    | 1 -> "red"
    | 2 -> "incomplete"
    | 3
    | 5 -> "no-verdict"
    | 4 -> "stale"
    | 6 -> "in-flight"
    | n when n > 128 -> "signalled"
    | _ -> "fail"

/// What the envelope is built from.
[<RequireQualifiedAccess>]
type Shape =
    /// `check`, `confirm`, `verdict`: the answer is a verdict. `None` when no verdict
    /// answers for this run.
    | VerdictBacked of Facts option
    /// Any other gated verb: its own output is its answer.
    | OutputBacked

let private scopeNode (scope: TestScope) : JsonNode =
    let node = JsonObject()

    let counts (kind: string) (ran: int) (total: int) =
        node["kind"] <- JsonValue.Create kind
        node["ran"] <- JsonValue.Create ran
        node["total"] <- JsonValue.Create total

    match scope with
    | FullSuite(n, _) -> counts "full" n n
    | ImpactFiltered(ran, total) -> counts "filtered" ran total
    | NoTestsRun _ -> node["kind"] <- JsonValue.Create "none"
    | ScopeUnknown -> node["kind"] <- JsonValue.Create "unknown"
    | ScopeUnreadable _ -> node["kind"] <- JsonValue.Create "unreadable"

    node

let private stringArray (items: string list) : JsonNode =
    let array = JsonArray()

    for item in items do
        array.Add(JsonValue.Create item :> JsonNode)

    array

/// The envelope for `command`, which exited `exitCode`. `transcript` holds the cleaned
/// lines the command printed; `None` when it printed nothing to a transcript (the
/// envelope then names no output file).
let render (command: string) (exitCode: int) (shape: Shape) (transcript: string list option) : string =
    let node = JsonObject()
    node["fshw"] <- JsonValue.Create Contract
    node["command"] <- JsonValue.Create command
    node["exit"] <- JsonValue.Create exitCode
    let lines = transcript |> Option.defaultValue []

    match shape with
    | Shape.VerdictBacked facts ->
        let outcome =
            facts |> Option.map _.Outcome |> Option.defaultValue (exitOutcome exitCode)

        node["outcome"] <- JsonValue.Create outcome

        match facts |> Option.bind _.Scope with
        | Some scope ->
            node["scope"] <- scopeNode scope

            match scope with
            | ImpactFiltered(ran, total) when outcome = "green" ->
                node["caveat"] <-
                    JsonValue.Create $"impact-filtered: %d{ran} of %d{total} test projects ran; not a full-suite green"
            | _ -> ()
        | None -> ()

        let reasons =
            facts |> Option.map _.Reasons |> Option.defaultValue [] |> List.map oneLine

        let fromOutput = exitCode <> 0 && List.isEmpty reasons

        let reasons =
            if fromOutput then
                lines
                |> List.skip (max 0 (List.length lines - MaxTailLines))
                |> List.map oneLine
            else
                reasons

        node["reasons"] <- stringArray reasons

        match facts with
        | Some f when f.ReasonsOmitted > 0 -> node["reasonsOmitted"] <- JsonValue.Create f.ReasonsOmitted
        | _ -> ()

        if fromOutput && not (List.isEmpty reasons) then
            node["reasonsFrom"] <- JsonValue.Create "output"

        match facts with
        | Some f when f.FromVerdictFile -> node["verdict"] <- JsonValue.Create Verdict.RelativePath
        | _ -> ()
    | Shape.OutputBacked ->
        node["outcome"] <- JsonValue.Create(if exitCode = 0 then "ok" else "fail")
        node["lines"] <- stringArray (List.truncate MaxLines lines)

        if List.length lines > MaxLines then
            node["linesOmitted"] <- JsonValue.Create(List.length lines - MaxLines)

    if transcript.IsSome then
        node["output"] <- JsonValue.Create TranscriptRelativePath

    node.ToJsonString()

/// Run `action` with stdout AND stderr sent to the transcript (overwritten), then put the
/// console back, also when `action` throws. Returns the exit code and the transcript's
/// cleaned lines.
///
/// What it cannot catch: output a child process writes straight to the inherited file
/// descriptors, and anything printed after an `exit` inside `action`.
let capture (repoRoot: string) (action: unit -> int) : int * string list =
    let path = transcriptPath repoRoot
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let priorOut = Console.Out
    let priorErr = Console.Error

    let exitCode =
        use file = new StreamWriter(path, false)
        file.AutoFlush <- true
        let writer = TextWriter.Synchronized file
        Console.SetOut writer
        Console.SetError writer

        try
            action ()
        finally
            Console.SetOut priorOut
            Console.SetError priorErr

    exitCode, cleanLines (File.ReadAllLines path)
