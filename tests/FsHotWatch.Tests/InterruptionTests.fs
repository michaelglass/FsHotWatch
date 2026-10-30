module FsHotWatch.Tests.InterruptionTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

let private at = DateTime(2026, 10, 30, 22, 40, 20, DateTimeKind.Utc)

let private building =
    { Interruption.Record.At = at
      Interruption.Record.Pid = 4242
      Interruption.Record.InFlight = [ "build"; "scan" ] }

[<Fact>]
let ``a shutdown that interrupted work is recorded and read back`` () =
    withTempDir "interruption-roundtrip" (fun repoRoot ->
        Interruption.record repoRoot building

        test <@ Interruption.tryRead repoRoot = Some building @>)

[<Fact>]
let ``a daemon that owned no work leaves no record`` () =
    withTempDir "interruption-idle" (fun repoRoot ->
        Interruption.record repoRoot { building with InFlight = [] }

        test <@ not (File.Exists(Interruption.path repoRoot)) @>
        test <@ Interruption.tryRead repoRoot = None @>)

[<Fact>]
let ``an unreadable record reads as none`` () =
    withTempDir "interruption-unreadable" (fun repoRoot ->
        Directory.CreateDirectory(FsHwPaths.root repoRoot) |> ignore
        File.WriteAllText(Interruption.path repoRoot, "{ not json")

        test <@ Interruption.tryRead repoRoot = None @>)

[<Fact>]
let ``the description names the shutdown, the work and that its output may be incomplete`` () =
    let text = Interruption.describe building

    test <@ text.Contains "shut down" @>
    test <@ text.Contains "build, scan" @>
    test <@ text.Contains "4242" @>
    test <@ text.Contains "not a crash" @>
    test <@ text.Contains "incomplete" @>

[<Fact>]
let ``only a shutdown at or after the moment asked about is that moment's`` () =
    test <@ Interruption.since at (Some building) = Some building @>
    test <@ Interruption.since (at.AddSeconds -1.0) (Some building) = Some building @>
    test <@ Interruption.since (at.AddSeconds 1.0) (Some building) = None @>
    test <@ Interruption.since at None = None @>

[<Fact>]
let ``repair re-stamps the sources past any output the killed work wrote, then clears the record`` () =
    withTempDir "interruption-repair" (fun repoRoot ->
        let source = Path.Combine(repoRoot, "Lib.fs")
        File.WriteAllText(source, "module Lib\n")
        // The half-written output the killed build left, newer than the source.
        let output = Path.Combine(repoRoot, "Lib.dll")
        File.WriteAllText(output, "partial")
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours -1.0)
        File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddMinutes -1.0)
        Interruption.record repoRoot building

        let repaired = Interruption.repair repoRoot [ source ]

        test <@ repaired = Some building @>
        test <@ File.GetLastWriteTimeUtc source > File.GetLastWriteTimeUtc output @>
        test <@ File.ReadAllText source = "module Lib\n" @>
        test <@ Interruption.tryRead repoRoot = None @>)

[<Fact>]
let ``with no record owed, repair touches nothing`` () =
    withTempDir "interruption-no-repair" (fun repoRoot ->
        let source = Path.Combine(repoRoot, "Lib.fs")
        File.WriteAllText(source, "module Lib\n")
        let stamped = DateTime.UtcNow.AddHours -1.0
        File.SetLastWriteTimeUtc(source, stamped)

        test <@ Interruption.repair repoRoot [ source ] = None @>
        test <@ File.GetLastWriteTimeUtc source = stamped @>)

[<Fact>]
let ``a source that vanished does not stop the repair`` () =
    withTempDir "interruption-vanished" (fun repoRoot ->
        let source = Path.Combine(repoRoot, "Lib.fs")
        File.WriteAllText(source, "module Lib\n")
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours -1.0)
        let before = File.GetLastWriteTimeUtc source
        Interruption.record repoRoot building

        let repaired =
            Interruption.repair repoRoot [ Path.Combine(repoRoot, "Gone.fs"); source ]

        test <@ repaired = Some building @>
        test <@ File.GetLastWriteTimeUtc source > before @>
        test <@ Interruption.tryRead repoRoot = None @>)

[<Fact>]
let ``a record that cannot be written does not throw out of a stopping daemon`` () =
    withTempDir "interruption-unwritable" (fun repoRoot ->
        // `.fshw` is a file, so nothing can be written under it.
        File.WriteAllText(FsHwPaths.root repoRoot, "not a directory")

        Interruption.record repoRoot building

        test <@ Interruption.tryRead repoRoot = None @>)

[<Fact>]
let ``a root that has gone is not recreated to hold the record`` () =
    withTempDir "interruption-gone" (fun parent ->
        let repoRoot = Path.Combine(parent, "deleted")

        Interruption.record repoRoot building

        test <@ not (Directory.Exists repoRoot) @>)
