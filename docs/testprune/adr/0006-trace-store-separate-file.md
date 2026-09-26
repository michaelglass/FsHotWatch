# ADR 0006 — Traces live in their own SQLite file, never in the index

Status: Accepted (2026-09-26)

## Context

TestPrune.Core deletes its index file (`.test-prune.db`) whenever `SchemaVersion` changes,
and rebuilds it from source. Extension data kept through `Ports.PluginStore` lives in that
file and is deleted with it. That is right for the index, which is cheap to rebuild from
the code. Traces are not: rebuilding them costs a full traced run of every test project.

## Decision

The trace store is a separate SQLite file (`.fshw/test-traces.db` under FsHotWatch,
`.test-prune-traces.db` otherwise) with its own `TraceSchemaVersion` in `PRAGMA
user_version` and forward-only migrations. Opening an older file applies each missing
migration in one transaction. A file written by a newer trace schema is refused
(`TraceSchemaNewerThanConsumer`) and left untouched; no code path deletes it. Symbols are
referenced by full name and content hash, never by an index row id, which a re-index
reassigns.

## Consequences

A Core `SchemaVersion` bump never touches recorded traces. When Core changes what a content
hash means, the traces are not deleted either: the hash scheme is part of each trace's
environment fingerprint, so traces recorded under the old scheme stop matching instead of
spuriously verifying. An older TestPrune.Trace that meets a newer store fails loudly rather
than destroying a store it cannot read; the census verb exits 2 on it.
