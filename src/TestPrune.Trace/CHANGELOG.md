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
- feat: shadow bin (`TestPrune.Trace.ShadowBin`): a woven copy of a test app under
  `bin/Traced/<tfm>/`, beside `bin/Debug/<tfm>/`. Every file is re-hardlinked on each prepare
  (`HardLink.mirror`); woven files are written to a temp file and renamed over their link, never
  written through it. Assemblies whose portable PDB names a document under the repository root are
  woven; the weave is cached under `obj/traced/<content key>` (3 keys kept). The recorder is
  injected into the copy's deps.json (`DepsJson.injectRecorder`) without its PDB, so it stays out of
  the app's coverage. A new weave is accepted only after every touched method JIT-compiles in the
  app's own runtime.
