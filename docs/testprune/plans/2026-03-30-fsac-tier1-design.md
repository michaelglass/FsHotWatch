# FSAC Tier 1 Improvements — Design

Date: 2026-03-30

## Scope

Four actionable Tier 1 items from `docs/plans/fsac-learnings.md`. Items skipped:
- MSBuild bounded retry — current code already uses `lock msbuildLock`, no infinite loop present.
- CancellationTokenSource TryCancel/TryDispose — no cancellation infrastructure to protect yet.

---

## 1. Script checking semaphore

**File:** `src/TestPrune.Core/AstAnalyzer.fs`

Add a module-level `SemaphoreSlim(1, 1)` to serialize calls to `GetProjectOptionsFromScript`. FCS has internal state corruption under concurrent script processing.

```fsharp
let private scriptSemaphore = new SemaphoreSlim(1, 1)
```

In `getScriptOptions`, acquire before calling `checker.GetProjectOptionsFromScript` and release in a `finally` block (or `use` pattern).

---

## 2. Build timeout

**File:** `src/TestPrune/Program.fs`

In `dotnetBuildRunner`, replace `buildProc.WaitForExit()` with `WaitForExit(timeoutMs)`. Default: 600,000 ms (10 minutes).

On timeout:
- Kill the process
- Emit `eprintfn "Build timed out after 10 minutes"`
- Return exit code `1`

No new CLI flag — hardcoded constant is sufficient for a pre-index step.

---

## 3. Process execution logging

**Files:** `src/TestPrune.Core/TestRunner.fs`, `src/TestPrune/Program.fs`

Add `Stopwatch` timing around process execution. After the process exits, emit:

```
eprintfn "[dotnet build <path>] → exit 0 in 4.2s"
eprintfn "[dotnet exec <dll>] → exit 0 in 1.1s"
```

Output to `eprintfn` (stderr), consistent with existing diagnostic output. No new abstractions — inline in the existing `runProcess` helper and `dotnetBuildRunner`.

---

## 4. Resolve relative paths before FCS

**File:** `src/TestPrune.Core/AstAnalyzer.fs`

Add a small helper:

```fsharp
let private resolveToAbsolute (basePath: string) (path: string) =
    if Path.IsPathRooted(path) then path
    else Path.GetFullPath(Path.Combine(basePath, path))
```

Apply in `getScriptOptions` over `projOptions.SourceFiles` and `projOptions.OtherOptions` before returning the enhanced options. Use the directory of `sourceFileName` as the base.

---

## Approach

All changes are local with no new abstractions introduced. Semaphore is a module-level value. Timeout is a constant. Logging goes to `eprintfn`. No new types or function signatures changed.
