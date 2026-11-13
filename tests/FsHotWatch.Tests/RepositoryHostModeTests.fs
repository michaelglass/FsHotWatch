/// The CLI's side of the repository host: who opts in, how commands parse, how a
/// worktree attaches. That a host killed outright leaves every worktree recoverable by
/// the next command is proven through real CLI processes in FsHotWatch.IntegrationTests.
module FsHotWatch.Tests.RepositoryHostModeTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open CommandTree
open FsHotWatch.Cli
open FsHotWatch.Cli.Program
open FsHotWatch.RepositoryIdentity
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// Opting in
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``a worktree opts in through its config, and the environment overrides it either way`` () =
    let on = "{ \"repositoryHost\": true }"
    let off = "{ \"repositoryHost\": false }"

    let env (value: string) =
        fun (name: string) -> if name = RepositoryHostMode.EnvVar then value else null

    test <@ RepositoryHostMode.configured on @>
    test <@ not (RepositoryHostMode.configured off) @>
    test <@ not (RepositoryHostMode.configured "") @>
    test <@ not (RepositoryHostMode.configured "not json") @>
    test <@ not (RepositoryHostMode.configured "{ \"repositoryHost\": \"yes\" }") @>

    test <@ RepositoryHostMode.enabled on (env null) @>
    test <@ RepositoryHostMode.enabled on (env "") @>
    test <@ not (RepositoryHostMode.enabled off (env null)) @>
    test <@ RepositoryHostMode.enabled off (env "1") @>
    test <@ RepositoryHostMode.enabled off (env " true ") @>
    test <@ not (RepositoryHostMode.enabled on (env "0")) @>

[<Fact(Timeout = 5000)>]
let ``the host is launched with every path quoted for the shell`` () =
    let line =
        RepositoryHostMode.hostShellCommand "/bin/fs hw" "tool run " "/r/it's" "/s/host.log"

    test <@ line = "nohup '/bin/fs hw' tool run host '/r/it'\\''s' >> '/s/host.log' 2>&1 < /dev/null &" @>

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``status, stop and host parse, with --repository where it applies`` () =
    let tree = FsHotWatch.Cli.Program.commandTree
    test <@ CommandTree.parse tree [| "status"; "--repository" |] = Ok(Status(None, [ Repository ])) @>
    test <@ CommandTree.parse tree [| "status"; "build"; "--repository" |] = Ok(Status(Some "build", [ Repository ])) @>
    test <@ CommandTree.parse tree [| "stop"; "--repository" |] = Ok(Stop [ Repository ]) @>
    test <@ CommandTree.parse tree [| "host"; "/some/root" |] = Ok(Host "/some/root") @>

[<Fact(Timeout = 5000)>]
let ``only commands that bring this worktree's daemon up attach; status and stop only observe`` () =
    for command in [ Start; Check []; Confirm []; Format []; Scan; Invalidate; Rerun "build" ] do
        test <@ needsDaemon command @>

    for command in
        [ Check [ RunOnce ]
          Confirm [ ConfirmFlag.RunOnce ]
          Format [ RunOnce ]
          Status(None, [])
          Status(None, [ Repository ])
          Stop []
          Stop [ Repository ]
          Verdict
          Init
          Completions
          DeadCode []
          Host "/r" ] do
        test <@ not (needsDaemon command) @>

[<Fact(Timeout = 5000)>]
let ``the repository status names the host, each session and the shared watcher`` () =
    let json =
        """{"watch":{"nativeStreams":1},"hostPid":7,"endpoint":"fshw-repo-x","sessions":[{"root":"/r","serving":true,"scanGeneration":3},{"root":"/r/.workspaces/b","serving":false,"scanGeneration":0}]}"""

    test
        <@
            renderRepositoryStatus json = [ "Repository host pid 7 on fshw-repo-x: 2 session(s)"
                                            "  /r  serving, scan generation 3"
                                            "  /r/.workspaces/b  starting, scan generation 0"
                                            "  watch: {\"nativeStreams\":1}" ]
        @>

    test
        <@
            renderRepositoryStatus """{"hostPid":7,"endpoint":"e","sessions":[]}""" = [ "Repository host pid 7 on e: 0 session(s)" ]
        @>

[<Fact(Timeout = 5000)>]
let ``the repository status names the host's footprint and lifecycle, and when each session attached`` () =
    let json =
        """{"hostPid":7,"endpoint":"e","startedAt":"2026-10-25T12:00:00.0000000Z","footprint":{"workingSetBytes":3221225472,"managedBytes":2147483648,"gcCommittedBytes":2684354560},"lifecycle":{"attached":3,"rejoined":2,"reloaded":1,"refused":{"binary-mismatch":2,"toolchain-mismatch":1},"ended":{"stopped":1,"faulted":1},"undelivered":0,"previousHostUnclean":true,"previousHostPid":41,"eventLog":"/s/host-events.jsonl"},"sessions":[{"root":"/r","serving":true,"scanGeneration":3,"attachedAt":"2026-10-25T12:01:00.0000000Z"}]}"""

    test
        <@
            renderRepositoryStatus json = [ "Repository host pid 7 on e: 1 session(s), up since 2026-10-25T12:00:00.0000000Z"
                                            "  footprint: working set 3072 MB, managed 2048 MB, GC committed 2560 MB"
                                            "  lifecycle: 3 attached, 2 rejoined, 1 reloaded, 3 refused (binary-mismatch 2, toolchain-mismatch 1), 2 ended (1 faulted), 0 undelivered"
                                            "  the previous host (pid 41) exited without recording a stop; events: /s/host-events.jsonl"
                                            "  /r  serving, scan generation 3, attached 2026-10-25T12:01:00.0000000Z" ]
        @>

    let unknownPid = json.Replace(""","previousHostPid":41""", "")

    test
        <@
            renderRepositoryStatus unknownPid
            |> List.contains "  the previous host exited without recording a stop; events: /s/host-events.jsonl"
        @>

    let clean =
        json.Replace(""""previousHostUnclean":true""", """"previousHostUnclean":false""")

    test
        <@
            renderRepositoryStatus clean
            |> List.forall (fun l -> not (l.Contains "previous host"))
        @>

    let quiet =
        json.Replace(""""refused":{"binary-mismatch":2,"toolchain-mismatch":1}""", """"refused":{}""")

    test
        <@
            renderRepositoryStatus quiet
            |> List.contains
                "  lifecycle: 3 attached, 2 rejoined, 1 reloaded, 0 refused, 2 ended (1 faulted), 0 undelivered"
        @>

[<Fact(Timeout = 5000)>]
let ``the repository status counts each session's accepted and stale-refused publications`` () =
    let json =
        """{"hostPid":7,"endpoint":"e","sessions":[{"root":"/r","serving":true,"scanGeneration":3,"publications":{"accepted":12,"refused":1}},{"root":"/s","serving":true,"scanGeneration":1}]}"""

    test
        <@
            renderRepositoryStatus json = [ "Repository host pid 7 on e: 2 session(s)"
                                            "  /r  serving, scan generation 3, publications 12 accepted / 1 refused as stale"
                                            "  /s  serving, scan generation 1" ]
        @>

[<Fact(Timeout = 5000)>]
let ``the repository status shows the host's check slots and each session's admission`` () =
    let json =
        """{"hostPid":7,"endpoint":"e","scheduler":{"capacity":12,"running":3,"queued":40},"sessions":[{"root":"/r","serving":true,"scanGeneration":3,"admission":{"queued":2,"running":1,"granted":5,"abandoned":1,"waitedMs":1500,"longestWaitMs":900,"heldMs":4200}},{"root":"/s","serving":true,"scanGeneration":1}]}"""

    test
        <@
            renderRepositoryStatus json = [ "Repository host pid 7 on e: 2 session(s)"
                                            "  checks: 3 of 12 slot(s) held, 40 queued"
                                            "  /r  serving, scan generation 3, admission 5 granted / 1 abandoned, 1 running, 2 queued, waited 1500 ms (longest 900 ms), held 4200 ms"
                                            "  /s  serving, scan generation 1" ]
        @>

[<Fact(Timeout = 5000)>]
let ``the repository status shows the host's process slots, held by test hosts and builds`` () =
    let json =
        """{"hostPid":7,"endpoint":"e","processes":{"capacity":12,"running":2,"queued":1},"sessions":[{"root":"/r","serving":true,"scanGeneration":3}]}"""

    test
        <@
            renderRepositoryStatus json = [ "Repository host pid 7 on e: 1 session(s)"
                                            "  processes (test hosts, builds): 2 of 12 slot(s) held, 1 queued"
                                            "  /r  serving, scan generation 3" ]
        @>

[<Fact(Timeout = 5000)>]
let ``the repository status names each session's snapshot epoch`` () =
    let json =
        """{"hostPid":7,"endpoint":"e","sessions":[{"root":"/r","serving":true,"scanGeneration":3,"snapshot":{"epoch":5,"manifest":"0123456789abcdef","refused":2}},{"root":"/s","serving":true,"scanGeneration":1}]}"""

    test
        <@
            renderRepositoryStatus json = [ "Repository host pid 7 on e: 2 session(s)"
                                            "  /r  serving, scan generation 3, snapshot epoch 5 (manifest 0123456789ab, 2 stale refused)"
                                            "  /s  serving, scan generation 1" ]
        @>

// ---------------------------------------------------------------------------
// Attaching
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``a directory that cannot be resolved is refused before anything is launched`` () =
    let launched = ref false

    match
        RepositoryHostMode.attach
            "/unused"
            (fun _ -> launched.Value <- true)
            (TimeSpan.FromSeconds 1.0)
            "/definitely/not/here"
            ""
    with
    | RepositoryHostMode.Attach.Refused message -> test <@ message.Contains "no repository identity" @>
    | other -> failwith $"%A{other}"

    test <@ not launched.Value @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a host that never comes up leaves the worktree to its own daemon within the bound, naming its log`` () =
    withTempDir "mode-nohost" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, ".jj", "repo")) |> ignore

        match RepositoryHostMode.attach (Path.Combine(dir, "state")) ignore (TimeSpan.FromSeconds 2.0) dir "" with
        | RepositoryHostMode.Attach.Unavailable u ->
            test <@ u.Reason = HostLifecycle.FallbackReason.HostDidNotStart @>
            test <@ u.Message.Contains "did not start" && u.Message.Contains "host.log" @>
        | other -> failwith $"%A{other}")

/// A resolvable jj primary at `dir`, and the reply a host serving it would send.
let private attachedReplyFor (dir: string) =
    Directory.CreateDirectory(Path.Combine(dir, ".jj", "repo")) |> ignore

    match resolveWorktree dir with
    | Error e -> failwith (IdentityError.describe e)
    | Ok worktree ->
        let id =
            { Repository = worktree.Repository
              Worktree = worktree.Worktree
              Incarnation = SessionIncarnation.mint () }

        let identity: AttachHandshake.HostIdentity =
            { Protocol = AttachHandshake.ProtocolIdentity.current ()
              Repository = worktree.Repository }

        id,
        AttachHandshake.encodeResponse (
            AttachHandshake.Attached(id, AttachHandshake.AttachDisposition.NewSession, identity)
        )

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a host that leaves between the probe and the reply is relaunched once`` () =
    // The host answered the liveness probe, then idled out before it read the attach:
    // the exchange ends with no reply. That is a host that has gone, not a failure.
    withTempDir "mode-left" (fun dir ->
        let id, reply = attachedReplyFor dir
        let launches = ref 0
        let exchanges = ref 0

        let probe _ =
            launches.Value > 0 || exchanges.Value = 0

        let exchange _ _ =
            async {
                exchanges.Value <- exchanges.Value + 1

                if exchanges.Value = 1 then
                    return Error(RepositoryIpc.ExchangeError.NoReply RepositoryIpc.FrameError.Truncated)
                else
                    return Ok reply
            }

        let outcome =
            RepositoryHostMode.attachWith
                probe
                exchange
                (Path.Combine(dir, "state"))
                (fun _ -> launches.Value <- launches.Value + 1)
                (TimeSpan.FromSeconds 5.0)
                dir
                ""

        match outcome with
        | RepositoryHostMode.Attach.Serving(_, session) -> test <@ session = id @>
        | other -> failwith $"%A{other}"

        test <@ launches.Value = 1 && exchanges.Value = 2 @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a host that never answers the attach is asked once more, then left to the worktree's own daemon`` () =
    withTempDir "mode-silent" (fun dir ->
        attachedReplyFor dir |> ignore
        let launches = ref 0
        let exchanges = ref 0

        let stoppedAnswering (outcome: RepositoryHostMode.Attach) =
            match outcome with
            | RepositoryHostMode.Attach.Unavailable u ->
                test <@ u.Reason = HostLifecycle.FallbackReason.HostStoppedAnswering @>
                u.Message
            | other -> failwith $"%A{other}"

        // Still answering the probe, so nothing is launched over it; one retry, then a
        // fallback rather than an exception.
        let outcome =
            RepositoryHostMode.attachWith
                (fun _ -> true)
                (fun _ _ ->
                    async {
                        exchanges.Value <- exchanges.Value + 1
                        return Error(RepositoryIpc.ExchangeError.Unreachable "no connection")
                    })
                (Path.Combine(dir, "state"))
                (fun _ -> launches.Value <- launches.Value + 1)
                (TimeSpan.FromSeconds 5.0)
                dir
                ""

        let message = stoppedAnswering outcome
        test <@ message.Contains "stopped answering" && message.Contains "host.log" @>

        test <@ launches.Value = 0 && exchanges.Value = 2 @>

        // Gone after the failed exchange, and a relaunch that does not come up: left to
        // the worktree's own daemon without asking again.
        let gone =
            RepositoryHostMode.attachWith
                (fun _ -> exchanges.Value = 2)
                (fun _ _ ->
                    async {
                        exchanges.Value <- exchanges.Value + 1
                        return Error(RepositoryIpc.ExchangeError.NoReply RepositoryIpc.FrameError.Truncated)
                    })
                (Path.Combine(dir, "state"))
                (fun _ -> launches.Value <- launches.Value + 1)
                (TimeSpan.FromMilliseconds 200.0)
                dir
                ""

        test <@ (stoppedAnswering gone).Contains "stopped answering" @>

        test <@ launches.Value = 1 && exchanges.Value = 3 @>)

[<Fact(Timeout = 60000)>]
let ``a host that hangs up mid-frame during the attach is left to the worktree's own daemon, not a crash`` () =
    // The crash under load: the host's endpoint accepted the attach and closed while the
    // CLI was still writing it. The write failed with a broken pipe, which escaped attach
    // and ended the CLI with an unhandled exception. A frame larger than any socket
    // buffer makes the hang-up land mid-write on every run: the host reads only the
    // frame's length, then closes.
    withTempDir "mode-hangup" (fun dir ->
        attachedReplyFor dir |> ignore
        let endpoint = "fshw-test-" + Guid.NewGuid().ToString "N"
        let request = String('x', 4 * 1024 * 1024)
        let exchanges = ref 0

        let opener: Ipc.IpcServer.ConnectionOpener =
            fun pipe _ ->
                async {
                    // Short of 4 bytes is the liveness probe, which connects and closes.
                    let header = Array.zeroCreate<byte> 4

                    do!
                        pipe.ReadAtLeastAsync(header.AsMemory(), header.Length, throwOnEndOfStream = false).AsTask()
                        |> Async.AwaitTask
                        |> Async.Ignore

                    return None
                }

        use cts = new CancellationTokenSource()

        let server =
            Async.StartImmediateAsTask(Ipc.IpcServer.serveConnections (TimeSpan.FromSeconds 5.0) endpoint opener cts)

        try
            test <@ Ipc.IpcServer.acceptsConnection endpoint @>

            let outcome =
                RepositoryHostMode.attachWith
                    (fun _ -> true)
                    (fun _ _ ->
                        exchanges.Value <- exchanges.Value + 1
                        RepositoryIpc.attach endpoint request)
                    (Path.Combine(dir, "state"))
                    ignore
                    (TimeSpan.FromSeconds 5.0)
                    dir
                    ""

            match outcome with
            | RepositoryHostMode.Attach.Unavailable u ->
                test <@ u.Message.Contains "stopped answering" && u.Message.Contains "hung up" @>
            | other -> failwith $"%A{other}"

            // Asked once more, as for any host that left mid-exchange.
            test <@ exchanges.Value = 2 @>
        finally
            cts.Cancel()
            server.Wait(TimeSpan.FromSeconds 10.0) |> ignore)

/// `attachWith` against a host that is up and answers every attach with `reply`.
let private attachAnswered (dir: string) (reply: string) =
    RepositoryHostMode.attachWith
        (fun _ -> true)
        (fun _ _ -> async { return Ok reply })
        (Path.Combine(dir, "state"))
        ignore
        (TimeSpan.FromSeconds 5.0)
        dir
        ""

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a host of a different fshw build is still a stop, and no fallback is recorded`` () =
    withTempDir "mode-otherbuild" (fun dir ->
        attachedReplyFor dir |> ignore

        let worktree =
            match resolveWorktree dir with
            | Ok w -> w
            | Error e -> failwith (IdentityError.describe e)

        let protocol = AttachHandshake.ProtocolIdentity.current ()

        let host: AttachHandshake.HostIdentity =
            { Protocol =
                { protocol with
                    Binary =
                        { protocol.Binary with
                            ContentHash = "another-build" } }
              Repository = worktree.Repository }

        let reply =
            AttachHandshake.encodeResponse (
                AttachHandshake.Refused(
                    AttachHandshake.AttachRefusal.BinaryMismatch(protocol.Binary, host.Protocol.Binary),
                    host
                )
            )

        match attachAnswered dir reply with
        | RepositoryHostMode.Attach.Refused message -> test <@ message.Contains "NOT be restarted" @>
        | other -> failwith $"%A{other}"

        let control =
            repositoryControlPaths (Path.Combine(dir, "state")) worktree.Repository

        test <@ not (File.Exists(HostLifecycle.eventLogPath control)) @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a host that stopped answering but still holds this worktree's session is a stop, naming its pid`` () =
    // Its own daemon cannot take the worktree while that session holds its lock, so a
    // fallback would only fail later, and less clearly.
    withTempDir "mode-held" (fun dir ->
        let id, _ = attachedReplyFor dir
        let record = RepositoryHost.HostSessionRecord.path dir
        Directory.CreateDirectory(Path.GetDirectoryName record) |> ignore

        File.WriteAllText(
            record,
            RepositoryHost.HostSessionRecord.render
                { HostPid = Environment.ProcessId
                  Endpoint = "fshw-repo-held"
                  Session = id }
        )

        let outcome =
            RepositoryHostMode.attachWith
                (fun _ -> true)
                (fun _ _ -> async { return Error(RepositoryIpc.ExchangeError.Unreachable "no connection") })
                (Path.Combine(dir, "state"))
                ignore
                (TimeSpan.FromSeconds 5.0)
                dir
                ""

        match outcome with
        | RepositoryHostMode.Attach.Refused message ->
            test <@ message.Contains "stopped answering" @>

            test
                <@
                    message.Contains $"pid %d{Environment.ProcessId}"
                    && message.Contains "own daemon cannot"
                @>
        | other -> failwith $"%A{other}")

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a fallback is recorded in the repository's event log, naming the host's pid when one is recorded`` () =
    withTempDir "mode-fellback" (fun dir ->
        attachedReplyFor dir |> ignore

        let unavailable =
            match
                RepositoryHostMode.attachWith
                    (fun _ -> false)
                    (fun _ _ -> async { return Error(RepositoryIpc.ExchangeError.Unreachable "unused") })
                    (Path.Combine(dir, "state"))
                    ignore
                    (TimeSpan.FromMilliseconds 200.0)
                    dir
                    ""
            with
            | RepositoryHostMode.Attach.Unavailable u -> u
            | other -> failwith $"%A{other}"

        let log = HostLifecycle.eventLogPath unavailable.Control
        let at = DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc)

        // No pid file: the host never got far enough to write one.
        test <@ RepositoryHostMode.recordFallback at unavailable @>
        Directory.CreateDirectory unavailable.Control.Directory |> ignore
        File.WriteAllText(unavailable.Control.PidFile, "77\n")
        test <@ RepositoryHostMode.recordFallback at unavailable @>
        // A pid file that does not hold a number (torn, or overwritten) names no host.
        File.WriteAllText(unavailable.Control.PidFile, "not-a-pid\n")
        test <@ RepositoryHostMode.recordFallback at unavailable @>

        let written = File.ReadAllLines log |> Array.filter (fun l -> l <> "")
        test <@ written.Length = 3 @>

        let field (line: string) (name: string) =
            use doc = Text.Json.JsonDocument.Parse line

            match doc.RootElement.TryGetProperty name with
            | true, v -> Some(v.ToString())
            | _ -> None

        for line in written do
            test <@ field line "event" = Some "fellBack" @>
            test <@ field line "reason" = Some "host-did-not-start" @>
            test <@ field line "clientPid" = Some(string Environment.ProcessId) @>
            test <@ field line "root" = Some unavailable.Root @>
            test <@ field line "detail" = Some unavailable.Message @>

        test <@ field written[0] "hostPid" = None @>
        test <@ field written[1] "hostPid" = Some "77" @>
        test <@ field written[2] "hostPid" = None @>)

// ---------------------------------------------------------------------------
// Real host processes
// ---------------------------------------------------------------------------

/// A jj primary with one F# project, configured for the smallest possible session.
let private withRepository (body: string -> string -> unit) =
    withTempDir "mode-host" (fun dir ->
        let root =
            match canonicalize dir with
            | Ok c -> Path.Combine(c.Value, "r")
            | Error e -> failwith (IdentityError.describe e)

        Directory.CreateDirectory(Path.Combine(root, ".jj", "repo")) |> ignore
        let lib = Path.Combine(root, "src", "Lib")
        Directory.CreateDirectory lib |> ignore

        File.WriteAllText(
            Path.Combine(lib, "Lib.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="Lib.fs" /></ItemGroup></Project>"""
        )

        File.WriteAllText(Path.Combine(lib, "Lib.fs"), "module Lib\nlet answer = 42\n")

        let config =
            """{ "build": false, "format": false, "lint": false, "repositoryHost": true }"""

        File.WriteAllText(Path.Combine(root, ".fshw.json"), config)
        body root (Path.Combine(Path.GetDirectoryName root, "state")))

// ---------------------------------------------------------------------------
// The host verb, in process
// ---------------------------------------------------------------------------

/// Serve `root`'s session through the host verb in this process, check it end to end, and
/// stop it on request. `logged` is what the session's log must say about its virtual root
/// and import store.
let private serveInProcessAndStop (root: string) (stateHome: string) (logged: string) =
    let run = Task.Run(fun () -> runHostVerb defaultGlobalOptions root)

    // A failed assertion still stops the host before the process moves
    // back and the scratch tree goes: the host stood the process in its
    // control directory, inside that tree, and a host left serving keeps
    // evaluating there (AUTOMATION-1295).
    try
        let config = File.ReadAllText(Path.Combine(root, ".fshw.json"))

        let endpoint, session =
            match RepositoryHostMode.attach stateHome ignore (TimeSpan.FromSeconds 60.0) root config with
            | RepositoryHostMode.Attach.Serving(endpoint, session) -> endpoint, session
            | other -> failwith $"%A{other}"

        let link: HostLink =
            { Endpoint = endpoint
              Session = fun () -> session
              Reattach = fun () -> true }

        let ipc = sessionIpcOps link
        test <@ ipc.IsRunning "ignored" @>

        test <@ not (String.IsNullOrWhiteSpace(ipc.GetStatus "ignored" |> Async.RunSynchronously)) @>

        test <@ not (String.IsNullOrWhiteSpace(ipc.ScanStatus "ignored" |> Async.RunSynchronously)) @>

        test <@ repositoryStatus false root = 0 @>
        test <@ repositoryStatus true root = 0 @>

        // The session's own log names the host behind it.
        let log =
            File.ReadAllText(FsHotWatch.Cli.DaemonConfig.DaemonLog.file root FsHotWatch.Cli.DaemonConfig.DefaultLogDir)
        test <@ log.Contains $"session=%s{SessionId.render session}" @>
        // …and whether it checks under the repository's virtual root.
        test <@ log.Contains $"virtualRoot=%s{logged}" @>
        // …and whether it reads in-repository references from the store.
        test <@ log.Contains $"importStore=%s{logged}" @>

        test <@ stopRepositoryHost root = 0 @>
        test <@ run.Wait(TimeSpan.FromSeconds 60.0) && run.Result = 0 @>
        test <@ repositoryStatus false root = 0 @>
        test <@ stopRepositoryHost root = 0 @>
    finally
        if not run.IsCompleted then
            stopRepositoryHost root |> ignore
            run.Wait(TimeSpan.FromSeconds 60.0) |> ignore

/// `fshw host` run in this process (it pins the process working directory and reads
/// the state home from the environment, so the test restores both).
[<Collection(LogGlobalCollectionName)>]
type HostVerbInProcess() =
    [<Theory(Timeout = 300000)>]
    [<InlineData("", "on")>]
    [<InlineData("0", "off")>]
    member _.``the host verb serves a session end to end, and stops on request``(switchedTo: string, logged: string) =
        withRepository (fun root stateHome ->
            let switch = if switchedTo = "" then None else Some switchedTo

            // Both controls together: each run says how it checked.
            withEnv RepositoryHostMode.VirtualRootEnvVar switch (fun () ->
                withEnv RepositoryHostMode.ImportStoreEnvVar switch (fun () ->
                    withEnv "FSHW_STATE_HOME" (Some stateHome) (fun () ->
                        // The host verb stands the process in its control directory.
                        withProcessWorkingDirectory None (fun () -> serveInProcessAndStop root stateHome logged)))))

    [<Fact(Timeout = 60000)>]
    member _.``the host verb refuses a root it cannot resolve``() =
        test <@ runHostVerb defaultGlobalOptions "/definitely/not/here" = 2 @>

    [<Fact(Timeout = 60000)>]
    member _.``the SDK probe answers with a version, or says why it could not``() =
        withRepository (fun root _ ->
            let here = SessionScope.SessionEnvironment.ofProcess root
            let version = RepositoryHostMode.sdkVersion root here
            test <@ version.Length > 0 && Char.IsDigit version[0] @>

            // With no `dotnet` on the session's PATH, the probe says it could not run.
            let nowhere =
                SessionScope.SessionEnvironment.create root (Map.ofList [ "PATH", "/nonexistent" ])

            test <@ (RepositoryHostMode.sdkVersion root nowhere).StartsWith "unresolved" @>)


// ---------------------------------------------------------------------------
// What an attach comes to, against a host in this process
// ---------------------------------------------------------------------------

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

let private inertWatcher: FsHotWatch.Daemon.Daemon.WatcherFactory =
    fun _ _ _ _ _ ->
        { Mode = Watcher.WatcherMode.NativeEvents
          Disposables = [] }

let private inProcessHost (root: string) (stateHome: string) (factory: SessionRegistry.SessionFactory) =
    let worktree =
        match resolveWorktree root with
        | Ok w -> w
        | Error e -> failwith (IdentityError.describe e)

    let settings: RepositoryHost.HostSettings =
        { Identity =
            { Protocol = AttachHandshake.ProtocolIdentity.current ()
              Repository = worktree.Repository }
          Control = repositoryControlPaths stateHome worktree.Repository
          // Whatever the attaching side sends: this very process's environment.
          Environment = SessionScope.SessionEnvironment.ofProcess root
          Toolchain = "sdk"
          ToolchainOf = fun _ _ -> "sdk"
          SinkFor =
            fun _ ->
                { Write = ignore
                  Level = Logging.LogLevel.Info
                  File = None }
          WatchConfig =
            fun _ _ ->
                { new IDisposable with
                    member _.Dispose() = () }
          SessionResources = fun _ -> []
          Describe = fun () -> Text.Json.Nodes.JsonObject() }

    let cts = new CancellationTokenSource()

    let run =
        Task.Run(fun () -> RepositoryHost.run settings factory (TimeSpan.FromMinutes 5.0) cts)

    test <@ waitUntilTrue (fun () -> RepositoryIpc.isRunning settings.Control.Endpoint) 20000 @>
    cts, run

let private daemonFactory: SessionRegistry.SessionFactory =
    fun spec ->
        FsHotWatch.Daemon.Daemon.createWithWatcherFactory
            nullChecker
            spec.Worktree.Root.Value
            { watchingDaemonOptions with
                Hosting =
                    FsHotWatch.DaemonHosting.hostedBy inertWatcher (fun _ -> nullChecker) (WorkScheduler.Scheduler 1) }
            inertWatcher

[<Fact(Timeout = 120000)>]
let ``a worktree its own daemon holds stays with that daemon`` () =
    withRepository (fun root stateHome ->
        use legacy = (RepositoryHost.tryLockWorktree root).Value
        let cts, run = inProcessHost root stateHome daemonFactory

        try
            match RepositoryHostMode.attach stateHome ignore (TimeSpan.FromSeconds 10.0) root "" with
            | RepositoryHostMode.Attach.OwnDaemon reason -> test <@ reason.Contains "fshw stop" @>
            | other -> failwith $"%A{other}"
        finally
            cts.Cancel()
            run.Wait(TimeSpan.FromSeconds 60.0) |> ignore)

[<Fact(Timeout = 120000)>]
let ``a session the host cannot build is a stop, not a fallback`` () =
    withRepository (fun root stateHome ->
        let cts, run =
            inProcessHost root stateHome (fun _ -> invalidOp "no projects discovered")

        try
            match RepositoryHostMode.attach stateHome ignore (TimeSpan.FromSeconds 10.0) root "" with
            | RepositoryHostMode.Attach.Refused reason -> test <@ reason.Contains "no projects discovered" @>
            | other -> failwith $"%A{other}"
        finally
            cts.Cancel()
            run.Wait(TimeSpan.FromSeconds 60.0) |> ignore)

/// Write an executable `dotnet` into `bin` that runs `body`, from a child shell, so this
/// process never holds a write handle on it. A test host forks children concurrently, and
/// a child forked while the file is open for writing here keeps a copy of that handle
/// until it execs; Linux refuses to exec a file open for writing (ETXTBSY, "Text file
/// busy"), which made the probe report "could not run" instead of what the test asks.
let private writeFakeDotnet (bin: string) (body: string) =
    Directory.CreateDirectory bin |> ignore
    let fake = Path.Combine(bin, "dotnet")
    let start = ProcessStartInfo("/bin/sh", UseShellExecute = false)

    for arg in
        [ "-c"
          "printf '%s' \"$1\" > \"$2\" && chmod 700 \"$2\""
          "sh"
          $"#!/bin/sh\n%s{body}\n"
          fake ] do
        start.ArgumentList.Add arg

    use writer = Process.Start start
    test <@ writer.WaitForExit 30000 && writer.ExitCode = 0 @>

[<Fact(Timeout = 60000)>]
let ``an SDK probe whose dotnet fails says how`` () =
    if not (OperatingSystem.IsWindows()) then
        withRepository (fun root _ ->
            let bin = Path.Combine(root, "fake-bin")
            writeFakeDotnet bin "exit 3"

            let env =
                SessionScope.SessionEnvironment.create root (Map.ofList [ "PATH", $"%s{bin}:/usr/bin:/bin" ])

            test <@ RepositoryHostMode.sdkVersion root env = "unresolved (`dotnet --version` exited 3)" @>)

[<Fact(Timeout = 60000)>]
let ``an SDK probe that does not finish within its bound says so`` () =
    if not (OperatingSystem.IsWindows()) then
        withRepository (fun root _ ->
            let bin = Path.Combine(root, "slow-bin")
            writeFakeDotnet bin "sleep 30"

            let env =
                SessionScope.SessionEnvironment.create root (Map.ofList [ "PATH", $"%s{bin}:/usr/bin:/bin" ])

            test
                <@
                    RepositoryHostMode.sdkVersionWithin (TimeSpan.FromMilliseconds 500.0) root env = "unresolved (`dotnet --version` did not complete)"
                @>)

[<Fact(Timeout = 60000)>]
let ``an attach reply the host cannot have written is a stop, naming why`` () =
    withRepository (fun root stateHome ->
        let worktree =
            match resolveWorktree root with
            | Ok w -> w
            | Error e -> failwith (IdentityError.describe e)

        let control = repositoryControlPaths stateHome worktree.Repository

        // Something on the repository's endpoint that answers every request with bytes
        // no host writes.
        let opener: Ipc.IpcServer.ConnectionOpener =
            fun pipe _ ->
                async {
                    match! RepositoryIpc.readFrame pipe CancellationToken.None |> Async.AwaitTask with
                    | Ok _ ->
                        do!
                            RepositoryIpc.writeFrame pipe "not json" CancellationToken.None
                            |> Async.AwaitTask
                            |> Async.Ignore
                    | Error _ -> ()

                    return None
                }

        use cts = new CancellationTokenSource()

        // Started on this thread: the server creates its listening instances before its
        // first wait, so it is listening when this returns. Queued to the thread pool
        // instead, it can wait behind a loaded runner's other work for longer than any
        // poll allows.
        let server =
            Async.StartImmediateAsTask(
                Ipc.IpcServer.serveConnections (TimeSpan.FromSeconds 5.0) control.Endpoint opener cts
            )

        try
            // Listening the moment it has started, not at some later point a poll must catch.
            test <@ Ipc.IpcServer.acceptsConnection control.Endpoint @>

            match RepositoryHostMode.attach stateHome ignore (TimeSpan.FromSeconds 10.0) root "" with
            | RepositoryHostMode.Attach.Refused reason -> test <@ reason.Contains "could not be read" @>
            | other -> failwith $"%A{other}"
        finally
            cts.Cancel()
            server.Wait(TimeSpan.FromSeconds 10.0) |> ignore)


[<Fact(Timeout = 5000)>]
let ``the repository status says which projects each worktree checks at its own paths, and why`` () =
    let json =
        """{"hostPid":7,"endpoint":"e","sessions":[{"root":"/r","serving":true,"scanGeneration":3}],"ownPaths":{"/r":[{"project":"src/App/App.fsproj","reason":"references-own-paths","upstreams":["src/Up/Up.fsproj","src/Other/Other.fsproj"]},{"project":"src/Lib/Lib.fsproj","reason":"not-canonical","heldBy":["/r/.workspaces/b"]},{"project":"src/Up/Up.fsproj","reason":"reads-its-location","construct":"__SOURCE_DIRECTORY__","where":"src/Up/Up.fs"}],"/r/.workspaces/b":[{"project":"src/New/New.fsproj","reason":"a-reason-from-a-later-host"},{"project":"src/Odd/Odd.fsproj"},{"project":"src/C/C.fsproj","reason":"not-canonical"}],"/r/.workspaces/c":[]}}"""

    test
        <@
            renderRepositoryStatus json = [ "Repository host pid 7 on e: 1 session(s)"
                                            "  /r  serving, scan generation 3"
                                            "  /r checks 3 project(s) at its own paths:"
                                            "    src/App/App.fsproj: references src/Up/Up.fsproj, src/Other/Other.fsproj, checked at their own paths"
                                            "    src/Lib/Lib.fsproj: its content differs from what /r/.workspaces/b checks under the virtual root"
                                            "    src/Up/Up.fsproj: it uses __SOURCE_DIRECTORY__ in src/Up/Up.fs"
                                            "  /r/.workspaces/b checks 1 project(s) at its own paths:"
                                            "    src/C/C.fsproj: its content differs from what another worktree checks under the virtual root" ]
        @>
