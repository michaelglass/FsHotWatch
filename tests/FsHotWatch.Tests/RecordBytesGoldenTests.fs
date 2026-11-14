/// The exact bytes the outcome store files (AUTOMATION-1695, L1). A record's file name is the
/// digest of its payload, and the audit and every other checkout read these files, so moving the
/// codec into FsHotWatch.Core must not change one byte. Characterised against the store BEFORE
/// the move; the same goldens must hold after it.
module FsHotWatch.Tests.RecordBytesGoldenTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.OutcomeStore
open FsHotWatch.Tests.TestHelpers

/// A record's file name is the SHA-256 of its payload, so each name pins the payload byte for byte.
let private GoldenTestRunName =
    "8114a03518e6047168a1d210888c66765c1c2f7400a09f2f403cb177f8b6cc74.json"

/// The SHA-256 of the whole file (envelope: schema, checksum, payload).
let private GoldenTestRunDigest =
    "73733bfdaebf4affd1ed4b9b4cf611b421b5f06d62c3bc636014d68d9fcbb894"

let private GoldenNonTestName =
    "f8a2f9905379a8a75269e33686a1bac9520973d515c2ea31d016b00ccd6d08bf.json"

/// The SHA-256 of the whole file (envelope: schema, checksum, payload).
let private GoldenNonTestDigest =
    "12ceb09fb1b3949a8928c520e7d49acd0132307e63ff71d17314b44b930a4fd5"

let private GoldenInRunName =
    "82c1689bd0c70286b09fb87d775165a2dd3d2d9aea65e4d946ad6d959504535a.json"

/// The SHA-256 of the whole file (envelope: schema, checksum, payload).
let private GoldenInRunDigest =
    "a3707caaf3892923a5621e947f1d499f43117908859b7f10870f3d9bbb13dbf3"

let private at = DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.FromHours 1.0)

/// The one file `record` (with `outputs`) is filed as: its name and its text.
let private filed (record: Record) (outputs: string option) =
    withTempDir "record-bytes" (fun dir ->
        let store =
            Store(
                { Root = dir
                  Ttl = defaultTtl
                  MaxBytes = defaultMaxBytes
                  Log = ignore }
            )

        test <@ store.RecordWithOutputs(record, outputs) @>

        let file =
            Directory.GetFiles(store.DirectoryOf record.Key, "*.json") |> Array.exactlyOne

        Path.GetFileName file, File.ReadAllText file)

[<Fact>]
let ``a test run's record is filed byte for byte as before`` () =
    let record =
        { Key = ActionKey.create "test" [ "tree", "t1"; "project", "A.Tests"; "scope", "full" ]
          Subject = "A.Tests"
          Outcome = Outcome.Fail
          Counts =
            Some
                { Total = 3
                  Passed = 2
                  Failed = 1
                  Skipped = 0 }
          DurationMs = Some 1200L
          Producer =
            { Binary = "fshw-1"
              Worktree = "/w/a+b é"
              RunId = Some "run-1" }
          RecordedAt = at }

    let name, text = filed record (Some """{"failed":["A.Tests.broken"]}""")
    test <@ name = GoldenTestRunName @>
    test <@ ContentHash.ofText text = GoldenTestRunDigest @>

[<Fact>]
let ``a record that ran no tests is filed byte for byte as before`` () =
    let record =
        Record.ofAction
            (ActionKey.create "plugin" [ "tree", "t1"; "producer", "fshw-1"; "plugin", "lint" ])
            "lint"
            Outcome.Pass
            "fshw-1"
            "/w/a"
            at

    let name, text = filed record None
    test <@ name = GoldenNonTestName @>
    test <@ ContentHash.ofText text = GoldenNonTestDigest @>

[<Fact>]
let ``a baseline earned in a run is filed byte for byte as before`` () =
    let record =
        { Record.ofAction
              (ActionKey.create "test-baseline" [ "project", "A.Tests" ])
              "A.Tests"
              Outcome.Pass
              "fshw-1"
              "/w/a"
              at with
            Producer =
                { Binary = "fshw-1"
                  Worktree = "/w/a"
                  RunId = Some "run-7" } }

    let name, text = filed record None
    test <@ name = GoldenInRunName @>
    test <@ ContentHash.ofText text = GoldenInRunDigest @>
