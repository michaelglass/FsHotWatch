# ADR-014: A plugin's summary is scoped to its cache key — not laundered at replay

Status: Accepted (2026-08-14)

Completes `docs/adr-013`-era work on cache honesty and closes AUTOMATION-191, the
`File = None` half of AUTOMATION-186.

## Context

AUTOMATION-186 established the scope rule: **a cache entry may only assert facts
derivable from its key's scope.** It enforced the rule for per-file entries
(`CachedFile*`) by making the stale state unrepresentable — those entries carry no
summary at all, and the replay derives one from the live error ledger.

Whole-run entries (`CachedRun*`, `File = None`) were left replaying their stored
verdict verbatim, which is correct exactly when the summary is a function of the
key. BuildPlugin satisfies that. **Format-check did not**: it subscribes to
`FileChanged`, whose key is a content merkle of *that event's files*, while its
summary counted `state.Unformatted` — the whole-session accumulated set. Session
one's "1 files need formatting" was stored under a key covering one clean file and
replayed, verbatim, into a later session over an empty green ledger.

## Decision

**Format-check's summary states what the run it is keyed on actually checked** —
`"3 of 12 files need formatting"` — rather than a session total. The summary
becomes a function of the same bytes the merkle covers, so the existing verbatim
replay is correct by the unmodified scope rule, and a cache hit is
indistinguishable from having run (the invariant AUTOMATION-245 stated for the
build cache).

The whole-session view moves to where it stays live: the error ledger (one entry
per unformatted file, which `fshw status` lists and the verdict gates on) and the
`unformatted` IPC command.

A run that compared nothing reports `"no files to check"`, not `"format OK"` — the
same refusal as AUTOMATION-272's zero-match rule.

## Roads not taken

The ticket proposed, and AUTOMATION-191's approval asked to compare, two
mechanisms for teaching the framework to *re-derive* a stale summary at replay:

- **(a) A general per-plugin "summary is ledger-derived" capability.** Rejected.
  Measured blast radius: as an eighth `PluginHandler` field, ~130 record literals
  across 23 files; routed instead through `RunVerdict` or `PluginCtx` to dodge
  that, it still costs a new `CachedStatus` variant, a cache-format bump, and a
  new concept in the plugin API. It is also *per-plugin* where the fact is
  *per-verdict*: format-check's own timeout summary ("format check timed out after
  60s") is run-scoped and a plugin-wide flag would have laundered it into a
  ledger count. Worst of all it makes the rule weaker — "derivable from the key's
  scope **or** from a live source the plugin declares authoritative" — to keep an
  assertion nobody needs to make.
- **(b) A narrow special-case for format-check in the framework.** Rejected: a
  plugin name matched as a string inside the framework, in another assembly,
  silently wrong the moment the plugin is renamed.

Both repair a wrong assertion at replay time. Scoping the summary means the wrong
assertion is never made, needs no declaration, no API surface and no serialization
change, and leaves AUTOMATION-186's rule stated exactly as it was. It is also
where AUTOMATION-303 and AUTOMATION-245 put their fixes: in the plugin's own cache
contract, using framework contracts that already existed.

## Consequences

- Format-check's status line for a single-file save reports that save, not a
  running total. On a scan — one `SourceChanged` carrying every file — the number
  is unchanged.
- No framework, cache-format or plugin-API change; pre-existing cache entries stay
  valid (their stored summaries are now recomputed identically on the next miss).
- Not closed by this ADR: a `File = None` replay calls `ClearPlugin` before
  replaying the entry's errors, so it drops ledger findings for files outside the
  replayed batch — measured as REAL ledger=1 vs REPLAYED ledger=0. Filed
  separately; it is a framework-wide property of the replay path, not a
  format-check one.

## Related

- Completes: AUTOMATION-186 (`src/FsHotWatch/CHANGELOG.md`, 0.10.0-alpha.4).
- Same family: AUTOMATION-245 (a cache hit re-verifies artifacts), AUTOMATION-303
  (a structural change must miss the key).
- Source: `src/FsHotWatch.Fantomas/FormatCheckPlugin.fs`.
- Regression tests: `tests/FsHotWatch.Tests/FormatCheckPluginTests.fs`
  ("a replayed format-check verdict cannot claim files its cache key never
  covered", plus its positive control).
