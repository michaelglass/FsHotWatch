/// Reflection inputs (AUTOMATION-1495, phase-2 plan Task 11): a whole-assembly type sweep is
/// a `types` input hashed over the assembly's type names, and union reflection over a type is
/// a `type` input joined to that type's symbol.
module TestPrune.Trace.Tests.ReflectionInputTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Microsoft.FSharp.Reflection
open TestPrune.Trace
open TestPrune.Trace.Recorder

/// Public: FSharp.Core refuses a private union representation without `BindingFlags.NonPublic`.
type Shade =
    | Light
    | Dark

let private fxLib = Path.Combine(Fixtures.fxLibDir, "FxLib.dll")

let private withRecorder (state: RecorderState) (f: unit -> 'a) =
    let saved = Runtime.state
    Runtime.state <- state

    try
        f ()
    finally
        Runtime.state <- saved

let private recorderWithRoot (root: string) =
    let state = RecorderState(8, None, null)
    state.RepoRoot <- root
    state

let private inputsOf (state: RecorderState) =
    state.CurrentScope().Inputs.Keys
    |> Seq.map (fun (struct (k, p)) -> k, p)
    |> Set.ofSeq

[<Fact>]
let ``the type-set hash is the assembly's sorted type names, and absent for a missing file`` () =
    let h = TraceIngest.typeSetHash fxLib
    test <@ h = TraceIngest.typeSetHash fxLib @>
    test <@ h <> "absent" && h.Length = 64 @>
    test <@ TraceIngest.typeSetHash (Path.Combine(Path.GetTempPath(), "no-such-assembly.dll")) = "absent" @>
    // Another assembly's types hash differently.
    test <@ TraceIngest.typeSetHash (typeof<Shade>.Assembly.Location) <> h @>

[<Fact>]
let ``a types input's current hash is the type-set hash`` () =
    let rel = Path.GetRelativePath(Fixtures.repoRoot, fxLib).Replace('\\', '/')

    test <@ TraceIngest.currentInputHash Fixtures.repoRoot ("types", rel) = Some(TraceIngest.typeSetHash fxLib) @>

[<Fact>]
let ``every FSharp.Core union-reflection API that takes a Type or a UnionCaseInfo is redirected`` () =
    let apis =
        [ typeof<FSharpType>; typeof<FSharpValue> ]
        |> List.collect (fun t -> t.GetMethods() |> List.ofArray)
        |> List.filter (fun m -> m.IsStatic && m.Name.Contains "Union")
        |> List.filter (fun m ->
            m.GetParameters()
            |> Array.exists (fun p -> p.ParameterType = typeof<Type> || p.ParameterType = typeof<UnionCaseInfo>))
        |> List.map (fun m -> m.DeclaringType.FullName + "::" + m.Name)
        |> List.distinct

    let redirected =
        Redirects.table
        |> List.map (fun r -> r.DeclaringType + "::" + r.Name)
        |> Set.ofList

    test <@ not apis.IsEmpty @>
    test <@ apis |> List.filter (redirected.Contains >> not) |> List.isEmpty @>

[<Fact>]
let ``the assembly sweeps are redirected`` () =
    let redirected =
        Redirects.table
        |> List.map (fun r -> r.DeclaringType + "::" + r.Name)
        |> Set.ofList

    test
        <@
            Set.isSubset
                (set
                    [ "System.Reflection.Assembly::GetTypes"
                      "System.Reflection.Assembly::GetExportedTypes"
                      "System.Reflection.Assembly::get_DefinedTypes"
                      "System.Reflection.Assembly::get_ExportedTypes"
                      "System.Reflection.Module::GetTypes" ])
                redirected
        @>

[<Fact>]
let ``union reflection notes each reflected type with its assembly file`` () =
    // Every assembly here sits in the test's bin, under the repository root, FSharp.Core's copy
    // too: which of them are repository code is ingestion's question (`reflectedSymbols`).
    let state = recorderWithRoot Fixtures.repoRoot
    state.EnterScope "T:reflect"

    withRecorder state (fun () ->
        test <@ Reflect.GetUnionCases(typeof<Shade>, None).Length = 2 @>
        test <@ Reflect.IsUnion(typeof<Shade>, None) @>
        test <@ Reflect.IsUnion(typeof<option<int>>, None) @>)

    let shade = typeof<Shade>
    let opt = typedefof<option<int>>

    test
        <@
            inputsOf state = set
                [ "type", shade.Assembly.Location + "|" + shade.FullName
                  "type", opt.Assembly.Location + "|" + opt.FullName ]
        @>

[<Fact>]
let ``a sweep of a repository assembly notes its type set`` () =
    let state = recorderWithRoot Fixtures.repoRoot
    state.EnterScope "T:sweep"
    let asm = typeof<Shade>.Assembly

    withRecorder state (fun () -> test <@ Reflect.Assembly_GetTypes(asm).Length > 0 @>)
    test <@ inputsOf state = set [ "types", asm.Location ] @>

[<Fact>]
let ``a mutated union type makes its reflection throw, and only its`` () =
    let state = recorderWithRoot Fixtures.repoRoot
    state.EnterScope "T:mutant"
    state.MutantTypes <- Runtime.mutantTypes typeof<Shade>.FullName

    withRecorder state (fun () ->
        Assert.Throws<MutantHitException>(fun () -> Reflect.GetUnionCases(typeof<Shade>, None) |> ignore)
        |> ignore

        test <@ Reflect.GetUnionCases(typeof<option<int>>, None).Length = 2 @>)

    test <@ isNull (Runtime.mutantTypes "") @>

[<Fact>]
let ``with no recorder a shim only calls through`` () =
    withRecorder null (fun () -> test <@ Reflect.GetUnionCases(typeof<Shade>, None).Length = 2 @>)

[<Fact>]
let ``a reflected repository type joins to its symbol, an unknown one is incomplete, a foreign one is ignored`` () =
    let ix: Joiner.SymbolIndex =
        { InFile = fun _ -> []
          Exists = fun n -> n = "Lib.Shapes.Colour" || n = "Lib.Unversioned"
          IsIndexedFile = fun _ -> true
          RepoRoot = "/repo" }

    let reflect (file: string) (name: string) : Model.RecordedInput =
        { Kind = Model.TypeReflected
          Path = file + "|" + name }

    let symbols, reasons =
        TraceIngest.reflectedSymbols
            ix
            (Map [ "Lib.Shapes.Colour", "h1" ])
            (set [ "Lib" ])
            [ reflect "/repo/bin/Lib.dll" "Lib.Shapes+Colour"
              reflect "/repo/bin/Lib.dll" "Lib.Shapes+Colour"
              reflect "/repo/bin/Lib.dll" "Lib.Gone"
              reflect "/repo/bin/Lib.dll" "Lib.Unversioned"
              { Kind = Model.TypeReflected
                Path = "no separator" }
              reflect "/repo/bin/FSharp.Core.dll" "Microsoft.FSharp.Core.FSharpOption`1"
              { Kind = Model.FileRead
                Path = "/repo/a.txt" } ]

    test <@ symbols = [ "Lib.Shapes.Colour", "h1" ] @>

    test
        <@
            reasons = [ Model.UnmappedCode "reflected-type:Lib.Gone"
                        Model.UnmappedCode "reflected-type:Lib.Unversioned" ]
        @>

type Boxed = Box of int

[<Fact>]
let ``every union shim notes its union and then does what the original does`` () =
    let state = recorderWithRoot Fixtures.repoRoot
    state.EnterScope "T:shims"
    let boxed = typeof<Boxed>
    let case = FSharpType.GetUnionCases(boxed).[0]

    withRecorder state (fun () ->
        test <@ Reflect.MakeUnion(case, [| box 3 |], None) = box (Box 3) @>

        let c, fields = Reflect.GetUnionFields(box (Box 4), boxed, None)
        test <@ c.Name = "Box" && fields = [| box 4 |] @>
        // A null type is the value's own type, for FSharp.Core and for the note.
        let byValue, _ = Reflect.GetUnionFields(box (Box 4), null, None)
        test <@ byValue.Name = "Box" @>

        Assert.ThrowsAny<exn>(fun () -> Reflect.GetUnionFields(null, null, None) |> ignore)
        |> ignore

        test <@ Reflect.PreComputeUnionTagReader (boxed, None) (box (Box 1)) = 0 @>
        test <@ not (isNull (Reflect.PreComputeUnionTagMemberInfo(boxed, None))) @>
        test <@ Reflect.PreComputeUnionReader (case, None) (box (Box 5)) = [| box 5 |] @>
        test <@ Reflect.PreComputeUnionConstructor (case, None) [| box 6 |] = box (Box 6) @>
        test <@ (Reflect.PreComputeUnionConstructorInfo(case, None)).Name = "NewBox" @>

        Assert.ThrowsAny<exn>(fun () -> Reflect.PreComputeUnionReader(Unchecked.defaultof<_>, None) |> ignore)
        |> ignore)

    test <@ inputsOf state = set [ "type", boxed.Assembly.Location + "|" + boxed.FullName ] @>

[<Fact>]
let ``every sweep notes the swept assembly`` () =
    let state = recorderWithRoot Fixtures.repoRoot
    state.EnterScope "T:sweeps"
    let asm = typeof<Boxed>.Assembly

    withRecorder state (fun () ->
        test <@ Reflect.Assembly_GetExportedTypes(asm).Length > 0 @>
        test <@ not (Seq.isEmpty (Reflect.Assembly_get_DefinedTypes asm)) @>
        test <@ not (Seq.isEmpty (Reflect.Assembly_get_ExportedTypes asm)) @>
        test <@ Reflect.Module_GetTypes(asm.ManifestModule).Length > 0 @>)

    test <@ inputsOf state = set [ "types", asm.Location ] @>

[<Fact>]
let ``nothing outside the root, generated at runtime, or unnamed is noted`` () =
    let dynamicType =
        let asm =
            Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                Reflection.AssemblyName "Dyn",
                Reflection.Emit.AssemblyBuilderAccess.Run
            )

        asm.DefineDynamicModule("Dyn").DefineType("Dyn.T").CreateType()

    // A type parameter: no full name.
    let typeParameter = typedefof<option<_>>.GetGenericArguments().[0]

    let outside = recorderWithRoot (Path.Combine(Path.GetTempPath(), "elsewhere"))
    outside.EnterScope "T:outside"
    Reflect.NoteTypeWith(outside, typeof<Boxed>)
    Reflect.NoteAssemblyWith(outside, typeof<Boxed>.Assembly)
    test <@ inputsOf outside |> Set.isEmpty @>

    let inside = recorderWithRoot Fixtures.repoRoot
    inside.EnterScope "T:inside"
    Reflect.NoteTypeWith(inside, dynamicType)
    Reflect.NoteAssemblyWith(inside, dynamicType.Assembly)
    Reflect.NoteTypeWith(inside, typeParameter)
    Reflect.NoteTypeWith(inside, null)
    Reflect.NoteAssemblyWith(inside, null)
    test <@ inputsOf inside |> Set.isEmpty @>

    // A generic instance is noted as its definition.
    Reflect.NoteTypeWith(inside, typeof<Result<Boxed, string>>)
    let def = typedefof<Result<_, _>>
    test <@ inputsOf inside = set [ "type", def.Assembly.Location + "|" + def.FullName ] @>

    // No repository root, or no recorder at all: nothing is noted, nothing throws.
    let rootless = RecorderState(8, None, null)
    rootless.EnterScope "T:rootless"
    Reflect.NoteTypeWith(rootless, typeof<Boxed>)
    Reflect.NoteAssemblyWith(rootless, typeof<Boxed>.Assembly)
    test <@ inputsOf rootless |> Set.isEmpty @>
    Reflect.NoteTypeWith(null, typeof<Boxed>)
    Reflect.NoteAssemblyWith(null, typeof<Boxed>.Assembly)

[<Fact>]
let ``a mutant-type list names the types between its separators`` () =
    test <@ isNull (Runtime.mutantTypes " ; ; ") @>
    test <@ isNull (Runtime.mutantTypes null) @>
    test <@ Runtime.mutantTypes "A.B; ;C+D" |> Set.ofSeq = set [ "A.B"; "C+D" ] @>

[<Fact>]
let ``a file that is not an assembly has no type set`` () =
    let text = Path.GetTempFileName()

    try
        File.WriteAllText(text, "not an assembly")
        test <@ TraceIngest.typeSetHash text = "absent" @>
    finally
        File.Delete text

[<Fact>]
let ``a PE file with no metadata has no type set`` () =
    // FxLib.dll with its CLI header's data-directory entry zeroed: still a PE file, no metadata.
    let copy = Path.GetTempFileName()

    try
        let bytes = File.ReadAllBytes fxLib
        let pe = BitConverter.ToInt32(bytes, 0x3C)
        let optional = pe + 24
        let pe32Plus = BitConverter.ToUInt16(bytes, optional) = 0x20bus
        let directories = optional + (if pe32Plus then 112 else 96)
        let cli = directories + 14 * 8
        Array.fill bytes cli 8 0uy
        File.WriteAllBytes(copy, bytes)
        test <@ TraceIngest.typeSetHash copy = "absent" @>
    finally
        File.Delete copy
