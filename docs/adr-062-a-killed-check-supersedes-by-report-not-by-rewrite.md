# ADR-062: a killed check supersedes an older verdict by report, not by rewrite

Status: Accepted (2026-10-30). AUTOMATION-1420.

## Context

AUTOMATION-1416 made a check that ends abnormally (returned without publishing, signalled,
exception) replace an older verdict with its own `incomplete` record
(`Verdict.tryPublishTerminal`). A check killed with SIGKILL runs no cleanup, so that record is
never written and the older green, over the same tree, stays readable as the answer. The killed
check leaves one trace: its run claim in `.fshw/in-flight/`, with a dead pid.

## Decision

Every verdict read (`fshw verdict`, `confirm`'s reuse through `priorConfirmation`, and the
nothing-to-run keep-green path that uses it) treats an abandoned claim that started after the
verdict's `producedAt`, and is not the verdict's own invocation, as superseding it. The read
reports `Stale` (exit 4) with `superseded: the last check (pid N on HOST, started T) died without
a verdict`. A died claim is kept on disk until a verdict published after it began makes it moot;
only then is it reaped (`RunClaim.survey`). `RunClaim.live` no longer reaps anything.

## Rejected: the reader rewrites verdict.json to an `incomplete` record

It could be done under the verdict write lock with the same compare `tryPublishTerminal` makes.
Rejected because:

- `fshw verdict` is a read that cannot perturb (ADR-013, ADR-019). A rewrite would make a reader
  a publisher, stamping the record with the reader's binary, the reader's tree hash and the
  reader's clock rather than the dead check's.
- The report is enough. The claim file is durable evidence, and every reader reaches the same
  answer from it until a newer verdict lands, so nothing is gained by copying it into the verdict.

## Rejected: a new exit code

Exit 4 already means "a verdict exists, do not reuse it, run again", which is the right action.
Consumers that gate on `fshw verdict` already treat 4 as not green; a new code would be one they
do not yet handle.
