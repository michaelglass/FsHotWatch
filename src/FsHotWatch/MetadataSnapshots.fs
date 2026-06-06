/// Experimental: supplies FCS with pointers into memory-mapped DLL images via
/// `FSharpChecker.Create(tryGetMetadataSnapshot = ...)`.
///
/// Why this exists
/// ---------------
/// Benchmarks attributed ~85% of the daemon's resident footprint to native FCS
/// memory — overwhelmingly the IL metadata readers, which by default copy the
/// referenced DLLs' bytes into private (anonymous, dirty) native buffers. FCS
/// exposes a host hook, `tryGetMetadataSnapshot: string * DateTime -> (obj *
/// nativeint * int) option`, that lets the host hand back a raw pointer + length
/// instead. When that pointer is into a file-backed memory-mapped view, the
/// pages are *shared, clean, file-backed* — they don't count as private dirty
/// memory and can be evicted/reloaded by the OS for free. This is how VS hosts
/// keep FSharp.Compiler.Service memory bounded.
///
/// What the pointer must point at
/// ------------------------------
/// CRUCIAL: the `(ptr, len)` must describe the CLI **metadata block** (starting at
/// the `BSJB` root), NOT the whole PE image. FCS consumes the snapshot through
/// `ilread.fs`'s `openPEMetadataOnly`, which calls `openMetadataReader` with a
/// hard-coded `metadataPhysLoc = 0`: it reads the snapshot at offset 0 and expects
/// the metadata magic there. The canonical Visual Studio provider returns
/// `Some(hold, mmr.MetadataPointer, mmr.MetadataLength)`. Returning the whole PE
/// image (which starts with `MZ` = `0x5A4D` = 23117) makes FCS throw
/// "bad metadata magic number: 23117" and silently `Aborted` every file. We derive
/// the metadata pointer from a `PEReader` over the mmap view — the same mechanism
/// Roslyn uses — so the block still points into shared, file-backed pages.
///
/// Holder lifetime
/// ---------------
/// The third element returned to FCS is an opaque `obj` "holder". FCS roots it
/// for exactly as long as it relies on the pointer, and drops the reference when
/// it re-reads the metadata (i.e. it never reads the pointer after releasing the
/// holder). The holder therefore owns the `MemoryMappedFile` +
/// `MemoryMappedViewAccessor` + the acquired pointer; its `SafeHandle`-backed
/// release (via the accessor's `SafeMemoryMappedViewHandle`) keeps the mapping
/// valid until the GC finalizes it. We deliberately do NOT dispose a stale entry
/// eagerly on replacement: an older FCS reader may still hold the old holder, and
/// disposing the accessor under it would invalidate a live pointer. Instead we
/// drop our reference to the old holder and let GC/finalization reclaim it once
/// FCS has also let go — correctness over promptness.
module FsHotWatch.MetadataSnapshots

open System
open System.Collections.Concurrent
open System.IO
open System.IO.MemoryMappedFiles
open System.Reflection.PortableExecutable
open Microsoft.FSharp.NativeInterop

#nowarn "9" // NativePtr use for the unmanaged PE image pointer

/// Owns a single memory-mapped DLL image plus a `PEReader` over it, and exposes a
/// stable pointer to the *metadata block* (the `BSJB` root), NOT the start of the
/// PE image. Handed to FCS as the opaque holder `obj`.
///
/// THE CONTRACT (verified against dotnet/fsharp `ilread.fs`): FCS consumes the
/// snapshot via `openPEMetadataOnly`, which calls
/// `openMetadataReader(fileName, mdfile, metadataPhysLoc = 0, ...)`. The hard-coded
/// `0` means FCS reads the snapshot at offset 0 and expects the CLI metadata magic
/// `BSJB` there. So the `(ptr, len)` we return MUST be the metadata block only —
/// `ptr` at the metadata root, `len` the metadata length — exactly as the canonical
/// Visual Studio provider does: `Some(hold, mmr.MetadataPointer, mmr.MetadataLength)`
/// (FSharp.Editor LanguageService.fs). Handing back the whole PE image (which begins
/// with `MZ`) makes FCS fail with "bad metadata magic number: 23117" and silently
/// return `FSharpCheckFileAnswer.Aborted` for every file.
///
/// We obtain the metadata pointer the same way Roslyn does: a `PEReader` over the
/// memory-mapped, file-backed image, then `GetMetadata().Pointer/Length`. The block
/// points into the mmap view, so pages stay shared/clean/file-backed.
///
/// `SafeMemoryMappedViewHandle.DangerousAddRef` pins the handle so it cannot be
/// released out from under the raw pointer; the `PEReader` is kept alive for the
/// holder's lifetime so the metadata block pointer it handed out stays valid.
type internal SnapshotHolder(mmf: MemoryMappedFile, accessor: MemoryMappedViewAccessor, length: int) =
    let mutable disposed = false
    let mutable refAdded = false
    let safeHandle = accessor.SafeMemoryMappedViewHandle

    // Acquire a pointer to the start of the PE image that stays valid for the
    // holder's lifetime. AddRef pins the SafeHandle so the view is not released
    // while FCS (and our PEReader) read.
    let imagePtr =
        let mutable p = 0n
        safeHandle.DangerousAddRef(&refAdded)
        p <- safeHandle.DangerousGetHandle()
        // PInvoke views can carry a small leading offset; for whole-file maps
        // created at offset 0 this is 0, but read it defensively.
        let basePtr = accessor.PointerOffset
        p + nativeint basePtr

    // A PEReader over the unmanaged whole-image view. Reading metadata through it
    // returns a block that points INTO the mmap, with no byte copy. Kept alive for
    // the holder's lifetime so the metadata pointer remains valid.
    let peReader = new PEReader(NativePtr.ofNativeInt<byte> imagePtr, length)

    // The metadata block: pointer at the CLI metadata root (BSJB) and its length.
    // This is what FCS expects from tryGetMetadataSnapshot (offset 0 == BSJB).
    let metadataPtr, metadataLen =
        let block = peReader.GetMetadata()
        NativePtr.toNativeInt block.Pointer, block.Length

    /// Raw pointer to the CLI metadata block (the `BSJB` root) inside the mapped
    /// image — NOT the PE image start. Valid while this holder is alive.
    member _.Pointer: nativeint = metadataPtr
    /// Length in bytes of the CLI metadata block.
    member _.Length: int = metadataLen

    member private _.Release() =
        if not disposed then
            disposed <- true
            // Dispose the PEReader first (it only references the view), then
            // balance the AddRef before disposing the view, accessor and mapping
            // (which also closes the owned FileStream).
            peReader.Dispose()

            if refAdded then
                safeHandle.DangerousRelease()

            accessor.Dispose()
            mmf.Dispose()

    interface IDisposable with
        member this.Dispose() =
            this.Release()
            GC.SuppressFinalize(this)

    override this.Finalize() = this.Release()

/// Cache entry: the holder plus the mtime that produced it, used for staleness
/// checks.
[<NoComparison; NoEquality>]
type internal Entry =
    { Mtime: DateTime
      Holder: SnapshotHolder }

/// Path -> live mapped snapshot. Keyed by absolute path.
let private cache = ConcurrentDictionary<string, Entry>(StringComparer.Ordinal)

/// Paths we've already logged a failure for, so a permanently-unmappable
/// reference doesn't spam the debug log on every check.
let private loggedFailures =
    ConcurrentDictionary<string, byte>(StringComparer.Ordinal)

let private logOnce (path: string) (msg: string) =
    if loggedFailures.TryAdd(path, 0uy) then
        Logging.debug "mmap-metadata" msg

/// Build a fresh holder for `path`. Opens the file with maximally-permissive
/// share flags (ReadWrite|Delete) so a concurrent build rewriting the DLL does
/// not fail our open, then maps it read-only.
let private openHolder (path: string) (length: int64) : SnapshotHolder =
    // FileShare.ReadWrite|Delete: tolerate concurrent writers/deleters. leaveOpen
    // = false hands ownership of the FileStream to the MMF, which disposes it.
    let fs =
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

    let mmf =
        MemoryMappedFile.CreateFromFile(
            fs,
            null,
            0L,
            MemoryMappedFileAccess.Read,
            HandleInheritability.None,
            leaveOpen = false
        )

    let accessor = mmf.CreateViewAccessor(0L, length, MemoryMappedFileAccess.Read)

    new SnapshotHolder(mmf, accessor, int length)

/// FCS hook. Returns a pointer + length into a memory-mapped, file-backed image
/// of `path`, or `None` to let FCS fall back to its default (byte-copying)
/// reader. Never throws — any failure is logged once at debug level and yields
/// `None`.
let tryGetSnapshot (path: string, mtime: DateTime) : (obj * nativeint * int) option =
    try
        let info = FileInfo(path)

        if not info.Exists || info.Length <= 0L then
            None
        else
            let key = info.FullName

            // Fast path: live entry whose mtime still matches.
            match cache.TryGetValue(key) with
            | true, entry when entry.Mtime = mtime ->
                let h = entry.Holder
                Some(box h, h.Pointer, h.Length)
            | _ ->
                // Miss or stale: build a new holder and replace any stale entry.
                // We intentionally do NOT dispose the previous holder here — a
                // live FCS reader may still hold it; GC/finalization reclaims it
                // once everyone has let go (see module doc).
                let holder = openHolder key info.Length

                let entry = { Mtime = mtime; Holder = holder }

                cache.[key] <- entry
                // A successful (re)map clears any prior failure log gate.
                loggedFailures.TryRemove(key) |> ignore
                Some(box holder, holder.Pointer, holder.Length)
    with ex ->
        // Any failure (illegal path → FileInfo throws, open/map failure, etc.)
        // is swallowed: FCS falls back to its default reader. Log once per path.
        logOnce path $"snapshot failed for %s{path}: %s{ex.Message}"
        None

/// Number of live cache entries. For tests / telemetry.
let cacheCount () : int = cache.Count

/// Drop all cache entries. Holders are released by GC/finalization once FCS also
/// lets go; we do not force-dispose here for the same lifetime reason as stale
/// replacement. For tests.
let clear () : unit =
    cache.Clear()
    loggedFailures.Clear()
