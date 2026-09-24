# Changelog — FsHotWatch.Analyzers

## Unreleased

## 0.7.0-alpha.37 - 2026-09-24

- host: check identical worktrees once, under one virtual root
- host: scope logs, child environments and process-wide effects to a session


## 0.7.0-alpha.36 - 2026-09-23

- Changed: the analyzer summary states its evidence. It read
  `analyzed N files, M findings (…)`, and a run whose last file came from cache read
  `M findings (…) (cached)` — the same whether the stage examined the tree or
  replayed all of it, and naming no analyzer set. The summary now reads
  `analyzed N files, replayed R from cache, M findings (…) — analyzer set <id>`,
  where `<id>` is the head of the `analyzer-inputs` cache-key slot (or
  `unidentified (cache off)` when the set has no identity). That slot is the
  analyzer set's identity: a rebuilt analyzer with a new rule moves it and misses
  every entry cached under the old set — the existing DLL-swap tests cover this.

- Fixed: a phantom analyzer finding for a file the project model dropped. A
  rediscovery that removes a file clears that file's findings in every plugin
  ledger, and it clears them before the replacement model is published — so a
  `FileChecked` captured under the OLD model and still queued in the
  analyzers mailbox was folded afterwards and re-reported a finding about a file that is
  no longer part of the build. Nothing checks a dropped path again, so the
  finding stood until the daemon was restarted. The fold now applies
  TestPrune's rule: a result is analyzed only when it carries the generation of
  the model the host currently publishes as available. A file the new model
  still has is re-checked against it and republishes, so refusing costs it
  nothing.

## 0.7.0-alpha.35 - 2026-09-22

- analyzers: supply `CliContext.TypedTree` instead of always passing `None`.
  Every analyzer the host ran received `typedTree = None`, so any rule that
  reads the typed tree returned no findings — an analyzer that needs type
  information and gets `None` reports nothing rather than failing, so such a
  rule was silently disarmed while its own unit tests (which supply a typed
  tree) kept passing. The typed tree now comes from the check results'
  `ImplementationFile`. A checker built without `keepAssemblyContents` raises
  on that access, so it is guarded: such a host still analyzes, with
  typed-tree rules quiet and every other rule running. `createCliContext` and
  its cached reflection moved to module level so the reflection constructor —
  the FCS-version-mismatch workaround, and the part that could plausibly have
  rejected a real typed tree — is covered by a test that drives a genuine
  `FSharpImplementationFileContents` through it.

  The typed tree is WITHDRAWN for the rest of the session if an analyzer proves it
  cannot walk one. An analyzer package compiled against a different
  FSharp.Compiler.Service returns early while `TypedTree` is `None` and never
  touches the differing types; handed a real typed tree it raises
  `MissingMethodException`. MEASURED with g-research 0.23.0 against FCS 43.12.x:
  11 of 13 analyzers raise `Method not found:
  FSharp.Compiler.Symbols.FSharpType.get_BasicQualifiedName()`. The host now
  detects that, warns once naming the cause and the remedy, and re-runs without the
  typed tree — so a mismatched set keeps exactly the behaviour it had instead of
  reporting an assembly mismatch as findings about the file, while a set that CAN
  use a typed tree gets one.

## 0.7.0-alpha.34 - 2026-09-17

- Plugin handlers state what they read and what they commit (breaking plugin API)


## 0.7.0-alpha.33 - 2026-09-16

- the per-file cache key names two inputs that decide the verdict and
  were missing, so changing either can no longer replay an old result.
  `fail-on-severity`: entries are stored after promotion, so a Hint was a pass under one
  threshold and a failure under another behind the same key. `analyzer-config`: every
  `.editorconfig` (MGA `mga_*` keys) and `fsharplint.json` (the FSharpLint shim) between
  the repository root and the file. Salt bumped to `analyzers-merkle-v6`.

## 0.7.0-alpha.32 - 2026-09-16

- the per-file analyzer cache is keyed on the compiler's receipt,
  not on analyzer DLL bytes, so a fresh workspace HITS the shared store. fsc writes the
  portable PDB's absolute path into the PE, so a house-rules DLL built from identical
  source in two checkouts differed byte-for-byte and every fresh workspace missed.
  New internal module `AnalyzerIdentity`: a DLL whose PDB records documents under the
  repository root is `FirstParty`, keyed by (repo-relative document path, the PDB's
  own SHA-256 of that document), the assembly references and the producer `.fsproj`;
  anything else (the NuGet shim and its bundled dependencies) keeps the byte digest.
  Cache-key slot `analyzer-assemblies` (bytes) is replaced by `analyzer-inputs`
  (semantic identity); salt bumped to `analyzers-merkle-v5`, orphaning byte-keyed
  entries. Reload-if-stale still watches the raw-byte digest: a rebuild with an
  unchanged receipt is reloaded into the process and keeps its cache entries.
  Every in-repo document is re-hashed from disk; a refusal (source drifted since
  the build, missing or stale PDB, no producer project, output older than its
  project) turns the cache OFF for that event — the analyzers still run, nothing is
  read or written — with one warning per distinct refusal set, not one per file.

## 0.7.0-alpha.31 - 2026-09-15

- Finish: update SourceLink to fix CVE-2026-62900 restore failure


## 0.7.0-alpha.30 - 2026-09-04

- the analyzers cache key names its file — and the analyzer
  directories — REPO-RELATIVELY, so an analyzer verdict is shared between the
  workspaces of one repository. The analyzer assembly CONTENT hashes, the source and
  the FCS check signature are unchanged inputs. Salt bumped to `analyzers-merkle-v4`,
  orphaning the old absolute-path entries.

## 0.7.0-alpha.29 - 2026-09-01

- chore: rebuild against the updated FsHotWatch core dependency.

## 0.7.0-alpha.28 - 2026-09-01

- chore(deps): rebuild for the synchronized FsHotWatch.Cli dependency release.
## 0.7.0-alpha.27 - 2026-08-29

- Fix: keep analyzer timeout visible behind execution fence
- Fix: fence timed-out synchronous analyzers


## 0.7.0-alpha.26 - 2026-08-13

- fix: unblock the release — coverage floor with real headroom, versions rolled back
- Comment audit: cut AI thinking-out-loud from comments

- Comment audit: cut AI thinking-out-loud from comments


## 0.7.0-alpha.24 - 2026-08-11

- fix: **a promoted finding says `[promoted from warning]`, not `[warning]`.**
  `promoteIfFailing` turns a sub-error finding into an `Error` when it meets the failure
  threshold and prefixes the message with its ORIGINAL severity, so the provenance
  survives — that part is right and is unchanged. The wording was not: a record carrying
  `severity: error` whose message began `[warning]` reads as a contradiction rather than
  as a history. It cost real time — a build-blocking analyzer finding was triaged as
  non-urgent because the text said "warning" while the exit code said otherwise. Both
  facts were true; only the rendering made them look inconsistent.

## 0.7.0-alpha.23 - 2026-08-03

- chore(deps): update dev-tools + external dependencies
- chore: trim stale/historical comments to minimal current-state context


## 0.7.0-alpha.22 - 2026-07-15

- fix!: adapt to verdict-carrying terminals: `Failed` statuses carry a
  `RunVerdict` (summary + measured elapsed), and the deleted `CompleteWithSummary`
  side-channel is replaced by the verdict the terminal itself carries.

- fix!: adapt to core `RunVerdict`: completions carry the analyzer
  summary plus the measured per-file analysis duration.

## 0.7.0-alpha.21 - 2026-07-02

- fix: skip compile items that resolve outside the repo root (NuGet-injected `_content`, e.g. xunit.v3's `DefaultRunnerReporters.fs`). Running F# analyzers (FSharpLint via the shim) over such third-party sources crashed the analyzer host. `create` / `createWithSlowHook` now take a leading `repoRoot: string option`.

## 0.7.0-alpha.20 - 2026-06-24

- chore(deps): bump `FSharp.Analyzers.SDK` 0.36.0 → 0.37.2 (FCS 43.12.201).

## 0.7.0-alpha.19 - 2026-06-17

- fix: a long-lived (warm) daemon now **reloads** the analyzer assembly set when it
  changes on disk, instead of running the set it loaded once at startup. Adding a NEW
  custom analyzer (a new `.fs` + `<Compile>` entry, rebuilt) previously left the warm
  daemon running a stale assembly that lacked the new analyzer — it silently never ran
  and the gate reported green (observed twice in a downstream repo: a new analyzer
  reported 0 violations while it actually had 24). The plugin now tracks the content
  identity of the loaded analyzer assemblies and, at the start of each `FileChecked`,
  loads the current set into a fresh `Client` and swaps it in if the on-disk identity
  differs. Complements the alpha.18 cache-key fix (stale cached *results*) and the
  alpha.17 fail-loud guard (a *0*-analyzer load): this closes the remaining hole — a
  *partial* stale load that's merely missing a newly-added analyzer.

## 0.7.0-alpha.18 - 2026-06-17

- fix: the per-file analyzer cache key now folds in the **content identity** of the
  loaded analyzer assemblies (`analyzer-assemblies` merkle slot), not just the
  configured path strings. Previously a rebuilt analyzer DLL (rule changed / analyzer
  added) on an unchanged path replayed cached per-file verdicts — a long-lived daemon
  could report a real violation as CLEAN while a fresh daemon flagged it (stale-green
  false negative). The `plugin-version` slot is bumped to `analyzers-merkle-v3` so old
  path-only cache entries never match after upgrade. See `docs/adr-011`.

## 0.7.0-alpha.17 - 2026-06-17

- feat: the plugin now exposes per-path analyzer load counts (`LoadedByPath`) on its
  state, so the host can fail the gate when any configured analyzer path loads zero
  analyzers (per-path fail-loud guard) rather than silently passing.

## 0.7.0-alpha.16 - 2026-06-12

- chore: float `FSharp.Compiler.Service` via a `43.*` wildcard and rebuild to bundle the
  refreshed transitive dependencies.

## 0.7.0-alpha.15 - 2026-06-09

- chore: rebuild to bundle updated dependencies


## 0.7.0-alpha.14 - 2026-06-02

- fix: the analyzer summary no longer reports a phantom finding/error count — the `N findings (N errors, …)` line now reflects real diagnostics, so a genuinely clean run can't display a non-zero error count that corresponds to no actual finding (a false-green in the display).

## 0.7.0-alpha.13 - 2026-05-28

- chore: refresh transitive dependencies (CommandTree 0.5.1, CoverageRatchet.Core 0.1.0-alpha.2, TestPrune.Core 4.0.2, FSharpLintAnalyzerShim 0.3.0-alpha.3 via the lint shim).

## 0.7.0-alpha.12 - 2026-05-04

- chore: dependency updates

## 0.7.0-alpha.11 - 2026-04-29

### Added

- **`failOnSeverity` parameter** on `AnalyzersPlugin.create` — promotes analyzer diagnostics at or above the given severity to error. Default: `Hint` (everything is fail-worthy). Configurable via `analyzers.failOnSeverity` in `.fshw.json` (parsed via `FsHotWatch.ErrorLedger.DiagnosticSeverity.fromString`); unknown strings are warned and ignored.
- **`§2a` content-merkle cache key.** AnalyzersPlugin's cache key is now a `merkleCacheKey` of `(plugin-version, analyzer-paths, file, source, fcs-signature)` — fully content-derived, independent of jj commit_id. Cross-file changes invalidate downstream caches via the `fcs-signature` slot (see core's `CheckCache.fcsCheckSignature`).
- **Synchronous analysis on terminal status.** Analysis now awaits inline so the framework's per-event capture window records `Errors` and `EmittedEvents` for §2a cache replay. Previously a fire-and-forget `Async.Start` race produced empty cache entries.

### Changed

- **BREAKING:** `create` no longer takes `getCommitId`. The parameter was unused under §2a's content-merkle keys; removed. New positional order: `analyzerPaths → timeoutSec → failOnSeverity`.
- `promoteIfFailing` simplified with an early-return; unknown `failOnSeverity` strings now log a warning instead of silently defaulting.

## 0.7.0-alpha.10 - 2026-04-26

### Changed

- Per-file error reporting now goes through `PluginCtxHelpers.reportOrClearFile` (core). No behavior change.

## 0.7.0-alpha.9 - 2026-04-25

### Added

- Per-event timeout. `create` accepts a new `timeoutSec: int option`;
  when analyzer work for a `FileChecked` event exceeds the timeout the run
  is recorded as `TimedOut` and the plugin continues with the next event.
  Timeouts are advisory — the orphan `client.RunAnalyzersSafely` call is
  not cancelled, only the result is discarded.

### Changed

- Emit a `"primary"` subtask with a descriptive label per `FileChecked` event,
  and a richer terminal summary of the form
  `analyzed N files, F findings (E errors, W warnings)`.

## 0.7.0-alpha.8 - 2026-04-23

- chore: bump upstream tool versions

## 0.7.0-alpha.7 (2026-04-22)

### Changed

- Extracted two pure helpers from `createCliContext` to enable deterministic
  unit tests for branches that the live-SDK integration tests used to hit
  nondeterministically:
  - `isKnownNonAnalyzerPrefix` — filter for the analyzer DLL exclusion
    list, lifted out of the `ExcludeFilter` closure.
  - `buildAnalyzerProjectOptions` — SDK-reflection that builds the
    `AnalyzerProjectOptions` instance, testable with `None` / throwing-ctor
    fixtures instead of requiring a real loaded SDK.
- `InternalsVisibleTo FsHotWatch.Tests` added so the unit tests can reach
  these helpers without bloating the package's public API.

---

## 0.5.0-alpha.1 (2026-04-12)

### Changed

- Run parse-only analyzers instead of skipping files without full type-check results — passes `null` for `checkResults` in `CliContext`, enabling syntax-only analyzers to run on all files

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure release. No public API changes.

- Fix per-platform coverage threshold for `AnalyzersPlugin.fs` — macOS reports 83% branch coverage vs Linux 66% due to reflection-based `CliContext` branches
- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No API changes.

- Add MIT license; add SourceLink; replace bespoke scripts with shared NuGet tools and reusable CI workflows

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- F# analyzer host using reflection-based `CliContext` (FCS version mismatch workaround)
