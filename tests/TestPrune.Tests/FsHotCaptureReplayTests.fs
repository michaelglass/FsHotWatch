module TestPrune.Tests.FsHotCaptureReplayTests

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open Swensen.Unquote
open TestPrune.AstAnalyzer
open Xunit

let private archivePath =
    Path.Combine(__SOURCE_DIRECTORY__, "Fixtures", "FcsTraversal", "FsHotFaithfulCapture.zip")

let private sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData(stream) |> Convert.ToHexString |> _.ToLowerInvariant()

let private verifyCaptureIntegrity captureRoot =
    let checksumPath = Path.Combine(captureRoot, "SHA256SUMS")
    let checksumLines = File.ReadAllLines checksumPath
    test <@ checksumLines.Length = 97 @>

    for line in checksumLines do
        let separator = line.IndexOf("  ./", StringComparison.Ordinal)
        test <@ separator = 64 @>
        let expected = line.Substring(0, separator)

        let relative =
            line.Substring(separator + 4).Replace('/', Path.DirectorySeparatorChar)

        let path = Path.Combine(captureRoot, relative)
        test <@ File.Exists path @>
        test <@ sha256 path = expected @>

    let sourceCount =
        Directory.EnumerateFiles(captureRoot, "*.fs", SearchOption.AllDirectories)
        |> Seq.length

    let referenceCount =
        Directory.EnumerateFiles(captureRoot, "*.dll", SearchOption.AllDirectories)
        |> Seq.length

    test <@ sourceCount = 82 @>
    test <@ referenceCount = 9 @>

    for optionsPath in Directory.EnumerateFiles(Path.Combine(captureRoot, "options"), "*.opts.txt") do
        let text = File.ReadAllText optionsPath
        test <@ not (text.Contains("/Users/", StringComparison.Ordinal)) @>

        let withoutAllowedPlaceholders =
            text.Replace("${CAPTURE_ROOT}", "").Replace("${NUGET_PACKAGES}", "").Replace("${DOTNET_ROOT}", "")

        test <@ not (withoutAllowedPlaceholders.Contains("${", StringComparison.Ordinal)) @>

let private parseCount (prefix: string) (line: string) =
    if
        not (
            line.StartsWith(prefix, StringComparison.Ordinal)
            && line.EndsWith("):", StringComparison.Ordinal)
        )
    then
        failwith $"Invalid captured-options section: %s{line}"

    Int32.Parse(line.Substring(prefix.Length, line.Length - prefix.Length - 2))

let private parseOptions captureRoot nugetPackages dotnetRoot path =
    let lines = File.ReadAllLines path
    let mutable index = 0

    let next () =
        let value = lines[index]
        index <- index + 1
        value

    let projectLine = next ()
    test <@ projectLine.StartsWith("# Project: ", StringComparison.Ordinal) @>

    let replaceRoots (value: string) =
        value
            .Replace("${CAPTURE_ROOT}", captureRoot, StringComparison.Ordinal)
            .Replace("${NUGET_PACKAGES}", nugetPackages, StringComparison.Ordinal)
            .Replace("${DOTNET_ROOT}", dotnetRoot, StringComparison.Ordinal)

    let readSection prefix =
        let count = next () |> parseCount prefix

        Array.init count (fun _ ->
            let line = next ()
            test <@ line.StartsWith("  ", StringComparison.Ordinal) @>
            line.Substring(2))

    let sourceFiles = readSection "# SourceFiles ("
    let otherOptions = readSection "# OtherOptions ("
    let referencedProjects = readSection "# ReferencedProjects ("
    test <@ index = lines.Length @>

    let resolveSource (value: string) =
        if value.StartsWith("${NUGET_PACKAGES}/", StringComparison.Ordinal) then
            let relative = value.Substring("${NUGET_PACKAGES}/".Length)
            let bundled = Path.Combine(captureRoot, "external", "nuget", relative)
            if File.Exists bundled then bundled else replaceRoots value
        else
            replaceRoots value

    { ProjectFileName = projectLine.Substring("# Project: ".Length) |> replaceRoots
      ProjectId = None
      SourceFiles = sourceFiles |> Array.map resolveSource
      OtherOptions = otherOptions |> Array.map replaceRoots
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UtcNow
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None },
    referencedProjects |> Array.map replaceRoots

let private replay
    (checker: FSharpChecker)
    (captureRoot: string)
    (nugetPackages: string)
    (dotnetRoot: string)
    (optionsName: string)
    (targetRelative: string)
    (expectedUses: int)
    (expectedDefinitions: int)
    =
    let options, referencedProjects =
        parseOptions captureRoot nugetPackages dotnetRoot (Path.Combine(captureRoot, "options", optionsName))

    test <@ options.SourceFiles |> Array.forall File.Exists @>
    // ReferencedProjects is provenance from project cracking. FCS consumes the
    // corresponding compiled `-r:` entries in OtherOptions for this file replay.
    test <@ referencedProjects.Length >= 1 @>

    let missingReferences =
        options.OtherOptions
        |> Array.choose (fun option ->
            if
                option.StartsWith("-r:", StringComparison.Ordinal)
                && not (File.Exists(option.Substring(3)))
            then
                Some(option.Substring(3))
            else
                None)

    if missingReferences.Length > 0 then
        let joined = String.concat "; " missingReferences
        failwith $"Captured options have missing references: %s{joined}"

    let target = Path.Combine(captureRoot, targetRelative)
    let source = File.ReadAllText target

    let parseResults, answer =
        checker.ParseAndCheckFileInProject(target, 0, SourceText.ofString source, options)
        |> Async.RunSynchronously

    let checkResults =
        match answer with
        | FSharpCheckFileAnswer.Succeeded results -> results
        | FSharpCheckFileAnswer.Aborted -> failwith $"Captured FCS replay aborted for %s{targetRelative}"

    let uses = checkResults.GetAllUsesOfAllSymbolsInFile() |> Seq.toArray
    test <@ uses.Length = expectedUses @>
    test <@ uses |> Array.sumBy (fun use' -> if use'.IsFromDefinition then 1 else 0) = expectedDefinitions @>
    source, parseResults, checkResults, uses

[<Collection("FCS-AstAnalyzer")>]
type ``faithful FsHot FCS capture``() =

    [<Fact>]
    member _.``archive is deterministic and semantic replay matches captured measurements``() =
        test <@ File.Exists archivePath @>
        test <@ sha256 archivePath = "c9846328cc5ba32622502d8bae05629beae320c0d0ba66d706bff4f2f7c048cc" @>

        use archive = ZipFile.OpenRead archivePath
        test <@ archive.Entries |> Seq.forall (fun entry -> entry.LastWriteTime.Year = 1980) @>

        let temp =
            Path.Combine(Path.GetTempPath(), $"testprune-fshot-capture-{Guid.NewGuid():N}")

        Directory.CreateDirectory temp |> ignore

        try
            ZipFile.ExtractToDirectory(archivePath, temp, false)
            let captureRoot = Path.Combine(temp, "capture")
            test <@ File.Exists(Path.Combine(captureRoot, "MANIFEST.txt")) @>
            test <@ File.Exists(Path.Combine(captureRoot, "LICENSE.FsHotWatch")) @>
            test <@ File.Exists(Path.Combine(captureRoot, "LICENSE.xunit")) @>
            verifyCaptureIntegrity captureRoot

            let runSemanticReplay () =
                let nugetPackages =
                    Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                    |> Option.ofObj
                    |> Option.defaultValue (
                        Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".nuget",
                            "packages"
                        )
                    )

                let dotnetRoot =
                    Environment.GetEnvironmentVariable("DOTNET_ROOT")
                    |> Option.ofObj
                    |> Option.defaultWith (fun () ->
                        Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                        |> Directory.GetParent
                        |> _.Parent
                        |> _.Parent
                        |> _.Parent
                        |> _.FullName)

                let checker = FSharpChecker.Create(keepAssemblyContents = true)

                let pluginSource, pluginParse, pluginCheck, _ =
                    replay
                        checker
                        captureRoot
                        nugetPackages
                        dotnetRoot
                        "FsHotWatch.TestPrune.opts.txt"
                        "src/FsHotWatch.TestPrune/TestPrunePlugin.fs"
                        7780
                        1134

                let testsSource, testsParse, testsCheck, testUses =
                    replay
                        checker
                        captureRoot
                        nugetPackages
                        dotnetRoot
                        "FsHotWatch.Tests.opts.txt"
                        "tests/FsHotWatch.Tests/TestPrunePluginTests.fs"
                        18593
                        2433

                let analyze maxVisits file source parse check =
                    let result, visits =
                        AnalysisTestHelpers.analyzeSourceFromResultsWithPolicy
                            maxVisits
                            true
                            file
                            source
                            parse
                            check
                            "FsHotCapture"

                    result, visits

                let pluginFile =
                    Path.Combine(captureRoot, "src/FsHotWatch.TestPrune/TestPrunePlugin.fs")

                let testsFile =
                    Path.Combine(captureRoot, "tests/FsHotWatch.Tests/TestPrunePluginTests.fs")

                let pluginResult, pluginVisits =
                    analyze 32768 pluginFile pluginSource pluginParse pluginCheck

                let testsResult, testsVisits =
                    analyze 32768 testsFile testsSource testsParse testsCheck

                test <@ pluginVisits = 7707 @>
                test <@ testsVisits = 27109 @>
                test <@ pluginResult |> Result.isOk @>
                test <@ testsResult |> Result.isOk @>

                test <@ (pluginResult |> Result.map _.Dependencies.Length) = Ok 1548 @>
                test <@ (testsResult |> Result.map _.Dependencies.Length) = Ok 9629 @>

                let lowPlugin, _ = analyze 4096 pluginFile pluginSource pluginParse pluginCheck
                let lowTests, _ = analyze 4096 testsFile testsSource testsParse testsCheck
                test <@ lowPlugin |> Result.isError @>
                test <@ lowTests |> Result.isError @>

                let boundaryTests, _ = analyze 27108 testsFile testsSource testsParse testsCheck
                test <@ boundaryTests |> Result.isError @>

                let uncachedTests, uncachedTestsVisits =
                    AnalysisTestHelpers.analyzeSourceFromResultsWithPolicy
                        32768
                        false
                        testsFile
                        testsSource
                        testsParse
                        testsCheck
                        "FsHotCapture"

                test <@ uncachedTests |> Result.isOk @>
                test <@ uncachedTestsVisits = 27493 @>

                let uncachedDependencies = uncachedTests |> Result.map _.Dependencies
                let cachedDependencies = testsResult |> Result.map _.Dependencies
                test <@ uncachedDependencies = cachedDependencies @>

                let memoThreshold = 27300

                let memoThresholdResult, memoThresholdVisits =
                    analyze memoThreshold testsFile testsSource testsParse testsCheck

                let uncachedThresholdResult, _ =
                    AnalysisTestHelpers.analyzeSourceFromResultsWithPolicy
                        memoThreshold
                        false
                        testsFile
                        testsSource
                        testsParse
                        testsCheck
                        "FsHotCapture"

                test <@ memoThresholdVisits = testsVisits @>
                test <@ memoThresholdResult |> Result.isOk @>
                test <@ uncachedThresholdResult |> Result.isError @>

                let checkFileSymbols =
                    testUses
                    |> Array.filter (fun use' -> use'.Symbol.DisplayName = "CheckFile")
                    |> Array.map _.Symbol

                test <@ checkFileSymbols.Length = 32 @>

                test
                    <@
                        checkFileSymbols
                        |> Array.map (fun symbol -> Runtime.CompilerServices.RuntimeHelpers.GetHashCode symbol)
                        |> Array.distinct
                        |> Array.length = 32
                    @>

                test <@ TestHelpers.testGenericEdgeSemanticKeyDistinctCount checkFileSymbols = 1 @>

                let dependencies =
                    match testsResult with
                    | Ok result -> result
                    | Error message -> failwith message

                test
                    <@
                        dependencies.Dependencies
                        |> List.exists (fun edge ->
                            edge.FromSymbol.EndsWith("withSingleProjectHarness", StringComparison.Ordinal)
                            && edge.ToSymbol.EndsWith("TestConfig", StringComparison.Ordinal))
                    @>

            runSemanticReplay ()
        finally
            Directory.Delete(temp, true)
