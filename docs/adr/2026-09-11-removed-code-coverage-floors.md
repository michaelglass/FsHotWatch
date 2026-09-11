# Correct counts for removed compiler-generated coverage points

The macOS count floors were captured before AUTOMATION-339 added VerifiedNothing
to RunOutcome. Commit 1819f42661656fe22c4da95d4e139e5bcdaefd9a changed the compiled
match shape in Verdict, Ipc and PluginActivity. The new case is exercised, while
some formerly counted compiler-generated branches no longer exist. Retaining their
absolute counts asks tests to execute code absent from the assembly.

The preserved full-run Cobertura evidence and the earlier compiled source comparison
established these exact changes:

| File | Old covered branch floor | Remaining covered branches | Old total | New total |
| --- | ---: | ---: | ---: | ---: |
| Verdict.fs | 449 | 443 | 540 | 534 |
| Ipc.fs | 41 | 38 | 52 | 48 |
| PluginActivity.fs | 40 | 36 | 56 | 52 |

Verdict's covered-line floor also changes from 1445 to 1444. The exact source
comparison is commit 26901395e1b3, which rephrased the UnearnedScope message from
“a merge verdict needs the whole suite” to “the requested full-suite evidence was
not earned”; one generated sequence point disappeared. This is not a lost test.

Only those measured macOS counts are corrected. Percentage overrides, Linux counts,
and every other count stay unchanged. In particular, the SafeWalk IOException arm,
IdleExit losing atomic claim, and BuildPlugin occupied-slot arm require deterministic
controls rather than reduced floors; see 2026-09-11-deterministic-coverage-controls.md.

The source comparison and the full-run evidence were preserved by the integration
owner as fshw-watcher-coverage-preserved.xml and fshw-watcher-ci-preserved.log in the
resume-2026-09-11 handoff. Final integration must run the full coverage gate against
these floors. Bulk ratchet/loosen was rejected because it would also conceal the
three real missing controls.
