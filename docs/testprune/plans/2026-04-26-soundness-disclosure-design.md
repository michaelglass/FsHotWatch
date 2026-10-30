# Soundness disclosure

**Status:** design only — not scheduled for implementation.

## The problem

TestPrune builds its symbol dependency graph statically from the F# AST via
FSharp.Compiler.Service. That graph is the authority for "what could this
test reach?" — but the AST cannot see every edge that exists at runtime.
When a real edge is missing from the graph, TestPrune may skip a test
that would have failed if run. That is **unsound test selection**, and
it is the worst failure mode the tool has: silent, looks like a green CI,
masks regressions.

Today we treat soundness as an implicit property — users who happen to
read the code or hit a bad miss learn the boundaries the hard way. We
should make it a first-class, surfaced concept instead.

## What "unsound" means here

A test selection is *sound* if every test that would have observed the
change is in the selected set. We are unsound whenever the static graph
is missing an edge that exists at runtime. Concretely, the F# constructs
that produce hidden edges are:

- **Reflection.** `typeof<_>`, `Type.GetType`, `Activator.CreateInstance`,
  `MethodInfo.Invoke`, attribute-driven discovery.
- **Type providers.** Generated types and members do not exist in the
  source AST; downstream references look like dependencies on the
  provider invocation, not on the generated surface.
- **SRTP / `inline` resolution.** Statically-resolved type parameters
  resolve at each call site; the "callee" is structural, not a fixed
  symbol. Our graph cannot enumerate every concrete resolution.
- **Computation expressions.** Builder method dispatch (`Bind`, `Return`,
  `Yield`, custom operations) is implicit; users writing `let!` rarely
  see the symbol they are actually depending on.
- **DI containers and service locators.** `IServiceProvider.GetService`,
  Autofac, etc. — the binding from interface to implementation lives in
  configuration, not in source.
- **String-keyed dispatch.** Falco route strings, configuration keys,
  `nameof` round-trips, dynamic dispatch tables. (TestPrune.Falco
  partially addresses the Falco case; the general pattern remains.)
- **Native interop / P-Invoke.** Out of scope for static F# analysis.
- **Build-time codegen** that runs outside FCS (Paket targets, T4-style
  generators, source generators we do not consume).

## Proposal

Three pieces, ordered by cost.

### 1. A documented unsoundness list

Ship a `docs/soundness.md` page that enumerates the constructs above,
explains why each one defeats static analysis, and states what TestPrune
does in practice (typically: records an edge to the *call site* but not
to the runtime target). This is mostly writing — no code — and it earns
credibility the way Ekstazi's papers do by being explicit about
limitations rather than hiding them.

### 2. Detection during indexing

When the AST walker encounters one of these constructs, record a
soundness note alongside the edge. Schema sketch (additive to the
existing SQLite store):

```
soundness_notes(
  symbol_id     INTEGER,   -- the symbol whose body contains the construct
  kind          TEXT,      -- 'reflection' | 'type_provider' | 'srtp' | ...
  detail        TEXT,      -- e.g. the reflected type name if known
  source_range  TEXT       -- file:line:col for the diagnostic
)
```

Detection is per-construct and varies in difficulty:

- **Easy:** syntactic patterns — `typeof`, `Type.GetType`, attribute
  usage, computation expression `let!`/`do!`/`yield!`, `inline` function
  declarations. These are AST node kinds.
- **Medium:** SRTP call sites (need the symbol-use info FCS already
  provides), type-provider-generated symbols (FCS marks these).
- **Hard / out of scope:** DI resolution, string-keyed dispatch.
  Document them; do not try to detect them automatically.

### 3. Surfacing in CLI output

Two surfaces, both opt-in-by-default-on:

- **`test-prune run`** prints a one-line summary when the *selected
  closure* contains soundness notes:

  ```
  selected 12 tests (skipped 187)
  ⚠ 3 selected tests touch reflection; 1 touches a type provider.
    re-run with --explain-soundness for details, or --sound to fall
    back to running all tests in affected files.
  ```

- **`test-prune status`** has a new section listing soundness notes for
  the change set, grouped by kind. Useful as a code-review aid: "this
  PR adds a new `Type.GetType` call — TestPrune will be blind here."

A `--sound` flag widens selection to include any test transitively
reaching a symbol with a soundness note in the changed closure. This
trades precision for safety and is the right default for release
branches.

## What this is not

- **Not a correctness guarantee.** Detection is best-effort; the
  enumerated list will always be incomplete. The point is to convert
  *silent* unsoundness into *visible* unsoundness wherever we can.
- **Not a fix.** We are not trying to resolve reflection or DI
  statically. That belongs to the (separate) FsHotWatch project, where
  a dynamic recorder can observe real edges and fold them into the
  graph.
- **Not coupled to FsHotWatch.** Everything proposed here lives in
  TestPrune.Core and the CLI. FsHotWatch consumes the soundness notes
  the same way it consumes any other graph data.

## Open questions

- Do we want a soundness *score* (count of notes in selected closure)
  exposed as a metric, or is the per-construct list enough? A single
  number is easy to ratchet in CI; a list is more honest.
- Should `--sound` be the default in CI and `--fast` (current behavior)
  the opt-in? Probably yes, but it's a behavior change worth a major
  version bump.
- Type providers: do we mark the *consumer* of a provided type as
  unsound, or the provider declaration? Consumer is more useful for
  test selection but noisier in the diagnostic.

## Prior art

- Ekstazi papers explicitly enumerate unsoundness sources (reflection,
  classloaders, native code) and report empirical miss rates. We
  should do the same in `docs/soundness.md`.
- Microsoft TIA documents that it is "scoped to managed code, single
  machine" — a coarser disclosure but the same pattern: state your
  boundaries up front.
- Bazel sidesteps the issue by requiring manual `BUILD` declarations;
  unsoundness becomes a human bug rather than a tool bug. We are not
  going there, but it is the alternative design point.
