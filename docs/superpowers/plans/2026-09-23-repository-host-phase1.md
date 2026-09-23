# Repository host, phase 1: one process, many worktree sessions

Status: plan, awaiting go. Measured against `main@origin` `ff8b9b43`.

Phase 1 puts N worktrees in one opt-in host process. Each worktree gets a
`WorktreeSession` that wraps today's `Daemon` unchanged: its own checker, plugin host
and `.fshw` state. The sessions share the process, the runtime, JIT'd code, loaded
assemblies, MSBuild's in-process state, and one FSEvents registration. Nothing else is
shared yet. There is no CAS, no scheduler and no FCS sharing.

## 1. Hypothesis: what phase 1 alone should save

The cost model is `legacy(N) = N·(F + V)` and `host(N) ≈ F + N·(V + s)`, where:

- `F` is the fixed per-process cost that a second session does not pay again: runtime
  native, JIT code and stubs, loader heaps, dirty image pages, and GC bookkeeping.
  ADR-006 measured a suspended daemon with no FCS state at about 390 MB post-GC. Only
  part of that is `F`. Plugin state, TestPrune's DB handles and analyzer
  AssemblyLoadContexts are loaded per session, so they belong to `s`.
- `V` is per-session FCS and plugin state. ADR-003/004 put it at about 2.8–3.1 GB
  settled on a ~775-file solution. It is unchanged in phase 1 because every session has
  its own checker.

**Prediction to measure:**

- **Memory.** Each additional session saves 150–250 MB of `physFootprint`, mostly in
  the harness's `runtimeNative` and `image` parts. At N=4, settled `host(4)/legacy(4)`
  should land in [0.93, 0.97]. Phase 1 does not approach the epic's 1.75× four-session
  budget and is not expected to. That budget needs the later phases, which share FCS
  work and artifacts.
- **CPU and latency.** Sessions 2..N skip runtime start-up, the JIT of
  FCS/MSBuild/StreamJsonRpc/plugins, and MSBuild assembly loading. Predictions:
  - the `Startup` phase (`DaemonPhases.Phase.Startup`) for attach-to-serving is under
    half of a legacy cold start;
  - cold-scan CPU-seconds fall by roughly (N−1)·(5–15) s.
- **FSEvents.** On macOS the fseventsd client count falls from N·(2+k) (ADR-009: one
  native stream, one sln FSW, and k FileCommand FSWs per daemon) to 1 native stream plus
  N `.fshw.json` watchers.
- **Risks the same measurement exposes:**
  - A single GC heap (ConserveMemory=9) and a single thread pool serve N scans, so
    concurrent-settle p95 can regress. Settle latency is measured with siblings busy.
  - One process-level crash (unhandled exception on a raw thread, OOM) now takes N
    sessions down instead of one.

## 2. Global-state audit

Every process-global I found. For each: how it becomes session-scoped, or why sharing it
is safe.

| # | Global | Where | Disposition |
|---|---|---|---|
| G1 | `logLevel`, `verbose` mutables; `log` writes to process stderr, and stderr *is* `.fshw/logs/daemon.log` under the nohup launch | `Logging.fs:11,16,37`; `Program.fs:2385-2390` | **Scope.** Add `AsyncLocal<LogSink>` (writer + level + session tag). `log` writes to the current sink, falling back to stderr (the host log). Sessions install their sink at construction, in the same way as G2. Code that runs without a flowed context (the FSEvents run-loop thread) logs to the host log. |
| G2 | `ProcessRegistry.currentRegistry` (`AsyncLocal`), installed by `createWithCore` | `ProcessRegistry.fs:264,269`; `Daemon.fs:3336-3337` | **Already scoped by ExecutionContext.** The host builds each session inside `ExecutionContext.SuppressFlow()` + `Task.Run`, so `install` lands in a fresh context and never leaks into the host's or a sibling's. `SupervisedWork.fs:251,278` and `PluginFramework.fs:570,576` already run work under the owner's captured context. The shared watcher delivers under the session's captured context (see §4). Test T6. |
| G3 | Process CWD; `AbsolutePath.lastKnownWorkingDirectory` | `Events.fs:26,30`; `RepositoryIdentity.fs:157` | **Pin and audit.** A legacy daemon runs with cwd = repoRoot (`DetachedLaunch.fs:172`). The host sets its cwd to its control directory, so any hidden cwd dependence fails deterministically. Audit relative-path resolution in config-derived inputs (analyzer paths, fileCommands, test projects). The parity test runs the host with a foreign cwd. |
| G4 | `KeyFingerprints.fingerprints` | `TaskCache.fs:119` | **Safe to share.** Diagnostics only and content-keyed (`TaskCache.fs:103-108`). An overflow clear only degrades miss-reason text. |
| G5 | `typedTreeWithheld` latch | `AnalyzersPlugin.fs:336` | **Scope.** The latch is a property of the loaded analyzer assemblies, and each session loads its own set. The SDK's `PluginLoader` gives each path its own ALC. Shared, it would let A's incompatible analyzer silently weaken B's typed-tree rules. Move it to a per-handler field. |
| G6 | FCS checker | `Daemon.fs:3794` | **Per session, unchanged.** |
| G7 | `ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients` on full rediscovery: it clears FCS's *static* IL-reader caches and forces a full blocking GC | `Daemon.fs:3460-3462` | **Hosted: `InvalidateAll` only.** A's rediscovery must not stall B or cold-start its caches (epic invariant). The static IL-reader cache is keyed by path and timestamp, so sharing it is correct. |
| G8 | Ionide.ProjInfo `Init.init`: process env (`MSBUILD_EXE_PATH`, `MSBuildExtensionsPath`, `MSBuildSDKsPath`) and assembly resolver | `Daemon.fs:3430`; `ProcessHelper.fs:657-672` | **Host-level, once.** Each session resolves its SDK directory from its own `global.json`. A mismatch refuses the attach (`ToolchainMismatch`), and that worktree keeps the legacy daemon. MSBuild can load only one SDK per process. |
| G9 | Process environment, inherited from whichever CLI launched the host. Children inherit it; in-process MSBuild evaluation reads it. In intelligence, `PATH` carries each workspace's own `scripts/toolchain-bin`. | `ProcessHelper.fs:808` spawn site | **Scope for spawns, guard for evaluation.** The attach carries the client's environment. ProcessHelper spawns start from an `AsyncLocal` session environment (same mechanism as G2). If MSBuild-relevant variables (`DOTNET_*`, `MSBuild*`, `NUGET_*`) differ from the host's after rebasing the worktree root, the attach is refused (`EnvironmentMismatch`) and that worktree falls back to legacy. **This needs attach protocol v2** (see §6, decision D1). |
| G10 | `ScanMetrics.readResources`: process RSS/managed and an opt-in forced GC | `ScanMetrics.fs:88-101` | **Relabel.** In a host these are host totals and are recorded with `scope: host`, never attributed to a session. The forced GC (`FSHW_SCAN_METRICS_GC`) is refused in hosted mode. |
| G11 | `IdleExit.readGcPressure` | `IdleExit.fs:63` | **Host-level input.** Hosted idle-exit **detaches the session** instead of exiting the process. The host exits after its last session detaches plus a grace period. |
| G12 | GC mode, thread pool, JIT | `FsHotWatch.Cli.fsproj` (`ConserveMemory=9`) | **Shared by design.** Noisy-neighbour risk is measured here and fixed by the scheduler phase. |
| G13 | `Console.CancelKeyPress`, `PosixSignalRegistration` | `Program.fs:2430`, `1784-1796` | **Host verb only.** Sessions never register handlers. |
| G14 | Per-worktree start files: `daemon.pid` (the host pid there would let legacy stale-pid logic at `Program.fs:1102,1290-1326` **kill the host**), `daemon.identity`, `config.hash` | `Program.fs:2404,2410,2415` | **Not written by sessions.** The host writes `.fshw/host-session.json` (host pid, endpoint, SessionId). Binary identity is the handshake's `BinaryMismatch`, not a restart. |
| G15 | Unhandled exceptions on non-supervised threads crash the process | 1 raw `Async.Start`/`Thread` in src | **Blast radius N, documented.** Audit it. Supervised faults (`runDaemonStep`, wedge, `RunWithIpc` failure) are session-local, per test T4. |
| G16 | Instance-scoped state: `IgnoreFilterCache`, `ContentDedup.Tracker` (`ContentDedup.fs:31-34` already documents the two-daemons-in-one-process case), `RecoveryTracker`, `CheckPipeline` dictionaries, `PluginHost`, `ProjectGraph`, `InMemoryCheckCache`, `OperationWatchdog`, `DaemonPhases`, `PluginActivity` | various | **Safe.** One instance per `Daemon`. |
| G17 | Shared task-cache dir (`Daemon.fs:3375`) | `FileTaskCache` | **Safe.** Already shared across processes on disk. Two in-process instances are the same case. |
| G18 | Env-var knobs read at call time (`PluginWedge.ambientBound`, `Ipc.ambientRpcDeadline`, `FsHwPaths`) | | **Safe.** Read-only host configuration. |
| G19 | SQLite pools (TestPrune.Core) | | **Safe.** Keyed by DB path, and the DB is per worktree. |

The audit found **no abandon-grade global**. G8 and G9 are the only ones that cannot be
scoped in process, and both are handled by refusing the attach, which leaves that
worktree on the legacy daemon.

## 3. File-by-file changes

### Core (`src/FsHotWatch`)

- **`Logging.fs`**: `LogSink` + `AsyncLocal` + `installSink : LogSink -> IDisposable`
  (G1).
- **`WatchRouter.fs`** (new, pure, after `RepositoryIdentity.fs`). A routing table from
  `CanonicalPath` root to session key.
  - `route path` returns the session with the **longest segment-boundary prefix**
    (`/r/.workspaces/a` never matches `/r/.workspaces/ab`), or `None`.
  - A path under a *known but unattached* nested worktree root routes to `None`, not to
    the enclosing session. The legacy primary daemon's root-recursive FileCommand FSWs
    currently see siblings' files; the host deliberately does not.
  - `routeMustScan dir` returns the owner of `dir` plus every attached session nested
    beneath it, each bounded to its own subtree.
- **`SharedWatchPool.fs`** (new, after `Watcher.fs`). A reference-counted native stream
  per **anchor**.
  - The anchor is the repository's primary root (parent of `.jj` / the git common dir)
    when the session root is under it; otherwise the session's own root.
  - `FSEventStreamSetExclusionPaths` is bound for `.jj` and `.git`; this is ADR-009
    option D at repository scope.
  - `SharedWatchPool.watcherFactoryFor session` returns a function of today's
    `Daemon.WatcherFactory` shape (`Daemon.fs:3251`). It wraps a per-session filter
    (today's `isRelevantFileOrExtra` + discovery roots + top-level sln + extra patterns +
    per-session `ContentLedger`) and delivers via `ExecutionContext.Run(sessionCtx, …)`.
  - The latency is the minimum over the subscribers present when the stream is created.
  - Off macOS, the pool is a pass-through to `FileWatcher.create`.
- **`Daemon.fs`**:
  - Split `RunWithIpc(pipeName, cts)` (`:2379`) into `RunWith(serve, cts)` plus today's
    `RunWithIpc = RunWith(IpcServer.start pipeName)`. `serve : DaemonRpcConfig ->
    CancellationTokenSource -> Async<unit>` replaces the only line that binds a pipe
    (`:2485`). The idle-exit, heartbeat and wedge lifecycle stays as it is.
  - `DaemonOptions.Hosting : Standalone | Hosted of HostedServices`. `Hosted` supplies
    the watcher factory, suppresses G7 and G10, and routes idle-exit to detach (G11).
- **`RepositoryIpc.fs`** (new, after `Ipc.fs`). The connection preamble: one bounded
  JSON line before JSON-RPC begins, answered with one line. Its kinds are:
  - `attach` → the existing `AttachHandshake` request/response;
  - `session {sessionId, invocationId}` → an ack, then JSON-RPC against **that session's**
    `DaemonRpcTarget`;
  - `repository {invocationId}` → JSON-RPC against the host target.

  `IpcClient` opens one connection per RPC (`Ipc.fs:812`), so every RPC carries
  `SessionId` + `InvocationId` without touching a single `DaemonRpcTarget` method.
- **`SessionRegistry.fs`** (new, after `Daemon.fs`). `WorktreeSession` holds: `SessionId`,
  `ResolvedWorktree`, the `Daemon`, the worktree lock stream, the sink, the environment,
  its cts, and the run task. The registry offers attach, detach and lookup. A session
  whose run task ends (fault, wedge restart, idle, config reload) is removed. Its
  incarnation is void, and a later `Resume` of it gets `UnknownSession`.
- **`RepositoryHost.fs`** (new).
  - Owns the control lock, pid and identity files (`repositoryControlPaths`).
  - Serves the endpoint.
  - Handles attach: decide, take the worktree's `.fshw/daemon.lock`, build the session
    through the injected factory, write `host-session.json`, reply. Every step rolls back
    on failure.
  - Offers `ListSessions` and `StopHost`, and exits when idle with no sessions.

### CLI (`src/FsHotWatch.Cli`)

- **`DaemonConfig.fs`**: parse the `repositoryHost: bool` key (default false).
- **`RepositoryHostMode.fs`** (new).
  - Opt-in resolution: the config key, overridden by `FSHW_REPOSITORY_HOST=0|1` for the
    harness.
  - `ensureAttached`: launch `fshw host` detached if the endpoint is down, then attach.
  - An `IpcOps` implementation that ignores `pipeName` and sends the session preamble.
    All ~15 existing `ipc.X pipeName` call sites are unchanged.
  - The session factory reuses `createDaemon` (`Program.fs:3039`), `registerPlugins`
    (`DaemonConfig.fs:1686`) and `watchRepoConfigFile`.
- **`Program.fs`**:
  - The hidden `host` verb.
  - `status --repository` and `stop --repository`.
  - `stop` with the host enabled detaches this worktree only.
  - On the legacy `Start` lock refusal (`:2372-2381`), read `host-session.json` and name
    the host and session instead of "already running".

## 4. The opt-in switch and the legacy-lock handoff

- **Default stays legacy.** The host path runs only when `repositoryHost` is true (or the
  env var is set). `.fshw.json` is per worktree, so each worktree opts in on its own.
- **One owner per worktree: whoever holds `.fshw/daemon.lock`.**
  - The host acquires it inside the attach transaction (`FileShare.None`, the same
    primitive as `Program.fs:2369`) and holds it for the session's life.
  - If a legacy daemon already holds it, the host refuses (`WorktreeOwnedByDaemon`), and
    the CLI uses the running legacy daemon with a one-line notice.
  - If the host holds it, a legacy `start` refuses and names the host session. A
    non-opted-in CLI then fails loudly (exit 2) rather than timing out on a pipe that
    will never appear.
- **Fallbacks.** If the host cannot be launched, or refuses for toolchain or
  environment reasons, the CLI prints why and uses the legacy daemon. A binary or
  protocol mismatch is **never** a fallback or a restart: it exits 2 with the
  handshake's text.

## 5. Isolation tests (written to fail first)

In `tests/FsHotWatch.Tests`, new classes. Two worktrees come from the d678 identity
fixtures: a jj primary plus a nested `.workspaces/b`, and one git worktree.

- **T1 `RepositoryHostIsolationTests`, edit.** An edit in A advances A's scan
  generation. B's `SessionId`, `GetStatus`, diagnostics and `verdict.json` bytes are
  unchanged.
- **T2, reload.** A `.fshw.json` edit in A gives A a new incarnation, and A's old id
  gets `StaleIncarnation`. B keeps the same `Daemon` instance and `SessionId`.
- **T3, cancel.** A client dropped mid-`WaitForComplete` on A, and a `stop` (detach) of
  A: B keeps serving, and B's in-flight wait completes.
- **T4, crash.** An injected fault in A's run task (and a wedge-triggered restart)
  removes only A. B answers throughout; A can reattach fresh.
- **T5 `AttachRefusalTests`.**
  - Binary, protocol or repository mismatch is refused with its `kind`.
  - The host pid, B's `SessionId` and B's `Daemon` are unchanged.
  - At the CLI level: exit 2 with the message, and no `Shutdown`/`StopHost` is ever sent.
- **T6 `SessionScopeTests`.**
  - A child tracked in A survives B's `KillAll`.
  - A's log lines land only in A's log.
  - After two sessions are built, the host thread has no registry or sink installed.
- **T7 `WatchRouterTests` (pure, property-based).**
  - Every path routes to at most one session, and it is the longest segment-boundary
    prefix.
  - Sibling-prefix names are handled.
  - An unattached nested root routes to `None`.
  - Must-scan fan-out stays bounded.
- **T8 `SharedWatchPoolTests` (macOS).**
  - Two sessions under one anchor create exactly one native stream (factory seam).
  - A write in B's `src` fires only B's `onChange`.
  - Detaching the last subscriber disposes the stream.
- **T9 `LegacyHandoffTests`.** Both directions of the lock refusal (§4).
- **T10.** `status --repository` lists both sessions.

## 6. Decisions I need from you

- **D1: bump the attach protocol to v2 to carry the client environment.** Without it,
  G9 means session B's test runs would use A's `PATH`, including A's
  `scripts/toolchain-bin` in intelligence. The alternative is refusing any attach whose
  environment differs from the host's; that is simpler, but it would refuse nearly
  every intelligence workspace.
- **D2: new host-side refusal kinds.** `WorktreeOwnedByDaemon`, `ToolchainMismatch`,
  `EnvironmentMismatch` and `SessionStartFailed` go in `AttachRefusal` (the wire `kind`
  is a free string, so old clients still print them).
- **D3: the watch anchor.** Option (a) is the ticket's design: a root-recursive stream
  with `.jj`/`.git` kernel-excluded. In *this* repository it would also carry bin/obj
  events from ~60 unattached `.workspaces/*`. Option (b) is one stream whose path list is
  the union of the attached sessions' `src`/`tests`, recreated with `sinceWhen =
  lastEventId` on attach and detach. I will build (a) and measure callbacks/sec and host
  CPU during a sibling build. If that exceeds the per-session streams' cost, I switch to
  (b) within phase 1.

## 7. Measurement plan (the 677 harness)

The harness lives in `.workspaces/d677-baseline`. It samples per pid, via footprint, the
EventPipe heap walk and the `split`. I am coordinating with d677-baseline to add
`run --mode host`:

- set `FSHW_REPOSITORY_HOST=1` in each bench worktree;
- start sessions through `fshw start`, which attaches;
- sample the **one** host pid per phase;
- keep the validity checks per session. That works because `scan-metrics.jsonl` and
  `daemon.log` stay per worktree (G1, G10). The per-worktree log announces
  `host pid + SessionId`, so "the log names the measured pid" still holds.

The matrix is `--sessions 1,2,4 --reps 5` in `legacy` and `host` modes on a quiet box.

Reported numbers:

- `physFootprint` and its managed/native `split` per phase: `host(N)` against
  `Σ legacy(N)`;
- per-session settle latency p50 and p95, concurrent;
- the Startup-phase duration of sessions 2..N;
- fseventsd client count;
- host CPU and callback rate during a sibling build (for D3).

The results go into this ticket, not into ADR-023.

### Harness contract (agreed with d677-baseline, who owns the harness change)

- **Session log.** Each hosted session's `.fshw/logs/daemon.log` has to support the harness's per-session checks:
  - On attach it still writes the top-level `[config] … verdictInputs:` line. That line is where the harness window starts.
  - It then writes exactly one line of the form `Attached to repository host pid=<pid> session=<SessionId>`.
  - `[scan] N projects, M files registered` and `[scan] Checked N files (T tiers), skipped S, unchecked U` keep their current wording.
- **Settle latency.** Each session logs `[check] settled epoch=E after=Nms files=K` when a change cohort finishes checking. `after` is the wall-clock time since the watcher saw the change. The harness adds a one-file edit phase that drives this line.
- **Launch.** The host is launched by the CLI's detached launcher (`DetachedLaunch`, running `fshw host`). The launcher passes the triggering CLI's environment through unchanged, so `DOTNET_DiagnosticPorts` and `FSHW_STATE_HOME` set on the first `fshw start` reach the host. The host pid is written to `<stateHome>/repositories/<repoId>/host.pid`.
- **Scan metrics.** Hosted `scan-metrics.jsonl` records keep every existing field. The only addition is an optional `scope: "host"` field.
- **Status.** `status --repository` reports the native stream count, the FileSystemWatcher count, and each session's id and root.

## 8. Abandon criteria (stop and report)

- **Parity.** MSBuild evaluation or FCS results differ between a hosted session and a
  legacy daemon on the same tree (harness file-count parity or diagnostics parity), from
  shared in-process MSBuild or FCS state that cannot be separated without per-session
  processes or ALCs. That is phase-3-sized.
- **CWD.** The cwd audit (G3) finds more than ~10 call sites across plugins that need an
  explicit root threaded through them.
- **Context flow.** Spawns or logs reach the wrong session through paths that do not
  flow the ExecutionContext (for example `UnsafeQueueUserWorkItem` or native callbacks)
  in more than a handful of places.
- **Measurement.** Either of these refutes phase 1's *efficiency* hypothesis:
  - `host(1)` settle p95 is more than 10% worse than legacy;
  - at N=4 the host saves less than (N−1)·50 MB.

  I report that rather than tune. Its structural value for later phases is your call.

## 9. Approval (2026-09-23) and the requirements it added

D1, D2 and D3 are approved as proposed. The approval added these requirements.

- **MSBuild-relevant variables.** One typed list, with a test, says which environment variables count as MSBuild-relevant.
- **Spawned processes use the session's environment.** A test shows that session B's child process sees B's `PATH`, never A's.
- **Refusal messages.** Every refusal says what differed and what the user can do about it.
- **Watcher measurement (D3).** Measure native callbacks/sec and host CPU while a sibling workspace that is not attached builds. Record the numbers here whatever they show.
- **Blast radius.** A host crash or OOM ends every attached session. The opt-in docs must say so.
  - A dead host's `host-session.json` must be detectably stale: its pid is dead, or its lock is not held.
  - The next legacy `start` or host attach reclaims the worktree cleanly.
  - A test kills the host and then re-attaches.
- **Early abandon signal.** Measure settle p95 for host(1) against legacy(1) as soon as a session can run.
- **Chunk order.** Each chunk leaves a landable change:
  1. router and pool (T7, T8), then report;
  2. session scoping (T6);
  3. IPC and attach (T5, T9);
  4. isolation (T1–T4), then report;
  5. status (T10).

**Launch race.** Two first attaches can start two `fshw host` processes at once. The singleton control lock picks one. The losing host process exits 0 and logs "repository host already running (pid N)". The losing CLI does not fail: it keeps waiting for the endpoint to accept, within the usual daemon start-up bound, and then attaches to the winner.

## 10. Measurements

### Early abandon signal: settle latency, host(1) against legacy(1)

Measured 2026-09-23 on a copy of commandtree: 4 projects and 22 files registered, with
only FCS checking enabled (`build`, `format` and `lint` off).

- **Method.** Each mode ran one warm daemon, then took 25 one-line edits to
  `src/CommandTree/Reflection.fs`. Each edit waited for its
  `[check] settled … after=Nms` line, then paused 1 s before the next.
- **Scope.** The first edit is dropped as warm-up. There was one run per mode, on a
  working (not quiet) box.

| mode | n | p50 | p95 | mean |
|---|---|---|---|---|
| legacy daemon | 24 | 2392 ms | 2515 ms | 2371 ms |
| host, 1 session | 24 | 2312 ms | 2536 ms | 2301 ms |

host(1) p95 is +0.8% against legacy(1), well inside the 10% abandon threshold. Settle
here is dominated by the 500 ms source debounce plus FCS work. Hosting adds routing
and context flow but no measurable latency at this size. The 1/2/4-session memory
matrix is the 677 harness's job, once it lands.
