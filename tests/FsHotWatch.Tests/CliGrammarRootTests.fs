/// The CLI's command grammar must be discoverable from the compiled assembly alone.
///
/// Release tooling that decides semantic-version bumps reads the CLI grammar by
/// reflection: a union with a command-level CommandTree attribute (`Cmd`, `CmdArg`,
/// `CmdExample`, `CmdDefault`) on any case is a command union, and the root is the one
/// command union no other command union names as a nested group. When that search
/// finds more than one root it refuses to guess and the grammar is never compared, so
/// a breaking CLI change would ship under a non-breaking version.
///
/// `CmdArg` documents a COMMAND's positional argument. CommandTree ignores it on a flag
/// case (the flag path reads only `CmdFlag`), so putting it there changes nothing the
/// user sees while turning the flag union into a second root.
module FsHotWatch.Tests.CliGrammarRootTests

open System
open Microsoft.FSharp.Reflection
open Xunit
open Swensen.Unquote
open CommandTree

let private commandAttributeTypes: Type list =
    [ typeof<CmdAttribute>
      typeof<CmdArgAttribute>
      typeof<CmdExampleAttribute>
      typeof<CmdDefaultAttribute> ]

let private cliUnions () : Type list =
    typeof<FsHotWatch.Cli.Program.Command>.Assembly.GetTypes()
    |> Array.filter (fun t -> not t.IsNested || not (FSharpType.IsUnion(t.DeclaringType, true)))
    |> Array.filter (fun t -> FSharpType.IsUnion(t, true))
    |> Array.toList

let private hasAny (types: Type list) (case: UnionCaseInfo) =
    types |> List.exists (fun a -> not (Array.isEmpty (case.GetCustomAttributes a)))

let private isCommandUnion (t: Type) =
    FSharpType.GetUnionCases(t, true) |> Array.exists (hasAny commandAttributeTypes)

/// Unions named by a case whose single field is that union: a nested command group.
let private nestedGroups (t: Type) : Type list =
    FSharpType.GetUnionCases(t, true)
    |> Array.choose (fun case ->
        match case.GetFields() with
        | [| f |] when FSharpType.IsUnion(f.PropertyType, true) -> Some f.PropertyType
        | _ -> None)
    |> Array.toList

[<Fact>]
let ``the CLI grammar has exactly one root command union`` () =
    let commandUnions = cliUnions () |> List.filter isCommandUnion

    let nested =
        commandUnions |> List.collect nestedGroups |> List.map _.FullName |> set

    let roots =
        commandUnions
        |> List.filter (fun (u: Type) -> not (nested.Contains u.FullName))
        |> List.map _.FullName

    test <@ roots = [ typeof<FsHotWatch.Cli.Program.Command>.FullName ] @>

[<Fact>]
let ``no flag case carries a command-level attribute`` () =
    let offenders =
        cliUnions ()
        |> List.collect (fun t ->
            FSharpType.GetUnionCases(t, true)
            |> Array.filter (fun case -> hasAny [ typeof<CmdFlagAttribute> ] case && hasAny commandAttributeTypes case)
            |> Array.map (fun case -> $"%s{t.Name}.%s{case.Name}")
            |> Array.toList)

    test <@ List.isEmpty offenders @>
