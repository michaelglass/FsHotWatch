module FsHotWatch.Tests.CheckCacheTests

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

    // Verify it's lowercase hex (only 0-9a-f characters)
    Assert.Matches("^[a-f0-9]+$", hash)
    // Verify no dashes or other separators
    Assert.DoesNotContain("-", hash)
