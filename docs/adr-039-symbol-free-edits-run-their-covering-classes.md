# ADR-039: A symbol-free source edit runs the classes covering the edited file

Status: Accepted (2026-10-17)

## Context

A verdict binds to exact bytes. After a green, an edit the symbol diff sees as changing no
symbol (a comment, a moved line) leaves no test evidence bound to the new tree, so the
zero-affected skip is refused (`ZeroAffectedWidening.UnverifiedTree`). That rule closed a
"no tests ran (the daemon did not say why)" false green, and it stays.

What it cost: the refusal launched an empty selection, which runs every configured test
project in full. On this repository, one appended comment line in `src/FsHotWatch/IdleExit.fs`
after a green check ran FsHotWatch.Tests and FsHotWatch.Bench.Tests unfiltered.

## Decision

When the zero-affected skip is refused ONLY because the tree moved, nothing is owed, and
every path that moved since the receipt's tree is a compiled `.fs` file that the symbol diff
has folded, run the classes covering ANY symbol each edited file holds, plus the test classes
declared in it. The run earns a receipt for the edited tree, as any impact-filtered run does.

The edited tree is then verified at the standard every symbol edit's own selection meets: the
tests whose outcome a symbol-free edit to `F` can change are tests that execute code from `F`
(a line number, caller info, stack-trace text, a body the diff hashed the same), and each of
them covers one of `F`'s symbols. Seeding all of `F`'s symbols, not the changed ones, is what
makes a behaviour change the diff missed still reach the tests that see it.

Every other shape keeps the whole-project run, and the refusal reason is logged beside the
`UnverifiedTree` line:

- the receipt tree's manifest is not held (a restart), or the current tree cannot be read;
- no project model publishes its compiled files;
- the last `BatchChecked` did not fold the current tree (the diff is not in hand);
- any added or removed path, or a moved path that is not a compiled `.fs` file (`.fsi`,
  `.fsproj`, props, data files, declared verdict inputs);
- an edited file the impact index holds no symbol for, or whose symbols no runnable test
  covers.

The receipt tree's entries are kept in state (`EvidenceManifest`) beside the receipt, and read
only while their identity is the receipt's `InputTreeHash`.

## Rejected

- **Prove the edit semantically empty (same IL or typed AST minus debug info) and carry the
  prior evidence forward.** F# names closures and generated types after source lines
  (`clo@45`), so most comment or blank-line edits that are not at the end of a file change the
  IL anyway, and the typed tree has no range-free equality to compare. A comparator that
  strips MVIDs, debug directories and line-derived names is large and itself a soundness
  surface. Low hit rate for high risk.
- **Run only the symbols the diff reported changed.** It reported none; that is the case.
- **Require no `#if` in the edited file.** A toggled preprocessor branch that changes a
  definition changes the symbol diff and never reaches this path. One that changes a body is a
  body change, and seeding every symbol of the file covers it.

## Accepted limitation

A test that reads another file's source text without executing it (a source guard) is not
selected by a symbol edit's impact selection either, and is not selected here. `confirm` (the
full, unfiltered suite) remains the gate for it.
