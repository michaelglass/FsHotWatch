/// What the kernel says about a file, read without its bytes, in one call: what git's index
/// compares to decide a file is unchanged (E5, AUTOMATION-1696). `ContentHashCache` trusts a
/// stored hash only while all of it is as it was.
///
/// The size and mtime alone are not enough: `touch -r`, `rsync -a` and `cp -p` restore an old
/// mtime onto new bytes of the same length. The ctime cannot be set from user space, so any
/// write, chmod or rename moves it; a file renamed over another has its own inode.
module FsHotWatch.FileIdentity

open System
open System.Runtime.InteropServices

/// A file's length, modification and status-change times (nanoseconds since the Unix epoch),
/// and its inode and device.
[<Struct>]
type Identity =
    { Size: int64
      ModifiedNs: int64
      ChangedNs: int64
      Inode: uint64
      Device: uint64 }

    /// The later of the two times: the last moment the file is known to have changed.
    member identity.LatestChangeNs = max identity.ModifiedNs identity.ChangedNs

let private nanoseconds (seconds: int64) (nanos: int64) = seconds * 1_000_000_000L + nanos

// ─── macOS: getattrlist(2) ───

/// The identity in a getattrlist reply for the device, mtime, ctime and file id (common
/// attributes, in the order of their bits) and the data length (a file attribute, after
/// them): a u32 length, the i32 device, two `timespec`s, the u64 id, the i64 length.
let internal parseMacOS (reply: byte array) : Identity =
    { Device = uint64 (BitConverter.ToUInt32(reply, 4))
      ModifiedNs = nanoseconds (BitConverter.ToInt64(reply, 8)) (BitConverter.ToInt64(reply, 16))
      ChangedNs = nanoseconds (BitConverter.ToInt64(reply, 24)) (BitConverter.ToInt64(reply, 32))
      Inode = BitConverter.ToUInt64(reply, 40)
      Size = BitConverter.ToInt64(reply, 48) }

let private askMacOS (path: string) : Identity voption =
    let reply = Array.zeroCreate<byte> 64

    match
        MacAttrList.askWith
            path
            (MacAttrList.AttrCmnDevId
             ||| MacAttrList.AttrCmnModTime
             ||| MacAttrList.AttrCmnChgTime
             ||| MacAttrList.AttrCmnFileId)
            MacAttrList.AttrFileDataLength
            0u
            reply
    with
    | Ok() -> ValueSome(parseMacOS reply)
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

/// `STATX_MTIME | STATX_CTIME | STATX_INO | STATX_SIZE`.
[<Literal>]
let private Wanted = 0x000003C0u

/// The identity in a `struct statx`, or none when `stx_mask` says the filesystem did not fill
/// in a field asked for. `stx_ino` and `stx_size` are at bytes 32 and 40, `stx_ctime` and
/// `stx_mtime` (i64 seconds, u32 nanoseconds) at 96 and 112, `stx_dev_major/minor` at 136.
let internal parseLinux (buf: byte array) : Identity voption =
    if (BitConverter.ToUInt32(buf, 0) &&& Wanted) <> Wanted then
        ValueNone
    else
        ValueSome
            { Inode = BitConverter.ToUInt64(buf, 32)
              Size = BitConverter.ToInt64(buf, 40)
              ChangedNs = nanoseconds (BitConverter.ToInt64(buf, 96)) (int64 (BitConverter.ToUInt32(buf, 104)))
              ModifiedNs = nanoseconds (BitConverter.ToInt64(buf, 112)) (int64 (BitConverter.ToUInt32(buf, 120)))
              Device =
                (uint64 (BitConverter.ToUInt32(buf, 136)) <<< 32)
                ||| uint64 (BitConverter.ToUInt32(buf, 140)) }

let private askLinux (path: string) : Identity voption =
    let buf = Array.zeroCreate<byte> 256

    if statx (AtFdCwd, path, 0, Wanted, buf) = 0 then
        parseLinux buf
    else
        ValueNone

/// `ask`, with a native call that cannot be made (no libc, no such entry point, a marshalling
/// failure) read as no identity: the cache then hashes the file, never errs.
let internal orNone (ask: string -> Identity voption) (path: string) : Identity voption =
    try
        ask path
    with
    | :? DllNotFoundException
    | :? EntryPointNotFoundException
    | :? MarshalDirectiveException -> ValueNone

/// The reader for a platform that is macOS (`isMacOS`), Linux (`isLinux`) or neither: `mac` or
/// `linux` with an unmakeable call read as no identity, or no identity at all elsewhere.
let internal selectReader
    (isMacOS: bool)
    (isLinux: bool)
    (mac: string -> Identity voption)
    (linux: string -> Identity voption)
    : string -> Identity voption =
    if isMacOS then orNone mac
    elif isLinux then orNone linux
    else fun _ -> ValueNone

/// The identity of what `path` names, following a symlink as a read of it does, or none: a
/// missing file, an unsupported platform or filesystem, or a failed call. None is never an
/// error; the cache reads the file instead.
let read: string -> Identity voption =
    selectReader (OperatingSystem.IsMacOS()) (OperatingSystem.IsLinux()) askMacOS askLinux
