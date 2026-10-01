/// Notes, in a traced run, the source files the F# compiler service reads by itself.
///
/// A traced run records a test's file inputs by rewriting call sites in the woven
/// assemblies. FCS is not woven: when a check needs a project's other sources (earlier
/// files, signatures, `#load`ed scripts) it reads them through its own file-system
/// abstraction, `FileSystemAutoOpens.FileSystem`, and those reads would leave no input. A
/// test whose result depends on such a file would then keep a trace that a change to the
/// file does not invalidate.
///
/// In a traced run this installs, before any test runs, a file system that delegates
/// everything to the one it replaces and notes each source it reads through the recorder's
/// `Scopes.NoteInput` (bound by reflection; the recorder is present only in a traced
/// launch's woven copy). Untraced, nothing is installed.
///
/// What is noted, and why only that:
///   * `read` when a source is opened, and `read` when its write time is asked for. FCS
///     keeps parsed files per checker and, on every later request, stamps each source to
///     decide whether its cached result still holds. A second test checking through the
///     same checker reads nothing, so the stamp is what attributes the file to it.
///   * `exists` when a source's existence is probed.
///   * Sources only (`.fs`, `.fsi`, `.fsx`). Assemblies are opened through the same shim;
///     a repository build output changes on every build, and its code is traced by the
///     weave itself, so noting it would invalidate traces without adding soundness.
module FsHotWatch.Tests.TraceFcsReads

open System
open System.IO
open FSharp.Compiler.IO

/// Notes `path` as an input of `kind` (`read`, `exists`) on the running test's scope.
type Note = string -> string -> unit

/// Whether `path` names an F# source.
let isSource (path: string) =
    not (isNull path)
    && (match Path.GetExtension path with
        | ".fs"
        | ".fsi"
        | ".fsx" -> true
        | _ -> false)

/// Delegates every member to `inner`, noting the sources it reads.
type RecordingFileSystem(inner: IFileSystem, note: Note) =
    let noteSource kind path =
        if isSource path then
            note kind path

    /// The file system this one delegates to.
    member _.Inner = inner

    interface IFileSystem with
        member _.AssemblyLoader = inner.AssemblyLoader

        member _.OpenFileForReadShim(filePath, ?useMemoryMappedFile, ?shouldShadowCopy) =
            noteSource "read" filePath

            inner.OpenFileForReadShim(
                filePath,
                ?useMemoryMappedFile = useMemoryMappedFile,
                ?shouldShadowCopy = shouldShadowCopy
            )

        member _.OpenFileForWriteShim(filePath, ?fileMode, ?fileAccess, ?fileShare) =
            inner.OpenFileForWriteShim(filePath, ?fileMode = fileMode, ?fileAccess = fileAccess, ?fileShare = fileShare)

        member _.GetFullPathShim fileName = inner.GetFullPathShim fileName

        member _.GetFullFilePathInDirectoryShim dir fileName =
            inner.GetFullFilePathInDirectoryShim dir fileName

        member _.IsPathRootedShim path = inner.IsPathRootedShim path
        member _.NormalizePathShim path = inner.NormalizePathShim path
        member _.IsInvalidPathShim path = inner.IsInvalidPathShim path
        member _.GetTempPathShim() = inner.GetTempPathShim()
        member _.GetDirectoryNameShim path = inner.GetDirectoryNameShim path

        member _.GetLastWriteTimeShim fileName =
            noteSource "read" fileName
            inner.GetLastWriteTimeShim fileName

        member _.GetCreationTimeShim path = inner.GetCreationTimeShim path
        member _.CopyShim(src, dest, overwrite) = inner.CopyShim(src, dest, overwrite)

        member _.FileExistsShim fileName =
            noteSource "exists" fileName
            inner.FileExistsShim fileName

        member _.FileDeleteShim fileName = inner.FileDeleteShim fileName
        member _.DirectoryCreateShim path = inner.DirectoryCreateShim path
        member _.DirectoryExistsShim path = inner.DirectoryExistsShim path
        member _.DirectoryDeleteShim path = inner.DirectoryDeleteShim path
        member _.EnumerateFilesShim(path, pattern) = inner.EnumerateFilesShim(path, pattern)
        member _.EnumerateDirectoriesShim path = inner.EnumerateDirectoriesShim path
        member _.IsStableFileHeuristic fileName = inner.IsStableFileHeuristic fileName

        member _.ChangeExtensionShim(path, extension) =
            inner.ChangeExtensionShim(path, extension)

/// Installs a recording file system over the current one, noting through `note`.
let installWith (note: Note) : unit =
    FileSystemAutoOpens.FileSystem <- RecordingFileSystem(FileSystemAutoOpens.FileSystem, note)

/// The variable a traced launch sets: this process records into that directory.
[<Literal>]
let TracedEnv = "TESTPRUNE_TRACE_OUT"

/// `Scopes.NoteInput(kind, path)` when this process is traced and the recorder has it.
let traceNote (getEnv: string -> string) : Note option =
    if String.IsNullOrEmpty(getEnv TracedEnv) then
        None
    else
        Type.GetType("TestPrune.Trace.Recorder.Scopes, TestPrune.Trace.Recorder", false)
        |> Option.ofObj
        |> Option.bind (fun scopes ->
            scopes.GetMethod("NoteInput", [| typeof<string>; typeof<string> |])
            |> Option.ofObj)
        |> Option.map (fun noteInput -> fun kind path -> noteInput.Invoke(null, [| box kind; box path |]) |> ignore)

/// Installs the recording file system when this process is traced; otherwise nothing.
let installIfTraced () : unit =
    traceNote Environment.GetEnvironmentVariable |> Option.iter installWith
