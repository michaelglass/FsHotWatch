/// AUTOMATION-1744: per-test rows of a test-project run, filed beside its outcome record so a
/// per-test flake rate outlives the checkout's `.fshw/test-runs`. The index format, and the
/// runner filing it through `IOutcomeStore` at the moment it files the record. Every time is a
/// value the test chose.
module FsHotWatch.Tests.TestRowsTests

open System
open System.IO
open System.IO.Compression
open System.Text
open Xunit
open Swensen.Unquote
// FsHotWatch.Core before FsHotWatch: its own `ActionKey` would otherwise shadow the store's.
open FsHotWatch.Core
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestEvidenceRun

let private t0 = DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero)

let private key =
    ActionKey.create "test" [ "kp", "inputs"; "project", "A.Tests"; "run-scope", "full" ]

let private everyOutcome =
    [ TestRows.Outcome.Passed
      TestRows.Outcome.Failed
      TestRows.Outcome.TimedOut
      TestRows.Outcome.Cancelled
      TestRows.Outcome.Skipped
      TestRows.Outcome.Other ]

let private index: TestRows.Index =
    { Run =
        { Key = key.Value
          Project = "A.Tests"
          RunId = "run-1"
          Tree = "sha256:tree"
          Tool = "fshw 1"
          Machine = "box"
          RecordedAt = t0 }
      Rows =
        { Head =
            { Name = "A.passes"
              Outcome = TestRows.Outcome.Passed
              DurationMs = 12L }
          Tail =
            everyOutcome
            |> List.mapi (fun i outcome ->
                ({ Name = $"A.t%d{i}"
                   Outcome = outcome
                   DurationMs = int64 i }
                : TestRows.Row)) } }

let private gzip (text: string) =
    use buffer = new MemoryStream()

    do
        use zip = new GZipStream(buffer, CompressionLevel.Fastest)
        let bytes = Encoding.UTF8.GetBytes text
        zip.Write(bytes, 0, bytes.Length)

    buffer.ToArray()

let private header =
    $"""{{"schema":"fshw-test-rows-v1","key":"%s{key.Value.Text}","project":"A.Tests","runId":"r","tree":"t","tool":"f","machine":"m","recordedAt":"2026-11-17T09:00:00.0000000+00:00"}}"""

let private row = """{"name":"A.x","outcome":"passed","ms":3}"""

[<Fact>]
let ``an index reads back as written: the run once, and every row with its outcome kept distinct`` () =
    test <@ TestRows.decode (TestRows.encode index) = Ok index @>

[<Fact>]
let ``the run is written once per file, not per row`` () =
    use doc = new MemoryStream(TestRows.encode index)
    use zip = new GZipStream(doc, CompressionMode.Decompress)
    use reader = new StreamReader(zip)
    let lines = reader.ReadToEnd().Split('\n') |> List.ofArray
    test <@ lines.Length = 1 + 1 + everyOutcome.Length @>
    test <@ lines |> List.filter (fun l -> l.Contains "\"runId\"") |> List.length = 1 @>
    test <@ lines |> List.exists (fun l -> l.Contains "\"outcome\":\"timedOut\"") @>
    test <@ lines |> List.exists (fun l -> l.Contains "\"outcome\":\"cancelled\"") @>

[<Fact>]
let ``every outcome token reads back as the outcome, and no other token is one`` () =
    test
        <@
            everyOutcome
            |> List.forall (fun o -> TestRows.Outcome.tryParse (TestRows.Outcome.token o) = Some o)
        @>

    test <@ TestRows.Outcome.tryParse "canceled" = None @>

[<Fact>]
let ``an index is named after the record it is filed beside`` () =
    test <@ TestRows.fileNameFor "/store/records/ab/abcd/0123.json" = "0123.tests.jsonl.gz" @>

[<Fact>]
let ``a file that is not a whole index is refused, with why`` () =
    let refused (bytes: byte[]) =
        match TestRows.decode bytes with
        | Ok _ -> None
        | Error why -> Some why

    let withHeader (edit: string -> string) (rows: string list) =
        gzip (String.concat "\n" (edit header :: rows))

    test <@ (refused (Encoding.UTF8.GetBytes "plain text")).IsSome @>
    test <@ refused (gzip "") = Some "empty" @>
    test <@ (refused (gzip "{not json")).IsSome @>
    test <@ refused (withHeader id []) = Some "no rows" @>
    test <@ refused (withHeader id [ row ]) = None @>
    test <@ refused (withHeader (fun h -> h.Replace("-v1", "-v0")) [ row ]) = Some "unknown schema fshw-test-rows-v0" @>
    test <@ refused (gzip ("[]\n" + row)) = Some "no schema" @>

    test
        <@
            refused (withHeader (fun h -> h.Replace("\"schema\":\"fshw-test-rows-v1\"", "\"schema\":1")) [ row ]) = Some
                "no schema"
        @>

    test <@ refused (withHeader (fun h -> h.Replace(key.Value.Text, "not-a-key")) [ row ]) = Some "no key" @>

    for field in [ "project"; "runId"; "tree"; "tool"; "machine" ] do
        test <@ refused (withHeader (fun h -> h.Replace($"\"%s{field}\"", "\"x\"")) [ row ]) = Some $"no %s{field}" @>

    test <@ refused (withHeader (fun h -> h.Replace("2026-11-17T09", "yesterday")) [ row ]) = Some "no recordedAt" @>
    test <@ refused (withHeader id [ row; """{"outcome":"passed","ms":3}""" ]) = Some "no name" @>
    test <@ refused (withHeader id [ """{"name":"A.x","outcome":"canceled","ms":3}""" ]) = Some "no outcome" @>
    test <@ refused (withHeader id [ """{"name":"A.x","outcome":"passed","ms":"3"}""" ]) = Some "no ms" @>
    test <@ refused (withHeader id [ """{"name":"A.x","outcome":"passed","ms":1.5}""" ]) = Some "no ms" @>

// --- the runner files the rows with the record ---

let private ctrf =
    """{"results":{"summary":{"tests":4,"passed":1,"failed":3,"pending":0,"skipped":0,"other":0},"tests":[
        {"name":"A.ok","status":"passed","duration":5},
        {"name":"A.bad","status":"failed","duration":7,"extra":{"exception":"Swensen.Unquote.AssertionFailedException"}},
        {"name":"A.slow","status":"failed","duration":15007,"extra":{"exception":"Xunit.Sdk.TestTimeoutException"}},
        {"name":"A.stopped","status":"failed","duration":9,"extra":{"exception":"System.OperationCanceledException"}}]}}"""

let private producer: TestEvidenceStore.Producer =
    { Worktree = "/w"
      RunId = "run-7"
      TreeHash = "sha256:tree"
      ProducerHash = "fshw 1"
      EarnedAt = t0 }

let private place = RunPlace.ofProducer "box" producer

let private produced (result: TestResult) (report: string option) : ProducedRun =
    { Project = "A.Tests"
      Result = result
      Report = report
      CoverageRequired = false
      Coverage = None }

let private recordOf (outcome: OutcomeStore.Outcome) : OutcomeStore.Record =
    { Key = key
      Subject = "A.Tests"
      Outcome = outcome
      Counts = None
      DurationMs = None
      Producer =
        { Binary = "fshw 1"
          Worktree = "/w"
          RunId = Some "run-7" }
      RecordedAt = t0 }

[<Fact>]
let ``a failed run's rows are filed beside its record, a timeout and a cancellation apart from a failure`` () =
    let store = OutcomeRecords.InMemoryOutcomeStore()
    let run = produced (TestsFailed("", false, TimeSpan.FromSeconds 1.0)) (Some ctrf)
    fileOutcome store key place run (recordOf OutcomeStore.Outcome.Fail) |> ignore

    let recordFile = store.FileNames key.Value |> List.exactlyOne

    match store.TestRowsUnder key.Value with
    | [ name, Ok filed ] ->
        test <@ name = TestRows.fileNameFor recordFile @>

        test
            <@
                filed.Run = { Key = key.Value
                              Project = "A.Tests"
                              RunId = "run-7"
                              Tree = "sha256:tree"
                              Tool = "fshw 1"
                              Machine = "box"
                              RecordedAt = t0 }
            @>

        test
            <@
                NonEmpty.toList filed.Rows
                |> List.map (fun r -> r.Name, r.Outcome, r.DurationMs) = [ "A.ok", TestRows.Outcome.Passed, 5L
                                                                           "A.bad", TestRows.Outcome.Failed, 7L
                                                                           "A.slow", TestRows.Outcome.TimedOut, 15007L
                                                                           "A.stopped", TestRows.Outcome.Cancelled, 9L ]
            @>
    | other -> failwith $"expected one index beside the record, got %d{other.Length}"

[<Fact>]
let ``a passing run files its rows too, and a run with no report or no rows files none`` () =
    let passing =
        """{"results":{"summary":{"tests":1,"passed":1,"failed":0,"pending":0,"skipped":0,"other":0},"tests":[{"name":"A.ok","status":"passed","duration":5}]}}"""

    let filedRows (report: string option) =
        let store = OutcomeRecords.InMemoryOutcomeStore()
        let run = produced (TestsPassed("", false, TimeSpan.FromSeconds 1.0)) report
        fileOutcome store key place run (recordOf OutcomeStore.Outcome.Pass) |> ignore
        store.TestRowsUnder key.Value |> List.length

    test <@ filedRows (Some passing) = 1 @>
    test <@ filedRows None = 0 @>
    test <@ filedRows (Some """{"results":{"tests":[]}}""") = 0 @>

[<Fact>]
let ``rows are write-once beside their record, and superseding the record drops the others' rows`` () =
    let store = OutcomeRecords.InMemoryOutcomeStore()
    let istore = store :> OutcomeRecords.IOutcomeStore

    let rows: TestRows.RunRows =
        { RunId = "run-7"
          Tree = "sha256:tree"
          Machine = "box"
          Rows = index.Rows }

    let first = OutcomeStore.justRan (recordOf OutcomeStore.Outcome.Fail) None
    test <@ istore.FileComputed first = OutcomeRecords.Filing.Filed @>
    test <@ istore.FileTestRows(first, rows) = OutcomeRecords.Filing.Filed @>
    test <@ istore.FileTestRows(first, rows) = OutcomeRecords.Filing.AlreadyFiled @>
    test <@ List.isEmpty (store.TestRowsUnder((ActionKey.create "test" [ "other", "key" ]).Value)) @>

    let later =
        OutcomeStore.toStored
            { recordOf OutcomeStore.Outcome.Pass with
                RecordedAt = t0.AddMinutes 1.0 }
            None

    istore.Supersede later |> ignore
    test <@ List.isEmpty (store.TestRowsUnder key.Value) @>

[<Fact>]
let ``a run whose record could not be filed files no rows`` () =
    TestHelpers.withTempDir "rows-unfiled" (fun dir ->
        // The store's root is a file, so nothing can be filed under it.
        let root = Path.Combine(dir, "root")
        File.WriteAllText(root, "")

        let store =
            OutcomeStore.Store(
                { Root = root
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 1_000_000L
                  Log = ignore }
            )

        let run = produced (TestsFailed("", false, TimeSpan.FromSeconds 1.0)) (Some ctrf)
        fileOutcome store key place run (recordOf OutcomeStore.Outcome.Fail) |> ignore
        test <@ (let scan = OutcomeStore.readAllTestRows root in List.isEmpty scan.Indexes && scan.Unreadable = 0) @>)

// --- end to end: a red run and a later green one through the real filing path ---

let private kp =
    TestEvidenceKey.ofLabels [ "toolchain", "t"; "closure", "A.Tests"; "scope", "verdict-tree" ]

let private candidateA: Candidate =
    { Project = "A.Tests"
      ProjectFile = "/w/tests/A.Tests/A.Tests.fsproj"
      Closure = []
      TestConfig = "{}" }

let private producerAt (runId: string) (at: DateTimeOffset) : TestEvidenceStore.Producer =
    { Worktree = "/w"
      RunId = runId
      TreeHash = "sha256:tree"
      ProducerHash = "fshw 1"
      EarnedAt = at }

/// File `run` as `publish` does: the record `outcomeRecordOf` builds, through `fileOutcome`.
let private fileThroughRunner (store: OutcomeStore.Store) (producer: TestEvidenceStore.Producer) (run: ProducedRun) =
    match outcomeRecordOf candidateA (Some(Ok kp)) (Some(Ok kp)) run producer with
    | Some record ->
        fileOutcome store (testActionKey kp "full") (RunPlace.ofProducer "box" producer) run record
        |> ignore
    | None -> failwith "the runner filed no record for this run"

[<Fact>]
let ``a red run and a later green one on the same tree and tool are reported flaky, and the red never replays`` () =
    TestHelpers.withTempDir "rows-e2e" (fun dir ->
        let root = Path.Combine(dir, "outcomes")

        let store =
            OutcomeStore.Store(
                { Root = root
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )

        let full = (testActionKey kp "full").Value

        let red =
            produced
                (TestsFailed("", false, TimeSpan.FromSeconds 1.0))
                (Some
                    """{"results":{"summary":{"tests":2,"passed":0,"failed":2,"pending":0,"skipped":0,"other":0},"tests":[
                        {"name":"A.bad","status":"failed","duration":7,"extra":{"exception":"Swensen.Unquote.AssertionFailedException"}},
                        {"name":"A.slow","status":"failed","duration":15007,"extra":{"exception":"Xunit.Sdk.TestTimeoutException"}}]}}""")

        fileThroughRunner store (producerAt "run-red" t0) red

        // The red run filed its record, and rows holding exactly its two outcomes.
        test
            <@
                store.History full |> List.map (fun r -> r.Outcome, r.Producer.RunId) = [ OutcomeStore.Outcome.Fail,
                                                                                          Some "run-red" ]
            @>

        let rowsOf (runId: string) =
            (OutcomeStore.readAllTestRows root).Indexes
            |> List.filter (fun i -> i.Run.RunId = runId)
            |> List.collect (fun i -> FsHotWatch.Core.NonEmpty.toList i.Rows)
            |> List.map (fun r -> r.Name, r.Outcome)

        test <@ rowsOf "run-red" = [ "A.bad", TestRows.Outcome.Failed; "A.slow", TestRows.Outcome.TimedOut ] @>

        // The red is never a replayable result: it does not qualify as test evidence, and a
        // green earned before it at the same K_P is refused reuse.
        test <@ qualifies (Some(Ok kp)) (Some(Ok kp)) red = Error "the run failed" @>
        test <@ (reuseRefusal (Some(store :> OutcomeRecords.IOutcomeStore)) kp (t0.AddHours -1.0)).IsSome @>

        let green =
            produced
                (TestsPassed("", false, TimeSpan.FromSeconds 1.0))
                (Some
                    """{"results":{"summary":{"tests":2,"passed":2,"failed":0,"pending":0,"skipped":0,"other":0},"tests":[
                        {"name":"A.bad","status":"passed","duration":6},
                        {"name":"A.slow","status":"passed","duration":40}]}}""")

        fileThroughRunner store (producerAt "run-green" (t0.AddHours 1.0)) green
        test <@ rowsOf "run-green" = [ "A.bad", TestRows.Outcome.Passed; "A.slow", TestRows.Outcome.Passed ] @>

        let report =
            FsHotWatch.Cli.OutcomeAnalytics.analyze
                None
                (OutcomeStore.readAll root)
                (OutcomeStore.readAllTestRows root)
                (FsHotWatch.Cli.OutcomeAnalytics.readShadowLogs [])

        test
            <@
                report.FlakyTests
                |> List.map (fun f -> f.Test, f.Outcomes, f.RedRun, f.GreenRun, f.Tree, f.Tool) = [ "A.bad",
                                                                                                    [ TestRows.Outcome.Passed,
                                                                                                      1
                                                                                                      TestRows.Outcome.Failed,
                                                                                                      1 ],
                                                                                                    "run-red",
                                                                                                    "run-green",
                                                                                                    "sha256:tree",
                                                                                                    "fshw 1"
                                                                                                    "A.slow",
                                                                                                    [ TestRows.Outcome.Passed,
                                                                                                      1
                                                                                                      TestRows.Outcome.TimedOut,
                                                                                                      1 ],
                                                                                                    "run-red",
                                                                                                    "run-green",
                                                                                                    "sha256:tree",
                                                                                                    "fshw 1" ]
            @>)
