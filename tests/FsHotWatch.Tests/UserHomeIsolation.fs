/// Points every box-wide home fshw resolves — the shared cache (`FSHW_CACHE_HOME`) and
/// the repository control state (`FSHW_STATE_HOME`) — at a directory private to this
/// test run, before any test runs.
///
/// Why: without it every daemon a test starts writes its shared task store into the
/// developer's real `~/.cache/fshw/cache/tasks`, one namespace per throwaway repo. Those
/// accumulated by the hundred thousand and saturated the directory's 65,535 link limit,
/// which can stop a real repository's namespace from being created at all.
///
/// The override is set on the test process's environment, so it reaches everything a
/// test can start: in-process daemons resolve through `FsHwPaths`, and spawned CLIs and
/// daemons inherit it. A test that sets either variable itself (`withEnv`) restores this
/// value, never the real home.
///
/// Compiled into both test assemblies; each registers `install`/`remove` from its own
/// (single) xUnit pipeline startup.
module FsHotWatch.Tests.UserHomeIsolation

open System
open System.IO
open FsHotWatch

/// This run's private root: unique per process, so two concurrent test runs on one box
/// (parallel agents, unit + integration) never share a store.
let root =
    lazy
        (let unique = Guid.NewGuid().ToString("N")

         Path.Combine(Path.GetTempPath(), $"fshw-test-homes-%d{Environment.ProcessId}-%s{unique}")
         |> Path.GetFullPath)

/// The shared cache home every test resolves.
let cacheHome () = Path.Combine(root.Value, "cache")

/// The repository state home every test resolves.
let stateHome () = Path.Combine(root.Value, "state")

/// Point both homes at this run's root. Called once, before any test.
let install () =
    Directory.CreateDirectory root.Value |> ignore
    Environment.SetEnvironmentVariable(FsHwPaths.CacheHomeEnvVar, cacheHome ())
    Environment.SetEnvironmentVariable(FsHwPaths.StateHomeEnvVar, stateHome ())

/// Delete this run's root. Best effort: a daemon still draining at shutdown may hold a
/// file open, and leaking one temp directory beats failing a finished run.
let remove () =
    if root.IsValueCreated then
        try
            if Directory.Exists root.Value then
                Directory.Delete(root.Value, true)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ()

/// True when `path` is `dir` or lies inside it.
let private isUnder (dir: string) (path: string) =
    let dir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar)
    let path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)

    path = dir
    || path.StartsWith(dir + string Path.DirectorySeparatorChar, StringComparison.Ordinal)

/// The box-wide homes this user's real fshw would resolve with no override: the shared
/// cache (`~/.cache/fshw`) and the repository control state (`~/.local/state/fshw`).
let realHomes () =
    let profile = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

    [ FsHwPaths.sharedCacheHomeFrom null (Environment.GetEnvironmentVariable "XDG_CACHE_HOME") profile
      FsHwPaths.stateHomeFrom null (Environment.GetEnvironmentVariable "XDG_STATE_HOME") profile ]

module UserHomeIsolationTests =
    open Swensen.Unquote
    open Xunit

    [<Fact(Timeout = 15000)>]
    let ``no test resolves the real shared cache home`` () =
        let resolved = FsHwPaths.sharedCacheHome ()
        test <@ realHomes () |> List.forall (fun real -> not (isUnder real resolved)) @>

    [<Fact(Timeout = 15000)>]
    let ``no test resolves the real repository state home`` () =
        let resolved = FsHwPaths.stateHome ()
        test <@ realHomes () |> List.forall (fun real -> not (isUnder real resolved)) @>

    // Not "the homes resolve inside the root": a test may point a home at its own temp
    // directory for its duration (`withEnv`), and this would race it. That is still not
    // the real home, which is what the two facts above pin.
    [<Fact(Timeout = 15000)>]
    let ``the private root was installed before any test ran`` () =
        test <@ root.IsValueCreated && Directory.Exists root.Value @>
