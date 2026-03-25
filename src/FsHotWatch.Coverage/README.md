# FsHotWatch.Coverage

FsHotWatch plugin that checks coverage thresholds after tests complete.
Reads Cobertura XML coverage reports and compares against per-project thresholds.

## Usage

```fsharp
daemon.Register(CoveragePlugin(
    coverageDir = "./coverage",
    thresholdsFile = ".coverage-thresholds.json",
    afterCheck = fun () -> Coverage.ratchet()
))
```

Thresholds JSON format:
```json
{
  "MyApp.Tests.Unit": { "line": 85.0, "branch": 70.0 },
  "MyApp.Tests.Database": { "line": 60.0, "branch": 50.0 }
}
```
