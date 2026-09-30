# Changelog — TestPrune.Trace

## Unreleased

- fix: a recursive directory listing (`SearchOption.AllDirectories`) is stored as a
  `list-deep` input hashed over every entry of the tree. It was stored as `list`, hashed
  over the directory's own entries only, so a file added or removed in a subdirectory left
  the trace valid. A `list` input keeps its meaning; traces recorded before this version
  are not reused, since the recorder and weaver versions are part of the environment
  fingerprint.
- fix: the redirect pass rewrites the call sites of the new recorder shims (XML loaders
  by path, `Encoding` overloads, file-system-entry and `DirectoryInfo` listings).
- fix: a test inherits the static init of a module it reads a value of. Reading an F# module
  value from another file runs only the value's getter, which has no document, and in an
  executable (a test project) the module has no type initializer of its own. So under a
  full weave of the test assembly, a module value computed from another module's value
  reached that module's `<StartupCode$…>` initializer only when it ran nested inside its
  own; if it had run first elsewhere, the file inputs it recorded (an existence probe that
  locates the repository root, for example) were missing from the dependent tests' traces.
  Every member of a module now touches the module's startup class, and a member with no
  document touches the files of its type's other members.
- audit: a type initializer a sampled test runs alone but not in parallel must be one it
  inherits in parallel, or the audit fails (`UncoveredInit`, `uncovered init <type>` in the
  table). Missing ids now compare everything a test holds or inherits, not only its own
  scope's ids, so an initializer that ran inside another is compared too. Before, every
  missing initializer counted as expected, so the audit could not see a test that lost a
  static-init dependency. `Audit.ownIds` is replaced by `Audit.observe`; `compareTest`
  takes each run's `Observed` and whether the parallel run recorded a scope.
- file census: the outside-the-repository run's dumps and CTRF report are copied to
  `file-census/outside/` under the run directory before its temp directory is deleted.

## 0.5.0 - 2026-09-29

- fix: the shadow bin copies, never hard-links, an assembly with symbols (a sibling `.pdb` or
  an embedded portable PDB) and its `.pdb`. MS CodeCoverage instruments such an assembly by
  writing into the file and restores it by replacing it, so through a hardlink a traced run
  under `--coverage` would have left the instrumented bytes in the build output. Assemblies
  without symbols, which a collector does not instrument, are still hard-linked.
- fix: ingestion drops the dump of a process that neither ran a test nor was started by one
  when another such process ran the tests. Under `--coverage`, MTP runs a test-host
  controller next to the test host; both are the woven app, and the controller's dump (its
  startup, no test) was read as the run's main process when its pid sorted first. With no
  process that ran tests, every dump is kept, so a run whose filter selected nothing still
  records.
- overhead: each traced launch starts from an emptied dump directory. Without that, rep N
  started with every earlier rep's dumps in place. Each sample, and its line in the table,
  now also reports the launch's wall time (`Wall`), the live descendant processes of the
  verb when it started (`LiveDescendants`: a process an earlier launch left behind is
  charged by `getrusage` to a later one), and for a traced launch the bytes and files it
  dumped (`DumpBytes`, `DumpFiles`). A traced CPU that climbs across reps while the dumps
  stay flat and no descendant lingers points outside the recorder's own work.
- fix: coverage of a woven assembly keeps the branch points of nested patterns: a union case
  whose field is matched too, as a case (`| Measured(InSync, _) ->`) or a literal
  (`| Command 0 ->`). The outer case test, the case class, its field and the inner test each
  get a probe, and only the branch after the first probe got a copy of the line's sequence
  point: the field's test stayed under a hidden point with probe calls ahead of it, and MS
  CodeCoverage dropped its branch points. Every branch after a probe, before the next probe,
  now gets its own copy of the line's point. Probes and their ids are unchanged.
- fix: hits on a generic union's cases (`Lookup`1+Found`) map to the indexed case instead of
  being reported as unmapped code; needs the TestPrune.Core release that indexes them under
  the union's own name. A generic type's constructor matches its type by the same short name
  the index uses, and `Joiner.typeCandidates` no longer tries an arity-less name, which no
  index entry has.
- fix: an F# anonymous record's generated type (`<>f__AnonymousType…`) is dropped instead of
  reported as unmapped code. It has no source document or symbol; the code that builds and
  reads the record is traced where it is written.

## 0.4.1 - 2026-09-28

- changelog: the TestPrune.Inline attribute index is a feature, not a breaking change
- coverage: pin the SDK band and remove the branches SDK 10.0.4xx left uncovered
- Index inline members as the TestPrune.Inline attribute


## 0.4.0 - 2026-09-27

- fix: a test inherits the static init of the code it ran. A type initializer runs once per
  process, in whichever test touches the type first, so what it read (a module value that
  locates the repository root, then reads a file there) was recorded under a static-init
  scope no test inherited: a change to that file invalidated no trace. Each initializer now
  records into its own `S:<type>` scope, and ingestion links it to every test that ran code
  of that type or code in the source file its initializer is in (F# initializes a module's
  values in a `<StartupCode$…>` class no user code names), transitively through the
  initializers an inherited one touched or ran inside it. An initializer the recorder cannot
  tie to a woven type is inherited by every test. New module `RunScopes` (`merged`,
  `isStaticInit`, `staticInheritance`); `Audit.merged` moved there. Garbage collection keeps
  every `S:` scope of a project's latest run.
- fix: type initializers are wrapped in `SitesOnly` assemblies too (the test project, by
  default). A test-project module value that read a file recorded the read into whichever
  test triggered it first (or into no test, when an executable initializes its files at
  startup), so the other tests that use it did not depend on the file. Nothing places such an
  initializer (no method probes name its types), so every test inherits it: sound, and
  wider than needed for a suite with many such values.
- fix: `file-census` runs the woven copy outside the repository traced against the same
  repository root. A test that reads a repository file at an absolute path
  (`__SOURCE_DIRECTORY__`) passes there, and used to count as over-recorded; the read it
  records from outside now counts as a dependency on the repository
  (`FileCensusReport.ReachOutside`). `FileCensus.readers` takes the manifest, and
  `summarize` the tests that reached the repository from outside.

## 0.3.1 - 2026-09-27

- fix: a traced prepare re-weaves when the weaver, the recorder or a pass is another build of
  the same version. The weave cache under `obj/traced/` was keyed on their versions, so a
  rebuilt or upgraded weaver of one version reused a weave (and its JIT verification) that
  another build made. The key now names each of those assemblies' module version id, which a
  deterministic build derives from the assembly's content: rebuilding an unchanged weaver
  still reuses the weave. Existing cache entries are woven again once.

- fix: coverage of a woven assembly keeps the branch points under a match's later hidden
  sequence points, such as the list test of `| Subset [] ->` after the union case test. The
  copy of a line's sequence point that 0.3.0 adds after a probe used to cover the probes that
  follow the restored branch, and MS CodeCoverage drops a hidden-range branch whose line's
  range holds a call: 4 of 1,644 branch points in TestPrune's own suite. The site-probe pass
  now also gives the instruction after the copy's last branch a hidden sequence point, so the
  copy's range holds no call. TestPrune's suite now loses no branch point. Probes and their ids
  are unchanged.

## 0.3.0 - 2026-09-27

- fix: coverage of a woven assembly keeps the branch points of union matches, field
  comparisons and type tests. MS CodeCoverage counts a conditional branch under a hidden
  sequence point only while no call lies between it and its line's visible point. F# puts
  a match's test there, so a site probe's call dropped the line's branch points while the
  line stayed hit: 98 of 1,646 in TestPrune's own suite. Where a probe precedes such a
  branch in code that falls through from the line, the site-probe pass now gives the
  instruction the probe resumes at a copy of the line's sequence point. On TestPrune's
  suite 4 of 1,644 branch points are still lost (two `match` lines whose later hidden
  range holds calls). Line coverage and hits are unchanged, as are probes and their ids.
  The rule follows MS CodeCoverage's observed behavior, not a documented one.

- fix: a measurement launch that exits nonzero now says why. `overhead` used to drop every
  launch's output and `audit` the parallel run's exit code and output, so a traced sample that
  exited 2 left no trace of which tests failed. `Overhead.Sample.Output` and the new
  `AuditReport.ParallelExitCode`/`ParallelOutput` hold the output of a nonzero-exit launch, and
  both reports print it under the failing launch. Breaking for code that builds these records:
  set `Output = ""` on a `Sample`; `Audit.summarize` fills the new fields with 0 and "", and
  `Audit.withParallelRun` sets them from a run.
- fix: `Launch.direct` (and so `Launch.run`) keeps the last `Launch.OutputTailChars` (65,536)
  characters of each output stream, starting with `Launch.DroppedOutputMarker` when it dropped
  earlier output, instead of buffering a test app's whole output. `Launch.outputLines` splits
  that output into report lines.

## 0.2.1 - 2026-09-27

- chore: rebuild to bundle updated dependencies


## 0.2.0 - 2026-09-26

- fix: a closure F# emits into a file's `<StartupCode$…>` class with no sequence points of its
  own (a wrapper that only passes a function along, such as `List.map f` in a class's `let` or
  member) now carries a source document in the weave manifest: the document of the method that
  creates it, at the line in its compiled name (`urlRegexes@431`). The joiner then maps it to
  its enclosing symbol, where it used to report `unmapped-code … (no-document)` and leave every
  test that ran it incomplete. A closure whose creators name no single document stays unmapped.
- fix: with TestPrune.Core indexing F# `exception` declarations, an exception type's probes join
  to its symbol instead of `unmapped-code … (type-not-indexed)`. Core's `SchemaVersion` moved
  17 -> 18, which is the environment fingerprint's hash scheme, so traces recorded before it
  are recorded again.
- feat: hosts can own and cancel a traced prepare. `TraceSession.prepareProjectWith launcher ct`
  (with `ShadowBin.prepareWith`, `ShadowBin.verifyWith` and `Weaver.weaveWith` beneath it) takes a
  `Launch.Launcher`, which starts the JIT-verification child so a host can admit it into its own
  process scope, and a `CancellationToken`, checked between assemblies while weaving and used to
  abandon the verification launch. A cancelled prepare raises `OperationCanceledException` rather
  than returning a refusal, and leaves no cache entry a later prepare would reuse. `Launch.direct`
  is the default launcher; it now kills the child's process tree when its token is cancelled.
  `prepareProject`, `ShadowBin.prepare`, `ShadowBin.verify`, `Weaver.weave` and `Launch.run` are
  unchanged.
- fix: `Census.latest` takes each project's newest run of any status. A project whose newest run
  was refused or failed to record used to be reported from an older recorded run; it is now
  reported with that run's `Status` and `Reason` (new `ProjectCensus` fields) and measures
  nothing. `Census.passes` is false for a run that stored no traces; `Census.fails` is true for
  one that misses the bars or failed to record, and false for a refused one.
- fix: `ShadowBin.prepare` refuses an app that ships its own recorder build
  (`AppShipsOwnRecorder`: "the app ships its own TestPrune.Trace.Recorder build; tracing would
  replace it"), before weaving anything: its deps.json lists the recorder as a project, or its
  build output holds a recorder assembly that differs from the weaver's. The shadow bin used to
  swap in the weaver's recorder silently, so the recorder's own suite ran against a different,
  live recorder and failed. `DepsJson.recorderLibraryType` reads the listed library's type.
- fix: the isolation audit selects every sampled test alone. The display name is escaped for
  xUnit's `--filter-display-name` (`Audit.displayFilter`), which rejects a `*` anywhere but at
  either end, so a theory row such as `f(body: "let f (p: int * string) = p")` used to select
  nothing and every id it ran counted as extra. A sampled test the isolated run does not select
  is now an audit `ERROR` (`TestAudit.IsolationError`: "could not isolate <test>: the filter
  selected nothing") with no extra ids, never a comparison against nothing.
- feat: `Ctrf.run` launches a test app with a CTRF report and returns its outcomes; a report an
  earlier run left in the results directory is removed first.
- fix: `Launch.run` no longer passes the calling process's `TESTPRUNE_TRACE_*` variables to
  the child. Run inside a traced test process, a woven child used to inherit its dump
  directory and id count and write its dump into the parent's run.

## 0.1.0 - 2026-09-26

- docs: the README documents the `test-prune-traces` verbs (flags, exit codes, bars and
  caveats), what is refused or not recorded, and the first measurements on TestPrune's own
  suite; its code blocks are compiled and kept in sync by `syncdocs`. Decision records
  0005–0008 cover the exit-time dump, the separate store, packaging and the deferred options.
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
- feat: host facade (`TestPrune.Trace.TraceSession`): `prepareProject` builds the shadow bin and a
  fresh dump directory and returns the apphost and the exact environment a host launches it with;
  `ingestProject` stores the run's traces; `recordRefusal` stores a project that could not be traced
  as a `refused` run. Neither `prepareProject` nor `ingestProject` throws. An empty weave set is
  refused at prepare time, before the project runs. `Ctrf.parse` reads per-test outcomes from a
  CTRF report. The package README documents the host flow and the `Scopes` contract for tests that
  call an in-process server.
- feat: census (`TestPrune.Trace.Census`): reads each project's newest recorded run (or a named
  run) back out of the trace store and measures it against the phase-1 bars: traced / executed
  at least 0.99, and ambient / total hits under 0.001 or every ambient symbol listed for a human to
  explain. It also reports executed tests with no test scope, tests per incomplete-reason kind, and
  each pool scope with its symbols, inputs and linked tests. A missing database is refused, never
  created.
- feat: process-driving measurements. `Audit.run` runs a woven project once in parallel, then a
  seeded sample of its tests one at a time, and compares each test's own probe ids: an id
  attributed in parallel that the test never hits alone is contamination (the bar is none);
  ids missing in parallel are grouped by manifest kind, and every non-`cctor`/`gen` one is listed.
  It also counts the parallel run's tests per incomplete reason the dumps show without a join
  (`child-process-untraced:<file>`, overflow, rejected dumps). `Overhead.run` interleaves
  untraced and traced launches and compares median CPU (user + system of the process tree, from
  `getrusage`, never wall time), with each side's range. `FileCensus.run` compares the tests whose
  traces hold a repository file input with the tests that fail when an untraced copy of the build
  output runs outside the repository. `Rusage.children` reads `getrusage(RUSAGE_CHILDREN)` on macOS
  and Linux.
