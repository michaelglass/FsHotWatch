/// Weaver behaviour on assembly shapes the fixture's compiler does not emit, made by editing
/// a scratch copy of FxLib with Cecil before weaving it.
module TestPrune.Trace.Tests.WeaverShapeTests

open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open Xunit
open Swensen.Unquote
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Weaver
open TestPrune.Trace.Tests

/// Edit a scratch copy of FxLib, weave it in `mode` with `passes`, and hand `check` the
/// scratch dir and the weave's result.
let private weaveEdited
    (edit: ModuleDefinition -> unit)
    (mode: WeaveMode)
    (passes: IWeavePass list)
    (check: string -> Result<WeaveResult, WeaveError> -> unit)
    =
    let dir = WeaverTests.copyFixture Fixtures.fxLibDir

    try
        let path = Path.Combine(dir, "FxLib.dll")

        do
            use asm =
                AssemblyDefinition.ReadAssembly(path, ReaderParameters(ReadWrite = true, ReadSymbols = true))

            edit asm.MainModule
            asm.Write(WriterParameters(WriteSymbols = true))

        check dir (weave passes [ { Path = path; Mode = mode } ] (Path.Combine(dir, "woven")))
    finally
        WeaverTests.deleteScratch dir

let private logicMethod (m: ModuleDefinition) name =
    m.GetType("FxLib.Logic").Methods |> Seq.find (fun x -> x.Name = name)

/// The root local-scope start offset of each named FxLib.Logic method in a woven PDB
/// (None for a method with no scope). Everything is read before the readers are disposed.
let private rootScopeStarts (dir: string) (names: string list) =
    use fs = File.OpenRead(Path.Combine(dir, "woven", "FxLib.pdb"))
    use provider = MetadataReaderProvider.FromPortablePdbStream fs
    let reader = provider.GetMetadataReader()
    use asm = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "woven", "FxLib.dll"))

    names
    |> List.map (fun name ->
        let token = (logicMethod asm.MainModule name).MetadataToken.ToInt32()
        let def = MetadataTokens.MethodDefinitionHandle(token &&& 0xffffff)

        name,
        reader.GetLocalScopes def
        |> Seq.map (fun h -> (reader.GetLocalScope h).StartOffset)
        |> Seq.tryHead)
    |> Map.ofList

[<Fact>]
let ``a root scope that did not start at the head is left where it was`` () =
    weaveEdited
        (fun m ->
            (logicMethod m "area").DebugInformation.Scope <- null
            let sumPoint = logicMethod m "sumPoint"
            sumPoint.DebugInformation.Scope.Start <- InstructionOffset(sumPoint.Body.Instructions.[1]))
        Full
        []
        (fun dir r ->
            test <@ Result.isOk r @>
            PdbCheck.decodeFailures (Path.Combine(dir, "woven", "FxLib.pdb")) =! []
            let start = rootScopeStarts dir [ "sumPoint"; "area"; "addConst" ]
            // Still on the method's second original instruction, now after the probe.
            test <@ start.["sumPoint"] |> Option.exists (fun s -> s > 0) @>
            // The probe did not invent a scope for a method that had none.
            test <@ start.["area"] = None @>
            // An unedited method's scope still moved to the head, over its probe.
            test <@ start.["addConst"] = Some 0 @>)

[<Fact>]
let ``a pass that leaves the sequence points out of order is refused as a corrupt PDB`` () =
    let corrupt =
        { new IWeavePass with
            member _.Prepare _ = ()

            member _.Rewrite(_, _, _, meth) =
                if meth.Name = "area" && meth.DeclaringType.Name = "Logic" then
                    let sps = meth.DebugInformation.SequencePoints
                    let doc = sps.[0].Document

                    let stray =
                        unboundSequencePointCtor.Value.Invoke [| box 1; box doc |] :?> SequencePoint

                    stray.StartLine <- 1
                    stray.EndLine <- 1
                    stray.StartColumn <- 1
                    stray.EndColumn <- 2
                    sps.Add stray
                    true
                else
                    false }

    weaveEdited ignore Full [ corrupt ] (fun _ r ->
        test
            <@
                match r with
                | Error(PdbCorrupt("FxLib", n)) -> n > 0
                | _ -> false
            @>)

[<Fact>]
let ``an attribute whose type cannot be resolved marks a test by its name`` () =
    let attribute (m: ModuleDefinition) (name: string) =
        let missing = AssemblyNameReference("NoSuchAssembly", Version(1, 0, 0, 0))
        m.AssemblyReferences.Add missing
        let t = TypeReference("Missing", name, m, missing)
        CustomAttribute(MethodReference(".ctor", m.TypeSystem.Void, t, HasThis = true))

    weaveEdited
        (fun m ->
            (logicMethod m "area").CustomAttributes.Add(attribute m "TheoryAttribute")
            (logicMethod m "sumPoint").CustomAttributes.Add(attribute m "FactAttribute")
            (logicMethod m "isCircleOnly").CustomAttributes.Add(attribute m "SkipAttribute"))
        SitesOnly
        []
        (fun _ r ->
            let probed =
                match r with
                | Ok w -> w.Manifest.Rows |> Array.map (fun x -> x.Member) |> Set.ofArray
                | Error e -> failwith $"%A{e}"

            test <@ probed = set [ "area"; "sumPoint" ] @>)

[<Fact>]
let ``a type marked compiler-generated, and a type nested in a generated one, hold generated methods`` () =
    weaveEdited
        (fun m ->
            let greeter = m.GetType "FxLib.Greeter"

            greeter.CustomAttributes.Add(
                CustomAttribute(
                    m.ImportReference(
                        typeof<System.Runtime.CompilerServices.CompilerGeneratedAttribute>.GetConstructor
                            Type.EmptyTypes
                    )
                )
            )

            let closure =
                m.GetTypes() |> Seq.find (fun t -> t.Name.Contains '@' && t.Methods.Count > 0)

            let inner =
                TypeDefinition("", "Inner", TypeAttributes.NestedPublic ||| TypeAttributes.Class, m.TypeSystem.Object)

            closure.NestedTypes.Add inner

            let run =
                MethodDefinition("Run", MethodAttributes.Public ||| MethodAttributes.Static, m.TypeSystem.Void)

            run.Body.GetILProcessor().Emit OpCodes.Ret
            inner.Methods.Add run)
        Full
        []
        (fun _ r ->
            let kinds =
                match r with
                | Ok w ->
                    w.Manifest.Rows
                    |> Array.map (fun x -> (x.TypeName, x.Member), x.Kind)
                    |> Map.ofArray
                | Error e -> failwith $"%A{e}"

            test
                <@
                    kinds
                    |> Map.exists (fun (t, mem) k -> t = "FxLib.Greeter" && mem = ".ctor" && k = GeneratedMethod)
                @>

            test
                <@
                    kinds
                    |> Map.exists (fun (t, mem) k -> t.EndsWith "+Inner" && mem = "Run" && k = GeneratedMethod)
                @>

            test <@ kinds.[("FxLib.Logic", "area")] = UserMethod @>)

[<Fact>]
let ``the pinned Cecil has the SequencePoint constructor the end-of-method fix needs`` () =
    test <@ not (isNull unboundSequencePointCtor.Value) @>
