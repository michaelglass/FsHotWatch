# ADR-011: the analyzer cache key must include analyzer-assembly *content* identity

Status: Accepted (2026-06-17); amended 2026-09-16 — see
[Amendment](#amendment-2026-09-16-the-identity-is-the-compilers-receipt-not-the-dll-bytes).

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

Observed downstream (a large private downstream repository): a file with a real, un-suppressed
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

## Amendment 2026-09-16: the identity is the compiler's receipt, not the DLL bytes

### What was wrong with bytes

The `analyzer-assemblies` slot above hashed the DLL **bytes**. That is the right
oracle for the failure this ADR was written against (a rebuilt rule replaying a
stale verdict on one long-lived daemon), and the wrong one for the property
ADR-010 and the tracked issue need: an entry written in one checkout hitting in
another. fsc writes the absolute path of the portable PDB into the PE's CodeView
debug entry (`<checkout>/analyzers/FsHotWatch.Rules/obj/Debug/net10.0/…pdb`), so
a first-party analyzer built from identical source in two jj workspaces differs
byte-for-byte, permanently, and every workspace that builds its own house rules
misses every analyzer entry the other wrote. The NuGet-sourced shim is
byte-identical across workspaces; only in-repo builds carry the salt.

### Decision (amended)

The slot is now `analyzer-inputs`, the **semantic** identity of the analyzer set
(`AnalyzerIdentity`, internal to `FsHotWatch.Analyzers`), and the plugin-version
salt is `analyzers-merkle-v5` so byte-keyed entries are orphaned rather than
misread. For each candidate DLL (the loader's own set: every `*.dll` under a
configured path minus the known-non-analyzer prefixes):

- **FirstParty** — the DLL's portable PDB (sidecar validated against the CodeView
  GUID, else embedded) records at least one document under the repository root.
  Its identity is SHA-256 over the sorted (repo-relative document path, the
  PDB's own SHA-256 of that document) pairs, the sorted assembly references
  (name, version), and the SHA-256 of the producer `.fsproj` (the unique project
  file in the nearest directory above the DLL, walking up to the repo root).
  The PDB's Document table is the compiler's own record of what it read; it is
  path-free once each document is named relative to the repository, and it is
  what makes the identity equal across checkouts.
- **PackageBytes** — anything without such a receipt for this repository (no PDB,
  or no document under the root: the shim, bundled dependencies) keeps the byte
  digest, which is already stable across checkouts for a package.

Recorded document paths are absolute on the machine that ran fsc, so the recorded
root is recovered, not read: when any document lies under the current repo root
the build was in place; otherwise the producer project's repo-relative directory
(`analyzers/FsHotWatch.Rules`) is a segment run every document under it carries,
and the recorded root is what precedes it. Documents outside the recorded root
(SDK sources) are excluded and do not disqualify the DLL.

Trust is bounded by verification. Every in-repo document is re-hashed from disk
and compared with the recorded checksum; any disagreement is a **refusal**, never
a silent fallback — a stale DLL beside edited sources is the one case where the
byte digest was honest and the receipt would lie. The refusals: `Unreadable`,
`MissingPdb` (the CodeView entry places the PDB under this repository but none is
there), `PdbMismatch` (a stale sidecar), `UnverifiableChecksum` (an algorithm
other than SHA-256/SHA-1), `DocumentMissing`, `DocumentDrift`,
`ProducerNotFound`, `OutputOlderThanProject` (an mtime **refusal trigger**, never
an acceptance — per ADR-008). A refused set means `CacheKey = None` for the event:
the analyzers still run, nothing is read from or written to the cache, and the
daemon logs one `Analyzer cache is off — …` warning per distinct refusal set (by
file and cause), not one per file.

Reload-if-stale is unchanged in mechanism and now keyed on the set's raw-byte
digest (`MaterializationDigest`): a rebuild whose receipt is unchanged swaps the
loaded set into the process and keeps every cache entry. While the set is refused
the snapshot is retaken on every event, because a refusal can clear without a
byte moving (an edit reverted; a project file touched and rebuilt to the same
bytes) and a bytes-only watcher would leave a daemon uncached for its life. The
snapshot is also refreshed inside the cache-key function — the framework computes
the key once per event on the plugin's own loop, before the lookup — so the entry
looked up and the analyzers that run describe the same set on disk (previously
the key's byte hash was computed once at construction and never followed a
reload).

### Two more things stood between "fresh workspace" and "hit"

The benchmark below found that the receipt alone could not make a fresh
workspace hit, because two older defects sat in front of it. Both are fixed under
each as its own change.

**The shared store was namespaced per checkout NAME.** `RepoIdentity.namespaceOf`
returned `<checkout directory name>-<digest>` and `Daemon.fs` used that whole
string as the store directory, so every jj workspace had a private directory —
65 of them for this repository on one box, 82,861 duplicated analyzer entries —
while the comment above the call site promised "namespaced by the repository (not
the workspace), so a freshly created workspace starts warm". The test that covered
it asserted only the digest suffix and said so ("the identity half must agree even
though the labels differ"): a check narrower than its name. Worse, `describe`
hashed the TEXT of jj's `.jj/repo` pointer, which jj writes RELATIVE
(`../../../.jj/repo`), so all same-depth workspaces shared one digest and the
default checkout (absolute path) had another. Now: pointers are resolved against
the directory they live in, the identity is that absolute directory, the label is
the repository's own directory name read off the identity, and the tests assert
the DIRECTORY — for a relative pointer as jj writes it, an absolute one, a git
worktree, and the real checkout through the real code path. A truly fresh
workspace used to miss with `no-entry`, not `InputsChanged`: it had no store to
read. Existing per-checkout directories are orphaned, not deleted (a disk-reclaim
follow-up).

**The "repo-relative" `analyzer-paths` slot was absolute.** `.fshw.json` writes
`tools/fsharplint-shim/bin/Debug/net10.0/` with a trailing separator;
`CachePathIdentity.ofPath` kept it through `GetRelativePath` as an empty last
segment, the portable grammar rejected it, and the identity silently fell back to
`external:<absolute path>` — workspace-specific under a repo-relative label. With
the receipt identity in place this was the ONLY differing label between two
checkouts (`inputs-changed:analyzer-paths`, 176 of 176 entries). The cross-workspace
test had built its path without the separator and stayed green. Now `ofPath`
trims trailing separators, and the test passes the path as the daemon does.

### Residual gap: the import closure

The receipt names sources, references and the producer project. It does not name
`Directory.Build.props`, `Directory.Build.targets` or the SDK — a change there
that alters the output without touching any listed document is invisible to the
identity. Mitigation today: `mise run build-analyzers` runs before every
daemon-backed task, so the DLL on disk is rebuilt from the current
import closure before the identity is taken; a rebuild that changes the emitted
IL without changing the receipt is the remaining theoretical hole, and a rebuild
that changes any source checksum or reference is caught.

### Rejected and deferred

- **MSBuild evaluation host** (predict the inputs by evaluating the project) —
  deferred. The acceptance criterion is about repeated work in fresh workspaces;
  the PDB is the compiler's record of what it actually read, which is stronger
  than a prediction of what it would read.
- **SourceLink verification** — deferred. SourceLink is disabled in this
  repository when no `.git` directory exists (`Directory.Build.props`), so it is
  inert in exactly the jj workspaces this is for.
- **`PathMap` / deterministic paths** — not adopted. Per prior art fsc does not
  apply the path map to the CodeView PDB path, so the salt would remain; to be
  confirmed before anyone re-investigates it.
- **Dropping the byte identity entirely** — rejected. Package DLLs have no receipt
  for this repository and their bytes are already stable; bytes stay the identity
  where the receipt is absent, and stay the reload trigger everywhere.

### How the tests stand in for a second build

The unit tests do not run a second `dotnet build`. They relocate the built
house-rules DLL, PDB, project file and every in-repo document into a temp
checkout and byte-patch exactly the two things fsc changes: the CodeView PDB path
in the DLL (case-flipped, equal length — a different checkout's salt) and, for
the "built from different source" case, that document's hash blob in the PDB.
Both patches are unique-match, so a fixture that stops describing the compiler's
output fails loudly (`tests/FsHotWatch.Tests/AnalyzerFixtures.fs`).

### Measured

See [bench-analyzer-identity.md](bench-analyzer-identity.md) for the commands
and the full table.

Analyzer entries per cold `check` (176 files with entries; the 8 `no-entry` misses
recur in every run and are files no run ever stores):

| condition | cold workspace 2 and 3 | reason named |
|---|---|---|
| before (v4, per-name store), same name, different path | 173 misses / 173 | `inputs-changed:analyzer-assemblies,analyzer-paths` |
| before, distinct name (unmodified main, 1 run) | 184 misses / 184 | `no-entry` (a private, empty store) |
| after, same name, different path | 0 misses (352 hits) | — |
| after, DISTINCT names (the acceptance test), 3 workspaces | 0 misses (352 hits) each | — |

Analyzer span time per cold check: 10.2–12.7 s before, 7.2–7.9 s after on a hit
run (the remaining time is the plugin's own scan of 176 files and the 8 uncached
ones). Whole-check wall time is 340–380 s in every condition and is dominated by
test-prune running the full suite in a workspace with no baseline (316–356 s) and
by the scan; it is not a claim of this change. Warm second check in the same
workspace: 18–29 s. The first `after` run in the shared store re-keyed the 173 v4
entries the default checkout had written (`inputs-changed:…,plugin-version`), as
a version bump must.

