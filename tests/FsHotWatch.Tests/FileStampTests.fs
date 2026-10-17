module FsHotWatch.Tests.FileStampTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.FileStamp
open FsHotWatch.Tests.TestHelpers

let private expected =
    { FileId = 0x0102030405060708UL
      ChangeSeconds = 1_760_000_000L
      ChangeNanoseconds = 123_456_789L }

/// A statx(2) that succeeds and fills `stx_mask` with `mask` and the stamp fields with
/// `expected`, at their offsets in `struct statx`.
let private statxFilling (mask: uint32) (_: string) (buf: byte array) =
    BitConverter.GetBytes(mask).CopyTo(buf, 0)
    BitConverter.GetBytes(expected.FileId).CopyTo(buf, 32)
    BitConverter.GetBytes(expected.ChangeSeconds).CopyTo(buf, 96)
    BitConverter.GetBytes(uint32 expected.ChangeNanoseconds).CopyTo(buf, 104)
    0

[<Fact(Timeout = 15000)>]
let ``askLinuxWith reads the file id and ctime when statx filled both`` () =
    test <@ askLinuxWith (statxFilling (StatxCtime ||| StatxIno)) "/f" = ValueSome expected @>

[<Fact(Timeout = 15000)>]
let ``askLinuxWith has no stamp when the filesystem left ctime or the inode out`` () =
    test <@ askLinuxWith (statxFilling StatxCtime) "/f" = ValueNone @>
    test <@ askLinuxWith (statxFilling StatxIno) "/f" = ValueNone @>

[<Fact(Timeout = 15000)>]
let ``askLinuxWith has no stamp when statx fails`` () =
    test <@ askLinuxWith (fun _ _ -> -1) "/missing" = ValueNone @>

/// A getattrlist(2) that succeeds and writes `expected` after the u32 reply length.
let private attrListFilling (_: string) (reply: byte array) : Result<unit, int> =
    BitConverter.GetBytes(expected.ChangeSeconds).CopyTo(reply, 4)
    BitConverter.GetBytes(expected.ChangeNanoseconds).CopyTo(reply, 12)
    BitConverter.GetBytes(expected.FileId).CopyTo(reply, 20)
    Ok()

[<Fact(Timeout = 15000)>]
let ``askMacOSWith reads the ctime and file id from the reply`` () =
    test <@ askMacOSWith attrListFilling "/f" = ValueSome expected @>

[<Fact(Timeout = 15000)>]
let ``askMacOSWith has no stamp when getattrlist fails`` () =
    test <@ askMacOSWith (fun _ _ -> Error 2) "/missing" = ValueNone @>

[<Fact(Timeout = 15000)>]
let ``orNone reads a call that throws as no stamp`` () =
    let throws (_: string) : Stamp voption =
        raise (EntryPointNotFoundException "statx")

    test <@ orNone throws "/f" = ValueNone @>
    test <@ orNone (fun _ -> ValueSome expected) "/f" = ValueSome expected @>

/// A reader that stamps every path with `expected` shifted by `id`, so a test can tell
/// which reader `selectReader` picked.
let private stampingAs (id: uint64) (_: string) : Stamp voption = ValueSome { expected with FileId = id }

let private throwing (_: string) : Stamp voption =
    raise (EntryPointNotFoundException "native")

[<Fact(Timeout = 15000)>]
let ``selectReader reads with the macOS reader on macOS`` () =
    test <@ selectReader true false (stampingAs 1UL) (stampingAs 2UL) "/f" = stampingAs 1UL "/f" @>

[<Fact(Timeout = 15000)>]
let ``selectReader reads with the Linux reader on Linux`` () =
    test <@ selectReader false true (stampingAs 1UL) (stampingAs 2UL) "/f" = stampingAs 2UL "/f" @>

[<Fact(Timeout = 15000)>]
let ``selectReader has no stamp on any other platform`` () =
    test <@ selectReader false false (stampingAs 1UL) (stampingAs 2UL) "/f" = ValueNone @>

[<Fact(Timeout = 15000)>]
let ``selectReader reads a platform reader that throws as no stamp`` () =
    test <@ selectReader true false throwing (stampingAs 2UL) "/f" = ValueNone @>
    test <@ selectReader false true (stampingAs 1UL) throwing "/f" = ValueNone @>

[<Fact(Timeout = 15000)>]
let ``read stamps an existing file with its last status change and none for a missing one`` () =
    withTempDir "filestamp" (fun dir ->
        let path = Path.Combine(dir, "A.fs")
        let before = DateTime.UtcNow.AddSeconds -5.0
        File.WriteAllText(path, "let x = 1")

        match read path with
        | ValueSome stamp ->
            let changedAt = stamp.ChangedAt
            test <@ changedAt > before && changedAt < DateTime.UtcNow.AddSeconds 5.0 @>
        | ValueNone -> failwith $"no stamp for an existing file on %s{Environment.OSVersion.Platform.ToString()}"

        test <@ read (Path.Combine(dir, "missing")) = ValueNone @>)
