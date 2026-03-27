module FsHotWatch.Tests.CheckCacheTests

open System
open Xunit
open FsHotWatch.Events
open FsHotWatch.CheckCache
open FSharp.Compiler.CodeAnalysis

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

    // Verify it's lowercase hex (only 0-9a-f characters)
    Assert.Matches("^[a-f0-9]+$", hash)
    // Verify no dashes or other separators
    Assert.DoesNotContain("-", hash)

[<Fact>]
let ``getFileHash returns consistent hash for same file`` () =
    // Create a temp file
    let tempFile = System.IO.Path.GetTempFileName()
    System.IO.File.WriteAllText(tempFile, "test content")

    let hash1 = getFileHash tempFile
    let hash2 = getFileHash tempFile

    try
        Assert.Equal<string>(hash1, hash2)
    finally
        System.IO.File.Delete(tempFile)

[<Fact>]
let ``getFileHash returns lowercase hex hash`` () =
    // Test that hash format is correct
    let hash = getFileHash "/nonexistent/test.fs"

    // Verify it's lowercase hex (only 0-9a-f characters)
    Assert.Matches("^[a-f0-9]+$", hash)
    // Verify no dashes or other separators
    Assert.DoesNotContain("-", hash)
    // Verify reasonable length (SHA256 is 64 hex chars)
    Assert.True(hash.Length = 64)
