/// AUTOMATION-1816 step 2b: the alternative-upstreams records live in the box-wide outcome
/// store as their own kind, so every workspace's daemon on a host reads what a land's daemon
/// wrote, and they are kept 14 days past their last rewrite.
module FsHotWatch.Tests.AlternativeKeyStoreTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.AlternativeKeys
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)

/// A clock whose wall time is `at`.
let private clockAt (at: DateTimeOffset) : HostClock.Clock =
    { WallNow = fun () -> at.UtcDateTime
      AwakeNow = fun () -> TimeSpan.Zero }

let private storeAt (root: string) (ttl: TimeSpan) =
    OutcomeStore.Store(
        { Root = root
          Ttl = ttl
          MaxBytes = 1_000_000_000L
          Log = ignore }
    )

let private recorded =
    AlternativeRecord.WithVerdict(
        { Signature = "sig"
          References = [ "src/A.fs" ]
          ReferencesFingerprint = "refs" },
        CheckVerdict.ofParts "diag" "fcs" "some:used" "none"
    )

/// A jj primary workspace and a secondary one of it, as `jj workspace add` lays them out.
let private twinWorkspaces (tmpDir: string) =
    let primary = Path.Combine(tmpDir, "primary")

    Directory.CreateDirectory(Path.Combine(primary, ".jj", "repo", "store"))
    |> ignore

    Directory.CreateDirectory(Path.Combine(primary, ".jj", "working_copy"))
    |> ignore

    let secondary = Path.Combine(tmpDir, "secondary")
    let jjDir = Path.Combine(secondary, ".jj")
    Directory.CreateDirectory jjDir |> ignore
    File.WriteAllText(Path.Combine(jjDir, "repo"), Path.GetRelativePath(jjDir, Path.Combine(primary, ".jj", "repo")))
    primary, secondary

[<Fact>]
let ``a record a daemon in workspace A writes is read by a fresh daemon in workspace B through the shared store`` () =
    withTempDir "alt-shared" (fun tmpDir ->
        let a, b = twinWorkspaces tmpDir
        // Both workspaces of one repository open the same box-wide store directory.
        test <@ OutcomeStore.directoryFor a = OutcomeStore.directoryFor b @>
        let root = Path.Combine(tmpDir, "cache", "outcomes")

        let writer =
            OutcomeAlternativeKeyStore(storeAt root OutcomeStore.defaultTtl, a, clockAt t0) :> IAlternativeKeyStore

        writer.Write "k1" recorded

        // A later daemon in the other workspace: a new store object over the same directory.
        let reader =
            OutcomeAlternativeKeyStore(storeAt root OutcomeStore.defaultTtl, b, clockAt t0) :> IAlternativeKeyStore

        test <@ reader.TryRead "k1" = Some recorded @>
        test <@ reader.TryRead "k2" = None @>)

[<Fact>]
let ``a rewrite replaces the record under its key, and the store holds one record per key`` () =
    withTempDir "alt-rewrite" (fun tmpDir ->
        let store = storeAt tmpDir OutcomeStore.defaultTtl

        let alternatives =
            OutcomeAlternativeKeyStore(store, tmpDir, clockAt t0) :> IAlternativeKeyStore

        alternatives.Write "k" (AlternativeRecord.Upstreams(AlternativeRecord.upstreams recorded))
        alternatives.Write "k" recorded
        test <@ alternatives.TryRead "k" = Some recorded @>
        test <@ store.History(OutcomeAlternativeKeyStore.KeyOf "k").Length = 1 @>)

[<Fact>]
let ``records not rewritten within 14 days are dropped by the store's gc even when the store keeps longer`` () =
    withTempDir "alt-retention" (fun tmpDir ->
        // A store that keeps everything else for 30 days.
        let store = storeAt tmpDir (TimeSpan.FromDays 30.0)

        let stale =
            OutcomeAlternativeKeyStore(store, tmpDir, clockAt t0) :> IAlternativeKeyStore

        stale.Write "old" recorded

        let fresh =
            OutcomeAlternativeKeyStore(store, tmpDir, clockAt (t0.AddDays 2.0)) :> IAlternativeKeyStore

        fresh.Write "young" recorded

        // Another kind, recorded at the same time as the stale record.
        let other = ActionKey.create "check" [ "file", "x" ]

        store.Record(OutcomeStore.Record.ofAction other "x" OutcomeStore.Outcome.Pass "b" tmpDir t0)
        |> ignore

        let evicted = store.Gc(t0.AddDays 15.0, Set.empty)

        test <@ evicted = [ (OutcomeAlternativeKeyStore.KeyOf "old").Value ] @>
        test <@ fresh.TryRead "old" = None @>
        test <@ fresh.TryRead "young" = Some recorded @>
        test <@ store.Lookup other <> None @>
        test <@ AlternativeKeys.Retention = TimeSpan.FromDays 14.0 @>)

[<Fact>]
let ``the per-workspace records directory is moved into the store once, dated by each file's last write`` () =
    withTempDir "alt-migrate" (fun tmpDir ->
        let legacy = Path.Combine(tmpDir, ".fshw", "cache", "alternative-keys")
        Directory.CreateDirectory legacy |> ignore
        File.WriteAllText(Path.Combine(legacy, "k1.json"), toJson recorded)
        File.SetLastWriteTimeUtc(Path.Combine(legacy, "k1.json"), t0.UtcDateTime)
        // Damaged and foreign files are not carried over.
        File.WriteAllText(Path.Combine(legacy, "k2.json"), "{ not json")
        File.WriteAllText(Path.Combine(legacy, "k3.json.abc.tmp"), toJson recorded)

        let store = storeAt (Path.Combine(tmpDir, "store")) OutcomeStore.defaultTtl

        let alternatives =
            OutcomeAlternativeKeyStore(store, tmpDir, clockAt (t0.AddDays 5.0))

        let moved = OutcomeAlternativeKeyStore.ImportLegacy legacy alternatives

        test <@ moved = 1 @>
        test <@ not (Directory.Exists legacy) @>
        test <@ (alternatives :> IAlternativeKeyStore).TryRead "k1" = Some recorded @>

        test <@ store.Lookup(OutcomeAlternativeKeyStore.KeyOf "k1") |> Option.map _.RecordedAt = Some t0 @>

        // Nothing to move the second time.
        test <@ OutcomeAlternativeKeyStore.ImportLegacy legacy alternatives = 0 @>)

[<Fact>]
let ``a record the store cannot file reads as none, and an unreadable legacy file is not moved`` () =
    withTempDir "alt-not-filed" (fun tmpDir ->
        // The store's directory is a file: nothing can be filed under it.
        let blocked = Path.Combine(tmpDir, "blocked")
        File.WriteAllText(blocked, "")

        let store =
            OutcomeAlternativeKeyStore(storeAt blocked OutcomeStore.defaultTtl, tmpDir, clockAt t0)

        (store :> IAlternativeKeyStore).Write "k" recorded
        test <@ (store :> IAlternativeKeyStore).TryRead "k" = None @>

        let legacy = Path.Combine(tmpDir, "alternative-keys")
        Directory.CreateDirectory legacy |> ignore
        // A record that reads but cannot be filed, and a file that cannot be read at all.
        File.WriteAllText(Path.Combine(legacy, "k1.json"), toJson recorded)

        File.CreateSymbolicLink(Path.Combine(legacy, "k2.json"), Path.Combine(tmpDir, "missing"))
        |> ignore

        test <@ OutcomeAlternativeKeyStore.ImportLegacy legacy store = 0 @>
        test <@ not (Directory.Exists legacy) @>)
