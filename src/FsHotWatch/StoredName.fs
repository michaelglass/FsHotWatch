/// How a path component's stored spelling is learned: by asking the entry itself where
/// the platform can say (macOS), from the name as given where that already is the stored
/// name (Linux), and otherwise from a listing of its directory. A listing costs
/// O(entries in the directory) per component, and canonicalizing repeats it for every
/// component and every symlink hop.
module FsHotWatch.StoredName

open System
open System.IO
open System.Runtime.InteropServices

/// What the volume answered about a path's last component.
[<RequireQualifiedAccess>]
type Answer =
    | Stored of string
    | Absent
    /// Any other failure: the caller learns the name another way.
    | Unanswered

/// Where a platform learns a stored name.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type Source =
    /// Ask the entry itself.
    | Native of ask: (string -> Answer)
    /// The name as given is the stored name: a case-sensitive filesystem.
    | AsGiven
    /// Only a listing of the directory can tell.
    | Listing

/// The name `name` is stored under inside `dir` according to `source`, with `listing`
/// for what a native lookup cannot answer and for platforms that only list, and
/// `exists` to tell a present entry from a missing one when the name is taken as given.
let internal lookupWith
    (source: Source)
    (listing: string -> string -> string option)
    (exists: string -> bool)
    (dir: string)
    (name: string)
    : string option =
    match source with
    | Source.Native ask ->
        match ask (Path.Combine(dir, name)) with
        | Answer.Stored stored -> Some stored
        | Answer.Absent -> None
        | Answer.Unanswered -> listing dir name
    | Source.AsGiven -> if exists (Path.Combine(dir, name)) then Some name else None
    | Source.Listing -> listing dir name

/// The names in `dir`, lazily: what a platform that can only list reads.
let internal listNames (dir: string) =
    Directory.EnumerateFileSystemEntries dir |> Seq.map Path.GetFileName

// ─── macOS: getattrlist(2), asked for one attribute, the entry's name as stored ───
[<Struct; StructLayout(LayoutKind.Sequential)>]
type private AttrList =
    val mutable BitmapCount: uint16
    val mutable Reserved: uint16
    val mutable CommonAttr: uint32
    val mutable VolAttr: uint32
    val mutable DirAttr: uint32
    val mutable FileAttr: uint32
    val mutable ForkAttr: uint32

[<DllImport("libc", SetLastError = true)>]
extern int private getattrlist(
    [<MarshalAs(UnmanagedType.LPUTF8Str)>] string path,
    AttrList& attrList,
    byte[] attrBuf,
    unativeint attrBufSize,
    uint32 options
)

[<Literal>]
let private AttrBitMapCount = 5us

[<Literal>]
let private AttrCmnName = 0x00000001u

/// The entry itself, not what a symlink there points at.
[<Literal>]
let private FsoptNofollow = 0x00000001u

[<Literal>]
let private ENOENT = 2

[<Literal>]
let private ENOTDIR = 20

/// The name `path`'s last component is stored under. One call, whatever the size
/// of the directory holding it. The reply is a u32 length, then an
/// `attrreference_t` (i32 offset from itself, u32 length including the NUL).
let internal askMacOS (path: string) : Answer =
    let mutable request = AttrList()
    request.BitmapCount <- AttrBitMapCount
    request.CommonAttr <- AttrCmnName
    // NAME_MAX UTF-8 bytes, the header and the NUL.
    let reply = Array.zeroCreate<byte> 1100

    if getattrlist (path, &request, reply, unativeint reply.Length, FsoptNofollow) = 0 then
        let offset = BitConverter.ToInt32(reply, 4)
        let length = int (BitConverter.ToUInt32(reply, 8))
        Answer.Stored(Text.Encoding.UTF8.GetString(reply, 4 + offset, length - 1))
    else
        match Marshal.GetLastPInvokeError() with
        | ENOENT
        | ENOTDIR -> Answer.Absent
        | _ -> Answer.Unanswered

/// This platform's source.
let internal current: Source =
    if OperatingSystem.IsMacOS() then Source.Native askMacOS
    elif OperatingSystem.IsLinux() then Source.AsGiven
    else Source.Listing
