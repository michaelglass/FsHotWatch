# ADR 0005 — A traced process writes its traces once, at exit

Status: Accepted (2026-09-26)

## Context

A woven test process attributes every probe hit to the current xUnit v3 test (or a
fixture, collection, assembly, pool or static-init scope) and keeps one bitset of probe ids
per scope. Those bitsets have to leave the process. Writing each test's trace as the test
finishes needs a per-test hook, and there is none without a compile-time reference in every
test project: xUnit v3 publishes a test's `TestState` only on a new `TestContext` object,
not through an event a loaded assembly can subscribe to, and Microsoft Testing Platform
registers its extensions at build time.

## Decision

The recorder writes one `testprune-trace/1` NDJSON dump per process, from a
`ProcessExit` handler, to `trace-<pid>.ndjson.tmp` and renames it to `trace-<pid>.ndjson`.
The last line is `{"end":true}`; a dump without it is rejected as truncated. Ingestion
merges every process's dump by scope after the run.

## Consequences

A continuation that runs after its test has finished, but still in the test's async flow,
is attributed to that test. That over-attributes, which is sound: a trace may name code the
test did not need, never omit code it ran. A process that is killed (a timeout, a signal, a
runtime crash) leaves no dump, so its tests keep no trace: a missing main-process dump
stores the run as failed, and a missing child dump marks the starting test incomplete.
`Environment.Exit` still runs the handler. Memory is one bit per probe id per scope,
O(scopes × ids / 64) words for the whole run, held until exit. If the phase-1 dogfood
measurement of peak RSS (`test-prune-traces overhead` reports it) shows that to bite, the
fallback is a two-level sparse bitset per scope; nothing else changes.
