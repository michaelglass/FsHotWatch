/// The gate logs what it decides, so it runs with the tests that read the global log.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.VcsQuietTests

open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.VcsPosition
open FsHotWatch.VcsQuiet

let private at (commit: char) =
    Position.GitBranch("refs/heads/main", GitRef.Points(System.String(commit, 40)))

let private jjAt (seed: char) (kind: JjOperationKind) =
    Position.JjWorkingCopy(System.String(seed, 128), kind)

/// A probe whose position the test moves.
let private movable (start: Result<Position, string>) =
    let current = ref start
    Probe.Tracked(fun () -> current.Value), current

let private edit name =
    SourceChanged [ $"/repo/src/{name}.fs" ]

let private isWaiting gate =
    match gate with
    | Gate.Waiting _ -> true
    | Gate.Ungated
    | Gate.Unanchored _
    | Gate.Live _ -> false

[<Fact>]
let ``an untracked checkout checks every batch`` () =
    let gate = start (Probe.Untracked "a plain directory")
    let gate, admission = admit gate [ edit "A" ]

    test <@ admission = Admission.Check [ edit "A" ] @>
    test <@ not (isWaiting gate) @>
    test <@ Notice.ofGate gate = Notice.Checking @>

[<Fact>]
let ``an edit at a still position checks at once`` () =
    let probe, _ = movable (Ok(at 'a'))
    let _, admission = admit (start probe) [ edit "A" ]

    test <@ admission = Admission.Check [ edit "A" ] @>

[<Fact>]
let ``a VCS operation holds what it rewrote until a client asks, then releases it`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe
    position.Value <- Ok(at 'b')

    let gate, rewritten = admit gate [ edit "A" ]
    test <@ rewritten = Admission.Hold @>
    test <@ isWaiting gate @>
    test <@ Notice.ofGate gate = Notice.Waiting(Position.describe (at 'a'), Position.describe (at 'b'), 1) @>

    let gate, released = wake gate (ClientAsked "check")
    test <@ released = [ edit "A" ] @>
    test <@ not (isWaiting gate) @>
    test <@ Notice.ofGate gate = Notice.Checking @>

    // Woken at the new position: the next edit checks at once.
    let _, after = admit gate [ edit "C" ]
    test <@ after = Admission.Check [ edit "C" ] @>

[<Fact>]
let ``a save after the operation is an edit and checks at once, while the operation's batch stays held`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe
    position.Value <- Ok(at 'b')
    let gate, _ = admit gate [ edit "Rebased" ]

    let gate, save = admit gate [ edit "Saved" ]

    test <@ save = Admission.Check [ edit "Saved" ] @>
    test <@ isWaiting gate @>

    let _, released = wake gate (ClientAsked "check")
    test <@ released = [ edit "Rebased" ] @>

[<Fact>]
let ``a second operation while waiting joins what is held and moves the reported position`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe
    position.Value <- Ok(at 'b')
    let gate, _ = admit gate [ edit "A" ]
    position.Value <- Ok(at 'c')

    let gate, second = admit gate [ edit "B" ]

    test <@ second = Admission.Hold @>
    test <@ Notice.ofGate gate = Notice.Waiting(Position.describe (at 'a'), Position.describe (at 'c'), 2) @>

    let _, released = wake gate (ClientAsked "check")
    test <@ released = [ edit "A"; edit "B" ] @>

[<Fact>]
let ``a jj snapshot while waiting is an edit that moves the baseline, not the reported operation`` () =
    let probe, position = movable (Ok(jjAt 'a' JjOperationKind.Rewrite))
    let gate = start probe
    position.Value <- Ok(jjAt 'b' JjOperationKind.Rewrite)
    let gate, _ = admit gate [ edit "Rebased" ]
    position.Value <- Ok(jjAt 'c' JjOperationKind.Snapshot)

    let gate, saved = admit gate [ edit "Saved" ]
    test <@ saved = Admission.Check [ edit "Saved" ] @>

    let reported =
        Notice.Waiting(
            Position.describe (jjAt 'a' JjOperationKind.Rewrite),
            Position.describe (jjAt 'b' JjOperationKind.Rewrite),
            1
        )

    test <@ Notice.ofGate gate = reported @>

    // The baseline is the snapshot: a save at that position still checks.
    let _, again = admit gate [ edit "Again" ]
    test <@ again = Admission.Check [ edit "Again" ] @>

[<Fact>]
let ``an unreadable position while waiting checks the batch and keeps what is held`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe
    position.Value <- Ok(at 'b')
    let gate, _ = admit gate [ edit "A" ]
    position.Value <- Error "HEAD.lock"

    let gate, unreadable = admit gate [ edit "B" ]

    test <@ unreadable = Admission.Check [ edit "B" ] @>
    test <@ isWaiting gate @>

[<Fact>]
let ``a client asking while nothing is held releases nothing`` () =
    let probe, _ = movable (Ok(at 'a'))
    let gate, released = wake (start probe) (ClientAsked "check")

    test <@ List.isEmpty released @>
    test <@ not (isWaiting gate) @>

[<Fact>]
let ``a position unreadable at start checks, and anchors once it reads`` () =
    let probe, position = movable (Error "no checkout yet")
    let gate = start probe

    test
        <@
            match gate with
            | Gate.Unanchored _ -> true
            | Gate.Ungated
            | Gate.Live _
            | Gate.Waiting _ -> false
        @>

    test <@ Notice.ofGate gate = Notice.Checking @>

    let gate, unanchored = admit gate [ edit "A" ]
    test <@ unanchored = Admission.Check [ edit "A" ] @>

    // Still unreadable: still checks, still unanchored.
    let gate, stillUnanchored = admit gate [ edit "B" ]
    test <@ stillUnanchored = Admission.Check [ edit "B" ] @>

    position.Value <- Ok(at 'a')
    let gate, anchoring = admit gate [ edit "C" ]
    test <@ anchoring = Admission.Check [ edit "C" ] @>

    position.Value <- Ok(at 'b')
    let _, moved = admit gate [ edit "D" ]
    test <@ moved = Admission.Hold @>

[<Fact>]
let ``an unreadable position mid-session checks and keeps the last baseline`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe
    position.Value <- Error "HEAD unreadable"

    let gate, unreadable = admit gate [ edit "A" ]
    test <@ unreadable = Admission.Check [ edit "A" ] @>

    position.Value <- Ok(at 'b')
    let _, moved = admit gate [ edit "B" ]
    test <@ moved = Admission.Hold @>

[<Fact>]
let ``a client waking a gate whose position no longer reads leaves it unanchored`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe
    position.Value <- Ok(at 'b')
    let gate, _ = admit gate [ edit "A" ]
    position.Value <- Error "mid-rewrite"

    let gate, released = wake gate (ClientAsked "check")

    test <@ released = [ edit "A" ] @>

    test
        <@
            match gate with
            | Gate.Unanchored _ -> true
            | Gate.Ungated
            | Gate.Live _
            | Gate.Waiting _ -> false
        @>

[<Fact>]
let ``a client asking an ungated or unanchored daemon releases nothing`` () =
    let ungated, fromUngated =
        wake (start (Probe.Untracked "plain")) (ClientAsked "check")

    let probe, _ = movable (Error "unreadable")
    let unanchored, fromUnanchored = wake (start probe) (ClientAsked "check")

    test <@ List.isEmpty fromUngated && List.isEmpty fromUnanchored @>
    test <@ not (isWaiting ungated) && not (isWaiting unanchored) @>

// ---- the notice `status` reports ---------------------------------------------------

let private readBack (notice: Notice) =
    use doc = JsonDocument.Parse((Notice.payload notice).ToJsonString())
    Notice.tryRead doc.RootElement

let private readJson (json: string) =
    use doc = JsonDocument.Parse json
    Notice.tryRead doc.RootElement

[<Fact>]
let ``a notice travels and reads back as itself`` () =
    let waiting = Notice.Waiting("jj op aaaaaaaaaaaa", "jj op bbbbbbbbbbbb", 3)

    test <@ readBack Notice.Checking = Some Notice.Checking @>
    test <@ readBack waiting = Some waiting @>

[<Fact>]
let ``a notice that is not one this build writes reads as none`` () =
    test <@ readJson "[]" = None @>
    test <@ readJson """{"state":"sleeping"}""" = None @>
    test <@ readJson """{"held":1}""" = None @>
    test <@ readJson """{"state":"waiting","before":"a","after":"b"}""" = None @>
    test <@ readJson """{"state":"waiting","before":"a","after":"b","held":-1}""" = None @>

[<Fact>]
let ``status says a waiting daemon is waiting, with the operation, and says nothing while checking`` () =
    test <@ Notice.line Notice.Checking = None @>

    let line =
        "vcs: tree changed by a VCS operation (jj op aaaaaaaaaaaa -> jj op bbbbbbbbbbbb); waiting for a client. Not hung: the next `fshw check` checks the 3 change(s) it rewrote"

    test <@ Notice.line (Notice.Waiting("jj op aaaaaaaaaaaa", "jj op bbbbbbbbbbbb", 3)) = Some line @>

// ---- one change request: changes first, then the client --------------------------

[<Fact>]
let ``a client asking in the same request as the operation's changes is answered for them`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe
    position.Value <- Ok(at 'b')

    let gate, admission = decide gate [ edit "Rebased" ] [ ClientAsked "scan" ]

    test <@ admission = Admission.Check [ edit "Rebased" ] @>
    test <@ not (isWaiting gate) @>

[<Fact>]
let ``a client asking while waiting releases what was held beside the request's own edits`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe
    position.Value <- Ok(at 'b')
    let gate, _ = decide gate [ edit "Rebased" ] []

    let gate, admission = decide gate [ edit "Saved" ] [ ClientAsked "scan" ]

    test <@ admission = Admission.Check [ edit "Rebased"; edit "Saved" ] @>
    test <@ Notice.ofGate gate = Notice.Checking @>

[<Fact>]
let ``a request without a client admits as admit does`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe
    position.Value <- Ok(at 'b')

    let gate, admission = decide gate [ edit "Rebased" ] []

    test <@ admission = Admission.Hold @>
    test <@ isWaiting gate @>

// ---- the setting: `afterVcsOperation` ---------------------------------------------------

[<Fact>]
let ``a repository that checks unasked never holds a VCS operation's batch, and reads no layout to decide it`` () =
    let gate =
        startFor AfterVcsOperation.CheckUnasked (fun () -> failwith "a repository that checks unasked reads no layout")

    let gate, admission = admit gate [ edit "Rebased" ]

    test <@ admission = Admission.Check [ edit "Rebased" ] @>
    test <@ Notice.ofGate gate = Notice.Checking @>

[<Fact>]
let ``a repository that waits for a client holds what a VCS operation rewrote`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = startFor AfterVcsOperation.WaitForClient (fun () -> probe)
    position.Value <- Ok(at 'b')

    let gate, admission = admit gate [ edit "Rebased" ]

    test <@ admission = Admission.Hold @>
    test <@ isWaiting gate @>

[<Fact>]
let ``the setting reads its two spellings and nothing else`` () =
    test <@ AfterVcsOperation.tryParse "checkUnasked" = Some AfterVcsOperation.CheckUnasked @>
    test <@ AfterVcsOperation.tryParse "waitForClient" = Some AfterVcsOperation.WaitForClient @>
    test <@ AfterVcsOperation.tryParse "WaitForClient" = None @>
    test <@ AfterVcsOperation.tryParse "true" = None @>

// ---- known limit --------------------------------------------------------------------------

/// Pins today's behaviour, not a wish: a VCS writes its position after the files, so the
/// part of an operation the debounce window closes on before that write is an edit.
[<Fact>]
let ``known limit: an operation slower than the debounce window has its first part checked as an edit`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe

    // The operation has rewritten its first files; its position is not written yet.
    let gate, first = admit gate [ edit "WrittenFirst" ]
    test <@ first = Admission.Check [ edit "WrittenFirst" ] @>
    test <@ not (isWaiting gate) @>

    // It finishes and writes its position: only what followed the move is held.
    position.Value <- Ok(at 'b')
    let gate, rest = admit gate [ edit "WrittenLast" ]
    test <@ rest = Admission.Hold @>
    test <@ Notice.ofGate gate = Notice.Waiting(Position.describe (at 'a'), Position.describe (at 'b'), 1) @>

/// Pins today's behaviour, not a wish: an operation that rewrites no file (`jj commit`,
/// `jj describe`, jj importing a git commit) makes no batch, but it moves the position, so
/// the next save is held as if the operation had made it.
[<Fact>]
let ``known limit: the first save after an operation that rewrote no file is held`` () =
    let probe, position = movable (Ok(jjAt 'a' JjOperationKind.Rewrite))
    let gate = start probe

    // `jj describe`: the position moves, and no file changes, so no batch comes.
    position.Value <- Ok(jjAt 'b' JjOperationKind.Rewrite)

    let gate, save = admit gate [ edit "Saved" ]
    test <@ save = Admission.Hold @>

    // Held once: the save after it is compared with the moved position, and checks.
    let _, next = admit gate [ edit "SavedAgain" ]
    test <@ next = Admission.Check [ edit "SavedAgain" ] @>
