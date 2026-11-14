/// The stat-validated content-hash cache (E5, AUTOMATION-1696) is an accelerator only: a hash it
/// TRUSTS is the hash a full re-read gives, and anything it cannot vouch for is reported
/// untrusted and read again. The property drives a model filesystem, whose timestamps can be as
/// coarse as one second, through writes, touches, restored mtimes, renames over, unreadable
/// files and the clock moving; the real-filesystem tests check the native identity reader the
/// model stands in for.
module FsHotWatch.Tests.ContentHashCacheTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.FSharp
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

// --- the model filesystem -------------------------------------------------------------

/// One file in the model: its bytes, and the stat a kernel would report for it.
type private ModelFile =
    { Content: string
      Readable: bool
      Identity: FileIdentity.Identity }

/// What happens to the model between two lookups.
type private Op =
    /// New bytes, written in place (same inode).
    | Write of file: int * content: string
    /// mtime and ctime to now, bytes unchanged.
    | Touch of file: int
    /// mtime put back to what it was before the last change (`touch -r`, `rsync -a`); ctime
    /// still moves, because no call can set it.
    | RestoreMtime of file: int
    /// A new file renamed over the old path: a new inode, and an mtime copied from the old.
    | RenameOver of file: int * content: string
    /// Unreadable now (or readable again).
    | ToggleReadable of file: int
    /// The clock moves on by this many milliseconds.
    | Tick of ms: int
    /// Every file is looked up through the cache.
    | Look

let private fileCount = 3

let private opGen: Gen<Op> =
    let file = Gen.choose (0, fileCount - 1)
    // Same-length contents, so a write never shows in the size.
    let content = Gen.elements [ "aaaa"; "bbbb"; "cccc"; "dddd" ]

    Gen.frequency
        [ 4, Gen.map2 (fun f c -> Write(f, c)) file content
          2, Gen.map Touch file
          3, Gen.map RestoreMtime file
          2, Gen.map2 (fun f c -> RenameOver(f, c)) file content
          1, Gen.map ToggleReadable file
          4, Gen.map Tick (Gen.elements [ 0; 1; 300; 999; 1000; 1500; 2500; 4000 ])
          5, Gen.constant Look ]

/// A model with timestamps truncated to `granuleNs`, a clock, and the last mtime each file had
/// before its latest change (what a restore puts back).
type private Model(granuleNs: int64) =
    let mutable clockNs = 1_700_000_000_000_000_000L
    let mutable nextInode = 100UL
    let files = Collections.Generic.Dictionary<int, ModelFile>()
    let previousMtime = Collections.Generic.Dictionary<int, int64>()

    let stamp () = clockNs - clockNs % granuleNs

    let fresh (content: string) (mtime: int64) =
        nextInode <- nextInode + 1UL

        { Content = content
          Readable = true
          Identity =
            { Size = int64 content.Length
              ModifiedNs = mtime
              ChangedNs = stamp ()
              Inode = nextInode
              Device = 1UL } }

    do
        for f in 0 .. fileCount - 1 do
            files[f] <- fresh "aaaa" (stamp ())

    member _.Path(f: int) = "f" + string f
    member _.NowNs() = clockNs

    member _.Identify(path: string) : FileIdentity.Identity voption =
        let f = int (path.Substring 1)
        ValueSome files[f].Identity

    member _.HashFile(path: string) : string =
        let f = int (path.Substring 1)
        let file = files[f]

        if file.Readable then
            ContentHash.ofText file.Content
        else
            ContentHash.UnhashableContent

    member _.Apply(op: Op) =
        let changed f (update: ModelFile -> ModelFile) =
            previousMtime[f] <- files[f].Identity.ModifiedNs
            files[f] <- update files[f]

        match op with
        | Write(f, content) ->
            changed f (fun m ->
                { m with
                    Content = content
                    Identity =
                        { m.Identity with
                            ModifiedNs = stamp ()
                            ChangedNs = stamp () } })
        | Touch f ->
            changed f (fun m ->
                { m with
                    Identity =
                        { m.Identity with
                            ModifiedNs = stamp ()
                            ChangedNs = stamp () } })
        | RestoreMtime f ->
            let old =
                match previousMtime.TryGetValue f with
                | true, t -> t
                | _ -> files[f].Identity.ModifiedNs

            files[f] <-
                { files[f] with
                    Identity =
                        { files[f].Identity with
                            ModifiedNs = old
                            ChangedNs = stamp () } }
        | RenameOver(f, content) ->
            let mtime = files[f].Identity.ModifiedNs
            files[f] <- fresh content mtime
        | ToggleReadable f ->
            files[f] <-
                { files[f] with
                    Readable = not files[f].Readable
                    Identity =
                        { files[f].Identity with
                            ChangedNs = stamp () } }
        | Tick ms -> clockNs <- clockNs + int64 ms * 1_000_000L
        | Look -> ()

let private checkHistory (granuleNs: int64) (ops: Op list) (trusted: int ref) (untrusted: int ref) =
    let model = Model granuleNs

    let sources: ContentHashCache.Sources =
        { Identify = model.Identify
          HashFile = model.HashFile
          NowNs = model.NowNs }

    // The cache survives between looks the way the file on disk does: saved, then loaded.
    let mutable saved = ContentHashCache.serialize ContentHashCache.empty

    ops
    |> List.forall (fun op ->
        model.Apply op

        match op with
        | Look ->
            let cache = ContentHashCache.Cache(ContentHashCache.parse saved, sources)

            let agree =
                [ 0 .. fileCount - 1 ]
                |> List.forall (fun f ->
                    let path = model.Path f
                    let hash, answer = cache.Hash(path, path)

                    match answer with
                    | ContentHashCache.Answer.Trusted ->
                        trusted.Value <- trusted.Value + 1
                        hash = model.HashFile path
                    | ContentHashCache.Answer.Untrusted _ ->
                        untrusted.Value <- untrusted.Value + 1
                        // An untrusted answer is a fresh read, so it is the full hash too.
                        hash = model.HashFile path)

            saved <- ContentHashCache.serialize cache.Entries
            agree
        | _ -> true)

let private historyGen =
    gen {
        let! length = Gen.choose (1, 40)
        return! Gen.listOfLength length opGen
    }

let private checkGranule (granuleNs: int64) =
    let trusted = ref 0
    let untrusted = ref 0

    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(4000),
        Prop.forAll (Arb.fromGen historyGen) (fun ops -> checkHistory granuleNs ops trusted untrusted)
    )

    // A property in which the cache never trusts anything proves nothing about trusting.
    if trusted.Value < 2000 || untrusted.Value < 2000 then
        failwith $"only {trusted.Value} trusted and {untrusted.Value} untrusted answers: the generator misses a region"

[<Fact(Timeout = PropertyTestCapMs)>]
let ``a trusted hash is the full hash, on a filesystem with nanosecond timestamps`` () = checkGranule 1L

[<Fact(Timeout = PropertyTestCapMs)>]
let ``a trusted hash is the full hash, on a filesystem with one-second timestamps`` () = checkGranule 1_000_000_000L

// --- the file on disk ----------------------------------------------------------------

let private identity size mtime ctime inode : FileIdentity.Identity =
    { Size = size
      ModifiedNs = mtime
      ChangedNs = ctime
      Inode = inode
      Device = 7UL }

[<Fact>]
let ``entries survive a save and a load`` () =
    let entries =
        Map.ofList
            [ "a/b.fs",
              { ContentHashCache.Entry.Identity = identity 4L 10L 11L 12UL
                ContentHashCache.Entry.Hash = "abc"
                ContentHashCache.Entry.VerifiedAtNs = 99L }
              "c d/é.txt",
              { ContentHashCache.Entry.Identity = identity 0L -1L 0L UInt64.MaxValue
                ContentHashCache.Entry.Hash = "def"
                ContentHashCache.Entry.VerifiedAtNs = Int64.MaxValue } ]

    test <@ ContentHashCache.parse (ContentHashCache.serialize entries) = entries @>

[<Fact>]
let ``a torn, foreign or empty cache file loads as no entries`` () =
    let whole =
        ContentHashCache.serialize (
            Map.ofList
                [ "x",
                  { ContentHashCache.Entry.Identity = identity 1L 2L 3L 4UL
                    ContentHashCache.Entry.Hash = "h"
                    ContentHashCache.Entry.VerifiedAtNs = 5L } ]
        )

    test <@ ContentHashCache.parse "" = Map.empty @>
    test <@ ContentHashCache.parse (whole.Substring(0, whole.Length - 3)) = Map.empty @>

    test
        <@ ContentHashCache.parse (whole.Replace("fshw-content-hash-cache 1", "fshw-content-hash-cache 9")) = Map.empty @>

    test <@ ContentHashCache.parse "not a cache\n" = Map.empty @>

[<Fact>]
let ``a path a line cannot hold is hashed every time and never stored`` () =
    let sources: ContentHashCache.Sources =
        { Identify = fun _ -> ValueSome(identity 1L 0L 0L 1UL)
          HashFile = fun _ -> "h"
          NowNs = fun () -> Int64.MaxValue }

    let cache = ContentHashCache.Cache(Map.empty, sources)
    let _ = cache.Hash("tab\there", "x")
    let _ = cache.Hash("line\nbreak", "x")
    test <@ cache.Entries = Map.empty @>

// --- the real filesystem -------------------------------------------------------------

let private nowNs () =
    (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100L

[<Fact>]
let ``the native identity reads the size and mtime the runtime reports`` () =
    withTempDir "identity" (fun dir ->
        let path = Path.Combine(dir, "f.txt")
        File.WriteAllText(path, "hello")
        let mtime = DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        File.SetLastWriteTimeUtc(path, mtime)

        match FileIdentity.read path with
        | ValueSome id ->
            let size, modified, changed = id.Size, id.ModifiedNs, id.ChangedNs
            test <@ size = 5L @>
            test <@ modified = (mtime - DateTime.UnixEpoch).Ticks * 100L @>
            test <@ changed > nowNs () - 60_000_000_000L @>
        | ValueNone -> failwith "no identity for a file that exists")

[<Fact>]
let ``the native identity keeps the inode across an in-place write and moves it on a rename over`` () =
    withTempDir "identity-inode" (fun dir ->
        let path = Path.Combine(dir, "f.txt")
        File.WriteAllText(path, "aaaa")
        let first = FileIdentity.read path
        File.WriteAllText(path, "bbbb")
        let rewritten = FileIdentity.read path
        let other = Path.Combine(dir, "g.txt")
        File.WriteAllText(other, "cccc")
        File.Move(other, path, true)
        let renamed = FileIdentity.read path

        match first, rewritten, renamed with
        | ValueSome a, ValueSome b, ValueSome c ->
            let inodeA, inodeB, inodeC = a.Inode, b.Inode, c.Inode
            let deviceA, deviceB = a.Device, b.Device
            test <@ inodeA = inodeB && deviceA = deviceB @>
            test <@ inodeC <> inodeA @>
        | _ -> failwith "no identity for a file that exists")

[<Fact>]
let ``no identity for a path that is not there`` () =
    withTempDir "identity-missing" (fun dir -> test <@ FileIdentity.read (Path.Combine(dir, "nope")) = ValueNone @>)

[<Fact>]
let ``on disk, a same-size write with its mtime restored is never trusted`` () =
    withTempDir "cache-restore" (fun dir ->
        let path = Path.Combine(dir, "f.txt")
        File.WriteAllText(path, "aaaa")
        let mtime = File.GetLastWriteTimeUtc path

        // A clock a minute ahead: the first read is old enough to trust at once.
        let sources =
            { ContentHashCache.Sources.real with
                NowNs = fun () -> nowNs () + 60_000_000_000L }

        let first = ContentHashCache.Cache(Map.empty, sources)
        let _ = first.Hash("f.txt", path)
        let again = ContentHashCache.Cache(first.Entries, sources)
        let hash, answer = again.Hash("f.txt", path)
        test <@ answer = ContentHashCache.Answer.Trusted && hash = ContentHash.ofFile path @>

        File.WriteAllText(path, "bbbb")
        File.SetLastWriteTimeUtc(path, mtime)
        let after = ContentHashCache.Cache(again.Entries, sources)
        let hash, answer = after.Hash("f.txt", path)
        test <@ answer <> ContentHashCache.Answer.Trusted @>
        test <@ hash = ContentHash.ofFile path @>)

[<Fact>]
let ``on disk, a file read moments after its last change is not trusted on the next look`` () =
    withTempDir "cache-racy" (fun dir ->
        let path = Path.Combine(dir, "f.txt")
        File.WriteAllText(path, "aaaa")
        let first = ContentHashCache.Cache(Map.empty, ContentHashCache.Sources.real)
        let _ = first.Hash("f.txt", path)
        let again = ContentHashCache.Cache(first.Entries, ContentHashCache.Sources.real)
        let _, answer = again.Hash("f.txt", path)
        test <@ answer = ContentHashCache.Answer.Untrusted ContentHashCache.UntrustedReason.Racy @>)

[<Fact>]
let ``an unreadable file is never stored`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "cache-unreadable" (fun dir ->
            let path = Path.Combine(dir, "f.txt")
            File.WriteAllText(path, "aaaa")
            File.SetUnixFileMode(path, UnixFileMode.None)

            try
                let cache = ContentHashCache.Cache(Map.empty, ContentHashCache.Sources.real)
                let hash, _ = cache.Hash("f.txt", path)
                test <@ hash = ContentHash.UnhashableContent @>
                test <@ cache.Entries = Map.empty @>
            finally
                File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact>]
let ``a tree hashed through the cache, cold and then warm, is the tree hashed in full`` () =
    withTempDir "cache-tree" (fun dir ->
        for rel in [ "src/a.fs"; "src/b.fs"; "tests/c.fs"; "README.md" ] do
            let path = Path.Combine(dir, rel)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, rel)

        let walked = TreeHash.files dir []
        let full = (TreeHash.ofWalkedWith TreeHash.Hashing.FullRehash walked).Hash
        let cacheFile = Path.Combine(dir, ".fshw", "content-hash-cache")

        let cached () =
            (TreeHash.ofWalkedWith (TreeHash.Hashing.StatValidated cacheFile) walked).Hash

        test <@ cached () = full @>
        test <@ File.Exists cacheFile @>
        test <@ cached () = full @>)

[<Fact>]
let ``a line that does not parse makes the whole file no entries`` () =
    let file (line: string) =
        "fshw-content-hash-cache 1\n" + line + "\nend 1\n"

    let good = "p\t1\t2\t3\t4\t5\t6\th"
    test <@ ContentHashCache.parse (file good) |> Map.count = 1 @>
    test <@ ContentHashCache.parse (file "p\t1\t2\t3\t4\t5\t6") = Map.empty @>
    test <@ ContentHashCache.parse (file "p\tx\t2\t3\t4\t5\t6\th") = Map.empty @>
    test <@ ContentHashCache.parse (file "p\t1\t2\t3\t-4\t5\t6\th") = Map.empty @>
    test <@ ContentHashCache.parse (file "p\t1\t2\t3\t4\t5\t6\t") = Map.empty @>
    test <@ ContentHashCache.parse ("fshw-content-hash-cache 1\n" + good + "\nend 2\n") = Map.empty @>

[<Fact>]
let ``a file with no identity is read every time and never stored`` () =
    let reads = ref 0

    let sources: ContentHashCache.Sources =
        { Identify = fun _ -> ValueNone
          HashFile =
            fun _ ->
                reads.Value <- reads.Value + 1
                "h"
          NowNs = fun () -> Int64.MaxValue }

    let cache = ContentHashCache.Cache(Map.empty, sources)

    test
        <@ cache.Hash("a", "a") = ("h", ContentHashCache.Answer.Untrusted ContentHashCache.UntrustedReason.NoIdentity) @>

    test <@ cache.Entries = Map.empty && reads.Value = 1 @>

[<Fact>]
let ``a file whose identity moves during the read is not stored`` () =
    let identities = ref [ identity 1L 0L 0L 1UL; identity 1L 0L 5L 1UL ]

    let sources: ContentHashCache.Sources =
        { Identify =
            fun _ ->
                match identities.Value with
                | first :: rest ->
                    identities.Value <- rest
                    ValueSome first
                | [] -> ValueNone
          HashFile = fun _ -> "h"
          NowNs = fun () -> Int64.MaxValue }

    let cache = ContentHashCache.Cache(Map.empty, sources)
    let _, answer = cache.Hash("a", "a")
    test <@ answer = ContentHashCache.Answer.Untrusted ContentHashCache.UntrustedReason.NotCached @>
    test <@ cache.Entries = Map.empty @>

[<Fact>]
let ``a missing cache file loads as no entries, and an unwritable one is skipped`` () =
    withTempDir "cache-io" (fun dir ->
        test <@ ContentHashCache.load (Path.Combine(dir, "none")) = Map.empty @>

        let entries =
            Map.ofList
                [ "x",
                  { ContentHashCache.Entry.Identity = identity 1L 2L 3L 4UL
                    ContentHashCache.Entry.Hash = "h"
                    ContentHashCache.Entry.VerifiedAtNs = 5L } ]

        // A cache path under a FILE cannot be created: the save is skipped, never thrown.
        let blocker = Path.Combine(dir, "blocker")
        File.WriteAllText(blocker, "")
        ContentHashCache.save (Path.Combine(blocker, "cache")) Map.empty entries

        // Unchanged entries are not written at all.
        let path = Path.Combine(dir, "cache")
        ContentHashCache.save path entries entries
        test <@ not (File.Exists path) @>
        ContentHashCache.save path Map.empty entries
        test <@ ContentHashCache.load path = entries @>)

/// A `struct statx` as the kernel fills it, with `mask` in `stx_mask`.
let private statxBuffer (mask: uint32) =
    let buf = Array.zeroCreate<byte> 256

    let put (offset: int) (bytes: byte array) =
        Array.blit bytes 0 buf offset bytes.Length

    put 0 (BitConverter.GetBytes mask)
    put 32 (BitConverter.GetBytes 77UL)
    put 40 (BitConverter.GetBytes 12L)
    put 96 (BitConverter.GetBytes 3L)
    put 104 (BitConverter.GetBytes 4u)
    put 112 (BitConverter.GetBytes 5L)
    put 120 (BitConverter.GetBytes 6u)
    put 136 (BitConverter.GetBytes 8u)
    put 140 (BitConverter.GetBytes 9u)
    buf

[<Fact>]
let ``a statx reply reads as the identity at its documented offsets`` () =
    let expected: FileIdentity.Identity =
        { Size = 12L
          ModifiedNs = 5_000_000_006L
          ChangedNs = 3_000_000_004L
          Inode = 77UL
          Device = (8UL <<< 32) ||| 9UL }

    test <@ FileIdentity.parseLinux (statxBuffer 0x3C0u) = ValueSome expected @>
    // A filesystem that did not fill in the mtime gives no identity, so the file is read.
    test <@ FileIdentity.parseLinux (statxBuffer 0x380u) = ValueNone @>
