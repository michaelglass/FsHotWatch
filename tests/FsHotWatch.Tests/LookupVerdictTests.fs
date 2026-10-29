/// ADR-059 §3: a verdict is a pure function of the outcomes recorded under the required
/// keys. Green needs every requirement satisfied; any recorded fail is red; anything
/// nothing on record decides is missing — never green.
module FsHotWatch.Tests.LookupVerdictTests

open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.LookupVerdict

let private key (name: string) =
    ActionKey.create "test" [ "name", name ]

let private store (entries: (ActionKey.ActionKey * OutcomeStore.Outcome) list) =
    let map = entries |> List.map (fun (k, o) -> k.Value, o) |> Map.ofList
    fun (value: string) -> Map.tryFind value map

let private pass = OutcomeStore.Outcome.Pass
let private fail = OutcomeStore.Outcome.Fail

[<Fact>]
let ``every requirement passed is green`` () =
    let reqs = [ Requirement.single "a" (key "a"); Requirement.single "b" (key "b") ]
    test <@ decide reqs (store [ key "a", pass; key "b", pass ]) = Answer.Green @>

[<Fact>]
let ``no requirements at all is green`` () =
    test <@ decide [] (store []) = Answer.Green @>

[<Fact>]
let ``a requirement with nothing on record is missing, never green`` () =
    let reqs = [ Requirement.single "a" (key "a"); Requirement.single "b" (key "b") ]
    test <@ decide reqs (store [ key "a", pass ]) = Answer.Missing [ "b" ] @>

[<Fact>]
let ``a recorded fail is red and outranks a missing requirement`` () =
    let reqs =
        [ Requirement.single "a" (key "a")
          Requirement.single "b" (key "b")
          Requirement.single "c" (key "c") ]

    test <@ decide reqs (store [ key "a", fail; key "c", fail ]) = Answer.Red [ "a"; "c" ] @>

[<Fact>]
let ``any one alternative whose keys all passed satisfies a requirement`` () =
    let req =
        { Name = "tests"
          Alternatives = [ [ key "full" ]; [ key "impact"; key "baseline" ] ] }

    test <@ decide [ req ] (store [ key "impact", pass; key "baseline", pass ]) = Answer.Green @>
    test <@ decide [ req ] (store [ key "full", pass ]) = Answer.Green @>

[<Fact>]
let ``an alternative is satisfied only when ALL its keys passed`` () =
    let req =
        { Name = "tests"
          Alternatives = [ [ key "full" ]; [ key "impact"; key "baseline" ] ] }

    test <@ decide [ req ] (store [ key "impact", pass ]) = Answer.Missing [ "tests" ] @>

[<Fact>]
let ``a fail under one alternative is red even when another alternative passed`` () =
    let req =
        { Name = "tests"
          Alternatives = [ [ key "full" ]; [ key "impact"; key "baseline" ] ] }

    test
        <@
            decide [ req ] (store [ key "full", fail; key "impact", pass; key "baseline", pass ]) = Answer.Red
                [ "tests" ]
        @>

[<Fact>]
let ``a requirement with no alternatives, or an empty one, can never be green`` () =
    test <@ decide [ { Name = "x"; Alternatives = [] } ] (store []) = Answer.Missing [ "x" ] @>
    test <@ decide [ { Name = "x"; Alternatives = [ [] ] } ] (store []) = Answer.Missing [ "x" ] @>
