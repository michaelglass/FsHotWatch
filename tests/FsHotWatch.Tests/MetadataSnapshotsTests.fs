module FsHotWatch.Tests.MetadataSnapshotsTests

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open Xunit
open FsHotWatch

/// A real managed PE we can memory-map and read metadata from. tryGetSnapshot
/// returns the CLI *metadata block* (BSJB root), NOT the whole PE image — so the
/// tests use a genuine assembly rather than synthetic bytes.
let private realDll = typeof<int list>.Assembly.Location // FSharp.Core.dll

/// Copy a real DLL to a fresh temp path so per-test mtime/replacement mutations
/// don't disturb the shared on-disk assembly. Keyed by absolute path, so each
/// test gets an isolated cache slot.
let private newTempDll () =
    let path = Path.Combine(Path.GetTempPath(), $"fshw-mmap-{Guid.NewGuid():N}.dll")
    File.Copy(realDll, path, overwrite = true)
    path

/// Reads `n` bytes from an unmanaged pointer.
let private readPtr (ptr: nativeint) (n: int) =
    let buf = Array.zeroCreate<byte> n
    Marshal.Copy(ptr, buf, 0, n)
    buf

[<Fact(Timeout = 15000)>]
let ``returns the CLI metadata block (BSJB root, len < file) for a real assembly`` () =
    let path = newTempDll ()

    try
        let mtime = File.GetLastWriteTimeUtc(path)
        let fileLen = (FileInfo path).Length

        match MetadataSnapshots.tryGetSnapshot (path, mtime) with
        | None -> Assert.Fail("expected Some snapshot for a real assembly")
        | Some(holder, ptr, len) ->
            Assert.NotNull(holder)
            Assert.True(ptr <> 0n, "pointer must be non-null")
            // The metadata block is a proper subset of the PE image.
            Assert.True(len > 0, "metadata length must be positive")
            Assert.True(int64 len < fileLen, $"metadata len {len} must be < file len {fileLen}")
            // Offset 0 of the snapshot MUST be the CLI metadata magic 'BSJB'
            // (0x42 0x53 0x4A 0x42) — this is exactly what FCS reads at offset 0.
            let head = readPtr ptr 4
            Assert.Equal<byte[]>([| 0x42uy; 0x53uy; 0x4Auy; 0x42uy |], head)
            GC.KeepAlive(holder)
    finally
        MetadataSnapshots.clear ()

        try
            File.Delete(path)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``returns None for missing file`` () =
    MetadataSnapshots.clear ()

    let path =
        Path.Combine(Path.GetTempPath(), $"fshw-mmap-missing-{Guid.NewGuid():N}.bin")

    let result = MetadataSnapshots.tryGetSnapshot (path, DateTime.UtcNow)
    Assert.True(result.IsNone)

[<Fact(Timeout = 15000)>]
let ``returns None for a non-PE file (no metadata to map)`` () =
    MetadataSnapshots.clear ()
    // A non-empty file that is not a valid PE: PEReader rejects it, the failure is
    // swallowed, and FCS falls back to its default reader.
    let path =
        Path.Combine(Path.GetTempPath(), $"fshw-mmap-nonpe-{Guid.NewGuid():N}.bin")

    File.WriteAllBytes(path, [| 0x4Duy; 0x5Auy; 0x90uy; 0x00uy; 0x03uy; 0x00uy |])

    try
        let result = MetadataSnapshots.tryGetSnapshot (path, File.GetLastWriteTimeUtc(path))
        Assert.True(result.IsNone)
    finally
        MetadataSnapshots.clear ()

        try
            File.Delete(path)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``cache hit returns same holder instance and keeps cacheCount at 1`` () =
    let path = newTempDll ()

    try
        MetadataSnapshots.clear ()
        let mtime = File.GetLastWriteTimeUtc(path)

        let first = MetadataSnapshots.tryGetSnapshot (path, mtime)
        let second = MetadataSnapshots.tryGetSnapshot (path, mtime)

        match first, second with
        | Some(h1, _, _), Some(h2, _, _) ->
            // Reference equality: the second call must return the cached holder.
            Assert.Same(h1, h2)
            Assert.Equal(1, MetadataSnapshots.cacheCount ())
        | _ -> Assert.Fail("expected both calls to return Some")
    finally
        MetadataSnapshots.clear ()

        try
            File.Delete(path)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``mtime change replaces entry returning a new holder with cacheCount still 1`` () =
    let path = newTempDll ()

    try
        MetadataSnapshots.clear ()
        let mtime1 = File.GetLastWriteTimeUtc(path)
        let first = MetadataSnapshots.tryGetSnapshot (path, mtime1)

        // Touch the file's mtime so the cached entry is considered stale.
        let mtime2 = mtime1.AddSeconds(5.0)
        File.SetLastWriteTimeUtc(path, mtime2)

        let second = MetadataSnapshots.tryGetSnapshot (path, File.GetLastWriteTimeUtc(path))

        match first, second with
        | Some(h1, _, _), Some(h2, _, len2) ->
            Assert.NotSame(h1, h2)
            Assert.True(len2 > 0)
            // Entry replaced in place — still a single cache slot for this path.
            Assert.Equal(1, MetadataSnapshots.cacheCount ())
        | _ -> Assert.Fail("expected both calls to return Some")
    finally
        MetadataSnapshots.clear ()

        try
            File.Delete(path)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``never throws on a garbage path and returns None`` () =
    MetadataSnapshots.clear ()
    // Null-byte-laden / illegal path: must be swallowed, never thrown.
    let garbage = " :::/this/does/not/exist "
    let result = MetadataSnapshots.tryGetSnapshot (garbage, DateTime.UtcNow)
    Assert.True(result.IsNone)

[<Fact(Timeout = 15000)>]
let ``returns None for a directory path`` () =
    MetadataSnapshots.clear ()
    // A directory exists but cannot be memory-mapped as a file → None, no throw.
    let dir = Path.GetTempPath()
    let result = MetadataSnapshots.tryGetSnapshot (dir, DateTime.UtcNow)
    Assert.True(result.IsNone)

[<Fact(Timeout = 15000)>]
let ``returns None for an empty (zero-length) file`` () =
    MetadataSnapshots.clear ()
    // A 0-byte file can't be mapped (and carries no metadata) → None.
    let path =
        Path.Combine(Path.GetTempPath(), $"fshw-mmap-empty-{Guid.NewGuid():N}.bin")

    File.WriteAllBytes(path, [||])

    try
        let result = MetadataSnapshots.tryGetSnapshot (path, File.GetLastWriteTimeUtc(path))
        Assert.True(result.IsNone)
    finally
        try
            File.Delete(path)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``disposing a holder releases the mapping and is idempotent`` () =
    let path = newTempDll ()

    try
        MetadataSnapshots.clear ()

        match MetadataSnapshots.tryGetSnapshot (path, File.GetLastWriteTimeUtc(path)) with
        | Some(holder, _, _) ->
            // The holder is a SnapshotHolder (internal; tests have access). Disposing
            // it explicitly exercises the PEReader / SafeHandle release / accessor /
            // mmf cleanup path. A second Dispose must be a no-op (idempotent).
            let disposable = holder :?> IDisposable
            disposable.Dispose()
            disposable.Dispose()
        | None -> Assert.Fail("expected Some snapshot")
    finally
        MetadataSnapshots.clear ()

        try
            File.Delete(path)
        with _ ->
            ()
