/// The evidence store may only ever answer "reuse" with an entry it can read back exactly
/// as it was written, for the key asked about, within its TTL. Everything else is a miss,
/// and a miss means the project runs.
module FsHotWatch.Tests.TestEvidenceStoreTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.TestEvidenceStore
open FsHotWatch.Tests.TestHelpers

let private keyFor (closure: string) : TestEvidenceKey.Key =
    TestEvidenceKey.ofLabels [ "toolchain", "t1"; "closure", closure; "shared", "s1" ]

let private t0 = DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)

let private entryFor (project: string) (key: TestEvidenceKey.Key) (earnedAt: DateTimeOffset) =
    { Key = key.Value
      Project = project
      KeyInputs = key.Labels
      Producer =
        { Worktree = "ws-a"
          RunId = "run-1"
          TreeHash = "sha256:tree"
          ProducerHash = "fshw-1"
          EarnedAt = earnedAt }
      Summary =
        { Tests = 3
          Passed = 3
          Failed = 0
          Skipped = 0
          DurationMs = 1200L }
      Report = """{"results":{"summary":{"tests":3,"passed":3,"failed":0}}}"""
      Coverage =
        Some
            { Path = "coverage/T/coverage.cobertura.xml"
              Content = $"<source>%s{RepoRootPlaceholder}/src</source>" } }

let private withStore (body: string -> (DateTimeOffset -> unit) -> (unit -> string list) -> Store -> unit) =
    withTempDir "evstore" (fun dir ->
        let mutable now = t0
        let log = Collections.Concurrent.ConcurrentQueue<string>()

        let store =
            Store(
                { Root = dir
                  Ttl = TimeSpan.FromHours 72.0
                  MaxBytes = 1_000_000L
                  Now = fun () -> now
                  Log = log.Enqueue }
            )

        body dir (fun t -> now <- t) (fun () -> List.ofSeq log) store)

let private entryFiles (dir: string) =
    Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
    |> Seq.filter (fun f -> Path.GetFileName(Path.GetDirectoryName f).Length = 2)
    |> Seq.toList

[<Fact>]
let ``a written entry is read back as a hit for its key`` () =
    withStore (fun _ _ _ store ->
        let key = keyFor "c1"
        test <@ store.Write(entryFor "tests/T/T.fsproj" key t0) @>

        match store.Lookup("tests/T/T.fsproj", key) with
        | Lookup.Hit entry -> test <@ entry = entryFor "tests/T/T.fsproj" key t0 @>
        | Lookup.Miss miss -> failwith (describeMiss miss))

[<Fact>]
let ``a different key misses and names the label that moved`` () =
    withStore (fun _ _ _ store ->
        store.Write(entryFor "tests/T/T.fsproj" (keyFor "c1") t0) |> ignore
        test <@ store.Lookup("tests/T/T.fsproj", keyFor "c2") = Lookup.Miss(Miss.Moved [ "closure" ]) @>
        test <@ store.Lookup("tests/U/U.fsproj", keyFor "c2") = Lookup.Miss Miss.NoEntry @>)

[<Fact>]
let ``an entry for another project under the same key value is never a hit`` () =
    withStore (fun _ _ _ store ->
        let key = keyFor "c1"
        store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore

        test
            <@
                match store.Lookup("tests/U/U.fsproj", key) with
                | Lookup.Hit _ -> false
                | Lookup.Miss _ -> true
            @>)

let private assertQuarantined (dir: string) (key: TestEvidenceKey.Key) =
    let path = Path.Combine(dir, key.Value.Substring(0, 2), key.Value + ".json")
    test <@ not (File.Exists path) @>
    test <@ Directory.EnumerateFiles(Path.Combine(dir, "quarantine")) |> Seq.length = 1 @>

[<Fact>]
let ``a torn write is a miss and is quarantined`` () =
    withStore (fun dir _ _ store ->
        let key = keyFor "c1"
        store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore
        let path = Path.Combine(dir, key.Value.Substring(0, 2), key.Value + ".json")
        let whole = File.ReadAllText path
        File.WriteAllText(path, whole.Substring(0, whole.Length / 2))

        test
            <@
                match store.Lookup("tests/T/T.fsproj", key) with
                | Lookup.Miss(Miss.Corrupt _) -> true
                | _ -> false
            @>

        assertQuarantined dir key
        // And the next lookup is a plain miss, not a second quarantine.
        test <@ store.Lookup("tests/T/T.fsproj", key) = Lookup.Miss Miss.NoEntry @>)

[<Fact>]
let ``a payload whose checksum does not match is a miss and is quarantined`` () =
    withStore (fun dir _ _ store ->
        let key = keyFor "c1"
        store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore
        let path = Path.Combine(dir, key.Value.Substring(0, 2), key.Value + ".json")
        // A well-formed file whose payload was altered: "failed\":0" -> "failed\":1".
        File.WriteAllText(path, File.ReadAllText(path).Replace("\\u0022failed\\u0022:0", "\\u0022failed\\u0022:1"))

        test
            <@
                match store.Lookup("tests/T/T.fsproj", key) with
                | Lookup.Miss(Miss.Corrupt "checksum mismatch") -> true
                | _ -> false
            @>

        assertQuarantined dir key)

[<Fact>]
let ``an unknown schema is a miss and is quarantined`` () =
    withStore (fun dir _ _ store ->
        let key = keyFor "c1"
        store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore
        let path = Path.Combine(dir, key.Value.Substring(0, 2), key.Value + ".json")
        File.WriteAllText(path, File.ReadAllText(path).Replace(Schema, "fshw-test-evidence-v9"))
        test <@ store.Lookup("tests/T/T.fsproj", key) = Lookup.Miss(Miss.UnknownSchema "fshw-test-evidence-v9") @>
        assertQuarantined dir key)

[<Fact>]
let ``concurrent writers of one key leave one valid entry`` () =
    withStore (fun dir _ _ store ->
        let key = keyFor "c1"

        Parallel.For(0, 32, fun i -> store.Write(entryFor "tests/T/T.fsproj" key (t0.AddSeconds(float i))) |> ignore)
        |> ignore

        test <@ entryFiles dir |> List.length = 1 @>

        test
            <@
                Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories)
                |> Seq.isEmpty
            @>

        test
            <@
                match store.Lookup("tests/T/T.fsproj", key) with
                | Lookup.Hit _ -> true
                | Lookup.Miss _ -> false
            @>)

[<Fact>]
let ``an entry past the TTL is a miss`` () =
    withStore (fun _ setNow _ store ->
        let key = keyFor "c1"
        store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore
        setNow (t0.AddHours 71.0)

        test
            <@
                match store.Lookup("tests/T/T.fsproj", key) with
                | Lookup.Hit _ -> true
                | Lookup.Miss _ -> false
            @>

        setNow (t0.AddHours 73.0)
        test <@ store.Lookup("tests/T/T.fsproj", key) = Lookup.Miss(Miss.Expired(TimeSpan.FromHours 73.0)) @>)

[<Fact>]
let ``retention is bounded by bytes, evicts the oldest first, and logs each eviction`` () =
    withTempDir "evstore-bytes" (fun dir ->
        let log = Collections.Concurrent.ConcurrentQueue<string>()
        let probe = envelope (entryFor "tests/T/T.fsproj" (keyFor "c0") t0)
        // Room for three entries, not four.
        let budget = int64 (Text.Encoding.UTF8.GetByteCount probe) * 3L + 10L

        let store =
            Store(
                { Root = dir
                  Ttl = TimeSpan.FromHours 72.0
                  MaxBytes = budget
                  Now = fun () -> DateTimeOffset.UtcNow
                  Log = log.Enqueue }
            )

        let keys = [ for i in 1..4 -> keyFor $"c%d{i}" ]

        for (i, key) in List.indexed keys do
            store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore
            let path = Path.Combine(dir, key.Value.Substring(0, 2), key.Value + ".json")
            // Distinct, ordered write times regardless of the filesystem's resolution.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(float (i - 10)))
            store.Prune() |> ignore

        let remaining =
            entryFiles dir |> List.map Path.GetFileNameWithoutExtension |> Set.ofList

        test <@ remaining = (keys |> List.skip 1 |> List.map (fun k -> k.Value) |> Set.ofList) @>
        let evictions = log |> Seq.filter (fun l -> l.Contains "evicted") |> Seq.toList
        test <@ evictions.Length = 1 @>
        test <@ evictions.Head.Contains(keys.Head.Value) @>)

[<Fact>]
let ``expired entries are evicted by prune`` () =
    withStore (fun dir _ log store ->
        let key = keyFor "c1"
        store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore
        let path = Path.Combine(dir, key.Value.Substring(0, 2), key.Value + ".json")
        File.SetLastWriteTimeUtc(path, t0.UtcDateTime.AddHours -80.0)
        test <@ store.Prune() = [ path ] @>
        test <@ log () |> List.exists (fun l -> l.Contains "expired") @>)

[<Fact>]
let ``a coverage report is relativised on write and rebound on read`` () =
    let report =
        "<coverage><source>/work/ws-a/src</source><class filename=\"/work/ws-a/src/A.fs\"/></coverage>"

    let stored = relativise "/work/ws-a" report
    test <@ not (stored.Contains "/work/ws-a") @>

    test
        <@
            rebind "/work/ws-b" stored = "<coverage><source>/work/ws-b/src</source><class filename=\"/work/ws-b/src/A.fs\"/></coverage>"
        @>

[<Fact>]
let ``the kill switch reads 0 as off and anything else as on`` () =
    test <@ not (enabledBy "0") @>
    test <@ not (enabledBy " 0 ") @>
    test <@ enabledBy null @>
    test <@ enabledBy "" @>
    test <@ enabledBy "1" @>

[<Fact>]
let ``a secret in an env file never reaches the store's bytes`` () =
    withTempDir "evstore-secret" (fun root ->
        File.WriteAllText(Path.Combine(root, ".env.test"), "SECRET=hunter2-sentinel\n")
        Directory.CreateDirectory(Path.Combine(root, "tests/T")) |> ignore
        File.WriteAllText(Path.Combine(root, "tests/T/T.fsproj"), "<Project />")

        let request: TestEvidenceKey.Request =
            { Project = Path.Combine(root, "tests/T/T.fsproj")
              Closure = []
              Config =
                { TestEvidenceKey.defaultConfig with
                    EnvFiles = [ ".env.test" ] }
              TestConfig = "{}"
              Toolchain = "t"
              OptionsHash = fun _ -> Some "o" }

        let key =
            match TestEvidenceKey.inputsFor (TestEvidenceKey.snapshot root []) request with
            | Ok(inputs, _) -> TestEvidenceKey.compute inputs
            | Error e -> failwith e

        withStore (fun dir _ _ store ->
            store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore

            for f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) do
                test <@ not ((File.ReadAllText f).Contains "hunter2") @>))

[<Fact>]
let ``every miss reads as a reason`` () =
    test <@ describeMiss Miss.NoEntry = "no evidence for this project" @>
    test <@ describeMiss (Miss.Moved [ "closure"; "shared" ]) = "key moved: closure, shared" @>
    test <@ (describeMiss (Miss.Expired(TimeSpan.FromHours 73.0))).Contains "73 h" @>
    test <@ (describeMiss (Miss.Corrupt "torn")).Contains "torn" @>
    test <@ (describeMiss (Miss.UnknownSchema "v9")).Contains "v9" @>

let private writeRaw (dir: string) (key: TestEvidenceKey.Key) (text: string) =
    let path = Path.Combine(dir, key.Value.Substring(0, 2), key.Value + ".json")
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)
    path

[<Fact>]
let ``an envelope with no schema, no payload, or a payload missing a field is corrupt`` () =
    let entry = entryFor "tests/T/T.fsproj" (keyFor "c1") t0
    let written = envelope entry
    let payload = Text.Json.Nodes.JsonNode.Parse(written).["payload"].GetValue<string>()

    let wrap (p: string) =
        let o = Text.Json.Nodes.JsonObject()
        o["schema"] <- Schema
        o["checksum"] <- ContentHash.ofText p
        o["payload"] <- p
        o.ToJsonString()

    let corrupt (text: string) =
        match decode text with
        | Decoded.Corrupt _ -> true
        | _ -> false

    test <@ corrupt """{ "payload": "x" }""" @>
    test <@ corrupt $$"""{ "schema": "{{Schema}}" }""" @>
    test <@ corrupt (wrap (payload.Replace("\"project\"", "\"projekt\""))) @>
    test <@ corrupt (wrap (payload.Replace("\"tests\":3", "\"tests\":\"3\""))) @>
    test <@ corrupt (wrap (payload.Replace("\"summary\":{", "\"summary\":7,\"x\":{"))) @>
    // The writer escapes '+', so the timestamp is matched by its date and time alone.
    test <@ payload.Contains "2026-10-16T12:00:00" @>
    test <@ corrupt (wrap (payload.Replace("2026-10-16T12:00:00", "not a time"))) @>

    test
        <@
            match decode (wrap payload) with
            | Decoded.Entry e -> e = entry
            | _ -> false
        @>

[<Fact>]
let ``a key that is not a digest is never looked up or written`` () =
    withStore (fun dir _ log store ->
        let bogus: TestEvidenceKey.Key = { Value = "../../etc"; Labels = [] }
        test <@ store.Lookup("tests/T/T.fsproj", bogus) = Lookup.Miss Miss.NoEntry @>

        test
            <@
                not (
                    store.Write
                        { entryFor "tests/T/T.fsproj" (keyFor "c1") t0 with
                            Key = "../../etc" }
                )
            @>

        test <@ log () |> List.exists (fun l -> l.Contains "could not store") @>
        test <@ store.Root = dir @>)

[<Fact>]
let ``an entry filed under another key is quarantined, never served`` () =
    withStore (fun dir _ _ store ->
        let k1 = keyFor "c1"
        let k2 = keyFor "c2"
        store.Write(entryFor "tests/T/T.fsproj" k1 t0) |> ignore

        let stored =
            File.ReadAllText(Path.Combine(dir, k1.Value.Substring(0, 2), k1.Value + ".json"))

        writeRaw dir k2 stored |> ignore

        test
            <@
                match store.Lookup("tests/T/T.fsproj", k2) with
                | Lookup.Miss(Miss.Corrupt _) -> true
                | _ -> false
            @>)

[<Fact>]
let ``an unreadable entry is a miss`` () =
    if not (OperatingSystem.IsWindows()) then
        withStore (fun dir _ _ store ->
            let key = keyFor "c1"
            store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore
            let path = Path.Combine(dir, key.Value.Substring(0, 2), key.Value + ".json")
            File.SetUnixFileMode(path, UnixFileMode.None)

            try
                test <@ store.Lookup("tests/T/T.fsproj", key) = Lookup.Miss(Miss.Corrupt "unreadable") @>
            finally
                File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact>]
let ``an entry that cannot be quarantined is still a miss, and says so`` () =
    withStore (fun dir _ log store ->
        let key = keyFor "c1"
        let path = writeRaw dir key "{ torn"
        // A FILE where the quarantine directory belongs.
        File.WriteAllText(Path.Combine(dir, "quarantine"), "")

        test
            <@
                match store.Lookup("tests/T/T.fsproj", key) with
                | Lookup.Miss(Miss.Corrupt _) -> true
                | _ -> false
            @>

        test <@ File.Exists path @>
        test <@ log () |> List.exists (fun l -> l.Contains "could not quarantine") @>)

[<Fact>]
let ``a project index that is unreadable or names another project names nothing`` () =
    withStore (fun dir _ _ store ->
        store.Write(entryFor "tests/T/T.fsproj" (keyFor "c1") t0) |> ignore
        let index = Directory.GetFiles(Path.Combine(dir, "projects")) |> Array.exactlyOne
        File.WriteAllText(index, File.ReadAllText(index).Replace("tests/T/T.fsproj", "tests/U/U.fsproj"))
        test <@ store.Lookup("tests/T/T.fsproj", keyFor "c2") = Lookup.Miss Miss.NoEntry @>
        File.WriteAllText(index, "{ torn")
        test <@ store.Lookup("tests/T/T.fsproj", keyFor "c2") = Lookup.Miss Miss.NoEntry @>)

[<Fact>]
let ``pruning a store that was never written evicts nothing`` () =
    withTempDir "evstore-empty" (fun dir ->
        let store =
            Store(
                { Root = Path.Combine(dir, "never")
                  Ttl = TimeSpan.FromHours 1.0
                  MaxBytes = 1L
                  Now = fun () -> DateTimeOffset.UtcNow
                  Log = ignore }
            )

        test <@ List.isEmpty (store.Prune()) @>)

[<Fact>]
let ``an eviction that fails is logged and the entry stays`` () =
    if not (OperatingSystem.IsWindows()) then
        withStore (fun dir _ log store ->
            let key = keyFor "c1"
            store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore
            let shard = Path.Combine(dir, key.Value.Substring(0, 2))
            let path = Path.Combine(shard, key.Value + ".json")
            File.SetLastWriteTimeUtc(path, t0.UtcDateTime.AddHours -80.0)
            File.SetUnixFileMode(shard, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

            try
                test <@ List.isEmpty (store.Prune()) @>
                test <@ File.Exists path @>
                test <@ log () |> List.exists (fun l -> l.Contains "could not evict") @>
            finally
                File.SetUnixFileMode(
                    shard,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

[<Fact>]
let ``the store of a repository lives under the shared cache home`` () =
    withTempDir "evstore-home" (fun root ->
        let store = openFor root ignore
        test <@ store.Root.StartsWith(FsHwPaths.sharedCacheHome ()) @>
        test <@ store.Root.EndsWith(Path.Combine("evidence", RepoIdentity.namespaceOf root, "v1")) @>)

[<Fact>]
let ``an envelope missing half its seal, or a payload of the wrong shape, is corrupt`` () =
    let corrupt (text: string) =
        match decode text with
        | Decoded.Corrupt _ -> true
        | _ -> false

    test <@ corrupt $$"""{ "schema": "{{Schema}}", "checksum": "abc" }""" @>
    test <@ corrupt $$"""{ "schema": "{{Schema}}", "payload": "{}" }""" @>
    // A sealed payload whose digest is a number, not a string.
    let written = envelope (entryFor "tests/T/T.fsproj" (keyFor "c1") t0)
    let payload = Text.Json.Nodes.JsonNode.Parse(written).["payload"].GetValue<string>()
    let numeric = payload.Replace("\"closure\":\"c1\"", "\"closure\":1")
    test <@ numeric <> payload @>
    let o = Text.Json.Nodes.JsonObject()
    o["schema"] <- Schema
    o["checksum"] <- ContentHash.ofText numeric
    o["payload"] <- numeric
    test <@ corrupt (o.ToJsonString()) @>

[<Fact>]
let ``a write that cannot be renamed into place leaves no temp file and reports failure`` () =
    withStore (fun dir _ log store ->
        let key = keyFor "c1"
        // A DIRECTORY where the entry belongs: the rename cannot replace it.
        Directory.CreateDirectory(Path.Combine(dir, key.Value.Substring(0, 2), key.Value + ".json"))
        |> ignore

        test <@ not (store.Write(entryFor "tests/T/T.fsproj" key t0)) @>

        test
            <@
                Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories)
                |> Seq.isEmpty
            @>

        test <@ log () |> List.exists (fun l -> l.Contains "could not store") @>)

[<Fact>]
let ``relativising rewrites only a whole root, under every spelling of it`` () =
    test
        <@
            relativise "/w/repo" "/w/repo/src and /w/repo2/src and /w/repo" = $"%s{RepoRootPlaceholder}/src and /w/repo2/src and %s{RepoRootPlaceholder}"
        @>

    if not (OperatingSystem.IsWindows()) then
        withTempDir "evstore-link" (fun dir ->
            let real = Path.Combine(dir, "real")
            Directory.CreateDirectory real |> ignore
            let link = Path.Combine(dir, "link")
            Directory.CreateSymbolicLink(link, real) |> ignore
            test <@ physical link = physical real @>
            let text = $"<source>%s{physical real}/src</source><source>%s{link}/src</source>"
            let stored = relativise link text

            test
                <@ stored = $"<source>%s{RepoRootPlaceholder}/src</source><source>%s{RepoRootPlaceholder}/src</source>" @>)

[<Fact>]
let ``a path under a directory that cannot be searched resolves as far as it can`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "evstore-physical" (fun dir ->
            let locked = Path.Combine(dir, "locked")
            Directory.CreateDirectory(Path.Combine(locked, "inner")) |> ignore
            File.SetUnixFileMode(locked, UnixFileMode.None)

            try
                test <@ (physical (Path.Combine(locked, "inner"))).EndsWith(Path.Combine("locked", "inner")) @>
            finally
                File.SetUnixFileMode(
                    locked,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

[<Fact(Timeout = 10000)>]
let ``a cycle of links resolves to a path rather than looping`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "evstore-cycle" (fun dir ->
            let a = Path.Combine(dir, "a")
            let b = Path.Combine(dir, "b")
            File.CreateSymbolicLink(a, b) |> ignore
            File.CreateSymbolicLink(b, a) |> ignore
            let resolved = physical (Path.Combine(a, "x"))

            test
                <@
                    resolved.EndsWith(Path.Combine("a", "x"))
                    || resolved.EndsWith(Path.Combine("b", "x"))
                @>)

[<Fact>]
let ``retention never weighs or evicts a write still in flight`` () =
    withStore (fun dir _ _ store ->
        let key = keyFor "c1"
        store.Write(entryFor "tests/T/T.fsproj" key t0) |> ignore
        let shard = Path.Combine(dir, key.Value.Substring(0, 2))
        let inFlight = Path.Combine(shard, key.Value + ".json.0123.tmp")
        File.WriteAllText(inFlight, "partial")
        File.SetLastWriteTimeUtc(inFlight, t0.UtcDateTime.AddHours -80.0)
        test <@ List.isEmpty (store.Prune()) @>
        test <@ File.Exists inFlight @>)

[<Fact>]
let ``a quarantined file is weighed by retention and ages out like an entry`` () =
    withStore (fun dir _ log store ->
        let key = keyFor "c1"
        writeRaw dir key "{ torn" |> ignore
        store.Lookup("tests/T/T.fsproj", key) |> ignore

        let quarantined =
            Directory.GetFiles(Path.Combine(dir, "quarantine")) |> Array.exactlyOne

        File.SetLastWriteTimeUtc(quarantined, t0.UtcDateTime.AddHours -80.0)
        test <@ store.Prune() = [ quarantined ] @>
        test <@ not (File.Exists quarantined) @>
        test <@ log () |> List.exists (fun l -> l.Contains "evicted") @>)
