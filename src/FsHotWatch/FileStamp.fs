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

/// The stamp of what `path` names, following a symlink. The reply is a u32 length, a
/// `timespec`, then the u64 file id.
let internal askMacOS (path: string) : Stamp voption =
    let reply = Array.zeroCreate<byte> 32

    match MacAttrList.ask path (MacAttrList.AttrCmnChgTime ||| MacAttrList.AttrCmnFileId) 0u reply with
    | Ok() ->
        ValueSome
            { ChangeSeconds = BitConverter.ToInt64(reply, 4)
              ChangeNanoseconds = BitConverter.ToInt64(reply, 12)
              FileId = BitConverter.ToUInt64(reply, 20) }
    | Error _ -> ValueNone

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
let private StatxCtime = 0x00000080u

[<Literal>]
let private StatxIno = 0x00000100u

[<Literal>]
let private Wanted = StatxCtime ||| StatxIno

/// The stamp of what `path` names, following a symlink. `stx_ino` is at byte 32 and
/// `stx_ctime` (i64 seconds, u32 nanoseconds) at byte 96 of the 256-byte `struct statx`;
/// `stx_mask`, at byte 0, says which fields the filesystem filled in.
let internal askLinux (path: string) : Stamp voption =
    let buf = Array.zeroCreate<byte> 256

    if statx (AtFdCwd, path, 0, Wanted, buf) <> 0 then
        ValueNone
    elif (BitConverter.ToUInt32(buf, 0) &&& Wanted) <> Wanted then
        ValueNone
    else
        ValueSome
            { FileId = BitConverter.ToUInt64(buf, 32)
              ChangeSeconds = BitConverter.ToInt64(buf, 96)
              ChangeNanoseconds = int64 (BitConverter.ToUInt32(buf, 104)) }

/// This platform's stamp of `path`, or `ValueNone` where it cannot be read: a missing
/// file, an unsupported platform or filesystem, or a failed call.
let read: string -> Stamp voption =
    if OperatingSystem.IsMacOS() then
        fun path ->
            try
                askMacOS path
            with _ ->
                ValueNone
    elif OperatingSystem.IsLinux() then
        fun path ->
            try
                askLinux path
            with _ ->
                ValueNone
    else
        fun _ -> ValueNone
