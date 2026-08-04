module TestPrune.Tests.SyntheticRouteContractTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open Falco.UnionRoutes
open TestPrune.Falco

// DRIFT ALARM (ADR 0001). TestPrune.Falco derives Falco.UnionRoutes case->URL composition from the
// route DU SOURCE (no runtime dependency, no built assembly). That AST copy of the composition
// rules can only stay correct if it matches the library's OWN canonical composition. This contract
// test pins that: it reflects the synthetic route type IN-PROCESS via Falco.UnionRoutes'
// `Route.enumerate` (the oracle — the type lives in this test assembly, so there is NO
// host-assembly loading and none of the abandoned reflection resolver's fragility) and asserts the
// AST resolver composes the SAME URLs, modulo route constraints.
//
// Constraints (`{id:guid}`) are intentionally out of scope: the AST cannot infer them from a
// wrapped id type, and the live matcher compares constraint-insensitively (see
// `UnionRouteLinks.leafReferenceRegexes`). Everything ELSE — segment kebab-casing, empty-segment
// convention names, nested-path concatenation, field-inferred parameters, multi-parent leaves —
// MUST agree. If Falco.UnionRoutes ever changes those rules, this test goes red and the AST copy
// in `UnionRouteLinks` gets a matching, deliberate update rather than drifting silently.
//
// This is Falco.UnionRoutes' ONLY use in the repo, and it is a TEST-ONLY dependency of
// TestPrune.Tests: the shipped TestPrune.Falco package takes no dependency on it.

/// The synthetic DU's exact source, copied next to the test dll (see the fsproj `None` item), so
/// the AST resolver parses precisely the text the reflection oracle compiled — zero drift.
let private syntheticSource () =
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SyntheticRoutes.fs"))

let private astMap () =
    UnionRouteLinks.buildLinkMap [ ("SyntheticRoutes.fs", syntheticSource ()) ]

/// Canonical case->URL map from Falco.UnionRoutes itself, reflecting the compiled synthetic type.
let private canonicalMap () : Map<string, Set<string>> =
    Route.enumerateType typeof<TestPrune.Tests.SyntheticRoutes.Route>
    |> List.fold
        (fun acc (symbol, info: Route.RouteInfo) ->
            let urls =
                match Map.tryFind symbol acc with
                | Some existing -> Set.add info.Path existing
                | None -> Set.singleton info.Path

            Map.add symbol urls acc)
        Map.empty

/// Drop the constraint from a route parameter: `{id:guid}` -> `{id}`, keeping the name.
let private stripConstraints (url: string) : string =
    Regex.Replace(url, @"\{([^:}]+)(:[^}]*)?\}", "{$1}")

let private normalize (m: Map<string, Set<string>>) =
    m |> Map.map (fun _ urls -> urls |> Set.map stripConstraints)

[<Fact>]
let ``AST composition matches Falco.UnionRoutes canonical, modulo constraints (drift alarm)`` () =
    let ast = astMap ()
    let canonical = canonicalMap ()

    // Same leaf case symbols — the AST neither misses nor invents a route.
    test <@ Set.ofSeq (Map.keys ast) = Set.ofSeq (Map.keys canonical) @>

    // Same composed URLs once constraints are normalized away. Any structural rule change in
    // Falco.UnionRoutes (kebab, empty-segment, nesting, param inference) breaks this.
    test <@ normalize ast = normalize canonical @>

[<Fact>]
let ``the only AST-vs-canonical differences are route constraints`` () =
    // Documents the boundary: exact equality does NOT hold (the AST omits `:guid`/`:int`), but the
    // sole differences are constraint suffixes — never structure. If a difference ever appeared
    // that survives constraint-stripping, the drift alarm above would already be red.
    let ast = astMap ()
    let canonical = canonicalMap ()

    let differingKeys =
        Map.keys canonical
        |> Seq.filter (fun k -> Map.tryFind k ast <> Map.tryFind k canonical)
        |> Seq.filter (fun k ->
            (Map.find k ast |> Set.map stripConstraints)
            <> (Map.find k canonical |> Set.map stripConstraints))
        |> Seq.toList

    test <@ List.isEmpty differingKeys @>
