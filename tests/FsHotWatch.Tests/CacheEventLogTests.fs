module FsHotWatch.Tests.CacheEventLogTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.CacheEventLog
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 2000)>]
let ``formatMiss produces tab-separated line ending in newline`` () =
    let timestamp = DateTime(2026, 4, 26, 12, 0, 0, DateTimeKind.Utc)
    let line = formatMiss timestamp "lint" "/repo" "/repo/Foo.fs"

    test <@ line.EndsWith("\n") @>

    let parts = line.TrimEnd('\n').Split('\t')
    test <@ parts.Length = 4 @>
    test <@ parts.[1] = "lint" @>
    test <@ parts.[2] = "/repo" @>
    test <@ parts.[3] = "/repo/Foo.fs" @>

[<Fact(Timeout = 2000)>]
let ``formatMiss includes triggerFile as last field`` () =
    let t = DateTime(2026, 4, 26, 12, 0, 0, DateTimeKind.Utc)
    let line = formatMiss t "lint" "/repo" "/repo/Foo.fs"
    let parts = line.TrimEnd('\n').Split('\t')
    test <@ parts.[3] = "/repo/Foo.fs" @>

[<Fact(Timeout = 2000)>]
let ``formatMiss allows empty triggerFile for non-file events`` () =
    let t = DateTime(2026, 4, 26, 12, 0, 0, DateTimeKind.Utc)
    let line = formatMiss t "build" "/repo" ""
    let parts = line.TrimEnd('\n').Split('\t')
    test <@ parts.[3] = "" @>

[<Fact(Timeout = 2000)>]
let ``formatMiss timestamp is ISO 8601 round-trip`` () =
    let t = DateTime(2026, 4, 26, 12, 30, 45, DateTimeKind.Utc)
    let line = formatMiss t "p" "r" ""
    let timestampField = line.Split('\t').[0]

    let roundTripped =
        DateTime.Parse(timestampField, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime()

    test <@ roundTripped = t @>

[<Fact(Timeout = 5000)>]
let ``appendTo creates file and writes line`` () =
    withTempDir "cacheeventlog-create" (fun tmpDir ->
        let path = Path.Combine(tmpDir, "events.log")
        let line = formatMiss DateTime.UtcNow "lint" "/repo" "/repo/Foo.fs"

        appendTo path line

        test <@ File.Exists(path) @>
        let content = File.ReadAllText(path)
        test <@ content = line @>)

[<Fact(Timeout = 5000)>]
let ``appendTo appends across multiple calls`` () =
    withTempDir "cacheeventlog-append" (fun tmpDir ->
        let path = Path.Combine(tmpDir, "events.log")
        let t = DateTime.UtcNow

        appendTo path (formatMiss t "lint" "/repo" "/repo/A.fs")
        appendTo path (formatMiss t "build" "/repo" "/repo/B.fs")
        appendTo path (formatMiss t "test-prune" "/repo" "")

        let lines = File.ReadAllLines(path)
        test <@ lines.Length = 3 @>
        test <@ lines.[0].Contains("\tlint\t") @>
        test <@ lines.[1].Contains("\tbuild\t") @>
        test <@ lines.[2].Contains("\ttest-prune\t") @>)

[<Fact(Timeout = 5000)>]
let ``appendTo silently swallows failures (does not throw)`` () =
    // Path with embedded null byte → File.AppendAllText throws ArgumentException.
    // The function must catch and return normally so telemetry can't crash the daemon.
    let invalidPath = " -not-a-real-path"
    appendTo invalidPath "anything\n"
// No assertion needed — reaching here without throwing is the test.
