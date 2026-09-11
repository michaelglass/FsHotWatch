# remove deleted PluginHost branch counts from the historical floor

The macOS full-unit reports for base `4854e579` and integrated `066141fb`
contain 58/70 and 47/58 covered/total PluginHost branches respectively.
The entire difference is deleted ownership bookkeeping: generation bump sites
contributed 6/6, the old dispatch loop 2/2, and the old CompletedDispatches
sum 3/4. Every surviving branch site has the same covered/total pair.

Set only the macOS absolute covered-branch floor from 58 to 47. Retain the
94% line and 82% branch requirements and every Linux floor. This correction
does not pass the percentage gate (47/58 is 81.03%); meaningful controls for
remaining uncovered behavior are still required. Keeping deleted counters
would reward preserving obsolete work authorities, against the approved design.

Evidence: Intelligence handoff `resume-2026-09-11/pluginhost-removed-branch-audit.md`,
base `fshw-watcher-coverage-preserved.xml`, and the integrated full-unit
`coverage/coverage.cobertura.xml`. The 3293-test run had 20 failures; this
comparison is coverage diagnosis, not an acceptance claim.
