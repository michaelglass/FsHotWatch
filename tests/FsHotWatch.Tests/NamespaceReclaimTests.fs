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
        // A marker stamped long ago is restamped on the next use.
        File.SetLastWriteTimeUtc(marker, now - MaxIdle - TimeSpan.FromDays 1.0)
        markUsed dir
        test <@ DateTime.UtcNow - File.GetLastWriteTimeUtc marker < MaxIdle @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``opening a repository's persisted stores marks its shared namespace used`` () =
    withTempDir "ns-open" (fun repoRoot ->
        FsHotWatch.CacheResidency.openPersistedStores repoRoot |> ignore

        let marker =
            Path.Combine(FsHotWatch.CacheResidency.sharedStoreDir repoRoot, LastUsedMarker)

        test <@ File.Exists marker @>)

/// `body` with `dir`'s permissions set to `mode`, restored afterwards so the scratch tree
/// can be deleted.
let private withMode (dir: string) (mode: UnixFileMode) (body: unit -> unit) =
    let before = File.GetUnixFileMode dir
    File.SetUnixFileMode(dir, mode)

    try
        body ()
    finally
        File.SetUnixFileMode(dir, before)

let private readOnly = UnixFileMode.UserRead ||| UnixFileMode.UserExecute

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``reclaiming where no tasks directory exists removes nothing`` () =
    withTempDir "ns-none" (fun dir -> test <@ reclaimIn (Path.Combine(dir, "absent")) "mine" now |> List.isEmpty @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a namespace that cannot be removed is left for a later pass`` () =
    withTempDir "ns-stuck" (fun tasksDir ->
        namespaceAt tasksDir "leaked" (EmptyGrace + TimeSpan.FromDays 1.0) 0 |> ignore

        withMode tasksDir readOnly (fun () -> test <@ reclaimIn tasksDir "mine" now |> List.isEmpty @>)

        test <@ remaining tasksDir = set [ "leaked" ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a namespace that cannot be read is not judged unused`` () =
    withTempDir "ns-unreadable" (fun tasksDir ->
        let dir = namespaceAt tasksDir "sealed" (MaxIdle + TimeSpan.FromDays 1.0) 1
        let stamp = Directory.GetLastWriteTimeUtc dir

        withMode dir UnixFileMode.None (fun () -> test <@ reclaimIn tasksDir "mine" now |> List.isEmpty @>)

        Directory.SetLastWriteTimeUtc(dir, stamp)
        test <@ remaining tasksDir = set [ "sealed" ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a namespace that cannot be created is not marked, and nothing is raised`` () =
    withTempDir "ns-uncreatable" (fun tasksDir ->
        let dir = Path.Combine(tasksDir, "mine")

        withMode tasksDir readOnly (fun () -> markUsed dir)

        test <@ not (Directory.Exists dir) @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``opening a repository's persisted stores reclaims an unused sibling namespace`` () =
    withTempDir "ns-open-reclaim" (fun repoRoot ->
        let tasksDir =
            Path.GetDirectoryName(FsHotWatch.CacheResidency.sharedStoreDir repoRoot)

        let leaked = $"leaked-{Guid.NewGuid():N}"
        let dir = Path.Combine(tasksDir, leaked)
        Directory.CreateDirectory dir |> ignore
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - EmptyGrace - TimeSpan.FromDays 1.0)

        FsHotWatch.CacheResidency.openPersistedStores repoRoot |> ignore

        test <@ not (Directory.Exists dir) @>)
