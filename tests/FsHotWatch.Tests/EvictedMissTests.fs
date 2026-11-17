/// AUTOMATION-1765: a lookup that misses because GC evicted its key is told apart from one of a
/// key never filed, and counted per record kind.
///
/// Measured on thellma-and-louisa (2026-11-17): every key GC evicted at the 2 GiB cap since
/// AUTOMATION-1583 was a testprune-analysis key, and how many of them were looked up again could
/// not be counted. The analysis lookup returns an option and logs nothing, and GC logs counts per
/// kind with no key names, so a re-lookup of an evicted key read exactly like one of a key never
/// filed. GC now files a tombstone of each key it evicts, beside the records and kept by its own
/// retention, and a lookup that finds nothing filed reads the tombstones to say which it was.
module FsHotWatch.Tests.EvictedMissTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.OutcomeStore
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero)

/// TestPrune's analysis kind (`AnalysisRecords.Kind`), the one kind GC evicted on the server.
[<Literal>]
let private AnalysisKind = "testprune-analysis"

let private storeAt (dir: string) (maxBytes: int64) (log: string -> unit) =
    Store(
        { Root = dir
          Ttl = TimeSpan.FromDays 14.0
          MaxBytes = maxBytes
          Log = log }
    )

let private fileAction (store: Store) (kind: string) (name: string) (at: DateTimeOffset) =
    let key = ActionKey.create kind [ "file", name ]
    let record = Record.ofAction key name Outcome.Pass "fshw" "ws" at
    test <@ store.RecordWithOutputs(record, Some """{"blob":"x"}""") @>
    key

let private evictedMisses (lines: string seq) =
    lines |> Seq.filter (fun l -> l.Contains "evicted-key miss") |> List.ofSeq

[<Fact>]
let ``a lookup of a key gc evicted is an evicted miss of its kind, and one of a key never filed is not`` () =
    withTempDir "evicted-miss" (fun dir ->
        let store = storeAt dir 1L ignore
        let first = fileAction store AnalysisKind "src/A.fs" t0
        let second = fileAction store AnalysisKind "src/B.fs" (t0.AddMinutes 1.0)
        let check = fileAction store "check" "src/A.fs" (t0.AddMinutes 2.0)
        let neverFiled = ActionKey.create AnalysisKind [ "file", "src/Never.fs" ]

        test
            <@
                store.Gc(t0.AddMinutes 3.0, Set.empty) |> Set.ofList = Set.ofList
                    [ first.Value; second.Value; check.Value ]
            @>

        // Another daemon over the same box-wide store, reading as the analysis lookup does.
        let logged = Collections.Concurrent.ConcurrentQueue<string>()

        let reader =
            storeAt dir defaultMaxBytes logged.Enqueue :> OutcomeRecords.IOutcomeStore

        test <@ Through.lookup reader neverFiled.Value = None @>
        test <@ evictedMisses logged |> List.isEmpty @>

        test <@ Through.lookup reader first.Value = None @>
        test <@ Through.lookup reader check.Value = None @>
        test <@ Through.lookup reader second.Value = None @>

        let misses = evictedMisses logged
        test <@ misses.Length = 3 @>
        test <@ misses[0].Contains(AnalysisKind + " " + first.Value.Text) @>
        test <@ misses[0].Contains(AnalysisKind + " evicted-key misses in this process: 1") @>
        test <@ misses[1].Contains("check " + check.Value.Text) @>
        test <@ misses[1].Contains "check evicted-key misses in this process: 1" @>
        test <@ misses[2].Contains(AnalysisKind + " evicted-key misses in this process: 2") @>)

[<Fact>]
let ``a key filed again after gc evicted it is a hit, not a miss`` () =
    withTempDir "evicted-miss" (fun dir ->
        let logged = Collections.Concurrent.ConcurrentQueue<string>()
        let store = storeAt dir 1L logged.Enqueue
        let key = fileAction store AnalysisKind "src/A.fs" t0
        test <@ store.Gc(t0.AddMinutes 1.0, Set.empty) = [ key.Value ] @>
        fileAction store AnalysisKind "src/A.fs" (t0.AddMinutes 2.0) |> ignore

        test <@ store.Lookup key <> None @>
        test <@ evictedMisses logged |> List.isEmpty @>)

[<Fact>]
let ``a tombstone past the store's ttl is collected, and the key's miss is no longer called evicted`` () =
    withTempDir "evicted-miss" (fun dir ->
        let store = storeAt dir 1L ignore
        let key = fileAction store AnalysisKind "src/A.fs" t0
        test <@ store.Gc(t0.AddMinutes 1.0, Set.empty) = [ key.Value ] @>
        store.Gc(t0.AddDays 15.0, Set.empty) |> ignore

        let logged = Collections.Concurrent.ConcurrentQueue<string>()
        let reader = storeAt dir 1L logged.Enqueue
        test <@ reader.Lookup key = None @>
        test <@ evictedMisses logged |> List.isEmpty @>)

[<Fact>]
let ``a tombstone file's name gives back the instant its gc ran, and no other name gives one`` () =
    let name = EvictedKeys.fileNameOf t0 "abc"
    test <@ EvictedKeys.evictedAtOf name = Some t0 @>
    test <@ EvictedKeys.evictedAtOf "notes.tsv" = None @>
    test <@ EvictedKeys.evictedAtOf "abcdefghijklmnopqrs-n.tsv" = None @>
    test <@ EvictedKeys.evictedAtOf "4000000000000000000-n.tsv" = None @>
    test <@ EvictedKeys.evictedAtOf (name.Replace(".tsv", ".json")) = None @>

[<Fact>]
let ``a tombstone file reads back the keys and kinds it names, skipping lines that are not one`` () =
    let a = ActionKey.create AnalysisKind [ "file", "src/A.fs" ]
    let b = ActionKey.create "check" [ "file", "src/B.fs" ]

    let text =
        EvictedKeys.encode [ a.Value, AnalysisKind; b.Value, "check" ]
        + "torn\nak1:zz\tcheck\n"
        + a.Value.Text
        + "\t\n"

    test
        <@
            EvictedKeys.decode t0 text = [ a.Value, { Kind = AnalysisKind; EvictedAt = t0 }
                                           b.Value, { Kind = "check"; EvictedAt = t0 } ]
        @>

[<Fact>]
let ``tombstone retention deletes the oldest files until the rest fit its cap, and leaves other files alone`` () =
    withTempDir "evicted-miss" (fun dir ->
        let tombstoneDir = Path.Combine(dir, EvictedKeys.DirectoryName)
        Directory.CreateDirectory tombstoneDir |> ignore
        let key = ActionKey.create AnalysisKind [ "file", "src/A.fs" ]
        let text = EvictedKeys.encode [ key.Value, AnalysisKind ]

        let names =
            [ for i in 0..2 ->
                  let name = EvictedKeys.fileNameOf (t0.AddHours(float i)) "n"
                  File.WriteAllText(Path.Combine(tombstoneDir, name), text)
                  name ]

        File.WriteAllText(Path.Combine(tombstoneDir, "README.tsv"), String('x', 10_000))
        let oneFile = int64 (Text.Encoding.UTF8.GetByteCount text)

        let collected =
            EvictedKeys.collect
                dir
                (t0.AddHours 3.0)
                { Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = oneFile }

        test <@ collected.Expired |> List.isEmpty @>
        test <@ collected.OverCap = List.take 2 names @>
        test <@ File.Exists(Path.Combine(tombstoneDir, List.last names)) @>
        test <@ File.Exists(Path.Combine(tombstoneDir, "README.tsv")) @>)

// One daemon's store reads the tombstones once and keeps them until their directory changes: a
// miss BEFORE a gc must not leave it blind to the keys that gc evicts.
[<Fact>]
let ``a store that missed before a gc reads that gc's tombstones on its next miss`` () =
    withTempDir "evicted-miss" (fun dir ->
        let logged = Collections.Concurrent.ConcurrentQueue<string>()
        let store = storeAt dir 1L logged.Enqueue
        let key = fileAction store AnalysisKind "src/A.fs" t0
        test <@ store.Lookup(ActionKey.create AnalysisKind [ "file", "src/Never.fs" ]) = None @>
        test <@ store.Gc(t0.AddMinutes 1.0, Set.empty) = [ key.Value ] @>

        test <@ store.Lookup key = None @>
        test <@ evictedMisses logged |> List.length = 1 @>)

[<Fact>]
let ``a gc whose tombstones cannot be filed says so, still evicts, and the miss is not called evicted`` () =
    withTempDir "evicted-miss" (fun dir ->
        let logged = Collections.Concurrent.ConcurrentQueue<string>()
        let store = storeAt dir 1L logged.Enqueue
        let key = fileAction store AnalysisKind "src/A.fs" t0
        // A file where the tombstones' directory belongs.
        File.WriteAllText(Path.Combine(dir, EvictedKeys.DirectoryName), "")

        test <@ store.Gc(t0.AddMinutes 1.0, Set.empty) = [ key.Value ] @>
        test <@ store.Lookup key = None @>
        test <@ logged |> Seq.exists (fun l -> l.Contains "could not file the tombstones") @>
        test <@ evictedMisses logged |> List.isEmpty @>)

[<Fact>]
let ``a tombstone file that cannot be read classifies nothing, and one that cannot be deleted is logged`` () =
    withTempDir "evicted-miss" (fun dir ->
        let logged = Collections.Concurrent.ConcurrentQueue<string>()
        let store = storeAt dir 1L logged.Enqueue
        let key = fileAction store AnalysisKind "src/A.fs" t0
        test <@ store.Gc(t0.AddMinutes 1.0, Set.empty) = [ key.Value ] @>
        let tombstoneDir = Path.Combine(dir, EvictedKeys.DirectoryName)
        let file = Directory.GetFiles tombstoneDir |> Array.exactlyOne
        let readable = File.GetUnixFileMode file
        File.SetUnixFileMode(file, UnixFileMode.None)

        try
            test <@ (storeAt dir 1L logged.Enqueue).Lookup key = None @>
            test <@ evictedMisses logged |> List.isEmpty @>
        finally
            File.SetUnixFileMode(file, readable)

        let writable = File.GetUnixFileMode tombstoneDir
        File.SetUnixFileMode(tombstoneDir, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

        try
            store.Gc(t0.AddDays 15.0, Set.empty) |> ignore
        finally
            File.SetUnixFileMode(tombstoneDir, writable)

        test <@ logged |> Seq.exists (fun l -> l.Contains "could not delete the tombstone file") @>
        test <@ File.Exists file @>)

[<Fact>]
let ``a tombstone directory that cannot be listed classifies nothing and does not fail the lookup`` () =
    withTempDir "evicted-miss" (fun dir ->
        let logged = Collections.Concurrent.ConcurrentQueue<string>()
        let store = storeAt dir 1L logged.Enqueue
        let key = fileAction store AnalysisKind "src/A.fs" t0
        test <@ store.Gc(t0.AddMinutes 1.0, Set.empty) = [ key.Value ] @>
        let tombstoneDir = Path.Combine(dir, EvictedKeys.DirectoryName)
        let listable = File.GetUnixFileMode tombstoneDir
        File.SetUnixFileMode(tombstoneDir, UnixFileMode.None)

        try
            test <@ (storeAt dir 1L logged.Enqueue).Lookup key = None @>
        finally
            File.SetUnixFileMode(tombstoneDir, listable)

        test <@ evictedMisses logged |> List.isEmpty @>)
