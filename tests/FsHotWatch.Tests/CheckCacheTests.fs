module FsHotWatch.Tests.CheckCacheTests

open System
open System.IO
open Xunit
open FsHotWatch.Events
open FsHotWatch.CheckCache

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
