/// Every integration test runs inside its own process scope, as a daemon's work runs
/// inside the daemon's: whatever a test spawns, directly or through a plugin it
/// registers, is admitted to a registry that reaps it when the test ends.
///
/// A spawn that still reaches `ProcessRegistry` with no registry in scope is logged
/// as unreapable. The scope's log sink records those lines and fails the test that
/// owns them, so a spawn path that escapes the scope cannot land unnoticed.
module FsHotWatch.Tests.ProcessScopeGuard

open System
open System.Collections.Concurrent
open System.Threading
open Xunit.v3
open FsHotWatch

/// The warning `ProcessRegistry` logs for a spawn or leak it has nowhere to record.
let UnscopedMarker = "no registry in scope"

/// One test's process scope: the registry its children are admitted to, the lines
/// logged in its context, and the installs that `close` undoes.
type Scope =
    { Registry: ProcessRegistry.Registry
      Lines: ConcurrentQueue<string>
      Installs: IDisposable list }

/// Install a fresh registry and a recording log sink in the current context. Lines
/// still reach stderr, at the verbosity stderr would have shown them, and a warning
/// is always recorded whatever that verbosity is.
let openScope () : Scope =
    let lines = ConcurrentQueue<string>()
    let registry = ProcessRegistry.Registry()

    let sink: Logging.LogSink =
        { Write =
            fun line ->
                lines.Enqueue line
                eprintfn "%s" line
          Level = max Logging.logLevel Logging.LogLevel.Warning }

    let logInstall = Logging.installSink sink
    let registryInstall = ProcessRegistry.install registry

    { Registry = registry
      Lines = lines
      Installs = [ registryInstall; logInstall ] }

/// The recorded lines that name an unreapable spawn or leak.
let unscopedLines (scope: Scope) : string list =
    scope.Lines |> Seq.filter (fun l -> l.Contains UnscopedMarker) |> List.ofSeq

/// Reap everything still in the scope, restore the prior registry and sink, and fail
/// if any spawn in this scope was logged as unreapable.
let close (scope: Scope) : unit =
    scope.Registry.KillAll()

    for install in scope.Installs do
        install.Dispose()

    match unscopedLines scope with
    | [] -> ()
    | lines ->
        let shown = String.concat "\n" lines

        failwith
            $"%d{lines.Length} spawn(s) had no process registry in scope and could not be reaped; the plugin host or process helper that spawned them needs a scope:\n%s{shown}"

/// Opens a scope before each test and closes it after, keyed by the test, since one
/// assembly-level instance serves every test and tests run in parallel.
[<AttributeUsage(AttributeTargets.Assembly)>]
type ProcessScopePerTestAttribute() =
    inherit BeforeAfterTestAttribute()

    static let scopes = ConcurrentDictionary<string, Scope>()

    override _.Before(_methodUnderTest, test) = scopes[test.UniqueID] <- openScope ()

    override _.After(_methodUnderTest, test) =
        match scopes.TryRemove test.UniqueID with
        | true, scope -> close scope
        | false, _ -> ()

[<assembly: ProcessScopePerTest>]
do ()
