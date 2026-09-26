/// CPU time and peak memory of this process's finished child processes, from
/// `getrusage(RUSAGE_CHILDREN)`. Overhead is measured as CPU (user + system), never wall
/// time: wall time is set by a suite's fixed timeouts, not by probes.
module TestPrune.Trace.Rusage

open System
open System.Runtime.InteropServices

[<DllImport("libc", SetLastError = true)>]
extern int private getrusage(int who, byte[] usage)

/// `RUSAGE_CHILDREN`: terminated descendants that have been waited for.
[<Literal>]
let private RusageChildren = -1

/// Decode a `struct rusage` (macOS and Linux, 64-bit): `ru_utime` is an int64 of seconds
/// then microseconds in the low 32 bits of the next 8 bytes, `ru_stime` likewise, then
/// `ru_maxrss` as an int64, in bytes on macOS and KiB on Linux.
let internal decode (isMacOS: bool) (buf: byte[]) : struct (TimeSpan * int64) =
    let tv (off: int) =
        TimeSpan.FromSeconds(float (BitConverter.ToInt64(buf, off)))
        + TimeSpan.FromMicroseconds(float (BitConverter.ToInt32(buf, off + 8)))

    let rss = BitConverter.ToInt64(buf, 32)
    struct (tv 0 + tv 16, (if isMacOS then rss else rss * 1024L))

/// `children` over a given `getrusage(RUSAGE_CHILDREN, buf)`; raises when it fails.
let internal childrenWith (call: byte[] -> int) (isMacOS: bool) : struct (TimeSpan * int64) =
    let buf = Array.zeroCreate<byte> 256

    if call buf <> 0 then
        invalidOp $"getrusage failed (errno %d{Marshal.GetLastPInvokeError()})"

    decode isMacOS buf

/// User + system CPU of every terminated, waited-for descendant so far, and the largest
/// peak RSS (bytes) among them. Both only grow: take the CPU before and after a launch
/// for that launch's CPU. The RSS is a maximum over every child so far, not per launch.
let children () : struct (TimeSpan * int64) =
    childrenWith (fun buf -> getrusage (RusageChildren, buf)) (OperatingSystem.IsMacOS())
