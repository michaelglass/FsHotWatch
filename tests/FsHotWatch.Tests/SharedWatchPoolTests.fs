/// One native file-event registration shared by every worktree session under a
/// repository root. The pool must deliver each event to exactly the session that owns
/// it, apply that session's own watch rules, and run the delivery under that session's
/// ExecutionContext — never the stream thread's, never a sibling's.
module FsHotWatch.Tests.SharedWatchPoolTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Watcher
open FsHotWatch.SharedWatchPool
open FsHotWatch.RepositoryIdentity
open FsHotWatch.Tests.TestHelpers

/// A native stream the test drives by hand.
type private FakeStream =
    { Dirs: string list
      Exclusions: string list
      OnFile: string -> unit
      OnCoalesced: string -> unit
      FlowSuppressedAtCreation: bool
      mutable Disposed: bool }

    interface IDisposable with
        member this.Dispose() = this.Disposed <- true

type private FakeNative() =
    let streams = ResizeArray<FakeStream>()

    member _.Streams = List.ofSeq streams

    member _.Factory: NativeFactory =
        fun dirs exclusions onFile onCoalesced _latency ->
            let s =
                { Dirs = dirs
                  Exclusions = exclusions
                  OnFile = onFile
                  OnCoalesced = onCoalesced
                  FlowSuppressedAtCreation = ExecutionContext.IsFlowSuppressed()
                  Disposed = false }

            streams.Add s
            s :> IDisposable

    member this.Live = this.Streams |> List.filter (fun s -> not s.Disposed)

let private noFallback: FallbackFactory =
    fun _ _ _ _ -> failwith "the native stream was expected to start"

let private write (path: string) (text: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)
    path

/// Records what one session was told, in order.
type private Recorder() =
    let seen = ConcurrentQueue<FileChangeKind>()
    member _.OnChange(change: FileChangeKind) = seen.Enqueue change
    member _.Seen = List.ofSeq seen

let private subscribe (pool: WatchPool) anchor root patterns (recorder: Recorder) =
    pool.Subscribe(anchor, root, patterns, 0.25, recorder.OnChange)

/// Two worktrees under one anchor: the primary at `<dir>/r` and `<dir>/r/.workspaces/b`.
let private withTwoSessions body =
    withTempDir "pool" (fun dir ->
        let anchor = Path.Combine(dir, "r")
        let b = Path.Combine(anchor, ".workspaces", "b")
        Directory.CreateDirectory(Path.Combine(b, ".jj")) |> ignore
        let native = FakeNative()
        let pool = WatchPool(native.Factory, noFallback)
        let recA = Recorder()
        let recB = Recorder()
        use _a = subscribe pool anchor anchor [] recA
        use _b = subscribe pool anchor b [] recB
        body native pool anchor b recA recB)

[<Fact(Timeout = 15000)>]
let ``two sessions under one anchor share exactly one native stream over the anchor`` () =
    withTwoSessions (fun native pool anchor _ _ _ ->
        test <@ native.Streams.Length = 1 @>
        let stream = native.Streams.Head
        test <@ stream.Dirs = [ anchor ] @>
        test <@ stream.Exclusions = [ Path.Combine(anchor, ".jj"); Path.Combine(anchor, ".git") ] @>
        test <@ pool.Stats.NativeStreams = 1 @>
        test <@ pool.Stats.Subscribers = 2 @>)

[<Fact(Timeout = 15000)>]
let ``an event in B's source tree reaches B and only B`` () =
    withTwoSessions (fun native _ _ b recA recB ->
        let file = write (Path.Combine(b, "src", "Lib", "A.fs")) "module A"
        native.Streams.Head.OnFile file
        test <@ recB.Seen = [ SourceChanged [ file ] ] @>
        test <@ recA.Seen.IsEmpty @>)

[<Fact(Timeout = 15000)>]
let ``an event in the primary's source tree reaches the primary only`` () =
    withTwoSessions (fun native _ anchor _ recA recB ->
        let file = write (Path.Combine(anchor, "tests", "T", "T.fs")) "module T"
        native.Streams.Head.OnFile file
        test <@ recA.Seen = [ SourceChanged [ file ] ] @>
        test <@ recB.Seen.IsEmpty @>)

[<Fact(Timeout = 15000)>]
let ``an event inside an unattached nested worktree reaches no session and is counted as unowned`` () =
    withTwoSessions (fun native pool anchor _ recA recB ->
        let other = Path.Combine(anchor, ".workspaces", "x")
        Directory.CreateDirectory(Path.Combine(other, ".jj")) |> ignore
        let file = write (Path.Combine(other, "src", "A.fs")) "module A"
        native.Streams.Head.OnFile file
        test <@ recA.Seen.IsEmpty && recB.Seen.IsEmpty @>
        test <@ pool.Stats.EventsUnowned = 1L @>)

[<Fact(Timeout = 15000)>]
let ``a nested worktree created after the probe cached its directory is learned from its metadata event`` () =
    withTempDir "pool-learn" (fun anchor ->
        let native = FakeNative()
        let pool = WatchPool(native.Factory, noFallback)
        let recA = Recorder()
        // A FileCommand pattern is matched anywhere under the root, so before the pool
        // knows `.workspaces/y` is a worktree the primary legitimately owns its match.
        use _a = subscribe pool anchor anchor [ FilePattern.parse "*.ratchet.json" ] recA
        let other = Path.Combine(anchor, ".workspaces", "y")
        let early = write (Path.Combine(other, "early.ratchet.json")) "{}"
        native.Streams.Head.OnFile early
        // `jj workspace add` writes metadata; its event teaches the pool.
        let meta = write (Path.Combine(other, ".jj", "repo")) "../../.jj/repo"
        // The probe must not be what finds it: only the event may teach the pool.
        Directory.Delete(Path.Combine(other, ".jj"), true)
        native.Streams.Head.OnFile meta
        let late = write (Path.Combine(other, "late.ratchet.json")) "{}"
        native.Streams.Head.OnFile late
        test <@ recA.Seen = [ SourceChanged [ early ] ] @>)

[<Fact(Timeout = 15000)>]
let ``each session keeps the legacy watch rules`` () =
    withTempDir "pool-rules" (fun anchor ->
        let native = FakeNative()
        let pool = WatchPool(native.Factory, noFallback)
        let rec' = Recorder()
        use _s = subscribe pool anchor anchor [ FilePattern.parse "*.ratchet.json" ] rec'
        let fire path = native.Streams.Head.OnFile path

        let topLevelFs = write (Path.Combine(anchor, "Loose.fs")) "x"
        let sln = write (Path.Combine(anchor, "App.slnx")) "<Solution />"
        let nestedSln = write (Path.Combine(anchor, "docs", "Other.sln")) "x"
        let ratchet = write (Path.Combine(anchor, "docs", "cov.ratchet.json")) "{}"
        let generated = write (Path.Combine(anchor, "src", "P", "obj", "Gen.fs")) "x"

        let assets =
            write (Path.Combine(anchor, "src", "P", "obj", "project.assets.json")) "{}"

        let fsproj = write (Path.Combine(anchor, "src", "P", "P.fsproj")) "<Project />"
        let untracked = write (Path.Combine(anchor, "docs", "notes.md")) "x"

        for path in [ topLevelFs; sln; nestedSln; ratchet; generated; assets; fsproj; untracked ] do
            fire path

        test
            <@
                rec'.Seen = [ SolutionChanged
                              SourceChanged [ ratchet ]
                              ProjectChanged [ assets ]
                              ProjectChanged [ fsproj ] ]
            @>)

[<Fact(Timeout = 15000)>]
let ``an event whose content did not change is not a change`` () =
    withTwoSessions (fun native _ _ b _ recB ->
        let file = write (Path.Combine(b, "src", "A.fs")) "module A"
        native.Streams.Head.OnFile file
        native.Streams.Head.OnFile file
        test <@ recB.Seen = [ SourceChanged [ file ] ] @>)

[<Fact(Timeout = 15000)>]
let ``delivery runs under the subscribing session's ExecutionContext, not the stream thread's`` () =
    withTempDir "pool-ctx" (fun dir ->
        let anchor = Path.Combine(dir, "r")
        let b = Path.Combine(anchor, ".workspaces", "b")
        Directory.CreateDirectory(Path.Combine(b, ".jj")) |> ignore
        let native = FakeNative()
        let pool = WatchPool(native.Factory, noFallback)
        let scope = AsyncLocal<string>()
        let observed = ConcurrentQueue<string * string>()

        let subscribeAs (name: string) (root: string) =
            // Each session subscribes from its own context, as a hosted Daemon does.
            let ready = new ManualResetEventSlim()
            let mutable sub: IDisposable = null

            let t =
                Thread(fun () ->
                    scope.Value <- name
                    sub <- pool.Subscribe(anchor, root, [], 0.25, (fun _ -> observed.Enqueue(name, scope.Value)))
                    ready.Set())

            t.Start()
            ready.Wait()
            sub

        use _a = subscribeAs "A" anchor
        use _b = subscribeAs "B" b

        test <@ native.Streams.Head.FlowSuppressedAtCreation @>

        let fileA = write (Path.Combine(anchor, "src", "A.fs")) "a"
        let fileB = write (Path.Combine(b, "src", "B.fs")) "b"

        // Fire from a thread whose own context carries neither session.
        let fireFrom (path: string) =
            let t = Thread(fun () -> native.Streams.Head.OnFile path)

            using (ExecutionContext.SuppressFlow()) (fun _ -> t.Start())

            t.Join()

        fireFrom fileA
        fireFrom fileB
        test <@ List.ofSeq observed = [ "A", "A"; "B", "B" ] @>)

[<Fact(Timeout = 15000)>]
let ``a session whose handler throws does not stop delivery to the others`` () =
    withTempDir "pool-throw" (fun dir ->
        let anchor = Path.Combine(dir, "r")
        let b = Path.Combine(anchor, ".workspaces", "b")
        Directory.CreateDirectory(Path.Combine(b, ".jj")) |> ignore
        let native = FakeNative()
        let pool = WatchPool(native.Factory, noFallback)
        let recB = Recorder()

        use _a =
            pool.Subscribe(anchor, anchor, [], 0.25, (fun _ -> failwith "session A is broken"))

        use _b = subscribe pool anchor b [] recB
        let fileA = write (Path.Combine(anchor, "src", "A.fs")) "a"
        let fileB = write (Path.Combine(b, "src", "B.fs")) "b"
        native.Streams.Head.OnFile fileA
        native.Streams.Head.OnFile fileB
        test <@ recB.Seen = [ SourceChanged [ fileB ] ] @>
        test <@ pool.Stats.DeliveryFailures = 1L @>)

[<Fact(Timeout = 15000)>]
let ``the stream lives while any session is subscribed and closes with the last`` () =
    withTempDir "pool-life" (fun dir ->
        let anchor = Path.Combine(dir, "r")
        let b = Path.Combine(anchor, ".workspaces", "b")
        Directory.CreateDirectory b |> ignore
        let native = FakeNative()
        let pool = WatchPool(native.Factory, noFallback)
        let a = subscribe pool anchor anchor [] (Recorder())
        let bSub = subscribe pool anchor b [] (Recorder())
        a.Dispose()
        test <@ native.Live.Length = 1 @>
        bSub.Dispose()
        test <@ native.Live.IsEmpty @>
        test <@ pool.Stats.NativeStreams = 0 @>
        // A later attach opens a fresh stream.
        use _again = subscribe pool anchor anchor [] (Recorder())
        test <@ native.Live.Length = 1 && native.Streams.Length = 2 @>)

[<Fact(Timeout = 15000)>]
let ``disposing a subscription twice detaches it once`` () =
    withTwoSessions (fun native pool anchor _ _ _ ->
        let extra = subscribe pool anchor (Path.Combine(anchor, "sub")) [] (Recorder())
        extra.Dispose()
        extra.Dispose()
        test <@ pool.Stats.Subscribers = 2 @>
        test <@ native.Live.Length = 1 @>)

[<Fact(Timeout = 15000)>]
let ``a session outside the anchor gets its own stream from the same pool`` () =
    withTempDir "pool-far" (fun dir ->
        let anchor = Path.Combine(dir, "r")
        let far = Path.Combine(dir, "far")
        Directory.CreateDirectory anchor |> ignore
        Directory.CreateDirectory far |> ignore
        let native = FakeNative()
        let pool = WatchPool(native.Factory, noFallback)
        use _a = subscribe pool anchor anchor [] (Recorder())
        use _f = subscribe pool far far [] (Recorder())
        test <@ native.Streams |> List.map (fun s -> s.Dirs) |> List.sort = List.sort [ [ anchor ]; [ far ] ] @>)

[<Fact(Timeout = 15000)>]
let ``a must-scan of the anchor makes every session rescan its own discovery roots`` () =
    withTwoSessions (fun native _ anchor b recA recB ->
        let fileA = write (Path.Combine(anchor, "src", "A.fs")) "a"
        let fileB = write (Path.Combine(b, "tests", "B.fs")) "b"
        native.Streams.Head.OnCoalesced anchor
        test <@ recA.Seen = [ SourceChanged [ fileA ] ] @>
        test <@ recB.Seen = [ SourceChanged [ fileB ] ] @>)

[<Fact(Timeout = 15000)>]
let ``a must-scan inside one discovery root rescans only that directory`` () =
    withTwoSessions (fun native _ anchor _ recA recB ->
        let inside = write (Path.Combine(anchor, "src", "Lib", "In.fs")) "a"
        write (Path.Combine(anchor, "src", "Other", "Out.fs")) "b" |> ignore
        native.Streams.Head.OnCoalesced(Path.Combine(anchor, "src", "Lib"))
        test <@ recA.Seen = [ SourceChanged [ inside ] ] @>
        test <@ recB.Seen.IsEmpty @>)

[<Fact(Timeout = 15000)>]
let ``a must-scan outside every discovery root rescans nothing`` () =
    withTwoSessions (fun native _ anchor _ recA _ ->
        write (Path.Combine(anchor, "docs", "D.fs")) "d" |> ignore
        native.Streams.Head.OnCoalesced(Path.Combine(anchor, "docs"))
        test <@ recA.Seen.IsEmpty @>)

[<Fact(Timeout = 15000)>]
let ``a native stream that fails to start leaves that session on its own fallback watcher`` () =
    withTempDir "pool-fail" (fun anchor ->
        let fellBack = ref 0

        let fallback: FallbackFactory =
            fun _ _ _ _ ->
                fellBack.Value <- fellBack.Value + 1

                { Mode = WatcherMode.ContentPolling "test"
                  Disposables = [] }

        let pool =
            WatchPool((fun _ _ _ _ _ -> raise (InvalidOperationException "fseventsd said no")), fallback)

        let watcher = pool.WatcherFactoryFor (anchor, true) anchor ignore None [] 0.25
        test <@ fellBack.Value = 1 @>
        test <@ watcher.Mode = WatcherMode.ContentPolling "test" @>
        test <@ pool.Stats.NativeStreams = 0 && pool.Stats.Subscribers = 0 @>)

[<Fact(Timeout = 15000)>]
let ``the Daemon-shaped factory subscribes on macOS and disposing its watcher detaches`` () =
    withTwoSessions (fun native pool anchor _ _ _ ->
        let watcher =
            pool.WatcherFactoryFor (anchor, true) (Path.Combine(anchor, "third")) ignore None [] 0.25

        test <@ watcher.Mode = WatcherMode.NativeEvents @>
        test <@ pool.Stats.Subscribers = 3 && native.Streams.Length = 1 @>
        (watcher :> IDisposable).Dispose()
        test <@ pool.Stats.Subscribers = 2 @>)

[<Fact(Timeout = 15000)>]
let ``off macOS the Daemon-shaped factory is the per-session fallback`` () =
    withTempDir "pool-linux" (fun anchor ->
        let fellBack = ref 0

        let fallback: FallbackFactory =
            fun _ _ _ _ ->
                fellBack.Value <- fellBack.Value + 1

                { Mode = WatcherMode.NativeEvents
                  Disposables = [] }

        let native = FakeNative()
        let pool = WatchPool(native.Factory, fallback)
        pool.WatcherFactoryFor (anchor, false) anchor ignore None [] 0.25 |> ignore
        test <@ fellBack.Value = 1 && native.Streams.IsEmpty @>)

[<Fact(Timeout = 15000)>]
let ``stats count what the stream received and where it went`` () =
    withTwoSessions (fun native pool anchor b _ _ ->
        let fileB = write (Path.Combine(b, "src", "B.fs")) "b"
        native.Streams.Head.OnFile fileB
        native.Streams.Head.OnFile(Path.Combine(anchor, ".workspaces", "b", ".jj", "x"))
        native.Streams.Head.OnFile "/nowhere/near/A.fs"
        let s = pool.Stats
        test <@ s.EventsReceived = 3L @>
        test <@ s.EventsDelivered = 1L @>
        test <@ s.EventsUnowned = 1L @>
        test <@ s.MetadataEvents = 1L @>)

[<Fact(Timeout = 15000)>]
let ``a session subscribing with flow suppressed still receives its changes`` () =
    withTempDir "pool-noflow" (fun anchor ->
        let native = FakeNative()
        let pool = WatchPool(native.Factory, noFallback)
        let recA = Recorder()

        let sub =
            using (ExecutionContext.SuppressFlow()) (fun _ -> subscribe pool anchor anchor [] recA)

        use _sub = sub
        let file = write (Path.Combine(anchor, "src", "A.fs")) "a"
        native.Streams.Head.OnFile file
        test <@ recA.Seen = [ SourceChanged [ file ] ] @>
        test <@ native.Streams.Head.FlowSuppressedAtCreation @>)

[<Fact(Timeout = 15000)>]
let ``events that arrive after the stream closed reach no one`` () =
    withTempDir "pool-late" (fun anchor ->
        let native = FakeNative()
        let pool = WatchPool(native.Factory, noFallback)
        let recA = Recorder()
        let sub = subscribe pool anchor anchor [] recA
        let file = write (Path.Combine(anchor, "src", "A.fs")) "a"
        let stream = native.Streams.Head
        sub.Dispose()
        stream.OnFile file
        stream.OnCoalesced anchor
        test <@ recA.Seen.IsEmpty @>
        test <@ pool.Stats.EventsUnowned = 1L @>)

[<Fact(Timeout = 15000)>]
let ``the default fallback is the per-worktree watcher`` () =
    withTempDir "pool-default" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        use watcher = defaultFallback root ignore [] 0.25
        test <@ not watcher.Disposables.IsEmpty @>)

// ---------------------------------------------------------------------------
// The watch anchor of a resolved worktree
// ---------------------------------------------------------------------------

let private resolved (root: string) =
    match resolveWorktree root with
    | Ok r -> r
    | Error e -> failwith (IdentityError.describe e)

[<Fact(Timeout = 15000)>]
let ``a jj secondary workspace under its primary is anchored at the primary`` () =
    withTempDir "anchor-jj" (fun dir ->
        let primary = Path.Combine(dir, "repo")
        Directory.CreateDirectory(Path.Combine(primary, ".jj", "repo")) |> ignore
        let ws = Path.Combine(primary, ".workspaces", "b")
        Directory.CreateDirectory(Path.Combine(ws, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(ws, ".jj", "repo"), "../../../.jj/repo")
        let p = resolved primary
        let w = resolved ws
        test <@ anchorOf w = p.Root.Value @>
        test <@ anchorOf p = p.Root.Value @>)

[<Fact(Timeout = 15000)>]
let ``a jj workspace outside its primary is its own anchor`` () =
    withTempDir "anchor-far" (fun dir ->
        let primary = Path.Combine(dir, "repo")
        Directory.CreateDirectory(Path.Combine(primary, ".jj", "repo")) |> ignore
        let ws = Path.Combine(dir, "ws")
        Directory.CreateDirectory(Path.Combine(ws, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(ws, ".jj", "repo"), "../../repo/.jj/repo")
        let w = resolved ws
        test <@ anchorOf w = w.Root.Value @>)

[<Fact(Timeout = 15000)>]
let ``a git worktree under its main checkout is anchored at the main checkout`` () =
    withTempDir "anchor-git" (fun dir ->
        let main = Path.Combine(dir, "main")

        Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "wt"))
        |> ignore

        let wt = Path.Combine(main, "wt")
        Directory.CreateDirectory wt |> ignore
        let gitDir = Path.Combine(main, ".git", "worktrees", "wt")
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: %s{gitDir}")
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..")
        let m = resolved main
        test <@ anchorOf (resolved wt) = m.Root.Value @>)

[<Fact(Timeout = 15000)>]
let ``a standalone directory is its own anchor`` () =
    withTempDir "anchor-alone" (fun dir ->
        let r = resolved dir
        test <@ anchorOf r = r.Root.Value @>)

// ---------------------------------------------------------------------------
// The real stream (macOS only)
// ---------------------------------------------------------------------------

[<Fact(Timeout = 150000)>]
let ``on macOS a real shared stream delivers a write to its owner only`` () =
    if OperatingSystem.IsMacOS() then
        withTempDir "pool-real" (fun dir ->
            let canonical =
                match canonicalize dir with
                | Ok c -> c.Value
                | Error e -> failwith (IdentityError.describe e)

            let anchor = Path.Combine(canonical, "r")
            let b = Path.Combine(anchor, ".workspaces", "b")
            Directory.CreateDirectory(Path.Combine(anchor, "src")) |> ignore
            Directory.CreateDirectory(Path.Combine(b, ".jj")) |> ignore
            Directory.CreateDirectory(Path.Combine(b, "src")) |> ignore
            let pool = WatchPool()
            let recA = Recorder()
            let recB = Recorder()
            use _a = pool.Subscribe(anchor, anchor, [], 0.05, recA.OnChange)
            use _b = pool.Subscribe(anchor, b, [], 0.05, recB.OnChange)
            let file = Path.Combine(b, "src", "Real.fs")

            // FSEvents cold start is an unbounded window for a new directory: probe-write
            // until the stream is live, as the MacFsEvents tests do.
            probeLoop
                (fun n -> File.WriteAllText(file, $"module Real // %d{n}"))
                (fun () -> not recB.Seen.IsEmpty)
                60000

            test <@ recB.Seen |> List.forall (fun c -> c = SourceChanged [ file ]) @>
            test <@ not recB.Seen.IsEmpty @>
            test <@ recA.Seen.IsEmpty @>
            test <@ pool.Stats.NativeStreams = 1 @>

            // The platform-selecting factory joins the same stream.
            let third = Path.Combine(anchor, "third")
            Directory.CreateDirectory third |> ignore
            use _c = pool.WatcherFactoryFor anchor third ignore None [] 0.05
            test <@ pool.Stats.NativeStreams = 1 && pool.Stats.Subscribers = 3 @>)

// ---------------------------------------------------------------------------
// The default pool, on every platform
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``the default pool starts empty, and its platform factory gives a session a working watcher`` () =
    withTempDir "pool-default-ctor" (fun dir ->
        let canonical =
            match canonicalize dir with
            | Ok c -> c.Value
            | Error e -> failwith (IdentityError.describe e)

        Directory.CreateDirectory(Path.Combine(canonical, "src")) |> ignore
        let pool = WatchPool()
        test <@ pool.Stats.NativeStreams = 0 && pool.Stats.Subscribers = 0 @>

        // Off macOS this is the session's own watcher; on macOS it joins a native stream.
        use watcher = pool.WatcherFactoryFor canonical canonical ignore None [] 0.05
        test <@ not watcher.Disposables.IsEmpty @>

        let expectedStreams = if OperatingSystem.IsMacOS() then 1 else 0
        test <@ pool.Stats.NativeStreams = expectedStreams @>)
