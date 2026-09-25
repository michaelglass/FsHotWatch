# Changelog — TestPrune.Trace.Recorder

## Unreleased

- feat: package scaffold. `Probes` and `Scopes` carry their final signatures with no-op
  bodies, and `Contract` names every probe the weaver emits. Targets net8.0 and pins
  FSharp.Core 8.0.403 so it never raises a host test process's FSharp.Core floor.
- feat: recorder core. Probe hits are attributed, per async flow, to static init, an
  explicit `Scopes.Enter` scope, the current xUnit v3 test (or its class, collection or
  assembly, bound by reflection), or ambient/the parent scope in a child process. One
  bitset per scope; the hit path allocates nothing once a thread and context are known.
  With `TESTPRUNE_TRACE_OUT` set, the process writes one `testprune-trace/1` NDJSON dump
  at exit, via a `.tmp` file and a rename, ending in an `{"end":true}` marker.
