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
open Mono.Cecil.Rocks
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

/// Weave a scratch FxLib after `edit` has changed it, and hand `check` the weave's result and
/// the woven FxLib's path.
let private weaveEdited (edit: ModuleDefinition -> unit) (check: Weaver.WeaveResult -> string -> unit) =
    let dir = WeaverTests.copyFixture Fixtures.fxLibDir

    try
        let path = Path.Combine(dir, "FxLib.dll")

        do
            use asm =
                AssemblyDefinition.ReadAssembly(path, ReaderParameters(ReadWrite = true, ReadSymbols = true))

            edit asm.MainModule
            asm.Write(WriterParameters(WriteSymbols = true))

        let result =
            Weaver.weave [ SiteProbes.pass () ] [ { Path = path; Mode = Full } ] (Path.Combine(dir, "woven"))
            |> Result.defaultWith (fun e -> failwith $"%A{e}")

        check result (Path.Combine(dir, "woven", "FxLib.dll"))
    finally
        WeaverTests.deleteScratch dir

let private methodOf (m: ModuleDefinition) typeName name =
    (m.GetType typeName).Methods |> Seq.find (fun x -> x.Name = name)

let private syntheticType (m: ModuleDefinition) name =
    let t =
        TypeDefinition(
            "FxLib",
            name,
            TypeAttributes.Public
            ||| TypeAttributes.Abstract
            ||| TypeAttributes.Sealed
            ||| TypeAttributes.BeforeFieldInit,
            m.TypeSystem.Object
        )

    m.Types.Add t
    t

/// `isCircleOnly` laid out as SDK 10.0.3xx's F# emits it: a hidden point between `match shape
/// with`'s visible point and its `isinst`, added when the compiler did not emit one. The
/// conditional branch that follows the `isinst`.
let private hideMatchTest (meth: MethodDefinition) =
    let typeTest =
        meth.Body.Instructions |> Seq.find (fun i -> i.OpCode = OpCodes.Isinst)

    let points = meth.DebugInformation.SequencePoints
    let line = points |> Seq.find (fun p -> not p.IsHidden)

    if not (points |> Seq.exists (fun p -> p.Offset = typeTest.Previous.Offset)) then
        points.Insert(
            points.IndexOf line + 1,
            SequencePoint(typeTest.Previous, line.Document, StartLine = 0xfeefee, EndLine = 0xfeefee)
        )

    typeTest.Next

/// SDK 10.0.3xx's F# put a hidden sequence point between `match shape with`'s visible point and
/// its `isinst`; 10.0.4xx does not. Under that hidden point, the probe after the `isinst` would
/// hide the line's branch from MS CodeCoverage, so the weave gives the branch a copy of the line.
/// The hidden point is added here when the compiler did not emit it, so the layout is the same
/// on every SDK.
[<Fact>]
let ``a type test under a hidden point after its line gets the line's point again past its probe`` () =
    weaveEdited (fun m -> methodOf m "FxLib.Logic" "isCircleOnly" |> hideMatchTest |> ignore) (fun _ path ->
        use woven =
            AssemblyDefinition.ReadAssembly(path, ReaderParameters(ReadSymbols = true))

        let meth = methodOf woven.MainModule "FxLib.Logic" "isCircleOnly"

        let branch =
            meth.Body.Instructions
            |> Seq.find (fun i -> i.OpCode.FlowControl = FlowControl.Cond_Branch)

        let probe = branch.Previous.Operand :?> MethodReference
        let point = meth.DebugInformation.GetSequencePoint branch

        test <@ probe.Name = Contract.HitIfNotNull @>
        test <@ not (isNull point) && not point.IsHidden && point.StartLine = 11 @>)

/// Where no point follows the prefix's last branch, the weave closes the copied line's range
/// with a hidden point there, so the code after the branch is not counted as the line's.
/// Both SDKs put a point after `isCircleOnly`'s branch; it is removed here.
[<Fact>]
let ``a copied line's range is closed by a hidden point after its last branch when none is there`` () =
    weaveEdited
        (fun m ->
            let meth = methodOf m "FxLib.Logic" "isCircleOnly"
            let branch = hideMatchTest meth
            let points = meth.DebugInformation.SequencePoints

            points
            |> Seq.tryFind (fun p -> p.Offset = branch.Next.Offset)
            |> Option.iter (points.Remove >> ignore))
        (fun _ path ->
            use woven =
                AssemblyDefinition.ReadAssembly(path, ReaderParameters(ReadSymbols = true))

            let meth = methodOf woven.MainModule "FxLib.Logic" "isCircleOnly"

            let branch =
                meth.Body.Instructions
                |> Seq.find (fun i -> i.OpCode.FlowControl = FlowControl.Cond_Branch)

            let copy = meth.DebugInformation.GetSequencePoint branch
            let closing = meth.DebugInformation.GetSequencePoint branch.Next

            test <@ not (isNull copy) && copy.StartLine = 11 @>
            test <@ not (isNull closing) && closing.IsHidden @>)

/// Another compiler can give a nullary case of a union with fields its own class, `_X`; a type
/// test on that class records the case.
[<Fact>]
let ``a type test on a nullary case's own class records that case`` () =
    weaveEdited
        (fun m ->
            let union = m.GetType "FxLib.Color5"

            let magenta =
                TypeDefinition("", "_Magenta", TypeAttributes.NestedAssembly ||| TypeAttributes.Class, union)

            union.NestedTypes.Add magenta

            let isMagenta =
                MethodDefinition("IsMagenta", MethodAttributes.Public ||| MethodAttributes.Static, m.TypeSystem.Object)

            isMagenta.Parameters.Add(ParameterDefinition("o", ParameterAttributes.None, m.TypeSystem.Object))
            let il = isMagenta.Body.GetILProcessor()
            il.Emit OpCodes.Ldarg_0
            il.Emit(OpCodes.Isinst, magenta)
            il.Emit OpCodes.Ret
            (syntheticType m "SyntheticCase").Methods.Add isMagenta)
        (fun result path ->
            use woven = AssemblyDefinition.ReadAssembly path

            let body = (methodOf woven.MainModule "FxLib.SyntheticCase" "IsMagenta").Body
            // The long forms: the writer shortened the probe's `ldc.i4` to `ldc.i4.<id>`.
            body.SimplifyMacros()
            let typeTest = body.Instructions |> Seq.find (fun i -> i.OpCode = OpCodes.Isinst)

            let magenta =
                result.Manifest.Rows
                |> Array.find (fun r -> r.Kind = UnionCase && r.TypeName = "FxLib.Color5" && r.Member = "Magenta")

            let probe = [ typeTest.Next; typeTest.Next.Next; typeTest.Next.Next.Next ]

            test <@ probe |> List.map (fun i -> i.OpCode) = [ OpCodes.Dup; OpCodes.Ldc_I4; OpCodes.Call ] @>
            test <@ probe.[1].Operand = box magenta.Id @>
            test <@ (probe.[2].Operand :?> MethodReference).Name = Contract.HitIfNotNull @>)

/// A type initializer's handler that ends inside the method keeps its end when the initializer
/// is wrapped; only a handler that ran to the method's end is closed at the new finally.
[<Fact>]
let ``a type initializer whose handler ends inside the method is wrapped and still valid`` () =
    weaveEdited
        (fun m ->
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

            // try { Values.get_threshold() } catch { } — then return.
            let il = cctor.Body.GetILProcessor()
            let tryStart = il.Create(OpCodes.Call, methodOf m "FxLib.Values" "get_threshold")
            let handlerStart = il.Create OpCodes.Pop
            let finish = il.Create OpCodes.Ret
            il.Append tryStart
            il.Emit OpCodes.Pop
            il.Emit(OpCodes.Leave, finish)
            il.Append handlerStart
            il.Emit(OpCodes.Leave, finish)
            il.Append finish

            cctor.Body.ExceptionHandlers.Add(
                ExceptionHandler(
                    ExceptionHandlerType.Catch,
                    CatchType = m.ImportReference typeof<Exception>,
                    TryStart = tryStart,
                    TryEnd = handlerStart,
                    HandlerStart = handlerStart,
                    HandlerEnd = finish
                )
            )

            (syntheticType m "SyntheticCaught").Methods.Add cctor)
        (fun result path ->
            let ctx = AssemblyLoadContext("site-probe-caught", true)
            let state = RecorderState(result.Manifest.IdCount, None, null)
            let saved = Runtime.state
            Runtime.state <- state

            try
                let asm = ctx.LoadFromStream(new MemoryStream(File.ReadAllBytes path))
                RuntimeHelpers.RunClassConstructor(asm.GetType("FxLib.SyntheticCaught").TypeHandle)
            finally
                Runtime.state <- saved
                ctx.Unload()

            let threshold =
                result.Manifest.Rows
                |> Array.find (fun r ->
                    r.Kind = UserMethod && r.TypeName = "FxLib.Values" && r.Member = "get_threshold")

            test <@ (idsIn state "S:static-init").Contains threshold.Id @>)
