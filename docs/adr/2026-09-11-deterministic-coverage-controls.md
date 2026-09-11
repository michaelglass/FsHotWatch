# Exercise coverage obligations at deterministic boundaries

The full coverage run exposed three real uncovered paths: a directory disappearing
between enumeration and entry, a later idle tick losing the shutdown latch, and a
dependency completing while the build slot still owns earlier work.

SafeWalk's control advances the lazy iterator past a root file, removes the already
listed child directory, and requires an explicit Unreadable entry. No timing race or
permission assumption is needed to exercise the IOException alternative.

Eligible idle ticks now make one atomic latch claim. The former separate hasFired
read made the losing claim depend on a thread interleaving. Later eligible ticks
exercise that same losing claim deterministically. Ineligible ticks still respect
an already-fired latch, and concurrent ticks produce exactly one Fired result.

The build control drives the real handler Update with a held local slot. Dependency
success must retain PendingFiles and make no shared lease claim. Releasing the
original slot and folding its completion drains the owed input exactly once.
Removing the running-slot guard makes this control fail: the pending input becomes
empty at the dependency notification. The production guard is unchanged.

Verification: the three focused classes passed 184 tests, with SafeWalk at 81 covered
lines and 13/14 branches and BuildPlugin at 779 covered lines and 75/80 branches.
IdleExit has 71 covered lines; its eligible loser is now deterministic. Full-suite
coverage remains the integration owner's final gate. None of these three files'
coverage thresholds or count floors is reduced.
