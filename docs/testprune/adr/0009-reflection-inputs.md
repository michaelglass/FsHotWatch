# ADR 0009 — Reflection inputs: union reflection is a symbol, a type sweep is an input

Status: Accepted (2026-11-05)

Two kinds of reflection change a test's outcome while no traced method changes: enumerating
a union's cases (add a case) and sweeping an assembly's types (add a type). The phase-2
plan's Task 11 calls for recording both (AUTOMATION-1495).

- **Union reflection is a `type` input, joined to the union's symbol.** The recorder
  redirects every `FSharpType`/`FSharpValue` union API that takes a `Type` or a
  `UnionCaseInfo` to the `Reflect` shims. Each shim notes `<assembly file>|<CLR name>`.
  Ingestion adds the type's symbol at its current version, so R2 reselects the test when
  the union's header hash moves. Adding a nullary case moves that hash and no case's hash
  (pinned in AstAnalyzerTests). The join is made only for an assembly of the weave set. A
  repository type the index does not know marks the trace incomplete. A type of any other
  assembly (FSharp.Core's `option`, a package's union) is ignored, even when its file sits
  in the repository's `bin/`.
- **A type sweep is a `types` input.** `Assembly.GetTypes`/`GetExportedTypes`/
  `DefinedTypes`/`ExportedTypes` and `Module.GetTypes` note the assembly. Its hash is the
  SHA-256 of the sorted type-definition names, read from metadata, so R3 reselects the test
  when a type is added or removed.
- **The union-type mutant** (`mutate --union-samples`, `TESTPRUNE_TRACE_MUTATE_TYPES`) makes
  every union-reflection call on one union throw. A union is a candidate only if the
  baseline run reflected over it. The mutant is judged against the tests whose trace
  reflected over the union, which is the selection "the union changed" produces.
- **Deferred: Type probes at `ldtoken` and at repository generic instantiations.** The
  shims see the runtime `Type`, however it was obtained (`typeof<'T>` inside a generic
  helper, `value.GetType()`), so union enumeration through FSharp.Core is covered without
  them. They would add a probe to every `typeof` and every repository generic call. What
  they would add is reflection that does not go through FSharp.Core or a sweep, for
  example `typeof<U>.GetProperties()`. That is reconsidered if a mutation sample misses a
  test that reflects that way.
