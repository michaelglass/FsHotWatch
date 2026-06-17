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
