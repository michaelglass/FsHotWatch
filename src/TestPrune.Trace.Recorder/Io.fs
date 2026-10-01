namespace TestPrune.Trace.Recorder

open Microsoft.Win32.SafeHandles
open System
open System.Collections.Generic
open System.IO
open System.Runtime.CompilerServices
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Xml
open System.Xml.Linq

/// Call-site replacements for the BCL's file readers (XML loaders by path among them),
/// existence probes, directory listings and metadata readers. Each shim notes (kind, absolute path) on the scope doing the I/O, then does
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

    /// `list-deep` for a listing that recurses into subdirectories, else `list`. Kept out of
    /// line: this assembly is optimized, and inlined into each shim the branch would be
    /// duplicated at every call site.
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member private ListKind(option: SearchOption) =
        if option = SearchOption.AllDirectories then
            "list-deep"
        else
            "list"

    /// `list-deep` for a listing whose options recurse into subdirectories, else `list`.
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member private ListKind(options: EnumerationOptions) =
        if not (isNull options) && options.RecurseSubdirectories then
            "list-deep"
        else
            "list"

    /// Notes `path` as an input of `kind` (`read`, `exists`, `list`, `list-deep` or `meta`) on `state`'s note
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

    /// Shim for `File.ReadAllLines(string, Encoding)`.
    static member File_ReadAllLines(path: string, encoding: Encoding) : string[] =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllLines(path, encoding)

    /// Shim for `File.ReadAllBytes(string)`.
    static member File_ReadAllBytes(path: string) : byte[] =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllBytes path

    /// Shim for `File.ReadLines(string)`.
    static member File_ReadLines(path: string) : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadLines path

    /// Shim for `File.ReadLines(string, Encoding)`.
    static member File_ReadLines(path: string, encoding: Encoding) : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadLines(path, encoding)

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

    /// Shim for `File.ReadAllTextAsync(string, Encoding, CancellationToken)`.
    static member File_ReadAllTextAsync(path: string, encoding: Encoding, ct: CancellationToken) : Task<string> =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllTextAsync(path, encoding, ct)

    /// Shim for `File.ReadAllLinesAsync(string, Encoding, CancellationToken)`.
    static member File_ReadAllLinesAsync(path: string, encoding: Encoding, ct: CancellationToken) : Task<string[]> =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadAllLinesAsync(path, encoding, ct)

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
        Io.NoteWith(Runtime.state, Io.ListKind option, path)
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
        Io.NoteWith(Runtime.state, Io.ListKind option, path)
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
        Io.NoteWith(Runtime.state, Io.ListKind option, path)
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
        Io.NoteWith(Runtime.state, Io.ListKind option, path)
        Directory.EnumerateDirectories(path, pattern, option)

    /// Shim for `Directory.GetFileSystemEntries(string)`.
    static member Directory_GetFileSystemEntries(path: string) : string[] =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.GetFileSystemEntries path

    /// Shim for `Directory.GetFileSystemEntries(string, string)`.
    static member Directory_GetFileSystemEntries(path: string, pattern: string) : string[] =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.GetFileSystemEntries(path, pattern)

    /// Shim for `Directory.GetFileSystemEntries(string, string, SearchOption)`.
    static member Directory_GetFileSystemEntries(path: string, pattern: string, option: SearchOption) : string[] =
        Io.NoteWith(Runtime.state, Io.ListKind option, path)
        Directory.GetFileSystemEntries(path, pattern, option)

    /// Shim for `Directory.EnumerateFileSystemEntries(string)`.
    static member Directory_EnumerateFileSystemEntries(path: string) : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.EnumerateFileSystemEntries path

    /// Shim for `Directory.EnumerateFileSystemEntries(string, string)`.
    static member Directory_EnumerateFileSystemEntries(path: string, pattern: string) : IEnumerable<string> =
        Io.NoteWith(Runtime.state, "list", path)
        Directory.EnumerateFileSystemEntries(path, pattern)

    /// Shim for `Directory.EnumerateFileSystemEntries(string, string, SearchOption)`.
    static member Directory_EnumerateFileSystemEntries
        (path: string, pattern: string, option: SearchOption)
        : IEnumerable<string> =
        Io.NoteWith(Runtime.state, Io.ListKind option, path)
        Directory.EnumerateFileSystemEntries(path, pattern, option)

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

    /// Shim for `new StreamReader(string, Encoding)`.
    static member StreamReader_ctor(path: string, encoding: Encoding) : StreamReader =
        Io.NoteWith(Runtime.state, "read", path)
        new StreamReader(path, encoding)

    /// Shim for `XDocument.Load(string)`.
    static member XDocument_Load(uri: string) : XDocument =
        Io.NoteWith(Runtime.state, "read", uri)
        XDocument.Load uri

    /// Shim for `XDocument.Load(string, LoadOptions)`.
    static member XDocument_Load(uri: string, options: LoadOptions) : XDocument =
        Io.NoteWith(Runtime.state, "read", uri)
        XDocument.Load(uri, options)

    /// Shim for `XElement.Load(string)`.
    static member XElement_Load(uri: string) : XElement =
        Io.NoteWith(Runtime.state, "read", uri)
        XElement.Load uri

    /// Shim for `XElement.Load(string, LoadOptions)`.
    static member XElement_Load(uri: string, options: LoadOptions) : XElement =
        Io.NoteWith(Runtime.state, "read", uri)
        XElement.Load(uri, options)

    /// Shim for `XmlReader.Create(string)`.
    static member XmlReader_Create(uri: string) : XmlReader =
        Io.NoteWith(Runtime.state, "read", uri)
        XmlReader.Create uri

    /// Shim for `XmlReader.Create(string, XmlReaderSettings)`.
    static member XmlReader_Create(uri: string, settings: XmlReaderSettings) : XmlReader =
        Io.NoteWith(Runtime.state, "read", uri)
        XmlReader.Create(uri, settings)

    /// Shim for `XmlDocument.Load(string)`.
    static member XmlDocument_Load(doc: XmlDocument, uri: string) : unit =
        Io.NoteWith(Runtime.state, "read", uri)
        doc.Load uri

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

    /// Shim for `DirectoryInfo.GetFiles()`.
    static member DirectoryInfo_GetFiles(di: DirectoryInfo) : FileInfo[] =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.GetFiles()

    /// Shim for `DirectoryInfo.GetFiles(string)`.
    static member DirectoryInfo_GetFiles(di: DirectoryInfo, pattern: string) : FileInfo[] =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.GetFiles pattern

    /// Shim for `DirectoryInfo.GetFiles(string, SearchOption)`.
    static member DirectoryInfo_GetFiles(di: DirectoryInfo, pattern: string, option: SearchOption) : FileInfo[] =
        Io.NoteWith(Runtime.state, Io.ListKind option, di.FullName)
        di.GetFiles(pattern, option)

    /// Shim for `DirectoryInfo.EnumerateFiles()`.
    static member DirectoryInfo_EnumerateFiles(di: DirectoryInfo) : IEnumerable<FileInfo> =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.EnumerateFiles()

    /// Shim for `DirectoryInfo.EnumerateFiles(string)`.
    static member DirectoryInfo_EnumerateFiles(di: DirectoryInfo, pattern: string) : IEnumerable<FileInfo> =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.EnumerateFiles pattern

    /// Shim for `DirectoryInfo.EnumerateFiles(string, SearchOption)`.
    static member DirectoryInfo_EnumerateFiles
        (di: DirectoryInfo, pattern: string, option: SearchOption)
        : IEnumerable<FileInfo> =
        Io.NoteWith(Runtime.state, Io.ListKind option, di.FullName)
        di.EnumerateFiles(pattern, option)

    /// Shim for `DirectoryInfo.Exists`.
    static member DirectoryInfo_get_Exists(di: DirectoryInfo) : bool =
        Io.NoteWith(Runtime.state, "exists", di.FullName)
        di.Exists

    /// Shim for `Directory.GetFiles(string, string, EnumerationOptions)`.
    static member Directory_GetFiles(path: string, pattern: string, options: EnumerationOptions) : string[] =
        Io.NoteWith(Runtime.state, Io.ListKind options, path)
        Directory.GetFiles(path, pattern, options)

    /// Shim for `Directory.EnumerateFiles(string, string, EnumerationOptions)`.
    static member Directory_EnumerateFiles
        (path: string, pattern: string, options: EnumerationOptions)
        : IEnumerable<string> =
        Io.NoteWith(Runtime.state, Io.ListKind options, path)
        Directory.EnumerateFiles(path, pattern, options)

    /// Shim for `Directory.GetDirectories(string, string, EnumerationOptions)`.
    static member Directory_GetDirectories(path: string, pattern: string, options: EnumerationOptions) : string[] =
        Io.NoteWith(Runtime.state, Io.ListKind options, path)
        Directory.GetDirectories(path, pattern, options)

    /// Shim for `Directory.EnumerateDirectories(string, string, EnumerationOptions)`.
    static member Directory_EnumerateDirectories
        (path: string, pattern: string, options: EnumerationOptions)
        : IEnumerable<string> =
        Io.NoteWith(Runtime.state, Io.ListKind options, path)
        Directory.EnumerateDirectories(path, pattern, options)

    /// Shim for `Directory.GetFileSystemEntries(string, string, EnumerationOptions)`.
    static member Directory_GetFileSystemEntries
        (path: string, pattern: string, options: EnumerationOptions)
        : string[] =
        Io.NoteWith(Runtime.state, Io.ListKind options, path)
        Directory.GetFileSystemEntries(path, pattern, options)

    /// Shim for `Directory.EnumerateFileSystemEntries(string, string, EnumerationOptions)`.
    static member Directory_EnumerateFileSystemEntries
        (path: string, pattern: string, options: EnumerationOptions)
        : IEnumerable<string> =
        Io.NoteWith(Runtime.state, Io.ListKind options, path)
        Directory.EnumerateFileSystemEntries(path, pattern, options)

    /// Shim for `DirectoryInfo.GetDirectories()`.
    static member DirectoryInfo_GetDirectories(di: DirectoryInfo) : DirectoryInfo[] =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.GetDirectories()

    /// Shim for `DirectoryInfo.GetDirectories(string)`.
    static member DirectoryInfo_GetDirectories(di: DirectoryInfo, pattern: string) : DirectoryInfo[] =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.GetDirectories pattern

    /// Shim for `DirectoryInfo.GetDirectories(string, SearchOption)`.
    static member DirectoryInfo_GetDirectories
        (di: DirectoryInfo, pattern: string, option: SearchOption)
        : DirectoryInfo[] =
        Io.NoteWith(Runtime.state, Io.ListKind option, di.FullName)
        di.GetDirectories(pattern, option)

    /// Shim for `DirectoryInfo.EnumerateDirectories()`.
    static member DirectoryInfo_EnumerateDirectories(di: DirectoryInfo) : IEnumerable<DirectoryInfo> =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.EnumerateDirectories()

    /// Shim for `DirectoryInfo.EnumerateDirectories(string)`.
    static member DirectoryInfo_EnumerateDirectories(di: DirectoryInfo, pattern: string) : IEnumerable<DirectoryInfo> =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.EnumerateDirectories pattern

    /// Shim for `DirectoryInfo.EnumerateDirectories(string, SearchOption)`.
    static member DirectoryInfo_EnumerateDirectories
        (di: DirectoryInfo, pattern: string, option: SearchOption)
        : IEnumerable<DirectoryInfo> =
        Io.NoteWith(Runtime.state, Io.ListKind option, di.FullName)
        di.EnumerateDirectories(pattern, option)

    /// Shim for `DirectoryInfo.GetFileSystemInfos()`.
    static member DirectoryInfo_GetFileSystemInfos(di: DirectoryInfo) : FileSystemInfo[] =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.GetFileSystemInfos()

    /// Shim for `DirectoryInfo.GetFileSystemInfos(string)`.
    static member DirectoryInfo_GetFileSystemInfos(di: DirectoryInfo, pattern: string) : FileSystemInfo[] =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.GetFileSystemInfos pattern

    /// Shim for `DirectoryInfo.GetFileSystemInfos(string, SearchOption)`.
    static member DirectoryInfo_GetFileSystemInfos
        (di: DirectoryInfo, pattern: string, option: SearchOption)
        : FileSystemInfo[] =
        Io.NoteWith(Runtime.state, Io.ListKind option, di.FullName)
        di.GetFileSystemInfos(pattern, option)

    /// Shim for `DirectoryInfo.EnumerateFileSystemInfos()`.
    static member DirectoryInfo_EnumerateFileSystemInfos(di: DirectoryInfo) : IEnumerable<FileSystemInfo> =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.EnumerateFileSystemInfos()

    /// Shim for `DirectoryInfo.EnumerateFileSystemInfos(string)`.
    static member DirectoryInfo_EnumerateFileSystemInfos
        (di: DirectoryInfo, pattern: string)
        : IEnumerable<FileSystemInfo> =
        Io.NoteWith(Runtime.state, "list", di.FullName)
        di.EnumerateFileSystemInfos pattern

    /// Shim for `DirectoryInfo.EnumerateFileSystemInfos(string, SearchOption)`.
    static member DirectoryInfo_EnumerateFileSystemInfos
        (di: DirectoryInfo, pattern: string, option: SearchOption)
        : IEnumerable<FileSystemInfo> =
        Io.NoteWith(Runtime.state, Io.ListKind option, di.FullName)
        di.EnumerateFileSystemInfos(pattern, option)

    /// Shim for `DirectoryInfo.GetFiles(string, EnumerationOptions)`.
    static member DirectoryInfo_GetFiles(di: DirectoryInfo, pattern: string, options: EnumerationOptions) : FileInfo[] =
        Io.NoteWith(Runtime.state, Io.ListKind options, di.FullName)
        di.GetFiles(pattern, options)

    /// Shim for `DirectoryInfo.EnumerateFiles(string, EnumerationOptions)`.
    static member DirectoryInfo_EnumerateFiles
        (di: DirectoryInfo, pattern: string, options: EnumerationOptions)
        : IEnumerable<FileInfo> =
        Io.NoteWith(Runtime.state, Io.ListKind options, di.FullName)
        di.EnumerateFiles(pattern, options)

    /// Shim for `DirectoryInfo.GetDirectories(string, EnumerationOptions)`.
    static member DirectoryInfo_GetDirectories
        (di: DirectoryInfo, pattern: string, options: EnumerationOptions)
        : DirectoryInfo[] =
        Io.NoteWith(Runtime.state, Io.ListKind options, di.FullName)
        di.GetDirectories(pattern, options)

    /// Shim for `DirectoryInfo.EnumerateDirectories(string, EnumerationOptions)`.
    static member DirectoryInfo_EnumerateDirectories
        (di: DirectoryInfo, pattern: string, options: EnumerationOptions)
        : IEnumerable<DirectoryInfo> =
        Io.NoteWith(Runtime.state, Io.ListKind options, di.FullName)
        di.EnumerateDirectories(pattern, options)

    /// Shim for `DirectoryInfo.GetFileSystemInfos(string, EnumerationOptions)`.
    static member DirectoryInfo_GetFileSystemInfos
        (di: DirectoryInfo, pattern: string, options: EnumerationOptions)
        : FileSystemInfo[] =
        Io.NoteWith(Runtime.state, Io.ListKind options, di.FullName)
        di.GetFileSystemInfos(pattern, options)

    /// Shim for `DirectoryInfo.EnumerateFileSystemInfos(string, EnumerationOptions)`.
    static member DirectoryInfo_EnumerateFileSystemInfos
        (di: DirectoryInfo, pattern: string, options: EnumerationOptions)
        : IEnumerable<FileSystemInfo> =
        Io.NoteWith(Runtime.state, Io.ListKind options, di.FullName)
        di.EnumerateFileSystemInfos(pattern, options)

    /// Shim for `File.OpenHandle(string, FileMode, FileAccess, FileShare, FileOptions, long)`.
    static member File_OpenHandle
        (
            path: string,
            mode: FileMode,
            access: FileAccess,
            share: FileShare,
            options: FileOptions,
            preallocationSize: int64
        ) : SafeFileHandle =
        Io.NoteWith(Runtime.state, "read", path)
        File.OpenHandle(path, mode, access, share, options, preallocationSize)

    /// Shim for `FileStream(string, FileMode, FileAccess, FileShare, int)`.
    static member FileStream_ctor
        (path: string, mode: FileMode, access: FileAccess, share: FileShare, bufferSize: int)
        : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        new FileStream(path, mode, access, share, bufferSize)

    /// Shim for `FileStream(string, FileMode, FileAccess, FileShare, int, bool)`.
    static member FileStream_ctor
        (path: string, mode: FileMode, access: FileAccess, share: FileShare, bufferSize: int, useAsync: bool)
        : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        new FileStream(path, mode, access, share, bufferSize, useAsync)

    /// Shim for `FileStream(string, FileMode, FileAccess, FileShare, int, FileOptions)`.
    static member FileStream_ctor
        (path: string, mode: FileMode, access: FileAccess, share: FileShare, bufferSize: int, options: FileOptions)
        : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        new FileStream(path, mode, access, share, bufferSize, options)

    /// Shim for `FileStream(string, FileStreamOptions)`.
    static member FileStream_ctor(path: string, options: FileStreamOptions) : FileStream =
        Io.NoteWith(Runtime.state, "read", path)
        new FileStream(path, options)

    /// Shim for `StreamReader(string, bool)`.
    static member StreamReader_ctor(path: string, detectEncoding: bool) : StreamReader =
        Io.NoteWith(Runtime.state, "read", path)
        new StreamReader(path, detectEncoding)

    /// Shim for `StreamReader(string, Encoding, bool)`.
    static member StreamReader_ctor(path: string, encoding: Encoding, detectEncoding: bool) : StreamReader =
        Io.NoteWith(Runtime.state, "read", path)
        new StreamReader(path, encoding, detectEncoding)

    /// Shim for `StreamReader(string, Encoding, bool, int)`.
    static member StreamReader_ctor
        (path: string, encoding: Encoding, detectEncoding: bool, bufferSize: int)
        : StreamReader =
        Io.NoteWith(Runtime.state, "read", path)
        new StreamReader(path, encoding, detectEncoding, bufferSize)

    /// Shim for `StreamReader(string, FileStreamOptions)`.
    static member StreamReader_ctor(path: string, options: FileStreamOptions) : StreamReader =
        Io.NoteWith(Runtime.state, "read", path)
        new StreamReader(path, options)

    /// Shim for `StreamReader(string, Encoding, bool, FileStreamOptions)`.
    static member StreamReader_ctor
        (path: string, encoding: Encoding, detectEncoding: bool, options: FileStreamOptions)
        : StreamReader =
        Io.NoteWith(Runtime.state, "read", path)
        new StreamReader(path, encoding, detectEncoding, options)

    /// Shim for `FileInfo.Open(FileMode)`.
    static member FileInfo_Open(fi: FileInfo, mode: FileMode) : FileStream =
        Io.NoteWith(Runtime.state, "read", fi.FullName)
        fi.Open mode

    /// Shim for `FileInfo.Open(FileMode, FileAccess)`.
    static member FileInfo_Open(fi: FileInfo, mode: FileMode, access: FileAccess) : FileStream =
        Io.NoteWith(Runtime.state, "read", fi.FullName)
        fi.Open(mode, access)

    /// Shim for `FileInfo.Open(FileMode, FileAccess, FileShare)`.
    static member FileInfo_Open(fi: FileInfo, mode: FileMode, access: FileAccess, share: FileShare) : FileStream =
        Io.NoteWith(Runtime.state, "read", fi.FullName)
        fi.Open(mode, access, share)

    /// Shim for `FileInfo.Open(FileStreamOptions)`.
    static member FileInfo_Open(fi: FileInfo, options: FileStreamOptions) : FileStream =
        Io.NoteWith(Runtime.state, "read", fi.FullName)
        fi.Open options

    /// Shim for `File.ReadLinesAsync(string, CancellationToken)`.
    static member File_ReadLinesAsync(path: string, ct: CancellationToken) : IAsyncEnumerable<string> =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadLinesAsync(path, ct)

    /// Shim for `File.ReadLinesAsync(string, Encoding, CancellationToken)`.
    static member File_ReadLinesAsync
        (path: string, encoding: Encoding, ct: CancellationToken)
        : IAsyncEnumerable<string> =
        Io.NoteWith(Runtime.state, "read", path)
        File.ReadLinesAsync(path, encoding, ct)

    /// Shim for `File.GetLastWriteTime(string)`.
    static member File_GetLastWriteTime(path: string) : DateTime =
        Io.NoteWith(Runtime.state, "meta", path)
        File.GetLastWriteTime path

    /// Shim for `File.GetLastWriteTimeUtc(string)`.
    static member File_GetLastWriteTimeUtc(path: string) : DateTime =
        Io.NoteWith(Runtime.state, "meta", path)
        File.GetLastWriteTimeUtc path

    /// Shim for `File.GetCreationTime(string)`.
    static member File_GetCreationTime(path: string) : DateTime =
        Io.NoteWith(Runtime.state, "meta", path)
        File.GetCreationTime path

    /// Shim for `File.GetCreationTimeUtc(string)`.
    static member File_GetCreationTimeUtc(path: string) : DateTime =
        Io.NoteWith(Runtime.state, "meta", path)
        File.GetCreationTimeUtc path

    /// Shim for `File.GetLastAccessTime(string)`.
    static member File_GetLastAccessTime(path: string) : DateTime =
        Io.NoteWith(Runtime.state, "meta", path)
        File.GetLastAccessTime path

    /// Shim for `File.GetLastAccessTimeUtc(string)`.
    static member File_GetLastAccessTimeUtc(path: string) : DateTime =
        Io.NoteWith(Runtime.state, "meta", path)
        File.GetLastAccessTimeUtc path

    /// Shim for `File.GetAttributes(string)`.
    static member File_GetAttributes(path: string) : FileAttributes =
        Io.NoteWith(Runtime.state, "meta", path)
        File.GetAttributes path

    /// Shim for `File.GetUnixFileMode(string)`.
    static member File_GetUnixFileMode(path: string) : UnixFileMode =
        Io.NoteWith(Runtime.state, "meta", path)
        File.GetUnixFileMode path

    /// Shim for `Directory.GetLastWriteTime(string)`.
    static member Directory_GetLastWriteTime(path: string) : DateTime =
        Io.NoteWith(Runtime.state, "meta", path)
        Directory.GetLastWriteTime path

    /// Shim for `Directory.GetLastWriteTimeUtc(string)`.
    static member Directory_GetLastWriteTimeUtc(path: string) : DateTime =
        Io.NoteWith(Runtime.state, "meta", path)
        Directory.GetLastWriteTimeUtc path

    /// Shim for `Directory.GetCreationTime(string)`.
    static member Directory_GetCreationTime(path: string) : DateTime =
        Io.NoteWith(Runtime.state, "meta", path)
        Directory.GetCreationTime path

    /// Shim for `Directory.GetCreationTimeUtc(string)`.
    static member Directory_GetCreationTimeUtc(path: string) : DateTime =
        Io.NoteWith(Runtime.state, "meta", path)
        Directory.GetCreationTimeUtc path

    /// Shim for `FileSystemInfo.LastWriteTime`.
    static member FileSystemInfo_get_LastWriteTime(info: FileSystemInfo) : DateTime =
        Io.NoteWith(Runtime.state, "meta", info.FullName)
        info.LastWriteTime

    /// Shim for `FileSystemInfo.LastWriteTimeUtc`.
    static member FileSystemInfo_get_LastWriteTimeUtc(info: FileSystemInfo) : DateTime =
        Io.NoteWith(Runtime.state, "meta", info.FullName)
        info.LastWriteTimeUtc

    /// Shim for `FileSystemInfo.CreationTime`.
    static member FileSystemInfo_get_CreationTime(info: FileSystemInfo) : DateTime =
        Io.NoteWith(Runtime.state, "meta", info.FullName)
        info.CreationTime

    /// Shim for `FileSystemInfo.CreationTimeUtc`.
    static member FileSystemInfo_get_CreationTimeUtc(info: FileSystemInfo) : DateTime =
        Io.NoteWith(Runtime.state, "meta", info.FullName)
        info.CreationTimeUtc

    /// Shim for `FileSystemInfo.Attributes`.
    static member FileSystemInfo_get_Attributes(info: FileSystemInfo) : FileAttributes =
        Io.NoteWith(Runtime.state, "meta", info.FullName)
        info.Attributes

    /// Shim for `FileSystemInfo.UnixFileMode`.
    static member FileSystemInfo_get_UnixFileMode(info: FileSystemInfo) : UnixFileMode =
        Io.NoteWith(Runtime.state, "meta", info.FullName)
        info.UnixFileMode

    /// Shim for `FileInfo.Length`.
    static member FileInfo_get_Length(fi: FileInfo) : int64 =
        Io.NoteWith(Runtime.state, "meta", fi.FullName)
        fi.Length

    /// Shim for `FileInfo.IsReadOnly`.
    static member FileInfo_get_IsReadOnly(fi: FileInfo) : bool =
        Io.NoteWith(Runtime.state, "meta", fi.FullName)
        fi.IsReadOnly
