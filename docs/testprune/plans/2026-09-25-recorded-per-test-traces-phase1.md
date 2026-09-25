# Recorded per-test traces, phase 1 (record only) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record a verifying trace for every executed test: which TestPrune symbols (at which content hash)
it executed, which repository files it read, and which fixture, pool and static-init scopes it inherited.
Store those traces durably and measure them against the phase-1 done-bar. **Test selection does not change
in this phase.**

**Architecture:** A post-compile Mono.Cecil weaver writes a woven copy of a test project's build output into
`bin/Traced/<tfm>/`. That copy hardlinks every unchanged file and references a tiny recorder assembly. The
recorder attributes each probe hit to the current xUnit v3 test through `TestContext.Current`, which is
AsyncLocal-based, or through an explicit scope a host sets. At process exit it writes one NDJSON dump per
process. An ingester joins the dump's probe ids, through the weave manifest, to TestPrune symbols and their
content hashes. It writes the result to a separate SQLite trace database that never shares a file with the
symbol index. FsHotWatch gains an opt-in `tests.traces` mode that launches the woven copy and ingests the
dumps. The mode first applies to full runs (`confirm`/nightly), then to every run.

**Tech Stack:** F# (net10.0 for tooling, net8.0 for the recorder); Mono.Cecil 0.11.6; System.Reflection.Metadata
(in the box); Microsoft.Data.Sqlite (already a Core dependency); xUnit v3 on Microsoft Testing Platform v2;
Unquote; FsHotWatch plugin framework.

## Global Constraints

- Open-source repositories: no private tracker ids, no consumer project names, no session links in code,
  comments, commits, changelogs or docs. Commit trailer:
  `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- VCS is jj. Always `jj commit -m "…"`. For a message with backticks, `$` or quotes, use
  `jj describe --stdin <<'MSG'` … `MSG`, then `jj new`.
- F# only, formatted with Fantomas, FSharpLint clean, `TreatWarningsAsErrors=true`,
  `GenerateDocumentationFile=true` on packable projects (repository convention).
- `TestPrune.Trace.Recorder` targets **net8.0**. Its only package dependency is **FSharp.Core pinned to
  8.0.403** (`DisableImplicitFSharpCoreReference` plus an explicit `VersionOverride`). Loaded into a consumer's
  test process, it must never raise that process's FSharp.Core floor.
- `TestPrune.Trace` targets **net10.0** and references **Mono.Cecil 0.11.6**.
- The weaver **refuses optimized assemblies** (`DebuggableAttribute` without
  `DisableOptimizations`/`0x100`). Release builds inline small functions across assemblies, so their entry
  probes never fire.
- **Every woven PDB must decode with System.Reflection.Metadata**, and **every touched non-generic method must
  JIT-prepare in the test app's own runtime**. Any failure refuses the project, which then runs untraced
  exactly as today.
- **No module-value `ldsfld` probe.** In Debug builds, F# reads module values through `get_x()`, so the
  method-entry probe on the getter records them.
- `[<Literal>]` and `inline` symbols cannot be probed. Phase 1 records nothing for them; phase 2 handles them
  with a one-hop static rule.
- Phase 1 never changes a verdict or a selection. A recording failure is logged and stored as a refused or
  incomplete trace, never raised.
- A trace is either complete or explicitly marked incomplete with reasons. Nothing is silently partial.
- The trace database is a **separate file** with its own `TraceSchemaVersion` and forward-only migrations.
  A TestPrune.Core `SchemaVersion` bump never touches it.
- Overhead is measured as **process CPU time (user+sys)**, never wall time.
- Everything is behind a flag. With no `tests.traces` key in `.fshw.json`, FsHotWatch behaves byte-for-byte as
  it does today.
- Verification:
  - Inner loop: `dotnet build`, then
    `dotnet run --project tests/<Project> --no-build -- --filter-class <Class>`.
  - Gate: each repository's `mise run ci`. Read the fshw verdict file as well as the exit code.
  - Never pipe a gating command into `tail`, `head` or `grep`. Redirect the output to a file, then read the
    file and the exit status separately.

---

## Background: what the spikes established

This section is here so an implementer does not re-derive it. Every number was measured with the prototypes
this plan productizes.

- **Mechanism:**
  - A post-compile IL weaver plus an AsyncLocal-attributed recorder.
  - MS CodeCoverage and Coverlet keep process-global counters, which are unusable per test when tests run in
    parallel.
  - EventPipe has no per-call method-enter event.
  - Profiler ELT hooks cannot read AsyncLocal attribution.
- **Probe set (final):**
  - method entry in product assemblies;
  - union-case probes: the callee-side `get_Tag` return; the union's tag-field `ldfld` outside the tag getter;
    `isinst` on a case class, recorded only on success; `castclass`/`ldfld` on a case class; `_unique_X`
    singleton reads;
  - type probes: `isinst` recorded on success, `castclass`, `unbox(.any)`, the FSharp.Core
    `UnboxGeneric`/`UnboxFast`/`TypeTestGeneric`/`TypeTestFast` intrinsics, and `ldfld(a)` of a product type's
    field outside the declaring type;
  - a `.cctor` try/finally depth counter that routes hits to a `S:static-init` scope.
- **Test assemblies** are woven **sites-only**: no method-entry probes, but their matches and type tests on
  product types are recorded. Full test weaving is optional and costs about 15 % CPU.
- **Two weaver bugs** were caught only by JIT verification:
  - a union case literally named `Tag` has a static `get_Tag` returning the case singleton;
  - a case field named `tag` gets a backing field `_tag`.

  Fixes:
  - the tag getter is the **instance** `get_Tag` returning `int32`;
  - the tag field is the field that getter returns, never a field chosen by name.
- **PDB bug:** F# emits a hidden sequence point at IL offset == code size. Cecil cannot bind it to an
  instruction and keeps the raw offset, so after inserting a probe it writes a negative delta into the portable
  PDB. SRM, and therefore MS CodeCoverage, then drops the whole method.
  - Fix: before inserting, collect every unbound sequence point; afterwards, re-create each one at
    `offset + inserted bytes`.
  - With the fix, coverage is identical with and without weaving, so **traces record in the same run as
    coverage**.
- **Attribution:**
  - Isolated vs parallel on 80 tests showed **0** extra-in-parallel ids.
  - The only ids missing in parallel runs are once-per-process code: `.cctor`s, closure-singleton
    constructors, and a handful of memoised lazies.
  - Unattributed (`A:ambient`) hits: 0.
- **Overhead:** under CPU-time noise for method-entry plus site probes. Wall time is bimodal and is set by a
  suite's fixed-timeout tests, not by probes; that is why the done-bar uses CPU.
- **Join:**
  - Match by name within the same file **before** matching by line. Line matching mis-attributed a record
    field to its neighbour when the index and binary trees differed.
  - Union-generated members map to cases or are dropped.
  - `_X` and `X@DebugTypeProxy` classes map to case X.
  - Hit-level mapped rate on a ~9k-test suite: 98.1 % of method ids, 99.4 % of case ids and 99.0 % of type
    ids.
- **Storage:**
  - Pairs layout: about 15 MiB per 8k unit traces; selecting 200 changed versions takes 0.6 ms.
  - Unmapped executed methods become file-level entries, which is sound.

## Decisions (with reasons)

### D1. Packaging

| Piece | Where | Why |
|---|---|---|
| **Recorder** | New package `TestPrune.Trace.Recorder` (`src/TestPrune.Trace.Recorder/`), F#, net8.0, FSharp.Core pinned 8.0.403, no other dependencies. | It is loaded *into the consumer's test process*, so it must not raise any floor there. net8.0 loads in net8+ hosts. F# keeps one language in the repo and the release tooling (`fssemantictagger`, Fantomas, FSharpLint and the coverage ratchet are F#-shaped). The prototype's C# recorder used nothing F# lacks: `Interlocked.Or`, `Volatile.Read` on array-element byrefs, `[<ThreadStatic>]`, `AsyncLocal`. |
| **Weaver, shadow bin, JIT verify driver, manifest, dump reader, joiner, trace store, ingestion, census** | New package `TestPrune.Trace` (`src/TestPrune.Trace/`), F#, net10.0; references `TestPrune.Core`, `TestPrune.Trace.Recorder` and Mono.Cecil. | It keeps Mono.Cecil and a second SQLite lifecycle out of `TestPrune.Core`, which the CLI, FsHotWatch and every extension consume. The joiner reads Core only through the existing `Ports.SymbolStore` port. The trace store has its own schema-version lifecycle; a separate package makes that boundary a compile-time fact. The weaver finds the recorder at `typeof<Probes>.Assembly.Location`, so the weaver and recorder versions can never skew. |
| **Measurement verbs** | A new tool project `src/TestPrune.Trace.Cli/` (command `test-prune-traces census\|audit\|overhead\|file-census`), released under the same `trace-v` tag as the two libraries. | The done-bar needs repeatable, scriptable measurements that read the trace DB. They cannot live in the existing `test-prune` CLI: it releases under the `core-v` tag together with Core, and TestPrune.Trace depends on Core, so a core-CLI dependency on Trace would make the release order circular. FsHotWatch's verb surface was deliberately collapsed to `check`/`status`, so they do not belong there either. |
| **Host integration** | FsHotWatch `src/FsHotWatch.TestPrune/Traces.fs` (new) plus small hooks in `TestPrunePlugin.fs`, `TestMode.fs` and `DaemonConfig.fs`. | fshw owns the launch, the run directory, CTRF outcomes, the gate slot and the run/mode lifecycle. TestPrune.Trace stays host-agnostic. |

**How the recorder gets into the test app:** the shadow-bin step rewrites the shadow copy's
`<App>.deps.json`:

- It adds a `project`-type library `TestPrune.Trace.Recorder/<version>` whose runtime asset is
  `TestPrune.Trace.Recorder.dll`.
- It lists that library as a dependency of the root target library.
- It copies the DLL into the shadow directory.
- The original `bin/Debug/<tfm>/<App>.deps.json` is never written: the shadow file is a fresh file, not a
  hardlink.
- If a consumer's deps.json already lists the recorder (it referenced the package directly), the step checks
  that the version equals the weaver's. On a mismatch it refuses with `RecorderVersionSkew`.
- The recorder is copied **without its PDB**. MS CodeCoverage skips modules without symbols, so the recorder
  never appears in a consumer's coverage report. `[<ExcludeFromCodeCoverage>]` is deliberately not used: it
  would also hide the recorder from this repository's own coverage ratchet. Task D1 verifies the claim on a
  real coverage run. If it fails, the README's fallback is a `<ModulePath>` exclusion in the consumer's
  coverage settings.
  Without this, recorder sources outside the consumer's repository would be `CoveragePathRejected` rows, which
  widen selection.

**How attribution hooks xUnit v3 / MTP:**

- The recorder reflects once over `Xunit.TestContext, xunit.v3.core`. It binds `TestContext.Current` to a
  delegate and the `ITestContext.Test/TestClass/TestMethod/TestCollection` properties to `PropertyInfo`s. It
  takes no compile-time xUnit dependency: the same recorder serves any xUnit v3 version whose public
  `ITestContext` shape is unchanged.
- Scope precedence per hit is:
  1. static-init depth > 0 → `S:static-init`;
  2. an explicit scope (`Scopes.Enter`) → that key;
  3. an xUnit test → `T:<ITest.UniqueID>`;
  4. a class (fixture construction) → `C:<class>`;
  5. a collection → `L:<collection>`;
  6. an assembly → `A:assembly`;
  7. otherwise → `A:ambient`.
- **No per-test flush hook in phase 1.** The recorder dumps once, at `ProcessExit`.
  - xUnit v3 publishes `TestState` only on a *new* context object created after the test finished, and MTP
    registers in-process extensions only through a build-time generated entry point. A per-test flush would
    need a compile-time package reference in every consumer test project, and the scope would still be
    flushed before class disposal ran.
  - An exit-time dump attributes every late continuation of a test to that test. That over-attributes, which
    is sound, so there are no "late hits" to mark.
  - A process killed before exit writes no dump. Every test of that process is then recorded as having *no*
    trace (reason `recorder-no-output` on the run), which is also sound.
  - Memory is O(tests × ids/64) words: about 113 MB for a 9k-test project at 100k ids. The done-bar measures
    it. Two-level sparse bitsets are the documented fallback if it bites; that is recorded as an ADR in
    Task 13.
- **Test outcome** (passed/failed/skipped) is **not** read from the recorder. It comes from the CTRF report
  fshw already reads as the verdict source. It is joined on the test's display name, then on (class, method).

### D2. Shadow bin

- **Location:** `<projectDir>/bin/Traced/<tfm>/`, a sibling of `bin/Debug/<tfm>/`, at the same depth. Tests
  that probe upward for a repository marker (`*.sln`, `mise.toml`, `.fshw.json`) or navigate relative to
  `AppContext.BaseDirectory` behave identically. A scratch directory outside the repository made at least
  15 repo-probing TestPrune tests fail.
- **Build flow** (`ShadowBin.prepare`), run once per traced project launch:
  1. Mirror `bin/Debug/<tfm>/` into `bin/Traced/<tfm>/` with hardlinks (`link(2)`, or `CreateHardLinkW` on
     Windows), falling back to a copy on `EXDEV`. Mirroring re-links every run. A rebuilt `bin/Debug` file is a
     new inode, and a stale hardlink would otherwise keep the old bytes. Files present in the shadow but not in
     the source are deleted.
  2. Select the weave set: every `*.dll` in the mirror whose portable PDB lists at least one document under
     the repository root. This auto-detects "built from this repo" with no configuration. The test project's
     own assembly is woven `SitesOnly` by default (`weaveTests: "full"` opts in to method probes); every other
     repo assembly is woven `Full`.
  3. Refuse the project if any weave-set assembly is optimized.
  4. Compute the weave key: SHA-256 over the weaver version, the recorder version, the weave mode per
     assembly, and each weave-set input's `.dll` + `.pdb` bytes. On a cache hit under
     `<projectDir>/obj/traced/<key>/`, reuse the woven files. On a miss, weave into that directory.
  5. **Replace, never write through, a hardlink.** Woven assemblies, their PDBs, the patched deps.json and the
     stamp are written to a temp file and moved over the mirror entry with `File.Move(tmp, dst, true)`. A rename
     replaces the directory entry. Writing into the hardlinked file would corrupt `bin/Debug`. A test pins
     this.
  6. SRM-decode every woven PDB. On a new weave key, JIT-verify by launching the shadow apphost with
     `DOTNET_STARTUP_HOOKS` pointing at the recorder and `TESTPRUNE_TRACE_VERIFY=<report.json>`. The
     recorder's startup hook `PrepareMethod`s every touched method in the test app's own runtime, including
     shared frameworks such as ASP.NET Core, writes the report and exits before `Main`. The verdict is cached in
     the weave-key directory.
  7. Write `bin/Traced/<tfm>/.testprune-trace.json` (the stamp): weave key, manifest path, id count, and
     verify verdict.
- **Invalidation:** the weave key is content-addressed, so a rebuild with new bytes is a miss and anything
  else is a hit. `obj/traced/` holds at most the 3 most recent keys; older ones are deleted at the end of
  `prepare`. `bin/Traced` and `obj/traced` live under `bin/` and `obj/`, which the fshw watcher, `.gitignore`
  and build-output freshness checks already ignore.

### D3. Storage that survives TestPrune schema bumps

- **A separate SQLite file.** fshw uses `.fshw/test-traces.db` and the CLI uses `.test-prune-traces.db`. Core
  deletes the *index* file on a `SchemaVersion` bump, and `Ports.PluginStore` tables live inside that file.
  That is why the plugin-store seam cannot hold data that costs a full recording run to rebuild.
- **Forward-only migrations.** `TraceSchemaVersion` is stored in `PRAGMA user_version`. Opening an older file
  applies each missing migration in one transaction. A **newer** file is refused with
  `TraceSchemaNewerThanConsumer` and left untouched. The file is **never deleted** by code.
- **Keys survive re-index.** Symbols are referenced by `full_name` (and `symbol_versions` interns
  `(full_name, content_hash)`), never by `symbols.id`, which a re-index reassigns.
- **Hash scheme is part of the fingerprint E.** If Core changes what a content hash means (for example, the
  split Type hash in Task 0), every trace recorded under the old scheme becomes a fingerprint mismatch rather
  than a spurious "verifies". Phase 1 uses Core's `Database.SchemaVersion` as the hash-scheme number, because
  any change to hash semantics must bump it (stored hashes are recomputed).
- **Tables** (from the design, normalised on *scope*; see Task 3 for the DDL):
  - `trace_runs`: one per (run, test project);
  - `trace_scopes`: every recorded scope of a run (T/C/L/A/P/S). Fixture and pool scopes are stored once;
  - `symbol_versions`: interned;
  - `trace_entries (symbol_version_id, scope_id)`;
  - `trace_inputs (scope_id, kind, key, hash)`: files, existence probes, directory listings and file-level
    entries;
  - `trace_tests`: the current trace per `(test_key, env_fingerprint)`, with status, `complete` and
    `incomplete_reasons`;
  - `trace_test_scopes (test_id, scope_id)`: the test's own T scopes (theory rows are unioned) plus every
    inherited scope.
- **Deviations from the design's §4 sketch, with reasons:**
  - Entries are keyed by scope, not by test. The same class/pool scope content is otherwise duplicated into
    every test of the class.
  - `trace_runs` is per project, because E is per test project (deps.json).
  - There is no `late_hits` column, because the exit-time dump has no late hits (D1).
- **Fingerprint E:** SHA-256 over canonical JSON of:
  - the runtime description (reported by the recorder);
  - the SHA-256 of the *original* `<App>.deps.json`;
  - OS and architecture;
  - the recorder and weaver versions;
  - the hash scheme;
  - SHA-256 of each configured `fingerprintInputs` file (sorted);
  - the value of each configured `fingerprintEnv` variable (sorted; a hash of the value is stored, never the
    value).

### D4. FsHotWatch integration

**Config** (`.fshw.json`):

```json
{
  "tests": {
    "traces": {
      "record": "full-runs",
      "db": ".fshw/test-traces.db",
      "weaveTests": "sites",
      "fingerprintInputs": ["global.json"],
      "fingerprintEnv": ["ASPNETCORE_ENVIRONMENT"]
    },
    "projects": [
      { "project": "Some.Tests", "traces": false }
    ]
  }
}
```

- `record`: `"off"` (the default when the key is absent), `"full-runs"` or `"every-run"`.
- A per-project `"traces": false` opts that project out.

**When it records:**

- The question is `TestMode.recordsTraces policy mode`, and it lives in `TestMode.fs`, the only file allowed to
  branch on the mode.
- `full-runs` records under `PassThrough` (`confirm`, nightly).
- `every-run` records under both modes: partial runs replace only the traces of tests they executed.
- Phase 1a ships `full-runs`. Phase 1b flips a consumer to `every-run` only after the done-bar holds on full
  runs (Task F5).

**Launch:**

- For a traced project, fshw calls `TraceSession.prepareProject`, which runs D2.
- It then launches the **shadow apphost** directly (`bin/Traced/<tfm>/<AssemblyName>`), not `dotnet run`.
  `dotnet run --no-build` resolves the original output path. The shadow apphost carries the shadow deps.json
  and runtimeconfig.
- App arguments are derived from the configured `dotnet run …` args. `run`, `--project/-p X`, `--no-build`,
  `--no-restore`, `-c/--configuration X` and `-f/--framework X` are dropped, as is every standalone `--`
  token. Filter, CTRF and coverage arguments are appended unchanged.
- Any other token before the first `--` is a `dotnet run` option fshw does not understand. The project then
  falls back to the normal untraced launch, with reason `unrecognized-run-option:<token>`.
- `DOTNET_ROOT` is set from the environment or from the resolved `dotnet` muxer's directory. `dotnet run`
  sets it for its child, and an apphost launched directly needs it.
- Environment added:
  - `TESTPRUNE_TRACE_OUT=<runDir>/traces/<project>/`;
  - `TESTPRUNE_TRACE_IDS=<id count>`;
  - `TESTPRUNE_TRACE_REPO_ROOT=<repoRoot>`.

**Ingestion:**

- It runs after the parallel project runs finish, serially, next to coverage ingest.
- For each traced project, fshw calls `TraceIngest.ingest` with:
  - the dump directory;
  - the manifest;
  - the project's CTRF outcomes;
  - the launch's input tree hash and the tree hash at ingestion;
  - `Ports.toSymbolStore db`.
- If the tree moved during the run, the run is stored `status = tree-moved` and every test of it is
  `complete = 0`, reason `tree-moved`: the index no longer describes the binary.

**Failure handling:**

- Every trace step is inside a `try` that turns an exception into a `refused` or `failed` `trace_runs` row and
  a warning line. A refused *prepare* falls back to the untraced launch, so the project still runs and the
  verdict is exactly today's.
- Incomplete-trace reasons are a closed union, `IncompleteReason`, each with one producer:
  - `NoOutcome` (the test is not in the CTRF report);
  - `NotPassed of status` (failed, skipped or other);
  - `SourceDrift of file` (the PDB document hash differs from the file on disk);
  - `NotIndexed of file` (the executed code's file is absent from the index);
  - `ChildProcessUntraced of fileName` (the test started a process with no dump);
  - `RecorderOverflow` (a probe id ≥ `TESTPRUNE_TRACE_IDS`);
  - `TreeMoved`;
  - `DumpRejected of reason`;
  - `UnmappedCode of detail` (executed code the joiner mapped to neither a symbol nor a file).

### D5. Consumer side (kept out of this repository)

A consumer with pooled in-process web servers needs to do four things; the concrete tasks are in a separate
consumer plan:

- send the current scope key on test HTTP and browser clients (`X-Test-Trace-Scope`);
- install a first-in-pipeline Test-only middleware that calls `Scopes.Enter` with it;
- run pool construction under an explicit `P:` scope;
- link each fixture that acquires a pooled server to that server's pool scope.

This repository provides:

- the stable reflection contract (`TestPrune.Trace.Recorder.Scopes`: `Enter`, `Exit`, `CurrentKey`,
  `LinkCurrentTo`), documented in the README. A consumer can bind it by reflection, with no package reference,
  and it is inert when the recorder is absent;
- child-process inheritance, implemented in the recorder's process-start shims (Task 6), so consumers write
  nothing for it.

### D6. Phase-1 done-bar: every item is a command with a threshold

| Bar | Measured by | Threshold |
|---|---|---|
| Traced coverage | `test-prune-traces census --run <id>`: `tracedExecuted / executed` over CTRF-executed (non-skipped) tests | ≥ 0.99 per project, on 3 consecutive full runs |
| Unattributed hits | the same census: `ambient / totalHits` from the recorder's per-bucket counters | < 0.001, or each ambient id listed with its symbol and explained in the results file |
| Pool-scope census | the same census: per `P:` scope, distinct ids, mapped symbols and linked tests | the table exists for every project that declares a pool; its size is recorded |
| Isolated-vs-parallel audit | `test-prune-traces audit --project-dir … --sample 0.01 --seed N` | 0 extra-in-parallel ids; missing-in-parallel only `StaticCtor`/`Generated` rows or ids flagged by the order-instability list |
| CPU overhead | `test-prune-traces overhead --project-dir … --reps 3` (interleaved, `getrusage(RUSAGE_CHILDREN)`) | traced median CPU ≤ 1.15 × untraced median CPU, per project |
| File census | `test-prune-traces file-census --project-dir …` | symmetric difference between "fails outside the repo" and "trace has a repo file input" ≤ 5 % of their union |
| PDB / JIT | built into `prepare` | 0 SRM decode failures, 0 invalid methods |
| Coverage parity | Task D1 step: cobertura of a traced full run vs an untraced one | identical per-file line and branch counts; no recorder module |

---

## File structure

**TestPrune (this repository)**

| File | Responsibility | Task |
|---|---|---|
| `src/TestPrune.Trace.Recorder/TestPrune.Trace.Recorder.fsproj` | recorder package, net8.0 | 1 |
| `src/TestPrune.Trace.Recorder/Contract.fs` | probe/shim method names shared with the weaver (literals) | 1 |
| `src/TestPrune.Trace.Recorder/Scope.fs` | `Scope` bitset + metadata | 2 |
| `src/TestPrune.Trace.Recorder/ContextSource.fs` | `IContextSource`, xUnit v3 reflection binding | 2 |
| `src/TestPrune.Trace.Recorder/RecorderState.fs` | per-process state: resolve, hit, counters | 2 |
| `src/TestPrune.Trace.Recorder/DumpWriter.fs` | NDJSON dump at exit | 2 |
| `src/TestPrune.Trace.Recorder/Probes.fs` | public static probe entry points, `Scopes` API, runtime bootstrap | 1 (stub) → 2 |
| `src/TestPrune.Trace.Recorder/Io.fs` | file-read shims | 6 |
| `src/TestPrune.Trace.Recorder/ProcessShims.fs` | child-process shims | 6 |
| `src/TestPrune.Trace.Recorder/StartupHook.fs` | JIT-verify mode (`DOTNET_STARTUP_HOOKS`) | 8 |
| `src/TestPrune.Trace/TestPrune.Trace.fsproj` | tooling package, net10.0 | 1 |
| `src/TestPrune.Trace/Model.fs` | shared records: manifest rows, dumps, outcomes, reasons | 1 |
| `src/TestPrune.Trace/DumpReader.fs` | parse NDJSON dumps | 2 |
| `src/TestPrune.Trace/TraceStore.fs` | trace DB schema, migrations, writes, GC | 3 |
| `src/TestPrune.Trace/Manifest.fs` | manifest TSV read/write | 4 |
| `src/TestPrune.Trace/PdbCheck.fs` | SRM sequence-point decode check | 4 |
| `src/TestPrune.Trace/Weaver.fs` | Cecil driver, method-entry probe, SP fix, pass pipeline | 4 |
| `src/TestPrune.Trace/SiteProbes.fs` | union-case/type site probes, tag callee probe, `.cctor` wrap | 5 |
| `src/TestPrune.Trace/Redirects.fs` | IO/process call-site redirection table + pass | 6 |
| `src/TestPrune.Trace/Joiner.fs` | manifest row → symbol/file-level/dropped | 7 |
| `src/TestPrune.Trace/HardLink.fs` | `link(2)`/`CreateHardLinkW` + copy fallback | 8 |
| `src/TestPrune.Trace/DepsJson.fs` | deps.json recorder injection | 8 |
| `src/TestPrune.Trace/ShadowBin.fs` | mirror, weave-set, cache, stamp, verify | 8 |
| `src/TestPrune.Trace/Fingerprint.fs` | environment fingerprint E | 9 |
| `src/TestPrune.Trace/TraceIngest.fs` | dumps + outcomes + join → store | 9 |
| `src/TestPrune.Trace/TraceSession.fs` | host facade: prepare → launch spec → ingest | 10 |
| `src/TestPrune.Trace/Census.fs` | done-bar metrics from DB + dumps | 11 |
| `src/TestPrune.Trace/Audit.fs`, `Overhead.fs`, `FileCensus.fs` | process-driving measurements | 12 |
| `src/TestPrune.Trace.Cli/{TestPrune.Trace.Cli.fsproj,Program.fs}` | `test-prune-traces …` verbs (tool, `trace-v` tag) | 11, 12 |
| `tests/TestPrune.Trace.Tests/*.fs` | tests for all of the above | 1–12 |
| `tests/TraceFixtures/src/FxLib`, `src/FxDriver`, `tests/FxTests` | built fixtures laid out as a mini-repository (F# shapes; an xUnit v3 project for attribution) | 1 |
| `docs/adr/0005-recorded-traces-exit-dump.md`, `0006-trace-db-separate-file.md` | decisions | 13 |

**FsHotWatch**

| File | Responsibility | Task |
|---|---|---|
| `src/FsHotWatch.Cli/DaemonConfig.fs` | parse `tests.traces` + per-project opt-out | F1 |
| `src/FsHotWatch.TestPrune/TestMode.fs` | `recordsTraces` | F2 |
| `src/FsHotWatch.TestPrune/Traces.fs` (new) | settings type, launch spec, ingest glue | F1, F3, F4 |
| `src/FsHotWatch.TestPrune/TestPrunePlugin.fs` | two call sites in `executeTests` | F4 |

---

## Cross-task contracts

These are fixed in Task 1, so tasks that run in parallel agree on names.

**Recorder public surface** (`namespace TestPrune.Trace.Recorder`; every type `[<AbstractClass; Sealed>]` with
static members, so each is a stable CLR static class):

```fsharp
type Probes =
    static member Hit: id: int -> unit
    static member HitIfNotNull: value: obj * id: int -> unit      // weaver emits: dup; ldc id; call
    static member HitIfTrue: value: bool * id: int -> unit
    static member HitTag: tag: int * baseId: int -> unit           // records baseId + tag
    static member EnterStatic: unit -> unit
    static member ExitStatic: unit -> unit

/// The consumer contract. Bind by reflection or by package reference. Inert when inactive.
type Scopes =
    static member Enter: key: string -> unit        // AsyncLocal override for the current flow
    static member Exit: unit -> unit
    static member CurrentKey: unit -> string        // "T:<uid>" | "C:…" | … | null when inactive
    static member LinkCurrentTo: scopeKey: string -> unit   // current scope inherits scopeKey
```

**Dump format** (`testprune-trace/1`, one file per process, written as `trace-<pid>.ndjson.tmp` then renamed to
`trace-<pid>.ndjson`):

```text
{"format":"testprune-trace/1","pid":123,"parentScope":null,"runtime":".NET 10.0.0","os":"OSX","arch":"Arm64","ids":102651,"cpuMs":43120,
 "counters":{"test":0,"class":0,"collection":0,"assembly":0,"override":0,"staticInit":0,"ambient":0,"overflow":0}}
{"key":"T:abc","test":{"class":"Ns.M+C","method":"m","display":"Ns.M+C.m"},"parents":["C:Ns.M+C","L:Test collection for Ns.M+C","A:assembly"],
 "links":[],"ids":[5,17,19],"inputs":[{"kind":"read","path":"/repo/x.json"}],"children":[{"pid":456,"file":"dotnet","env":true}]}
{"end":true}
```

A dump without the `{"end":true}` line is rejected (`DumpRejected "truncated"`).

**Manifest** (`manifest.tsv`, one row per probe id, tab-separated, no header):
`id  kind  assembly  type  member  document  firstLine  lastLine`, where `kind` is one of `user`, `gen`,
`cctor`, `case` or `type`. `documents.tsv` has `path  sha256` (the PDB's recorded source hash, lowercase hex).

**Shared model** (`src/TestPrune.Trace/Model.fs`):

```fsharp
module TestPrune.Trace.Model

type ProbeKind =
    | UserMethod
    | GeneratedMethod
    | StaticCtor
    | UnionCase
    | TypeUse

type ManifestRow =
    { Id: int
      Kind: ProbeKind
      Assembly: string
      TypeName: string   // CLR full name, nested types with '+'
      Member: string     // method name, or case name for UnionCase, "" for TypeUse
      Document: string option   // absolute path from the PDB
      FirstLine: int
      LastLine: int }

type WeaveMode =
    | Full
    | SitesOnly

type Manifest =
    { Rows: ManifestRow[]
      Documents: Map<string, string>  // path -> sha256
      IdCount: int }

/// Named so no case shadows a core module (`List`) or a record label (`Exists`).
type InputKind =
    | FileRead
    | ExistenceProbe
    | DirectoryListing

type RecordedInput = { Kind: InputKind; Path: string }
type ChildNote = { Pid: int; FileName: string; EnvInjected: bool }

type TestIdentity = { Class: string; Method: string; Display: string }

type RecordedScope =
    { Key: string
      Test: TestIdentity option
      Parents: string list
      Links: string list
      Ids: int[]
      Inputs: RecordedInput list
      Children: ChildNote list }

type HitCounters =
    { Test: int64; Class: int64; Collection: int64; Assembly: int64
      Override: int64; StaticInit: int64; Ambient: int64; Overflow: int64 }

type ProcessDump =
    { Pid: int
      ParentScope: string option
      Runtime: string
      Os: string
      Arch: string
      IdCount: int
      CpuMs: int64
      Counters: HitCounters
      Scopes: RecordedScope list }

type OutcomeKind =
    | Passed
    | Failed
    | Skipped
    | OtherOutcome

type TestOutcome = { Name: string; Outcome: OutcomeKind }   // CTRF "name" verbatim

type IncompleteReason =
    | NoOutcome
    | NotPassed of OutcomeKind
    | SourceDrift of file: string
    | NotIndexed of file: string
    | ChildProcessUntraced of fileName: string
    | RecorderOverflow
    | TreeMoved
    | DumpRejected of reason: string
    /// Executed code the joiner could map to neither a symbol nor a source file.
    | UnmappedCode of detail: string
```

---

## Task graph and parallel groups

```text
G0:  T0 (external, in flight)  ∥  T1
G1:  T2  ∥  T3  ∥  T4  ∥  F1  ∥  F2  ∥  F3        (and consumer C1–C3, see the consumer plan)
G2:  T5 (T4)  ∥  T6 (T2,T4)  ∥  T7 (T2,T4)  ∥  T8 (T2,T4)
G3:  T9 (T3,T7)
G4:  T10 (T5,T6,T8,T9)  ∥  T11 (T9)
G5:  T12 (T10)
G6:  T13 release (T10–T12)
G7:  F4 (T13, F1–F3)            → FsHotWatch release
G8:  D1 dogfood done-bar on TestPrune (F4 released + pinned)   ∥  consumer C4–C6
G9:  F5 every-run (D1 bar met on full runs)
```

Each task ships on its own, behind a flag:

- New packages are inert until a host calls them.
- F1–F3 are dormant code paths until F4 wires them.
- F4 is gated on `tests.traces.record ≠ off`.
- Consumer changes are no-ops while the recorder is absent.

---

## Task 0: Split the Type content hash (in flight elsewhere; interface only)

This task is being done separately. Phase 1 does **not** block on it. This section fixes the interface this
plan relies on.

**Interface consumed by this plan:**

- `TestPrune.Database.SchemaVersion` is bumped (15 → 16) by the change, because stored `content_hash` values
  change meaning. Task 9 folds `SchemaVersion` into the fingerprint E as the hash scheme. So every trace
  recorded before Task 0 lands becomes an R5 fingerprint mismatch, not a spurious verification.
- `SymbolInfo.ContentHash` for `Kind = Type` covers the type **header** only: name, attributes, type
  parameters, base and interface list. Union cases (`Kind = DuCase`), record fields and members each carry
  their own range's hash. The joiner (Task 7) maps a type probe to the `Type` symbol and a case probe to the
  `DuCase` symbol either way.
- If Task 0 also introduces per-field symbols (for example `Kind = Property` for record fields), Task 7's
  `TypeUse` rule stays "map to the Type". A follow-up (not in phase 1) may split a field read into its field
  symbol. That follow-up allocates per-field ids, so it is a re-weave. The weave key changes with the weaver
  version, so the re-weave happens automatically.

No steps here.

---

## Task 1: Scaffold the two packages, the test project and the fixtures

**Files:**

- Create: `src/TestPrune.Trace.Recorder/TestPrune.Trace.Recorder.fsproj`
- Create: `src/TestPrune.Trace.Recorder/Contract.fs`
- Create: `src/TestPrune.Trace.Recorder/Probes.fs` (stub bodies)
- Create: `src/TestPrune.Trace.Recorder/CHANGELOG.md`
- Create: `src/TestPrune.Trace/TestPrune.Trace.fsproj`
- Create: `src/TestPrune.Trace/Model.fs` (the contract in "Cross-task contracts", verbatim)
- Create: `src/TestPrune.Trace/CHANGELOG.md`
- Create: `tests/TestPrune.Trace.Tests/TestPrune.Trace.Tests.fsproj`
- Create: `tests/TestPrune.Trace.Tests/ScaffoldTests.fs`
- Create: `tests/TestPrune.Trace.Tests/Fixtures.fs` (paths to the built fixtures)
- Create: `tests/TraceFixtures/src/FxLib/{FxLib.fsproj,Types.fs,Values.fs,Logic.fs}`
- Create: `tests/TraceFixtures/src/FxDriver/{FxDriver.fsproj,Program.fs}`
- Create: `tests/TraceFixtures/tests/FxTests/{FxTests.fsproj,AttributionTests.fs}`

The fixtures are laid out as a miniature repository (`src/`, `tests/`) so TestPrune's own indexer can index
`tests/TraceFixtures` as a repository root; Task 7 relies on that.
- Modify: `TestPrune.slnx`, `Directory.Packages.props`, `semantic-tagger.json`, `.fshw.json`

**Interfaces:**

- Produces: `TestPrune.Trace.Recorder.Contract` literals (below). Tasks 2, 4, 5, 6 and 8 use these names and
  never spell a probe name inline.
- Produces: `Probes` and `Scopes` with their final signatures and no-op bodies (Task 2 fills them in).
- Produces: `TestPrune.Trace.Model` (the contract above).
- Produces: `Fixtures.fxLibDir`, `Fixtures.fxDriverDir`, `Fixtures.fxTestsDir`, `Fixtures.repoRoot`. Each is an
  absolute `bin/Debug/net10.0` directory of a fixture built by the normal solution build.

- [ ] **Step 1: Write the failing test**

`tests/TestPrune.Trace.Tests/ScaffoldTests.fs`:

```fsharp
module TestPrune.Trace.Tests.ScaffoldTests

open System.IO
open System.Reflection
open Xunit
open Swensen.Unquote
open TestPrune.Trace.Recorder
open TestPrune.Trace.Tests

[<Fact>]
let ``recorder exposes every probe the contract names, with the contract signature`` () =
    let t = typeof<Probes>

    let has name (args: System.Type[]) =
        t.GetMethod(name, BindingFlags.Public ||| BindingFlags.Static, null, args, null) |> isNull |> not

    test <@ has Contract.Hit [| typeof<int> |] @>
    test <@ has Contract.HitIfNotNull [| typeof<obj>; typeof<int> |] @>
    test <@ has Contract.HitIfTrue [| typeof<bool>; typeof<int> |] @>
    test <@ has Contract.HitTag [| typeof<int>; typeof<int> |] @>
    test <@ has Contract.EnterStatic [||] @>
    test <@ has Contract.ExitStatic [||] @>

[<Fact>]
let ``recorder targets net8 and carries no package dependency but a low FSharp.Core`` () =
    let asm = typeof<Probes>.Assembly
    let refs = asm.GetReferencedAssemblies()
    let isFramework (n: string) = n = "netstandard" || n.StartsWith "System." || n = "System"
    // Anything that is neither the framework nor FSharp.Core would be a package the
    // consumer's test process has to resolve.
    let foreign = refs |> Array.filter (fun a -> a.Name <> "FSharp.Core" && not (isFramework a.Name))
    let fsCore = refs |> Array.find (fun a -> a.Name = "FSharp.Core")
    test <@ Array.isEmpty foreign @>
    test <@ fsCore.Version.Major = 8 @>

[<Fact>]
let ``the fixtures are built next to the tests`` () =
    test <@ File.Exists(Path.Combine(Fixtures.fxLibDir, "FxLib.dll")) @>
    test <@ File.Exists(Path.Combine(Fixtures.fxDriverDir, "FxDriver.dll")) @>
    test <@ File.Exists(Path.Combine(Fixtures.fxTestsDir, "FxTests.dll")) @>
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build`
Expected: FAIL. The build breaks because `tests/TestPrune.Trace.Tests` does not exist yet, so nothing named
`TestPrune.Trace.Recorder` compiles.

- [ ] **Step 3: Create the projects**

`src/TestPrune.Trace.Recorder/TestPrune.Trace.Recorder.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net8.0</TargetFramework>
        <PackageId>TestPrune.Trace.Recorder</PackageId>
        <Version>0.1.0</Version>
        <Authors>Michael Glass</Authors>
        <Description>Runtime recorder loaded into a woven test process: attributes probe hits to the current xUnit v3 test.</Description>
        <PackageLicenseExpression>MIT</PackageLicenseExpression>
        <RepositoryUrl>https://github.com/michaelglass/TestPrune</RepositoryUrl>
        <Optimize>true</Optimize>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
        <GenerateDocumentationFile>true</GenerateDocumentationFile>
        <!-- Loaded INTO a consumer's test process: never raise its FSharp.Core floor. -->
        <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>
        <NoWarn>$(NoWarn);NU1605;NU1608</NoWarn>
    </PropertyGroup>
    <ItemGroup>
        <Compile Include="Contract.fs" />
        <Compile Include="Probes.fs" />
    </ItemGroup>
    <ItemGroup>
        <PackageReference Include="FSharp.Core" VersionOverride="8.0.403" />
    </ItemGroup>
</Project>
```

`src/TestPrune.Trace.Recorder/Contract.fs`:

```fsharp
/// Names the weaver emits calls to. The weaver resolves each one by name on the recorder
/// assembly it ships with, so a rename here is a compile error in the weaver, never a
/// runtime MissingMethodException in a consumer's test process.
module TestPrune.Trace.Recorder.Contract

[<Literal>]
let ProbesType = "TestPrune.Trace.Recorder.Probes"

[<Literal>]
let IoType = "TestPrune.Trace.Recorder.Io"

[<Literal>]
let ProcessShimsType = "TestPrune.Trace.Recorder.ProcessShims"

[<Literal>]
let Hit = "Hit"

[<Literal>]
let HitIfNotNull = "HitIfNotNull"

[<Literal>]
let HitIfTrue = "HitIfTrue"

[<Literal>]
let HitTag = "HitTag"

[<Literal>]
let EnterStatic = "EnterStatic"

[<Literal>]
let ExitStatic = "ExitStatic"

/// Environment the host sets on a traced test process.
[<Literal>]
let OutEnv = "TESTPRUNE_TRACE_OUT"

[<Literal>]
let IdsEnv = "TESTPRUNE_TRACE_IDS"

[<Literal>]
let RepoRootEnv = "TESTPRUNE_TRACE_REPO_ROOT"

[<Literal>]
let ParentScopeEnv = "TESTPRUNE_TRACE_PARENT_SCOPE"

[<Literal>]
let VerifyEnv = "TESTPRUNE_TRACE_VERIFY"

[<Literal>]
let VerifyAssembliesEnv = "TESTPRUNE_TRACE_VERIFY_ASSEMBLIES"

[<Literal>]
let DumpFormat = "testprune-trace/1"
```

`src/TestPrune.Trace.Recorder/Probes.fs` (stub; Task 2 replaces the bodies):

```fsharp
namespace TestPrune.Trace.Recorder

/// Static entry points the weaver calls. Stub until the recorder core lands.
[<AbstractClass; Sealed>]
type Probes =
    static member Hit(id: int) : unit = ignore id
    static member HitIfNotNull(value: obj, id: int) : unit = ignore (value, id)
    static member HitIfTrue(value: bool, id: int) : unit = ignore (value, id)
    static member HitTag(tag: int, baseId: int) : unit = ignore (tag, baseId)
    static member EnterStatic() : unit = ()
    static member ExitStatic() : unit = ()

/// The consumer-facing scope contract (see README). Inert when the recorder is inactive.
[<AbstractClass; Sealed>]
type Scopes =
    static member Enter(key: string) : unit = ignore key
    static member Exit() : unit = ()
    static member CurrentKey() : string = null
    static member LinkCurrentTo(scopeKey: string) : unit = ignore scopeKey
```

`src/TestPrune.Trace/TestPrune.Trace.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <NoWarn>$(NoWarn);NU1605;NU1608;MSB3277;NETSDK1188</NoWarn>
        <PackageId>TestPrune.Trace</PackageId>
        <Version>0.1.0</Version>
        <Authors>Michael Glass</Authors>
        <Description>Recorded per-test dependencies for TestPrune: IL weaver, shadow bin, trace store and symbol joiner.</Description>
        <PackageLicenseExpression>MIT</PackageLicenseExpression>
        <RepositoryUrl>https://github.com/michaelglass/TestPrune</RepositoryUrl>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
        <GenerateDocumentationFile>true</GenerateDocumentationFile>
    </PropertyGroup>
    <ItemGroup>
        <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
            <_Parameter1>TestPrune.Trace.Tests</_Parameter1>
        </AssemblyAttribute>
    </ItemGroup>
    <ItemGroup>
        <Compile Include="Model.fs" />
    </ItemGroup>
    <ItemGroup>
        <ProjectReference Include="../TestPrune.Core/TestPrune.Core.fsproj" />
        <ProjectReference Include="../TestPrune.Trace.Recorder/TestPrune.Trace.Recorder.fsproj" />
    </ItemGroup>
    <ItemGroup>
        <PackageReference Include="Mono.Cecil" />
        <PackageReference Include="Microsoft.Data.Sqlite" />
    </ItemGroup>
</Project>
```

`Directory.Packages.props`: add `<PackageVersion Include="Mono.Cecil" Version="0.11.6" />` to the
`ItemGroup`.

`tests/TestPrune.Trace.Tests/TestPrune.Trace.Tests.fsproj`: copy `tests/TestPrune.Tests/TestPrune.Tests.fsproj`'s
first `PropertyGroup` verbatim (Exe, MTP runner, `IsPackable=false`, the same `NoWarn`), then:

```xml
    <ItemGroup>
        <Compile Include="Fixtures.fs" />
        <Compile Include="ScaffoldTests.fs" />
    </ItemGroup>
    <ItemGroup>
        <ProjectReference Include="../../src/TestPrune.Trace/TestPrune.Trace.fsproj" />
        <ProjectReference Include="../../src/TestPrune.Trace.Recorder/TestPrune.Trace.Recorder.fsproj" />
        <!-- Build order only: the fixtures must be built before these tests read them. -->
        <ProjectReference Include="../TraceFixtures/src/FxDriver/FxDriver.fsproj" ReferenceOutputAssembly="false" />
        <ProjectReference Include="../TraceFixtures/tests/FxTests/FxTests.fsproj" ReferenceOutputAssembly="false" />
    </ItemGroup>
    <ItemGroup>
        <PackageReference Include="xunit.v3.mtp-v2" />
        <PackageReference Include="Unquote" />
        <PackageReference Include="Microsoft.Testing.Extensions.CodeCoverage" />
    </ItemGroup>
```

`tests/TestPrune.Trace.Tests/Fixtures.fs`:

```fsharp
module TestPrune.Trace.Tests.Fixtures

open System
open System.IO

/// The repository root: the first ancestor of the test binary holding TestPrune.slnx.
let repoRoot =
    let rec up (d: DirectoryInfo) =
        if isNull d then failwith "TestPrune.slnx not found above the test binary"
        elif File.Exists(Path.Combine(d.FullName, "TestPrune.slnx")) then d.FullName
        else up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

/// The fixtures' miniature repository root (it has its own src/ and tests/).
let fixtureRoot = Path.Combine(repoRoot, "tests", "TraceFixtures")

let private built (sub: string) (project: string) =
    Path.Combine(fixtureRoot, sub, project, "bin", "Debug", "net10.0")

let fxLibDir = built "src" "FxLib"
let fxDriverDir = built "src" "FxDriver"
let fxTestsDir = built "tests" "FxTests"
```

Fixture projects:

- **FxLib:** the three F# files exactly as below. They cover the probe shapes: a 2-case union, a 5-case union,
  an all-nullary union, a struct union, a record, a class hierarchy, an interface, module values, a literal
  and an active pattern.

  `tests/TraceFixtures/src/FxLib/FxLib.fsproj`:

  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
          <TargetFramework>net10.0</TargetFramework>
          <IsPackable>false</IsPackable>
          <NoWarn>$(NoWarn);NU1605;NU1608</NoWarn>
      </PropertyGroup>
      <ItemGroup>
          <Compile Include="Types.fs" />
          <Compile Include="Values.fs" />
          <Compile Include="Logic.fs" />
      </ItemGroup>
  </Project>
  ```

  `Types.fs`:

  ```fsharp
  namespace FxLib

  type Shape =
      | Circle of radius: float
      | Square of side: float

  type Color5 =
      | Red
      | Green of int
      | Blue of int
      | Cyan of string
      | Magenta

  type Dir =
      | North
      | East
      | South
      | West

  [<Struct>]
  type SResult =
      | SOk of okValue: int
      | SErr of errValue: string

  /// A case literally named `Tag` and a case field named `tag`: the two shapes that
  /// produced invalid IL in the prototype.
  type Tricky =
      | Tag
      | Other of tag: string

  type Point = { X: int; Y: int }

  type Animal() =
      abstract Speak: unit -> string
      default _.Speak() = "..."

  type Dog() =
      inherit Animal()
      override _.Speak() = "woof"

  type Cat() =
      inherit Animal()
      override _.Speak() = "meow"

  type IGreeter =
      abstract Greet: string -> string

  type Greeter() =
      interface IGreeter with
          member _.Greet n = "hi " + n
  ```

  `Values.fs`:

  ```fsharp
  module FxLib.Values

  let computeThreshold () = 40 + 2
  let threshold = computeThreshold ()
  let constValue = 7

  [<Literal>]
  let LiteralValue = 99

  let defaultShape = Square 3.0
  let names = [ "a"; "b" ]
  let unusedValue = computeThreshold () * 2
  let thresholdPlus x = threshold + x
  let prebuiltBlue = Blue 3
  let constPlus x = constValue + x
  ```

  `Logic.fs`:

  ```fsharp
  module FxLib.Logic

  open FxLib

  let area shape =
      match shape with
      | Circle r -> 3.0 * r * r
      | Square s -> s * s

  let isCircleOnly shape =
      match shape with
      | Circle _ -> true
      | _ -> false

  let colorCode c =
      match c with
      | Red -> 1
      | Green n -> 10 + n
      | Blue n -> 20 + n
      | Cyan s -> s.Length
      | Magenta -> 5

  let turn d =
      match d with
      | North -> East
      | East -> South
      | South -> West
      | West -> North

  let sval r =
      match r with
      | SOk v -> v
      | SErr e -> e.Length

  let tricky t =
      match t with
      | Tag -> 0
      | Other s -> s.Length

  let (|Big|Small|) (p: Point) = if p.X + p.Y > 10 then Big else Small

  let classify p =
      match p with
      | Big -> "big"
      | Small -> "small"

  let sumPoint (p: Point) = p.X + p.Y

  let describe (o: obj) =
      match o with
      | :? Dog as d -> "dog " + d.Speak()
      | :? Cat -> "cat"
      | _ -> "other"

  let castDog (o: obj) = (o :?> Dog).Speak()
  let aboveThreshold x = x > Values.threshold
  let addConst x = x + Values.constValue
  let addLiteral x = x + Values.LiteralValue
  let defaultArea () = area Values.defaultShape
  let isCircleProp (s: Shape) = s.IsCircle
  let samePoint (a: Point) (b: Point) = a = b
  let greet (g: IGreeter) = g.Greet "x"
  let readRepoFile (path: string) = System.IO.File.ReadAllText path
  ```

- **FxDriver:** a console app (`OutputType=Exe`, `IsPackable=false`) with `ProjectReference`s to
  `../FxLib/FxLib.fsproj` and `../../../../src/TestPrune.Trace.Recorder/TestPrune.Trace.Recorder.fsproj`. Its `Program.fs` runs each scenario inside `Scopes.Enter name` /
  `Scopes.Exit ()`, with pre-built values created in a `"warm"` scope, the same design as the prototype driver.

  ```fsharp
  module FxDriver.Program

  open FxLib
  open TestPrune.Trace.Recorder

  let scope name (f: unit -> unit) =
      Scopes.Enter("T:" + name)

      try
          f ()
      finally
          Scopes.Exit()

  [<EntryPoint>]
  let main _ =
      let circle = ref Unchecked.defaultof<Shape>
      let circle2 = ref Unchecked.defaultof<Shape>
      let square = ref Unchecked.defaultof<Shape>
      let blue = ref Unchecked.defaultof<Color5>
      let east = ref Unchecked.defaultof<Dir>
      let err = ref Unchecked.defaultof<SResult>
      let dog = ref Unchecked.defaultof<Dog>
      let cat = ref Unchecked.defaultof<Cat>
      let pt = ref Unchecked.defaultof<Point>
      let tagCase = ref Unchecked.defaultof<Tricky>

      scope "warm" (fun () ->
          circle.Value <- Circle 2.0
          circle2.Value <- Circle 2.0
          square.Value <- Square 1.0
          blue.Value <- Blue 3
          east.Value <- East
          err.Value <- SErr "e"
          dog.Value <- Dog()
          cat.Value <- Cat()
          pt.Value <- { X = 1; Y = 2 }
          tagCase.Value <- Tag
          Values.threshold |> ignore)

      let sink = System.Collections.Generic.List<obj>()
      let run name (f: unit -> obj) = scope name (fun () -> sink.Add(f ()))
      run "caseA_area" (fun () -> box (Logic.area circle.Value))
      run "caseB_isCircleOnly" (fun () -> box (Logic.isCircleOnly square.Value))
      run "static_case_match" (fun () -> box (Logic.defaultArea ()))
      run "tag5" (fun () -> box (Logic.colorCode blue.Value))
      run "tag5_static" (fun () -> box (Logic.colorCode Values.prebuiltBlue))
      run "nullary" (fun () -> box (Logic.turn east.Value))
      run "struct" (fun () -> box (Logic.sval err.Value))
      run "tricky_tag" (fun () -> box (Logic.tricky tagCase.Value))
      run "modval" (fun () -> box (Logic.aboveThreshold 50))
      run "sameFileValue" (fun () -> box (Values.thresholdPlus 1))
      run "typetest_dog" (fun () -> box (Logic.describe (box dog.Value)))
      run "typetest_cat" (fun () -> box (Logic.describe (box cat.Value)))
      run "unboxgeneric" (fun () -> box (Logic.castDog (box dog.Value)))
      run "record" (fun () -> box (Logic.sumPoint pt.Value))
      run "testcode_match" (fun () -> box (match circle.Value with Circle r -> r | Square s -> -s))
      run "testcode_typetest" (fun () -> box (match box cat.Value with :? Dog -> 1 | _ -> 2))
      run "constValue" (fun () -> box (Logic.addConst 1))
      run "literal" (fun () -> box (Logic.addLiteral 1))
      run "isCaseProp" (fun () -> box (Logic.isCircleProp square.Value))
      run "activePattern" (fun () -> box (Logic.classify pt.Value))
      run "recordEquality" (fun () -> box (Logic.samePoint pt.Value pt.Value))
      run "unionEquality" (fun () -> box (circle.Value = circle2.Value))
      run "fileRead" (fun () -> box (Logic.readRepoFile (System.IO.Path.Combine(System.Environment.GetEnvironmentVariable "TESTPRUNE_TRACE_REPO_ROOT", "global.json"))))
      0
  ```

- **FxTests:** an xUnit v3 project (`xunit.v3.mtp-v2`, `UseMicrosoftTestingPlatformRunner`, `IsPackable=false`)
  referencing `../../src/FxLib/FxLib.fsproj` only (**not** the recorder). `AttributionTests.fs`:

  ```fsharp
  module FxTests.AttributionTests

  open System
  open System.Diagnostics
  open System.Threading.Tasks
  open Xunit
  open FxLib

  type SharedFixture() =
      member val Seed = Logic.sumPoint { X = 1; Y = 1 }

  type ClassA(fixture: SharedFixture) =
      interface IClassFixture<SharedFixture>

      [<Fact>]
      member _.``a sync area`` () = Assert.Equal(12.0, Logic.area (Circle 2.0))

      [<Fact>]
      member _.``a async task turn`` () =
          task {
              do! Task.Yield()
              Assert.Equal(South, Logic.turn East)
          }

      [<Fact>]
      member _.``a Task.Run describe`` () =
          task {
              let! s = Task.Run(fun () -> Logic.describe (box (Dog())))
              Assert.Equal("dog woof", s)
          }

  type ClassB() =
      [<Fact>]
      member _.``b async block colorCode`` () =
          async {
              do! Async.Sleep 1
              Assert.Equal(23, Logic.colorCode (Blue 3))
          }
          |> Async.StartAsTask

      [<Fact>]
      member _.``b Async.Parallel sval`` () =
          let r =
              [ async { return Logic.sval (SOk 1) }; async { return Logic.sval (SErr "ab") } ]
              |> Async.Parallel
              |> Async.RunSynchronously

          Assert.Equal<int[]>([| 1; 2 |], r)

      [<Theory>]
      [<InlineData(1)>]
      [<InlineData(20)>]
      member _.``b theory aboveThreshold`` (x: int) = Assert.Equal(x > 42, Logic.aboveThreshold x)

  type ClassC() =
      [<Fact>]
      member _.``c reads a repo file`` () =
          let root = Environment.GetEnvironmentVariable "TESTPRUNE_TRACE_REPO_ROOT"
          if not (String.IsNullOrEmpty root) then
              Assert.NotEmpty(Logic.readRepoFile (IO.Path.Combine(root, "global.json")))

      [<Fact>]
      member _.``c starts a child process`` () =
          let psi = ProcessStartInfo("/bin/echo", "hi", UseShellExecute = false, RedirectStandardOutput = true)
          use p = Process.Start psi
          p.WaitForExit()
          Assert.Equal(0, p.ExitCode)
  ```

`TestPrune.slnx`: under `/tests/` add `tests/TestPrune.Trace.Tests/TestPrune.Trace.Tests.fsproj`; under `/src/`
add both new src projects. Add a new folder:

```xml
  <Folder Name="/tests/TraceFixtures/">
    <Project Path="tests/TraceFixtures/src/FxLib/FxLib.fsproj" />
    <Project Path="tests/TraceFixtures/src/FxDriver/FxDriver.fsproj" />
    <Project Path="tests/TraceFixtures/tests/FxTests/FxTests.fsproj" />
  </Folder>
```

`semantic-tagger.json`: add

```json
{ "name": "TestPrune.Trace.Recorder", "fsproj": "src/TestPrune.Trace.Recorder/TestPrune.Trace.Recorder.fsproj", "tagPrefix": "trace-v", "fsProjsSharingSameTag": ["src/TestPrune.Trace/TestPrune.Trace.fsproj"] }
```

One tag covers both packages: they version in lockstep because the weaver resolves recorder methods by name.

`.fshw.json`: add a second entry to `tests.projects`:

```json
{ "project": "TestPrune.Trace.Tests", "command": "dotnet", "args": "run --project tests/TestPrune.Trace.Tests --no-build --", "filterTemplate": "--filter-class {classes}", "classJoin": " ", "group": "trace" }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.ScaffoldTests`
Expected: PASS, 3 tests.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t1-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict. Commit:

```bash
jj commit -m "Scaffold TestPrune.Trace and its recorder, with F# probe-shape fixtures

Two new packages that version together under one trace-v tag: the recorder is
loaded into a test process and pins FSharp.Core 8.0.403 so it never raises the
host's floor; the tooling package will hold the weaver, shadow bin, store and
joiner. Probe names live in Recorder.Contract so the weaver cannot drift from
the recorder.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Recorder core, dump writer and dump reader

**Files:**

- Create: `src/TestPrune.Trace.Recorder/Scope.fs`, `ContextSource.fs`, `RecorderState.fs`, `DumpWriter.fs`
- Modify: `src/TestPrune.Trace.Recorder/Probes.fs` (real bodies + `Runtime` bootstrap),
  `TestPrune.Trace.Recorder.fsproj` (compile order; `InternalsVisibleTo TestPrune.Trace.Tests`)
- Create: `src/TestPrune.Trace/DumpReader.fs` (add to `TestPrune.Trace.fsproj` after `Model.fs`)
- Test: `tests/TestPrune.Trace.Tests/RecorderStateTests.fs`, `XunitContextTests.fs`, `DumpRoundTripTests.fs`

**Interfaces:**

- Consumes: `Contract` literals and the `Probes`/`Scopes` signatures (Task 1); `Model.ProcessDump` and friends
  (Task 1).
- Produces (recorder, internal, visible to the tests):
  - `RecorderState(idCount: int, source: IContextSource option, parentScope: string)` with members
    `Hit(id)`, `EnterStatic()`, `ExitStatic()`, `EnterScope(key)`, `ExitScope()`, `CurrentScope(): Scope`
    (null when unattributed), `Scopes: seq<Scope>`, `Counters(): int64[]`, and the constants `IdCount` and
    `ParentScope`.
  - `Scope` with `Key`, `Set(id)`, `Ids(): int[]`, `Links`, `Inputs`, `Children`, and the test identity
    fields.
  - `IContextSource`, `ContextInfo`, `XunitContextSource.tryCreate(): IContextSource option`.
  - `DumpWriter.write (path: string) (state: RecorderState) : unit`.
  - `Runtime.state : RecorderState` (null when `TESTPRUNE_TRACE_OUT` is unset).
- Produces (tooling): `DumpReader.readFile (path: string) : Result<Model.ProcessDump, string>` and
  `DumpReader.readDirectory (dir: string) : Model.ProcessDump list * (string * string) list`, which returns
  the good dumps plus a `(file, reason)` for each rejected one.

- [ ] **Step 1: Write the failing tests**

`tests/TestPrune.Trace.Tests/RecorderStateTests.fs`:

```fsharp
module TestPrune.Trace.Tests.RecorderStateTests

open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open TestPrune.Trace.Recorder

/// A context source whose "current test" is an AsyncLocal the test sets, exactly
/// like xUnit's TestContext.Current.
type FakeSource() =
    let current = AsyncLocal<obj>()
    member _.Set(ctx: obj) = current.Value <- ctx

    interface IContextSource with
        member _.Current() = current.Value

        member _.Describe(ctx) =
            let name = string ctx

            { Key = "T:" + name
              TestClass = "Ns.C"
              TestMethod = name
              TestDisplay = "Ns.C." + name
              Parents = [| "C:Ns.C"; "A:assembly" |] }

let private idsOf (state: RecorderState) key =
    state.Scopes |> Seq.find (fun s -> s.Key = key) |> fun s -> s.Ids() |> Set.ofArray

[<Fact>]
let ``hits go to the current test, and parallel tests never contaminate each other`` () =
    let src = FakeSource()
    let state = RecorderState(1000, Some(src :> IContextSource), null)

    let work (name: string) (ids: int list) =
        Task.Run(fun () ->
            src.Set(box name)

            for _ in 1..200 do
                for id in ids do
                    state.Hit id)

    Task.WaitAll([| work "a" [ 1; 2; 3 ]; work "b" [ 3; 4 ]; work "c" [ 999 ] |])
    test <@ idsOf state "T:a" = set [ 1; 2; 3 ] @>
    test <@ idsOf state "T:b" = set [ 3; 4 ] @>
    test <@ idsOf state "T:c" = set [ 999 ] @>

[<Fact>]
let ``attribution flows through awaits and Task.Run`` () =
    let src = FakeSource()
    let state = RecorderState(64, Some(src :> IContextSource), null)

    let t =
        task {
            src.Set(box "flow")
            do! Task.Yield()
            let! () = Task.Run(fun () -> state.Hit 7)
            state.Hit 8
        }

    t.Wait()
    test <@ idsOf state "T:flow" = set [ 7; 8 ] @>

[<Fact>]
let ``static init wins over the test, an explicit scope wins over the test`` () =
    let src = FakeSource()
    let state = RecorderState(64, Some(src :> IContextSource), null)
    src.Set(box "t")
    state.EnterStatic()
    state.Hit 1
    state.ExitStatic()
    state.EnterScope "P:pool"
    state.Hit 2
    state.ExitScope()
    state.Hit 3
    test <@ idsOf state "S:static-init" = set [ 1 ] @>
    test <@ idsOf state "P:pool" = set [ 2 ] @>
    test <@ idsOf state "T:t" = set [ 3 ] @>

[<Fact>]
let ``no context means ambient, or the parent scope in a child process`` () =
    let lone = RecorderState(64, None, null)
    lone.Hit 5
    test <@ idsOf lone "A:ambient" = set [ 5 ] @>
    let child = RecorderState(64, None, "T:parent")
    child.Hit 6
    test <@ idsOf child "T:parent" = set [ 6 ] @>

[<Fact>]
let ``an id outside the manifest is counted as overflow and never indexes out of range`` () =
    let state = RecorderState(10, None, null)
    // Counters are per THREAD and summed process-wide, so compare against a snapshot.
    let before = state.Counters()
    state.Hit 10
    state.Hit -1
    test <@ state.Counters().[7] - before.[7] = 2L @>

[<Fact>]
let ``counters split hits by bucket`` () =
    let src = FakeSource()
    let state = RecorderState(64, Some(src :> IContextSource), null)
    let before = state.Counters()
    state.Hit 1 // ambient
    src.Set(box "t")
    state.Hit 1 // test
    state.EnterStatic()
    state.Hit 1 // static
    state.ExitStatic()
    let c = Array.map2 (-) (state.Counters()) before
    test <@ (c.[0], c.[5], c.[6]) = (1L, 1L, 1L) @>
```

`tests/TestPrune.Trace.Tests/XunitContextTests.fs`. This test runs under real xUnit v3, so the source binds
the real `TestContext`:

```fsharp
module TestPrune.Trace.Tests.XunitContextTests

open Xunit
open Swensen.Unquote
open TestPrune.Trace.Recorder

[<Fact>]
let ``the xUnit source names the running test and its parents`` () =
    let src = (XunitContextSource.tryCreate ()).Value
    let info = src.Describe(src.Current())
    test <@ info.Key.StartsWith "T:" @>
    test <@ info.TestMethod = "the xUnit source names the running test and its parents" @>
    test <@ info.TestClass = "TestPrune.Trace.Tests.XunitContextTests" @>
    test <@ info.Parents |> Array.contains ("C:" + info.TestClass) @>
    test <@ info.Parents |> Array.last = "A:assembly" @>
```

`tests/TestPrune.Trace.Tests/DumpRoundTripTests.fs`:

```fsharp
module TestPrune.Trace.Tests.DumpRoundTripTests

open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace.Recorder
open TestPrune.Trace
open TestPrune.Trace.Model

[<Fact>]
let ``a dump reads back exactly what the recorder held`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    let state = RecorderState(128, None, null)
    state.EnterScope "T:x"
    state.Hit 3
    state.Hit 70
    state.CurrentScope().Links.TryAdd("P:pool", 0uy) |> ignore
    state.ExitScope()
    let path = Path.Combine(dir, "trace-1.ndjson")
    DumpWriter.write path state
    let dump = DumpReader.readFile path |> Result.defaultWith failwith
    let x = dump.Scopes |> List.find (fun s -> s.Key = "T:x")
    test <@ x.Ids = [| 3; 70 |] @>
    test <@ x.Links = [ "P:pool" ] @>
    test <@ dump.IdCount = 128 @>

[<Fact>]
let ``a dump without its end marker is rejected`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    let path = Path.Combine(dir, "trace-2.ndjson")
    File.WriteAllLines(path, [| """{"format":"testprune-trace/1","pid":2,"parentScope":null,"runtime":"r","os":"o","arch":"a","ids":1,"cpuMs":0,"counters":{"test":0,"class":0,"collection":0,"assembly":0,"override":0,"staticInit":0,"ambient":0,"overflow":0}}""" |])
    test <@ DumpReader.readFile path = Error "truncated" @>

[<Fact>]
let ``readDirectory ignores tmp files and reports rejected dumps by name`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    File.WriteAllText(Path.Combine(dir, "trace-3.ndjson.tmp"), "partial")
    File.WriteAllText(Path.Combine(dir, "trace-4.ndjson"), "not json")
    let good, bad = DumpReader.readDirectory dir
    test <@ good = [] @>
    test <@ bad |> List.map fst = [ Path.Combine(dir, "trace-4.ndjson") ] @>
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build`
Expected: FAIL. `RecorderState`, `XunitContextSource`, `DumpWriter` and `DumpReader` are undefined.

- [ ] **Step 3: Implement the recorder**

`Scope.fs`:

```fsharp
namespace TestPrune.Trace.Recorder

open System.Collections.Concurrent
open System.Threading

/// One attribution bucket: a bitset of probe ids plus what the host needs to name,
/// link and join it. One word per 64 ids; set with a racy read then an atomic OR, so
/// the common "already set" case is a plain load.
[<Sealed; AllowNullLiteral>]
type Scope(key: string, idCount: int) =
    let bits: uint64[] = Array.zeroCreate ((idCount + 63) >>> 6)
    member _.Key = key
    member val TestClass: string = null with get, set
    member val TestMethod: string = null with get, set
    member val TestDisplay: string = null with get, set
    member val Parents: string[] = [||] with get, set
    member val Links = ConcurrentDictionary<string, byte>()
    member val Inputs = ConcurrentDictionary<struct (string * string), byte>()
    member val Children = ConcurrentQueue<struct (int * string * bool)>()

    member _.Set(id: int) =
        let w = id >>> 6
        let m = 1UL <<< (id &&& 63)

        if (Volatile.Read(&bits.[w]) &&& m) = 0UL then
            Interlocked.Or(&bits.[w], m) |> ignore

    member _.Ids() : int[] =
        let out = ResizeArray()

        for w in 0 .. bits.Length - 1 do
            let mutable word = bits.[w]

            while word <> 0UL do
                let b = System.Numerics.BitOperations.TrailingZeroCount word
                out.Add((w <<< 6) + b)
                word <- word &&& (word - 1UL)

        out.ToArray()

    member this.IsEmpty =
        bits |> Array.forall ((=) 0UL)
        && this.Links.IsEmpty
        && this.Inputs.IsEmpty
        && this.Children.IsEmpty
```

`ContextSource.fs`:

```fsharp
namespace TestPrune.Trace.Recorder

open System
open System.Collections.Concurrent
open System.Reflection

/// What the recorder needs to know about one attribution context.
type ContextInfo =
    { Key: string
      TestClass: string
      TestMethod: string
      TestDisplay: string
      Parents: string[] }

/// Where "the current test" comes from. Production binds xUnit v3 by reflection
/// (no compile-time dependency); tests substitute a fake.
type IContextSource =
    /// The current context object, compared by reference for caching; null when none.
    abstract Current: unit -> obj
    abstract Describe: context: obj -> ContextInfo

module XunitContextSource =
    let private props = ConcurrentDictionary<struct (Type * string), PropertyInfo>()

    /// Read `name` from `o` through its type or any interface it implements. xUnit's
    /// concrete context types are internal; their interfaces are the public surface.
    let private read (o: obj) (name: string) : obj =
        if isNull o then
            null
        else
            let t = o.GetType()

            let p =
                props.GetOrAdd(
                    struct (t, name),
                    fun _ ->
                        Seq.append [ t ] (t.GetInterfaces())
                        |> Seq.tryPick (fun i -> i.GetProperty name |> Option.ofObj)
                        |> Option.toObj
                )

            if isNull p then null else p.GetValue o

    let private str (o: obj) = if isNull o then null else string o

    type private Source(current: Func<obj>) =
        interface IContextSource with
            member _.Current() = current.Invoke()

            member _.Describe(ctx) =
                let testObj = read ctx "Test"
                let cls = read ctx "TestClass"
                let meth = read ctx "TestMethod"
                let coll = read ctx "TestCollection"
                let className = str (read cls "TestClassName")
                let collName = str (read coll "TestCollectionDisplayName")

                let collParent =
                    if isNull collName then [||] else [| "L:" + collName |]

                if not (isNull testObj) then
                    { Key = "T:" + str (read testObj "UniqueID")
                      TestClass = className
                      TestMethod = str (read meth "MethodName")
                      TestDisplay = str (read testObj "TestDisplayName")
                      Parents = Array.concat [ [| "C:" + className |]; collParent; [| "A:assembly" |] ] }
                elif not (isNull className) then
                    { Key = "C:" + className
                      TestClass = className
                      TestMethod = null
                      TestDisplay = null
                      Parents = Array.append collParent [| "A:assembly" |] }
                elif not (isNull collName) then
                    { Key = "L:" + collName
                      TestClass = null
                      TestMethod = null
                      TestDisplay = null
                      Parents = [| "A:assembly" |] }
                else
                    { Key = "A:assembly"
                      TestClass = null
                      TestMethod = null
                      TestDisplay = null
                      Parents = [||] }

    /// Bind `Xunit.TestContext.Current` once. None when xUnit v3 is not loaded.
    let tryCreate () : IContextSource option =
        match Type.GetType("Xunit.TestContext, xunit.v3.core", false) with
        | null -> None
        | t ->
            match t.GetProperty("Current", BindingFlags.Public ||| BindingFlags.Static) with
            | null -> None
            | p ->
                // A bound delegate, not MethodInfo.Invoke: this runs on every probe hit. Valid
                // because the getter returns a reference type and delegate returns are covariant.
                let f = Delegate.CreateDelegate(typeof<Func<obj>>, p.GetGetMethod()) :?> Func<obj>
                Some(Source f :> IContextSource)
```

`RecorderState.fs`:

```fsharp
namespace TestPrune.Trace.Recorder

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open System.Threading

/// Per-thread hot-path cache. One instance per thread, registered so the exit dump
/// can sum its counters.
[<Sealed; AllowNullLiteral>]
type internal ThreadState() =
    member val StaticDepth = 0 with get, set
    member val LastCtx: obj = null with get, set
    member val LastScope: Scope = null with get, set
    member val LastOwner: obj = null with get, set
    /// test, class, collection, assembly, override, staticInit, ambient, overflow
    member val Counts: int64[] = Array.zeroCreate 8

[<AbstractClass; Sealed>]
type internal Threads =
    [<ThreadStatic; DefaultValue>]
    static val mutable private current: ThreadState

    static member val All = ConcurrentBag<ThreadState>()

    static member Get() =
        let t = Threads.current

        if isNull t then
            let fresh = ThreadState()
            Threads.current <- fresh
            Threads.All.Add fresh
            fresh
        else
            t

[<Sealed; AllowNullLiteral>]
type RecorderState(idCount: int, source: IContextSource option, parentScope: string) =
    let byKey = ConcurrentDictionary<string, Scope>()
    let byCtx = ConditionalWeakTable<obj, Scope>()
    let newScope key = byKey.GetOrAdd(key, fun k -> Scope(k, idCount))

    let ambient =
        newScope (if String.IsNullOrEmpty parentScope then "A:ambient" else parentScope)

    let ambientBucket = if String.IsNullOrEmpty parentScope then 6 else 4
    let staticInit = newScope "S:static-init"
    let overrideScope = AsyncLocal<Scope>()
    let mutable anyOverride = false

    let bucketOf (s: Scope) =
        match s.Key.[0] with
        | 'T' -> 0
        | 'C' -> 1
        | 'L' -> 2
        | _ -> 3

    let resolve (src: IContextSource) (ctx: obj) =
        match byCtx.TryGetValue ctx with
        | true, s -> s
        | _ ->
            let info = src.Describe ctx
            let s = newScope info.Key

            if isNull s.TestClass then
                s.TestClass <- info.TestClass
                s.TestMethod <- info.TestMethod
                s.TestDisplay <- info.TestDisplay
                s.Parents <- info.Parents

            byCtx.AddOrUpdate(ctx, s)
            s

    member _.IdCount = idCount
    member _.ParentScope = parentScope
    member _.Scopes: seq<Scope> = byKey.Values :> _

    /// The scope a hit would go to now (ignoring static init); null when unattributed.
    member this.CurrentScope() : Scope =
        let o = if anyOverride then overrideScope.Value else null

        if not (isNull o) then
            o
        else
            match source with
            | Some src ->
                match src.Current() with
                | null -> if ambientBucket = 4 then ambient else null
                | ctx -> resolve src ctx
            | None -> if ambientBucket = 4 then ambient else null

    member _.EnterScope(key: string) =
        anyOverride <- true
        overrideScope.Value <- newScope key

    member _.ExitScope() = overrideScope.Value <- null
    member _.EnterStatic() = let t = Threads.Get() in t.StaticDepth <- t.StaticDepth + 1
    member _.ExitStatic() = let t = Threads.Get() in t.StaticDepth <- t.StaticDepth - 1

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member this.Hit(id: int) =
        let t = Threads.Get()

        if uint32 id >= uint32 idCount then
            t.Counts.[7] <- t.Counts.[7] + 1L
        elif t.StaticDepth > 0 then
            staticInit.Set id
            t.Counts.[5] <- t.Counts.[5] + 1L
        else
            let o = if anyOverride then overrideScope.Value else null

            if not (isNull o) then
                o.Set id
                t.Counts.[4] <- t.Counts.[4] + 1L
            else
                match source with
                | None ->
                    ambient.Set id
                    t.Counts.[ambientBucket] <- t.Counts.[ambientBucket] + 1L
                | Some src ->
                    let ctx = src.Current()

                    if isNull ctx then
                        ambient.Set id
                        t.Counts.[ambientBucket] <- t.Counts.[ambientBucket] + 1L
                    else
                        let s =
                            if Object.ReferenceEquals(ctx, t.LastCtx) && Object.ReferenceEquals(this, t.LastOwner) then
                                t.LastScope
                            else
                                let r = resolve src ctx
                                t.LastCtx <- ctx
                                t.LastScope <- r
                                t.LastOwner <- this
                                r

                        s.Set id
                        let b = bucketOf s
                        t.Counts.[b] <- t.Counts.[b] + 1L

    /// Sum of every thread's counters (see ThreadState.Counts for the bucket order).
    member _.Counters() : int64[] =
        let sum = Array.zeroCreate 8

        for t in Threads.All do
            for i in 0..7 do
                sum.[i] <- sum.[i] + t.Counts.[i]

        sum
```

`Threads.All` is process-wide: counters are per *thread* and summed over every thread, so two `RecorderState`
instances in one test process share them. A real traced process has exactly one `RecorderState`, so production
counters are exact. It is a `[<ThreadStatic>]` rather than a per-instance `ThreadLocal<T>` because the probe
cost budget is a few nanoseconds. In tests, the counter assertions compare against a snapshot, and the whole
`RecorderStateTests` module runs outside parallelization so that no other class's hits land between the
snapshot and the assertion. Add to `RecorderStateTests.fs`:

```fsharp
[<CollectionDefinition("recorder-counters", DisableParallelization = true)>]
type RecorderCountersCollection() = class end
```

and put `[<Collection("recorder-counters")>]` on the module (F# modules compile to static classes, and xUnit
reads the attribute from the class).

`DumpWriter.fs`:

```fsharp
module TestPrune.Trace.Recorder.DumpWriter

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json

let private osName () =
    if RuntimeInformation.IsOSPlatform OSPlatform.OSX then "OSX"
    elif RuntimeInformation.IsOSPlatform OSPlatform.Linux then "Linux"
    elif RuntimeInformation.IsOSPlatform OSPlatform.Windows then "Windows"
    else "Other"

/// Write every non-empty scope (every T scope, even an empty one) to `path` via a
/// `.tmp` sibling and a rename, so a reader never sees a half-written file.
let write (path: string) (state: RecorderState) =
    let tmp = path + ".tmp"

    do
        use stream = File.Create tmp
        use w = new Utf8JsonWriter(stream)
        let line () =
            w.Flush()
            stream.WriteByte(byte '\n')
            w.Reset()

        let c = state.Counters()
        w.WriteStartObject()
        w.WriteString("format", Contract.DumpFormat)
        w.WriteNumber("pid", Environment.ProcessId)

        if String.IsNullOrEmpty state.ParentScope then
            w.WriteNull "parentScope"
        else
            w.WriteString("parentScope", state.ParentScope)

        w.WriteString("runtime", RuntimeInformation.FrameworkDescription)
        w.WriteString("os", osName ())
        w.WriteString("arch", string RuntimeInformation.ProcessArchitecture)
        w.WriteNumber("ids", state.IdCount)
        w.WriteNumber("cpuMs", int64 (Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds))
        w.WriteStartObject "counters"

        for i, name in
            [ 0, "test"; 1, "class"; 2, "collection"; 3, "assembly"
              4, "override"; 5, "staticInit"; 6, "ambient"; 7, "overflow" ] do
            w.WriteNumber(name, c.[i])

        w.WriteEndObject()
        w.WriteEndObject()
        line ()

        for s in state.Scopes do
            if s.Key.StartsWith "T:" || not s.IsEmpty then
                w.WriteStartObject()
                w.WriteString("key", s.Key)

                if isNull s.TestMethod then
                    w.WriteNull "test"
                else
                    w.WriteStartObject "test"
                    w.WriteString("class", s.TestClass)
                    w.WriteString("method", s.TestMethod)
                    w.WriteString("display", s.TestDisplay)
                    w.WriteEndObject()

                w.WriteStartArray "parents"
                for p in s.Parents do w.WriteStringValue p
                w.WriteEndArray()
                w.WriteStartArray "links"
                for l in s.Links.Keys |> Seq.sort do w.WriteStringValue l
                w.WriteEndArray()
                w.WriteStartArray "ids"
                for id in s.Ids() do w.WriteNumberValue id
                w.WriteEndArray()
                w.WriteStartArray "inputs"

                for KeyValue(struct (kind, p), _) in s.Inputs |> Seq.sortBy (fun kv -> kv.Key) do
                    w.WriteStartObject()
                    w.WriteString("kind", kind)
                    w.WriteString("path", p)
                    w.WriteEndObject()

                w.WriteEndArray()
                w.WriteStartArray "children"

                for struct (pid, file, env) in s.Children do
                    w.WriteStartObject()
                    w.WriteNumber("pid", pid)
                    w.WriteString("file", file)
                    w.WriteBoolean("env", env)
                    w.WriteEndObject()

                w.WriteEndArray()
                w.WriteEndObject()
                line ()

        w.WriteStartObject()
        w.WriteBoolean("end", true)
        w.WriteEndObject()
        line ()

    File.Move(tmp, path, true)

/// The file name for this process inside the host-provided output directory.
let pathIn (dir: string) =
    Path.Combine(dir, $"trace-%d{Environment.ProcessId}.ndjson")
```

`Probes.fs`: replace the stub bodies. Add a `Runtime` module *before* the types:

```fsharp
namespace TestPrune.Trace.Recorder

open System
open System.IO

module internal Runtime =
    /// The process's recorder, or null when this process is not being traced
    /// (TESTPRUNE_TRACE_OUT unset). Built once, on the first probe hit.
    let state: RecorderState =
        match Environment.GetEnvironmentVariable Contract.OutEnv with
        | null
        | "" -> null
        | outDir ->
            let ids =
                match Int32.TryParse(Environment.GetEnvironmentVariable Contract.IdsEnv) with
                | true, n when n > 0 -> n
                | _ -> 1 <<< 20

            let s =
                RecorderState(ids, XunitContextSource.tryCreate (), Environment.GetEnvironmentVariable Contract.ParentScopeEnv)

            AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
                Directory.CreateDirectory outDir |> ignore
                DumpWriter.write (DumpWriter.pathIn outDir) s)

            s

[<AbstractClass; Sealed>]
type Probes =
    static member Hit(id: int) =
        let s = Runtime.state
        if not (isNull s) then s.Hit id

    static member HitIfNotNull(value: obj, id: int) =
        if not (isNull value) then Probes.Hit id

    static member HitIfTrue(value: bool, id: int) = if value then Probes.Hit id
    static member HitTag(tag: int, baseId: int) = Probes.Hit(baseId + tag)

    static member EnterStatic() =
        let s = Runtime.state
        if not (isNull s) then s.EnterStatic()

    static member ExitStatic() =
        let s = Runtime.state
        if not (isNull s) then s.ExitStatic()

[<AbstractClass; Sealed>]
type Scopes =
    static member Enter(key: string) =
        let s = Runtime.state
        if not (isNull s) then s.EnterScope key

    static member Exit() =
        let s = Runtime.state
        if not (isNull s) then s.ExitScope()

    static member CurrentKey() : string =
        let s = Runtime.state

        if isNull s then
            null
        else
            match s.CurrentScope() with
            | null -> null
            | scope -> scope.Key

    static member LinkCurrentTo(scopeKey: string) =
        let s = Runtime.state

        if not (isNull s) then
            match s.CurrentScope() with
            | null -> ()
            | scope -> scope.Links.TryAdd(scopeKey, 0uy) |> ignore
```

The `.fsproj` compile order is `Contract.fs`, `Scope.fs`, `ContextSource.fs`, `RecorderState.fs`,
`DumpWriter.fs`, `Probes.fs`. Add
`<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo"><_Parameter1>TestPrune.Trace.Tests</_Parameter1></AssemblyAttribute>`.

`src/TestPrune.Trace/DumpReader.fs`:

```fsharp
module TestPrune.Trace.DumpReader

open System
open System.IO
open System.Text.Json
open TestPrune.Trace.Model

let private strOpt (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private strs (e: JsonElement) (name: string) =
    [ for v in e.GetProperty(name).EnumerateArray() -> v.GetString() ]

let private inputKind =
    function
    | "read" -> FileRead
    | "exists" -> ExistenceProbe
    | "list" -> DirectoryListing
    | other -> failwith $"unknown input kind %s{other}"

let private scopeOf (e: JsonElement) : RecordedScope =
    let test =
        match e.GetProperty "test" with
        | t when t.ValueKind = JsonValueKind.Object ->
            Some
                { Class = t.GetProperty("class").GetString()
                  Method = t.GetProperty("method").GetString()
                  Display = t.GetProperty("display").GetString() }
        | _ -> None

    { Key = e.GetProperty("key").GetString()
      Test = test
      Parents = strs e "parents"
      Links = strs e "links"
      Ids = [| for v in e.GetProperty("ids").EnumerateArray() -> v.GetInt32() |]
      Inputs =
        [ for v in e.GetProperty("inputs").EnumerateArray() ->
              { Kind = inputKind (v.GetProperty("kind").GetString())
                Path = v.GetProperty("path").GetString() } ]
      Children =
        [ for v in e.GetProperty("children").EnumerateArray() ->
              { Pid = v.GetProperty("pid").GetInt32()
                FileName = v.GetProperty("file").GetString()
                EnvInjected = v.GetProperty("env").GetBoolean() } ] }

let readFile (path: string) : Result<ProcessDump, string> =
    try
        let lines = File.ReadAllLines path |> Array.filter (String.IsNullOrWhiteSpace >> not)

        if lines.Length = 0 then
            Error "empty"
        else
            use last = JsonDocument.Parse(lines.[lines.Length - 1])

            match last.RootElement.TryGetProperty "end" with
            | true, v when v.ValueKind = JsonValueKind.True ->
                use head = JsonDocument.Parse lines.[0]
                let h = head.RootElement

                if h.GetProperty("format").GetString() <> "testprune-trace/1" then
                    Error "unknown format"
                else
                    let c = h.GetProperty "counters"
                    let n (k: string) = c.GetProperty(k).GetInt64()

                    Ok
                        { Pid = h.GetProperty("pid").GetInt32()
                          ParentScope = strOpt h "parentScope"
                          Runtime = h.GetProperty("runtime").GetString()
                          Os = h.GetProperty("os").GetString()
                          Arch = h.GetProperty("arch").GetString()
                          IdCount = h.GetProperty("ids").GetInt32()
                          CpuMs = h.GetProperty("cpuMs").GetInt64()
                          Counters =
                            { Test = n "test"; Class = n "class"; Collection = n "collection"
                              Assembly = n "assembly"; Override = n "override"; StaticInit = n "staticInit"
                              Ambient = n "ambient"; Overflow = n "overflow" }
                          Scopes =
                            [ for line in lines.[1 .. lines.Length - 2] ->
                                  use d = JsonDocument.Parse line
                                  scopeOf d.RootElement ] }
            | _ -> Error "truncated"
    with ex ->
        Error ex.Message

let readDirectory (dir: string) : ProcessDump list * (string * string) list =
    if not (Directory.Exists dir) then
        [], []
    else
        let results =
            Directory.GetFiles(dir, "trace-*.ndjson")
            |> Array.sort
            |> Array.map (fun f -> f, readFile f)

        [ for _, r in results do match r with Ok d -> yield d | Error _ -> () ],
        [ for f, r in results do match r with Error e -> yield f, e | Ok _ -> () ]
```

The "truncated" test writes a file whose single line is the header, and the header has no `end` property. The
reader parses that line as `last`, finds no `end`, and returns `Error "truncated"`, which is what the test
asserts.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.RecorderStateTests --filter-class TestPrune.Trace.Tests.XunitContextTests --filter-class TestPrune.Trace.Tests.DumpRoundTripTests`
Expected: PASS, 10 tests.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t2-gate.log 2>&1; echo "exit=$?"` and read the log and the verdict. Then:

```bash
jj commit -m "Recorder: attribute probe hits to the current xUnit v3 test and dump at exit

Scope precedence is static init, then an explicit scope, then the xUnit test,
class, collection and assembly, then ambient (or the parent scope in a child
process). One bitset per scope, one exit-time NDJSON dump per process with an
end marker so a killed process's partial file is rejected, per-bucket hit
counters for the unattributed-hits bar. The reader lives in TestPrune.Trace.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: The trace store: a separate file with forward-only migrations

**Files:**

- Create: `src/TestPrune.Trace/TraceStore.fs` (compile after `DumpReader.fs`)
- Test: `tests/TestPrune.Trace.Tests/TraceStoreTests.fs`

**Interfaces:**

- Consumes: `Model.IncompleteReason`, `Model.OutcomeKind` (Task 1).
- Produces:

```fsharp
module TestPrune.Trace.TraceStore

type RunKind = FullRun | PartialRun
type RunStatus = Recorded | Refused | FailedToRecord | TreeMovedDuringRun

type TraceRun =
    { RunId: string; TestProject: string; TreeHash: string; EnvFingerprint: string
      RecordedAt: System.DateTimeOffset; Kind: RunKind; Status: RunStatus; Reason: string; StatsJson: string }

/// One scope's content, already joined to symbols.
type ScopeContent =
    { Key: string
      Symbols: (string * string) list          // (symbol full name, version hash)
      Inputs: (string * string * string) list } // (kind, key, hash); kind: read|exists|list|file-level

type TestTrace =
    { TestKey: string          // "<project>|<class>|<method>"
      Status: Model.OutcomeKind option
      Reasons: Model.IncompleteReason list   // empty = complete
      ScopeKeys: string list } // own T scopes + inherited scopes, all present in the run's ScopeContent list

type StoredTrace =
    { TestKey: string; EnvFingerprint: string; RunId: string; Complete: bool
      Reasons: string list; Symbols: Set<string * string>; Inputs: Set<string * string * string> }

exception TraceSchemaNewerThanConsumer of path: string * found: int * supported: int

val TraceSchemaVersion: int
val reasonCode: Model.IncompleteReason -> string

type Store =
    static member Open: path: string -> Store             // raises TraceSchemaNewerThanConsumer
    member RecordRun: run: TraceRun * scopes: ScopeContent list * tests: TestTrace list -> unit
    member RecordRunWithoutTraces: run: TraceRun -> unit  // refused / failed / tree-moved bookkeeping
    member TryRead: testKey: string * envFingerprint: string -> StoredTrace option
    member TestKeysOf: testProject: string * envFingerprint: string -> Set<string>
    member Runs: testProject: string -> TraceRun list      // newest first
    member CollectGarbage: testProject: string * envFingerprint: string * liveTestKeys: Set<string> option -> unit
    interface System.IDisposable
```

- [ ] **Step 1: Write the failing tests**

`tests/TestPrune.Trace.Tests/TraceStoreTests.fs`:

```fsharp
module TestPrune.Trace.Tests.TraceStoreTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Microsoft.Data.Sqlite
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.TraceStore

let private tempPath () = Path.Combine(Directory.CreateTempSubdirectory().FullName, "traces.db")

let private run id =
    { RunId = id; TestProject = "P"; TreeHash = "tree"; EnvFingerprint = "E1"
      RecordedAt = DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero)
      Kind = FullRun; Status = Recorded; Reason = ""; StatsJson = "{}" }

let private classScope =
    { Key = "C:Ns.C"; Symbols = [ "Lib.fixtureSetup", "h0" ]; Inputs = [] }

let private testScope key syms =
    { Key = key; Symbols = syms; Inputs = [ "read", "cfg/app.json", "fh" ] }

[<Fact>]
let ``a test's trace is the union of its own scopes and the scopes it inherits`` () =
    use store = Store.Open(tempPath ())

    store.RecordRun(
        run "r1",
        [ classScope; testScope "T:1" [ "Lib.f", "h1" ] ],
        [ { TestKey = "P|Ns.C|m"; Status = Some Passed; Reasons = []; ScopeKeys = [ "T:1"; "C:Ns.C" ] } ]
    )

    let t = store.TryRead("P|Ns.C|m", "E1") |> Option.get
    test <@ t.Complete @>
    test <@ t.Symbols = set [ "Lib.f", "h1"; "Lib.fixtureSetup", "h0" ] @>
    test <@ t.Inputs = set [ "read", "cfg/app.json", "fh" ] @>

[<Fact>]
let ``re-recording a test replaces its trace; other fingerprints are kept`` () =
    use store = Store.Open(tempPath ())
    let t1 = { TestKey = "P|Ns.C|m"; Status = Some Passed; Reasons = []; ScopeKeys = [ "T:1" ] }
    store.RecordRun(run "r1", [ testScope "T:1" [ "Lib.f", "h1" ] ], [ t1 ])
    store.RecordRun({ run "r2" with EnvFingerprint = "E2" }, [ testScope "T:1" [ "Lib.g", "h9" ] ], [ t1 ])
    store.RecordRun(run "r3", [ testScope "T:9" [ "Lib.g", "h2" ] ], [ { t1 with ScopeKeys = [ "T:9" ] } ])
    test <@ (store.TryRead("P|Ns.C|m", "E1") |> Option.get).Symbols = set [ "Lib.g", "h2" ] @>
    test <@ (store.TryRead("P|Ns.C|m", "E2") |> Option.get).Symbols = set [ "Lib.g", "h9" ] @>

[<Fact>]
let ``an incomplete trace keeps its reasons and says so`` () =
    use store = Store.Open(tempPath ())

    store.RecordRun(
        run "r1",
        [ testScope "T:1" [] ],
        [ { TestKey = "P|Ns.C|m"; Status = Some Failed
            Reasons = [ NotPassed Failed; ChildProcessUntraced "dotnet" ]; ScopeKeys = [ "T:1" ] } ]
    )

    let t = store.TryRead("P|Ns.C|m", "E1") |> Option.get
    test <@ not t.Complete @>
    test <@ t.Reasons = [ "not-passed:failed"; "child-process-untraced:dotnet" ] @>

[<Fact>]
let ``garbage collection drops dead tests, unreferenced scopes and versions`` () =
    let path = tempPath ()
    use store = Store.Open path
    let t name scope = { TestKey = $"P|Ns.C|%s{name}"; Status = Some Passed; Reasons = []; ScopeKeys = [ scope ] }
    store.RecordRun(run "r1", [ testScope "T:a" [ "Lib.a", "1" ]; testScope "T:b" [ "Lib.b", "1" ] ], [ t "a" "T:a"; t "b" "T:b" ])
    store.RecordRun(run "r2", [ testScope "T:a2" [ "Lib.a", "2" ] ], [ t "a" "T:a2" ])
    store.CollectGarbage("P", "E1", Some(set [ "P|Ns.C|a" ]))
    test <@ store.TestKeysOf("P", "E1") = set [ "P|Ns.C|a" ] @>
    use conn = new SqliteConnection($"Data Source=%s{path}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT COUNT(*) FROM symbol_versions"
    test <@ (cmd.ExecuteScalar() :?> int64) = 1L @>

[<Fact>]
let ``a file written by a newer trace schema is refused and left byte-identical`` () =
    let path = tempPath ()
    (Store.Open path :> IDisposable).Dispose()

    do
        use conn = new SqliteConnection($"Data Source=%s{path}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"PRAGMA user_version = %d{TraceSchemaVersion + 1};"
        cmd.ExecuteNonQuery() |> ignore

    SqliteConnection.ClearAllPools()
    let before = File.ReadAllBytes path

    raises<TraceSchemaNewerThanConsumer> <@ Store.Open path @>
    test <@ File.ReadAllBytes path = before @>

[<Fact>]
let ``migrations run forward and keep every row`` () =
    let path = tempPath ()

    do
        use store = Store.OpenWith(TraceStore.migrations, path)
        store.RecordRun(run "r1", [ testScope "T:1" [ "Lib.f", "h1" ] ], [ { TestKey = "P|Ns.C|m"; Status = Some Passed; Reasons = []; ScopeKeys = [ "T:1" ] } ])

    let plusOne = TraceStore.migrations @ [ TraceSchemaVersion + 1, "ALTER TABLE trace_runs ADD COLUMN note TEXT;" ]
    use store = Store.OpenWith(plusOne, path)
    test <@ (store.TryRead("P|Ns.C|m", "E1") |> Option.get).Symbols = set [ "Lib.f", "h1" ] @>

[<Fact>]
let ``a TestPrune.Core schema recreate of the index never touches the trace file`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    let tracePath = Path.Combine(dir, "test-traces.db")
    let indexPath = Path.Combine(dir, "test-impact.db")

    do
        use store = Store.Open tracePath
        store.RecordRun(run "r1", [ testScope "T:1" [ "Lib.f", "h1" ] ], [ { TestKey = "P|Ns.C|m"; Status = Some Passed; Reasons = []; ScopeKeys = [ "T:1" ] } ])

    // An index stamped with an older core schema: Database.create deletes and recreates it.
    do
        use conn = new SqliteConnection($"Data Source=%s{indexPath}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "CREATE TABLE junk (x INTEGER); PRAGMA user_version = 1;"
        cmd.ExecuteNonQuery() |> ignore

    SqliteConnection.ClearAllPools()
    let db = TestPrune.Database.Database.create indexPath
    test <@ db.WasRecreated @>
    use store = Store.Open tracePath
    test <@ store.TryRead("P|Ns.C|m", "E1") |> Option.isSome @>
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build`
Expected: FAIL, with `TraceStore` undefined.

- [ ] **Step 3: Implement**

`src/TestPrune.Trace/TraceStore.fs`:

```fsharp
module TestPrune.Trace.TraceStore

open System
open System.IO
open System.Text.Json
open Microsoft.Data.Sqlite
open TestPrune.Trace.Model

type RunKind =
    | FullRun
    | PartialRun

type RunStatus =
    | Recorded
    | Refused
    | FailedToRecord
    | TreeMovedDuringRun

type TraceRun =
    { RunId: string
      TestProject: string
      TreeHash: string
      EnvFingerprint: string
      RecordedAt: DateTimeOffset
      Kind: RunKind
      Status: RunStatus
      Reason: string
      StatsJson: string }

type ScopeContent =
    { Key: string
      Symbols: (string * string) list
      Inputs: (string * string * string) list }

type TestTrace =
    { TestKey: string
      Status: OutcomeKind option
      Reasons: IncompleteReason list
      ScopeKeys: string list }

type StoredTrace =
    { TestKey: string
      EnvFingerprint: string
      RunId: string
      Complete: bool
      Reasons: string list
      Symbols: Set<string * string>
      Inputs: Set<string * string * string> }

exception TraceSchemaNewerThanConsumer of path: string * found: int * supported: int with
    override this.Message =
        $"%s{this.path} was written by trace schema v%d{this.found}; this TestPrune.Trace supports v%d{this.supported}. Upgrade TestPrune.Trace; the file was left untouched."

/// Forward-only. Append a new (version, DDL) pair for every change; never edit a
/// published entry. The file is never deleted by code.
let migrations: (int * string) list =
    [ 1,
      """
      CREATE TABLE trace_runs (
          id INTEGER PRIMARY KEY,
          run_id TEXT NOT NULL,
          test_project TEXT NOT NULL,
          tree_hash TEXT NOT NULL,
          env_fingerprint TEXT NOT NULL,
          recorded_at TEXT NOT NULL,
          kind TEXT NOT NULL CHECK (kind IN ('full', 'partial')),
          status TEXT NOT NULL CHECK (status IN ('recorded', 'refused', 'failed', 'tree-moved')),
          reason TEXT NOT NULL DEFAULT '',
          stats_json TEXT NOT NULL DEFAULT '{}',
          UNIQUE (run_id, test_project));
      CREATE TABLE trace_scopes (
          id INTEGER PRIMARY KEY,
          trace_run_id INTEGER NOT NULL REFERENCES trace_runs(id) ON DELETE CASCADE,
          scope_key TEXT NOT NULL,
          UNIQUE (trace_run_id, scope_key));
      CREATE TABLE symbol_versions (
          id INTEGER PRIMARY KEY,
          symbol_full_name TEXT NOT NULL,
          content_hash TEXT NOT NULL,
          UNIQUE (symbol_full_name, content_hash));
      CREATE TABLE trace_entries (
          symbol_version_id INTEGER NOT NULL REFERENCES symbol_versions(id),
          scope_id INTEGER NOT NULL REFERENCES trace_scopes(id) ON DELETE CASCADE,
          PRIMARY KEY (symbol_version_id, scope_id)) WITHOUT ROWID;
      CREATE INDEX trace_entries_by_scope ON trace_entries (scope_id);
      CREATE TABLE trace_inputs (
          scope_id INTEGER NOT NULL REFERENCES trace_scopes(id) ON DELETE CASCADE,
          kind TEXT NOT NULL,
          key TEXT NOT NULL,
          hash TEXT NOT NULL,
          PRIMARY KEY (kind, key, scope_id)) WITHOUT ROWID;
      CREATE INDEX trace_inputs_by_scope ON trace_inputs (scope_id);
      CREATE TABLE trace_tests (
          id INTEGER PRIMARY KEY,
          test_key TEXT NOT NULL,
          test_project TEXT NOT NULL,
          env_fingerprint TEXT NOT NULL,
          trace_run_id INTEGER NOT NULL REFERENCES trace_runs(id),
          status TEXT NOT NULL,
          complete INTEGER NOT NULL,
          incomplete_reasons TEXT NOT NULL DEFAULT '[]',
          UNIQUE (test_key, env_fingerprint));
      CREATE TABLE trace_test_scopes (
          test_id INTEGER NOT NULL REFERENCES trace_tests(id) ON DELETE CASCADE,
          scope_id INTEGER NOT NULL REFERENCES trace_scopes(id),
          PRIMARY KEY (test_id, scope_id)) WITHOUT ROWID;
      CREATE INDEX trace_test_scopes_by_scope ON trace_test_scopes (scope_id);
      """ ]

let TraceSchemaVersion = migrations |> List.map fst |> List.max

let reasonCode (r: IncompleteReason) =
    match r with
    | NoOutcome -> "no-outcome"
    | NotPassed Failed -> "not-passed:failed"
    | NotPassed Skipped -> "not-passed:skipped"
    | NotPassed OtherOutcome -> "not-passed:other"
    | NotPassed Passed -> "not-passed:passed"
    | SourceDrift f -> $"source-drift:%s{f}"
    | NotIndexed f -> $"not-indexed:%s{f}"
    | ChildProcessUntraced f -> $"child-process-untraced:%s{f}"
    | RecorderOverflow -> "recorder-overflow"
    | TreeMoved -> "tree-moved"
    | DumpRejected why -> $"dump-rejected:%s{why}"
    | UnmappedCode detail -> $"unmapped-code:%s{detail}"

let private outcomeCode =
    function
    | Some Passed -> "passed"
    | Some Failed -> "failed"
    | Some Skipped -> "skipped"
    | Some OtherOutcome -> "other"
    | None -> "unknown"

let private runKindCode = function FullRun -> "full" | PartialRun -> "partial"

let private statusCode =
    function
    | Recorded -> "recorded"
    | Refused -> "refused"
    | FailedToRecord -> "failed"
    | TreeMovedDuringRun -> "tree-moved"

let private exec (conn: SqliteConnection) (tx: SqliteTransaction) (sql: string) (ps: (string * obj) list) =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- sql
    for n, v in ps do cmd.Parameters.AddWithValue(n, (if isNull v then box DBNull.Value else v)) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private scalar (conn: SqliteConnection) (tx: SqliteTransaction) (sql: string) (ps: (string * obj) list) : int64 =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- sql
    for n, v in ps do cmd.Parameters.AddWithValue(n, v) |> ignore
    cmd.ExecuteScalar() :?> int64

type Store private (path: string, conn: SqliteConnection) =

    static member OpenWith(migrations: (int * string) list, path: string) : Store =
        let supported = migrations |> List.map fst |> List.max
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
        let conn = new SqliteConnection($"Data Source=%s{path}")
        conn.Open()

        try
            let found =
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "PRAGMA user_version;"
                cmd.ExecuteScalar() :?> int64 |> int

            if found > supported then
                raise (TraceSchemaNewerThanConsumer(path, found, supported))

            exec conn null "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;" []

            for version, ddl in migrations |> List.filter (fun (v, _) -> v > found) |> List.sortBy fst do
                use tx = conn.BeginTransaction()
                exec conn tx ddl []
                exec conn tx $"PRAGMA user_version = %d{version};" []
                tx.Commit()

            new Store(path, conn)
        with _ ->
            conn.Dispose()
            SqliteConnection.ClearPool conn
            reraise ()

    static member Open(path: string) = Store.OpenWith(migrations, path)

    member private _.InsertRun(tx, run: TraceRun) : int64 =
        exec
            conn
            tx
            """INSERT INTO trace_runs (run_id, test_project, tree_hash, env_fingerprint, recorded_at, kind, status, reason, stats_json)
               VALUES (@r, @p, @t, @e, @at, @k, @s, @why, @stats)
               ON CONFLICT (run_id, test_project) DO UPDATE SET status = excluded.status, reason = excluded.reason, stats_json = excluded.stats_json"""
            [ "@r", box run.RunId; "@p", box run.TestProject; "@t", box run.TreeHash; "@e", box run.EnvFingerprint
              "@at", box (run.RecordedAt.ToString "O"); "@k", box (runKindCode run.Kind); "@s", box (statusCode run.Status)
              "@why", box run.Reason; "@stats", box run.StatsJson ]

        scalar conn tx "SELECT id FROM trace_runs WHERE run_id = @r AND test_project = @p" [ "@r", box run.RunId; "@p", box run.TestProject ]

    member this.RecordRunWithoutTraces(run: TraceRun) =
        use tx = conn.BeginTransaction()
        this.InsertRun(tx, run) |> ignore
        tx.Commit()

    /// One transaction: the run, its scopes with their content, and each test's
    /// trace, replacing any earlier trace of the same (test, fingerprint).
    member this.RecordRun(run: TraceRun, scopes: ScopeContent list, tests: TestTrace list) =
        use tx = conn.BeginTransaction()
        let runId = this.InsertRun(tx, run)

        let scopeIds =
            scopes
            |> List.map (fun s ->
                exec conn tx "INSERT OR IGNORE INTO trace_scopes (trace_run_id, scope_key) VALUES (@r, @k)" [ "@r", box runId; "@k", box s.Key ]
                let sid = scalar conn tx "SELECT id FROM trace_scopes WHERE trace_run_id = @r AND scope_key = @k" [ "@r", box runId; "@k", box s.Key ]

                for name, hash in s.Symbols |> List.distinct do
                    exec conn tx "INSERT OR IGNORE INTO symbol_versions (symbol_full_name, content_hash) VALUES (@n, @h)" [ "@n", box name; "@h", box hash ]

                    exec
                        conn
                        tx
                        "INSERT OR IGNORE INTO trace_entries (symbol_version_id, scope_id) SELECT id, @s FROM symbol_versions WHERE symbol_full_name = @n AND content_hash = @h"
                        [ "@s", box sid; "@n", box name; "@h", box hash ]

                for kind, key, hash in s.Inputs |> List.distinct do
                    exec conn tx "INSERT OR IGNORE INTO trace_inputs (scope_id, kind, key, hash) VALUES (@s, @k, @key, @h)" [ "@s", box sid; "@k", box kind; "@key", box key; "@h", box hash ]

                s.Key, sid)
            |> Map.ofList

        for t in tests do
            exec
                conn
                tx
                """INSERT INTO trace_tests (test_key, test_project, env_fingerprint, trace_run_id, status, complete, incomplete_reasons)
                   VALUES (@k, @p, @e, @r, @s, @c, @why)
                   ON CONFLICT (test_key, env_fingerprint) DO UPDATE SET trace_run_id = excluded.trace_run_id,
                       status = excluded.status, complete = excluded.complete, incomplete_reasons = excluded.incomplete_reasons"""
                [ "@k", box t.TestKey; "@p", box run.TestProject; "@e", box run.EnvFingerprint; "@r", box runId
                  "@s", box (outcomeCode t.Status); "@c", box (if List.isEmpty t.Reasons then 1 else 0)
                  "@why", box (JsonSerializer.Serialize(t.Reasons |> List.map reasonCode)) ]

            let tid = scalar conn tx "SELECT id FROM trace_tests WHERE test_key = @k AND env_fingerprint = @e" [ "@k", box t.TestKey; "@e", box run.EnvFingerprint ]
            exec conn tx "DELETE FROM trace_test_scopes WHERE test_id = @t" [ "@t", box tid ]

            for key in t.ScopeKeys |> List.distinct do
                match scopeIds.TryFind key with
                | Some sid -> exec conn tx "INSERT INTO trace_test_scopes (test_id, scope_id) VALUES (@t, @s)" [ "@t", box tid; "@s", box sid ]
                | None -> invalidArg "tests" $"test %s{t.TestKey} links scope %s{key}, which this run does not contain"

        tx.Commit()

    member _.TryRead(testKey: string, envFingerprint: string) : StoredTrace option =
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            """SELECT t.id, r.run_id, t.complete, t.incomplete_reasons FROM trace_tests t
               JOIN trace_runs r ON r.id = t.trace_run_id WHERE t.test_key = @k AND t.env_fingerprint = @e"""
        cmd.Parameters.AddWithValue("@k", testKey) |> ignore
        cmd.Parameters.AddWithValue("@e", envFingerprint) |> ignore
        use rd = cmd.ExecuteReader()

        if not (rd.Read()) then
            None
        else
            let tid = rd.GetInt64 0
            let runId = rd.GetString 1
            let complete = rd.GetInt64 2 = 1L
            let reasons = JsonSerializer.Deserialize<string list>(rd.GetString 3)
            rd.Close()

            let rows (sql: string) (f: SqliteDataReader -> 'a) =
                use c = conn.CreateCommand()
                c.CommandText <- sql
                c.Parameters.AddWithValue("@t", tid) |> ignore
                use r = c.ExecuteReader()
                [ while r.Read() do yield f r ] |> Set.ofList

            Some
                { TestKey = testKey
                  EnvFingerprint = envFingerprint
                  RunId = runId
                  Complete = complete
                  Reasons = reasons
                  Symbols =
                    rows
                        """SELECT v.symbol_full_name, v.content_hash FROM trace_test_scopes ts
                           JOIN trace_entries e ON e.scope_id = ts.scope_id
                           JOIN symbol_versions v ON v.id = e.symbol_version_id WHERE ts.test_id = @t"""
                        (fun r -> r.GetString 0, r.GetString 1)
                  Inputs =
                    rows
                        """SELECT i.kind, i.key, i.hash FROM trace_test_scopes ts
                           JOIN trace_inputs i ON i.scope_id = ts.scope_id WHERE ts.test_id = @t"""
                        (fun r -> r.GetString 0, r.GetString 1, r.GetString 2) }

    member _.TestKeysOf(testProject: string, envFingerprint: string) : Set<string> =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT test_key FROM trace_tests WHERE test_project = @p AND env_fingerprint = @e"
        cmd.Parameters.AddWithValue("@p", testProject) |> ignore
        cmd.Parameters.AddWithValue("@e", envFingerprint) |> ignore
        use r = cmd.ExecuteReader()
        [ while r.Read() do yield r.GetString 0 ] |> Set.ofList

    member _.Runs(testProject: string) : TraceRun list =
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT run_id, tree_hash, env_fingerprint, recorded_at, kind, status, reason, stats_json FROM trace_runs WHERE test_project = @p ORDER BY id DESC"
        cmd.Parameters.AddWithValue("@p", testProject) |> ignore
        use r = cmd.ExecuteReader()

        [ while r.Read() do
              yield
                  { RunId = r.GetString 0; TestProject = testProject; TreeHash = r.GetString 1; EnvFingerprint = r.GetString 2
                    RecordedAt = DateTimeOffset.Parse(r.GetString 3)
                    Kind = (if r.GetString 4 = "full" then FullRun else PartialRun)
                    Status =
                      (match r.GetString 5 with
                       | "recorded" -> Recorded
                       | "refused" -> Refused
                       | "failed" -> FailedToRecord
                       | _ -> TreeMovedDuringRun)
                    Reason = r.GetString 6; StatsJson = r.GetString 7 } ]

    /// A full run passes the tests it saw as `liveTestKeys`: every other trace of the
    /// project under that fingerprint belongs to a test that no longer exists. Then drop
    /// scopes no test links, runs no test or scope references (keeping the newest 50
    /// run rows per project as history), and versions no entry references.
    member _.CollectGarbage(testProject: string, envFingerprint: string, liveTestKeys: Set<string> option) =
        use tx = conn.BeginTransaction()

        match liveTestKeys with
        | Some live ->
            let json = JsonSerializer.Serialize(Set.toArray live)

            exec
                conn
                tx
                "DELETE FROM trace_tests WHERE test_project = @p AND env_fingerprint = @e AND test_key NOT IN (SELECT value FROM json_each(@live))"
                [ "@p", box testProject; "@e", box envFingerprint; "@live", box json ]
        | None -> ()

        exec conn tx "DELETE FROM trace_scopes WHERE id NOT IN (SELECT scope_id FROM trace_test_scopes)" []

        exec
            conn
            tx
            """DELETE FROM trace_runs WHERE test_project = @p
                 AND id NOT IN (SELECT trace_run_id FROM trace_tests) AND id NOT IN (SELECT trace_run_id FROM trace_scopes)
                 AND id NOT IN (SELECT id FROM trace_runs WHERE test_project = @p ORDER BY id DESC LIMIT 50)"""
            [ "@p", box testProject ]

        exec conn tx "DELETE FROM symbol_versions WHERE id NOT IN (SELECT symbol_version_id FROM trace_entries)" []
        tx.Commit()

    interface IDisposable with
        member _.Dispose() =
            SqliteConnection.ClearPool conn
            conn.Dispose()
```

Garbage collection deletes scopes no test links. So a partial run's class scope survives only while a test
links it: that is the intended retention rule. The test-key `IN` list travels as **one JSON parameter** through
`json_each`, following this repository's ADR 0003 ("name sets travel as one JSON parameter"). That avoids
SQLite's variable limit.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.TraceStoreTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t3-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj commit -m "Trace store: a separate SQLite file that survives core schema bumps

Traces cost a full recording run to rebuild, so they cannot live in the index
file core deletes on a SchemaVersion bump. The store has its own forward-only
migrations, refuses (and never touches) a newer file, keys symbols by full
name + content hash, and stores fixture/pool scope content once, linked from
each test.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Weaver core: manifest, method-entry probes, PDB invariants, optimized refusal

**Files:**

- Create: `src/TestPrune.Trace/Manifest.fs`, `PdbCheck.fs`, `Launch.fs`, `Weaver.fs` (compile in that order,
  after `TraceStore.fs`)
- Test: `tests/TestPrune.Trace.Tests/WeaverTests.fs`, `ManifestTests.fs`

**Interfaces:**

- Consumes: `Contract.*` (Task 1) and the recorder assembly's path (`typeof<Probes>.Assembly.Location`).
- Produces:

```fsharp
module TestPrune.Trace.Manifest
val write: dir: string -> Model.Manifest -> unit            // manifest.tsv + documents.tsv
val read: dir: string -> Result<Model.Manifest, string>

module TestPrune.Trace.PdbCheck
val decodeFailures: pdbPath: string -> (int * string) list   // (method row number, message); [] = healthy

module TestPrune.Trace.Launch
val dotnetRoot: unit -> string
val run: exe: string -> args: string list -> env: (string * string) list -> workDir: string -> timeout: System.TimeSpan -> int * string

module TestPrune.Trace.Weaver
type WeaveInput = { Path: string; Mode: Model.WeaveMode }
type WeaveError =
    | Optimized of assembly: string
    | MissingPdb of assembly: string
    | UnboundSequencePoint of assembly: string * methodName: string
    | PdbCorrupt of assembly: string * failures: int
    | CecilFailed of assembly: string * message: string
type WeaveSet =
    { Modules: (Mono.Cecil.ModuleDefinition * Model.WeaveMode) list
      Alloc: Model.ManifestRow -> int
      RecorderMethod: Mono.Cecil.ModuleDefinition -> string -> string -> Mono.Cecil.MethodReference }  // module, recorder type, method name
type IWeavePass =
    abstract Prepare: WeaveSet -> unit
    abstract Rewrite: WeaveSet * Mono.Cecil.ModuleDefinition * Model.WeaveMode * Mono.Cecil.MethodDefinition -> bool
type WeaveStats = { MethodsProbed: int; OrphanSequencePointsRemoved: int; Touched: Map<string, string list> }
type WeaveResult = { Manifest: Model.Manifest; Outputs: string list; Stats: WeaveStats }
val isOptimized: Mono.Cecil.AssemblyDefinition -> bool
val weave: passes: IWeavePass list -> inputs: WeaveInput list -> outputDir: string -> Result<WeaveResult, WeaveError>
```

`Touched` maps an assembly name to `"<CLR type full name>::<method name>"` keys, every method whose body
changed. Task 8 hands the list to the JIT verifier.

- [ ] **Step 1: Write the failing tests**

`tests/TestPrune.Trace.Tests/ManifestTests.fs`:

```fsharp
module TestPrune.Trace.Tests.ManifestTests

open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model

[<Fact>]
let ``a manifest round-trips`` () =
    let dir = Directory.CreateTempSubdirectory().FullName

    let m =
        { Rows =
            [| { Id = 0; Kind = UserMethod; Assembly = "A"; TypeName = "N.M"; Member = "f"; Document = Some "/r/M.fs"; FirstLine = 3; LastLine = 5 }
               { Id = 1; Kind = UnionCase; Assembly = "A"; TypeName = "N.M+U"; Member = "X"; Document = None; FirstLine = 0; LastLine = 0 } |]
          Documents = Map.ofList [ "/r/M.fs", "ab12" ]
          IdCount = 2 }

    Manifest.write dir m
    test <@ Manifest.read dir = Ok m @>

[<Fact>]
let ``a field containing a tab is refused at write time`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    let bad = { Rows = [| { Id = 0; Kind = UserMethod; Assembly = "A"; TypeName = "N\tM"; Member = "f"; Document = None; FirstLine = 0; LastLine = 0 } |]; Documents = Map.empty; IdCount = 1 }
    raises<System.ArgumentException> <@ Manifest.write dir bad @>
```

`tests/TestPrune.Trace.Tests/WeaverTests.fs`:

```fsharp
module TestPrune.Trace.Tests.WeaverTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Mono.Cecil
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Weaver
open TestPrune.Trace.Tests

/// Copy a fixture's build output to a scratch dir INSIDE the repo (so repo-root probing
/// still works) and return it.
let copyFixture (sourceDir: string) =
    let dir = Path.Combine(Fixtures.repoRoot, "tests", "TraceFixtures", "bin-scratch", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore

    for f in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories) do
        let dst = Path.Combine(dir, Path.GetRelativePath(sourceDir, f))
        Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
        File.Copy(f, dst)

    dir

let weaveFx (passes: IWeavePass list) =
    let dir = copyFixture Fixtures.fxDriverDir
    let out = Path.Combine(dir, "woven")

    let r =
        weave passes [ { Path = Path.Combine(dir, "FxLib.dll"); Mode = Full }; { Path = Path.Combine(dir, "FxDriver.dll"); Mode = SitesOnly } ] out
        |> Result.defaultWith (fun e -> failwith $"%A{e}")

    for f in r.Outputs do
        File.Copy(f, Path.Combine(dir, Path.GetFileName f), true)
        File.Copy(Path.ChangeExtension(f, ".pdb"), Path.Combine(dir, Path.ChangeExtension(Path.GetFileName f, ".pdb")), true)

    dir, r

[<Fact>]
let ``every product method gets an entry probe row that points at its source`` () =
    let _, r = weaveFx []
    let area = r.Manifest.Rows |> Array.find (fun x -> x.TypeName = "FxLib.Logic" && x.Member = "area")
    test <@ area.Kind = UserMethod @>
    test <@ area.Document |> Option.exists (fun d -> d.EndsWith "Logic.fs") @>
    test <@ area.FirstLine >= 5 && area.LastLine <= 9 @>
    test <@ r.Manifest.Documents |> Map.exists (fun p h -> p.EndsWith "Logic.fs" && h.Length = 64) @>

[<Fact>]
let ``a sites-only assembly probes only its test entry points`` () =
    let _, r = weaveFx []
    let driverRows = r.Manifest.Rows |> Array.filter (fun x -> x.Assembly = "FxDriver")
    // FxDriver has no [<Fact>]s, so sites-only means no entry probes at all.
    test <@ driverRows = [||] @>

[<Fact>]
let ``woven PDBs decode with System.Reflection.Metadata and the end-of-method marker bug is exercised`` () =
    let dir, r = weaveFx []
    test <@ PdbCheck.decodeFailures (Path.Combine(dir, "FxLib.pdb")) = [] @>
    // F# emits the unbindable end-of-method hidden sequence point on ordinary methods;
    // if this is 0 the regression below guards nothing.
    test <@ r.Stats.OrphanSequencePointsRemoved > 0 @>

[<Fact>]
let ``an optimized assembly is refused`` () =
    let dir = copyFixture Fixtures.fxLibDir
    let path = Path.Combine(dir, "FxLib.dll")

    do
        use asm = AssemblyDefinition.ReadAssembly(path, ReaderParameters(ReadWrite = true, ReadSymbols = true))
        let dbg = asm.CustomAttributes |> Seq.find (fun a -> a.AttributeType.Name = "DebuggableAttribute")
        asm.CustomAttributes.Remove dbg |> ignore
        asm.Write(WriterParameters(WriteSymbols = true))

    test <@ weave [] [ { Path = path; Mode = Full } ] (Path.Combine(dir, "woven")) = Error(Optimized "FxLib") @>

[<Fact>]
let ``weaving is deterministic`` () =
    let _, a = weaveFx []
    let _, b = weaveFx []
    test <@ a.Manifest = b.Manifest @>

[<Fact>]
let ``the woven driver runs and attributes each scenario to its own scope`` () =
    let dir, r = weaveFx []
    let out = Path.Combine(dir, "traces")

    let code, output =
        Launch.run
            (Path.Combine(dir, "FxDriver"))
            []
            [ Recorder.Contract.OutEnv, out
              Recorder.Contract.IdsEnv, string r.Manifest.IdCount
              Recorder.Contract.RepoRootEnv, Fixtures.repoRoot ]
            dir
            (TimeSpan.FromMinutes 1.0)

    test <@ code = 0 @>
    let dumps, bad = DumpReader.readDirectory out
    test <@ bad = [] @>
    let idOf typ mem = (r.Manifest.Rows |> Array.find (fun x -> x.TypeName = typ && x.Member = mem)).Id
    let scope key = dumps |> List.collect (fun d -> d.Scopes) |> List.find (fun s -> s.Key = key)
    test <@ (scope "T:caseA_area").Ids |> Array.contains (idOf "FxLib.Logic" "area") @>
    test <@ not ((scope "T:caseA_area").Ids |> Array.contains (idOf "FxLib.Logic" "turn")) @>
```

`r.Stats.OrphanSequencePointsRemoved > 0` holds for the F# compiler this repository pins. If a future compiler
stops emitting the marker, the test fails loudly; that is the intended alarm, because the guarded code path
would then be dead.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build`
Expected: FAIL, with `Manifest`, `PdbCheck`, `Launch` and `Weaver` undefined.

- [ ] **Step 3: Implement**

`src/TestPrune.Trace/Manifest.fs`:

```fsharp
module TestPrune.Trace.Manifest

open System
open System.IO
open TestPrune.Trace.Model

let private kindCode =
    function
    | UserMethod -> "user"
    | GeneratedMethod -> "gen"
    | StaticCtor -> "cctor"
    | UnionCase -> "case"
    | TypeUse -> "type"

let private kindOf =
    function
    | "user" -> Ok UserMethod
    | "gen" -> Ok GeneratedMethod
    | "cctor" -> Ok StaticCtor
    | "case" -> Ok UnionCase
    | "type" -> Ok TypeUse
    | other -> Error $"unknown probe kind '%s{other}'"

let private field (s: string) =
    if s.IndexOfAny [| '\t'; '\n'; '\r' |] >= 0 then
        invalidArg "manifest" $"manifest field contains a tab or newline: %s{s}"

    s

let write (dir: string) (m: Manifest) =
    Directory.CreateDirectory dir |> ignore

    let rows =
        m.Rows
        |> Array.map (fun r ->
            String.Join(
                "\t",
                [| string r.Id; kindCode r.Kind; field r.Assembly; field r.TypeName; field r.Member
                   field (defaultArg r.Document ""); string r.FirstLine; string r.LastLine |]
            ))

    File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), rows)

    File.WriteAllLines(
        Path.Combine(dir, "documents.tsv"),
        m.Documents |> Map.toArray |> Array.map (fun (p, h) -> field p + "\t" + h)
    )

let read (dir: string) : Result<Manifest, string> =
    try
        let rows =
            File.ReadAllLines(Path.Combine(dir, "manifest.tsv"))
            |> Array.map (fun line ->
                let c = line.Split '\t'

                match kindOf c.[1] with
                | Error e -> failwith e
                | Ok kind ->
                    { Id = int c.[0]; Kind = kind; Assembly = c.[2]; TypeName = c.[3]; Member = c.[4]
                      Document = (if c.[5] = "" then None else Some c.[5]); FirstLine = int c.[6]; LastLine = int c.[7] })

        let docs =
            File.ReadAllLines(Path.Combine(dir, "documents.tsv"))
            |> Array.map (fun l -> let c = l.Split '\t' in c.[0], c.[1])
            |> Map.ofArray

        Ok { Rows = rows; Documents = docs; IdCount = rows.Length }
    with ex ->
        Error ex.Message
```

`src/TestPrune.Trace/PdbCheck.fs`:

```fsharp
/// The invariant MS CodeCoverage depends on: every method's sequence-point blob in a
/// portable PDB decodes with System.Reflection.Metadata. Cecil tolerates blobs SRM
/// rejects, so this must be checked with SRM itself.
module TestPrune.Trace.PdbCheck

open System.IO
open System.Reflection.Metadata

let decodeFailures (pdbPath: string) : (int * string) list =
    use fs = File.OpenRead pdbPath
    use provider = MetadataReaderProvider.FromPortablePdbStream fs
    let reader = provider.GetMetadataReader()

    // Method-debug-information rows are 1-based and enumerate in row order, so the index
    // is the row number (a diagnostic only).
    [ for index, h in Seq.indexed reader.MethodDebugInformation do
          let info = reader.GetMethodDebugInformation h

          if not info.SequencePointsBlob.IsNil then
              let failure =
                  try
                      for _ in info.GetSequencePoints() do
                          ()

                      None
                  with ex ->
                      Some ex.Message

              match failure with
              | Some message -> yield index + 1, message
              | None -> () ]
```

`src/TestPrune.Trace/Launch.fs`:

```fsharp
module TestPrune.Trace.Launch

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices

/// The dotnet root an apphost needs. DOTNET_ROOT wins; otherwise the root of the
/// runtime THIS process runs on (…/shared/Microsoft.NETCore.App/<v>/ → three levels up).
/// Deliberately not "the `dotnet` on PATH": a repository may put a wrapper script there.
let dotnetRoot () : string =
    match Environment.GetEnvironmentVariable "DOTNET_ROOT" with
    | null
    | "" -> Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."))
    | root -> root

/// Run `exe` to completion (or kill it at `timeout`), returning exit code and the
/// combined output. Both pipes are drained concurrently.
let run (exe: string) (args: string list) (env: (string * string) list) (workDir: string) (timeout: TimeSpan) : int * string =
    let psi = ProcessStartInfo(exe, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workDir)

    for a in args do
        psi.ArgumentList.Add a

    psi.Environment.["DOTNET_ROOT"] <- dotnetRoot ()

    for k, v in env do
        psi.Environment.[k] <- v

    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()

    if not (p.WaitForExit(int timeout.TotalMilliseconds)) then
        p.Kill true
        p.WaitForExit()

    p.WaitForExit()
    p.ExitCode, out.Result + err.Result
```

`src/TestPrune.Trace/Weaver.fs`:

```fsharp
module TestPrune.Trace.Weaver

open System
open System.Collections.Generic
open System.IO
open Mono.Cecil
open Mono.Cecil.Cil
open Mono.Cecil.Rocks
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

type WeaveInput = { Path: string; Mode: WeaveMode }

type WeaveError =
    | Optimized of assembly: string
    | MissingPdb of assembly: string
    | UnboundSequencePoint of assembly: string * methodName: string
    | PdbCorrupt of assembly: string * failures: int
    | CecilFailed of assembly: string * message: string

type WeaveSet =
    { Modules: (ModuleDefinition * WeaveMode) list
      Alloc: ManifestRow -> int
      RecorderMethod: ModuleDefinition -> string -> string -> MethodReference }

type IWeavePass =
    abstract Prepare: WeaveSet -> unit
    abstract Rewrite: WeaveSet * ModuleDefinition * WeaveMode * MethodDefinition -> bool

type WeaveStats =
    { MethodsProbed: int
      OrphanSequencePointsRemoved: int
      Touched: Map<string, string list> }

type WeaveResult =
    { Manifest: Manifest
      Outputs: string list
      Stats: WeaveStats }

exception private WeaveFailure of WeaveError

/// Debug F# builds carry DebuggableAttribute with DisableOptimizations (0x100). Anything
/// else may have inlined small functions across assemblies, whose entry probes then
/// never fire: refuse rather than record a trace that silently misses them.
let isOptimized (asm: AssemblyDefinition) =
    match asm.CustomAttributes |> Seq.tryFind (fun a -> a.AttributeType.Name = "DebuggableAttribute") with
    | None -> true
    | Some a when a.ConstructorArguments.Count = 1 -> (Convert.ToInt32(a.ConstructorArguments.[0].Value) &&& 0x100) = 0
    | Some a when a.ConstructorArguments.Count = 2 -> not (Convert.ToBoolean(a.ConstructorArguments.[1].Value))
    | Some _ -> true

let private hasAttr (name: string) (p: ICustomAttributeProvider) =
    p.HasCustomAttributes && p.CustomAttributes |> Seq.exists (fun a -> a.AttributeType.Name = name)

let rec private isGeneratedType (t: TypeDefinition) =
    not (isNull t)
    && (t.Name.Contains '@' || t.Name.StartsWith "<" || hasAttr "CompilerGeneratedAttribute" t || isGeneratedType t.DeclaringType)

let private kindOf (t: TypeDefinition) (m: MethodDefinition) =
    if m.IsConstructor && m.IsStatic then StaticCtor
    elif hasAttr "CompilerGeneratedAttribute" m || isGeneratedType t then GeneratedMethod
    else UserMethod

/// A test entry point: a method carrying xUnit's FactAttribute or anything derived
/// from it (TheoryAttribute, custom facts). Probing it puts the test's own symbol in
/// its trace, so a test-body edit invalidates the trace, and guarantees the test's
/// scope exists even when it runs no product code.
let private isTestMethod (m: MethodDefinition) =
    let rec fact (t: TypeReference) depth =
        if isNull t || depth > 8 then false
        elif t.FullName = "Xunit.FactAttribute" then true
        else
            match (try t.Resolve() with _ -> null) with
            | null -> t.Name = "FactAttribute" || t.Name = "TheoryAttribute"
            | d -> fact d.BaseType (depth + 1)

    m.HasCustomAttributes && m.CustomAttributes |> Seq.exists (fun a -> fact a.AttributeType 0)

let private sha256Hex (bytes: byte[]) =
    if isNull bytes || bytes.Length = 0 then "" else Convert.ToHexString(bytes).ToLowerInvariant()

let weave (passes: IWeavePass list) (inputs: WeaveInput list) (outputDir: string) : Result<WeaveResult, WeaveError> =
    let recorderPath = typeof<Probes>.Assembly.Location
    let recorder = ModuleDefinition.ReadModule recorderPath
    let rows = ResizeArray<ManifestRow>()
    let documents = Dictionary<string, string>()
    let touched = Dictionary<string, ResizeArray<string>>()
    let mutable probed = 0
    let mutable orphansRemoved = 0

    let alloc (row: ManifestRow) =
        let id = rows.Count
        rows.Add { row with Id = id }
        id

    let recorderMethod (m: ModuleDefinition) (typeName: string) (name: string) =
        let t = recorder.GetType typeName
        m.ImportReference(t.Methods |> Seq.find (fun x -> x.Name = name))

    try
        let modules =
            inputs
            |> List.map (fun input ->
                let name = Path.GetFileNameWithoutExtension input.Path

                if not (File.Exists(Path.ChangeExtension(input.Path, ".pdb"))) then
                    raise (WeaveFailure(MissingPdb name))

                let resolver = new DefaultAssemblyResolver()
                resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath input.Path))
                resolver.AddSearchDirectory(Path.GetDirectoryName recorderPath)

                let m =
                    ModuleDefinition.ReadModule(
                        input.Path,
                        ReaderParameters(ReadSymbols = true, InMemory = true, AssemblyResolver = resolver, SymbolReaderProvider = PortablePdbReaderProvider())
                    )

                if isOptimized m.Assembly then
                    raise (WeaveFailure(Optimized name))

                m, input.Mode)

        let set = { Modules = modules; Alloc = alloc; RecorderMethod = recorderMethod }

        for p in passes do
            p.Prepare set

        for m, mode in modules do
            let asmName = m.Assembly.Name.Name
            let hit = recorderMethod m Contract.ProbesType Contract.Hit

            for t in m.GetTypes() |> Seq.toList do
                for meth in t.Methods |> Seq.toList do
                    if meth.HasBody then
                        let dbg = meth.DebugInformation
                        let body = meth.Body

                        // PDB INVARIANT. F# emits a hidden sequence point at offset == code size
                        // (end of method). Cecil cannot bind it to an instruction and would write it
                        // back at its OLD offset after insertions: a negative delta in the portable
                        // PDB, which SRM (and so MS CodeCoverage) rejects for the whole method. It
                        // carries no line, so remove it. A NON-hidden unbound point would carry a line
                        // we would lose: refuse instead.
                        if dbg.HasSequencePoints then
                            let offsets = HashSet<int>(body.Instructions |> Seq.map (fun i -> i.Offset))

                            let orphans =
                                dbg.SequencePoints |> Seq.filter (fun sp -> not (offsets.Contains sp.Offset)) |> Seq.toList

                            for sp in orphans do
                                if not sp.IsHidden then
                                    raise (WeaveFailure(UnboundSequencePoint(asmName, t.FullName + "::" + meth.Name)))

                                dbg.SequencePoints.Remove sp |> ignore
                                orphansRemoved <- orphansRemoved + 1

                        body.SimplifyMacros()

                        let mutable changed = false

                        for p in passes do
                            if p.Rewrite(set, m, mode, meth) then
                                changed <- true

                        if mode = Full || isTestMethod meth then
                            let sps = if dbg.HasSequencePoints then dbg.SequencePoints |> Seq.filter (fun s -> not s.IsHidden) |> Seq.toList else []
                            let first = List.tryHead sps
                            let last = List.tryLast sps

                            for sp in sps do
                                if not (documents.ContainsKey sp.Document.Url) then
                                    documents.[sp.Document.Url] <- sha256Hex sp.Document.Hash

                            let id =
                                alloc
                                    { Id = 0
                                      Kind = kindOf t meth
                                      Assembly = asmName
                                      TypeName = t.FullName.Replace('/', '+')
                                      Member = meth.Name
                                      Document = first |> Option.map (fun s -> s.Document.Url)
                                      FirstLine = first |> Option.map (fun s -> s.StartLine) |> Option.defaultValue 0
                                      LastLine = last |> Option.map (fun s -> s.EndLine) |> Option.defaultValue 0 }

                            let il = body.GetILProcessor()
                            let head = body.Instructions.[0]
                            il.InsertBefore(head, il.Create(OpCodes.Ldc_I4, id))
                            il.InsertBefore(head, il.Create(OpCodes.Call, hit))
                            probed <- probed + 1
                            changed <- true

                        body.OptimizeMacros()

                        if changed then
                            // Keep the root local scope spanning the whole body, as the compiler emitted it.
                            if not (isNull dbg.Scope) && not dbg.Scope.Start.IsEndOfMethod then
                                dbg.Scope.Start <- InstructionOffset(body.Instructions.[0])

                            match touched.TryGetValue asmName with
                            | true, l -> l.Add(t.FullName.Replace('/', '+') + "::" + meth.Name)
                            | _ -> touched.[asmName] <- ResizeArray [ t.FullName.Replace('/', '+') + "::" + meth.Name ]

        Directory.CreateDirectory outputDir |> ignore

        let outputs =
            modules
            |> List.map (fun (m, _) ->
                let dst = Path.Combine(outputDir, m.Assembly.Name.Name + ".dll")
                m.Write(dst, WriterParameters(WriteSymbols = true, SymbolWriterProvider = PortablePdbWriterProvider()))

                match PdbCheck.decodeFailures (Path.ChangeExtension(dst, ".pdb")) with
                | [] -> dst
                | failures -> raise (WeaveFailure(PdbCorrupt(m.Assembly.Name.Name, failures.Length))))

        Ok
            { Manifest =
                { Rows = rows.ToArray()
                  Documents = documents |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                  IdCount = rows.Count }
              Outputs = outputs
              Stats =
                { MethodsProbed = probed
                  OrphanSequencePointsRemoved = orphansRemoved
                  Touched = touched |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Map.ofSeq } }
    with
    | WeaveFailure e -> Error e
    | ex -> Error(CecilFailed("?", ex.Message))
```

`typeof<Probes>` is used only to locate the recorder DLL. The weaver never *calls* the recorder.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.WeaverTests --filter-class TestPrune.Trace.Tests.ManifestTests`
Expected: PASS, 8 tests. Add `tests/TraceFixtures/bin-scratch/` to `.gitignore`, and delete scratch directories
in a `finally` inside `copyFixture` callers once the assertions are done.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t4-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj describe --stdin <<'MSG'
Weaver: method-entry probes with a manifest, PDB invariants and optimized refusal

Every product method gets `ldc.i4 id; call Probes.Hit` before its first
instruction; a sites-only (test) assembly probes only its [Fact]/[Theory]
entry points, which puts the test's own symbol in its trace. The unbindable
hidden end-of-method sequence point F# emits is removed before rewriting:
left in place, Cecil writes it back at its old offset, SRM rejects the method
and MS CodeCoverage silently drops it. Every woven PDB is SRM-decoded before
the weave is accepted. Optimized assemblies are refused.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
MSG
jj new
```

---

## Task 5: Union-case, type and static-init probes

**Files:**

- Create: `src/TestPrune.Trace/SiteProbes.fs` (compile after `Weaver.fs`)
- Test: `tests/TestPrune.Trace.Tests/SiteProbeScenarioTests.fs`

**Interfaces:**

- Consumes: `Weaver.IWeavePass`, `Weaver.WeaveSet`, `Weaver.weave` (Task 4); `Contract.HitIfNotNull`,
  `HitIfTrue`, `HitTag`, `EnterStatic` and `ExitStatic` (Task 1).
- Produces: `SiteProbes.pass : unit -> Weaver.IWeavePass`, a fresh pass per weave because it holds that weave's
  catalogue. Manifest rows of kind `UnionCase` have `TypeName` set to the union's CLR name (with `+`) and
  `Member` set to the case name. Their ids are contiguous per union, in tag order, so `HitTag(tag, baseId)`
  records `baseId + tag`. Rows of kind `TypeUse` have `TypeName` set to the type's CLR name and an empty
  `Member`.

- [ ] **Step 1: Write the failing test**

`tests/TestPrune.Trace.Tests/SiteProbeScenarioTests.fs`:

```fsharp
module TestPrune.Trace.Tests.SiteProbeScenarioTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Tests

type Key =
    | Case of union: string * case: string
    | Ty of string
    | Meth of typ: string * mem: string

/// Weave FxLib (full) + FxDriver (sites-only) with the site probes, run the driver once,
/// and return (manifest, scope key -> set of manifest keys it recorded).
let private recorded =
    lazy
        (let dir, r = WeaverTests.weaveFx [ SiteProbes.pass () ]
         let out = Path.Combine(dir, "traces")

         let code, output =
             Launch.run (Path.Combine(dir, "FxDriver")) []
                 [ Recorder.Contract.OutEnv, out; Recorder.Contract.IdsEnv, string r.Manifest.IdCount
                   Recorder.Contract.RepoRootEnv, Fixtures.repoRoot ]
                 dir (TimeSpan.FromMinutes 1.0)

         if code <> 0 then failwith $"woven driver failed (invalid IL?):\n%s{output}"

         let keyOf (row: ManifestRow) =
             match row.Kind with
             | UnionCase -> Case(row.TypeName, row.Member)
             | TypeUse -> Ty row.TypeName
             | _ -> Meth(row.TypeName, row.Member)

         let dumps, _ = DumpReader.readDirectory out

         dumps
         |> List.collect (fun d -> d.Scopes)
         |> List.map (fun s -> s.Key, s.Ids |> Array.map (fun id -> keyOf r.Manifest.Rows.[id]) |> Set.ofArray)
         |> Map.ofList)

let private scenarios: obj[] seq =
    [ "caseA_area", [ Case("FxLib.Shape", "Circle"); Meth("FxLib.Logic", "area") ], [ Case("FxLib.Shape", "Square") ]
      "caseB_isCircleOnly", [ Meth("FxLib.Logic", "isCircleOnly") ], [ Case("FxLib.Shape", "Circle"); Case("FxLib.Shape", "Square") ]
      "static_case_match", [ Meth("FxLib.Values", "get_defaultShape"); Case("FxLib.Shape", "Square"); Meth("FxLib.Logic", "area") ], [ Case("FxLib.Shape", "Circle") ]
      "tag5", [ Case("FxLib.Color5", "Blue") ], [ Case("FxLib.Color5", "Red"); Case("FxLib.Color5", "Green"); Case("FxLib.Color5", "Cyan"); Case("FxLib.Color5", "Magenta") ]
      "tag5_static", [ Case("FxLib.Color5", "Blue"); Meth("FxLib.Values", "get_prebuiltBlue") ], [ Case("FxLib.Color5", "Red"); Case("FxLib.Color5", "Magenta") ]
      "nullary", [ Case("FxLib.Dir", "East"); Case("FxLib.Dir", "South") ], [ Case("FxLib.Dir", "North"); Case("FxLib.Dir", "West") ]
      "struct", [ Case("FxLib.SResult", "SErr") ], [ Case("FxLib.SResult", "SOk") ]
      "tricky_tag", [ Case("FxLib.Tricky", "Tag") ], [ Case("FxLib.Tricky", "Other") ]
      "modval", [ Meth("FxLib.Values", "get_threshold") ], [ Meth("FxLib.Values", "get_unusedValue"); Meth("FxLib.Values", "computeThreshold") ]
      "sameFileValue", [ Meth("FxLib.Values", "thresholdPlus"); Meth("FxLib.Values", "get_threshold") ], []
      "typetest_dog", [ Ty "FxLib.Dog" ], [ Ty "FxLib.Cat" ]
      "typetest_cat", [ Ty "FxLib.Cat" ], [ Ty "FxLib.Dog" ]
      "unboxgeneric", [ Ty "FxLib.Dog" ], [ Ty "FxLib.Cat" ]
      "record", [ Ty "FxLib.Point"; Meth("FxLib.Logic", "sumPoint") ], []
      "testcode_match", [ Case("FxLib.Shape", "Circle") ], [ Case("FxLib.Shape", "Square") ]
      "testcode_typetest", [], [ Ty "FxLib.Dog" ]
      "constValue", [ Meth("FxLib.Logic", "addConst"); Meth("FxLib.Values", "get_constValue") ], []
      "literal", [ Meth("FxLib.Logic", "addLiteral") ], []
      "activePattern", [ Meth("FxLib.Logic", "|Big|Small|") ], []
      "unionEquality", [ Case("FxLib.Shape", "Circle") ], [ Case("FxLib.Shape", "Square") ] ]
    |> Seq.map (fun (n, must, mustNot) -> [| box n; box must; box mustNot |])

[<Theory>]
[<MemberData(nameof scenarios)>]
let ``each scenario records what it executed and nothing it did not`` (name: string, must: Key list, mustNot: Key list) =
    let got = recorded.Value.["T:" + name]
    test <@ must |> List.filter (fun k -> not (got.Contains k)) = [] @>
    test <@ mustNot |> List.filter got.Contains = [] @>

[<Fact>]
let ``static init lands in its own scope, not in the test that triggered it`` () =
    let s = recorded.Value.["S:static-init"]
    test <@ s.Contains(Meth("FxLib.Values", "computeThreshold")) @>
    test <@ s.Contains(Case("FxLib.Shape", "Square")) @>
    test <@ s.Contains(Case("FxLib.Color5", "Blue")) @>
    test <@ not (recorded.Value.["T:warm"].Contains(Meth("FxLib.Values", "computeThreshold"))) @>
```

The `must`/`mustNot` table is the spike's fixture table, expressed in manifest keys. `isCaseProp` is omitted on
purpose: a Debug `x.IsCircle` on a `Square` records `Circle`. That over-approximation is sound, and the test
must not freeze it either way. `tricky_tag` is new: it is the regression for both invalid-IL bugs. With either
bug present, the driver throws `InvalidProgramException` and `recorded` fails first.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build`
Expected: FAIL, with `SiteProbes` undefined.

- [ ] **Step 3: Implement**

`src/TestPrune.Trace/SiteProbes.fs`:

```fsharp
/// Site probes: record which union CASES and which product TYPES a method's code
/// actually touched, not just which methods it entered. F# pattern matches compile to
/// `get_Tag`+switch, `isinst` on case classes, or `castclass`/`ldfld` on a case class;
/// type tests to `isinst`/`castclass`/`unbox.any`/FSharp.Core intrinsics.
module TestPrune.Trace.SiteProbes

open System
open System.Collections.Generic
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder
open TestPrune.Trace.Weaver

/// F# `SourceConstructFlags` from `CompilationMappingAttribute`: SumType=1, Module=7, UnionCase=8.
let private fsMapping (p: ICustomAttributeProvider) =
    p.CustomAttributes
    |> Seq.tryFind (fun a -> a.AttributeType.Name = "CompilationMappingAttribute" && a.ConstructorArguments.Count > 0)
    |> Option.map (fun a ->
        let flags = Convert.ToInt32(a.ConstructorArguments.[0].Value) &&& 31
        let index = if a.ConstructorArguments.Count >= 2 then Convert.ToInt32(a.ConstructorArguments.[1].Value) else -1
        flags, index)

let private flagsOf p = fsMapping p |> Option.map fst |> Option.defaultValue -1
let private clrName (t: TypeReference) = t.FullName.Replace('/', '+')

/// The union's Tag PROPERTY getter: instance, returning int32. A union may ALSO have a
/// nullary case literally named `Tag`, whose singleton getter is a STATIC `get_Tag`
/// returning the union. Treating that one as the tag getter feeds an object to
/// HitTag(int) and produces invalid IL.
let private isTagGetter (m: MethodDefinition) =
    m.Name = "get_Tag" && not m.IsStatic && m.ReturnType.MetadataType = MetadataType.Int32

type private Catalogue() =
    member val UnionBase = Dictionary<string, int>()
    member val UnionCases = Dictionary<string, Dictionary<string, int>>()
    member val TypeIds = Dictionary<string, int>()
    /// Identified structurally (the field the instance tag getter returns), never by
    /// name: a case field named `tag` gets a backing field `_tag` too.
    member val TagFields = HashSet<string>()
    member val Products = Dictionary<string, ModuleDefinition>()

type private Pass() =
    let cat = Catalogue()

    let def (tr: TypeReference) : TypeDefinition =
        if isNull tr then
            null
        else
            let tr = tr.GetElementType()

            match tr with
            | :? GenericParameter -> null
            | _ ->
                let asm =
                    match tr with
                    | :? TypeDefinition as d -> d.Module.Assembly.Name.Name
                    | _ ->
                        match tr.Scope with
                        | :? AssemblyNameReference as a -> a.Name
                        | :? ModuleDefinition as m -> m.Assembly.Name.Name
                        | _ -> null

                match (if isNull asm then None else Some asm) |> Option.bind (fun a -> match cat.Products.TryGetValue a with | true, m -> Some m | _ -> None) with
                | None -> null
                | Some m -> (try m.GetType(tr.FullName) with _ -> null) |> fun d -> if isNull d then (try tr.Resolve() with _ -> null) else d

    let caseIdOf (d: TypeDefinition) =
        if isNull d || isNull d.DeclaringType then
            None
        else
            let u = d.DeclaringType

            match cat.UnionBase.TryGetValue u.FullName with
            | true, b when not (isNull d.BaseType) && d.BaseType.GetElementType().FullName = u.FullName ->
                let cases = cat.UnionCases.[u.FullName]

                // Nullary cases of a union that also has fields compile to a class named `_X`.
                match cases.TryGetValue d.Name with
                | true, i when i >= 0 -> Some(b + i)
                | _ ->
                    match cases.TryGetValue(d.Name.TrimStart '_') with
                    | true, i when i >= 0 -> Some(b + i)
                    | _ -> None
            | _ -> None

    let typeIdOf (d: TypeDefinition) =
        if isNull d then None
        else match cat.TypeIds.TryGetValue d.FullName with | true, i -> Some i | _ -> None

    let within (m: MethodDefinition) (t: TypeDefinition) =
        let rec go (x: TypeDefinition) = not (isNull x) && (x.FullName = t.FullName || go x.DeclaringType)
        go m.DeclaringType

    interface IWeavePass with
        member _.Prepare(set) =
            for m, mode in set.Modules do
                if mode = Full then
                    cat.Products.[m.Assembly.Name.Name] <- m

            for m, mode in set.Modules do
                if mode = Full then
                    for t in m.GetTypes() do
                        let fl = flagsOf t

                        if fl = 1 then
                            let cases = Dictionary<string, int>()

                            for meth in t.Methods do
                                match fsMapping meth with
                                | Some(8, index) ->
                                    let n =
                                        if meth.Name.StartsWith "New" && meth.IsStatic && meth.Parameters.Count > 0 then meth.Name.Substring 3
                                        elif meth.Name.StartsWith "get_" then meth.Name.Substring 4
                                        else meth.Name

                                    cases.[n] <- index
                                | _ -> ()

                            match t.NestedTypes |> Seq.tryFind (fun n -> n.Name = "Tags") with
                            | Some tags ->
                                for f in tags.Fields do
                                    if f.HasConstant then cases.[f.Name] <- Convert.ToInt32 f.Constant
                            | None -> ()

                            if cases.Count > 0 then
                                let n = (cases.Values |> Seq.max) + 1
                                let names = Array.create n ""

                                for KeyValue(name, i) in cases do
                                    if i >= 0 then names.[i] <- name

                                let mutable baseId = -1

                                for i in 0 .. n - 1 do
                                    let id =
                                        set.Alloc
                                            { Id = 0; Kind = UnionCase; Assembly = m.Assembly.Name.Name; TypeName = clrName t
                                              Member = (if names.[i] = "" then $"#%d{i}" else names.[i]); Document = None; FirstLine = 0; LastLine = 0 }

                                    if i = 0 then baseId <- id

                                cat.UnionBase.[t.FullName] <- baseId
                                cat.UnionCases.[t.FullName] <- cases

                                match t.Methods |> Seq.tryFind (fun x -> isTagGetter x && x.HasBody) with
                                | Some tg ->
                                    let ins = tg.Body.Instructions |> Seq.filter (fun i -> i.OpCode <> OpCodes.Nop) |> Seq.toArray

                                    if ins.Length = 3 && ins.[1].OpCode = OpCodes.Ldfld then
                                        cat.TagFields.Add((ins.[1].Operand :?> FieldReference).FullName) |> ignore
                                | None -> ()

                        let isCaseClass =
                            not (isNull t.DeclaringType) && flagsOf t.DeclaringType = 1
                            && not (isNull t.BaseType) && t.BaseType.GetElementType().FullName = t.DeclaringType.FullName

                        if fl <> 1 && fl <> 7 && not (t.Name.Contains '@') && not (t.FullName.StartsWith "<")
                           && t.Name <> "Tags" && not isCaseClass && not (t.Name.EndsWith "@DebugTypeProxy")
                           && not t.IsInterface && not t.IsEnum then
                            cat.TypeIds.[t.FullName] <-
                                set.Alloc { Id = 0; Kind = TypeUse; Assembly = m.Assembly.Name.Name; TypeName = clrName t; Member = ""; Document = None; FirstLine = 0; LastLine = 0 }

        member _.Rewrite(set, m, mode, meth) =
            let body = meth.Body
            let il = body.GetILProcessor()
            let r name = set.RecorderMethod m Contract.ProbesType name
            let hit, hitNN, hitTrue, hitTag = r Contract.Hit, r Contract.HitIfNotNull, r Contract.HitIfTrue, r Contract.HitTag
            let mutable changed = false

            let emitAfter (at: Instruction) (xs: Instruction list) =
                let mutable cur = at

                for x in xs do
                    il.InsertAfter(cur, x)
                    cur <- x

                changed <- true

            let ldc (id: int) = il.Create(OpCodes.Ldc_I4, id)
            let call (mr: MethodReference) = il.Create(OpCodes.Call, mr)

            for ins in body.Instructions |> Seq.toList do
                let op = ins.OpCode

                if (op = OpCodes.Isinst || op = OpCodes.Castclass || op = OpCodes.Unbox_Any || op = OpCodes.Unbox) && (ins.Operand :? TypeReference) then
                    let d = def (ins.Operand :?> TypeReference)

                    if not (isNull d) && not (within meth d) then
                        match caseIdOf d |> Option.orElse (typeIdOf d) with
                        | Some id when op = OpCodes.Isinst -> emitAfter ins [ il.Create OpCodes.Dup; ldc id; call hitNN ]
                        | Some id -> emitAfter ins [ ldc id; call hit ]
                        | None -> ()
                elif (op = OpCodes.Call || op = OpCodes.Callvirt) && (ins.Operand :? GenericInstanceMethod) then
                    let gim = ins.Operand :?> GenericInstanceMethod

                    if gim.DeclaringType.FullName = "Microsoft.FSharp.Core.LanguagePrimitives/IntrinsicFunctions"
                       && (gim.Name = "UnboxGeneric" || gim.Name = "UnboxFast" || gim.Name = "TypeTestGeneric" || gim.Name = "TypeTestFast") then
                        let d = def gim.GenericArguments.[0]

                        match (if isNull d then None else caseIdOf d |> Option.orElse (typeIdOf d)) with
                        | Some id when gim.Name.StartsWith "TypeTest" -> emitAfter ins [ il.Create OpCodes.Dup; ldc id; call hitTrue ]
                        | Some id -> emitAfter ins [ ldc id; call hit ]
                        | None -> ()
                elif (op = OpCodes.Ldfld || op = OpCodes.Ldflda) && (ins.Operand :? FieldReference) then
                    let fr = ins.Operand :?> FieldReference
                    let d = def fr.DeclaringType

                    if not (isNull d) then
                        let resolved = (try fr.Resolve() with _ -> null)
                        let fullName = if isNull resolved then fr.FullName else resolved.FullName

                        if cat.TagFields.Contains fullName && cat.UnionBase.ContainsKey d.FullName then
                            if op = OpCodes.Ldfld && not (isTagGetter meth && meth.DeclaringType.FullName = d.FullName) then
                                emitAfter ins [ il.Create OpCodes.Dup; ldc cat.UnionBase.[d.FullName]; call hitTag ]
                        elif not (within meth d) then
                            match caseIdOf d |> Option.orElse (typeIdOf d) with
                            | Some id -> emitAfter ins [ ldc id; call hit ]
                            | None -> ()
                elif (op = OpCodes.Ldsfld || op = OpCodes.Ldsflda) && (ins.Operand :? FieldReference) then
                    let sf = ins.Operand :?> FieldReference
                    let d = def sf.DeclaringType

                    if not (isNull d) && sf.Name.StartsWith "_unique_" && cat.UnionBase.ContainsKey d.FullName && not (within meth d) then
                        match cat.UnionCases.[d.FullName].TryGetValue(sf.Name.Substring 8) with
                        | true, i when i >= 0 -> emitAfter ins [ ldc (cat.UnionBase.[d.FullName] + i); call hit ]
                        | _ -> ()

            // Callee-side tag probe: every return of a product union's tag getter records
            // the value's case. Covers callers in assemblies that are not woven at all.
            if mode = Full && isTagGetter meth then
                match cat.UnionBase.TryGetValue meth.DeclaringType.FullName with
                | true, b ->
                    for ret in body.Instructions |> Seq.filter (fun i -> i.OpCode = OpCodes.Ret) |> Seq.toList do
                        // Mutate the ret INTO the dup so branch targets that pointed at it stay valid.
                        ret.OpCode <- OpCodes.Dup
                        ret.Operand <- null
                        emitAfter ret [ ldc b; call hitTag; il.Create OpCodes.Ret ]
                | _ -> ()

            // Static-init scope: wrap a type initializer in try/finally Enter/ExitStatic, so
            // what runs once per process is never attributed to whichever test got there first.
            if mode = Full && meth.IsConstructor && meth.IsStatic
               && body.ExceptionHandlers |> Seq.forall (fun h -> not (isNull h.HandlerEnd) && not (isNull h.TryEnd)) then
                let rets = body.Instructions |> Seq.filter (fun i -> i.OpCode = OpCodes.Ret) |> Seq.toList
                let firstBody = body.Instructions.[0]
                let callExit = call (r Contract.ExitStatic)
                let endFinally = il.Create OpCodes.Endfinally
                let endRet = il.Create OpCodes.Ret
                il.Append callExit
                il.Append endFinally
                il.Append endRet

                for ret in rets do
                    ret.OpCode <- OpCodes.Leave
                    ret.Operand <- endRet

                il.InsertBefore(firstBody, call (r Contract.EnterStatic))

                body.ExceptionHandlers.Add(
                    ExceptionHandler(ExceptionHandlerType.Finally, TryStart = firstBody, TryEnd = callExit, HandlerStart = callExit, HandlerEnd = endRet)
                )

                changed <- true

            changed

let pass () : IWeavePass = Pass() :> IWeavePass
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.SiteProbeScenarioTests`
Expected: PASS, 21 test cases (20 scenario rows + the static-init test).

If a scenario fails, dump the method's IL with Cecil (`for i in meth.Body.Instructions do printfn "%O" i`) and
compare it with the spike's IL notes in "Background". Do not loosen the table: each row is a measured property
of the spike weaver.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t5-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj commit -m "Weaver: union-case, type-use and static-init site probes

Records which union cases and product types code touched: the callee-side tag
getter return, case-class isinst (on success only), castclass, case-field and
tag-field loads, nullary-case singletons, type tests and the FSharp.Core
unbox/type-test intrinsics. Type initializers run inside a try/finally that
routes their hits to the static-init scope. The tag getter and tag field are
identified structurally, which is what keeps a case named Tag and a case field
named tag from producing invalid IL.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Input capture: file reads and child processes

**Files:**

- Create: `src/TestPrune.Trace.Recorder/Io.fs`, `ProcessShims.fs` (compile after `RecorderState.fs`, before
  `DumpWriter.fs`)
- Modify: `src/TestPrune.Trace.Recorder/RecorderState.fs`: add `RepoRoot` (settable) and `NoteScope()`
- Modify: `src/TestPrune.Trace.Recorder/Probes.fs`: set `RepoRoot` from `TESTPRUNE_TRACE_REPO_ROOT` in
  `Runtime.state`
- Create: `src/TestPrune.Trace/Redirects.fs` (compile after `SiteProbes.fs`)
- Test: `tests/TestPrune.Trace.Tests/InputCaptureTests.fs`

**Interfaces:**

- Consumes: `RecorderState`, `Scope.Inputs`, `Scope.Children` (Task 2); `Weaver.IWeavePass` (Task 4).
- Produces (recorder):
  - `RecorderState.RepoRoot : string` (settable).
  - `RecorderState.NoteScope() : Scope`: the static-init scope while static depth > 0, else `CurrentScope()`,
    else the ambient scope. Never null.
  - `Io` (public static class): one shim per `Redirects.table` row, named in that table.
  - `Io.NoteWith(state: RecorderState, kind: string, path: string) : unit` (internal).
  - `ProcessShims` (public static class): shims for `Process.Start`.
  - `ProcessShims.PrepareWith(state, psi) : bool` and `ProcessShims.RecordWith(state, proc, injected) : unit`
    (internal).
- Produces (tooling): `Redirects.table : Redirect list` and `Redirects.pass : unit -> Weaver.IWeavePass`.

```fsharp
type Redirect =
    { DeclaringType: string     // CLR full name of the BCL type
      Name: string              // method name, ".ctor" for constructors
      Params: string list       // parameter type full names
      IsInstance: bool          // instance method: the shim takes `this` first
      ShimType: string          // Contract.IoType or Contract.ProcessShimsType
      Shim: string }            // shim method name on ShimType
```

- [ ] **Step 1: Write the failing tests**

`tests/TestPrune.Trace.Tests/InputCaptureTests.fs`:

```fsharp
module TestPrune.Trace.Tests.InputCaptureTests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Recorder
open TestPrune.Trace.Tests

let private shimType name = typeof<Probes>.Assembly.GetType(name, true)

[<Fact>]
let ``every redirect names a shim with the original's stack shape`` () =
    for r in Redirects.table do
        let bcl = Type.GetType(r.DeclaringType + ", System.Private.CoreLib", false) |> Option.ofObj |> Option.orElse (Type.GetType(r.DeclaringType + ", System.Diagnostics.Process", false) |> Option.ofObj) |> Option.get
        // Cecil spells a generic instance `IEnumerable`1<System.String>`; reflection wants `[...]`.
        let ps = r.Params |> List.map (fun p -> Type.GetType(p.Replace('<', '[').Replace('>', ']'), true)) |> Array.ofList
        let shimParams = if r.IsInstance then Array.append [| bcl |] ps else ps
        let shim = (shimType r.ShimType).GetMethod(r.Shim, BindingFlags.Public ||| BindingFlags.Static, null, shimParams, null)
        test <@ not (isNull shim) @>

        let expectedReturn =
            if r.Name = ".ctor" then bcl
            else (bcl.GetMethod(r.Name, (if r.IsInstance then BindingFlags.Instance else BindingFlags.Static) ||| BindingFlags.Public, null, ps, null)).ReturnType

        test <@ shim.ReturnType = expectedReturn @>

[<Fact>]
let ``reads under the repository are noted on the current scope; reads outside are not`` () =
    let state = RecorderState(8, None, null, RepoRoot = Fixtures.repoRoot)
    state.EnterScope "T:t"
    Io.NoteWith(state, "read", Path.Combine(Fixtures.repoRoot, "global.json"))
    Io.NoteWith(state, "read", "/etc/hosts")
    let inputs = state.CurrentScope().Inputs.Keys |> Seq.map (fun (struct (k, p)) -> k, p) |> Set.ofSeq
    test <@ inputs = set [ "read", Path.Combine(Fixtures.repoRoot, "global.json") ] @>

[<Fact>]
let ``a started child inherits the parent scope and is noted with its pid`` () =
    let state = RecorderState(8, None, null, RepoRoot = Fixtures.repoRoot)
    state.EnterScope "T:parent"
    let psi = ProcessStartInfo("/bin/echo", "hi", UseShellExecute = false, RedirectStandardOutput = true)
    let injected = ProcessShims.PrepareWith(state, psi)
    test <@ injected && psi.Environment.[Contract.ParentScopeEnv] = "T:parent" @>
    use p = Process.Start psi
    ProcessShims.RecordWith(state, p, injected)
    p.WaitForExit()
    let struct (pid, file, env) = state.CurrentScope().Children |> Seq.exactlyOne
    test <@ pid = p.Id && file = "echo" && env @>

[<Fact>]
let ``a shell-executed child cannot inherit and is noted as such`` () =
    let state = RecorderState(8, None, null, RepoRoot = Fixtures.repoRoot)
    state.EnterScope "T:parent"
    test <@ not (ProcessShims.PrepareWith(state, ProcessStartInfo("/bin/echo", UseShellExecute = true))) @>

[<Fact>]
let ``the woven driver's file read lands in its scope`` () =
    let dir, r = WeaverTests.weaveFx [ SiteProbes.pass (); Redirects.pass () ]
    let out = Path.Combine(dir, "traces")

    let code, output =
        Launch.run (Path.Combine(dir, "FxDriver")) []
            [ Contract.OutEnv, out; Contract.IdsEnv, string r.Manifest.IdCount; Contract.RepoRootEnv, Fixtures.repoRoot ]
            dir (TimeSpan.FromMinutes 1.0)

    test <@ code = 0 @>
    let dumps, _ = DumpReader.readDirectory out
    let scope = dumps |> List.collect (fun d -> d.Scopes) |> List.find (fun s -> s.Key = "T:fileRead")
    test <@ scope.Inputs |> List.exists (fun i -> i.Kind = Model.FileRead && i.Path = Path.Combine(Fixtures.repoRoot, "global.json")) @>
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build`
Expected: FAIL, with `Io`, `ProcessShims` and `Redirects` undefined, and `RecorderState` having no `RepoRoot`.

- [ ] **Step 3: Implement**

In `RecorderState.fs`, add these members to `RecorderState`, using the existing `staticInit`/`ambient` lets:

```fsharp
    /// Absolute repository root; inputs outside it are not recorded.
    member val RepoRoot: string = null with get, set

    /// Where a file read or a child process is noted: static init, then the current
    /// scope, then ambient. Never null.
    member this.NoteScope() : Scope =
        if Threads.Get().StaticDepth > 0 then staticInit
        else match this.CurrentScope() with
             | null -> ambient
             | s -> s
```

In `Probes.fs` `Runtime.state`, after constructing `s`, add
`s.RepoRoot <- Environment.GetEnvironmentVariable Contract.RepoRootEnv`.

`src/TestPrune.Trace.Recorder/Io.fs`:

```fsharp
namespace TestPrune.Trace.Recorder

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

/// Call-site replacements for the BCL's file readers. Each shim notes (kind, absolute
/// path) on the scope that is reading, then does exactly what the original did. The
/// weaver rewrites `call File::ReadAllText(string)` into `call Io::File_ReadAllText(string)`
/// (and `newobj FileStream(string, FileMode)` into `call Io::FileStream_ctor(...)`), so
/// the stack shape is unchanged.
[<AbstractClass; Sealed>]
type Io =
    static member internal NoteWith(state: RecorderState, kind: string, path: string) =
        if not (isNull state) && not (isNull path) && not (isNull state.RepoRoot) then
            let full = try Path.GetFullPath path with _ -> null

            if not (isNull full) && full.StartsWith(state.RepoRoot, StringComparison.Ordinal) then
                state.NoteScope().Inputs.TryAdd(struct (kind, full), 0uy) |> ignore

    static member private Note(kind: string, path: string) = Io.NoteWith(Runtime.state, kind, path)

    static member File_Exists(path: string) : bool = Io.Note("exists", path); File.Exists path
    static member File_ReadAllText(path: string) : string = Io.Note("read", path); File.ReadAllText path
    static member File_ReadAllText(path: string, encoding: Encoding) : string = Io.Note("read", path); File.ReadAllText(path, encoding)
    static member File_ReadAllLines(path: string) : string[] = Io.Note("read", path); File.ReadAllLines path
    static member File_ReadAllBytes(path: string) : byte[] = Io.Note("read", path); File.ReadAllBytes path
    static member File_ReadLines(path: string) : IEnumerable<string> = Io.Note("read", path); File.ReadLines path
    static member File_OpenRead(path: string) : FileStream = Io.Note("read", path); File.OpenRead path
    static member File_OpenText(path: string) : StreamReader = Io.Note("read", path); File.OpenText path
    static member File_Open(path: string, mode: FileMode) : FileStream = Io.Note("read", path); File.Open(path, mode)
    static member File_Open(path: string, mode: FileMode, access: FileAccess) : FileStream = Io.Note("read", path); File.Open(path, mode, access)
    static member File_Open(path: string, mode: FileMode, access: FileAccess, share: FileShare) : FileStream = Io.Note("read", path); File.Open(path, mode, access, share)
    static member File_ReadAllTextAsync(path: string, ct: CancellationToken) : Task<string> = Io.Note("read", path); File.ReadAllTextAsync(path, ct)
    static member File_ReadAllLinesAsync(path: string, ct: CancellationToken) : Task<string[]> = Io.Note("read", path); File.ReadAllLinesAsync(path, ct)
    static member File_ReadAllBytesAsync(path: string, ct: CancellationToken) : Task<byte[]> = Io.Note("read", path); File.ReadAllBytesAsync(path, ct)
    static member Directory_Exists(path: string) : bool = Io.Note("exists", path); Directory.Exists path
    static member Directory_GetFiles(path: string) : string[] = Io.Note("list", path); Directory.GetFiles path
    static member Directory_GetFiles(path: string, pattern: string) : string[] = Io.Note("list", path); Directory.GetFiles(path, pattern)
    static member Directory_GetFiles(path: string, pattern: string, option: SearchOption) : string[] = Io.Note("list", path); Directory.GetFiles(path, pattern, option)
    static member Directory_EnumerateFiles(path: string) : IEnumerable<string> = Io.Note("list", path); Directory.EnumerateFiles path
    static member Directory_EnumerateFiles(path: string, pattern: string) : IEnumerable<string> = Io.Note("list", path); Directory.EnumerateFiles(path, pattern)
    static member Directory_EnumerateFiles(path: string, pattern: string, option: SearchOption) : IEnumerable<string> = Io.Note("list", path); Directory.EnumerateFiles(path, pattern, option)
    static member Directory_GetDirectories(path: string) : string[] = Io.Note("list", path); Directory.GetDirectories path
    static member Directory_GetDirectories(path: string, pattern: string) : string[] = Io.Note("list", path); Directory.GetDirectories(path, pattern)
    static member Directory_GetDirectories(path: string, pattern: string, option: SearchOption) : string[] = Io.Note("list", path); Directory.GetDirectories(path, pattern, option)
    static member Directory_EnumerateDirectories(path: string) : IEnumerable<string> = Io.Note("list", path); Directory.EnumerateDirectories path
    static member Directory_EnumerateDirectories(path: string, pattern: string) : IEnumerable<string> = Io.Note("list", path); Directory.EnumerateDirectories(path, pattern)
    static member Directory_EnumerateDirectories(path: string, pattern: string, option: SearchOption) : IEnumerable<string> = Io.Note("list", path); Directory.EnumerateDirectories(path, pattern, option)
    static member FileStream_ctor(path: string, mode: FileMode) : FileStream = Io.Note("read", path); new FileStream(path, mode)
    static member FileStream_ctor(path: string, mode: FileMode, access: FileAccess) : FileStream = Io.Note("read", path); new FileStream(path, mode, access)
    static member FileStream_ctor(path: string, mode: FileMode, access: FileAccess, share: FileShare) : FileStream = Io.Note("read", path); new FileStream(path, mode, access, share)
    static member StreamReader_ctor(path: string) : StreamReader = Io.Note("read", path); new StreamReader(path)
    static member FileInfo_get_Exists(fi: FileInfo) : bool = Io.Note("exists", fi.FullName); fi.Exists
    static member FileInfo_OpenRead(fi: FileInfo) : FileStream = Io.Note("read", fi.FullName); fi.OpenRead()
    static member FileInfo_OpenText(fi: FileInfo) : StreamReader = Io.Note("read", fi.FullName); fi.OpenText()
    static member DirectoryInfo_get_Exists(di: DirectoryInfo) : bool = Io.Note("exists", di.FullName); di.Exists
```

A `FileStream` opened for writing is also noted as `read`. That over-approximates: a test that writes a repo file
is then reselected when the file changes, which is sound.

`src/TestPrune.Trace.Recorder/ProcessShims.fs`:

```fsharp
namespace TestPrune.Trace.Recorder

open System.Collections.Generic
open System.Diagnostics
open System.IO

/// Call-site replacements for Process.Start: the child inherits the parent's scope
/// (TESTPRUNE_TRACE_PARENT_SCOPE), so a woven child records into the test that started
/// it, and the start is noted on that scope. A child with no dump of its own makes the
/// test's trace incomplete (ingestion decides; this only notes).
[<AbstractClass; Sealed>]
type ProcessShims =
    static member internal PrepareWith(state: RecorderState, psi: ProcessStartInfo) : bool =
        if isNull state || psi.UseShellExecute then
            false
        else
            psi.Environment.[Contract.ParentScopeEnv] <- state.NoteScope().Key
            true

    static member internal RecordWith(state: RecorderState, p: Process, injected: bool) =
        if not (isNull state) && not (isNull p) then
            state.NoteScope().Children.Enqueue(struct (p.Id, Path.GetFileName p.StartInfo.FileName, injected))

    static member Process_Start(psi: ProcessStartInfo) : Process =
        let s = Runtime.state
        let injected = ProcessShims.PrepareWith(s, psi)
        let p = Process.Start psi
        ProcessShims.RecordWith(s, p, injected)
        p

    static member Process_Start(fileName: string) : Process =
        ProcessShims.Process_Start(ProcessStartInfo fileName)

    static member Process_Start(fileName: string, arguments: string) : Process =
        ProcessShims.Process_Start(ProcessStartInfo(fileName, arguments))

    static member Process_Start(fileName: string, arguments: IEnumerable<string>) : Process =
        ProcessShims.Process_Start(ProcessStartInfo(fileName, arguments))

    static member Process_Start(p: Process) : bool =
        let s = Runtime.state
        let injected = ProcessShims.PrepareWith(s, p.StartInfo)
        let started = p.Start()
        if started then ProcessShims.RecordWith(s, p, injected)
        started
```

In the tests, `RecorderState(8, None, null, RepoRoot = …)` uses F#'s property-setter-at-construction syntax on
the settable `RepoRoot`.

`src/TestPrune.Trace/Redirects.fs`:

```fsharp
module TestPrune.Trace.Redirects

open System.Collections.Generic
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder
open TestPrune.Trace.Weaver

type Redirect =
    { DeclaringType: string
      Name: string
      Params: string list
      IsInstance: bool
      ShimType: string
      Shim: string }

let private io t name ps shim = { DeclaringType = t; Name = name; Params = ps; IsInstance = false; ShimType = Contract.IoType; Shim = shim }
let private ioI t name ps shim = { io t name ps shim with IsInstance = true }
let private S = "System.String"
let private Mode, Access, Share = "System.IO.FileMode", "System.IO.FileAccess", "System.IO.FileShare"
let private Opt, Ct, Enc = "System.IO.SearchOption", "System.Threading.CancellationToken", "System.Text.Encoding"
let private F, D = "System.IO.File", "System.IO.Directory"

let table: Redirect list =
    [ io F "Exists" [ S ] "File_Exists"
      io F "ReadAllText" [ S ] "File_ReadAllText"
      io F "ReadAllText" [ S; Enc ] "File_ReadAllText"
      io F "ReadAllLines" [ S ] "File_ReadAllLines"
      io F "ReadAllBytes" [ S ] "File_ReadAllBytes"
      io F "ReadLines" [ S ] "File_ReadLines"
      io F "OpenRead" [ S ] "File_OpenRead"
      io F "OpenText" [ S ] "File_OpenText"
      io F "Open" [ S; Mode ] "File_Open"
      io F "Open" [ S; Mode; Access ] "File_Open"
      io F "Open" [ S; Mode; Access; Share ] "File_Open"
      io F "ReadAllTextAsync" [ S; Ct ] "File_ReadAllTextAsync"
      io F "ReadAllLinesAsync" [ S; Ct ] "File_ReadAllLinesAsync"
      io F "ReadAllBytesAsync" [ S; Ct ] "File_ReadAllBytesAsync"
      io D "Exists" [ S ] "Directory_Exists"
      for name in [ "GetFiles"; "EnumerateFiles"; "GetDirectories"; "EnumerateDirectories" ] do
          io D name [ S ] ("Directory_" + name)
          io D name [ S; S ] ("Directory_" + name)
          io D name [ S; S; Opt ] ("Directory_" + name)
      io "System.IO.FileStream" ".ctor" [ S; Mode ] "FileStream_ctor"
      io "System.IO.FileStream" ".ctor" [ S; Mode; Access ] "FileStream_ctor"
      io "System.IO.FileStream" ".ctor" [ S; Mode; Access; Share ] "FileStream_ctor"
      io "System.IO.StreamReader" ".ctor" [ S ] "StreamReader_ctor"
      ioI "System.IO.FileInfo" "get_Exists" [] "FileInfo_get_Exists"
      ioI "System.IO.FileInfo" "OpenRead" [] "FileInfo_OpenRead"
      ioI "System.IO.FileInfo" "OpenText" [] "FileInfo_OpenText"
      ioI "System.IO.DirectoryInfo" "get_Exists" [] "DirectoryInfo_get_Exists"
      let pr = "System.Diagnostics.Process"
      { io pr "Start" [ "System.Diagnostics.ProcessStartInfo" ] "Process_Start" with ShimType = Contract.ProcessShimsType }
      { io pr "Start" [ S ] "Process_Start" with ShimType = Contract.ProcessShimsType }
      { io pr "Start" [ S; S ] "Process_Start" with ShimType = Contract.ProcessShimsType }
      { io pr "Start" [ S; "System.Collections.Generic.IEnumerable`1<System.String>" ] "Process_Start" with ShimType = Contract.ProcessShimsType }
      { ioI pr "Start" [] "Process_Start" with ShimType = Contract.ProcessShimsType } ]

let private key (t: string) (n: string) (ps: string seq) = t + "::" + n + "(" + String.concat "," ps + ")"

type private Pass() =
    let byKey = Dictionary<string, Redirect>()

    interface IWeavePass with
        member _.Prepare(_) =
            for r in table do
                byKey.[key r.DeclaringType r.Name r.Params] <- r

        member _.Rewrite(set, m, _, meth) =
            let mutable changed = false

            for ins in meth.Body.Instructions do
                if (ins.OpCode = OpCodes.Call || ins.OpCode = OpCodes.Callvirt || ins.OpCode = OpCodes.Newobj) && (ins.Operand :? MethodReference) then
                    let mr = ins.Operand :?> MethodReference

                    match byKey.TryGetValue(key mr.DeclaringType.FullName mr.Name (mr.Parameters |> Seq.map (fun p -> p.ParameterType.FullName))) with
                    | true, r ->
                        let shimParams = (if r.IsInstance then [ r.DeclaringType ] else []) @ r.Params
                        let recorder = (set.RecorderMethod m r.ShimType r.Shim).Resolve().DeclaringType

                        let shim =
                            recorder.Methods
                            |> Seq.find (fun x -> x.Name = r.Shim && (x.Parameters |> Seq.map (fun p -> p.ParameterType.FullName) |> List.ofSeq) = shimParams)

                        ins.OpCode <- OpCodes.Call
                        ins.Operand <- m.ImportReference shim
                        changed <- true
                    | _ -> ()

            changed

let pass () : IWeavePass = Pass() :> IWeavePass
```

`WeaveSet.RecorderMethod` (Task 4) finds a method by name only. `Redirects` therefore resolves its declaring
type and picks the overload by its parameter list. `table` mixes single rows with a `for` loop, which F#'s
implicit-yield list expressions allow.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.InputCaptureTests`
Expected: PASS, 5 tests.

Also re-run `SiteProbeScenarioTests` and `WeaverTests`: the redirect pass must not change their results.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t6-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj commit -m "Record repository file reads and child processes per test

Call sites of the BCL file readers, directory listings and Process.Start are
redirected to recorder shims with the same stack shape. A read under the
repository root is noted on the reading scope; a started child inherits the
parent's scope through the environment and is noted with its pid, so
ingestion can tell a child that recorded from one that did not.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: The joiner: manifest row → symbol, file-level entry, or dropped

**Files:**

- Create: `src/TestPrune.Trace/Joiner.fs` (compile after `Redirects.fs`)
- Modify: `src/TestPrune.Core/AstAnalyzer.fs`: make `canonicalShortName` **public** (it is `internal`), so the
  joiner shares Core's one definition of "a symbol's short name" rather than a copy. This is an additive Core
  API change; add a Core CHANGELOG line.
- Modify: `tests/TestPrune.Trace.Tests/TestPrune.Trace.Tests.fsproj`: add
  `<ProjectReference Include="../../src/TestPrune/TestPrune.fsproj" />` (for the indexer in the integration test)
- Test: `tests/TestPrune.Trace.Tests/JoinerTests.fs`, `tests/TestPrune.Trace.Tests/FixtureIndex.fs`

**Interfaces:**

- Consumes: `Model.ManifestRow`, `Model.Manifest` (Task 1); `TestPrune.Ports.SymbolStore` (Core);
  `AstAnalyzer.canonicalShortName`.
- Produces:

```fsharp
module TestPrune.Trace.Joiner

type SymbolIndex =
    { InFile: string -> TestPrune.AstAnalyzer.SymbolInfo list   // repo-relative path
      Exists: string -> bool                                    // a symbol full name is indexed
      IsIndexedFile: string -> bool
      RepoRoot: string }

type JoinTarget =
    | ToSymbol of fullName: string
    | ToFile of repoRelativePath: string   // sound fallback: a file-level entry
    | Dropped                              // compiler-generated; the probes inside attribute elsewhere
    | Unmapped of reason: string

val ofStore: TestPrune.Ports.SymbolStore -> repoRoot: string -> SymbolIndex
val typeCandidates: clrName: string -> string list
val joinRow: SymbolIndex -> unionTypes: Set<string> -> Model.ManifestRow -> JoinTarget
val joinManifest: SymbolIndex -> Model.Manifest -> JoinTarget[]   // indexed by probe id
```

**Rules, in order** (the spike's measured rules; the "Background" section explains each):

1. `UnionCase` → DuCase `U.X`, trying `typeCandidates U`.
2. `TypeUse` → Type, trying `typeCandidates T`.
3. A method on a union type `U`:
   - `get_Tag`, `Equals`, `CompareTo`, `GetHashCode`, `ToString`, `.ctor` and `.cctor` → `Dropped`. The site
     probes inside them already record the compared values' cases.
   - `NewX`, `get_IsX` and a static `get_X` whose case exists → case `X`.
4. A method on a case class `U+X`, `U+_X` or `U+X@DebugTypeProxy` → case `X`.
5. A closure-singleton `.ctor`/`.cctor` (the type's last segment contains `@`) → `Dropped`.
6. With a document under the repository root:
   - if the file is not indexed → `ToFile`;
   - otherwise, **same-file name match first**: symbols whose `canonicalShortName` equals the member name,
     nearest preceding the method's first line. The member name has `get_`/`set_` stripped; a closure uses its
     `f@12` prefix; a constructor uses its type's name;
   - then the **line rule**: the nearest symbol declared at or before the first line;
   - else `ToFile`.
7. Without a document (generated members have no sequence points): owner member, then the owner type, then
   each enclosing module in turn. Else `Unmapped "no-document"`.

`typeCandidates` returns, in order: the dotted name (`+` → `.`), the same with generic arity (`` `1 ``)
removed, and the same with a `Module` suffix removed from each segment (F# `ModuleSuffix`). Every candidate is
checked with `Exists` before use, so a wrong candidate can never match.

- [ ] **Step 1: Write the failing tests**

`tests/TestPrune.Trace.Tests/JoinerTests.fs`:

```fsharp
module TestPrune.Trace.Tests.JoinerTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Joiner

let private sym name kind file line =
    { FullName = name; Kind = kind; SourceFile = file; LineStart = line; LineEnd = line; ContentHash = "h"; IsExtern = false }

let private index (syms: SymbolInfo list) =
    { InFile = fun f -> syms |> List.filter (fun s -> s.SourceFile = f)
      Exists = fun n -> syms |> List.exists (fun s -> s.FullName = n)
      IsIndexedFile = fun f -> syms |> List.exists (fun s -> s.SourceFile = f)
      RepoRoot = "/r" }

let private row kind typ mem doc first =
    { Id = 0; Kind = kind; Assembly = "A"; TypeName = typ; Member = mem; Document = doc; FirstLine = first; LastLine = first }

let private ix =
    index
        [ sym "N.M" Module "src/M.fs" 1
          sym "N.M.area" Function "src/M.fs" 5
          sym "N.M.fieldName" Value "src/M.fs" 10
          sym "N.M.helpText" Value "src/M.fs" 12
          sym "N.Shape" Type "src/T.fs" 3
          sym "N.Shape.Circle" DuCase "src/T.fs" 4
          sym "N.Shape.Square" DuCase "src/T.fs" 5
          sym "N.Dog" Type "src/T.fs" 9 ]

let private unions = set [ "N.Shape" ]

[<Fact>]
let ``case and type rows join by name`` () =
    test <@ joinRow ix unions (row UnionCase "N.Shape" "Circle" None 0) = ToSymbol "N.Shape.Circle" @>
    test <@ joinRow ix unions (row TypeUse "N.Dog" "" None 0) = ToSymbol "N.Dog" @>

[<Fact>]
let ``union-generated members map to cases or are dropped`` () =
    test <@ joinRow ix unions (row GeneratedMethod "N.Shape" "get_Tag" None 0) = Dropped @>
    test <@ joinRow ix unions (row GeneratedMethod "N.Shape" "NewCircle" None 0) = ToSymbol "N.Shape.Circle" @>
    test <@ joinRow ix unions (row GeneratedMethod "N.Shape" "get_IsSquare" None 0) = ToSymbol "N.Shape.Square" @>
    test <@ joinRow ix unions (row GeneratedMethod "N.Shape+Circle" "get_radius" None 0) = ToSymbol "N.Shape.Circle" @>
    test <@ joinRow ix unions (row GeneratedMethod "N.Shape+_Square" ".ctor" None 0) = ToSymbol "N.Shape.Square" @>
    test <@ joinRow ix unions (row GeneratedMethod "N.Shape+Circle@DebugTypeProxy" "get_radius" None 0) = ToSymbol "N.Shape.Circle" @>

[<Fact>]
let ``a closure joins to the binding it was written in`` () =
    test <@ joinRow ix unions (row GeneratedMethod "N.M+area@6" "Invoke" (Some "/r/src/M.fs") 6) = ToSymbol "N.M.area" @>
    test <@ joinRow ix unions (row GeneratedMethod "N.M+area@6" ".ctor" (Some "/r/src/M.fs") 0) = Dropped @>

[<Fact>]
let ``same-file name match wins over the line rule when the index and binary drift`` () =
    // The getter's first line (13) is past `helpText` (12): the line rule alone
    // would pick the neighbour. The name picks the right one.
    test <@ joinRow ix unions (row UserMethod "N.M" "get_fieldName" (Some "/r/src/M.fs") 13) = ToSymbol "N.M.fieldName" @>

[<Fact>]
let ``the line rule, then the file, are the fallbacks`` () =
    test <@ joinRow ix unions (row UserMethod "N.M" "helper" (Some "/r/src/M.fs") 7) = ToSymbol "N.M.area" @>
    test <@ joinRow ix unions (row UserMethod "N.Other" "f" (Some "/r/src/Unindexed.fs") 3) = ToFile "src/Unindexed.fs" @>

[<Fact>]
let ``without a document: member, then type, then enclosing module`` () =
    test <@ joinRow ix unions (row GeneratedMethod "N.Dog" "Equals" None 0) = ToSymbol "N.Dog" @>
    test <@ joinRow ix unions (row GeneratedMethod "N.M+Inner" "get_x" None 0) = ToSymbol "N.M" @>
    test <@ joinRow ix unions (row GeneratedMethod "Q.Nowhere" "f" None 0) = Unmapped "no-document" @>

[<Fact>]
let ``type candidates cover arity and module suffix, most specific first`` () =
    test <@ typeCandidates "N.M+AnswerState`1" = [ "N.M.AnswerState`1"; "N.M.AnswerState" ] @>
    test <@ typeCandidates "N.ShapeModule" = [ "N.ShapeModule"; "N.Shape" ] @>
```

`tests/TestPrune.Trace.Tests/FixtureIndex.fs` indexes the fixture mini-repository with TestPrune's real
indexer:

```fsharp
module TestPrune.Trace.Tests.FixtureIndex

open System
open System.IO
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Orchestration
open TestPrune.ProjectLoader

/// Project options whose sources are the project's own compile list (no MSBuild).
let private compileListOptions: ProjectOptionsProvider =
    fun checker fsprojPath ->
        let compileFiles, _ = parseProjectFile fsprojPath
        let first = List.head compileFiles
        let options = getScriptOptions checker first (File.ReadAllText first) |> Async.RunSynchronously
        { options with SourceFiles = List.toArray compileFiles }

/// Index a scratch COPY of tests/TraceFixtures (sources only), so the real tree gets no
/// .test-prune.db. The copy keeps the same relative layout, so a PDB document path made
/// relative to the REAL fixture root matches the index's paths.
let build =
    lazy
        (let scratch = Path.Combine(Path.GetTempPath(), "tp-fixture-index-" + Guid.NewGuid().ToString "N")

         for f in Directory.EnumerateFiles(Fixtures.fixtureRoot, "*.*", SearchOption.AllDirectories) do
             let rel = Path.GetRelativePath(Fixtures.fixtureRoot, f)

             if (rel.EndsWith ".fs" || rel.EndsWith ".fsproj") && not (rel.Contains "bin") && not (rel.Contains "obj") then
                 let dst = Path.Combine(scratch, rel)
                 Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
                 File.Copy(f, dst)

         let code = runIndexWith (fun _ -> 0) compileListOptions scratch (createChecker ()) 1 (TestPrune.AuditSink.createNoopSink ())
         if code <> 0 then failwith $"indexing the fixtures failed: %d{code}"
         Database.create (Path.Combine(scratch, ".test-prune.db")))
```

Append this integration test to `JoinerTests.fs`:

```fsharp
[<Fact>]
let ``the woven fixture joins to the real index`` () =
    let _, r = WeaverTests.weaveFx [ SiteProbes.pass (); Redirects.pass () ]
    let ix = ofStore (TestPrune.Ports.toSymbolStore FixtureIndex.build.Value) Fixtures.fixtureRoot
    let targets = joinManifest ix r.Manifest
    let find typ mem = targets.[(r.Manifest.Rows |> Array.find (fun x -> x.TypeName = typ && x.Member = mem)).Id]
    test <@ find "FxLib.Logic" "area" = ToSymbol "FxLib.Logic.area" @>
    test <@ find "FxLib.Shape" "Circle" = ToSymbol "FxLib.Shape.Circle" @>
    test <@ find "FxLib.Values" "get_threshold" = ToSymbol "FxLib.Values.threshold" @>
    test <@ find "FxLib.Dog" "" = ToSymbol "FxLib.Dog" @>
    test <@ find "FxLib.Shape" "get_Tag" = Dropped @>

    let fxLib = r.Manifest.Rows |> Array.filter (fun x -> x.Assembly = "FxLib")
    let unmapped = fxLib |> Array.filter (fun x -> match targets.[x.Id] with Unmapped _ -> true | _ -> false)
    // The spike's hit-level rate was 98.1 % on a large suite; the fixture must do no worse.
    test <@ float unmapped.Length / float fxLib.Length <= 0.02 @>
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build`
Expected: FAIL, with `Joiner` undefined.

- [ ] **Step 3: Implement**

In `src/TestPrune.Core/AstAnalyzer.fs`, change `let internal canonicalShortName` to `let canonicalShortName`
and keep its doc comment.

`src/TestPrune.Trace/Joiner.fs`:

```fsharp
module TestPrune.Trace.Joiner

open System
open System.IO
open System.Text.RegularExpressions
open TestPrune.AstAnalyzer
open TestPrune.Trace.Model

type SymbolIndex =
    { InFile: string -> SymbolInfo list
      Exists: string -> bool
      IsIndexedFile: string -> bool
      RepoRoot: string }

type JoinTarget =
    | ToSymbol of fullName: string
    | ToFile of repoRelativePath: string
    | Dropped
    | Unmapped of reason: string

let ofStore (store: TestPrune.Ports.SymbolStore) (repoRoot: string) : SymbolIndex =
    let names = store.GetAllSymbolNames()

    { InFile = store.GetSymbolsInFile
      Exists = names.Contains
      IsIndexedFile = fun f -> (store.GetFileKey f).IsSome
      RepoRoot = repoRoot }

let private arity = Regex(@"`\d+", RegexOptions.Compiled)

let typeCandidates (clr: string) : string list =
    let dotted = clr.Replace('+', '.')
    let noArity = arity.Replace(dotted, "")

    let noSuffix =
        noArity.Split '.'
        |> Array.map (fun s -> if s.Length > 6 && s.EndsWith "Module" then s.Substring(0, s.Length - 6) else s)
        |> String.concat "."

    [ dotted; noArity; noSuffix ] |> List.distinct

/// `N.M+f@12` → (`N.M`, Some "f"); nested closures unwrap to the outermost binding.
let rec private splitClosure (clr: string) : string * string option =
    match clr.LastIndexOf '+' with
    | -1 -> clr, None
    | i ->
        let last = clr.Substring(i + 1)

        match last.IndexOf '@' with
        | at when at > 0 ->
            let owner, inner = splitClosure (clr.Substring(0, i))
            owner, (match inner with Some n -> Some n | None -> Some(last.Substring(0, at)))
        | _ -> clr, None

let private memberName (m: string) =
    if m.StartsWith "get_" || m.StartsWith "set_" then m.Substring 4
    elif m = "Invoke" || m = "MoveNext" || m = "Specialize" || m = ".ctor" || m = ".cctor" then ""
    else m

let private lastSegment (clr: string) =
    let d = clr.Replace('+', '.')
    match d.LastIndexOf '.' with -1 -> d | i -> d.Substring(i + 1)

let joinRow (ix: SymbolIndex) (unions: Set<string>) (row: ManifestRow) : JoinTarget =
    let firstExisting (names: string list) = names |> List.tryFind ix.Exists
    let caseOf (union: string) (case: string) = typeCandidates union |> List.map (fun u -> u + "." + case) |> firstExisting

    let byLocation () =
        match row.Document with
        | Some doc ->
            let rel = Path.GetRelativePath(ix.RepoRoot, doc).Replace('\\', '/')

            if rel.StartsWith ".." then
                Unmapped "outside-repo"
            elif not (ix.IsIndexedFile rel) then
                ToFile rel
            else
                let syms = ix.InFile rel |> List.filter (fun s -> s.Kind <> ExternRef)
                let owner, closureName = splitClosure row.TypeName

                let name =
                    match closureName with
                    | Some n -> n
                    | None ->
                        match memberName row.Member with
                        | "" when row.Member = ".ctor" -> lastSegment owner
                        | n -> n

                let nearest (cands: SymbolInfo list) =
                    match cands |> List.filter (fun s -> s.LineStart <= row.FirstLine) with
                    | [] -> cands |> List.sortBy (fun s -> abs (s.LineStart - row.FirstLine)) |> List.tryHead
                    | before -> before |> List.maxBy (fun s -> s.LineStart) |> Some

                let named = if name = "" then [] else syms |> List.filter (fun s -> canonicalShortName s.FullName = name)

                match nearest named with
                | Some s -> ToSymbol s.FullName
                | None ->
                    match syms |> List.filter (fun s -> row.FirstLine > 0 && s.LineStart <= row.FirstLine) with
                    | [] -> ToFile rel
                    | before -> ToSymbol (before |> List.maxBy (fun s -> s.LineStart)).FullName
        | None ->
            let owner, closureName = splitClosure row.TypeName
            let name = defaultArg closureName (memberName row.Member)
            let owners = typeCandidates owner

            let enclosing =
                owners
                |> List.collect (fun o ->
                    let segs = o.Split '.'
                    [ for n in segs.Length - 1 .. -1 .. 1 -> String.Join('.', segs.[0 .. n - 1]) ])

            [ if name <> "" then yield! owners |> List.map (fun o -> o + "." + name)
              yield! owners
              yield! enclosing ]
            |> firstExisting
            |> Option.map ToSymbol
            |> Option.defaultValue (Unmapped "no-document")

    match row.Kind with
    | UnionCase -> caseOf row.TypeName row.Member |> Option.map ToSymbol |> Option.defaultValue (Unmapped "case-not-indexed")
    | TypeUse -> typeCandidates row.TypeName |> firstExisting |> Option.map ToSymbol |> Option.defaultValue (Unmapped "type-not-indexed")
    | UserMethod
    | GeneratedMethod
    | StaticCtor ->
        let t = row.TypeName
        let parent, last = match t.LastIndexOf '+' with -1 -> "", t | i -> t.Substring(0, i), t.Substring(i + 1)

        if unions.Contains t then
            match row.Member with
            | "get_Tag" | "Equals" | "CompareTo" | "GetHashCode" | "ToString" | ".ctor" | ".cctor" -> Dropped
            | m when m.StartsWith "New" && (caseOf t (m.Substring 3)).IsSome -> ToSymbol (caseOf t (m.Substring 3)).Value
            | m when m.StartsWith "get_Is" && (caseOf t (m.Substring 6)).IsSome -> ToSymbol (caseOf t (m.Substring 6)).Value
            | m when m.StartsWith "get_" && (caseOf t (m.Substring 4)).IsSome -> ToSymbol (caseOf t (m.Substring 4)).Value
            | _ -> byLocation ()
        elif parent <> "" && unions.Contains parent then
            match caseOf parent (last.Replace("@DebugTypeProxy", "").TrimStart '_') with
            | Some s -> ToSymbol s
            | None -> Unmapped "case-class"
        elif last.Contains '@' && (row.Member = ".ctor" || row.Member = ".cctor") then
            Dropped
        else
            byLocation ()

let joinManifest (ix: SymbolIndex) (m: Manifest) : JoinTarget[] =
    let unions = m.Rows |> Array.filter (fun r -> r.Kind = UnionCase) |> Array.map (fun r -> r.TypeName) |> Set.ofArray
    m.Rows |> Array.map (joinRow ix unions)
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.JoinerTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t7-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict. The Core change is
an API addition, so `fssemantictagger` will see a minor bump; that is intended.

```bash
jj commit -m "Join woven probe ids to TestPrune symbols

Union cases and types join by name; union-generated members map to their case
or are dropped (their inner probes already attribute); closures join to the
binding they were written in; a method with a source document matches by name
in its own file before falling back to the nearest preceding declaration, and
an unindexed file becomes a file-level entry, which is sound. canonicalShortName
becomes public so the joiner shares core's definition of a short name.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Shadow bin, deps.json injection and JIT verification

**Files:**

- Create: `src/TestPrune.Trace.Recorder/StartupHook.fs` (last in the recorder's compile order)
- Create: `src/TestPrune.Trace/HardLink.fs`, `DepsJson.fs`, `ShadowBin.fs` (compile after `Joiner.fs`)
- Test: `tests/TestPrune.Trace.Tests/ShadowBinTests.fs`

**Interfaces:**

- Consumes: `Weaver.weave`, `SiteProbes.pass`, `Redirects.pass`, `Manifest.write/read`, `Launch.run`
  (Tasks 4–6).
- Produces:

```fsharp
module TestPrune.Trace.HardLink
type MirrorStats = { Linked: int; Copied: int; Removed: int }
val mirror: sourceDir: string -> shadowDir: string -> MirrorStats
val replaceWith: path: string -> write: (string -> unit) -> unit   // write to a temp sibling, then rename over

module TestPrune.Trace.DepsJson
val RecorderName: string   // "TestPrune.Trace.Recorder"
val injectRecorder: depsJson: string -> appName: string -> recorderVersion: string -> Result<string * bool, string>  // (json, changed)

module TestPrune.Trace.ShadowBin
type ShadowRequest = { RepoRoot: string; ProjectDir: string; AssemblyName: string; WeaveTests: Model.WeaveMode; VerifyTimeout: System.TimeSpan }
type VerifyReport = { Prepared: int; Invalid: string list; SkippedGeneric: int; Other: int }
type ShadowRefusal =
    | NoBuildOutput of binDebug: string
    | AmbiguousTfm of dirs: string list
    | NoApphost of path: string
    | WeaveRefused of Weaver.WeaveError
    | RecorderVersionSkew of found: string * expected: string
    | DepsJsonUnreadable of reason: string
    | JitInvalid of methods: string list
    | VerifyFailed of exitCode: int * output: string
type Shadow =
    { Dir: string; Apphost: string; ManifestDir: string; Manifest: Model.Manifest; WeaveKey: string
      Reused: bool; OriginalDepsJsonSha256: string; Verify: VerifyReport }
val describeRefusal: ShadowRefusal -> string
val prepare: ShadowRequest -> Result<Shadow, ShadowRefusal>
val verify: shadowDir: string -> apphost: string -> touched: Map<string, string list> -> reportPath: string -> timeout: System.TimeSpan -> Result<VerifyReport, ShadowRefusal>
```

- [ ] **Step 1: Write the failing tests**

`tests/TestPrune.Trace.Tests/ShadowBinTests.fs`:

```fsharp
module TestPrune.Trace.Tests.ShadowBinTests

open System
open System.IO
open System.Security.Cryptography
open Xunit
open Swensen.Unquote
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.ShadowBin
open TestPrune.Trace.Tests

let private sha (path: string) = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path))

[<Fact>]
let ``replacing a mirrored file never writes through the hardlink`` () =
    let root = Directory.CreateTempSubdirectory().FullName
    let src, dst = Path.Combine(root, "src"), Path.Combine(root, "dst")
    Directory.CreateDirectory src |> ignore
    File.WriteAllText(Path.Combine(src, "a.dll"), "original")
    HardLink.mirror src dst |> ignore
    HardLink.replaceWith (Path.Combine(dst, "a.dll")) (fun tmp -> File.WriteAllText(tmp, "woven"))
    test <@ File.ReadAllText(Path.Combine(src, "a.dll")) = "original" @>
    test <@ File.ReadAllText(Path.Combine(dst, "a.dll")) = "woven" @>

[<Fact>]
let ``mirroring re-links a rebuilt file and removes files the source no longer has`` () =
    let root = Directory.CreateTempSubdirectory().FullName
    let src, dst = Path.Combine(root, "src"), Path.Combine(root, "dst")
    Directory.CreateDirectory src |> ignore
    File.WriteAllText(Path.Combine(src, "a.dll"), "v1")
    File.WriteAllText(Path.Combine(src, "gone.dll"), "x")
    HardLink.mirror src dst |> ignore
    // A rebuild replaces the file (new inode); a stale link would keep "v1".
    File.Delete(Path.Combine(src, "a.dll"))
    File.WriteAllText(Path.Combine(src, "a.dll"), "v2")
    File.Delete(Path.Combine(src, "gone.dll"))
    let stats = HardLink.mirror src dst
    test <@ File.ReadAllText(Path.Combine(dst, "a.dll")) = "v2" @>
    test <@ not (File.Exists(Path.Combine(dst, "gone.dll"))) @>
    test <@ stats.Removed = 1 @>

let private deps =
    """{ "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
         "targets": { ".NETCoreApp,Version=v10.0": { "App/1.0.0": { "dependencies": { "FSharp.Core": "10.1.0" }, "runtime": { "App.dll": {} } } } },
         "libraries": { "App/1.0.0": { "type": "project", "serviceable": false, "sha512": "" } } }"""

[<Fact>]
let ``the recorder is injected as a project library the app depends on, idempotently`` () =
    let json, changed = DepsJson.injectRecorder deps "App" "0.1.0" |> Result.defaultWith failwith
    test <@ changed @>
    test <@ json.Contains "\"TestPrune.Trace.Recorder/0.1.0\"" && json.Contains "\"TestPrune.Trace.Recorder.dll\"" @>
    let again, changedAgain = DepsJson.injectRecorder json "App" "0.1.0" |> Result.defaultWith failwith
    test <@ not changedAgain && again = json @>
    test <@ DepsJson.injectRecorder json "App" "0.2.0" |> Result.isError @>

[<Fact>]
let ``prepare weaves the fixture test project into bin/Traced and leaves bin/Debug untouched`` () =
    let projectDir = Path.Combine(Fixtures.fixtureRoot, "tests", "FxTests")
    let libBefore = sha (Path.Combine(Fixtures.fxTestsDir, "FxLib.dll"))

    let req =
        { RepoRoot = Fixtures.repoRoot; ProjectDir = projectDir; AssemblyName = "FxTests"
          WeaveTests = SitesOnly; VerifyTimeout = TimeSpan.FromMinutes 2.0 }

    let shadow = prepare req |> Result.defaultWith (fun e -> failwith (describeRefusal e))
    test <@ shadow.Dir = Path.Combine(projectDir, "bin", "Traced", "net10.0") @>
    test <@ File.Exists shadow.Apphost @>
    test <@ sha (Path.Combine(Fixtures.fxTestsDir, "FxLib.dll")) = libBefore @>
    test <@ sha (Path.Combine(shadow.Dir, "FxLib.dll")) <> libBefore @>
    test <@ shadow.Manifest.Rows |> Array.exists (fun r -> r.Assembly = "FxLib" && r.Member = "area") @>
    test <@ shadow.Manifest.Rows |> Array.filter (fun r -> r.Assembly = "FxTests") |> Array.forall (fun r -> r.Kind <> StaticCtor) @>
    test <@ shadow.Verify.Invalid = [] && shadow.Verify.Prepared > 0 @>
    test <@ not (File.Exists(Path.Combine(shadow.Dir, "TestPrune.Trace.Recorder.pdb"))) @>
    let again = prepare req |> Result.defaultWith (fun e -> failwith (describeRefusal e))
    test <@ again.Reused && again.WeaveKey = shadow.WeaveKey @>

[<Fact>]
let ``JIT verification in the app's own runtime names an invalid method`` () =
    let dir = WeaverTests.copyFixture Fixtures.fxDriverDir
    let lib = Path.Combine(dir, "FxLib.dll")

    do
        use asm = AssemblyDefinition.ReadAssembly(lib, ReaderParameters(ReadWrite = true))
        let area = asm.MainModule.GetType("FxLib.Logic").Methods |> Seq.find (fun m -> m.Name = "area")
        let il = area.Body.GetILProcessor()
        // Stack underflow: invalid IL the JIT rejects.
        il.InsertBefore(area.Body.Instructions.[0], il.Create OpCodes.Pop)
        asm.Write()

    let report = Path.Combine(dir, "verify.json")
    let r = verify dir (Path.Combine(dir, "FxDriver")) (Map.ofList [ "FxLib", [ "FxLib.Logic::area" ] ]) report (TimeSpan.FromMinutes 1.0)
    test <@ r = Error(JitInvalid [ "FxLib.Logic::area" ]) @>
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build`
Expected: FAIL, with `HardLink`, `DepsJson`, `ShadowBin` and `StartupHook` undefined.

- [ ] **Step 3: Implement**

`src/TestPrune.Trace.Recorder/StartupHook.fs`. The runtime requires this exact shape: a type named
`StartupHook` in **no namespace**, with a `public static void Initialize()`.

```fsharp
namespace global

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Text.Json
open TestPrune.Trace.Recorder

/// Loaded via DOTNET_STARTUP_HOOKS. Inert unless TESTPRUNE_TRACE_VERIFY names a report
/// path: then it JIT-prepares every woven method listed in the file named by
/// TESTPRUNE_TRACE_VERIFY_ASSEMBLIES ("<assembly>\t<Type>::<method>" per line) in THIS
/// app's runtime (its shared frameworks included), writes the report and exits before
/// Main. A weaver bug that emits invalid IL is caught here, not by a test failing.
[<AbstractClass; Sealed>]
type StartupHook =
    static member Initialize() =
        match Environment.GetEnvironmentVariable Contract.VerifyEnv with
        | null
        | "" -> ()
        | reportPath ->
            let mutable prepared, skipped, other = 0, 0, 0
            let invalid = ResizeArray<string>()
            let flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly

            for line in File.ReadAllLines(Environment.GetEnvironmentVariable Contract.VerifyAssembliesEnv) do
                match line.Split '\t' with
                | [| asmName; key |] ->
                    let sep = key.LastIndexOf "::"
                    let typeName, methodName = key.Substring(0, sep), key.Substring(sep + 2)

                    try
                        let t = Assembly.Load(AssemblyName asmName).GetType(typeName, true)

                        let candidates: MethodBase seq =
                            if methodName = ".ctor" || methodName = ".cctor" then
                                t.GetConstructors flags |> Seq.filter (fun c -> c.IsStatic = (methodName = ".cctor")) |> Seq.cast
                            else
                                t.GetMethods flags |> Seq.filter (fun m -> m.Name = methodName) |> Seq.cast

                        for m in candidates do
                            if t.ContainsGenericParameters || m.ContainsGenericParameters then
                                skipped <- skipped + 1
                            else
                                try
                                    RuntimeHelpers.PrepareMethod m.MethodHandle
                                    prepared <- prepared + 1
                                with :? InvalidProgramException ->
                                    invalid.Add key
                    with _ ->
                        other <- other + 1
                | _ -> other <- other + 1

            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(
                    {| prepared = prepared; invalid = invalid |> Seq.distinct |> Seq.toArray; skippedGeneric = skipped; other = other |}
                )
            )

            Environment.Exit 0
```

`src/TestPrune.Trace/HardLink.fs`:

```fsharp
module TestPrune.Trace.HardLink

open System
open System.IO
open System.Runtime.InteropServices

module private Native =
    [<DllImport("libc", SetLastError = true, EntryPoint = "link")>]
    extern int link(string oldpath, string newpath)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool CreateHardLinkW(string newName, string existing, nativeint security)

let private tryLink (src: string) (dst: string) =
    try
        if OperatingSystem.IsWindows() then Native.CreateHardLinkW(dst, src, 0n) else Native.link (src, dst) = 0
    with _ ->
        false

type MirrorStats = { Linked: int; Copied: int; Removed: int }

let private files (dir: string) =
    let opts = EnumerationOptions(RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint)
    Directory.EnumerateFiles(dir, "*", opts) |> Seq.map (fun f -> Path.GetRelativePath(dir, f)) |> Set.ofSeq

/// Make `shadowDir` a hardlink mirror of `sourceDir`. EVERY file is re-linked each
/// time: a rebuilt source file is a new inode, and a surviving old link would keep
/// serving the old bytes. Falls back to a copy when linking fails (e.g. across devices).
let mirror (sourceDir: string) (shadowDir: string) : MirrorStats =
    Directory.CreateDirectory shadowDir |> ignore
    let src = files sourceDir
    let mutable linked, copied, removed = 0, 0, 0

    for rel in files shadowDir do
        if not (src.Contains rel) then
            File.Delete(Path.Combine(shadowDir, rel))
            removed <- removed + 1

    for rel in src do
        let s, d = Path.Combine(sourceDir, rel), Path.Combine(shadowDir, rel)
        Directory.CreateDirectory(Path.GetDirectoryName d) |> ignore
        if File.Exists d then File.Delete d

        if tryLink s d then
            linked <- linked + 1
        else
            File.Copy(s, d)
            copied <- copied + 1

    { Linked = linked; Copied = copied; Removed = removed }

/// Replace `path` by writing a temp sibling and renaming it over. A rename replaces the
/// DIRECTORY ENTRY; writing into `path` would write through the hardlink into bin/Debug.
let replaceWith (path: string) (write: string -> unit) =
    let tmp = path + ".tp-tmp"
    write tmp
    File.Move(tmp, path, true)
```

`src/TestPrune.Trace/DepsJson.fs`:

```fsharp
module TestPrune.Trace.DepsJson

open System.Text.Json
open System.Text.Json.Nodes

[<Literal>]
let RecorderName = "TestPrune.Trace.Recorder"

/// Add the recorder as a `project` library the app depends on, so the default load
/// context resolves the woven assemblies' reference to it. Returns (json, changed).
/// An app that already lists the recorder at a DIFFERENT version is refused: the woven
/// IL references this weaver's recorder.
let injectRecorder (depsJson: string) (appName: string) (recorderVersion: string) : Result<string * bool, string> =
    try
        let root = JsonNode.Parse(depsJson).AsObject()
        let libraries = root.["libraries"].AsObject()
        let existing = libraries |> Seq.tryFind (fun kv -> kv.Key.StartsWith(RecorderName + "/"))

        match existing with
        | Some kv when kv.Key = $"%s{RecorderName}/%s{recorderVersion}" -> Ok(depsJson, false)
        | Some kv -> Error $"recorder-version-skew:%s{kv.Key.Substring(RecorderName.Length + 1)}"
        | None ->
            let key = $"%s{RecorderName}/%s{recorderVersion}"
            let targetName = root.["runtimeTarget"].["name"].GetValue<string>()
            let target = root.["targets"].[targetName].AsObject()
            let app = target |> Seq.find (fun kv -> kv.Key.StartsWith(appName + "/"))
            let appObj = app.Value.AsObject()

            if not (appObj.ContainsKey "dependencies") then
                appObj.["dependencies"] <- JsonObject()

            appObj.["dependencies"].AsObject().[RecorderName] <- JsonValue.Create recorderVersion
            target.[key] <- JsonObject(dict [ "runtime", JsonObject(dict [ RecorderName + ".dll", JsonObject() :> JsonNode ]) :> JsonNode ])
            libraries.[key] <- JsonObject(dict [ "type", JsonValue.Create "project" :> JsonNode; "serviceable", JsonValue.Create false; "sha512", JsonValue.Create "" ])
            Ok(root.ToJsonString(JsonSerializerOptions(WriteIndented = true)), true)
    with ex ->
        Error ex.Message
```

`src/TestPrune.Trace/ShadowBin.fs`:

```fsharp
module TestPrune.Trace.ShadowBin

open System
open System.IO
open System.Reflection.Metadata
open System.Security.Cryptography
open System.Text.Json
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

type ShadowRequest =
    { RepoRoot: string
      ProjectDir: string
      AssemblyName: string
      WeaveTests: WeaveMode
      VerifyTimeout: TimeSpan }

type VerifyReport = { Prepared: int; Invalid: string list; SkippedGeneric: int; Other: int }

type ShadowRefusal =
    | NoBuildOutput of binDebug: string
    | AmbiguousTfm of dirs: string list
    | NoApphost of path: string
    | WeaveRefused of Weaver.WeaveError
    | RecorderVersionSkew of found: string * expected: string
    | DepsJsonUnreadable of reason: string
    | JitInvalid of methods: string list
    | VerifyFailed of exitCode: int * output: string

type Shadow =
    { Dir: string
      Apphost: string
      ManifestDir: string
      Manifest: Manifest
      WeaveKey: string
      Reused: bool
      OriginalDepsJsonSha256: string
      Verify: VerifyReport }

let describeRefusal =
    function
    | NoBuildOutput d -> $"no build output under %s{d}"
    | AmbiguousTfm ds -> $"more than one target framework holds the app: %s{String.concat ", " ds}"
    | NoApphost p -> $"no apphost at %s{p}"
    | WeaveRefused e -> $"weave refused: %A{e}"
    | RecorderVersionSkew(found, expected) -> $"the app references recorder %s{found}; this weaver needs %s{expected}"
    | DepsJsonUnreadable why -> $"deps.json: %s{why}"
    | JitInvalid ms -> $"woven IL failed JIT verification: %s{String.concat ", " ms}"
    | VerifyFailed(code, out) -> $"JIT verification did not complete (exit %d{code}): %s{out}"

let private sha256File (path: string) = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

let private sha256Text (s: string) =
    Convert.ToHexString(SHA256.HashData(Text.Encoding.UTF8.GetBytes s)).ToLowerInvariant()

/// Built from this repository: its portable PDB names at least one document under the root.
let private builtFromRepo (repoRoot: string) (dll: string) =
    let pdb = Path.ChangeExtension(dll, ".pdb")

    File.Exists pdb
    && (try
            use fs = File.OpenRead pdb
            use p = MetadataReaderProvider.FromPortablePdbStream fs
            let r = p.GetMetadataReader()

            r.Documents
            |> Seq.exists (fun h -> r.GetString(r.GetDocument(h).Name).StartsWith(repoRoot, StringComparison.Ordinal))
        with _ ->
            false)

let verify (shadowDir: string) (apphost: string) (touched: Map<string, string list>) (reportPath: string) (timeout: TimeSpan) =
    let listPath = reportPath + ".methods.tsv"
    File.WriteAllLines(listPath, [ for KeyValue(asm, keys) in touched do for k in keys -> asm + "\t" + k ])
    let recorder = Path.Combine(shadowDir, DepsJson.RecorderName + ".dll")

    let code, output =
        Launch.run apphost [] [ "DOTNET_STARTUP_HOOKS", recorder; Contract.VerifyEnv, reportPath; Contract.VerifyAssembliesEnv, listPath ] shadowDir timeout

    if code <> 0 || not (File.Exists reportPath) then
        Error(VerifyFailed(code, output))
    else
        use doc = JsonDocument.Parse(File.ReadAllText reportPath)
        let r = doc.RootElement

        let report =
            { Prepared = r.GetProperty("prepared").GetInt32()
              Invalid = [ for v in r.GetProperty("invalid").EnumerateArray() -> v.GetString() ]
              SkippedGeneric = r.GetProperty("skippedGeneric").GetInt32()
              Other = r.GetProperty("other").GetInt32() }

        if report.Invalid.IsEmpty then Ok report else Error(JitInvalid report.Invalid)

let prepare (req: ShadowRequest) : Result<Shadow, ShadowRefusal> =
    let binDebug = Path.Combine(req.ProjectDir, "bin", "Debug")

    let tfmDirs =
        if Directory.Exists binDebug then
            Directory.GetDirectories binDebug |> Array.filter (fun d -> File.Exists(Path.Combine(d, req.AssemblyName + ".dll"))) |> List.ofArray
        else
            []

    match tfmDirs with
    | [] -> Error(NoBuildOutput binDebug)
    | _ :: _ :: _ -> Error(AmbiguousTfm tfmDirs)
    | [ sourceDir ] ->
        let shadowDir = Path.Combine(req.ProjectDir, "bin", "Traced", Path.GetFileName sourceDir)
        let apphostName = if OperatingSystem.IsWindows() then req.AssemblyName + ".exe" else req.AssemblyName
        let apphost = Path.Combine(shadowDir, apphostName)
        HardLink.mirror sourceDir shadowDir |> ignore

        if not (File.Exists apphost) then
            Error(NoApphost apphost)
        else
            let recorderSrc = typeof<Probes>.Assembly.Location
            let recorderVersion = typeof<Probes>.Assembly.GetName().Version.ToString 3
            let weaverVersion = typeof<Weaver.WeaveResult>.Assembly.GetName().Version.ToString 3

            let inputs =
                Directory.GetFiles(sourceDir, "*.dll")
                |> Array.filter (fun f -> Path.GetFileNameWithoutExtension f <> DepsJson.RecorderName && builtFromRepo req.RepoRoot f)
                |> Array.sort
                |> Array.map (fun f ->
                    { Weaver.WeaveInput.Path = f
                      Weaver.WeaveInput.Mode = (if Path.GetFileNameWithoutExtension f = req.AssemblyName then req.WeaveTests else Full) })
                |> List.ofArray

            let key =
                sha256Text (
                    String.concat
                        "\n"
                        [ yield weaverVersion
                          yield recorderVersion
                          for i in inputs do
                              yield $"%s{Path.GetFileName i.Path}|%A{i.Mode}|%s{sha256File i.Path}|%s{sha256File (Path.ChangeExtension(i.Path, ".pdb"))}" ]
                )

            let cacheDir = Path.Combine(req.ProjectDir, "obj", "traced", key)
            let wovenDir = Path.Combine(cacheDir, "woven")
            let manifestDir = Path.Combine(cacheDir, "manifest")
            let donePath = Path.Combine(cacheDir, "done.json")
            let reused = File.Exists donePath

            let woven =
                if reused then
                    Ok(Directory.GetFiles(wovenDir, "*.dll") |> List.ofArray, None)
                else
                    match Weaver.weave [ SiteProbes.pass (); Redirects.pass () ] inputs wovenDir with
                    | Error e -> Error(WeaveRefused e)
                    | Ok r ->
                        Manifest.write manifestDir r.Manifest
                        Ok(r.Outputs, Some r.Stats.Touched)

            match woven with
            | Error e -> Error e
            | Ok(outputs, touched) ->
                for dll in outputs do
                    for f in [ dll; Path.ChangeExtension(dll, ".pdb") ] do
                        HardLink.replaceWith (Path.Combine(shadowDir, Path.GetFileName f)) (fun tmp -> File.Copy(f, tmp, true))

                let depsPath = Path.Combine(shadowDir, req.AssemblyName + ".deps.json")
                let originalDeps = File.ReadAllText(Path.Combine(sourceDir, req.AssemblyName + ".deps.json"))

                match DepsJson.injectRecorder originalDeps req.AssemblyName recorderVersion with
                | Error e when e.StartsWith "recorder-version-skew:" -> Error(RecorderVersionSkew(e.Substring 22, recorderVersion))
                | Error e -> Error(DepsJsonUnreadable e)
                | Ok(json, injected) ->
                    if injected then
                        HardLink.replaceWith depsPath (fun tmp -> File.WriteAllText(tmp, json))
                        HardLink.replaceWith (Path.Combine(shadowDir, DepsJson.RecorderName + ".dll")) (fun tmp -> File.Copy(recorderSrc, tmp, true))
                        // No PDB for an injected recorder: MS CodeCoverage skips symbol-less modules,
                        // so it never shows up (as paths outside the repo) in the app's coverage.
                        let pdb = Path.Combine(shadowDir, DepsJson.RecorderName + ".pdb")
                        if File.Exists pdb then File.Delete pdb

                    let verified =
                        let reportPath = Path.Combine(cacheDir, "verify.json")

                        match touched with
                        | Some t -> verify shadowDir apphost t reportPath req.VerifyTimeout
                        | None ->
                            use doc = JsonDocument.Parse(File.ReadAllText reportPath)
                            let r = doc.RootElement
                            Ok { Prepared = r.GetProperty("prepared").GetInt32(); Invalid = []; SkippedGeneric = r.GetProperty("skippedGeneric").GetInt32(); Other = r.GetProperty("other").GetInt32() }

                    match verified with
                    | Error e -> Error e
                    | Ok report ->
                        if not reused then File.WriteAllText(donePath, "{}")

                        match Manifest.read manifestDir with
                        | Error e -> Error(DepsJsonUnreadable $"manifest: %s{e}")
                        | Ok manifest ->
                            File.WriteAllText(
                                Path.Combine(shadowDir, ".testprune-trace.json"),
                                JsonSerializer.Serialize({| weaveKey = key; manifestDir = manifestDir; ids = manifest.IdCount; verified = report.Prepared |})
                            )

                            // Keep the 3 most recent weave keys.
                            let traced = Path.Combine(req.ProjectDir, "obj", "traced")
                            for old in Directory.GetDirectories traced |> Array.sortByDescending Directory.GetLastWriteTimeUtc |> Array.skip 3 do
                                if Path.GetFileName old <> key then Directory.Delete(old, true)

                            Ok
                                { Dir = shadowDir; Apphost = apphost; ManifestDir = manifestDir; Manifest = manifest; WeaveKey = key
                                  Reused = reused; OriginalDepsJsonSha256 = sha256Text originalDeps; Verify = report }
```

`done.json` is written only after a successful verify. A weave whose verification failed is therefore never
reused: the next `prepare` re-weaves and re-verifies. A verify *report* is written by the startup hook, so a
`done.json` with no `verify.json` beside it cannot occur.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.ShadowBinTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t8-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj commit -m "Shadow bin: a woven, hardlinked copy of a test app under bin/Traced

The copy sits beside bin/Debug so repository-root probing behaves the same.
Every file is re-linked each run (a rebuilt file is a new inode); woven
assemblies, their PDBs and the patched deps.json are written to a temp file
and renamed over the link, never written through it. Weaves are cached by a
content key under obj/traced. Every touched method is JIT-prepared inside the
app's own runtime through a DOTNET_STARTUP_HOOKS mode of the recorder before
the weave is accepted; an injected recorder ships without its PDB so it never
enters the app's coverage report.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: Ingestion: fingerprint, outcomes, join and the incomplete-trace rules

**Files:**

- Create: `src/TestPrune.Trace/Fingerprint.fs`, `TraceIngest.fs` (compile after `ShadowBin.fs`)
- Test: `tests/TestPrune.Trace.Tests/TraceIngestTests.fs`

**Interfaces:**

- Consumes: `DumpReader.readDirectory` (Task 2); `TraceStore.Store`, `RecordRun`, `CollectGarbage` (Task 3);
  `Joiner.ofStore`, `joinManifest` (Task 7); `ShadowBin.Shadow` (Task 8); `TestPrune.Ports.SymbolStore`,
  `TestPrune.Database.SchemaVersion` (Core).
- Produces:

```fsharp
module TestPrune.Trace.Fingerprint
type Inputs =
    { Runtime: string; Os: string; Arch: string; DepsJsonSha256: string; RecorderVersion: string
      WeaverVersion: string; HashScheme: int; ConfigFiles: (string * string) list; ConfigEnv: (string * string) list }
val compute: Inputs -> string                      // lowercase sha256 hex of canonical JSON
val hashFile: repoRoot: string -> relPath: string -> string   // sha256 hex, or "missing"
val gather: repoRoot: string -> files: string list -> env: string list -> dump: Model.ProcessDump -> shadow: ShadowBin.Shadow -> Inputs

module TestPrune.Trace.TraceIngest
type IngestRequest =
    { RunId: string; TestProject: string; RepoRoot: string; InputRoot: string; Kind: TraceStore.RunKind
      LaunchTreeHash: string; CurrentTreeHash: string; DumpDir: string; Shadow: ShadowBin.Shadow
      Outcomes: Model.TestOutcome list; Symbols: TestPrune.Ports.SymbolStore
      FingerprintFiles: string list; FingerprintEnv: string list; RecordedAt: System.DateTimeOffset }
type IngestSummary =
    { EnvFingerprint: string option; Executed: int; Traced: int; Complete: int
      UntracedExecuted: string list; ReasonCounts: Map<string, int>; Counters: Model.HitCounters
      RejectedDumps: (string * string) list; CpuMs: int64; UnmappedIds: int }
val testKey: project: string -> cls: string -> meth: string -> string   // "<project>|<class>|<method>"
val repoRelative: repoRoot: string -> absPath: string -> string option  // bin/Traced/ → bin/Debug/
val ingest: store: TraceStore.Store -> IngestRequest -> IngestSummary
```

**Rules implemented here** (the design's R4 inputs):

- **Outcome join:** match the CTRF `name` equal to the scope's display name; else match a name that equals, or
  starts with `<class>.<method>(`, the normalised `<class>.<method>` (with `+` → `.`). Theory rows of one
  `test_key` union their scopes; the key's status is the worst row: Failed > Other > Skipped > Passed. No match
  → `NoOutcome`.
- **Per-test reasons:**
  - `NotPassed` when the status is not Passed.
  - `SourceDrift f`: a hit id's row has a document whose current SHA-256 differs from the PDB's recorded hash
    (the index no longer describes the binary).
  - `NotIndexed f`: the document file is missing on disk.
  - `UnmappedCode`: a hit id joined to `Unmapped`.
  - `ChildProcessUntraced f`: a child noted on the test's own scopes has no dump with that pid whose
    `parentScope` is one of those scopes.
- **Process-wide reasons** (applied to every test of the project in this run):
  - `RecorderOverflow` (any dump's overflow counter > 0);
  - `DumpRejected r` (any rejected dump);
  - `TreeMoved` (launch tree ≠ current tree).
- **Inherited scopes:** the transitive closure of `parents` and `links` from the test's T scopes, restricted to
  scopes that recorded something. `S:static-init` and `A:ambient` are stored as run scopes but never linked;
  phase 2 decides how static-init-only symbols fall back.
- **Symbol version hash:**
  - a symbol with one occurrence uses its `ContentHash`;
  - a symbol with several occurrences (`.fsi` + `.fs`) uses `sha256` of its sorted `file|hash` pairs.

  Phase 2 must compute "current version" with this same function, so it is public: `TraceIngest.versionHashes`.
- **Inputs** (`trace_inputs.kind`):
  - `read` → SHA-256 of the file now;
  - `exists` → `"present"` or `"absent"`;
  - `list` → SHA-256 of the sorted entry names, or `"absent"`;
  - `file-level` → SHA-256 of a `ToFile` target.

  Keys are repo-relative, with `bin/Traced/` rewritten to `bin/Debug/`.
- **No usable dump** → a `trace_runs` row `failed`, reason `recorder-no-output`, and no test rows. Those tests
  keep any older trace. A full run then garbage-collects them, because a test that ran without being traced
  must be re-recorded (R1), not verified against a stale trace.

The module also exports `val versionHashes: TestPrune.Ports.SymbolStore -> Map<string, string>`.

- [ ] **Step 1: Write the failing tests**

`tests/TestPrune.Trace.Tests/TraceIngestTests.fs`:

```fsharp
module TestPrune.Trace.Tests.TraceIngestTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder
open TestPrune.Trace.TraceIngest

/// A miniature world: a repo dir with one source file, an index holding its symbols,
/// a manifest whose rows point at that file, and dumps written by a real RecorderState.
type World() =
    let root = Directory.CreateTempSubdirectory().FullName
    let src = Path.Combine(root, "src", "M.fs")
    do Directory.CreateDirectory(Path.GetDirectoryName src) |> ignore
    do File.WriteAllText(src, "module N.M\nlet f () = 1\nlet g () = 2\n")
    let srcHash = Fingerprint.hashFile root "src/M.fs"
    let db = Database.create (Path.Combine(root, "index.db"))

    do
        db.RebuildProjects(
            [ { Symbols =
                  [ { FullName = "N.M.f"; Kind = Function; SourceFile = "src/M.fs"; LineStart = 2; LineEnd = 2; ContentHash = "hf"; IsExtern = false }
                    { FullName = "N.M.g"; Kind = Function; SourceFile = "src/M.fs"; LineStart = 3; LineEnd = 3; ContentHash = "hg"; IsExtern = false } ]
                Dependencies = []; TestMethods = []; Attributes = []; ParentLinks = []; Diagnostics = AnalysisDiagnostics.Zero } ],
            fileKeys = [ "src/M.fs", "k" ]
        )

    let manifest =
        { Rows =
            [| { Id = 0; Kind = UserMethod; Assembly = "A"; TypeName = "N.M"; Member = "f"; Document = Some src; FirstLine = 2; LastLine = 2 }
               { Id = 1; Kind = UserMethod; Assembly = "A"; TypeName = "N.M"; Member = "g"; Document = Some src; FirstLine = 3; LastLine = 3 }
               { Id = 2; Kind = GeneratedMethod; Assembly = "A"; TypeName = "Q.Nowhere"; Member = "x"; Document = None; FirstLine = 0; LastLine = 0 } |]
          Documents = Map.ofList [ src, srcHash ]
          IdCount = 3 }

    member _.Root = root
    member _.Src = src
    member _.Store = TestPrune.Ports.toSymbolStore db
    member val DumpDir = Path.Combine(root, "dumps")
    member val TraceDb = Path.Combine(root, "traces.db")

    member this.Shadow =
        { ShadowBin.Shadow.Dir = root; Apphost = ""; ManifestDir = ""; Manifest = manifest; WeaveKey = "k"
          Reused = false; OriginalDepsJsonSha256 = "d"; Verify = { Prepared = 0; Invalid = []; SkippedGeneric = 0; Other = 0 } }

    /// Record one process: `f` drives a RecorderState, then it is dumped as pid `pid`.
    member this.Process(pid: int, parent: string, f: RecorderState -> unit) =
        let st = RecorderState(3, None, parent)
        st.RepoRoot <- root
        f st
        Directory.CreateDirectory this.DumpDir |> ignore
        DumpWriter.write (Path.Combine(this.DumpDir, $"trace-%d{pid}.ndjson")) st

    member this.Request(outcomes: TestOutcome list) =
        { RunId = "run1"; TestProject = "P"; RepoRoot = root; InputRoot = root; Kind = TraceStore.FullRun
          LaunchTreeHash = "t"; CurrentTreeHash = "t"; DumpDir = this.DumpDir; Shadow = this.Shadow
          Outcomes = outcomes; Symbols = this.Store; FingerprintFiles = []; FingerprintEnv = []
          RecordedAt = DateTimeOffset.UtcNow }

/// Put a test identity on a scope the way the xUnit source would.
let private asTest (st: RecorderState) (key: string) (cls: string) (meth: string) (display: string) =
    st.EnterScope key
    let s = st.CurrentScope()
    s.TestClass <- cls
    s.TestMethod <- meth
    s.TestDisplay <- display
    s.Parents <- [| "C:" + cls; "A:assembly" |]

[<Fact>]
let ``a passing test is stored complete with its symbols at their current hashes`` () =
    let w = World()
    w.Process(10, null, fun st -> asTest st "T:1" "N.Tests" "t" "N.Tests.t"; st.Hit 0)
    use store = TraceStore.Store.Open w.TraceDb
    let summary = ingest store (w.Request [ { Name = "N.Tests.t"; Outcome = Passed } ])
    test <@ (summary.Executed, summary.Traced, summary.Complete) = (1, 1, 1) @>
    let t = store.TryRead(testKey "P" "N.Tests" "t", summary.EnvFingerprint.Value) |> Option.get
    test <@ t.Complete && t.Symbols = set [ "N.M.f", "hf" ] @>

[<Fact>]
let ``theory rows union, the worst outcome wins, and a class scope is inherited`` () =
    let w = World()

    w.Process(10, null, fun st ->
        st.EnterScope "C:N.Tests"
        st.Hit 1
        asTest st "T:row1" "N.Tests" "th" "N.Tests.th(x: 1)"
        st.Hit 0
        asTest st "T:row2" "N.Tests" "th" "N.Tests.th(x: 2)")

    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ { Name = "N.Tests.th(x: 1)"; Outcome = Passed }; { Name = "N.Tests.th(x: 2)"; Outcome = Failed } ])
    let t = store.TryRead(testKey "P" "N.Tests" "th", s.EnvFingerprint.Value) |> Option.get
    test <@ t.Symbols = set [ "N.M.f", "hf"; "N.M.g", "hg" ] @>
    test <@ t.Reasons = [ "not-passed:failed" ] @>

[<Fact>]
let ``a test with no outcome, unmapped code, or an untraced child is incomplete and says why`` () =
    let w = World()

    w.Process(10, null, fun st ->
        asTest st "T:1" "N.Tests" "a" "N.Tests.a"
        st.Hit 2
        asTest st "T:2" "N.Tests" "b" "N.Tests.b"
        st.CurrentScope().Children.Enqueue(struct (4242, "dotnet", true)))

    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ { Name = "N.Tests.b"; Outcome = Passed } ])
    let a = store.TryRead(testKey "P" "N.Tests" "a", s.EnvFingerprint.Value) |> Option.get
    let b = store.TryRead(testKey "P" "N.Tests" "b", s.EnvFingerprint.Value) |> Option.get
    test <@ a.Reasons = [ "no-outcome"; "unmapped-code:Q.Nowhere::x" ] @>
    test <@ b.Reasons = [ "child-process-untraced:dotnet" ] @>

[<Fact>]
let ``a traced child's hits merge into the parent test and complete it`` () =
    let w = World()
    w.Process(10, null, fun st -> asTest st "T:1" "N.Tests" "t" "N.Tests.t"; st.CurrentScope().Children.Enqueue(struct (11, "app", true)))
    w.Process(11, "T:1", fun st -> st.Hit 1)
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ { Name = "N.Tests.t"; Outcome = Passed } ])
    let t = store.TryRead(testKey "P" "N.Tests" "t", s.EnvFingerprint.Value) |> Option.get
    test <@ t.Complete && t.Symbols = set [ "N.M.g", "hg" ] @>

[<Fact>]
let ``a source file edited since the build marks its executors as drifted`` () =
    let w = World()
    w.Process(10, null, fun st -> asTest st "T:1" "N.Tests" "t" "N.Tests.t"; st.Hit 0)
    File.AppendAllText(w.Src, "let h () = 3\n")
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ { Name = "N.Tests.t"; Outcome = Passed } ])
    test <@ (store.TryRead(testKey "P" "N.Tests" "t", s.EnvFingerprint.Value) |> Option.get).Reasons = [ "source-drift:src/M.fs" ] @>

[<Fact>]
let ``no usable dump records a failed run and no tests; executed tests are listed as untraced`` () =
    let w = World()
    Directory.CreateDirectory w.DumpDir |> ignore
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ { Name = "N.Tests.t"; Outcome = Passed } ])
    test <@ s.Traced = 0 && s.UntracedExecuted = [ "N.Tests.t" ] @>
    test <@ (store.Runs "P" |> List.head).Reason = "recorder-no-output" @>

[<Fact>]
let ``a tree that moved during the run makes every trace incomplete`` () =
    let w = World()
    w.Process(10, null, fun st -> asTest st "T:1" "N.Tests" "t" "N.Tests.t"; st.Hit 0)
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store { w.Request [ { Name = "N.Tests.t"; Outcome = Passed } ] with CurrentTreeHash = "moved" }
    test <@ (store.TryRead(testKey "P" "N.Tests" "t", s.EnvFingerprint.Value) |> Option.get).Reasons = [ "tree-moved" ] @>

[<Fact>]
let ``the fingerprint changes with any input and is stable otherwise`` () =
    let i =
        { Fingerprint.Inputs.Runtime = ".NET 10.0.0"; Os = "OSX"; Arch = "Arm64"; DepsJsonSha256 = "d"; RecorderVersion = "0.1.0"
          WeaverVersion = "0.1.0"; HashScheme = 16; ConfigFiles = [ "global.json", "h" ]; ConfigEnv = [] }

    test <@ Fingerprint.compute i = Fingerprint.compute i @>
    test <@ Fingerprint.compute i <> Fingerprint.compute { i with HashScheme = 17 } @>
    test <@ Fingerprint.compute i <> Fingerprint.compute { i with DepsJsonSha256 = "e" } @>
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build`
Expected: FAIL, with `Fingerprint` and `TraceIngest` undefined.

- [ ] **Step 3: Implement**

`src/TestPrune.Trace/Fingerprint.fs`:

```fsharp
module TestPrune.Trace.Fingerprint

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open TestPrune.Trace.Model

type Inputs =
    { Runtime: string
      Os: string
      Arch: string
      DepsJsonSha256: string
      RecorderVersion: string
      WeaverVersion: string
      HashScheme: int
      ConfigFiles: (string * string) list
      ConfigEnv: (string * string) list }

let private hex (bytes: byte[]) = Convert.ToHexString(bytes).ToLowerInvariant()

let compute (i: Inputs) : string =
    let canonical =
        JsonSerializer.Serialize(
            {| runtime = i.Runtime; os = i.Os; arch = i.Arch; deps = i.DepsJsonSha256; recorder = i.RecorderVersion
               weaver = i.WeaverVersion; hashScheme = i.HashScheme
               files = i.ConfigFiles |> List.sort |> List.map (fun (p, h) -> p + "=" + h)
               env = i.ConfigEnv |> List.sort |> List.map (fun (n, h) -> n + "=" + h) |}
        )

    hex (SHA256.HashData(Encoding.UTF8.GetBytes canonical))

let hashFile (repoRoot: string) (relPath: string) : string =
    let p = Path.Combine(repoRoot, relPath)
    if File.Exists p then hex (SHA256.HashData(File.ReadAllBytes p)) else "missing"

let gather (repoRoot: string) (files: string list) (env: string list) (dump: ProcessDump) (shadow: ShadowBin.Shadow) : Inputs =
    { Runtime = dump.Runtime
      Os = dump.Os
      Arch = dump.Arch
      DepsJsonSha256 = shadow.OriginalDepsJsonSha256
      RecorderVersion = typeof<TestPrune.Trace.Recorder.Probes>.Assembly.GetName().Version.ToString 3
      WeaverVersion = typeof<Weaver.WeaveResult>.Assembly.GetName().Version.ToString 3
      // Any change to what a content hash means bumps core's SchemaVersion (stored hashes
      // are recomputed), so it is the hash-scheme number: traces from an older scheme
      // become a fingerprint mismatch, never a spurious "still verifies".
      HashScheme = TestPrune.Database.SchemaVersion
      ConfigFiles = files |> List.map (fun f -> f, hashFile repoRoot f)
      ConfigEnv =
        env
        |> List.map (fun n ->
            n,
            match Environment.GetEnvironmentVariable n with
            | null -> "unset"
            | v -> hex (SHA256.HashData(Encoding.UTF8.GetBytes v))) }
```

`src/TestPrune.Trace/TraceIngest.fs`:

```fsharp
module TestPrune.Trace.TraceIngest

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open TestPrune.Trace.Model
open TestPrune.Trace.Joiner
open TestPrune.Trace.TraceStore

type IngestRequest =
    { RunId: string
      TestProject: string
      /// Root the joiner resolves PDB documents against.
      RepoRoot: string
      /// Root the recorder filtered file inputs against; input keys are relative to it.
      /// The same directory in production; a test fixture may index a sub-tree.
      InputRoot: string
      Kind: RunKind
      LaunchTreeHash: string
      CurrentTreeHash: string
      DumpDir: string
      Shadow: ShadowBin.Shadow
      Outcomes: TestOutcome list
      Symbols: TestPrune.Ports.SymbolStore
      FingerprintFiles: string list
      FingerprintEnv: string list
      RecordedAt: DateTimeOffset }

type IngestSummary =
    { EnvFingerprint: string option
      Executed: int
      Traced: int
      Complete: int
      UntracedExecuted: string list
      ReasonCounts: Map<string, int>
      Counters: HitCounters
      RejectedDumps: (string * string) list
      CpuMs: int64
      UnmappedIds: int }

let testKey (project: string) (cls: string) (meth: string) = $"%s{project}|%s{cls}|%s{meth}"

let private hex (b: byte[]) = Convert.ToHexString(b).ToLowerInvariant()

let repoRelative (repoRoot: string) (absPath: string) : string option =
    let rel = Path.GetRelativePath(repoRoot, absPath).Replace('\\', '/')
    if rel.StartsWith ".." then None else Some(rel.Replace("/bin/Traced/", "/bin/Debug/"))

/// The version hash of every indexed symbol: its ContentHash when it has one occurrence,
/// else the hash of its sorted (file|hash) pairs. Phase 2 must use this same function.
let versionHashes (store: TestPrune.Ports.SymbolStore) : Map<string, string> =
    store.GetAllSymbols()
    |> List.groupBy (fun s -> s.FullName)
    |> List.map (fun (name, occ) ->
        match occ with
        | [ one ] -> name, one.ContentHash
        | many ->
            let joined = many |> List.map (fun o -> o.SourceFile + "|" + o.ContentHash) |> List.sort |> String.concat "\n"
            name, hex (SHA256.HashData(Encoding.UTF8.GetBytes joined)))
    |> Map.ofList

let private zero =
    { Test = 0L; Class = 0L; Collection = 0L; Assembly = 0L; Override = 0L; StaticInit = 0L; Ambient = 0L; Overflow = 0L }

let private add (a: HitCounters) (b: HitCounters) =
    { Test = a.Test + b.Test; Class = a.Class + b.Class; Collection = a.Collection + b.Collection; Assembly = a.Assembly + b.Assembly
      Override = a.Override + b.Override; StaticInit = a.StaticInit + b.StaticInit; Ambient = a.Ambient + b.Ambient; Overflow = a.Overflow + b.Overflow }

let private norm (s: string) = s.Replace('+', '.')

let private severity =
    function
    | Failed -> 3
    | OtherOutcome -> 2
    | Skipped -> 1
    | Passed -> 0

/// Merged view of one scope key across every process of the run.
type private Merged =
    { Key: string
      Ids: HashSet<int>
      Inputs: HashSet<RecordedInput>
      Parents: HashSet<string>
      Links: HashSet<string>
      Children: ResizeArray<ChildNote>
      mutable Test: TestIdentity option }

let ingest (store: Store) (req: IngestRequest) : IngestSummary =
    let dumps, rejected = DumpReader.readDirectory req.DumpDir
    let counters = dumps |> List.fold (fun acc d -> add acc d.Counters) zero
    let cpu = dumps |> List.sumBy (fun d -> d.CpuMs)

    let executedNames =
        req.Outcomes |> List.filter (fun o -> o.Outcome <> Skipped) |> List.map (fun o -> o.Name) |> List.distinct

    // Annotated: IngestRequest shares several labels with TraceRun.
    let run fingerprint status reason statsJson : TraceRun =
        { RunId = req.RunId; TestProject = req.TestProject; TreeHash = req.LaunchTreeHash; EnvFingerprint = fingerprint
          RecordedAt = req.RecordedAt; Kind = req.Kind; Status = status; Reason = reason; StatsJson = statsJson }

    match dumps |> List.tryFind (fun d -> d.ParentScope.IsNone) with
    | None ->
        let why = if rejected.IsEmpty then "recorder-no-output" else "recorder-no-output; rejected: " + String.concat "; " (rejected |> List.map snd)
        store.RecordRunWithoutTraces(run "" FailedToRecord (if rejected.IsEmpty then "recorder-no-output" else why) "{}")

        { EnvFingerprint = None; Executed = executedNames.Length; Traced = 0; Complete = 0; UntracedExecuted = executedNames
          ReasonCounts = Map.empty; Counters = counters; RejectedDumps = rejected; CpuMs = cpu; UnmappedIds = 0 }
    | Some mainDump ->
        let fp = Fingerprint.compute (Fingerprint.gather req.RepoRoot req.FingerprintFiles req.FingerprintEnv mainDump req.Shadow)
        let manifest = req.Shadow.Manifest
        let targets = joinManifest (ofStore req.Symbols req.RepoRoot) manifest
        let versions = versionHashes req.Symbols

        // Per document: drifted (edited since the build) or missing on disk.
        let docState =
            manifest.Documents
            |> Map.map (fun doc recorded ->
                match repoRelative req.RepoRoot doc with
                | None -> None
                | Some rel ->
                    match Fingerprint.hashFile req.RepoRoot rel with
                    | "missing" -> Some(NotIndexed rel)
                    | now when recorded <> "" && now <> recorded -> Some(SourceDrift rel)
                    | _ -> None)

        // Merge scopes by key across processes.
        let merged = Dictionary<string, Merged>()

        for d in dumps do
            for s in d.Scopes do
                let m =
                    match merged.TryGetValue s.Key with
                    | true, m -> m
                    | _ ->
                        let m: Merged = { Key = s.Key; Ids = HashSet(); Inputs = HashSet(); Parents = HashSet(); Links = HashSet(); Children = ResizeArray(); Test = None }
                        merged.[s.Key] <- m
                        m

                m.Ids.UnionWith s.Ids
                m.Inputs.UnionWith s.Inputs
                m.Parents.UnionWith s.Parents
                m.Links.UnionWith s.Links
                m.Children.AddRange s.Children
                if m.Test.IsNone then m.Test <- s.Test

        let tracedChildren = dumps |> List.choose (fun d -> d.ParentScope |> Option.map (fun p -> p, d.Pid)) |> Set.ofList
        let mutable unmappedIds = 0

        let contentOf (m: Merged) : ScopeContent * IncompleteReason list =
            let reasons = ResizeArray<IncompleteReason>()
            let symbols = ResizeArray<string * string>()
            let inputs = ResizeArray<string * string * string>()

            for id in m.Ids do
                if id >= 0 && id < targets.Length then
                    let row = manifest.Rows.[id]

                    match row.Document |> Option.bind (fun d -> docState.TryFind d |> Option.flatten) with
                    | Some r -> reasons.Add r
                    | None -> ()

                    match targets.[id] with
                    | ToSymbol n ->
                        match versions.TryFind n with
                        | Some h -> symbols.Add(n, h)
                        | None -> reasons.Add(UnmappedCode $"%s{row.TypeName}::%s{row.Member}")
                    | ToFile rel -> inputs.Add("file-level", rel, Fingerprint.hashFile req.RepoRoot rel)
                    | Dropped -> ()
                    | Unmapped _ ->
                        unmappedIds <- unmappedIds + 1
                        reasons.Add(UnmappedCode $"%s{row.TypeName}::%s{row.Member}")

            for i in m.Inputs do
                match repoRelative req.InputRoot i.Path with
                | None -> ()
                | Some rel ->
                    match i.Kind with
                    | FileRead -> inputs.Add("read", rel, Fingerprint.hashFile req.InputRoot rel)
                    | ExistenceProbe ->
                        let p = Path.Combine(req.InputRoot, rel)
                        inputs.Add("exists", rel, (if File.Exists p || Directory.Exists p then "present" else "absent"))
                    | DirectoryListing ->
                        let p = Path.Combine(req.InputRoot, rel)

                        let h =
                            if Directory.Exists p then
                                Directory.GetFileSystemEntries p
                                |> Array.map Path.GetFileName
                                |> Array.sort
                                |> String.concat "\n"
                                |> Encoding.UTF8.GetBytes
                                |> SHA256.HashData
                                |> hex
                            else
                                "absent"

                        inputs.Add("list", rel, h)

            for c in m.Children do
                if not (tracedChildren.Contains(m.Key, c.Pid)) then
                    reasons.Add(ChildProcessUntraced c.FileName)

            ({ Key = m.Key; Symbols = List.ofSeq symbols; Inputs = List.ofSeq inputs }: ScopeContent), List.ofSeq (Seq.distinct reasons)

        let contents = merged.Values |> Seq.map (fun m -> m.Key, contentOf m) |> Map.ofSeq

        let processWide =
            [ if counters.Overflow > 0L then yield RecorderOverflow
              for f, why in rejected do yield DumpRejected $"%s{Path.GetFileName f}: %s{why}"
              if req.LaunchTreeHash <> req.CurrentTreeHash then yield TreeMoved ]

        let rec closure (seen: Set<string>) (keys: string list) =
            match keys with
            | [] -> seen
            | k :: rest when seen.Contains k || not (merged.ContainsKey k) -> closure seen rest
            | k :: rest -> closure (seen.Add k) (List.ofSeq merged.[k].Parents @ List.ofSeq merged.[k].Links @ rest)

        let outcomeOf (t: TestIdentity) =
            let exact = req.Outcomes |> List.filter (fun o -> o.Name = t.Display)

            let rows =
                if not exact.IsEmpty then
                    exact
                else
                    let stem = norm (t.Class + "." + t.Method)
                    req.Outcomes |> List.filter (fun o -> let n = norm o.Name in n = stem || n.StartsWith(stem + "("))

            if rows.IsEmpty then None else rows |> List.map (fun o -> o.Outcome) |> List.maxBy severity |> Some

        let testScopes =
            merged.Values |> Seq.filter (fun m -> m.Key.StartsWith "T:" && m.Test.IsSome) |> List.ofSeq

        let tests =
            testScopes
            |> List.groupBy (fun m -> testKey req.TestProject m.Test.Value.Class m.Test.Value.Method)
            |> List.map (fun (key, rows) ->
                let statuses = rows |> List.choose (fun m -> outcomeOf m.Test.Value)
                let status = if statuses.IsEmpty then None else Some(statuses |> List.maxBy severity)
                let linked = closure Set.empty (rows |> List.map (fun m -> m.Key)) |> Set.toList

                let reasons =
                    [ if status.IsNone then yield NoOutcome
                      match status with
                      | Some s when s <> Passed -> yield NotPassed s
                      | _ -> ()
                      for k in linked do yield! snd contents.[k]
                      yield! processWide ]
                    |> List.distinct

                ({ TestKey = key; Status = status; Reasons = reasons; ScopeKeys = linked }: TestTrace))

        let linkedScopes = tests |> List.collect (fun t -> t.ScopeKeys) |> Set.ofList

        let scopes =
            contents
            |> Map.toList
            |> List.filter (fun (k, _) -> linkedScopes.Contains k || k = "S:static-init" || k = "A:ambient")
            |> List.map (fun (_, (c, _)) -> c)

        let tracedNames =
            req.Outcomes
            |> List.filter (fun o ->
                testScopes |> List.exists (fun m -> o.Name = m.Test.Value.Display || norm o.Name = norm (m.Test.Value.Class + "." + m.Test.Value.Method) || norm(o.Name).StartsWith(norm (m.Test.Value.Class + "." + m.Test.Value.Method) + "(")))
            |> List.map (fun o -> o.Name)
            |> Set.ofList

        let reasonCounts = tests |> List.collect (fun t -> t.Reasons |> List.map reasonCode) |> List.countBy id |> Map.ofList
        let complete = tests |> List.filter (fun t -> t.Reasons.IsEmpty) |> List.length

        let stats =
            JsonSerializer.Serialize(
                {| executed = executedNames.Length; traced = executedNames |> List.filter tracedNames.Contains |> List.length
                   complete = complete; unmappedIds = unmappedIds; cpuMs = cpu
                   counters = counters; rejected = rejected |> List.map (fun (f, w) -> Path.GetFileName f + ": " + w) |}
            )

        let status = if req.LaunchTreeHash <> req.CurrentTreeHash then TreeMovedDuringRun else Recorded
        store.RecordRun(run fp status "" stats, scopes, tests)

        store.CollectGarbage(
            req.TestProject,
            fp,
            (match req.Kind with
             | FullRun -> Some(tests |> List.map (fun t -> t.TestKey) |> Set.ofList)
             | PartialRun -> None)
        )

        { EnvFingerprint = Some fp
          Executed = executedNames.Length
          Traced = executedNames |> List.filter tracedNames.Contains |> List.length
          Complete = complete
          UntracedExecuted = executedNames |> List.filter (tracedNames.Contains >> not)
          ReasonCounts = reasonCounts
          Counters = counters
          RejectedDumps = rejected
          CpuMs = cpu
          UnmappedIds = unmappedIds }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.TraceIngestTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t9-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj commit -m "Ingest per-test traces: join, outcomes, fingerprint, incomplete reasons

Dumps from every process of a run are merged by scope; ids are joined to
symbols at their current version hash, file reads hashed, fixture/pool scopes
linked by the parents and links the recorder saw. A trace is complete only
when its test passed, every executed file still matches the PDB it was built
from, every executed id mapped, and every child process it started recorded
too; otherwise the reasons are stored. The fingerprint folds in core's schema
version as the hash scheme.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: Host facade and the end-to-end test on a real xUnit v3 suite

**Files:**

- Create: `src/TestPrune.Trace/Ctrf.fs` (compile after `Launch.fs`), `src/TestPrune.Trace/TraceSession.fs` (last)
- Create: `src/TestPrune.Trace/README.md` (the consumer contract; see Step 3)
- Test: `tests/TestPrune.Trace.Tests/EndToEndTests.fs`

**Interfaces:**

- Consumes: everything from Tasks 2–9.
- Produces (what FsHotWatch calls; nothing else in `TestPrune.Trace` is needed by a host):

```fsharp
module TestPrune.Trace.Ctrf
val parse: json: string -> Model.TestOutcome list     // results.tests[] (or top-level tests[]); [] when unreadable

module TestPrune.Trace.TraceSession
type PrepareRequest =
    { RepoRoot: string; ProjectDir: string; AssemblyName: string; TestProject: string
      WeaveTests: Model.WeaveMode; RunDir: string; VerifyTimeout: System.TimeSpan }
type TraceLaunch =
    { TestProject: string; Apphost: string; Env: (string * string) list; DumpDir: string
      InputRoot: string; Shadow: ShadowBin.Shadow }
type Completion =
    { RunId: string; Kind: TraceStore.RunKind; LaunchTreeHash: string; CurrentTreeHash: string
      Outcomes: Model.TestOutcome list; Symbols: TestPrune.Ports.SymbolStore
      FingerprintFiles: string list; FingerprintEnv: string list }
val prepareProject: PrepareRequest -> Result<TraceLaunch, string>      // never throws
val ingestProject: store: TraceStore.Store -> repoRoot: string -> TraceLaunch -> Completion -> Result<TraceIngest.IngestSummary, string>  // never throws
val recordRefusal: store: TraceStore.Store -> runId: string -> testProject: string -> kind: TraceStore.RunKind -> treeHash: string -> reason: string -> unit
```

`TraceLaunch.Env` is exactly:

- `TESTPRUNE_TRACE_OUT` = `<RunDir>/traces/<TestProject>/` (created empty);
- `TESTPRUNE_TRACE_IDS` = the manifest id count;
- `TESTPRUNE_TRACE_REPO_ROOT` = the repository root;
- `DOTNET_ROOT` = `Launch.dotnetRoot ()`.

- [ ] **Step 1: Write the failing test**

`tests/TestPrune.Trace.Tests/EndToEndTests.fs`:

```fsharp
module TestPrune.Trace.Tests.EndToEndTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.TraceSession
open TestPrune.Trace.Tests

/// Prepare the fixture's xUnit v3 project, run its woven apphost with the same CTRF
/// switches a host passes, ingest, and hand back the store and the summary.
let private run =
    lazy
        (let runDir = Directory.CreateTempSubdirectory().FullName
         let projectDir = Path.Combine(Fixtures.fixtureRoot, "tests", "FxTests")

         let launch =
             prepareProject
                 { RepoRoot = Fixtures.repoRoot; ProjectDir = projectDir; AssemblyName = "FxTests"; TestProject = "FxTests"
                   WeaveTests = SitesOnly; RunDir = runDir; VerifyTimeout = TimeSpan.FromMinutes 2.0 }
             |> Result.defaultWith failwith

         let code, output =
             Launch.run launch.Apphost [ "--report-xunit-ctrf"; "--report-xunit-ctrf-filename"; "FxTests.ctrf.json"; "--results-directory"; runDir ]
                 launch.Env Fixtures.repoRoot (TimeSpan.FromMinutes 3.0)

         if code <> 0 then failwith $"woven fixture suite failed:\n%s{output}"
         let outcomes = Ctrf.parse (File.ReadAllText(Path.Combine(runDir, "FxTests.ctrf.json")))
         let store = TraceStore.Store.Open(Path.Combine(runDir, "traces.db"))
         let symbols = TestPrune.Ports.toSymbolStore FixtureIndex.build.Value

         // The fixture index is rooted at a scratch copy with the fixtures' relative layout;
         // the woven PDB documents live under the real fixture root. Ingest against that root.
         let summary =
             ingestProject store Fixtures.fixtureRoot launch
                 { RunId = "e2e"; Kind = TraceStore.FullRun; LaunchTreeHash = "t"; CurrentTreeHash = "t"; Outcomes = outcomes
                   Symbols = symbols; FingerprintFiles = []; FingerprintEnv = [] }
             |> Result.defaultWith failwith

         store, summary)

let private traceOf (meth: string) =
    let store, summary = run.Value
    let cls = if meth.StartsWith "a " then "ClassA" elif meth.StartsWith "b " then "ClassB" else "ClassC"
    store.TryRead(TraceIngest.testKey "FxTests" $"FxTests.AttributionTests+%s{cls}" meth, summary.EnvFingerprint.Value) |> Option.get

let private names (t: TraceStore.StoredTrace) = t.Symbols |> Set.map fst

[<Fact>]
let ``every executed test is traced and nothing is unattributed`` () =
    let _, s = run.Value
    test <@ s.Executed = 9 && s.Traced = 9 @>
    test <@ s.UntracedExecuted = [] @>
    test <@ s.Counters.Ambient = 0L @>

[<Fact>]
let ``sync, task, Task.Run, async and Async.Parallel tests each record exactly their own code`` () =
    test <@ names (traceOf "a sync area") |> Set.isSuperset <| set [ "FxLib.Logic.area"; "FxLib.Shape.Circle" ] @>
    test <@ not (names (traceOf "a sync area") |> Set.contains "FxLib.Logic.turn") @>
    test <@ names (traceOf "a async task turn") |> Set.isSuperset <| set [ "FxLib.Logic.turn"; "FxLib.Dir.East"; "FxLib.Dir.South" ] @>
    test <@ names (traceOf "a Task.Run describe") |> Set.isSuperset <| set [ "FxLib.Logic.describe"; "FxLib.Dog" ] @>
    test <@ names (traceOf "b async block colorCode") |> Set.contains "FxLib.Color5.Blue" @>
    test <@ names (traceOf "b Async.Parallel sval") |> Set.isSuperset <| set [ "FxLib.SResult.SOk"; "FxLib.SResult.SErr" ] @>
    test <@ names (traceOf "b theory aboveThreshold") |> Set.isSuperset <| set [ "FxLib.Logic.aboveThreshold"; "FxLib.Values.threshold" ] @>

[<Fact>]
let ``a class fixture's code is inherited by that class's tests only`` () =
    test <@ names (traceOf "a sync area") |> Set.contains "FxLib.Logic.sumPoint" @>
    test <@ not (names (traceOf "b async block colorCode") |> Set.contains "FxLib.Logic.sumPoint") @>

[<Fact>]
let ``each test's own entry point is in its trace`` () =
    test <@ names (traceOf "a sync area") |> Set.exists (fun n -> n.StartsWith "FxTests.AttributionTests.ClassA.") @>

[<Fact>]
let ``a repository file read is an input; an untraced child makes the trace incomplete`` () =
    let read = traceOf "c reads a repo file"
    test <@ read.Complete && read.Inputs |> Set.exists (fun (k, key, _) -> k = "read" && key = "global.json") @>
    let child = traceOf "c starts a child process"
    test <@ child.Reasons = [ "child-process-untraced:echo" ] @>
```

The class names use `+` because F# module-nested classes compile as nested CLR types. That matches both
xUnit's `TestClassName` and TestPrune's `test_methods.test_class`.

Two roots are in play:

- The fixture index is rooted at `tests/TraceFixtures`, so `ingestProject` is given that directory for the
  joiner's document paths.
- The recorder filtered file inputs against the *repository* root (`PrepareRequest.RepoRoot`), which
  `TraceLaunch.InputRoot` carries into `IngestRequest.InputRoot`. That is why the `global.json` key is
  repository-relative.

In production both roots are the repository root.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build`
Expected: FAIL, with `Ctrf` and `TraceSession` undefined.

- [ ] **Step 3: Implement**

`src/TestPrune.Trace/Ctrf.fs`:

```fsharp
module TestPrune.Trace.Ctrf

open System.Text.Json.Nodes
open TestPrune.Trace.Model

/// The per-test rows of a CTRF report (`results.tests`, or a top-level `tests`). A real
/// MTP report omits rows for tests that threw a raw exception; such a test simply gets
/// no outcome here, which ingestion records as `NoOutcome`, never as passed.
let parse (json: string) : TestOutcome list =
    try
        let root = JsonNode.Parse json

        let arr =
            match root.["results"] with
            | null -> root.["tests"]
            | r -> match r.["tests"] with null -> root.["tests"] | t -> t

        match arr with
        | :? JsonArray as a ->
            [ for n in a do
                  match n with
                  | null -> ()
                  | n ->
                      match n.["name"], n.["status"] with
                      | null, _
                      | _, null -> ()
                      | name, status ->
                          yield
                              { Name = name.GetValue<string>()
                                Outcome =
                                  match status.GetValue<string>().ToLowerInvariant() with
                                  | "passed" -> Passed
                                  | "failed" -> Failed
                                  | "skipped"
                                  | "pending" -> Skipped
                                  | _ -> OtherOutcome } ]
        | _ -> []
    with _ ->
        []
```

`src/TestPrune.Trace/TraceSession.fs`:

```fsharp
module TestPrune.Trace.TraceSession

open System
open System.IO
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

type PrepareRequest =
    { RepoRoot: string
      ProjectDir: string
      AssemblyName: string
      TestProject: string
      WeaveTests: WeaveMode
      RunDir: string
      VerifyTimeout: TimeSpan }

type TraceLaunch =
    { TestProject: string
      Apphost: string
      Env: (string * string) list
      DumpDir: string
      InputRoot: string
      Shadow: ShadowBin.Shadow }

type Completion =
    { RunId: string
      Kind: TraceStore.RunKind
      LaunchTreeHash: string
      CurrentTreeHash: string
      Outcomes: TestOutcome list
      Symbols: TestPrune.Ports.SymbolStore
      FingerprintFiles: string list
      FingerprintEnv: string list }

let prepareProject (req: PrepareRequest) : Result<TraceLaunch, string> =
    try
        match
            ShadowBin.prepare
                { RepoRoot = req.RepoRoot; ProjectDir = req.ProjectDir; AssemblyName = req.AssemblyName
                  WeaveTests = req.WeaveTests; VerifyTimeout = req.VerifyTimeout }
        with
        | Error refusal -> Error(ShadowBin.describeRefusal refusal)
        | Ok shadow ->
            let dumpDir = Path.Combine(req.RunDir, "traces", req.TestProject)
            if Directory.Exists dumpDir then Directory.Delete(dumpDir, true)
            Directory.CreateDirectory dumpDir |> ignore

            Ok
                { TestProject = req.TestProject
                  Apphost = shadow.Apphost
                  DumpDir = dumpDir
                  InputRoot = req.RepoRoot
                  Shadow = shadow
                  Env =
                    [ Contract.OutEnv, dumpDir
                      Contract.IdsEnv, string shadow.Manifest.IdCount
                      Contract.RepoRootEnv, req.RepoRoot
                      "DOTNET_ROOT", Launch.dotnetRoot () ] }
    with ex ->
        Error $"trace preparation failed: %s{ex.Message}"

let ingestProject (store: TraceStore.Store) (repoRoot: string) (launch: TraceLaunch) (c: Completion) =
    try
        Ok(
            TraceIngest.ingest
                store
                { RunId = c.RunId; TestProject = launch.TestProject; RepoRoot = repoRoot; InputRoot = launch.InputRoot
                  Kind = c.Kind; LaunchTreeHash = c.LaunchTreeHash; CurrentTreeHash = c.CurrentTreeHash
                  DumpDir = launch.DumpDir; Shadow = launch.Shadow; Outcomes = c.Outcomes; Symbols = c.Symbols
                  FingerprintFiles = c.FingerprintFiles; FingerprintEnv = c.FingerprintEnv; RecordedAt = DateTimeOffset.UtcNow }
        )
    with ex ->
        Error $"trace ingestion failed: %s{ex.Message}"

let recordRefusal (store: TraceStore.Store) (runId: string) (testProject: string) (kind: TraceStore.RunKind) (treeHash: string) (reason: string) =
    store.RecordRunWithoutTraces
        { RunId = runId; TestProject = testProject; TreeHash = treeHash; EnvFingerprint = ""; RecordedAt = DateTimeOffset.UtcNow
          Kind = kind; Status = TraceStore.Refused; Reason = reason; StatsJson = "{}" }
```

`src/TestPrune.Trace/README.md` (packed as the package readme). It states:

- what the package does;
- that a host calls `TraceSession.prepareProject`, launches `Apphost` with `Env` plus its own arguments, then
  calls `ingestProject`;
- the **scope contract** for consumers with in-process servers. It gives the four `Scopes` methods and this
  reflection-binding snippet, which needs no package reference and is inert when the recorder is absent:

```fsharp
/// Bind TestPrune.Trace.Recorder.Scopes if the process is traced; no-ops otherwise.
module TraceScope =
    let private t = System.Type.GetType("TestPrune.Trace.Recorder.Scopes, TestPrune.Trace.Recorder", false)
    let private m (name: string) = if isNull t then null else t.GetMethod name
    let private enter, exit, current, link = m "Enter", m "Exit", m "CurrentKey", m "LinkCurrentTo"
    let enter (key: string) = if not (isNull enter) then enter.Invoke(null, [| box key |]) |> ignore
    let exit () = if not (isNull exit) then exit.Invoke(null, [||]) |> ignore
    let currentKey () : string = if isNull current then null else current.Invoke(null, [||]) :?> string
    let linkCurrentTo (key: string) = if not (isNull link) then link.Invoke(null, [| box key |]) |> ignore
```

- the header convention `X-Test-Trace-Scope: <CurrentKey()>`: the server middleware calls `enter` with the
  header value when it starts with `T:`, and otherwise `enter ("P:" + poolName)`;
- the coverage note from Decision D1;
- that Release builds are refused.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.EndToEndTests --filter-class TestPrune.Trace.Tests.TraceIngestTests`
Expected: PASS, 13 tests.

If `Executed = 9` fails because the CTRF report counts theory rows differently, print `outcomes` and fix the
**expectation's number** only after confirming, from the CTRF file, that every row the runner executed is
present. Do not weaken the equality between `Executed` and `Traced`.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t10-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj commit -m "Host facade for recording traces, proven end to end on a real xUnit v3 suite

prepareProject builds the shadow bin and returns the apphost and environment a
host launches; ingestProject turns the run's dumps and CTRF outcomes into
stored traces. Neither throws: a host records the refusal and runs the project
untraced. The end-to-end test runs a woven xUnit v3 fixture with parallel
classes, async, Task.Run, Async.Parallel, a theory, a class fixture, a file
read and a child process, and checks each trace.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 11: The census verb (traced %, unattributed hits, pool scopes)

**Files:**

- Create: `src/TestPrune.Trace/Census.fs` (compile after `TraceSession.fs`)
- Create: `src/TestPrune.Trace.Cli/TestPrune.Trace.Cli.fsproj`, `src/TestPrune.Trace.Cli/Program.fs`
- Modify: `TestPrune.slnx` (add the tool under `/src/`); `semantic-tagger.json` (add
  `src/TestPrune.Trace.Cli/TestPrune.Trace.Cli.fsproj` to the `trace-v` entry's `fsProjsSharingSameTag`)
- Test: `tests/TestPrune.Trace.Tests/CensusTests.fs`

**Interfaces:**

- Consumes: `TraceStore.Store` and the `stats_json` written by Task 9 (keys `executed`, `traced`, `complete`,
  `unmappedIds`, `cpuMs`, `counters`, `rejected`).
- Produces:

```fsharp
module TestPrune.Trace.Census
type PoolScope = { Key: string; Symbols: int; Inputs: int; LinkedTests: int }
type ProjectCensus =
    { TestProject: string; RunId: string; Executed: int; Traced: int; Complete: int
      TotalHits: int64; AmbientHits: int64; AmbientSymbols: string list
      ReasonCounts: Map<string, int>; Pools: PoolScope list }
type Bars = { TracedAtLeast: float; AmbientBelow: float }
val defaultBars: Bars                       // 0.99, 0.001
val tracedRatio: ProjectCensus -> float     // Traced / Executed (1.0 when Executed = 0)
val ambientRatio: ProjectCensus -> float    // AmbientHits / TotalHits (0.0 when no hits)
val passes: Bars -> ProjectCensus -> bool   // ambient passes when below the bar OR every ambient symbol is listed for explanation (the report prints them)
val latest: dbPath: string -> runId: string option -> ProjectCensus list   // per project, the given run or the newest 'recorded' run
val render: ProjectCensus list -> string    // human table + the ambient symbols and pool table
```

`TestPrune.Trace.Cli` is `<PackAsTool>true</PackAsTool>` with `<ToolCommandName>test-prune-traces</ToolCommandName>`,
net10.0, and references `TestPrune.Trace`. Verbs:

```text
test-prune-traces census [--db <path>] [--run <runId>] [--json]
    exit 0 when every project passes the bars, 1 otherwise
```

- [ ] **Step 1: Write the failing test**

`tests/TestPrune.Trace.Tests/CensusTests.fs`:

```fsharp
module TestPrune.Trace.Tests.CensusTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.TraceStore

[<Fact>]
let ``census reads a run's ratios, ambient symbols and pool scopes`` () =
    let path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "t.db")

    do
        use store = Store.Open path

        let stats =
            """{"executed":200,"traced":199,"complete":190,"unmappedIds":0,"cpuMs":1,
                "counters":{"Test":9990,"Class":0,"Collection":0,"Assembly":0,"Override":0,"StaticInit":0,"Ambient":10,"Overflow":0},"rejected":[]}"""

        store.RecordRun(
            { RunId = "r"; TestProject = "P"; TreeHash = "t"; EnvFingerprint = "E"; RecordedAt = DateTimeOffset.UtcNow
              Kind = FullRun; Status = Recorded; Reason = ""; StatsJson = stats },
            [ { Key = "T:1"; Symbols = [ "N.f", "h" ]; Inputs = [] }
              { Key = "P:pool"; Symbols = [ "N.handler", "h"; "N.job", "h" ]; Inputs = [] }
              { Key = "A:ambient"; Symbols = [ "N.timerTick", "h" ]; Inputs = [] } ],
            [ { TestKey = "P|C|m"; Status = Some Passed; Reasons = []; ScopeKeys = [ "T:1"; "P:pool" ] } ]
        )

    let c = Census.latest path None |> List.exactlyOne
    test <@ Census.tracedRatio c = 0.995 @>
    test <@ Census.ambientRatio c = 0.001 @>
    test <@ c.AmbientSymbols = [ "N.timerTick" ] @>
    test <@ c.Pools = [ { Key = "P:pool"; Symbols = 2; Inputs = 0; LinkedTests = 1 } ] @>
    test <@ Census.passes Census.defaultBars c @>
    test <@ not (Census.passes Census.defaultBars { c with Traced = 197 }) @>
```

The ambient bar reads "< 0.1 %, or explained". At exactly 0.1 % the ratio fails `< 0.001`, but the ambient
symbols are listed, which is the "explained" arm. `passes` therefore returns true only when the listed symbols
are all present in the report. A human signs off on them in the results file (Task D1); the verb never claims
the explanation is *good*.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build`
Expected: FAIL, with `Census` undefined.

- [ ] **Step 3: Implement**

`src/TestPrune.Trace/Census.fs`:

```fsharp
module TestPrune.Trace.Census

open System.Text.Json
open Microsoft.Data.Sqlite

type PoolScope = { Key: string; Symbols: int; Inputs: int; LinkedTests: int }

type ProjectCensus =
    { TestProject: string
      RunId: string
      Executed: int
      Traced: int
      Complete: int
      TotalHits: int64
      AmbientHits: int64
      AmbientSymbols: string list
      ReasonCounts: Map<string, int>
      Pools: PoolScope list }

type Bars = { TracedAtLeast: float; AmbientBelow: float }

let defaultBars = { TracedAtLeast = 0.99; AmbientBelow = 0.001 }
let tracedRatio c = if c.Executed = 0 then 1.0 else float c.Traced / float c.Executed
let ambientRatio c = if c.TotalHits = 0L then 0.0 else float c.AmbientHits / float c.TotalHits

let passes (bars: Bars) (c: ProjectCensus) =
    tracedRatio c >= bars.TracedAtLeast
    && (ambientRatio c < bars.AmbientBelow || (c.AmbientHits = 0L || not c.AmbientSymbols.IsEmpty))

let latest (dbPath: string) (runId: string option) : ProjectCensus list =
    (TraceStore.Store.Open dbPath :> System.IDisposable).Dispose() // version check; refuses a newer file
    use conn = new SqliteConnection($"Data Source=%s{dbPath};Mode=ReadOnly")
    conn.Open()

    let rows (sql: string) (ps: (string * obj) list) (f: SqliteDataReader -> 'a) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        for n, v in ps do cmd.Parameters.AddWithValue(n, v) |> ignore
        use r = cmd.ExecuteReader()
        [ while r.Read() do yield f r ]

    let runs =
        rows
            """SELECT r.id, r.test_project, r.run_id, r.stats_json FROM trace_runs r
               WHERE r.status IN ('recorded', 'tree-moved') AND (@run IS NULL OR r.run_id = @run)
                 AND r.id = (SELECT MAX(id) FROM trace_runs x WHERE x.test_project = r.test_project
                             AND x.status IN ('recorded', 'tree-moved') AND (@run IS NULL OR x.run_id = @run))"""
            [ "@run", (match runId with Some r -> box r | None -> box System.DBNull.Value) ]
            (fun r -> r.GetInt64 0, r.GetString 1, r.GetString 2, r.GetString 3)

    runs
    |> List.map (fun (id, project, run, statsJson) ->
        use doc = JsonDocument.Parse statsJson
        let st = doc.RootElement
        let counters = st.GetProperty "counters"
        let total = counters.EnumerateObject() |> Seq.filter (fun p -> p.Name <> "Overflow") |> Seq.sumBy (fun p -> p.Value.GetInt64())

        let scopeSymbols key =
            rows
                """SELECT v.symbol_full_name FROM trace_scopes s JOIN trace_entries e ON e.scope_id = s.id
                   JOIN symbol_versions v ON v.id = e.symbol_version_id WHERE s.trace_run_id = @id AND s.scope_key = @k ORDER BY 1"""
                [ "@id", box id; "@k", box key ]
                (fun r -> r.GetString 0)

        let pools =
            rows
                """SELECT s.scope_key,
                          (SELECT COUNT(*) FROM trace_entries e WHERE e.scope_id = s.id),
                          (SELECT COUNT(*) FROM trace_inputs i WHERE i.scope_id = s.id),
                          (SELECT COUNT(*) FROM trace_test_scopes ts WHERE ts.scope_id = s.id)
                   FROM trace_scopes s WHERE s.trace_run_id = @id AND s.scope_key LIKE 'P:%' ORDER BY 1"""
                [ "@id", box id ]
                (fun r -> { Key = r.GetString 0; Symbols = r.GetInt32 1; Inputs = r.GetInt32 2; LinkedTests = r.GetInt32 3 })

        let reasons =
            rows
                "SELECT incomplete_reasons FROM trace_tests WHERE trace_run_id = @id AND complete = 0"
                [ "@id", box id ]
                (fun r -> JsonSerializer.Deserialize<string list>(r.GetString 0))
            |> List.concat
            |> List.map (fun code -> match code.IndexOf ':' with -1 -> code | i -> code.Substring(0, i))
            |> List.countBy id
            |> Map.ofList

        { TestProject = project
          RunId = run
          Executed = st.GetProperty("executed").GetInt32()
          Traced = st.GetProperty("traced").GetInt32()
          Complete = st.GetProperty("complete").GetInt32()
          TotalHits = total
          AmbientHits = counters.GetProperty("Ambient").GetInt64()
          AmbientSymbols = scopeSymbols "A:ambient"
          ReasonCounts = reasons
          Pools = pools })

let render (cs: ProjectCensus list) : string =
    let sb = System.Text.StringBuilder()

    for c in cs do
        sb.AppendLine($"%s{c.TestProject}  run %s{c.RunId}") |> ignore
        sb.AppendLine($"  traced   %d{c.Traced}/%d{c.Executed} (%.4f{tracedRatio c})  complete %d{c.Complete}") |> ignore
        sb.AppendLine($"  ambient  %d{c.AmbientHits}/%d{c.TotalHits} (%.5f{ambientRatio c})") |> ignore
        for s in c.AmbientSymbols do sb.AppendLine($"    ambient symbol: %s{s}") |> ignore
        for KeyValue(r, n) in c.ReasonCounts do sb.AppendLine($"  incomplete %-28s{r} %d{n}") |> ignore
        for p in c.Pools do sb.AppendLine($"  pool %s{p.Key}: %d{p.Symbols} symbols, %d{p.Inputs} inputs, %d{p.LinkedTests} tests") |> ignore

    sb.ToString()
```

Task 9 serialises `counters` with `JsonSerializer.Serialize` of the F# record, so the property names are the
record labels (`Test`, `Ambient`, …). The census reads exactly those names.

`src/TestPrune.Trace.Cli/Program.fs`:

```fsharp
module TestPrune.Trace.Cli.Program

open System
open System.IO
open System.Text.Json
open TestPrune.Trace

let private defaultDb () =
    let fshw = Path.Combine(Environment.CurrentDirectory, ".fshw", "test-traces.db")
    if File.Exists fshw then fshw else Path.Combine(Environment.CurrentDirectory, ".test-prune-traces.db")

let rec private flag (name: string) (args: string list) =
    match args with
    | n :: v :: _ when n = name -> Some v
    | _ :: rest -> flag name rest
    | [] -> None

let private census (args: string list) =
    let db = flag "--db" args |> Option.defaultWith defaultDb
    let cs = Census.latest db (flag "--run" args)
    if List.contains "--json" args then printfn "%s" (JsonSerializer.Serialize cs) else printf "%s" (Census.render cs)
    if cs |> List.forall (Census.passes Census.defaultBars) then 0 else 1

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "census" :: rest -> census rest
    | _ ->
        eprintfn "usage: test-prune-traces census|audit|overhead|file-census [options]"
        2
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.CensusTests`
Expected: PASS.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t11-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj commit -m "test-prune-traces census: traced share, unattributed hits and pool scopes

Reads a recorded run back out of the trace store and checks it against the
phase-1 bars: at least 99% of executed tests traced, and unattributed hits
under 0.1% or listed symbol by symbol for explanation. Pool scopes are
tabulated with the tests that inherit them.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 12: Process-driving measurements: isolation audit, CPU overhead, file census

**Files:**

- Create: `src/TestPrune.Trace/Rusage.fs`, `Audit.fs`, `Overhead.fs`, `FileCensus.fs` (after `Census.fs`)
- Modify: `src/TestPrune.Trace.Cli/Program.fs` (three verbs)
- Test: `tests/TestPrune.Trace.Tests/MeasurementTests.fs` (runs against the FxTests fixture)

**Interfaces:**

- Consumes: `TraceSession.prepareProject`, `Launch.run`, `DumpReader`, `Ctrf.parse`, `Manifest` (Tasks 2–10).
- Produces:

```fsharp
module TestPrune.Trace.Rusage
/// user+sys CPU of terminated, waited-for descendants so far, and their max RSS in bytes.
val children: unit -> struct (System.TimeSpan * int64)

module TestPrune.Trace.Audit
type TestAudit = { Display: string; Extra: int list; MissingByKind: Map<string, int>; MissingUser: string list }
type AuditReport = { Sampled: int; ExtraTotal: int; Tests: TestAudit list }
/// Run the woven project once in parallel, then `sample` of its tests one at a time
/// (`--filter-display-name`), and compare each test's OWN scope ids.
val run: TraceSession.PrepareRequest -> appArgs: string list -> sample: float -> seed: int -> timeout: System.TimeSpan -> AuditReport
val passes: AuditReport -> bool     // ExtraTotal = 0 (MissingUser is reported for review, not failed)

module TestPrune.Trace.Overhead
type Sample = { Traced: bool; Cpu: System.TimeSpan; MaxRssBytes: int64; ExitCode: int }
type OverheadReport = { Samples: Sample list; BaseMedianCpu: System.TimeSpan; TracedMedianCpu: System.TimeSpan; Ratio: float; TracedMaxRssBytes: int64 }
val run: TraceSession.PrepareRequest -> appArgs: string list -> reps: int -> timeout: System.TimeSpan -> OverheadReport
val passes: OverheadReport -> bool  // Ratio <= 1.15

module TestPrune.Trace.FileCensus
type FileCensusReport =
    { FailOutside: Set<string>; ReadsRepo: Set<string>; SymmetricDifference: Set<string>; Ratio: float }
val run: TraceSession.PrepareRequest -> appArgs: string list -> timeout: System.TimeSpan -> FileCensusReport
val passes: FileCensusReport -> bool  // Ratio <= 0.05
```

**Procedures:**

- **Audit.**
  1. `prepareProject`, then run the shadow apphost once with the app args. This is the parallel run.
  2. Collect each T scope's display name and ids from its dumps.
  3. Choose `max 1 (ceil (n × sample))` displays with `Random(seed)` over the sorted list.
  4. For each chosen display, run the apphost alone with `--filter-display-name "<display>"` and a fresh dump
     directory (a new `TESTPRUNE_TRACE_OUT`).
  5. Compare the chosen test's own T-scope ids:
     - `Extra` = parallel − isolated. The bar is empty.
     - `Missing` = isolated − parallel, grouped by the manifest row kind (`cctor`, `gen`, `user`, `case`,
       `type`). `MissingUser` lists the `type::member` of every non-`cctor`/`gen` id.
  6. The verb prints every `MissingUser` entry. D1 records each one as either explained (memoised lazy,
     static-init helper) or a defect.
- **Overhead.**
  1. `reps` iterations, interleaved: base, then traced.
  2. Base runs the **original** apphost (`bin/Debug/<tfm>/<Asm>`) with no trace environment. Traced runs the
     shadow apphost with `Env`.
  3. Wrap each launch in `Rusage.children()` before and after; the deltas are that run's CPU and max RSS.
  4. Report the medians and `Ratio = traced median / base median`.

  This measures CPU, not wall time, by design (Decision D6).
- **File census** (self-contained, like the audit; it needs no trace store):
  1. `prepareProject`, then run the shadow apphost in the repository with CTRF. `ReadsRepo` = the tests whose
     own or inherited scopes (from the dumps) hold any file-read, existence-probe or directory-listing input.
     `FailInRepo` = the tests that failed in this run.
  2. Copy (not link) the original `bin/Debug/<tfm>/` to a directory under `Path.GetTempPath()`, outside the
     repository, and run it untraced with CTRF. `FailOutside` = its failures minus `FailInRepo`.
  3. Compare on normalised `Class.Method`: `+` → `.`, and theory arguments stripped at the first `(`.
  4. `Ratio = |FailOutside Δ ReadsRepo| / |FailOutside ∪ ReadsRepo|`, which is 0 when both sets are empty.

`Rusage.fs`:

```fsharp
module TestPrune.Trace.Rusage

open System
open System.Runtime.InteropServices

[<DllImport("libc", SetLastError = true)>]
extern int getrusage(int who, byte[] usage)

/// RUSAGE_CHILDREN (-1): terminated and waited-for descendants. Layout (macOS and
/// Linux, 64-bit): ru_utime {int64 sec; usec in the low 32 bits of the next 8 bytes},
/// ru_stime likewise, then ru_maxrss (int64: bytes on macOS, KiB on Linux).
let children () : struct (TimeSpan * int64) =
    let buf = Array.zeroCreate<byte> 256
    if getrusage (-1, buf) <> 0 then failwith "getrusage failed"
    let tv (off: int) = TimeSpan.FromSeconds(float (BitConverter.ToInt64(buf, off))) + TimeSpan.FromMicroseconds(float (BitConverter.ToInt32(buf, off + 8)))
    let rss = BitConverter.ToInt64(buf, 32)
    struct (tv 0 + tv 16, (if OperatingSystem.IsMacOS() then rss else rss * 1024L))
```

- [ ] **Step 1: Write the failing tests**

`tests/TestPrune.Trace.Tests/MeasurementTests.fs`:

```fsharp
module TestPrune.Trace.Tests.MeasurementTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Tests

let private req () =
    { TraceSession.PrepareRequest.RepoRoot = Fixtures.repoRoot
      ProjectDir = Path.Combine(Fixtures.fixtureRoot, "tests", "FxTests")
      AssemblyName = "FxTests"; TestProject = "FxTests"; WeaveTests = SitesOnly
      RunDir = Directory.CreateTempSubdirectory().FullName; VerifyTimeout = TimeSpan.FromMinutes 2.0 }

[<Fact>]
let ``getrusage sees a child's CPU`` () =
    let struct (before, _) = Rusage.children ()
    Launch.run "/bin/sh" [ "-c"; "i=0; while [ $i -lt 200000 ]; do i=$((i+1)); done" ] [] "/" (TimeSpan.FromMinutes 1.0) |> ignore
    let struct (after, _) = Rusage.children ()
    test <@ after > before @>

[<Fact>]
let ``the isolation audit finds no id attributed in parallel that the test does not run alone`` () =
    let report = Audit.run (req ()) [] 1.0 7 (TimeSpan.FromMinutes 5.0)
    test <@ report.Sampled = 9 @>
    test <@ report.ExtraTotal = 0 @>
    test <@ Audit.passes report @>

[<Fact>]
let ``overhead reports medians and a ratio over interleaved runs`` () =
    let r = Overhead.run (req ()) [] 1 (TimeSpan.FromMinutes 5.0)
    test <@ r.Samples.Length = 2 && r.Samples |> List.forall (fun s -> s.ExitCode = 0) @>
    test <@ r.BaseMedianCpu > TimeSpan.Zero && r.Ratio > 0.0 @>

[<Fact>]
let ``the file census names the reading test and the tests that break outside the repository`` () =
    // FxTests' one repository read is guarded by TESTPRUNE_TRACE_REPO_ROOT: traced (in the
    // repository) it reads global.json; untraced outside the repository it skips the read
    // and passes. So the census must report the reader and no outside failures. This pins
    // both halves of the measurement, not a ratio the fixture cannot make meaningful.
    let r = FileCensus.run (req ()) [] (TimeSpan.FromMinutes 5.0)
    test <@ r.ReadsRepo = set [ "FxTests.AttributionTests.ClassC.c reads a repo file" ] @>
    test <@ r.FailOutside = Set.empty @>
```

The overhead test asserts shape, not a threshold: one fixture rep on a shared machine proves nothing about
overhead. The threshold is applied by the verb on real suites in D1.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build`
Expected: FAIL, with `Rusage`, `Audit`, `Overhead` and `FileCensus` undefined.

- [ ] **Step 3: Implement** each module exactly per the procedures above. Constraints on the code:

  - Use `Launch.run` for every process and `DumpReader.readDirectory` for every dump.
  - For the audit, keep the manifest row kind next to each id through `Manifest.read shadow.ManifestDir`.
  - The ingest-free audit compares **ids**, not symbols, because a join could hide an attribution error.
  - The file census resolves a T scope's inherited inputs through its `parents` and `links`, exactly as Task 9
    links scopes.
  - The census, audit and overhead parameters come from the verb's flags:

  ```text
  test-prune-traces audit       --project-dir <dir> --assembly <name> [--repo <root>] [--sample 0.01] [--seed 1] [--timeout-min 30] [-- <app args>]
  test-prune-traces overhead    --project-dir <dir> --assembly <name> [--repo <root>] [--reps 3] [--timeout-min 30] [-- <app args>]
  test-prune-traces file-census --project-dir <dir> --assembly <name> [--repo <root>] [--timeout-min 30] [-- <app args>]
  ```

  Each prints a report, writes it as JSON with `--json`, and exits 0 when `passes`, else 1.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.MeasurementTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/t12-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj commit -m "test-prune-traces audit, overhead and file-census

audit reruns a seeded sample of tests one at a time and compares each test's
own probe ids with the parallel run: any id attributed in parallel that the
test does not execute alone is contamination. overhead interleaves untraced
and traced runs and compares children's CPU from getrusage, not wall time.
file-census runs the untraced build from outside the repository and compares
the tests that break with the tests whose traces read repository files.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task 13: Documentation, decision records and the first release

**Files:**

- Create: `docs/adr/0005-recorded-traces-exit-dump.md`
- Create: `docs/adr/0006-trace-store-separate-file.md`
- Create: `docs/adr/0007-trace-packaging-and-release-tag.md`
- Create: `docs/adr/0008-trace-deferred-options.md`
- Modify: `README.md` (a "Recorded per-test traces (preview)" section pointing at `src/TestPrune.Trace/README.md`);
  `mise.toml` `[tasks.release]` (the `trace-v` level);
  `src/TestPrune.Trace/CHANGELOG.md`, `src/TestPrune.Trace.Recorder/CHANGELOG.md`, `src/TestPrune.Core/CHANGELOG.md`
- Test: none new. This task is documentation and release plumbing. `mise run sync-docs-check` and the existing
  `ReleaseEnrollmentTests`/`ReleaseOrchestrationTests` are its tests.

**Interfaces:** none.

- [ ] **Step 1: Run the release-enrollment tests to see what they require of a new package**

Run: `dotnet run --project tests/TestPrune.Tests --no-build -- --filter-class TestPrune.Tests.ReleaseEnrollmentTests --filter-class TestPrune.Tests.ReleaseOrchestrationTests`
Expected: FAIL. The new `trace-v` package is not enrolled in `mise.toml`'s release levels. Read the failure: it
names what enrollment requires.

- [ ] **Step 2: Enroll the package**

In `mise.toml` `[tasks.release]`, add `TestPrune.Trace.Recorder` to the second level, the one that already
releases `TestPrune.Falco,TestPrune.Sql`. `TestPrune.Trace` depends only on Core, and the tag's
`fsProjsSharingSameTag` carries `TestPrune.Trace` and `TestPrune.Trace.Cli`. Then run the tests from Step 1
again. Expected: PASS.

- [ ] **Step 3: Write the ADRs** in the repository's minimal ADR format (context, decision, consequences; one
  paragraph each):

  - **0005, exit dump:**
    - no per-test flush hook without a compile-time reference in every test project (xUnit v3 publishes
      `TestState` only on a new context object, and MTP registers extensions at build time);
    - an exit dump over-attributes late continuations, which is sound;
    - a killed process leaves no dump, so its tests keep no trace (R1);
    - memory is O(tests × ids / 64). Two-level sparse bitsets are the documented fallback if D1's maximum RSS
      bites.
  - **0006, separate store:** core deletes the index on every `SchemaVersion` bump, and plugin-store tables die
    with it. Traces cost a full run to rebuild. So the store is a separate file with forward-only migrations,
    and it is refused (never deleted) when newer.
  - **0007, packaging:**
    - the recorder is in F#, net8.0, and pins FSharp.Core 8.0.403;
    - one `trace-v` tag covers the recorder, the tooling and the CLI, because the weaver resolves recorder
      methods by name and the two must never skew;
    - the measurement verbs sit outside the core CLI to avoid a circular release order;
    - the recorder is found through `typeof<Probes>.Assembly.Location`.
  - **0008, deferred or rejected** (one line each, with the measured reason):
    - the module-value `ldsfld` probe: 0 sites in Debug builds;
    - Release-build tracing: refused because of cross-assembly inlining;
    - the process-level `open()` interposer: deferred, file reads are captured at call sites instead, and the
      file census measures the gap;
    - profiler ReJIT weaving: deferred; it collides with the coverage profiler slot;
    - per-field type ids: deferred to after the Type-hash split;
    - DB table tracing: phase 4;
    - `[<ExcludeFromCodeCoverage>]` on the recorder: rejected, because it hides the recorder from this
      repository's own ratchet.

- [ ] **Step 4: Verify the docs gates**

Run: `mise run sync-docs-check > /tmp/t13-docs.log 2>&1; echo "exit=$?"`, then `mise run ci > /tmp/t13-gate.log 2>&1; echo "exit=$?"`.
Expected: exit 0 for both, and a current verdict.

- [ ] **Step 5: Commit**

```bash
jj commit -m "Document recorded per-test traces and enroll the trace-v release

Decision records for the exit-time dump, the separate trace store, packaging
and the release tag, and the options deferred or rejected with the measurements
behind them. The recorder, tooling and census CLI release together under
trace-v, after core.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

**Release** is a maintainer step after the stack lands on `main`: `mise run release`. Record the exact
published `trace-v` version; Task F4 pins it.

---

# FsHotWatch tasks

These tasks run in `~/Developer/opensource/FsHotWatch`, in their own jj workspace
(`jj workspace add .workspaces/<name> -r main@origin`).

- Inner loop: `mise run compile`, then
  `dotnet run --project tests/FsHotWatch.Tests --no-build -- --filter-class <Class>`.
- Gate: `mise run ci`, reading the verdict file as well as the exit code.

F1–F3 do not depend on TestPrune.Trace and can start as soon as Task 1 fixes the contracts. F4 needs the
released `trace-v` package.

## Task F1: Parse `tests.traces` and the per-project opt-out

**Files:**

- Create: `src/FsHotWatch.TestPrune/Traces.fs` (settings types only in this task; compile after `TestMode.fs`)
- Modify: `src/FsHotWatch.TestPrune/FsHotWatch.TestPrune.fsproj` (compile item)
- Modify: `src/FsHotWatch.Cli/DaemonConfig.fs`:
  - `TestProjectConfig` gains `Traces: bool`;
  - the `Tests` record gains `Traces: FsHotWatch.TestPrune.Traces.TraceSettings option`;
  - both are parsed inside `parseConfig`, beside the per-project `coverage` block.
- Test: `tests/FsHotWatch.Tests/DaemonConfigTests.fs` (append)

**Interfaces:**

- Produces (`FsHotWatch.TestPrune.Traces`):

```fsharp
namespace FsHotWatch.TestPrune

type TraceRecordPolicy =
    | RecordOff
    | RecordFullRuns
    | RecordEveryRun

type TraceWeaveTests =
    | WeaveTestSites
    | WeaveTestFull

type TraceSettings =
    { Record: TraceRecordPolicy
      /// Repo-relative as written in .fshw.json; resolved against the repo root at registration.
      DbPath: string
      WeaveTests: TraceWeaveTests
      FingerprintInputs: string list
      FingerprintEnv: string list
      VerifyTimeoutSec: int }

module TraceSettings =
    let defaultDbPath = ".fshw/test-traces.db"
    /// Parse the `record` value; unknown text is `None` (the caller warns and treats it as off).
    val parseRecord: string -> TraceRecordPolicy option
```

- [ ] **Step 1: Write the failing tests** (append to `DaemonConfigTests.fs`):

```fsharp
// --- parseConfig: tests.traces ---

[<Fact>]
let ``parseConfig without tests.traces records nothing`` () =
    let config = parseConfig """{"tests": {"projects": []}}""" defaults
    test <@ config.Tests.Value.Traces = None @>

[<Fact>]
let ``parseConfig tests.traces full-runs with defaults`` () =
    let config = parseConfig """{"tests": {"traces": {"record": "full-runs"}, "projects": []}}""" defaults
    let t = config.Tests.Value.Traces.Value
    test <@ t.Record = RecordFullRuns && t.DbPath = ".fshw/test-traces.db" && t.WeaveTests = WeaveTestSites @>
    test <@ t.FingerprintInputs = [] && t.FingerprintEnv = [] && t.VerifyTimeoutSec = 300 @>

[<Fact>]
let ``parseConfig tests.traces reads every field`` () =
    let json =
        """{"tests": {"traces": {"record": "every-run", "db": "x/t.db", "weaveTests": "full",
            "fingerprintInputs": ["global.json"], "fingerprintEnv": ["APP_ENV"], "verifyTimeoutSec": 60}, "projects": []}}"""

    let t = (parseConfig json defaults).Tests.Value.Traces.Value
    test <@ (t.Record, t.DbPath, t.WeaveTests) = (RecordEveryRun, "x/t.db", WeaveTestFull) @>
    test <@ (t.FingerprintInputs, t.FingerprintEnv, t.VerifyTimeoutSec) = ([ "global.json" ], [ "APP_ENV" ], 60) @>

[<Fact>]
let ``parseConfig an unknown record value is off, not an error`` () =
    let t = (parseConfig """{"tests": {"traces": {"record": "sometimes"}, "projects": []}}""" defaults).Tests.Value.Traces.Value
    test <@ t.Record = RecordOff @>

[<Fact>]
let ``parseConfig a project opts out with traces false; the default is in`` () =
    let json =
        """{"tests": {"projects": [
            {"project": "A", "command": "dotnet", "args": "run --project tests/A --no-build"},
            {"project": "B", "command": "dotnet", "args": "run --project tests/B --no-build", "traces": false}]}}"""

    let ps = (parseConfig json defaults).Tests.Value.Projects
    test <@ ps |> List.map (fun p -> p.Project, p.Traces) = [ "A", true; "B", false ] @>
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `mise run compile`
Expected: FAIL, with `Traces`/`RecordFullRuns` undefined and `TestProjectConfig` having no `Traces` field.

- [ ] **Step 3: Implement.** `Traces.fs`:

```fsharp
namespace FsHotWatch.TestPrune

type TraceRecordPolicy =
    | RecordOff
    | RecordFullRuns
    | RecordEveryRun

type TraceWeaveTests =
    | WeaveTestSites
    | WeaveTestFull

type TraceSettings =
    { Record: TraceRecordPolicy
      DbPath: string
      WeaveTests: TraceWeaveTests
      FingerprintInputs: string list
      FingerprintEnv: string list
      VerifyTimeoutSec: int }

module TraceSettings =
    let defaultDbPath = ".fshw/test-traces.db"

    let parseRecord (s: string) =
        match s.ToLowerInvariant() with
        | "off" -> Some RecordOff
        | "full-runs" -> Some RecordFullRuns
        | "every-run" -> Some RecordEveryRun
        | _ -> None
```

In `DaemonConfig.parseConfig`:

- In the per-project loop, next to `timeoutSec`, add:

  ```fsharp
  let traces =
      match p.TryGetProperty("traces") with
      | true, v when v.ValueKind = JsonValueKind.False -> false
      | _ -> true
  ```

  Set `Traces = traces` in the `TestProjectConfig` literal.
- Where the `Tests` anonymous record is built (the literal that sets `CoverageDir = coverageDir`), add
  `Traces = traces`, computed from the `tests` element:

  ```fsharp
  let traces =
      match testsElement.TryGetProperty("traces") with
      | true, t when t.ValueKind = JsonValueKind.Object ->
          let str name fallback = match t.TryGetProperty(name: string) with | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() | _ -> fallback
          let strs name = match t.TryGetProperty(name: string) with | true, v when v.ValueKind = JsonValueKind.Array -> [ for x in v.EnumerateArray() -> x.GetString() ] | _ -> []
          let record =
              match FsHotWatch.TestPrune.TraceSettings.parseRecord (str "record" "off") with
              | Some r -> r
              | None ->
                  Logging.warn "config" $"Unknown tests.traces.record value '%s{str "record" ""}', recording is off"
                  FsHotWatch.TestPrune.RecordOff
          Some
              { FsHotWatch.TestPrune.TraceSettings.Record = record
                DbPath = str "db" FsHotWatch.TestPrune.TraceSettings.defaultDbPath
                WeaveTests = (if str "weaveTests" "sites" = "full" then FsHotWatch.TestPrune.WeaveTestFull else FsHotWatch.TestPrune.WeaveTestSites)
                FingerprintInputs = strs "fingerprintInputs"
                FingerprintEnv = strs "fingerprintEnv"
                VerifyTimeoutSec = (match t.TryGetProperty "verifyTimeoutSec" with | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() | _ -> 300) }
      | _ -> None
  ```

  Use the variable name `parseConfig` already gives the `tests` JSON element in place of `testsElement`.
- Every other construction of `TestProjectConfig` and of the `Tests` record in `src/` and `tests/` gains
  `Traces = true` or `Traces = None`. The compiler lists each one.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `mise run compile && dotnet run --project tests/FsHotWatch.Tests --no-build -- --filter-class FsHotWatch.Tests.DaemonConfigTests`
Expected: PASS, all tests (5 new).

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/f1-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj commit -m "Config: tests.traces and a per-project traces opt-out

Parsed and carried, not yet acted on. Absent means off; an unknown record
value warns and is off; every project participates unless it says
traces: false.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task F2: `TestMode.recordsTraces`, the one place that decides when to record

**Files:**

- Modify: `src/FsHotWatch.TestPrune/TestMode.fs` (it must compile after `Traces.fs`: move `Traces.fs` before
  `TestMode.fs` in the `.fsproj`)
- Test: `tests/FsHotWatch.Tests/TestModeSeamTests.fs` (append)

**Interfaces:**

- Consumes: `TraceRecordPolicy` (F1).
- Produces: `TestMode.recordsTraces: TraceRecordPolicy -> TestMode -> bool`.

- [ ] **Step 1: Write the failing test** (append):

```fsharp
[<Theory>]
[<InlineData("off", false, false)>]
[<InlineData("full-runs", false, true)>]
[<InlineData("every-run", true, true)>]
let ``recordsTraces follows the policy per mode`` (policy: string, underCheck: bool, underConfirm: bool) =
    let p = (FsHotWatch.TestPrune.TraceSettings.parseRecord policy).Value
    test <@ FsHotWatch.TestPrune.TestMode.recordsTraces p FsHotWatch.TestPrune.ImpactSelection = underCheck @>
    test <@ FsHotWatch.TestPrune.TestMode.recordsTraces p FsHotWatch.TestPrune.PassThrough = underConfirm @>
```

- [ ] **Step 2: Run it to verify it fails**

Run: `mise run compile`
Expected: FAIL, with `recordsTraces` undefined.

- [ ] **Step 3: Implement.** Append to the `TestMode` module:

```fsharp
    /// Whether a run launched under `mode` records per-test traces. `full-runs` records only
    /// where every project runs in full (confirm/nightly); `every-run` also records the
    /// impact-selected subset, which refreshes exactly the traces of the tests it ran.
    let recordsTraces (policy: TraceRecordPolicy) (mode: TestMode) =
        match policy with
        | RecordOff -> false
        | RecordEveryRun -> true
        | RecordFullRuns -> requestsFullSuite mode
```

- [ ] **Step 4: Run it to verify it passes**

Run: `mise run compile && dotnet run --project tests/FsHotWatch.Tests --no-build -- --filter-class FsHotWatch.Tests.TestModeSeamTests`
Expected: PASS. The existing seam scan also passes, because the new branch is inside `TestMode.fs`.

- [ ] **Step 5: Gate and commit**

```bash
jj commit -m "TestMode.recordsTraces: when a run records per-test traces

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task F3: The traced launch spec (pure)

**Files:**

- Modify: `src/FsHotWatch.TestPrune/Traces.fs` (add the `TracedLaunch` module)
- Test: `tests/FsHotWatch.Tests/TracedLaunchTests.fs` (new; add it to the test `.fsproj`)

**Interfaces:**

- Produces:

```fsharp
module TracedLaunch =
    /// The app arguments a `dotnet run …` config line passes to the app, or why they cannot be derived.
    val appArgs: configCommand: string -> configArgs: string -> extraArgs: string list -> Result<string list, string>
```

**Rules:**

- The config command must be `dotnet` and the first token `run`.
- Dropped: `run`; `--project`/`-p` and their value; `--no-build`; `--no-restore`; `-c`/`--configuration`
  and their value; `-f`/`--framework` and their value; every standalone `--` token.
- Any other token before the first `--` → `Error "unrecognized-run-option:<token>"`.
- Tokens after a `--`, then every token of each `extraArgs` element (split with `ProcessHelper.splitArgs`,
  and again without standalone `--`), are kept in order.

- [ ] **Step 1: Write the failing tests**

```fsharp
module FsHotWatch.Tests.TracedLaunchTests

open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune

[<Fact>]
let ``a dotnet run line becomes the app's own arguments`` () =
    test
        <@ TracedLaunch.appArgs "dotnet" "run --project tests/X --no-build" [ "-- --filter-class A B"; "--report-xunit-ctrf --results-directory \"/r d\"" ] =
            Ok [ "--filter-class"; "A"; "B"; "--report-xunit-ctrf"; "--results-directory"; "/r d" ] @>

[<Fact>]
let ``a trailing separator in the config line is dropped`` () =
    test <@ TracedLaunch.appArgs "dotnet" "run --project tests/X --no-build --" [ "--filter-class A" ] = Ok [ "--filter-class"; "A" ] @>

[<Fact>]
let ``configuration and framework options are consumed with their values`` () =
    test <@ TracedLaunch.appArgs "dotnet" "run -c Debug -f net10.0 -p tests/X --no-restore --no-build" [] = Ok [] @>

[<Fact>]
let ``an unknown run option refuses the traced launch`` () =
    test <@ TracedLaunch.appArgs "dotnet" "run --project tests/X --launch-profile p" [] = Error "unrecognized-run-option:--launch-profile" @>

[<Fact>]
let ``a non-dotnet-run command refuses the traced launch`` () =
    test <@ TracedLaunch.appArgs "./run-tests.sh" "" [] |> Result.isError @>
```

- [ ] **Step 2: Run them to verify they fail**

Run: `mise run compile`
Expected: FAIL, with `TracedLaunch` undefined.

- [ ] **Step 3: Implement** in `Traces.fs`:

```fsharp
module TracedLaunch =
    let private tokens (s: string) = FsHotWatch.ProcessHelper.splitArgs s |> Option.defaultValue [||] |> List.ofArray
    let private withValue = set [ "--project"; "-p"; "-c"; "--configuration"; "-f"; "--framework" ]
    let private flags = set [ "--no-build"; "--no-restore" ]

    let appArgs (configCommand: string) (configArgs: string) (extraArgs: string list) : Result<string list, string> =
        let rec consume (ts: string list) (acc: string list) =
            match ts with
            | [] -> Ok(List.rev acc)
            | "--" :: rest -> Ok(List.rev acc @ (rest |> List.filter ((<>) "--")))
            | t :: _ :: rest when withValue.Contains t -> consume rest acc
            | t :: rest when flags.Contains t -> consume rest acc
            | t :: _ -> Error $"unrecognized-run-option:%s{t}"

        match configCommand, tokens configArgs with
        | "dotnet", "run" :: rest ->
            consume rest []
            |> Result.map (fun head -> head @ (extraArgs |> List.collect tokens |> List.filter ((<>) "--")))
        | _ -> Error "not-a-dotnet-run-command"
```

- [ ] **Step 4: Run them to verify they pass**

Run: `mise run compile && dotnet run --project tests/FsHotWatch.Tests --no-build -- --filter-class FsHotWatch.Tests.TracedLaunchTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Gate and commit**

```bash
jj commit -m "Derive a traced launch's app arguments from its dotnet run line

A traced project runs its woven apphost directly, so the dotnet run options
are consumed and the app's own arguments kept; an option this does not
understand refuses the traced launch rather than guessing.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

## Task F4: Record traces in the test run (wiring, failure handling, status)

**Files:**

- Modify: `src/FsHotWatch.TestPrune/FsHotWatch.TestPrune.fsproj`: add
  `<PackageReference Include="TestPrune.Trace" Version="<released trace-v version from Task 13>" />` and bump
  `TestPrune.Core` to the Core release that carries the public `canonicalShortName` (Task 7) and the Type-hash
  split (Task 0).
- Modify: `src/FsHotWatch.TestPrune/Traces.fs` (the `TraceRun` glue below)
- Modify: `src/FsHotWatch.TestPrune/TestPrunePlugin.fs`:
  - `executeTests` gains two parameters: `traces: Traces.TraceRuntime option` and `launchTreeHash: string`;
  - the per-config body calls `TraceRun.decide` before building `finalArgs`, and swaps the launch command and
    args when the decision is `Traced`;
  - the serial post-parallel section (where coverage ingest runs) calls `TraceRun.ingestAll`;
  - `createWithScope`, `createWithLaunchDeadline` and `createWithQueries` thread a
    `traces: TraceSettings option` through to `executeTests`.
- Modify: `src/FsHotWatch.Cli/DaemonConfig.fs`: pass `t.Traces` (with `DbPath` resolved against `repoRoot`) and
  the set of projects with `Traces = false` into `createWithScope`.
- Test: `tests/FsHotWatch.Tests/TestPruneTracesTests.fs` (new)

**Interfaces:**

- Consumes: `TraceSession.prepareProject`, `ingestProject`, `recordRefusal`; `Ctrf.parse`;
  `TraceStore.Store.Open` (TestPrune.Trace); `TestMode.recordsTraces` (F2); `TracedLaunch.appArgs` (F3);
  `ReceiptInputTree.read` (existing); `deriveProjectBin` (existing).
- Produces:

```fsharp
type TraceRuntime = { Settings: TraceSettings; RepoRoot: string; ExcludedProjects: Set<string>; Mode: TestMode }

type TraceDecision =
    | Untraced of reason: string option      // None: tracing does not apply to this run; Some: refused, logged
    | Traced of command: string * args: string * env: (string * string) list * launch: TestPrune.Trace.TraceSession.TraceLaunch

/// What `decide` needs from the plugin's TestConfig. Traces.fs compiles BEFORE
/// TestPrunePlugin.fs (TestMode.fs depends on it), so it cannot name TestConfig itself.
type TraceProject =
    { Project: string
      Command: string
      Args: string
      Environment: (string * string) list
      Target: ArtifactFreshness.RunnerTarget option }   // the caller passes deriveProjectBin config.Args repoRoot

module TraceRun =
    /// For one project about to launch: trace it or not, and how.
    val decide: rt: TraceRuntime -> project: TraceProject -> runDir: string -> extraArgs: string list -> TraceDecision
    /// After the parallel section: ingest every Traced project, or record its refusal. Never throws.
    val ingestAll:
        rt: TraceRuntime -> db: TestPrune.Database.Database -> runId: System.Guid -> launchTreeHash: string ->
        currentTreeHash: (unit -> string) ->
        decisions: (string * TraceDecision * string option (* ctrf path *)) list -> log: (string -> unit) -> unit
```

`decide`:

1. Returns `Untraced None` unless `TestMode.recordsTraces rt.Settings.Record rt.Mode` holds and the project is
   not excluded.
2. With `project.Target = None` it returns `Untraced (Some "no-derivable-project")`.
3. It calls `prepareProject` with `ProjectDir = target.ProjectDir` and `AssemblyName = target.AssemblyName`.
   An `Error e` becomes `Untraced (Some e)`.
4. It calls `TracedLaunch.appArgs project.Command project.Args extraArgs`. An `Error e` becomes
   `Untraced (Some e)`.
5. Otherwise it returns `Traced(launch.Apphost, <args joined with ProcessHelper quoting>, project.Environment @ launch.Env, launch)`.

`ingestAll`:

1. Opens the store once.
2. For each decision:
   - `Traced` → `ingestProject` with:
     - `Outcomes = Ctrf.parse` of the project's CTRF file, or `[]` if it is unreadable;
     - `Kind = FullRun` when the mode requests the full suite, else `PartialRun`;
     - `LaunchTreeHash = launchTreeHash`;
     - `CurrentTreeHash = currentTreeHash ()`. The caller passes
       `fun () -> ReceiptInputTree.read repoRoot |> Option.defaultValue ""`, because `ReceiptInputTree` lives in
       the plugin module;
     - `Symbols = Ports.toSymbolStore db`;
     - the settings' fingerprint inputs.
   - `Untraced (Some reason)` → `recordRefusal`.
3. Logs one line per project:
   - `traces: <project> <traced>/<executed> traced, <complete> complete`, or
   - `traces: <project> not recorded — <reason>`.
4. Catches every exception into a log line and a `failed` run row. **A trace failure never changes a test
   result, the verdict, or the run's lifecycle.**

- [ ] **Step 1: Write the failing tests**

`tests/FsHotWatch.Tests/TestPruneTracesTests.fs` covers the decisions and the refusal path without building
anything. The traced launch itself is covered end to end by the integration test described after the block.

```fsharp
module FsHotWatch.Tests.TestPruneTracesTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune

let private settings record =
    { Record = record; DbPath = ".fshw/test-traces.db"; WeaveTests = WeaveTestSites
      FingerprintInputs = []; FingerprintEnv = []; VerifyTimeoutSec = 120 }

let private config (root: string) project : TraceProject =
    { Project = project; Command = "dotnet"; Args = $"run --project tests/%s{project} --no-build"; Environment = []
      Target =
        Some
            { ArtifactFreshness.ProjectFile = None
              ArtifactFreshness.ProjectDir = Path.Combine(root, "tests", project)
              ArtifactFreshness.AssemblyName = project
              ArtifactFreshness.BinDir = Path.Combine(root, "tests", project, "bin", "Debug") } }

[<Fact>]
let ``check under full-runs does not trace`` () =
    let rt = { Settings = settings RecordFullRuns; RepoRoot = "/nowhere"; ExcludedProjects = Set.empty; Mode = ImpactSelection }
    test <@ TraceRun.decide rt (config "/nowhere" "T") "/tmp/run" [] = Untraced None @>

[<Fact>]
let ``an excluded project does not trace`` () =
    let rt = { Settings = settings RecordEveryRun; RepoRoot = "/nowhere"; ExcludedProjects = set [ "T" ]; Mode = PassThrough }
    test <@ TraceRun.decide rt (config "/nowhere" "T") "/tmp/run" [] = Untraced None @>

[<Fact>]
let ``a project with no build output is refused with a reason, and so runs untraced`` () =
    let root = Directory.CreateTempSubdirectory().FullName
    let rt = { Settings = settings RecordEveryRun; RepoRoot = root; ExcludedProjects = Set.empty; Mode = PassThrough }
    match TraceRun.decide rt (config root "T") "/tmp/run" [] with
    | Untraced(Some reason) -> test <@ reason.Contains "no build output" @>
    | other -> failwith $"%A{other}"

[<Fact>]
let ``ingestAll records a refusal and never throws on a missing CTRF`` () =
    let root = Directory.CreateTempSubdirectory().FullName
    let rt = { Settings = settings RecordEveryRun; RepoRoot = root; ExcludedProjects = Set.empty; Mode = PassThrough }
    let db = TestPrune.Database.Database.create (Path.Combine(root, "i.db"))
    let lines = ResizeArray()
    TraceRun.ingestAll rt db (Guid.NewGuid()) "t" (fun () -> "t") [ "T", Untraced(Some "no build output"), None ] lines.Add
    test <@ lines |> Seq.exists (fun l -> l.Contains "T not recorded") @>
    use store = TestPrune.Trace.TraceStore.Store.Open(Path.Combine(root, ".fshw", "test-traces.db"))
    test <@ (store.Runs "T" |> List.head).Status = TestPrune.Trace.TraceStore.Refused @>
```

End-to-end coverage of the traced launch through the real plugin belongs in
`tests/FsHotWatch.IntegrationTests`. Its scratch repository is a temp directory holding:

- `src/L/L.fsproj` (one function);
- `tests/T/T.fsproj` (xUnit v3, one `[<Fact>]` calling it);
- a `.fshw.json` with `tests.traces.record = "full-runs"`.

It is built once with `dotnet build` in the fixture's constructor. Add one test there:

- it runs the daemon in `confirm` mode on the scratch repository above;
- it asserts that `.fshw/test-traces.db` has a `recorded` run with one complete trace;
- it asserts that the verdict equals the verdict of the same run with `tests.traces` removed.

- [ ] **Step 2: Run them to verify they fail**

Run: `mise run compile`
Expected: FAIL, with `TraceRuntime`, `TraceDecision` and `TraceRun` undefined.

- [ ] **Step 3: Implement** `TraceRuntime`, `TraceDecision` and `TraceRun` in `Traces.fs` per the interface and
the numbered rules above. Then wire them in `TestPrunePlugin.executeTests`:

  - **Launch:** immediately after `let finalArgs = …` and before the `Logging.info "test-prune" $"Running: …"`
    line, add:

    ```fsharp
    let traceDecision =
        match traces with
        | Some rt ->
            TraceRun.decide
                rt
                { Project = config.Project; Command = config.Command; Args = config.Args
                  Environment = config.Environment; Target = deriveProjectBin config.Args repoRoot }
                runDir
                (List.ofSeq extraArgs)
        | None -> Untraced None

    let launchCommand, launchArgs, launchEnv =
        match traceDecision with
        | Traced(cmd, args, env, _) -> cmd, args, env
        | Untraced _ -> config.Command, finalArgs, config.Environment
    ```

    Replace `config.Command finalArgs repoRoot config.Environment` in `runOnce`'s `runProcessTo` call with
    `launchCommand launchArgs repoRoot launchEnv`. Record `(config.Project, traceDecision, ctrfPath)` into a
    `traceDecisions` list guarded by a lock, like `coverageInputs`.
  - **Ingest:** in the serial section after the parallel groups complete (beside the coverage ingest that
    consumes `coverageInputs`), add:

    ```fsharp
    match traces with
    | Some rt when not traceDecisions.IsEmpty ->
        TraceRun.ingestAll rt db runId launchTreeHash (fun () -> ReceiptInputTree.read repoRoot |> Option.defaultValue "") traceDecisions logToCtx
    | _ -> ()
    ```

  - **Threading:** `createWithQueries` builds a `TraceRuntime` per launch from the `TraceSettings option`, with
    the launch's `Mode`. It passes `launch.InputTreeHash |> Option.defaultValue ""` as `launchTreeHash`, and
    passes both into `executeTests`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `mise run compile && dotnet run --project tests/FsHotWatch.Tests --no-build -- --filter-class FsHotWatch.Tests.TestPruneTracesTests --filter-class FsHotWatch.Tests.TestPrunePluginTests --filter-class FsHotWatch.Tests.TestPruneConfirmPassThroughTests`
Expected: PASS. The existing plugin and pass-through suites are unchanged, because `traces = None` everywhere
they construct the plugin.

- [ ] **Step 5: Gate and commit**

Run `mise run ci > /tmp/f4-gate.log 2>&1; echo "exit=$?"`, then read the log and the verdict.

```bash
jj commit -m "Record per-test traces during test runs when tests.traces asks for it

A traced project launches its woven apphost from bin/Traced with the trace
environment; everything else about the run is unchanged. After the parallel
section each traced project's dumps and CTRF outcomes are ingested into the
separate trace store. A refusal (no build output, a weave or JIT-verify
failure, an unknown run option) runs the project untraced and is recorded;
no trace failure can change a test result or the verdict.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

Release FsHotWatch (maintainer step) and record the CLI version that carries F4.

---

## Task F5 (phase 1b): Record on every run

Only after D1's bars hold on three consecutive full runs of each consumer.

**Files:**

- Modify: `src/FsHotWatch.TestPrune/Traces.fs`: `decide` passes `WeaveTests` through unchanged; no logic change
  is needed, because `recordsTraces RecordEveryRun` already returns true under `ImpactSelection`.
- Test: `tests/FsHotWatch.IntegrationTests`: an impact-selected `check` under `every-run` refreshes only the
  traces of the classes it ran. Assert that another class's `trace_tests.trace_run_id` is unchanged, and that
  the partial run's `CollectGarbage` deleted nothing outside what it ran.

- [ ] **Step 1: Write the failing integration test** (described above; same scratch repository as F4, with two
  test classes and an edit that selects one of them).
- [ ] **Step 2:** `mise run compile && dotnet run --project tests/FsHotWatch.IntegrationTests --no-build -- --filter-class <the new class>`
  Expected: FAIL only if partial ingestion touches unselected tests. If it passes at once, the behaviour was
  already right: keep the test as the regression guard and say so in the commit.
- [ ] **Step 3:** Fix any finding in `TraceIngest`/`TraceRun` (for example, never pass `Some liveKeys` for a
  partial run).
- [ ] **Step 4:** Re-run the test. Expected: PASS.
- [ ] **Step 5:** Gate, then commit
  `"Guard: an impact-selected run refreshes only the traces of the tests it ran"`, with the trailer.

---

# Done-bar

## Task D1: The phase-1 bar on TestPrune's own suite (dogfood)

This runs in TestPrune once FsHotWatch with F4 is released. TestPrune's runbook `fshotwatch-pin-upgrade.md`
fixes the order: release first, then bump the manifest pin and `ToolchainPinTests`' `InlineData`, then run the
full gate.

**Files:**

- Modify: `.config/dotnet-tools.json`, `tests/TestPrune.Tests/ToolchainPinTests.fs` (the pin bump)
- Modify: `.fshw.json` (add `tests.traces`: `{"record": "full-runs", "fingerprintInputs": ["global.json", "Directory.Packages.props"]}`)
- Create: `docs/plans/2026-09-25-recorded-per-test-traces-phase1-results.md` (the measurements)

- [ ] **Step 1: Bump the pin** exactly per the runbook. Commit that alone.
- [ ] **Step 2: Enable traces.** Add `tests.traces` to `.fshw.json`. Run `dotnet fshw confirm` three times on
  an unchanged tree. Traced runs are full runs, so `confirm` is the mode that records.
- [ ] **Step 3: Measure every bar** and paste the verb output into the results file:

  ```bash
  dotnet tool run test-prune-traces census > /tmp/d1-census.log 2>&1; echo "exit=$?"
  dotnet tool run test-prune-traces audit --project-dir tests/TestPrune.Tests --assembly TestPrune.Tests --sample 0.01 --seed 1 > /tmp/d1-audit.log 2>&1; echo "exit=$?"
  dotnet tool run test-prune-traces overhead --project-dir tests/TestPrune.Tests --assembly TestPrune.Tests --reps 3 > /tmp/d1-overhead.log 2>&1; echo "exit=$?"
  dotnet tool run test-prune-traces file-census --project-dir tests/TestPrune.Tests --assembly TestPrune.Tests > /tmp/d1-files.log 2>&1; echo "exit=$?"
  ```

  Run the same four commands for `tests/TestPrune.Trace.Tests`.

  | Bar | Pass when |
  |---|---|
  | census | ≥ 0.99 traced; ambient < 0.1 % or each listed symbol explained in the results file |
  | audit | `ExtraTotal = 0`; every `MissingUser` entry explained |
  | overhead | CPU ratio ≤ 1.15. Also record `TracedMaxRssBytes` for ADR 0005's memory decision. |
  | file-census | ratio ≤ 0.05. The spike saw at least 15 repo-probing tests in this suite; the census must find them. |
  | coverage parity | run `confirm` once more with `tests.traces` removed. The shared cobertura must be identical per file (lines and branches), and no `TestPrune.Trace.Recorder` package may appear. |
  | PDB / JIT | `prepare` refused nothing, verified from the census run's `trace_runs.status`. |

- [ ] **Step 4: Record the results** (tables, not prose) and any explained exceptions. File each
  unexplained item as a defect in the owning repository's tracker **before** declaring the bar met.
- [ ] **Step 5: Commit** `.fshw.json` and the results file:
  `"Record per-test traces on full runs of TestPrune's own suite"`, with the trailer.

The consumer's done-bar (pooled servers, browser tests, child processes) is in the separate consumer plan.

---

## Out of scope for phase 1 (recorded in ADR 0008)

- Any selection change: R1–R9 are phase 2, starting in shadow mode.
- Removing composition-root barriers or the emptied-project fail-safe (phase 3).
- Database table tracing through the Npgsql `ActivitySource` (phase 4).
- The process-level `open()` interposer; the file census measures what call-site capture misses.
- Tracing Release builds; profiler ReJIT weaving; per-field type ids; module-value `ldsfld` probes.

## Self-review against the brief

| Brief item | Where |
|---|---|
| Packaging of weaver, recorder, joiner; deps.json; xUnit v3/MTP attribution | Decision D1; Tasks 1, 2, 7, 8 |
| Shadow bin: hardlinked, under the project's `bin/Traced/`, build flow, invalidation | Decision D2; Task 8 |
| Storage that survives SchemaVersion bumps; the §4 tables; fingerprint E | Decision D3; Tasks 3, 9 |
| fshw traces mode, config shape, when it records, per-test ingestion, failure handling, incomplete marking | Decision D4; Tasks F1–F5, 9, 10 |
| Consumer: header, middleware, pool scope, child processes (separate file) | Decision D5; recorder side in Tasks 6 and 10; consumer plan |
| Done-bar as tests and measurements | Decision D6; Tasks 11, 12, D1; the consumer plan's D2 |
| Prerequisites: split Type hash (task 0), name-before-line join, CPU not wall | Task 0; Task 7 (test "same-file name match wins…"); Task 12 `Overhead` |
| Spike rules: the GO probe set; no `ldsfld` probe; refuse optimized; JIT-verify touched methods; SRM-decode woven PDBs; same run as coverage | Tasks 4, 5, 8; D1 coverage parity |
| Ordered, independently shippable behind flags, parallel groups marked | "Task graph and parallel groups" |
