/// A warm daemon must never rely on an analyzer noticing that its configuration
/// changed. These tests load `tests/Fixtures/ConfigCacheAnalyzer` — an analyzer that
/// caches the config it read in a static, forever — through the real load path, edit
/// the config the analyzer reads while the handler stays warm, and read what the
/// analyzer reports next.
///
/// One module, one class: the tests share no state, but they count live load contexts
/// by name and run serially so that count is theirs alone.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.AnalyzerReloadTests

open System
open System.IO
open System.Runtime.Loader
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Analyzers
open FsHotWatch.Tests.TestHelpers

/// The fixture analyzer, built through FsHotWatch.Tests' project reference.
let private fixtureDll =
    let dll =
        Path.Combine(
            AnalyzerFixtures.repoRoot,
            "tests",
            "Fixtures",
            "ConfigCacheAnalyzer",
            "bin",
            "Debug",
            "net10.0",
            "ConfigCacheAnalyzer.dll"
        )

    if not (File.Exists dll) then
        failwith $"fixture analyzer not built at %s{dll}: build tests/FsHotWatch.Tests"

    dll

/// A fresh analyzer directory holding the fixture under `fileName`. The SDK names a
/// load context after the DLL's file name, so a unique name makes this directory's
/// contexts countable. `fileName` must match `*Analyzer*.dll` and must not contain
/// "test" (the SDK skips those).
let private analyzerDir (root: string) (fileName: string) =
    let dir = Path.Combine(root, "bins", Path.GetFileNameWithoutExtension fileName)
    Directory.CreateDirectory dir |> ignore
    File.Copy(fixtureDll, Path.Combine(dir, fileName))
    dir

let private projectOptions: FSharp.Compiler.CodeAnalysis.FSharpProjectOptions =
    { ProjectFileName = "/my/repo/Fixture.fsproj"
      ProjectId = None
      SourceFiles = [||]
      OtherOptions = [||]
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UtcNow
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

/// One finding of the fixture analyzer, parsed from
/// `context=<name> load=<id> editorconfig=<text> fsharplint=<text>`.
type private Report =
    { Context: string
      Load: string
      EditorConfig: string
      FSharpLint: string }

let private parse (message: string) : Report =
    let fields =
        message.Split(' ')
        |> Array.choose (fun part ->
            match part.Split('=', 2) with
            | [| key; value |] -> Some(key, value)
            | _ -> None)
        |> Map.ofArray

    { Context = fields["context"]
      Load = fields["load"]
      EditorConfig = fields["editorconfig"]
      FSharpLint = fields["fsharplint"] }

/// A warm handler over `dirs` in a repository at `root`, with one source file to check.
type private Warm(root: string, dirs: string list) =
    let host = createModelHost (Unchecked.defaultof<_>) root
    let file = Path.Combine(root, "src", "Subject.fs")

    do
        Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
        File.WriteAllText(file, "module Subject\n")
        // Error keeps the fixture's Warning unpromoted, so its text is untouched.
        host.RegisterHandler(AnalyzersPlugin.create (Some root) dirs None DiagnosticSeverity.Error)

    let mutable version = 0L

    /// Check the file again and return what each analyzer reported, by load context.
    member _.Analyze() : Map<string, Report> =
        version <- version + 1L

        host.EmitFileChecked
            { fakeFileCheckResult file with
                Source = "module Subject\n"
                ParseResults = Unchecked.defaultof<_>
                ProjectOptions = projectOptions
                Version = version }

        waitForQuiescent host 15000

        host.GetErrorsByPlugin("analyzers")
        |> Map.toList
        |> List.collect snd
        |> List.filter (fun entry -> entry.Message.StartsWith("context=", StringComparison.Ordinal))
        |> List.map (fun entry -> let report = parse entry.Message in report.Context, report)
        |> Map.ofList

let private write (root: string) (name: string) (content: string) =
    File.WriteAllText(Path.Combine(root, name), content)

/// How many load contexts named `name` are still alive after the collector has had
/// every chance to reclaim the unloaded ones.
let private liveContexts (name: string) (atMost: int) =
    let count () =
        AssemblyLoadContext.All |> Seq.filter (fun c -> c.Name = name) |> Seq.length

    let mutable attempts = 0

    while count () > atMost && attempts < 50 do
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()
        attempts <- attempts + 1

    count ()

[<Fact(Timeout = 60000)>]
let ``a config edit on a warm handler reaches an analyzer that caches its config in a static`` () =
    withTempDir "az-reload-static" (fun root ->
        write root ".editorconfig" "one"
        let warm = Warm(root, [ analyzerDir root "ConfigCacheAnalyzer.dll" ])

        let before = warm.Analyze()["ConfigCacheAnalyzer"]
        test <@ before.EditorConfig = "one" @>

        write root ".editorconfig" "two"
        let after = warm.Analyze()["ConfigCacheAnalyzer"]

        // The cache key moved with the edit, so the analyzer ran again. What it
        // reports must describe the config on disk, not the one it cached before.
        test <@ after.EditorConfig = "two" @>
        test <@ after.Load <> before.Load @>)

[<Fact(Timeout = 60000)>]
let ``an edited config reloads only the analyzer path that reads it`` () =
    withTempDir "az-reload-scoped" (fun root ->
        write root ".editorconfig" "one"
        write root "fsharplint.json" "one"

        // The same analyzer twice, under the two consumers' names: a DLL named for
        // FSharpLint is the fsharplint.json consumer, every other one reads .editorconfig.
        let warm =
            Warm(
                root,
                [ analyzerDir root "ScopedConfigAnalyzer.dll"
                  analyzerDir root "FSharpLintScopedAnalyzer.dll" ]
            )

        let initial = warm.Analyze()
        test <@ initial.Count = 2 @>

        write root ".editorconfig" "two"
        let afterEditorConfig = warm.Analyze()

        // MGA's side reloaded and reads the new file; the FSharpLint side did not reload.
        test
            <@
                afterEditorConfig["ScopedConfigAnalyzer"].Load
                <> initial["ScopedConfigAnalyzer"].Load
            @>

        test <@ afterEditorConfig["ScopedConfigAnalyzer"].EditorConfig = "two" @>
        test <@ afterEditorConfig["FSharpLintScopedAnalyzer"].Load = initial["FSharpLintScopedAnalyzer"].Load @>

        write root "fsharplint.json" "two"
        let afterLint = warm.Analyze()

        test
            <@
                afterLint["FSharpLintScopedAnalyzer"].Load
                <> initial["FSharpLintScopedAnalyzer"].Load
            @>

        test <@ afterLint["FSharpLintScopedAnalyzer"].FSharpLint = "two" @>
        test <@ afterLint["ScopedConfigAnalyzer"].Load = afterEditorConfig["ScopedConfigAnalyzer"].Load @>)

[<Fact(Timeout = 60000)>]
let ``an unchanged config set never reloads, however many files share it`` () =
    withTempDir "az-reload-steady" (fun root ->
        write root ".editorconfig" "one"
        let warm = Warm(root, [ analyzerDir root "SteadyConfigAnalyzer.dll" ])

        let first = warm.Analyze()["SteadyConfigAnalyzer"]
        let second = warm.Analyze()["SteadyConfigAnalyzer"]

        test <@ second.Load = first.Load @>)

[<Fact(Timeout = 60000)>]
let ``a config created where none was reloads its consumer`` () =
    withTempDir "az-reload-created" (fun root ->
        let warm = Warm(root, [ analyzerDir root "CreatedConfigAnalyzer.dll" ])

        let before = warm.Analyze()["CreatedConfigAnalyzer"]
        test <@ before.EditorConfig = "none" @>

        write root ".editorconfig" "new"
        let after = warm.Analyze()["CreatedConfigAnalyzer"]

        test <@ after.EditorConfig = "new" @>)

[<Fact(Timeout = 90000)>]
let ``repeated config edits leave no retired load context alive`` () =
    withTempDir "az-reload-leak" (fun root ->
        write root ".editorconfig" "0"
        let warm = Warm(root, [ analyzerDir root "LeakConfigAnalyzer.dll" ])
        let first = warm.Analyze()["LeakConfigAnalyzer"]

        let loads =
            [ for edit in 1..5 do
                  write root ".editorconfig" (string edit)
                  let report = warm.Analyze()["LeakConfigAnalyzer"]
                  test <@ report.EditorConfig = string edit @>
                  report.Load ]

        // Every edit really reloaded...
        test <@ (first.Load :: loads |> List.distinct |> List.length) = 6 @>
        // ...and every context it retired was unloaded: only the live one remains.
        test <@ liveContexts "LeakConfigAnalyzer" 1 = 1 @>)

[<Fact(Timeout = 90000)>]
let ``a rebuilt analyzer set retires the old load contexts too`` () =
    withTempDir "az-reload-rebuilt" (fun root ->
        let dir = analyzerDir root "RebuiltConfigAnalyzer.dll"
        let warm = Warm(root, [ dir ])
        let before = warm.Analyze()["RebuiltConfigAnalyzer"]

        // The DLL-change reload: another analyzer lands in the same directory.
        File.Copy(AnalyzerFixtures.rulesDll, Path.Combine(dir, Path.GetFileName AnalyzerFixtures.rulesDll))
        let after = warm.Analyze()["RebuiltConfigAnalyzer"]

        test <@ after.Load <> before.Load @>
        test <@ liveContexts "RebuiltConfigAnalyzer" 1 = 1 @>)

[<Fact(Timeout = 15000)>]
let ``a config seen for the first time is not a change; a different digest or a created file is`` () =
    let chain (digest: string option) : (string * (string * string option) list) list =
        [ "editorconfig", [ "editorconfig:.editorconfig", digest ]
          "fsharplint", [ "fsharplint:fsharplint.json", None ] ]

    let seen =
        Map.ofList [ "editorconfig:.editorconfig", Some "a"; "fsharplint:fsharplint.json", None ]

    test <@ List.isEmpty (AnalyzersPlugin.changedConfigLabels Map.empty (chain (Some "a"))) @>
    test <@ List.isEmpty (AnalyzersPlugin.changedConfigLabels seen (chain (Some "a"))) @>
    test <@ AnalyzersPlugin.changedConfigLabels seen (chain (Some "b")) = [ "editorconfig" ] @>
    test <@ AnalyzersPlugin.changedConfigLabels seen (chain None) = [ "editorconfig" ] @>

    let created = [ "fsharplint", [ "fsharplint:fsharplint.json", Some "new" ] ]

    test <@ AnalyzersPlugin.changedConfigLabels seen created = [ "fsharplint" ] @>

[<Fact(Timeout = 15000)>]
let ``fsharplint.json is read by the FSharpLint shim alone, .editorconfig by every other analyzer`` () =
    let path (assemblies: string list) : AnalyzerLoad.LoadedPath =
        { Path = "/p"
          Client =
            FSharp.Analyzers.SDK.Client<FSharp.Analyzers.SDK.CliAnalyzerAttribute, FSharp.Analyzers.SDK.CliContext>()
          Count = assemblies.Length
          Assemblies = assemblies
          Loaders = [] }

    let shim = path [ "FSharpLintAnalyzerShim" ]
    let mga = path [ "MichaelGlass.FSharp.Analyzers" ]
    let both = path [ "FSharpLintAnalyzerShim"; "MichaelGlass.FSharp.Analyzers" ]
    let empty = path []

    test
        <@
            AnalyzersPlugin.readsConfig "fsharplint" shim
            && not (AnalyzersPlugin.readsConfig "editorconfig" shim)
        @>

    test
        <@
            AnalyzersPlugin.readsConfig "editorconfig" mga
            && not (AnalyzersPlugin.readsConfig "fsharplint" mga)
        @>

    test
        <@
            AnalyzersPlugin.readsConfig "editorconfig" both
            && AnalyzersPlugin.readsConfig "fsharplint" both
        @>

    test <@ not (AnalyzersPlugin.readsConfig "editorconfig" empty) @>
    test <@ not (AnalyzersPlugin.readsConfig "unknown" mga) @>

[<Fact(Timeout = 30000)>]
let ``a path loads into collectible contexts that unload`` () =
    withTempDir "az-load-unload" (fun root ->
        let dir = analyzerDir root "UnloadConfigAnalyzer.dll"
        // Not an assembly at all, an assembly with no analyzer in it, and one not built
        // against the SDK: all skipped, and none left loaded.
        File.WriteAllBytes(Path.Combine(dir, "BrokenAnalyzer.dll"), [| 1uy; 2uy; 3uy |])
        File.Copy(typeof<AnalyzerLoad.LoadedPath>.Assembly.Location, Path.Combine(dir, "NoneAnalyzer.dll"))

        File.Copy(
            typeof<Swensen.Unquote.AssertionFailedException>.Assembly.Location,
            Path.Combine(dir, "PlainAnalyzer.dll")
        )

        let loaded = AnalyzerLoad.loadPath (fun _ -> false) dir

        test <@ loaded.Count = 1 @>
        test <@ loaded.Assemblies = [ "UnloadConfigAnalyzer" ] @>

        test
            <@
                AssemblyLoadContext.All
                |> Seq.exists (fun c -> c.Name = "UnloadConfigAnalyzer" && c.IsCollectible)
            @>

        AnalyzerLoad.unload loaded
        test <@ liveContexts "UnloadConfigAnalyzer" 0 = 0 @>)

[<Fact(Timeout = 15000)>]
let ``an excluded analyzer is not registered, and a missing path loads nothing`` () =
    withTempDir "az-load-exclude" (fun root ->
        let dir = analyzerDir root "ExcludedConfigAnalyzer.dll"

        let excluded = AnalyzerLoad.loadPath (fun name -> name = "ConfigCacheAnalyzer") dir
        let missing = AnalyzerLoad.loadPath (fun _ -> false) (Path.Combine(root, "absent"))

        test <@ excluded.Count = 0 && excluded.Loaders.IsEmpty @>
        test <@ missing.Count = 0 && missing.Loaders.IsEmpty @>)

[<Fact(Timeout = 15000)>]
let ``the loader opens what the SDK opens: *Analyzer*.dll, recursively, minus the SDK and test-named files`` () =
    withTempDir "az-load-glob" (fun root ->
        let nested = Path.Combine(root, "nested")
        Directory.CreateDirectory nested |> ignore

        for name in
            [ "OneAnalyzer.dll"
              "FSharp.Analyzers.SDK.dll"
              "MytestAnalyzer.dll"
              "Plain.dll" ] do
            File.WriteAllBytes(Path.Combine(root, name), [||])

        File.WriteAllBytes(Path.Combine(nested, "TwoAnalyzer.dll"), [||])

        test
            <@ AnalyzerLoad.analyzerDlls root |> List.map Path.GetFileName = [ "OneAnalyzer.dll"; "TwoAnalyzer.dll" ] @>)
