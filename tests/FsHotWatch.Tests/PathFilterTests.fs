module FsHotWatch.Tests.PathFilterTests

open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.PathFilter
open FsHotWatch.Tests.TestHelpers

// --- isGeneratedPath ---

[<Fact(Timeout = 15000)>]
let ``isGeneratedPath returns true for obj directory`` () =
    test <@ isGeneratedPath "/src/obj/Debug/net10.0/AssemblyInfo.fs" @>

[<Fact(Timeout = 15000)>]
let ``isGeneratedPath returns true for bin directory`` () =
    test <@ isGeneratedPath "/src/bin/Release/net10.0/Thing.fs" @>

[<Fact(Timeout = 15000)>]
let ``isGeneratedPath returns false for normal source file`` () =
    test <@ not (isGeneratedPath "/src/MyProject/Program.fs") @>

// --- isOutsideRepo ---

[<Fact(Timeout = 15000)>]
let ``isOutsideRepo true for NuGet-cache _content compile item`` () =
    // A package injects source into the consumer from the ~/.nuget cache
    // (xunit.v3's _content) — outside the repo, so never ours to lint.
    test
        <@
            isOutsideRepo
                "/home/dev/myrepo"
                "/home/dev/.nuget/packages/xunit.v3.core.mtp-v1/3.2.2/_content/DefaultRunnerReporters.fs"
        @>

[<Fact(Timeout = 15000)>]
let ``isOutsideRepo false for repo-owned source`` () =
    test <@ not (isOutsideRepo "/home/dev/myrepo" "/home/dev/myrepo/src/File.fs") @>
    test <@ not (isOutsideRepo "/home/dev/myrepo" "/home/dev/myrepo/tests/T.fs") @>

// --- isOutsideRepoScoped (the Option-lifted form the report plugins call) ---

[<Fact(Timeout = 15000)>]
let ``isOutsideRepoScoped None includes everything (includeOutsideRepo override)`` () =
    // None = no repo scope → nothing is "outside", so nothing is skipped.
    test <@ not (isOutsideRepoScoped None "/home/dev/.nuget/packages/x/3.2.2/_content/A.fs") @>
    test <@ not (isOutsideRepoScoped None "/home/dev/myrepo/src/File.fs") @>

[<Fact(Timeout = 15000)>]
let ``isOutsideRepoScoped Some skips out-of-repo but keeps repo-owned`` () =
    test <@ isOutsideRepoScoped (Some "/home/dev/myrepo") "/home/dev/.nuget/packages/x/3.2.2/_content/A.fs" @>
    test <@ not (isOutsideRepoScoped (Some "/home/dev/myrepo") "/home/dev/myrepo/src/File.fs") @>

// --- isExcludedPath with gitignore-style globs ---

[<Fact(Timeout = 15000)>]
let ``isExcludedPath matches glob pattern with wildcard`` () =
    test <@ isExcludedPath "/repo" [ "vendor/" ] "/repo/vendor/sqlhydra/src/Lib.fs" @>

[<Fact(Timeout = 15000)>]
let ``isExcludedPath does not match unrelated path`` () =
    test <@ not (isExcludedPath "/repo" [ "vendor/" ] "/repo/src/MyProject/Lib.fs") @>

[<Fact(Timeout = 15000)>]
let ``isExcludedPath matches directory glob`` () =
    test <@ isExcludedPath "/repo" [ "generated/" ] "/repo/src/generated/Types.fs" @>

[<Fact(Timeout = 15000)>]
let ``isExcludedPath matches file extension glob`` () =
    test <@ isExcludedPath "/repo" [ "*.fsx" ] "/repo/build.fsx" @>

[<Fact(Timeout = 15000)>]
let ``isExcludedPath matches nested glob pattern`` () =
    test <@ isExcludedPath "/repo" [ "**/temp/" ] "/repo/src/deep/temp/file.fs" @>

[<Fact(Timeout = 15000)>]
let ``isExcludedPath always excludes obj and bin`` () =
    test <@ isExcludedPath "/repo" [] "/repo/src/obj/Debug/net10.0/Info.fs" @>
    test <@ isExcludedPath "/repo" [] "/repo/src/bin/Release/net10.0/Thing.fs" @>

// --- isExcludedPath: gitignore patterns must be matched against repo-relative paths ---
// Regression: previously matched against absolute paths, so a pattern like
// `.workspaces/` would match every absolute path that contains that segment,
// even when the repo root was inside `.workspaces/` itself.

[<Fact(Timeout = 15000)>]
let ``isExcludedPath does not match files outside repo root`` () =
    // File lives at /somewhere/elsewhere/.workspaces/foo/bar.fsproj, but the
    // repo root is /somewhere/myrepo. Pattern .workspaces/ is repo-relative and
    // must not match files outside the repo.
    test
        <@ not (isExcludedPath "/somewhere/myrepo" [ ".workspaces/" ] "/somewhere/elsewhere/.workspaces/foo/bar.fsproj") @>

[<Fact(Timeout = 15000)>]
let ``isExcludedPath matches files at repo-root-relative path`` () =
    test <@ isExcludedPath "/somewhere/myrepo" [ ".workspaces/" ] "/somewhere/myrepo/.workspaces/foo/bar.fsproj" @>

[<Fact(Timeout = 15000)>]
let ``isExcludedPath does not match the repo-root parent directory`` () =
    // Path.GetRelativePath("/a/b", "/a") returns "..": confirm that a path
    // exactly one level above the repo root is treated as outside the repo.
    test <@ not (isExcludedPath "/a/b" [ "*.fs" ] "/a") @>

[<Fact(Timeout = 15000)>]
let ``isExcludedPath does not match when repo root is inside the excluded directory`` () =
    // Stress-test scenario: repo IS the workspace, so paths relative to it
    // do not contain `.workspaces/`. Pattern must not match anything in here.
    test
        <@
            not (
                isExcludedPath
                    "/somewhere/myrepo/.workspaces/sub"
                    [ ".workspaces/" ]
                    "/somewhere/myrepo/.workspaces/sub/Project.fsproj"
            )
        @>

// --- loadIgnoreFile ---

[<Fact(Timeout = 15000)>]
let ``loadIgnoreFile returns matcher that respects gitignore patterns`` () =
    withTempDir "ignorefile" (fun tmpDir ->
        let ignoreFile = Path.Combine(tmpDir, ".testignore")
        File.WriteAllText(ignoreFile, "*.fsx\nvendor/\n")

        let isIgnored = loadIgnoreFile tmpDir ignoreFile
        test <@ isIgnored (Path.Combine(tmpDir, "build.fsx")) @>
        test <@ isIgnored (Path.Combine(tmpDir, "vendor", "lib.fs")) @>
        test <@ not (isIgnored (Path.Combine(tmpDir, "src", "Program.fs"))) @>)

[<Fact(Timeout = 15000)>]
let ``loadIgnoreFile handles comments and blank lines`` () =
    withTempDir "ignorefile-comments" (fun tmpDir ->
        let ignoreFile = Path.Combine(tmpDir, ".testignore")
        File.WriteAllText(ignoreFile, "# This is a comment\n\n*.generated.fs\n")

        let isIgnored = loadIgnoreFile tmpDir ignoreFile
        test <@ isIgnored (Path.Combine(tmpDir, "Foo.generated.fs")) @>
        test <@ not (isIgnored (Path.Combine(tmpDir, "Foo.fs"))) @>)

[<Fact(Timeout = 15000)>]
let ``loadIgnoreFile returns false-for-all when file does not exist`` () =
    withTempDir "ignorefile-missing" (fun tmpDir ->
        let isIgnored = loadIgnoreFile tmpDir (Path.Combine(tmpDir, ".nonexistent"))
        test <@ not (isIgnored (Path.Combine(tmpDir, "anything.fs"))) @>)

// --- collectIgnoreRules ---

[<Fact(Timeout = 15000)>]
let ``collectIgnoreRules combines gitignore and fantomasignore`` () =
    withTempDir "collect-rules" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.fsx\n")
        File.WriteAllText(Path.Combine(tmpDir, ".fantomasignore"), "vendor/\n")

        let isIgnored = collectIgnoreRules tmpDir
        test <@ isIgnored (Path.Combine(tmpDir, "build.fsx")) @>
        test <@ isIgnored (Path.Combine(tmpDir, "vendor", "lib.fs")) @>
        test <@ not (isIgnored (Path.Combine(tmpDir, "src", "Program.fs"))) @>)

[<Fact(Timeout = 15000)>]
let ``collectIgnoreRules works when only gitignore exists`` () =
    withTempDir "collect-gitonly" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.fsx\n")

        let isIgnored = collectIgnoreRules tmpDir
        test <@ isIgnored (Path.Combine(tmpDir, "build.fsx")) @>
        test <@ not (isIgnored (Path.Combine(tmpDir, "src", "Program.fs"))) @>)

[<Fact(Timeout = 15000)>]
let ``collectIgnoreRules works when no ignore files exist`` () =
    withTempDir "collect-none" (fun tmpDir ->
        let isIgnored = collectIgnoreRules tmpDir
        test <@ not (isIgnored (Path.Combine(tmpDir, "anything.fs"))) @>)

// --- IgnoreFilterCache invalidation ---

[<Fact(Timeout = 20000)>]
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

[<Fact(Timeout = 20000)>]
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

[<Fact(Timeout = 15000)>]
let ``IgnoreFilterCache reloads when ignore file is created`` () =
    withTempDir "cache-reload-created" (fun tmpDir ->
        let cache = IgnoreFilterCache()
        let filter1 = cache.Get(tmpDir)
        test <@ not (filter1 (Path.Combine(tmpDir, "vendor", "lib.fs"))) @>

        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "vendor/\n")

        let filter2 = cache.Get(tmpDir)
        test <@ filter2 (Path.Combine(tmpDir, "vendor", "lib.fs")) @>)

[<Fact(Timeout = 15000)>]
let ``IgnoreFilterCache reloads when ignore file is deleted`` () =
    withTempDir "cache-reload-deleted" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "vendor/\n")

        let cache = IgnoreFilterCache()
        let filter1 = cache.Get(tmpDir)
        test <@ filter1 (Path.Combine(tmpDir, "vendor", "lib.fs")) @>

        File.Delete(Path.Combine(tmpDir, ".gitignore"))

        let filter2 = cache.Get(tmpDir)
        test <@ not (filter2 (Path.Combine(tmpDir, "vendor", "lib.fs"))) @>)

[<Fact(Timeout = 15000)>]
let ``IgnoreFilterCache returns cached result when files unchanged`` () =
    withTempDir "cache-stable" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.log\n")

        let cache = IgnoreFilterCache()
        let filter1 = cache.Get(tmpDir)
        let filter2 = cache.Get(tmpDir)
        // Same object reference — cache hit, not reloaded
        test <@ obj.ReferenceEquals(filter1, filter2) @>)

// Note: a 16-thread "is safe under concurrent Get" stress test lives in
// FsHotWatch.IntegrationTests (excluded from coverage). It validates the
// double-checked-lock contention path under high contention. Keeping it here
// caused PathFilter.fs branch coverage to flicker run-to-run because the
// inner-lock-fresh branch (line 90) was recorded inconsistently under
// coverlet instrumentation when threads happened to serialize.
//
// The deterministic cousin below (`IgnoreFilterCache double-checked lock
// returns cached entry on contended populate`) drives the same inner branch
// from a barrier-coordinated 2-thread setup using the internal test hook,
// so coverage stays stable.
[<Fact(Timeout = 15000)>]
let ``IgnoreFilterCache double-checked lock returns cached entry on contended populate`` () =
    withTempDir "cache-double-check" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.log\n")
        let cache = IgnoreFilterCache()

        // Both worker threads should miss the outer Volatile.Read, then block at
        // this barrier so the lock-acquire ordering is forced: one thread enters
        // the lock and populates while the other waits, exercising the
        // "Some entry when isFresh entry" branch on the second thread.
        use barrier = new Barrier(2)
        cache.SetTestHookAfterOuterMiss(fun () -> barrier.SignalAndWait())

        let logPath = Path.Combine(tmpDir, "a.log")
        let fsPath = Path.Combine(tmpDir, "b.fs")

        let errors = ResizeArray<exn>()
        let errLock = obj ()
        let results = ResizeArray<string -> bool>()
        let resLock = obj ()

        let work () =
            try
                let f = cache.Get(tmpDir)
                lock resLock (fun () -> results.Add(f))
            with ex ->
                lock errLock (fun () -> errors.Add(ex))

        let t1 = Thread(ThreadStart(work))
        let t2 = Thread(ThreadStart(work))
        t1.Start()
        t2.Start()
        t1.Join()
        t2.Join()

        test <@ errors.Count = 0 @>
        test <@ results.Count = 2 @>
        // Both threads should observe the same correct filter behaviour.
        for f in results do
            test <@ f logPath @>
            test <@ not (f fsPath) @>
        // Both threads must have received the same Filter instance from the
        // cache — i.e. the second thread's inner-lock check returned the
        // entry the first thread populated, rather than rebuilding.
        test <@ obj.ReferenceEquals(results.[0], results.[1]) @>)
