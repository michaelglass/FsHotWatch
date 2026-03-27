module FsHotWatch.Tests.CheckCacheTests

open System
open System.IO
open Xunit
open FsHotWatch.Events
open FsHotWatch.CheckCache
open FsHotWatch.InMemoryCheckCache
open FsHotWatch.FileCheckCache

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
let ``TimestampCacheKeyProvider global cache is always invalid`` () =
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    Assert.False(provider.IsGlobalCacheValid())

[<Fact>]
let ``JjCacheKeyProvider delegates per-file hash to timestamp`` () =
    let jjProvider = JjCacheKeyProvider() :> ICacheKeyProvider
    let tsProvider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let tempFile = Path.GetTempFileName()
    File.WriteAllText(tempFile, "test content")

    try
        let jjHash = jjProvider.GetFileHash(tempFile)
        let tsHash = tsProvider.GetFileHash(tempFile)
        Assert.Equal<string>(jjHash, tsHash)
    finally
        File.Delete(tempFile)

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
