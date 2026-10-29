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
        test <@ store.Lookup key.Value |> Option.map _.Outcome = Some Outcome.Pass @>
        test <@ store.Lookup (keyFor "sha256:t2").Value = None @>)

[<Fact>]
let ``a record reads back exactly as it was written`` () =
    withStore 1_000_000L (fun _ _ store ->
        let record = recordOf (keyFor "sha256:t1") Outcome.Fail t0
        store.Record record |> ignore
        test <@ store.Lookup record.Key.Value = Some record @>)

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

        test <@ reopened.Lookup record.Key.Value = Some record @>)

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
        test <@ store.Lookup key.Value |> Option.map _.Outcome = Some Outcome.Pass @>

        test <@ store.History key.Value |> List.map _.Outcome = [ Outcome.Fail; Outcome.Pass ] @>)

[<Fact>]
let ``a fail and a pass recorded at the same instant answer fail`` () =
    withStore 1_000_000L (fun _ _ store ->
        let key = keyFor "sha256:t1"
        store.Record(recordOf key Outcome.Pass t0) |> ignore
        store.Record(recordOf key Outcome.Fail t0) |> ignore
        test <@ store.Lookup key.Value |> Option.map _.Outcome = Some Outcome.Fail @>)

[<Fact>]
let ``a torn record is a miss and is quarantined`` () =
    withStore 1_000_000L (fun dir log store ->
        let key = keyFor "sha256:t1"
        store.Record(recordOf key Outcome.Pass t0) |> ignore
        let file = recordFiles dir |> List.exactlyOne
        File.WriteAllText(file, (File.ReadAllText file).Substring(0, 40))
        test <@ store.Lookup key.Value = None @>
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
        test <@ store.Lookup key.Value = None @>)

[<Fact>]
let ``a record filed under a key its inputs do not fold to is a miss`` () =
    withStore 1_000_000L (fun dir _ store ->
        let honest = keyFor "sha256:t1"
        let other = keyFor "sha256:t2"
        store.Record(recordOf honest Outcome.Pass t0) |> ignore
        let file = recordFiles dir |> List.exactlyOne
        // Move the honest record under another key's directory, as a buggy writer might.
        let target = (store.DirectoryOf other.Value)
        Directory.CreateDirectory target |> ignore
        File.Move(file, Path.Combine(target, Path.GetFileName file))
        test <@ store.Lookup other.Value = None @>)

[<Fact>]
let ``a record with an unknown schema is a miss`` () =
    withStore 1_000_000L (fun dir _ store ->
        let key = keyFor "sha256:t1"
        store.Record(recordOf key Outcome.Pass t0) |> ignore
        let file = recordFiles dir |> List.exactlyOne
        File.WriteAllText(file, (File.ReadAllText file).Replace(Schema, "fshw-outcome-v0"))
        test <@ store.Lookup key.Value = None @>)

[<Fact>]
let ``a value that is not a key finds nothing`` () =
    withStore 1_000_000L (fun _ _ store -> test <@ store.Lookup "../../etc" = None @>)

[<Fact>]
let ``gc evicts keys whose newest record is older than the ttl`` () =
    withStore 1_000_000L (fun _ _ store ->
        let old = keyFor "sha256:old"
        let fresh = keyFor "sha256:fresh"
        store.Record(recordOf old Outcome.Pass t0) |> ignore
        store.Record(recordOf fresh Outcome.Pass (t0.AddDays 10.0)) |> ignore
        let evicted = store.Gc(t0.AddDays 15.0, Set.empty)
        test <@ evicted = [ old.Value ] @>
        test <@ store.Lookup old.Value = None @>
        test <@ store.Lookup fresh.Value <> None @>)

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
        test <@ store.Lookup a.Value <> None @>)

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
            store.LookupWithOutputs key.Value |> Option.map snd

        let roundTripped = read withOutputs |> Option.flatten

        test
            <@
                roundTripped
                |> Option.map Text.Json.Nodes.JsonNode.Parse
                |> Option.map _.ToJsonString() = Some(Text.Json.Nodes.JsonNode.Parse(outputs).ToJsonString())
            @>

        test <@ read without = Some None @>
        test <@ store.Lookup(withOutputs.Value) = Some(recordOf withOutputs Outcome.Pass t0) @>
        test <@ store.LookupWithOutputs "not a key" = None @>)
