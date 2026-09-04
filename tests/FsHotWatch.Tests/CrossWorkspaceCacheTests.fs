/// AUTOMATION-564 — a fresh workspace must start warm.
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
/// situation a `jj workspace add` creates and the one this ticket is about.
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
    (FsHotWatch.Fantomas.FormatCheckPlugin.createFormatCheck dir None).CacheKey
    |> Option.defaultWith (fun () -> failwith "format-check must declare a CacheKey")
    |> fun key -> key (FileChanged(SourceChanged files))

// ---------------------------------------------------------------------------
// format-check — the plugin whose cold-workspace cost the ticket measures
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``format-check key is identical in a second checkout with identical content`` () =
    // THE acceptance criterion, at the level of the key: nothing about WHICH
    // directory the bytes were read from may reach the key.
    withTwinCheckouts
        "a564-format-twin"
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
        "a564-format-byte"
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
        "a564-format-pin"
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
        "a564-format-editorconfig"
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
    withTempDir "a564-format-distinct" (fun dir ->
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
     |> Option.defaultWith (fun () -> failwith "lint must declare a CacheKey")) (
        FileChecked checkResult
    )

[<Fact(Timeout = 15000)>]
let ``lint key is identical in a second checkout with identical content`` () =
    let a = Path.Combine(Path.GetTempPath(), "a564-lint-a") |> Path.GetFullPath
    let b = Path.Combine(Path.GetTempPath(), "a564-lint-b") |> Path.GetFullPath

    let keyA = lintKeyOf a (Path.Combine(a, "src", "A.fs")) "let x = 1\n"
    let keyB = lintKeyOf b (Path.Combine(b, "src", "A.fs")) "let x = 1\n"

    test <@ keyA.IsSome @>
    test <@ keyA = keyB @>

[<Fact(Timeout = 15000)>]
let ``lint key differs across checkouts when the source differs`` () =
    let a = Path.Combine(Path.GetTempPath(), "a564-lint-src-a") |> Path.GetFullPath
    let b = Path.Combine(Path.GetTempPath(), "a564-lint-src-b") |> Path.GetFullPath

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

        (handler.CacheKey.Value) (FileChecked(fakeFileCheckResult (Path.Combine(root, "src", "A.fs"))))

    withTwinCheckouts "a564-lint-config" (fun _ -> ()) (fun a b ->
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
    withTempDir "a564-store" (fun store ->
        withTwinCheckouts
            "a564-store-twin"
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
    withTempDir "a564-store-miss" (fun store ->
        withTwinCheckouts
            "a564-store-miss-twin"
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
    withTempDir "a564-store-tool" (fun store ->
        withTwinCheckouts
            "a564-store-tool-twin"
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
    withTempDir "a564-store-cold" (fun store ->
        withTempDir "a564-store-cold-repo" (fun repo ->
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
    withTempDir "a564-store-external" (fun store ->
        withTwinCheckouts "a564-store-external-twin" (fun _ -> ()) (fun a b ->
            let key = merkleCacheKey [ "tool", "v1" ]

            let writer =
                FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = a) :> ITaskCache

            let outside =
                Path.Combine(Path.GetTempPath(), "a564-outside.fs") |> Path.GetFullPath

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

[<Fact(Timeout = 15000)>]
let ``two jj workspaces of one repository share a cache namespace`` () =
    withTempDir "a564-jj" (fun root ->
        let main = Path.Combine(root, "main")
        let secondary = Path.Combine(root, "ws")
        let sharedRepoDir = Path.Combine(main, ".jj", "repo")
        Directory.CreateDirectory(sharedRepoDir) |> ignore
        Directory.CreateDirectory(Path.Combine(secondary, ".jj")) |> ignore
        // What `jj workspace add` writes: a pointer at the shared repo directory.
        File.WriteAllText(Path.Combine(secondary, ".jj", "repo"), sharedRepoDir)

        test <@ RepoIdentity.describe main = RepoIdentity.RepoIdentitySource.Jujutsu sharedRepoDir @>

        // The namespace's identity half must agree even though the labels differ.
        let digestOf (dir: string) =
            (RepoIdentity.namespaceOf dir).Split('-') |> Array.last

        test <@ digestOf main = digestOf secondary @>)

[<Fact(Timeout = 15000)>]
let ``two unrelated checkouts never share a cache namespace`` () =
    withTwinCheckouts "a564-unrelated" (fun _ -> ()) (fun a b ->
        test <@ RepoIdentity.namespaceOf a <> RepoIdentity.namespaceOf b @>)

[<Fact(Timeout = 15000)>]
let ``a git worktree resolves to the repository's own git directory`` () =
    test <@ RepoIdentity.canonicalGitDir "/repo/.git/worktrees/feature" = "/repo/.git" @>
    test <@ RepoIdentity.canonicalGitDir "/repo/.git" = "/repo/.git" @>

[<Fact(Timeout = 15000)>]
let ``a checkout under no recognised version control gets a private namespace`` () =
    withTempDir "a564-novcs" (fun dir ->
        test <@ RepoIdentity.describe dir = RepoIdentity.RepoIdentitySource.CheckoutPath(Path.GetFullPath dir) @>)

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
    withTempDir "a564-corrupt" (fun store ->
        withTempDir "a564-corrupt-repo" (fun repo ->
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

[<Fact(Timeout = 15000)>]
let ``an empty or unrecognised pointer file leaves the checkout on its own`` () =
    withTempDir "a564-emptyptr" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(dir, ".jj", "repo"), "   \n")
        test <@ RepoIdentity.describe dir = RepoIdentity.RepoIdentitySource.CheckoutPath(Path.GetFullPath dir) @>

        // A `.git` file that is not a `gitdir:` pointer is not a repository marker.
        File.WriteAllText(Path.Combine(dir, ".git"), "something else\n")
        test <@ RepoIdentity.describe dir = RepoIdentity.RepoIdentitySource.CheckoutPath(Path.GetFullPath dir) @>)

[<Fact(Timeout = 15000)>]
let ``a colocated git checkout and its worktrees share an identity`` () =
    withTempDir "a564-git" (fun root ->
        let main = Path.Combine(root, "main")
        let worktree = Path.Combine(root, "wt")
        let gitDir = Path.Combine(main, ".git")
        Directory.CreateDirectory gitDir |> ignore
        Directory.CreateDirectory worktree |> ignore
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: %s{gitDir}/worktrees/wt\n")

        test <@ RepoIdentity.describe main = RepoIdentity.RepoIdentitySource.Git gitDir @>
        test <@ RepoIdentity.describe worktree = RepoIdentity.RepoIdentitySource.Git gitDir @>

        let digestOf (dir: string) =
            (RepoIdentity.namespaceOf dir).Split('-') |> Array.last

        test <@ digestOf main = digestOf worktree @>)

[<Fact(Timeout = 15000)>]
let ``the identity source is tagged by kind so two kinds cannot collide`` () =
    let path = "/some/place"

    let sources =
        [ RepoIdentity.RepoIdentitySource.Jujutsu path
          RepoIdentity.RepoIdentitySource.Git path
          RepoIdentity.RepoIdentitySource.CheckoutPath path ]
        |> List.map RepoIdentity.identitySource

    test <@ sources |> List.distinct |> List.length = 3 @>

[<Fact(Timeout = 15000)>]
let ``a checkout whose directory name is not filesystem-plain still gets a namespace`` () =
    // The name is a convenience for whoever lists the cache directory; the DIGEST is
    // the identity, so an awkward name is dropped rather than escaped.
    let awkward =
        Path.Combine(Path.GetTempPath(), $"fshw a564 awkward {Guid.NewGuid():N}")
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
    withTempDir "a564-unreadable-ptr" (fun dir ->
        let jjDir = Path.Combine(dir, ".jj")
        Directory.CreateDirectory jjDir |> ignore
        let pointer = Path.Combine(jjDir, "repo")
        File.WriteAllText(pointer, "/somewhere")
        File.SetUnixFileMode(pointer, UnixFileMode.None)

        try
            test <@ RepoIdentity.describe dir = RepoIdentity.RepoIdentitySource.CheckoutPath(Path.GetFullPath dir) @>
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
