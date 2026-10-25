/// `fshw findings`: the warm daemon's analyzer findings, as an answer about the tree on
/// disk or not at all.
///
/// The verb is a READER. It never starts a daemon, never triggers a scan, a build or an
/// analyzer run, and never answers for a tree other than the one on disk:
///
///   1. no daemon serving the worktree → `NoDaemon` (exit 5), so a caller falls back to
///      its own analyzer host instead of paying for a daemon it did not ask for;
///   2. wait until the daemon owns no work (`WaitForRest`), the settle half of `check`'s
///      wait — not its verdict half, which re-checks suspect files and waits for test
///      evidence (ADR-041);
///   3. read the analyzers plugin's stamped reply and `AnalyzerFindings.judge` it against
///      the disk: a file whose source, analyzer set or config moved since its analysis is
///      `Stale` (exit 4), an expected file with no complete analysis is `Incomplete`
///      (exit 2);
///   4. take the tree hash before the read and after the judgement, and refuse when the
///      tree moved between them.
///
/// `--wait` retries a `Stale` answer until `--timeout`: the edit the daemon has not
/// picked up yet is the common case, and it resolves once the watcher delivers it.
module FsHotWatch.Cli.Findings

open System
open System.IO
open System.Text.Json.Nodes
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.Analyzers.AnalyzerFindings

/// The JSON answer's schema. Bump it on any change a reader could misread.
[<Literal>]
let Schema = "fshw-findings-v1"

/// How long a `--wait` lets the watcher deliver a pending edit before reading again.
let RetryInterval = TimeSpan.FromSeconds 1.0

/// What `answer` reads, so the decision is testable without a daemon or a disk.
[<NoComparison; NoEquality>]
type Seams =
    {
        /// Whether a daemon is serving this worktree. Probes; never starts one.
        IsRunning: unit -> bool
        /// `WaitForRest(timeoutMs)`: `"rest"` once the daemon owns no work, anything else
        /// (`"busy: …"`) when it still did at the bound. `0` probes without waiting.
        WaitForRest: int -> string
        /// The analyzers plugin's `analyzer-findings` reply.
        ReadReply: unit -> string
        /// The content address of the tree on disk.
        TreeHash: unit -> TreeHash.Tree
        /// The disk as `judge` reads it, under the reply's scope root.
        Disk: Reply -> DiskView
        Now: unit -> DateTime
        Sleep: TimeSpan -> unit
    }

type Options =
    {
        /// Wait for the daemon to settle, and retry a stale answer, until `Timeout`.
        Wait: bool
        Timeout: TimeSpan
        /// Whether a warning below the analyzers' failure threshold still fails, as in
        /// `check` (`--no-warn-fail` turns it off).
        WarningsAreFailures: bool
    }

/// What the answer is. Only `Clean` and `Findings` carry findings.
type Outcome =
    /// About the tree on disk, and nothing in it fails.
    | Clean
    /// About the tree on disk; this many findings fail.
    | Findings of failing: int
    /// An expected file has no complete analysis.
    | Incomplete
    /// Not about the tree on disk (or not yet: the daemon is still working).
    | Stale
    /// No answer can be given: no identity for the analyzer set, a daemon that does not
    /// serve the command, a reply this build cannot read.
    | Refused
    /// No daemon serves this worktree, and this verb starts none.
    | NoDaemon

module Outcome =
    /// 0 clean, 1 failing findings, 2 incomplete, 4 stale, 5 no daemon, 6 refused. The
    /// codes `check` (2) and `verdict` (4) use for the same kinds of non-answer.
    let exitCode (outcome: Outcome) : int =
        match outcome with
        | Clean -> 0
        | Findings _ -> 1
        | Incomplete -> 2
        | Stale -> 4
        | NoDaemon -> 5
        | Refused -> 6

    let token (outcome: Outcome) : string =
        match outcome with
        | Clean -> "clean"
        | Findings _ -> "findings"
        | Incomplete -> "incomplete"
        | Stale -> "stale"
        | NoDaemon -> "no-daemon"
        | Refused -> "refused"

type Answer =
    {
        Outcome: Outcome
        /// Why the answer is what it is, one line each.
        Reasons: string list
        /// The tree the answer is about; `None` when no reply was judged.
        TreeHash: string option
        /// The reply judged, when there was one.
        Reply: Reply option
        /// The expected files' findings, only when the answer is current.
        Findings: (string * ErrorEntry) list
        /// Expected files with no complete analysis.
        Missing: string list
        /// Files whose analysis is about other bytes than the disk's.
        StaleFiles: string list
    }

let private bare (outcome: Outcome) (reasons: string list) : Answer =
    { Outcome = outcome
      Reasons = reasons
      TreeHash = None
      Reply = None
      Findings = []
      Missing = []
      StaleFiles = [] }

/// The answer when asking failed outright (the daemon went away mid-answer, a fault on
/// the socket): no reply, no findings.
let refused (reason: string) : Answer = bare Refused [ reason ]

/// The answer a judged reply gives, and whether waiting could change it (only a stale
/// one: the daemon may not have picked an edit up yet).
let private ofJudgement (warningsAreFailures: bool) (about: Answer) (judged: Judgement) : Answer * bool =
    match judged with
    | Current findings ->
        let failing =
            findings
            |> List.filter (snd >> ErrorEntry.isFailing warningsAreFailures)
            |> List.length

        { about with
            Outcome = if failing = 0 then Clean else Findings failing
            Findings = findings },
        false
    | Judgement.Stale reasons ->
        { about with
            Reasons = reasons |> List.map StaleReason.describe
            StaleFiles = reasons |> List.choose StaleReason.file },
        true
    | Judgement.Incomplete missing ->
        { about with
            Outcome = Incomplete
            Reasons =
                [ $"%d{missing.Length} expected file(s) have no complete analysis (never analyzed, a parse-only check, or a timed-out analyzer)" ]
            Missing = missing },
        false
    | Judgement.Refused why ->
        { about with
            Outcome = Refused
            Reasons = [ why ] },
        false

/// One read of a daemon at rest: the reply, judged against the disk between two tree
/// hashes. Returns the answer and whether a retry could change it.
let private readOnce (seams: Seams) (warningsAreFailures: bool) : Answer * bool =
    let before = seams.TreeHash()
    let json = seams.ReadReply()

    if Ipc.isUnknownCommandReply json then
        bare
            Refused
            [ $"the daemon serves no `%s{CommandName}` command: no analyzers are configured in .fshw.json, or the daemon was built before this verb" ],
        false
    else
        match tryParseReply json with
        | Result.Error why -> bare Refused [ why ], false
        | Result.Ok reply ->
            let judged = judge (seams.Disk reply) reply
            let after = seams.TreeHash()

            let about =
                { bare Stale [] with
                    TreeHash = Some before.Hash
                    Reply = Some reply }

            if after.Hash <> before.Hash then
                { about with
                    Reasons = [ "the tree changed while the answer was being read" ] },
                true
            else
                ofJudgement warningsAreFailures about judged

/// The answer for this worktree's daemon. See the module comment for the rules.
let answer (seams: Seams) (options: Options) : Answer =
    if not (seams.IsRunning()) then
        bare NoDaemon [ "no fshw daemon is serving this worktree, and `findings` starts none" ]
    else
        let deadline = seams.Now() + options.Timeout

        let rec attempt () =
            let bound =
                if options.Wait then
                    max 1 (int (deadline - seams.Now()).TotalMilliseconds)
                else
                    0

            match seams.WaitForRest bound with
            | "rest" ->
                match readOnce seams options.WarningsAreFailures with
                | _, true when options.Wait && seams.Now() < deadline ->
                    seams.Sleep RetryInterval
                    attempt ()
                | answered, _ -> answered
            | busy ->
                // Under `--wait` the bound was the whole remaining budget: nothing is
                // left to retry with.
                bare Stale [ $"the daemon is still working, so its answer is not about this tree yet (%s{busy})" ]

        attempt ()

let private relativeTo (repoRoot: string) (path: string) : string =
    let relative = Path.GetRelativePath(repoRoot, path)

    if
        relative.StartsWith("..", StringComparison.Ordinal)
        || Path.IsPathRooted relative
    then
        path
    else
        relative.Replace('\\', '/')

let private codeOf (entry: ErrorEntry) =
    match entry.Code with
    | Some code -> $" %s{code}"
    | None -> ""

/// One finding in the form `fsharp-analyzers` and FSharpLint print it.
let findingLine (repoRoot: string) (file: string, entry: ErrorEntry) : string =
    $"%s{relativeTo repoRoot file}(%d{entry.Line},%d{entry.Column}): %s{DiagnosticSeverity.toString entry.Severity}%s{codeOf entry}: %s{entry.Message}"

/// The answer for a person: a headline, the reasons, and each finding.
let render (repoRoot: string) (answer: Answer) : string list =
    let about =
        match answer.TreeHash, answer.Reply with
        | Some tree, Some reply ->
            let set =
                match reply.AnalyzerSet with
                | Result.Ok key -> key.Substring(0, min 12 key.Length)
                | Result.Error _ -> "unidentified"

            $" (tree %s{tree.Substring(0, min 19 tree.Length)}, %d{reply.Expected.Length} files, %d{reply.AnalyzerCount} analyzers, set %s{set})"
        | _ -> ""

    let headline =
        match answer.Outcome with
        | Clean -> $"findings: clean%s{about} — %d{answer.Findings.Length} finding(s), none failing"
        | Findings failing -> $"findings: %d{failing} failing%s{about} — %d{answer.Findings.Length} finding(s)"
        | other -> $"findings: %s{Outcome.token other}%s{about} — no answer about the tree on disk"

    [ yield headline
      for reason in answer.Reasons do
          yield $"  %s{reason}"
      for file in answer.Missing |> List.truncate 20 do
          yield $"  missing: %s{relativeTo repoRoot file}"
      for finding in answer.Findings do
          yield findingLine repoRoot finding ]

let private stringArray (values: string seq) : JsonArray =
    let array = JsonArray()

    for value in values do
        array.Add(JsonValue.Create value)

    array

/// The answer as the versioned `fshw-findings-v1` document. Paths are repo-relative.
let toJson (repoRoot: string) (answer: Answer) : string =
    let root = JsonObject()
    let rel = relativeTo repoRoot
    root["schema"] <- Schema
    root["outcome"] <- Outcome.token answer.Outcome
    root["exit"] <- Outcome.exitCode answer.Outcome
    root["reasons"] <- stringArray answer.Reasons
    root["treeHash"] <- (answer.TreeHash |> Option.toObj)
    root["treeHashAlgorithm"] <- TreeHash.Algorithm

    match answer.Reply with
    | Some reply ->
        root["modelGeneration"] <- (reply.ModelGeneration |> Option.toNullable)

        match reply.AnalyzerSet with
        | Result.Ok key -> root["analyzerSet"] <- key
        | Result.Error why ->
            root["analyzerSet"] <- null
            root["analyzerSetRefusal"] <- why

        root["analyzers"] <- reply.AnalyzerCount
        root["failOnSeverity"] <- DiagnosticSeverity.toString reply.FailOnSeverity

        let typed =
            reply.Files |> List.filter _.Analyzed.Typed |> List.map _.File |> Set.ofList

        root["expectedFiles"] <- stringArray (reply.Expected |> List.map rel)

        root["analyzedFiles"] <- stringArray (reply.Expected |> List.filter typed.Contains |> List.map rel)
    | None ->
        root["modelGeneration"] <- null
        root["analyzerSet"] <- null
        root["analyzers"] <- null
        root["failOnSeverity"] <- null
        root["expectedFiles"] <- JsonArray()
        root["analyzedFiles"] <- JsonArray()

    root["missingFiles"] <- stringArray (answer.Missing |> List.map rel)
    root["staleFiles"] <- stringArray (answer.StaleFiles |> List.map rel)

    let findings = JsonArray()

    for file, entry in answer.Findings do
        let f = JsonObject()
        f["file"] <- rel file
        f["line"] <- entry.Line
        f["column"] <- entry.Column
        f["severity"] <- DiagnosticSeverity.toString entry.Severity
        f["code"] <- (entry.Code |> Option.toObj)
        f["message"] <- entry.Message
        findings.Add(f)

    root["findings"] <- findings
    // Relaxed: the document is read by programs and people, never embedded in HTML, and
    // paths and messages stay legible instead of `\u0060`-escaped.
    root.ToJsonString(
        System.Text.Json.JsonSerializerOptions(
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )
    )
