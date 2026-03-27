module FsHotWatch.Tests.CheckCacheTests

open System
open System.IO
open Xunit
open FsHotWatch.Events
open FsHotWatch.CheckCache
open FsHotWatch.InMemoryCheckCache
open FsHotWatch.FileCheckCache
open FsHotWatch.JjHelper

[<Fact>]
let ``CacheKey produces consistent hash for same inputs`` () =
    let key1 =
        { FileHash = "abc123"
          ProjectOptionsHash = "def456" }

    let key2 =
        { FileHash = "abc123"
          ProjectOptionsHash = "def456" }

    let hash1 = hashCacheKey key1
    let hash2 = hashCacheKey key2

    Assert.Equal(hash1, hash2)

[<Fact>]
let ``CacheKey produces different hash for different FileHash`` () =
    let key1 =
        { FileHash = "abc123"
          ProjectOptionsHash = "def456" }

    let key2 =
        { FileHash = "xyz789"
          ProjectOptionsHash = "def456" }

    let hash1 = hashCacheKey key1
    let hash2 = hashCacheKey key2

    Assert.NotEqual<string>(hash1, hash2)

[<Fact>]
let ``CacheKey produces different hash for different ProjectOptionsHash`` () =
    let key1 =
        { FileHash = "abc123"
          ProjectOptionsHash = "def456" }

    let key2 =
        { FileHash = "abc123"
          ProjectOptionsHash = "xyz789" }

    let hash1 = hashCacheKey key1
    let hash2 = hashCacheKey key2

    Assert.NotEqual<string>(hash1, hash2)

[<Fact>]
let ``hash format is lowercase hex with no dashes`` () =
    let key =
        { FileHash = "abc123"
          ProjectOptionsHash = "def456" }

    let hash = hashCacheKey key

    Assert.Matches("^[a-f0-9]+$", hash)
    Assert.DoesNotContain("-", hash)

[<Fact>]
let ``TimestampCacheKeyProvider returns consistent hash for same file`` () =
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let tempFile = Path.GetTempFileName()
    File.WriteAllText(tempFile, "test content")

    try
        let hash1 = provider.GetFileHash(tempFile)
        let hash2 = provider.GetFileHash(tempFile)
        Assert.Equal<string>(hash1, hash2)
    finally
        File.Delete(tempFile)

[<Fact>]
let ``TimestampCacheKeyProvider returns different hash after file modification`` () =
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let tempFile = Path.GetTempFileName()
    File.WriteAllText(tempFile, "original content")

    try
        let hash1 = provider.GetFileHash(tempFile)

        // Ensure mtime changes (some filesystems have 1s resolution)
        System.Threading.Thread.Sleep(1100)
        File.WriteAllText(tempFile, "modified content")

        let hash2 = provider.GetFileHash(tempFile)
        Assert.NotEqual<string>(hash1, hash2)
    finally
        File.Delete(tempFile)

[<Fact>]
let ``TimestampCacheKeyProvider returns lowercase hex hash`` () =
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let hash = provider.GetFileHash("/nonexistent/test.fs")

    Assert.Matches("^[a-f0-9]+$", hash)
    Assert.DoesNotContain("-", hash)
    Assert.True(hash.Length = 64)

[<Fact>]
let ``makeCacheKey produces different keys for different files`` () =
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let tempFile1 = Path.GetTempFileName()
    let tempFile2 = Path.GetTempFileName()
    File.WriteAllText(tempFile1, "content1")
    File.WriteAllText(tempFile2, "content2")

    let checker = FSharp.Compiler.CodeAnalysis.FSharpChecker.Create()

    let opts1, _ =
        checker.GetProjectOptionsFromScript(tempFile1, FSharp.Compiler.Text.SourceText.ofString "module A")
        |> Async.RunSynchronously

    let key1 = makeCacheKey provider tempFile1 opts1
    let key2 = makeCacheKey provider tempFile2 opts1

    try
        Assert.NotEqual<string>(key1.FileHash, key2.FileHash)
    finally
        File.Delete(tempFile1)
        File.Delete(tempFile2)

// --- InMemoryCheckCache tests ---

let private makeTestResult (file: string) (version: int64) : FileCheckResult =
    { File = file
      Source = "test"
      ParseResults = Unchecked.defaultof<_>
      CheckResults = Unchecked.defaultof<_>
      ProjectOptions = Unchecked.defaultof<_>
      Version = version }

let private makeKey (fileHash: string) : CacheKey =
    { FileHash = fileHash
      ProjectOptionsHash = "proj" }

[<Fact>]
let ``InMemoryCheckCache stores and retrieves results`` () =
    let cache = InMemoryCheckCache(10) :> ICheckCacheBackend
    let key = makeKey "file1"
    let result = makeTestResult "test.fs" 1L

    cache.Set key result

    match cache.TryGet key with
    | Some r -> Assert.Equal("test.fs", r.File)
    | None -> Assert.Fail("Expected Some but got None")

[<Fact>]
let ``InMemoryCheckCache returns None for missing key`` () =
    let cache = InMemoryCheckCache(10) :> ICheckCacheBackend
    let key = makeKey "nonexistent"

    Assert.True(cache.TryGet(key).IsNone)

[<Fact>]
let ``InMemoryCheckCache evicts LRU on overflow`` () =
    let cache = InMemoryCheckCache(2) :> ICheckCacheBackend
    let key1 = makeKey "a"
    let key2 = makeKey "b"
    let key3 = makeKey "c"

    cache.Set key1 (makeTestResult "a.fs" 1L)
    cache.Set key2 (makeTestResult "b.fs" 2L)
    // This should evict key1 (oldest)
    cache.Set key3 (makeTestResult "c.fs" 3L)

    Assert.True(cache.TryGet(key1).IsNone)
    Assert.True(cache.TryGet(key2).IsSome)
    Assert.True(cache.TryGet(key3).IsSome)

[<Fact>]
let ``InMemoryCheckCache LRU access refreshes entry`` () =
    let cache = InMemoryCheckCache(2) :> ICheckCacheBackend
    let key1 = makeKey "a"
    let key2 = makeKey "b"
    let key3 = makeKey "c"

    cache.Set key1 (makeTestResult "a.fs" 1L)
    cache.Set key2 (makeTestResult "b.fs" 2L)
    // Access key1 to refresh it — key2 is now the LRU
    cache.TryGet key1 |> ignore
    // This should evict key2 (now the oldest)
    cache.Set key3 (makeTestResult "c.fs" 3L)

    Assert.True(cache.TryGet(key1).IsSome)
    Assert.True(cache.TryGet(key2).IsNone)
    Assert.True(cache.TryGet(key3).IsSome)

[<Fact>]
let ``InMemoryCheckCache invalidates entry`` () =
    let cache = InMemoryCheckCache(10) :> ICheckCacheBackend
    let key = makeKey "file1"

    cache.Set key (makeTestResult "test.fs" 1L)
    cache.Invalidate key

    Assert.True(cache.TryGet(key).IsNone)

[<Fact>]
let ``InMemoryCheckCache updates existing key with new value`` () =
    let cache = InMemoryCheckCache(10) :> ICheckCacheBackend
    let key = makeKey "file1"

    cache.Set key (makeTestResult "test.fs" 1L)
    cache.Set key (makeTestResult "test.fs" 2L)

    match cache.TryGet key with
    | Some r -> Assert.Equal(2L, r.Version)
    | None -> Assert.Fail("Expected Some but got None")

[<Fact>]
let ``InMemoryCheckCache clear removes all entries`` () =
    let cache = InMemoryCheckCache(10) :> ICheckCacheBackend
    let key1 = makeKey "a"
    let key2 = makeKey "b"
    let key3 = makeKey "c"

    cache.Set key1 (makeTestResult "a.fs" 1L)
    cache.Set key2 (makeTestResult "b.fs" 2L)
    cache.Set key3 (makeTestResult "c.fs" 3L)

    cache.Clear()

    Assert.True(cache.TryGet(key1).IsNone)
    Assert.True(cache.TryGet(key2).IsNone)
    Assert.True(cache.TryGet(key3).IsNone)

// --- FileCheckCache tests ---

[<Fact>]
let ``FileCheckCache stores and retrieves results`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let cache = FileCheckCache(tempDir) :> ICheckCacheBackend
        let key = makeKey "file1"
        let result = makeTestResult "test.fs" 42L

        cache.Set key result

        match cache.TryGet key with
        | Some r ->
            Assert.Equal("test.fs", r.File)
            Assert.Equal(42L, r.Version)
        | None -> Assert.Fail("Expected Some but got None")
    finally
        Directory.Delete(tempDir, true)

[<Fact>]
let ``FileCheckCache returns None for missing key`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let cache = FileCheckCache(tempDir) :> ICheckCacheBackend
        let key = makeKey "nonexistent"
        Assert.True(cache.TryGet(key).IsNone)
    finally
        Directory.Delete(tempDir, true)

[<Fact>]
let ``FileCheckCache persists across instances`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let cache1 = FileCheckCache(tempDir) :> ICheckCacheBackend
        let key = makeKey "file1"
        cache1.Set key (makeTestResult "test.fs" 99L)

        // New instance simulates cold start
        let cache2 = FileCheckCache(tempDir) :> ICheckCacheBackend

        match cache2.TryGet key with
        | Some r ->
            Assert.Equal("test.fs", r.File)
            Assert.Equal(99L, r.Version)
        | None -> Assert.Fail("Expected cached result to persist across instances")
    finally
        Directory.Delete(tempDir, true)

[<Fact>]
let ``FileCheckCache invalidates entry`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let cache = FileCheckCache(tempDir) :> ICheckCacheBackend
        let key = makeKey "file1"
        cache.Set key (makeTestResult "test.fs" 1L)
        cache.Invalidate key
        Assert.True(cache.TryGet(key).IsNone)
    finally
        Directory.Delete(tempDir, true)

[<Fact>]
let ``FileCheckCache clear removes all entries`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let cache = FileCheckCache(tempDir) :> ICheckCacheBackend
        cache.Set (makeKey "a") (makeTestResult "a.fs" 1L)
        cache.Set (makeKey "b") (makeTestResult "b.fs" 2L)
        cache.Clear()
        Assert.True(cache.TryGet(makeKey "a").IsNone)
        Assert.True(cache.TryGet(makeKey "b").IsNone)
    finally
        Directory.Delete(tempDir, true)

// --- JjScanGuard tests ---

[<Fact>]
let ``JjScanGuard returns SkipAll when commit_id matches stored`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fshw-jj-guard-{Guid.NewGuid():N}")
    let fshwDir = Path.Combine(tempDir, ".fshw")
    Directory.CreateDirectory(fshwDir) |> ignore
    File.WriteAllText(Path.Combine(fshwDir, "last-commit.id"), "abc123def456")

    try
        let guard =
            JjScanGuard(tempDir, getCommitId = (fun () -> Some "abc123def456"), getDiff = (fun _ -> Set.empty))

        let decision = guard.BeginScan()

        match decision with
        | SkipAll -> ()
        | other -> Assert.Fail($"Expected SkipAll but got %A{other}")
    finally
        Directory.Delete(tempDir, true)

[<Fact>]
let ``JjScanGuard returns CheckSubset when commit_id differs`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fshw-jj-guard-{Guid.NewGuid():N}")
    let fshwDir = Path.Combine(tempDir, ".fshw")
    Directory.CreateDirectory(fshwDir) |> ignore
    File.WriteAllText(Path.Combine(fshwDir, "last-commit.id"), "old_commit_id")

    let changedFiles = set [ "/repo/src/File.fs"; "/repo/src/Other.fs" ]

    try
        let guard =
            JjScanGuard(tempDir, getCommitId = (fun () -> Some "new_commit_id"), getDiff = (fun _from -> changedFiles))

        let decision = guard.BeginScan()

        match decision with
        | CheckSubset files -> Assert.Equal<Set<string>>(changedFiles, files)
        | other -> Assert.Fail($"Expected CheckSubset but got %A{other}")
    finally
        Directory.Delete(tempDir, true)

[<Fact>]
let ``JjScanGuard returns CheckAll when no stored commit_id`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fshw-jj-guard-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let guard =
            JjScanGuard(tempDir, getCommitId = (fun () -> Some "some_commit_id"), getDiff = (fun _ -> Set.empty))

        let decision = guard.BeginScan()

        match decision with
        | CheckAll -> ()
        | other -> Assert.Fail($"Expected CheckAll but got %A{other}")
    finally
        Directory.Delete(tempDir, true)

[<Fact>]
let ``JjScanGuard returns CheckAll when jj unavailable`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fshw-jj-guard-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let guard =
            JjScanGuard(tempDir, getCommitId = (fun () -> None), getDiff = (fun _ -> Set.empty))

        let decision = guard.BeginScan()

        match decision with
        | CheckAll -> ()
        | other -> Assert.Fail($"Expected CheckAll but got %A{other}")
    finally
        Directory.Delete(tempDir, true)

[<Fact>]
let ``JjScanGuard CommitScanSuccess writes commit_id to disk`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fshw-jj-guard-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let guard =
            JjScanGuard(tempDir, getCommitId = (fun () -> Some "written_commit_id"), getDiff = (fun _ -> Set.empty))

        guard.BeginScan() |> ignore
        guard.CommitScanSuccess()

        let storedId =
            File.ReadAllText(Path.Combine(tempDir, ".fshw", "last-commit.id")).Trim()

        Assert.Equal("written_commit_id", storedId)
    finally
        Directory.Delete(tempDir, true)

[<Fact>]
let ``JjScanGuard second scan returns SkipAll after CommitScanSuccess`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fshw-jj-guard-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let commitId = "stable_commit_id"

        let guard =
            JjScanGuard(tempDir, getCommitId = (fun () -> Some commitId), getDiff = (fun _ -> Set.empty))

        // First scan: CheckAll (no stored commit_id)
        match guard.BeginScan() with
        | CheckAll -> ()
        | other -> Assert.Fail($"Expected CheckAll on first scan but got %A{other}")

        guard.CommitScanSuccess()

        // Second scan: SkipAll (commit_id now matches stored)
        match guard.BeginScan() with
        | SkipAll -> ()
        | other -> Assert.Fail($"Expected SkipAll on second scan but got %A{other}")
    finally
        Directory.Delete(tempDir, true)
