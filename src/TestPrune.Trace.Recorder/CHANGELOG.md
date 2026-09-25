# Changelog — TestPrune.Trace.Recorder

## Unreleased

- feat: package scaffold. `Probes` and `Scopes` carry their final signatures with no-op
  bodies, and `Contract` names every probe the weaver emits. Targets net8.0 and pins
  FSharp.Core 8.0.403 so it never raises a host test process's FSharp.Core floor.
