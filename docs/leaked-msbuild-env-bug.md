# Leaked MSBuild env poisons spawned `dotnet build`

## Symptom

On a pristine checkout, `fshw check --run-once` (and `fshw build --run-once`)
deterministically fails the BUILD node within a few seconds reporting

```
Build FAILED.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:07.xx
```

while a standalone `dotnet build` of the same tree from a normal shell is clean.
It was long mis-attributed to an "MSBuild cold-start race" and waved off as
environmental.

## Root cause

The daemon evaluates projects in-process via `Ionide.ProjInfo.Init.init`
(`Daemon.fs`). To make in-process design-time MSBuild evaluation work, ProjInfo
**writes MSBuild-discovery variables into the daemon's own process
environment**:

```
MSBUILD_EXE_PATH      = <SDK band>/sdk/<ver>/MSBuild.dll
MSBuildExtensionsPath = <SDK band>/sdk/<ver>/
MSBuildSDKsPath       = <SDK band>/sdk/<ver>/Sdks
```

These pin MSBuild to the exact SDK band ProjInfo selected at startup. When the
BuildPlugin later spawns `dotnet build` via `Process.Start`, the child inherits
the daemon's full environment — including those three variables. On a machine
with multiple installed SDKs (no `global.json` SDK pin), the child's **muxer
resolves one SDK band while the leaked vars force MSBuild from another** (which
may be incomplete — e.g. missing `Sdks/Microsoft.NET.SDK.WorkloadAutoImportPropsLocator/Sdk`).
The implicit restore's restore-graph sub-build then returns exit 1 with **no
diagnostics surfaced**, which the plugin honestly reports as "Build FAILED /
0 Error(s)".

A plain shell never has these vars set, so a manual `dotnet build` always works
— which is exactly why the failure looked non-reproducible outside the daemon.

### Minimal repro (no daemon)

Setting just the three variables in a shell reproduces the failure on the same
tree; unsetting them makes the build pass again.

## Fix

`ProcessHelper.runProcess` strips `MSBUILD_EXE_PATH`,
`MSBuildExtensionsPath`, and `MSBuildSDKsPath` from the child environment before
every spawn (alongside the existing arch-specific `DOTNET_ROOT_*` strip). They
are meaningful only to an *in-process* MSBuild host, so dropping them is a no-op
for non-dotnet children, and a spawned `dotnet` re-resolves MSBuild correctly
from its own SDK. A caller that passes one of these keys explicitly still wins,
because the explicit-env overlay is applied after the strip.
