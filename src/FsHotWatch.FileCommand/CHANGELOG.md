# Changelog — FsHotWatch.FileCommand

## Unreleased

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
