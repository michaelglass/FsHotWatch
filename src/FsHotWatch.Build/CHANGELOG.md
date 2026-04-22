# Changelog — FsHotWatch.Build

## Unreleased

- chore: bump upstream tool versions

## 0.5.0-alpha.1 (2026-04-12)

*No changes since 0.3.0-alpha.1.*

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure release. No public API changes.

- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No API changes.

- Add MIT license; add SourceLink; enable `TreatWarningsAsErrors`; replace bespoke scripts with shared NuGet tools and reusable CI workflows

---

## 0.1.0-alpha.3 (2026-04-02)

- Build plugin now reports parsed MSBuild diagnostics via new `BuildDiagnostics.parseMSBuildDiagnostics` — structured `ErrorEntry` items with severity, file, line, column instead of raw text

---

## 0.1.0-alpha.2 (2026-03-28)

- **Breaking:** `BuildPlugin.create` gains required `dependsOn: string list` parameter (pass `[]` for no dependencies)
- **Breaking:** `BuildState` gains `SatisfiedDeps` and `PendingFiles` fields
- Build dependency ordering — build waits for named `CommandCompleted` events before starting

### Migration from 0.1.0-alpha.1

```fsharp
// BuildPlugin.create: add dependsOn parameter
BuildPlugin.create(buildTemplate, dependsOn = [])
```

Config file changes:
```jsonc
// "build" can now be an array of build steps:
"build": [{ "command": "dotnet", "args": "build", "dependsOn": [] }]
```

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Build plugin wrapping `dotnet build` with concurrent-build guard
