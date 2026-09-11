# Require coherent CTRF evidence before earning a test verdict

A104's owner protocol proves input identity, run ownership, and committed results.
Those properties do not establish that a reported result is internally coherent.
The producer still accepted a clean summary claiming seven tests with only one row;
its durable suite reader copied all seven claimed passes. Fifteen regression controls
failed against that existing boundary, while seven valid/malformed controls passed.

Ctrf.VerdictReport is now an opaque proof constructed only by tryVerdictReport.
All six summary counters must be present nonnegative integers. A clean report must
have an actual tests array whose length and passed/pending/skipped statuses exactly
match its summary. Missing, malformed, fractional, negative, overflowing, or
contradictory data cannot manufacture that proof. The typed value exposes its Summary
for consumers without exposing a constructor.

Captured MTP raw-exception reports intentionally have fewer rows than their total and
can count other inside failed. Their red summary remains authoritative, so neither
row equality nor summing all six counters is imposed on red reports. Nested and
flattened coherent reports remain accepted. Report coherence alone grants neither
scope nor run ownership, and a coherent zero-count report does not prove execution.

Diagnostic trySummary/tryReadReport/reportsForRun remain permissive. New strict file
and run readers feed CLI suiteVerdicts so durable counts cannot resurrect rejected
evidence. TestPrune's owner migration consumes the typed parser at its requested-report
boundary; missing requested evidence must not fall back to a successful process exit.

The old FsHotWatch AUTOMATION-617 proposal informed this narrow boundary. That ticket
was completed by Intelligence's ReportCoherence consumer gate; it is not reopened.
The producer fix belongs to A104's private earned-evidence acceptance and supports
consumers that do not have Intelligence's additional gate. Importing the old 23-file
proposal and duplicating its broad outcome/wire redesign was rejected.

Verification: baseline controls 15 failed / 7 passed; after the parser and durable
reader changes, all 354 FlakinessTests and VerdictTests passed, including the captured
raw-exception positive control. Full owner integration still gates the live classifier,
ordinary check/confirm, cache, receipts, and complete suite behavior together.
