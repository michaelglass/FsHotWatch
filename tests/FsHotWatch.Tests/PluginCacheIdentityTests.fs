module FsHotWatch.Tests.PluginCacheIdentityTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

let private under (root: string) (relative: string) =
    Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))

let private fileChecked path source =
    FileChecked
        { fakeFileCheckResult path with
            Source = source }

let private testPruneKey root path source =
    FsHotWatch.TestPrune.TestPrunePlugin.cacheKeyForRoot
        root
        (fun () -> "symbols")
        (fun () -> None)
        (fun () -> None)
        (fun () -> "structure")
        (fun () -> None)
        (fun () -> false)
        (fun () -> true)
        (fileChecked path source)

let private pluginKeys: (string * (string -> string -> string -> ContentHash option)) list =
    [ "build",
      fun root path source ->
          FsHotWatch.Build.BuildPlugin.computeInputsMerkleWith (fun _ -> Some source) root [ path ]
          |> FsHotWatch.Build.BuildPlugin.computeBuildCacheKey "dotnet" "build" []
          |> Some
      "fantomas",
      fun root path source ->
          FsHotWatch.Fantomas.FormatCheckPlugin.formatCheckCacheKeyWith
              (fun _ -> Some source)
              root
              (FileChanged(SourceChanged [ path ]))
      "lint",
      fun root path source -> FsHotWatch.Lint.LintPlugin.lintCacheKeyFor root "tool" "config" (fileChecked path source)
      "analyzers",
      fun root path source ->
          FsHotWatch.Analyzers.AnalyzersPlugin.analyzersCacheKeyFor
              root
              "analyzer-paths"
              "analyzer-content"
              (fileChecked path source)
      "test-prune", testPruneKey
      "file-command",
      fun root path source ->
          FsHotWatch.FileCommand.FileCommandPlugin.fileCommandCacheKeyFromInputs
              root
              "tool"
              "--config config.json"
              [ path, source ]
          |> ContentHash.create
          |> Some ]

[<Fact>]
let ``production plugin keys agree across equivalent checkout roots`` () =
    let rootA = Path.Combine(Path.GetTempPath(), "plugin-cache-root-a")
    let rootB = Path.Combine(Path.GetTempPath(), "plugin-cache-root-b")

    for _name, cacheKey in pluginKeys do
        let keyA = cacheKey rootA (under rootA "src/Feature/File.fs") "same-content"
        let keyB = cacheKey rootB (under rootB "src/Feature/File.fs") "same-content"
        test <@ keyA = keyB @>

[<Fact>]
let ``production plugin keys distinguish path content and external roots`` () =
    let root = Path.Combine(Path.GetTempPath(), "plugin-cache-root")
    let otherRootA = Path.Combine(Path.GetTempPath(), "other-checkout-a")
    let otherRootB = Path.Combine(Path.GetTempPath(), "other-checkout-b")
    let first = under root "src/First.fs"

    for _name, cacheKey in pluginKeys do
        let baseline = cacheKey root first "same-content"
        test <@ baseline <> cacheKey root (under root "src/Second.fs") "same-content" @>
        test <@ baseline <> cacheKey root first "changed-content" @>
        test <@ baseline <> cacheKey root (under otherRootA "src/First.fs") "same-content" @>

        test
            <@
                cacheKey root (under otherRootA "src/First.fs") "same-content"
                <> cacheKey root (under otherRootB "src/First.fs") "same-content"
            @>

[<Fact>]
let ``plugin-specific non-path inputs still invalidate keys`` () =
    let root = Path.Combine(Path.GetTempPath(), "plugin-cache-root")
    let path = under root "src/File.fs"
    let event = fileChecked path "source"

    let inputs =
        FsHotWatch.Build.BuildPlugin.computeInputsMerkleWith (fun _ -> Some "source") root [ path ]

    let buildKey =
        FsHotWatch.Build.BuildPlugin.computeBuildCacheKey "dotnet" "build" [] inputs

    test
        <@
            buildKey
            <> FsHotWatch.Build.BuildPlugin.computeBuildCacheKey "other" "build" [] inputs
        @>

    test
        <@
            buildKey
            <> FsHotWatch.Build.BuildPlugin.computeBuildCacheKey "dotnet" "other" [] inputs
        @>

    test
        <@
            buildKey
            <> FsHotWatch.Build.BuildPlugin.computeBuildCacheKey "dotnet" "build" [ "lint" ] inputs
        @>

    let lintKey =
        FsHotWatch.Lint.LintPlugin.lintCacheKeyFor root "tool" "config-a" event

    test
        <@
            lintKey
            <> FsHotWatch.Lint.LintPlugin.lintCacheKeyFor root "tool" "config-b" event
        @>

    let analyzerKey =
        FsHotWatch.Analyzers.AnalyzersPlugin.analyzersCacheKeyFor root "paths-a" "assemblies-a" event

    test
        <@
            analyzerKey
            <> FsHotWatch.Analyzers.AnalyzersPlugin.analyzersCacheKeyFor root "paths-b" "assemblies-a" event
        @>

    test
        <@
            analyzerKey
            <> FsHotWatch.Analyzers.AnalyzersPlugin.analyzersCacheKeyFor root "paths-a" "assemblies-b" event
        @>

    let commandKey =
        FsHotWatch.FileCommand.FileCommandPlugin.fileCommandCacheKeyFromInputs root "tool" "args-a" [ path, "hash" ]

    test
        <@
            commandKey
            <> FsHotWatch.FileCommand.FileCommandPlugin.fileCommandCacheKeyFromInputs
                root
                "other"
                "args-a"
                [ path, "hash" ]
        @>

    test
        <@
            commandKey
            <> FsHotWatch.FileCommand.FileCommandPlugin.fileCommandCacheKeyFromInputs
                root
                "tool"
                "args-b"
                [ path, "hash" ]
        @>

[<Fact>]
let ``missing analyzer directories have portable identities`` () =
    let rootA = Path.Combine(Path.GetTempPath(), "plugin-cache-root-a")
    let rootB = Path.Combine(Path.GetTempPath(), "plugin-cache-root-b")

    let identityA =
        FsHotWatch.Analyzers.AnalyzersPlugin.analyzerAssemblyIdentityForRoot
            rootA
            [||]
            [ under rootA "analyzers/missing" ]

    let identityB =
        FsHotWatch.Analyzers.AnalyzersPlugin.analyzerAssemblyIdentityForRoot
            rootB
            [||]
            [ under rootB "analyzers/missing" ]

    test <@ identityA = identityB @>

[<Fact>]
let ``analyzer path identity preserves input boundaries`` () =
    let hash = FsHotWatch.Analyzers.AnalyzersPlugin.hashAnalyzerPathIdentities

    test <@ hash [ "a|b"; "c" ] <> hash [ "a"; "b|c" ] @>
