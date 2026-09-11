# ADR-011: the analyzer cache key must include analyzer-assembly *content* identity

Status: Accepted (2026-06-17)

## Context

`AnalyzersPlugin` caches its per-file verdict (the diagnostics it reported for a
file) in the task cache, so a rescan that hasn't changed anything replays the
cached result instead of re-running the analyzers. The cache key (a content
merkle, ADR-008's principle applied to plugin caches) was:

```
(plugin-version, analyzer-paths, file, source, fcs-signature)
```

where `analyzer-paths` is a hash of the configured analyzer-path **strings**.

That key is blind to *which analyzers actually live at those paths*. The path
strings don't move when a custom-analyzer DLL is **rebuilt** — a rule changed, a
new analyzer added, a severity bumped — so every slot in the key is identical
across the rebuild for an unchanged source file. A long-lived daemon therefore
**replayed the pre-rebuild verdict**: the new/changed rule never re-ran on files
whose source bytes hadn't changed.

Observed downstream (thellma/intelligence): a file with a real, un-suppressed
custom-analyzer violation reported CLEAN on a long-lived daemon, while a fresh
workspace/daemon (empty cache) correctly flagged it — same source bytes, same
analyzer DLL on disk. A stale-green false negative on the gate: the worst
failure class for a correctness tool.

This is the same family as ADR-008 ("mtime is never a content oracle"). There,
mtime was a too-weak proxy for file content. Here, the analyzer **path string**
is a too-weak proxy for the analyzer **assembly content**: it never moves on a
rebuild, so it's strictly worse than mtime.

## Decision

**The analyzer cache key folds in the content identity of the loaded analyzer
assemblies, not just their path strings.** A new `analyzer-assemblies` merkle
slot is the SHA-256 over `(filename + SHA-256(bytes))` of every `*.dll` in each
configured path whose filename is **not** a known-non-analyzer prefix
(`isKnownNonAnalyzerPrefix`) — i.e. the same DLL set the loader inspects for
analyzers. A rebuilt analyzer DLL changes its bytes, which changes this slot,
which changes the key, which invalidates exactly the cached per-file verdicts
that the changed analyzer set could affect — and nothing else.

Implementation: `AnalyzersPlugin.analyzerAssemblyIdentity` (pure, `internal`,
unit-tested with throwaway byte files — no real SDK load). The `plugin-version`
slot is bumped `analyzers-merkle-v2 → v3` so every entry written under the old
path-only key is unconditionally non-matching after upgrade.

### Why exclude known-non-analyzer prefixes from the identity

Analyzer packages (e.g. `FSharpLintAnalyzerShim`) ship bundled BCL/FCS deps
(`FSharp.Core.dll`, `System.*.dll`, …). Those refresh for reasons unrelated to
the analyzer rules. Hashing the full bin dir would churn the key — and discard
the whole file-cache — on every transitive-dep bump. Restricting the identity to
the analyzer assemblies themselves (the set the loader reflects over) tracks the
signal (rules changed) without the noise (deps refreshed).

### Why content, not mtime

Per ADR-008: a rebuild that restores an old mtime (or a `cp -p`/`rsync -a` of a
prebuilt analyzer bin) would fool an mtime-keyed identity into replaying stale
verdicts. Content hashing is the only proxy that can't be fooled. Analyzer bin
dirs hold a handful of DLLs; the per-construction read cost is negligible next to
the FCS check the cache gates.

## Relationship to the per-path fail-loud guard

A *sibling* failure mode — a configured analyzer path that loads **zero**
analyzers (missing/empty bin from a build that failed or built the wrong
configuration) — is handled separately by `DaemonConfig.analyzerPathFailures`:
it raises `ConfigError`, which the `check --run-once` gate surfaces as a non-zero
(RED) exit naming the offending path, instead of silently registering an analyzer
plugin that finds nothing and passes green. That guard runs at plugin
registration, *before* any `FileChecked` event flows, so an incomplete/zero load
can never write a "clean" cache entry in the first place. The two guards compose:
the per-path guard prevents a zero-load from poisoning the cache; this ADR's
content identity prevents a *rebuilt* (non-zero) analyzer set from replaying a
stale verdict.

## Consequences

- A rebuilt or extended analyzer set re-runs on all previously-cached files on
  the next check; no daemon restart or `cache-clear` needed to pick up a rule
  change. Cold (fresh-workspace) and warm (long-lived-daemon) verdicts now agree.
- The cache still hits across unrelated rescans (identical analyzer bytes ⇒
  identical identity ⇒ same key), so the "rescans are mostly cache hits" property
  is preserved — it's now *correctly* conditioned on the analyzer set too.
- Future plugin caches that depend on a loaded plugin/assembly set must key on
  that set's content identity, the same way — path strings and versions are not
  enough.

## 2026-09-11: accepted input identity and successful-build provenance

The maintainer's 2026-09-10 decision supersedes this ADR's emitted-DLL cache key.
Package ID and version identify immutable package dependencies. First-party analyzer
identity comes from source/project inputs, local imports, effective compiler
options, and dependency identities. The compiler patch and PathMap workaround are
rejected. DLL debug padding is not a semantic input.

A current source hash alone cannot establish that those sources produced the DLL
being loaded. The SDK's `CoreCompileInputs.cache` hashes item specifications and
selected options before compilation; `CoreCompile` itself uses timestamp-based
incremental inputs/outputs. Neither is a content receipt for a successful output.
Portable PDB source checksums omit project/options provenance as well.

An opt-in producer target records evaluated inputs before compilation and includes
that snapshot in incremental compiler inputs. The F# compiler's
`TargetsTriggeredByCompilation` hook records successful compilation. A generic
`AfterTargets=CoreCompile` hook was rejected: it also runs when compilation was
skipped and could bless an externally replaced output. Publication validates the
recorded inputs and verifies that the copied output matches the recorded compiler
output. Incremental builds reuse existing proof; they cannot mint it merely from
an old DLL's existence or timestamp.

Local output digests bind the materialized and loaded rule set. They never enter
the shared semantic key. Packages and SDK imports use immutable identity/version,
not repeated dependency-DLL hashing. Local sources, project/import files and
first-party dependency receipts remain content checked. The receipt also records
project input membership and absence of standard ancestor build configuration, so
an added source or newly appearing Directory.Build.props cannot hide behind the
previous evaluated file list.

The analyzer reevaluates semantic provenance for each cache lookup. Missing,
malformed or stale proof refuses reuse; execution validates before loading and
again before reporting success. This also corrects the previous warm-daemon split:
the original key captured a DLL hash once, while reload inspected live DLL bytes.

The target is shipped as the package's opt-in build asset. A first-party producer
sets `FsHotWatchAnalyzerProvenance=true`; a restore-only package-copy project sets
`FsHotWatchPackageProvenance=true`. Project-reference producers must provide their
own receipts. Unresolved provenance fails with the offending reference rather
than quietly treating a project DLL as an immutable package.

This introduces a build-only receipt task and content validation cost; it avoids a
compiler fork and makes the proof inspectable. Arbitrary dynamic MSBuild inputs
outside the captured project/import/reference closure are not assumed safe.
Five-fresh-workspace timing and exact candidate consumer qualification are still
required; parser controls alone do not complete AUTOMATION-564.


### Evaluated source membership and private invocation context

A recorded list alone cannot detect a new external glob match. The producer now
captures MSBuild's ordered evaluation-time Compile items in a private SDK-hosted
ProjectCollection. The reader invokes that exact SDK out of process, with no
build targets, and compares current resolved items. This preserves imported
relative paths, item indirection, include/exclude/remove and absent external
roots without a handwritten glob matcher or runtime Microsoft.Build dependency.
Evaluation-time Compile and actual pre-Fsc Sources remain distinct evidence.

Producer invocation globals are retained in a private local per-user context;
public receipts carry only its opaque identifier and digest. Unix storage uses
0700 directories and 0600 files; Windows storage restricts ownership/access to
the current user. Values are passed in a private response file and never printed
in diagnostics. Missing context refuses proof. Context project/source binding
prevents substituting another producer's evaluation evidence.

Evaluation inherits the current environment. The predicate concerns current
ordered membership: environment changes producing different items refuse reuse;
changes producing the same items preserve it. Original environment replay and
read tracing are unnecessary and are not implemented. Actual Fsc option/input
and loaded-output binding remain separate checks, not a claim that evaluation
can predict arbitrary future target behavior.

One evaluation is reused per producer within a snapshot, including dependencies;
new snapshots reevaluate without a TTL. SDK startup is a material warm-path cost.
This source implementation remains uncompiled and unverified pending admission,
real producer controls, cross-workspace identity checks and the existing five-
workspace benchmark at no more than twice the warm baseline. Compiler/PathMap
workarounds remain rejected.
