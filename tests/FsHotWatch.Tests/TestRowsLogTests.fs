/// AUTOMATION-1744: the daily test-row log. Each run's per-test rows are written, once, to a file of
/// their own, `<store root>/test-rows/<yyyy-mm-dd>.<run>.<project>.jsonl.gz`, at the moment its index is filed
/// beside its record, from the same index. The log lives outside the evictable keys, under its
/// own TTL and byte cap, so a flake outlives the record that exposed it. Every time is a value
/// the test chose.
module FsHotWatch.Tests.TestRowsLogTests

open System
open System.IO
open System.IO.Compression
open System.Text
open Xunit
open Swensen.Unquote
// FsHotWatch.Core before FsHotWatch: its own `ActionKey` would otherwise shadow the store's.
open FsHotWatch.Core
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero)

let private keyFor (tree: string) =
    ActionKey.create "test" [ "tree", tree; "project", "tests/A/A.fsproj"; "scope", "full" ]

let private rowsOf (runId: string) (rows: (string * TestRows.Outcome) list) : TestRows.RunRows =
    { RunId = runId
      Tree = "sha256:tree"
      Machine = "box"
      Rows =
        (rows
         |> List.mapi (fun i (name, outcome) ->
             ({ Name = name
                Outcome = outcome
                DurationMs = int64 (i + 1) }
             : TestRows.Row))
         |> NonEmpty.ofList)
            .Value }

let private indexOf (runId: string) (at: DateTimeOffset) (rows: (string * TestRows.Outcome) list) : TestRows.Index =
    { Run =
        { Key = (keyFor "sha256:tree").Value
          Project = "tests/A/A.fsproj"
          RunId = runId
          Tree = "sha256:tree"
          Tool = "fshw-1"
          Machine = "box"
          RecordedAt = at }
      Rows = (rowsOf runId rows).Rows }

let private logDir (root: string) =
    Path.Combine(root, TestRowsLog.DirectoryName)

let private logFiles (root: string) =
    if Directory.Exists(logDir root) then
        Directory.GetFiles(logDir root)
        |> Array.map Path.GetFileName
        |> Array.sort
        |> List.ofArray
    else
        []

let private gzipText (text: string) =
    use buffer = new MemoryStream()

    do
        use z = new GZipStream(buffer, CompressionLevel.Fastest)
        let b = Encoding.UTF8.GetBytes text
        z.Write(b, 0, b.Length)

    buffer.ToArray()

let private gunzipText (bytes: byte[]) =
    use reader =
        new StreamReader(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress), Encoding.UTF8)

    reader.ReadToEnd()

/// The 12-hex token a file name carries for `text`.
let private tokenOf (text: string) =
    (ContentHash.ofText text).Substring(0, 12)

let private projectA = tokenOf "tests/A/A.fsproj"

// --- the codec: one file per run, one line per row, each line naming its run ---

[<Fact>]
let ``a run's log file decodes back to the index it was encoded from`` () =
    let index =
        indexOf "run-1" t0 [ "A.one", TestRows.Outcome.Passed; "A.two", TestRows.Outcome.TimedOut ]

    test
        <@
            TestRows.decodeLog (TestRows.encodeLog index) = Ok
                { Runs = [ index ]
                  UnreadableLines = 0 }
        @>

[<Fact>]
let ``every line of a log file carries the run's header fields beside the row's own`` () =
    let index =
        indexOf "run-1" t0 [ "A.one", TestRows.Outcome.Passed; "A.two", TestRows.Outcome.Failed ]

    let text = gunzipText (TestRows.encodeLog index)

    // Every line is ended: a file whose last line is not was cut short.
    test <@ text.EndsWith "\n" @>
    let lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    test <@ lines.Length = 2 @>

    for line in lines do
        use doc = Text.Json.JsonDocument.Parse line
        let o = doc.RootElement

        let fields =
            [ "schema"
              "key"
              "project"
              "runId"
              "tree"
              "tool"
              "machine"
              "recordedAt"
              "name"
              "outcome"
              "ms" ]
            |> List.forall (fun f -> fst (o.TryGetProperty f))

        test <@ fields @>
        let schema = o.GetProperty("schema").GetString()
        test <@ schema = TestRows.LogSchema @>

[<Fact>]
let ``a torn, empty or non-gzip file is unreadable as a whole`` () =
    let bytes =
        TestRows.encodeLog (indexOf "run-1" t0 [ "A.one", TestRows.Outcome.Failed; "A.two", TestRows.Outcome.Passed ])

    test <@ Result.isError (TestRows.decodeLog (Array.take (bytes.Length / 2) bytes)) @>
    test <@ Result.isError (TestRows.decodeLog (Encoding.UTF8.GetBytes "not gzip")) @>
    test <@ Result.isError (TestRows.decodeLog (gzipText "")) @>
    // Whole gzip, but its last line was cut short.
    let cut = gunzipText bytes
    test <@ Result.isError (TestRows.decodeLog (gzipText (cut.Substring(0, cut.Length - 5)))) @>

[<Fact>]
let ``a line that is not a log line is counted unreadable, and the others read`` () =
    let index = indexOf "run-1" t0 [ "A.one", TestRows.Outcome.Passed ]

    // Another schema; a row with no outcome; not JSON.
    let text =
        gunzipText (TestRows.encodeLog index)
        + "{\"schema\":\"fshw-test-rows-v1\",\"name\":\"x\"}\n"
        + "{\"schema\":\""
        + TestRows.LogSchema
        + "\",\"key\":\"x\"}\n"
        + "not json\n"

    test
        <@
            TestRows.decodeLog (gzipText text) = Ok
                { Runs = [ index ]
                  UnreadableLines = 3 }
        @>

// --- naming: one file per run and project, under its recorded UTC day ---

[<Fact>]
let ``a run's file is named by its recorded UTC day, its run id and its project`` () =
    let late = DateTimeOffset(2026, 11, 17, 23, 30, 0, TimeSpan.FromHours -2.0) // 2026-11-18 01:30Z

    let name =
        TestRowsLog.fileNameOf (indexOf "0f3a9c" late [ "A.one", TestRows.Outcome.Passed ])

    test <@ name = $"2026-11-18.0f3a9c.{projectA}.jsonl.gz" @>

[<Fact>]
let ``a run id that is not plain letters and digits is named by its hash`` () =
    let name =
        TestRowsLog.fileNameOf (indexOf "../run-1" t0 [ "A.one", TestRows.Outcome.Passed ])

    let run = tokenOf "../run-1"
    test <@ name = $"2026-11-17.sha-{run}.{projectA}.jsonl.gz" @>

[<Fact>]
let ``one run id in two projects names two files`` () =
    let a = indexOf "r1" t0 [ "A.one", TestRows.Outcome.Passed ]

    let b =
        { a with
            Run =
                { a.Run with
                    Project = "tests/B/B.fsproj" } }

    test <@ TestRowsLog.fileNameOf a <> TestRowsLog.fileNameOf b @>

// --- reading the log: each file on its own ---

let private writeRunFile (root: string) (index: TestRows.Index) =
    Directory.CreateDirectory(logDir root) |> ignore
    let path = Path.Combine(logDir root, TestRowsLog.fileNameOf index)
    File.WriteAllBytes(path, TestRows.encodeLog index)
    path

[<Fact>]
let ``a torn or garbage file loses only its own run: the others read, and it is counted`` () =
    withTempDir "rows-log-torn" (fun root ->
        let first = indexOf "r1" t0 [ "A.one", TestRows.Outcome.Failed ]
        let second = indexOf "r2" (t0.AddHours 1.0) [ "A.one", TestRows.Outcome.Passed ]
        writeRunFile root first |> ignore
        writeRunFile root second |> ignore

        let tornPath =
            writeRunFile root (indexOf "r3" t0 [ "A.one", TestRows.Outcome.Passed ])

        let whole = File.ReadAllBytes tornPath
        File.WriteAllBytes(tornPath, Array.take (whole.Length / 2) whole)
        File.WriteAllText(Path.Combine(logDir root, "2026-11-17.r4.0123456789ab.jsonl.gz"), "not gzip")
        // Not named as a run's: neither read nor counted.
        File.WriteAllText(Path.Combine(logDir root, "notes.txt"), "x")
        let read = TestRowsLog.read root
        test <@ read.Runs |> List.sortBy _.Run.RunId = [ first; second ] @>
        test <@ read.UnreadableFiles = 2 && read.UnreadableLines = 0 @>)

[<Fact>]
let ``reading a store with no log reads nothing; a file that cannot be opened is counted`` () =
    withTempDir "rows-log-read" (fun root ->
        test
            <@
                TestRowsLog.read root = { Runs = []
                                          UnreadableFiles = 0
                                          UnreadableLines = 0 }
            @>

        let path = writeRunFile root (indexOf "r1" t0 [ "A.one", TestRows.Outcome.Passed ])
        let readable = File.GetUnixFileMode path
        File.SetUnixFileMode(path, UnixFileMode.None)

        try
            test
                <@
                    TestRowsLog.read root = { Runs = []
                                              UnreadableFiles = 1
                                              UnreadableLines = 0 }
                @>
        finally
            File.SetUnixFileMode(path, readable))

// --- retention: whole day files, by age first, then oldest beyond the cap ---

let private writeDay (root: string) (name: string) (size: int) =
    Directory.CreateDirectory(logDir root) |> ignore
    File.WriteAllBytes(Path.Combine(logDir root, name), Array.zeroCreate size)

[<Fact>]
let ``retention deletes every day file older than the ttl, by its day, and nothing else`` () =
    withTempDir "rows-log-ttl" (fun root ->
        for name in
            [ "2026-08-18.r1.0123456789ab.jsonl.gz"
              "2026-08-18.r0.0123456789ab.jsonl.gz"
              "2026-08-19.r1.0123456789ab.jsonl.gz"
              "2026-11-17.r1.0123456789ab.jsonl.gz"
              // A file not named as one run's: not the log's, never deleted.
              "2026-08-18.jsonl.gz"
              "garbage.jsonl.gz"
              "notes.txt" ] do
            writeDay root name 10

        let retention: TestRowsLog.Retention =
            { Ttl = TimeSpan.FromDays 90.0
              MaxBytes = 1_000_000L }

        // 90 days before 2026-11-17 09:00Z is 2026-08-19 09:00Z: 08-18 is past it, 08-19 is not.
        let collected = TestRowsLog.collect root t0 retention

        test
            <@
                collected = { Expired =
                                [ "2026-08-18.r0.0123456789ab.jsonl.gz"; "2026-08-18.r1.0123456789ab.jsonl.gz" ]
                              OverCap = []
                              NotDeleted = [] }
            @>

        test
            <@
                logFiles root = [ "2026-08-18.jsonl.gz"
                                  "2026-08-19.r1.0123456789ab.jsonl.gz"
                                  "2026-11-17.r1.0123456789ab.jsonl.gz"
                                  "garbage.jsonl.gz"
                                  "notes.txt" ]
            @>)

[<Fact>]
let ``retention deletes the oldest day files until the log fits its cap`` () =
    withTempDir "rows-log-cap" (fun root ->
        for name in
            [ "2026-11-14.r1.0123456789ab.jsonl.gz"
              "2026-11-15.r1.0123456789ab.jsonl.gz"
              "2026-11-16.r1.0123456789ab.jsonl.gz"
              "2026-11-17.r1.0123456789ab.jsonl.gz" ] do
            writeDay root name 100

        // A file that is not a day file is not the log's, and does not count toward the cap.
        writeDay root "notes.txt" 1000

        let retention: TestRowsLog.Retention =
            { Ttl = TimeSpan.FromDays 90.0
              MaxBytes = 250L }

        test
            <@
                TestRowsLog.collect root t0 retention = { Expired = []
                                                          OverCap =
                                                            [ "2026-11-14.r1.0123456789ab.jsonl.gz"
                                                              "2026-11-15.r1.0123456789ab.jsonl.gz" ]
                                                          NotDeleted = [] }
            @>

        test
            <@
                logFiles root = [ "2026-11-16.r1.0123456789ab.jsonl.gz"
                                  "2026-11-17.r1.0123456789ab.jsonl.gz"
                                  "notes.txt" ]
            @>)

[<Fact>]
let ``retention over no log deletes nothing, and a day file it cannot delete is named`` () =
    withTempDir "rows-log-stuck" (fun root ->
        let retention: TestRowsLog.Retention =
            { Ttl = TimeSpan.FromDays 1.0
              MaxBytes = 1_000_000L }

        test
            <@
                TestRowsLog.collect root t0 retention = { Expired = []
                                                          OverCap = []
                                                          NotDeleted = [] }
            @>

        writeDay root "2026-01-01.r1.0123456789ab.jsonl.gz" 10
        let dir = logDir root
        let writable = File.GetUnixFileMode dir
        File.SetUnixFileMode(dir, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

        let collected =
            try
                TestRowsLog.collect root t0 retention
            finally
                File.SetUnixFileMode(dir, writable)

        test <@ List.isEmpty collected.Expired && List.isEmpty collected.OverCap @>
        test <@ collected.NotDeleted |> List.map fst = [ "2026-01-01.r1.0123456789ab.jsonl.gz" ] @>
        test <@ logFiles root = [ "2026-01-01.r1.0123456789ab.jsonl.gz" ] @>)

// --- configuration: `testRowsLog` in .fshw.json ---

let private mib = 1024L * 1024L

[<Fact>]
let ``the log's retention defaults to 90 days and 512 MiB`` () =
    test
        <@
            TestRowsLog.Retention.defaults = { Ttl = TimeSpan.FromDays 90.0
                                               MaxBytes = 512L * mib }
        @>

    test <@ TestRowsLog.Retention.parse "{}" = Ok TestRowsLog.Retention.defaults @>
    test <@ TestRowsLog.Retention.parse """{"testRowsLog":{}}""" = Ok TestRowsLog.Retention.defaults @>

[<Fact>]
let ``testRowsLog overrides either setting, keeping the other's default`` () =
    test
        <@
            TestRowsLog.Retention.parse """{"testRowsLog":{"ttlDays":30}}""" = Ok
                { Ttl = TimeSpan.FromDays 30.0
                  MaxBytes = 512L * mib }
        @>

    test
        <@
            TestRowsLog.Retention.parse """{"testRowsLog":{"maxMegabytes":64,"_why":"small disk"}}""" = Ok
                { Ttl = TimeSpan.FromDays 90.0
                  MaxBytes = 64L * mib }
        @>

    test
        <@
            TestRowsLog.Retention.parse """{"testRowsLog":{"ttlDays":180,"maxMegabytes":2048}}""" = Ok
                { Ttl = TimeSpan.FromDays 180.0
                  MaxBytes = 2048L * mib }
        @>

[<Theory>]
[<InlineData("""{"testRowsLog":5}""", "testRowsLog: 5 is not an object")>]
[<InlineData("""{"testRowsLog":null}""", "testRowsLog: null is not an object")>]
[<InlineData("""{"testRowsLog":{"ttlDays":0}}""", "testRowsLog.ttlDays: 0 is not a positive whole number")>]
[<InlineData("""{"testRowsLog":{"ttlDays":-1}}""", "testRowsLog.ttlDays: -1 is not a positive whole number")>]
[<InlineData("""{"testRowsLog":{"ttlDays":1.5}}""", "testRowsLog.ttlDays: 1.5 is not a positive whole number")>]
[<InlineData("""{"testRowsLog":{"ttlDays":"30"}}""", "testRowsLog.ttlDays: \"30\" is not a positive whole number")>]
[<InlineData("""{"testRowsLog":{"maxMegabytes":0}}""", "testRowsLog.maxMegabytes: 0 is not a positive whole number")>]
[<InlineData("""{"testRowsLog":{"ttl":30}}""",
             "testRowsLog: unknown key 'ttl' — expected ttlDays or maxMegabytes (a key starting with _ is a comment)")>]
let ``testRowsLog refuses what is not a positive whole number of days or megabytes`` (json: string, why: string) =
    test <@ TestRowsLog.Retention.parse json = Error why @>

[<Fact>]
let ``a repository's retention is its .fshw.json's, or the defaults when it has none`` () =
    withTempDir "rows-log-repo" (fun repo ->
        test <@ TestRowsLog.Retention.ofRepo repo = Ok TestRowsLog.Retention.defaults @>
        File.WriteAllText(Path.Combine(repo, ".fshw.json"), """{"testRowsLog":{"ttlDays":7}}""")
        test <@ (TestRowsLog.Retention.ofRepo repo |> Result.map _.Ttl) = Ok(TimeSpan.FromDays 7.0) @>
        File.WriteAllText(Path.Combine(repo, ".fshw.json"), "{ not json")
        test <@ Result.isError (TestRowsLog.Retention.ofRepo repo) @>
        let config = Path.Combine(repo, ".fshw.json")
        File.WriteAllText(config, "{}")
        let readable = File.GetUnixFileMode config
        File.SetUnixFileMode(config, UnixFileMode.None)

        let unreadable =
            try
                TestRowsLog.Retention.ofRepo repo
            finally
                File.SetUnixFileMode(config, readable)

        test <@ Result.isError unreadable @>)

[<Fact>]
let ``the daemon's config accepts testRowsLog and refuses a bad one`` () =
    let defaults = defaultTestConfig ()

    FsHotWatch.Cli.DaemonConfig.parseConfig """{"testRowsLog":{"ttlDays":30}}""" defaults
    |> ignore

    let refused =
        try
            FsHotWatch.Cli.DaemonConfig.parseConfig """{"testRowsLog":{"ttlDays":0}}""" defaults
            |> ignore

            None
        with FsHotWatch.Cli.DaemonConfig.ConfigErrorException message ->
            Some message

    test <@ refused = Some "testRowsLog.ttlDays: 0 is not a positive whole number" @>

// --- the store: the log is written with the index, and outlives the key ---

let private storeAt (root: string) (retention: Result<TestRowsLog.Retention, string>) (log: string -> unit) =
    OutcomeStore.Store(
        { Root = root
          Ttl = TimeSpan.FromDays 14.0
          MaxBytes = 100_000_000L
          Log = log },
        Lazy<_>.CreateFromValue retention
    )

let private recordOf (key: ActionKey.ActionKey) (outcome: OutcomeStore.Outcome) (runId: string) (at: DateTimeOffset) =
    ({ Key = key
       Subject = "tests/A/A.fsproj"
       Outcome = outcome
       Counts = None
       DurationMs = None
       Producer =
         { Binary = "fshw-1"
           Worktree = "ws-a"
           RunId = Some runId }
       RecordedAt = at }
    : OutcomeStore.Record)

/// File a run's record, then its rows, as the runner does.
let private fileRun (store: OutcomeStore.Store) (outcome: OutcomeStore.Outcome) (runId: string) at rows =
    let computed =
        OutcomeStore.justRan (recordOf (keyFor "sha256:tree") outcome runId at) None

    let outcomes = store :> OutcomeRecords.IOutcomeStore
    test <@ OutcomeStore.Through.fileComputed outcomes computed @>
    outcomes.FileTestRows(computed, rowsOf runId rows)

[<Fact>]
let ``the log and the index carry the same rows of each run`` () =
    withTempDir "rows-log-store" (fun root ->
        let store = storeAt root (Ok TestRowsLog.Retention.defaults) ignore

        test
            <@
                fileRun store OutcomeStore.Outcome.Fail "run-red" t0 [ "A.one", TestRows.Outcome.Failed ] = OutcomeRecords.Filing.Filed
            @>

        fileRun
            store
            OutcomeStore.Outcome.Pass
            "run-green"
            (t0.AddHours 1.0)
            [ "A.one", TestRows.Outcome.Passed; "A.two", TestRows.Outcome.Passed ]
        |> ignore

        let indexed = (OutcomeStore.readAllTestRows root).Indexes |> List.sortBy _.Run.RunId

        let logged = (TestRowsLog.read root).Runs |> List.sortBy _.Run.RunId
        test <@ indexed.Length = 2 && logged = indexed @>)

[<Fact>]
let ``rows filed again are not written to the log again`` () =
    withTempDir "rows-log-again" (fun root ->
        let store = storeAt root (Ok TestRowsLog.Retention.defaults) ignore
        let rows = [ "A.one", TestRows.Outcome.Passed ]
        fileRun store OutcomeStore.Outcome.Pass "run-1" t0 rows |> ignore
        test <@ fileRun store OutcomeStore.Outcome.Pass "run-1" t0 rows = OutcomeRecords.Filing.AlreadyFiled @>
        test <@ (TestRowsLog.read root).Runs.Length = 1 @>)

/// File a passing run of `project` under its own key, as the runner does.
let private fileProjectRun (store: OutcomeStore.Store) (project: string) (runId: string) (at: DateTimeOffset) =
    let key =
        ActionKey.create "test" [ "tree", "sha256:tree"; "project", project; "scope", "full" ]

    let record =
        { recordOf key OutcomeStore.Outcome.Pass runId at with
            Subject = project }

    let computed = OutcomeStore.justRan record None
    let outcomes = store :> OutcomeRecords.IOutcomeStore
    test <@ OutcomeStore.Through.fileComputed outcomes computed @>
    let rows = rowsOf runId [ "T.one", TestRows.Outcome.Passed ]
    test <@ outcomes.FileTestRows(computed, rows) = OutcomeRecords.Filing.Filed @>
    OutcomeRecords.TestRowsOf.index (OutcomeRecords.Computed.record computed) rows

[<Fact>]
let ``two runs on one day get two files, and both read`` () =
    withTempDir "rows-log-two" (fun root ->
        let store = storeAt root (Ok TestRowsLog.Retention.defaults) ignore
        let first = fileProjectRun store "tests/A/A.fsproj" "r1" t0
        let second = fileProjectRun store "tests/A/A.fsproj" "r2" (t0.AddHours 1.0)

        test <@ logFiles root = List.sort [ TestRowsLog.fileNameOf first; TestRowsLog.fileNameOf second ] @>

        test <@ (TestRowsLog.read root).Runs |> List.sortBy _.Run.RunId = [ first; second ] @>)

[<Fact>]
let ``one run id in two projects files two log files, and both read`` () =
    withTempDir "rows-log-projects" (fun root ->
        let store = storeAt root (Ok TestRowsLog.Retention.defaults) ignore
        let a = fileProjectRun store "tests/A/A.fsproj" "r1" t0
        let b = fileProjectRun store "tests/B/B.fsproj" "r1" t0
        test <@ (logFiles root).Length = 2 @>
        test <@ (TestRowsLog.read root).Runs |> List.sortBy _.Run.Project = [ a; b ] @>)

[<Fact>]
let ``a log file already under the run's name is taken as filed, not as an error`` () =
    withTempDir "rows-log-there" (fun root ->
        let logs = Collections.Concurrent.ConcurrentQueue<string>()
        let store = storeAt root (Ok TestRowsLog.Retention.defaults) logs.Enqueue
        let record = recordOf (keyFor "sha256:tree") OutcomeStore.Outcome.Pass "r1" t0
        let rows = rowsOf "r1" [ "A.one", TestRows.Outcome.Passed ]

        let name =
            TestRowsLog.fileNameOf (OutcomeRecords.TestRowsOf.index (OutcomeStore.toStored record None) rows)

        Directory.CreateDirectory(logDir root) |> ignore
        let there = Path.Combine(logDir root, name)
        File.WriteAllText(there, "filed elsewhere")
        let computed = OutcomeStore.justRan record None
        let outcomes = store :> OutcomeRecords.IOutcomeStore
        OutcomeStore.Through.fileComputed outcomes computed |> ignore
        test <@ outcomes.FileTestRows(computed, rows) = OutcomeRecords.Filing.Filed @>
        test <@ File.ReadAllText there = "filed elsewhere" @>
        test <@ logs |> Seq.forall (fun l -> not (l.Contains "test-row log")) @>)

[<Fact>]
let ``a record's eviction leaves its log lines intact`` () =
    withTempDir "rows-log-evict" (fun root ->
        let store = storeAt root (Ok TestRowsLog.Retention.defaults) ignore

        fileRun store OutcomeStore.Outcome.Fail "run-red" t0 [ "A.one", TestRows.Outcome.Failed ]
        |> ignore

        test <@ store.Gc(t0.AddDays 15.0, Set.empty) = [ (keyFor "sha256:tree").Value ] @>
        test <@ List.isEmpty (OutcomeStore.readAllTestRows root).Indexes @>
        test <@ (TestRowsLog.read root).Runs |> List.map _.Run.RunId = [ "run-red" ] @>)

[<Fact>]
let ``a log that cannot be written to is logged, and the index stands`` () =
    withTempDir "rows-log-unwritable" (fun root ->
        let logs = Collections.Concurrent.ConcurrentQueue<string>()
        let store = storeAt root (Ok TestRowsLog.Retention.defaults) logs.Enqueue
        Directory.CreateDirectory(logDir root) |> ignore
        let writable = File.GetUnixFileMode(logDir root)
        File.SetUnixFileMode(logDir root, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

        let filing =
            try
                fileRun store OutcomeStore.Outcome.Pass "run-1" t0 [ "A.one", TestRows.Outcome.Passed ]
            finally
                File.SetUnixFileMode(logDir root, writable)

        test <@ filing = OutcomeRecords.Filing.Filed @>
        test <@ (OutcomeStore.readAllTestRows root).Indexes.Length = 1 @>
        test <@ List.isEmpty (TestRowsLog.read root).Runs @>
        test <@ logs |> Seq.exists (fun l -> l.Contains "could not write the test rows of") @>)

[<Fact>]
let ``the store's gc applies the log's retention and says what it deleted`` () =
    withTempDir "rows-log-gc" (fun root ->
        let logs = Collections.Concurrent.ConcurrentQueue<string>()

        let retention: TestRowsLog.Retention =
            { Ttl = TimeSpan.FromDays 90.0
              MaxBytes = 1_000_000L }

        let store = storeAt root (Ok retention) logs.Enqueue
        writeDay root "2026-01-01.r1.0123456789ab.jsonl.gz" 10
        writeDay root "2026-11-17.r1.0123456789ab.jsonl.gz" 10
        store.Gc(t0, Set.empty) |> ignore
        test <@ logFiles root = [ "2026-11-17.r1.0123456789ab.jsonl.gz" ] @>

        test
            <@
                logs
                |> Seq.exists (fun l -> l.Contains "test-row log: deleted 1 day file(s) past the ttl, 0 over the cap")
            @>

        // Nothing to delete: nothing said.
        let before = logs.Count
        store.Gc(t0, Set.empty) |> ignore
        test <@ logs.Count = before @>)

[<Fact>]
let ``the store's gc names a day file it could not delete`` () =
    withTempDir "rows-log-gc-stuck" (fun root ->
        let logs = Collections.Concurrent.ConcurrentQueue<string>()
        let store = storeAt root (Ok TestRowsLog.Retention.defaults) logs.Enqueue
        writeDay root "2026-01-01.r1.0123456789ab.jsonl.gz" 10
        let writable = File.GetUnixFileMode(logDir root)
        File.SetUnixFileMode(logDir root, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

        try
            store.Gc(t0, Set.empty) |> ignore
        finally
            File.SetUnixFileMode(logDir root, writable)

        test
            <@
                logs
                |> Seq.exists (fun l ->
                    l.Contains "test-row log: could not delete 2026-01-01.r1.0123456789ab.jsonl.gz")
            @>)

[<Fact>]
let ``a store whose log retention could not be read deletes no day file, and says why`` () =
    withTempDir "rows-log-gc-unread" (fun root ->
        let logs = Collections.Concurrent.ConcurrentQueue<string>()

        let store =
            storeAt root (Error "testRowsLog.ttlDays: 0 is not a positive whole number") logs.Enqueue

        writeDay root "2020-01-01.r1.0123456789ab.jsonl.gz" 10
        store.Gc(t0, Set.empty) |> ignore
        test <@ logFiles root = [ "2020-01-01.r1.0123456789ab.jsonl.gz" ] @>

        test
            <@
                logs
                |> Seq.exists (fun l ->
                    l.Contains "test-row log: not collected"
                    && l.Contains "testRowsLog.ttlDays: 0 is not a positive whole number")
            @>)

[<Fact>]
let ``a store opened for a repository takes the log's retention from its .fshw.json`` () =
    withTempDir "rows-log-openfor" (fun repo ->
        File.WriteAllText(Path.Combine(repo, ".fshw.json"), """{"testRowsLog":{"ttlDays":0}}""")
        let logs = Collections.Concurrent.ConcurrentQueue<string>()
        let store = OutcomeStore.openFor repo logs.Enqueue
        store.Gc(t0, Set.empty) |> ignore

        test
            <@
                logs
                |> Seq.exists (fun l -> l.Contains "testRowsLog.ttlDays: 0 is not a positive whole number")
            @>)

// --- analytics: a flake whose red record was evicted is found in the log ---

[<Fact>]
let ``analytics finds a flake whose red record was evicted, from the daily log alone`` () =
    withTempDir "rows-log-flake" (fun root ->
        let store = storeAt root (Ok TestRowsLog.Retention.defaults) ignore

        fileRun store OutcomeStore.Outcome.Fail "run-red" t0 [ "A.one", TestRows.Outcome.Failed ]
        |> ignore

        store.Gc(t0.AddDays 15.0, Set.empty) |> ignore

        fileRun store OutcomeStore.Outcome.Pass "run-green" (t0.AddDays 15.0) [ "A.one", TestRows.Outcome.Passed ]
        |> ignore

        let analyze (log: TestRowsLog.LogRead) =
            FsHotWatch.Cli.OutcomeAnalytics.analyze
                None
                { Head =
                    { Source = FsHotWatch.Cli.OutcomeAnalytics.StoreSource.ofPath root
                      Scan = OutcomeStore.readAll root
                      Rows = OutcomeStore.readAllTestRows root
                      Log = log }
                  Tail = [] }
                (FsHotWatch.Cli.OutcomeAnalytics.readShadowLogs [])

        // The index alone has only the green: no flake.
        test
            <@
                List.isEmpty
                    (analyze
                        { Runs = []
                          UnreadableFiles = 0
                          UnreadableLines = 0 })
                        .FlakyTests
            @>

        let report = analyze (TestRowsLog.read root)
        let flake = List.exactlyOne report.FlakyTests

        test
            <@
                flake.Test = "A.one"
                && flake.Red.RunId = "run-red"
                && flake.Green.RunId = "run-green"
            @>

        test
            <@
                NonEmpty.toList flake.Sources |> List.map _.Via = [ FsHotWatch.Cli.OutcomeAnalytics.RowSource.Index
                                                                    FsHotWatch.Cli.OutcomeAnalytics.RowSource.DailyLog ]
            @>

        // The green run is in both sources and counted once.
        test <@ report.TestRowRuns = 2 && report.Stores.Head.LogRuns = 2 @>)
