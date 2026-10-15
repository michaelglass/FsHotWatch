module FsHotWatch.Tests.ToolchainScopedTaskCacheTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.TaskCache
open FsHotWatch.FileTaskCache
open FsHotWatch.DotnetToolchain
open FsHotWatch.Tests.TestHelpers

// A task cache keyed on the tree alone replayed a build that failed because the wrong
// SDK resolved ("A compatible .NET SDK was not found") as `(cached)` after PATH and
// DOTNET_ROOT were fixed: the tree had not moved, so neither had the key.
//
// RED-BEFORE-GREEN: make `scopeToToolchain` return `key` unchanged and the
// different-SDK tests hit.

let private wrongSdk =
    describe "failed (exit 1): A compatible .NET SDK was not found." (Some "/nix/sdk-10.0.302") None

let private pinnedSdk = describe "10.0.400" (Some "/mise/dotnet-10.0.400") None

let private buildKey: CompositeKey = { Plugin = "build"; File = None }

let private failedBuild (key: ContentHash) =
    { CacheKey = key
      Errors = []
      Status =
        CachedRunFailed("A compatible .NET SDK was not found.", RunVerdict.create "build failed" System.TimeSpan.Zero)
      EmittedEvents = [] }

[<Fact(Timeout = 15000)>]
let ``the same tree under a different resolved SDK is a miss naming the toolchain`` () =
    let store = InMemoryTaskCache() :> ITaskCache
    let tree = merkleCacheKey [ "inputs", "tree-1" ]

    (ToolchainScopedTaskCache(store, wrongSdk) :> ITaskCache).Set buildKey tree (failedBuild tree)

    let fixedEnv = ToolchainScopedTaskCache(store, pinnedSdk) :> ITaskCache

    test <@ fixedEnv.Lookup buildKey tree = CacheMiss(CacheMissReason.InputsChanged [ ToolchainLabel ]) @>

[<Fact(Timeout = 15000)>]
let ``the same tree under the same resolved SDK is a hit under the requested key`` () =
    let store = InMemoryTaskCache() :> ITaskCache
    let tree = merkleCacheKey [ "inputs", "tree-2" ]

    (ToolchainScopedTaskCache(store, pinnedSdk) :> ITaskCache).Set buildKey tree (failedBuild tree)

    let sameEnv = ToolchainScopedTaskCache(store, pinnedSdk) :> ITaskCache

    test <@ sameEnv.TryGet buildKey tree = Some(failedBuild tree) @>

[<Fact(Timeout = 15000)>]
let ``a persisted wrong-SDK failure is not replayed by a later process with the SDK fixed`` () =
    withTempDir "toolchain-scope" (fun dir ->
        let tree = merkleCacheKey [ "inputs", "tree-3" ]

        // The first run-once, inside an environment that resolved the wrong SDK.
        let broken =
            Resolver(fun _ -> "failed (exit 1): A compatible .NET SDK was not found.")

        (ToolchainScopedTaskCache(FileTaskCache dir, broken.Identity dir) :> ITaskCache).Set
            buildKey
            tree
            (failedBuild tree)

        // The next run-once: a new process, a new store over the same directory, the
        // pinned SDK resolved.
        let fixedEnv = Resolver(fun _ -> "10.0.400")

        let fresh =
            ToolchainScopedTaskCache(FileTaskCache dir, fixedEnv.Identity dir) :> ITaskCache

        test <@ fresh.TryGet buildKey tree = None @>

        // And a process that resolves the same SDK as the writer still replays it.
        let sameAgain =
            Resolver(fun _ -> "failed (exit 1): A compatible .NET SDK was not found.")

        let replaying =
            ToolchainScopedTaskCache(FileTaskCache dir, sameAgain.Identity dir) :> ITaskCache

        test <@ replaying.TryGet buildKey tree = Some(failedBuild tree) @>)

[<Fact(Timeout = 15000)>]
let ``the resolver asks dotnet once per SDK selection, not once per lookup`` () =
    withTempDir "toolchain-once" (fun dir ->
        let mutable asked = 0

        let resolver =
            Resolver(fun _ ->
                asked <- asked + 1
                "10.0.400")

        let first = resolver.Identity dir
        let second = resolver.Identity dir

        test <@ asked = 1 @>
        test <@ first = second @>

        // A global.json is a different SDK selection, so it is asked again.
        File.WriteAllText(Path.Combine(dir, "global.json"), """{ "sdk": { "version": "10.0.400" } }""")
        let pinned = resolver.Identity dir

        test <@ asked = 2 @>
        test <@ pinned <> first @>)

[<Fact(Timeout = 15000)>]
let ``the identity names the version dotnet resolved, DOTNET_ROOT and global.json`` () =
    test <@ describe "10.0.302" None None <> describe "10.0.400" None None @>
    test <@ describe "10.0.400" (Some "/a") None <> describe "10.0.400" (Some "/b") None @>
    test <@ describe "10.0.400" None (Some "{}") <> describe "10.0.400" None None @>

[<Fact(Timeout = 15000)>]
let ``a dotnet that cannot resolve an SDK is an answer of its own`` () =
    let succeeded =
        answerOf (fun () ->
            FsHotWatch.ProcessHelper.Succeeded(FsHotWatch.ProcessHelper.ProcessOutput.Drained "10.0.400\n"))

    let failed =
        answerOf (fun () ->
            FsHotWatch.ProcessHelper.Failed(
                1,
                FsHotWatch.ProcessHelper.ProcessOutput.Drained "A compatible .NET SDK was not found."
            ))

    let timedOut =
        answerOf (fun () ->
            FsHotWatch.ProcessHelper.TimedOut(
                System.TimeSpan.FromSeconds 30.0,
                FsHotWatch.ProcessHelper.ProcessOutput.Drained "",
                FsHotWatch.ProcessHelper.KillOutcome.Killed
            ))

    let threw =
        answerOf (fun () -> raise (System.ComponentModel.Win32Exception "No such file or directory"))

    test <@ succeeded = "10.0.400" @>
    test <@ failed = "failed (exit 1): A compatible .NET SDK was not found." @>
    test <@ timedOut.StartsWith "failed: timed out after 30s" @>
    test <@ threw = "failed: Win32Exception: No such file or directory" @>

[<Fact(Timeout = 15000)>]
let ``a key with no recorded inputs still misses under another SDK, without naming one`` () =
    let store = InMemoryTaskCache() :> ITaskCache
    // A commit-id key: not minted by `merkleCacheKey`, so no inputs are on record.
    let commit = ContentHash.create "commit-without-fingerprint"

    (ToolchainScopedTaskCache(store, wrongSdk) :> ITaskCache).Set buildKey commit (failedBuild commit)

    let fixedEnv = ToolchainScopedTaskCache(store, pinnedSdk) :> ITaskCache

    test <@ fixedEnv.Lookup buildKey commit = CacheMiss CacheMissReason.InputsNotComparable @>

[<Fact(Timeout = 15000)>]
let ``clearing through the scope clears the store beneath it`` () =
    let store = InMemoryTaskCache() :> ITaskCache
    let scoped = ToolchainScopedTaskCache(store, pinnedSdk) :> ITaskCache
    let key = ContentHash.create "k"
    let lintA: CompositeKey = { Plugin = "lint"; File = Some "A.fs" }
    let lintB: CompositeKey = { Plugin = "lint"; File = Some "B.fs" }

    let fmtA: CompositeKey =
        { Plugin = "format-check"
          File = Some "A.fs" }

    let fill () =
        for composite in [ buildKey; lintA; lintB; fmtA ] do
            scoped.Set composite key (failedBuild key)

    fill ()
    scoped.ClearPluginFile "lint" "A.fs"
    test <@ scoped.TryGet lintA key = None && (scoped.TryGet lintB key).IsSome @>

    scoped.ClearFile "A.fs"
    test <@ scoped.TryGet fmtA key = None && (scoped.TryGet buildKey key).IsSome @>

    scoped.ClearPlugin "lint"
    test <@ scoped.TryGet lintB key = None && (scoped.TryGet buildKey key).IsSome @>

    fill ()
    scoped.Clear()

    test
        <@
            [ buildKey; lintA; lintB; fmtA ]
            |> List.forall (fun c -> scoped.TryGet c key = None)
        @>
