module TestPrune.Tests.CoverageImpactTests

open System
open Swensen.Unquote
open Xunit
open TestPrune.CoverageImpact

let private now = DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero)
let private root = "/repo"

let private policy =
    { RepoRoot = root
      ExpectedBaseline = "baseline-a"
      Now = now
      MaximumAge = TimeSpan.FromHours 2 }

let private snapshot project baseline observedAt health files =
    createSnapshot root project baseline observedAt health files

[<Fact>]
let ``runtime coverage is additive and reports every matching file`` () =
    let snapshots =
        [ snapshot "Browser.Tests" "baseline-a" (now.AddMinutes(-10)) Complete [ "src/A.fs"; "src/B.fs" ]
          snapshot "Unit.Tests" "baseline-a" now Complete [] ]

    let selected =
        selectProjects policy [ "Browser.Tests"; "Unit.Tests" ] [ "Unit.Tests" ] [ "src/B.fs"; "src/A.fs" ] snapshots

    test <@ selected["Browser.Tests"] = [ RuntimeCoverage "src/A.fs"; RuntimeCoverage "src/B.fs" ] @>
    test <@ selected["Unit.Tests"] = [ AstImpact ] @>

[<Fact>]
let ``one project retains AST plus every runtime attribution`` () =
    let snapshots =
        [ snapshot "Browser.Tests" "baseline-a" now Complete [ "src/A.fs"; "src/B.fs" ] ]

    let selected =
        selectProjects policy [ "Browser.Tests" ] [ "Browser.Tests" ] [ "src/B.fs"; "src/A.fs" ] snapshots

    test <@ selected["Browser.Tests"] = [ AstImpact; RuntimeCoverage "src/A.fs"; RuntimeCoverage "src/B.fs" ] @>

[<Fact>]
let ``expected trailing baseline is accepted instead of current changed tree`` () =
    let snapshots =
        [ snapshot "Browser.Tests" "baseline-a" now Complete [ "src/Changed.fs" ] ]

    let selected =
        selectProjects policy [ "Browser.Tests" ] [] [ "src/Changed.fs" ] snapshots

    test <@ selected["Browser.Tests"] = [ RuntimeCoverage "src/Changed.fs" ] @>

[<Fact>]
let ``unexpected baseline widens selection`` () =
    let snapshots = [ snapshot "Browser.Tests" "other" now Complete [] ]

    let selected =
        selectProjects policy [ "Browser.Tests" ] [] [ "src/Changed.fs" ] snapshots

    test <@ selected["Browser.Tests"] = [ CoverageFromUnexpectedBaseline("other", "baseline-a") ] @>

[<Fact>]
let ``future candidate does not supersede newest non-future snapshot`` () =
    let snapshots =
        [ snapshot "Browser.Tests" "baseline-a" (now.AddMinutes 5) Complete []
          snapshot "Browser.Tests" "baseline-a" (now.AddMinutes(-5)) Complete [ "src/Changed.fs" ] ]

    let selected =
        selectProjects policy [ "Browser.Tests" ] [] [ "src/Changed.fs" ] snapshots

    test <@ selected["Browser.Tests"] = [ RuntimeCoverage "src/Changed.fs" ] @>

[<Fact>]
let ``only future candidates widen with clock-skew reason`` () =
    let future = now.AddMinutes 5

    let selected =
        selectProjects
            policy
            [ "Browser.Tests" ]
            []
            [ "src/Changed.fs" ]
            [ snapshot "Browser.Tests" "baseline-a" future Complete [] ]

    test <@ selected["Browser.Tests"] = [ CoverageClockSkew(future, now) ] @>

[<Fact>]
let ``closest future timestamp is reported when every candidate is future`` () =
    let closest = now.AddMinutes 2

    let snapshots =
        [ snapshot "Browser.Tests" "baseline-a" (now.AddMinutes 10) Complete []
          snapshot "Browser.Tests" "baseline-a" closest Complete [] ]

    let selected = selectProjects policy [ "Browser.Tests" ] [] [] snapshots
    test <@ selected["Browser.Tests"] = [ CoverageClockSkew(closest, now) ] @>

[<Fact>]
let ``missing expired empty malformed and incomplete reports widen`` () =
    let snapshots =
        [ snapshot "Old.Tests" "baseline-a" (now.AddHours(-3)) Complete []
          snapshot "Empty.Tests" "baseline-a" now Empty []
          snapshot "Malformed.Tests" "baseline-a" now (Malformed "bad xml") []
          snapshot "Incomplete.Tests" "baseline-a" now (Incomplete 2) [] ]

    let projects =
        [ "Missing.Tests"
          "Old.Tests"
          "Empty.Tests"
          "Malformed.Tests"
          "Incomplete.Tests" ]

    let selected = selectProjects policy projects [] [ "src/Changed.fs" ] snapshots

    test <@ selected["Missing.Tests"] = [ CoverageMissing ] @>
    test <@ selected["Old.Tests"] = [ CoverageExpired(TimeSpan.FromHours 3, TimeSpan.FromHours 2) ] @>
    test <@ selected["Empty.Tests"] = [ CoverageReportEmpty ] @>
    test <@ selected["Malformed.Tests"] = [ CoverageReportMalformed "bad xml" ] @>
    test <@ selected["Incomplete.Tests"] = [ CoverageReportIncomplete 2 ] @>

[<Fact>]
let ``snapshot validates malformed empty and partially invalid reports`` () =
    let malformed = snapshotFromCobertura root "A.Tests" "baseline-a" now "<coverage>"
    let empty = snapshotFromCobertura root "B.Tests" "baseline-a" now "<coverage/>"

    let incomplete =
        snapshotFromCobertura
            root
            "C.Tests"
            "baseline-a"
            now
            """<coverage><class filename="src/Good.fs"><line number="1" hits="1"/></class><class filename="../Outside.fs"><line number="2" hits="1"/></class></coverage>"""

    test
        <@
            match snapshotHealth malformed with
            | Malformed _ -> true
            | _ -> false
        @>

    test <@ snapshotHealth empty = Empty @>
    test <@ snapshotHealth incomplete = Incomplete 1 @>
    test <@ snapshotSourceFiles incomplete = Set.ofList [ "src/Good.fs" ] @>

    let selected =
        selectProjects policy [ "C.Tests" ] [] [ "src/Other.fs" ] [ incomplete ]

    test <@ selected["C.Tests"] = [ CoveragePathRejected "../Outside.fs"; CoverageReportIncomplete 1 ] @>

[<Fact>]
let ``path canonicalization is symmetric and preserves dotfiles`` () =
    let xml =
        """<coverage><class filename=".config\Reached.fs"><line number="1" hits="1"/></class></coverage>"""

    let observed = snapshotFromCobertura root "Browser.Tests" "baseline-a" now xml

    let selected =
        selectProjects policy [ "Browser.Tests" ] [] [ "./.config/Reached.fs" ] [ observed ]

    test <@ snapshotSourceFiles observed = Set.ofList [ ".config/Reached.fs" ] @>
    test <@ selected["Browser.Tests"] = [ RuntimeCoverage ".config/Reached.fs" ] @>

[<Fact>]
let ``outside-root changed path is rejected and widens every project`` () =
    let snapshots = [ snapshot "Browser.Tests" "baseline-a" now Complete [] ]

    let selected =
        selectProjects policy [ "Browser.Tests" ] [] [ "../Outside.fs" ] snapshots

    test <@ selected["Browser.Tests"] = [ CoveragePathRejected "../Outside.fs" ] @>

[<Fact>]
let ``invalid changed path is data rather than an exception`` () =
    let invalid = "bad" + string (char 0) + "path.fs"
    let snapshots = [ snapshot "Browser.Tests" "baseline-a" now Complete [] ]
    let selected = selectProjects policy [ "Browser.Tests" ] [] [ invalid ] snapshots
    test <@ selected["Browser.Tests"] = [ CoveragePathRejected invalid ] @>

[<Fact>]
let ``foreign absolute coverage paths are rejected on every host`` () =
    let windows =
        snapshotFromCobertura
            root
            "Browser.Tests"
            "baseline-a"
            now
            """<coverage><class filename="C:\outside\Foo.fs"><line number="1" hits="1"/></class></coverage>"""

    test <@ snapshotHealth windows = Incomplete 1 @>

[<Fact>]
let ``invalid line metadata does not misreport its valid source path`` () =
    let observed =
        snapshotFromCobertura
            root
            "Browser.Tests"
            "baseline-a"
            now
            """<coverage><class filename="src/Good.fs"><line number="not-a-number" hits="1"/></class></coverage>"""

    let selected = selectProjects policy [ "Browser.Tests" ] [] [] [ observed ]
    test <@ selected["Browser.Tests"] = [ CoverageReportIncomplete 1 ] @>

[<Fact>]
let ``construction and policy reject blank identities and negative age`` () =
    Assert.Throws<ArgumentException>(fun () -> createSnapshot root " " "baseline-a" now Complete [] |> ignore)
    |> ignore

    Assert.Throws<ArgumentException>(fun () -> createSnapshot root "A.Tests" "" now Complete [] |> ignore)
    |> ignore

    Assert.Throws<ArgumentException>(fun () -> createSnapshot "" "A.Tests" "baseline-a" now Complete [] |> ignore)
    |> ignore

    let invalidPolicy =
        { policy with
            MaximumAge = TimeSpan.FromSeconds(-1.0) }

    Assert.Throws<ArgumentException>(fun () -> selectProjects invalidPolicy [] [] [] [] |> ignore)
    |> ignore

[<Fact>]
let ``construction rejects blank paths and accumulates prior incomplete count`` () =
    let fromComplete = createSnapshot root "A.Tests" "baseline-a" now Complete [ " " ]

    let fromIncomplete =
        createSnapshot root "B.Tests" "baseline-a" now (Incomplete 2) [ " " ]

    test <@ snapshotHealth fromComplete = Incomplete 1 @>
    test <@ snapshotHealth fromIncomplete = Incomplete 3 @>

[<Fact>]
let ``absolute inside-root coverage path is accepted`` () =
    let observed =
        createSnapshot root "A.Tests" "baseline-a" now Complete [ "/repo/src/A.fs" ]

    test <@ snapshotSourceFiles observed = Set.ofList [ "src/A.fs" ] @>

[<Fact>]
let ``wrong XML root is malformed and class without lines is incomplete`` () =
    let wrongRoot =
        snapshotFromCobertura root "A.Tests" "baseline-a" now "<not-coverage/>"

    let noLines =
        snapshotFromCobertura root "B.Tests" "baseline-a" now "<coverage><class filename='src/A.fs'/></coverage>"

    test
        <@
            match snapshotHealth wrongRoot with
            | Malformed _ -> true
            | _ -> false
        @>

    test <@ snapshotHealth noLines = Incomplete 1 @>

[<Fact>]
let ``fresh unrelated coverage does not narrow AST selection`` () =
    let snapshots =
        [ snapshot "Unit.Tests" "baseline-a" now Complete [ "src/Unrelated.fs" ] ]

    let selected =
        selectProjects policy [ "Unit.Tests" ] [ "Unit.Tests" ] [ "src/Changed.fs" ] snapshots

    test <@ selected["Unit.Tests"] = [ AstImpact ] @>
