module FsHotWatch.Tests.CheckCacheTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.CheckCache
open FsHotWatch.InMemoryCheckCache
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 15000)>]
let ``CacheKey produces consistent hash for same inputs`` () =
    let key1 =
        { FileHash = ContentHash.create "abc123"
          ProjectOptionsHash = ContentHash.create "def456" }

    let key2 =
        { FileHash = ContentHash.create "abc123"
          ProjectOptionsHash = ContentHash.create "def456" }

    let hash1 = hashCacheKey key1
    let hash2 = hashCacheKey key2

    Assert.Equal(hash1, hash2)

[<Fact(Timeout = 15000)>]
let ``CacheKey produces different hash for different FileHash`` () =
    let key1 =
        { FileHash = ContentHash.create "abc123"
          ProjectOptionsHash = ContentHash.create "def456" }

    let key2 =
        { FileHash = ContentHash.create "xyz789"
          ProjectOptionsHash = ContentHash.create "def456" }

    let hash1 = hashCacheKey key1
    let hash2 = hashCacheKey key2

    Assert.NotEqual<string>(hash1, hash2)

[<Fact(Timeout = 15000)>]
let ``CacheKey produces different hash for different ProjectOptionsHash`` () =
    let key1 =
        { FileHash = ContentHash.create "abc123"
          ProjectOptionsHash = ContentHash.create "def456" }

    let key2 =
        { FileHash = ContentHash.create "abc123"
          ProjectOptionsHash = ContentHash.create "xyz789" }

    let hash1 = hashCacheKey key1
    let hash2 = hashCacheKey key2

    Assert.NotEqual<string>(hash1, hash2)

[<Fact(Timeout = 15000)>]
let ``hash format is lowercase hex with no dashes`` () =
    let key =
        { FileHash = ContentHash.create "abc123"
          ProjectOptionsHash = ContentHash.create "def456" }

    let hash = hashCacheKey key

    Assert.Matches("^[a-f0-9]+$", hash)
    Assert.DoesNotContain("-", hash)

[<Fact(Timeout = 15000)>]
let ``TimestampCacheKeyProvider returns consistent hash for same file`` () =
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let tempFile = Path.GetTempFileName()
    File.WriteAllText(tempFile, "test content")

    try
        let hash1 = provider.GetFileHash(tempFile)
        let hash2 = provider.GetFileHash(tempFile)
        Assert.Equal<string option>(hash1, hash2)
        Assert.True(hash1.IsSome, "expected Some for readable file")
    finally
        File.Delete(tempFile)

[<Fact(Timeout = 20000)>]
let ``cache key is content-addressed: same bytes + different mtime → same hash`` () =
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let tempFile = Path.GetTempFileName()
    File.WriteAllText(tempFile, "stable content")

    try
        let hash1 = provider.GetFileHash(tempFile)

        // Bump mtime by ~2s without touching content. With timestamp-based
        // hashing this would produce a different hash; with content-addressed
        // hashing the bytes determine the key, so it must stay the same.
        System.Threading.Thread.Sleep(1100)
        File.SetLastWriteTimeUtc(tempFile, DateTime.UtcNow)

        let hash2 = provider.GetFileHash(tempFile)
        Assert.Equal<string option>(hash1, hash2)
    finally
        File.Delete(tempFile)

[<Fact(Timeout = 20000)>]
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
        Assert.NotEqual<string option>(hash1, hash2)
    finally
        File.Delete(tempFile)

/// A hasher whose clock reads every file as older than the racy window, so it remembers
/// every hash it computes: the memo is what these tests exercise.
let private rememberingHasher () =
    FileContentHasher(fun () -> DateTime.UtcNow + FileContentHasher.RacyWindow + TimeSpan.FromHours 1.0)

/// Replaces `path` with `content` (the same length as before) through `replace`, then
/// puts back the last-write time it had: what a restore of an older DLL does.
let private restoreSameLengthPreservingMtime (replace: string -> string -> unit) (path: string) (content: string) =
    // Past the change-time granularity (a kernel tick on Linux), so the replacement's
    // status change is a different time from the original's; the racy window covers
    // this outside the tests.
    Threading.Thread.Sleep 50
    let mtime = File.GetLastWriteTimeUtc path
    let length = FileInfo(path).Length
    replace path content
    File.SetLastWriteTimeUtc(path, mtime)
    test <@ FileInfo(path).Length = length && File.GetLastWriteTimeUtc path = mtime @>

[<Fact(Timeout = 15000)>]
let ``FileContentHasher re-reads a file overwritten in place with its mtime restored`` () =
    withTempDir "hasher-inplace" (fun dir ->
        let path = Path.Combine(dir, "Lib.dll")
        File.WriteAllText(path, "original")
        let hasher = rememberingHasher ()
        let before = hasher.Hash path

        restoreSameLengthPreservingMtime (fun p c -> File.WriteAllText(p, c)) path "restored"

        test <@ hasher.Hash path <> before @>
        test <@ hasher.Hash path = FileContentHasher().Hash path @>)

[<Fact(Timeout = 15000)>]
let ``FileContentHasher re-reads a file renamed over with its mtime restored`` () =
    withTempDir "hasher-rename" (fun dir ->
        let path = Path.Combine(dir, "Lib.dll")
        File.WriteAllText(path, "original")
        let hasher = rememberingHasher ()
        let before = hasher.Hash path

        let renameOver (p: string) (c: string) =
            let staged = p + ".tmp"
            File.WriteAllText(staged, c)
            File.SetLastWriteTimeUtc(staged, File.GetLastWriteTimeUtc p)
            File.Move(staged, p, true)

        restoreSameLengthPreservingMtime renameOver path "restored"

        test <@ hasher.Hash path <> before @>
        test <@ hasher.Hash path = FileContentHasher().Hash path @>)

[<Fact(Timeout = 15000)>]
let ``FileContentHasher serves an untouched file from its memo`` () =
    withTempDir "hasher-memo" (fun dir ->
        let path = Path.Combine(dir, "Lib.dll")
        File.WriteAllText(path, "original")
        let hasher = rememberingHasher ()
        let before = hasher.Hash path
        // Locked against reading, and unchanged in every stamp field (a lock moves no
        // timestamp): only the memo can answer.
        use _lock =
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

        test <@ hasher.Hash path = before @>)

[<Fact(Timeout = 15000)>]
let ``FileContentHasher does not remember a file changed within the racy window`` () =
    withTempDir "hasher-racy" (fun dir ->
        let path = Path.Combine(dir, "Lib.dll")
        File.WriteAllText(path, "original")
        let hasher = FileContentHasher(fun () -> DateTime.UtcNow)
        hasher.Hash path |> ignore
        // A second write in the same change-time tick would leave the stamp as it was;
        // with nothing remembered, the next look reads the file again.
        use _lock =
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

        test <@ hasher.Hash path = "unreadable" @>)

[<Fact(Timeout = 15000)>]
let ``FileContentHasher reads an existing file whose stamp cannot be read on every look`` () =
    withTempDir "hasher-nostamp" (fun dir ->
        let path = Path.Combine(dir, "Lib.dll")
        File.WriteAllText(path, "original")
        let mutable looks = 0

        let noStamp (_: string) : FsHotWatch.FileStamp.Stamp voption =
            looks <- looks + 1
            ValueNone

        // A clock past the racy window: a file with a stamp would be remembered.
        let hasher =
            FileContentHasher((fun () -> DateTime.UtcNow + TimeSpan.FromHours 1.0), noStamp)

        let expected =
            Convert.ToHexString(Security.Cryptography.SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

        test <@ hasher.Hash path = expected @>
        // Locked against reading: nothing was remembered, so the file is read again.
        use _lock =
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

        test <@ hasher.Hash path = "unreadable" @>
        test <@ looks = 2 @>)

[<Fact(Timeout = 15000)>]
let ``TimestampCacheKeyProvider returns Some lowercase hex hash for readable file`` () =
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let tempFile = Path.GetTempFileName()
    File.WriteAllText(tempFile, "x")

    try
        match provider.GetFileHash(tempFile) with
        | Some hash ->
            Assert.Matches("^[a-f0-9]+$", hash)
            Assert.DoesNotContain("-", hash)
            Assert.True(hash.Length = 64)
        | None -> Assert.Fail "expected Some hash for readable file"
    finally
        File.Delete(tempFile)

[<Fact(Timeout = 15000)>]
let ``TimestampCacheKeyProvider returns None for unreadable file (F7: cache miss + retry)`` () =
    // F7 (docs/plans/2026-05-02-error-handling-audit.md): a synthesized
    // "unreadable:<path>" key lived forever in downstream caches, so a transient
    // lock that cleared on retry kept serving stale data. None forces a miss.
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let result = provider.GetFileHash("/nonexistent/test-f7.fs")
    Assert.True(result.IsNone, $"expected None for unreadable file, got %A{result}")

[<Fact(Timeout = 15000)>]
let ``makeCacheKey returns None when file is unreadable`` () =
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

    let opts, _ =
        checker.GetProjectOptionsFromScript(Path.GetTempFileName(), FSharp.Compiler.Text.SourceText.ofString "module A")
        |> Async.RunSynchronously

    let key = makeCacheKey provider "/nonexistent/missing-makeCacheKey.fs" opts
    Assert.True(key.IsNone, $"expected None CacheKey for unreadable file, got %A{key}")

[<Fact(Timeout = 15000)>]
let ``makeCacheKey produces different keys for different files`` () =
    let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider
    let tempFile1 = Path.GetTempFileName()
    let tempFile2 = Path.GetTempFileName()
    File.WriteAllText(tempFile1, "content1")
    File.WriteAllText(tempFile2, "content2")

    let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

    let opts1, _ =
        checker.GetProjectOptionsFromScript(tempFile1, FSharp.Compiler.Text.SourceText.ofString "module A")
        |> Async.RunSynchronously

    let key1 = makeCacheKey provider tempFile1 opts1
    let key2 = makeCacheKey provider tempFile2 opts1

    try
        match key1, key2 with
        | Some k1, Some k2 -> Assert.NotEqual<string>(ContentHash.value k1.FileHash, ContentHash.value k2.FileHash)
        | _ -> Assert.Fail $"expected both Some, got %A{key1}, %A{key2}"
    finally
        File.Delete(tempFile1)
        File.Delete(tempFile2)

// --- InMemoryCheckCache tests ---

/// An entry for `file` that referenced no project outputs.
let private makeTestResult (file: string) (version: int64) : CachedCheck =
    { Result =
        { File = AbsFilePath.create file
          Source = "test"
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = version
          ModelGeneration = None
          Frame = None }
      ProjectOutputs = [] }

let private makeKey (fileHash: string) : CacheKey =
    { FileHash = ContentHash.create fileHash
      ProjectOptionsHash = ContentHash.create "proj" }

[<Fact(Timeout = 15000)>]
let ``InMemoryCheckCache stores and retrieves results`` () =
    let cache = InMemoryCheckCache(10) :> ICheckCacheBackend
    let key = makeKey "file1"
    let result = makeTestResult "test.fs" 1L

    cache.Set key result

    match cache.TryGet key with
    | Some r -> Assert.Equal(AbsFilePath.create "test.fs", r.Result.File)
    | None -> Assert.Fail("Expected Some but got None")

[<Fact(Timeout = 15000)>]
let ``InMemoryCheckCache returns None for missing key`` () =
    let cache = InMemoryCheckCache(10) :> ICheckCacheBackend
    let key = makeKey "nonexistent"

    Assert.True(cache.TryGet(key).IsNone)

[<Fact(Timeout = 15000)>]
let ``InMemoryCheckCache evicts LRU on overflow`` () =
    let cache = InMemoryCheckCache(2) :> ICheckCacheBackend
    let key1 = makeKey "a"
    let key2 = makeKey "b"
    let key3 = makeKey "c"

    cache.Set key1 (makeTestResult "a.fs" 1L)
    cache.Set key2 (makeTestResult "b.fs" 2L)
    cache.Set key3 (makeTestResult "c.fs" 3L)

    Assert.True(cache.TryGet(key1).IsNone)
    Assert.True(cache.TryGet(key2).IsSome)
    Assert.True(cache.TryGet(key3).IsSome)

[<Fact(Timeout = 15000)>]
let ``InMemoryCheckCache LRU access refreshes entry`` () =
    let cache = InMemoryCheckCache(2) :> ICheckCacheBackend
    let key1 = makeKey "a"
    let key2 = makeKey "b"
    let key3 = makeKey "c"

    cache.Set key1 (makeTestResult "a.fs" 1L)
    cache.Set key2 (makeTestResult "b.fs" 2L)
    // Access key1 to refresh it — key2 is now the LRU, so it loses the eviction.
    cache.TryGet key1 |> ignore
    cache.Set key3 (makeTestResult "c.fs" 3L)

    Assert.True(cache.TryGet(key1).IsSome)
    Assert.True(cache.TryGet(key2).IsNone)
    Assert.True(cache.TryGet(key3).IsSome)

[<Fact(Timeout = 15000)>]
let ``InMemoryCheckCache invalidates entry`` () =
    let cache = InMemoryCheckCache(10) :> ICheckCacheBackend
    let key = makeKey "file1"

    cache.Set key (makeTestResult "test.fs" 1L)
    cache.Invalidate key

    Assert.True(cache.TryGet(key).IsNone)

[<Fact(Timeout = 15000)>]
let ``InMemoryCheckCache updates existing key with new value`` () =
    let cache = InMemoryCheckCache(10) :> ICheckCacheBackend
    let key = makeKey "file1"

    cache.Set key (makeTestResult "test.fs" 1L)
    cache.Set key (makeTestResult "test.fs" 2L)

    match cache.TryGet key with
    | Some r -> Assert.Equal(2L, r.Result.Version)
    | None -> Assert.Fail("Expected Some but got None")

[<Fact(Timeout = 15000)>]
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

// --- fcsCheckSignature ---

[<Fact(Timeout = 15000)>]
let ``fcsCheckSignature returns parse-only marker for ParseOnly`` () =
    let sig1 = fcsCheckSignature ParseOnly
    let sig2 = fcsCheckSignature ParseOnly
    Assert.Equal("parse-only", sig1)
    Assert.Equal(sig1, sig2)

[<Fact(Timeout = 15000)>]
let ``fcsCheckSignature differs between ParseOnly and FullCheck`` () =
    // Plugin caches must invalidate when FCS moves between parse-only and
    // full-check: the lint result depends on whether type info was available.
    let parseOnly = fcsCheckSignature ParseOnly
    let fullCheckNull = fcsCheckSignature (FullCheck(Unchecked.defaultof<_>))
    Assert.NotEqual<string>(parseOnly, fullCheckNull)

[<Fact(Timeout = 15000)>]
let ``fcsCheckSignature is stable across calls with same null FullCheck`` () =
    let s1 = fcsCheckSignature (FullCheck(Unchecked.defaultof<_>))
    let s2 = fcsCheckSignature (FullCheck(Unchecked.defaultof<_>))
    Assert.Equal(s1, s2)

// --- hashDiagnosticSignatures (extracted for testability without FCS) ---

let private mkSig line col errNo sev msg : DiagnosticSignature =
    { StartLine = line
      StartColumn = col
      ErrorNumber = errNo
      Severity = sev
      Message = msg }

[<Fact(Timeout = 2000)>]
let ``hashDiagnosticSignatures empty sequence is stable`` () =
    let h1 = hashDiagnosticSignatures Seq.empty
    let h2 = hashDiagnosticSignatures Seq.empty
    Assert.Equal(h1, h2)

[<Fact(Timeout = 2000)>]
let ``hashDiagnosticSignatures differs across distinct diagnostics`` () =
    let h1 = hashDiagnosticSignatures [ mkSig 1 2 100 "Error" "type mismatch" ]
    let h2 = hashDiagnosticSignatures [ mkSig 1 2 101 "Error" "type mismatch" ]
    Assert.NotEqual<string>(h1, h2)

[<Fact(Timeout = 2000)>]
let ``hashDiagnosticSignatures is order-independent (sorted internally)`` () =
    let a = mkSig 1 2 100 "Error" "first"
    let b = mkSig 5 8 200 "Warning" "second"
    Assert.Equal(hashDiagnosticSignatures [ a; b ], hashDiagnosticSignatures [ b; a ])

[<Fact(Timeout = 2000)>]
let ``hashDiagnosticSignatures distinguishes severity changes`` () =
    let warn = mkSig 1 2 100 "Warning" "msg"
    let err = mkSig 1 2 100 "Error" "msg"
    Assert.NotEqual<string>(hashDiagnosticSignatures [ warn ], hashDiagnosticSignatures [ err ])

[<Fact(Timeout = 2000)>]
let ``hashDiagnosticSignatures distinguishes message text changes`` () =
    let a = mkSig 1 2 100 "Error" "type int but expected string"
    let b = mkSig 1 2 100 "Error" "type string but expected int"
    Assert.NotEqual<string>(hashDiagnosticSignatures [ a ], hashDiagnosticSignatures [ b ])

[<Fact(Timeout = 2000)>]
let ``hashDiagnosticSignatures stable when same diagnostic list given twice`` () =
    let diags =
        [ mkSig 1 1 100 "Error" "a"
          mkSig 5 1 200 "Warning" "b"
          mkSig 10 1 300 "Info" "c" ]

    Assert.Equal(hashDiagnosticSignatures diags, hashDiagnosticSignatures diags)

// --- F1 regression: diagnostic-hash failures must not collide on a magic string ---

[<Fact(Timeout = 2000)>]
let ``hashDiagnosticsOrFailure folds exception class so distinct failures don't collide`` () =
    // F1 (docs/plans/2026-05-02-error-handling-audit.md): returning the literal
    // "full-check-error" for every failure mode gave two distinct exceptions the
    // same downstream cache key. The payload now folds in the exception class.
    let throwInvalidOp () : DiagnosticSignature seq =
        raise (System.InvalidOperationException "boom-1")

    let throwArg () : DiagnosticSignature seq =
        raise (System.ArgumentException "boom-2")

    let h1 = hashDiagnosticsOrFailure throwInvalidOp
    let h2 = hashDiagnosticsOrFailure throwArg
    Assert.NotEqual<string>(h1, h2)
    Assert.NotEqual<string>("full-check-error", h1)
    Assert.NotEqual<string>("full-check-error", h2)

[<Fact(Timeout = 2000)>]
let ``hashDiagnosticsOrFailure is deterministic across calls for the same failure`` () =
    let throwInvalidOp () : DiagnosticSignature seq =
        raise (System.InvalidOperationException "stable-message")

    let h1 = hashDiagnosticsOrFailure throwInvalidOp
    let h2 = hashDiagnosticsOrFailure throwInvalidOp
    Assert.Equal<string>(h1, h2)

[<Fact(Timeout = 2000)>]
let ``hashDiagnosticsOrFailure success path differs from any failure hash`` () =
    let success () : DiagnosticSignature seq = seq { mkSig 1 2 100 "Error" "ok" }

    let throwIo () : DiagnosticSignature seq = raise (System.IO.IOException "io-fail")

    Assert.NotEqual<string>(hashDiagnosticsOrFailure success, hashDiagnosticsOrFailure throwIo)

// --- upstreamFingerprint: what a file's check result depends on besides its own bytes ---

let private fakeHasher (contents: Map<string, string>) : string -> string =
    fun path -> contents |> Map.tryFind path |> Option.defaultValue "missing"

let private twoFileOptions =
    makeProjectOptions "/repo/P.fsproj" [ "/repo/A.fs"; "/repo/B.fs"; "/repo/C.fs" ] []

[<Fact(Timeout = 15000)>]
let ``upstreamFingerprint changes when a file EARLIER in the project changes`` () =
    // B is type-checked against A's signature, so B's cached result is stale once A
    // changes even though B's own bytes did not.
    let before =
        upstreamFingerprint
            (fakeHasher (Map [ "/repo/A.fs", "a1"; "/repo/B.fs", "b"; "/repo/C.fs", "c" ]))
            None
            "/repo/B.fs"
            twoFileOptions

    let after =
        upstreamFingerprint
            (fakeHasher (Map [ "/repo/A.fs", "a2"; "/repo/B.fs", "b"; "/repo/C.fs", "c" ]))
            None
            "/repo/B.fs"
            twoFileOptions

    Assert.NotEqual<string>(before, after)

[<Fact(Timeout = 15000)>]
let ``upstreamFingerprint ignores files LATER in the project`` () =
    // F# compilation order: B cannot see C, so an edit to C must not cost B its entry.
    let before =
        upstreamFingerprint
            (fakeHasher (Map [ "/repo/A.fs", "a"; "/repo/B.fs", "b"; "/repo/C.fs", "c1" ]))
            None
            "/repo/B.fs"
            twoFileOptions

    let after =
        upstreamFingerprint
            (fakeHasher (Map [ "/repo/A.fs", "a"; "/repo/B.fs", "b"; "/repo/C.fs", "c2" ]))
            None
            "/repo/B.fs"
            twoFileOptions

    Assert.Equal(before, after)

[<Fact(Timeout = 15000)>]
let ``upstreamFingerprint changes when a referenced project's source changes`` () =
    let lib = makeProjectOptions "/repo/Lib.fsproj" [ "/repo/Lib.fs" ] []

    let app =
        { makeProjectOptions "/repo/App.fsproj" [ "/repo/App.fs" ] [ "-r:/repo/obj/Lib.dll" ] with
            ReferencedProjects =
                [| FSharp.Compiler.CodeAnalysis.FSharpReferencedProject.FSharpReference("/repo/obj/Lib.dll", lib) |] }

    let fp libHash =
        upstreamFingerprint
            (fakeHasher (Map [ "/repo/Lib.fs", libHash; "/repo/App.fs", "app" ]))
            None
            "/repo/App.fs"
            app

    Assert.NotEqual<string>(fp "lib1", fp "lib2")

[<Fact(Timeout = 15000)>]
let ``upstreamFingerprint changes when a referenced assembly's content changes`` () =
    // A non-F# reference (a C# project's output, a vendored dll) is read by FCS as
    // metadata, so its bytes are an input too.
    let opts =
        makeProjectOptions "/repo/App.fsproj" [ "/repo/App.fs" ] [ "-r:/repo/lib/Vendored.dll" ]

    let fp dllHash =
        upstreamFingerprint
            (fakeHasher (Map [ "/repo/lib/Vendored.dll", dllHash; "/repo/App.fs", "app" ]))
            None
            "/repo/App.fs"
            opts

    Assert.NotEqual<string>(fp "v1", fp "v2")

[<Fact(Timeout = 15000)>]
let ``upstreamFingerprint does not hash assemblies outside the repository`` () =
    // NuGet and SDK assemblies live at version-qualified paths, so the path (already in
    // the options hash) identifies their content; hashing hundreds of them per lookup
    // would spend the CPU the cache exists to save.
    let hashed = System.Collections.Generic.List<string>()

    let hasher path =
        hashed.Add path
        "h"

    let opts =
        makeProjectOptions "/repo/App.fsproj" [ "/repo/App.fs" ] [ "-r:/nuget/pkg/1.0/Pkg.dll" ]

    upstreamFingerprint hasher (Some "/repo") "/repo/App.fs" opts |> ignore
    Assert.DoesNotContain("/nuget/pkg/1.0/Pkg.dll", hashed)

[<Fact(Timeout = 15000)>]
let ``upstreamFingerprint follows references TRANSITIVELY`` () =
    // C -> B -> A: C never names A, but is type-checked against A's sources.
    let a = makeProjectOptions "/repo/A.fsproj" [ "/repo/A.fs" ] []

    let b =
        { makeProjectOptions "/repo/B.fsproj" [ "/repo/B.fs" ] [ "-r:/repo/obj/A.dll" ] with
            ReferencedProjects =
                [| FSharp.Compiler.CodeAnalysis.FSharpReferencedProject.FSharpReference("/repo/obj/A.dll", a) |] }

    let c =
        { makeProjectOptions "/repo/C.fsproj" [ "/repo/C.fs" ] [ "-r:/repo/obj/B.dll" ] with
            ReferencedProjects =
                [| FSharp.Compiler.CodeAnalysis.FSharpReferencedProject.FSharpReference("/repo/obj/B.dll", b) |] }

    let fp aHash =
        upstreamFingerprint
            (fakeHasher (Map [ "/repo/A.fs", aHash; "/repo/B.fs", "b"; "/repo/C.fs", "c" ]))
            None
            "/repo/C.fs"
            c

    Assert.NotEqual<string>(fp "a1", fp "a2")

[<Fact(Timeout = 15000)>]
let ``upstreamFingerprints table agrees with the single-file fingerprint`` () =
    let contents =
        fakeHasher (Map [ "/repo/A.fs", "a"; "/repo/B.fs", "b"; "/repo/C.fs", "c" ])

    let table, _ = upstreamFingerprints contents None twoFileOptions

    for file in twoFileOptions.SourceFiles do
        Assert.Equal(upstreamFingerprint contents None file twoFileOptions, table[file])

[<Fact(Timeout = 15000)>]
let ``UpstreamFingerprints without generations sees every edit`` () =
    // A caller that never declares generations must still get correct keys.
    withTempDir "fp-nogen" (fun dir ->
        let a = Path.Combine(dir, "A.fs")
        let b = Path.Combine(dir, "B.fs")
        File.WriteAllText(a, "module A")
        File.WriteAllText(b, "module B")
        let opts = makeProjectOptions (Path.Combine(dir, "P.fsproj")) [ a; b ] []
        let fps = UpstreamFingerprints(None)
        let before = fps.For(b, opts, "h")
        File.WriteAllText(a, "module A // edited")
        Assert.NotEqual<string>(before, fps.For(b, opts, "h")))

[<Fact(Timeout = 15000)>]
let ``UpstreamFingerprints holds one snapshot per generation`` () =
    withTempDir "fp-gen" (fun dir ->
        let a = Path.Combine(dir, "A.fs")
        let b = Path.Combine(dir, "B.fs")
        File.WriteAllText(a, "module A")
        File.WriteAllText(b, "module B")
        let opts = makeProjectOptions (Path.Combine(dir, "P.fsproj")) [ a; b ] []
        let fps = UpstreamFingerprints(None)
        fps.BeginGeneration()
        let before = fps.For(b, opts, "h")
        File.WriteAllText(a, "module A // edited")
        // Same generation: the snapshot (computed once per project) is reused...
        Assert.Equal(before, fps.For(b, opts, "h"))
        // ...while Fresh reads the disk as it stands, which is what guards stores.
        Assert.NotEqual<string>(before, fps.Fresh(b, opts))
        fps.BeginGeneration()
        Assert.NotEqual<string>(before, fps.For(b, opts, "h")))

[<Fact(Timeout = 15000)>]
let ``FileContentHasher re-hashes a file whose content changed`` () =
    withTempDir "content-hasher" (fun dir ->
        let path = Path.Combine(dir, "A.fs")
        let hasher = FileContentHasher()
        File.WriteAllText(path, "let x = 1")
        let first = hasher.Hash path
        File.WriteAllText(path, "let x = \"changed\"")
        Assert.NotEqual<string>(first, hasher.Hash path))

[<Fact(Timeout = 15000)>]
let ``FileContentHasher hashes a file it cannot read to a fixed marker`` () =
    // An unreadable file is what FCS sees as unreadable too: a fixed marker, never an
    // exception out of the cache key.
    withTempDir "content-hasher-unreadable" (fun dir ->
        let path = Path.Combine(dir, "A.fs")
        File.WriteAllText(path, "let x = 1")
        File.SetUnixFileMode(path, UnixFileMode.None)

        try
            Assert.Equal("unreadable", FileContentHasher().Hash path)
        finally
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact(Timeout = 15000)>]
let ``a non-F# project reference is fingerprinted by its output path, not its sources`` () =
    // FCS reads an IL or PE reference as metadata; which one it is lives in its path,
    // and it has no sources to walk.
    let withReference (output: string) =
        { makeProjectOptions "/repo/App.fsproj" [ "/repo/App.fs" ] [] with
            ReferencedProjects =
                [| FSharp.Compiler.CodeAnalysis.FSharpReferencedProject.ILModuleReference(
                       output,
                       (fun () -> DateTime(2020, 1, 1)),
                       fun () -> failwith "a fingerprint never reads the module"
                   ) |] }

    let fp output =
        upstreamFingerprint (fakeHasher (Map [ "/repo/App.fs", "app" ])) None "/repo/App.fs" (withReference output)

    Assert.Equal(fp "/repo/obj/Cs.dll", fp "/repo/obj/Cs.dll")
    Assert.NotEqual<string>(fp "/repo/obj/Cs.dll", fp "/repo/obj/Other.dll")

[<Fact(Timeout = 15000)>]
let ``a file outside the project gets the whole project's fingerprint from a generation's table`` () =
    // A file not in `SourceFiles` (a script, a file just removed) depends on every
    // source of the project, so any edit moves it.
    withTempDir "fp-outside" (fun dir ->
        let a = Path.Combine(dir, "A.fs")
        let outside = Path.Combine(dir, "Outside.fsx")
        File.WriteAllText(a, "module A")
        let opts = makeProjectOptions (Path.Combine(dir, "P.fsproj")) [ a ] []
        let fps = UpstreamFingerprints(None)
        fps.BeginGeneration()
        let before = fps.For(outside, opts, "h")
        Assert.Equal(fps.Fresh(outside, opts), before)
        Assert.NotEqual<string>(fps.For(a, opts, "h"), before)
        File.WriteAllText(a, "module A // edited")
        fps.BeginGeneration()
        Assert.NotEqual<string>(before, fps.For(outside, opts, "h")))

// --- InMemoryCheckCache under a sequential scan ---

let private scanKey (i: int) = makeKey $"file-%d{i}"

let private scanPass (cache: ICheckCacheBackend) (n: int) =
    let mutable hits = 0

    for i in 0 .. n - 1 do
        match cache.TryGet(scanKey i) with
        | Some _ -> hits <- hits + 1
        | None -> cache.Set (scanKey i) (makeTestResult $"f%d{i}.fs" 1L)

    hits

[<Fact(Timeout = 15000)>]
let ``an LRU smaller than the working set gets zero hits on a repeated sequential scan`` () =
    // The pathology this cache was configured into: 500 entries against 1835 files
    // visited in the same order every scan. Pinned so the sizing below has a reason.
    let cache = InMemoryCheckCache(500) :> ICheckCacheBackend
    scanPass cache 1835 |> ignore
    Assert.Equal(0, scanPass cache 1835)

[<Fact(Timeout = 15000)>]
let ``a working-set cache stores nothing until a working set is observed`` () =
    // Its bound IS the observed working set, which starts at zero: a result stored
    // before the first observation has no room, rather than an unbounded one.
    let cache = InMemoryCheckCache(CacheCapacity.WorkingSet)
    let backend = cache :> ICheckCacheBackend
    backend.Set (scanKey 0) (makeTestResult "f0.fs" 1L)
    Assert.True((backend.TryGet(scanKey 0)).IsNone)
    Assert.Equal(0, cache.Capacity)

[<Fact(Timeout = 15000)>]
let ``a working-set cache grows to what it admits so a repeated scan hits every file`` () =
    let cache = InMemoryCheckCache(CacheCapacity.WorkingSet)
    (cache :> IScopedCheckCache).ObserveWorkingSet [ "/r/P.fsproj", 1835 ]
    let backend = cache :> ICheckCacheBackend
    scanPass backend 1835 |> ignore
    Assert.Equal(1835, scanPass backend 1835)
    // It cannot be undersized, so it never warns about thrashing.
    Assert.True(cache.ThrashWarning.IsNone)

[<Fact(Timeout = 15000)>]
let ``a fixed maxEntries is a real bound, not a floor`` () =
    // A number is the user's memory budget; the working set must not override it.
    let cache = InMemoryCheckCache(500)
    (cache :> IScopedCheckCache).ObserveWorkingSet [ "/r/P.fsproj", 1835 ]
    Assert.Equal(500, cache.Capacity)

[<Fact(Timeout = 15000)>]
let ``a fixed maxEntries below the admitted working set warns, naming both numbers`` () =
    let cache = InMemoryCheckCache(500)
    (cache :> IScopedCheckCache).ObserveWorkingSet [ "/r/P.fsproj", 1835 ]

    match cache.ThrashWarning with
    | Some text ->
        Assert.Contains("500", text)
        Assert.Contains("1835", text)
        Assert.Contains("\"all\"", text)
    | None -> Assert.Fail "expected a warning for 500 entries against 1835 files"

[<Fact(Timeout = 15000)>]
let ``no warning when maxEntries covers the admitted working set`` () =
    let cache = InMemoryCheckCache(2000)
    (cache :> IScopedCheckCache).ObserveWorkingSet [ "/r/P.fsproj", 1835 ]
    Assert.True(cache.ThrashWarning.IsNone)

[<Fact(Timeout = 15000)>]
let ``the working set counts only admitted projects`` () =
    // Excluding a large library is how a user fits the cache under a budget; the
    // warning and the working-set bound must follow what is actually cached.
    let cache =
        InMemoryCheckCache(CacheCapacity.Entries 500, admits = (fun p -> p <> "/r/Lib.fsproj"))

    let scoped = cache :> IScopedCheckCache
    scoped.ObserveWorkingSet [ "/r/App.fsproj", 400; "/r/Lib.fsproj", 1435 ]
    Assert.True(cache.ThrashWarning.IsNone)
    Assert.False(scoped.Admits "/r/Lib.fsproj")
    Assert.True(scoped.Admits "/r/App.fsproj")

[<Fact(Timeout = 15000)>]
let ``a working-set cache is sized to admitted projects only`` () =
    let cache =
        InMemoryCheckCache(CacheCapacity.WorkingSet, admits = (fun p -> p <> "/r/Lib.fsproj"))

    (cache :> IScopedCheckCache).ObserveWorkingSet [ "/r/App.fsproj", 10; "/r/Lib.fsproj", 1000 ]
    Assert.Equal(10, cache.Capacity)

[<Fact(Timeout = 15000)>]
let ``Set drops the superseded entry for the same file and project`` () =
    // One slot per (file, project): an edit produces a new key, and the old entry can
    // never be looked up again. Keeping it until LRU pressure found it would hold a
    // dead typed tree per edit for the life of the daemon.
    let cache = InMemoryCheckCache(100)
    let backend = cache :> ICheckCacheBackend
    backend.Set (makeKey "v1") (makeTestResult "a.fs" 1L)
    backend.Set (makeKey "v2") (makeTestResult "a.fs" 2L)
    Assert.Equal(1, cache.Count)
    Assert.True(backend.TryGet(makeKey "v1").IsNone)
    Assert.True(backend.TryGet(makeKey "v2").IsSome)

// --- Startup description: an inert cache must say so ---

[<Fact(Timeout = 15000)>]
let ``describeCheckCache says OFF when there is no backend`` () =
    // Silence is what let an inert cache read as a working one.
    Assert.Contains("OFF", describeCheckCache CheckCacheSetup.Off)

[<Fact(Timeout = 15000)>]
let ``describeCheckCache names a fixed bound`` () =
    let text =
        describeCheckCache (CheckCacheSetup.On(InMemoryCheckCache(500), None, None))

    Assert.Contains("in-memory", text)
    Assert.Contains("500", text)

[<Fact(Timeout = 15000)>]
let ``describeCheckCache says a working-set cache holds every admitted file`` () =
    let text =
        describeCheckCache (CheckCacheSetup.On(InMemoryCheckCache(CacheCapacity.WorkingSet), None, None))

    Assert.Contains("working set", text)

type private ForeignBackend() =
    interface ICheckCacheBackend with
        member _.TryGet _ = None
        member _.Set _ _ = ()
        member _.Invalidate _ = ()
        member _.Clear() = ()

[<Fact(Timeout = 15000)>]
let ``describeCheckCache names a backend it does not know by its type`` () =
    let text = describeCheckCache (CheckCacheSetup.On(ForeignBackend(), None, None))

    Assert.Equal("check-result cache: ForeignBackend", text)

[<Fact(Timeout = 15000)>]
let ``describeCheckCache states a scope that left this checkout out, and no advice to enable the cache`` () =
    let text =
        describeCheckCache (CheckCacheSetup.OffInThisCheckout "cache.scope is \"default-workspace\" and this is X")

    Assert.Equal("check-result cache: OFF in this checkout — cache.scope is \"default-workspace\" and this is X", text)

[<Fact(Timeout = 15000)>]
let ``describeCheckCache names the scope a running cache runs under`` () =
    let text =
        describeCheckCache (CheckCacheSetup.On(InMemoryCheckCache(500), None, Some "cache.scope is \"all\""))

    Assert.Equal("check-result cache: in-memory, at most 500 entries — cache.scope is \"all\"", text)

[<Fact(Timeout = 15000)>]
let ``a setup hands the daemon a backend and key provider only when the cache is on`` () =
    let backend = InMemoryCheckCache(500) :> ICheckCacheBackend
    let keyProvider = TimestampCacheKeyProvider() :> ICacheKeyProvider

    match CheckCacheSetup.components (CheckCacheSetup.On(backend, Some keyProvider, None)) with
    | Some b, Some k ->
        Assert.Same(backend, b)
        Assert.Same(keyProvider, k)
    | other -> failwith $"expected the configured backend and key provider, got %A{other}"

    for off in
        [ CheckCacheSetup.Off
          CheckCacheSetup.OffInThisCheckout "cache.scope is \"default-workspace\"" ] do
        match CheckCacheSetup.components off with
        | None, None -> ()
        | other -> failwith $"expected no cache components, got %A{other}"

// --- Removal of keys the cache no longer holds ---

[<Fact(Timeout = 15000)>]
let ``InMemoryCheckCache invalidating a key it never held changes nothing`` () =
    let cache = InMemoryCheckCache(10)
    let backend = cache :> ICheckCacheBackend
    backend.Set (makeKey "held") (makeTestResult "a.fs" 1L)

    backend.Invalidate(makeKey "never-set")

    Assert.Equal(1, cache.Count)
    Assert.True(backend.TryGet(makeKey "held").IsSome)

[<Fact(Timeout = 15000)>]
let ``InMemoryCheckCache a slot naming an invalidated key is taken over cleanly`` () =
    // One key re-set with another file's result leaves its first slot naming it. Once
    // that key is invalidated, the stale slot must not block, or evict, the next result
    // for the first file.
    let cache = InMemoryCheckCache(10)
    let backend = cache :> ICheckCacheBackend
    backend.Set (makeKey "k1") (makeTestResult "a.fs" 1L)
    backend.Set (makeKey "k1") (makeTestResult "b.fs" 2L)
    backend.Set (makeKey "other") (makeTestResult "c.fs" 3L)
    backend.Invalidate(makeKey "k1")

    backend.Set (makeKey "k2") (makeTestResult "a.fs" 4L)

    Assert.Equal(2, cache.Count)
    Assert.True(backend.TryGet(makeKey "k2").IsSome)
    Assert.True(backend.TryGet(makeKey "other").IsSome)

[<Fact(Timeout = 15000)>]
let ``InMemoryCheckCache a key moved to another file releases its new slot on removal`` () =
    let cache = InMemoryCheckCache(10)
    let backend = cache :> ICheckCacheBackend
    backend.Set (makeKey "k1") (makeTestResult "a.fs" 1L)
    backend.Set (makeKey "k1") (makeTestResult "b.fs" 2L)

    // k2 takes b.fs's slot, which k1 holds, so k1 is superseded outright.
    backend.Set (makeKey "k2") (makeTestResult "b.fs" 3L)

    Assert.Equal(1, cache.Count)
    Assert.True(backend.TryGet(makeKey "k1").IsNone)

// --- what the key names beyond the repository: the toolchain, and references outside it ---

[<Fact(Timeout = 15000)>]
let ``a different toolchain gives a different cache key for the same file, options and upstream`` () =
    // A result is the checker's answer, not only the code's: a cache shared by two
    // daemons on different fshw or FCS builds must not hand one's answers to the other.
    withTempDir "key-toolchain" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        File.WriteAllText(file, "module A")
        let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider

        let keyUnder toolchain =
            makeCacheKeyWith provider toolchain "options" "upstream" file

        test <@ keyUnder "fcs-1" = keyUnder "fcs-1" @>
        test <@ keyUnder "fcs-1" <> keyUnder "fcs-2" @>)

[<Fact(Timeout = 15000)>]
let ``toolchainIdentity names the exact compiler service, FSharp.Core and fshw builds`` () =
    // A module version id changes with every build of an assembly, so it names the
    // checker's code (and the settings fshw creates it with) more exactly than a version.
    let mvid (t: Type) =
        string t.Assembly.ManifestModule.ModuleVersionId

    test <@ toolchainIdentity.Contains(mvid typeof<FSharp.Compiler.CodeAnalysis.FSharpChecker>) @>
    test <@ toolchainIdentity.Contains(mvid typeof<option<int>>) @>
    test <@ toolchainIdentity.Contains(mvid typeof<CacheKey>) @>

[<Fact(Timeout = 15000)>]
let ``makeCacheKey keys under this process's toolchain`` () =
    withTempDir "key-default-toolchain" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        File.WriteAllText(file, "module A")
        let opts = makeProjectOptions (Path.Combine(dir, "P.fsproj")) [ file ] []
        let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider

        let upstream =
            upstreamFingerprint (FileContentHasher().Hash) None file opts

        test
            <@
                makeCacheKey provider file opts = makeCacheKeyWith
                                                      provider
                                                      toolchainIdentity
                                                      (getProjectOptionsHash opts)
                                                      upstream
                                                      file
            @>)

[<Fact(Timeout = 15000)>]
let ``a non-F# project reference's stamp is part of the fingerprint`` () =
    // FCS re-reads an IL or PE reference when its stamp moves. A fingerprint naming only
    // the path served results typed against the module as it was.
    let withStamp (stamp: DateTime) =
        { makeProjectOptions "/repo/App.fsproj" [ "/repo/App.fs" ] [] with
            ReferencedProjects =
                [| FSharp.Compiler.CodeAnalysis.FSharpReferencedProject.ILModuleReference(
                       "/repo/obj/Cs.dll",
                       (fun () -> stamp),
                       fun () -> failwith "a fingerprint never reads the module"
                   ) |] }

    let fp stamp =
        upstreamFingerprint (fakeHasher (Map [ "/repo/App.fs", "app" ])) None "/repo/App.fs" (withStamp stamp)

    Assert.Equal(fp (DateTime(2020, 1, 1)), fp (DateTime(2020, 1, 1)))
    Assert.NotEqual<string>(fp (DateTime(2020, 1, 1)), fp (DateTime(2020, 1, 2)))

[<Fact(Timeout = 15000)>]
let ``upstreamFingerprint stamps assemblies outside the repository`` () =
    // A path outside the repository names its bytes only when it is version-qualified
    // (NuGet, the SDK). A sibling repository's bin/Sibling.dll is rebuilt in place under
    // one path, so the path alone served results typed against the old build. A stamp
    // read by one stat catches that without reading the bytes.
    let stamped = System.Collections.Generic.List<string>()

    let opts =
        makeProjectOptions
            "/repo/App.fsproj"
            [ "/repo/App.fs" ]
            [ "-r:/elsewhere/bin/Sibling.dll"; "-r:/repo/lib/Vendored.dll" ]

    let fp stamp =
        upstreamFingerprintWith
            (fakeHasher (Map [ "/repo/App.fs", "app"; "/repo/lib/Vendored.dll", "v" ]))
            (fun path ->
                stamped.Add path
                stamp)
            (Some "/repo")
            "/repo/App.fs"
            opts

    Assert.Equal(fp "s1", fp "s1")
    Assert.NotEqual<string>(fp "s1", fp "s2")
    // In-repo assemblies are hashed by content instead; only the outside one is stamped.
    Assert.DoesNotContain("/repo/lib/Vendored.dll", stamped)
    Assert.Contains("/elsewhere/bin/Sibling.dll", stamped)

[<Fact(Timeout = 15000)>]
let ``fileStampText moves when a rebuild changes a file's last-write time or length`` () =
    // Times are set, not waited for: the stamp must not depend on the clock ticking
    // between two writes.
    withTempDir "stamp-text" (fun dir ->
        let path = Path.Combine(dir, "Sibling.dll")
        let first = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        let second = DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)

        let writeAt (bytes: byte array) (time: DateTime) =
            File.WriteAllBytes(path, bytes)
            File.SetLastWriteTimeUtc(path, time)
            fileStampText path

        let original = writeAt [| 1uy |] first
        Assert.Equal(original, writeAt [| 1uy |] first)
        // Same length, rebuilt later.
        Assert.NotEqual<string>(original, writeAt [| 2uy |] second)
        // Same time, different length.
        Assert.NotEqual<string>(original, writeAt [| 1uy; 2uy |] first)
        Assert.Equal("missing", fileStampText (Path.Combine(dir, "absent.dll"))))

// --- CacheTally: what the cache did in one scan, and why each miss missed ---

open FsHotWatch.CheckCacheTally

[<Fact(Timeout = 15000)>]
let ``CacheTally counts hits, misses by reason and the outputs that moved`` () =
    let tally =
        CacheTally.empty
        |> CacheTally.lookedUp (CacheLookupOutcome.Hit)
        |> CacheTally.lookedUp (CacheLookupOutcome.Miss CacheMiss.NoEntry)
        |> CacheTally.lookedUp (CacheLookupOutcome.Miss(CacheMiss.OutputsMoved [ "${repo}/bin/Lib.dll" ]))
        |> CacheTally.lookedUp (CacheLookupOutcome.Miss(CacheMiss.OutputsMoved [ "${repo}/bin/Lib.dll" ]))
        |> CacheTally.stored CacheStore.Stored
        |> CacheTally.stored CacheStore.InputsMoved

    test <@ tally.Hits = 1L @>
    test <@ CacheTally.misses tally = 3L @>
    test <@ tally.Misses = Map [ CacheMiss.label CacheMiss.NoEntry, 1L; "output moved", 2L ] @>
    test <@ tally.MovedOutputs = Map [ "${repo}/bin/Lib.dll", 2L ] @>
    test <@ tally.Stored = 1L @>
    test <@ tally.NotStored = Map [ CacheStore.label CacheStore.InputsMoved, 1L ] @>

[<Fact(Timeout = 15000)>]
let ``CacheTally.since is what happened between two snapshots`` () =
    let atScanStart =
        CacheTally.empty
        |> CacheTally.lookedUp CacheLookupOutcome.Hit
        |> CacheTally.lookedUp (CacheLookupOutcome.Miss CacheMiss.NoEntry)

    let atScanEnd =
        atScanStart
        |> CacheTally.lookedUp CacheLookupOutcome.Hit
        |> CacheTally.lookedUp (CacheLookupOutcome.Miss(CacheMiss.OutputsMoved [ "${repo}/bin/Lib.dll" ]))

    let scan = CacheTally.since atScanStart atScanEnd

    test <@ scan.Hits = 1L @>
    // A reason with no misses in between is dropped, not kept at zero.
    test <@ scan.Misses = Map [ "output moved", 1L ] @>
    test <@ scan.MovedOutputs = Map [ "${repo}/bin/Lib.dll", 1L ] @>

[<Fact(Timeout = 15000)>]
let ``describeCacheTally names hits, each miss reason and the outputs whose bytes moved`` () =
    // The line a reader needs to tell "the cache missed because Lib.dll was rebuilt"
    // from "the cache is not working": a count alone cannot.
    let tally =
        { CacheTally.empty with
            Hits = 2110L
            Misses = Map [ "output moved", 318L ]
            MovedOutputs = Map [ "${repo}/src/Lib/bin/Lib.dll", 318L ]
            Stored = 318L }

    let line = describeCacheTally tally

    test <@ line.StartsWith "check-result cache: 2110 hits, 318 misses" @>
    test <@ line.Contains "318 output moved" @>
    test <@ line.Contains "${repo}/src/Lib/bin/Lib.dll ×318" @>
    test <@ line.Contains "stored 318" @>

[<Fact(Timeout = 15000)>]
let ``describeCacheTally names at most five moved outputs, the most-missed first`` () =
    let moved = Map [ for i in 1..7 -> $"o%d{i}.dll", int64 i ]

    let line =
        describeCacheTally
            { CacheTally.empty with
                Misses = Map [ "output moved", 28L ]
                MovedOutputs = moved }

    test <@ line.Contains "o7.dll ×7, o6.dll ×6, o5.dll ×5, o4.dll ×4, o3.dll ×3" @>
    test <@ not (line.Contains "o2.dll") @>
    test <@ line.Contains "and 2 more" @>
