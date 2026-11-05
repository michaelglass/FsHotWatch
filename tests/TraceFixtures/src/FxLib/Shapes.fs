/// Reflection over a union and over the assembly's types (AUTOMATION-1495): code whose outcome
/// changes when a case or a type is added, with no traced method changing.
module FxLib.Shapes

open Microsoft.FSharp.Reflection

type Colour =
    | Red
    | Green

/// A generic helper: `typeof<'T>` names no type in its own IL.
let unionCases<'T> () =
    FSharpType.GetUnionCases typeof<'T> |> Array.map (fun c -> c.Name)

module Colour =
    /// The case names, through the generic helper.
    let names () = unionCases<Colour> ()

/// Reflection over a value's own type.
let caseNamesOf (value: obj) =
    FSharpType.GetUnionCases(value.GetType()) |> Array.map (fun c -> c.Name)

/// A production audit that sweeps the assembly's types.
let audit () =
    typeof<Colour>.Assembly.GetTypes()
    |> Array.filter (fun t -> t.Name.StartsWith "Forbidden")
    |> Array.length
