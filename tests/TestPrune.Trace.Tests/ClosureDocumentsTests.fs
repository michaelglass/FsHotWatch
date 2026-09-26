/// `ClosureDocuments` over modules built in memory, for the creator shapes the fixture's
/// compiler does not emit on its own.
module TestPrune.Trace.Tests.ClosureDocumentsTests

open Xunit
open Swensen.Unquote
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace

let private startup = "<StartupCode$M>"

let private newModule () =
    ModuleDefinition.CreateModule("M", ModuleKind.Dll)

let private addType (m: ModuleDefinition) (ns: string) (name: string) =
    let t = TypeDefinition(ns, name, TypeAttributes.Class, m.TypeSystem.Object)
    m.Types.Add t
    t

let private addNested (outer: TypeDefinition) (name: string) =
    let t =
        TypeDefinition("", name, TypeAttributes.NestedPublic ||| TypeAttributes.Class, outer.BaseType)

    outer.NestedTypes.Add t
    t

/// A constructor and a static instance field, the two ways a closure is created.
let private closure (outer: TypeDefinition) (name: string) =
    let m = outer.Module
    let t = addNested outer name

    let ctor =
        MethodDefinition(".ctor", MethodAttributes.Public ||| MethodAttributes.SpecialName, m.TypeSystem.Void)

    ctor.Body.GetILProcessor().Emit OpCodes.Ret
    t.Methods.Add ctor

    let instance =
        FieldDefinition("@_instance", FieldAttributes.Static ||| FieldAttributes.Public, t)

    t.Fields.Add instance
    t, ctor, instance

/// A method of `owner` whose body is `instructions`, with a sequence point per `lines`
/// entry (`0xfeefee` is hidden) in `document`.
let private methodOn (owner: TypeDefinition) (document: string) (lines: int list) (instructions: Instruction list) =
    let meth =
        MethodDefinition("run", MethodAttributes.Public ||| MethodAttributes.Static, owner.Module.TypeSystem.Void)

    let il = meth.Body.GetILProcessor()

    for i in instructions do
        il.Append i

    il.Emit OpCodes.Ret
    let doc = Document document

    for line in lines do
        let sp = SequencePoint(meth.Body.Instructions.[0], doc)
        sp.StartLine <- line
        sp.EndLine <- line
        meth.DebugInformation.SequencePoints.Add sp

    owner.Methods.Add meth
    meth

let private derive (m: ModuleDefinition) (t: TypeDefinition) =
    ClosureDocuments.Resolver(m).Derive t
    |> Option.map (fun (d, line) -> d.Url, line)

[<Fact>]
let ``a closure's line is the number after its name's at`` () =
    test <@ ClosureDocuments.lineOf "DoubleAll@12" = 12 @>
    test <@ ClosureDocuments.lineOf "clo@263-1" = 263 @>
    test <@ ClosureDocuments.lineOf "Pipe #2 stage #1 at line 389@389" = 389 @>
    test <@ ClosureDocuments.lineOf "$Values" = 0 @>

[<Fact>]
let ``a closure takes the document of the method that constructs it`` () =
    let m = newModule ()
    let file = addType m startup "$File"
    let target, ctor, _ = closure file "f@7"
    let user = addType m "N" "User"

    methodOn
        user
        "/r/File.fs"
        [ 7; 0xfeefee ]
        [ Instruction.Create(OpCodes.Newobj, ctor); Instruction.Create OpCodes.Pop ]
    |> ignore

    test <@ derive m target = Some("/r/File.fs", 7) @>

[<Fact>]
let ``a closure takes the document of the method that loads its cached instance`` () =
    let m = newModule ()
    let file = addType m startup "$File"
    let target, _, instance = closure file "g@9-1"
    let user = addType m "N" "User"

    methodOn user "/r/File.fs" [ 9 ] [ Instruction.Create(OpCodes.Ldsfld, instance); Instruction.Create OpCodes.Pop ]
    |> ignore

    test <@ derive m target = Some("/r/File.fs", 9) @>

[<Fact>]
let ``a creator without sequence points passes on its own creator's document`` () =
    let m = newModule ()
    let file = addType m startup "$File"
    let inner, innerCtor, _ = closure file "inner@4"
    let outer, outerCtor, _ = closure file "outer@3"
    // The outer closure creates the inner one and has no sequence points of its own.
    methodOn
        outer
        "/unused"
        []
        [ Instruction.Create(OpCodes.Newobj, innerCtor)
          Instruction.Create OpCodes.Pop ]
    |> ignore

    let user = addType m "N" "User"

    methodOn
        user
        "/r/File.fs"
        [ 3 ]
        [ Instruction.Create(OpCodes.Newobj, outerCtor)
          Instruction.Create OpCodes.Pop ]
    |> ignore

    test <@ derive m inner = Some("/r/File.fs", 4) @>

[<Fact>]
let ``nothing is derived without a single creator document`` () =
    let m = newModule ()
    let file = addType m startup "$File"
    let orphan, _, _ = closure file "orphan@1"
    let twoFiles, twoCtor, _ = closure file "two@2"
    let cyclic, _, cyclicInstance = closure file "cyclic@3"
    let user = addType m "N" "User"

    methodOn user "/r/A.fs" [ 2 ] [ Instruction.Create(OpCodes.Newobj, twoCtor); Instruction.Create OpCodes.Pop ]
    |> ignore

    methodOn user "/r/B.fs" [ 2 ] [ Instruction.Create(OpCodes.Newobj, twoCtor); Instruction.Create OpCodes.Pop ]
    |> ignore
    // Only created by itself (its type initializer caches its instance).
    methodOn
        cyclic
        "/unused"
        []
        [ Instruction.Create(OpCodes.Ldsfld, cyclicInstance)
          Instruction.Create OpCodes.Pop ]
    |> ignore

    // Created only by an ordinary method that has no sequence points.
    let unplaced, unplacedCtor, _ = closure file "unplaced@4"

    methodOn
        user
        "/unused"
        []
        [ Instruction.Create(OpCodes.Newobj, unplacedCtor)
          Instruction.Create OpCodes.Pop ]
    |> ignore

    // A method with no body, and one that creates an ordinary type, create no closure.
    user.Methods.Add(
        MethodDefinition(
            "abstractOne",
            MethodAttributes.Public
            ||| MethodAttributes.Abstract
            ||| MethodAttributes.Virtual,
            m.TypeSystem.Void
        )
    )

    let ordinary = addType m "N" "Ordinary"
    let ordinaryCtor = MethodReference(".ctor", m.TypeSystem.Void, ordinary)
    ordinaryCtor.HasThis <- true

    methodOn
        user
        "/r/A.fs"
        [ 5 ]
        [ Instruction.Create(OpCodes.Newobj, ordinaryCtor)
          Instruction.Create OpCodes.Pop ]
    |> ignore

    test <@ derive m orphan = None @>
    test <@ derive m twoFiles = None @>
    test <@ derive m cyclic = None @>
    test <@ derive m unplaced = None @>
    // An ordinary type is never derived, even with a creator.
    test <@ derive m ordinary = None @>
