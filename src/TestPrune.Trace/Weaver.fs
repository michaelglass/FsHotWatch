/// The Mono.Cecil weaver: reads a set of Debug assemblies with their portable PDBs, runs
/// the site passes, puts a method-entry probe (`ldc.i4 id; call Probes.Hit`) on every
/// method of a full-mode assembly and on every test method of a sites-only one, and
/// writes the woven copies with PDBs that System.Reflection.Metadata can decode.
module TestPrune.Trace.Weaver

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open Mono.Cecil
open Mono.Cecil.Cil
open Mono.Cecil.Rocks
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

/// One assembly to weave. Its PDB must sit next to it.
type WeaveInput = { Path: string; Mode: WeaveMode }

/// Why a weave was refused. A refused project runs untraced.
type WeaveError =
    /// The assembly is optimized (no DebuggableAttribute with DisableOptimizations).
    | Optimized of assembly: string
    /// The assembly has no portable PDB next to it.
    | MissingPdb of assembly: string
    /// A sequence point binds to no instruction and is not the end-of-method marker.
    | UnboundSequencePoint of assembly: string * methodName: string
    /// The woven PDB has methods System.Reflection.Metadata cannot decode.
    | PdbCorrupt of assembly: string * failures: int
    /// Cecil could not read or write the assembly.
    | CecilFailed of assembly: string * message: string

/// What a pass sees: every module in the weave, the probe-id allocator, and a way to
/// import a recorder method into a module (module, recorder type full name, method name).
type WeaveSet =
    { Modules: (ModuleDefinition * WeaveMode) list
      Alloc: ManifestRow -> int
      RecorderMethod: ModuleDefinition -> string -> string -> MethodReference }

/// A rewrite that runs on every method body before the entry probe is inserted. Bodies
/// are macro-simplified while a pass sees them.
type IWeavePass =
    /// Called once, after every module is read and before any body is rewritten.
    abstract Prepare: WeaveSet -> unit
    /// Rewrite one method body; return true when it changed.
    abstract Rewrite: WeaveSet * ModuleDefinition * WeaveMode * MethodDefinition -> bool

/// Counters from a weave.
type WeaveStats =
    {
        MethodsProbed: int
        /// End-of-method sequence points re-created at the woven body's end.
        EndOfMethodSequencePointsMoved: int
        /// Assembly name -> "<CLR type full name>::<method name>" of every method whose body changed.
        Touched: Map<string, string list>
    }

/// A successful weave: the manifest, the woven assembly paths, and counters.
type WeaveResult =
    { Manifest: Manifest
      Outputs: string list
      Stats: WeaveStats }

exception private WeaveFailure of WeaveError

/// Debug F# builds carry DebuggableAttribute with DisableOptimizations (0x100). Anything
/// else may have inlined small functions across assemblies, whose entry probes then
/// never fire: refuse rather than record a trace that silently misses them.
let isOptimized (asm: AssemblyDefinition) =
    match
        asm.CustomAttributes
        |> Seq.tryFind (fun a -> a.AttributeType.FullName = "System.Diagnostics.DebuggableAttribute")
    with
    | Some a when a.ConstructorArguments.Count = 1 -> (Convert.ToInt32(a.ConstructorArguments.[0].Value) &&& 0x100) = 0
    | Some a when a.ConstructorArguments.Count = 2 -> not (Convert.ToBoolean(a.ConstructorArguments.[1].Value))
    | _ -> true

let private hasAttr (name: string) (p: ICustomAttributeProvider) =
    p.HasCustomAttributes
    && p.CustomAttributes |> Seq.exists (fun a -> a.AttributeType.Name = name)

let rec private isGeneratedType (t: TypeDefinition) =
    not (isNull t)
    && (t.Name.Contains '@'
        || t.Name.StartsWith "<"
        || hasAttr "CompilerGeneratedAttribute" t
        || isGeneratedType t.DeclaringType)

let private kindOf (t: TypeDefinition) (m: MethodDefinition) =
    if m.IsConstructor && m.IsStatic then
        StaticCtor
    elif hasAttr "CompilerGeneratedAttribute" m || isGeneratedType t then
        GeneratedMethod
    else
        UserMethod

/// A test entry point: a method carrying xUnit's FactAttribute or anything derived
/// from it (TheoryAttribute, custom facts). Probing it puts the test's own symbol in
/// its trace, so a test-body edit invalidates the trace, and guarantees the test's
/// scope exists even when it runs no product code.
let private isTestMethod (m: MethodDefinition) =
    let rec fact (t: TypeReference) depth =
        if isNull t || depth > 8 then
            false
        elif t.FullName = "Xunit.FactAttribute" then
            true
        else
            let resolved =
                try
                    t.Resolve()
                with _ ->
                    null

            match resolved with
            | null -> t.Name = "FactAttribute" || t.Name = "TheoryAttribute"
            | d -> fact d.BaseType (depth + 1)

    m.HasCustomAttributes
    && m.CustomAttributes |> Seq.exists (fun a -> fact a.AttributeType 0)

/// A document's recorded hash as lowercase hex ("" when it has none: Cecil gives an
/// empty array, never null).
let private hex (bytes: byte[]) =
    Convert.ToHexString(bytes).ToLowerInvariant()

/// Cecil 0.11.6's public SequencePoint constructor binds to an instruction; a point at
/// a raw offset (the end-of-method marker) needs its internal (int, Document) one.
let internal unboundSequencePointCtor =
    lazy
        (typeof<SequencePoint>
            .GetConstructor(
                BindingFlags.NonPublic ||| BindingFlags.Instance,
                null,
                [| typeof<int>; typeof<Document> |],
                null
            ))

let private codeSize (body: MethodBody) =
    body.Instructions |> Seq.fold (fun size i -> size + i.GetSize()) 0

let private typeKey (t: TypeDefinition) = t.FullName.Replace('/', '+')

/// PDB INVARIANT, part 1. F# emits a hidden sequence point at IL offset == code size to
/// mark the end of a method. Cecil cannot bind it to an instruction and keeps its raw
/// offset, so after any insertion it would write the point back BEFORE its predecessor:
/// a negative delta in the portable-PDB blob, which SRM (and so MS CodeCoverage) rejects
/// for the whole method. Collect those points (with their index) before rewriting. Any
/// other unbound point would carry a position we cannot place: refuse the assembly.
let private endOfMethodPoints asmName (t: TypeDefinition) (meth: MethodDefinition) =
    let dbg = meth.DebugInformation

    if not dbg.HasSequencePoints then
        []
    else
        let body = meth.Body
        let offsets = HashSet<int>(body.Instructions |> Seq.map (fun i -> i.Offset))

        dbg.SequencePoints
        |> Seq.indexed
        |> Seq.filter (fun (_, sp) -> not (offsets.Contains sp.Offset))
        |> Seq.map (fun (ix, sp) ->
            if sp.Offset <> body.CodeSize then
                raise (WeaveFailure(UnboundSequencePoint(asmName, typeKey t + "::" + meth.Name)))

            ix, sp)
        |> Seq.toList

/// PDB INVARIANT, part 2: re-create each end-of-method point at the woven body's end,
/// keeping its document, lines and columns. Returns how many moved.
let private moveEndOfMethodPoints (meth: MethodDefinition) (points: (int * SequencePoint) list) =
    let newEnd = codeSize meth.Body
    let ctor = unboundSequencePointCtor.Value

    points
    |> List.sumBy (fun (ix, sp) ->
        if sp.Offset = newEnd then
            0
        else
            let moved = ctor.Invoke [| box newEnd; box sp.Document |] :?> SequencePoint
            moved.StartLine <- sp.StartLine
            moved.StartColumn <- sp.StartColumn
            moved.EndLine <- sp.EndLine
            moved.EndColumn <- sp.EndColumn
            meth.DebugInformation.SequencePoints.[ix] <- moved
            1)

let private readModule (recorderPath: string) (input: WeaveInput) =
    let name = Path.GetFileNameWithoutExtension input.Path

    if not (File.Exists(Path.ChangeExtension(input.Path, ".pdb"))) then
        raise (WeaveFailure(MissingPdb name))

    let resolver = new DefaultAssemblyResolver()
    resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath input.Path))
    resolver.AddSearchDirectory(Path.GetDirectoryName recorderPath)

    let m =
        ModuleDefinition.ReadModule(
            input.Path,
            ReaderParameters(
                ReadSymbols = true,
                InMemory = true,
                AssemblyResolver = resolver,
                SymbolReaderProvider = PortablePdbReaderProvider()
            )
        )

    if isOptimized m.Assembly then
        m.Dispose()
        raise (WeaveFailure(Optimized name))

    m, input.Mode

/// Weave `inputs` into `outputDir` (DLL + PDB per input), running `passes` on every method
/// body. Refuses the whole set on the first error; nothing is written to `outputDir`
/// before every body has been rewritten.
let weave (passes: IWeavePass list) (inputs: WeaveInput list) (outputDir: string) : Result<WeaveResult, WeaveError> =
    let recorderPath = typeof<Probes>.Assembly.Location
    use recorder = ModuleDefinition.ReadModule recorderPath
    let rows = ResizeArray<ManifestRow>()
    let documents = Dictionary<string, string>()
    let touched = Dictionary<string, ResizeArray<string>>()
    let modules = ResizeArray<ModuleDefinition * WeaveMode>()
    // The assembly being read, rewritten or written: named in a Cecil failure.
    let current = ref "?"
    let mutable probed = 0
    let mutable moved = 0

    let alloc (row: ManifestRow) =
        let id = rows.Count
        rows.Add { row with Id = id }
        id

    let recorderMethod (m: ModuleDefinition) (typeName: string) (name: string) =
        let t = recorder.GetType typeName
        m.ImportReference(t.Methods |> Seq.find (fun x -> x.Name = name))

    let entryRow asmName (t: TypeDefinition) (meth: MethodDefinition) =
        let dbg = meth.DebugInformation

        let sps =
            if dbg.HasSequencePoints then
                dbg.SequencePoints |> Seq.filter (fun s -> not s.IsHidden) |> Seq.toList
            else
                []

        for sp in sps do
            if not (documents.ContainsKey sp.Document.Url) then
                documents.[sp.Document.Url] <- hex sp.Document.Hash

        let first = List.tryHead sps

        { Id = 0
          Kind = kindOf t meth
          Assembly = asmName
          TypeName = typeKey t
          Member = meth.Name
          Document = first |> Option.map (fun s -> s.Document.Url)
          FirstLine = first |> Option.map (fun s -> s.StartLine) |> Option.defaultValue 0
          LastLine = sps |> List.tryLast |> Option.map (fun s -> s.EndLine) |> Option.defaultValue 0 }

    let rewrite
        set
        (m: ModuleDefinition)
        mode
        (hit: MethodReference)
        asmName
        (t: TypeDefinition)
        (meth: MethodDefinition)
        =
        let dbg = meth.DebugInformation
        let body = meth.Body
        let endPoints = endOfMethodPoints asmName t meth
        let head = body.Instructions.[0]

        // The root local scope starts at the first instruction; keep it spanning the probe.
        let rootAtHead =
            not (isNull dbg.Scope)
            && not dbg.Scope.Start.IsEndOfMethod
            && dbg.Scope.Start.Offset = head.Offset

        body.SimplifyMacros()

        let mutable changed = false

        for p in passes do
            if p.Rewrite(set, m, mode, meth) then
                changed <- true

        if mode = Full || isTestMethod meth then
            let id = alloc (entryRow asmName t meth)
            let il = body.GetILProcessor()
            let first = body.Instructions.[0]
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, id))
            il.InsertBefore(first, il.Create(OpCodes.Call, hit))
            probed <- probed + 1
            changed <- true

        body.OptimizeMacros()
        moved <- moved + moveEndOfMethodPoints meth endPoints

        if changed then
            if rootAtHead then
                dbg.Scope.Start <- InstructionOffset(body.Instructions.[0])

            let key = typeKey t + "::" + meth.Name

            match touched.TryGetValue asmName with
            | true, l -> l.Add key
            | _ -> touched.[asmName] <- ResizeArray [ key ]

    try
        try
            for input in inputs do
                current.Value <- Path.GetFileNameWithoutExtension input.Path
                modules.Add(readModule recorderPath input)

            let set =
                { Modules = List.ofSeq modules
                  Alloc = alloc
                  RecorderMethod = recorderMethod }

            for p in passes do
                p.Prepare set

            for m, mode in modules do
                let asmName = m.Assembly.Name.Name
                current.Value <- asmName
                let hit = recorderMethod m Contract.ProbesType Contract.Hit

                for t in m.GetTypes() |> Seq.toList do
                    for meth in t.Methods |> Seq.toList do
                        if meth.HasBody && meth.Body.Instructions.Count > 0 then
                            rewrite set m mode hit asmName t meth

            Directory.CreateDirectory outputDir |> ignore

            let outputs =
                modules
                |> Seq.map (fun (m, _) ->
                    let asmName = m.Assembly.Name.Name
                    current.Value <- asmName
                    let dst = Path.Combine(outputDir, asmName + ".dll")

                    m.Write(
                        dst,
                        WriterParameters(WriteSymbols = true, SymbolWriterProvider = PortablePdbWriterProvider())
                    )

                    match PdbCheck.decodeFailures (Path.ChangeExtension(dst, ".pdb")) with
                    | [] -> dst
                    | failures -> raise (WeaveFailure(PdbCorrupt(asmName, failures.Length))))
                |> Seq.toList

            Ok
                { Manifest =
                    { Rows = rows.ToArray()
                      Documents = documents |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                      IdCount = rows.Count }
                  Outputs = outputs
                  Stats =
                    { MethodsProbed = probed
                      EndOfMethodSequencePointsMoved = moved
                      Touched = touched |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Map.ofSeq } }
        with
        | WeaveFailure e -> Error e
        | ex -> Error(CecilFailed(current.Value, ex.Message))
    finally
        for m, _ in modules do
            m.Dispose()
