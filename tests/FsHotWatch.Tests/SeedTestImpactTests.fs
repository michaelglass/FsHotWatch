module FsHotWatch.Tests.SeedTestImpactTests

open System.Collections.Generic
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.SeedTestImpact
open FsHotWatch.Tests.TestHelpers

// These tests drive the seed decision through injectable `fileExists` /
// `readVersion` / `countMethods` / `copyFile` / `info` fakes (no real jj, no real
// sqlite, no disk), so they touch NO process-global state and stay parallel-safe
// without joining the LogGlobal / FileWatch DisableParallelization collections.
// One end-to-end test exercises the real `copyDonor` over two temp sqlite files.

let private localDb = "/repo/.fshw/test-impact.db"
let private donorDb = "/main/.fshw/test-impact.db"

/// A `run` invocation whose effects all throw if called — each test overrides
/// only the fakes its branch is allowed to touch, so an unexpected effect is a
/// loud failure rather than a silent pass.
let private runWith
    (fileExists: string -> bool)
    (readVersion: string -> int)
    (countMethods: string -> int)
    (copyFile: string -> string -> unit)
    (info: string -> string -> unit)
    (configOverride: string option)
    (defaultWorkspaceRoot: string option)
    =
    run fileExists readVersion countMethods copyFile info configOverride defaultWorkspaceRoot localDb

// --- schema guard predicate ---

[<Fact(Timeout = 15000)>]
let ``isCompatibleDonorSchema requires an exact match`` () =
    test <@ isCompatibleDonorSchema LocalSchemaVersion LocalSchemaVersion @>
    test <@ not (isCompatibleDonorSchema (LocalSchemaVersion + 1) LocalSchemaVersion) @>
    test <@ not (isCompatibleDonorSchema (LocalSchemaVersion - 1) LocalSchemaVersion) @>
    // 0 (pre-versioning marker) only matches a local 0, never the real version.
    test <@ not (isCompatibleDonorSchema 0 LocalSchemaVersion) @>
    test <@ isCompatibleDonorSchema 0 0 @>

// --- resolveSeedDonorOverride: rooted vs relative config value ---

[<Fact(Timeout = 15000)>]
let ``resolveSeedDonorOverride keeps a rooted path verbatim`` () =
    let rooted = Path.Combine(Path.GetTempPath(), "donor.db")
    test <@ resolveSeedDonorOverride "/repo" (Some rooted) = Some rooted @>

[<Fact(Timeout = 15000)>]
let ``resolveSeedDonorOverride resolves a relative path against repoRoot`` () =
    let result = resolveSeedDonorOverride "/repo" (Some "sub/donor.db")
    let expected = Path.GetFullPath(Path.Combine("/repo", "sub/donor.db"))
    test <@ result = Some expected @>

[<Fact(Timeout = 15000)>]
let ``resolveSeedDonorOverride is None when no override configured`` () =
    test <@ resolveSeedDonorOverride "/repo" None = None @>

// --- resolveDonorPath: override precedence + auto-detection ---

[<Fact(Timeout = 15000)>]
let ``resolveDonorPath prefers the config override when its file exists`` () =
    let result =
        resolveDonorPath (Some "/custom/donor.db") (Some "/main") (fun p -> p = "/custom/donor.db")

    test <@ result = Ok "/custom/donor.db" @>

[<Fact(Timeout = 15000)>]
let ``resolveDonorPath errors when the config override file is missing`` () =
    let result =
        resolveDonorPath (Some "/custom/donor.db") (Some "/main") (fun _ -> false)

    match result with
    | Error msg -> test <@ msg.Contains("/custom/donor.db") @>
    | Ok _ -> failwith "expected Error for a missing override donor"

[<Fact(Timeout = 15000)>]
let ``resolveDonorPath auto-detects the default workspace donor under .fshw`` () =
    let expected = Path.Combine(FsHotWatch.FsHwPaths.root "/main", "test-impact.db")
    let result = resolveDonorPath None (Some "/main") (fun p -> p = expected)
    test <@ result = Ok expected @>

[<Fact(Timeout = 15000)>]
let ``resolveDonorPath errors when there is no default workspace (non-jj or self is default)`` () =
    let result =
        resolveDonorPath None None (fun _ -> failwith "fileExists must not run with no donor root")

    match result with
    | Error msg -> test <@ msg.Contains("no default jj workspace donor") @>
    | Ok _ -> failwith "expected Error when no default workspace root"

[<Fact(Timeout = 15000)>]
let ``resolveDonorPath errors when the default workspace has no test-impact.db`` () =
    let result = resolveDonorPath None (Some "/main") (fun _ -> false)

    match result with
    | Error msg -> test <@ msg.Contains("no test-impact.db") @>
    | Ok _ -> failwith "expected Error when default workspace donor file is absent"

// --- shouldSeed: the pure decision over hand-built facts ---

[<Fact(Timeout = 15000)>]
let ``shouldSeed SEEDS when local is empty, donor present, schema matches`` () =
    let facts =
        { LocalExists = false
          LocalTestMethodCount = 0
          Donor =
            Ok
                { DbPath = donorDb
                  SchemaVersion = LocalSchemaVersion
                  TestMethodCount = 1436 } }

    test <@ shouldSeed facts = Seed(donorDb, 1436) @>

[<Fact(Timeout = 15000)>]
let ``shouldSeed SKIPS (AlreadyPopulated) when local has test methods`` () =
    let facts =
        { LocalExists = true
          LocalTestMethodCount = 42
          Donor =
            Ok
                { DbPath = donorDb
                  SchemaVersion = LocalSchemaVersion
                  TestMethodCount = 1436 } }

    // A populated local db wins even when a perfectly good donor is available.
    test <@ shouldSeed facts = AlreadyPopulated 42 @>

[<Fact(Timeout = 15000)>]
let ``shouldSeed NO-OPs (NoDonor) when no donor is available`` () =
    let facts =
        { LocalExists = false
          LocalTestMethodCount = 0
          Donor = Error "this IS the default workspace" }

    test <@ shouldSeed facts = NoDonor "this IS the default workspace" @>

[<Fact(Timeout = 15000)>]
let ``shouldSeed SKIPS (SchemaMismatch) when donor schema differs`` () =
    let facts =
        { LocalExists = false
          LocalTestMethodCount = 0
          Donor =
            Ok
                { DbPath = donorDb
                  SchemaVersion = LocalSchemaVersion - 1
                  TestMethodCount = 1436 } }

    test <@ shouldSeed facts = SchemaMismatch(LocalSchemaVersion - 1, LocalSchemaVersion) @>

[<Fact(Timeout = 15000)>]
let ``shouldSeed treats an existing-but-empty local db as seedable`` () =
    // LocalExists=true but 0 test methods (a created-but-never-indexed db) is
    // still empty for seeding purposes — prune would fall back to all-tests.
    let facts =
        { LocalExists = true
          LocalTestMethodCount = 0
          Donor =
            Ok
                { DbPath = donorDb
                  SchemaVersion = LocalSchemaVersion
                  TestMethodCount = 7 } }

    test <@ shouldSeed facts = Seed(donorDb, 7) @>

// --- run: the wired branches copy / skip / log correctly ---

[<Fact(Timeout = 15000)>]
let ``run copies the donor and logs when seeding`` () =
    let copies = List<string * string>()
    let logs = List<string>()

    let decision =
        runWith
            (fun p -> p = donorDb) // local absent, donor present
            (fun _ -> LocalSchemaVersion)
            (fun p -> if p = donorDb then 1436 else 0)
            (fun src dst -> copies.Add(src, dst))
            (fun _ msg -> logs.Add msg)
            None
            (Some "/main")

    test <@ decision = Seed(donorDb, 1436) @>
    test <@ List.ofSeq copies = [ (donorDb, localDb) ] @>
    test <@ logs.Count = 1 @>
    test <@ logs.[0].Contains("Seeded test-impact.db") @>
    test <@ logs.[0].Contains(donorDb) @>

[<Fact(Timeout = 15000)>]
let ``run skips (no copy) when local already has test methods`` () =
    let copies = List<string * string>()
    let logs = List<string>()

    let decision =
        runWith
            (fun _ -> true) // both exist
            (fun _ -> LocalSchemaVersion)
            (fun p -> if p = localDb then 99 else 1436)
            (fun src dst -> copies.Add(src, dst))
            (fun _ msg -> logs.Add msg)
            None
            (Some "/main")

    test <@ decision = AlreadyPopulated 99 @>
    test <@ copies.Count = 0 @>
    test <@ logs.[0].Contains("already has 99") @>

[<Fact(Timeout = 15000)>]
let ``run no-ops (no copy) when there is no donor`` () =
    let copies = List<string * string>()
    let logs = List<string>()

    let decision =
        runWith
            (fun _ -> false) // local absent
            (fun _ -> failwith "readVersion must not run with no donor")
            (fun _ -> 0)
            (fun src dst -> copies.Add(src, dst))
            (fun _ msg -> logs.Add msg)
            None
            None // no default workspace

    match decision with
    | NoDonor _ -> ()
    | other -> failwith $"expected NoDonor, got %A{other}"

    test <@ copies.Count = 0 @>
    test <@ logs.[0].Contains("Not seeding") @>

[<Fact(Timeout = 15000)>]
let ``run skips (no copy) on a schema mismatch`` () =
    let copies = List<string * string>()
    let logs = List<string>()

    let decision =
        runWith
            (fun p -> p = donorDb) // local absent, donor present
            (fun _ -> LocalSchemaVersion - 1) // donor too old
            (fun _ -> 1436)
            (fun src dst -> copies.Add(src, dst))
            (fun _ msg -> logs.Add msg)
            None
            (Some "/main")

    test <@ decision = SchemaMismatch(LocalSchemaVersion - 1, LocalSchemaVersion) @>
    test <@ copies.Count = 0 @>
    test <@ logs.[0].Contains("schema") @>

[<Fact(Timeout = 15000)>]
let ``run uses the config override in preference to auto-detection`` () =
    let copies = List<string * string>()
    let logs = List<string>()
    let overrideDonor = "/custom/seed.db"

    let decision =
        runWith
            (fun p -> p = overrideDonor) // ONLY the override donor exists
            (fun _ -> LocalSchemaVersion)
            (fun p -> if p = overrideDonor then 500 else 0)
            (fun src dst -> copies.Add(src, dst))
            (fun _ msg -> logs.Add msg)
            (Some overrideDonor)
            (Some "/main") // auto-detection would point elsewhere; override wins

    test <@ decision = Seed(overrideDonor, 500) @>
    test <@ List.ofSeq copies = [ (overrideDonor, localDb) ] @>

// --- end-to-end: real copyDonor over two temp sqlite files ---

/// Create a sqlite db at `path` with a `test_methods` table holding `count` rows
/// and `PRAGMA user_version = version`. Mirrors the donor shape the real seed
/// reads (schema version + test-method count).
let private makeSeedDb (path: string) (version: int) (count: int) : unit =
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore

    use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{path}")

    conn.Open()
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
        $"PRAGMA user_version = %d{version};\n\
          CREATE TABLE test_methods (symbol_id INTEGER PRIMARY KEY, test_project TEXT, test_class TEXT, test_method TEXT);"

    cmd.ExecuteNonQuery() |> ignore

    for i in 1..count do
        use ins = conn.CreateCommand()
        ins.CommandText <- $"INSERT INTO test_methods VALUES (%d{i}, 'P', 'C', 'm%d{i}');"
        ins.ExecuteNonQuery() |> ignore

[<Fact(Timeout = 30000)>]
let ``seedDefault end-to-end copies a real donor db into an empty workspace`` () =
    // Two sibling temp dirs: a "donor" repo with a populated db, and a "fresh"
    // workspace with no db. We point the config override straight at the donor
    // file (so the test never shells out to jj) and assert the local db lands
    // with the donor's schema version and test-method count.
    withTempDir "seed-donor" (fun donorRoot ->
        withTempDir "seed-fresh" (fun freshRoot ->
            let donorPath = Path.Combine(FsHotWatch.FsHwPaths.root donorRoot, "test-impact.db")
            makeSeedDb donorPath LocalSchemaVersion 3

            let localPath = Path.Combine(FsHotWatch.FsHwPaths.root freshRoot, "test-impact.db")
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)) |> ignore

            // Sanity: fresh workspace starts with no db.
            test <@ not (File.Exists localPath) @>

            let decision = seedDefault freshRoot localPath (Some donorPath)

            test <@ decision = Seed(donorPath, 3) @>
            test <@ File.Exists localPath @>
            // The copied db carries the donor's portable graph verbatim.
            test <@ readSchemaVersion localPath = LocalSchemaVersion @>
            test <@ countTestMethods localPath = 3 @>))

[<Fact(Timeout = 30000)>]
let ``seedDefault end-to-end is a no-op when the local db already has methods`` () =
    withTempDir "seed-donor2" (fun donorRoot ->
        withTempDir "seed-fresh2" (fun freshRoot ->
            let donorPath = Path.Combine(FsHotWatch.FsHwPaths.root donorRoot, "test-impact.db")
            makeSeedDb donorPath LocalSchemaVersion 5

            // Local already populated with a DIFFERENT count — must not be overwritten.
            let localPath = Path.Combine(FsHotWatch.FsHwPaths.root freshRoot, "test-impact.db")
            makeSeedDb localPath LocalSchemaVersion 11

            let decision = seedDefault freshRoot localPath (Some donorPath)

            test <@ decision = AlreadyPopulated 11 @>
            // Untouched: still the local 11, not the donor's 5.
            test <@ countTestMethods localPath = 11 @>))
