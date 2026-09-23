module FsHotWatch.Tests.FrameExclusionsTests

open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

[<Theory>]
[<InlineData("let here = __SOURCE_DIRECTORY__", "__SOURCE_DIRECTORY__")>]
[<InlineData("let me = __SOURCE_FILE__", "__SOURCE_FILE__")>]
[<InlineData("module X\n#line 12 \"Generated.fs\"\nlet x = 1", "#line")>]
[<InlineData("module X\n# 3 \"Other.fs\"\nlet x = 1", "#line")>]
let ``a construct that reads the file's location is found`` (text: string, construct: string) =
    test <@ FrameExclusions.constructIn text = Some construct @>

[<Theory>]
[<InlineData("module X\nlet x = 1")>]
[<InlineData("#nowarn \"57\"\n#if DEBUG\nlet x = 1\n#endif")>]
[<InlineData("let callerFile ([<System.Runtime.CompilerServices.CallerFilePath>] ?path: string) = path")>]
let ``code that does not read its location is not`` (text: string) =
    // CallerFilePath is a value in the typed tree, never a change to what typechecks.
    test <@ FrameExclusions.constructIn text = None @>

let private optionsWith (dir: string) (sources: (string * string) list) (otherOptions: string list) =
    let files =
        sources
        |> List.map (fun (name, text) ->
            let path = Path.Combine(dir, name)
            File.WriteAllText(path, text)
            path)

    { ProjectFileName = Path.Combine(dir, "P.fsproj")
      ProjectId = None
      SourceFiles = Array.ofList files
      OtherOptions = Array.ofList otherOptions
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = System.DateTime.UtcNow
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

[<Fact>]
let ``a project is excluded by the first source that reads its location, and says which`` () =
    withTempDir "frame-exclusion" (fun dir ->
        let options =
            optionsWith
                dir
                [ "A.fs", "module A\nlet a = 1"
                  "B.fs", "module B\nlet b = __SOURCE_DIRECTORY__" ]
                []

        match FrameExclusions.exclusionFor File.ReadAllText options with
        | Some exclusion ->
            test <@ exclusion.Construct = "__SOURCE_DIRECTORY__" @>
            test <@ exclusion.Where = Path.Combine(dir, "B.fs") @>
            let where = Path.Combine(dir, "B.fs")
            test <@ FrameExclusions.Exclusion.describe exclusion = $"__SOURCE_DIRECTORY__ in %s{where}" @>
        | None -> failwith "expected an exclusion")

[<Fact>]
let ``an option read relative to the project directory excludes it`` () =
    withTempDir "frame-exclusion-option" (fun dir ->
        let options = optionsWith dir [ "A.fs", "module A" ] [ "--version:@version.txt" ]

        test <@ FrameExclusions.exclusionFor File.ReadAllText options |> Option.map _.Construct = Some "--version" @>)

[<Fact>]
let ``a project that reads nothing of its location is checked under the virtual root`` () =
    withTempDir "frame-exclusion-none" (fun dir ->
        // A real, non-provider reference: this test assembly.
        let reference = typeof<FrameExclusions.Exclusion>.Assembly.Location

        let options =
            optionsWith dir [ "A.fs", "module A\nlet a = 1" ] [ $"-r:%s{reference}" ]

        test <@ FrameExclusions.exclusionFor File.ReadAllText options = None @>)

/// An assembly that declares itself a type provider, the way a real provider does:
/// FSharp.Core's `TypeProviderAssemblyAttribute`, applied to the assembly.
let private writeProviderAssembly (path: string) =
    let builder =
        System.Reflection.Emit.PersistedAssemblyBuilder(
            System.Reflection.AssemblyName "FakeProvider",
            typeof<obj>.Assembly
        )

    builder.DefineDynamicModule "FakeProvider" |> ignore

    builder.SetCustomAttribute(
        System.Reflection.Emit.CustomAttributeBuilder(
            typeof<Microsoft.FSharp.Core.CompilerServices.TypeProviderAssemblyAttribute>.GetConstructor
                System.Type.EmptyTypes,
            [||]
        )
    )

    builder.Save path

[<Fact>]
let ``a type provider among the references excludes the project`` () =
    withTempDir "frame-exclusion-provider" (fun dir ->
        let provider = Path.Combine(dir, "FakeProvider.dll")
        writeProviderAssembly provider
        let options = optionsWith dir [ "A.fs", "module A" ] [ $"-r:%s{provider}" ]

        test
            <@
                FrameExclusions.exclusionFor File.ReadAllText options = Some
                    { Construct = "a type provider"
                      Where = provider }
            @>)

[<Fact>]
let ``a file that is not an assembly is no type provider`` () =
    withTempDir "frame-exclusion-junk" (fun dir ->
        let junk = Path.Combine(dir, "Junk.dll")
        File.WriteAllText(junk, "not an assembly")
        let options = optionsWith dir [ "A.fs", "module A" ] [ $"-r:%s{junk}" ]
        test <@ FrameExclusions.exclusionFor File.ReadAllText options = None @>)

[<Fact>]
let ``an assembly's own attributes are read without mistaking them for a provider's`` () =
    withTempDir "frame-exclusion-local" (fun dir ->
        // An attribute the assembly defines itself: its constructor is a definition in
        // that assembly, not a reference to another.
        let path = Path.Combine(dir, "Local.dll")

        let builder =
            System.Reflection.Emit.PersistedAssemblyBuilder(
                System.Reflection.AssemblyName "Local",
                typeof<obj>.Assembly
            )

        let m = builder.DefineDynamicModule "Local"

        let attributeType =
            m.DefineType(
                "LocalAttribute",
                System.Reflection.TypeAttributes.Public
                ||| System.Reflection.TypeAttributes.Class,
                typeof<System.Attribute>
            )

        let ctor =
            attributeType.DefineDefaultConstructor System.Reflection.MethodAttributes.Public

        attributeType.CreateType() |> ignore
        builder.SetCustomAttribute(System.Reflection.Emit.CustomAttributeBuilder(ctor, [||]))
        builder.Save path

        let options = optionsWith dir [ "A.fs", "module A" ] [ $"-r:%s{path}" ]
        test <@ FrameExclusions.exclusionFor File.ReadAllText options = None @>)

[<Fact>]
let ``a source that cannot be read excludes nothing`` () =
    withTempDir "frame-exclusion-unreadable" (fun dir ->
        let options = optionsWith dir [ "A.fs", "module A" ] []

        let options =
            { options with
                SourceFiles = Array.append options.SourceFiles [| Path.Combine(dir, "Missing.fs") |] }

        test <@ FrameExclusions.exclusionFor File.ReadAllText options = None @>)

[<Fact>]
let ``a reference that is not there is no type provider`` () =
    withTempDir "frame-exclusion-missing-ref" (fun dir ->
        let missing = Path.Combine(dir, "Missing.dll")
        let options = optionsWith dir [ "A.fs", "module A" ] [ $"-r:%s{missing}" ]
        test <@ FrameExclusions.exclusionFor File.ReadAllText options = None @>)

/// An attribute whose type is a generic instantiation: its constructor's parent is a
/// type specification, not a type reference.
type GenericMarkerAttribute<'T>() =
    inherit System.Attribute()

[<Fact>]
let ``an attribute of a generic type is read without mistaking it for a provider's`` () =
    withTempDir "frame-exclusion-generic" (fun dir ->
        let path = Path.Combine(dir, "Generic.dll")

        let builder =
            System.Reflection.Emit.PersistedAssemblyBuilder(
                System.Reflection.AssemblyName "Generic",
                typeof<obj>.Assembly
            )

        builder.DefineDynamicModule "Generic" |> ignore

        builder.SetCustomAttribute(
            System.Reflection.Emit.CustomAttributeBuilder(
                typeof<GenericMarkerAttribute<int>>.GetConstructor System.Type.EmptyTypes,
                [||]
            )
        )

        builder.Save path
        let options = optionsWith dir [ "A.fs", "module A" ] [ $"-r:%s{path}" ]
        test <@ FrameExclusions.exclusionFor File.ReadAllText options = None @>)
