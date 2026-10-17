/// Whether a file's bytes may have changed since an earlier look, without reading them.
///
/// The last-write time and length are not enough: `rsync -a`, `tar -x`, `cp -p` and
/// `touch -r` put back an old last-write time, and a same-length replacement keeps the
/// length. The stamp is the file's id on its volume and its status-change time
/// (`ctime`). No call sets `ctime` to a chosen value: writing the file, setting its
/// times and renaming it all move it to the current time, and a file renamed over the
/// old one has its own id.
module FsHotWatch.FileStamp

open System
open System.Runtime.InteropServices

/// A file's id on its volume and its last status change.
[<Struct>]
type Stamp =
    { FileId: uint64
      ChangeSeconds: int64
      ChangeNanoseconds: int64 }

    /// The status change, as a UTC time.
    member stamp.ChangedAt =
        DateTime.UnixEpoch.AddSeconds(float stamp.ChangeSeconds).AddTicks(stamp.ChangeNanoseconds / 100L)

// ─── macOS: getattrlist(2), asked for the status-change time and the file id ───

/// The stamp in a getattrlist reply that `fill` wrote for `path`: a u32 length, a
/// `timespec`, then the u64 file id. `fill` asks for exactly those attributes.
let internal askMacOSWith (fill: string -> byte array -> Result<unit, int>) (path: string) : Stamp voption =
    let reply = Array.zeroCreate<byte> 32

    match fill path reply with
    | Ok() ->
        ValueSome
            { ChangeSeconds = BitConverter.ToInt64(reply, 4)
              ChangeNanoseconds = BitConverter.ToInt64(reply, 12)
              FileId = BitConverter.ToUInt64(reply, 20) }
    | Error _ -> ValueNone

/// The stamp of what `path` names, following a symlink.
let internal askMacOS (path: string) : Stamp voption =
    askMacOSWith
        (fun target reply -> MacAttrList.ask target (MacAttrList.AttrCmnChgTime ||| MacAttrList.AttrCmnFileId) 0u reply)
        path

// ─── Linux: statx(2), whose `struct statx` has one layout on every architecture ───

[<DllImport("libc", SetLastError = true)>]
extern int private statx(
    int dirfd,
    [<MarshalAs(UnmanagedType.LPUTF8Str)>] string path,
    int flags,
    uint32 mask,
    byte[] buf
)

[<Literal>]
let private AtFdCwd = -100

[<Literal>]
let internal StatxCtime = 0x00000080u

[<Literal>]
let internal StatxIno = 0x00000100u

[<Literal>]
let private Wanted = StatxCtime ||| StatxIno

/// The stamp in a `struct statx` that `fill` wrote for `path`, where `fill` returns the
/// call's result. `stx_ino` is at byte 32 and `stx_ctime` (i64 seconds, u32
/// nanoseconds) at byte 96 of the 256-byte struct; `stx_mask`, at byte 0, says which
/// fields the filesystem filled in.
let internal askLinuxWith (fill: string -> byte array -> int) (path: string) : Stamp voption =
    let buf = Array.zeroCreate<byte> 256

    if fill path buf <> 0 then
        ValueNone
    elif (BitConverter.ToUInt32(buf, 0) &&& Wanted) <> Wanted then
        ValueNone
    else
        ValueSome
            { FileId = BitConverter.ToUInt64(buf, 32)
              ChangeSeconds = BitConverter.ToInt64(buf, 96)
              ChangeNanoseconds = int64 (BitConverter.ToUInt32(buf, 104)) }

/// The stamp of what `path` names, following a symlink.
let internal askLinux (path: string) : Stamp voption =
    askLinuxWith (fun target buf -> statx (AtFdCwd, target, 0, Wanted, buf)) path

/// `ask`, with a call that throws (a missing native entry point, a marshalling
/// failure) read as no stamp.
let internal orNone (ask: string -> Stamp voption) (path: string) : Stamp voption =
    try
        ask path
    with _ ->
        ValueNone

/// The reader for a platform that is macOS (`isMacOS`), Linux (`isLinux`) or neither:
/// `mac` or `linux` with a throw read as no stamp, or no stamp at all elsewhere.
let internal selectReader
    (isMacOS: bool)
    (isLinux: bool)
    (mac: string -> Stamp voption)
    (linux: string -> Stamp voption)
    : string -> Stamp voption =
    if isMacOS then orNone mac
    elif isLinux then orNone linux
    else fun _ -> ValueNone

/// This platform's stamp of `path`, or `ValueNone` where it cannot be read: a missing
/// file, an unsupported platform or filesystem, or a failed call.
let read: string -> Stamp voption =
    selectReader (OperatingSystem.IsMacOS()) (OperatingSystem.IsLinux()) askMacOS askLinux
