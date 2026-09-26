# ADR-036: A type incompatible with itself is our fault, not yours

## Context

A full `fshw confirm` over a large repository produced 335 `reddenedBy` entries. 334 of
them were FCS diagnostics of one shape:

```
REDDENED fcs:.../src/Intelligence/Database/BriefQueries.fs: error This expression was expected to have type
  'Intelligence.Domain.BriefEntryEditV3.Edit' but here has type 'Intelligence.Domain.BriefEntryEditV3.Edit'
```

Both sides render identically. `dotnet build` over the same tree, at the same moment,
reported **0 errors in 84 seconds**. Every one of the 334 was phantom.

The daemon already knew about this shape and had for some time. `TestPrunePlugin.hasFcsErrors`
carries the sentence "cold-start FCS sometimes returns *expected type X but here has type X*
for files that compile cleanly once warm" and holds the prior symbol snapshot when it sees
one. So the fault had been diagnosed on the CACHE side and left untouched on the REPORTING
side: we distrusted the check enough not to write its symbols to the database, and trusted it
enough to tell the reader their code was broken.

### Why an identical render can only be our fault

The two `'…'` slots in these messages are not each type's `ToString()`. They are the output
of `NicePrint.minimalStringsOfTwoTypes`, whose entire job is to render two types **so a reader
can tell them apart**. It escalates until they differ. Measured against
FSharp.Compiler.Service 43.12.401, the version we pin:

| the two types | what the compiler prints |
| --- | --- |
| same name, different modules | `'A.T'` vs `'B.T'` |
| same name, different assemblies | `'Dup.T (LibA, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null)'` vs `'Dup.T (LibB, …)'` |

Both were produced from a real compilation, not recalled. So when both sides still render
identically, the compiler has exhausted every axis it knows how to distinguish types on —
name, namespace, assembly name, version, culture, public key token — and found nothing.
There is no edit the reader could make in response, because nothing was said to differ.

Whatever produced the diagnostic is therefore inside our checking, not inside the code being
checked.

### What causes it is NOT known, and this decision does not depend on knowing

The obvious story — the checker holding two `FSharpEntity` objects for one type, from two
live compilations of the same assembly — is a guess. The one concrete version of it that was
investigated, a `LoadTime`/`Stamp` defect reaching `AreSameForChecking`, was measured and
**retracted**: `useTransparentCompiler` is on and TransparentCompiler never calls that
function, so the mechanism was real but on a path FCS does not take.

That retraction changes nothing above, because the argument above is about what the COMPILER
SAID, not about why it said it. A diagnostic that names no difference cannot be acted on,
whatever produced it. The two must be kept apart: an incident proves a symptom, never a
diagnosis, and a future mechanism story — or its retraction — is evidence for neither this
predicate nor against it.

It does change what this guard IS. It is not a belt-and-braces measure sitting alongside a
fix; with no fix in existence, it is the only thing between these diagnostics and a red gate.
That makes the negative controls load-bearing rather than decorative: nothing else would
catch a guard that had started swallowing real type errors.

### Why the structured data is the wrong place to read it

`FSharpDiagnostic.ExtendedData` can carry a `TypeMismatchDiagnosticExtendedData` with
`ExpectedType` and `ActualType` as `FSharpType` values. Reaching for those instead of the
rendered message looks like the more robust choice and is strictly worse. Formatting each
`FSharpType` on its own does not run the escalation above, so the genuine cross-assembly
conflict in the table would format as `Dup.T` and `Dup.T` — the exact false positive this
guard must not have. The rendered message **is** the compiler's own verdict on whether the
two types are distinguishable, which is the question being asked. (It is also the only one of
the two a test can construct: `TypeMismatchDiagnosticExtendedData` has no public constructor.)

## Decision

- **A self-incompatible diagnostic has no reportable representation.**
  `FcsDiagnosticFilter.classifyDiagnostic` returns `FcsDiagnosticClass`, whose
  `SelfIncompatible` case carries a `SelfIncompatibleDiagnostic` — never an `ErrorEntry`. No
  function turns one into the other. The ledger's reporting path takes `ErrorEntry` values, so
  a self-incompatible diagnostic is not filtered out at the edge; it never has the shape the
  edge accepts.

- **The predicate is the three two-type message families, and only those.** Taken verbatim
  from `FSStrings.resources`: `ErrorFromAddingTypeEquation1`, `ErrorFromAddingTypeEquation2`,
  `ErrorsFromAddingSubsumptionConstraint`. The tuple-shaped siblings are excluded: they
  compare a tuple against a non-tuple, or two tuples of different length, so their two slots
  are not two renderings of one question.

- **A trailing explanation defeats the guard.** Every template ends with a `{2}` slot the
  compiler fills with type-parameter constraint text — the place it puts the distinguishing
  information when the two names alone do not carry it. `tryRenderedTypePair` requires that
  slot empty. Everything about the parse fails CLOSED: an unrecognised message is reported as
  an ordinary diagnostic.

- **One re-check is attempted, bounded per project — and never trusted.**
  `CheckPipeline.CheckFileCore` drops the project's checker configuration and re-checks the
  file once. With the cause unknown this is a guess at the class of thing that might clear it,
  not a derived remedy; it is kept because it is cheap and bounded, and the bound is per
  project per cooldown rather than per occurrence because the incident produced 334 of these
  in one run and 334 project re-typechecks would cost more than the bug. Because it is not
  known to work, a survivor is surfaced rather than assumed cured.

- **A survivor is surfaced as OUR fault, at a severity that cannot redden.** It is reported
  under the `fcs-internal` ledger key at `Info`, and logged at `warn` naming the project and
  file, so the rate is measurable rather than invisible. With no known cause, that count is
  the only evidence a future investigation will have; the entry says plainly that the cause is
  unknown and asks for a report, rather than naming a mechanism we cannot stand behind. A reader must be able to separate
  "your code is wrong" from "our checker went stale"; a shared ledger key makes that
  impossible, and an `Error` — or a `Warning` under `warningsAreFailures` — would redden a run
  on evidence that says nothing about the code.

- **The cache-poisoning gate is deliberately NOT taught this.** `hasFcsErrors` keeps tripping
  on self-incompatible diagnostics. Its job is to refuse to trust a poisoned check, and a
  phantom diagnostic is precisely the signal that a check was poisoned. Teaching it to ignore
  one would make it flush symbols from the run it most needs to distrust. The asymmetry is the
  decision, not an oversight: the two paths ask opposite questions.

## Consequences

- A run whose only reds were phantom is now green — correctly. The incident's tree compiled
  cleanly under `dotnet build`.
- A genuine same-name conflict across assemblies still reddens, because the compiler
  distinguishes it. This is pinned by a negative-control test built from the real rendered
  form measured above.
- We can now count how often this happens. Before this, the number was buried in reds
  attributed to user code — which is also why nobody could investigate it.
- **Naming.** The identifiers say `selfIncompatible` and `recheck`, never `staleEntity` or
  `assemblyIdentityConflict`. A name is a claim, and the only claim we can support is the
  observation: the compiler reported a type against itself.

## Cause, found later

One cause is now known, reproduced, and fixed (`CheckerEvictionTests`,
`Daemon.checkerCacheSizes`). FCS keys a file's type-check (`TcIntermediate`) by the
content of the file and of the files before it — not by the type-check results of the
files above it that it was computed from — and releases entries least-recently-used
first. A check reaches a project's files in dependency order, so once the files checked
outnumber the entries kept strongly (`20 × cacheSizeFactor`), a project's first files are
released first and a collection frees them. The next check computes them again, declaring
their types a second time, and folds in the downstream entries it still holds, which name
the first declaration. Cancellation and invalidation play no part.

The daemon's checker now keeps every current per-file type-check, whatever the factor.
The decision above is unchanged: the guard still reads what the compiler said, not why,
and still stands between any other path and a red run.
