/// Which prefixed variables the options key reads. Any variable that starts with a key prefix
/// (`envPrefixes`) and that the evaluated files do not name falls into one of three classes:
/// - classified as changing an evaluation: keyed;
/// - classified, with evidence, as not changing one: not keyed;
/// - not classified at all: still keyed, and reported as "keyed, unclassified".
/// An unlisted variable never silently changes nothing (that would be a false hit) and is never
/// keyed in silence (a miss with no visible cause).
module FsHotWatch.Tests.EvaluationEnvironmentTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.FSharp
open FsHotWatch
open FsHotWatch.OptionsRecords

let private request: Request =
    { Root = "/r"
      Projects = [ "/r/src/A/A.fsproj" ]
      Toolchain = "toolchain"
      Sdk = "/sdk"
      GlobalProperties = [] }

let private inputs: EvaluationInputs =
    { Reads = [ "/r/src/A/A.fsproj" ]
      Probes = []
      Listings = []
      EnvNames = [ "HOME" ] }

let private environment = [ "HOME", "/home/u"; "PATH", "/bin" ]

let private sameName (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

let private classOf (name: string) =
    classifiedEnvironment |> List.tryFind (fst >> sameName name) |> Option.map snd

/// Not keyed for a reason of its own: a host knob, a variable the process sets for itself, or a
/// runtime-internal knob.
let private namedElsewhere (name: string) =
    SessionScope.isHostKnob name
    || SessionScope.processOwned |> List.exists (sameName name)
    || name.StartsWith(runtimeInternalPrefix, StringComparison.OrdinalIgnoreCase)

let private suffixChars = [ 'A' .. 'Z' ] @ [ '0' .. '9' ] @ [ '_' ]

/// A variable name under one of the key prefixes: sometimes a random one, sometimes one the
/// lists name, in any case.
let private prefixedName: Gen<string> =
    let random =
        gen {
            let! prefix = Gen.elements envPrefixes
            let! length = Gen.choose (1, 24)
            let! suffix = Gen.arrayOfLength length (Gen.elements suffixChars)
            return prefix + String suffix
        }

    let listed =
        Gen.elements (
            (classifiedEnvironment |> List.map fst)
            @ (SessionScope.hostKnobs |> List.map fst)
            @ SessionScope.processOwned
        )

    let cased (name: string) =
        Gen.elements [ name; name.ToLowerInvariant(); name.ToUpperInvariant() ]

    Gen.oneof [ random; listed ] |> Gen.bind cased

[<Fact>]
let ``a prefixed variable moves the options key and is reported unless it is classified otherwise`` () =
    let unclassifiedSeen = ref 0

    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(1000),
        Prop.forAll (Arb.fromGen prefixedName) (fun name ->
            let before = optionsKey request inputs environment
            let after = optionsKey request inputs (environment @ [ name, "1" ])
            let reported = unclassifiedEnvironment inputs.EnvNames (environment @ [ name, "1" ])

            let expectKeyed, expectReported =
                match classOf name with
                | _ when namedElsewhere name -> false, false
                | Some(DoesNotEvaluate _) -> false, false
                | Some(Evaluates _) -> true, false
                | None ->
                    unclassifiedSeen.Value <- unclassifiedSeen.Value + 1
                    true, true

            (after <> before) = expectKeyed
            && (reported = (if expectReported then [ name ] else [])))
    )

    // A property that never met an unclassified name proves nothing about them.
    if unclassifiedSeen.Value < 200 then
        failwith $"only {unclassifiedSeen.Value} of 1000 cases drew an unclassified name"

[<Fact>]
let ``a variable the evaluated files name is keyed and never reported, whatever its class`` () =
    for name in
        [ "MSBUILDALWAYSOVERWRITEREADONLYFILES"
          "DOTNET_SOMETHING_UNCLASSIFIED"
          "MSBUILDTERMINALLOGGER" ] do
        let naming =
            { inputs with
                EnvNames = name :: inputs.EnvNames }

        let off = optionsKey request naming (environment @ [ name, "0" ])
        let on = optionsKey request naming (environment @ [ name, "1" ])
        test <@ (name, off <> on) = (name, true) @>
        test <@ (name, unclassifiedEnvironment naming.EnvNames (environment @ [ name, "1" ])) = (name, []) @>

[<Fact>]
let ``every classified variable is under a key prefix, listed once, with a reason and, when unkeyed, evidence`` () =
    let names = classifiedEnvironment |> List.map fst
    test <@ names |> List.distinctBy (fun n -> n.ToUpperInvariant()) = names @>

    for name, cls in classifiedEnvironment do
        test
            <@
                (name,
                 envPrefixes
                 |> List.exists (fun p -> name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) = (name, true)
            @>

        test <@ (name, namedElsewhere name) = (name, false) @>

        match cls with
        | Evaluates reason -> test <@ (name, reason.Length > 10) = (name, true) @>
        | DoesNotEvaluate(reason, evidence) ->
            test <@ (name, reason.Length > 10 && evidence.Length > 10) = (name, true) @>

[<Fact>]
let ``the variables measured in intelligence's evaluations are classified`` () =
    // Prefixed variables present in the 50 options records intelligence filed under alpha.150
    // that the evaluated files did not name (2026-11-16), less the host knobs.
    for name in
        [ "MSBUILDALWAYSOVERWRITEREADONLYFILES"
          "MSBUILDFAILONDRIVEENUMERATINGWILDCARD"
          "DOTNET_ROOT"
          "DOTNET_ENVIRONMENT"
          "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"
          "DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK" ] do
        test <@ (name, (classOf name).IsSome) = (name, true) @>
