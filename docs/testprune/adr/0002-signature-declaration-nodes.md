# Signature declarations have separate graph nodes

Superseded by [ADR 0004](0004-symbol-source-occurrences.md): a signature
declaration is now a source occurrence of the canonical symbol.

A public F# declaration in a signature and its implementation have the same
compiler symbol identity. The symbol index stores one row per full name, so
using that identity for both source occurrences overwrites one file's hash.
Signature syntax also needs declaration ranges: implementation binding visitors
do not visit `val` declarations, and identifier ranges miss multiline types.

Keep canonical names for implementation symbols and consumer references. Store
signature declarations under the reserved synthetic namespace
`TestPrune.__Signature__.` followed by the canonical name. These nodes represent
source declarations, not additional callable functions. Their source location
and content hash belong to the signature. A canonical-symbol-to-declaration
edge makes a signature edit reach the canonical symbol's consumers.

Both signature analysis and implementation analysis emit this edge. The latter
uses the compiler symbol's `SignatureLocation`, without reading adjacent files.
This is required because replacing an implementation's analysis clears its old
outgoing edges. Extern placeholders preserve the bridge when the other file is
indexed later; they cannot replace a real declaration's source or hash.

The signature visitor includes value and type declaration ranges, including
nested declarations and member signatures. Signature-local dependencies and
containment links refer to declaration nodes. Signature nodes are not separate
dead-code recommendations: the canonical implementation is the actionable code.

A separate source-occurrences table was considered. It would require a schema
migration and changes to source lookup, orphan removal, and dependency ownership
in both persistent and in-memory stores. Separate graph nodes use the existing
synthetic-node mechanism and keep source ownership explicit without changing
those storage contracts. The in-memory store follows the same real-declaration
preference as SQLite when placeholders and declarations appear in either order.
A general occurrence model remains appropriate if the
index later needs to represent arbitrary multiple implementations of one name.

The regression uses compiler analysis, the SQLite store, and the standalone
runner. It checks changed, removed, and unchanged declarations; full indexing;
implementation-only and signature-only replacement; and reversed file order.
