/// AUTOMATION-1816 step 2b: the JSON forms of the nightly's references-key shadow
/// (`ReferencesShadowReport` holds the model) and the files they are kept in.
///
/// Files: `.fshw/references-shadow/<runId>.json` holds the detail (the last
/// `RetainedReports`), and `.fshw/references-shadow.jsonl` one summary line per report.
module FsHotWatch.ReferencesShadowJson

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch.ReferencesShadowReport

[<Literal>]
let ReportSchema = "fshw-references-shadow-v1"

[<Literal>]
let SummarySchema = "fshw-references-shadow-summary-v1"

module private Write =
    let finding (f: Finding) : JsonNode =
        let o = JsonObject()

        match f.Code with
        | Some code -> o["code"] <- code
        | None -> ()

        o["line"] <- f.Line
        o["column"] <- f.Column
        o["severity"] <- f.Severity
        o["message"] <- f.Message
        o

    let array (items: JsonNode list) : JsonNode =
        let a = JsonArray()

        for item in items do
            a.Add item

        a

    let counts (c: Counts) : JsonNode =
        let o = JsonObject()
        o["equal"] <- c.Equal
        o["mismatch"] <- c.Mismatch
        o["unverifiable"] <- c.Unverifiable
        o["sameKey"] <- c.SameKey
        o

    let fileOutcome (file: string, outcome: FileOutcome) : JsonNode =
        let o = JsonObject()
        o["file"] <- file

        match outcome with
        | FileOutcome.Equal -> o["outcome"] <- "equal"
        | FileOutcome.Mismatch(reused, fresh) ->
            o["outcome"] <- "mismatch"
            o["reused"] <- array (reused |> List.map finding)
            o["fresh"] <- array (fresh |> List.map finding)
        | FileOutcome.Unverifiable reason ->
            o["outcome"] <- "unverifiable"
            o["reason"] <- UnverifiableReason.token reason
        | FileOutcome.SameKey -> o["outcome"] <- "sameKey"

        o

    let spotOutcome (file: string, outcome: SpotOutcome) : JsonNode =
        let o = JsonObject()
        o["file"] <- file

        match outcome with
        | SpotOutcome.Equal -> o["outcome"] <- "equal"
        | SpotOutcome.Mismatch(cached, recomputed) ->
            o["outcome"] <- "mismatch"
            o["cached"] <- array (cached |> List.map finding)
            o["recomputed"] <- array (recomputed |> List.map finding)
        | SpotOutcome.NotRecomputed reason ->
            o["outcome"] <- "notRecomputed"
            o["reason"] <- reason

        o

    let spotOutcomes (outcomes: (string * SpotOutcome) list) : JsonNode =
        array (outcomes |> List.map spotOutcome)

    let spotChecks (s: SpotChecks) : JsonNode =
        let o = JsonObject()
        o["seed"] <- s.Seed
        o["analyzers"] <- spotOutcomes s.Analyzers
        o["lint"] <- spotOutcomes s.Lint
        o

    let spotCounts (c: SpotCheckCounts) : JsonNode =
        let o = JsonObject()
        o["sampled"] <- c.Sampled
        o["equal"] <- c.Equal
        o["mismatched"] <- array (c.Mismatched |> List.map (fun file -> JsonValue.Create file :> JsonNode))
        o["notRecomputed"] <- c.NotRecomputed
        o

    let spotSummary (s: SpotCheckSummary) : JsonNode =
        let o = JsonObject()
        o["seed"] <- s.Seed
        o["analyzers"] <- spotCounts s.Analyzers
        o["lint"] <- spotCounts s.Lint
        o

    let plugin (p: PluginComparison) : JsonNode =
        let o = JsonObject()
        o["plugin"] <- p.Plugin
        o["counts"] <- counts (PluginComparison.counts p)
        o["files"] <- array (p.Files |> List.map fileOutcome)
        o

    let testFinding (t: TestFinding) : JsonNode =
        let o = JsonObject()
        o["test"] <- t.Test
        o["watermark"] <- TestOutcome.token t.Watermark
        o["nightly"] <- TestOutcome.token t.Nightly
        o

    let impactDbCopy (o: JsonObject) (copy: ImpactDbCopy) =
        match copy with
        | ImpactDbCopy.NotCopied -> ()
        | ImpactDbCopy.Copied(bytes, milliseconds) ->
            o["impactDbCopyBytes"] <- bytes
            o["impactDbCopyMs"] <- milliseconds

    let testPrune (section: TestPruneSection) : JsonNode =
        let o = JsonObject()

        match section with
        | TestPruneSection.NotCompared reason ->
            o["result"] <- "notCompared"
            o["reason"] <- reason
        | TestPruneSection.Invalid reason ->
            o["result"] <- "invalid"
            o["reason"] <- reason
        | TestPruneSection.Compared tp ->
            o["result"] <- "compared"
            o["differingAnalyses"] <- tp.DifferingAnalyses
            o["unverifiableAnalyses"] <- tp.UnverifiableAnalyses
            o["outsideSPrimeFailedOrChanged"] <- tp.OutsideSPrimeFailedOrChanged
            o["attributable"] <- array (tp.Attributable |> List.map testFinding)
            o["selectorMiss"] <- array (tp.SelectorMiss |> List.map testFinding)
            o["flakyChanged"] <- array (tp.FlakyChanged |> List.map testFinding)
            impactDbCopy o tp.ImpactDbCopy

        o

    let header (schema: string) (runId: string) (writtenAt: DateTimeOffset) =
        let o = JsonObject()
        o["schema"] <- schema
        o["runId"] <- runId
        o["writtenAt"] <- writtenAt.ToString("O")
        o

    let hostMismatch (o: JsonObject) (host: string) (landHost: string) =
        o["result"] <- "hostMismatch"
        o["host"] <- host
        o["landHost"] <- landHost

/// Thrown by the readers below on a shape they do not accept; caught by `tryOfJson`.
exception private MalformedException of string

module private Read =
    let prop (e: JsonElement) (name: string) : JsonElement =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v -> v
            | false, _ -> raise (MalformedException $"no %s{name}")
        | _ -> raise (MalformedException $"%s{name}: not an object")

    let str (e: JsonElement) (name: string) : string =
        let v = prop e name

        match v.ValueKind with
        | JsonValueKind.String -> v.ToString()
        | _ -> raise (MalformedException $"%s{name}: not a string")

    let int (e: JsonElement) (name: string) = (prop e name).GetInt32()
    let int64 (e: JsonElement) (name: string) = (prop e name).GetInt64()

    let items (e: JsonElement) (name: string) =
        (prop e name).EnumerateArray() |> List.ofSeq

    let parsed (what: string) (value: 'a option) : 'a =
        match value with
        | Some v -> v
        | None -> raise (MalformedException $"unknown %s{what}")

    let finding (e: JsonElement) : Finding =
        { Code =
            match e.TryGetProperty "code" with
            | true, v -> Some(v.ToString())
            | false, _ -> None
          Line = int e "line"
          Column = int e "column"
          Severity = str e "severity"
          Message = str e "message" }

    let counts (e: JsonElement) : Counts =
        { Equal = int e "equal"
          Mismatch = int e "mismatch"
          Unverifiable = int e "unverifiable"
          SameKey = int e "sameKey" }

    let fileOutcome (e: JsonElement) : string * FileOutcome =
        let outcome =
            match str e "outcome" with
            | "equal" -> FileOutcome.Equal
            | "mismatch" ->
                FileOutcome.Mismatch(items e "reused" |> List.map finding, items e "fresh" |> List.map finding)
            | "unverifiable" ->
                FileOutcome.Unverifiable(UnverifiableReason.tryParse (str e "reason") |> parsed "reason")
            | "sameKey" -> FileOutcome.SameKey
            | other -> raise (MalformedException $"outcome %s{other}")

        str e "file", outcome

    let spotOutcome (e: JsonElement) : string * SpotOutcome =
        let outcome =
            match str e "outcome" with
            | "equal" -> SpotOutcome.Equal
            | "mismatch" ->
                SpotOutcome.Mismatch(items e "cached" |> List.map finding, items e "recomputed" |> List.map finding)
            | "notRecomputed" -> SpotOutcome.NotRecomputed(str e "reason")
            | other -> raise (MalformedException $"spot-check outcome %s{other}")

        str e "file", outcome

    let spotChecks (e: JsonElement) : SpotChecks =
        { Seed = int e "seed"
          Analyzers = items e "analyzers" |> List.map spotOutcome
          Lint = items e "lint" |> List.map spotOutcome }

    let spotCounts (e: JsonElement) : SpotCheckCounts =
        { Sampled = int e "sampled"
          Equal = int e "equal"
          Mismatched = items e "mismatched" |> List.map (fun file -> file.GetString())
          NotRecomputed = int e "notRecomputed" }

    let spotSummary (e: JsonElement) : SpotCheckSummary =
        { Seed = int e "seed"
          Analyzers = spotCounts (prop e "analyzers")
          Lint = spotCounts (prop e "lint") }

    let plugin (e: JsonElement) : PluginComparison =
        { Plugin = str e "plugin"
          Files = items e "files" |> List.map fileOutcome }

    let impactDbCopy (e: JsonElement) : ImpactDbCopy =
        match e.TryGetProperty "impactDbCopyBytes" with
        | true, _ -> ImpactDbCopy.Copied(int64 e "impactDbCopyBytes", int64 e "impactDbCopyMs")
        | false, _ -> ImpactDbCopy.NotCopied

    let testFinding (e: JsonElement) : TestFinding =
        { Test = str e "test"
          Watermark = TestOutcome.tryParse (str e "watermark") |> parsed "outcome"
          Nightly = TestOutcome.tryParse (str e "nightly") |> parsed "outcome" }

    let testPrune (e: JsonElement) : TestPruneSection =
        match str e "result" with
        | "notCompared" -> TestPruneSection.NotCompared(str e "reason")
        | "invalid" -> TestPruneSection.Invalid(str e "reason")
        | "compared" ->
            TestPruneSection.Compared
                { DifferingAnalyses = int e "differingAnalyses"
                  UnverifiableAnalyses = int e "unverifiableAnalyses"
                  OutsideSPrimeFailedOrChanged = int e "outsideSPrimeFailedOrChanged"
                  Attributable = items e "attributable" |> List.map testFinding
                  SelectorMiss = items e "selectorMiss" |> List.map testFinding
                  FlakyChanged = items e "flakyChanged" |> List.map testFinding
                  ImpactDbCopy = impactDbCopy e }
        | other -> raise (MalformedException $"test-prune result %s{other}")

    /// `read root` for a document of `schema`; None for anything else.
    // MGA-ERROR-REPORT-001:ok — a line or file that does not parse is a normal input (an older
    // or damaged file) and is read as nothing; it decides nothing.
    let document (schema: string) (text: string) (read: JsonElement -> 'a) : 'a option =
        try
            using (JsonDocument.Parse text) (fun doc ->
                let root = doc.RootElement

                if root.ValueKind <> JsonValueKind.Object then None
                elif str root "schema" <> schema then None
                else Some(read root))
        with
        | MalformedException _
        | :? JsonException
        | :? InvalidOperationException
        | :? FormatException -> None

    let writtenAt (root: JsonElement) =
        DateTimeOffset.Parse(str root "writtenAt", Globalization.CultureInfo.InvariantCulture)

module Report =
    let toJson (report: Report) : string =
        let o = Write.header ReportSchema report.RunId report.WrittenAt

        match report.Result with
        | HostMismatch(host, landHost) -> Write.hostMismatch o host landHost
        | Compared c ->
            o["result"] <- "compared"
            o["servable"] <- c.Servable
            o["analyzers"] <- Write.plugin c.Analyzers
            o["lint"] <- Write.plugin c.Lint
            o["testPrune"] <- Write.testPrune c.TestPrune
            o["spotChecks"] <- Write.spotChecks c.SpotChecks

        o.ToJsonString()

    let tryOfJson (text: string) : Report option =
        Read.document ReportSchema text (fun root ->
            let result =
                match Read.str root "result" with
                | "hostMismatch" -> HostMismatch(Read.str root "host", Read.str root "landHost")
                | "compared" ->
                    Compared
                        { Servable = Read.int root "servable"
                          Analyzers = Read.plugin (Read.prop root "analyzers")
                          Lint = Read.plugin (Read.prop root "lint")
                          TestPrune = Read.testPrune (Read.prop root "testPrune")
                          SpotChecks = Read.spotChecks (Read.prop root "spotChecks") }
                | other -> raise (MalformedException $"result %s{other}")

            { RunId = Read.str root "runId"
              WrittenAt = Read.writtenAt root
              Result = result })

module Summary =
    let toJson (summary: Summary) : string =
        let o = Write.header SummarySchema summary.RunId summary.WrittenAt

        match summary.Result with
        | SummaryResult.HostMismatch(host, landHost) -> Write.hostMismatch o host landHost
        | SummaryResult.Compared c ->
            o["result"] <- "compared"
            o["servable"] <- c.Servable
            o["analyzers"] <- Write.counts c.Analyzers
            o["lint"] <- Write.counts c.Lint
            let tp = JsonObject()

            match c.TestPrune with
            | TestPruneSummary.NotCompared reason ->
                tp["result"] <- "notCompared"
                tp["reason"] <- reason
            | TestPruneSummary.Invalid reason ->
                tp["result"] <- "invalid"
                tp["reason"] <- reason
            | TestPruneSummary.Compared t ->
                tp["result"] <- "compared"
                tp["differingAnalyses"] <- t.DifferingAnalyses
                tp["unverifiableAnalyses"] <- t.UnverifiableAnalyses
                tp["outsideSPrimeFailedOrChanged"] <- t.OutsideSPrimeFailedOrChanged
                tp["attributable"] <- t.Attributable
                tp["selectorMiss"] <- t.SelectorMiss
                tp["flakyChanged"] <- t.FlakyChanged
                Write.impactDbCopy tp t.ImpactDbCopy

            o["testPrune"] <- tp
            o["spotChecks"] <- Write.spotSummary c.SpotChecks

        o.ToJsonString()

    let tryOfJson (text: string) : Summary option =
        Read.document SummarySchema text (fun root ->
            let result =
                match Read.str root "result" with
                | "hostMismatch" -> SummaryResult.HostMismatch(Read.str root "host", Read.str root "landHost")
                | "compared" ->
                    let tp = Read.prop root "testPrune"

                    SummaryResult.Compared
                        { Servable = Read.int root "servable"
                          Analyzers = Read.counts (Read.prop root "analyzers")
                          Lint = Read.counts (Read.prop root "lint")
                          SpotChecks = Read.spotSummary (Read.prop root "spotChecks")
                          TestPrune =
                            match Read.str tp "result" with
                            | "notCompared" -> TestPruneSummary.NotCompared(Read.str tp "reason")
                            | "invalid" -> TestPruneSummary.Invalid(Read.str tp "reason")
                            | "compared" ->
                                TestPruneSummary.Compared
                                    { DifferingAnalyses = Read.int tp "differingAnalyses"
                                      UnverifiableAnalyses = Read.int tp "unverifiableAnalyses"
                                      OutsideSPrimeFailedOrChanged = Read.int tp "outsideSPrimeFailedOrChanged"
                                      Attributable = Read.int tp "attributable"
                                      SelectorMiss = Read.int tp "selectorMiss"
                                      FlakyChanged = Read.int tp "flakyChanged"
                                      ImpactDbCopy = Read.impactDbCopy tp }
                            | other -> raise (MalformedException $"test-prune result %s{other}") }
                | other -> raise (MalformedException $"result %s{other}")

            { RunId = Read.str root "runId"
              WrittenAt = Read.writtenAt root
              Result = result })

// --- the commands the nightly asks -------------------------------------------------------

/// The daemon's own command: the files the references key would have served since it started
/// (`ServableFile`), as `Servable.toJson` writes them.
[<Literal>]
let ServableCommand = "references-shadow-servable"

/// The analyzers plugin's command: given `Servable.toJson`'s list, what reuse would have handed it
/// for each file against what this run found (`PluginReply`).
[<Literal>]
let AnalyzersCommand = "references-shadow-analyzers"

/// The lint plugin's command: as `AnalyzersCommand`, for lint.
[<Literal>]
let LintCommand = "references-shadow-lint"

/// `Servable.toJson`'s schema.
[<Literal>]
let ServableSchema = "fshw-references-shadow-servable-v1"

/// `PluginReply.toJson`'s schema.
[<Literal>]
let PluginReplySchema = "fshw-references-shadow-plugin-v1"

/// The servable files as the daemon hands them to the plugins.
module Servable =
    let toJson (files: ServableFile list) : string =
        let o = JsonObject()
        o["schema"] <- ServableSchema

        o["files"] <-
            Write.array (
                files
                |> List.map (fun f ->
                    let row = JsonObject()
                    row["file"] <- f.File
                    row["project"] <- f.Project
                    row["fcsSignature"] <- f.FcsSignature

                    match f.UsedSignatures with
                    | UsedSignaturesKey.Described hash -> row["usedSignatures"] <- hash
                    | UsedSignaturesKey.Undescribed -> ()

                    row :> JsonNode)
            )

        o.ToJsonString()

    let tryOfJson (text: string) : ServableFile list option =
        Read.document ServableSchema text (fun root ->
            Read.items root "files"
            |> List.map (fun e ->
                ({ File = Read.str e "file"
                   Project = Read.str e "project"
                   FcsSignature = Read.str e "fcsSignature"
                   UsedSignatures =
                     match e.TryGetProperty "usedSignatures" with
                     | true, _ -> UsedSignaturesKey.Described(Read.str e "usedSignatures")
                     | false, _ -> UsedSignaturesKey.Undescribed }
                : ServableFile)))

/// One plugin's comparison as its command replies with it.
module PluginReply =
    let toJson (comparison: PluginComparison) : string =
        let o = Write.plugin comparison
        o["schema"] <- PluginReplySchema
        o.ToJsonString()

    let tryOfJson (text: string) : PluginComparison option =
        Read.document PluginReplySchema text Read.plugin

/// The analyzers plugin's spot-check command: given `SpotCheckRequest`'s files (sampled from its
/// `sameKey` files), each one's result recomputed with the plugin's cache bypassed against the result
/// this run served (`SpotCheckReply`). Nothing recomputed is reported, filed or served.
[<Literal>]
let AnalyzersSpotCheckCommand = "references-shadow-analyzers-spot-check"

/// The lint plugin's spot-check command: as `AnalyzersSpotCheckCommand`, for lint.
[<Literal>]
let LintSpotCheckCommand = "references-shadow-lint-spot-check"

/// `SpotCheckRequest.toJson`'s schema.
[<Literal>]
let SpotCheckRequestSchema = "fshw-references-shadow-spot-check-request-v1"

/// `SpotCheckReply.toJson`'s schema.
[<Literal>]
let SpotCheckReplySchema = "fshw-references-shadow-spot-check-v1"

/// The sampled files a plugin is asked to recompute, by repository-relative path.
module SpotCheckRequest =
    let toJson (files: string list) : string =
        let o = JsonObject()
        o["schema"] <- SpotCheckRequestSchema
        o["files"] <- Write.array (files |> List.map (fun f -> JsonValue.Create f :> JsonNode))
        o.ToJsonString()

    let tryOfJson (text: string) : string list option =
        Read.document SpotCheckRequestSchema text (fun root ->
            Read.items root "files" |> List.map (fun e -> e.ToString()))

/// A plugin's spot-check, as its command replies with it.
module SpotCheckReply =
    let toJson (outcomes: (string * SpotOutcome) list) : string =
        let o = JsonObject()
        o["schema"] <- SpotCheckReplySchema
        o["files"] <- Write.spotOutcomes outcomes
        o.ToJsonString()

    let tryOfJson (text: string) : (string * SpotOutcome) list option =
        Read.document SpotCheckReplySchema text (fun root -> Read.items root "files" |> List.map Read.spotOutcome)

/// The test-prune plugin's command: given a `TestPruneRequest`, the selections S and S′
/// (`SelectionsReply`).
[<Literal>]
let TestPruneCommand = "references-shadow-test-prune"

/// `TestPruneRequest.toJson`'s schema.
[<Literal>]
let TestPruneRequestSchema = "fshw-references-shadow-test-prune-request-v1"

/// `SelectionsReply.toJson`'s schema.
[<Literal>]
let SelectionsSchema = "fshw-references-shadow-selections-v2"

/// What the test-prune plugin is asked: the run (whose temporary directory holds the impact-DB
/// copy), C, and the servable files.
type TestPruneRequest =
    { RunId: string
      Changed: string list
      Files: ServableFile list }

module TestPruneRequest =
    let toJson (request: TestPruneRequest) : string =
        let o = JsonObject()
        o["schema"] <- TestPruneRequestSchema
        o["runId"] <- request.RunId
        o["changed"] <- Write.array (request.Changed |> List.map (fun s -> JsonValue.Create s :> JsonNode))
        o["files"] <- JsonNode.Parse(Servable.toJson request.Files)
        o.ToJsonString()

    let tryOfJson (text: string) : TestPruneRequest option =
        Read.document TestPruneRequestSchema text (fun root ->
            { RunId = Read.str root "runId"
              Changed = Read.items root "changed" |> List.map (fun e -> e.ToString())
              Files =
                match Servable.tryOfJson ((Read.prop root "files").GetRawText()) with
                | Some files -> files
                | None -> raise (MalformedException "files") })

module SelectionsReply =
    let private names (set: Set<string>) =
        Write.array (set |> Set.toList |> List.map (fun s -> JsonValue.Create s :> JsonNode))

    let toJson (answer: SelectionsAnswer) : string =
        let o = JsonObject()
        o["schema"] <- SelectionsSchema

        match answer with
        | SelectionsAnswer.Refused reason ->
            o["result"] <- "refused"
            o["reason"] <- reason
        | SelectionsAnswer.Selected selections ->
            o["result"] <- "selected"
            o["s"] <- names selections.S
            o["sPrime"] <- names selections.SPrime
            o["differingAnalyses"] <- selections.DifferingAnalyses
            o["unverifiableAnalyses"] <- selections.UnverifiableAnalyses
            Write.impactDbCopy o selections.Copy

        o.ToJsonString()

    let tryOfJson (text: string) : SelectionsAnswer option =
        Read.document SelectionsSchema text (fun root ->
            let names (name: string) =
                Read.items root name |> List.map (fun e -> e.ToString()) |> Set.ofList

            match Read.str root "result" with
            | "refused" -> SelectionsAnswer.Refused(Read.str root "reason")
            | "selected" ->
                SelectionsAnswer.Selected
                    { S = names "s"
                      SPrime = names "sPrime"
                      DifferingAnalyses = Read.int root "differingAnalyses"
                      UnverifiableAnalyses = Read.int root "unverifiableAnalyses"
                      Copy = Read.impactDbCopy root }
            | other -> raise (MalformedException $"selections result %s{other}"))

// --- files -------------------------------------------------------------------------------

/// How many detailed reports are kept.
[<Literal>]
let RetainedReports = 30

let reportsDirectory (repoRoot: string) =
    Path.Combine(FsHwPaths.root repoRoot, "references-shadow")

let summaryPath (repoRoot: string) =
    Path.Combine(FsHwPaths.root repoRoot, "references-shadow.jsonl")

/// File `report`'s detail and its summary line, then keep only the newest `RetainedReports`
/// reports (by when each was written). A diagnostic, never load-bearing: a write that fails is
/// logged and changes no exit code or verdict.
// MGA-ERROR-REPORT-001:ok — the report is measurement; failing to write it must not fail the
// confirm that produced it.
let write (repoRoot: string) (report: Report) : unit =
    try
        let dir = reportsDirectory repoRoot
        Directory.CreateDirectory dir |> ignore
        let path = Path.Combine(dir, report.RunId + ".json")
        let temporary = path + ".tmp"
        File.WriteAllText(temporary, Report.toJson report)
        File.Move(temporary, path, true)
        File.AppendAllText(summaryPath repoRoot, Summary.toJson (Summary.ofReport report) + "\n")

        let dated =
            Directory.GetFiles(dir, "*.json")
            |> Array.map (fun file ->
                let writtenAt =
                    File.ReadAllText file
                    |> Report.tryOfJson
                    |> Option.map _.WrittenAt
                    |> Option.defaultValue DateTimeOffset.MinValue

                file, writtenAt)
            |> Array.sortByDescending snd

        for file, _ in dated |> Array.skip (min RetainedReports dated.Length) do
            File.Delete file
    with ex ->
        Logging.warn "references-shadow" $"report %s{report.RunId} not written: %s{ex.Message}"

/// Every summary line, oldest first; lines that do not parse are skipped. None before the first report.
let readSummaries (repoRoot: string) : Summary list =
    let path = summaryPath repoRoot

    if File.Exists path then
        File.ReadAllLines path |> Array.choose Summary.tryOfJson |> List.ofArray
    else
        []
