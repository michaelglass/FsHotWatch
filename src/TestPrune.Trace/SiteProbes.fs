/// Site probes: record which union CASES and which product TYPES a method's code actually
/// touched, not just which methods it entered. F# pattern matches compile to `get_Tag` plus
/// a switch, to `isinst` on case classes, or to `castclass`/`ldfld` on a case class; type
/// tests compile to `isinst`/`castclass`/`unbox.any` or FSharp.Core intrinsics. A type
/// initializer is wrapped, in either weave mode, so what it runs is recorded in its own
/// static-init scope.
module TestPrune.Trace.SiteProbes

open System
open System.Collections.Generic
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder
open TestPrune.Trace.Weaver

/// F# `SourceConstructFlags` (the low five bits of `CompilationMappingAttribute`'s first argument).
[<Literal>]
let private SumType = 1

[<Literal>]
let private ModuleKind = 7

[<Literal>]
let private UnionCaseKind = 8

/// `(flags, index)` from an F# `CompilationMappingAttribute`; index is -1 when absent.
let internal compilationMapping (p: ICustomAttributeProvider) : (int * int) option =
    p.CustomAttributes
    |> Seq.tryFind (fun a -> a.AttributeType.Name = "CompilationMappingAttribute")
    |> Option.map (fun a ->
        let args = a.ConstructorArguments
        let flags = Convert.ToInt32 args.[0].Value &&& 31

        flags,
        (if args.Count >= 2 then
             Convert.ToInt32 args.[1].Value
         else
             -1))

let private flagsOf (p: ICustomAttributeProvider) =
    compilationMapping p |> Option.map fst |> Option.defaultValue 0

/// The union's Tag PROPERTY getter: instance, returning int32. A union may also have a
/// nullary case literally named `Tag`, whose singleton getter is a STATIC `get_Tag`
/// returning the union; probing that one would feed an object to `HitTag(int)`.
let internal isTagGetter (m: MethodDefinition) =
    m.Name = "get_Tag"
    && not m.IsStatic
    && m.ReturnType.MetadataType = MetadataType.Int32

/// The assembly that defines `t` (a reference's scope, or the referencing module's own
/// assembly for a reference to a type in the same module).
let private definingAssembly (t: TypeReference) =
    match t.Scope with
    | :? AssemblyNameReference as a -> a.Name
    | _ -> t.Module.Assembly.Name.Name

/// A type identity that survives generic instantiation and cross-assembly references.
let internal typeKey (t: TypeReference) =
    let e = t.GetElementType()
    definingAssembly e + "|" + e.FullName

let private fieldKey (f: FieldReference) = typeKey f.DeclaringType + "::" + f.Name

let private clrName (t: TypeDefinition) = t.FullName.Replace('/', '+')

let private isGeneratedName (t: TypeDefinition) =
    t.Name.Contains '@' || t.FullName.StartsWith "<"

/// Records and classes, given a type that is not a union: every type that is not a module,
/// an interface, an enum (enum values are inlined at their uses), a closure or startup
/// class, or a class the compiler nests in a union (case classes, `Tags`, debug proxies).
let private isProductType (t: TypeDefinition) =
    flagsOf t <> ModuleKind
    && not t.IsInterface
    && not t.IsEnum
    && not (isGeneratedName t)
    && (isNull t.DeclaringType || flagsOf t.DeclaringType <> SumType)

/// A union's cases in tag order, as (name, constructing method): `NewX`, or the nullary
/// case's singleton getter `get_X`. Those carry `CompilationMapping(UnionCase, tag)`.
let private casesOf (union: TypeDefinition) =
    let byTag =
        union.Methods
        |> Seq.choose (fun m -> compilationMapping m |> Option.map (fun (flags, tag) -> flags, tag, m))
        |> Seq.filter (fun (flags, _, _) -> flags = UnionCaseKind)
        |> Seq.map (fun (_, tag, m) -> tag, (m.Name.Substring(if m.IsGetter then 4 else 3), m))
        |> Map.ofSeq

    // Every case has a constructor, so the tags are exactly 0..n-1. A gap cannot happen;
    // were it to, the lookup throws and the weave is refused rather than shifting ids.
    Seq.init byTag.Count (fun tag -> byTag.[tag]) |> Seq.toArray

/// Probe ids for one weave, built by `Prepare` from the full-mode (product) modules.
type private Catalogue() =
    /// typeKey of a case class -> its case id; typeKey of a product type -> its type id.
    member val TypeIds = Dictionary<string, int>()
    /// fieldKey of a union's own tag field -> the union's base id.
    member val TagFields = Dictionary<string, int>()
    /// fieldKey of a nullary case's singleton field (`_unique_X`) -> its case id.
    member val Singletons = Dictionary<string, int>()
    /// Each union's instance tag getter -> the union's base id.
    member val TagGetters = Dictionary<MethodDefinition, int>(HashIdentity.Reference)

/// The recorder methods a module's probes call.
type private ProbeRefs =
    { Hit: MethodReference
      HitIfNotNull: MethodReference
      HitIfTrue: MethodReference
      HitTag: MethodReference
      EnterStatic: MethodReference
      ExitStatic: MethodReference }

/// FSharp.Core's generic unbox and type-test intrinsics -> whether it is a type test
/// (recorded only when it returns true).
let private intrinsics =
    let t = "Microsoft.FSharp.Core.LanguagePrimitives/IntrinsicFunctions::"

    dict
        [ t + "UnboxGeneric", false
          t + "UnboxFast", false
          t + "TypeTestGeneric", true
          t + "TypeTestFast", true ]

/// Flow of an instruction whose operand is one jump target (a `switch` holds several).
let private jumps = set [ FlowControl.Branch; FlowControl.Cond_Branch ]

/// Flow after which the next instruction is reached only by a jump.
let private transfers =
    set [ FlowControl.Branch; FlowControl.Return; FlowControl.Throw ]

/// The line number a PDB gives a hidden sequence point.
[<Literal>]
let private hiddenLine = 0xFEEFEE

type private Pass() =
    let cat = Catalogue()
    let refs = Dictionary<ModuleDefinition, ProbeRefs>(HashIdentity.Reference)

    let refsFor (set: WeaveSet) (m: ModuleDefinition) =
        match refs.TryGetValue m with
        | true, r -> r
        | _ ->
            let get = set.RecorderMethod m Contract.ProbesType

            let r =
                { Hit = get Contract.Hit
                  HitIfNotNull = get Contract.HitIfNotNull
                  HitIfTrue = get Contract.HitIfTrue
                  HitTag = get Contract.HitTag
                  EnterStatic = get Contract.EnterStatic
                  ExitStatic = get Contract.ExitStatic }

            refs.[m] <- r
            r

    let row (set: WeaveSet) (m: ModuleDefinition) kind (t: TypeDefinition) name =
        set.Alloc
            { Id = 0
              Kind = kind
              Assembly = m.Assembly.Name.Name
              TypeName = clrName t
              Member = name
              Document = None
              FirstLine = 0
              LastLine = 0 }

    let catalogueUnion set (m: ModuleDefinition) (union: TypeDefinition) =
        let cases = casesOf union
        let byName = Dictionary<string, int>()

        for name, ctor in cases do
            let id = row set m UnionCase union name
            byName.[name] <- id

            // A nullary case's getter reads its `_unique_X` singleton: found structurally,
            // like the tag field, so no field is taken for a singleton by its name alone.
            if ctor.IsGetter then
                for i in ctor.Body.Instructions do
                    if i.OpCode = OpCodes.Ldsfld then
                        cat.Singletons.[fieldKey (i.Operand :?> FieldReference)] <- id

        let baseId = byName.[fst cases.[0]]

        // The only types the compiler nests in a union are its case classes (named after
        // the case, or `_X` for a nullary case of a union that also has fields), `Tags`,
        // and `X@DebugTypeProxy`, whose names are no case's.
        for n in union.NestedTypes do
            match byName.TryGetValue(if n.Name.StartsWith "_" then n.Name.Substring 1 else n.Name) with
            | true, id -> cat.TypeIds.[typeKey n] <- id
            | _ -> ()

        // The tag field is the one the instance tag getter reads, never a field found by
        // name: a case field named `tag` has a backing field `_tag` too.
        for getter in union.Methods do
            if isTagGetter getter then
                cat.TagGetters.[getter] <- baseId

                for i in getter.Body.Instructions do
                    if i.OpCode = OpCodes.Ldfld then
                        cat.TagFields.[fieldKey (i.Operand :?> FieldReference)] <- baseId

    /// Insert `xs` after `at`, in order.
    let emitAfter (il: ILProcessor) (at: Instruction) (xs: Instruction list) =
        xs
        |> List.fold
            (fun (cur: Instruction) x ->
                il.InsertAfter(cur, x)
                x)
            at
        |> ignore

    /// Probes for one instruction, given the method's own type keys (uses of its own
    /// types are not recorded: a type's members touching its own representation say
    /// nothing about which cases or types a caller used).
    let probesFor (r: ProbeRefs) (il: ILProcessor) (meth: MethodDefinition) (own: Set<string>) (ins: Instruction) =
        let ldc (id: int) = il.Create(OpCodes.Ldc_I4, id)
        let call (mr: MethodReference) = il.Create(OpCodes.Call, mr)
        let op = ins.OpCode

        let siteId (t: TypeReference) =
            let key = typeKey t

            match cat.TypeIds.TryGetValue key with
            | true, id when not (own.Contains key) -> Some id
            | _ -> None

        if op = OpCodes.Isinst then
            // Recorded only on success: a failed test says nothing about the value's case.
            siteId (ins.Operand :?> TypeReference)
            |> Option.map (fun id -> [ il.Create OpCodes.Dup; ldc id; call r.HitIfNotNull ])
        elif op = OpCodes.Castclass || op = OpCodes.Unbox_Any || op = OpCodes.Unbox then
            siteId (ins.Operand :?> TypeReference)
            |> Option.map (fun id -> [ ldc id; call r.Hit ])
        elif op = OpCodes.Ldfld || op = OpCodes.Ldflda then
            let f = ins.Operand :?> FieldReference

            match cat.TagFields.TryGetValue(fieldKey f) with
            // The tag getter's own read is recorded at its returns instead; an address of
            // the tag is not a tag value to pass to HitTag.
            | true, _ when isTagGetter meth || op = OpCodes.Ldflda -> None
            | true, baseId -> Some [ il.Create OpCodes.Dup; ldc baseId; call r.HitTag ]
            | _ -> siteId f.DeclaringType |> Option.map (fun id -> [ ldc id; call r.Hit ])
        elif op = OpCodes.Ldsfld || op = OpCodes.Ldsflda then
            let f = ins.Operand :?> FieldReference

            // Recorded wherever it is read, the case's own `get_X` included: Debug callers
            // always obtain a nullary case through that getter, so it is the read that fires.
            match cat.Singletons.TryGetValue(fieldKey f) with
            | true, id -> Some [ ldc id; call r.Hit ]
            | _ -> None
        else
            match ins.Operand with
            | :? GenericInstanceMethod as g ->
                match intrinsics.TryGetValue(g.DeclaringType.FullName + "::" + g.Name) with
                | true, isTest ->
                    siteId g.GenericArguments.[0]
                    |> Option.map (fun id ->
                        if isTest then
                            [ il.Create OpCodes.Dup; ldc id; call r.HitIfTrue ]
                        else
                            [ ldc id; call r.Hit ])
                | _ -> None
            | _ -> None

    /// The sequence points that keep a woven body's branch coverage the unwoven body's, as
    /// `(key, point)`: the original instruction offset the point belongs at. Planned on the
    /// unwoven body, since the probes' own calls change what is observed.
    ///
    /// MS CodeCoverage reports a conditional branch on a line when the sequence point
    /// governing it (the last one at or before it) is that line's visible point, or is
    /// hidden in the chain of hidden points after it, with no call in the visible point's
    /// range and none before the branch in the chain's first hidden range. F# puts a match's
    /// or comparison's test under such hidden points, so a probe's call there removes the
    /// line's branch points while the line stays hit.
    ///
    /// So where a hidden chain follows a visible point by falling through from it, and a
    /// probe in the chain's prefix (up to its first call, transfer or jump target) precedes
    /// a conditional branch of that prefix, the instruction the probe resumes at gets a copy
    /// of the visible point: the branch is governed by the line again, and the copy runs only
    /// when the line's code did. A nested pattern (a case whose field is itself tested, as a
    /// case or a literal) puts several probes in one prefix, each call ahead of later branches,
    /// so every probe followed by a branch before the next probe gets its own copy. The
    /// instruction after the prefix's last branch gets a hidden point, unless a point is
    /// already there: the last copy's range then holds no call, so the branches under the
    /// chain's later hidden points count as they did unwoven. Nothing else
    /// gets a point: a copy in a chain reached by a jump would mark the line hit when only the
    /// chain ran, and past a transfer or a target (a handler, a filter's `endfilter`, a
    /// match's join) coverage does not carry the line.
    ///
    /// The rule follows MS CodeCoverage's observed behavior, which is not documented.
    let branchPoints (meth: MethodDefinition) (sites: Instruction list) : (int * SequencePoint) list =
        let body = meth.Body
        let points = meth.DebugInformation.SequencePoints |> Seq.toArray
        let probed = HashSet<Instruction>(sites, HashIdentity.Reference)
        let at = body.Instructions |> Seq.map (fun i -> i.Offset, i) |> dict
        let isCall (i: Instruction) = i.OpCode.FlowControl = FlowControl.Call

        // The visible points' offsets, ending in a sentinel past every instruction.
        // Seq rather than Array: FSharp.Core inlines Array.map, loop branches included.
        let visibleOffsets =
            Seq.append
                (points |> Seq.filter (fun p -> not p.IsHidden) |> Seq.map (fun p -> p.Offset))
                [ Int32.MaxValue ]
            |> Seq.toArray

        let pointed = HashSet<int>(points |> Seq.map (fun p -> p.Offset))
        let targets = HashSet<Instruction>(HashIdentity.Reference)

        for i in body.Instructions do
            if i.OpCode.OperandType = OperandType.InlineSwitch then
                targets.UnionWith(i.Operand :?> Instruction array)
            elif jumps.Contains i.OpCode.FlowControl then
                targets.Add(i.Operand :?> Instruction) |> ignore

        let reachedOnlyByFallingIn (first: Instruction) =
            not (targets.Contains first || transfers.Contains first.Previous.OpCode.FlowControl)

        let startsNothingNew (i: Instruction) =
            not (
                isCall i.Previous
                || transfers.Contains i.Previous.OpCode.FlowControl
                || targets.Contains i
            )

        let copyAt (i: Instruction) (line: SequencePoint) =
            i.Offset,
            SequencePoint(
                i,
                line.Document,
                StartLine = line.StartLine,
                StartColumn = line.StartColumn,
                EndLine = line.EndLine,
                EndColumn = line.EndColumn
            )

        let hiddenAt (i: Instruction) (document: Document) =
            i.Offset, SequencePoint(i, document, StartLine = hiddenLine, EndLine = hiddenLine)

        let isBranch (i: Instruction) =
            i.OpCode.FlowControl = FlowControl.Cond_Branch

        // What follows each probed instruction of `xs`, up to and including the next one: the
        // instructions one probe's call precedes and the next probe's call does not.
        let rec runsAfterProbes (xs: Instruction list) =
            match List.skipWhile (probed.Contains >> not) xs with
            | [] -> []
            | _ :: after ->
                let run =
                    match List.tryFindIndex probed.Contains after with
                    | Some next -> List.take (next + 1) after
                    | None -> after

                run :: runsAfterProbes after

        points
        |> Array.pairwise
        |> Array.toList
        |> List.choose (fun (visible, hidden) ->
            // The end-of-method point is at no instruction and starts no chain.
            match at.TryGetValue hidden.Offset with
            | true, first when hidden.IsHidden && not visible.IsHidden && reachedOnlyByFallingIn first ->
                let chainEnd = visibleOffsets |> Array.find (fun o -> o > hidden.Offset)

                let prefix =
                    first
                    :: (first.Next
                        |> List.unfold (fun (i: Instruction) ->
                            if isNull i || i.Offset >= chainEnd then
                                None
                            else
                                Some(i, i.Next))
                        |> List.takeWhile startsNothingNew)
                    |> List.takeWhile (isCall >> not)

                match runsAfterProbes prefix |> List.filter (List.exists isBranch) with
                | [] -> None
                | branching ->
                    let lastBranch = List.last branching |> List.findBack isBranch

                    Some(
                        List.map (fun (run: Instruction list) -> copyAt run.Head visible) branching
                        @ (if pointed.Contains lastBranch.Next.Offset then
                               []
                           else
                               [ hiddenAt lastBranch.Next visible.Document ])
                    )
            | _ -> None)
        |> List.concat

    /// Merge `added` (from `branchPoints`) into the method's points, ordered by their
    /// original offsets as the portable PDB requires; `keys` holds those of the original points.
    let addPoints (meth: MethodDefinition) (keys: (int * SequencePoint) list) (added: (int * SequencePoint) list) =
        let merged = keys @ added |> List.sortBy fst
        let points = meth.DebugInformation.SequencePoints
        points.Clear()

        for _, p in merged do
            points.Add p

    /// Every return of a union's tag getter records `baseId + tag`: the case of the value
    /// every `get_Tag`+switch match inspects, including callers that are not woven.
    let probeTagReturns (r: ProbeRefs) (il: ILProcessor) (body: MethodBody) (baseId: int) =
        for ret in body.Instructions |> Seq.filter (fun i -> i.OpCode = OpCodes.Ret) |> Seq.toArray do
            // Turn the ret INTO the dup so branches that targeted it still land on it.
            ret.OpCode <- OpCodes.Dup
            ret.Operand <- null

            emitAfter
                il
                ret
                [ il.Create(OpCodes.Ldc_I4, baseId)
                  il.Create(OpCodes.Call, r.HitTag)
                  il.Create OpCodes.Ret ]

    /// Wrap a type initializer in `EnterStatic(); try { body } finally { ExitStatic() }`,
    /// so what runs once per process is never attributed to whichever test got there first.
    let wrapStaticInit (r: ProbeRefs) (il: ILProcessor) (body: MethodBody) =
        let first = body.Instructions.[0]

        let rets =
            body.Instructions |> Seq.filter (fun i -> i.OpCode = OpCodes.Ret) |> Seq.toArray

        let callExit = il.Create(OpCodes.Call, r.ExitStatic)
        let endRet = il.Create OpCodes.Ret

        // A handler that ran to the end of the method now ends where the finally starts.
        for h in body.ExceptionHandlers do
            if isNull h.HandlerEnd then
                h.HandlerEnd <- callExit

        il.Append callExit
        il.Append(il.Create OpCodes.Endfinally)
        il.Append endRet

        for ret in rets do
            ret.OpCode <- OpCodes.Leave
            ret.Operand <- endRet

        il.InsertBefore(first, il.Create(OpCodes.Call, r.EnterStatic))

        body.ExceptionHandlers.Add(
            ExceptionHandler(
                ExceptionHandlerType.Finally,
                TryStart = first,
                TryEnd = callExit,
                HandlerStart = callExit,
                HandlerEnd = endRet
            )
        )

    interface IWeavePass with
        member _.Prepare(set) =
            for m, mode in set.Modules do
                if mode = Full then
                    for t in m.GetTypes() |> Seq.toArray do
                        if flagsOf t = SumType then
                            catalogueUnion set m t
                        elif isProductType t then
                            cat.TypeIds.[typeKey t] <- row set m TypeUse t ""

        member _.Rewrite(set, m, _mode, meth) =
            let r = refsFor set m
            let body = meth.Body
            let il = body.GetILProcessor()

            let own =
                meth.DeclaringType
                |> Seq.unfold (fun t -> if isNull t then None else Some(typeKey t, t.DeclaringType))
                |> Set.ofSeq

            let sites =
                body.Instructions
                |> Seq.choose (fun ins -> probesFor r il meth own ins |> Option.map (fun xs -> ins, xs))
                |> Seq.toList

            // Planned on the unwoven body, keyed by original offsets, before any insertion.
            let keys =
                meth.DebugInformation.SequencePoints
                |> Seq.map (fun p -> p.Offset, p)
                |> Seq.toList

            let added = branchPoints meth (sites |> List.map fst)

            for ins, xs in sites do
                emitAfter il ins xs

            if not added.IsEmpty then
                addPoints meth keys added

            let tagBase =
                match cat.TagGetters.TryGetValue meth with
                | true, b -> Some b
                | _ -> None

            tagBase |> Option.iter (probeTagReturns r il body)
            // In both modes: a SitesOnly assembly's initializer has no manifest row to place
            // it by, so every test inherits its scope, whichever test triggered it.
            let isStaticInit = meth.IsConstructor && meth.IsStatic

            if isStaticInit then
                wrapStaticInit r il body

            not sites.IsEmpty || tagBase.IsSome || isStaticInit

/// A fresh site-probe pass for one weave (it holds that weave's catalogue).
let pass () : IWeavePass = Pass() :> IWeavePass
