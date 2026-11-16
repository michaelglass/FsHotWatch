/// An options evaluation's inputs are sets: what MSBuild read, probed and listed, and the
/// variables its files name. They are gathered in concurrent collections whose order is the
/// process's hash order, so two evaluations of the same inputs gathered them in different orders
/// and filed different bytes (AUTOMATION-1723, AUTOMATION-1672). Every set is written in one
/// canonical order: shuffling any of them, or repeating an entry, moves neither the bytes, nor
/// their digest, nor the key.
module FsHotWatch.Tests.OptionsCanonicalOrderTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.FSharp
open FsHotWatch
open FsHotWatch.OptionsRecords

let private paths =
    [ "/r/src/A/A.fsproj"
      "/r/src/A/obj/project.assets.json"
      "/r/Directory.Build.props"
      "/r/src/B/B.fsproj"
      "/nuget/packages/fsharp.core/10.0.100/build/FSharp.Core.props"
      "/sdk/Sdk.props"
      "/r/src/a/x.fs"
      "/r/src/A/X.fs" ]

let private listings: Listing list =
    [ for kind in [ ListingKind.Files; ListingKind.Directories; ListingKind.Entries ] do
          for directory in [ "/r/src/A"; "/r/src/A/obj"; "/sdk/Targets" ] do
              for pattern in [ "*"; "*.props" ] do
                  { Kind = kind
                    Directory = directory
                    Pattern = pattern
                    Recursive = false } ]

/// MSBuild property names are case-insensitive, so one variable can be named in several cases.
let private envNames =
    [ "NoWarn"
      "nowarn"
      "NOWARN"
      "MSBuildThisFile"
      "ThellmaRuntimePatch"
      "UserSecretsId" ]

let private inputsGen: Gen<EvaluationInputs> =
    gen {
        let! reads = Gen.subListOf paths
        let! probes = Gen.subListOf paths
        let! listed = Gen.subListOf listings
        let! names = Gen.subListOf envNames

        return
            { Reads = reads
              Probes = probes
              Listings = listed
              EnvNames = names }
    }

/// `values` gathered in another order, with some gathered twice.
let private again (values: 'T list) : Gen<'T list> =
    gen {
        let! repeated = Gen.subListOf values
        let! shuffled = Gen.shuffle (values @ repeated)
        return List.ofArray shuffled
    }

/// `inputs` gathered in another order, with some entries gathered twice.
let private regathered (inputs: EvaluationInputs) : Gen<EvaluationInputs> =
    gen {
        let! reads = again inputs.Reads
        let! probes = again inputs.Probes
        let! listed = again inputs.Listings
        let! names = again inputs.EnvNames

        return
            { Reads = reads
              Probes = probes
              Listings = listed
              EnvNames = names }
    }

/// Compile items a build generated under the projects' build output.
let private generatedItems =
    [ "/r/src/A/obj/Debug/net10.0/A.AssemblyInfo.fs"
      "/r/src/B/obj/Debug/net10.0/B.AssemblyInfo.fs"
      "/r/src/A/obj/Debug/net10.0/A.AssemblyAttributes.fs" ]

let private filedGen: Gen<FiledInputs> =
    gen {
        let! keyed = inputsGen
        let! generated = Gen.subListOf generatedItems
        return { Keyed = keyed; Generated = generated }
    }

/// `filed` gathered in another order, with some entries gathered twice.
let private regatheredFiled (filed: FiledInputs) : Gen<FiledInputs> =
    gen {
        let! keyed = regathered filed.Keyed
        let! generated = again filed.Generated
        return { Keyed = keyed; Generated = generated }
    }

/// A filed set of inputs and the same set gathered again, as the options-inputs property draws them.
let private filedCase: Gen<FiledInputs * FiledInputs> =
    gen {
        let! filed = filedGen
        let! other = regatheredFiled filed
        return filed, other
    }

let private request: Request =
    { Root = "/r"
      Projects = [ "/r/src/A/A.fsproj" ]
      Toolchain = "toolchain"
      Sdk = "/sdk"
      GlobalProperties = [ "Configuration", "Debug" ] }

let private environment =
    [ "NoWarn", "FS0025"; "DOTNET_ROOT", "/sdk"; "PATH", "/bin" ]

[<Fact>]
let ``an options-inputs record of the same inputs is the same bytes, digest and key however they were gathered`` () =
    let differed = ref 0

    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(500),
        Prop.forAll (Arb.fromGen filedCase) (fun (filed, other) ->
            if filed <> other then
                differed.Value <- differed.Value + 1

            let key = optionsKey request filed.Keyed environment
            let bytes = InputsJson.toJson filed [] key

            optionsKey request other.Keyed environment = key
            && InputsJson.toJson other [] key = bytes
            && ContentHash.ofText (InputsJson.toJson other [] key) = ContentHash.ofText bytes)
    )

    // A property whose two gatherings were always already identical proves nothing about order.
    if differed.Value < 400 then
        failwith $"only {differed.Value} of 500 cases gathered the inputs differently"

[<Fact>]
let ``a portable index of the same inputs is the same bytes however they were gathered`` () =
    let indexOf (inputs: EvaluationInputs) (generated: (string * string) list) : OptionsPortable.Index =
        { Reads = inputs.Reads
          Probes = inputs.Probes
          Listings = inputs.Listings
          EnvNames = inputs.EnvNames
          Generated = generated }

    let generatedGen =
        Gen.subListOf [ "/r/src/A/obj/a.fs", "s1"; "/r/src/B/obj/b.fs", "s2"; "/r/x.fs", "s3" ]

    let case =
        gen {
            let! inputs = inputsGen
            let! other = regathered inputs
            let! generated = generatedGen
            let! shuffled = Gen.shuffle generated
            return indexOf inputs generated, indexOf other (List.ofArray shuffled)
        }

    let key =
        optionsKey
            request
            { Reads = []
              Probes = []
              Listings = []
              EnvNames = [] }
            []

    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(300),
        Prop.forAll (Arb.fromGen case) (fun (index, other) ->
            OptionsPortable.IndexJson.toJson other key = OptionsPortable.IndexJson.toJson index key)
    )

[<Fact>]
let ``the canonical order sorts each set ordinally and names a variable once, in one spelling`` () =
    let inputs =
        { Reads = [ "/r/b"; "/r/a"; "/r/b" ]
          Probes = [ "/r/Z"; "/r/a" ]
          Listings =
            [ { Kind = ListingKind.Files
                Directory = "/r/b"
                Pattern = "*"
                Recursive = false }
              { Kind = ListingKind.Directories
                Directory = "/r/a"
                Pattern = "*"
                Recursive = false } ]
          EnvNames = [ "nowarn"; "UserSecretsId"; "NoWarn" ] }

    let canonical = Canonical.inputs inputs
    test <@ canonical.Reads = [ "/r/a"; "/r/b" ] @>
    test <@ canonical.Probes = [ "/r/Z"; "/r/a" ] @>
    test <@ canonical.Listings |> List.map _.Directory = [ "/r/a"; "/r/b" ] @>
    test <@ canonical.EnvNames = [ "NoWarn"; "UserSecretsId" ] @>
    test <@ Canonical.inputs canonical = canonical @>

/// The list fields of record `'T` that no draw of `pairs` varies (never non-empty) or regathers
/// (never different between its two gatherings). A field that is itself a record is checked by
/// the caller, as its own `'T`.
let private unexercised<'T> (pairs: ('T * 'T) list) : string list =
    let isEmpty (value: obj) =
        (value :?> Collections.IEnumerable) |> Seq.cast<obj> |> Seq.isEmpty

    [ for field in FSharp.Reflection.FSharpType.GetRecordFields typeof<'T> do
          if not (FSharp.Reflection.FSharpType.IsRecord field.PropertyType) then
              let values = pairs |> List.map (fun (a, b) -> field.GetValue a, field.GetValue b)

              if values |> List.forall (fst >> isEmpty) then
                  $"%s{typeof<'T>.Name}.%s{field.Name} is never varied"

              if values |> List.forall (fun (a, b) -> a = b) then
                  $"%s{typeof<'T>.Name}.%s{field.Name} is never regathered" ]

// AUTOMATION-1741: a field added to the inputs records must be drawn varied and regathered by the
// order property's generators. Record syntax makes a generator name a new field, but a constant
// there would leave its order untested, as `FiledInputs.Generated` was (always empty) until this.
[<Fact>]
let ``the order property's generators vary and regather every field of the inputs records`` () =
    let pairs = Gen.sample 300 filedCase |> List.ofArray
    let keyed = pairs |> List.map (fun (a, b) -> a.Keyed, b.Keyed)
    test <@ unexercised<FiledInputs> pairs @ unexercised<EvaluationInputs> keyed = [] @>
