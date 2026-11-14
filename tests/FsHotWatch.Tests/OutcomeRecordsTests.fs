/// L1 in FsHotWatch.Core (AUTOMATION-1695): the record codec, what one read of a key finds, and
/// the in-memory `IOutcomeStore` the simulation uses. The disk store reads through the same
/// `KeyRead.ofFiles`, so these properties are the disk store's too.
module FsHotWatch.Tests.OutcomeRecordsTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.OutcomeRecords

let private at = DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)

let private key =
    ActionKey.create "test" [ "tree", "t1"; "project", "A.Tests"; "scope", "full" ]

let private recordOf (outcome: LookupVerdict.Outcome) (ran: Ran) (recordedAt: DateTimeOffset) : StoredRecord =
    { Key = key
      Subject = "A.Tests"
      Outcome = outcome
      Ran = ran
      Producer = { Binary = "fshw-1"; Worktree = "/w" }
      RecordedAt = recordedAt
      Outputs = None }

let private testRun =
    Ran.TestRun
        { RunId = Some "run-1"
          Counts =
            { Total = 3
              Passed = 3
              Failed = 0
              Skipped = 0 }
          DurationMs = Some 12L }

let private store () = InMemoryOutcomeStore()

let private read (s: InMemoryOutcomeStore) = (s :> IOutcomeStore).Read key.Value

let private file (s: InMemoryOutcomeStore) (r: StoredRecord) = (s :> IOutcomeStore).File r

/// AUTOMATION-1568: a file that could not be READ says nothing about what it holds, and may be
/// the newest fail. The whole key is a miss, and nothing is moved, so the miss is not made
/// permanent by quarantining the file it could not read.
[<Fact>]
let ``an unreadable file makes the whole key a miss and moves nothing`` () =
    let s = store ()
    test <@ file s (recordOf LookupVerdict.Outcome.Pass testRun at) = Filing.Filed @>
    s.PlantUnreadable(key.Value, "newer.json", "permission denied")
    let before = s.FileNames key.Value

    let r = read s

    test <@ r = KeyRead.Unreadable("newer.json", "permission denied") @>
    test <@ KeyRead.outcome r = None @>
    test <@ KeyRead.history r = [] @>
    test <@ s.FileNames key.Value = before @>
    test <@ s.Quarantined = [] @>

/// AUTOMATION-1701: a writer holding the bytes the name is the digest of repairs an unreadable
/// or torn file of that name; a read still moves nothing.
[<Fact>]
let ``filing the same record again repairs an unreadable or torn file of its name`` () =
    let s = store ()
    let r = recordOf LookupVerdict.Outcome.Pass testRun at
    test <@ file s r = Filing.Filed @>
    let name = s.FileNames key.Value |> List.exactlyOne

    s.Replace(key.Value, name, Error "permission denied")
    test <@ KeyRead.outcome (read s) = None @>
    test <@ file s r = Filing.Filed @>
    test <@ KeyRead.outcome (read s) = Some LookupVerdict.Outcome.Pass @>

    s.Replace(key.Value, name, Ok "{\"schema\":\"fshw-outc")
    test <@ file s r = Filing.Filed @>
    test <@ s.FileNames key.Value = [ name ] @>
    test <@ KeyRead.outcome (read s) = Some LookupVerdict.Outcome.Pass @>
    test <@ file s r = Filing.AlreadyFiled @>

[<Fact>]
let ``a torn file is quarantined and the records beside it still answer`` () =
    let s = store ()
    file s (recordOf LookupVerdict.Outcome.Fail testRun at) |> ignore
    s.Plant(key.Value, "torn.json", """{"schema":"fshw-outcome-v1","checksum":"00""")

    let r = read s

    test <@ KeyRead.outcome r = Some LookupVerdict.Outcome.Fail @>

    test
        <@
            match r with
            | KeyRead.Answered(_, [], [ ("torn.json", Rejected.Torn _) ]) -> true
            | _ -> false
        @>

    test <@ s.FileNames key.Value |> List.contains "torn.json" |> not @>
    test <@ s.Quarantined |> List.map fst = [ "torn.json" ] @>

[<Fact>]
let ``files of which none is a record of the key are a miss that names them`` () =
    let s = store ()

    let other =
        ActionKey.create "test" [ "tree", "t2"; "project", "A.Tests"; "scope", "full" ]

    let foreign =
        RecordCodec.envelope (
            RecordCodec.encodePayload
                { recordOf LookupVerdict.Outcome.Pass testRun at with
                    Key = other }
        )

    s.Plant(key.Value, "foreign.json", foreign)
    s.Plant(key.Value, "future.json", """{"schema":"fshw-outcome-v9","checksum":"x","payload":"{}"}""")

    let r = read s

    test <@ KeyRead.outcome r = None @>

    test
        <@
            match r with
            | KeyRead.OnlyRejected rejected ->
                FsHotWatch.Core.NonEmpty.toList rejected = [ "foreign.json", Rejected.OtherKey other.Value
                                                             "future.json", Rejected.UnknownSchema "fshw-outcome-v9" ]
            | _ -> false
        @>

[<Fact>]
let ``nothing filed is a miss of its own kind`` () =
    test <@ read (store ()) = KeyRead.NothingFiled @>

[<Fact>]
let ``the newest record answers and a fail outranks a pass at the same instant`` () =
    let s = store ()
    file s (recordOf LookupVerdict.Outcome.Fail testRun at) |> ignore

    file s (recordOf LookupVerdict.Outcome.Pass testRun (at.AddMinutes 1.0))
    |> ignore

    test <@ KeyRead.outcome (read s) = Some LookupVerdict.Outcome.Pass @>

    let tie = store ()
    file tie (recordOf LookupVerdict.Outcome.Fail testRun at) |> ignore
    file tie (recordOf LookupVerdict.Outcome.Pass testRun at) |> ignore
    test <@ KeyRead.outcome (read tie) = Some LookupVerdict.Outcome.Fail @>

    test
        <@ KeyRead.history (read tie) |> List.map _.Outcome = [ LookupVerdict.Outcome.Pass; LookupVerdict.Outcome.Fail ] @>

[<Fact>]
let ``the same record filed twice is filed once`` () =
    let s = store ()
    let r = recordOf LookupVerdict.Outcome.Pass testRun at
    test <@ file s r = Filing.Filed @>
    test <@ file s r = Filing.AlreadyFiled @>
    test <@ s.FileNames key.Value |> List.length = 1 @>

[<Fact>]
let ``superseding leaves the key one record`` () =
    let s = store ()
    file s (recordOf LookupVerdict.Outcome.Fail testRun at) |> ignore

    (s :> IOutcomeStore).Supersede(recordOf LookupVerdict.Outcome.Pass testRun (at.AddMinutes 1.0))
    |> ignore

    test <@ KeyRead.history (read s) |> List.map _.Outcome = [ LookupVerdict.Outcome.Pass ] @>

[<Fact>]
let ``every kind of run round-trips through the codec`` () =
    for ran in
        [ testRun
          Ran.TestRun
              { RunId = None
                Counts =
                  { Total = 1
                    Passed = 0
                    Failed = 1
                    Skipped = 0 }
                DurationMs = None }
          Ran.InRun "run-7"
          Ran.NonTest ] do
        let r =
            { recordOf LookupVerdict.Outcome.Pass ran at with
                Outputs = Some """{"a":[1,2]}""" }

        let text = RecordCodec.envelope (RecordCodec.encodePayload r)
        test <@ RecordCodec.decode key.Value text = Ok r @>

/// A duration belongs to a test run. A payload with one and no counts is not a record this
/// format can mean, so it is torn, a miss, never a record of something else.
[<Fact>]
let ``a duration without counts is torn`` () =
    let payload =
        RecordCodec.encodePayload (recordOf LookupVerdict.Outcome.Pass Ran.NonTest at)
        |> fun p -> p.Replace("\"producer\":", "\"durationMs\":5,\"producer\":")

    test
        <@
            RecordCodec.decode key.Value (RecordCodec.envelope payload) = Error(
                Rejected.Torn "a duration without counts"
            )
        @>

/// The disk store files through the same codec: the same record gets the same name in both.
[<Fact>]
let ``the disk store and the in-memory store file a record under the same name`` () =
    FsHotWatch.Tests.TestHelpers.withTempDir "outcome-records" (fun dir ->
        let disk =
            OutcomeStore.Store(
                { Root = dir
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        let memory = store ()
        let r = recordOf LookupVerdict.Outcome.Fail testRun at
        test <@ (disk :> IOutcomeStore).File r = Filing.Filed @>
        file memory r |> ignore

        let onDisk =
            IO.Directory.GetFiles(disk.DirectoryOf key.Value, "*.json")
            |> Array.map IO.Path.GetFileName
            |> List.ofArray

        test <@ onDisk = memory.FileNames key.Value @>
        test <@ KeyRead.history ((disk :> IOutcomeStore).Read key.Value) = [ r ] @>)
