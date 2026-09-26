# Changelog — FsHotWatch (core)

## Unreleased

- fix: a timed call (`runWithCancellableTimeout`, `runWithCancellableTimeoutTracked`,
  `runWithTimeout`) no longer starts a new thread. Its work runs on a reused
  `fshw-deadline-worker` thread; one is added only when none is idle, so work abandoned
  at its deadline still never holds up the next call. Lint and the analyzers used to
  create and retire one thread per file checked. Timeout, cancellation and outcome
  reporting are unchanged.

- fix: a restore by a second tool no longer reads as a project change. The content
  tracker compares `obj/project.assets.json` with its `project.restore` block removed
  (raw bytes when it does not parse), so a rewrite that changes only restore metadata —
  a JavaScript compiler's project cracker records its own `restoreLockProperties` — no
  longer re-runs MSBuild evaluation and re-checks the project and its dependents. The
  change batch logs the project inputs whose content changed by their own paths.

- fix: a scan request admitted before a running scan began reading the tree is answered
  by that scan instead of running a second full scan. `check` against a daemon still in
  its cold scan used to queue one extra full scan per waiting client. A request admitted
  later is still a real scan; a scan that left files unchecked, or whose project files
  changed after its model was captured, answers nothing.

- fix: a check of a file joins a running check with the same project, checker generation
  and snapshot key instead of cancelling it and asking FCS again; a running check of a
  different snapshot is still superseded by a newer call, and an older call gives way.
  Cancel and join lines name both callers (`scan`, `change batch`) and whether the
  snapshot key changed; `Checking N files after change` is logged at info with the
  changed files that caused it.

- feat: the heartbeat adds the 1-minute load average, the GC heap size and the
  gen0/gen1/gen2 collections since the previous heartbeat. `OperationWatchdog.Watchdog`
  takes an optional `resources` reader; `ResourceReading`, `readResources`,
  `loadAverage` and `resourceSuffix` are new.

## 0.10.0-alpha.47 - 2026-09-25

- feat: `PluginWork.resultFirst work` lets a run's result fold ahead of the dispatched
  events queued before it. Once the result is in the mailbox, the plugin's own messages
  (`Custom`) fold in their own order until the result has, then dispatched events resume
  in theirs. So the result folds once the fold in flight commits, not after the whole
  backlog. Undeclared runs keep the first-in, first-out mailbox. docs/writing-plugins.md,
  "Result ordering", gives the contract and when a plugin may declare it.
  `PluginWork.isResultFirst` reads the declaration.

- **BREAKING:** `PluginCtx.IsRunning: string -> bool` is replaced by
  `PluginCtx.SlotHolder: string -> SlotHolder` (`Free | LiveRun | Fold`). `IsRunning`
  read false while a finished run's result fold still held the key, yet a claim on that
  key returned `SlotBusy`; `SlotHolder.Fold` names that case. `ctx.IsRunning key` is
  `ctx.SlotHolder key = SlotHolder.LiveRun`. `CommandCtx` and `CommandReadCtx` keep
  `IsRunning`.

- feat: the watchdog heartbeat carries the GC pause share since the previous heartbeat
  (`GC.GetTotalPauseDuration` deltas), e.g. `heartbeat: idle; gc-pause 1.00% (300ms of
  30s)`, so a daemon log shows what a runtime GC setting costs. `OperationWatchdog.Watchdog`
  takes an optional `gcPauseTotal` source; `gcPauseSuffix` renders it.

- fix: the daemon hands Ionide.ProjInfo's `Init.init` the `dotnet` muxer at its real
  location, so the `DOTNET_HOST_PATH` it writes into the daemon's own environment names the
  directory that holds `sdk/`. Through a symlinked PATH entry (a Nix wrapper, mise, Homebrew)
  it named the symlink's `bin/`, and in-process FCS, which finds the SDK beside
  `DOTNET_HOST_PATH`, resolved a script with no framework references and aborted its check.
  `ProcessHelper.installedDotnet` is the resolution, shared with the spawn path.

- fix: `DOTNET_INTERNAL_ThreadSuspendInjection` is in `SessionScope.processOwned`, so a
  repository host the CLI launched with thread-suspend injection off still accepts a shell
  that does not set it, instead of refusing the attach as an MSBuild environment mismatch.

- perf: `CheckPipeline` observes a canceled check through `IsCancellationRequested`
  instead of throwing `OperationCanceledException`, so a superseded check no longer
  unwinds an exception (which, on macOS, walks the unwinder under dyld's loader lock and
  prolongs GC thread suspension). Results, logging and caching are unchanged: a check
  canceled before FCS logs `Cancelled: <file>` at debug level, one canceled after FCS
  logs the same `Failed to check <path>: The operation was canceled.` error as before,
  and neither returns nor caches a result.

- fix: a check whose answer declared a type incompatible with ITSELF is re-checked
  whenever the project's checker state has been dropped since the check began, without
  spending the drop budget. The budget allowed one drop per project per five minutes,
  so under load the first faulty file to finish spent it and every other check of that
  project already in flight kept its answer from the dropped state. The budget now
  bounds only how often state is dropped, and the spend, the drop and the generation
  read are one atomic decision per project.
- fix: two more FS0001 shapes are recognised as a type incompatible with itself:
  `The type 'T' does not match the type 'T'`, and the pattern-match and `if` branch
  messages (`… which here is 'T'. This branch returns a value of type 'T'.`). These
  templates drop the constraint text, so a render naming a type variable is refused.
- fix: every other error of a check that reported a type incompatible with itself is
  reported under `fcs-internal` at its own severity instead of under `fcs`. Such a
  check has shown its answer for the file is not a reading of the code, and its
  knock-on errors (an inferred type that no longer unifies, a match that no longer
  looks complete) cannot be recognised by message. They are never demoted to `Info`,
  which could let a genuinely broken file go green.
- feat: `PluginActivity.AnalyzersPluginName`, the analyzers plugin's ledger key.
- fix: the `fcs-internal` ledger text no longer claims every surfaced fault survived a
  re-check.
- obs: `check start` and `checked` lines name the project's checker generation and a
  snapshot key; a denied re-check logs who spent the budget and when; a cancellation
  of an in-flight check logs how many other in-flight checks share its project
  type-check.

## 0.10.0-alpha.46 - 2026-09-25

- feat: `HookStep` — a running hook step (label, index and count, command, pid, bound),
  its one rendering (`HookStep.describe`), and a `Tracker` that holds it for as long as it
  runs; `HookStep.asSubtasks` holds it as a plugin subtask, so the wait and wedge lines
  name it.
- feat: `ProcessHelper.runProcessObserved` — `runProcess`, telling a callback the child's
  pid as soon as it is running.

- feat: `CommandPreprocessor` — an `IFsHotWatchPreprocessor` over a configured command,
  for a generator that rewrites files in place before the build and the checks see
  them. Its writes are attributed by content (each declared path hashed before and
  after), so its own echo never re-triggers it, and are reported in the watcher's path
  form. A non-zero exit, a timeout or a command that cannot start is a refusal.
- feat: preprocessors run in registration order, and each is offered the files the
  ones before it rewrote. The files a preprocessor rewrites join the batch the plugins
  receive, whether or not the batch held them. `PluginHost.PreprocessorNames` lists
  them in run order.

- fix: a gate no longer fails because a read of in-memory state timed out. Plugin
  statuses and the error ledger were each owned by an agent that answered reads from
  its mailbox, so with the thread pool saturated — a daemon busy running the very
  tests the check asked for — a read waited 30s for the agent to be scheduled and
  raised. One such read failed `WaitForComplete`, `GetStatus` or the whole scan, and
  the CLI reported "Could not connect to daemon" for a daemon that was alive and
  working. Both are now written under a lock on the writer's own thread and publish an
  immutable snapshot; `PluginHost.GetStatus`/`GetAllStatuses` and every `ErrorLedger`
  read answer from the last snapshot without waiting on any other thread. A write is
  visible to the next read on any thread as soon as it returns, so a plugin that
  reports findings and then goes terminal is never read as terminal and clean.
- fix: an `ErrorLedger` write that throws now stops the ledger at once: every later
  read raises, instead of timing out after 30s. `AgentCrashed` still reports it.
- fix: `ErrorLedger` reporters (the `.fshw/errors` file mirror) no longer run under
  the ledger's lock. Each write queues its notifications in write order and one
  dedicated thread delivers them, so a plugin thread never waits on another plugin's
  disk I/O, and a reporter that writes back into the ledger no longer loses those
  findings. A reporter that fails is still recorded as a `failed to record` error
  entry, and a reporter or log sink that throws (a full disk) no longer stops the
  ledger.
- fix: `fshw` diagnostics read plugin statuses before the error ledger, so a plugin
  that reported findings and finished between the two reads is no longer returned as
  "Completed, 0 errors".

- fix: a caller waiting for a supervised or debounced queue's admission or close,
  or for the daemon's scan admission, gives up within its bound however busy the
  thread pool is. The bound was kept by a timer whose callback needs a pool thread,
  so with the pool saturated a 5s wait ran for as long as the pool stayed busy.

- fix: `Daemon.Run`, `Daemon.RunWith` and a child process scope started on the
  caller's thread (`Async.StartImmediate`) no longer leave their process registry in
  the caller's context once they first wait. The caller's spawns stay the caller's,
  and are not refused after the daemon or scope has shut down.

- Fix: the check cache no longer serves a result typed against a referenced project's
  old build output. The compiler types a file against a referenced F# project's output
  whenever that output is at a real path and at least as new as the project's
  sources. A rebuild that changed the output and not the sources (sources restored
  with their old timestamps, then rebuilt) served the old diagnostics. Each entry now
  records the bytes of every real-path project output its check referenced, and is
  served only while each output still holds them. A mismatch re-checks. Outputs under
  a virtual root are typed from their sources, so they are not recorded, and framed
  worktrees whose builds differ in bytes still share entries.

- Fix: a project reference at a real path is stamped by its output assembly's bytes
  again, in per-worktree daemons and for unframed host projects. A reference to a
  framed project, under the virtual root, keeps its upstream's closure as its stamp:
  nothing exists at that path, so the compiler types the upstream from its sources.
  Since the virtual-root change, a real-path reference had been stamped by its
  upstream's sources. The compiler types against a real-path output whenever it is
  at least as new as those sources, so a rebuild that changed the output and not the
  sources left results typed against the old output.

## 0.10.0-alpha.45 - 2026-09-24

- fix: a repository host's session serves its RPCs in the session's context: its
  process scope, log sink and client environment, as a daemon on its own pipe does.
  The host's endpoint served them in the host's own context, so what a plugin command
  spawned while handling a call was not reaped with its session, and what the call
  logged went to the host's log. `DaemonRpcConfig` gains `Context`.

- fix: a daemon's process registry is the daemon's. Building a daemon in-process no
  longer leaves its registry installed in the caller's context, so a spawn the caller
  makes afterwards is the caller's, and is not refused once the daemon is disposed. A
  plugin's work, inline or exclusive, runs in the context it was registered in, whoever
  dispatches to it, and `Daemon.RegisterHandler` registers in the daemon's scope, so
  what a plugin spawns is still reaped when its daemon stops.

- fix: `Daemon.RunWith` and `RepositoryHost.run` start their IPC server on their own
  thread, so a daemon or host accepts connections before its startup goes on. Queued to
  the thread pool, a loaded box could leave a client probing for it with nothing
  listening.

- A per-file plugin's replayed summary ("N files examined, M replayed from cache") now
  counts the current run, not everything since the daemon started. A run is the set of
  files one scan or change batch checked. A rescan that serves both of a repository's
  files from cache used to read "3 files examined, 3 replayed" because of earlier runs,
  and now reads "0 files examined, 2 replayed". Two sessions of one repository therefore
  report the same run the same way, whichever of them filled the shared cache first.
  The `task-cache` debug line for a hit or miss now names the file.

- Each per-file FCS check now logs `check start <file>` when it begins, and
  `checked <file> in Nms (snapshot Xms, fcs Yms)` when it succeeds. The split shows
  whether a slow check is spent building the project snapshot or in the checker itself.
  A change batch that waits 100 ms or longer for a settled project model says so at
  debug level.

- feat: a repository host checks every worktree under one virtual root, so a project
  whose content is the same in several worktrees is checked once and its results serve
  them all. `ProjectSnapshots.buildFramed` checks each project under the frame its
  session chooses (`PathFrame`, `SessionFrames`); `CanonicalProjects` keeps one content
  per project shared, and a session whose project differs checks it at its own paths.
  A project that reads its own location (`__SOURCE_DIRECTORY__`, `__SOURCE_FILE__`,
  `#line`, a type provider, `--version:@`, `--load`, `--use`, a response file) is never
  shared (`FrameExclusions`), and the host's log says which and why. Diagnostics, and
  analyzer and TestPrune results, are rebased to the worktree's paths.
  `FileCheckResult.Frame` names the frame a result was checked under. A host refuses to
  start while its virtual root exists (`HostRun.VirtualRootExists`).

- feat!: a repository host's sessions of one checker configuration check through one
  checker (`CheckerPartitions`), so they hold one copy of the framework imports and
  `TcGlobals`. `DaemonHosting.hostedBy` takes the partition's checker factory, and
  `HostingSeams` gains `Checker`: a hosted session's full rediscovery drops only its
  own projects, never its siblings'. `Daemon.Checker` exposes the checker a daemon
  checks through.

- feat: a repository host can serve many worktrees from one process, each as its own
  session of today's `Daemon`.
  - `SessionRegistry` builds each session in its own scope: a log sink, the client's
    environment, and its own process registry. A session that ends for any reason is
    removed alone.
  - `RepositoryHost` attaches a worktree as a transaction and refuses loudly. It
    covers the attach handshake (now protocol 2, which carries the client's
    environment), MSBuild-relevant environment, SDK, and the worktree's own lock.
  - `RepositoryIpc` serves one endpoint per repository, with a preamble naming each
    connection's session and invocation.
  - `SharedWatchPool` and `WatchRouter` share one FSEvents stream per repository
    anchor and route each event to exactly one session.
- feat: `Daemon.RunWith` serves through whatever it is handed; `RunWithIpc` is that
  over its own pipe. `DaemonOptions.Hosting` marks a hosted session: it watches
  through the host's stream, never clears process-wide compiler caches, and records
  resources as the host's (`scope` in `scan-metrics.jsonl`).
- feat: every checked change cohort logs `[check] settled epoch=E after=Nms files=K`.
- fix: the analyzers' withheld-typed-tree latch belongs to the plugin instance, not
  the process.
- feat: `ProcessHelper` spawns from the session's environment when one is in scope,
  and looks a bare command up on that environment's `PATH`.
- fix: a host with no plugins registered settles once it owns no work. A
  configuration that turns every plugin off (`{"build": false, "format": false,
  "lint": false}` and nothing else) used to leave `check` waiting out the whole
  verdict deadline, and `--run-once` its 30-minute settle. The `WaitForComplete` log
  line now names the bound it applied, and a plugin-free host that times out names
  the work it still owns.
- fix!: invalidating a project no longer makes the checks already under way report its
  types as incompatible with themselves (`The type 'X' is not compatible with the
  type 'X'`). `ProjectSnapshots.invalidate` removed the project's entries from the
  checker's caches while other checks of the project were still running. Such a check
  then type-checked a removed file again, or took another check's new result for it,
  against results it already held, so one file's types existed twice. A scan that
  re-checked one self-incompatible diagnostic that way produced more of them in the
  project's other files. `invalidate` now moves the project to a new generation
  instead: the snapshots `ProjectSnapshots.build` makes afterwards stamp its
  references differently, so the project and everything downstream of it are
  type-checked again under new cache keys, and nothing a running check depends on is
  removed. Breaking: `ProjectSnapshots.build` takes a new first argument, the
  generation of each project (`ProjectSnapshots.generationOf checker`), and
  `ProjectSnapshots.Generation` is new. The self-incompatible re-check builds its
  snapshot again after invalidating. Under a repository host's virtual root a project's
  generation belongs to the virtual identity its sessions share: the self-incompatible
  re-check moves every session sharing it to a new generation together
  (`ProjectSnapshots.invalidateShared`, with the aliases `recordFrame` records), while a
  session's own rediscovery or project change (`invalidate`) leaves it where it is,
  since the shared key already carries the project's content.
- fix: a full rediscovery no longer makes the checks already under way report types
  as incompatible with themselves. A standalone daemon called `InvalidateAll` and
  `ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients`, which both
  replace the TransparentCompiler's caches under the running checks: the same race
  as removing one project's entries. `Daemon.dropForRediscovery checker projects`
  now moves every project it is handed to a new generation, in both hosting modes,
  and removes nothing. FCS keeps one version of each cache entry strongly (per
  project, and per file of a project), so each entry the new generation computes
  demotes the previous generation's version to a weak reference, and the next
  collection releases it. No full collection is forced on rediscovery.
  `HostingSeams.ClearsProcessCaches` is gone (nothing clears them), and a source guard
  refuses `InvalidateAll`, `ClearCaches`, `InvalidateConfiguration` and
  `ClearLanguageServiceRootCaches*` anywhere in `src/`.
- fix: a project is type-checked with one source list, whether one of its own files
  is being checked or a project downstream of it is. `CheckPipeline.RegisterProject`
  stored each project's options with the files generated under obj/ and bin/ removed,
  while downstream projects reached it through their unfiltered `ReferencedProjects`.
  The checker keys its per-project and per-file caches by project, so the two source
  lists were two versions of every entry. Checking a downstream project demoted the
  project's own warm type-checks, and the project's own check was then type-checked
  again. (Both lists share one set of imports, which FCS keys without the source
  files, so this was CPU, not retained memory.) The
  project's own check also missed code generated into obj/ that its files use, and
  reported it as not defined. `RegisterProject` now keeps the options whole and only
  leaves the generated files unregistered, so they are still never checked on their
  own. The check-result cache's options hash changes with the source list, so each
  project's cached check results are recomputed once after upgrading.
- fix!: a checkout's jj/git layout is read in one place,
  `RepositoryIdentity.resolveWorktree`, which now also returns the checkout's `Kind`.
  The shared cache namespace (`RepoIdentity.namespaceOf`) derives from the same
  canonical common store as the `RepositoryId`, so it no longer disagrees with it:
  two repositories under a directory named `worktrees` no longer share a namespace
  (git's common directory is read from `commondir`, not cut at `/worktrees/`), and a
  git worktree of a colocated jj repository shares its jj workspaces' namespace. A
  layout that cannot be read still falls back to a private namespace.
  `RepoIdentitySource` is now `Store | Unreadable`; `CheckoutKind` moved to
  `RepositoryIdentity` (`CheckoutKind.isSecondary`, `CheckoutKind.describe`);
  `RepoIdentity.checkoutKind` and `canonicalGitDir` are gone.
  **A workspace may miss the shared cache once after upgrading**: namespaces are now
  built from canonical paths (symlinks resolved, e.g. `/private/var` rather than
  `/var` on macOS, and each name spelled as stored on disk) and git's `commondir`,
  and a checkout under no VCS is keyed as a standalone store. Wherever that moves a
  repository's store path, its namespace directory changes and starts cold.
- refactor!: removed `CheckPipeline.FingerprintTime`, `CheckPipeline.FingerprintTablesBuilt`,
  `UpstreamFingerprints.ComputeTime` and `UpstreamFingerprints.TablesBuilt`. Nothing read
  them; the fingerprint memo no longer times or counts its own work.

## 0.10.0-alpha.44 - 2026-09-23

- fix: a per-file result a plugin finishes while an exclusive run owns its status is
  still written to the task cache. The funnel withholds the status publication, as
  before, but a per-file entry carries no summary and replays through the same funnel,
  so the next scan replays the file instead of re-running its work. A cold scan analyses
  its files while the run it started is in flight, so a `check` after a cold `check` or
  `confirm` re-analysed every file. A whole-run entry still captures only a terminal
  that landed.

- fix: a rewrite that leaves a file's bytes unchanged no longer re-typechecks the
  projects downstream of it. The check pipeline builds the checker's project
  snapshots itself (`ProjectSnapshots`) instead of handing it project options, so
  every source file is versioned by its content hash, not its last-write time, and
  every `-r:` reference inside the repository by a stamp derived from its content
  hash. A no-op rebuild of an upstream assembly, a restore or a checkout used to
  discard the type-check work of every project downstream of the file it rewrote. Two
  checkouts with identical content now produce identical file versions and reference
  stamps. The hashes come from the memo the check-result cache's upstream
  fingerprints already keep, so a file is read again only when its stat moves.

- fix: invalidating a project now clears what the checker holds for it. The options
  overload of `InvalidateConfiguration` clears nothing on the TransparentCompiler the
  daemon uses, so both the per-project invalidation on a project change and the
  one-time re-check of a self-incompatible type diagnostic went to FCS's cache
  unchanged. Both now invalidate by the project's snapshot identity.

- feat: `DaemonOptions.CheckerCacheSizeFactor` (default `Daemon.DefaultCheckerCacheSizeFactor`,
  100) sets the checker's `TransparentCompiler.CacheSizes`. The checker is built by
  `Daemon.createCheckerWithCacheSizes`, and `createChecker ()` stays at the default.

- feat: exclusive work carries consumer leases, so a run nobody is waiting for any
  more stops holding the box. A plugin command runs under its requester's token (the
  IPC server's per-connection token), and every intent it enqueues is held by that
  client; the daemon's own wants (the watcher, a plugin's own event, a result fold)
  hold a lease no client can release. Coalescing merges the consumers, and the
  daemon's lease absorbs any client's. When the LAST lease is released: a queued
  intent is withdrawn (its receipt fails; it never folds), and a running run is
  cancelled only if its work was declared `PluginWork.cooperativeSafe`. Its process
  scope is reaped, no result folds and no failure is recorded (a result it returns
  after its processes were killed is dropped too), a shared resource goes back in the
  state the run was handed, and the status its `Running` displaced is reported
  again. Undeclared work still runs to completion. One client leaving never cancels
  work another client or the daemon still needs.

- fix: a client that disconnects mid-call no longer leaves its RPC running for
  nobody. The IPC server builds one RPC target per connection and cancels it when the
  connection drops: the call is released and retired from the operation watchdog at
  once (it no longer shows as in flight), and a `RunCommand` handler's own async runs
  under that token, so work it does inline stops at its next cancellation point. Work
  the call only WAITS on is shared and keeps running: plugin runs (including a run a
  command queued on a plugin's key, e.g. `run-tests`), the daemon-wide terminal and
  scan waits, triggered builds and re-runs, and formatting on the change agent. One
  waiter leaving never cancels what another client or the watcher still depends on.

- fix: a finished run keeps its `Running` status until its result fold commits. The
  status funnel dropped an unrelated terminal only while a worker was live. Once the
  worker finished, its result fold could sit in the mailbox behind a long fold, and a
  per-file `Completed` reported in that window replaced the run's `Running` while the
  plugin still owned the run. The verdict wait then saw owned work with nothing Running
  and declared a false WEDGED the moment a declared bounded fold ended. The funnel now
  drops such a terminal until the run's verdict is folded (`Snapshot.OwesRunVerdict`);
  the result fold's own report still lands.
- feat: `RepositoryIdentity` — distinct, stable identities for a repository host that
  serves many worktrees. `RepositoryId` digests the canonical COMMON metadata store and
  its provider, so every jj workspace (primary or secondary) and every git worktree of
  one repository share it, a `git worktree add` made from a colocated jj repository
  joins that repository (proven by jj's `git_target` pointing back), and two
  independent clones never collide, even with the same origin URL. `WorktreeId` is one
  canonical physical root within the repository: a worktree deleted and recreated at
  the same path keeps it. `SessionIncarnation` is a host-minted nonce, so a recreated
  worktree cannot accept its previous life's completions. Roots are canonicalized
  component by component — every symlink resolved, `..` applied physically, each name
  spelled as stored on disk — so symlinked and case-variant spellings are one worktree.
  Git's common directory is read from the `commondir` file git writes rather than
  guessed from a `/worktrees/` path segment. Unlike the cache namespace
  (`RepoIdentity`), an unreadable, malformed or dangling VCS pointer is an
  `IdentityError`, never a guess. `repositoryControlPaths` puts a repository's lock,
  pid, identity, host log and endpoint name under `FsHwPaths.stateHome ()`
  (`$FSHW_STATE_HOME`, else `$XDG_STATE_HOME/fshw`, else `~/.local/state/fshw`) keyed
  by `RepositoryId` — never inside a worktree, so it survives the deletion of any one.

- feat: `AttachHandshake` — the versioned attach handshake (`fshw.attach`, protocol 1)
  carrying repository id, worktree id and canonical root, session-incarnation
  expectation, configuration digest and binary/protocol identity. The host re-derives
  the worktree's identity from the claimed root and checks every claim; `decide` is
  pure. Mixed incompatible clients FAIL LOUDLY: a protocol or binary mismatch, a wrong
  repository, a claim the host's derivation contradicts, an unknown session or a stale
  incarnation is a typed refusal with an explanation — never a restart, which with one
  host serving many worktrees would tear down every sibling session. A request of
  another protocol version, or a malformed one, is still answered with a refusal. No
  host serves this yet.

- `runProcessAccounted` (internal): `runProcess` plus a `TreeTeardown` for a child that
  overran — its tree read from `ps` before the kill (afterwards a survivor has been
  re-parented and cannot be found from the root), the kill's outcome and duration, and the
  members still alive after it, polled with `kill(pid, 0)` for up to ~2s and stopping as soon
  as the tree is gone (not `ps -p`, whose exit code is 1 for some live pids on macOS).
  Survivors are booked with `ProcessRegistry` as leaks. An unreadable table or an unanswered
  probe is reported as unknown, never as none.
  `runProcessTo` / `runProcess` are unchanged.

- Changed: a per-file cache replay's summary carries a tally. It was the plugin's
  ledger counts plus `(cached)`, which read the same whether every file had been
  examined or every file had been served from cache. It now reads
  `M findings (…); E files examined, R replayed from cache (cached)`, counted per
  registered plugin: `E` is per-file results produced by running the handler, `R`
  is per-file results replayed.

- fix: the check-result cache served stale diagnostics, and could not help the scan it
  sits in front of.
  - **Unsound key.** Entries were keyed on a file's own bytes and its project's
    options, not on the sources it is type-checked against. Change a signature in
    project A and a cached file in C (C → B → A) kept its old, clean result on every
    rescan and change batch — including the from-disk rescan `check`/`confirm` force.
    The key now carries `CheckCache.upstreamFingerprints`: the files before it in
    compile order, every source file of every TRANSITIVELY referenced project, and
    in-repo non-project `-r:` assemblies. Unchanged files are re-read only when their
    (mtime, length) stamp moves (`FileContentHasher`).
  - **Fingerprint cost.** Computed once per project per generation
    (`CheckPipeline.BeginGeneration`, called by each scan and change batch) instead of
    per file: 213 (file, project) pairs cost 4 ms as per-project tables vs 126–305 ms
    per lookup; a warm scan of this repository spends 9–21 ms on it. A result is
    stored only if the key re-derived from disk after the check still matches, so a
    check that ran against an upstream edited mid-generation is never written.
  - **Sized below the working set.** A 500-entry LRU against a sequential scan of a
    larger tree evicts each entry just before it is needed: measured **0 of 208**
    hits at the same 27% ratio. `InMemoryCheckCache` now takes a
    `CacheCapacity` — `Entries n` (a fixed budget) or `WorkingSet` (grows to what it
    admits: **208 of 208** hits, warm-rescan CPU 6.7–15.7 s → 0.3–0.5 s, ~250 KB held
    per entry) — and a project filter; the pipeline skips non-admitted projects
    entirely (`IScopedCheckCache`). A fixed bound below the working set is warned
    about once the generation starts, naming the final count.
  - **Unbounded by edits.** A new result for a (file, project) replaces the old one.
- feat: `RepoIdentity.checkoutKind` reads whether a directory is the jj default
  workspace (`.jj/repo` is a directory), a secondary jj workspace (a file), a git main
  checkout, a git worktree, or neither.
- The daemon logs the cache's state at startup (`describeCheckCache`), including
  `OFF`, so an inert cache no longer reads as a working one.
- fix: every agent round-trip in the daemon is bounded. `PostAndReply` with no
  timeout waits FOREVER, and all nine call sites in `src/` — seven in the error
  ledger, two in the plugin host's status agent — passed no timeout. A mailbox that
  stops draining then hangs every caller permanently rather than the agent, and these
  readers are on the gate's path. On expiry the call RAISES, which is the behaviour
  this wants: an agent that cannot answer must not be read as an agent with nothing
  to say, because returning an empty result would turn a wedged ledger into a green
  verdict.

- feat(analyzers): `FSHW-WAIT-002` fails the build on a `PostAndReply` family call
  with no timeout, in production sources, with a `FSHW-WAIT-002 ok: <reason>` opt-out
  so the exceptions stay countable. Matched in both AST spellings: a compound
  receiver folds to `DotGet` and a bare identifier receiver to `LongIdent`, and every
  real call site is the second — a rule written against `DotGet` alone would have
  matched nothing and looked like a clean tree.

- perf: a scan dispatches each file once, across tiers as well as within one. The
  scan resolves each tier's files to check thunks in a PER-TIER dictionary, which
  collapses a file two projects in the same tier both compile but cannot see one
  reached through projects in different tiers — that file got a thunk in each and was
  checked, and emitted, twice. The scan's own summary had been reporting it:
  `checked 211 of 206 registered file(s), unchecked 0`, and `checked 2083 of 2057` on
  a larger tree. The first tier to reach a file now wins it, which is the more
  upstream project, so which options type-check a shared file is deterministic rather
  than "whichever tier ran last".

- obs: a scoped invalidation names the projects it invalidated instead of only
  counting them. `Scoped project change — 2 changed + 14 dependent` said that
  something rewrote two project inputs mid-scan and refused to say which two, while
  holding the paths — they were emitted one line earlier at DEBUG, the level nobody
  runs a long gate at, so recovering them meant re-running the whole gate. Named
  repo-relative, because two projects in different directories can share a `.fsproj`
  name; elided past eight, and the line says that it elided.

- CORRECTION to the `0.10.0-alpha.43` entry below. "a build no longer re-discovers
  the tree it was triggered by" is too strong. Measured since on a second machine:
  of five `project.assets.json` files rewritten during a run, three were byte-identical
  and correctly suppressed by the seeding, and two had genuinely changed and correctly
  invalidated. So the accurate claim is that a restore's byte-identical rewrites no
  longer report every project as changed — which is real, and is not the same as the
  tree no longer being re-discovered. A single scoped invalidation regenerates the
  project model and supersedes an in-flight scan as thoroughly as twenty-three do, so
  narrowing the set does not on its own recover the duplicated pass. What rewrites
  those two projects' inputs mid-run is not yet identified; a gate's own `beforeRun`
  steps have been eliminated as the writer.

## 0.10.0-alpha.43 - 2026-09-22

- perf: a build no longer re-discovers the tree it was triggered by. A cold scan
  emits a change for every registered file, which starts a build, then waits for
  that build to leave Running before the FCS tiers read the `obj/` refs it
  rewrites. The wait is correct and it was not enough: `dotnet restore` rewrites
  every project's `obj/project.assets.json`, the watcher admits those at the
  project tier, and a cold daemon held no prior content for any of them — so each
  was reported changed on the content tracker's no-prior default. The scoped
  invalidation that followed landed WHILE the scan was still blocked on the build
  that provoked it, discarding the model the tiers were about to run against and
  superseding every in-flight check. Measured over a cold scan of 1,868 files,
  80.5% sat at exactly one cancellation and one check and 82.8% paired exactly
  n-for-n; an independent run on another machine over 1,573 files put those at
  84.4% and 86.8%. `observeProjectContent` now seeds each project's assets file
  alongside its `.fsproj` — without the exclude filter, since assets live under
  `obj/` by construction and `Watcher.isProjectAssetsJson` bypasses that same
  filter for the same reason.

- perf: stop retaining background resolutions nothing reads. `FSharpChecker` was
  created with `keepAllBackgroundResolutions = true` while no code in `src/` reads
  symbol uses or semantic classification off a check result, so the retained
  resolutions were pure footprint. A test now scans `src/` for the APIs that would
  consume them, so turning the flag back off cannot silently break a future reader.

## 0.10.0-alpha.42 - 2026-09-22

- core: the self-incompatible guard now sees FCS's GROUP SEPARATOR


## 0.10.0-alpha.41 - 2026-09-22

- core: recovery attempts are serialised per project, so an in-flight restore is
  no longer mistaken for a failed one. `MarkAttempted` is written before the
  restore runs and cleared only once it succeeds, so for the whole duration of a
  restore the mark read "already attempted" — and the scan and the change-batch
  supervisor evaluate the gate CONCURRENTLY on one shared tracker. The loser of
  that race dropped every file of the project from its scan and published the
  generation anyway. Measured in a consuming repository's daemon log: of 38
  `deps still stale` events, 36 had an `auto-restored OK` for the SAME project
  0.0-13.5s later (median 1.1s) and none had one before — the projects were
  restoring, not unrestorable. A concurrent evaluator now waits for the holder and
  re-reads the disk instead of trusting the mark; the uncontended path is
  unchanged.


- core: a project the deps-freshness gate skips now reports its own diagnostic.
  `SkipAlreadyAttempted` drops EVERY file of a project from the scan, and relied on
  the `FailFast` diagnostic from the first recovery attempt still being in the
  ledger — which nothing guarantees, since a re-discovery or any `ClearErrors`
  removes it. Whole projects could therefore leave a scan with nothing said about
  them while the scan published its generation as authoritative. Skipping stays
  (a project whose deps will not restore must not be type-checked against an empty
  reference set); skipping silently does not.


- core: the per-scan metrics record now says where a scan's files went.
  `scan-metrics.jsonl` carried checked, unchecked and registered counts, so a scan
  that covered a third of the tree and completed successfully was indistinguishable
  from one that covered all of it — the missing files left through the
  deps-freshness gate, which drops a whole project's files without touching the
  unchecked count. Records gain `filesSkipped` and, separately, `filesDepsGated`:
  the gated count is broken out because it is the reason that hides a partial scan
  behind a successful one. Records written before these fields still parse, with
  the new counts reading as 0 — the value of this ledger is its accumulated series,
  and a field addition that discarded it would look like an empty history rather
  than an error.


- core: the daemon's checker construction moved to a named `Daemon.createChecker`,
  so the `keepAssemblyContents` capability the analyzers stage depends on is
  pinned by a test rather than left implicit at the construction site. No
  behaviour change: the flags are unchanged.

## 0.10.0-alpha.40 - 2026-09-20

- core: root the rerun synthetic path so it cannot resolve against a deleted cwd
- Drop private-tracker references from comments and docs


## 0.10.0-alpha.39 - 2026-09-18

- core, cli: a superseded scan re-captures its model instead of ending the check


## 0.10.0-alpha.38 - 2026-09-18

- (breaking) `WaitForComplete` waits for EVIDENCE, not for a reported status. The
  `requireVerdict` guard, the 200 ms quiescence window and the `activeVerdictWaits` counter
  are all gone. The wait rests when the host owns no work and something has earned evidence
  for the model it would answer about — a test receipt, an analysis receipt, or a completed
  build failure. A host that observes no model resolves instead of blocking: nothing can
  earn evidence for a model that does not exist, and a green over one is already refused by
  the verdict. `Events.CompletedBuildFailure` and `ICompletedBuildFailureState` are new;
  `PluginWorkOwner.HostSnapshot` gains `CompletedFailures`.

- (breaking) An in-flight verdict wait is a client OBSERVATION, published with the work it
  waits on. `Store.Observe()` takes a lease and `HostSnapshot.ObserverCount` reports it;
  idle-exit and the heartbeat read that instead of a counter kept beside the publication.
  A watcher is not work, so the lease never makes the host busy, and it is released on
  every exit — verdict, timeout, or shutdown cancellation.

- Fixed: the `WaitForComplete` stall detector called a cold discovery WEDGED. Owned work
  with nothing `Running` and no completed dispatch is the signature of a stuck inflight
  count, and also of a scan legitimately inside its deadline. A row whose work runs under a
  finite deadline is now marked `Supervised` (`SupervisedWork` only), and live supervised
  work counts as progress while it is inside that deadline; past it its own deadline
  records the failure and the wedge is named as before. An event fold, which nothing will
  ever time out, is never supervised and is still named.

- Test and analysis evidence are minted by the owner folds and published with the work they
  belong to: `Events.EarnedEvidence` (a completion's run, what it covered in full, and every
  reason it refuses a green), `Events.AnalysisEvidence` for a daemon with no test projects,
  and `HostSnapshot.Evidence` / `.AnalysisEvidence` beside `IsBusy`. `ProjectGraphAccessor`
  gains `ObserveCheckableFiles` (the available model's files and generation). `GetDiagnostics`
  serves `modelReceipts` (`runId`, `modelGeneration`, `refusals`) — additive on the existing
  reply, and omitted entirely when no registered plugin mints evidence. Every scan now seals
  its cohort, including one that dispatched no file, so an analysis-only repository has an
  answer about its model. Breaking for code that constructs `ProjectGraphAccessor`.

- Check results carry the project-model generation they were captured against
  (`FileCheckResult.ModelGeneration`, `BatchChecked.ModelGeneration`; `CheckPipeline`
  leaves it `None`). A scan and a change batch capture the model once discovery is
  settled and publish only while it is still current. A scan admitted during a
  rediscovery waits for it. A scan whose model is replaced fails with the reason
  "invalidated before scan publication". A change batch reruns against the current model
  and keeps its admitted inputs. After 5 superseded attempts it fails ("the project
  model kept changing during the batch"), and its changes run ahead of the next batch. The host work store publishes the model with its
  checkable files, and plugins read it through `ProjectGraphAccessor.ObserveModel`, which a
  `PluginHost` always answers from its own store. Breaking for code that constructs these
  records.

## 0.10.0-alpha.37 - 2026-09-17

- Scans and watcher change batches run under a bounded supervisor (`SupervisedWork`,
  `DebouncedWork`) that publishes into the host work store. `GetScanState`,
  `GetScanGeneration` and `FormatScanStatus` never wait for a scan; in-flight scans and
  batches count as host work (`scan`, `changes`), carry the ambient verdict deadline, run
  in their own child-process scope, and fail their receipts instead of abandoning them.
  `ScanAll` raises the scan's failure, and `ObjectDisposedException` after `Dispose`.
  `ScanSignal.ObserveScan` delivers a failed scan to its `WaitForScan` waiters.

## 0.10.0-alpha.36 - 2026-09-17

- Plugins run on the work owner. An event is outstanding until its state is committed
  (publish, `Finalize`, cache write, receipt); `RegisteredPlugin.DispatchTracked` returns its
  receipt. `PluginCommand.Observe` reads the committed snapshot without waiting. Exclusive
  runs own their key through result commit and tear down their children before retiring.
  `PluginHost` owns dispatch fan-out and preprocessor passes, reads busy/progress/faults from
  one snapshot, and adds `FailedWork` and `FailedOperations`. Breaking: `WorkCycleGenerations`
  is removed; shared starters return `SharedRunStart`; `PluginCtx`/`CommandCtx` gain
  `EnqueueExclusiveIntent`.
- Breaking plugin API: `PluginHandler.CacheKey` receives the committed state
  (`'State -> PluginEvent<'Msg> -> ContentHash option`); `Commands` are
  `PluginCommand.Observe` (reads state, cannot post) or `PluginCommand.Request` (posts,
  never sees state); new `PrepareCommit` field for durable preparation before a new state
  counts. `PluginCommand.invoke` runs a command against an explicit state.

- `Ctrf.tryVerdictReport` validates a CTRF report as verdict evidence and returns the
  opaque `Ctrf.VerdictReport` (read its counts with `VerdictReport.summary`); it is the
  only way to construct one. All six summary counters (`tests`, `passed`, `failed`,
  `pending`, `skipped`, `other`) must be present nonnegative integers, and a clean
  summary (no `failed`, no `other`) must be accounted for by its `tests` rows: one row
  per counted test, every row `passed`, `pending` or `skipped`, and each of those counts
  matching. A red summary stays authoritative without its rows, because a test that threw
  a raw exception is counted but gets none. A clean summary claiming seven tests beside
  one row was previously read as seven passes. `Ctrf.tryReadVerdictReport` and
  `Ctrf.verdictReportsForRun` are the strict counterparts of `tryReadReport` and
  `reportsForRun`, which stay diagnostic.

- Fixed: when `IpcServer.start` returned, its pipe name could still accept connections for
  a moment, so a client probing just after a daemon stopped found a daemon that was not
  there (measured: most of 25 consecutive shutdowns). The server no longer replaces an
  acceptor once shutdown has begun, waits for its acceptors, lets open connections finish
  for up to `IpcServer.ConnectionDrainBound` before closing them, and then waits up to
  `IpcServer.ReleaseBound` until a connection to the name is refused — disposing a pipe
  does not release its listening socket at once.
- Fixed: a `Shutdown` RPC ran the daemon's whole teardown inline on the RPC handler's
  thread (the cancellation continuation was synchronous), so its reply waited on the
  teardown. `RunWithIpc` now continues asynchronously and waits for the IPC server
  without blocking a thread.

- breaking: the daemon now serves its project model to clients.
  `DaemonRpcConfig` gains the required `GetProjectModel: unit -> ProjectModel.Observation`,
  and the `GetDiagnostics` reply carries it as `projectModel` — the versioned
  `fshw-project-model-v1` payload (`ProjectModelWire.payload`) — read at request time
  beside `unchecked` and the statuses. `Daemon.ProjectModel()` exposes the coordinator's
  observation: `Rediscovering` while a discovery attempt is in flight, `Unobserved` before
  any completed, otherwise the classified completed outcome. This is what lets a client
  tell a model mid-rediscovery from a healthy one that selected nothing; before, both
  reached the CLI as the same empty reply.
- Fix: the RPC seam's deadline now covers a callback's synchronous prefix. The timer
  starts before the callback runs, and the callback is scheduled on the thread pool, so
  an RPC body that blocks before returning its Task (a `task { }` runs inline up to its
  first real await) faults with `TimeoutException` instead of holding the caller forever.

## 0.10.0-alpha.35 - 2026-09-16

- A deleted working directory is named once, not reported as a missing file at every call site

## 0.10.0-alpha.34 - 2026-09-16

- new `FsHotWatch.CacheInputs` module for cache-key inputs that live
  outside the judged file. `configChainInputs` collects a walked-up config file (every
  one from the repository root down to the file, repo-relative label, content);
  `editorConfigInputs` is its `.editorconfig` case, moved here from
  `FsHotWatch.Fantomas.FantomasTool`.

- `CheckPipeline.RegisterProject` REPLACES a project's compile-item set on
  re-registration. A file the prior options listed and the new ones do not stops mapping
  to that project, and leaves `GetAllRegisteredFiles` when no other project lists it;
  before, it survived with the project's old options. Daemon discovery was not exposed
  (it calls `PrepareForRediscovery` first), but the public `Daemon.RegisterProject` was.
## 0.10.0-alpha.33 - 2026-09-16

- `ProcessHelper.quoteArg` / `ProcessHelper.splitArgs` — the one
  per-argument quoting rule for a child's `ProcessStartInfo.Arguments` string and its
  inverse. Every builder of a runner arg string (TestPrune's impact filter, the CLI's
  `test-rerun` filter) quotes through it so a spaced value survives as one argument.

## 0.10.0-alpha.32 - 2026-09-16

- `CachePathIdentity.ofPath` names a directory the same with or
  without a trailing separator. `.fshw.json` analyzer paths are written
  `…/bin/Debug/net10.0/`; the separator survived as an empty last segment, the
  portable grammar rejected it, and the analyzers cache key's `analyzer-paths` slot
  silently fell back to the ABSOLUTE path — so no second checkout could hit it even
  once the assemblies were identified by their receipt.

- the box-wide task-cache store is now actually shared between the
  checkouts of one repository. `RepoIdentity.namespaceOf` named the store directory
  `<checkout name>-<digest>`, so every jj workspace had a private directory (65 of
  them for one repository on one box), and `describe` hashed the TEXT of jj's
  relative `.jj/repo` pointer, so workspaces and the default checkout did not even
  agree on the digest. Pointers are now resolved against the directory they live in
  and the label is the repository's own directory name; the tests assert the
  directory, not just the digest. Existing per-checkout directories are orphaned,
  not deleted; the repository-level directory warms once.

## 0.10.0-alpha.31 - 2026-09-15

- Fix: a scan can no longer read an in-flight rediscovery as an empty project model
- Add: a typed project-model observation, so a scan can tell rediscovery from emptiness
- Finish: update SourceLink to fix CVE-2026-62900 restore failure

## 0.10.0-alpha.30 - 2026-09-07

- **`RunOutcome.VerifiedNothing of detail`** — a `Completed` run that
  executed nothing, as a case beside `CompletedRun | FailedRun | TimedOut`. It is
  derived from the verdict: `RunVerdict` carries `NothingVerified: string option`, built
  only by the new `RunVerdict.verifiedNothing detail elapsed` (throws on a blank detail,
  as `create` does on a blank summary), and `RunOutcome.ofCompletedVerdict` is the one
  mapping the host applies on every `Completed` terminal. New `PluginStatus.verifiedNothingNow`
  and `PluginCtxHelpers.completeVerifyingNothing` mirror `completedNow` / `completeWith`.
  The status payload serialises the case as `{"tag":"verifiedNothing","detail":…}`.
  **Removed:** `RunSummary.saysNothingVerified` — the fact is no longer read off the
  summary's words. `RunSummary.nothingVerified` remains, as pure display. The fact lives
  on the verdict rather than as an activity-level outcome override (the
  `CompleteWithTimeout` mechanism) because the task cache stores the verdict and replays
  it as a fresh `Completed`: an override would not survive replay and the replayed run
  would render as a pass. `FileTaskCache` writes the detail as `nothingVerified` on run
  entries and bumps the entry format to 5, so entries written before the field existed
  read as a miss instead of as a verified run.

## 0.10.0-alpha.29 - 2026-09-06

- rework of verdict wall-time attribution: **the daemon records every phase it spends wall time in.**
  New `DaemonPhases` module: a bounded, thread-safe `Ledger` of `PhaseRecord`s
  (`Scope`, `StartedAt`, `Elapsed`, `Detail`), reachable as `PluginHost.Phases`.
  - `Phase.Startup` (process start → IPC pipe listening), `Phase.Discover` (MSBuild
    evaluation, every call), `Phase.Scan kind` (the whole `performScan`: discovery
    admission, build settlement, the FCS tiers), `Phase.Check` (each incremental change
    batch) and `Phase.PluginRun name` — the plugin's WHOLE `Running` → terminal
    interval, recorded by the status agent on every terminal transition, so a run a
    later re-run supersedes stays on the ledger and test-prune's pre-run symbol
    analysis counts.
  - A phase is recorded on EVERY exit (completion, exception, cancellation) through a
    `use`-bound `PhaseHandle`; a snapshot taken mid-phase reports the in-flight phase
    clipped at now and marked `in flight`. Same-scope records at most 250 ms apart
    coalesce into one (the analyzers plugin reports a terminal status per file — ~130
    transitions in seconds — which on the first dogfood run evicted the startup and
    discovery records and left a 33 s hole); the ledger retains 256 records.
  - `GetDiagnostics` serves the ledger as `daemonPhases[]` (`scope`, `startedAt` in
    round-trip UTC, `elapsedMs`, `detail`) beside `statuses`.

- rework of the FSEvents stream-refusal retry: **a native FSEvents stream macOS refuses on EVERY attempt of
  the retry budget fails the watcher closed instead of demoting it to polling.** The
  first landing retried transient refusals (100/300/900 ms) but let a refusal past the
  budget fall into the same `with ex ->` catch-all as any other setup fault, and that
  catch-all's answer is the 1-second content-snapshot poller — so a persistent refusal
  silently produced a daemon watching the repository by polling for its whole lifetime.
  - `FileWatcher.create` on macOS now raises `NativeStreamRefusedPastBudgetException`
    (public; carries a `NativeStartRefusal` record: attempts made, backoff spent,
    macOS's last refusal) when the budget is exhausted. Nothing is left behind: the
    native start runs before any `FileSystemWatcher` is registered, and no polling
    watcher is constructed.
  - The split is structural, not a flag. Driving the native factory through the budget
    yields a `NativeStartOutcome` — `Started` / `RefusedPastBudget` / `Faulted` — and
    the polling fallback is the `Faulted` arm only. `WatcherMode.ContentPolling` now
    means exactly "a non-refusal setup fault": a `FileSystemWatcher` that would not
    start, a callback that would not pin.
  - `NativeStartRetry.none` is a one-attempt budget, so a refusal under it is a refusal
    past the budget.

- `ErrorLedger.Transport` bounds every mirror of the ledger with one
  per-field cap and one response-wide `detail` budget. `DaemonRpcTarget.GetDiagnostics`
  had no cap at all, so a broadly red suite asked it for a reply of `failing entries ×
  captured output` characters — ~45 GB on the measured incident — and it faulted with an
  out-of-memory error at the end of a run that had already succeeded. Entries are
  trimmed, never dropped (the count and severities decide the exit code); an entry whose
  detail the budget could not carry says so. `FileErrorReporter`'s existing cap is now
  the same one.

## 0.10.0-alpha.28 - 2026-09-04

- **the per-file caches are content-addressed, and a store is shared
  between the workspaces of one repository — so a freshly created workspace starts
  warm.** Nothing about WHICH checkout a file was read from reaches a cache key any
  more:
  - Every per-file cache key names its file REPO-RELATIVELY (`CachePathIdentity`),
    across the plugin framework's composite key (which becomes the on-disk entry's
    file name), the format-check merkle (path AND `.editorconfig` labels), the lint
    merkle and the analyzers merkle (including the analyzer directory hash). Two
    checkouts of one repository now mint the SAME key for byte-identical content.
    Each plugin's salt is bumped (`format-check-pinned-tool-v3`, `lint-merkle-v2`,
    `analyzers-merkle-v4`), so entries written under the old absolute-path keys are
    orphaned rather than misread.
  - `CheckCache.getProjectOptionsHashRelativeTo` names in-repo paths relatively in the
    compiler-options hash — the recorded prerequisite for a single shared daemon,
    which would otherwise hold one disjoint compiler snapshot per workspace of
    identical code. Out-of-repo references (the NuGet cache, the SDK) stay absolute:
    they are machine-local, and on one machine they are identical for every workspace.
    `CheckPipeline` takes an optional `repoRoot`; without one the hash is exactly what
    it was.
  - A second, BOX-WIDE store lives outside every checkout, under `$FSHW_CACHE_HOME`
    (else `$XDG_CACHE_HOME/fshw`, else `~/.cache/fshw`), namespaced per REPOSITORY by
    `RepoIdentity` — which reads jj's `.jj/repo` workspace pointer or git's `gitdir:`
    worktree pointer as a directory-layout question, with a per-checkout fallback when
    neither is recognised, so an unknown layout means "no sharing", never "sharing with
    the wrong repository".
  - `CacheResidency` decides, per plugin, which store an entry lives in. It is an
    ALLOWLIST and it fails closed. **Shared:** `format-check`, `lint`, `analyzers` —
    each a pure function of the inputs its key names, each producing diagnostics only.
    **Deliberately NOT shared:** `build` (a cached verdict asserts artifacts a fresh
    checkout has not produced — and its `replayBlockers` gate already refuses such a
    replay), `test-prune` (a cached green asserts a test process that did not run
    here), `coverage` (declares no key) and `file-command` (an arbitrary shell
    command's effects are unmodelled). An unclassified plugin is workspace-local.
  - Entries are PORTABLE, not merely their keys: every file path an entry names is
    stored `repo:`-relative and rebound into whichever checkout reads it back, so a
    replayed finding lands on the right file. A path that cannot be honestly resolved
    here reads as a MISS. Entry format bumped to 4.
- `ITaskCache.Lookup` returns a typed `CacheMissReason` beside the
  result, so a cold start says WHY: `NoEntryForKey`, `InputsChanged [labels]` (the
  merkle inputs that actually differ, named), `InputsNotComparable`, or
  `UnreadableEntry`. Each entry persists the per-label digests of the key that minted
  it, so the diff works against an entry another workspace wrote. `TryGet` is retained
  and is defined in terms of `Lookup`, so the two cannot disagree about hit or miss.
- `FsHwPaths.atomicWriteAllText` gives each write a unique temp name. With a store
  shared between two daemons, two processes can be mid-write to one entry path, and a
  single fixed `.tmp` name let them truncate each other's partial file.

## 0.10.0-alpha.27 - 2026-09-04

- `DaemonOptions` gains `RunMode` — `Watching` (the default: a
  persistent daemon builds its `FileWatcher` exactly as before) or `OneShot`, under
  which daemon construction never reaches watcher construction at all: no native
  FSEvents stream, no `FileSystemWatcher`, no polling fallback, no watcher thread. A
  one-shot host's verdict can therefore no longer fail at `FSEventStreamStart` for a
  stream it would never have read from. Callers building `DaemonOptions` by hand must
  supply the field; `{ DaemonOptions.defaults with ... }` is unaffected.
- `Daemon.WatcherFactory` names the `FileWatcher.create` signature, and the internal
  `createWithWatcherFactory` seam proves the two modes: `OneShot` never invokes the
  factory, `Watching` invokes it once.

## 0.10.0-alpha.26 - 2026-09-04

- a native FSEvents stream macOS transiently refuses is retried with
  bounded backoff (100/300/900 ms) before the polling fallback is chosen, so one refused
  `FSEventStreamStart` no longer demotes the daemon's whole lifetime to 1-second
  content-snapshot polling. Only `MacFsEvents.NativeStreamRefusedException` (the new
  base of `StartFailedException` and `CreateFailedException`, which replaces the
  untyped `FSEventStreamCreate` failure) spends the budget; any other setup fault falls
  back at once, and a healthy start pays no delay.
- `FileWatcher` gains `Mode: WatcherMode` — `NativeEvents` or `ContentPolling of reason`
  — so a caller can tell a recovered native watcher from a polling fallback without
  reading logs. Consumers constructing the record must supply it.
- The `createWithFactories` / `createWithNativeStream` test seams take a
  `NativeStartRetry` (`defaults` or `none`, or a recording one) as their new argument
  after `latencySeconds`.

## 0.10.0-alpha.25 - 2026-09-01

- authoritative daemon and run-once verdict reads now remove late
  findings for renamed-away repository files. Cleanup is revision-conditional, so a
  concurrent report for a recreated same-name file is retained; outside-repository and
  angle-bracket diagnostic keys remain opaque.

## 0.10.0-alpha.24 - 2026-08-31

- chore(deps): rebuild for the synchronized FsHotWatch.Cli dependency release.
## 0.10.0-alpha.23 - 2026-08-31

- Case 1: the daemon now registers the enforcing BuildPlugin path.
  Cached and freshly produced build success cannot outlive missing, older, or
  byte-divergent graph artifacts; generated `bin/`/`obj/` compile items remain excluded
  from the authored-source clock by the "a compile item is not an edit" fix (ADR-015).

- expose bounded daemon cache invalidation over IPC. The live
  task-cache instance clears persisted and in-memory entries without restarting FCS.

## 0.10.0-alpha.22 - 2026-08-30

- fix: propagate `MSBUILDDISABLENODEREUSE=1` through shell and other wrapper
  commands so descendant `dotnet` builds cannot reuse stale MSBuild nodes.

## 0.10.0-alpha.21 - 2026-08-29

- fix: cached test-run replay now synthesizes the `TestRunStarted` lifecycle
  boundary omitted by newly written cache entries. Build deferral therefore
  observes the same active-host lifetime on cache hits as on live test runs and
  cannot rewrite instrumented binaries before the matching completion event.

## 0.10.0-alpha.20 - 2026-08-28

- macOS watcher startup now falls back transactionally to one symlink-safe,
  content-hashed polling watcher when native FSEvents setup fails. The fallback
  covers source/project files, top-level solutions, and configured extra file
  patterns without retaining partially-created FSEvents-backed watchers.

- project discovery now atomically records one completed,
  serialized loader epoch, keeping discovered, Ionide-loaded, FCS-options-mapped,
  and pipeline-registered counts distinct. `WaitForComplete` waits through an
  in-progress clear/load window and rechecks the discovery generation after the
  host settles, so a rediscovery that starts after verdict admission cannot reuse
  stale terminal plugin state. A completed zero-loaded discovery terminates
  immediately with the `LoadProject FAILED` diagnosis instead of waiting out the
  verdict deadline as an empty host.

## 0.10.0-alpha.19 - 2026-08-26

- **`TreeHash.Algorithm` is `fshw-tree-sha256-v3` — the hashed set now covers
  what DECIDES a check, not just its source.** Up to v2 the set was the `src/`+`tests/` walk
  plus `.fshw.json`; everything outside those roots that changes an answer — the coverage
  floors, the analyzer rules, `Directory.Build.props` — contributed nothing, so weakening a
  check left a green earned under the stronger one still reporting `Applies`.
- New module `FsHotWatch.VerdictInputs`, which owns the derivation rule (*a file belongs in
  the tree hash iff changing it can change what a check concludes*) and both halves of its
  application: `toolKnownInputs` (`.fshw.json` plus the root-level toolchain/dependency
  files) and the repo's own `verdictInputs.hashed` / `verdictInputs.notInputs` declarations,
  each entry carrying a required, reviewable reason. A declaration that resolves to no file
  contributes a sentinel entry rather than zero — see `VerdictInputs.AbsentDeclaration`.
- `TreeHash.Tree` gains `DeclaredCount` and `AbsentDeclarationCount`; `TreeHash.Walked` gains
  `AbsentDeclarations` and `DeclaredCount`. Both are additive record fields, so a consumer
  CONSTRUCTING either type must supply them.
- `DepsFreshness`'s dependency-file name list is now sourced from
  `VerdictInputs.DependencyFileNames` rather than restated — "is the restore stale?" and "can
  this file change what a check concludes?" are two questions with one answer.

- **the copy-consistency predicate now lives in core** (`OutputCopyFreshness`),
  which is what makes it reachable from `FsHotWatch.Build`. It had lived only in
  `FsHotWatch.TestPrune.ArtifactFreshness`; the two plugins are siblings over core, so the
  one that owns the build cache could not ask the question that its wedges were actually
  made of. `ArtifactFreshness` now goes through the same rule rather than restating it —
  two implementations of a rule that has to agree is how a gate degrades without anyone
  noticing.
  Two questions, deliberately separate, because the answer decides what a caller may DO:
  `isPending` is MSBuild's own `SkipUnchangedFiles` predicate (same size AND same mtime),
  pure `stat`, and a build provably clears it — so a cache gate may use it. `verdict` is
  the byte comparison, and it is not a substitute: a copy that differs in content while
  matching on size and mtime is one MSBuild skips for ever. Measured on a real
  two-project build — a plain `dotnet build` leaves that destination byte-for-byte as it
  found it, while a copy merely left behind by a refreshed producer is re-emitted and
  comes back with size and mtime equal again.

## 0.10.0-alpha.18 - 2026-08-25

- **BREAKING (API)** `KillOutcome` gains a fourth case, `KillTimedOut of budget: TimeSpan`
  — an exhaustive match will not compile until it is handled. It is deliberately NOT a flavour of
  `KillFailed`: that case is the OS refusing with a reason it can name, this one is the OS answering
  nothing at all inside the teardown budget. Both leave the tree unaccounted for and both fail closed;
  only one has a reason to give. The budget exists because an unbounded teardown is a phase that cannot
  finish, and a phase that cannot finish cannot report a verdict.
- new public surface on `ProcessRegistry` so an unaccounted tree is named rather than
  dropped — the `LeakedTree` record, `reportLeak`, and the registry's `ReportLeak` / `Leaks` members.
  The leak list is append-only: a tree we could not account for is never un-leaked, because nothing
  observed later would distinguish it having died from its pid being reused.

## 0.10.0-alpha.17 - 2026-08-24

- confirm records the check-scoped verdict it already computes — as a PROJECTION when it does not have to escalate

## 0.10.0-alpha.16 - 2026-08-23

- docs: remove three stray diff3 base markers from the changelogs

## 0.10.0-alpha.15 - 2026-08-23

- **a compile item is not an edit** — `ProjectGraph.GetMaxSourceMtime`
  now excludes build output. It folded over MSBuild's whole compile-item list, which
  includes `obj/<cfg>/<tfm>/<Project>.AssemblyInfo.fs`; every design-time evaluation
  regenerates that file and project discovery IS a design-time evaluation, so each
  discovery pass stamped every project's newest "source" after its own freshly-built
  DLL and the artifact gate read an untouched tree as universally stale. Measured over
  the report-only window across ~40 workspaces of a consuming repo: 2090 stale
  findings, 91% within 90 s of an `MSBuild evaluation` pass in the same daemon log.
  The gate stays report-only — the corrected reading has not run against a real
  repository either. See [ADR-015](../../docs/adr-015-a-compile-item-is-not-an-edit.md).
- `SafeWalk` gains `BuildOutputDirs` and `isBuildOutput`. `SourceExcludedDirs` is now
  defined in terms of `BuildOutputDirs`, so the walk TestPrune's `ArtifactFreshness`
  does and the per-path lookup the project graph does answer from one fact rather than
  having been separately taught it. `isBuildOutput` asks RELATIVE to the project
  directory: matching `bin`/`obj` in the absolute path would classify every file of a
  repo checked out under such a directory as output, and the gate would answer FRESH
  forever.

- **BREAKING** — `ErrorLedger.DiagnosticSeverity` gains a `HostAborted` case.
  Every exhaustive match over the severity has to decide it, which is
  the point: it is the third answer — "this did not run because the runner died" —
  alongside `Deferred`'s "this did not run because the build was not ready". Like
  `Deferred` it is never a failure (`ErrorEntry.isFailing` is `false` for it in both
  warn-fail policies); `ErrorEntry.isRunnerAbort` and `ErrorEntry.abortedWithDetail` are
  the reader and the writer.
- `ProcessHelper.TerminatingSignal` + `tryOfExitCode` / `terminatingSignalOf` — tell
  "the program chose this exit code" from "the OS killed it". .NET on Unix reports a
  child terminated by signal N as `128 + N`; the recognised set is a NAMED, closed list
  (SIGKILL, SIGABRT, SIGSEGV, SIGTERM, …) whose numbers mean the same signal on Linux and
  macOS, and which no test runner's chosen exit code can collide with. A timeout answers
  `None` deliberately — it already has its own outcome carrying who killed it.

## 0.10.0-alpha.14 - 2026-08-19

- **BREAKING** — `SafeWalk` now reports what it could not see.
  `enumerateFilesMatching` / `enumerateFiles` / `enumerateFilePaths` are renamed
  `bestEffortFilesMatching` / `bestEffortFiles` / `bestEffortFilePaths`, and two total
  entry points are added: `enumerateEntries` (a lazy `seq<WalkEntry>` — `Found` a file,
  or `Skipped` a directory) and `walk` (a `WalkResult` with `Files` and `Skipped`).
  - Why: an unreadable directory, and a subtree past `MaxDepth`, contributed **zero
    entries** and were invisible to the caller. `ContentHash` goes to real trouble to
    make an unreadable FILE hash to a sentinel — and the walker one level up deleted
    that file from the list before the sentinel could ever be reached, which is the
    "skip the file" answer `ContentHash`'s own header names as failing OPEN.
  - The rename is the point: best-effort is correct for enumeration that only adds work
    (what to watch, what to register), and wrong for anything drawing a conclusion from
    the *absence* of files. The name now says which one you picked.

- **BREAKING** — `TreeHash.Algorithm` is `fshw-tree-sha256-v2`, `TreeHash.Tree` gains
  `SkippedCount`, and `TreeHash.files` returns a `Walked` record rather than a list.
  A directory the walk could not see is hashed as an entry of its own
  (`relPath + "/"` → `ContentHash.UnhashableContent`), so a tree we only partly saw no
  longer hashes like the tree we saw whole. `FileCount` made an *empty* walk visible;
  nothing made a *truncated* one visible.

## 0.10.0-alpha.13 - 2026-08-19

- give the artifact gate a real path, and keep it report-only
- a cache HIT must leave the ledger where a cache MISS leaves it
- a renamed file must not leave findings about a path that is gone

## 0.10.0-alpha.12 - 2026-08-18

- feat: `FsHotWatch.StructureFiles` — THE list of files that decide what is compiled
  (`*.fsproj`, `*.csproj`, and MSBuild's implicit imports `Directory.Build.props`,
  `Directory.Build.targets`, `Directory.Packages.props`), plus `implicitImportsFor`,
  which resolves the ones that apply to a project by MSBuild's own nearest-ancestor rule.
  The list previously existed twice, in the build plugin and in
  test-prune, and the copies disagreed — which is how one cache misses on a structural
  change while the other replays a green over a tree it never saw.

- **BREAKING** — `TestResult.isPassed` is **deleted**. Replaced by
  `TestResult.verdict : TestResult -> ProjectVerdict` (`Verified` | `Refuted` |
  `NothingVerified`) plus the single derived bool `TestResult.verifiedGreen`.
  - Why: `isPassed` answered TRUE for `TestsNoMatch`, so "we ran nothing" was a
    sub-case of "we passed". Every aggregator folding on it was blind to a
    zero-test run by construction and had to remember to re-derive the fact
    separately; of the five that fold over results, three did and two did not — and
    a fifth (the per-symbol pending-verification commit) was found only after the
    first fix, still discharging a symbol's test debt from a project that executed
    zero tests.
  - The migration is the compiler telling you where you were guessing. A fold that
    meant "did this project prove its tests green?" becomes `verifiedGreen`
    (`Verified` only). A fold that meant "is this project a failure to report?"
    must `match` and name `Refuted` — `not verifiedGreen` is deliberately NOT that
    predicate, and the asymmetry is the point.
  - `TestResult.executedTests` now derives from `verdict` rather than re-matching
    the cases; behaviour is unchanged.
  - `TestResult.isNoMatch`, `isDeferred`, `isErrored`, `isTimedOut`,
    `executedAnything`, `noneFiltered` and `RunVerification` are untouched.

## 0.10.0-alpha.11 - 2026-08-17

- feat: **`RunSummary.nothingVerified` — the marker a run uses to say it verified
  nothing**. A run that executed no test still `Completed`, so every
  surface rendered it `✓`: nothing failed, and nothing was checked either. The plugin
  status was sound; the glyph was not.
  - A summary minted through `RunSummary.nothingVerified` reads
    `NOTHING VERIFIED: <detail>`, and `ParsedPluginStatus.verifiedNothing` is the one
    predicate every consumer asks. The fact travels INSIDE the summary string
    deliberately: `summary` is the only field that survives the IPC JSON, `FileTaskCache`
    format 2 and `markCached`'s `" (cached)"` suffix, so no wire or cache-format change
    was needed and there is no default for a daemon/CLI version skew to get wrong.
  - The plugin STATUS deliberately stays `Completed`. Nothing failed, and reporting a
    failure would turn an honest exit 3 (no verdict) into an exit 1 (failures found).
  - Known gap, tracked rather than papered over: this is a marker a plugin *may* set,
    not one it *must*. A plugin that verifies nothing and does not say so still renders
    a `✓`. Making it structural means moving the fact into `RunVerdict.create`, which is
    a wire + cache-format change and is deliberately not bundled into this release.

## 0.10.0-alpha.10 - 2026-08-13

- fix: unblock the release — coverage floor with real headroom, versions rolled back
- Comment audit: cut AI thinking-out-loud from comments

- Comment audit: cut AI thinking-out-loud from comments

## 0.10.0-alpha.8 - 2026-08-12

- fix: **the wedge detector no longer fails a healthy check of a large repo.** It
  compared the set of busy plugin NAMES across polls and called an unchanged set a
  stall. That set does not change while a single plugin drains a long
  `FileChecked` backlog — busy throughout, nothing `Running` — so any drain longer
  than the threshold was reported as `WEDGED`. Measured on a green run of a large
  repo: three uninterrupted minutes in that state, against a five-minute
  threshold.

  It now measures **progress**: `PluginHost.CompletedDispatches()` counts events
  plugins have actually finished, incremented in the same `finally` that releases
  the in-flight count. A drain moves it on every event and can never look stalled;
  a stopped agent never moves it and always does. The distinction is structural
  rather than a tuned threshold.

- feat: **a plugin whose message loop dies now says so** —
  `PluginHost.FaultedPlugins()` and `RegisteredPlugin.Fault`. `WaitForComplete`
  fails immediately naming that plugin, and says the failure is in fshw rather
  than in the tree being checked.

  Liveness is a state, not a quantity: `IsBusy` is `inflightCount > 0`, and one
  integer cannot tell "0 and alive" from "n and dead". So a dead agent could only
  be inferred — from silence, over a threshold, at best five minutes late and at
  worst never. Publishing the fault removes the inference.

- perf: **the wait's 50ms poll no longer does a status round-trip and a
  per-plugin activity copy just to ask "is anything running?"**. The wedge check
  now tests the allocation-free busy predicate first and the expensive question
  last, and the every-10s diagnostic is no longer formatted-then-discarded at the
  default log level. Over an hour-long wait that is ~72,000 avoided round-trips,
  each of which copied every running plugin's activity tail.

- fix: **a forced `Failed` from a dispatch fault now carries a measured elapsed**,
  not a fabricated `TimeSpan.Zero` — the rule `runOne` states about itself. The
  three hand-copied fault paths are one `reportForcedFailure`, so the
  ownership rule that keeps a fault from stomping a live exclusive run's status
  is stated once instead of transcribed three times.

- fix: **a fault in a plugin's dispatch loop no longer wedges `WaitForComplete`
  forever.** `check`/`confirm` could hang with every plugin terminal, nothing
  `Running`, and the wait spinning to its timeout. Three deploys in a downstream
  repo sat on that for an hour each, because `check` gates the deploy preflight.

  The dispatch loop computed `handler.CacheKey event` *outside* the `try/finally`
  that decrements the plugin's in-flight counter. A throw there leaked the
  increment the post had already taken **and** escaped the message loop, which
  stops the `MailboxProcessor` — silently, because nothing subscribed to its
  `Error` event. Every later post then incremented into a mailbox nobody was
  reading. Since a plugin is "busy" exactly when that counter is above zero, a
  dead agent was indistinguishable from a working one, permanently, and both
  satisfaction paths in the wait require no plugin to be busy.

  `safeUpdate` was never a net for this: it wraps `handler.Update` alone, while
  the fault is in the machinery around the handler — the cache-key thunks and the
  cache-replay lookup, which do file I/O and read raw FCS results on the dispatch
  thread.

  A fault is now accounted for (the `finally` still decrements), visible (a forced
  `Failed`, under the same ownership rule as `safeUpdate`, so it cannot stomp a
  live exclusive run's status), and survivable (the loop continues, so the plugin
  keeps serving later events). Plugin agents also subscribe to `agent.Error` as a
  last resort, which `ErrorLedger` and the scan-signal agent already did.

  Field note for anyone diagnosing a similar hang: "nothing running, but plugins
  still BUSY" is *not* by itself a wedge — a healthy run emits it for minutes
  while draining a large `FileChecked` backlog. What identifies a dead agent is a
  plugin that goes **completely silent** while the host still counts it busy.

- chore(deps): **StreamJsonRpc 2.24.92 → 2.25.29; both MessagePack pins removed.**

  The direct `MessagePack` reference here and the repo-wide `Nerdbank.MessagePack` pin
  in `Directory.Build.props` both existed only to lift StreamJsonRpc's transitive floors
  above advisories — GHSA-hv8m-jj95-wg3x (MessagePack LZ4 decompression
  AccessViolation, patched 2.5.301) and GHSA-2cwq-pwfr-wcw3 (Nerdbank.MessagePack
  attacker-controlled stackalloc in `DateTime` decoding, patched 1.1.62). StreamJsonRpc
  2.25.29 asks for `MessagePack [2.5.302, )` and `Nerdbank.MessagePack [1.2.4, )`, both
  above their fixes, so the dependency now carries its own floors and neither pin does
  any work. Removed both: restore resolves 2.5.302 and 1.2.4 transitively with zero
  advisories solution-wide. Dropping the direct reference without the StreamJsonRpc bump
  was checked too, and does not work — it resolves the range floor, `MessagePack
  2.5.198`, which is seven advisories below the fix.

  This also reverses the 3.1.8 bump from earlier in this cycle, which was a worse answer
  than it looked. **FsHotWatch never uses MessagePack**: `Ipc.fs` builds `JsonRpc` over
  a bare `HeaderDelimitedMessageHandler`, which is StreamJsonRpc's default *JSON*
  formatter. The previous entry claimed 43 IPC round-trip tests exercise MessagePack 3.x
  at runtime; they do not — they exercise the JSON path and say nothing about
  MessagePack either way. And because a direct `PackageReference` is a public dependency
  of the published package, it pushed MessagePack 3.x — a breaking rewrite that
  StreamJsonRpc 2.x was not built against — onto every consumer, including any that do
  select the MessagePack formatter. Tracking the 2.x line StreamJsonRpc actually targets
  is both safer for consumers and two fewer pins to maintain.

## 0.10.0-alpha.7 - 2026-08-11

- **BREAKING: `TestRunCompleted.RanFullSuite: bool` is replaced by
  `Verification: RunVerification`, and scope now lives inside the case that ran.**

  The bool had no inhabitant for "this run executed nothing". `true` claimed a suite
  that never ran; `false` claimed an impact-filtering that never happened. So the two
  degenerate lifecycles (the aborted preflight and the "0 affected classes" skip) each
  picked the less harmful lie — one said so in a comment, *"a lie either way"* — and
  `CoveragePlugin`, `FileCommandPlugin` and TestPrune's ledger discharge each bolted
  on a private "…and something actually ran" conjunct to undo it. Three
  reconstructions of one missing state.

  `RunVerification` gains `NothingExecuted` (projects reported, not one ran a test —
  previously indistinguishable from a real run, so `verifiedNothing` answered `false`
  for a run that verified nothing) and `Ran of scope: RunScope`, where
  `RunScope = Partial | FullSuite`. Asking about breadth now requires first
  establishing that something ran, so those conjuncts are deleted rather than
  reworded, and `gateVerdict` takes a `RunScope` it cannot be handed for a run that
  verified nothing.

  **Migration:** `completed.RanFullSuite` becomes
  `RunVerification.ranFullSuite completed.Verification` for the gate-on-full-suite
  question, or match `completed.Verification` when the other cases matter.
  `TestResult.ranFullSuite` is gone — `RunVerification.ofResults` is the single
  derivation.

  **Wire:** the `coverage` token `"ran"` is retired for `"ran-partial"` /
  `"ran-full-suite"`, and `"nothing-executed"` is new. `tryParse` refuses `"ran"`
  rather than inventing the scope it never carried; a version-skewed CLI gets one
  no-verdict, which fails closed.

  **Cache:** entry format 2 → 3 (`testRunCompleted` carries a `verification` token,
  not a `ranFullSuite` bool). Format-2 entries read as a miss and are re-run.

- feat: **`ProcessHelper.runProcessTo` — an output sink fed AS THE CHILD SPEAKS.**
  `runProcess` is unchanged (it is now `runProcessTo None`); the new form takes an
  optional `string -> unit` that receives every chunk on the pump thread, inside the
  same lock as the in-memory capture, so a caller's file and `ProcessOutput.text` can
  never disagree about what was said or in what order. "As it arrives" is the whole
  contract, not a performance note: the returned `ProcessOutcome` only exists for a
  child we outlived, so anything derived from it is unavailable in exactly the case
  where evidence matters most. A sink fed from the final capture would be no sink.
  A **throwing** sink is disabled (once, with a warning) rather than fatal, and never
  touches the pump's own `failure` latch — that latch means "the stream died", and a
  full disk must not downgrade a complete capture to `DrainTimedOut`.
- feat: **`RunLog` — the streamed per-project test-run log**, at
  `.fshw/test-runs/<runId>/<Project>.output.log`, beside that run's CTRF report.
  `AutoFlush`, so bytes are on disk before the close and `tail -f` works on a suite
  that is still running; `FileShare.Read` so a reader can open it mid-flight. Opening
  is best-effort and NEVER throws — failing to open a place to write evidence may not
  fail the run that produced it. `RunLog.Ref` is a DU (`Written of path` /
  `Unavailable of reason`) because `Written` can only be constructed by the code
  holding the open handle: a path in a diagnostic is therefore a path that exists.

- feat!: **`TestResult` gains a `TestsNoMatch` case — "the filter matched no test" is
  now a state, not a string.** It was previously encoded as
  `TestsPassed` carrying a magic `[fshw:no-tests-matched] ` prefix on the output, which
  made "we ran nothing" a *sub-case of* "we passed": `TestResult.isPassed` was true for
  it, and every fold over results had to remember to re-derive the fact with a
  `StartsWith`. Two folds remembered and two did not, which is what produced
  "N passed, 0 failed in N projects" for projects that executed no test. `TestsTimedOut`
  already warned against exactly this — "without grepping the output for a magic prefix"
  — and the next case that needed one got a prefix anyway.
  **`isPassed` is deliberately still TRUE for `TestsNoMatch`.** Per project, a filter
  matching nothing is not that project's failure — an impact selection naming no class
  in some project must leave it passing, or every filtered run goes red. The dishonesty
  was only ever the RUN-level verdict. New: `TestResult.isNoMatch`, which is the
  predicate a run-level fold actually wants.
  Breaking for anyone pattern-matching `TestResult` exhaustively. In this repo the
  compiler named 8 such sites, and each now records an explicit decision at the call
  site rather than a catch-all. Note the honest limit of that enumeration: folds that go
  through `isPassed` are *not* flagged, because `isPassed` remains total and true here —
  the two aggregators that were actually wrong had to be found and fixed by inspection
  (see FsHotWatch.TestPrune's entry). What the case buys is that the fact can no longer
  be lost by forgetting a string comparison, and that every future `match` must decide.
- fix: **a cache entry written before the above is no longer replayed as a genuine pass.**
  Such entries are stored as `"passed"` with the marker embedded in
  their output; read back naively they would claim a project's tests passed when the
  filter had matched none of them — the bug resurrected from a warm cache rather than
  from the code. `FileTaskCache` reconstructs the case from the legacy prefix and strips
  it, so a replayed entry matches what a fresh run produces. The prefix survives only as
  `TestResult.LegacyZeroMatchMarker`, read on that one path and constructed by nothing.
- feat: **`FSHW-VERDICT-001` — a third repo-local convention rule
  (`analyzers/FsHotWatch.Rules`), closing the honest limit named two bullets up.** The
  new case made every `match` decide, but a `forall` over `TestResult.isPassed` still
  type-checks, is still total, and still folds a run in which every project executed
  nothing into a green — which is why the two aggregators that were wrong had to be
  found by hand. The rule names the *fold*: a `Map`/`List`/`Seq`/`Set.forall` whose
  predicate is `isPassed`, pointing at `verificationOf` → `RunVerification` instead. It
  stays quiet on the shapes that are correct — filtering *for* the non-green, any
  per-project use on a single result, and a fold that states the run-level guard beside
  it (TestPrune's cacheable-green gate, which conjoins its `allPassed` with
  `not (allZeroMatchOf …)`). Pinned by positive *and* negative controls, like the other
  two.

- feat: **activity heartbeat.** The daemon rewrites `<repoRoot>/.fshw/heartbeat` —
  Unix epoch seconds, decimal ASCII — every 15s for exactly as long as a run is in
  progress, and **never while idle**. Same contract shape as `daemon.pid`: fshw
  publishes a fact about itself and holds no opinion about who reads it or why.
  Process liveness answers the wrong question (a daemon can be alive and wedged,
  holding something while doing nothing); "is it beating?" answers the right one.
  Absence or unparseable contents means **UNKNOWN, never "stale"** — the file is
  never deleted (a stale timestamp is a stronger signal than absence), writes are
  atomic so no torn read can look like a fresh beat, and a failed beat is logged and
  swallowed rather than killing the daemon. The beat is driven by the inflight-work
  signal (`AnyPluginBusy`, held for the whole lifetime of an exclusive run) plus
  active verdict waits — never by log output — so a test phase that runs for minutes
  in silence keeps beating. The timer wakes every 1s but writes every 15s, so a run's
  first beat lands within a second rather than up to a cadence late.
- fix: heartbeat beats are serialised. A `Timer` fires on its period whether or not the
  previous callback finished, so at a one-second tick under load the callbacks overlapped
  and two beats raced on the single temp file inside the atomic write — the loser's rename
  found its source already consumed. It failed safe (logged and swallowed, next beat
  correct) but cost a beat; a non-blocking gate now keeps beats strictly serial.
- fix: **`%A` no longer truncates diagnostic logs.** `%A` caps sequences at 100
  elements (four for a bare `seq`) and hard-wraps at 80 columns, so a log line built
  with it could not distinguish 101 items from 1,500 and smeared one record across
  many lines. New `StringHelpers.describeMany` always leads with the exact count,
  then a bounded single-line sample.

## 0.10.0-alpha.6 - 2026-08-05

- check/confirm: classify "waiting on build" as Incomplete (exit 2), not a failure (exit 1)

## 0.10.0-alpha.5 - 2026-08-03

- chore(deps): update dev-tools + external dependencies
- chore: trim stale/historical comments to minimal current-state context

## 0.10.0-alpha.4 - 2026-07-22

- chore(deps): bump the transitive `System.Security.Cryptography.Xml` pin 10.0.9 → 10.0.10
  (10.0.9 became affected by GHSA-8q5v-6pqq-x66h / GHSA-23rf-6693-g89p and siblings, high
  severity, patched in 10.0.10).

- fix: **a cached per-file plugin result can no longer replay a stale whole-session
  summary that contradicts the verdict**. The analyzers plugin computed
  its `summary:` line from the whole-session diagnostics map + run counter, but the
  framework cached that terminal status under a *per-file* key whose content hash covers
  only that one file. Fixing a finding in file A rewrote only A's cache entry; every other
  file's entry kept the old "5 findings" summary string, and on the next cache hit
  `tryReplayCache` re-reported it verbatim as the plugin's current status — so `confirm`
  and `verdict.json` rendered "5 findings (cached)" while the diagnostics ledger the
  verdict actually gates on was empty and green. Same defect reached lint (also per-file).
  - The stale state is now **unrepresentable**: a per-file cache entry (`CachedFile*`)
    carries elapsed only — no summary, no timestamp. On replay the summary is *derived*
    from the live diagnostics ledger at report time, inside the same status-lock guard that
    reports it (atomic derive-and-report), so a summary that claims findings the verdict
    can't see cannot occur. Run-level (`File=None`) entries — e.g. Build, whose summary
    carries pass counts that are not in the error ledger — keep replaying their stored
    summary verbatim. The scope rule: *a cache entry may only assert facts derivable from
    its key's scope.*
  - `FileTaskCache` serialization is versioned (format 2); pre-existing on-disk entries in
    the old shape deterministically miss (telemetry counter increments) rather than
    half-parsing. First check per workspace after upgrade is a cold cache — self-healing.
  - Format-check has the same defect but sits on the shared `File=None` path; deliberately
    scoped to a follow-up rather than bundled here. *Closed since* — not by
    the per-plugin "summary is ledger-derived" signal anticipated here, but by scoping
    format-check's own summary to the run its key covers, so the rule below needed no
    weakening. See `docs/adr-014-a-plugin-summary-is-scoped-to-its-cache-key.md`.

## 0.10.0-alpha.3 - 2026-07-15

- fix!: **the task cache can no longer DESTROY the result of a run that actually
  happened.** `PluginFramework` consulted the cache for *every*
  dispatched event, including a plugin's own `Custom` messages — and a `Custom` message is
  not an observation of the world, it is the **delivery of work already done**. Its payload
  is not in the cache key: TestPrune's `cacheKeyFor` reads the `TestRunCompleted` it
  carries only far enough to decide whether the result is *cacheable*, never far enough to
  *identify* it. Two different runs — different run ids, different results, both green —
  therefore collide on one key, and serving the hit **skips the handler**, which is the
  only thing that folds a finished run into plugin state.
  - Observed on an unchanged tree: `fshw confirm` forced the full suite, ran it for 102
    seconds, passed 1965 tests and wrote a complete CTRF report — and the framework
    replayed a cached terminal over the `TestsFinished` carrying it. The plugin never
    learned the run had happened, `test-scope` went on answering *"no tests ran"*, and
    `confirm` refused to give a verdict on evidence it had just spent 102 seconds
    producing.
  - A `Custom` message is now a cache **writer**, never a cache **reader**. The write is
    kept: a Custom window is how the entry the next `BuildCompleted` hits gets minted at
    all. A cache that can destroy evidence is worse than no cache.

- fix: **`Ctrf.tidyRunsDir` can no longer FAULT the run it is cleaning up after.** It
  enumerated the run directory TWICE and applied the SECOND enumeration's count to the
  FIRST enumeration's list (`runDirs |> List.skip (min keepRuns (List.length (runDirs
  …)))`). A run directory appearing between them — a second fshw process, a concurrent
  workspace, a parallel suite finishing its own run — pushed the skip count past the
  list's length, and `List.skip` raised `ArgumentException`, which the enclosing
  `IOException | UnauthorizedAccessException` handler did not catch. It escaped
  best-effort housekeeping that is explicitly documented as *"must never fail the run
  that produced the evidence"*. It now enumerates ONCE, and the catch is widened —
  "must never fail the run" is a promise about all exceptions or it is not a promise.

- fix: **the terminal-status ownership guard is now ATOMIC against a run-slot claim**.
  A design review alleged the shipped guard was
  "a narrowing, not a cure", and it was right about the mechanism: `liveRunOwnsStatus`
  read the run slots under `runSlotsLock` and the caller reported the status *after*
  releasing it. A `RunExclusive` claim landing in that window publishes `Running`, and
  the stale terminal — a cache replay, a `FileChecked` completion, a `safeUpdate`
  crash-net stamp — then lands **on top of the live run**: the "content-free ✓ while
  tests are still running" signature (`started:` with no `elapsed:`) that issue
  exists to make impossible. This reproduces against the real framework.
  - It was **not reachable from any shipped plugin**: every `ctx.RunExclusive` and
    `ctx.ReportStatus` call in all eight plugins is lexically inside `Update`, and the
    agent loop awaits each `Update` before dequeuing the next event — so the check, the
    claim and the report were totally ordered on one logical thread. But `PluginCtx` is
    a record of closures; nothing in the type system confines it to the mailbox. That
    was soundness by **convention**, and any plugin claiming a slot from a `work` async
    or a spawned task — a legal use of the API — reopened the window.
  - The ownership *decision* and the *report* are now one critical section under a new
    per-handler `statusLock`, which `runExclusive` also holds across [claim slot +
    report `Running`]. A terminal therefore either wins the race (published while no run
    is genuinely live) or loses it (the claim is already visible and it is suppressed) —
    never both. Lock order is always `statusLock` → `runSlotsLock` and never the reverse,
    and nothing reachable from `services.ReportStatus` re-enters the framework
    (`PluginHost.setStatus` is a dictionary write plus a non-blocking `MailboxProcessor.Post`),
    so no cycle exists.

- feat!: **the daemon records its own binary identity, so a new CLI can never
  silently talk to an old daemon.** On startup the daemon writes
  `.fshw/daemon.identity` — its assembly version **and a content hash of its binary** —
  before the IPC pipe starts listening. A hash, not just a semver: a locally-repacked
  build can share a version string and differ in content, which is the stale-package problem
  (same version, different bytes) reincarnated as a *process* rather than a package. `DaemonIdentity.compareIdentity`
  is the whole contract, and it fails CLOSED: a daemon whose recorded identity differs
  is stale, and a daemon with **no recorded identity at all** (any build predating this
  handshake) is stale too. The check is therefore **unilateral** — an old daemon needs
  to cooperate in nothing to be found stale, which is what protects the first repin
  after this ships.
  - **BREAKING:** the internal `Daemon` constructor takes its `ProcessRegistry.Registry`
    as a parameter (see below).

- feat: **a wedged plugin is detected, named, and recovered from**.
  A plugin that reported `Running` and has posted no completion past a bound is *by
  definition* wedged. `PluginWedge` now says so on a cadence — `[wedge] analyzers still
  running after 5m (no completion posted; treated as wedged at 1h 5m)` — and past the
  bound it names the wedge, leaves a breadcrumb the next `fshw` command prints, and
  gracefully restarts the daemon down the same `cts.Cancel()` path as `fshw stop`.
  A silent log during a wedge is indistinguishable from a healthy idle daemon; the
  wedge that prompted this was silent for 8h36m.
  - The bound sits **above** the verdict deadline plus grace, so a client blocked on
    `WaitForComplete` still gets its own, more specific `TimeoutException` first, and a
    long-but-live run is never restarted out from under its warm FCS cache.
  - The detector is honest about its own limits: work in flight with *no* plugin
    reporting `Running` past the bound reports that it **cannot tell which plugin** and
    fails closed. It never concludes "healthy" by default.
  - `FSHW_PLUGIN_WEDGE_SEC` overrides the bound. There is deliberately no way to
    configure it off.

- fix: **the daemon reaps the children its plugins spawned — it never did.**
  `ProcessRegistry` is scoped by an `AsyncLocal`, and an `AsyncLocal`
  is only visible to `ExecutionContext`s captured *after* it is set. The daemon
  installed its registry in the `Daemon` **constructor** — by which point the
  `PluginHost` and the scan/change mailboxes already existed. So when the scan mailbox
  dispatched to a plugin, that plugin's `runProcess` resolved **no registry**,
  `ProcessRegistry.track` dropped the child on a silent `| None -> ()`, and `KillAll`
  truthfully reported nothing to kill. Every plugin child — test runners, file-command
  processes — outlived the daemon, reparented to init. Observed on a live daemon:
  `track pid=69166 -> NO REGISTRY (dropped)` / `KillAll: 0 tracked`. The registry is now
  constructed and installed by `createWith` **before anything captures a context**, and
  is passed into the daemon, which makes the correct ordering the only constructible one.
  This is what makes `fshw stop` *and* the wedge self-heal actually reap in-flight
  children instead of orphaning them.

- fix: **`ProcessRegistry.track` warns instead of silently dropping.** A child spawned
  with no registry in scope can never be reaped, so the miss is now logged loudly. A leak
  you cannot see is a leak you cannot fix — the silence above is precisely how this one
  survived.

- feat: **`FsHotWatch.ContentHash` — ONE hasher, ONE fail-closed policy.**
  Three hashers had grown independently, each with its own answer to the only question
  that matters: *what do you hash when you CANNOT READ the file?* There is one safe
  answer — a sentinel that will NOT match the hash of the same file readable — and it
  must be the same everywhere, or a claim can silently cover a file nobody looked at.
  `TreeHash` and the verdict's producer identity both route through it; the daemon's
  binary identity should adopt it on merge.

- feat!: **`FsHotWatch.Ctrf` — ONE DIRECTORY PER RUN.** Reports live in
  `.fshw/test-runs/<runId>/<Project>.ctrf.json`, and nothing else does — so **the
  directory IS the run**. A flat shared pile was unreadable in BOTH directions and both
  directions bit us on the same day: PRESENCE was ambiguous (nine files, and nothing
  saying which belonged to the run you just did — the only way to answer was forensics
  on mtimes), and ABSENCE was ambiguous (an empty listing could mean "no tests ran" —
  the single most important fact an agent can learn — or "cleaned up", or "wrong glob").
  Two capable readers guessed, an hour apart, and both guessed wrong.
  - The run-dir is created BEFORE anything executes, so a run that executed and reported
    nothing leaves an EMPTY DIRECTORY — a stated fact — while a run that never happened
    leaves none at all. **Absence stops being something the reader has to decode.**
  - `reportsForRun` replaces any mtime-window heuristic: membership is DECLARED.
  - `tidyRunsDir` rotates whole RUN DIRECTORIES (newest 10) and purges the older flat
    layout. History is evidence — old runs are rotated, never wiped on start.

- feat: **`FsHotWatch.TreeHash` — the content address of the tree fshw verifies.**
  `TreeHash.compute repoRoot excludePatterns` hashes every file under the discovery roots
  that is not build output, tooling state, or config-excluded — **sources AND
  content/fixture files** — plus `.fshw.json` itself, by CONTENT (never mtime, per
  ADR-008). This is what a verdict is addressed by, so that "a green from a different
  tree" becomes detectable rather than silently reusable. The recipe
  (`fshw-tree-sha256-v1`) is a documented contract: `relPath + NUL + sha256hex(bytes) +
  LF` per file in ordinal path order, SHA-256 over the whole. Fixtures are in the hash on
  purpose — a changed JSON fixture that MSBuild declined to re-copy once let a suite run
  green against the OLD fixture and put a red commit on `main` for hours.

- feat: **`FsHotWatch.Ctrf` — one CTRF reader, and reports that are RETAINED.**
  The summary parser the verdict layer, the flakiness recorder and `.fshw/verdict.json`
  all read a report through, so they cannot disagree about what it says. Plus report
  discovery (`reportsSince` — the reports a given run produced, never "the newest file in
  the directory") and `tidyRunsDir`, which bounds retention to the newest few per project
  and purges the DEAD `.log` format.

- feat: `FsHwPaths.configFile` / `ConfigFileName` — `.fshw.json`'s path, named in one
  place. It is an input to the tree hash, and a second spelling is a way for that to stop
  being true.

- fix!: **a refused `RunExclusive` claim is a value you cannot drop.**
  `PluginCtx.RunExclusive` returned `unit` and silently discarded the work when the
  slot was held. The caller could not tell — so a force-run whose claim was refused
  reported success having executed nothing, and a reply resolved *inside* the dropped
  work never resolved at all. It now returns `RunClaim = Claimed | SlotBusy`, which
  (with `TreatWarningsAsErrors`) the caller must handle: skip with a stated reason, or
  queue. The repo's own `FSHW-CLAIM-001` analyzer rejects `|> ignore`-ing it.
  - **BREAKING:** every `ctx.RunExclusive` call site must handle the returned `RunClaim`.

- fix!: **the framework reports `Running` at the claim**. A launched run
  that nobody can see as `Running` is now unrepresentable: `runExclusive` reports it
  itself. (`CoveragePlugin` shipped exactly that gap — it rendered `✓` while running,
  and because the host's work-cycle generation only advances on a `Running` transition,
  its generation never moved, so `WaitForComplete` could never take its fast path while
  coverage was registered.) The hand-written `ReportStatus(Running)`-before-`RunExclusive`
  pairs are gone.

- fix!: **every terminal carries its verdict; `RunVerdict` refuses to be empty.**
  `Failed` is now `Failed of error * at * verdict: RunVerdict` — the
  error is the *diagnosis*, the verdict is the one-line summary + the measured duration.
  The old shape fell back to `startedAt = at` when no `Running` preceded a `Failed`,
  recording an elapsed of ZERO — i.e. the "`started:` with no `elapsed:`" render that is
  this bug's own diagnostic tell survived on the `Failed` path. `RunVerdict` is now a
  private-field type whose only constructor (`RunVerdict.create`) rejects an
  empty/whitespace summary, so no site — daemon, cache deserializer, CLI, test helper or
  example — can build a content-free `✓`.
  - **BREAKING:** `ReportStatus(Failed …)` takes a verdict; `CompleteWithSummary` and
    `PluginHostServices.SetSummary` are DELETED (the status is the summary channel).
    `PluginCtxHelpers.failedWith` is the counterpart to `completeWith`.

- fix: **terminal-stamp ownership is enforced at the framework's one funnel**, not
  re-implemented per plugin. While an exclusive run is in flight it OWNS
  the plugin's status; any *other* terminal — from a per-file handler, a cache replay, or
  the handler-crash net — is dropped. Previously three hand-written
  `if not (ctx.IsRunning "tests")` guards in TestPrune enforced this for one plugin only,
  keyed by a slot name the framework didn't know: the same duplication class that caused
  the bug. A suppressed terminal is now also barred from the task cache, so it cannot be
  replayed later as a verdict no run produced.

- fix!: **IPC commands get a narrow `CommandCtx` — they can observe and `Post`, never
  launch work.** `PluginHandler.Commands` received the full `PluginCtx`
  and ran on the IPC thread, outside the mailbox and outside the busy accounting — the
  exact capability that caused this bug, and one that had a second live user
  (`coverage-ratchet` rewriting its config file while a check might be reading it).
  `CommandCtx` exposes `RepoRoot` / `Log` / `Post` / `IsRunning` / `ProjectGraph` and
  nothing else, so `Post` is the only expressible way for a command to cause work.
  - **BREAKING:** command handlers take `CommandCtx<'Msg>` instead of `PluginCtx<'Msg>`.

- feat: repo-local convention analyzers (`analyzers/FsHotWatch.Rules`, loaded by
  `check`/`confirm` via `.fshw.json`): `FSHW-CLAIM-001` (a `RunClaim` must never be discarded) and
  `FSHW-CLOCK-001` (no `DateTime.Now` — every daemon timestamp is UTC). Both fire in CI
  and are pinned by positive *and* negative controls.

- fix!: **`Completed` carries its verdict — a guard that cannot say what it measured
  has not measured anything.** `PluginStatus.Completed` is now
  `Completed of at * verdict: RunVerdict` where `RunVerdict = { Summary; Elapsed }`,
  so a plugin physically cannot report "done" without stating what it did and how
  long it took. This kills the manufactured "✓ with `started:` but no `elapsed:`"
  terminal — the signature of a status stamped over a live run — at the type level,
  and collapses the summary side-channel: the host routes `verdict.Summary` into the
  run record, so the status and the history can never disagree.
  - **BREAKING:** every `ReportStatus(Completed …)` site must supply a `RunVerdict`;
    `PluginCtxHelpers.completeWith` now takes the elapsed duration.
  - The run record's elapsed is the verdict's sworn duration (startedAt derived as
    `at - Elapsed`); the old fallback that recorded a ZERO elapsed for a Completed
    with no preceding Running is gone.
  - Cache replay keeps the ORIGINAL verdict and marks it `(cached)`; pre-verdict
    on-disk cache entries are rejected as misses (no evidence to replay).
  - Preprocessor completions now carry a verdict (files checked / rewritten + duration).
- fix!: **a plugin with work in flight is BUSY, full stop — one counter, no hand-off
  gap.** An exclusive `RunExclusive` run now holds a token in the
  SAME `inflightCount` that counts mailbox events, from claim until AFTER its
  completion message is posted. The previous `inflight > 0 || anyRunSlotBusy()`
  composite read two atomics at different instants, and the slot-release →
  completion-post hand-off had a window in which a verdict-waiting `check` could
  observe "at rest" while the run's verdict was still in flight.
- fix: a handler that throws while an exclusive run is in flight no longer stomps a
  forced `Failed` over the live run's `Running` (the run's completion path is
  guaranteed to deliver the earned terminal status); the crash is still logged.
  With no run in flight the forced `Failed` stands, as before.

- feat: `ErrorLedger.ErrorEntry.warningWithDetail` — a Warning-severity entry with a
  detail body, the sibling `errorWithDetail` never had. For conditions that deny a
  clean verdict under the default warn-fail policy without themselves being a failed
  check: the first is a source file the symbol analyser could not read, which leaves a
  hole in the impact graph the gate must not silently paper over.

- fix!: **ONE spawn primitive, bounded at both ends.** There were TWO —
  `runProcessWithTimeout` and `runProcessWithLaunchWatchdog` — and every caller
  except TestPrune used the unsafe one, so the two wedges the watchdog was built
  to close stayed wide open in Build / FileCommand / DepsFreshness / hooks: a
  single blocking `WaitForExit(-1)` that a machine sleep turns into a permanent
  wait, and an UNBOUNDED `Task.WaitAll` drain **on the success path** that never
  returns when a child exits while a GRANDCHILD (an MSBuild node, a Playwright
  driver) still holds the inherited stdout pipe — EOF never comes, and that is
  the 16 h wedge. Both are now collapsed into `ProcessHelper.runProcess`, which
  ALWAYS polls `HasExited` and ALWAYS bounds the post-exit drain.
  - **BREAKING:** `runProcessWithTimeout` and `runProcessWithLaunchWatchdog` are
    gone; `runProcess` takes a `ProcessBounds` instead of a bare `TimeSpan`.
  - `ProcessBounds` is constructed only via `ProcessBounds.streaming` (a child
    whose first byte proves liveness — a test runner) or `ProcessBounds.silent`
    (a child that may print nothing for its whole run — `dotnet build -v q`, a
    buffering `sh -c` wrapper — for which a launch deadline would false-kill a
    healthy slow build, so a finite timeout is the bound). Its fields are
    private, so a call site cannot assemble "no bound at all" out of two
    `InfiniteTimeSpan`s.
- fix!: **`WaitForScan` could reproduce the 8h36m wedge exactly.** It had no
  deadline: `WaitForScanGeneration` raced only daemon *shutdown*, never a clock;
  the CLI passes `-1L` (including on every convergence re-scan); and it is
  `check`'s FIRST step. Any hang inside `performScan` — a Fantomas preprocessor,
  an Ionide design-time evaluation, an FCS check — meant "Scanning…" forever: no
  timeout, no error, no verdict. The deadline is now enforced at the
  `trackedTask` SEAM, so EVERY bracketed RPC is bounded by construction rather
  than one method at a time (bounding them one at a time is how you get the
  second `WaitForScan`). An RPC that bounds itself more precisely —
  `WaitForComplete`, which names the still-running plugins — still wins the race.
- fix!: **the operation watchdog went blind under concurrency — the one thing it
  existed for.** It tracked a SINGLE in-flight op while the IPC server runs three
  acceptors by design: a second `Begin` overwrote the first's record and the
  first `End` erased the second's. Two parallel `fshw check` clients were enough,
  and a genuinely wedged op then heartbeat as `idle` with `WedgeReport() = None`.
  Ops are now keyed by an `OpToken` minted at `Begin` and retired at `End`, and
  the report names the OLDEST overrunning op.
  - **BREAKING:** `Watchdog.Begin` returns an `OpToken`; `Watchdog.End` takes one.
    `WatchdogState.InFlight` is an `InFlightOp list`, not an `InFlightOp option`.
- fix!: **the on-disk FCS check cache was a structural no-op — removed.**
  `FileCheckCache.TryGet` always reconstructed `CheckResults = ParseOnly` (FCS
  types aren't serializable), and `ParseOnly` is exactly what
  `CheckPipeline.tryGetCachedFullCheck` treats as a MISS. It could not hit —
  ever, by construction, on any input — while writing and enumerating a dead JSON
  file per checked file per daemon restart (1,051 measured in one repo). It was
  also the DEFAULT.
  - **BREAKING:** the `FsHotWatch.FileCheckCache` module and the
    `CacheBackendConfig.FileBackend` case are gone, as is
    `detectDefaultCacheBackend`. The default is now `NoCache` — which is what the
    file backend already did, minus the dead I/O. `"cache": "file"` / `"jj"` in
    `.fshw.json` is REJECTED with a loud config warning naming the removal, not
    silently accepted as a setting that does nothing. `"cache": "memory"` is
    unaffected and really does cache.
- fix: the on-disk **task cache grew without bound**. Entries are named
  `{plugin--file}@{contentHash}.json` so "multiple versions coexist", but only
  the entry matching the CURRENT content is reachable (`tryGet` reconstructs the
  exact path), so every edit permanently added a dead sibling: 3,126 files /
  13 MB in a ~1.5-day-old workspace, while `Stats`/`clearFile`/`clearPlugin`
  full-scan the directory. A write now collects its superseded siblings. No LRU —
  an LRU would retain entries that are not merely cold but unreachable.

- fix!: **the daemon can no longer wait forever.** A client-unbounded
  `WaitForComplete` (what `fshw check` issues) used to resolve to
  `TimeSpan.MaxValue` — a literally infinite wait. When a plugin wedged, the
  daemon heartbeat-logged `in-flight WaitForComplete running Ns` indefinitely
  and the gate never returned (observed: 8h36m, silent, no error, no timeout).
  The daemon now always applies a hard deadline (`Ipc.resolveVerdictDeadline`,
  default 60 min, override `FSHW_VERDICT_DEADLINE_SEC`; there is deliberately no
  "infinite" setting). On breach it raises a `TimeoutException` NAMING the
  still-running plugin and its elapsed time, which the CLI renders as a
  diagnostic exit 2 plus the recovery path, instead of hanging.
- fix: `SafeWalk` — a new symlink-safe, depth-bounded directory walker, now THE
  walker for every repo-scale enumeration (`Discovery`, `Watcher`, plus the CLI
  and TestPrune plugin walks). It never descends a symlinked directory, so
  termination is structural. Both `Directory.GetDirectories`-plus-recursion and
  `SearchOption.AllDirectories` follow directory symlinks into cycles: on a
  devenv/nix repo, `.devenv/profile` links into `/nix/store`, whose reachable
  tree has TWO self-loop symlinks in one directory
  (`ncurses-6.6-dev/include/{ncurses,ncursesw} -> .`). That branches — every
  level doubles the path count — so within the kernel's ~32-symlink ELOOP
  envelope there are ~2^32 paths to enumerate (measured with
  `AllDirectories`: 800k+ files in 52s, still climbing). This is what wedged
  the gate.

## 0.10.0-alpha.2 - 2026-07-08

- fix: a compile-item-only `.fsproj` edit no longer wedges the deps-freshness
  gate red. The gate's mtime fast-path read an added/reordered `<Compile>` item
  as a stale restore; on a memory-pressured box where the phantom restore timed
  out, the debounce tracker kept the project pinned Stale (deps RED) on every
  subsequent cycle until the daemon restarted. The mtime probe is now backed by
  a CONTENT signature over ONLY the dependency-declaring inputs — the fsproj's
  `PackageReference` / `ProjectReference` / `Import` / `Sdk` / target-framework
  subset (source items like `<Compile>` are excluded), plus the bytes of every
  governing `Directory.Packages.props` / `Directory.Build.props` / `paket.lock` /
  `paket.dependencies`. A compile-item-only edit leaves that signature unchanged
  so the phantom Stale is recognised and suppressed; a real package-graph change
  still moves it and re-arms recovery. See
  `docs/adr-008-mtime-is-not-a-content-oracle.md`.

## 0.10.0-alpha.1 - 2026-07-05

- feat: `ProcessHelper.runProcessWithLaunchWatchdog` — run a child under a
  **launch-liveness watchdog** that can never block forever. `runProcessWithTimeout`
  bounds only the *total* run via one `WaitForExit`, which is INFINITE for a
  caller that passes no timeout; if the spawned child never becomes a live,
  progressing process — an overloaded box where it never appears, or a machine
  sleep that kills it mid-launch — that wait hangs forever. The watchdog polls
  liveness instead and enforces a bounded *launch* deadline: the window in which
  the child must show its FIRST sign of life (any output, or an exit). Once it
  does, the wait is unbounded again, so a slow-but-progressing suite streaming
  output is never launch-killed — the deadline governs launch, not total
  duration. Only a **stall** (no life within the deadline) kills the tree and
  raises `LaunchStalledException`; a child that EXITS is classified normally by
  its exit code (a nonzero exit with no output is a genuine failing / zero-match
  test, indistinguishable from a spawn-death, so it is never force-aborted). The
  machine-sleep case is closed instead by (a) polling `HasExited` so the exit is
  observed at all, and (b) a **bounded** post-exit drain — an unbounded
  `WaitForExit` blocks forever if a grandchild (an MSBuild/vstest node) inherits
  the stdout pipe and outlives the child, which is the actual 16 h wedge. The
  pure decision (`decideLaunchStep`) and injectable loop
  (`launchWatchdogLoopWith`) are deterministically testable, mirroring
  `waitForDaemonReadyWith`. (QA finding: the launch gap)

## 0.9.0-alpha.1 - 2026-07-03

- fix: a faulted exclusive run (`RunExclusive` build/coverage/tests slot) can no longer strand its plugin in `Running` — the fault branch now forces a terminal `Failed` status, so a client `WaitForComplete` gets a prompt non-zero verdict instead of waiting forever (the fresh-workspace "test run never launches" wedge).
- fix: the idle-exit can no longer fire while a client verdict-wait is in flight — active `WaitForAllTerminal` waits now count as busy via `IdleExit.busyForIdleExit` (previously the daemon shut down mid-`check` after 30 min, dropping the client with a connection error).

## 0.8.0-alpha.34 - 2026-07-02

- feat: `PathFilter.isOutsideRepo` — true when a path resolves outside the repo root (a rooted or `..`-prefixed relative path), e.g. a NuGet-injected `_content` compile item under `~/.nuget`. `isExcludedPath`'s out-of-repo test now shares this check. `PathFilter.isOutsideRepoScoped` lifts it over an optional repo root (`None` = include everything) — the shared predicate the analyzers + lint plugins use for the `includeOutsideRepo` skip.

## 0.8.0-alpha.33 - 2026-06-24

- chore(deps): bump `System.Security.Cryptography.Xml` 10.0.9; suite-wide dependency refresh (version-coupled with the cli/build release).

## 0.8.0-alpha.32 - 2026-06-19

- feat: a new `TestResult.TestsErrored` case (plus `TestResult.isErrored`) for a
  test run that aborted before producing a usable result — not a pass and not a
  failure. The TestPrune gate uses it to surface an honest "errored" diagnostic
  instead of a misleading test failure. Note: exhaustive matches on `TestResult`
  in downstream code now need an arm for it.

## 0.8.0-alpha.31 - 2026-06-17

- docs: README accuracy & early-alpha status-note pass (no functional changes).

## 0.8.0-alpha.30 - 2026-06-16

- feat: `PluginCtx.ProjectGraph` exposes a read-only project-graph accessor
  (`GetAllProjects` / `GetTransitiveDependentProjects` / `GetProjectReferences`
  / `GetCanonicalDllPath`) to plugins. The daemon installs the live graph before
  registering plugins (`PluginHost.SetProjectGraph`); tests and the null-checker
  daemon get a no-op accessor. Enables the TestPrune plugin's dependency-
  fingerprint fanout.

## 0.8.0-alpha.29 - 2026-06-15

- feat: FSEvents watch latency is now configurable (was a hardcoded 50ms).
  `MacFsEvents.create` / `createWithCoalesced` and `FileWatcher.create` take an explicit
  `latencySeconds`, threaded from the new `DaemonOptions.FsEventsLatencySeconds` (default 0.25s).

## 0.8.0-alpha.28 - 2026-06-12

- fix: `ContentDedup` is now scoped per daemon instance, so two daemons sharing a
  machine can no longer poison each other's content-hash cache (a change seen by one
  daemon was wrongly deduplicated as "already seen" by another).
- fix: `PluginHost.setStatus` is non-blocking, closing a latent deadlock where a plugin
  setting its status from an agent thread could stall the host.
- fix: the IPC accept loop now logs faults instead of silently swallowing them, so a
  failing client connection surfaces in the daemon log rather than vanishing.
- fix: `FilePattern.parse` rejects globs it cannot match consistently rather than
  accepting a pattern that would silently never fire.
- refactor: discovery roots now have a single source of truth and the fingerprint honors
  configured excludes, so excluded paths no longer perturb change detection.
- chore: float `FSharp.Compiler.Service` via a `43.*` wildcard so restore resolves the
  latest published 43.x.
- fix(deps): lift transitive `MessagePack` to 2.5.301 (GHSA-hv8m-jj95-wg3x).

## 0.8.0-alpha.27 - 2026-06-11

### Changed

- `DepsFreshness.productionRestoreRunner` now composes a pure, internal `restoreSteps` plan
  with a thin `runRestoreSteps` executor (behavior, ordering, and per-step timeouts unchanged).
  `restoreSteps repoRoot fsproj` returns the ordered list of `RestoreStep` records (`Purpose`,
  `Args`, `WorkingDir`) — `dotnet restore` first, then one `dotnet paket restore --group <g>`
  per group enumerated from the in-scope `paket.lock` (via `paketGroupsFromLock`, falling back
  to `Main` when no lock is found), then `dotnet tool restore` when a `.config/dotnet-tools.json`
  is in scope. Extracting the branchy "which steps, in what order, with what args" decision from
  the process shell-out makes it unit-testable without invoking `dotnet`/`paket`; the
  DepsFreshness.fs coverage floor rose accordingly (macOS line 72→91, branch 46→83).

## 0.8.0-alpha.26 - 2026-06-10

### Changed

- Extracted the post-kill output-drain tail into an internal `ProcessHelper.drainedOrEmpty`
  helper (behavior unchanged). Part of making unit-suite coverage deterministic under machine
  load: the two real-subprocess plugin timeout tests moved to the coverage-excluded integration
  suite and ratchet floors are settled to stable actuals.

## 0.8.0-alpha.25 - 2026-06-10

### Fixed

- Deps-freshness recovery no longer runs a bare `dotnet paket restore`. Bare `paket restore` is paket's full-repo `AllProjects` mode: it walks every project directory (`FindAllProjects`) to inject references, following directory symlinks with no cycle detection. A self-referential symlink loop — e.g. the macOS-SDK `ncurses` links inside a Nix `.devenv` profile — made it recurse forever, wedging the restore until the per-step timeout and failing the deps gate. Recovery now restores **per group** (`paket restore --group <g>`, enumerated from `paket.lock` via the new `paketGroupsFromLock`): passing an explicit group makes paket skip the project-discovery walk while still restoring that group's sources/git-dependencies. Per-project reference injection (for repos using `paket.references`) is already handled by the preceding `dotnet restore <fsproj>` step via `Paket.Restore.targets`' `paket restore --project`.

## 0.8.0-alpha.24 - 2026-06-08

### Performance

- The plugin dispatch loop computes each event's task-cache key once and threads the single value to both the cache lookup (`tryReplayCache`) and the store (`runAndCache`). Previously `cacheKeyFn event` was called twice per dispatch; for BuildPlugin that key is a full content-hash of the project graph, so a cache miss paid two SHA-256 passes per trigger. Threading one value also guarantees the lookup key equals the store key by construction.

### Fixed

- `WaitForComplete` no longer reports a vacuous clean on a cold / never-ran daemon. The daemon-side wait (`waitForAllTerminalCore`) gained a `requireVerdict` guard: on the `WaitForComplete` RPC path (`waitForVerdict`), the host is only considered at rest once at least one plugin has reached a real terminal state (Completed/Failed) — an all-Idle host (registered plugins, nothing run/verified) now keeps blocking until a real verdict instead of resolving immediately via the quiescence leg. All other callers (`RunOnce`/scan-settling via `waitForAllTerminal`) keep the Idle-tolerant behavior so they can never hang on a legitimately never-run plugin.
- Deps-freshness signature is now content-hashed instead of keyed on max dep-file mtime ticks. A preserved-mtime dep-file rewrite (`rsync -a` / `cp -p` / branch-switch over `paket.lock` / `Directory.Packages.props`) previously left the signature byte-identical, so debounced restore recovery never re-armed. `evaluateProject` also gains a content-drift cross-check: an mtime-`Fresh` verdict whose content signature drifted from the last fresh baseline is re-restored rather than silently proceeded. `staleSignature` return type changed `int64 → string`; `RecoveryTracker` gains `HasContentDrifted`/`RecordFreshSignature`. See `docs/adr-008-mtime-is-not-a-content-oracle.md`.
- The daemon's blocking waits (`waitForAllTerminal` and the IPC `WaitForScanGeneration`) now observe the daemon's shutdown `CancellationToken`. Previously, if the daemon was torn down (`fshw stop`, idle-exit, or any `cts.Cancel()`) while an RPC was blocked mid-wait, the wait could resolve cleanly during teardown and the in-flight `check` / `WaitForComplete` / `WaitForScan` RPC would falsely report **success** (or, for in-process callers, hang). The waits now fault with `OperationCanceledException("daemon shutting down")` the moment the shutdown token fires, so the in-flight RPC propagates a failure to the client instead of a false green.

### Changed

- `status`, `WaitForScan`, and the daemon logs now report **live** completeness — registered files minus the ones currently lacking a valid full type-check — instead of a frozen scan-end snapshot. They are computed from the same coverage signal `fshw check` uses, so the `complete:`/`incomplete:` line always agrees with `check` and no longer rots when an incremental edit + re-check fixes a file after the scan finished. Mechanically, `ScanState.ScanComplete` dropped its `total`/`unchecked` snapshot counts (it now only carries `elapsed`); both consumers read one shared `liveCoverage` (registered minus checked) at request time. The rendered string formats are unchanged.

### Added

- feat: `fshw check` now converges on incompleteness instead of reporting it. After the daemon settles, `check` reads diagnostics **and a live coverage signal** (how many registered files currently lack a valid full type-check result). If failures are found it short-circuits to **exit 1** immediately (real problems are reported now, not re-scanned away). If coverage is complete and clean → **exit 0**. If the check is incomplete but otherwise clean, `check` tries to **fix** it: it forces a bounded series of re-scans (up to 3), re-reading coverage each time, and stops early on completion, on a newly-surfaced failure, or when the unchecked count stops shrinking. Only a genuinely un-completable check returns the new **exit 2**. Structurally, the verdict is a total function over an explicit outcome type and coverage is a *required* input: a daemon that doesn't report coverage (old build / parse gap) is treated as `Unknown`, which enters convergence and can never read as a false green 0. The daemon-side signal is a live "checked files" set in `PluginHost` (a full check via `EmitFileChecked` adds a file; a file change via `EmitFileChanged` removes it), exposed over IPC as an `unchecked` field on the check response.

- feat: memory pressure shortens idle-exit (`pressureIdleFloorMin` in `.fshw.json`). When a daemon is already idle-exit-eligible (a `/.workspaces/` checkout in AUTO mode, or an explicit `idleExitMin N`) AND the machine is under memory pressure, the effective idle window is shortened to `min(idleExitMin, pressureIdleFloorMin)` so a tight machine sheds idle daemons fast — a 30-min workspace daemon quits after 2 min idle under pressure. Pressure is the GC's own high-load mark (`GC.GetGCMemoryInfo().MemoryLoadBytes >= HighMemoryLoadThresholdBytes`), re-evaluated each 30s tick (if pressure subsides the full window is restored). The default/main workspace stays **exempt**: pressure only shortens an already-applicable window, it never creates one. Absent → floor at `2` min; `0`/`false` → pressure-shortening disabled; positive `N` → floor at `N` min. This replaces the earlier (unreleased, same-session) `pressureTrimPct` in-place cache-trim, which was reversed before release: trimming kept ~400 MB plus the process resident yet still forced a cold FCS rebuild on the next edit (the file-backed CheckCache survives both trim and quit), so quitting dominates. See ADR-005.

- `ErrorLedger` now accepts an optional `logError: string -> string -> unit` sink
  (defaults to the process-global `Logging.error`/stderr) used when a reporter throws.
  The failure log is emitted on the ledger's MailboxProcessor agent thread, so a
  `Console.Error` capture in tests raced any concurrent `Console.SetError`; the
  injectable sink lets callers (notably tests) observe reporter failures without
  touching process-global console state. No behavior change at the default.
## 0.8.0-alpha.23 - 2026-06-07

- chore: republish so the bundled `fshw` carries the CLI's dead-code schema-probe fix
  (now sourced from TestPrune.Core's public `Database.SchemaVersion`, 4.2.1). Also folds
  in an internal `ProcessHelper` refactor unifying the child-env sanitization lists into
  one `sanitizedChildEnvKeys`. No core API change.

## 0.8.0-alpha.22 - 2026-06-07

- feat: idle-exit — a daemon quits gracefully after a configurable idle period, freeing
  100% of its footprint; the next `fshw` command auto-restarts it (the file-backed check
  cache survives, so the rescan is mostly cache hits). `idleExitMin` in `.fshw.json`:
  absent → AUTO (30 min, but only for `/.workspaces/` checkouts — the default/main
  workspace never auto-quits); `0`/`false` → disabled; positive `N` → `N` min in any
  workspace. See docs/adr-004. (Entry restored 2026-06-07: shipped in alpha.22 but the
  CHANGELOG line was lost in a merge.)
- fix: spawned `dotnet build` (and any other child process) no longer inherits the
  `MSBUILD_EXE_PATH` / `MSBuildExtensionsPath` / `MSBuildSDKsPath` variables that
  Ionide.ProjInfo writes into the daemon's own environment during in-process project
  evaluation. On a multi-SDK machine those leaked vars pinned the child's MSBuild to a
  different (or incomplete) SDK band than the muxer resolved, so the implicit restore
  failed with exit 1 and zero diagnostics — surfacing as the long-standing
  `fshw check --run-once` "Build FAILED / 0 Error(s)" while a plain-shell `dotnet build`
  of the same tree was clean. `ProcessHelper.runProcessWithTimeout` now strips these keys
  before every spawn (same treatment as the arch-specific `DOTNET_ROOT_*` keys);
  caller-supplied overrides still win. See docs/leaked-msbuild-env-bug.md.
- fix: the deps-freshness gate no longer counts .config/dotnet-tools.json as a dependency input — a dotnet-tool bump no longer false-stales every project into restore-recovery/skipped scans

## 0.8.0-alpha.21 - 2026-06-06

- feat: `fshw dead-code` — runs TestPrune's unreachable-symbol analysis against the daemon's .fshw/test-impact.db (same semantics as the standalone test-prune CLI: --entry/--include-tests/--verbose), no DB copying needed.
- feat: the CLI now ships `System.GC.ConserveMemory=9` in its runtimeconfig, cutting the
  daemon's steady memory footprint ~25–40% (benchmarked: settled ~3.0 GB vs 3.9–4.4 GB,
  peak 5.0 vs 5.9–7.8 GB against a 32-project solution) at no scan-speed or diagnostics
  cost. Override per-process with the `DOTNET_GCConserveMemory` env var. Also dropped the
  dead `projectCacheSize` arg (ignored by the TransparentCompiler). See docs/adr-003.
- fix: cold scans no longer silently truncate. A build touching `obj/**/ref/*.dll` could
  cancel in-flight scan checks (`CancelPreviousCheck`); cancelled checks surfaced as
  `None` and were dropped, so a scan could report green with a shrunken diagnostic set.
  The scan now retries cancelled/aborted checks (bounded) and surfaces any still-unchecked
  count as a non-ok `incomplete:` condition in status + the scan log line.
  (Both entries restored 2026-06-07: shipped in alpha.21 but the CHANGELOG lines were lost in a merge.)

## 0.8.0-alpha.20 - 2026-06-05

- chore: bundle FsHotWatch.TestPrune 0.7.0-alpha.19 (clears the FCS check-cache after a
  schema-bump DB recreate, so the symbol graph re-indexes fully instead of staying
  partial). No core API change; republished so the bundled tool ships the fix.

## 0.8.0-alpha.19 - 2026-06-04

- chore: bundle FsHotWatch.TestPrune 0.7.0-alpha.18 (cold-start coverage no longer
  clobbers prior coverage while the symbol graph is still indexing). No CLI API change;
  republished so `dotnet fshw` carries the fix.

## 0.8.0-alpha.18 - 2026-06-04

- chore: bundle the DB-backed coverage plugins (FsHotWatch.TestPrune 0.7.0-alpha.17,
  FsHotWatch.Coverage 0.7.0-alpha.11). No CLI API change; republished so the `fshw`
  tool's bundled plugins carry TestPrune-native single-source coverage.

## 0.8.0-alpha.17 - 2026-06-03

- feat: auto-recovering deps-freshness gate before FCS analysis. When a project's `obj/project.assets.json` is stale relative to its declared deps (a `PackageReference` added without `dotnet restore`, or a half-completed restore), the daemon detects it *before* type-checking, attempts a one-shot `dotnet`/`paket`/`tool restore` to recover automatically, and only if recovery fails reports a single actionable `deps` diagnostic — instead of letting FCS emit a phantom "namespace/type not found" error-storm. Detection and orchestration are pure and unit-tested (`FsHotWatch.DepsFreshness`: `compareFreshness`, `dependencyFiles`, `evaluateProject`, `RecoveryTracker`); each restore step is bounded by a 5-minute timeout so a hung restore fails fast rather than wedging the scan.

## 0.8.0-alpha.16 - 2026-06-02

- feat: TestsDeferred result case — a test project that never ran (apphost not yet produced) is reported as deferred ("waiting on build"), non-passing, instead of a false-green pass; the aggregate verdict/exit code can no longer be green when tests didn't actually run.
- fix: a throwing IErrorReporter no longer yields a false-clean verdict — the ledger self-reports the reporter failure so the verdict/exit code reflects that diagnostics couldn't be recorded.

## 0.8.0-alpha.15 - 2026-05-28

- fix: `FileErrorReporter` caps oversized `message`/`detail` fields before serialization to avoid `System.Text.Json` transcode `OverflowException` (FR `fr-fileerrorreporter-overflow.md`).

## 0.8.0-alpha.14 - 2026-05-26

- feat: daemon auto-refreshes FCS state on `.fsproj` / `obj/project.assets.json` changes — adding a `PackageReference` + `dotnet restore` now resolves without a daemon restart or a `.fs` save (FR `docs/fr-auto-refresh-fsproj-changes.md`). `Watcher.classifyChange` routes `obj/project.assets.json` through `ProjectChanged` (recovers the `.fsproj`-edit-races-`restore` window), and `Daemon.processBatch` re-checks the affected project's source files after re-discovery.
- feat: scoped FCS invalidation — a single project change invalidates only that project plus its transitive dependents (`Daemon.resolveAffectedProjects`), keeping unrelated projects' warm FCS state and cached check results instead of cold-starting the whole solution. Repo-wide changes (`.props`, solution edits, a brand-new project) fall back to full re-discovery.
- feat: `CheckPipeline.PrepareForRediscovery` gained an optional `?clearCheckCache` parameter (default `true`); the scoped project-change path passes `false` to retain unrelated projects' cached check results across re-discovery.

## 0.8.0-alpha.13 - 2026-05-04

- fix: `Daemon.DaemonOptions.FcsSuppressedCodes = None` now resolves to an empty `Set<int>` instead of `Set.ofList [ 1182 ]`; projects that need FS1182 silenced should declare `<NoWarn>FS1182</NoWarn>` in their fsproj
- feat: `Daemon.resolveFcsSuppressedCodes : int list option -> Set<int>` — public helper exposing the option→Set resolution so it is directly testable

## 0.8.0-alpha.12 - 2026-04-29

### Added

- **`FsHotWatch.ErrorLedger.DiagnosticSeverity.order`** — total order on `Error/Warning/Info/Hint` for severity-threshold comparisons.
- **`FsHotWatch.CheckCache.DiagnosticSignature`** record (`StartLine/StartColumn/ErrorNumber/Severity/Message`) and **`hashDiagnosticSignatures`** — extracted from `fcsCheckSignature` so the hashing/sorting logic is unit-testable without a live `FSharpCheckFileResults`.
- **`FsHotWatch.FileTaskCache`** — atomic on-disk writes (write-temp-then-rename) and startup size telemetry logging total entry count and on-disk bytes.
- **`IProjectGraphReader.GetTargetFramework`** + **`ProjectGraph.GetTargetFramework`** — exposes the first `<TargetFramework>` (or first entry of `<TargetFrameworks>`) parsed from each .fsproj at registration time. Avoids re-opening + re-parsing the .fsproj from downstream consumers. **`extractTargetFramework`** is the underlying pure XDocument-taking helper.
- **`IProjectGraphReader.GetCanonicalDllPath`** — returns `<projDir>/bin/Debug/<TFM>/<projectName>.dll` (or `None` when TFM is missing). Centralises the canonical-DLL convention so consumers like `BuildPlugin.verifyArtifactsFresh` don't reinvent the path.
- **`IProjectGraphReader.GetMaxSourceMtime`** — newest `LastWriteTimeUtc` across a project's on-disk source files. Drives mtime-based artifact-freshness checks.

### Removed

- **`FsHotWatch.ProjectDirtyTracker` module.** The dirty-bit handoff between BuildPlugin and TestPrunePlugin is gone — staleness is enforced inline by BuildPlugin's post-build verification, so the heuristic dirty tracker has no consumers. Files using it: `markDirty`, `clearFreshProjects`, `isStaleProject`, the manual-run-tests deadlock workaround. See FsHotWatch.Build / FsHotWatch.TestPrune CHANGELOGs for downstream impact.

### Changed

- **BREAKING — `TestResult` DU widened with `elapsed: TimeSpan`.** All three constructors (`TestsPassed`, `TestsFailed`, `TestsTimedOut`) now carry a per-project wall-clock duration. `FileTaskCache` round-trips it via a new `elapsedSeconds` JSON field; older cached entries that omit the field deserialize as `TimeSpan.Zero` (no recorded duration). Pattern-match callers must add the new bind position. New `TestResult.elapsed` accessor is the recommended way to read it.

### Changed

- **`FsHotWatch.ErrorLedger.fromString`** now returns `DiagnosticSeverity option` instead of throwing on unknown severity strings. Callers that previously caught the exception should match on `None`.
- **`FsHotWatch.CheckCache.fcsCheckSignature`** guards `Unchecked.defaultof<FSharpCheckFileResults>` and other null cases — returns `"full-check-null"` / `"full-check-error"` instead of throwing.
- **`TimestampCacheKeyProvider.GetFileHash`** now hashes file content (SHA-256) instead of metadata (path + size + mtime). Closes a correctness gap where two files with the same size but different bytes (or same bytes with different mtime) would produce the wrong key. The name is preserved for backward compatibility; behavior matches the original "ls-tree merkle hash" design intent that was deferred at module creation.

### Removed

- **`JjCacheKeyProvider`** — was a stub that delegated to `TimestampCacheKeyProvider`. Its only role was as a marker for `Daemon.fs` to wire up `JjScanGuard` via runtime type-test.
- **`FsHotWatch.JjHelper` module** — `JjScanGuard`, `JjScanDecision`, `getWorkingCopyCommitId`, and `getChangedFiles`. The scan-skip-when-commit-unchanged optimization is gone. Plugin caches are content-addressed (post-§2a) and the FCS check-result cache now hashes file content directly; together they make the scan-skip path's marginal benefit negligible while removing the only jj runtime reliance.
- **`Daemon.DaemonOptions.EnableJjScanGuard`** — no longer needed; the option is dropped from the public surface.
- **`DaemonConfig.JjFileBackend`** variant — `"jj"` cache config string is still accepted as a legacy alias and falls back to `FileBackend`.
- **BREAKING — `force` parameter removed from scan API:** `Daemon.ScanAll(?force)` → `ScanAll()`, `DaemonRpcConfig.RequestScan: bool -> unit` → `unit -> unit`, `DaemonRpcTarget.Scan(force)` → `Scan()`, `IpcClient.scan pipeName force` → `IpcClient.scan pipeName`. The flag had been a no-op since `JjScanGuard` was deleted.

### Changed

- **`DaemonConfig.createCacheComponents`** return type went back from `(backend, provider, enableJjScanGuard)` triple to `(backend, provider)` pair (the third element was always paired with the now-removed `JjFileBackend`).
- **`DaemonConfig.detectDefaultCacheBackend`** now always returns `FileBackend` (kept for API compatibility; previously returned `JjFileBackend` when a `.jj/` directory existed).
- **`InitConfig.generateConfig`** signature dropped its `hasJj: bool` parameter.

## 0.8.0-alpha.11 - 2026-04-26

### Added

- **`FsHotWatch.ProcessHelper.isDotnetCommand`** and **`mergeDotnetEnv`** — public helpers that detect a `dotnet`/`dotnet.exe` command basename and merge `MSBUILDDISABLENODEREUSE=1` into its env (unless already set).
- `runProcessWithTimeout` now injects the env automatically for `dotnet` commands. Eliminates orphan `MSBuild.dll /nodemode:1` workers across daemon-spawned builds without per-plugin opt-in. See `docs/msbuild-node-reuse-bug.md`.
- **`FsHotWatch.PluginFramework.PluginCtxHelpers.reportOrClearFile`** — collapses the per-file `if entries.IsEmpty then ClearErrors else ReportErrors` idiom shared across analyzer-style plugins.

## 0.8.0-alpha.10 - 2026-04-25

### Added

- **`FsHotWatch.ProcessHelper.ProcessOutcome` DU** (`Succeeded of output` /
  `Failed of exitCode * output` / `TimedOut of after * tail`) replaces the
  historical `bool * string` return on `runProcessWithTimeout` / `runProcess`.
  Callers pattern-match instead of parsing a magic prefix from the output. Helpers:
  `isSucceeded`, `isTimedOut`, `outputOf`.
- **`FsHotWatch.ProcessHelper.WorkOutcome<'a>` DU** (`WorkCompleted of 'a` /
  `WorkTimedOut of after`) replaces `Result<'a, string>` on `runWithTimeout`.
- **`FsHotWatch.Events.TestResult.TestsTimedOut of output * after * wasFiltered`** —
  new variant distinguishing timeout-killed test runs from regular failures.
  `TestResult.isTimedOut` helper added; existing helpers updated to handle the
  new case. `FileTaskCache` round-trips it under the `"timed-out"` JSON tag.
- **`FsHotWatch.ProcessRegistry`** module — per-daemon, `AsyncLocal`-scoped
  registry of live `Process` handles. `Daemon.Dispose` calls `KillAll` so
  `dotnet fs-hot-watch stop` reaps in-flight test runners (and their playwright
  drivers etc.) instead of leaving orphans that contend with the next start.
  `runProcessWithTimeout` registers spawned children and unregisters in
  `finally`.

### Changed

- **BREAKING:** `runProcessWithTimeout` / `runProcess` return `ProcessOutcome`
  (was `bool * string`).
- **BREAKING:** `runWithTimeout` returns `WorkOutcome<'a>` (was `Result<'a, string>`).
- **BREAKING:** `FsHotWatch.ProcessHelper.TimedOutPrefix` literal removed.
  Pattern-match the new DUs.
- **Plugin status visibility sweep.** Plugins are now responsible for calling
  `ctx.CompleteWithSummary` explicitly at the end of each run; the framework
  no longer derives a summary from the last log line or the longest-running
  subtask. `IActivitySink` / `PluginCtx` gain `UpdateSubtask(key, label)` for
  in-place label updates on a long-lived primary subtask without churning
  state. The compact renderer now shows the `"primary"` subtask's descriptive
  label when present, instead of falling back to the activity tail.

## 0.8.0-alpha.9 - 2026-04-23

### Changed (breaking)

- **Test lifecycle events split into three**: `TestCompleted` is replaced by
  `TestRunStarted` (once per run, with `RunId` + `StartedAt`), `TestProgress`
  (per-group delta with `RunId` + `NewResults`), and `TestRunCompleted` (once
  per run, with final cumulative `Results` + `Outcome`). All three share one
  `RunId` per run. Subscribers that only care about end-of-run state read
  `TestRunCompleted.Results`; subscribers that want per-group progress consume
  `TestProgress` deltas and accumulate locally keyed by `RunId`.
- `PluginEvent` adds `TestRunStarted` / `TestProgress` / `TestRunCompleted`;
  drops `TestCompleted`.
- `SubscribedEvent` / `PluginDispatchEvent` gain matching variants; drop
  `SubscribeTestCompleted` / `DispatchTestCompleted`.
- `PluginCtx<_>` and `PluginHostServices` replace `EmitTestCompleted` with
  `EmitTestRunStarted` / `EmitTestProgress` / `EmitTestRunCompleted`.
- `TestResults` kept as a plain value type for internal TestPrune use; no
  longer dispatched as an event.

### Added

- `TestRunOutcome` DU (`Normal` / `Aborted of reason`). Per-project pass/fail
  lives in `TestRunCompleted.Results` (derivable from `TestResult` values).

### Fixed

- `FileCommandPlugin`'s `afterTests` trigger previously used a superset
  heuristic to detect batch boundaries and would silently skip every run
  after the first when project sets were identical (the dominant case for
  stable configs). Now keyed on `RunId`: fires exactly once per distinct run.

- chore: bump upstream tool versions

## 0.8.0-alpha.8 (2026-04-22)

### Added

- `TaskCache.saltedCacheKey` / `TaskCache.optionalSaltedCacheKey` — cache-key
  builders that fold a per-event salt into the commit-based key. Plugins whose
  cache validity depends on state beyond the commit (e.g. a config file whose
  edits don't change the commit) can salt with a hash of that state. Empty salt
  produces the pre-existing key format, so on-disk cache compatibility is
  preserved.

### Changed

- **BREAKING (IPC)**: `WaitForComplete` RPC now accepts a `timeoutMs: int` argument; `<= 0` means no client-imposed timeout. `DaemonRpcConfig.WaitForAllTerminal` signature changed from `unit -> Task<unit>` to `TimeSpan -> Task<unit>` so clients can pass their own deadline. The daemon's previous hard-coded 30-minute cap no longer applies when the client supplies a timeout.

### Fixed

- `PluginFramework.registerHandler`: when a handler's `Update` throws, the framework now auto-reports `PluginStatus.Failed(ex.Message, now)` in addition to logging. Previously an uncaught handler throw left the plugin in whatever transient status it had reported beforehand (classic case: TestPrune reports `Running`, hits a schema-drifted DB, never transitions further → UI shows "running" forever). This is a structural fix: no plugin author can forget to do it, and no plugin can leave an observable status non-terminal due to an exception inside its handler.

## 0.8.0-alpha.3 (2026-04-18)

### Added

- Project-discovery diagnostics: `Ionide.ProjInfo.IWorkspaceLoader.Notifications` is now
  subscribed during `discoverAndRegisterProjects` so per-project design-time failures
  (e.g. `ProjectNotRestored`, `ReferencesNotLoaded`) are logged instead of silently dropped
- Per-project FCS options dumped to `.fshw/logs/projinfo/<Project>.opts.txt` after every
  discovery pass. Contains source files, `OtherOptions` (incl. `-r:` references), and
  referenced project outputs. Registration log line now includes the `-r:` reference count
- `FSHW_PROJINFO_BINLOG=1` env var enables MSBuild binary-log capture at
  `.fshw/logs/projinfo/binlogs/<project>.binlog` for diffing design-time eval vs `dotnet build`
- `PathFilter` module with shared path filtering utilities:
  - `isGeneratedPath` — checks if a path is inside obj/ or bin/ directories
  - `isExcludedPath` — gitignore-style glob matching via the `Ignore` package (replaces string-contains matching)
  - `loadIgnoreFile` / `collectIgnoreRules` — load .gitignore and .fantomasignore files and combine into a single predicate
  - `IgnoreFilterCache` — caches ignore rules per repo root, auto-reloads when files change on disk
- `excludePatterns` parameter on `Daemon.create` / `Daemon.createWith` — exclude entire project trees from discovery using gitignore-style globs
- `CheckPipeline.RegisterProject` filters out generated files in obj/ and bin/ directories

### Changed

- `Daemon.performScan` takes `BatchContext` instead of 12 individual parameters
- Path filtering across Watcher, CheckPipeline, and Daemon consolidated through `PathFilter` module

### Dependencies

- Added `Ignore` 0.2.1 (gitignore-style pattern matching, same package used by Fantomas)

---

## 0.5.0-alpha.1 (2026-04-12)

### Added

- Enable TransparentCompiler for hash-based deterministic FCS caching (`useTransparentCompiler = true`)
- Parse `#nowarn` directives to suppress FCS TransparentCompiler warnings (workaround for dotnet/fsharp#9796)
- Plugin teardown support in `PluginHandler` (disposes semaphores, CTS, DB handles)

### Changed (Breaking)

- Type safety overhaul: `AbsFilePath`/`AbsProjectPath` single-case DUs replace raw strings; `PluginName` single-case DU with uniqueness check; `ContentHash` wrapper; `CommandOutcome` DU replaces `Succeeded: bool` + `Output: string`; `FileCheckState` DU replaces `CheckResults option`; `AffectedTestsState` DU; `RerunIntent` DU; `Set<SubscribedEvent>` replaces `PluginSubscriptions` bool record; `TaskCacheKey` struct replaces string key; `TestExtensionKind` DU; `CacheClearFilter` DU
- Plugin registration uses `PluginHostServices` record instead of multi-param function
- `Daemon` changed from F# record to class with `internal` constructor
- `IProjectGraphReader` interface decouples `BuildPlugin` from mutable `ProjectGraph`
- `BuildPhase` folds `PendingFiles` into `IdlePhase` (only meaningful when idle)

### Improved

- Extract pure filtering functions from `MacFsEvents` for testability
- `Watcher` accepts injectable `isMacOS` flag for cross-platform testability

### Changed (Breaking)

- `IProjectGraphReader` adds `GetProjectsForFile` method returning `AbsProjectPath list`
- `ProjectGraph.fileToProjects` now stores all projects per file (was `fileToProject` storing one)
- `CheckPipeline.projectOptionsByFile` stores all project options per file (list instead of single)
- New `CheckPipeline.CheckFileWithOptions` method for checking a file with explicit project options
- New `CheckPipeline.GetProjectOptions` method

### Fixed

- Propagate cancellation token into `CheckFileCore` — `CancelPreviousCheck` now actually stops in-flight FCS checks (previously only checked at entry, not around the expensive FCS call)
- Handle shared source files (linked items): a file appearing in multiple projects now triggers re-checks in all projects, not just the last-registered one
- `Daemon` implements `IDisposable` and stops all internal `MailboxProcessor` agents on dispose — agents previously ran indefinitely, keeping processes alive after tests
- `RunWithIpc` races initial scan against cancellation to prevent test-process hangs when `cts` is cancelled during slow `ScanAll`
- Standalone files not in any project now checked via uncovered-files fallback

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure and tooling release. No public API changes.

- CLI moved under core's shared tag in `semantic-tagger.json` — CLI now versions and releases together with the core package
- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No public API changes beyond dependency bumps.

- Add MIT license; add SourceLink; replace bespoke scripts with shared NuGet tools and reusable CI workflows
- Bump `TestPrune.Core` 0.1.0-beta.1 → 1.0.1
- Bump `Ionide.ProjInfo` / `Ionide.ProjInfo.FCS` 0.68.0 → 0.74.2
- Bump `FSharp.Data.Adaptive` 1.2.16 → 1.2.26

### Migration from 0.1.0-alpha.3

- Update `TestPrune.Core` dependency to 1.0.1 (check for API changes in that library)
- Update `Ionide.ProjInfo` to 0.74.2 (may affect project loading behavior)
- No FsHotWatch API signature changes

---

## 0.1.0-alpha.3 (2026-03-28 → 2026-04-02)

Severity-aware diagnostics, FCS warning reporting, MSBuild diagnostic parsing.

- **Breaking:** `ErrorLedger.HasErrors()` removed → use `HasFailingReasons(warningsAreFailures: bool)`
- **Breaking:** `ErrorLedger.Count()` removed → use `FailingReasons(warningsAreFailures: bool)` which returns `Map<string, (string * ErrorEntry) list>`
- **Breaking:** `PluginHost.HasErrors()` removed → use `HasFailingReasons(warningsAreFailures: bool)`
- **Breaking:** `PluginHost.ErrorCount()` removed → use `FailingReasons(warningsAreFailures: bool)`
- **Breaking:** IPC method `GetErrors` renamed to `GetDiagnostics` (both server and client)
- **Breaking:** `Daemon.create` gains `fcsSuppressedCodes: int list option` parameter (pass `None` for default — suppresses FS1182)
- **Behavioral change:** FCS now reports all diagnostic severities (Error, Warning, Info, Hidden), not just errors. Warnings will appear in the error ledger. Use `--no-warn-fail` or filter by severity if this is unwanted.
- Add `ErrorEntry.isFailing` helper for severity-aware failure checks

### Migration from 0.1.0-alpha.2

```fsharp
// ErrorLedger: before
ledger.HasErrors()
ledger.Count()

// ErrorLedger: after
ledger.HasFailingReasons(false)       // errors only
ledger.HasFailingReasons(true)        // errors + warnings
ledger.FailingReasons(false)          // get failing entries

// PluginHost: same pattern
host.HasFailingReasons(false)
host.FailingReasons(false)

// IPC client: before
IpcClient.getErrors proxy

// IPC client: after
IpcClient.getDiagnostics proxy

// Daemon.create: add fcsSuppressedCodes parameter
Daemon.create(config, plugins, fcsSuppressedCodes = None)
```

**Behavioral change:** FCS warnings now appear in the error ledger. If your workflow only expected errors, either:
- Pass `warningsAreFailures = false` to `HasFailingReasons`/`FailingReasons`
- Use `--no-warn-fail` CLI flag
- Filter `ErrorEntry` items by severity

---

## 0.1.0-alpha.2 (2026-03-28)

Subcommands, `--run-once` mode, `CommandCompleted` events, build dependencies, config enhancements.

- **Breaking:** `PluginEvent<'Msg>` gains `CommandCompleted of CommandCompletedResult` case — exhaustive matches must handle it
- **Breaking:** `PluginSubscriptions` gains `CommandCompleted: bool` field — direct construction must include it (or use `PluginSubscriptions.none`)
- **Breaking:** `PluginCtx` gains `EmitCommandCompleted: CommandCompletedResult -> unit` field
- **Breaking:** `CachedEvent` gains `CachedCommandCompleted` case
- Add `CommandCompletedResult` type and full event pipeline
- Add `Daemon.RunOnce()` for single-pass in-process mode

### Migration from 0.1.0-alpha.1

```fsharp
// PluginSubscriptions: add CommandCompleted field
{ PluginSubscriptions.none with FileChanged = true; CommandCompleted = false }
```

Handle new union cases in exhaustive matches:
```fsharp
match event with
| FileChanged _ -> ...
| BuildCompleted _ -> ...
| CommandCompleted _ -> ()  // new
// etc.
```

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Daemon with FSharpChecker warm cache
- File watcher with source/project/solution change detection
- Plugin host with event dispatch and command registration
- IPC server/client over named pipes (StreamJsonRpc)
- Preprocessor pipeline (format-on-save runs before events dispatch)
- Debounced file changes (500ms source, 200ms project)
- ProjectGraph for cross-project dependency tracking
- TaskCache for event deduplication
