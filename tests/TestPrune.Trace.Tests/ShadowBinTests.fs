module TestPrune.Trace.Tests.ShadowBinTests

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Security.Cryptography
open System.Text.Json
open Xunit
open Swensen.Unquote
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder
open TestPrune.Trace.ShadowBin
open TestPrune.Trace.Tests

let private sha (path: string) =
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path))

let private tempDir () =
    Directory.CreateTempSubdirectory("tp-shadow-").FullName

// ---------------------------------------------------------------- HardLink

[<Fact>]
let ``a mirrored file is a hardlink, so writing into it would reach the source`` () =
    let root = tempDir ()
    let src, dst = Path.Combine(root, "src"), Path.Combine(root, "dst")
    Directory.CreateDirectory(Path.Combine(src, "de")) |> ignore
    File.WriteAllText(Path.Combine(src, "a.dll"), "original")
    File.WriteAllText(Path.Combine(src, "de", "r.dll"), "resource")
    let stats = HardLink.mirror src dst
    test <@ stats = { Linked = 2; Copied = 0; Removed = 0 } @>
    File.AppendAllText(Path.Combine(dst, "a.dll"), "!")
    test <@ File.ReadAllText(Path.Combine(src, "a.dll")) = "original!" @>
    test <@ File.ReadAllText(Path.Combine(dst, "de", "r.dll")) = "resource" @>

[<Fact>]
let ``replacing a mirrored file never writes through the hardlink`` () =
    let root = tempDir ()
    let src, dst = Path.Combine(root, "src"), Path.Combine(root, "dst")
    Directory.CreateDirectory src |> ignore
    File.WriteAllText(Path.Combine(src, "a.dll"), "original")
    HardLink.mirror src dst |> ignore
    HardLink.replaceWith (Path.Combine(dst, "a.dll")) (fun tmp -> File.WriteAllText(tmp, "woven"))
    test <@ File.ReadAllText(Path.Combine(src, "a.dll")) = "original" @>
    test <@ File.ReadAllText(Path.Combine(dst, "a.dll")) = "woven" @>
    test <@ Directory.GetFiles dst |> Array.map Path.GetFileName = [| "a.dll" |] @>

[<Fact>]
let ``mirroring re-links a rebuilt file and removes files the source no longer has`` () =
    let root = tempDir ()
    let src, dst = Path.Combine(root, "src"), Path.Combine(root, "dst")
    Directory.CreateDirectory src |> ignore
    File.WriteAllText(Path.Combine(src, "a.dll"), "v1")
    File.WriteAllText(Path.Combine(src, "gone.dll"), "x")
    HardLink.mirror src dst |> ignore
    // A rebuild replaces the file (new inode); a stale link would keep "v1".
    File.Delete(Path.Combine(src, "a.dll"))
    File.WriteAllText(Path.Combine(src, "a.dll"), "v2")
    File.Delete(Path.Combine(src, "gone.dll"))
    let stats = HardLink.mirror src dst
    test <@ File.ReadAllText(Path.Combine(dst, "a.dll")) = "v2" @>
    test <@ not (File.Exists(Path.Combine(dst, "gone.dll"))) @>
    test <@ stats = { Linked = 1; Copied = 0; Removed = 1 } @>

[<Fact>]
let ``mirroring copies a file it cannot link`` () =
    let root = tempDir ()
    let src, dst = Path.Combine(root, "src"), Path.Combine(root, "dst")
    Directory.CreateDirectory src |> ignore
    File.WriteAllText(Path.Combine(src, "a.dll"), "bytes")
    let stats = HardLink.mirrorWith (fun _ _ -> false) src dst
    test <@ stats = { Linked = 0; Copied = 1; Removed = 0 } @>
    File.AppendAllText(Path.Combine(dst, "a.dll"), "!")
    test <@ File.ReadAllText(Path.Combine(src, "a.dll")) = "bytes" @>

// ---------------------------------------------------------------- DepsJson

let private deps =
    """{ "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
         "targets": { ".NETCoreApp,Version=v10.0": { "App/1.0.0": { "dependencies": { "FSharp.Core": "10.1.0" }, "runtime": { "App.dll": {} } } } },
         "libraries": { "App/1.0.0": { "type": "project", "serviceable": false, "sha512": "" } } }"""

[<Fact>]
let ``the recorder is injected as a project library the app depends on, idempotently`` () =
    let json, changed =
        DepsJson.injectRecorder deps "App" "0.1.0" |> Result.defaultWith failwith

    test <@ changed @>
    use doc = JsonDocument.Parse json

    let target =
        doc.RootElement.GetProperty("targets").GetProperty(".NETCoreApp,Version=v10.0")

    let dependsOn =
        target.GetProperty("App/1.0.0").GetProperty("dependencies").GetProperty("TestPrune.Trace.Recorder").GetString()

    let listsDll, _ =
        target
            .GetProperty("TestPrune.Trace.Recorder/0.1.0")
            .GetProperty("runtime")
            .TryGetProperty("TestPrune.Trace.Recorder.dll")

    let libraryType =
        doc.RootElement
            .GetProperty("libraries")
            .GetProperty("TestPrune.Trace.Recorder/0.1.0")
            .GetProperty("type")
            .GetString()

    test <@ dependsOn = "0.1.0" && listsDll && libraryType = "project" @>

    let again, changedAgain =
        DepsJson.injectRecorder json "App" "0.1.0" |> Result.defaultWith failwith

    test <@ not changedAgain && again = json @>
    test <@ DepsJson.injectRecorder json "App" "0.2.0" = Error(DepsJson.SkewPrefix + "0.1.0") @>

[<Fact>]
let ``an app with no dependencies gets one, and unreadable deps are an error`` () =
    let bare = deps.Replace("\"dependencies\": { \"FSharp.Core\": \"10.1.0\" }, ", "")

    let json, _ =
        DepsJson.injectRecorder bare "App" "0.1.0" |> Result.defaultWith failwith

    test <@ json.Contains "\"TestPrune.Trace.Recorder\": \"0.1.0\"" @>
    test <@ DepsJson.injectRecorder "not json" "App" "0.1.0" |> Result.isError @>
    test <@ DepsJson.injectRecorder deps "Missing" "0.1.0" |> Result.isError @>

// ---------------------------------------------------------------- JIT verification (in process)

/// A load context over `dir`, so a test can load a deliberately broken fixture assembly
/// without it colliding with anything the test host has loaded.
type private DirContext(dir: string) =
    inherit AssemblyLoadContext(isCollectible = true)

    override this.Load(name: AssemblyName) =
        let p = Path.Combine(dir, name.Name + ".dll")

        if name.Name <> "FSharp.Core" && File.Exists p then
            this.LoadFromAssemblyPath p
        else
            null

/// Copy a fixture's build output to a fresh scratch directory inside the repository.
let private scratch (sourceDir: string) = WeaverTests.copyFixture sourceDir

/// Put a stack underflow (a `pop` on an empty stack) at the head of FxLib.Logic::area.
let private breakArea (lib: string) =
    use asm =
        AssemblyDefinition.ReadAssembly(lib, ReaderParameters(ReadWrite = true, ReadSymbols = true))

    let area =
        asm.MainModule.GetType("FxLib.Logic").Methods
        |> Seq.find (fun m -> m.Name = "area")

    let il = area.Body.GetILProcessor()
    il.InsertBefore(area.Body.Instructions.[0], il.Create OpCodes.Pop)
    asm.Write(WriterParameters(WriteSymbols = true))

[<Fact>]
let ``in-process verification names the invalid method and counts everything else`` () =
    let dir = scratch Fixtures.fxDriverDir

    try
        breakArea (Path.Combine(dir, "FxLib.dll"))
        let ctx = DirContext dir

        let load name =
            match name with
            | "FxLib" -> ctx.LoadFromAssemblyPath(Path.Combine(dir, "FxLib.dll"))
            | n -> Assembly.Load(AssemblyName n)

        let report =
            JitVerify.prepareListed
                load
                [ "FxLib\tFxLib.Logic::area"
                  "FxLib\tFxLib.Logic::turn"
                  "FxLib\tFxLib.Dog::.ctor"
                  "FxLib\tFxLib.Values::.cctor"
                  "FSharp.Core\tMicrosoft.FSharp.Collections.ListModule::Map"
                  "FSharp.Core\tMicrosoft.FSharp.Core.FSharpOption`1::get_Value"
                  "System.Private.CoreLib\tSystem.IO.Stream::Flush"
                  "FxLib\tFxLib.Logic::noSuchMethod"
                  "FxLib\tFxLib.NoSuchType::x"
                  "no tab here"
                  "FxLib\tno-separator" ]

        test <@ report.Invalid = [ "FxLib.Logic::area" ] @>
        test <@ report.Prepared >= 2 @>
        test <@ report.SkippedGeneric = 2 @>
        // Stream.Flush is abstract; the rest name nothing loadable, or are malformed.
        test <@ report.Other >= 5 @>
        ctx.Unload()
    finally
        WeaverTests.deleteScratch dir

[<Fact>]
let ``a method whose dependencies cannot load counts as other, not invalid`` () =
    let dir = scratch Fixtures.fxDriverDir

    try
        File.Delete(Path.Combine(dir, "FxLib.dll"))
        let ctx = DirContext dir

        let load (_: string) =
            ctx.LoadFromAssemblyPath(Path.Combine(dir, "FxDriver.dll"))

        let report = JitVerify.prepareListed load [ "FxDriver\tFxDriver.Program::main" ]

        test
            <@
                report = { Prepared = 0
                           Invalid = []
                           SkippedGeneric = 0
                           Other = 1 }
            @>

        ctx.Unload()
    finally
        WeaverTests.deleteScratch dir

[<Fact>]
let ``the startup hook is inert without the verify variable and exits after writing a report with it`` () =
    let dir = tempDir ()
    let list, report = Path.Combine(dir, "list.tsv"), Path.Combine(dir, "report.json")
    File.WriteAllLines(list, [ "TestPrune.Trace.Recorder\tTestPrune.Trace.Recorder.JitVerify::write" ])
    let exits = ResizeArray<int>()
    StartupHook.Run((fun _ -> null), exits.Add)
    StartupHook.Run((fun _ -> ""), exits.Add)
    StartupHook.Initialize() // the test host has no verify variable: returns
    test <@ exits.Count = 0 @>

    let env =
        Map.ofList [ Contract.VerifyEnv, report; Contract.VerifyAssembliesEnv, list ]

    StartupHook.Run((fun k -> Map.tryFind k env |> Option.toObj), exits.Add)
    test <@ List.ofSeq exits = [ 0 ] @>
    use doc = JsonDocument.Parse(File.ReadAllText report)
    test <@ doc.RootElement.GetProperty("prepared").GetInt32() = 1 @>
    test <@ doc.RootElement.GetProperty("invalid").GetArrayLength() = 0 @>

// ---------------------------------------------------------------- verify (child process)

[<Fact>]
let ``JIT verification in the app's own runtime names an invalid method`` () =
    let dir = scratch Fixtures.fxDriverDir

    try
        breakArea (Path.Combine(dir, "FxLib.dll"))
        let report = Path.Combine(dir, "verify.json")

        let r =
            verify
                dir
                (Path.Combine(dir, "FxDriver"))
                (Map.ofList [ "FxLib", [ "FxLib.Logic::area"; "FxLib.Logic::turn" ] ])
                report
                (TimeSpan.FromMinutes 1.0)

        test <@ r = Error(JitInvalid [ "FxLib.Logic::area" ]) @>
    finally
        WeaverTests.deleteScratch dir

[<Fact>]
let ``verification that never writes a report is a failure, not a pass`` () =
    let dir = scratch Fixtures.fxDriverDir

    try
        // Without the recorder the startup hook cannot load: the app exits non-zero.
        File.Delete(Path.Combine(dir, DepsJson.RecorderName + ".dll"))

        let r =
            verify
                dir
                (Path.Combine(dir, "FxDriver"))
                Map.empty
                (Path.Combine(dir, "v.json"))
                (TimeSpan.FromMinutes 1.0)

        test
            <@
                match r with
                | Error(VerifyFailed(code, _)) -> code <> 0
                | _ -> false
            @>
    finally
        WeaverTests.deleteScratch dir

// ---------------------------------------------------------------- prepare

/// A scratch test project: FxTests' build output under `<scratch>/FxTests/bin/Debug/net10.0`.
let private scratchProject () =
    let projectDir =
        Path.Combine(Fixtures.fixtureRoot, "bin-scratch", Guid.NewGuid().ToString "N", "FxTests")

    let debug = Path.Combine(projectDir, "bin", "Debug", "net10.0")

    for f in Directory.GetFiles(Fixtures.fxTestsDir, "*", SearchOption.AllDirectories) do
        let dst = Path.Combine(debug, Path.GetRelativePath(Fixtures.fxTestsDir, f))
        Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
        File.Copy(f, dst)

    projectDir

let private deleteProject (projectDir: string) =
    WeaverTests.deleteScratch (Path.GetDirectoryName projectDir)

let private request projectDir =
    { RepoRoot = Fixtures.repoRoot
      ProjectDir = projectDir
      AssemblyName = "FxTests"
      WeaveTests = SitesOnly
      Passes = [ SiteProbes.pass (); Redirects.pass () ]
      VerifyTimeout = TimeSpan.FromMinutes 2.0 }

let private prepared req =
    prepare req |> Result.defaultWith (fun e -> failwith (describeRefusal e))

let private debugDir projectDir =
    Path.Combine(projectDir, "bin", "Debug", "net10.0")

/// Rewrite an assembly in place (a new file, so a new inode, as a rebuild makes).
let private rebuild (dll: string) (edit: AssemblyDefinition -> unit) =
    let bytes =
        use asm =
            AssemblyDefinition.ReadAssembly(dll, ReaderParameters(ReadSymbols = true, InMemory = true))

        edit asm
        use ms = new MemoryStream()
        use pdb = new MemoryStream()

        asm.Write(
            ms,
            WriterParameters(
                WriteSymbols = true,
                SymbolStream = pdb,
                SymbolWriterProvider = PortablePdbWriterProvider()
            )
        )

        ms.ToArray(), pdb.ToArray()

    File.Delete dll
    File.WriteAllBytes(dll, fst bytes)
    let pdbPath = Path.ChangeExtension(dll, ".pdb")
    File.Delete pdbPath
    File.WriteAllBytes(pdbPath, snd bytes)

[<Fact>]
let ``prepare weaves the test project into bin/Traced and leaves bin/Debug untouched`` () =
    let projectDir = scratchProject ()

    try
        let debug = debugDir projectDir
        let before = Directory.GetFiles(debug) |> Array.map (fun f -> f, sha f)
        let shadow = prepared (request projectDir)

        test <@ shadow.Dir = Path.Combine(projectDir, "bin", "Traced", "net10.0") @>
        test <@ File.Exists shadow.Apphost && not shadow.Reused @>
        test <@ Directory.GetFiles(debug) |> Array.map (fun f -> f, sha f) = before @>

        test
            <@
                sha (Path.Combine(shadow.Dir, "FxLib.dll"))
                <> sha (Path.Combine(debug, "FxLib.dll"))
            @>

        test
            <@
                sha (Path.Combine(shadow.Dir, "FxTests.dll"))
                <> sha (Path.Combine(debug, "FxTests.dll"))
            @>
        // Not built from the repository: mirrored, never woven.
        test <@ sha (Path.Combine(shadow.Dir, "xunit.v3.core.dll")) = sha (Path.Combine(debug, "xunit.v3.core.dll")) @>

        test
            <@
                shadow.Manifest.Rows
                |> Array.exists (fun r -> r.Assembly = "FxLib" && r.Member = "area")
            @>

        // Sites-only: the test assembly carries probes on its test methods only.
        test
            <@
                shadow.Manifest.Rows
                |> Array.filter (fun r -> r.Assembly = "FxTests")
                |> Array.forall (fun r -> r.Kind = UserMethod)
            @>

        test <@ List.isEmpty shadow.Verify.Invalid && shadow.Verify.Prepared > 0 @>
        test <@ File.Exists(Path.Combine(shadow.Dir, "TestPrune.Trace.Recorder.dll")) @>
        test <@ not (File.Exists(Path.Combine(shadow.Dir, "TestPrune.Trace.Recorder.pdb"))) @>
        test <@ File.ReadAllText(Path.Combine(shadow.Dir, "FxTests.deps.json")).Contains "TestPrune.Trace.Recorder/" @>
        test <@ not (File.ReadAllText(Path.Combine(debug, "FxTests.deps.json")).Contains "TestPrune.Trace.Recorder") @>
        test <@ shadow.OriginalDepsJsonSha256 = (sha (Path.Combine(debug, "FxTests.deps.json"))).ToLowerInvariant() @>
        test <@ (Manifest.read shadow.ManifestDir |> Result.map _.IdCount) = Ok shadow.Manifest.IdCount @>

        use stamp =
            JsonDocument.Parse(File.ReadAllText(Path.Combine(shadow.Dir, StampName)))

        test <@ stamp.RootElement.GetProperty("weaveKey").GetString() = shadow.WeaveKey @>
        test <@ stamp.RootElement.GetProperty("ids").GetInt32() = shadow.Manifest.IdCount @>

        let again = prepared (request projectDir)
        test <@ again.Reused && again.WeaveKey = shadow.WeaveKey && again.Verify = shadow.Verify @>
        test <@ sha (Path.Combine(again.Dir, "FxLib.dll")) = sha (Path.Combine(shadow.Dir, "FxLib.dll")) @>
        test <@ Directory.GetFiles(debug) |> Array.map (fun f -> f, sha f) = before @>
    finally
        deleteProject projectDir

[<Fact>]
let ``a rebuilt input changes the weave key and is re-woven; old keys are evicted`` () =
    let projectDir = scratchProject ()

    try
        let lib = Path.Combine(debugDir projectDir, "FxLib.dll")
        let first = prepared (request projectDir)

        let keys =
            [ for i in 1..3 do
                  rebuild lib (fun asm -> asm.MainModule.Mvid <- Guid.NewGuid())
                  let next = prepared (request projectDir)
                  test <@ not next.Reused @>
                  test <@ sha (Path.Combine(next.Dir, "FxLib.dll")) <> sha lib @>
                  yield next.WeaveKey ]

        test
            <@
                keys |> List.distinct |> List.length = 3
                && not (List.contains first.WeaveKey keys)
            @>

        let kept =
            Directory.GetDirectories(Path.Combine(projectDir, "obj", "traced"))
            |> Array.map Path.GetFileName
            |> Set.ofArray

        test <@ kept = Set.ofList keys @>
    finally
        deleteProject projectDir

[<Fact>]
let ``a weave whose woven IL is invalid is refused and never cached as good`` () =
    let projectDir = scratchProject ()

    try
        breakArea (Path.Combine(debugDir projectDir, "FxLib.dll"))
        let r = prepare (request projectDir)

        test <@ r = Error(JitInvalid [ "FxLib.Logic::area" ]) @>

        // Not reused: the next prepare weaves and verifies again.
        test <@ prepare (request projectDir) = r @>
    finally
        deleteProject projectDir

[<Fact>]
let ``prepare refuses what it cannot shadow`` () =
    let projectDir = scratchProject ()

    try
        let req = request projectDir
        let debug = debugDir projectDir

        let binDebug = Path.Combine(projectDir, "bin", "Debug")
        test <@ prepare { req with AssemblyName = "Nope" } = Error(NoBuildOutput binDebug) @>

        test
            <@
                prepare
                    { req with
                        ProjectDir = Path.Combine(projectDir, "nothing") }
                |> Result.isError
            @>

        let other = Path.Combine(binDebug, "net9.0")
        Directory.CreateDirectory other |> ignore
        File.Copy(Path.Combine(debug, "FxTests.dll"), Path.Combine(other, "FxTests.dll"))
        test <@ prepare req = Error(AmbiguousTfm [ debug; other ]) @>
        Directory.Delete(other, true)

        File.Move(Path.Combine(debug, "FxTests"), Path.Combine(debug, "FxTests.bak"))
        test <@ prepare req = Error(NoApphost(Path.Combine(projectDir, "bin", "Traced", "net10.0", "FxTests"))) @>
        File.Move(Path.Combine(debug, "FxTests.bak"), Path.Combine(debug, "FxTests"))

        let depsPath = Path.Combine(debug, "FxTests.deps.json")
        let goodDeps = File.ReadAllText depsPath
        File.WriteAllText(depsPath, "{")

        test
            <@
                match prepare req with
                | Error(DepsJsonUnreadable _) -> true
                | _ -> false
            @>

        let skewed, _ =
            DepsJson.injectRecorder goodDeps "FxTests" "9.9.9"
            |> Result.defaultWith failwith

        File.WriteAllText(depsPath, skewed)

        test
            <@ prepare req = Error(RecorderVersionSkew("9.9.9", typeof<Probes>.Assembly.GetName().Version.ToString 3)) @>

        File.WriteAllText(depsPath, goodDeps)

        // An optimized repository assembly (no DebuggableAttribute) is refused.
        rebuild (Path.Combine(debug, "FxLib.dll")) (fun asm ->
            asm.CustomAttributes
            |> Seq.filter (fun a -> a.AttributeType.Name = "DebuggableAttribute")
            |> Seq.toList
            |> List.iter (asm.CustomAttributes.Remove >> ignore))

        test <@ prepare req = Error(WeaveRefused(Weaver.Optimized "FxLib")) @>
    finally
        deleteProject projectDir

[<Fact>]
let ``every refusal has a one-line description`` () =
    let all =
        [ NoBuildOutput "/b"
          AmbiguousTfm [ "a"; "b" ]
          NoApphost "/x"
          WeaveRefused(Weaver.Optimized "L")
          RecorderVersionSkew("1", "2")
          DepsJsonUnreadable "why"
          JitInvalid [ "T::m" ]
          VerifyFailed(1, "out")
          VerifyFailed(134, "Could not load file or assembly 'FSharp.Core, Version=8.0.0.0'") ]

    let lines = all |> List.map describeRefusal
    test <@ lines |> List.forall (fun l -> l <> "" && not (l.Contains "\n")) @>
    test <@ lines |> List.distinct |> List.length = all.Length @>

    test
        <@
            (List.last lines).Contains "C#-only"
            && not (lines.[lines.Length - 2].Contains "C#-only")
        @>

[<Fact>]
let ``only assemblies built from the repository are woven`` () =
    let projectDir = scratchProject ()

    try
        let debug = debugDir projectDir
        let recorder = typeof<Probes>.Assembly.Location
        // The app already ships the recorder (with its PDB, whose documents ARE in the repo),
        // and an assembly whose PDB is not a portable PDB at all.
        File.Copy(recorder, Path.Combine(debug, Path.GetFileName recorder))
        File.Copy(Path.ChangeExtension(recorder, ".pdb"), Path.Combine(debug, DepsJson.RecorderName + ".pdb"))
        File.WriteAllText(Path.Combine(debug, "Junk.dll"), "not an assembly")
        File.WriteAllText(Path.Combine(debug, "Junk.pdb"), "not a pdb")

        let shadow = prepared (request projectDir)
        let assemblies = shadow.Manifest.Rows |> Array.map _.Assembly |> Set.ofArray
        test <@ assemblies = set [ "FxLib"; "FxTests" ] @>
        test <@ sha (Path.Combine(shadow.Dir, "Junk.dll")) = sha (Path.Combine(debug, "Junk.dll")) @>
        test <@ not (File.Exists(Path.Combine(shadow.Dir, DepsJson.RecorderName + ".pdb"))) @>

        // A repository root that holds none of the PDBs' documents weaves nothing.
        let elsewhere =
            prepared
                { request projectDir with
                    RepoRoot = Path.Combine(projectDir, "nowhere") }

        test <@ elsewhere.Manifest.IdCount = 0 && elsewhere.Verify.Prepared = 0 @>
        test <@ sha (Path.Combine(elsewhere.Dir, "FxLib.dll")) = sha (Path.Combine(debug, "FxLib.dll")) @>
    finally
        deleteProject projectDir

[<Fact>]
let ``the passes are part of the weave key, and an unreadable cache entry is re-woven`` () =
    let projectDir = scratchProject ()

    try
        let plain = prepared (request projectDir)

        let noop =
            { new Weaver.IWeavePass with
                member _.Prepare _ = ()
                member _.Rewrite(_, _, _, _) = false }

        let withPass =
            prepared
                { request projectDir with
                    Passes = [ noop ] }

        test <@ withPass.WeaveKey <> plain.WeaveKey && not withPass.Reused @>

        let verifyJson =
            Path.Combine(projectDir, "obj", "traced", plain.WeaveKey, "verify.json")

        File.WriteAllText(verifyJson, "{")
        let again = prepared (request projectDir)
        test <@ again.WeaveKey = plain.WeaveKey && not again.Reused @>
        test <@ (prepared (request projectDir)).Reused @>
    finally
        deleteProject projectDir
