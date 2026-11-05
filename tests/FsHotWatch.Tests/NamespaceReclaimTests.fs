/// Shared task-cache namespaces nothing uses are reclaimed, boundedly (AUTOMATION-1424).
module FsHotWatch.Tests.NamespaceReclaimTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.NamespaceReclaim
open FsHotWatch.Tests.TestHelpers

let private now = DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)

/// A namespace directory last used `age` before `now`, with `entries` cache entries.
let private namespaceAt (tasksDir: string) (name: string) (age: TimeSpan) (entries: int) =
    let dir = Path.Combine(tasksDir, name)
    Directory.CreateDirectory dir |> ignore

    for i in 1..entries do
        let entry = Path.Combine(dir, $"entry-%d{i}.json")
        File.WriteAllText(entry, "{}")
        File.SetLastWriteTimeUtc(entry, now - age)

    Directory.SetLastWriteTimeUtc(dir, now - age)
    dir

let private remaining (tasksDir: string) =
    Directory.GetDirectories tasksDir |> Array.map Path.GetFileName |> Set.ofArray

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an empty namespace past the grace is reclaimed and a fresh one is kept`` () =
    withTempDir "ns-empty" (fun tasksDir ->
        namespaceAt tasksDir "leaked" (EmptyGrace + TimeSpan.FromHours 1.0) 0 |> ignore
        namespaceAt tasksDir "being-created" (TimeSpan.FromMinutes 5.0) 0 |> ignore

        let reclaimed = reclaimIn tasksDir "mine" now

        test <@ reclaimed = [ "leaked" ] @>
        test <@ remaining tasksDir = set [ "being-created" ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a namespace with entries is reclaimed only once its owner is idle past MaxIdle`` () =
    withTempDir "ns-idle" (fun tasksDir ->
        namespaceAt tasksDir "abandoned" (MaxIdle + TimeSpan.FromDays 1.0) 3 |> ignore
        namespaceAt tasksDir "in-use" (MaxIdle - TimeSpan.FromDays 1.0) 3 |> ignore

        test <@ reclaimIn tasksDir "mine" now = [ "abandoned" ] @>
        test <@ remaining tasksDir = set [ "in-use" ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a namespace that is only read stays live through its last-used marker`` () =
    withTempDir "ns-read" (fun tasksDir ->
        let dir = namespaceAt tasksDir "read-only" (MaxIdle + TimeSpan.FromDays 10.0) 2
        let marker = Path.Combine(dir, LastUsedMarker)
        File.WriteAllText(marker, "")
        File.SetLastWriteTimeUtc(marker, now - TimeSpan.FromDays 1.0)
        Directory.SetLastWriteTimeUtc(dir, now - MaxIdle - TimeSpan.FromDays 10.0)

        test <@ reclaimIn tasksDir "mine" now |> List.isEmpty @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the caller's own namespace is never reclaimed`` () =
    withTempDir "ns-own" (fun tasksDir ->
        namespaceAt tasksDir "mine" (MaxIdle + TimeSpan.FromDays 100.0) 0 |> ignore

        test <@ reclaimIn tasksDir "mine" now |> List.isEmpty @>
        test <@ remaining tasksDir = set [ "mine" ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``one pass removes at most ReclaimBound namespaces`` () =
    withTempDir "ns-bound" (fun tasksDir ->
        for i in 1 .. ReclaimBound + 5 do
            namespaceAt tasksDir $"leaked-%03d{i}" (EmptyGrace + TimeSpan.FromDays 1.0) 0
            |> ignore

        test <@ (reclaimIn tasksDir "mine" now).Length = ReclaimBound @>
        test <@ (remaining tasksDir).Count = 5 @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``marking a namespace used stamps its last-used marker`` () =
    withTempDir "ns-mark" (fun tasksDir ->
        let dir = namespaceAt tasksDir "mine" (MaxIdle + TimeSpan.FromDays 1.0) 1
        markUsed dir
        let marker = Path.Combine(dir, LastUsedMarker)
        test <@ File.Exists marker @>
        test <@ DateTime.UtcNow - File.GetLastWriteTimeUtc marker < MaxIdle @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``opening a repository's persisted stores marks its shared namespace used`` () =
    withTempDir "ns-open" (fun repoRoot ->
        FsHotWatch.CacheResidency.openPersistedStores repoRoot |> ignore
        let marker = Path.Combine(FsHotWatch.CacheResidency.sharedStoreDir repoRoot, LastUsedMarker)
        test <@ File.Exists marker @>)
