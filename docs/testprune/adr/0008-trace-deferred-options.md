# ADR 0008 — Trace options deferred or rejected in phase 1

Status: Accepted (2026-09-26)

Each line is an option that was considered for recorded per-test traces and left out of
phase 1, with the measurement or reason that decided it.

- **Module-value `ldsfld` probe: rejected.** It found 0 sites in Debug builds: F# reads a
  module value through its `get_x()` getter there, and the method-entry probe on the
  getter already records it.
- **Tracing Release builds: rejected.** Release builds inline small functions across
  assemblies, so their entry probes never fire and a trace would silently miss code. The
  weaver refuses optimized assemblies and the project runs untraced.
- **Process-level `open()` interposer: deferred.** File reads are captured at their .NET
  call sites instead. The file census (`test-prune-traces file-census`) measures what that
  misses; the interposer is reconsidered only if the census shows a gap it would close.
- **Profiler ReJIT weaving: deferred.** It needs the CLR profiler slot, which the coverage
  profiler already occupies, so traces could not record in the same run as coverage.
- **Per-field type ids: deferred** until after TestPrune.Core splits a type's content hash,
  which is what a field-level id would join against.
- **Database table tracing: deferred to phase 4** (through the Npgsql `ActivitySource`).
- **`[<ExcludeFromCodeCoverage>]` on the recorder: rejected.** It would also hide the
  recorder from this repository's own coverage ratchet. The recorder is instead copied
  into a traced app without its PDB, which keeps it out of the app's coverage report.
- **Selection from traces: out of scope for phase 1.** Phase 1 records and measures only;
  selection rules start in phase 2 in shadow mode.
