namespace TestPrune.Trace.Recorder

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

/// Call-site replacements for the BCL's file readers, existence probes and directory
/// listings. Each shim notes (kind, absolute path) on the scope doing the I/O, then does
/// exactly what the original does. The weaver rewrites `call File::ReadAllText(string)`
/// into `call Io::File_ReadAllText(string)`, `newobj FileStream(string, FileMode)` into
/// `call Io::FileStream_ctor(string, FileMode)`, and an instance call into a static one
/// taking the instance first, so the stack shape never changes.
///
/// A stream opened for writing is noted as a read. That over-approximates: a test that
/// writes a repository file is reselected when the file changes, which is sound.
[<AbstractClass; Sealed>]
type Io =
    /// True when `full` is `root` or lies under it. `root` may end with a separator.
    static member private IsUnder(full: string, root: string) =
        let r = root.TrimEnd Path.DirectorySeparatorChar

        full.StartsWith(r, StringComparison.Ordinal)
        && (full.Length = r.Length || full.[r.Length] = Path.DirectorySeparatorChar)

    /// Notes `path` as an input of `kind` (`read`, `exists` or `list`) on `state`'s note
    /// scope when it resolves under the repository root. Never throws.
    static member internal NoteWith(state: RecorderState, kind: string, path: string) : unit =
        if not (isNull state) && not (isNull path) && not (isNull state.RepoRoot) then
            let full =
                try
                    Path.GetFullPath path
                with _ ->
                    null

            if not (isNull full) && Io.IsUnder(full, state.RepoRoot) then
                state.NoteScope().Inputs.TryAdd(struct (kind, full), 0uy) |> ignore

    /// Shim for `File.Exists(string)`.
    static member File_Exists(path: string) : bool =
        Io.NoteWith(Runtime.state, "exists", path)
        File.Exists path

    /// Shim for `File.ReadAllText(string)`.
    static member File_ReadAllText(path: string) : string =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllText path

    /// Shim for `File.ReadAllText(string, Encoding)`.
    static member File_ReadAllText(path: string, encoding: Encoding) : string =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllText(path, encoding)

    /// Shim for `File.ReadAllLines(string)`.
    static member File_ReadAllLines(path: string) : string[] =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllLines path

    /// Shim for `File.ReadAllBytes(string)`.
    static member File_ReadAllBytes(path: string) : byte[] =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllBytes path

    /// Shim for `File.ReadLines(string)`.
    static member File_ReadLines(path: string) : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadLines path

    /// Shim for `File.OpenRead(string)`.
    static member File_OpenRead(path: string) : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        File.OpenRead path

    /// Shim for `File.OpenText(string)`.
    static member File_OpenText(path: string) : StreamReader =
        Io.NoteWith(Runtime.state, "read", path)
        File.OpenText path

    /// Shim for `File.Open(string, FileMode)`.
    static member File_Open(path: string, mode: FileMode) : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        File.Open(path, mode)

    /// Shim for `File.Open(string, FileMode, FileAccess)`.
    static member File_Open(path: string, mode: FileMode, access: FileAccess) : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        File.Open(path, mode, access)

    /// Shim for `File.Open(string, FileMode, FileAccess, FileShare)`.
    static member File_Open(path: string, mode: FileMode, access: FileAccess, share: FileShare) : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        File.Open(path, mode, access, share)

    /// Shim for `File.ReadAllTextAsync(string, CancellationToken)`.
    static member File_ReadAllTextAsync(path: string, ct: CancellationToken) : Task<string> =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllTextAsync(path, ct)

    /// Shim for `File.ReadAllLinesAsync(string, CancellationToken)`.
    static member File_ReadAllLinesAsync(path: string, ct: CancellationToken) : Task<string[]> =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllLinesAsync(path, ct)

    /// Shim for `File.ReadAllBytesAsync(string, CancellationToken)`.
    static member File_ReadAllBytesAsync(path: string, ct: CancellationToken) : Task<byte[]> =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllBytesAsync(path, ct)

    /// Shim for `Directory.Exists(string)`.
    static member Directory_Exists(path: string) : bool =
        Io.NoteWith(Runtime.state, "exists", path)
        Directory.Exists path

    /// Shim for `Directory.GetFiles(string)`.
    static member Directory_GetFiles(path: string) : string[] =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.GetFiles path

    /// Shim for `Directory.GetFiles(string, string)`.
    static member Directory_GetFiles(path: string, pattern: string) : string[] =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.GetFiles(path, pattern)

    /// Shim for `Directory.GetFiles(string, string, SearchOption)`.
    static member Directory_GetFiles(path: string, pattern: string, option: SearchOption) : string[] =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.GetFiles(path, pattern, option)

    /// Shim for `Directory.EnumerateFiles(string)`.
    static member Directory_EnumerateFiles(path: string) : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.EnumerateFiles path

    /// Shim for `Directory.EnumerateFiles(string, string)`.
    static member Directory_EnumerateFiles(path: string, pattern: string) : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.EnumerateFiles(path, pattern)

    /// Shim for `Directory.EnumerateFiles(string, string, SearchOption)`.
    static member Directory_EnumerateFiles(path: string, pattern: string, option: SearchOption) : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.EnumerateFiles(path, pattern, option)

    /// Shim for `Directory.GetDirectories(string)`.
    static member Directory_GetDirectories(path: string) : string[] =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.GetDirectories path

    /// Shim for `Directory.GetDirectories(string, string)`.
    static member Directory_GetDirectories(path: string, pattern: string) : string[] =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.GetDirectories(path, pattern)

    /// Shim for `Directory.GetDirectories(string, string, SearchOption)`.
    static member Directory_GetDirectories(path: string, pattern: string, option: SearchOption) : string[] =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.GetDirectories(path, pattern, option)

    /// Shim for `Directory.EnumerateDirectories(string)`.
    static member Directory_EnumerateDirectories(path: string) : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.EnumerateDirectories path

    /// Shim for `Directory.EnumerateDirectories(string, string)`.
    static member Directory_EnumerateDirectories(path: string, pattern: string) : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.EnumerateDirectories(path, pattern)

    /// Shim for `Directory.EnumerateDirectories(string, string, SearchOption)`.
    static member Directory_EnumerateDirectories
        (path: string, pattern: string, option: SearchOption)
        : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.EnumerateDirectories(path, pattern, option)

    /// Shim for `new FileStream(string, FileMode)`.
    static member FileStream_ctor(path: string, mode: FileMode) : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        new FileStream(path, mode)

    /// Shim for `new FileStream(string, FileMode, FileAccess)`.
    static member FileStream_ctor(path: string, mode: FileMode, access: FileAccess) : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        new FileStream(path, mode, access)

    /// Shim for `new FileStream(string, FileMode, FileAccess, FileShare)`.
    static member FileStream_ctor(path: string, mode: FileMode, access: FileAccess, share: FileShare) : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        new FileStream(path, mode, access, share)

    /// Shim for `new StreamReader(string)`.
    static member StreamReader_ctor(path: string) : StreamReader =
        Io.NoteWith(Runtime.state, "read", path)
        new StreamReader(path)

    /// Shim for `FileSystemInfo.Exists`, the virtual a compiler may bind `fi.Exists` to.
    static member FileSystemInfo_get_Exists(info: FileSystemInfo) : bool =
        Io.NoteWith(Runtime.state, "exists", info.FullName)
        info.Exists

    /// Shim for `FileInfo.Exists`.
    static member FileInfo_get_Exists(fi: FileInfo) : bool =
        Io.NoteWith(Runtime.state, "exists", fi.FullName)
        fi.Exists

    /// Shim for `FileInfo.OpenRead()`.
    static member FileInfo_OpenRead(fi: FileInfo) : FileStream =
        Io.NoteWith(Runtime.state, "read", fi.FullName)
        fi.OpenRead()

    /// Shim for `FileInfo.OpenText()`.
    static member FileInfo_OpenText(fi: FileInfo) : StreamReader =
        Io.NoteWith(Runtime.state, "read", fi.FullName)
        fi.OpenText()

    /// Shim for `DirectoryInfo.Exists`.
    static member DirectoryInfo_get_Exists(di: DirectoryInfo) : bool =
        Io.NoteWith(Runtime.state, "exists", di.FullName)
        di.Exists
