/// A session's snapshot epoch and manifest (AUTOMATION-681 slice 3, ADR-045): a cohort
/// hashes exactly the paths it carries, the epoch advances only when the manifest changes,
/// and every manifest is an immutable value whose id is its content.
module FsHotWatch.Tests.SnapshotEpochTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.SnapshotEpoch
open FsHotWatch.Tests.TestHelpers

let private root = Path.Combine(Path.GetTempPath(), "snapshot-root")
let private at (relative: string) = Path.Combine(root, relative)

/// A reader over a fixed table that records every path it was asked for.
let private readerOf (table: Map<string, ContentRead>) =
    let asked = Collections.Generic.List<string>()

    let read (path: string) =
        asked.Add path
        table |> Map.tryFind path |> Option.defaultValue ContentRead.Absent

    read, asked

[<Fact>]
let ``an advance hashes exactly the paths it carries, and names them repository-relative`` () =
    let read, asked =
        readerOf (
            Map.ofList
                [ at "src/A.fs", ContentRead.Read("aa", 10L)
                  at "src/B.fs", ContentRead.Read("bb", 32L)
                  at "src/Untouched.fs", ContentRead.Read("cc", 999L) ]
        )

    let manifest, hashed =
        Manifest.advance read [ root ] [ at "src/A.fs"; at "src/B.fs" ] Manifest.empty

    test <@ List.ofSeq asked = [ at "src/A.fs"; at "src/B.fs" ] @>
    test <@ hashed = { Files = 2; Bytes = 42L } @>
    test <@ manifest.Epoch = SnapshotEpoch 1L @>

    test
        <@
            manifest.Entries = Map.ofList
                [ "src/A.fs", ManifestEntry.Content "aa"
                  "src/B.fs", ManifestEntry.Content "bb" ]
        @>

[<Fact>]
let ``a path read twice in one cohort is hashed once`` () =
    let read, asked = readerOf (Map.ofList [ at "A.fs", ContentRead.Read("aa", 4L) ])

    let _, hashed =
        Manifest.advance read [ root ] [ at "A.fs"; at "A.fs" ] Manifest.empty

    test <@ asked.Count = 1 @>
    test <@ hashed = { Files = 1; Bytes = 4L } @>

[<Fact>]
let ``a cohort that leaves every entry as it was does not advance the epoch`` () =
    let read, _ = readerOf (Map.ofList [ at "A.fs", ContentRead.Read("aa", 4L) ])
    let first, _ = Manifest.advance read [ root ] [ at "A.fs" ] Manifest.empty
    let second, hashed = Manifest.advance read [ root ] [ at "A.fs" ] first
    test <@ second = first @>
    test <@ hashed = { Files = 1; Bytes = 4L } @>

[<Fact>]
let ``a removed path is a tombstone and an unreadable one is never equal to content`` () =
    let read, _ =
        readerOf (
            Map.ofList
                [ at "A.fs", ContentRead.Read("aa", 4L)
                  at "Locked.fs", ContentRead.Unreadable ]
        )

    let first, _ = Manifest.advance read [ root ] [ at "A.fs" ] Manifest.empty

    let gone, hashed =
        Manifest.advance
            (fun p -> if p = at "A.fs" then ContentRead.Absent else read p)
            [ root ]
            [ at "A.fs"; at "Locked.fs" ]
            first

    test <@ hashed = Hashed.none @>
    test <@ gone.Epoch = SnapshotEpoch 2L @>

    test <@ gone.Entries = Map.ofList [ "A.fs", ManifestEntry.Removed; "Locked.fs", ManifestEntry.Unreadable ] @>

    test <@ gone.Id <> first.Id @>

[<Fact>]
let ``a manifest's id is its content, and the previous epoch's manifest is left intact`` () =
    let read, _ =
        readerOf (Map.ofList [ at "A.fs", ContentRead.Read("aa", 1L); at "B.fs", ContentRead.Read("bb", 1L) ])

    let forward, _ =
        Manifest.advance read [ root ] [ at "A.fs"; at "B.fs" ] Manifest.empty

    let reversed, _ =
        Manifest.advance read [ root ] [ at "B.fs"; at "A.fs" ] Manifest.empty

    test <@ forward.Id = reversed.Id @>
    test <@ forward.Id = Manifest.idOf forward.Entries @>
    test <@ forward.Id <> Manifest.empty.Id @>

    let changed, _ =
        Manifest.advance (fun _ -> ContentRead.Read("a2", 1L)) [ root ] [ at "A.fs" ] forward

    test <@ changed.Epoch = SnapshotEpoch 2L @>
    test <@ forward.Entries["A.fs"] = ManifestEntry.Content "aa" @>
    test <@ changed.Entries["A.fs"] = ManifestEntry.Content "a2" @>
    test <@ changed.Id <> forward.Id @>

[<Fact>]
let ``the epoch counts from zero`` () =
    test <@ SnapshotEpoch.value SnapshotEpoch.initial = 0L @>
    test <@ SnapshotEpoch.value (SnapshotEpoch.next SnapshotEpoch.initial) = 1L @>
    test <@ Manifest.empty.Epoch = SnapshotEpoch.initial @>
    test <@ Manifest.empty.Id = Manifest.idOf Map.empty @>

[<Fact>]
let ``a path is named relative to the root with forward slashes`` () =
    test <@ Manifest.relativePath [ root ] (Path.Combine(root, "src", "Deep", "A.fs")) = "src/Deep/A.fs" @>

    // The watcher reports real paths (`/private/var/…` for a macOS `/var/…`), a
    // preprocessor its own form: each is named against the root it lies under.
    let real = Path.Combine(Path.GetTempPath(), "real-root")
    test <@ Manifest.relativePath [ root; real ] (Path.Combine(real, "src", "A.fs")) = "src/A.fs" @>
    test <@ Manifest.relativePath [ root; real ] (Path.Combine(root, "src", "A.fs")) = "src/A.fs" @>
    // Under neither: named against the configured root, the first.
    test
        <@
            Manifest.relativePath [ root; real ] (Path.Combine(Path.GetTempPath(), "elsewhere", "A.fs")) = "../elsewhere/A.fs"
        @>

[<Fact>]
let ``reading a file hashes its bytes, and an absent one reads as absent`` () =
    withTempDir "snapshot-read" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        File.WriteAllText(file, "module A\n")

        match Manifest.readFile file with
        | ContentRead.Read(id, bytes) ->
            test <@ bytes = 9L @>
            test <@ id = FsHotWatch.ContentHash.ofBytes (File.ReadAllBytes file) @>
        | other -> failwith $"expected Read, got %A{other}"

        test <@ Manifest.readFile (Path.Combine(dir, "missing.fs")) = ContentRead.Absent @>
        test <@ Manifest.readFile (Path.Combine(dir, "no-such-dir", "A.fs")) = ContentRead.Absent @>
        // A directory exists and holds no bytes to read: it is unreadable, never absent.
        test <@ Manifest.readFile dir = ContentRead.Unreadable @>)

[<Fact>]
let ``a ledger advances its own manifest and sums what it hashed`` () =
    let read, _ =
        readerOf (Map.ofList [ at "A.fs", ContentRead.Read("aa", 3L); at "B.fs", ContentRead.Read("bb", 5L) ])

    let ledger = Ledger([ root ], read)
    test <@ ledger.Current = Manifest.empty @>
    test <@ ledger.Advance [] = None @>

    let first = ledger.Advance [ at "A.fs" ]

    test
        <@ first |> Option.map (fun a -> a.Manifest.Epoch, a.Hashed) = Some(SnapshotEpoch 1L, { Files = 1; Bytes = 3L }) @>

    // Unchanged content: hashed again, but no new epoch.
    test <@ ledger.Advance [ at "A.fs" ] = None @>

    let second = ledger.Advance [ at "B.fs" ]
    test <@ second |> Option.map (fun a -> a.Manifest.Epoch) = Some(SnapshotEpoch 2L) @>
    test <@ ledger.Current.Epoch = SnapshotEpoch 2L @>
    test <@ ledger.Hashed = { Files = 3; Bytes = 11L } @>

// ---------------------------------------------------------------------------
// Slice 4 (ADR-046): publication CAS on the epoch
// ---------------------------------------------------------------------------

[<Fact>]
let ``a publication of old bytes after a later epoch changed the file is refused, and counted`` () =
    let a = ref (ContentRead.Read("a1", 1L))

    let read (path: string) =
        if path = at "A.fs" then
            a.Value
        else
            ContentRead.Read("b1", 1L)

    let ledger = Ledger([ root ], read)
    // A scan captures epoch 0, and reads A.fs as a1.
    let captured = ledger.Current.Epoch
    test <@ ledger.Accepts(captured, at "A.fs", "a1") @>

    // A save of A.fs is the next epoch.
    a.Value <- ContentRead.Read("a2", 1L)
    ledger.Advance [ at "A.fs" ] |> ignore

    // The scan's check of a1 lands after: refused. One that read a2 is current.
    test <@ not (ledger.Accepts(captured, at "A.fs", "a1")) @>
    test <@ ledger.Accepts(captured, at "A.fs", "a2") @>
    // A path no later epoch changed is accepted whatever was read.
    test <@ ledger.Accepts(captured, at "B.fs", "anything") @>
    // Work captured at the new epoch is current about every path.
    test <@ ledger.Accepts(ledger.Current.Epoch, at "A.fs", "a1") @>
    test <@ ledger.Status.Refused = 1L @>

[<Fact>]
let ``a file removed at a later epoch refuses any earlier read of it`` () =
    let present = ref true

    let ledger =
        Ledger(
            [ root ],
            fun _ ->
                if present.Value then
                    ContentRead.Read("x", 1L)
                else
                    ContentRead.Absent
        )

    let captured = ledger.Current.Epoch
    ledger.Advance [ at "A.fs" ] |> ignore
    present.Value <- false
    ledger.Advance [ at "A.fs" ] |> ignore
    test <@ not (ledger.Accepts(captured, at "A.fs", "x")) @>

[<Fact>]
let ``a later change is matched under either spelling of the root`` () =
    let real = Path.Combine(Path.GetTempPath(), "real-root")
    let ledger = Ledger([ root; real ], fun _ -> ContentRead.Read("x", 1L))
    let captured = ledger.Current.Epoch
    ledger.Advance [ at "A.fs" ] |> ignore
    test <@ not (ledger.Accepts(captured, Path.Combine(real, "A.fs"), "old")) @>

[<Fact>]
let ``the status names the epoch, the manifest and the stale refusals`` () =
    let ledger = Ledger([ root ], fun _ -> ContentRead.Read("x", 1L))
    let captured = ledger.Current.Epoch
    ledger.Advance [ at "A.fs" ] |> ignore
    ledger.Accepts(captured, at "A.fs", "old") |> ignore
    let status = ledger.Status
    test <@ status.Epoch = SnapshotEpoch 1L @>
    test <@ status.Manifest = ledger.Current.Id @>
    test <@ status.Refused = 1L @>

[<Fact>]
let ``the status round-trips its wire form, and a malformed one reads as none`` () =
    let status =
        { Epoch = SnapshotEpoch 3L
          Manifest = "abc"
          Refused = 2L }

    let parse (json: string) =
        use doc = System.Text.Json.JsonDocument.Parse json
        SnapshotStatus.tryRead (doc.RootElement.Clone())

    test <@ parse ((SnapshotStatus.payload status).ToJsonString()) = Some status @>
    test <@ parse """{"epoch":"3","manifest":"abc","refused":2}""" = None @>
    test <@ parse """{"epoch":-1,"manifest":"abc","refused":2}""" = None @>
    test <@ parse """{"epoch":3,"manifest":"abc","refused":-2}""" = None @>
    test <@ parse """{"epoch":3,"refused":2}""" = None @>
    test <@ parse "null" = None @>
