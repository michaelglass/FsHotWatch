# ADR-019: A verdict is current only while no other run is in flight over its tree

Status: Accepted (2026-09-05)

## Context

ADR-013 made the verdict a file content-addressed to its SUBJECT: it carries the hash
of the tree it verified and the identity of the binary that produced it, and `fshw
verdict` refuses it — exit 4 — when either link breaks. That closed the question "is
this verdict about the code I am looking at?".

It left the question "is this verdict about the run that is happening?" unasked.

The verdict is stamped ONCE, at completion. Between a run's start and its publish, the
file on disk is the previous run's result. When the tree has not moved — and it usually
has not; a `confirm` after a `check`, a re-run of the same tree, a daemon's convergence
pass — that result parses cleanly, its `treeHash` matches, its producer matches, and it
reads GREEN. Only `command` and `producedAt` said anything different, and separating
"the run that finished at 14:38" from "the run that started at 14:39" by comparing two
timestamps by eye is not a machine-readable answer. It is a puzzle, and it has been
solved wrongly in practice.

Driven through the shipped 0.14.0-alpha.40 assembly, on a tree with a green verdict and
a run in flight:

```
mid-run report = Applies (outcome=Green, exit=0)
mid-run exit   = 0
mid-run json   = { "applies": true, ... }
Applicability cases: [|"Applies"; "StaleTree"; "StaleProducer"; "StaleAlgorithm"|]
```

Nothing in `.fshw/` recorded that a run was in flight — the directory held
`verdict.json` and `verdict.write.lock` and nothing else — so no reader could have
answered the question even if it had thought to ask.

Under continuous verification this is not an edge case. If the verdict file IS the
queried state and something is nearly always running, then nearly every read is a read
against an in-flight run, and a consumer that polls sees a stale green routinely.

## Decision

**A run CLAIMS the repo for its duration, and a verdict is current only while no OTHER
run holds a claim over the tree it describes.**

Three pieces:

1. **The claim is a file** — `.fshw/in-flight/<invocationId>.json`, written before the
   run starts and removed after its verdict is on disk. A file, not a socket query,
   because `fshw verdict` starts no daemon and contacts nothing: a read must not be able
   to perturb what it reads. ONE FILE PER INVOCATION, never a shared one, so two
   concurrent runs in a workspace cannot release each other's claim.

2. **The claim is taken by the run bracket.** `withRunHooksCommandUsingSignals` already
   wraps both transports (the daemon path and `--run-once`) and already owns the one
   finalizer every exit passes through — a normal return, an exception, a signal. The
   claim is acquired beside the invocation id and released inside that finalizer, AFTER
   the verdict write, so there is no instant in which the file is both unclaimed and
   describing an earlier run.

3. **The reader joins on the invocation id, not the clock.** A claim carrying the
   verdict's own `attribution.invocationId` is the run that PUBLISHED it, and is not a
   refusal; any other live claim is. This is exact and clock-free — no timestamp
   comparison, no skew, no "close enough".

The answer reaches consumers as a fifth `Applicability` case (`RunInFlight`), a third
`Report` case (`InFlight`), **exit code 6**, and an additive `inFlight` boolean beside
the existing `applies` in the `fshw-verdict-report-v1` envelope.

### Which question this answers

Not "is any run in flight" — the loose question, which would false-alarm on a run
started before an edit, over a tree that has since moved. The COMPOSITE question: *does
the verdict on disk describe the run that is happening now?* The in-flight check is
reached only after the producer, algorithm and tree checks have all passed, so a run
over some other tree never clouds a verdict that genuinely applies to what is on disk.

### Why 6 and not 4

Because the consequence differs. 4 says "the code moved — go and re-run". 6 says "the
answer is being computed — waiting is what closes this". Folding them together would
tell a reader to start a second check against a tree that is already being verified,
which is how a box ends up with two runs racing for one answer.

### Why the existing exit codes are untouched

The in-flight question is asked ONLY on the path that would otherwise return `Applies`.
Every pre-existing staleness answer — 4 for a moved tree, a different binary or a
different hashing scheme, 5 for no usable verdict — is reached exactly as before, even
when a run is in flight at the same time. The new gate sits across the one road a stale
green could ever have travelled, and across nothing else.

### The `confirm` carve-out

`Verdict.priorConfirmation` — `confirm`'s fast path, the only green in fshw allowed to
cross a process boundary — still accepts a full-suite green over this tree while a run
is in flight, and this is deliberate. Its question is not "what is the current state?"
but "has this evidence already been EARNED?". A full-suite green, over this exact tree,
from this exact binary, was earned when it was earned; a later run cannot un-earn it.
Refusing would disable confirm's only fast path whenever anything else in the workspace
was running — most of the time, under continuous verification — and would buy nothing,
because the re-run would have to produce the same evidence again. The WIRE still refuses:
`fshw verdict` reports 6.

## Consequences

* A poll loop running for the duration of a check never observes a green attributable to
  the previous run. That is the benchmark, and it is a test.
* A crashed run does not wedge a workspace. A claim whose process is provably gone is
  abandoned and reaped by the next command, the same contract `daemon.pid` has.
* Every unknown leans HELD — a foreign host whose pids cannot be probed, a probe that
  errored, a claim file this build cannot parse. Unparseable is not absent: what is
  unknown is WHO is running, never WHETHER anyone is.
* Consumers that read only `applies` are correct without changing: mid-run it is
  `false`. Consumers that act on the exit code must learn 6, or they will read it as an
  unrecognized failure. `RecallMiss.applicabilityOfExit`-style mappings in intelligence
  need the new code adding.

## Alternatives rejected

**Stamp the verdict file itself as in-progress at run start.** It makes the verdict its
own concurrency record, so a crashed run leaves a file that says "running" forever with
nothing to expire it, and it destroys the previous run's result — which is exactly the
evidence a reader wants beside the refusal, and which a completed no-op re-scan is
required to preserve.

**Compare `producedAt` against a run-start timestamp.** Two clocks, one of them the
file system's, deciding whether a green may be used for a merge. The invocation id is
already recorded, already exact, and needs no clock at all.

**Ask the daemon.** `fshw verdict` is a pure read that starts nothing and contacts
nothing, deliberately: an observation that can start a daemon is an observation that
perturbs. And the `--run-once` path has no daemon to ask.
