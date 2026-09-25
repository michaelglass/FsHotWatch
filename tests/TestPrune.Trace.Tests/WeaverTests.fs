module TestPrune.Trace.Tests.WeaverTests

open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open Xunit
open Swensen.Unquote
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Weaver
open TestPrune.Trace.Tests

/// Copy a fixture's build output to a scratch dir INSIDE the repo (so repo-root probing
/// still works) and return it. Callers delete it with `deleteScratch`.
let copyFixture (sourceDir: string) =
    let dir =
        Path.Combine(Fixtures.repoRoot, "tests", "TraceFixtures", "bin-scratch", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore

    for f in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories) do
        let dst = Path.Combine(dir, Path.GetRelativePath(sourceDir, f))
        Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
        File.Copy(f, dst)

    dir

/// Remove a scratch directory made by `copyFixture`.
let deleteScratch (dir: string) =
    if Directory.Exists dir then
        Directory.Delete(dir, true)

/// Weave FxLib (full) and FxDriver (sites-only) from a scratch copy of the driver's build
/// output, then copy the woven files over the originals so the scratch dir runs woven.
/// A sequence point at an IL offset no instruction starts at (Cecil's public constructor
/// only binds to an instruction).
let private unboundSequencePoint (offset: int) (document: Document) =
    typeof<SequencePoint>
        .GetConstructor(
            System.Reflection.BindingFlags.NonPublic
            ||| System.Reflection.BindingFlags.Instance,
            null,
            [| typeof<int>; typeof<Document> |],
            null
        )
        .Invoke([| box offset; box document |])
    :?> SequencePoint

/// Give every method of `dllPath` that has sequence points the hidden end-of-method point
/// (IL offset = code size) older F# compilers emit and newer ones do not, so the tests of
/// the weaver's handling of it see that shape whichever SDK built the fixture.
let addEndOfMethodMarkers (dllPath: string) =
    use asm =
        AssemblyDefinition.ReadAssembly(dllPath, ReaderParameters(ReadWrite = true, ReadSymbols = true))

    for t in asm.MainModule.GetTypes() do
        for m in t.Methods do
            if m.HasBody && m.DebugInformation.HasSequencePoints then
                let sps = m.DebugInformation.SequencePoints
                let size = m.Body.CodeSize

                if not (sps |> Seq.exists (fun sp -> sp.Offset = size)) then
                    let marker = unboundSequencePoint size sps.[0].Document
                    marker.StartLine <- 0xfeefee
                    marker.EndLine <- 0xfeefee
                    sps.Add marker

    asm.Write(WriterParameters(WriteSymbols = true))

let private weaveFxPrepared (prepare: string -> unit) (passes: IWeavePass list) =
    let dir = copyFixture Fixtures.fxDriverDir
    prepare dir
    let out = Path.Combine(dir, "woven")

    let r =
        weave
            passes
            [ { Path = Path.Combine(dir, "FxLib.dll")
                Mode = Full }
              { Path = Path.Combine(dir, "FxDriver.dll")
                Mode = SitesOnly } ]
            out
        |> Result.defaultWith (fun e ->
            deleteScratch dir
            failwith $"%A{e}")

    for f in r.Outputs do
        File.Copy(f, Path.Combine(dir, Path.GetFileName f), true)

        File.Copy(
            Path.ChangeExtension(f, ".pdb"),
            Path.Combine(dir, Path.ChangeExtension(Path.GetFileName f, ".pdb")),
            true
        )

    dir, r

/// Weave FxLib (full) and FxDriver (sites-only); see `weaveFxPrepared`.
let weaveFx (passes: IWeavePass list) = weaveFxPrepared ignore passes

let private withWeave passes (f: string -> WeaveResult -> unit) =
    let dir, r = weaveFx passes

    try
        f dir r
    finally
        deleteScratch dir

/// Every method's sequence points, decoded with SRM (the reader MS CodeCoverage uses), as
/// (method row, [(document, startLine, startColumn, endLine, endColumn, isHidden, ilOffset)]).
let private sequencePoints (pdbPath: string) =
    use fs = File.OpenRead pdbPath
    use provider = MetadataReaderProvider.FromPortablePdbStream fs
    let reader = provider.GetMetadataReader()

    [ for h in reader.MethodDebugInformation do
          let info = reader.GetMethodDebugInformation h
          let row = MetadataTokens.GetRowNumber h

          if not info.SequencePointsBlob.IsNil then
              let sps =
                  [ for sp in info.GetSequencePoints() ->
                        let doc = reader.GetString(reader.GetDocument(sp.Document).Name)

                        Path.GetFileName doc,
                        sp.StartLine,
                        sp.StartColumn,
                        sp.EndLine,
                        sp.EndColumn,
                        sp.IsHidden,
                        sp.Offset ]

              yield row, sps ]
    |> Map.ofList

/// Root local-scope start offset per method row.
let private rootScopeStarts (pdbPath: string) =
    use fs = File.OpenRead pdbPath
    use provider = MetadataReaderProvider.FromPortablePdbStream fs
    let reader = provider.GetMetadataReader()

    [ for h in reader.MethodDebugInformation do
          let def = h.ToDefinitionHandle()
          let scopes = reader.GetLocalScopes def |> Seq.map reader.GetLocalScope |> Seq.toList

          if not scopes.IsEmpty then
              yield MetadataTokens.GetRowNumber def, scopes |> List.map (fun s -> s.StartOffset) |> List.min ]
    |> Map.ofList

/// IL code size per method row, read from the assembly itself.
let private codeSizes (dllPath: string) =
    use pe = new PEReader(File.OpenRead dllPath)
    let md = pe.GetMetadataReader()

    [ for h in md.MethodDefinitions do
          let m = md.GetMethodDefinition h

          if m.RelativeVirtualAddress <> 0 then
              yield MetadataTokens.GetRowNumber h, pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes().Length ]
    |> Map.ofList

/// Rows of the methods whose PDB (decoded with SRM) has a point exactly at the end of the IL.
let private endMarkerRows (dllPath: string) =
    let sizes = codeSizes dllPath

    sequencePoints (Path.ChangeExtension(dllPath, ".pdb"))
    |> Map.filter (fun row sps -> sps |> List.exists (fun (_, _, _, _, _, _, off) -> off = sizes.[row]))
    |> Map.keys
    |> Seq.toList

/// `withWeave` over a FxLib and FxDriver given end-of-method markers first
/// (`addEndOfMethodMarkers`): FxLib's methods are all probed, so their markers move; the
/// sites-only FxDriver has no test methods, so its markers stay. `f` also gets FxLib's
/// marked, pre-weave sequence points. Fails loudly if the shape under test is missing,
/// rather than letting the assertions below pass over nothing.
let private withMarkedWeave (f: string -> WeaveResult -> Map<int, _> -> unit) =
    let mutable original = Map.empty

    let dir, r =
        weaveFxPrepared
            (fun dir ->
                for name in [ "FxLib.dll"; "FxDriver.dll" ] do
                    let path = Path.Combine(dir, name)
                    addEndOfMethodMarkers path
                    test <@ not (endMarkerRows path).IsEmpty @>

                original <- sequencePoints (Path.Combine(dir, "FxLib.pdb")))
            []

    try
        f dir r original
    finally
        deleteScratch dir

[<Fact>]
let ``every product method gets an entry probe row that points at its source`` () =
    withWeave [] (fun _ r ->
        let area =
            r.Manifest.Rows
            |> Array.find (fun x -> x.TypeName = "FxLib.Logic" && x.Member = "area")

        test <@ area.Kind = UserMethod @>
        test <@ area.Document |> Option.exists (fun d -> d.EndsWith "Logic.fs") @>
        test <@ area.FirstLine >= 5 && area.LastLine <= 9 @>

        test
            <@
                r.Manifest.Documents
                |> Map.exists (fun p h -> p.EndsWith "Logic.fs" && h.Length = 64)
            @>

        test <@ r.Manifest.Rows |> Array.mapi (fun i x -> x.Id = i) |> Array.forall id @>
        test <@ r.Manifest.IdCount = r.Manifest.Rows.Length @>
        test <@ r.Stats.MethodsProbed = r.Manifest.Rows.Length @>
        test <@ r.Stats.Touched.["FxLib"] |> List.contains "FxLib.Logic::area" @>)

[<Fact>]
let ``compiler-generated methods and type initializers get their own kinds`` () =
    withWeave [] (fun _ r ->
        let kinds = r.Manifest.Rows |> Array.map (fun x -> x.Kind) |> Set.ofArray
        test <@ kinds.Contains StaticCtor @>
        test <@ kinds.Contains GeneratedMethod @>)

[<Fact>]
let ``a sites-only assembly probes only its test entry points`` () =
    withWeave [] (fun _ r ->
        let driverRows = r.Manifest.Rows |> Array.filter (fun x -> x.Assembly = "FxDriver")
        // FxDriver has no [<Fact>]s, so sites-only means no entry probes at all.
        driverRows =! [||]
        test <@ not (r.Stats.Touched.ContainsKey "FxDriver") @>)

[<Fact>]
let ``a sites-only test assembly probes its Fact and Theory methods`` () =
    let dir = copyFixture Fixtures.fxTestsDir

    try
        let r =
            weave
                []
                [ { Path = Path.Combine(dir, "FxLib.dll")
                    Mode = Full }
                  { Path = Path.Combine(dir, "FxTests.dll")
                    Mode = SitesOnly } ]
                (Path.Combine(dir, "woven"))
            |> Result.defaultWith (fun e -> failwith $"%A{e}")

        let testRows =
            r.Manifest.Rows
            |> Array.filter (fun x -> x.Assembly = "FxTests")
            |> Array.map (fun x -> x.TypeName, x.Member)

        test <@ testRows |> Array.contains ("FxTests.AttributionTests+ClassA", "a sync area") @>

        test
            <@
                testRows
                |> Array.contains ("FxTests.AttributionTests+ClassB", "b theory aboveThreshold")
            @>
        // The fixture class's members are not tests.
        test <@ testRows |> Array.forall (fun (t, _) -> not (t.EndsWith "SharedFixture")) @>
        PdbCheck.decodeFailures (Path.Combine(dir, "woven", "FxTests.pdb")) =! []
    finally
        deleteScratch dir

[<Fact>]
let ``woven PDBs decode with System.Reflection.Metadata and the end-of-method marker bug is exercised`` () =
    withMarkedWeave (fun dir r _ ->
        PdbCheck.decodeFailures (Path.Combine(dir, "FxLib.pdb")) =! []
        PdbCheck.decodeFailures (Path.Combine(dir, "FxDriver.pdb")) =! []
        // The unprobed driver's markers are left where they are, still at the body's end.
        test <@ not (endMarkerRows (Path.Combine(dir, "FxDriver.dll"))).IsEmpty @>
        // Every marked method is probed, so every marker had to move; if this is 0 the
        // regression below guards nothing.
        test <@ r.Stats.EndOfMethodSequencePointsMoved > 0 @>)

/// Assert the woven FxLib in `dir` keeps `original`'s sequence points but for their offsets,
/// and that every offset lies inside the woven body.
let private onlyShifted (dir: string) (original: Map<int, _>) =
    let woven = sequencePoints (Path.Combine(dir, "FxLib.pdb"))
    let sizes = codeSizes (Path.Combine(dir, "FxLib.dll"))
    let strip = List.map (fun (d, sl, sc, el, ec, h, _) -> d, sl, sc, el, ec, h)

    // Same methods, same lines, columns, documents and hidden flags: the inputs to
    // line coverage are unchanged, so coverage with and without weaving is identical.
    test <@ Map.keys woven |> Seq.toList = (Map.keys original |> Seq.toList) @>

    let differing =
        original
        |> Map.filter (fun row sps -> strip sps <> strip woven.[row])
        |> Map.keys
        |> Seq.toList

    differing =! []

    let outside =
        woven
        |> Map.toList
        |> List.collect (fun (row, sps) ->
            sps
            |> List.filter (fun (_, _, _, _, _, _, off) -> off > sizes.[row])
            |> List.map (fun sp -> row, sp))

    outside =! []

[<Fact>]
let ``woven sequence points match the original ones except for the probe's shift`` () =
    withMarkedWeave (fun dir _ original ->
        onlyShifted dir original
        // Every method was marked and every marker still sits exactly at its body's end.
        endMarkerRows (Path.Combine(dir, "FxLib.dll"))
        =! (Map.keys original |> Seq.toList))

[<Fact>]
let ``woven sequence points of the compiler's own PDB match except for the probe's shift`` () =
    withWeave [] (fun dir _ -> onlyShifted dir (sequencePoints (Path.Combine(Fixtures.fxDriverDir, "FxLib.pdb"))))

[<Fact>]
let ``the root local scope still spans the whole woven body`` () =
    withWeave [] (fun dir _ ->
        let original = rootScopeStarts (Path.Combine(Fixtures.fxDriverDir, "FxLib.pdb"))
        let woven = rootScopeStarts (Path.Combine(dir, "FxLib.pdb"))

        let startedAtZero =
            original |> Map.filter (fun _ s -> s = 0) |> Map.keys |> Seq.toList

        test <@ not startedAtZero.IsEmpty @>
        (startedAtZero |> List.filter (fun row -> woven.[row] <> 0)) =! [])

let private withDebuggable (dir: string) (edit: AssemblyDefinition -> unit) =
    let path = Path.Combine(dir, "FxLib.dll")

    do
        use asm =
            AssemblyDefinition.ReadAssembly(path, ReaderParameters(ReadWrite = true, ReadSymbols = true))

        edit asm
        asm.Write(WriterParameters(WriteSymbols = true))

    path

[<Fact>]
let ``an assembly without DebuggableAttribute is refused as optimized`` () =
    let dir = copyFixture Fixtures.fxLibDir

    try
        let path =
            withDebuggable dir (fun asm ->
                let dbg =
                    asm.CustomAttributes
                    |> Seq.find (fun a -> a.AttributeType.Name = "DebuggableAttribute")

                asm.CustomAttributes.Remove dbg |> ignore)

        test <@ weave [] [ { Path = path; Mode = Full } ] (Path.Combine(dir, "woven")) = Error(Optimized "FxLib") @>
    finally
        deleteScratch dir

[<Fact>]
let ``a release-shaped DebuggableAttribute is refused as optimized`` () =
    let dir = copyFixture Fixtures.fxLibDir

    try
        let path =
            withDebuggable dir (fun asm ->
                let dbg =
                    asm.CustomAttributes
                    |> Seq.find (fun a -> a.AttributeType.Name = "DebuggableAttribute")

                // What a Release build emits: IgnoreSymbolStoreSequencePoints only.
                let arg = dbg.ConstructorArguments.[0]
                dbg.ConstructorArguments.[0] <- CustomAttributeArgument(arg.Type, box 2))

        test <@ weave [] [ { Path = path; Mode = Full } ] (Path.Combine(dir, "woven")) = Error(Optimized "FxLib") @>
    finally
        deleteScratch dir

[<Fact>]
let ``the fixture's Debug build is not optimized`` () =
    use asm =
        AssemblyDefinition.ReadAssembly(Path.Combine(Fixtures.fxLibDir, "FxLib.dll"))

    test <@ not (isOptimized asm) @>

[<Fact>]
let ``an assembly without a PDB is refused`` () =
    let dir = copyFixture Fixtures.fxLibDir

    try
        File.Delete(Path.Combine(dir, "FxLib.pdb"))

        test
            <@
                weave
                    []
                    [ { Path = Path.Combine(dir, "FxLib.dll")
                        Mode = Full } ]
                    (Path.Combine(dir, "woven")) = Error(MissingPdb "FxLib")
            @>
    finally
        deleteScratch dir

[<Fact>]
let ``an unreadable assembly is a Cecil failure naming it`` () =
    let dir = copyFixture Fixtures.fxLibDir

    try
        File.WriteAllText(Path.Combine(dir, "FxLib.dll"), "not a PE file")

        let r =
            weave
                []
                [ { Path = Path.Combine(dir, "FxLib.dll")
                    Mode = Full } ]
                (Path.Combine(dir, "woven"))

        test
            <@
                match r with
                | Error(CecilFailed("FxLib", _)) -> true
                | _ -> false
            @>
    finally
        deleteScratch dir

[<Fact>]
let ``weaving is deterministic`` () =
    withWeave [] (fun _ a -> withWeave [] (fun _ b -> test <@ a.Manifest = b.Manifest @>))

[<Fact>]
let ``a pass sees every method with a body and can mark it touched`` () =
    let seen = ResizeArray<string>()
    let mutable prepared = 0

    let pass =
        { new IWeavePass with
            member _.Prepare(set) = prepared <- set.Modules.Length

            member _.Rewrite(_, m, mode, meth) =
                if mode = SitesOnly then
                    seen.Add(meth.DeclaringType.FullName + "::" + meth.Name)

                m.Assembly.Name.Name = "FxDriver" && meth.Name = "main" }

    withWeave [ pass ] (fun _ r ->
        test <@ prepared = 2 @>
        test <@ seen.Contains "FxDriver.Program::main" @>
        test <@ r.Stats.Touched.["FxDriver"] = [ "FxDriver.Program::main" ] @>)

[<Fact>]
let ``the woven driver runs`` () =
    withWeave [] (fun dir r ->
        let code, output =
            Launch.run
                (Path.Combine(dir, "FxDriver"))
                []
                [ TestPrune.Trace.Recorder.Contract.OutEnv, Path.Combine(dir, "traces")
                  TestPrune.Trace.Recorder.Contract.IdsEnv, string r.Manifest.IdCount
                  TestPrune.Trace.Recorder.Contract.RepoRootEnv, Fixtures.repoRoot ]
                dir
                (TimeSpan.FromMinutes 1.0)

        test <@ (code, output) = (0, "") @>)

let private withDebuggableArgs (jitTracking: bool) (optimizerDisabled: bool) =
    use asm =
        AssemblyDefinition.ReadAssembly(Path.Combine(Fixtures.fxLibDir, "FxLib.dll"))

    let old =
        asm.CustomAttributes
        |> Seq.find (fun a -> a.AttributeType.Name = "DebuggableAttribute")

    asm.CustomAttributes.Remove old |> ignore

    let ctor =
        asm.MainModule.ImportReference(
            typeof<System.Diagnostics.DebuggableAttribute>.GetConstructor([| typeof<bool>; typeof<bool> |])
        )

    let attr = CustomAttribute ctor
    let boolType = asm.MainModule.TypeSystem.Boolean
    attr.ConstructorArguments.Add(CustomAttributeArgument(boolType, box jitTracking))
    attr.ConstructorArguments.Add(CustomAttributeArgument(boolType, box optimizerDisabled))
    asm.CustomAttributes.Add attr
    isOptimized asm

[<Fact>]
let ``the two-argument DebuggableAttribute form is read by its optimizer-disabled flag`` () =
    test <@ not (withDebuggableArgs true true) @>
    test <@ withDebuggableArgs true false @>

[<Fact>]
let ``a sequence point that binds to no instruction and is not the end marker is refused`` () =
    let dir = copyFixture Fixtures.fxLibDir

    try
        let path = Path.Combine(dir, "FxLib.dll")

        do
            use asm =
                AssemblyDefinition.ReadAssembly(path, ReaderParameters(ReadWrite = true, ReadSymbols = true))

            let area =
                asm.MainModule.GetType("FxLib.Logic").Methods
                |> Seq.find (fun m -> m.Name = "area")

            let sps = area.DebugInformation.SequencePoints
            let last = sps |> Seq.filter (fun sp -> not sp.IsHidden) |> Seq.last
            let stray = unboundSequencePoint (area.Body.CodeSize + 3) last.Document
            stray.StartLine <- last.StartLine
            stray.EndLine <- last.EndLine
            stray.StartColumn <- 1
            stray.EndColumn <- 2
            sps.Add stray
            asm.Write(WriterParameters(WriteSymbols = true))

        test
            <@
                weave [] [ { Path = path; Mode = Full } ] (Path.Combine(dir, "woven")) = Error(
                    UnboundSequencePoint("FxLib", "FxLib.Logic::area")
                )
            @>
    finally
        deleteScratch dir

[<Fact>]
let ``PdbCheck catches the corrupt blob plain Cecil writes after an insertion`` () =
    let dir = copyFixture Fixtures.fxLibDir

    try
        let path = Path.Combine(dir, "FxLib.dll")
        // The corruption needs the end-of-method marker; newer compilers do not emit it.
        addEndOfMethodMarkers path
        test <@ not (endMarkerRows path).IsEmpty @>

        do
            use asm =
                AssemblyDefinition.ReadAssembly(path, ReaderParameters(ReadWrite = true, ReadSymbols = true))

            for t in asm.MainModule.GetTypes() do
                for m in t.Methods do
                    if m.HasBody then
                        // Load the debug info before editing, as a weaver must to use it.
                        m.DebugInformation.SequencePoints |> ignore
                        let il = m.Body.GetILProcessor()
                        let head = m.Body.Instructions.[0]

                        // As many bytes as an entry probe (ldc.i4 + call), so the stale
                        // end-of-method offset falls before the shifted last point.
                        for _ in 1..10 do
                            il.InsertBefore(head, il.Create OpCodes.Nop)

            asm.Write(WriterParameters(WriteSymbols = true))

        let failures = PdbCheck.decodeFailures (Path.ChangeExtension(path, ".pdb"))
        test <@ not failures.IsEmpty @>
        test <@ failures |> List.forall (fun (row, message) -> row > 0 && message <> "") @>
    finally
        deleteScratch dir
