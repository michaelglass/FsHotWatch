/// The gate logs what it decides, so it runs with the tests that read the global log.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.VcsQuietTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.VcsPosition
open FsHotWatch.VcsQuiet

let private at (commit: char) =
    Position.GitBranch("refs/heads/main", GitRef.Points(System.String(commit, 40)))

/// A probe whose position the test moves.
let private movable (start: Result<Position, string>) =
    let current = ref start
    Probe.Tracked(fun () -> current.Value), current

let private edit name =
    SourceChanged [ $"/repo/src/{name}.fs" ]

let private isQuiet gate =
    match gate with
    | Gate.Quiet _ -> true
    | Gate.Ungated
    | Gate.Unanchored _
    | Gate.Live _ -> false

[<Fact>]
let ``an untracked checkout checks every batch`` () =
    let gate = start (Probe.Untracked "a plain directory")
    let gate, admission = admit gate [ edit "A" ]

    test <@ admission = Admission.Check [ edit "A" ] @>
    test <@ not (isQuiet gate) @>

[<Fact>]
let ``an edit at a still position checks at once`` () =
    let probe, _ = movable (Ok(at 'a'))
    let _, admission = admit (start probe) [ edit "A" ]

    test <@ admission = Admission.Check [ edit "A" ] @>

[<Fact>]
let ``a VCS operation holds its batch and every later one until a client asks, then checks them all`` () =
    let probe, position = movable (Ok(at 'a'))
    let gate = start probe
    position.Value <- Ok(at 'b')

    let gate, first = admit gate [ edit "A" ]
    test <@ first = Admission.Hold @>
    test <@ isQuiet gate @>

    let gate, second = admit gate [ edit "B" ]
    test <@ second = Admission.Hold @>

    let gate, released = wake gate (ClientAsked "check")
    test <@ released = [ edit "A"; edit "B" ] @>
    test <@ not (isQuiet gate) @>

    // Woken at the new position: the next edit checks at once.
    let _, after = admit gate [ edit "C" ]
    test <@ after = Admission.Check [ edit "C" ] @>

[<Fact>]
let ``a client asking while nothing is held releases nothing`` () =
    let probe, _ = movable (Ok(at 'a'))
    let gate, released = wake (start probe) (ClientAsked "check")

    test <@ List.isEmpty released @>
    test <@ not (isQuiet gate) @>

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
            | Gate.Quiet _ -> false
        @>

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
            | Gate.Quiet _ -> false
        @>

[<Fact>]
let ``a client asking an ungated or unanchored daemon releases nothing`` () =
    let ungated, fromUngated =
        wake (start (Probe.Untracked "plain")) (ClientAsked "check")

    let probe, _ = movable (Error "unreadable")
    let unanchored, fromUnanchored = wake (start probe) (ClientAsked "check")

    test <@ List.isEmpty fromUngated && List.isEmpty fromUnanchored @>
    test <@ not (isQuiet ungated) && not (isQuiet unanchored) @>
