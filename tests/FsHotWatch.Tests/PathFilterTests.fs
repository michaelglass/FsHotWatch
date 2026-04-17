module FsHotWatch.Tests.PathFilterTests

open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.PathFilter
open FsHotWatch.Tests.TestHelpers

// --- isGeneratedPath ---

[<Fact(Timeout = 5000)>]
let ``isGeneratedPath returns true for obj directory`` () =
    test <@ isGeneratedPath "/src/obj/Debug/net10.0/AssemblyInfo.fs" @>

[<Fact(Timeout = 5000)>]
let ``isGeneratedPath returns true for bin directory`` () =
    test <@ isGeneratedPath "/src/bin/Release/net10.0/Thing.fs" @>

[<Fact(Timeout = 5000)>]
let ``isGeneratedPath returns false for normal source file`` () =
    test <@ not (isGeneratedPath "/src/MyProject/Program.fs") @>

// --- isExcludedPath with gitignore-style globs ---

[<Fact(Timeout = 5000)>]
let ``isExcludedPath matches glob pattern with wildcard`` () =
    test <@ isExcludedPath [ "vendor/" ] "/repo/vendor/sqlhydra/src/Lib.fs" @>

[<Fact(Timeout = 5000)>]
let ``isExcludedPath does not match unrelated path`` () =
    test <@ not (isExcludedPath [ "vendor/" ] "/repo/src/MyProject/Lib.fs") @>

[<Fact(Timeout = 5000)>]
let ``isExcludedPath matches directory glob`` () =
    test <@ isExcludedPath [ "generated/" ] "/repo/src/generated/Types.fs" @>

[<Fact(Timeout = 5000)>]
let ``isExcludedPath matches file extension glob`` () =
    test <@ isExcludedPath [ "*.fsx" ] "/repo/build.fsx" @>

[<Fact(Timeout = 5000)>]
let ``isExcludedPath matches nested glob pattern`` () =
    test <@ isExcludedPath [ "**/temp/" ] "/repo/src/deep/temp/file.fs" @>

[<Fact(Timeout = 5000)>]
let ``isExcludedPath always excludes obj and bin`` () =
    test <@ isExcludedPath [] "/repo/src/obj/Debug/net10.0/Info.fs" @>
    test <@ isExcludedPath [] "/repo/src/bin/Release/net10.0/Thing.fs" @>

// --- loadIgnoreFile ---

[<Fact(Timeout = 5000)>]
let ``loadIgnoreFile returns matcher that respects gitignore patterns`` () =
    withTempDir "ignorefile" (fun tmpDir ->
        let ignoreFile = Path.Combine(tmpDir, ".testignore")
        File.WriteAllText(ignoreFile, "*.fsx\nvendor/\n")

        let isIgnored = loadIgnoreFile tmpDir ignoreFile
        test <@ isIgnored (Path.Combine(tmpDir, "build.fsx")) @>
        test <@ isIgnored (Path.Combine(tmpDir, "vendor", "lib.fs")) @>
        test <@ not (isIgnored (Path.Combine(tmpDir, "src", "Program.fs"))) @>)

[<Fact(Timeout = 5000)>]
let ``loadIgnoreFile handles comments and blank lines`` () =
    withTempDir "ignorefile-comments" (fun tmpDir ->
        let ignoreFile = Path.Combine(tmpDir, ".testignore")
        File.WriteAllText(ignoreFile, "# This is a comment\n\n*.generated.fs\n")

        let isIgnored = loadIgnoreFile tmpDir ignoreFile
        test <@ isIgnored (Path.Combine(tmpDir, "Foo.generated.fs")) @>
        test <@ not (isIgnored (Path.Combine(tmpDir, "Foo.fs"))) @>)

[<Fact(Timeout = 5000)>]
let ``loadIgnoreFile returns false-for-all when file does not exist`` () =
    withTempDir "ignorefile-missing" (fun tmpDir ->
        let isIgnored = loadIgnoreFile tmpDir (Path.Combine(tmpDir, ".nonexistent"))
        test <@ not (isIgnored (Path.Combine(tmpDir, "anything.fs"))) @>)

// --- collectIgnoreRules ---

[<Fact(Timeout = 5000)>]
let ``collectIgnoreRules combines gitignore and fantomasignore`` () =
    withTempDir "collect-rules" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.fsx\n")
        File.WriteAllText(Path.Combine(tmpDir, ".fantomasignore"), "vendor/\n")

        let isIgnored = collectIgnoreRules tmpDir
        test <@ isIgnored (Path.Combine(tmpDir, "build.fsx")) @>
        test <@ isIgnored (Path.Combine(tmpDir, "vendor", "lib.fs")) @>
        test <@ not (isIgnored (Path.Combine(tmpDir, "src", "Program.fs"))) @>)

[<Fact(Timeout = 5000)>]
let ``collectIgnoreRules works when only gitignore exists`` () =
    withTempDir "collect-gitonly" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.fsx\n")

        let isIgnored = collectIgnoreRules tmpDir
        test <@ isIgnored (Path.Combine(tmpDir, "build.fsx")) @>
        test <@ not (isIgnored (Path.Combine(tmpDir, "src", "Program.fs"))) @>)

[<Fact(Timeout = 5000)>]
let ``collectIgnoreRules works when no ignore files exist`` () =
    withTempDir "collect-none" (fun tmpDir ->
        let isIgnored = collectIgnoreRules tmpDir
        test <@ not (isIgnored (Path.Combine(tmpDir, "anything.fs"))) @>)

// --- IgnoreFilterCache invalidation ---

[<Fact(Timeout = 10000)>]
let ``IgnoreFilterCache reloads when gitignore is modified`` () =
    withTempDir "cache-reload-gitignore" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.log\n")

        let cache = IgnoreFilterCache()
        let filter1 = cache.Get(tmpDir)
        test <@ not (filter1 (Path.Combine(tmpDir, "build.fsx"))) @>
        test <@ filter1 (Path.Combine(tmpDir, "debug.log")) @>

        // Ensure file timestamp changes (some filesystems have 1s resolution)
        Thread.Sleep(1100)
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.fsx\n")

        let filter2 = cache.Get(tmpDir)
        test <@ filter2 (Path.Combine(tmpDir, "build.fsx")) @>
        test <@ not (filter2 (Path.Combine(tmpDir, "debug.log"))) @>)

[<Fact(Timeout = 10000)>]
let ``IgnoreFilterCache reloads when fantomasignore is modified`` () =
    withTempDir "cache-reload-fantomas" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".fantomasignore"), "vendor/\n")

        let cache = IgnoreFilterCache()
        let filter1 = cache.Get(tmpDir)
        test <@ filter1 (Path.Combine(tmpDir, "vendor", "lib.fs")) @>
        test <@ not (filter1 (Path.Combine(tmpDir, "generated", "Types.fs"))) @>

        Thread.Sleep(1100)
        File.WriteAllText(Path.Combine(tmpDir, ".fantomasignore"), "generated/\n")

        let filter2 = cache.Get(tmpDir)
        test <@ not (filter2 (Path.Combine(tmpDir, "vendor", "lib.fs"))) @>
        test <@ filter2 (Path.Combine(tmpDir, "generated", "Types.fs")) @>)

[<Fact(Timeout = 5000)>]
let ``IgnoreFilterCache reloads when ignore file is created`` () =
    withTempDir "cache-reload-created" (fun tmpDir ->
        let cache = IgnoreFilterCache()
        let filter1 = cache.Get(tmpDir)
        test <@ not (filter1 (Path.Combine(tmpDir, "vendor", "lib.fs"))) @>

        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "vendor/\n")

        let filter2 = cache.Get(tmpDir)
        test <@ filter2 (Path.Combine(tmpDir, "vendor", "lib.fs")) @>)

[<Fact(Timeout = 5000)>]
let ``IgnoreFilterCache reloads when ignore file is deleted`` () =
    withTempDir "cache-reload-deleted" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "vendor/\n")

        let cache = IgnoreFilterCache()
        let filter1 = cache.Get(tmpDir)
        test <@ filter1 (Path.Combine(tmpDir, "vendor", "lib.fs")) @>

        File.Delete(Path.Combine(tmpDir, ".gitignore"))

        let filter2 = cache.Get(tmpDir)
        test <@ not (filter2 (Path.Combine(tmpDir, "vendor", "lib.fs"))) @>)

[<Fact(Timeout = 5000)>]
let ``IgnoreFilterCache returns cached result when files unchanged`` () =
    withTempDir "cache-stable" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.log\n")

        let cache = IgnoreFilterCache()
        let filter1 = cache.Get(tmpDir)
        let filter2 = cache.Get(tmpDir)
        // Same object reference — cache hit, not reloaded
        test <@ obj.ReferenceEquals(filter1, filter2) @>)
