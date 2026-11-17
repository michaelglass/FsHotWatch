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
open FsHotWatch.TestRoots

/// This process's temp directory (`TestRoots.systemTemp`). TMPDIR is never changed: .NET
/// caches the directory a named pipe lives in on first use (the test runner's own pipe,
/// before any startup hook), so moving TMPDIR mid-process splits an in-process pipe server
/// from the CLI it spawns.
let originalTempPath = systemTemp

/// This run's private homes: under this process's root, so two concurrent test runs on
/// one box (parallel agents, unit + integration) never share a store, and removed with it.
let root = lazy (Path.Combine(processRoot.Value, "homes"))

/// Where every scratch directory a test makes lives: `TestRoots.processRoot`
/// (AUTOMATION-1621, AUTOMATION-1775). It holds THIS repository's `global.json`. `dotnet`
/// looks for a global.json in every directory above the one it runs in, so a scratch
/// repository with none of its own (the common case), and every child process run in one,
/// resolves the SDK this repository builds with rather than the machine's newest. Before
/// this, each fixture copied (or forgot) the file itself, and three went red on GitHub's
/// ubuntu-26.04 image 20261111.196, which ships SDK 11.0.100 beside 10.x. A test that needs
/// a directory with NO global.json above it opts out by name with
/// `TestHelpers.withTempDirOutsideRepositorySdk`.
let scratchRoot = processRoot

/// The shared cache home every test resolves.
let cacheHome () = Path.Combine(root.Value, "cache")

/// The repository state home every test resolves.
let stateHome () = Path.Combine(root.Value, "state")

/// Point both homes at this run's root. Called once, before any test.
let install () =
    Directory.CreateDirectory root.Value |> ignore
    Environment.SetEnvironmentVariable(FsHwPaths.CacheHomeEnvVar, cacheHome ())
    Environment.SetEnvironmentVariable(FsHwPaths.StateHomeEnvVar, stateHome ())

/// Delete this run's root, homes and scratch directories alike. Best effort: a daemon
/// still draining at shutdown may hold a file open, and leaking one temp directory beats
/// failing a finished run. The process root is removed again at process exit.
let remove () =
    if processRoot.IsValueCreated then
        try
            if Directory.Exists processRoot.Value then
                Directory.Delete(processRoot.Value, true)
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
    // FSHW-ROOT-001 ok: names the REAL homes only to assert that no test resolves them.
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
