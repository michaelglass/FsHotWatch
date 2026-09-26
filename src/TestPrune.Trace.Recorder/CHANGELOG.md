# Changelog — TestPrune.Trace.Recorder

## Unreleased

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
