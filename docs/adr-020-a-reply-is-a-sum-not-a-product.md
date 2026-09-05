# ADR-020: A reply is a sum, not a product — the ledger is bounded at every mirror

Status: Accepted (2026-09-05)

Relates to: [ADR-013](adr-013-the-verdict-is-a-file-content-addressed-to-its-tree.md)
(the verdict is a file). ADR-013 established that a finished run leaves a
machine-readable answer behind. This one is about the run that finished and left
nothing, because the CLI died on the last call before it could write one.

## Context

On 2026-09-05 a merge gate in a consuming repository was admitted seven times over
roughly two hours and produced a verdict none of those times. Each attempt ran to
completion — the daemon log reads, in order, `Tests complete: 7 projects, 117.1s`,
`Committing 23 verified symbol(s)`, `[rpc] WaitForComplete() resolved`, then an idle
heartbeat — and six seconds later the CLI exited 2 with:

> The fshw CLI ran out of memory while handling daemon IPC: Insufficient memory to
> continue the execution of the program.

Two hours of investigation went to the client. `DOTNET_GCConserveMemory=9` was set on
the CLI and changed nothing. The box's free memory was measured repeatedly at 6–8 GB.
None of it could have mattered.

### What the reply actually was

The call that failed is `GetDiagnostics` — the ledger read the CLI makes immediately
after the settle, which is what the exit code and `verdict.json`'s `reddenedBy` are
computed from. The tree under test was broadly red: 1,661 of 1,832 failing in one test
project and 753 of 779 in another, 2,416 failing tests in all.

`TestPrunePlugin.failuresOf` attached `output` — the **whole captured run of the
project** — as the `Detail` of **every parsed per-test failure**. In the daemon's heap
that is free: 753 entries holding one string reference. On the wire it is the product.
The captured outputs for that run are still on disk and were measured:

| project | failing entries | captured output | `detail` on the wire |
|---|---:|---:|---:|
| `Intelligence.Tests.Integration` | 753 | 50,660,713 B | ~36 GB |
| `Intelligence.Tests.Database` | 1,661 | 5,355,163 B | ~8.5 GB |

`JsonSerializer.Serialize` returns a `string`. Long before 45 GB it is asking for an
allocation .NET cannot make, and it raises `OutOfMemoryException` — whose message is
`Insufficient memory to continue the execution of the program.`

### Why the message named the wrong process

A fault raised on the daemon crosses StreamJsonRpc as `RemoteInvocationException` and
is reconstructed on the client from `CommonErrorData`. `classifyIpcFault` then asked
one question of it — "did this arise in `HeaderDelimitedMessageHandler`?" — and mapped
every OOM that had not to `ClientOutOfMemory`. There was no case for a daemon OOM, so a
daemon OOM was *by construction* reported as the CLI's. The evidence needed to tell
them apart is not in the stack trace; it is knowing which side reported the fault, and
only the caller knows that.

`detail` is also read by nothing. `RunOnceOutput.formatErrors` renders `Message`;
`agentDiagnosticLine` renders `Message`; `reddenedBy` records `Message`. The field that
killed seven gates has never been printed.

### The same defect, one mirror earlier

`FileErrorReporter` already carried this comment:

> A misbehaving plugin can stuff an entire test-suite output into a diagnostic
> message/detail (observed when many integration tests fail at once), which crashes the
> reporter and wedges the daemon.

It capped its own fields and stopped there. The IPC mirror of the same ledger had no
cap at all, so the identical product survived one mirror over — and killed the merge
gate instead of the reporter.

## Decision

**1. The bound lives above every mirror.** `ErrorLedger.Transport` owns a per-field cap
and a response-wide `detail` budget, and both `FileErrorReporter` and
`DaemonRpcTarget.GetDiagnostics` are bound by it. A cap that one writer applies and
another forgets is the shape of this whole bug; there is now one number and no way to
apply it to one mirror only.

**2. Entries are trimmed, never dropped.** The entry count and the severities are what
the exit code and `reddenedBy` are computed from. A bound that bought memory by
removing entries would buy it by corrupting the answer. An entry whose `detail` the
budget could not carry says so — absence and elision are different facts, and only one
of them means "go and look".

**3. The plugin stops making the product in the first place.** A per-test failure
carries its own failing line and no detail. The project-wide capture was never that
entry's fact: it was one string repeated per test, nothing renders it, and the
untruncated text is already on disk at
`.fshw/test-runs/<runId>/<project>.output.log`. The transport bound stays as a
backstop — a plugin must not be *able* to do this again — but the backstop is not the
fix.

**4. A memory fault names the process that had it.** `IpcFault.DaemonOutOfMemory` is
its own case, chosen from the fault's **origin** rather than guessed from its stack
trace. A daemon-side transcoder `OverflowException` joins it: "this reply cannot exist"
is the same fact whichever exception carries it. Neither restarts the daemon — throwing
away a daemon that has just finished a twenty-minute run discards the run with it.

**5. "The run finished and its result was lost" is its own outcome, and exit 7.**
`CheckOutcome.ResultUnreceived` is reached only when the settle already happened, so
its words are true by construction: the daemon built, ran and committed, and the CLI
failed carrying the answer back. It publishes a verdict like every other terminal, so a
finished run stops leaving the *previous* run's verdict standing as current — which is
what made seven completed runs read back as an earlier refusal stub for a different
tree.

It is deliberately outside the `0/1/2/3` band that `fshw verdict` re-uses. Those four
are claims about the code, and this makes none. It continues the codes that describe
the **answer's availability** instead — 4 stale, 5 absent, 6 in flight — with the case
those have no room for.

## Consequences

- A red suite of any size produces a bounded diagnostics reply. The growth law is
  pinned by test, in both places it can be broken: `failuresOf` may not make the
  product, and `GetDiagnostics` may not carry one if some other plugin does.
- Full per-test output is no longer available through the ledger. It never reached a
  human through the ledger either; the run log is where it is, and the messages that
  used to imply otherwise now name that path.
- A caller can distinguish "spend the time" from "the time was already spent". A retry
  loop reading exit 2 for both re-ran a twenty-minute suite seven times to re-derive an
  answer that was already on disk.

## Rejected

**Cap the client instead.** The client was not the failing party, and on the measured
numbers no client-side limit helps: a 45 GB reply is not representable at any heap
size. Sizing the CLI's memory was the first two hours of this incident.

**Drop `detail` from the wire.** It is read by nothing today, so this is tempting and
would be simpler. Rejected because the field's stated purpose — a full excerpt for
println debugging — is worth keeping for the ordinary case of a handful of failures,
and a bound gives that for free. The budget is what makes the pathological case
impossible, not the field's absence.

**Recover a green/red verdict from the CTRFs the daemon already wrote.** The test
reports for a completed run are on disk and could be re-read after a client death. But
a `check` verdict is not only the tests: the FCS diagnostics, the lint entries, the
format entries and the coverage gate all live in the ledger that was lost. Synthesising
an outcome from the half of the evidence that survived would produce exactly the vacuous
green this codebase refuses everywhere else. The publish on this path records that the
run FINISHED and points at `.fshw/test-runs/`; it does not claim to know how it went.
