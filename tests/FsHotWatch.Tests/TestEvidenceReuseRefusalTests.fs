/// ADR-059 §34 (AUTOMATION-1615): an ordinary evidence reuse stands in for a project's run
/// only while the outcome store holds no NEWER fail at the same K_P. The evidence store holds
/// greens only; the fails a flaky run or another checkout filed live in the outcome store,
/// under the same `testActionKey K_P "full"` (or `"impact"`). `reuseRefusal` is the one place
/// a reuse asks it. Every time is a value the test chose.
module FsHotWatch.Tests.TestEvidenceReuseRefusalTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.TestPrune.TestEvidenceRun
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)

let private keyOf (closure: string) =
    TestEvidenceKey.ofLabels [ "toolchain", "t"; "closure", closure; "scope", "verdict-tree" ]

let private recordAt
    (key: TestEvidenceKey.Key)
    (scope: string)
    (outcome: OutcomeStore.Outcome)
    (run: string)
    (at: DateTimeOffset)
    : OutcomeStore.Record =
    { Key = testActionKey key scope
      Subject = "TX"
      Outcome = outcome
      Counts = None
      DurationMs = None
      Producer =
        { Binary = "fshw-1"
          Worktree = $"/w/%s{run}"
          RunId = Some run }
      RecordedAt = at }

let private withOutcomes (body: string -> OutcomeStore.Store -> unit) =
    withTempDir "reuse-refusal" (fun dir ->
        let root = Path.Combine(dir, "outcomes")

        body
            root
            (OutcomeStore.Store(
                { Root = root
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )))

let private refused (outcomes: OutcomeStore.Store) key earnedAt =
    reuseRefusal (Some outcomes) key earnedAt

[<Fact>]
let ``a full fail newer than the green at the same K_P refuses the reuse`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Fail "run-b" (t0.AddMinutes 5.0))
        |> ignore

        let why = refused outcomes key t0
        test <@ why.IsSome @>
        test <@ why.Value.Contains "run-b" @>)

[<Fact>]
let ``a fail and the pass at the same instant is the fail: the reuse is refused`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Fail "run-b" t0)
        |> ignore

        test <@ (refused outcomes key t0).IsSome @>)

[<Fact>]
let ``fail then pass at the same K_P still reuses (§25)`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Fail "run-a" t0)
        |> ignore

        let later = t0.AddMinutes 5.0

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-b" later)
        |> ignore

        test <@ refused outcomes key later = None @>)

[<Fact>]
let ``a full fail a later run cleared (§25 clearance) still reuses`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"
        let full = testActionKey key "full"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        let failedAt = t0.AddMinutes 1.0

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Fail "run-b" failedAt)
        |> ignore

        test <@ (refused outcomes key t0).IsSome @>

        let clearance =
            { recordAt key "impact" OutcomeStore.Outcome.Pass "run-c" (failedAt.AddMinutes 1.0) with
                Key = TestFailureClearance.keyOf full.Value "run-b" }

        outcomes.Record clearance |> ignore
        test <@ refused outcomes key t0 = None @>)

[<Fact>]
let ``a fail under a different K_P does not bear on this key's reuse`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"
        let other = keyOf "c2"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        outcomes.Record(recordAt other "full" OutcomeStore.Outcome.Fail "run-b" (t0.AddMinutes 5.0))
        |> ignore

        outcomes.Record(recordAt other "impact" OutcomeStore.Outcome.Fail "run-c" (t0.AddMinutes 6.0))
        |> ignore

        // Positive control: the fail is really on file, and it is the answer at ITS key.
        test <@ outcomes.Lookup(testActionKey other "full") |> Option.map _.Outcome = Some OutcomeStore.Outcome.Fail @>

        test <@ (refused outcomes other t0).IsSome @>
        test <@ refused outcomes key t0 = None @>)

[<Fact>]
let ``no record at the key, or no store wired, refuses nothing`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"
        test <@ refused outcomes key t0 = None @>
        test <@ reuseRefusal None key t0 = None @>)

let private recordFiles (root: string) =
    let records = Path.Combine(root, "records")

    if Directory.Exists records then
        allFilesUnder "*.json" records
    else
        []

let private unreadable (file: string) (body: unit -> unit) =
    File.SetUnixFileMode(file, UnixFileMode.None)

    try
        // The control must be real: a reader that can still read the file (root) proves nothing.
        let readable =
            try
                File.ReadAllText file |> ignore
                true
            with :? UnauthorizedAccessException ->
                false

        test <@ not readable @>
        body ()
    finally
        if File.Exists file then
            File.SetUnixFileMode(file, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

[<Fact>]
let ``an unreadable record at the full key is a miss for reuse, never read as no fail (1568)`` () =
    withOutcomes (fun root outcomes ->
        let key = keyOf "c1"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        let passFile = recordFiles root |> List.exactlyOne
        test <@ refused outcomes key t0 = None @>

        unreadable passFile (fun () ->
            // Positive control: today's lookup reads this key as having no record at all.
            test <@ outcomes.Lookup(testActionKey key "full") = None @>
            let why = refused outcomes key t0
            test <@ why.IsSome @>
            test <@ why.Value.Contains "could not be read" @>)

        test <@ refused outcomes key t0 = None @>)

[<Fact>]
let ``an unreadable record at the impact key is a miss for reuse too`` () =
    withOutcomes (fun root outcomes ->
        let key = keyOf "c1"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        let passFile = recordFiles root |> List.exactlyOne

        outcomes.Record(recordAt key "impact" OutcomeStore.Outcome.Pass "run-b" (t0.AddMinutes 1.0))
        |> ignore

        let impactFile = recordFiles root |> List.except [ passFile ] |> List.exactlyOne
        unreadable impactFile (fun () -> test <@ (refused outcomes key t0).IsSome @>))

// The filtered-scope case. An impact run of a project at K_P runs a subset of the very tests
// the full green ran, over the same bytes. A fail there contradicts the full green exactly as
// a full fail does, so one filed AFTER the green refuses the reuse. A later impact pass does
// not clear it (it may have run a different subset); a later full pass, or a §25 clearance of
// a newer full fail, is what clears it. An impact fail older than the full green does not
// bear: the full run re-ran every test after it.

[<Fact>]
let ``an impact fail newer than the full green at the same K_P refuses the reuse`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        outcomes.Record(recordAt key "impact" OutcomeStore.Outcome.Fail "run-b" (t0.AddMinutes 5.0))
        |> ignore

        let why = refused outcomes key t0
        test <@ why.IsSome @>
        test <@ why.Value.Contains "run-b" @>)

[<Fact>]
let ``a later impact pass does not clear a newer impact fail; a later full pass does`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        outcomes.Record(recordAt key "impact" OutcomeStore.Outcome.Fail "run-b" (t0.AddMinutes 1.0))
        |> ignore

        outcomes.Record(recordAt key "impact" OutcomeStore.Outcome.Pass "run-c" (t0.AddMinutes 2.0))
        |> ignore

        test <@ (refused outcomes key t0).IsSome @>

        let later = t0.AddMinutes 3.0

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-d" later)
        |> ignore

        test <@ refused outcomes key later = None @>)

[<Fact>]
let ``an impact fail older than the full green does not bear on it`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"

        outcomes.Record(recordAt key "impact" OutcomeStore.Outcome.Fail "run-a" t0)
        |> ignore

        let later = t0.AddMinutes 5.0

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-b" later)
        |> ignore

        test <@ refused outcomes key later = None @>)

[<Fact>]
let ``with no full record on file, an impact fail is judged against the green's own earned time`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"

        outcomes.Record(recordAt key "impact" OutcomeStore.Outcome.Fail "run-a" t0)
        |> ignore

        test <@ refused outcomes key (t0.AddMinutes 5.0) = None @>
        test <@ (refused outcomes key (t0.AddMinutes -5.0)).IsSome @>)

[<Fact>]
let ``a newer full fail no run filed has no clearance to read and refuses the reuse`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        let unnamed =
            { recordAt key "full" OutcomeStore.Outcome.Fail "run-b" (t0.AddMinutes 1.0) with
                Producer =
                    { Binary = "fshw-1"
                      Worktree = "/w/b"
                      RunId = None } }

        outcomes.Record unnamed |> ignore
        let why = refused outcomes key t0
        test <@ why.IsSome @>
        test <@ why.Value.Contains "an unnamed run" @>)

[<Fact>]
let ``a clearance filed no later than the fail does not clear it`` () =
    withOutcomes (fun _ outcomes ->
        let key = keyOf "c1"
        let full = testActionKey key "full"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        let failedAt = t0.AddMinutes 1.0

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Fail "run-b" failedAt)
        |> ignore

        let early =
            { recordAt key "impact" OutcomeStore.Outcome.Pass "run-c" failedAt with
                Key = TestFailureClearance.keyOf full.Value "run-b" }

        outcomes.Record early |> ignore
        test <@ (refused outcomes key t0).IsSome @>)

[<Fact>]
let ``an unreadable clearance record is a miss for reuse, not a clearance and not no clearance`` () =
    withOutcomes (fun root outcomes ->
        let key = keyOf "c1"
        let full = testActionKey key "full"

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Pass "run-a" t0)
        |> ignore

        let failedAt = t0.AddMinutes 1.0

        outcomes.Record(recordAt key "full" OutcomeStore.Outcome.Fail "run-b" failedAt)
        |> ignore

        let before = recordFiles root

        let clearance =
            { recordAt key "impact" OutcomeStore.Outcome.Pass "run-c" (failedAt.AddMinutes 1.0) with
                Key = TestFailureClearance.keyOf full.Value "run-b" }

        outcomes.Record clearance |> ignore
        let clearanceFile = recordFiles root |> List.except before |> List.exactlyOne
        test <@ refused outcomes key t0 = None @>

        unreadable clearanceFile (fun () ->
            let why = refused outcomes key t0
            test <@ why.IsSome @>
            test <@ why.Value.Contains "clearance" @>))
