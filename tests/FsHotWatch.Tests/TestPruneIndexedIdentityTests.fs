/// AUTOMATION-1151: the identity of a file's indexed TestPrune analysis
/// (`IndexedAnalysis.identityOf`) decides whether a `FileChecked` cache hit may stand in for
/// analysing the file again. It must move whenever what the analysis would record can move,
/// and must not move for an upstream edit the file cannot observe.
///
/// Real FCS: `Down.fs` is checked against two versions of `Up.fs` that both compile it
/// cleanly, so neither its source nor its diagnostics tell the two checks apart.
module FsHotWatch.Tests.TestPruneIndexedIdentityTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestHelpers

/// Uses `make` from whichever module `open Up` resolves it to last.
let private down = "module Down\nopen Up\nlet v = make ()\n"

let private plain = "module Up\nlet make () = 1\n"

/// The same `Up.make`, and a later auto-opened `make` that shadows it for `Down`.
let private shadowed =
    "module Up\nlet make () = 1\n[<AutoOpen>]\nmodule Later =\n    let make () = 2\n"

/// `make`'s signature changes; `Down` still compiles cleanly.
let private widened = "module Up\nlet make () = 1L\n"

/// A body and a comment: nothing `Down` can see.
let private bodyEdited = "module Up\n// moved down a line\nlet make () = 1 + 0\n"

/// Check `Down.fs` against `upA` and `upB`, each in its own root, and return both results.
let private checkBoth (layout: UpstreamLayout) (upA: string) (upB: string) =
    withTempDir "identity-a" (fun rootA ->
        withTempDir "identity-b" (fun rootB ->
            let resultA, checkA = checkDownstream rootA layout upA down
            let resultB, checkB = checkDownstream rootB layout upB down
            // Every pair compiles cleanly from the same bytes.
            test <@ resultA.Source = resultB.Source @>
            test <@ Array.isEmpty checkA.Diagnostics && Array.isEmpty checkB.Diagnostics @>

            let identityA = IndexedAnalysis.identityOf rootA resultA
            let identityB = IndexedAnalysis.identityOf rootB resultB
            test <@ identityA.IsSome && identityB.IsSome @>

            let dependencies (result: FileCheckResult) check =
                match
                    TestPrune.AstAnalyzer.analyzeSourceFromResults
                        "Down.fs"
                        result.Source
                        result.ParseResults
                        check
                        "App"
                with
                | Ok analysis -> analysis.Dependencies |> List.map _.ToSymbol |> Set.ofList
                | Error reason -> failwith $"analysis failed: %s{reason}"

            (identityA, identityB), (dependencies resultA checkA, dependencies resultB checkB)))

[<Fact(Timeout = 120000)>]
let ``an upstream edit that changes what the downstream analysis records changes its identity`` () =
    let (identityA, identityB), (dependsA, dependsB) =
        checkBoth EarlierFile plain shadowed

    // The gap is observable: the index would record a different dependency.
    test <@ dependsA <> dependsB @>
    test <@ identityA <> identityB @>

[<Fact(Timeout = 120000)>]
let ``an upstream signature edit changes the downstream identity, in the project and across a reference`` () =
    for layout in [ EarlierFile; ReferencedProject ] do
        let (identityA, identityB), _ = checkBoth layout plain widened
        test <@ identityA <> identityB @>

[<Fact(Timeout = 15000)>]
let ``an analysis whose upstream shape cannot be described has no identity and is never matched`` () =
    // A check result with no state throws from every query.
    let broken =
        System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof<FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults>
        )
        :?> FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults

    let result =
        { fakeFileCheckResult "/src/A.fs" with
            CheckResults = FullCheck broken }

    test <@ IndexedAnalysis.identityOf "/src" result = None @>

    // What a flush records for it instead matches nothing, not even another such record.
    let recorded = IndexedAnalysis.undescribed ()
    test <@ recorded <> IndexedAnalysis.undescribed () @>
    test <@ not (IndexedAnalysis.mayReplay (Some recorded) None (IndexedAnalysis.undescribed ())) @>

[<Fact(Timeout = 120000)>]
let ``an upstream body-only edit keeps the downstream identity`` () =
    for layout in [ EarlierFile; ReferencedProject ] do
        let (identityA, identityB), _ = checkBoth layout plain bodyEdited
        test <@ identityA = identityB @>
