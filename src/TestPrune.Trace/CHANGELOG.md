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
- feat: input-capture weave pass (`TestPrune.Trace.Redirects`): rewrites call, callvirt and
  newobj sites of the redirected BCL methods (`Redirects.table`) into the recorder's `Io` and
  `ProcessShims` shims. Method pointers (`ldftn`) are left alone.
- feat: joiner (`TestPrune.Trace.Joiner`): maps each manifest row to an indexed symbol, a file-level
  entry, or `Dropped` (compiler plumbing whose inner probes attribute elsewhere). A method with a
  source document matches a same-named symbol in its own file before the nearest preceding
  declaration, so a line drift between the index and the binary cannot re-attribute it. Union
  members map to their case, closures to the binding they were written in, and generated members
  without a document to their owner member, type or enclosing module.
  A CLR type with no namespace maps to TestPrune.Core's `<global>.`-qualified name for a
  global-namespace type (`StartupHook` → `<global>.StartupHook`) or, for a top-level module,
  its bare name.
- feat: shadow bin (`TestPrune.Trace.ShadowBin`): a woven copy of a test app under
  `bin/Traced/<tfm>/`, beside `bin/Debug/<tfm>/`. Every file is re-hardlinked on each prepare
  (`HardLink.mirror`); woven files are written to a temp file and renamed over their link, never
  written through it. Assemblies whose portable PDB names a document under the repository root are
  woven; the weave is cached under `obj/traced/<content key>` (3 keys kept). The recorder is
  injected into the copy's deps.json (`DepsJson.injectRecorder`) without its PDB, so it stays out of
  the app's coverage. A new weave is accepted only after every touched method JIT-compiles in the
  app's own runtime.
  A JIT-verification failure whose output names FSharp.Core says why: the recorder is F#, and an
  app that ships no FSharp.Core (a C#-only test app) cannot load it.
- feat: ingestion (`TestPrune.Trace.TraceIngest.ingest`): merges every process's dump by scope (a
  traced child records into the scope that started it, static init included), joins probe ids to
  symbols at their current version hash (`versionHashes`), hashes file inputs as they are now
  (repo-relative, `bin/Traced/` keyed as `bin/Debug/`), matches CTRF outcomes (display name, else
  class and method; theory rows union, the worst outcome wins) and links each test to the fixture,
  collection and pool scopes it inherited. A trace is complete only when its test passed, every
  executed file still matches its PDB hash, every executed id mapped and every child process it
  started left a dump; otherwise its reasons are stored. An empty weave set is stored as a
  `refused` run with a reason naming the likely cause (PDB paths mapped to `/_/`), never as a set
  of empty verified traces. A dump from another weave (a different id count, or an id outside the
  manifest) is rejected. No usable main-process dump stores a `failed` run and no tests.
- feat: `DumpReader.readEach` returns every dump with its file.
- feat: `TraceStore.RunScopeKeys`; garbage collection keeps the static-init and ambient scopes of
  each project's latest recorded run.
