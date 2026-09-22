# A symbol has one node and one source occurrence per declaring file

Supersedes [ADR 0002](0002-signature-declaration-nodes.md).

A public F# declaration in a signature file (`.fsi`) and its implementation
have the same compiler identity. The signature is not code of its own. It
describes the implementation. ADR 0002 still gave it a second graph node under
the reserved namespace `TestPrune.__Signature__.`, joined to the canonical
symbol by a bridging edge. The only reason was storage: a `symbols` row held
one `source_file`, line range and `content_hash`, re-indexing a file deleted
rows by `source_file`, and so the last file indexed overwrote the other's hash.
That split cost a string-prefix convention the compiler cannot check. Every
consumer that enumerates symbols (dead code, reports, anything new) had to know
to skip the prefix.

The store now models the declaration directly. A `symbols` row is the node:
its name, kind, containment parent and whether it is only an extern
placeholder. Each file that declares it contributes a `symbol_occurrences` row
with that file's location and content hash. Both files analyze to the same
canonical names. The signature's occurrence changes when the signature changes,
so its consumers are selected by the ordinary edges to that name, without any
bridge.

Per-file facts are owned by the file whose analysis produced them, not by the
symbol they hang off: dependency edges, test methods and attributes each carry
a `source_file`. `AstAnalyzer.factOwner` states the rule once. A fact belongs
to its anchor symbol's occurrence in the same analysis result, or, when the
anchor is declared elsewhere, to the result's single file. A shared-literal
bridge is anchored at its producer. Both the SQLite store and the in-memory
store use this rule. Re-indexing `Library.fs` therefore replaces only
`Library.fs`'s occurrences and edges. `Library.fsi`'s occurrence and the edges
it contributes survive, for example the types named in a `val` signature. A
symbol row survives while any occurrence remains. A real symbol with no
occurrence left was removed from the code, so it is deleted and its incoming
edges go with it, as before. Coverage points belong to an occurrence, because
their offsets are measured from that occurrence's lines.

`GetSymbolsInFile` and `GetAllSymbols` return occurrences, so a symbol declared
in a signature appears once per declaring file. Dead-code reporting shows one
occurrence per name and prefers the implementation, since that is the code to
delete.

ADR 0002 rejected an occurrences table because it "would require a schema
migration". The index is a cache: a `SchemaVersion` bump (v14) deletes and
rebuilds it. The real cost was code, meaning source lookup, orphan removal and
fact ownership in both stores, and that code is the change recorded here.

Fact ownership widens each edge row with its `source_file`. That would slow the
reverse walk in `QueryAffectedTests`, so `idx_deps_to` now covers
`(to_symbol_id, from_symbol_id)` and the walk reads no table rows. On a synthetic
graph (50,000 symbols, about 200,000 edges, 5,000 tests), measured in process CPU
time against the v13 store over four alternating rounds, 30 selection queries
chose the identical 88,664 tests in about 40% less time. A single-file re-index
cost the same, within noise. A full rebuild's SQLite writes cost about 37% more
(7.4 s to 10.1 s), because each declaration now writes a symbol row and an
occurrence row.

ADR 0002's regressions pass unchanged against this model: changed, removed and
unchanged declarations, full indexing, implementation-only and signature-only
replacement, and reversed file order. `SymbolOccurrenceStoreTests` pins the
store seam with hand-built results. Re-indexing either file ALONE keeps the
other file's occurrence, edges and consumer selection; this is the 8.2.0
under-selection, tested in both directions. SQLite and the in-memory store agree
on every port read whichever file was re-indexed last. A v13 database is
recreated as v14 through the schema-version path. `SignatureOccurrenceTests`
pins the analysis seam: both files emit the canonical names, each with its own
hash, with no synthetic namespace, and a file without a signature keeps its
ordinary graph.
