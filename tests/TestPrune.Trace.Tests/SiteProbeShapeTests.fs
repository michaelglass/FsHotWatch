/// Site probes on IL shapes the fixture's Debug F# build does not emit (another compiler, or
/// a Release build, can): FSharp.Core's type-test intrinsic at a concrete product type, and a
/// type initializer whose last handler runs to the end of the method. Each is added to a
/// scratch FxLib with Cecil, woven, and run in this process under an installed recorder, so
/// these tests share the non-parallel collection of the others that swap the recorder.
[<Xunit.Collection("recorder-counters")>]
module TestPrune.Trace.Tests.SiteProbeShapeTests

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Runtime.Loader
open Xunit
open Swensen.Unquote
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder
open TestPrune.Trace.Tests

let private intrinsics =
    typeof<list<int>>.Assembly.GetType "Microsoft.FSharp.Core.LanguagePrimitives+IntrinsicFunctions"

/// `static class FxLib.Synthetic` with `bool IsDog(object)` (a TypeTestGeneric<Dog> call) and
/// a type initializer `try { Values.get_threshold(); throw } catch { rethrow }`: no `ret`, so
/// its handler ends at the end of the method.
let private addSynthetic (m: ModuleDefinition) =
    let t =
        TypeDefinition(
            "FxLib",
            "Synthetic",
            TypeAttributes.Public
            ||| TypeAttributes.Abstract
            ||| TypeAttributes.Sealed
            ||| TypeAttributes.BeforeFieldInit,
            m.TypeSystem.Object
        )

    m.Types.Add t

    let isDog =
        MethodDefinition("IsDog", MethodAttributes.Public ||| MethodAttributes.Static, m.TypeSystem.Boolean)

    isDog.Parameters.Add(ParameterDefinition("o", ParameterAttributes.None, m.TypeSystem.Object))

    let typeTest =
        GenericInstanceMethod(m.ImportReference(intrinsics.GetMethod "TypeTestGeneric"))

    typeTest.GenericArguments.Add(m.GetType "FxLib.Dog")
    let il = isDog.Body.GetILProcessor()
    il.Emit OpCodes.Ldarg_0
    il.Emit(OpCodes.Call, typeTest)
    il.Emit OpCodes.Ret
    t.Methods.Add isDog

    // An unbox to a type that is not a product type: no probe.
    let unboxInt =
        MethodDefinition("UnboxInt", MethodAttributes.Public ||| MethodAttributes.Static, m.TypeSystem.Int32)

    unboxInt.Parameters.Add(ParameterDefinition("o", ParameterAttributes.None, m.TypeSystem.Object))

    let unbox =
        GenericInstanceMethod(m.ImportReference(intrinsics.GetMethod "UnboxGeneric"))

    unbox.GenericArguments.Add m.TypeSystem.Int32
    let il = unboxInt.Body.GetILProcessor()
    il.Emit OpCodes.Ldarg_0
    il.Emit(OpCodes.Call, unbox)
    il.Emit OpCodes.Ret
    t.Methods.Add unboxInt

    let cctor =
        MethodDefinition(
            ".cctor",
            MethodAttributes.Private
            ||| MethodAttributes.Static
            ||| MethodAttributes.SpecialName
            ||| MethodAttributes.RTSpecialName
            ||| MethodAttributes.HideBySig,
            m.TypeSystem.Void
        )

    let il = cctor.Body.GetILProcessor()

    let threshold =
        m.GetType("FxLib.Values").Methods
        |> Seq.find (fun x -> x.Name = "get_threshold")

    let tryStart = il.Create(OpCodes.Call, threshold)
    let handlerStart = il.Create OpCodes.Pop
    il.Append tryStart
    il.Emit OpCodes.Pop

    il.Emit(OpCodes.Newobj, m.ImportReference(typeof<InvalidOperationException>.GetConstructor Type.EmptyTypes))

    il.Emit OpCodes.Throw
    il.Append handlerStart
    il.Emit OpCodes.Rethrow

    cctor.Body.ExceptionHandlers.Add(
        ExceptionHandler(
            ExceptionHandlerType.Catch,
            CatchType = m.ImportReference typeof<Exception>,
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = null
        )
    )

    t.Methods.Add cctor

type private Woven =
    { Result: Weaver.WeaveResult
      FxLib: byte[] }

let private woven =
    lazy
        (let dir = WeaverTests.copyFixture Fixtures.fxLibDir

         try
             let path = Path.Combine(dir, "FxLib.dll")

             do
                 use asm =
                     AssemblyDefinition.ReadAssembly(path, ReaderParameters(ReadWrite = true, ReadSymbols = true))

                 addSynthetic asm.MainModule
                 asm.Write(WriterParameters(WriteSymbols = true))

             let r =
                 Weaver.weave [ SiteProbes.pass () ] [ { Path = path; Mode = Full } ] (Path.Combine(dir, "woven"))
                 |> Result.defaultWith (fun e -> failwith $"%A{e}")

             { Result = r
               FxLib = File.ReadAllBytes(Path.Combine(dir, "woven", "FxLib.dll")) }
         finally
             WeaverTests.deleteScratch dir)

/// Load the woven FxLib into a fresh collectible context, run `f` on it under a recorder
/// sized for the weave, and return that recorder.
let private runWoven (f: Assembly -> unit) =
    let w = woven.Value
    let ctx = AssemblyLoadContext("site-probe-shapes", true)
    let state = RecorderState(w.Result.Manifest.IdCount, None, null)
    let saved = Runtime.state
    Runtime.state <- state

    try
        f (ctx.LoadFromStream(new MemoryStream(w.FxLib)))
    finally
        Runtime.state <- saved
        ctx.Unload()

    state

let private idOf kind typeName mem =
    woven.Value.Result.Manifest.Rows
    |> Array.find (fun r -> r.Kind = kind && r.TypeName = typeName && r.Member = mem)
    |> fun r -> r.Id

let private idsIn (state: RecorderState) key =
    state.Scopes
    |> Seq.tryFind (fun s -> s.Key = key)
    |> Option.map (fun s -> s.Ids() |> Set.ofArray)
    |> Option.defaultValue Set.empty

[<Fact>]
let ``a type-test intrinsic records its type only when the test succeeds`` () =
    let state =
        runWoven (fun asm ->
            let isDog = asm.GetType("FxLib.Synthetic").GetMethod "IsDog"
            Scopes.Enter "T:warm"
            let dog = Activator.CreateInstance(asm.GetType "FxLib.Dog")
            let cat = Activator.CreateInstance(asm.GetType "FxLib.Cat")

            for name, value, expected in [ "T:dog", dog, true; "T:cat", cat, false ] do
                Scopes.Enter name
                test <@ isDog.Invoke(null, [| value |]) = box expected @>

            Scopes.Exit())

    let dogType = idOf TypeUse "FxLib.Dog" ""
    test <@ (idsIn state "T:dog").Contains dogType @>
    test <@ not ((idsIn state "T:cat").Contains dogType) @>

[<Fact>]
let ``an unbox to a type with no row gets no probe`` () =
    let state =
        runWoven (fun asm ->
            Scopes.Enter "T:unbox"
            test <@ asm.GetType("FxLib.Synthetic").GetMethod("UnboxInt").Invoke(null, [| box 5 |]) = box 5 @>
            Scopes.Exit())

    // Only the method's own entry probe.
    test <@ idsIn state "T:unbox" = set [ idOf UserMethod "FxLib.Synthetic" "UnboxInt" ] @>

[<Fact>]
let ``a type initializer whose handler runs to the method's end is wrapped and still valid`` () =
    let state =
        runWoven (fun asm ->
            Scopes.Enter "T:trigger"

            let thrown =
                try
                    RuntimeHelpers.RunClassConstructor(asm.GetType("FxLib.Synthetic").TypeHandle)
                    None
                with :? TypeInitializationException as e ->
                    Some(e.InnerException.GetType())

            // The initializer's own exception, not an InvalidProgramException from the wrap.
            test <@ thrown = Some typeof<InvalidOperationException> @>
            // The finally left the static-init scope: this hit is the test's again.
            Probes.Hit(idOf UserMethod "FxLib.Logic" "area")
            Scopes.Exit())

    test <@ (idsIn state "S:static-init").Contains(idOf UserMethod "FxLib.Values" "get_threshold") @>
    test <@ (idsIn state "T:trigger").Contains(idOf UserMethod "FxLib.Logic" "area") @>
    test <@ not ((idsIn state "T:trigger").Contains(idOf UserMethod "FxLib.Values" "get_threshold")) @>
