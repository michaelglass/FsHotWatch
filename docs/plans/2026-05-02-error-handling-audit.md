# Error-handling audit (read-only)

Audit date: 2026-05-02. Scope: `src/` only. Tests, examples, and generated code excluded per the audit brief.

The team-lead's stance — "we are allowed to catch errors, or ignore errors, but only when there's a principled reason" — frames every finding below. The canonical anti-example is the recently reverted display-time filter for FCS "type X does not match type X" diagnostics (`rlvvpkvr`): a broad heuristic suppression that papered over a real correctness signal. We use that as our calibration: a catch is "papered-over" when it could plausibly hide a class of bugs that we would *want* to learn about.

## 1. Headline counts

Counts via `grep -E "^\s*with " --include='*.fs' src/`:

| Pattern                                       | Count |
| --------------------------------------------- | ----- |
| Total `with`-handlers in source                |    76 |
| `with _ -> ...`  (catch-all, ex discarded)     |    22 |
| `with ex -> ...` (catch-all, ex bound)         |    45 |
| `with :? <type>` (narrow)                      |     6 |
| Other (multi-pattern, `ConfigError`, etc.)     |     3 |

Distribution by project:

- `src/FsHotWatch/` — 26 (core: Daemon, CheckPipeline, ErrorLedger, PluginHost, PluginFramework, ProcessHelper, FileCheckCache, FileTaskCache, ProcessRegistry, MacFsEvents, Watcher, PathFilter, CheckCache)
- `src/FsHotWatch.Cli/` — 13 (Program, DaemonConfig, IpcParsing, IpcOutput, InitConfig)
- `src/FsHotWatch.TestPrune/` — 13 (TestPrunePlugin, Flakiness)
- `src/FsHotWatch.Build/` — 3 (BuildPlugin)
- `src/FsHotWatch.FileCommand/` — 3 (FileCommandPlugin)
- `src/FsHotWatch.Analyzers/` — 3 (AnalyzersPlugin)
- `src/FsHotWatch.Lint/` — 1 (LintPlugin)
- `src/FsHotWatch.Fantomas/` — 3 (FormatCheckPlugin)

Item 5B narrowed several TestPrunePlugin sites; the narrowing has held — the broad `with ex` sites that remain in TestPrunePlugin are now either (a) wrapped in a `try ... Ok / with ex -> Error` shape that immediately re-dispatches `tryRepairSchemaDrift` (lines 1214–1224, 1332–1349) or (b) sit at the outer Update boundary as defense-in-depth with the framework already providing a Failed-status safety net (line 1164 — see F10).

After the team-lead sharpened the "principled" bar mid-audit (see §2b below), the report was re-passed. Several sites originally listed as principled were downgraded to findings F11–F20. The §3 list now contains only sites that pass the sharper test — i.e., either narrow `:? <ExceptionType>` patterns, in-source comments naming the invariant, or explicit `reraise()` flows.

## 2. Top findings — papered-over (high severity)

Severity reflects the *blast radius* if the swallowed exception were a real bug.

### F1. `CheckCache.fs:120` — diagnostic-hash failure becomes a literal cache key

```fsharp
| FullCheck results ->
    try
        results.Diagnostics |> Array.map (fun d -> ...) |> hashDiagnosticSignatures
    with _ ->
        "full-check-error"
```

`fcsCheckSignature` feeds downstream plugin task-cache keys. If `results.Diagnostics` ever throws (e.g. internal FCS state mismatch — exactly the territory we just spent two stress cycles in), every failing call returns the *same string literal* `"full-check-error"`. That literal then collides across files, projects, and machines, producing cache hits that shouldn't exist. This is structurally identical to the reverted "type X does not match type X" filter: a heuristic that hides a load-bearing signal.

- Pattern: category 5 (default value where the default isn't semantically meaningful).
- Judgment: **papered-over**.
- Recommendation: let the exception propagate, or fold the exception type/message into the literal so two distinct failures don't collide. At minimum, log with `Logging.error`.

### F2. `FormatCheckPlugin.fs:185` — unreadable file silently treated as empty in cache key

```fsharp
let source =
    try File.ReadAllText(f)
    with _ -> ""
```

Inside the merkle key for the format-check cache. Two unreadable files (transient lock, permission, missing) hash identically — and identically to a real empty file. A cache hit that shouldn't have happened means stale "format OK" verdicts.

- Pattern: category 5.
- Judgment: **papered-over**.
- Recommendation: skip the file from the merkle (returning `None` from the caller) or include the exception's class name in the hashed payload so distinct failures stay distinct.

### F3. `DaemonConfig.fs:599` — config-watcher onChange exception fully suppressed

```fsharp
try onChange reason
with _ -> ()
```

`onChange` is the daemon's "config changed, stopping" callback. If it throws, the daemon does *not* stop, and the user's `.fshw.json` edit is silently ignored. A user editing config and seeing the daemon ignore them is a debugging nightmare.

- Pattern: category 3 (empty catch).
- Judgment: **papered-over**.
- Recommendation: log at error and re-raise (or surface via the daemon's standard error path).

### F4. `FileCheckCache.fs:66–69` — Invalidate failure swallowed entirely

```fsharp
try File.Delete(path)
with
| :? FileNotFoundException -> ()
| :? DirectoryNotFoundException -> ()
```

This one is actually narrow — but I noticed during the read that line 66's `try` is followed by a *second* set of pattern arms that match only the two Not-Found exceptions. Anything else (permission denied, sharing violation) bubbles. **Principled** — flagging here only because it's a useful contrast with F3.

### F5. `FileCommandPlugin.fs:87` — file-hash failure returns `None` (cache key path)

```fsharp
let hashFile (path: string) : string option =
    try
        let bytes = read path
        let hash = SHA256.HashData(bytes)
        Some(BitConverter.ToString(hash).Replace("-", ""))
    with _ -> None
```

Used by the file-command plugin to merkle inputs. A transient read failure (file locked by editor, etc.) drops the file from the merkle — meaning a subsequent successful read could match a previously cached "missing" key.

- Pattern: category 5 (default value swaps the input).
- Judgment: **papered-over (low–medium)**: the file-command plugin is a thin user-config layer, but the same shape is exactly what we just fixed in BuildInputsHasher (item 5A: silent cache poisoning).
- Recommendation: narrow to `IOException`; surface anything else.

### F6. `Flakiness.fs` (lines 49, 58, 88, 128, 160) — silent JSON parse fallbacks

Five bare `with _ -> ` sites returning `None`, `[]`, or `Map.empty` when CTRF reports / history files fail to parse. The module's docstring **explicitly justifies** this: "silent failure is the right behaviour for an opportunistic post-test step that shouldn't crash the test runner if the report is missing or malformed." 

- Judgment: **principled**. Documented invariant; bounded blast radius (flakiness telemetry, not correctness).
- Note for future readers: this is the rare case where bare `with _` is OK *because the comment proves the author thought about it*. Don't tighten without first replacing the comment.

### F7. `CheckCache.fs:55` — file-read failure synthesizes a "unreadable" cache key

```fsharp
with ex ->
    Logging.debug "cache" $"Could not read %s{normalizedPath}: %s{ex.Message}"
    sha256Hex $"unreadable:%s{normalizedPath}"
```

Better than F1: the path is in the synthetic key, so two different unreadable files don't collide. Still problematic: a transient lock that resolves on retry produces a "unreadable" cache entry that lives forever and incorrectly serves stale data.

- Pattern: category 5.
- Judgment: **borderline papered-over**. The synthesized key bounds the collision but the cache poisoning is still real.
- Recommendation: return `None` upstream and let the caller decide (cache miss + retry).

### F8. `IpcOutput.fs:105–108` & `IpcParsing.fs:155–163` — silent JSON parse failures

```fsharp
try Some(JsonDocument.Parse(result))
with _ -> None
```

```fsharp
try ... use doc = JsonDocument.Parse(json) ... build map
with _ -> Map.empty
```

CLI-side IPC parsing. If the daemon emits malformed JSON we silently render an empty UI. A real bug in the daemon's JSON producer would be invisible — exactly what the team-lead wants to avoid.

- Pattern: category 4 (filter that drops bad input without raising).
- Judgment: **papered-over (low)**: probably the right UX surface in `IpcOutput` (don't crash the user's terminal), but `IpcParsing` should log at warn so a regressing daemon shows up.
- Recommendation: narrow to `JsonException` and emit a warn-level log on the catch.

### F9. `Program.fs:641` & `Program.fs:471` — bulk-stop / coverage-cleanup bare catches

Both are bulk operations over a list (`forEach: try; with _ -> ()/None`). Defensible (one bad item shouldn't halt the loop) but undocumented.

- Judgment: **borderline**. Add a debug-log line and they become principled.

### F10. `TestPrunePlugin.fs:1164` — broad `with ex` at the Update boundary

```fsharp
with ex ->
    if isIdle then
        ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
    return state
```

Item 5B narrowed several adjacent sites; this one survived. The framework's `safeUpdate` (PluginFramework.fs:382–389) already wraps Update in a top-level try and reports Failed, so this catch is redundant for non-idle paths — meaning when not-isIdle, a real bug in the analysis code path is *swallowed twice* and the user only sees a generic framework-level Failed status. Worse: the catch returns the un-mutated `state`, which can leave `ChangedFiles` / `ChangedSymbols` with stale entries the next event will re-process.

- Pattern: category 1.
- Judgment: **papered-over (medium)**. Item 5B's narrowing pass missed this site.
- Recommendation: drop this try entirely (let `safeUpdate` handle it) or narrow to the SQLite/schema-drift exceptions and run the schema-drift self-heal as the adjacent sites do.

## 2b. Sharpened-bar re-pass

After an initial pass that filed F1–F10, the team-lead sharpened the definition of "principled":

> **Principled means there's a positive, specific reason the catch is correct** — the catch documents the EXACT exception class it expects and what would have to break for it to occur. The defining test: can a future reader, looking at the code, derive WHY the catch is the right shape? If the answer requires "trust the original author had a reason," it's papered over.

Several sites I called "principled" in §3 below survive this bar; several do not. I re-examined each. The ones that fail the sharper test are filed as additional findings F11–F20 here. §3 has been pruned to only what survives.

Common failure modes the sharper bar exposes:
- **Mailbox-loop guards** that catch any exception "to keep the agent alive". Inside a typed `match msg with` over messages we own and fields we read, what would actually throw? Almost certainly a programming bug — exactly what we want the agent to crash on so we *learn* about it. A daemon-shutdown is fine; silently dropping events forever is not.
- **"Third-party plugin/reporter/analyzer can throw anything"** is a real reason — but only when the catch is documented to that effect. Without the comment, the reader can't tell whether the breadth is justified by an external-code boundary or by author uncertainty.
- **Bare `with _` on a `proc.Kill` / `proc.HasExited`** — Process APIs throw a known small set (`InvalidOperationException` when never started, `Win32Exception` on permission failure). Using `_` instead of those types means we'd also swallow, e.g., a `NullReferenceException` from a real bug.

### F11. `ErrorLedger.fs:143` — reporter call broad-catches without naming the boundary

```fsharp
let notifyReporters action =
    for r in reporters do
        try action r
        with ex -> Logging.error "error-ledger" $"Reporter failed: %s{ex.Message}"
```

The catch is right *in spirit* (an `IErrorReporter` impl is third-party-shaped — `FileErrorReporter` already lives here, but the interface admits arbitrary impls). The catch is **wrong as written** under the sharper bar: no comment names the third-party boundary, and `Logging.error` strips the stack trace (`ex.Message`, not `ex.ToString()`). A reader can't tell whether the broad catch is principled (third-party impl) or papered (author didn't think it through).

- Pattern: category 2.
- Judgment: **papered-over** (under sharper bar; was previously called principled).
- Recommendation: comment the third-party-boundary justification *and* log `ex.ToString()` so a misbehaving reporter is debuggable.

### F12. `ErrorLedger.fs:275`, `Daemon.fs:332` — mailbox-loop "agent must not die" catches

```fsharp
// ErrorLedger.fs:275
with ex ->
    Logging.error "error-ledger" $"Agent failed: %s{ex.ToString()}"
    state
```

```fsharp
// Daemon.fs:332 (ScanSignal)
with ex ->
    Logging.error "scan-signal" $"Agent failed: %s{ex.ToString()}"
    return! loop latestGeneration waiters
```

What would actually throw inside these handlers? They're typed-message pattern matches over data the agent itself stores (`Map.add`, `List.partition`, `tcs.TrySetResult`). If any of those throws, it's a programming bug — and continuing the loop in the original state means the bug recurs forever and silently. The "agent must not die" instinct is real, but the right move is to crash the agent (or at minimum, surface a daemon-level Failed status) so the user sees a problem instead of a stuck UI.

- Pattern: category 1 / 2.
- Judgment: **papered-over**.
- Recommendation: drop these inner catches. Let the agent's outer `MailboxProcessor` exception handler run. If we want recoverability, narrow to the specific exception classes we know can occur (none, in the current code) and let everything else crash.

### F13. `Daemon.fs:1265, 1274, 1304` — debounce / scan agent broad catches

Same shape as F12 but with `processBatch` / `performScan` inside, which call into FCS, MSBuild, and plugin code. Those *can* throw varied exceptions. The catch is closer to defensible (the surface area is real), but still has no comment, no narrowing, and `Logging.error` swallows the stack. Returning to idle on an arbitrary exception means the next scan-state diverges silently from on-disk state.

- Pattern: category 2.
- Judgment: **papered-over (medium)** — the surface area is broad enough that *some* catch is reasonable, but the current shape doesn't pass the bar.
- Recommendation: split into a narrow catch over expected exception classes (`OperationCanceledException`, the IO classes already narrowed in PathFilter, MSBuild's `Microsoft.Build.Exceptions.InvalidProjectFileException`, etc.) and let everything else propagate to a top-level daemon failure handler.

### F14. `PluginHost.fs:421–425` — plugin Teardown loop

```fsharp
for p in registeredPlugins do
    match p.Teardown with
    | Some teardown ->
        try teardown ()
        with ex -> Logging.error (PluginFramework.PluginName.value p.Name) $"Teardown failed: %s{ex.Message}"
    | None -> ()
```

Plugins are third-party; the broad catch is justified by the boundary. But: no comment, and again `ex.Message` instead of stack. Under the sharper bar, **papered-over** for documentation reasons, not breadth reasons.

- Recommendation: add a one-line comment ("plugin Teardown is third-party; broad catch keeps cleanup going across other plugins") and log `ex.ToString()`.

### F15. `PluginFramework.fs:225` — RunExclusive work failure

```fsharp
try
    let! msg = w
    completion <- ValueSome msg
with ex ->
    error (PluginName.value handler.Name) $"RunExclusive '%s{key}' work failed: %s{ex.ToString()}"
finally
    lock runSlotsLock (fun () -> runSlots.[key] <- false)
```

The `finally` is load-bearing (slot must release). The `with ex ->` is what the team-lead called out: *catching to avoid a crash without a specific theory of which crash*. The work async is plugin-supplied, so by analogy with F14 there's a third-party boundary, but it's undocumented.

- Judgment: **papered-over** under sharper bar.
- Recommendation: comment the third-party-async justification, or restructure so only `finally` runs and the exception propagates to `safeUpdate` (which already handles plugin exceptions per F=principled in §3).

### F16. `AnalyzersPlugin.fs:308` — per-file analyzer crash

```fsharp
with ex -> error "analyzers" $"Error analyzing %s{fileStr}: %s{ex.ToString()}"
```

Third-party analyzer boundary. Same "valid in spirit, not documented" issue as F14/F15.

- Judgment: **papered-over** under sharper bar.
- Recommendation: comment.

### F17. `ProcessHelper.fs:132` — `proc.Kill(entireProcessTree = true)` bare-catch

```fsharp
try proc.Kill(entireProcessTree = true)
with _ -> ()
```

`Process.Kill` throws `InvalidOperationException` (process already exited) and `Win32Exception` (permission/system error). The first is the case we expect (race between WaitForExit-timeout and the child exiting on its own). The second is a real failure we should at least log. `with _` catches both indistinguishably.

- Pattern: category 3.
- Judgment: **papered-over**.
- Recommendation: narrow to `:? InvalidOperationException -> ()` and let `Win32Exception` propagate (or log it).

### F18. `ProcessHelper.fs:138–141` — drain `Task.WaitAll` bare-catch

```fsharp
try Task.WaitAll([| stdoutTask :> Task; stderrTask :> Task |], drainMs) |> ignore
with _ -> ()
```

`Task.WaitAll` propagates `AggregateException` from the underlying tasks. The intent is "drain anything available; we already killed the process so partial output is fine." But `with _` also swallows e.g. `ObjectDisposedException` from the streams or a real bug.

- Judgment: **papered-over**.
- Recommendation: narrow to `:? AggregateException` and `:? IOException`.

### F19. `ProcessRegistry.fs:33` — `not p.HasExited` bare-catch

```fsharp
let alive =
    try not p.HasExited
    with _ -> false
```

`Process.HasExited` throws `InvalidOperationException` (no process associated) or `Win32Exception` (access denied). Treating both as "not alive" is *probably* what we want during snapshotting, but `with _` is broader than warranted.

- Judgment: **papered-over (low)**.
- Recommendation: narrow to those two types.

### F20. `ProcessRegistry.fs:42–50` — `KillAll` per-process bare-catch

```fsharp
member _.KillAll() : unit =
    for kv in live do
        try
            let p = kv.Value
            if not p.HasExited then p.Kill(entireProcessTree = true)
        with _ -> ()
```

Same critique as F17 + F19, plus the doc-comment ("Tracks added concurrently with iteration may be missed") names a *different* hazard than the catch addresses. The comment talks about the dictionary race; the catch handles process-state exceptions. A reader cannot derive WHY the catch is the right shape from the comment.

- Judgment: **papered-over (low)** — daemon shutdown only, but still fails the sharper test.
- Recommendation: narrow exception types and align the comment with what the catch actually covers.

## 3. Principled callouts (under sharpened bar) — DO NOT tighten without thought

These pass the "future reader can derive WHY the catch is the right shape" test — either via a narrow `:? <ExceptionType>` pattern that names the expected class explicitly, an in-source comment that documents the invariant, or a `reraise()` that proves the catch isn't actually swallowing.

| File:line                        | Why it survives the sharper bar                                                  |
| -------------------------------- | --------------------------------------------------------------------------------- |
| `ProcessHelper.fs:101–108`        | `with _ -> ()` *with a comment* naming "path missing or not a symlink — leave the original value alone". Borderline (`_` is broader than the comment claims), but the comment is specific enough that a reader can derive the invariant. |
| `Daemon.fs:1347` (`with _`)       | Constructor cleanup-and-reraise. The `_` discards no information because `reraise()` propagates the original exception. |
| `MacFsEvents.fs:347`              | `with ex -> cleanup(); raise ex` — explicit reraise after resource cleanup.      |
| `PluginFramework.fs:382–389`      | `safeUpdate`: documented defense-in-depth Failed-status safety net. Comment names the exact pathology ("plugin reports Running, hits a DB error, never reports terminal status"). |
| `CheckPipeline.fs:17`             | `with :? ObjectDisposedException -> ()`. Narrow + named.                          |
| `CheckPipeline.fs:285,304`        | `with :? OperationCanceledException -> ...`. Narrow + correct for cancel paths.   |
| `Watcher.fs:165–167`              | `:? DirectoryNotFoundException` / `:? UnauthorizedAccessException` only.          |
| `PathFilter.fs:59,76`             | `:? FileNotFoundException` / `:? DirectoryNotFoundException` only.                |
| `Program.fs:573,613,868`          | `:? IOException` / `:? OperationCanceledException`. Narrow.                       |
| `Program.fs:995–997`              | `with ConfigError msg ->`. Domain-specific exception type.                        |
| `DaemonConfig.fs:537`             | `with ConfigError _ -> reraise() | ex -> raise (ConfigError ...)`. Wraps non-domain exceptions into a typed wrapper that callers handle. |
| `Flakiness.fs:49,58,88,128,160`   | Module-level docstring documents "opportunistic post-test step that shouldn't crash if report is missing or malformed". The bar is met by the docstring, not by per-site comments. |
| `BuildPlugin.fs:314,388,412`      | Exception turns into typed `BuildOutputFailed` outcome that the rest of the system already understands — the catch is reshaping the exception into the domain, not hiding it. |
| `AnalyzersPlugin.fs:94`           | Reflection ctor for FCS-version-mismatch workaround. Justification documented in `CLAUDE.md` ("AnalyzersPlugin uses reflection to construct CliContext (FCS version mismatch workaround)"). |

Sites previously listed here that have been **moved to findings F11–F20** under the sharper bar: `ErrorLedger.fs:143`, `ErrorLedger.fs:275`, `PluginHost.fs:421–425`, `PluginFramework.fs:225`, `Daemon.fs:332`, `Daemon.fs:1265,1274`, `Daemon.fs:1304`, `AnalyzersPlugin.fs:308`, `ProcessHelper.fs:132,140`, `ProcessRegistry.fs:33,49`.

## 4a. Cross-cutting observation: `Logging.error "..." ex.Message`

A pattern that recurs across F11, F14, and most of the catches in §3-removed: logging `ex.Message` instead of `ex.ToString()`. `ex.Message` strips the stack trace and the inner-exception chain. When the catch is broad enough to admit a real bug, losing the stack means we can't diagnose it from logs alone — we have to reproduce locally. Several of the borderline-papered findings would become *less* problematic if their log call captured the stack. This isn't itself a finding, but it converts category-2 sites ("logs and continues") into "logs *usefully* and continues" with no other change. Worth a sweep.

## 4. Anti-patterns surfaced

For the team's future reference; each maps to a real instance above.

1. **"Synthesize a magic-string fallback for a hash."** F1 (`"full-check-error"`), F7 (`"unreadable:..."`). When hashing produces a literal under failure, that literal becomes a real cache value. Either fold the exception into the hash payload (so distinct failures stay distinct) or refuse to produce a key.
2. **"Bare catch on the cache-key input."** F2, F5. Treating an unreadable file as `""` or omitting it from a merkle silently turns transient IO faults into stale cache hits. We just shipped item 5A (BuildInputsHasher mtime) for exactly this category — avoid recreating it elsewhere.
3. **"Empty catch on a control-plane callback."** F3. If the catch swallows the only signal that something should happen (daemon stop, plugin teardown, scan re-trigger), the system enters a quietly-broken state. Empty catches are only ever OK on best-effort cleanup — never on the happy path's invocation of a side-effect.
4. **"Double-wrapped catch."** F10. The framework already provides `safeUpdate`; a redundant inner `with ex -> return state` adds a path that swallows real bugs and risks state corruption (returning the un-mutated state can leak stale ChangedFiles into the next cycle). When framework-level handling exists, inner handlers must catch *narrow, recoverable* exceptions only.
5. **"Silent JSON drop."** F8. JSON parse failures at boundaries (IPC, config, cache files) should be logged at warn or higher when they cross a process boundary — they almost always indicate a producer/consumer schema drift, which is the kind of bug you want to learn about loudly.

## 5. Recommended priority order for fixes

Ordered by **expected correctness payoff per minute of work**. Tier 1 = clear bug or correctness hazard; Tier 2 = sharper-bar cleanup that improves debuggability and removes silent-failure surface area.

**Tier 1 — correctness / cache-poisoning (first pass):**

1. **F1 — `CheckCache.fs:120`** (10 min). Magic-string fallback in cache-key signature. High blast radius, tiny diff.
2. **F3 — `DaemonConfig.fs:599`** (5 min). Empty catch on daemon-stop callback. One-line fix.
3. **F10 — `TestPrunePlugin.fs:1164`** (15 min). Item 5B narrowing missed this one. Drop or narrow to schema-drift.
4. **F2 — `FormatCheckPlugin.fs:185`** (10 min). Same shape as item 5A.
5. **F7 — `CheckCache.fs:55`** (15 min). Synthesized "unreadable:..." cache key.

**Tier 2 — sharper-bar cleanup (after Tier 1):**

6. **F12 — `ErrorLedger.fs:275`, `Daemon.fs:332`** (15 min). Drop the inner mailbox-loop guards or restructure to surface failures. These are the most clearly-papered-over of the sharper-bar findings.
7. **F17, F18, F19, F20 — Process-handling narrows** (20 min, all together). Replace `with _` over `Process.Kill` / `HasExited` / `Task.WaitAll` with specific `:? InvalidOperationException` / `:? Win32Exception` / `:? AggregateException`.
8. **F8 — `IpcOutput.fs:107` / `IpcParsing.fs:162`** (10 min). Narrow to `JsonException`.
9. **F11, F14, F15, F16 — Comment + log-stack-trace sweep** (15 min). The third-party-boundary catches need (a) a one-line comment naming the boundary and (b) `ex.ToString()` instead of `ex.Message`. See §4a.
10. **F13 — `Daemon.fs:1265, 1274, 1304`** (30 min). Larger refactor; needs a top-level daemon failure handler design before tightening.
11. **F5 — `FileCommandPlugin.fs:87`** (10 min). Narrow to `IOException`.
12. **F9 — `Program.fs:471, 641`** (5 min). Add a `Logging.debug` for observability.

F4 and F6 require no action.

**Total**: 18 actionable findings (10 from initial pass + 8 from sharpened-bar re-pass). Tier 1 ~55 min; Tier 2 ~105 min. Combined with the §4a `ex.ToString()` sweep this is roughly a half-day cleanup that meaningfully tightens the daemon's signal-to-noise on errors.
