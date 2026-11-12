/// ADR-059 §2: the outcome store answers only with a record it can read back exactly as
/// written, under the key asked about. A record is immutable; several outcomes for one key
/// are history, not overwrites; anything torn, foreign or mis-keyed is a miss and is moved
/// aside. GC can only turn a hit into a miss. No test here reads the wall clock: every time
/// is a value the test chose.
module FsHotWatch.Tests.OutcomeStoreTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.OutcomeStore
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero)

let private keyFor (tree: string) =
    ActionKey.create "test" [ "tree", tree; "project", "tests/A/A.fsproj"; "scope", "full" ]

let private recordOf (key: ActionKey.ActionKey) (outcome: Outcome) (at: DateTimeOffset) =
    { Key = key
      Subject = "tests/A/A.fsproj"
      Outcome = outcome
      Counts =
        Some
            { Total = 3
              Passed = (if outcome = Outcome.Pass then 3 else 2)
              Failed = (if outcome = Outcome.Pass then 0 else 1)
              Skipped = 0 }
      DurationMs = Some 1200L
      Producer =
        { Binary = "fshw-1"
          Worktree = "ws-a"
          RunId = Some "run-1" }
      RecordedAt = at }

let private withStore (maxBytes: int64) (body: string -> (unit -> string list) -> Store -> unit) =
    withTempDir "outcomes" (fun dir ->
        let log = Collections.Concurrent.ConcurrentQueue<string>()

        let store =
            Store(
                { Root = dir
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = maxBytes
                  Log = log.Enqueue }
            )

        body dir (fun () -> List.ofSeq log) store)

let private recordFiles (dir: string) =
    let records = Path.Combine(dir, "records")

    if Directory.Exists records then
        allFilesUnder "*.json" records
    else
        []

[<Fact>]
let ``a recorded outcome is found under its key and nowhere else`` () =
    withStore 1_000_000L (fun _ _ store ->
        let key = keyFor "sha256:t1"
        test <@ store.Record(recordOf key Outcome.Pass t0) @>
        test <@ store.Lookup key |> Option.map _.Outcome = Some Outcome.Pass @>
        test <@ store.Lookup(keyFor "sha256:t2") = None @>)

[<Fact>]
let ``a record reads back exactly as it was written`` () =
    withStore 1_000_000L (fun _ _ store ->
        let record = recordOf (keyFor "sha256:t1") Outcome.Fail t0
        store.Record record |> ignore
        test <@ store.Lookup record.Key = Some record @>)

[<Fact>]
let ``a store opened afresh over the same directory gives the same answer`` () =
    withStore 1_000_000L (fun dir _ store ->
        let record = recordOf (keyFor "sha256:t1") Outcome.Pass t0
        store.Record record |> ignore

        let reopened =
            Store(
                { Root = dir
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 1_000_000L
                  Log = ignore }
            )

        test <@ reopened.Lookup record.Key = Some record @>)

[<Fact>]
let ``re-recording an identical outcome writes nothing new`` () =
    withStore 1_000_000L (fun dir _ store ->
        let record = recordOf (keyFor "sha256:t1") Outcome.Pass t0
        test <@ store.Record record @>
        test <@ store.Record record @>
        test <@ List.length (recordFiles dir) = 1 @>)

[<Fact>]
let ``two outcomes for one key are history and the newest is the answer`` () =
    withStore 1_000_000L (fun _ _ store ->
        let key = keyFor "sha256:t1"
        store.Record(recordOf key Outcome.Fail t0) |> ignore
        store.Record(recordOf key Outcome.Pass (t0.AddMinutes 1.0)) |> ignore
        test <@ store.Lookup key |> Option.map _.Outcome = Some Outcome.Pass @>

        test <@ store.History key |> List.map _.Outcome = [ Outcome.Fail; Outcome.Pass ] @>)

[<Fact>]
let ``a fail and a pass recorded at the same instant answer fail`` () =
    withStore 1_000_000L (fun _ _ store ->
        let key = keyFor "sha256:t1"
        store.Record(recordOf key Outcome.Pass t0) |> ignore
        store.Record(recordOf key Outcome.Fail t0) |> ignore
        test <@ store.Lookup key |> Option.map _.Outcome = Some Outcome.Fail @>)

[<Fact>]
let ``a torn record is a miss and is quarantined`` () =
    withStore 1_000_000L (fun dir log store ->
        let key = keyFor "sha256:t1"
        store.Record(recordOf key Outcome.Pass t0) |> ignore
        let file = recordFiles dir |> List.exactlyOne
        File.WriteAllText(file, (File.ReadAllText file).Substring(0, 40))
        test <@ store.Lookup key = None @>
        test <@ List.isEmpty (recordFiles dir) @>
        test <@ Directory.GetFiles(Path.Combine(dir, "quarantine")).Length = 1 @>
        test <@ log () |> List.exists (fun l -> l.Contains "quarantined") @>)

[<Fact>]
let ``a record whose payload does not match its checksum is a miss`` () =
    withStore 1_000_000L (fun dir _ store ->
        let key = keyFor "sha256:t1"
        store.Record(recordOf key Outcome.Fail t0) |> ignore
        let file = recordFiles dir |> List.exactlyOne
        let envelope = Text.Json.Nodes.JsonNode.Parse(File.ReadAllText file).AsObject()
        let payload = envelope["payload"].GetValue<string>()
        test <@ payload.Contains "\"fail\"" @>
        envelope["payload"] <- Text.Json.Nodes.JsonValue.Create(payload.Replace("\"fail\"", "\"pass\""))
        File.WriteAllText(file, envelope.ToJsonString())
        test <@ store.Lookup key = None @>)

[<Fact>]
let ``a record filed under a key its inputs do not fold to is a miss`` () =
    withStore 1_000_000L (fun dir _ store ->
        let honest = keyFor "sha256:t1"
        let other = keyFor "sha256:t2"
        store.Record(recordOf honest Outcome.Pass t0) |> ignore
        let file = recordFiles dir |> List.exactlyOne
        // Move the honest record under another key's directory, as a buggy writer might.
        let target = (store.DirectoryOf other)
        Directory.CreateDirectory target |> ignore
        File.Move(file, Path.Combine(target, Path.GetFileName file))
        test <@ store.Lookup other = None @>)

[<Fact>]
let ``a record with an unknown schema is a miss`` () =
    withStore 1_000_000L (fun dir _ store ->
        let key = keyFor "sha256:t1"
        store.Record(recordOf key Outcome.Pass t0) |> ignore
        let file = recordFiles dir |> List.exactlyOne
        File.WriteAllText(file, (File.ReadAllText file).Replace(Schema, "fshw-outcome-v0"))
        test <@ store.Lookup key = None @>)

[<Fact>]
let ``a value that is not a key finds nothing`` () =
    withStore 1_000_000L (fun _ _ _ -> test <@ ActionKey.KeyValue.tryParse "../../etc" = None @>)

[<Fact>]
let ``gc evicts keys whose newest record is older than the ttl`` () =
    withStore 1_000_000L (fun _ _ store ->
        let old = keyFor "sha256:old"
        let fresh = keyFor "sha256:fresh"
        store.Record(recordOf old Outcome.Pass t0) |> ignore
        store.Record(recordOf fresh Outcome.Pass (t0.AddDays 10.0)) |> ignore
        let evicted = store.Gc(t0.AddDays 15.0, Set.empty)
        test <@ evicted = [ old.Value ] @>
        test <@ store.Lookup old = None @>
        test <@ store.Lookup fresh <> None @>)

[<Fact>]
let ``gc evicts the oldest keys until the store fits its byte budget, sparing the kept ones`` () =
    withStore 1L (fun _ _ store ->
        let a = keyFor "sha256:a"
        let b = keyFor "sha256:b"
        let c = keyFor "sha256:c"
        store.Record(recordOf a Outcome.Pass t0) |> ignore
        store.Record(recordOf b Outcome.Pass (t0.AddMinutes 1.0)) |> ignore
        store.Record(recordOf c Outcome.Pass (t0.AddMinutes 2.0)) |> ignore
        let evicted = store.Gc(t0.AddMinutes 3.0, Set.ofList [ a.Value ])
        test <@ evicted = [ b.Value; c.Value ] @>
        test <@ store.Lookup a <> None @>)

[<Fact>]
let ``the store directory is namespaced per repository under the shared cache`` () =
    let dir = directoryFor "/some/repo"
    test <@ dir.Contains(Path.Combine("cache", "outcomes")) @>
    test <@ dir.EndsWith(Path.Combine(RepoIdentity.namespaceOf "/some/repo", "v1")) @>

[<Fact>]
let ``a record's outputs are read back as filed, and a record without outputs has none`` () =
    withStore defaultMaxBytes (fun _ _ store ->
        let withOutputs = keyFor "tree-outputs"
        let without = keyFor "tree-no-outputs"
        let outputs = """{"schema":"x","diagnostics":[{"message":"a \"quoted\" <b>"}]}"""
        test <@ store.RecordWithOutputs(recordOf withOutputs Outcome.Pass t0, Some outputs) @>
        test <@ store.Record(recordOf without Outcome.Pass t0) @>

        let read (key: ActionKey.ActionKey) =
            store.LookupWithOutputs key |> Option.map snd

        let roundTripped = read withOutputs |> Option.flatten

        test
            <@
                roundTripped
                |> Option.map Text.Json.Nodes.JsonNode.Parse
                |> Option.map _.ToJsonString() = Some(Text.Json.Nodes.JsonNode.Parse(outputs).ToJsonString())
            @>

        test <@ read without = Some None @>
        test <@ store.Lookup(withOutputs) = Some(recordOf withOutputs Outcome.Pass t0) @>
        test <@ ActionKey.KeyValue.tryParse "not a key" = None @>)

// --- AUTOMATION-1368: the read-only scan analytics reads ---

[<Fact>]
let ``readAll returns every readable record of every key, oldest first, and moves nothing`` () =
    withStore 1_000_000L (fun dir _ store ->
        let a = keyFor "sha256:a"
        let b = keyFor "sha256:b"
        store.Record(recordOf a Outcome.Pass (t0.AddMinutes 2.0)) |> ignore
        store.Record(recordOf b Outcome.Fail t0) |> ignore
        store.Record(recordOf a Outcome.Fail (t0.AddMinutes 1.0)) |> ignore
        let c = keyFor "sha256:c"
        store.Record(recordOf c Outcome.Pass (t0.AddMinutes 3.0)) |> ignore
        let tornFile = Directory.GetFiles(store.DirectoryOf c) |> Array.exactlyOne

        File.WriteAllText(tornFile, (File.ReadAllText tornFile).Substring(0, 40))

        let scan = readAll dir

        test
            <@
                scan.Records |> List.map (fun r -> r.Key.Value, r.Outcome) = [ b.Value, Outcome.Fail
                                                                               a.Value, Outcome.Fail
                                                                               a.Value, Outcome.Pass ]
            @>

        test <@ scan.Unreadable = 1 @>
        // A read for analytics never quarantines: the torn file is still where it was.
        test <@ File.Exists tornFile @>
        test <@ not (Directory.Exists(Path.Combine(dir, "quarantine"))) @>)

[<Fact>]
let ``readAll of a directory that holds no store is empty`` () =
    withTempDir "outcomes-none" (fun dir ->
        let scan = readAll (Path.Combine(dir, "absent"))
        test <@ List.isEmpty scan.Records && scan.Unreadable = 0 @>)

[<Fact>]
let ``an action's record carries no test counts, duration or run, and what it was given`` () =
    let key = keyFor "sha256:t"
    let r = Record.ofAction key "src/A.fs" Outcome.Fail "fshw 1" "/w" t0

    test
        <@
            r = { Key = key
                  Subject = "src/A.fs"
                  Outcome = Outcome.Fail
                  Counts = None
                  DurationMs = None
                  Producer =
                    { Binary = "fshw 1"
                      Worktree = "/w"
                      RunId = None }
                  RecordedAt = t0 }
        @>

// AUTOMATION-1506: GC's evictions were silent. Both callers discard the evicted keys, so on
// intelligence a cold daemon's first-tier TestPrune analyses were filed, then evicted for
// bytes within minutes, and a fresh workspace demanded all 119 of them live with nothing in
// any log to say why.
[<Fact>]
let ``gc says in the store's log what it evicted, why, and by kind`` () =
    withStore 1L (fun _ logged store ->
        let a = keyFor "sha256:a"
        let b = keyFor "sha256:b"
        store.Record(recordOf a Outcome.Pass t0) |> ignore
        store.Record(recordOf b Outcome.Pass (t0.AddMinutes 1.0)) |> ignore
        store.Gc(t0.AddMinutes 2.0, Set.empty) |> ignore
        let lines = logged () |> List.filter (fun l -> l.Contains "evicted")
        test <@ lines.Length = 1 @>
        test <@ lines.Head.Contains "evicted 2 key(s)" @>
        test <@ lines.Head.Contains "0 past the ttl, 2 over the byte budget" @>
        test <@ lines.Head.Contains "test 2" @>)

[<Fact>]
let ``gc that evicts nothing logs nothing`` () =
    withStore 1_000_000L (fun _ logged store ->
        store.Record(recordOf (keyFor "sha256:a") Outcome.Pass t0) |> ignore
        store.Gc(t0.AddMinutes 1.0, Set.empty) |> ignore
        test <@ logged () |> List.filter (fun l -> l.Contains "evicted") |> List.isEmpty @>)

// AUTOMATION-1506, measured on intelligence (2731 checked files) at fshotwatch.cli
// 0.14.0-alpha.137: one tree's TestPrune analysis records alone are about 250 MB on disk
// (about 120 KB a file), so with check, format and options records one tree is about 300 MB,
// over the 256 MB budget before a single check finished. GC then evicted the oldest keys of
// the very tree the daemon had just filed. The budget must hold several such trees, because
// workspaces of the same repository at different trees share the store.
[<Fact>]
let ``the default byte budget holds four intelligence-sized trees`` () =
    let oneTree = 340L * 1024L * 1024L
    test <@ defaultMaxBytes >= 4L * oneTree @>

// --- AUTOMATION-1583: one byte budget across kinds must not let one kind starve another ---
//
// Measured on intelligence's store (thellma-and-louisa, 2026-11-11): testprune-analysis held
// 2.12 GB on disk of a 2 GiB budget, check 15.5 MB. GC evicted the keys with the oldest newest
// record whatever their kind, so one GC evicted 13,504 check keys to pay for analyses. And
// 16,752 of the 18,087 analysis records (92%) were superseded history: the same key re-filed
// by every checkout, the newest record per key holding only 162 MB.

let private actionKey (kind: string) (name: string) = ActionKey.create kind [ "file", name ]

let private fileAction (store: Store) (kind: string) (name: string) (outputBytes: int) (at: DateTimeOffset) =
    let key = actionKey kind name
    let outputs = $"""{{"blob":"%s{String('x', outputBytes)}"}}"""
    let record = Record.ofAction key name Outcome.Pass "fshw" $"ws-%d{at.Minute}" at
    test <@ store.RecordWithOutputs(record, Some outputs) @>
    key

let private bytesUnder (path: string) =
    if Directory.Exists path then
        allFilesUnder "*" path |> List.sumBy (fun f -> FileInfo(f).Length)
    else
        0L

/// The same store directory under another byte budget.
let private withBudget (dir: string) (maxBytes: int64) (log: string -> unit) =
    Store(
        { Root = dir
          Ttl = TimeSpan.FromDays 14.0
          MaxBytes = maxBytes
          Log = log }
    )

[<Fact>]
let ``gc over budget reclaims from the kind over its share and evicts no record of a kind under its share`` () =
    withStore 1_000_000_000L (fun dir _ store ->
        // The small kinds are filed FIRST, so their newest records are the oldest in the store:
        // a budget walk by age alone takes them before any analysis.
        let checks =
            [ for i in 0..5 -> fileAction store "check" $"src/C%d{i}.fs" 200 (t0.AddMinutes(float i)) ]

        let format = fileAction store "format" "src/F.fs" 100 (t0.AddMinutes 6.0)
        let options = fileAction store "options" "src/A.fsproj" 2_000 (t0.AddMinutes 7.0)
        let small = bytesUnder (Path.Combine(dir, "records"))

        let analyses =
            [ for i in 0..7 ->
                  fileAction store "testprune-analysis" $"src/C%d{i}.fs" 20_000 (t0.AddMinutes(10.0 + float i)) ]

        let oneAnalysis = bytesUnder (store.DirectoryOf(List.head analyses))
        let budget = small + 3L * oneAnalysis + oneAnalysis / 2L
        let evicted = (withBudget dir budget ignore).Gc(t0.AddMinutes 30.0, Set.empty)

        test <@ checks |> List.forall (fun k -> store.Lookup k <> None) @>
        test <@ store.Lookup format <> None && store.Lookup options <> None @>
        test <@ evicted = (analyses |> List.take 5 |> List.map _.Value) @>
        test <@ bytesUnder (Path.Combine(dir, "records")) <= budget @>)

[<Fact>]
let ``gc trims the superseded records of the kind over its share before evicting any of its keys`` () =
    withStore 1_000_000_000L (fun dir _ store ->
        let check = fileAction store "check" "src/C.fs" 200 t0

        // One analysis re-filed by five checkouts: four superseded records and the answer.
        let refiled =
            [ for i in 1..5 -> fileAction store "testprune-analysis" "src/A.fs" 20_000 (t0.AddMinutes(float i)) ]
            |> List.head

        let other =
            fileAction store "testprune-analysis" "src/B.fs" 20_000 (t0.AddMinutes 9.0)

        let answer = store.Lookup refiled
        let oneRecord = bytesUnder (store.DirectoryOf other)
        let budget = bytesUnder (Path.Combine(dir, "records")) - 3L * oneRecord
        let logged = Collections.Concurrent.ConcurrentQueue<string>()

        let evicted =
            (withBudget dir budget logged.Enqueue).Gc(t0.AddMinutes 30.0, Set.empty)

        test <@ List.isEmpty evicted @>
        test <@ store.Lookup refiled = answer && store.Lookup other <> None @>
        test <@ store.Lookup check <> None @>
        test <@ store.History refiled |> List.map _.RecordedAt = [ t0.AddMinutes 4.0; t0.AddMinutes 5.0 ] @>
        test <@ bytesUnder (Path.Combine(dir, "records")) <= budget @>

        test
            <@
                logged
                |> Seq.exists (fun l -> l.Contains "evicted 0 key(s)" && l.Contains "3 superseded record(s)")
            @>)

[<Fact>]
let ``gc reclaims a small kind that is over its own share, oldest first, and spares the others`` () =
    withStore 1_000_000_000L (fun dir _ store ->
        let checks =
            [ for i in 0..9 -> fileAction store "check" $"src/C%d{i}.fs" 2_000 (t0.AddMinutes(10.0 + float i)) ]

        let format = fileAction store "format" "src/F.fs" 100 t0
        let total = bytesUnder (Path.Combine(dir, "records"))
        let oneCheck = bytesUnder (store.DirectoryOf(List.head checks))
        let budget = total - 2L * oneCheck - oneCheck / 2L
        let evicted = (withBudget dir budget ignore).Gc(t0.AddMinutes 30.0, Set.empty)

        test <@ evicted = (checks |> List.take 3 |> List.map _.Value) @>
        test <@ store.Lookup format <> None @>
        test <@ bytesUnder (Path.Combine(dir, "records")) <= budget @>)

[<Fact>]
let ``the ttl still evicts every kind, including a kind under its share`` () =
    withStore 1_000_000_000L (fun _ _ store ->
        let oldCheck = fileAction store "check" "src/C.fs" 200 t0
        let oldFormat = fileAction store "format" "src/F.fs" 100 t0
        let oldAnalysis = fileAction store "testprune-analysis" "src/A.fs" 2_000 t0
        let fresh = fileAction store "check" "src/D.fs" 200 (t0.AddDays 10.0)
        let evicted = store.Gc(t0.AddDays 15.0, Set.empty) |> Set.ofList

        test <@ evicted = Set.ofList [ oldCheck.Value; oldFormat.Value; oldAnalysis.Value ] @>
        test <@ store.Lookup fresh <> None @>)

[<Fact>]
let ``when the kind over its share is all kept, gc still meets the total budget from the rest`` () =
    withStore 1_000_000_000L (fun dir _ store ->
        let checks =
            [ for i in 0..3 -> fileAction store "check" $"src/C%d{i}.fs" 200 (t0.AddMinutes(float i)) ]

        let analyses =
            [ for i in 0..3 ->
                  fileAction store "testprune-analysis" $"src/A%d{i}.fs" 20_000 (t0.AddMinutes(10.0 + float i)) ]

        let oneCheck = bytesUnder (store.DirectoryOf(List.head checks))
        let budget = bytesUnder (Path.Combine(dir, "records")) - oneCheck - oneCheck / 2L
        let keep = analyses |> List.map _.Value |> Set.ofList
        let evicted = (withBudget dir budget ignore).Gc(t0.AddMinutes 30.0, keep)

        test <@ evicted = (checks |> List.take 2 |> List.map _.Value) @>
        test <@ analyses |> List.forall (fun k -> store.Lookup k <> None) @>
        test <@ bytesUnder (Path.Combine(dir, "records")) <= budget @>)

[<Fact>]
let ``a superseded record gc cannot delete is logged, and the answer stays`` () =
    withStore 1_000_000_000L (fun dir _ store ->
        let key =
            [ for i in 1..2 -> fileAction store "testprune-analysis" "src/A.fs" 20_000 (t0.AddMinutes(float i)) ]
            |> List.head

        let answer = store.Lookup key
        let keyDir = store.DirectoryOf key
        let budget = bytesUnder (Path.Combine(dir, "records")) - 100L
        let logged = Collections.Concurrent.ConcurrentQueue<string>()
        let writable = File.GetUnixFileMode keyDir
        File.SetUnixFileMode(keyDir, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

        try
            (withBudget dir budget logged.Enqueue).Gc(t0.AddMinutes 30.0, Set.ofList [])
            |> ignore
        finally
            File.SetUnixFileMode(keyDir, writable)

        test <@ logged |> Seq.exists (fun l -> l.Contains "could not trim") @>
        test <@ store.Lookup key = answer @>)

// AUTOMATION-1568 (B): a record that could not be READ is not a record that was read and found
// wrong. A torn or mis-keyed file is evidence of nothing and is moved aside; an unreadable one
// (a transient IO error, a GC's `Directory.Delete` mid-flight) may well be the newest fail, so
// answering the key from the rest would return the older pass, and quarantining it would make
// that answer permanent. `TestEvidenceStore.Lookup` already separates the two: unreadable is a
// miss, and nothing is moved.
let private unreadable (file: string) (body: unit -> unit) =
    File.SetUnixFileMode(file, UnixFileMode.None)

    try
        // The control must be real: a reader that can still read the file (root) proves nothing.
        let readable =
            try
                File.ReadAllText file |> ignore
                true
            with :? UnauthorizedAccessException ->
                false

        test <@ not readable @>
        body ()
    finally
        // A store that quarantines it has moved it aside; then there is nothing to restore.
        if File.Exists file then
            File.SetUnixFileMode(file, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

[<Fact>]
let ``an unreadable newest fail is a miss for its key, not the older pass, and is not quarantined`` () =
    withStore 1_000_000L (fun dir _ store ->
        let key = keyFor "sha256:t1"
        store.Record(recordOf key Outcome.Pass t0) |> ignore
        let passFile = recordFiles dir |> List.exactlyOne
        store.Record(recordOf key Outcome.Fail (t0.AddMinutes 1.0)) |> ignore
        let failFile = recordFiles dir |> List.except [ passFile ] |> List.exactlyOne

        unreadable failFile (fun () ->
            test <@ store.Lookup key = None @>
            test <@ store.LookupWithOutputs key = None @>
            test <@ List.isEmpty (store.History key) @>
            test <@ File.Exists failFile @>
            test <@ not (Directory.Exists(Path.Combine(dir, "quarantine"))) @>)

        // Once it reads again, the fail it always was is the answer.
        test <@ store.Lookup key |> Option.map _.Outcome = Some Outcome.Fail @>)

[<Fact>]
let ``a key whose records are all readable still answers with its newest beside an unreadable other key`` () =
    withStore 1_000_000L (fun dir _ store ->
        let good = keyFor "sha256:good"
        let bad = keyFor "sha256:bad"
        store.Record(recordOf good Outcome.Pass t0) |> ignore
        let goodFile = recordFiles dir |> List.exactlyOne
        store.Record(recordOf bad Outcome.Fail t0) |> ignore
        let badFile = recordFiles dir |> List.except [ goodFile ] |> List.exactlyOne

        unreadable badFile (fun () ->
            test <@ store.Lookup good |> Option.map _.Outcome = Some Outcome.Pass @>
            test <@ store.Lookup bad = None @>))

// AUTOMATION-1615: a reader that must not take "could not be read" for "nothing on file" (an
// evidence reuse asking whether a newer fail is filed) gets the two apart.
[<Fact>]
let ``TryHistory tells an unreadable key from an empty one, and answers a readable one in order`` () =
    withStore 1_000_000L (fun dir _ store ->
        let key = keyFor "sha256:t1"
        test <@ store.TryHistory key.Value = Ok [] @>
        store.Record(recordOf key Outcome.Pass t0) |> ignore
        let passFile = recordFiles dir |> List.exactlyOne
        store.Record(recordOf key Outcome.Fail (t0.AddMinutes 1.0)) |> ignore

        test <@ store.TryHistory key.Value |> Result.map (List.map _.Outcome) = Ok [ Outcome.Pass; Outcome.Fail ] @>

        unreadable passFile (fun () ->
            test <@ Result.isError (store.TryHistory key.Value) @>
            test <@ File.Exists passFile @>))

// --- AUTOMATION-1587 step 2: a record that is rewritten in place, and records that keep others ---

[<Fact>]
let ``a superseding record is its key's only record, and a lookup answers with it`` () =
    withStore 1_000_000_000L (fun _ _ store ->
        let key = actionKey "test-baseline-index" "index"

        let filed =
            [ for i in 1..5 ->
                  let record =
                      Record.ofAction key "index" Outcome.Pass "fshw" "ws" (t0.AddMinutes(float i))

                  store.Supersede(record, Some $"""{{"n":%d{i}}}""") ]

        test
            <@
                filed
                |> List.forall (fun size -> size |> Option.exists (fun bytes -> bytes > 0L))
            @>

        test <@ store.History key |> List.map _.RecordedAt = [ t0.AddMinutes 5.0 ] @>
        test <@ store.LookupWithOutputs key |> Option.bind snd = Some """{"n":5}""" @>
        test <@ Directory.GetFiles(store.DirectoryOf key, "*.json").Length = 1 @>
        // The size is the filed record's own.
        test <@ filed |> List.last = Some(FileInfo(Directory.GetFiles(store.DirectoryOf key, "*.json")[0]).Length) @>)

[<Fact>]
let ``gc keeps every key the newest record of a live key names in its keeps, and only those`` () =
    withStore 1_000_000_000L (fun _ _ store ->
        let named = fileAction store "test-baseline" "tree-1" 100 t0
        let dropped = fileAction store "test-baseline" "tree-0" 100 t0
        let index = actionKey "test-baseline-index" "index"

        let older = Record.ofAction index "index" Outcome.Pass "fshw" "ws" (t0.AddDays 9.0)

        // A superseded record's keeps keep nothing: only the answer speaks for the key.
        store.RecordWithOutputs(older, Some $"""{{"keeps":["%s{dropped.Value.Text}"]}}""")
        |> ignore

        let live = Record.ofAction index "index" Outcome.Pass "fshw" "ws" (t0.AddDays 10.0)

        store.RecordWithOutputs(live, Some $"""{{"keeps":["%s{named.Value.Text}","not-a-key"]}}""")
        |> ignore

        let evicted = store.Gc(t0.AddDays 15.0, Set.empty)

        test <@ evicted = [ dropped.Value ] @>
        test <@ store.Lookup named <> None @>
        test <@ store.Lookup index <> None @>)
