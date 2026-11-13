/// What an action READ, as the process saw it (ADR-059 §35 Change 1b, AUTOMATION-1634).
///
/// The read-set audit (`ReadSetAudit`) compares what an action read with what its key
/// declared. This module is the in-process half of the tracing: a check's reads, seen
/// through FCS's file-system shim (`FileSystemAutoOpens.FileSystem`), plus the source reads
/// fshw makes on FCS's behalf (`ProjectSnapshots`, which reads a source file when FCS asks
/// for its text and reports it here through `opened`). Child processes are traced by
/// syscall instead (`Strace`, Linux only).
///
/// A trace is PROCESS-WIDE: FCS's shim is one global, and FCS reads on its own threads, so
/// no ambient context can attribute a read to the action that caused it. A trace therefore
/// owns the process while it runs: one at a time (a nested or concurrent one is refused),
/// and whoever traces must run nothing else that reads through FCS meanwhile (the tests use
/// a serialized collection, `CollectionName`).
///
/// What a trace cannot see (ADR-059 §37): a read FCS answered from a cache an earlier action
/// filled in this process (its assembly-reader cache is process-wide), a read through any
/// path other than the shim or `opened`, and every input that is not a read at all.
module FsHotWatch.ReadTrace

open System
open System.Collections.Concurrent
open System.IO
open FSharp.Compiler.IO

/// How a file was read.
[<RequireQualifiedAccess>]
type ReadKind =
    /// Its bytes (or, for a program, its execution).
    | Open
    /// Only whether it exists, its stamp, or its directory listing: still an input, since
    /// the action could decide on it.
    | Probe

/// One read: a full path and how it was read.
type Read = { Path: string; Kind: ReadKind }

/// The xUnit collection every test that traces belongs to: a trace owns the process.
[<Literal>]
let CollectionName = "ReadTrace"

/// The reads of the trace running now; None outside a trace.
let mutable private sink: ConcurrentQueue<Read> option = None

let private gate = obj ()

let private record (kind: ReadKind) (path: string) =
    match sink with
    | Some reads when not (String.IsNullOrEmpty path) ->
        let full =
            try
                Path.GetFullPath path
            with _ ->
                path

        reads.Enqueue { Path = full; Kind = kind }
    | _ -> ()

/// Report a read of `path`'s bytes made outside FCS's shim on a check's behalf. Free
/// outside a trace.
let opened (path: string) : unit = record ReadKind.Open path

/// FCS's own file system, recording every read while a trace runs.
type private TracingFileSystem() =
    inherit DefaultFileSystem()

    override _.OpenFileForReadShim(filePath, useMemoryMappedFile, shouldShadowCopy) =
        record ReadKind.Open filePath

        base.OpenFileForReadShim(
            filePath,
            ?useMemoryMappedFile = useMemoryMappedFile,
            ?shouldShadowCopy = shouldShadowCopy
        )

    override _.FileExistsShim filePath =
        record ReadKind.Probe filePath
        base.FileExistsShim filePath

    override _.DirectoryExistsShim path =
        record ReadKind.Probe path
        base.DirectoryExistsShim path

    override _.GetLastWriteTimeShim fileName =
        record ReadKind.Probe fileName
        base.GetLastWriteTimeShim fileName

    override _.EnumerateFilesShim(path, pattern) =
        record ReadKind.Probe path
        base.EnumerateFilesShim(path, pattern)

    override _.EnumerateDirectoriesShim path =
        record ReadKind.Probe path
        base.EnumerateDirectoriesShim path

/// Run `action` under a trace and return its result with every read it made, in order.
/// FCS's shim is installed for the duration and the one it replaced restored after.
/// Throws `InvalidOperationException` when another trace is running.
let trace (action: unit -> 'T) : 'T * Read list =
    let reads = ConcurrentQueue<Read>()

    lock gate (fun () ->
        if sink.IsSome then
            invalidOp "a read trace is already running: a trace owns the process"

        sink <- Some reads)

    let previous = FileSystemAutoOpens.FileSystem
    FileSystemAutoOpens.FileSystem <- TracingFileSystem()

    try
        let result = action ()
        result, List.ofSeq reads
    finally
        FileSystemAutoOpens.FileSystem <- previous
        lock gate (fun () -> sink <- None)
