/// A pid file is parsed in one place (`PidFile`), so every reader agrees on what it can
/// hold: only a positive integer names one process.
module FsHotWatch.Tests.PidFileTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 5000)>]
let ``a positive integer is a pid, surrounding whitespace ignored`` () =
    test <@ PidFile.parse "4242" = Some 4242 @>
    test <@ PidFile.parse "  4242\n" = Some 4242 @>

[<Fact(Timeout = 5000)>]
let ``zero, a negative number and text name no process`` () =
    test <@ PidFile.parse "0" = None @>
    test <@ PidFile.parse "-1" = None @>
    test <@ PidFile.parse "" = None @>
    test <@ PidFile.parse "daemon" = None @>
    test <@ PidFile.parse "12 34" = None @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the daemon pid file is read from .fshw under the root, and an absent one reads as none`` () =
    withTempDir "pidfile" (fun root ->
        test <@ PidFile.daemonPath root = Path.Combine(root, ".fshw", "daemon.pid") @>
        test <@ PidFile.tryReadDaemon root = None @>
        Directory.CreateDirectory(Path.Combine(root, ".fshw")) |> ignore
        File.WriteAllText(PidFile.daemonPath root, "not a pid")
        test <@ PidFile.tryReadDaemon root = None @>
        File.WriteAllText(PidFile.daemonPath root, "77\n")
        test <@ PidFile.tryReadDaemon root = Some 77 @>)
