# ADR-008: mtime is never a content oracle — content-hash for freshness/cache keys

Status: Accepted (2026-06-08)

## Context

A file's last-write mtime is *not* a reliable proxy for its content. Several
tools routinely change a file's bytes while preserving (or restoring) the old
mtime:

- `rsync -a` / `rsync --times`
- `cp -p`
- `tar -x` (restores archived mtimes)
- `git checkout` / branch switches (can restore an older committed mtime)

Any freshness check or cache key that keys on `(path, mtime)` — or compares two
mtimes — is therefore fooled by a content rewrite that preserves mtime: it sees
"unchanged" and serves a stale verdict.

This bit us repeatedly. The flagship incident was a **phantom ~1700-error FS1178
cascade** across a consumer's solution: a vendored source file was rewritten by
`rsync -a` (preserved mtime), the build merkle was keyed on `(path, mtime)`, so
the cached content hash never moved, the build cache key never moved, and the
daemon replayed a stale `BuildDone` forever — clearable only by a daemon
restart. Confident false-RED that blocks a merge.

## Decision

**Freshness and cache-key decisions must reflect on-disk *content*, not mtime.**
Use a content hash (`FsHotWatch.CheckCache.sha256Hex` over file bytes/text).
mtime may only be used as a *fast-path negative cache* (a cheap "probably
unchanged" hint), never as proof of content equality — and only where a separate
content-aware guard backstops the gap.

## Sites audited

| Site | File | Status |
|---|---|---|
| FCS check cache key provider | `src/FsHotWatch/CheckCache.fs` (`TimestampCacheKeyProvider`) | ✅ content-hashed (name kept for back-compat; impl reads + hashes bytes) |
| Build input merkle | `src/FsHotWatch.Build/BuildPlugin.fs` (`BuildInputsHasher`) | ✅ content-hashed (Bug 1 fix — `(path, mtime)` memo dropped) |
| Deps-freshness dep signature | `src/FsHotWatch/DepsFreshness.fs` (`depRelevantSignature`) | ✅ content-hashed over the package-graph-declaring inputs ONLY (this ADR — was `staleSignature` over the whole fsproj + dep files; before that, max dep-file mtime ticks) |
| Deps-freshness detector | `src/FsHotWatch/DepsFreshness.fs` (`compareFreshness`/`detectProjectFreshness` + `evaluateProject`) | ✅ mtime fast-path + content-drift escape hatch + compile-item false-positive suppression (this ADR) |
| Post-build artifact-staleness guard | `src/FsHotWatch.Build/BuildPlugin.fs` (`verifyArtifactsFresh`) | ☑️ mtime kept *by design* — see below |

### `depRelevantSignature` (changed — was `staleSignature`)

Evolution in two steps:

1. **mtime → content** (original ADR). Was `max(dep-file mtime ticks)`. A
   preserved-mtime rewrite of `paket.lock` / `Directory.Packages.props` left it
   byte-identical, so debounced restore recovery never re-armed. Became SHA-256
   over `(path + bytes)` of every dep file (including the whole `.fsproj`), so any
   content change moved the signature and re-armed recovery.

2. **whole-fsproj → dep-relevant subset** (this revision). Hashing the WHOLE
   `.fsproj` conflated source-list edits with dependency edits: a compile-item-only
   change (adding/moving `<Compile>` entries — which does NOT alter the package
   graph) moved the signature and, worse, bumped the fsproj mtime so the detector
   read `Stale` → a phantom restore that under machine load could time out and pin
   a permanent deps-RED (the debounce keeps the unchanged signature red across
   runs). `depRelevantSignature` now hashes ONLY the package-graph-declaring inputs:
   the canonicalized dep-declaring fsproj elements (`PackageReference`,
   `ProjectReference`, `PackageVersion`, `PackageDownload`, `FrameworkReference`,
   `Import`, `<Sdk>` / the `<Project Sdk="…">` attr, `TargetFramework(s)`) plus the
   FULL bytes of the ancestor dep files. Source-item elements (`Compile`/`Content`/
   `None`/`EmbeddedResource`) are excluded, so a compile-item-only edit yields the
   SAME signature. Canonicalization is order- and whitespace-insensitive (sorted
   elements, sorted attributes, `LocalName` so namespaced old-style fsprojs match),
   and a parse failure folds a content-derived sentinel (never throws). Known
   limitation: fsproj `<PropertyGroup>` properties are NOT hashed, so a
   property-indirected package version (`Version="$(FooVersion)"` with `<FooVersion>`
   local to the fsproj) or a `<RestoreSources>` change is invisible to the signature
   — the common cases (direct versions, Central Package Management via
   `Directory.Packages.props`, paket) are all covered because those inputs are
   hashed in full.

### Deps-freshness detector (changed)

`compareFreshness` (assets-mtime vs dep-mtimes) remains an mtime **fast path** —
it answers "were the restored assets regenerated after the deps?", inherently a
temporal question. It has TWO blind spots, both now closed in `evaluateProject`
against the content-hashed `depRelevantSignature`, symmetrically:

- **Content-drift escape hatch** (`Fresh` → treat as `Stale`). A preserved-mtime
  dep rewrite (`rsync -a` / `git checkout`) makes a changed `paket.lock` look
  *older* than the assets, so the probe reports `Fresh`. The deps gate is the
  **only** guard for dep-file content drift (the build merkle tracks source +
  `.fsproj`, not `paket.lock`/`Directory.Packages.props`/`project.assets.json`), so
  this gap is load-bearing. A `Fresh` verdict whose signature DRIFTED from the last
  fresh/recovered baseline is treated as `Stale` and re-restored.
- **Compile-item false-positive suppression** (`Stale` → treat as `Fresh`). A
  compile-item-only fsproj edit bumps the fsproj mtime, so the probe reports
  `Stale` even though the package graph is untouched. A `Stale` verdict whose
  signature MATCHES the last fresh/recovered baseline is proceeded on — the existing
  assets are still valid — with NO restore. A first sighting has no baseline, so a
  genuinely-cold `Stale` still restores.

`RecoveryTracker` records the fresh-content baseline (shared by both the
`HasContentDrifted` and the new `MatchesFreshBaseline` predicates).

### `verifyArtifactsFresh` (kept mtime — by design)

This post-build guard compares each project's DLL mtime to its max source mtime
to catch MSBuild's incremental cache lying (built, but skipped relinking a DLL a
real edit should have rebuilt). mtime is the **correct** signal here:

- it answers a strictly temporal question ("DLL regenerated *after* the newest
  source?"); the MSBuild-lie failure mode it targets always involves a real edit
  that bumped the source mtime, so `DLL < source` is exactly the tell;
- the preserved-mtime content-rewrite class is *not* this guard's job — the
  content-hashed `BuildInputsHasher` already invalidates the build-cache key on a
  preserved-mtime content change and forces a real rebuild, whose fresh DLL then
  post-dates the (old-mtime) source, so this check correctly sees it as fresh;
- there is no "expected DLL content" to hash against, so a content check is not
  even expressible here. The merkle is the content guard; this is its temporal
  complement.

A precise comment at the call site records this reasoning so it isn't "fixed"
into a no-op later.

## Consequences

- Freshness/cache correctness no longer depends on mtime fidelity, so VCS
  operations, `rsync`-based deploys, and restored archives can no longer pin a
  stale verdict.
- `staleSignature` reads + hashes dep-file content each cycle instead of stat-ing
  mtimes. Dep files are few per project and the cost is dominated by the restore
  it gates; correctness outranks the micro-optimization.
- Future freshness/cache work: default to content hashing. If you reach for
  mtime, you must (a) use it only as a fast path and (b) name the content-aware
  backstop — as `verifyArtifactsFresh` does.
