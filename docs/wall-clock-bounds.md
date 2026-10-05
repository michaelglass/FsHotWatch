# Wall-clock bounds in the test suites

Every place a test in `tests/` puts an upper bound on wall-clock time, what kind of bound
it is, and why it cannot turn a slow box into a red test. The gate runs these suites on
machines that are also building, releasing and running other gates, so a bound that a
saturated box can reach will eventually fail on load rather than on behaviour.

## The rules

A bound is one of three things.

1. **A timing assertion.** The test's claim is a duration: `elapsed < X`, a relative
   bound against a control, or a race between the work and a delay. These are the
   dangerous ones. Each must either be replaced by an assertion on what happened (an
   outcome, a count, a log line, a handshake) or carry a written reason it cannot flake.
2. **A hang cap.** An xUnit `Timeout`, a `waitUntil`/`waitFor*` deadline, an asserted
   `Wait(timeout)`: it ends a test that would otherwise hang, and the failure it reports
   is "this never happened". A hang cap is fail-safe when correct work cannot reach it:
   - the work under it is in-process, or one trivial child (`echo`, `sh -c true`,
     `sleep`), and the cap is at least 5 s; or
   - the work has a product-owned bound (a tool timeout, a `TimeoutSec`) and the cap
     sits above it, so the product decides first and reports a typed outcome; or
   - the work is a real FCS type-check or lint pass, which has no product bound, and
     the wait is at least `RealCheckWaitMs` (30 s) with the xUnit cap above every wait
     the test makes.

   A hang cap that breaks these rules is a timing assertion in disguise and is treated
   as rule 1.
3. **A lower bound or negative window.** `elapsed >= X`, or "this did not happen within
   N ms". Load only lengthens durations and delays events, so these can fail to detect
   a defect on a slow box, but cannot fail a correct one.

The literals that encode rule 2 live in `tests/FsHotWatch.Tests/TestHelpers.fs`
(`FantomasRunBudgetMs`, `FantomasRunWaitMs`, `RealFantomasTestCapMs`, `RealCheckWaitMs`,
`RealCheckTestCapMs`). `FormatCheckPluginTests` pins `FantomasRunBudgetMs` to the
product's `FormatTimeoutDefaultSec`, so raising the product bound without the caps goes
red.

## Timing assertions

Every upper wall-clock bound on a measured duration, as of this commit.

| # | Site | Bound | Verdict | Reason or fix |
|---|------|-------|---------|---------------|
| 1 | `NuGetPublicationBarrierTests` "a wedged restore is killed at the process timeout and cleanup still runs" | `result.Elapsed < control.Elapsed + 30 s` | CAN-FLAKE (red under load 10-05) | **Fixed.** The control run and the clock are gone. A waited-out fake exits 0 and the probe succeeds, so the run's stderr is the proof: `restore timed out` (the 100 ms budget fired) and `process tree kill` (the kill that follows it). |
| 2 | `IntegrationTests` "runProcess kills child when exceeded" | `< 3 s` | redundant | **Fixed.** Removed. `sleep 10` left to finish is `Succeeded`, so `TimedOut` already proves the 200 ms bound killed it. |
| 3 | `IntegrationTests` "TestPrune honors per-project TimeoutSec and records TimedOut" | `< 8 s` | redundant | **Fixed.** Removed. A run that ignored its 1 s `TimeoutSec` lets `sleep 10` exit 0, which is not the asserted `TimedOut`. |
| 4 | `IntegrationTests` "waitForPluginTerminalIfRunning returns immediately when plugin not registered" | `< 3 s` | timing claim | **Fixed.** The wait's timeout is now 10 min, past the 30 s xUnit cap. Returning at all is the claim, and a wait that does not return is reported as a hang. |
| 5 | `IntegrationTests` "waitForPluginTerminalIfRunning returns when plugin reaches terminal" | `> 200 ms` and `< 14 s`, with release on a 300 ms `Task.Delay` | timing claim | **Fixed.** A handshake replaces the delay. The test waits for `Running`, starts the wait (10 min timeout), checks it has not returned (a negative window), releases the plugin, and asserts the status is terminal. |
| 6 | `IntegrationTests` "waitForPluginTerminalIfRunning times out when plugin never leaves Running" | `< 10 s` | redundant | **Fixed.** Replaced by an outcome: on return the plugin is still `Running`, so the wait's own timeout ended it. The `> 450 ms` lower bound stays (rule 3). |
| 7 | `AnalyzerPathDiagnosisTests` "check whose daemon refused to start … exits 2 promptly" | `< 10 s` | timing claim | **Fixed.** Counts pipe probes after the launch: exactly 1. A wait that ignored the recorded refusal polls every 100 ms until its 20 s timeout. |
| 8 | `ProgramTests` "startFreshDaemonWith reports a failed launch without waiting for a daemon" | `< 5 s` | redundant | **Fixed.** Removed. `probes = 1` already shows the start never polled. |
| 9 | `TestPruneDiscoveryWaitTests` "a host that has observed no discovery launches at once" | `< 10 s` | timing claim | **Fixed.** Asserts the log has no `project discovery` line. Every discovery wait leaves one: "waited …", "still running after …", or "is in flight; waiting". |
| 10 | `ProcessHelperTests` "callKillWithin: a BLOCKING kill is cut off at the budget" | `< 3 s` | redundant | **Fixed.** Removed. The kill blocks until the `finally` releases it, so without the budget the call never returns. `DidNotReturn shortBudget` is the outcome. The `>= budget - 10 ms` lower bound stays (rule 3). |
| 11 | `ProcessHelperTests` "callKillWithin: the budget is not spent on a kill that returns" | `< 3 s` | timing claim | **Fixed.** The budget is now 10 min, past the 5 s xUnit cap, so a call that sits its budget out is a reported hang. `Returned` is the claim. |
| 12 | `ProcessHelperTests` "runProcess does not wait for a grandchild that inherited the stdout pipe" | `< 10 s` | redundant | **Fixed.** Removed. A drain that waited for EOF ends in a complete capture when the grandchild's 30 s sleep releases the pipe, never in the asserted `DrainTimedOut(_, 2 s)`. |
| 13 | `ProcessHelperTests` "runProcessTo streams DURING the run — on disk, mid-flight" | `firstChunkAt < elapsedAtEnd - 1.5 s` (relative) | CAN-FLAKE (a 1.5 s stall of the output pump) | **Fixed.** A handshake replaces it. The child prints, then waits for a release file that only the sink's first chunk creates, then prints `released`. A sink that hears nothing until exit never releases the child, and the xUnit cap reports the hang. |
| 14 | `DaemonConfigTests` "a beforeRun hook that hangs TIMES OUT instead of wedging the tests slot" | `< 15 s` | redundant | **Fixed.** Removed. A hook left to finish `sleep 60` succeeds and says nothing of a timeout. `not success` and "timed out" are the outcome. |
| 15 | `DaemonConfigTests` "a beforeRun hook whose grandchild holds the stdout pipe still returns" | `< 15 s` | timing claim | **Fixed.** The grandchild now sleeps 90 s, past the hook's own 60 s timeout, so a hook that waits for EOF can only end as `not success`. `success` is the outcome. |
| 16 | `PluginHostTests` "waitForAllTerminal returns within quiescence window when no work is pending" | was `< 4.9 s`. After the earlier fix: a 5 s product timeout and `Wait(10 s)` | was CAN-FLAKE | **Fixed.** The earlier change made it an outcome. Now the wait's timeout is 10 min and the test waits without a bound under its 20 s cap, so only quiescence can end it. |
| 17 | `AdmissionBoundTests` (all three facts, via `givesUpWithinBound`) | `elapsed < AdmissionBound (5 s) + 2 s` | CANNOT-FLAKE | The wait is `WaitOne` on the test's own thread, not a pool callback. Overshooting by 2 s needs that one runnable thread to go unscheduled for 2 s. The defect it guards against waits for the starved pool to inject a thread for every held item ahead of its timer. The class is in the serialized `LogGlobal` collection, so the pool starvation it causes reaches no other test. |
| 18 | `ProgramTests` "…stop…" facts at the three `elapsed () < …` sites | `< 1 s`, `< quiet + exit + 1 s` | CANNOT-FLAKE | `fakeStopOps` is a virtual clock: `SleepMs` advances it and nothing reads real time. |
| 19 | `DaemonPhasesTests` "`StartedAt + Elapsed <= now`" | ordering | CANNOT-FLAKE | `now` is read after the record is closed. Load moves `now` later, never earlier. |

## Hang caps that were the assertion

These broke rule 2. A saturated box could reach them before the product or the test's
own named waits decided.

| Site | Was | Verdict | Fix |
|------|-----|---------|-----|
| `IntegrationTests` "Full pipeline: format → build → test" | xUnit 5 s over a real `dotnet tool run fantomas` plus two spawns. Its own waits were 5 s and 10 s | CAN-FLAKE (cancelled under load 10-04/05, and in CI run 36806003051) | `RealFantomasTestCapMs` |
| `FormatCheckPluginTests`, 11 real-fantomas facts (FormatPreprocessor formats / skips / reports a parse error, format check reports / detects / clears, the agreement test, the pinned-version test, both replay tests) | xUnit 30–120 s, waits 25–30 s, all below the product's 60 s bound per run (the agreement test makes 5 runs under a 60 s cap) | CAN-FLAKE (FormatPreprocessor cases cancelled at 30 s under load 10-04/05) | caps `RealFantomasTestCapMs` (5 runs × 60 s + 60 s), waits `FantomasRunWaitMs` (75 s) |
| `IntegrationTests`, 6 real-fantomas facts (format check detects / passes / lifecycle / debounced, FormatPreprocessor succeeds / reformats) | xUnit 30 s, waits 25 s | CAN-FLAKE (same mechanism) | same literals |
| `IntegrationTests` "all plugins receive events when checking a file", "lint plugin detects warnings on bad code", "LintPlugin reports no warnings on clean code", "LintPlugin reports warnings on code with issues" | xUnit 5–10 s over a real FCS check and lint pass, below the tests' own 30 s waits. Lint waits were 5 s | CAN-FLAKE | caps `RealCheckTestCapMs`, waits `RealCheckWaitMs` |
| `IntegrationTests` "BuildPlugin serializes changes that arrive during a build" | a 5 s wait over two sequential `sleep 1` builds (2 s of deliberate work) under a 10 s cap | CAN-FLAKE (3 s of margin over real work) | the wait and cap match its twin "concurrent FileChanged events run two builds sequentially": 15 s and 30 s |
| `PluginTimeoutTests` "a descendant that outlives the killed shell is named as a survivor" | the product's 3 s `ps` budget decided the test: a loaded box failed with "`ps` did not answer within 3s" (10-04/05) | CAN-FLAKE | the test reads the table with its own 15 s budget (`readProcessTableWithin`). A teardown now reads with `TeardownBudget` (10 s), see the next section. Its tree-size wait is now asserted, and its cap is 60 s. |

## Product bounds that decided the claim

These tests asserted what a product bound decided, and load could reach the product
bound before the event the test needed. Each is now decided by an event, or by a bound
that meets rule 2.

| Site | Was | Verdict | Fix |
|------|-----|---------|-----|
| `PluginTimeoutTests` "an overrunning build names its command, budget, tree and what the kill left" | the product read the process table during the build's teardown with a 3 s `ps` budget. When `ps` missed it the report said the tree was unknown, and the tree assertions failed | CAN-FLAKE | **Fixed in the product.** A teardown reads the table with `TeardownBudget` (10 s), the bound the kill it brackets already has. `ps` is a trivial child, so a 10 s budget meets rule 2. A shorter one turned a slow read into "tree unknown" in real overrun reports too. The test's wait (45 s) and cap (60 s) sit above the whole teardown: 1 s timeout, 10 s read, 10 s kill, about 2 s settle, 2 s drain. |
| `PluginTimeoutTests` "an overrunning quiet dotnet build names the target it was stalled in" | `TimeoutSec = 20` from the spawn. A `dotnet build` that had not reached `StallsHere` in 20 s was killed in an earlier target, and the report named that one | CAN-FLAKE | **Fixed.** The build's timeout (now 1 s) is armed when `StallsHere` starts: its `Exec` touches a marker, and the test builds the plugin with `BuildPlugin.createArmedWith`, which arms the timeout when the marker exists (`ProcessBounds.armedWhen`). The timeout cannot start before the build is in the target the report must name. `FinishesFirst` takes 3 s, so a timeout armed at the spawn fires there and the test is red. Before the marker the build has no product bound, so the wait (240 s) and cap (300 s) bound a `dotnet build` of a project with no references, then the product's interrupt grace (15 s), teardown (10 s + 10 s) and binlog replay (60 s). |
| `IntegrationTests` "runProcess reports TimedOut on kill, carrying the child's pre-kill stdout" (300 ms); `ProcessHelperTests` "runProcess streaming: a progressing run that overruns the overall timeout is TimedOut" (300 ms) and "runProcessTo keeps what a SIGKILLed child said BEFORE the kill" (600 ms) | sub-second timeouts from the spawn. A shell not scheduled within the timeout wrote nothing, and the tail or sink assertion failed | CAN-FLAKE | **Fixed.** The timeouts are armed on the child's first write (`ProcessBounds.armedWhen id`). `runProcessCore` puts each chunk in the capture and the sink before it marks the child as having written, so when the timeout starts the marker is already captured. Each child now waits 1 s before its first write, so a timeout armed at the spawn kills it first and the test is red. |

## Hang caps (fail-safe, excluded from the tables)

One line per kind. Each meets rule 2 as written above.

- xUnit `Timeout` on about 3,800 facts (2,366 at 15 s, 326 at 20 s, 320 at 30 s, 320 at 5 s, 210 at 60 s, the rest 2 s–15 min). Unless listed above, the work under them is in-process or a trivial child.
- `waitUntil`/`waitUntilTrue` (about 380 calls), `waitForTerminalStatus` (118), `waitForQuiescent` (79), `waitForCommitted`, `waitForSettled`, `waitForStatusSettled`, `waitCompleted`/`waitTerminal`: polling deadlines that end a wait for an event. The assertion that follows names what did not happen.
- `probeLoop`/`probeUntilEvent` (30 calls): re-write a probe every 2 s until a live watcher reports it. The budget only ends the failure case, and the watcher tests run in the serialized `FileWatch` collection.
- Asserted positive waits, `test <@ x.Wait(…) @>`/`Assert.True(x.Wait(…))` (165 sites): a gate or event the test itself releases, with caps of 5–60 s over in-process hand-offs.
- `Task.WhenAny(work, Task.Delay n)` asserted to be `work` (`SupervisedWorkTests`, `PluginWorkOwnerTests`, `ProcessOwnershipTests`, `PluginFrameworkOwnershipTests` ×2, `IpcTests`, `TraceLauncherTests`): 5–10 s observation bounds on a receipt or completion that settles in-process.
- `PluginHostTests` reader loop (`deadline.Elapsed < 45 s`, `reader.Wait(50 s)`): bounds an in-process observation loop. The assertion is `lastSeen = runs`.
- `ColdCheckFixture` 840 s work budget inside a 900 s xUnit cap: leaves 60 s to reap owned children and clean up.
- `withEveryPoolThreadBusy` `Thread.Sleep 200`, then `WaitAll(10 s)`: fixture setup and teardown, not an assertion.

## Lower bounds and negative windows (cannot fail on load)

- Lower bounds: `DaemonPhasesTests` (`>= 15 ms`, `> 0`), `DaemonHostingTests` (`>= 3 s`), `BuildOverrunTests` (`>= SettlePause - 1 ms`, `>= InterruptPoll - 1 ms`), `PluginHostTests` (`>= 50 ms`), `IntegrationTests` "BuildPlugin serializes…" and its twin (`>= 1800 ms`), "waitForPluginTerminalIfRunning times out…" (`> 450 ms`), `ProcessHelperTests` "a BLOCKING kill…" (`>= budget - 10 ms`), and the `> 0`/`>= 0` checks in `IpcPayloadTests`, `DaemonTests`, `CliTests` and `FileCommandPluginTests`.
- Negative windows ("did not happen within N ms"): `RunOnceOutputTests` (1 s), `DaemonTests` ×4 (500 ms–1 s), `LockingTests` (200 ms), the `IntegrationTests` premature-return check (300 ms), and the `Thread.Sleep`-then-"nothing more happened" checks in `DaemonTests`, `TaskCacheTests`, `HeartbeatTests`, `DaemonConfigTests`, `OperationWatchdogTests`, `PluginFrameworkTests`, `PluginWedgeTests`, `BuildPluginTests`, `FileCommandPluginTests`, `AnalyzersPluginTests`, `RunStatusHandoffTests` and `TestPrunePendingVerificationTests`.
- `SnapshotVersionTests` `WhenAny(checkD, Delay 5 s)`: lets a defective invalidation's re-check finish before releasing `S`. On a slow box the defect may go unseen, but a correct run waits out the 5 s and passes.
- `PathFilterTests` `Thread.Sleep 1100`: waits out filesystem mtime granularity before a change.

## Keeping it true

- Assert what happened, not how long it took. Prefer an outcome (`TimedOut`, a typed
  failure), a count, a log line, or a handshake the test controls.
- If a test exists to show that something did not wait, give the wait a timeout far past
  the xUnit cap, so the defect shows up as a reported hang rather than a slow pass.
- Over real tool work, size caps and waits from the product's own bound with the literals
  above, never from how long the tool usually takes.
- A new upper wall-clock bound needs a row in this file.
