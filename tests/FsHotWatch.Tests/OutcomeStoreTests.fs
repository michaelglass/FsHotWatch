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
