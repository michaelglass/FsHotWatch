# Changelog — TestPrune.Trace.Recorder

## Unreleased

- fix: the exit dump can no longer crash the traced process. Under `--coverage`, MTP runs a
  test-host controller next to the test host, and MS CodeCoverage instruments the app's
  assemblies in place on disk and restores them afterwards; the controller's recorder module
  could be unreadable by exit, so its ProcessExit handler failed to compile and the process
  died (exit 139) after every test had passed. The handler is compiled when it is registered
  and catches any failure, which it reports on stderr in one line
  (`testprune-trace: dump write failed: <type>: <message>`); a dump it left unfinished stays
  a `.tmp`, which no reader takes for a dump.

## 0.4.1 - 2026-09-28

- changelog: the TestPrune.Inline attribute index is a feature, not a breaking change
- coverage: pin the SDK band and remove the branches SDK 10.0.4xx left uncovered
- Index inline members as the TestPrune.Inline attribute


## 0.4.0 - 2026-09-27

- fix: each type initializer records into its own `S:<type>` scope, named from the static
  constructor on the stack (one stack walk per initialized type), instead of one
  `S:static-init` scope for the process. An initializer that runs inside another is linked
  from it. `RecorderState.EnterStatic` takes the type name; `S:static-init` remains for an
  initializer with no static constructor on the stack.

## 0.3.1 - 2026-09-27

- Trace: key the weave cache on the weaver's build, not its version
- Trace: keep branch points under a match's later hidden sequence points

## 0.3.0 - 2026-09-27

- Merge main@origin into the branch-point weaver fix
- Keep branch points in coverage of woven assemblies
- Merge main@origin into the trace output fix and phase-1 parity results
- Keep a failing launch's output in the trace measurement verbs


## 0.2.1 - 2026-09-27

- chore: rebuild to bundle updated dependencies


## 0.2.0 - 2026-09-26

- fix: a started child whose `TESTPRUNE_TRACE_OUT` differs from this process's is a trace
  of its own: `ProcessShims` no longer gives it this process's scope as its parent scope,
  and removes an inherited one.

## 0.1.0 - 2026-09-26

- docs: ships the TestPrune.Trace README as its package README.
- feat: package scaffold. `Probes` and `Scopes` carry their final signatures with no-op
  bodies, and `Contract` names every probe the weaver emits. Targets net8.0 and pins
  FSharp.Core 8.0.403 so it never raises a host test process's FSharp.Core floor.
- feat: recorder core. Probe hits are attributed, per async flow, to static init, an
  explicit `Scopes.Enter` scope, the current xUnit v3 test (or its class, collection or
  assembly, bound by reflection), or ambient/the parent scope in a child process. One
  bitset per scope; the hit path allocates nothing once a thread and context are known.
  With `TESTPRUNE_TRACE_OUT` set, the process writes one `testprune-trace/1` NDJSON dump
  at exit, via a `.tmp` file and a rename, ending in an `{"end":true}` marker.
- feat: input capture. `Io` shims the BCL's file readers, existence probes and directory
  listings, and `ProcessShims` shims `Process.Start`; a woven call site calls them with the
  original's stack shape. A path under `TESTPRUNE_TRACE_REPO_ROOT` is noted on the scope
  doing the I/O (static init, then the current scope, then ambient). A started child gets
  the starting scope in `TESTPRUNE_TRACE_PARENT_SCOPE` and is noted with its pid, and with
  whether the environment could be passed (not for shell-executed children).
- feat: JIT-verification mode. Loaded through `DOTNET_STARTUP_HOOKS` with
  `TESTPRUNE_TRACE_VERIFY` set, the recorder's `StartupHook` JIT-prepares every listed woven
  method in the app's own runtime, writes a report naming each method the JIT rejects, and
  exits before `Main`. Without the variable the hook does nothing.
