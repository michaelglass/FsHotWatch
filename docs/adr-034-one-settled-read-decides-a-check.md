# ADR-034: One settled read decides a check

## Context

`fshw check` read the daemon's diagnostics, coverage and test scope, and if that reading was
INCOMPLETE but carried no failures it re-scanned and read again — up to three times —
keeping whichever read looked best. The comparison was made on an "unchecked magnitude": a
coverage reading mapped onto an int, with `Unknown` mapped to `Int32.MaxValue` so that
`Unknown → Incomplete` could be counted as progress.

The loop held no second opinion about what a reading MEANS: every attempt went through the
same `CheckVerdict.verdict`. Its only power was to take a LATER reading. Every outcome it
treated as terminal carried a comment saying why re-scanning could not help — a re-scan does
not earn a baseline, un-kill a host, un-defer a test, clear stale daemon state, or widen the
scope of a run that already happened. `Incomplete` was the one case left, and it was treated
as a cue to look again rather than as the answer.

What changed underneath it: the reading is taken after settling, and settling now means the
host owns no work AND holds evidence for the current project model (ADR-030, ADR-033). A
later reading is therefore not a better answer about this tree; it is an answer about a
different one. Taking it is how a check reported green about a state it never verified.

## Decision

- **One settled read decides.** `CheckVerdict.converge` and `IpcOutput.MaxConvergeAttempts`
  are deleted. Both the daemon path (`pollAndRender`) and the daemon-less path
  (`RunOnceCheck`) compute `CheckVerdict.verdict` from the reading they already have.
- **`Incomplete` is an answer, not a prompt.** A reading that says files went unchecked exits
  2 — "could not complete, retry" — rather than spending three more full passes to say so.
- **Coverage is read, never ranked.** `uncheckedMagnitude` and its `Int32.MaxValue` sentinel
  are deleted with the loop. `verdict` already reads `Unknown` as `Incomplete -1` ("the
  daemon did not report a count"), which is exit 2 and never a green.
- **No scan trigger reaches the render loop.** `pollAndRender`'s `triggerScan` parameter is
  deleted: a caller passing a re-scan thunk that can never fire is the same residue as the
  loop itself. The pre-verdict scan still runs once, before the reading.
- **`explainOutcome` loses its re-scan count.** The parameter existed to say "after N
  re-scan attempt(s)"; with no path re-scanning, it could only ever be `None`.

## Consequences

- A `check` over a tree whose coverage is incomplete exits 2 on the FIRST reading. Where it
  previously re-scanned and sometimes turned green, it now reports the incompleteness. That
  is the intended change: the green it used to produce was about a later tree.
- Three fewer full scans in the worst case, and no "Re-scanning (incomplete)" phase.
- The user-facing advice for a no-verdict check no longer promises a convergence loop; it
  says to re-run once the tests it needs have run.
- Retention of executed evidence across a quiet reading is unchanged. It was never a
  property of the loop: `TestRunEvidence` (`observeTestRun`) puts every reading through the
  same fold, and the tests for it moved to that seam, where they can be stated without a
  loop to produce the sequence.

## Not in this change

- **The scan's residual `None` retry stays.** `Daemon.runChecksWithRetry` re-checks files
  whose per-file check returned `None`, and the plan for this slice proposed deleting that
  handling on the grounds that the cancelling change batch publishes the file itself. That
  argument covers ONE of the causes of `None`. `CheckPipeline` also returns `None` when the
  check throws (a transient fault while MSBuild rewrites `obj/`, for instance) and when a
  file has no project options — and in those cases nothing else publishes anything for that
  file. The retry therefore has behaviour of its own, and it was kept rather than deleted on
  a plausible story. See the note below on what would make it safe to remove.
- **A truncated scan has no receipt-level guard in a repo with tests.**
  `Events.AnalysisEvidence.fromCompleted` mints nothing when test projects are configured,
  so the cohort-seal refusal that would catch an unchecked file does not apply there. Making
  the scan's cohort account for every checkable file of the model regardless of whether tests
  are configured would give the retry's guarantee an evidence-level home — and only then is
  deleting it a deletion rather than a loss.
