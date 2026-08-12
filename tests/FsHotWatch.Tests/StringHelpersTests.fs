/// `describeMany` — the non-truncating replacement for `%A` in diagnostic logs.
/// `%A` caps a sequence at 100 elements, so a log line renders a 1,500-element
/// collection identically to a 101-element one, and any measurement of BREADTH
/// taken from that line is worthless. These tests pin the count.
module FsHotWatch.Tests.StringHelpersTests

open Xunit
open Swensen.Unquote
open FsHotWatch.StringHelpers

[<Fact>]
let ``the exact count survives past the sample size`` () =
    // Two collections `%A` renders identically (both capped at 100 plus an
    // ellipsis) must be distinguishable here.
    let small = List.init 101 (fun i -> $"c%d{i}")
    let huge = List.init 1500 (fun i -> $"c%d{i}")

    let renderedSmall = describeMany small
    let renderedHuge = describeMany huge

    test <@ renderedSmall <> renderedHuge @>
    test <@ renderedSmall.StartsWith "101 " @>
    test <@ renderedHuge.StartsWith "1500 " @>

[<Fact>]
let ``an empty collection renders as a distinguishable zero`` () =
    // TestPrune reads these logs: a project present with NO affected classes means
    // "run it in full", which must never look like an absent project.
    test <@ describeMany ([]: string list) = "0 []" @>

[<Fact>]
let ``a collection at or under the sample size is rendered in full`` () =
    test <@ describeMany [ "a" ] = "1 [a]" @>
    test <@ describeMany [ "a"; "b"; "c" ] = "3 [a; b; c]" @>

    let exactly = List.init DefaultLogSample (fun i -> string i)
    let rendered = describeMany exactly

    test <@ rendered.StartsWith $"%d{DefaultLogSample} [" @>
    test <@ not (rendered.Contains "more") @>

[<Fact>]
let ``an oversized collection samples and says how many it withheld`` () =
    let rendered = describeManyWith 3 [ "a"; "b"; "c"; "d"; "e" ]
    test <@ rendered = "5 [a; b; c; … +2 more]" @>

[<Fact>]
let ``the sample plus the withheld count always equals the total`` () =
    for total in [ 0; 1; 4; 5; 6; 99; 100; 101; 1500 ] do
        let rendered = describeManyWith 5 (List.init total (fun i -> $"x%d{i}"))
        test <@ rendered.StartsWith $"%d{total} " @>

        if total > 5 then
            test <@ rendered.Contains $"+%d{total - 5} more" @>

[<Fact>]
let ``the rendering is a single line`` () =
    // `%A` hard-wraps at 80 columns, smearing one log record across many lines
    // and breaking the `[tag] ts msg` framing every other log line obeys.
    let rendered =
        describeMany (List.init 100 (fun i -> $"SomeNamespace.SomeLongTypeName%d{i}"))

    test <@ not (rendered.Contains "\n") @>
    test <@ not (rendered.Contains "\r") @>

[<Fact>]
let ``describeAll never truncates`` () =
    // For diagnostics where the FULL membership is the point — an impact query's
    // seed set, where a sample that omits the one bad entry hides it entirely.
    let huge = List.init 5000 (fun i -> $"s%d{i}")
    let rendered = describeAll huge

    test <@ rendered.StartsWith "5000 " @>
    test <@ not (rendered.Contains "more") @>
    test <@ rendered.Contains "s4999" @>
    test <@ rendered.Contains "s0" @>

[<Fact>]
let ``describeAll agrees with describeMany below the sample size`` () =
    test <@ describeAll [ "a"; "b" ] = describeMany [ "a"; "b" ] @>
    test <@ describeAll ([]: string list) = "0 []" @>

[<Fact>]
let ``the default sample size does not print less than %A did`` () =
    // Swapping `%A` for `describeMany` must never LOSE detail that was visible
    // before — the count is meant to be pure gain.
    test <@ DefaultLogSample = 100 @>
