/// Two worktrees checked under one virtual root, through one checker: a project whose
/// content is the same in both is checked once, and its results serve both sessions.
/// A project whose content differs is checked at its own worktree's paths.
module FsHotWatch.Tests.RepositoryHostSharingTests

open System
open System.Collections.Concurrent
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.AttachHandshake
open FsHotWatch.Events
open FsHotWatch.RepositoryIdentity
open FsHotWatch.SessionRegistry
open FsHotWatch.SessionScope
open FsHotWatch.Tests.TestHelpers

let private inertWatcher: Daemon.Daemon.WatcherFactory =
    fun _ _ _ _ _ ->
        { Mode = Watcher.WatcherMode.NativeEvents
          Disposables = [] }

let private project =
    """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="Lib.fs" /></ItemGroup></Project>"""

let private libSource = "module Lib\nlet answer: int = \"no\"\n"

let private writeWorktree (root: string) =
    let lib = Path.Combine(root, "src", "Lib")
    Directory.CreateDirectory lib |> ignore
    File.WriteAllText(Path.Combine(lib, "Lib.fsproj"), project)
    File.WriteAllText(Path.Combine(lib, "Lib.fs"), libSource)
    let fsproj = Path.Combine(lib, "Lib.fsproj")

    match
        ProcessHelper.runProcess
            "dotnet"
            $"restore \"%s{fsproj}\""
            root
            []
            (ProcessHelper.ProcessBounds.silent (TimeSpan.FromMinutes 3.0))
    with
    | ProcessHelper.Succeeded _ -> ()
    | other -> failwith $"restore failed: %A{other}"

let private resolved (root: string) =
    match resolveWorktree root with
    | Ok r -> r
    | Error e -> failwith (IdentityError.describe e)

[<NoComparison; NoEquality>]
type private World =
    { A: ResolvedWorktree
      B: ResolvedWorktree
      VirtualRoot: string
      Registry: SessionRegistry
      SessionA: WorktreeSession
      SessionB: WorktreeSession
      Logged: ConcurrentQueue<string> }

let private start (registry: SessionRegistry) (worktree: ResolvedWorktree) =
    let id =
        { Repository = worktree.Repository
          Worktree = worktree.Worktree
          Incarnation = SessionIncarnation.mint () }

    let spec =
        { Worktree = worktree
          Config = ConfigDigest.ofText "sharing"
          Environment = SessionEnvironment.ofProcess worktree.Root.Value
          Sink =
            { Write = ignore
              Level = Logging.LogLevel.Info }
          Owned = [] }

    match registry.Start(id, spec) with
    | Ok session -> session
    | Error e -> failwith e

/// Two sessions of identical worktrees, with the virtual root on or off.
let private withWorldUnder (framesOn: bool) (body: World -> unit) =
    withTempDir "sharing" (fun dir ->
        let canonical =
            match canonicalize dir with
            | Ok c -> c.Value
            | Error e -> failwith (IdentityError.describe e)

        let rootA = Path.Combine(canonical, "r")
        Directory.CreateDirectory(Path.Combine(rootA, ".jj", "repo")) |> ignore
        let rootB = Path.Combine(rootA, ".workspaces", "b")
        Directory.CreateDirectory(Path.Combine(rootB, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(rootB, ".jj", "repo"), "../../../.jj/repo")
        writeWorktree rootA
        writeWorktree rootB

        let virtualRoot = Path.Combine(canonical, "state", "virtual")

        let partitions =
            CheckerPartitions.Partitions Daemon.Daemon.createCheckerWithCacheSizes

        let canonicalProjects = CanonicalProjects.Registry()
        let logged = ConcurrentQueue<string>()

        let factory (spec: SessionSpec) =
            let root = spec.Worktree.Root.Value

            Daemon.Daemon.createUsing
                Daemon.Daemon.createCheckerWithCacheSizes
                root
                { Daemon.Daemon.DaemonOptions.defaults with
                    Hosting =
                        DaemonHosting.hostedUnderFrames
                            inertWatcher
                            partitions.For
                            (if framesOn then
                                 SessionFrames.choice canonicalProjects root virtualRoot logged.Enqueue
                             else
                                 PathFrame.realPaths) }

        use registry = new SessionRegistry(factory)
        let a = resolved rootA
        let b = resolved rootB
        let sessionA = start registry a
        test <@ waitUntilTrue (fun () -> sessionA.Daemon.GetScanGeneration() > 0L) 300000 @>
        let sessionB = start registry b
        test <@ waitUntilTrue (fun () -> sessionB.Daemon.GetScanGeneration() > 0L) 300000 @>

        body
            { A = a
              B = b
              VirtualRoot = virtualRoot
              Registry = registry
              SessionA = sessionA
              SessionB = sessionB
              Logged = logged })

let private withWorld = withWorldUnder true

let private libOf (worktree: ResolvedWorktree) =
    AbsFilePath.create (Path.Combine(worktree.Root.Value, "src", "Lib", "Lib.fs"))

let private check (session: WorktreeSession) (file: AbsFilePath) =
    (session.Daemon.Pipeline.CheckFile file |> Async.RunSynchronously).Value

let private fullCheck (result: FileCheckResult) =
    match result.CheckResults with
    | FullCheck results -> results
    | ParseOnly -> failwith $"%s{AbsFilePath.value result.File} was only parsed"

let private messages (result: FileCheckResult) =
    (fullCheck result).Diagnostics |> Array.map _.Message |> List.ofArray

[<Fact(Timeout = 600000)>]
let ``identical worktrees are checked once, and the result serves both`` () =
    withWorld (fun world ->
        let a = check world.SessionA (libOf world.A)
        let b = check world.SessionB (libOf world.B)

        test <@ a.Frame.IsSome && b.Frame.IsSome @>
        // Each session's own result, for its own file…
        test <@ a.File = libOf world.A && b.File = libOf world.B @>
        // …served from one check: the checker handed both the object it computed once.
        test <@ obj.ReferenceEquals(fullCheck a, fullCheck b) @>
        // The shared result is the right one for each: the planted error, in both.
        test <@ messages a |> List.exists (fun m -> m.Contains "string") @>
        test <@ messages a = messages b @>
        // Nothing was ever written at the virtual root.
        test <@ not (Path.Exists world.VirtualRoot) @>)

[<Fact(Timeout = 600000)>]
let ``a worktree whose project differs is checked at its own paths, and its sibling keeps the virtual root`` () =
    withWorld (fun world ->
        let before = check world.SessionA (libOf world.A)
        File.WriteAllText(AbsFilePath.value (libOf world.B), "module Lib\nlet answer: int = 42\n")

        let b = check world.SessionB (libOf world.B)
        test <@ b.Frame.IsNone @>
        test <@ not (obj.ReferenceEquals(fullCheck b, fullCheck before)) @>
        test <@ List.isEmpty (messages b) @>

        let a = check world.SessionA (libOf world.A)
        test <@ a.Frame.IsSome @>
        test <@ messages a = messages before @>
        test <@ not (Path.Exists world.VirtualRoot) @>)

[<Fact(Timeout = 600000)>]
let ``without the virtual root, the same worktrees are checked twice`` () =
    // The control: one checker alone does not share a project's results. The virtual
    // root does.
    withWorldUnder false (fun world ->
        let a = check world.SessionA (libOf world.A)
        let b = check world.SessionB (libOf world.B)
        test <@ a.Frame.IsNone && b.Frame.IsNone @>
        test <@ not (obj.ReferenceEquals(fullCheck a, fullCheck b)) @>
        test <@ messages a = messages b @>)
