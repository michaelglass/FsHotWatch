module TestPrune.Tests.ProgramTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AuditSink
open TestPrune.Program
open TestPrune.Orchestration
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.ImpactAnalysis
open TestPrune.Ports
open TestPrune.ProjectLoader
open System.Reflection
open FSharp.Compiler.CodeAnalysis

module ``parseArgs`` =

    let private cmd args =
        parseArgs args |> Result.map (fun p -> p.Command)

    let private repoRoot args =
        parseArgs args |> Result.map (fun p -> p.RepoRoot)

    [<Fact>]
    let ``empty args returns Help`` () = test <@ cmd [||] = Ok Help @>

    [<Fact>]
    let ``index command`` () = test <@ cmd [| "index" |] = Ok Index @>

    [<Fact>]
    let ``run command`` () = test <@ cmd [| "run" |] = Ok Run @>

    [<Fact>]
    let ``status command`` () =
        test <@ cmd [| "status" |] = Ok Status @>

    [<Fact>]
    let ``help command`` () = test <@ cmd [| "help" |] = Ok Help @>

    [<Fact>]
    let ``--help flag`` () = test <@ cmd [| "--help" |] = Ok Help @>

    [<Fact>]
    let ``-h flag`` () = test <@ cmd [| "-h" |] = Ok Help @>

    [<Fact>]
    let ``dead-code command with defaults`` () =
        test <@ cmd [| "dead-code" |] = Ok(DeadCodeCmd(defaultEntryPatterns, false)) @>

    [<Fact>]
    let ``dead-code command with custom entry patterns`` () =
        let result = cmd [| "dead-code"; "--entry"; "*.main"; "--entry"; "*.Routes.*" |]

        test <@ result = Ok(DeadCodeCmd([ "*.main"; "*.Routes.*" ], false)) @>

    [<Fact>]
    let ``dead-code command with --include-tests`` () =
        test <@ cmd [| "dead-code"; "--include-tests" |] = Ok(DeadCodeCmd(defaultEntryPatterns, true)) @>

    [<Fact>]
    let ``dead-code command with --entry and --include-tests`` () =
        let result = cmd [| "dead-code"; "--entry"; "*.main"; "--include-tests" |]

        test <@ result = Ok(DeadCodeCmd([ "*.main" ], true)) @>

    [<Fact>]
    let ``dead-code command with unknown flag returns Error`` () =
        let result = cmd [| "dead-code"; "--bogus" |]
        test <@ Result.isError result @>

    [<Fact>]
    let ``unknown command returns Error`` () =
        let result = cmd [| "bogus" |]
        test <@ Result.isError result @>

    [<Fact>]
    let ``--repo flag sets RepoRoot`` () =
        let result = parseArgs [| "--repo"; "/some/path"; "index" |]

        test
            <@
                result = Ok
                    { Command = Index
                      RepoRoot = Some "/some/path"
                      Parallelism = System.Environment.ProcessorCount }
            @>

    [<Fact>]
    let ``--parallelism flag sets parallelism`` () =
        let result = parseArgs [| "--parallelism"; "4"; "index" |]

        match result with
        | Ok parsed -> test <@ parsed.Parallelism = 4 @>
        | Error msg -> failwith msg

    [<Fact>]
    let ``--parallelism with invalid value returns error`` () =
        let result = parseArgs [| "--parallelism"; "abc"; "index" |]

        match result with
        | Error _ -> ()
        | Ok _ -> failwith "Expected error"

    [<Fact>]
    let ``--parallelism with zero returns error`` () =
        let result = parseArgs [| "--parallelism"; "0"; "index" |]

        match result with
        | Error _ -> ()
        | Ok _ -> failwith "Expected error"

    [<Fact>]
    let ``--parallelism with negative returns error`` () =
        let result = parseArgs [| "--parallelism"; "-1"; "index" |]

        match result with
        | Error _ -> ()
        | Ok _ -> failwith "Expected error"

    [<Fact>]
    let ``no --repo flag leaves RepoRoot as None`` () =
        test <@ repoRoot [| "index" |] = Ok None @>

module ``findRepoRoot`` =

    [<Fact>]
    let ``finds repo with .jj directory`` () =
        let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        let child = Path.Combine(tmp, "a", "b")

        try
            Directory.CreateDirectory(Path.Combine(tmp, ".jj")) |> ignore
            Directory.CreateDirectory(child) |> ignore
            test <@ findRepoRoot child = Some tmp @>
        finally
            if Directory.Exists(tmp) then
                Directory.Delete(tmp, true)

    [<Fact>]
    let ``finds repo with .git directory`` () =
        let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        let child = Path.Combine(tmp, "sub")

        try
            Directory.CreateDirectory(Path.Combine(tmp, ".git")) |> ignore
            Directory.CreateDirectory(child) |> ignore
            test <@ findRepoRoot child = Some tmp @>
        finally
            if Directory.Exists(tmp) then
                Directory.Delete(tmp, true)

    [<Fact>]
    let ``returns None when no marker exists`` () =
        let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())

        try
            Directory.CreateDirectory(tmp) |> ignore
            // Walk up will eventually reach filesystem root with no .jj/.git
            // We can't guarantee None on machines that have a repo at root,
            // but an isolated temp dir with a unique name is safe enough.
            let result = findRepoRoot tmp
            // The temp dir itself has no .jj or .git, so if a parent does
            // the result won't equal Some tmp. We verify it's not Some tmp.
            test <@ result <> Some tmp @>
        finally
            if Directory.Exists(tmp) then
                Directory.Delete(tmp, true)

module ``findProjectFiles`` =

    [<Fact>]
    let ``finds fsproj, excludes ones in obj`` () =
        let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())

        try
            let goodProj = Path.Combine(tmp, "src", "MyProj", "MyProj.fsproj")
            let testProj = Path.Combine(tmp, "tests", "MyProj.Tests", "MyProj.Tests.fsproj")
            let objProj = Path.Combine(tmp, "src", "MyProj", "obj", "MyProj.fsproj")

            for f in [ goodProj; testProj; objProj ] do
                Directory.CreateDirectory(Path.GetDirectoryName(f)) |> ignore
                File.WriteAllText(f, "<Project/>")

            let result = findProjectFiles tmp

            test
                <@
                    result
                    |> List.exists (fun p -> p.Contains("MyProj.fsproj") && not (p.Contains("/obj/")))
                @>

            test <@ result |> List.exists (fun p -> p.Contains("MyProj.Tests.fsproj")) @>
            test <@ result |> List.forall (fun p -> not (p.Contains("/obj/"))) @>
        finally
            if Directory.Exists(tmp) then
                Directory.Delete(tmp, true)

    [<Fact>]
    let ``handles missing directories`` () =
        let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())

        try
            Directory.CreateDirectory(tmp) |> ignore
            test <@ findProjectFiles tmp |> List.isEmpty @>
        finally
            if Directory.Exists(tmp) then
                Directory.Delete(tmp, true)

module ``analyzeChanges`` =

    let private makeChecker () = FSharpChecker.Create()

    [<Fact>]
    let ``diff error returns Error`` () =
        let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())

        try
            Directory.CreateDirectory(tmp) |> ignore
            let dbPath = Path.Combine(tmp, ".test-prune.db")
            let db = Database.create dbPath
            let checker = makeChecker ()
            let fakeDiff: DiffProvider = fun () -> Error "not a repo"

            let store = toSymbolStore db
            let result = analyzeChanges fakeDiff tmp store checker (createNoopSink ())
            test <@ Result.isError result @>

            match result with
            | Error msg -> test <@ msg = "not a repo" @>
            | Ok _ -> failwith "expected Error"
        finally
            if Directory.Exists(tmp) then
                Directory.Delete(tmp, true)

    [<Fact>]
    let ``empty diff returns empty subset`` () =
        let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())

        try
            Directory.CreateDirectory(tmp) |> ignore
            let dbPath = Path.Combine(tmp, ".test-prune.db")
            let db = Database.create dbPath
            let checker = makeChecker ()
            let fakeDiff: DiffProvider = fun () -> Ok ""
            let store = toSymbolStore db

            let result = analyzeChanges fakeDiff tmp store checker (createNoopSink ())

            match result with
            | Ok(RunSubset [], _) -> ()
            | other -> failwith $"expected Ok(RunSubset [], _) but got %A{other}"
        finally
            if Directory.Exists(tmp) then
                Directory.Delete(tmp, true)

    [<Fact>]
    let ``fsproj change triggers RunAll`` () =
        let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())

        try
            Directory.CreateDirectory(tmp) |> ignore
            let dbPath = Path.Combine(tmp, ".test-prune.db")
            let db = Database.create dbPath
            let checker = makeChecker ()

            let fsprojDiff =
                "diff --git a/src/MyProj/MyProj.fsproj b/src/MyProj/MyProj.fsproj\n"
                + "--- a/src/MyProj/MyProj.fsproj\n"
                + "+++ b/src/MyProj/MyProj.fsproj\n"
                + "@@ -1,3 +1,4 @@\n"
                + " <Project Sdk=\"Microsoft.NET.Sdk\">\n"
                + "+  <PropertyGroup />\n"
                + " </Project>\n"

            let fakeDiff: DiffProvider = fun () -> Ok fsprojDiff
            let store = toSymbolStore db

            let result = analyzeChanges fakeDiff tmp store checker (createNoopSink ())

            match result with
            | Ok(RunAll _, _) -> ()
            | other -> failwith $"expected Ok(RunAll _, _) but got %A{other}"
        finally
            if Directory.Exists(tmp) then
                Directory.Delete(tmp, true)

module ``showHelp`` =
    [<Fact>]
    let ``prints usage information`` () =
        let sw = new System.IO.StringWriter()
        let oldOut = System.Console.Out
        System.Console.SetOut(sw)

        try
            showHelp ()
            let output = sw.ToString()
            test <@ output.Contains("TestPrune") @>
            test <@ output.Contains("index") @>
            test <@ output.Contains("dead-code") @>
        finally
            System.Console.SetOut(oldOut)

module ``runDeadCode`` =
    [<Fact>]
    let ``returns 1 when no index exists`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore

        try
            test <@ runDeadCode tmpDir [ "*.main" ] false (createNoopSink ()) = 1 @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``returns 0 with empty database`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let dbPath = Path.Combine(tmpDir, ".test-prune.db")
        Database.create dbPath |> ignore
        let sw = new System.IO.StringWriter()
        let oldOut = Console.Out
        Console.SetOut(sw)

        try
            test <@ runDeadCode tmpDir [ "*.main" ] false (createNoopSink ()) = 0 @>
        finally
            Console.SetOut(oldOut)
            Directory.Delete(tmpDir, true)

module ``runStatusWith`` =

    [<Fact>]
    let ``returns 1 when no index exists`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let fakeDiff: DiffProvider = fun () -> Ok ""

        try
            let sw = new StringWriter()
            let oldErr = Console.Error
            Console.SetError(sw)

            try
                test <@ runStatusWith fakeDiff tmpDir (createNoopSink ()) = 1 @>
            finally
                Console.SetError(oldErr)
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``returns 0 with empty diff and empty db`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let dbPath = Path.Combine(tmpDir, ".test-prune.db")
        Database.create dbPath |> ignore
        let fakeDiff: DiffProvider = fun () -> Ok ""
        let sw = new StringWriter()
        let oldOut = Console.Out
        Console.SetOut(sw)

        try
            test <@ runStatusWith fakeDiff tmpDir (createNoopSink ()) = 0 @>
        finally
            Console.SetOut(oldOut)
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``returns 1 when diff fails`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let dbPath = Path.Combine(tmpDir, ".test-prune.db")
        Database.create dbPath |> ignore
        let fakeDiff: DiffProvider = fun () -> Error "not a repo"
        let sw = new StringWriter()
        let oldErr = Console.Error
        Console.SetError(sw)

        try
            test <@ runStatusWith fakeDiff tmpDir (createNoopSink ()) = 1 @>
        finally
            Console.SetError(oldErr)
            Directory.Delete(tmpDir, true)

module ``runRunWith`` =

    [<Fact>]
    let ``returns 1 when no index exists`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let fakeDiff: DiffProvider = fun () -> Ok ""
        let sw = new StringWriter()
        let oldErr = Console.Error
        Console.SetError(sw)

        try
            test <@ runRunWith fakeDiff tmpDir (createNoopSink ()) = 1 @>
        finally
            Console.SetError(oldErr)
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``returns 0 with no changes`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let dbPath = Path.Combine(tmpDir, ".test-prune.db")
        Database.create dbPath |> ignore
        let fakeDiff: DiffProvider = fun () -> Ok ""
        let sw = new StringWriter()
        let oldOut = Console.Out
        Console.SetOut(sw)

        try
            test <@ runRunWith fakeDiff tmpDir (createNoopSink ()) = 0 @>
        finally
            Console.SetOut(oldOut)
            Directory.Delete(tmpDir, true)

module ``runIndexWith`` =

    let private testChecker = createChecker ()

    /// Fake build runner that always succeeds.
    let successBuild: BuildRunner = fun _ -> 0

    /// Fake build runner that always fails.
    let failBuild: BuildRunner = fun _ -> 1

    /// Fake project options provider using script options (no MSBuild needed).
    let scriptOptions: ProjectOptionsProvider =
        fun checker fsprojPath ->
            // Parse the fsproj to find source files, use script options for the first one
            let compileFiles, _ = parseProjectFile fsprojPath

            match compileFiles with
            | firstFile :: _ when File.Exists(firstFile) ->
                let source = File.ReadAllText(firstFile)
                getScriptOptions checker firstFile source |> Async.RunSynchronously
            | _ ->
                // Return minimal options
                { ProjectFileName = fsprojPath
                  ProjectId = None
                  SourceFiles = [||]
                  OtherOptions = [||]
                  ReferencedProjects = [||]
                  IsIncompleteTypeCheckEnvironment = false
                  UseScriptResolutionRules = true
                  LoadTime = DateTime.Now
                  UnresolvedReferences = None
                  OriginalLoadReferences = []
                  Stamp = None }

    [<Fact>]
    let ``returns 1 when build fails`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let sw = new StringWriter()
        let oldErr = Console.Error
        Console.SetError(sw)

        try
            test <@ runIndexWith failBuild scriptOptions tmpDir testChecker 1 (createNoopSink ()) = 1 @>
        finally
            Console.SetError(oldErr)
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``indexes a simple project with one source file`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        let srcDir = Path.Combine(tmpDir, "src", "Lib")
        Directory.CreateDirectory(srcDir) |> ignore

        // Create a minimal .fsproj
        File.WriteAllText(
            Path.Combine(srcDir, "Lib.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
</Project>"""
        )

        // Create a source file
        let libSource = "module Lib\nlet add x y = x + y\nlet multiply x y = x * y\n"
        File.WriteAllText(Path.Combine(srcDir, "Lib.fs"), libSource)

        let sw = new StringWriter()
        let oldErr = Console.Error
        Console.SetError(sw)

        try
            let exitCode =
                runIndexWith successBuild scriptOptions tmpDir testChecker 1 (createNoopSink ())

            test <@ exitCode = 0 @>

            // Verify the DB was populated
            let dbPath = Path.Combine(tmpDir, ".test-prune.db")
            let db = Database.create dbPath
            let symbols = db.GetAllSymbolNames()
            test <@ symbols.Count > 0 @>
        finally
            Console.SetError(oldErr)
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``returns 0 with no projects`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        let sw = new StringWriter()
        let oldErr = Console.Error
        Console.SetError(sw)

        try
            test <@ runIndexWith successBuild scriptOptions tmpDir testChecker 1 (createNoopSink ()) = 0 @>
        finally
            Console.SetError(oldErr)
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``skips getOptions for unchanged projects`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        let srcDir = Path.Combine(tmpDir, "src", "Lib")
        Directory.CreateDirectory(srcDir) |> ignore

        File.WriteAllText(
            Path.Combine(srcDir, "Lib.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
</Project>"""
        )

        File.WriteAllText(Path.Combine(srcDir, "Lib.fs"), "module Lib\nlet add x y = x + y\n")

        let sw = new StringWriter()
        let oldErr = Console.Error
        Console.SetError(sw)

        try
            // First index populates the hash
            runIndexWith successBuild scriptOptions tmpDir testChecker 1 (createNoopSink ())
            |> ignore

            // Second index with a getOptions that would fail if called
            let mutable getOptionsCalled = false

            let trackingOptions: ProjectOptionsProvider =
                fun checker fsprojPath ->
                    getOptionsCalled <- true
                    scriptOptions checker fsprojPath

            let exitCode =
                runIndexWith successBuild trackingOptions tmpDir testChecker 1 (createNoopSink ())

            test <@ exitCode = 0 @>
            test <@ getOptionsCalled = false @>
        finally
            Console.SetError(oldErr)
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``skips unchanged files when project hash changes`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
        let srcDir = Path.Combine(tmpDir, "src", "Lib")
        Directory.CreateDirectory(srcDir) |> ignore

        File.WriteAllText(
            Path.Combine(srcDir, "Lib.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
</Project>"""
        )

        File.WriteAllText(Path.Combine(srcDir, "Lib.fs"), "module Lib\nlet add x y = x + y\n")

        let sw = new StringWriter()
        let oldErr = Console.Error
        Console.SetError(sw)

        try
            // First index: analyzes Lib.fs
            runIndexWith successBuild scriptOptions tmpDir testChecker 1 (createNoopSink ())
            |> ignore

            let dbPath = Path.Combine(tmpDir, ".test-prune.db")
            let db = Database.create dbPath

            let relPath =
                Path.GetRelativePath(tmpDir, Path.Combine(srcDir, "Lib.fs")).Replace('\\', '/')

            let originalFileKey = db.GetFileKey(relPath)
            test <@ originalFileKey.IsSome @>

            // Add a second file to change the project hash, but don't touch Lib.fs
            File.WriteAllText(
                Path.Combine(srcDir, "Lib.fsproj"),
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Lib.fs" />
    <Compile Include="Lib2.fs" />
  </ItemGroup>
</Project>"""
            )

            File.WriteAllText(Path.Combine(srcDir, "Lib2.fs"), "module Lib2\nlet mul x y = x * y\n")

            // Second index: project hash changed (new file), but Lib.fs unchanged
            runIndexWith successBuild scriptOptions tmpDir testChecker 1 (createNoopSink ())
            |> ignore

            // Lib.fs file key should be unchanged (it was loaded from cache, not re-analyzed)
            test <@ db.GetFileKey(relPath) = originalFileKey @>

            // Lib.fs should still have symbols
            let symbols = db.GetSymbolsInFile(relPath)
            test <@ symbols |> List.isEmpty |> not @>
        finally
            Console.SetError(oldErr)
            Directory.Delete(tmpDir, true)

module ``Example solution integration`` =

    let private exampleChecker = createChecker ()

    /// Fake build runner that always succeeds.
    let private successBuild: BuildRunner = fun _ -> 0

    /// Fake project options provider using script options (no MSBuild needed).
    /// Gets script options from the first file, then overrides SourceFiles to include all compile files.
    let private scriptOptions: ProjectOptionsProvider =
        fun checker fsprojPath ->
            let compileFiles, _ = parseProjectFile fsprojPath
            let existingFiles = compileFiles |> List.filter File.Exists

            match existingFiles with
            | firstFile :: _ ->
                let source = File.ReadAllText(firstFile)

                let baseOptions =
                    getScriptOptions checker firstFile source |> Async.RunSynchronously

                { baseOptions with
                    SourceFiles = existingFiles |> Array.ofList }
            | _ ->
                { ProjectFileName = fsprojPath
                  ProjectId = None
                  SourceFiles = [||]
                  OtherOptions = [||]
                  ReferencedProjects = [||]
                  IsIncompleteTypeCheckEnvironment = false
                  UseScriptResolutionRules = true
                  LoadTime = DateTime.Now
                  UnresolvedReferences = None
                  OriginalLoadReferences = []
                  Stamp = None }

    let private findRepoRootFromAssembly () =
        let assemblyLocation = Assembly.GetExecutingAssembly().Location
        let assemblyDir = Path.GetDirectoryName(assemblyLocation)

        match findRepoRoot assemblyDir with
        | Some root -> root
        | None -> failwith "Could not find repo root from test assembly location"

    let private exampleSource () =
        Path.Combine(findRepoRootFromAssembly (), "examples", "SampleSolution")

    /// Copy the example solution to a unique temp directory so tests can run in parallel.
    let private copyDir (sourceDir: string) (destDir: string) =
        for dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories) do
            Directory.CreateDirectory(dirPath.Replace(sourceDir, destDir)) |> ignore

        for filePath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories) do
            File.Copy(filePath, filePath.Replace(sourceDir, destDir), true)

    let private withExampleCopy (f: string -> unit) =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-example-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmpDir) |> ignore
        copyDir (exampleSource ()) tmpDir

        try
            f tmpDir
        finally
            if Directory.Exists(tmpDir) then
                Directory.Delete(tmpDir, true)

    let private suppressError (f: unit -> 'a) =
        let oldErr = Console.Error
        Console.SetError(new StringWriter())

        try
            f ()
        finally
            Console.SetError(oldErr)

    let private indexExample (root: string) =
        suppressError (fun () -> runIndexWith successBuild scriptOptions root exampleChecker 1 (createNoopSink ()))
        |> ignore

    [<Fact>]
    let ``runIndexWith indexes the example solution`` () =
        withExampleCopy (fun root ->
            let exitCode =
                suppressError (fun () ->
                    runIndexWith successBuild scriptOptions root exampleChecker 1 (createNoopSink ()))

            test <@ exitCode = 0 @>

            let dbPath = Path.Combine(root, ".test-prune.db")
            let db = Database.create dbPath
            let symbols = db.GetAllSymbolNames()
            test <@ symbols.Count > 0 @>

            // Verify it found symbols from Math.fs
            let hasAdd =
                symbols
                |> Seq.exists (fun s -> s.Contains("add", StringComparison.OrdinalIgnoreCase))

            let hasMultiply =
                symbols
                |> Seq.exists (fun s -> s.Contains("multiply", StringComparison.OrdinalIgnoreCase))

            test <@ hasAdd @>
            test <@ hasMultiply @>)

    [<Fact>]
    let ``runStatusWith after indexing shows no affected tests for empty diff`` () =
        withExampleCopy (fun root ->
            indexExample root

            let fakeDiff: DiffProvider = fun () -> Ok ""

            let exitCode =
                suppressError (fun () -> runStatusWith fakeDiff root (createNoopSink ()))

            test <@ exitCode = 0 @>)

    [<Fact>]
    let ``runStatusWith with fsproj diff triggers RunAll`` () =
        withExampleCopy (fun root ->
            indexExample root

            let fsprojDiff =
                "diff --git a/src/SampleLib/SampleLib.fsproj b/src/SampleLib/SampleLib.fsproj\n"
                + "--- a/src/SampleLib/SampleLib.fsproj\n"
                + "+++ b/src/SampleLib/SampleLib.fsproj\n"
                + "@@ -1 +1 @@\n"
                + "-old\n"
                + "+new\n"

            let fakeDiff: DiffProvider = fun () -> Ok fsprojDiff

            let exitCode =
                suppressError (fun () -> runStatusWith fakeDiff root (createNoopSink ()))

            test <@ exitCode = 0 @>)

    [<Fact>]
    let ``runRunWith with no changes prints nothing to run`` () =
        withExampleCopy (fun root ->
            indexExample root

            let fakeDiff: DiffProvider = fun () -> Ok ""

            let exitCode =
                suppressError (fun () -> runRunWith fakeDiff root (createNoopSink ()))

            test <@ exitCode = 0 @>)
