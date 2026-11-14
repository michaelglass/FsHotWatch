/// macOS getattrlist(2): one call that asks a volume for a chosen set of an entry's
/// attributes. The reply is a u32 total length, then each requested attribute in the
/// order of its bit, each starting on a 4-byte boundary.
module FsHotWatch.MacAttrList

open System.Runtime.InteropServices

[<Struct; StructLayout(LayoutKind.Sequential)>]
type internal AttrList =
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

/// The entry's name as stored: an `attrreference_t`.
[<Literal>]
let internal AttrCmnName = 0x00000001u

/// The last status change: a `timespec` (i64 seconds, i64 nanoseconds).
[<Literal>]
let internal AttrCmnChgTime = 0x00000800u

/// The entry's id on its volume: a u64.
[<Literal>]
let internal AttrCmnFileId = 0x02000000u

/// The entry itself, not what a symlink there points at.
[<Literal>]
let internal FsoptNofollow = 0x00000001u

[<Literal>]
let internal ENOENT = 2

[<Literal>]
let internal ENOTDIR = 20

/// The common attributes `commonAttr` of `path` into `reply`: `Ok ()`, or `Error errno`.
let internal ask (path: string) (commonAttr: uint32) (options: uint32) (reply: byte array) : Result<unit, int> =
    let mutable request = AttrList()
    request.BitmapCount <- AttrBitMapCount
    request.CommonAttr <- commonAttr

    if getattrlist (path, &request, reply, unativeint reply.Length, options) = 0 then
        Ok()
    else
        Error(Marshal.GetLastPInvokeError())
