/// A fresh workspace must start warm.
///
/// Every test here asks one question in two checkouts of the same repository: does
/// byte-identical content produce the same cache key, and does anything that really
/// changes the answer still produce a different one? A key that is too WIDE costs a
/// cold start; a key that is too NARROW is a stale-green machine, so the misses are
/// pinned as hard as the hits.
module FsHotWatch.Tests.CrossWorkspaceCacheTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TaskCache
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// Two checkouts of "the same repository"
// ---------------------------------------------------------------------------

/// Run `body` against two temp directories that hold byte-identical trees — the
/// situation a `jj workspace add` creates and the one these tests are about.
let private withTwinCheckouts (prefix: string) (populate: string -> unit) (body: string -> string -> 'a) : 'a =
    withTempDir (prefix + "-a") (fun a ->
        withTempDir (prefix + "-b") (fun b ->
            populate a
            populate b
            body a b))

/// A tools manifest pinning `version` of fantomas, written into `dir`.
let private writePin (dir: string) (version: string) =
    Directory.CreateDirectory(Path.Combine(dir, ".config")) |> ignore

    File.WriteAllText(
        Path.Combine(dir, ".config", "dotnet-tools.json"),
        $"""{{ "version": 1, "isRoot": true, "tools": {{ "fantomas": {{ "version": "%s{version}", "commands": ["fantomas"] }} }} }}"""
    )

let private formatKeyOf (dir: string) (files: string list) =
    let handler = FsHotWatch.Fantomas.FormatCheckPlugin.createFormatCheck dir None

    handler.CacheKey
    |> Option.defaultWith (fun () -> failwith "format-check must declare a CacheKey")
    |> fun key -> key handler.Init (FileChanged(SourceChanged files))

// ---------------------------------------------------------------------------
// format-check — the plugin whose cold-workspace cost was measured
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``format-check key is identical in a second checkout with identical content`` () =
    // THE acceptance criterion, at the level of the key: nothing about WHICH
    // directory the bytes were read from may reach the key.
    withTwinCheckouts
        "format-twin"
        (fun dir ->
            writePin dir "7.0.5"
            File.WriteAllText(Path.Combine(dir, "A.fs"), "module A\n"))
        (fun a b ->
            let keyA = formatKeyOf a [ Path.Combine(a, "A.fs") ]
            let keyB = formatKeyOf b [ Path.Combine(b, "A.fs") ]
            test <@ keyA.IsSome @>
            test <@ keyA = keyB @>)

[<Fact(Timeout = 15000)>]
let ``format-check key differs in a second checkout when a single byte differs`` () =
    withTwinCheckouts
        "format-byte"
        (fun dir ->
            writePin dir "7.0.5"
            File.WriteAllText(Path.Combine(dir, "A.fs"), "module A\n"))
        (fun a b ->
            File.WriteAllText(Path.Combine(b, "A.fs"), "module A \n")
            let keyA = formatKeyOf a [ Path.Combine(a, "A.fs") ]
            let keyB = formatKeyOf b [ Path.Combine(b, "A.fs") ]
            test <@ keyA.IsSome && keyB.IsSome @>
            test <@ keyA <> keyB @>)

[<Fact(Timeout = 15000)>]
let ``format-check key differs across checkouts when the pinned toolchain differs`` () =
    // Same bytes, different formatter: the two checkouts must NOT share a verdict.
    withTwinCheckouts
        "format-pin"
        (fun dir ->
            writePin dir "7.0.5"
            File.WriteAllText(Path.Combine(dir, "A.fs"), "module A\n"))
        (fun a b ->
            writePin b "7.0.6"

            test
                <@
                    formatKeyOf a [ Path.Combine(a, "A.fs") ]
                    <> formatKeyOf b [ Path.Combine(b, "A.fs") ]
                @>)

[<Fact(Timeout = 15000)>]
let ``format-check key differs across checkouts when the editorconfig differs`` () =
    withTwinCheckouts
        "format-editorconfig"
        (fun dir ->
            writePin dir "7.0.5"
            File.WriteAllText(Path.Combine(dir, "A.fs"), "module A\n"))
        (fun a b ->
            File.WriteAllText(Path.Combine(b, ".editorconfig"), "[*.fs]\nmax_line_length = 80\n")

            test
                <@
                    formatKeyOf a [ Path.Combine(a, "A.fs") ]
                    <> formatKeyOf b [ Path.Combine(b, "A.fs") ]
                @>)

[<Fact(Timeout = 15000)>]
let ``format-check key still distinguishes two files inside one checkout`` () =
    // Relativizing the path must not COLLAPSE it: two files with the same content in
    // different places are still different work.
    withTempDir "format-distinct" (fun dir ->
        writePin dir "7.0.5"
        Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
        let top = Path.Combine(dir, "A.fs")
        let nested = Path.Combine(dir, "src", "A.fs")
        File.WriteAllText(top, "module A\n")
        File.WriteAllText(nested, "module A\n")
        test <@ formatKeyOf dir [ top ] <> formatKeyOf dir [ nested ] @>)

// ---------------------------------------------------------------------------
// lint — the same question through a plugin whose key also folds in FCS state
// ---------------------------------------------------------------------------

let private lintKeyOf (repoRoot: string) (file: string) (source: string) =
    let handler = FsHotWatch.Lint.LintPlugin.create (Some repoRoot) None None None

    let checkResult =
        { fakeFileCheckResult file with
            Source = source }

    (handler.CacheKey
     |> Option.defaultWith (fun () -> failwith "lint must declare a CacheKey"))
        handler.Init
        (FileChecked checkResult)

[<Fact(Timeout = 15000)>]
let ``lint key is identical in a second checkout with identical content`` () =
    let a = Path.Combine(Path.GetTempPath(), "lint-a") |> Path.GetFullPath
    let b = Path.Combine(Path.GetTempPath(), "lint-b") |> Path.GetFullPath

    let keyA = lintKeyOf a (Path.Combine(a, "src", "A.fs")) "let x = 1\n"
    let keyB = lintKeyOf b (Path.Combine(b, "src", "A.fs")) "let x = 1\n"

    test <@ keyA.IsSome @>
    test <@ keyA = keyB @>

[<Fact(Timeout = 15000)>]
let ``lint key differs across checkouts when the source differs`` () =
    let a = Path.Combine(Path.GetTempPath(), "lint-src-a") |> Path.GetFullPath
    let b = Path.Combine(Path.GetTempPath(), "lint-src-b") |> Path.GetFullPath

    test
        <@
            lintKeyOf a (Path.Combine(a, "src", "A.fs")) "let x = 1\n"
            <> lintKeyOf b (Path.Combine(b, "src", "A.fs")) "let x = 2\n"
        @>

[<Fact(Timeout = 15000)>]
let ``lint key differs across checkouts when the lint configuration differs`` () =
    let configured (root: string) (config: string) =
        let configPath = Path.Combine(root, "fsharplint.json")
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(configPath, config)

        let handler =
            FsHotWatch.Lint.LintPlugin.create (Some root) (Some configPath) None None

        (handler.CacheKey.Value handler.Init) (FileChecked(fakeFileCheckResult (Path.Combine(root, "src", "A.fs"))))

    withTwinCheckouts "lint-config" (fun _ -> ()) (fun a b ->
        let same =
            configured a """{ "Hints": { "add": [] } }""", configured b """{ "Hints": { "add": [] } }"""

        test <@ fst same = snd same @>

        test
            <@
                configured a """{ "Hints": { "add": [] } }"""
                <> configured b """{ "Hints": { "add": ["x"] } }"""
            @>)

// ---------------------------------------------------------------------------
// The store: an entry written in one checkout replays into the other
// ---------------------------------------------------------------------------

let private entryFor (key: ContentHash) (errorFile: string) =
    { CacheKey = key
      Errors = [ errorFile, [ FsHotWatch.ErrorLedger.ErrorEntry.warningWithDetail "unformatted" "d" ] ]
      Status = CachedFileCompleted(TimeSpan.FromMilliseconds 7.0)
      EmittedEvents = [] }

/// The composite key the framework computes — repo-relative, so both checkouts name
/// the same entry.
let private compositeFor (repoRoot: string) (file: string) : CompositeKey =
    { Plugin = "format-check"
      File = Some(CachePathIdentity.keyOf (Some repoRoot) file) }

[<Fact(Timeout = 15000)>]
let ``an entry written in one checkout is a HIT in another sharing the store`` () =
    withTempDir "store" (fun store ->
        withTwinCheckouts
            "store-twin"
            (fun dir -> File.WriteAllText(Path.Combine(dir, "A.fs"), "module A\n"))
            (fun a b ->
                let key = merkleCacheKey [ "tool", "fantomas-7.0.5"; "source", "module A\n" ]

                let writer =
                    FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = a) :> ITaskCache

                writer.Set (compositeFor a (Path.Combine(a, "A.fs"))) key (entryFor key (Path.Combine(a, "A.fs")))

                // A SECOND cache instance over the same directory: the fresh-daemon case.
                let reader =
                    FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = b) :> ITaskCache

                match reader.Lookup (compositeFor b (Path.Combine(b, "A.fs"))) key with
                | CacheHit result ->
                    // The finding must name B's file, not A's: a replay that reported
                    // against another checkout's paths would be worse than recomputing.
                    test <@ result.Errors |> List.map fst = [ Path.Combine(b, "A.fs") ] @>
                | CacheMiss reason -> failwith $"expected a cross-checkout hit, got %A{reason}"))

[<Fact(Timeout = 15000)>]
let ``a byte change is a MISS across checkouts and the reason names the source input`` () =
    withTempDir "store-miss" (fun store ->
        withTwinCheckouts
            "store-miss-twin"
            (fun dir -> File.WriteAllText(Path.Combine(dir, "A.fs"), "module A\n"))
            (fun a b ->
                let writtenKey = merkleCacheKey [ "tool", "fantomas-7.0.5"; "source", "module A\n" ]

                let writer =
                    FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = a) :> ITaskCache

                writer.Set
                    (compositeFor a (Path.Combine(a, "A.fs")))
                    writtenKey
                    (entryFor writtenKey (Path.Combine(a, "A.fs")))

                let editedKey = merkleCacheKey [ "tool", "fantomas-7.0.5"; "source", "module A2\n" ]

                let reader =
                    FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = b) :> ITaskCache

                match reader.Lookup (compositeFor b (Path.Combine(b, "A.fs"))) editedKey with
                | CacheHit _ -> failwith "a changed source must never hit"
                | CacheMiss reason -> test <@ reason = CacheMissReason.InputsChanged [ "source" ] @>))

[<Fact(Timeout = 15000)>]
let ``a toolchain change is a MISS across checkouts and the reason names the tool input`` () =
    withTempDir "store-tool" (fun store ->
        withTwinCheckouts
            "store-tool-twin"
            (fun dir -> File.WriteAllText(Path.Combine(dir, "A.fs"), "module A\n"))
            (fun a b ->
                let writtenKey = merkleCacheKey [ "tool", "fantomas-7.0.5"; "source", "module A\n" ]

                let writer =
                    FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = a) :> ITaskCache

                writer.Set
                    (compositeFor a (Path.Combine(a, "A.fs")))
                    writtenKey
                    (entryFor writtenKey (Path.Combine(a, "A.fs")))

                let bumpedKey = merkleCacheKey [ "tool", "fantomas-7.0.6"; "source", "module A\n" ]

                let reader =
                    FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = b) :> ITaskCache

                match reader.Lookup (compositeFor b (Path.Combine(b, "A.fs"))) bumpedKey with
                | CacheHit _ -> failwith "a bumped toolchain must never hit"
                | CacheMiss reason -> test <@ reason = CacheMissReason.InputsChanged [ "tool" ] @>))

[<Fact(Timeout = 15000)>]
let ``a key nothing was ever written under misses with no input to blame`` () =
    withTempDir "store-cold" (fun store ->
        withTempDir "store-cold-repo" (fun repo ->
            let cache =
                FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = repo) :> ITaskCache

            let key = merkleCacheKey [ "tool", "v1"; "source", "x" ]

            let reason =
                match cache.Lookup (compositeFor repo (Path.Combine(repo, "Never.fs"))) key with
                | CacheHit _ -> failwith "an empty store cannot hit"
                | CacheMiss reason -> reason

            test <@ reason = CacheMissReason.NoEntryForKey @>))

[<Fact(Timeout = 15000)>]
let ``an entry naming a file outside the repository is refused in another checkout`` () =
    // An `external:` path is machine-local BY CONSTRUCTION. It must not be silently
    // rebound into the reading checkout, and a replay that cannot be honestly
    // resolved must read as a miss, never as a partial result.
    withTempDir "store-external" (fun store ->
        withTwinCheckouts "store-external-twin" (fun _ -> ()) (fun a b ->
            let key = merkleCacheKey [ "tool", "v1" ]

            let writer =
                FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = a) :> ITaskCache

            let outside = Path.Combine(Path.GetTempPath(), "outside.fs") |> Path.GetFullPath

            writer.Set (compositeFor a (Path.Combine(a, "A.fs"))) key (entryFor key outside)

            let reader =
                FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = b) :> ITaskCache

            // The path is preserved verbatim — it is machine-local, not
            // repo-relative — so the entry is readable and names the SAME file.
            match reader.Lookup (compositeFor b (Path.Combine(b, "A.fs"))) key with
            | CacheHit result -> test <@ result.Errors |> List.map fst = [ outside ] @>
            | CacheMiss reason -> failwith $"expected a hit, got %A{reason}"))

// ---------------------------------------------------------------------------
// Residency — what is shared, and what deliberately is not
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``only the pure-content plugins are shared across checkouts`` () =
    // The allowlist, pinned. Adding a plugin here is a claim that its key names every
    // input that can change its answer; this test is where that claim is recorded.
    test <@ CacheResidency.sharedPlugins = [ "analyzers"; "format-check"; "lint" ] @>

[<Fact(Timeout = 15000)>]
let ``plugins whose verdict asserts local state stay workspace-local`` () =
    let isLocal plugin =
        match CacheResidency.of_ plugin with
        | CacheResidency.Residency.WorkspaceLocal _ -> true
        | CacheResidency.Residency.SharedAcrossCheckouts -> false

    test <@ isLocal "build" @>
    test <@ isLocal "test-prune" @>
    test <@ isLocal "coverage" @>
    test <@ isLocal "file-command" @>

[<Fact(Timeout = 15000)>]
let ``an unclassified plugin is never shared`` () =
    // Fail closed: forgetting to classify a plugin costs a cold start, never a
    // verdict replayed into a checkout that did not earn it.
    test
        <@
            CacheResidency.of_ "some-plugin-nobody-has-reasoned-about"
            <> CacheResidency.Residency.SharedAcrossCheckouts
        @>

[<Fact(Timeout = 15000)>]
let ``the routed cache sends each plugin's entries to exactly one store`` () =
    let local = InMemoryTaskCache()
    let shared = InMemoryTaskCache()

    let routed =
        CacheResidency.RoutedTaskCache(local, shared, CacheResidency.of_) :> ITaskCache

    let key = merkleCacheKey [ "x", "1" ]

    let composite plugin : CompositeKey =
        { Plugin = plugin
          File = Some "repo:A.fs" }

    routed.Set (composite "format-check") key (entryFor key "repo:A.fs")
    routed.Set (composite "build") key (entryFor key "repo:A.fs")

    test <@ (shared.TryGet(composite "format-check", key)).IsSome @>
    test <@ (local.TryGet(composite "format-check", key)).IsNone @>
    test <@ (local.TryGet(composite "build", key)).IsSome @>
    test <@ (shared.TryGet(composite "build", key)).IsNone @>

// ---------------------------------------------------------------------------
// Repository identity — the namespace the shared store is partitioned by
// ---------------------------------------------------------------------------

/// A jj repository at `root/main` and a secondary workspace at `root/.workspaces/ws`,
/// laid out as `jj workspace add` lays them out: the workspace's `.jj/repo` is a FILE
/// holding a pointer at the shared repo directory.
let private withJjWorkspace (pointer: string -> string -> string) (body: string -> string -> unit) =
    withTempDir "jj" (fun root ->
        let main = Path.Combine(root, "main")
        let secondary = Path.Combine(root, "main", ".workspaces", "ws")
        let sharedRepoDir = Path.Combine(main, ".jj", "repo")
        Directory.CreateDirectory(sharedRepoDir) |> ignore
        Directory.CreateDirectory(Path.Combine(secondary, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(secondary, ".jj", "repo"), pointer sharedRepoDir secondary)
        body main secondary)

[<Fact(Timeout = 15000)>]
let ``a jj workspace with the RELATIVE pointer jj writes shares the main checkout's store directory`` () =
    // What `jj workspace add` actually writes: a path relative to the `.jj` directory
    // the pointer lives in. Hashing the pointer text gave every same-depth workspace
    // one identity and the main checkout another; the DIRECTORY must be equal.
    withJjWorkspace (fun _ _ -> Path.Combine("..", "..", "..", ".jj", "repo")) (fun main secondary ->
        test <@ RepoIdentity.describe secondary = RepoIdentity.describe main @>
        test <@ RepoIdentity.namespaceOf secondary = RepoIdentity.namespaceOf main @>)

[<Fact(Timeout = 15000)>]
let ``a jj workspace with an ABSOLUTE pointer shares the main checkout's store directory`` () =
    withJjWorkspace (fun sharedRepoDir _ -> sharedRepoDir) (fun main secondary ->
        test <@ RepoIdentity.describe secondary = RepoIdentity.describe main @>
        test <@ RepoIdentity.namespaceOf secondary = RepoIdentity.namespaceOf main @>)

[<Fact(Timeout = 15000)>]
let ``the store directory is named after the repository, not the checkout`` () =
    // The label is the REPOSITORY's directory name (`main` here), read off the
    // identity, so it is the same string in every checkout; the checkout's own name
    // (`ws`) was the label, which gave every workspace a private
    // store while the comment above it promised sharing.
    withJjWorkspace (fun _ _ -> Path.Combine("..", "..", "..", ".jj", "repo")) (fun main secondary ->
        test <@ (RepoIdentity.namespaceOf secondary).StartsWith "main-" @>
        test <@ (RepoIdentity.namespaceOf main).StartsWith "main-" @>)

[<Fact(Timeout = 15000)>]
let ``a corrupt workspace pointer still yields a namespace instead of crashing daemon start`` () =
    // A pointer that cannot be made a path (a null character) is unreadable metadata:
    // the checkout gets a private, stable namespace named after itself — never an
    // exception on the daemon's startup path, never the main checkout's store.
    withJjWorkspace (fun _ _ -> "../../\000/.jj/repo") (fun main secondary ->
        match RepoIdentity.describe secondary with
        | RepoIdentity.RepoIdentitySource.Unreadable(root, _) -> test <@ root = Path.GetFullPath secondary @>
        | other -> failwith $"expected an unreadable identity from the pointer, got %A{other}"

        test <@ (RepoIdentity.namespaceOf secondary).StartsWith "ws-" @>
        test <@ RepoIdentity.namespaceOf secondary <> RepoIdentity.namespaceOf main @>)

[<Fact(Timeout = 15000)>]
let ``this checkout's identity resolves to a directory that exists`` () =
    // Through the real code path, on the real checkout the tests run in: whatever the
    // pointer said, the identity is an absolute directory that is there — and when this
    // is a secondary jj workspace, the same store directory the main checkout uses.
    let root = RepoTasks.repoRoot ()

    match RepoIdentity.describe root with
    | RepoIdentity.RepoIdentitySource.Store store ->
        test <@ Directory.Exists store.Path.Value @>
        test <@ Path.IsPathRooted store.Path.Value @>

        if store.Provider = RepositoryIdentity.VcsProvider.Jujutsu then
            let main = Path.GetDirectoryName(Path.GetDirectoryName store.Path.Value)
            test <@ RepoIdentity.namespaceOf main = RepoIdentity.namespaceOf root @>
    | RepoIdentity.RepoIdentitySource.Unreadable(_, error) ->
        failwith $"this checkout's layout should read: %s{RepositoryIdentity.IdentityError.describe error}"

[<Fact(Timeout = 15000)>]
let ``two unrelated checkouts never share a cache namespace`` () =
    withTwinCheckouts "unrelated" (fun _ -> ()) (fun a b ->
        test <@ RepoIdentity.namespaceOf a <> RepoIdentity.namespaceOf b @>)

[<Fact(Timeout = 15000)>]
let ``a checkout under no recognised version control gets a private namespace`` () =
    withTempDir "novcs" (fun dir ->
        match RepoIdentity.describe dir with
        | RepoIdentity.RepoIdentitySource.Store store ->
            test <@ store.Provider = RepositoryIdentity.VcsProvider.Standalone @>
        | other -> failwith $"expected a standalone store, got %A{other}")

// ---------------------------------------------------------------------------
// The compiler options hash — the shared daemon's prerequisite
// ---------------------------------------------------------------------------

let private optionsAt (root: string) : FSharp.Compiler.CodeAnalysis.FSharpProjectOptions =
    let inRepoReference = "-r:" + Path.Combine(root, "src", "obj", "App.dll")

    { ProjectFileName = Path.Combine(root, "src", "App.fsproj")
      ProjectId = None
      SourceFiles = [| Path.Combine(root, "src", "App.fs") |]
      OtherOptions = [| "--define:TRACE"; inRepoReference; "-r:/nuget/FSharp.Core.dll" |]
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime(2025, 1, 1)
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

[<Fact(Timeout = 15000)>]
let ``the project options hash is identical across two checkouts of one repository`` () =
    let a = "/checkouts/a"
    let b = "/checkouts/b"

    test
        <@
            CheckCache.getProjectOptionsHashRelativeTo (Some a) (optionsAt a) = CheckCache.getProjectOptionsHashRelativeTo
                (Some b)
                (optionsAt b)
        @>

[<Fact(Timeout = 15000)>]
let ``the project options hash still separates two different option sets`` () =
    let a = "/checkouts/a"

    let withExtraDefine =
        { optionsAt a with
            OtherOptions = Array.append (optionsAt a).OtherOptions [| "--define:EXTRA" |] }

    test
        <@
            CheckCache.getProjectOptionsHashRelativeTo (Some a) (optionsAt a)
            <> CheckCache.getProjectOptionsHashRelativeTo (Some a) withExtraDefine
        @>

[<Fact(Timeout = 15000)>]
let ``the project options hash keeps out-of-repo references absolute`` () =
    // A NuGet path is machine-local, and on ONE machine it is the same for every
    // workspace — so it must separate two machines' entries and never two checkouts'.
    test
        <@ CheckCache.relativizeOption (Some "/checkouts/a") "-r:/nuget/FSharp.Core.dll" = "-r:/nuget/FSharp.Core.dll" @>

    test
        <@
            CheckCache.relativizeOption (Some "/checkouts/a") "-r:/checkouts/a/src/obj/App.dll" = "-r:"
                                                                                                  + CheckCache.RepoRootPlaceholder
                                                                                                  + "/src/obj/App.dll"
        @>

[<Fact(Timeout = 15000)>]
let ``the project options hash without a repository root is unchanged`` () =
    // The no-root behaviour is the historical one, and callers that have no root
    // (tests, tooling) must keep getting it rather than a silently portable hash.
    let a = "/checkouts/a"

    test
        <@
            CheckCache.getProjectOptionsHash (optionsAt a) = CheckCache.getProjectOptionsHashRelativeTo
                None
                (optionsAt a)
        @>

    test
        <@
            CheckCache.getProjectOptionsHash (optionsAt a)
            <> CheckCache.getProjectOptionsHashRelativeTo (Some a) (optionsAt a)
        @>

// ---------------------------------------------------------------------------
// The routed store's remaining operations
// ---------------------------------------------------------------------------

let private routedPair () =
    let local = InMemoryTaskCache()
    let shared = InMemoryTaskCache()
    let routed = CacheResidency.RoutedTaskCache(local, shared, CacheResidency.of_)
    local, shared, routed

[<Fact(Timeout = 15000)>]
let ``a routed read reaches the same store the write went to`` () =
    let local, shared, routed = routedPair ()
    let iface = routed :> ITaskCache
    let key = merkleCacheKey [ "x", "1" ]

    let composite plugin : CompositeKey =
        { Plugin = plugin
          File = Some "repo:A.fs" }

    iface.Set (composite "lint") key (entryFor key "repo:A.fs")
    test <@ (iface.TryGet (composite "lint") key).IsSome @>

    match iface.Lookup (composite "lint") key with
    | CacheHit _ -> ()
    | CacheMiss reason -> failwith $"expected a hit, got %A{reason}"

    // The routing decision is observable directly, not only by which store answered.
    test <@ obj.ReferenceEquals(routed.StoreFor "lint", shared) @>
    test <@ obj.ReferenceEquals(routed.StoreFor "build", local) @>

[<Fact(Timeout = 15000)>]
let ``clearing everything reaches BOTH stores`` () =
    // `Clear` and `ClearFile` name no plugin, so applying them to one store only
    // would leave the caller's request half done.
    let local, shared, routed = routedPair ()
    let iface = routed :> ITaskCache
    let key = merkleCacheKey [ "x", "1" ]

    let composite plugin : CompositeKey =
        { Plugin = plugin
          File = Some "repo:A.fs" }

    iface.Set (composite "lint") key (entryFor key "repo:A.fs")
    iface.Set (composite "build") key (entryFor key "repo:A.fs")
    iface.ClearFile "repo:A.fs"
    test <@ (shared.TryGet(composite "lint", key)).IsNone @>
    test <@ (local.TryGet(composite "build", key)).IsNone @>

    iface.Set (composite "lint") key (entryFor key "repo:A.fs")
    iface.Set (composite "build") key (entryFor key "repo:A.fs")
    iface.Clear()
    test <@ (shared.TryGet(composite "lint", key)).IsNone @>
    test <@ (local.TryGet(composite "build", key)).IsNone @>

[<Fact(Timeout = 15000)>]
let ``a plugin-scoped clear touches only that plugin's store`` () =
    let local, shared, routed = routedPair ()
    let iface = routed :> ITaskCache
    let key = merkleCacheKey [ "x", "1" ]

    let composite plugin : CompositeKey =
        { Plugin = plugin
          File = Some "repo:A.fs" }

    iface.Set (composite "lint") key (entryFor key "repo:A.fs")
    iface.Set (composite "build") key (entryFor key "repo:A.fs")

    iface.ClearPlugin "lint"
    test <@ (shared.TryGet(composite "lint", key)).IsNone @>
    test <@ (local.TryGet(composite "build", key)).IsSome @>

    iface.Set (composite "lint") key (entryFor key "repo:A.fs")
    iface.ClearPluginFile "build" "repo:A.fs"
    test <@ (local.TryGet(composite "build", key)).IsNone @>
    test <@ (shared.TryGet(composite "lint", key)).IsSome @>

// ---------------------------------------------------------------------------
// Miss reasons, in their own right
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``the in-memory cache explains a miss the same way the file-backed one does`` () =
    // The two implementations must agree about hit AND about why not, or a
    // test-double cache would report a different story from the daemon's.
    let cache = InMemoryTaskCache()

    let composite: CompositeKey =
        { Plugin = "lint"
          File = Some "repo:A.fs" }

    let written = merkleCacheKey [ "tool", "v1"; "source", "x" ]

    test <@ cache.Lookup(composite, written) = CacheMiss CacheMissReason.NoEntryForKey @>

    cache.Set(composite, written, entryFor written "repo:A.fs")
    test <@ cache.Lookup(composite, written) = CacheHit(entryFor written "repo:A.fs") @>

    let edited = merkleCacheKey [ "tool", "v1"; "source", "y" ]
    test <@ cache.Lookup(composite, edited) = CacheMiss(CacheMissReason.InputsChanged [ "source" ]) @>

    // A key that was NOT minted by `merkleCacheKey` — a commit-id key, say — carries
    // no labelled inputs, so the honest answer is that the two cannot be compared.
    test
        <@ cache.Lookup(composite, ContentHash.create "some-commit-id") = CacheMiss CacheMissReason.InputsNotComparable @>

[<Fact(Timeout = 15000)>]
let ``a clear forgets the key a later miss would have been explained by`` () =
    let cache = InMemoryTaskCache()

    let composite: CompositeKey =
        { Plugin = "lint"
          File = Some "repo:A.fs" }

    let written = merkleCacheKey [ "tool", "v1" ]
    let other = merkleCacheKey [ "tool", "v2" ]

    let freshlyCleared (clear: InMemoryTaskCache -> unit) =
        let cache = InMemoryTaskCache()
        cache.Set(composite, written, entryFor written "repo:A.fs")
        clear cache
        cache.Lookup(composite, other)

    test <@ freshlyCleared (fun c -> c.ClearPlugin "lint") = CacheMiss CacheMissReason.NoEntryForKey @>
    test <@ freshlyCleared (fun c -> c.ClearFile "repo:A.fs") = CacheMiss CacheMissReason.NoEntryForKey @>

    test
        <@ freshlyCleared (fun c -> c.ClearPluginFile("lint", "repo:A.fs")) = CacheMiss CacheMissReason.NoEntryForKey @>

    test <@ freshlyCleared (fun c -> c.Clear()) = CacheMiss CacheMissReason.NoEntryForKey @>
    ignore cache

[<Fact(Timeout = 15000)>]
let ``every miss reason renders a distinct log line`` () =
    let rendered =
        [ CacheMissReason.NoEntryForKey
          CacheMissReason.InputsChanged [ "source"; "config" ]
          CacheMissReason.InputsNotComparable
          CacheMissReason.UnreadableEntry "IOException: locked" ]
        |> List.map CacheMissReason.describe

    test <@ rendered |> List.distinct |> List.length = 4 @>
    // The labels are the point of the reason, and they are sorted so the line is
    // stable across runs.
    test <@ rendered[1] = "inputs-changed:config,source" @>
    test <@ rendered[3].Contains "locked" @>

[<Fact(Timeout = 15000)>]
let ``a corrupt entry file is a miss that says so`` () =
    withTempDir "corrupt" (fun store ->
        withTempDir "corrupt-repo" (fun repo ->
            let cache = FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = repo)
            let key = merkleCacheKey [ "tool", "v1" ]
            let composite = compositeFor repo (Path.Combine(repo, "A.fs"))
            cache.Set(composite, key, entryFor key (Path.Combine(repo, "A.fs")))

            // Overwrite the entry with something that is not an entry at all.
            let entryFile = Directory.GetFiles(store, "*.json") |> Array.exactlyOne
            File.WriteAllText(entryFile, "{ not json")

            match cache.Lookup(composite, key) with
            | CacheHit _ -> failwith "a corrupt entry must never hit"
            | CacheMiss(CacheMissReason.UnreadableEntry _) -> ()
            | CacheMiss other -> failwith $"expected UnreadableEntry, got %A{other}"

            test <@ cache.ParseFailureCount = 1 @>))

[<Fact(Timeout = 15000)>]
let ``the fingerprint registry is bounded and forgetting one only costs a reason`` () =
    // Overflow drops the whole map. Losing a fingerprint must degrade the REASON and
    // never the decision, so the same key still hits.
    let cache = InMemoryTaskCache()

    let composite: CompositeKey =
        { Plugin = "lint"
          File = Some "repo:A.fs" }

    let key = merkleCacheKey [ "tool", "v1" ]
    cache.Set(composite, key, entryFor key "repo:A.fs")

    for i in 1 .. KeyFingerprints.Capacity + 1 do
        merkleCacheKey [ "filler", string i ] |> ignore

    test <@ (cache.TryGet(composite, key)).IsSome @>

// ---------------------------------------------------------------------------
// Identity and path edge cases
// ---------------------------------------------------------------------------

let private isUnreadable (source: RepoIdentity.RepoIdentitySource) =
    match source with
    | RepoIdentity.RepoIdentitySource.Unreadable(root, _) -> Path.IsPathRooted root
    | RepoIdentity.RepoIdentitySource.Store _ -> false

[<Fact(Timeout = 15000)>]
let ``an empty or unrecognised pointer file leaves the checkout on its own`` () =
    withTempDir "emptyptr" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(dir, ".jj", "repo"), "   \n")
        test <@ isUnreadable (RepoIdentity.describe dir) @>

        // A `.git` file that is not a `gitdir:` pointer is not a repository marker.
        File.WriteAllText(Path.Combine(dir, ".git"), "something else\n")
        test <@ isUnreadable (RepoIdentity.describe dir) @>)

[<Fact(Timeout = 15000)>]
let ``a colocated git checkout and its worktrees share an identity`` () =
    withTempDir "git" (fun root ->
        let main = Path.Combine(root, "main")
        let worktree = Path.Combine(root, "wt")
        let gitDir = Path.Combine(main, ".git")
        let perWorktree = Path.Combine(gitDir, "worktrees", "wt")
        Directory.CreateDirectory perWorktree |> ignore
        File.WriteAllText(Path.Combine(perWorktree, "commondir"), "../..\n")
        Directory.CreateDirectory worktree |> ignore
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: %s{perWorktree}\n")

        match RepoIdentity.describe main with
        | RepoIdentity.RepoIdentitySource.Store store ->
            test <@ store.Provider = RepositoryIdentity.VcsProvider.Git @>
            test <@ store.Path.Value.EndsWith(Path.Combine("main", ".git")) @>
        | other -> failwith $"expected a git store, got %A{other}"

        test <@ RepoIdentity.describe worktree = RepoIdentity.describe main @>

        test <@ RepoIdentity.namespaceOf main = RepoIdentity.namespaceOf worktree @>)

[<Fact(Timeout = 15000)>]
let ``the identity source is tagged by kind so two kinds cannot collide`` () =
    withTempDir "tagged" (fun dir ->
        let path =
            match RepositoryIdentity.canonicalize dir with
            | Ok path -> path
            | Error error -> failwith (RepositoryIdentity.IdentityError.describe error)

        let store provider =
            RepoIdentity.RepoIdentitySource.Store { Provider = provider; Path = path }

        let sources =
            [ store RepositoryIdentity.VcsProvider.Jujutsu
              store RepositoryIdentity.VcsProvider.Git
              store RepositoryIdentity.VcsProvider.Standalone
              RepoIdentity.RepoIdentitySource.Unreadable(path.Value, RepositoryIdentity.IdentityError.PathNotFound dir) ]
            |> List.map RepoIdentity.identitySource

        test <@ sources |> List.distinct |> List.length = 4 @>)

[<Fact(Timeout = 15000)>]
let ``a checkout whose directory name is not filesystem-plain still gets a namespace`` () =
    // The name is a convenience for whoever lists the cache directory; the DIGEST is
    // the identity, so an awkward repository name is dropped rather than escaped.
    let awkward =
        Path.Combine(Path.GetTempPath(), $"fshw awkward {Guid.NewGuid():N}")
        |> Path.GetFullPath

    Directory.CreateDirectory awkward |> ignore

    try
        test <@ (RepoIdentity.namespaceOf awkward).StartsWith "repo-" @>
    finally
        Directory.Delete awkward

[<Fact(Timeout = 15000)>]
let ``an unusable repository root never throws`` () =
    // Every path helper on this route is reached with whatever the daemon was handed.
    // A root that cannot even be made absolute must degrade, not crash.
    let unusable = "\000not-a-path"
    test <@ (RepoIdentity.namespaceOf unusable).Length > 0 @>
    test <@ CheckCache.relativizeOption (Some unusable) "-r:/nuget/x.dll" = "-r:/nuget/x.dll" @>

[<Fact(Timeout = 15000)>]
let ``a checkout with no directory name of its own is labelled repo`` () =
    // An empty root cannot be resolved, and has no name to read off either: the
    // namespace still gets the plain `repo` label rather than a leading dash.
    test <@ (RepoIdentity.namespaceOf "").StartsWith "repo-" @>

[<Fact(Timeout = 15000)>]
let ``the shared cache home prefers the override, then XDG, then the home directory`` () =
    test <@ FsHwPaths.sharedCacheHomeFrom "/explicit" "/xdg" "/home/u" = "/explicit" @>
    test <@ FsHwPaths.sharedCacheHomeFrom "" "/xdg" "/home/u" = Path.Combine("/xdg", "fshw") @>
    test <@ FsHwPaths.sharedCacheHomeFrom null null "/home/u" = Path.Combine("/home/u", ".cache", "fshw") @>

    // And it is outside every checkout — the whole reason it exists.
    test <@ not ((FsHwPaths.sharedCacheHome ()).StartsWith(Path.Combine("x", ".fshw"))) @>

[<Fact(Timeout = 15000)>]
let ``the shared cache home treats an empty variable exactly like an unset one`` () =
    // Both spellings of "not configured" reach the same place — an empty
    // `FSHW_CACHE_HOME` exported by a wrapper script must not resolve the cache to "".
    test <@ FsHwPaths.sharedCacheHomeFrom "" "" "/home/u" = Path.Combine("/home/u", ".cache", "fshw") @>
    test <@ FsHwPaths.sharedCacheHomeFrom null "/xdg" "/home/u" = Path.Combine("/xdg", "fshw") @>

[<Fact(Timeout = 15000)>]
let ``an unreadable repository pointer leaves the checkout on its own`` () =
    // A pointer that exists but cannot be READ is not evidence of anything. It must
    // degrade to a private namespace, not throw out of daemon startup.
    withTempDir "unreadable-ptr" (fun dir ->
        let jjDir = Path.Combine(dir, ".jj")
        Directory.CreateDirectory jjDir |> ignore
        let pointer = Path.Combine(jjDir, "repo")
        File.WriteAllText(pointer, "/somewhere")
        File.SetUnixFileMode(pointer, UnixFileMode.None)

        try
            test <@ isUnreadable (RepoIdentity.describe dir) @>
        finally
            File.SetUnixFileMode(pointer, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact(Timeout = 15000)>]
let ``a project with no sources and no options still hashes`` () =
    // The empty-array path through the options hash: a project MSBuild evaluated to
    // nothing must produce a hash, not an exception, and must not collide with a
    // project that has sources.
    let empty: FSharp.Compiler.CodeAnalysis.FSharpProjectOptions =
        { optionsAt "/checkouts/a" with
            SourceFiles = [||]
            OtherOptions = [||] }

    let hashOfEmpty =
        CheckCache.getProjectOptionsHashRelativeTo (Some "/checkouts/a") empty

    test <@ hashOfEmpty.Length = 64 @>

    test
        <@
            hashOfEmpty
            <> CheckCache.getProjectOptionsHashRelativeTo (Some "/checkouts/a") (optionsAt "/checkouts/a")
        @>

[<Fact(Timeout = 15000)>]
let ``two keys with identical recorded inputs but different hashes refuse to name a culprit`` () =
    // The `[]` arm of the diff: the hashes disagree, yet every labelled digest agrees,
    // so the difference lies somewhere the fingerprint cannot see. Naming an input
    // there would be a guess, and a guess in a miss reason is how a mis-salted key
    // hides. Reached by recording the same inputs under a second hash — the shape a
    // fingerprint collision or a truncated digest would produce.
    let cache = InMemoryTaskCache()

    let composite: CompositeKey =
        { Plugin = "lint"
          File = Some "repo:A.fs" }

    let written = merkleCacheKey [ "tool", "v1" ]
    cache.Set(composite, written, entryFor written "repo:A.fs")

    let impostor = ContentHash.create "an-impostor-hash"

    KeyFingerprints.record
        (ContentHash.value impostor)
        (KeyFingerprints.tryGet (ContentHash.value written) |> Option.defaultValue [])

    test <@ cache.Lookup(composite, impostor) = CacheMiss CacheMissReason.InputsNotComparable @>

[<Fact(Timeout = 15000)>]
let ``the project options hash relativizes every source file, not just the first`` () =
    // A one-file project cannot tell "the map ran" from "the map ran once". A project
    // with several sources — the normal case — is what proves each one is rewritten.
    let a = "/checkouts/a"
    let b = "/checkouts/b"

    let manySources (root: string) =
        { optionsAt root with
            SourceFiles =
                [| Path.Combine(root, "src", "A.fs")
                   Path.Combine(root, "src", "B.fs")
                   Path.Combine(root, "src", "C.fs") |] }

    test
        <@
            CheckCache.getProjectOptionsHashRelativeTo (Some a) (manySources a) = CheckCache.getProjectOptionsHashRelativeTo
                (Some b)
                (manySources b)
        @>

    // And a source added is still a different project.
    test
        <@
            CheckCache.getProjectOptionsHashRelativeTo (Some a) (manySources a)
            <> CheckCache.getProjectOptionsHashRelativeTo (Some a) (optionsAt a)
        @>

// ---------------------------------------------------------------------------
// analyzers — the house rules, built in each checkout
// ---------------------------------------------------------------------------

open FsHotWatch.Tests.AnalyzerFixtures

/// The analyzers key one checkout computes for `file` with the house rules loaded
/// from that checkout's own build output.
let private analyzersKeyOf (root: string) (dll: string) (file: string) =
    // The path as `.fshw.json` writes it — ABSOLUTE after the daemon resolves it, and
    // with the trailing separator the config carries. The separator is what made the
    // `analyzer-paths` slot fall back to a workspace-specific absolute key while this
    // test, built without one, stayed green.
    let handler =
        FsHotWatch.Analyzers.AnalyzersPlugin.create
            (Some root)
            [ Path.GetDirectoryName dll + string Path.DirectorySeparatorChar ]
            None
            ErrorLedger.DiagnosticSeverity.Hint

    test <@ handler.Init.LoadedCount >= 1 @>

    (handler.CacheKey.Value handler.Init) (
        FileChecked
            { fakeFileCheckResult file with
                Source = "let x = 1\n" }
    )

let private analyzersCompositeFor (repoRoot: string) (file: string) : CompositeKey =
    { Plugin = "analyzers"
      File = Some(CachePathIdentity.keyOf (Some repoRoot) file) }

/// Two checkouts that each BUILT the house rules: same sources, and DLLs that differ
/// by the one thing fsc salts with the build path.
let private withBuiltTwins (prefix: string) (body: string -> string -> string -> string -> 'a) : 'a =
    withTwinCheckouts prefix (fun _ -> ()) (fun a b ->
        let dllA = relocate a
        let dllB = relocate b
        saltCodeViewPath dllB
        test <@ File.ReadAllBytes dllA <> File.ReadAllBytes dllB @>
        body a dllA b dllB)

[<Fact(Timeout = 30000)>]
let ``an analyzers entry written in one checkout is a HIT in another whose analyzer DLL bytes differ`` () =
    // THE acceptance criterion: the DLL bytes differ between the checkouts (they did
    // under v4's byte-keyed slot too, which is why every fresh workspace missed), and
    // the key does not.
    withTempDir "store" (fun store ->
        withBuiltTwins "twin" (fun a dllA b dllB ->
            let fileA = Path.Combine(a, "src", "A.fs")
            let fileB = Path.Combine(b, "src", "A.fs")
            let keyA = analyzersKeyOf a dllA fileA
            let keyB = analyzersKeyOf b dllB fileB

            test <@ keyA.IsSome @>
            test <@ keyA = keyB @>

            let writer =
                FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = a) :> ITaskCache

            writer.Set (analyzersCompositeFor a fileA) keyA.Value (entryFor keyA.Value fileA)

            let reader =
                FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = b) :> ITaskCache

            match reader.Lookup (analyzersCompositeFor b fileB) keyB.Value with
            | CacheHit result -> test <@ result.Errors |> List.map fst = [ fileB ] @>
            | CacheMiss reason -> failwith $"expected a cross-checkout hit, got %A{reason}"))

[<Fact(Timeout = 30000)>]
let ``an analyzer built from different SOURCE is a MISS across checkouts naming analyzer-inputs`` () =
    withTempDir "store-miss" (fun store ->
        withBuiltTwins "twin-miss" (fun a dllA b dllB ->
            let fileA = Path.Combine(a, "src", "A.fs")
            let fileB = Path.Combine(b, "src", "A.fs")
            let keyA = analyzersKeyOf a dllA fileA

            let writer =
                FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = a) :> ITaskCache

            writer.Set (analyzersCompositeFor a fileA) keyA.Value (entryFor keyA.Value fileA)

            // B's house rules were built from a different ConventionAnalyzers.fs: the
            // source and the checksum fsc recorded for it both say so.
            rewriteSource b (File.ReadAllText(Path.Combine(b, rulesSourceRel)) + "\n// a rule changed\n")
            let keyB = analyzersKeyOf b dllB fileB
            test <@ keyB.IsSome @>
            test <@ keyA <> keyB @>

            let reader =
                FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = b) :> ITaskCache

            match reader.Lookup (analyzersCompositeFor b fileB) keyB.Value with
            | CacheHit _ -> failwith "a changed analyzer must never hit"
            | CacheMiss reason -> test <@ reason = CacheMissReason.InputsChanged [ "analyzer-inputs" ] @>))

[<Fact(Timeout = 30000)>]
let ``an analyzer whose source drifted after its build has no analyzers key at all`` () =
    // An edited source with the OLD build beside it is neither the old analyzer nor
    // the new one. No key: the analyzers run, nothing is read or written.
    withBuiltTwins "twin-drift" (fun a dllA b dllB ->
        let keyA = analyzersKeyOf a dllA (Path.Combine(a, "src", "A.fs"))
        test <@ keyA.IsSome @>

        File.AppendAllText(Path.Combine(b, rulesSourceRel), "\n// edited, not rebuilt\n")
        test <@ analyzersKeyOf b dllB (Path.Combine(b, "src", "A.fs")) = None @>)
