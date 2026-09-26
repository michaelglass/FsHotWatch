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
- feat: site probes (`TestPrune.Trace.SiteProbes.pass`): record which union cases and product types
  code touched, not only which methods it entered. Cases are recorded at the union's tag getter
  returns, tag-field reads, successful `isinst` on a case class, `castclass` and field reads on a
  case class, and nullary-case singleton reads; types at successful `isinst`, `castclass`, `unbox`,
  FSharp.Core's unbox/type-test intrinsics and field reads outside the declaring type. Type
  initializers run inside a try/finally that routes their hits to the `S:static-init` scope. The tag
  getter, tag field and singleton fields are identified structurally, so a case named `Tag` and a
  case field named `tag` weave to valid IL.
