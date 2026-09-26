# Changelog — TestPrune.Trace

## Unreleased

- feat: package scaffold with the shared trace model (`TestPrune.Trace.Model`).
- feat: `DumpReader` parses recorder dumps; a dump without its end marker is rejected as
  truncated, and `readDirectory` reports each rejected file with its reason.
- feat: trace store (`TestPrune.Trace.TraceStore`): a SQLite file separate from the index, with its
  own `TraceSchemaVersion` and forward-only migrations. A file from a newer trace schema is refused
  (`TraceSchemaNewerThanConsumer`) and left untouched; no code path deletes it. Content is stored per
  scope, so fixture and pool content is stored once and linked from each test.
- feat: environment fingerprint (`TestPrune.Trace.Fingerprint`): SHA-256 of canonical JSON over the
  runtime, OS/arch, original deps.json hash, recorder and weaver versions, TestPrune.Core's
  `SchemaVersion` as the hash scheme, and configured files and environment variables (values hashed).
- feat: weaver core (`TestPrune.Trace.Weaver`): Mono.Cecil method-entry probes with a manifest; refuses
  optimized builds; re-anchors F#'s end-of-method hidden sequence points at the woven code size so
  portable PDBs stay decodable (checked for every woven method by `PdbCheck`).
- feat: joiner (`TestPrune.Trace.Joiner`): maps each manifest row to an indexed symbol, a file-level
  entry, or `Dropped` (compiler plumbing whose inner probes attribute elsewhere). A method with a
  source document matches a same-named symbol in its own file before the nearest preceding
  declaration, so a line drift between the index and the binary cannot re-attribute it. Union
  members map to their case, closures to the binding they were written in, and generated members
  without a document to their owner member, type or enclosing module.
