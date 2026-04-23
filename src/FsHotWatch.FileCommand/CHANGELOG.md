# Changelog — FsHotWatch.FileCommand

## Unreleased

### Added
- `afterTests` trigger — fileCommand entries can now run on `TestCompleted` events, optionally filtered by a list of test project names.
- Required `name` field for entries whose primary trigger is `afterTests`.

### Changed
- **BREAKING:** `FileCommandPlugin.create` now takes a `CommandTrigger` record (`FilePattern` + `AfterTests`) instead of `fileFilter` + `runOnStart`. Callers registering directly against the plugin API must migrate.
- `afterTests` list-form semantics: the plugin now fires iff **every** listed project appears in the `TestResults.Results` map (previously "any"). Combined with TestPrune's progressive cumulative `TestCompleted` emission (see FsHotWatch.TestPrune CHANGELOG), this means the command fires exactly once per batch — on the first cumulative emission that covers every listed project — so a hanging non-listed project (e.g. integration tests) never blocks it. Internal `FileCommandState` gains a `LastAfterTestsKey` sentinel to suppress re-firing on later cumulative emissions within the same batch.

### Removed
- `runOnStart` config/API field. Use an explicit trigger (e.g. `afterTests: true`) or schedule initialization another way.

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

## 0.1.0-alpha.2 (2026-03-28)

- **Breaking:** `FileCommandPlugin.create` gains required `runOnStart: bool` parameter (pass `false` for previous behavior)
- FileCommandPlugin now emits `CommandCompleted` events after each run

### Migration from 0.1.0-alpha.1

```fsharp
// FileCommandPlugin.create: add runOnStart parameter
FileCommandPlugin.create(pattern, command, args, runOnStart = false)
```

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Run arbitrary shell commands when specific file patterns change
