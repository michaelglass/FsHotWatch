# ADR-078: Async timing comes from one runtime the simulation drives

The host's async work takes every timing source from one capability, `AsyncRuntime.Runtime`. In
production that is the pool. In tests it is a deterministic simulation that decides every
interleaving from a seed. AUTOMATION-1695.

Status: Accepted (2026-11-17)

## Context

A test or coverage floor whose result depends on which thread ran first is a flake. Two shapes
recurred:

- **Await arms.** Since SDK 10.0.404, every `let!` in a `task {}` is a branch: either the awaited
  task has already finished, or the code suspends. The pool decided which. Every file under a 100%
  floor flapped:
  - PluginWorkOwner (AUTOMATION-1601);
  - DebouncedWork and SupervisedWork (AUTOMATION-1708);
  - RepositoryIpc's payload await (AUTOMATION-1681), which reddened the 11-13 nightly.
- **Unsynchronised observation.** A test read state before a fire-and-forget delivery landed
  (AUTOMATION-1768).

AUTOMATION-1601 fixed these one seam at a time: `dispatchWriter`, `delayTask`, `awaitReceipt` and
`scheduleDeadline`. Every new await or timer could bring the class back.

## Decision

1. **One capability.** `AsyncRuntime.Runtime` has these fields:

   | Field | What it covers |
   |---|---|
   | `Dispatch` | pool work items |
   | `Wait` | blocking waits |
   | `Scheduler` | `StartNew` and `ContinueWith` |
   | `Schedule` | timers |
   | `Delay` | `Task.Delay` |
   | `Cancel` | `CancelAsync` |
   | `RunSynchronously` | a worker's blocking run of its `Async` |

   - A `PluginWorkOwner.Store` takes the runtime.
   - `SupervisedWork` and `DebouncedWork` take theirs from their store (`Store.Runtime`). One host
     has one runtime, so a simulated store never drives work on the real pool.
   - `AsyncRuntime.pool` is exactly the previous behaviour.
2. **The simulation** (`tests/FsHotWatch.Tests/SimRuntime.fs`) runs everything on one thread:
   - **Dispatch:** runs its work inside the call or queues it, as the seed says.
   - **Continuations:** while installed, the simulation is the thread's synchronization context,
     and its scheduler queues every started task and continuation.
   - **Pump:** the pump runs one ready item at a time, and the seed picks which.
   - **Virtual time:** time advances only when nothing is ready, to the next armed timer.
   - **Blocking waits:** a wait pumps until its task settles. A wait that nothing left to run can
     settle raises `SimDeadlockException` instead of hanging.
   - **Escapes:** work arriving from another thread is recorded as an escape and is not run.
3. **Histories, theorems and planted defects** are defined for each component:
   - the Store;
   - SupervisedWork;
   - DebouncedWork;
   - RepositoryIpc's frames, over scripted streams.

   RepositoryIpc's frames need no product change: their only timing source is the stream. Each
   component's census asserts that the interleavings the pool reaches only by chance are reached.
4. **Seeds.**
   - **CI:** runs a fixed band, seeds 1..N with N = 1,000 per component (`FSHW_SIM_HISTORIES`,
     `FSHW_SIM_SEED`). The interleavings, and the coverage they contribute, are then the same on
     every run.
   - **Nightly:** runs 100,000 fresh seeds.
   - **Regression list:** every seed the nightly finds failing is appended to the checked-in
     `tests/FsHotWatch.Tests/SimRegressions.txt`, with the theorem it violated and its ticket.
     CI runs every listed seed beside the band. A nightly failure therefore becomes a ticket plus
     a permanent CI case.

   The ticket asked for seeds derived from the commit. This departs from it: a commit-derived band
   would move the coverage the simulations contribute with every commit, which is the flake class
   this exists to remove.
5. **The house rule.** FSHW-TIME-001 (FsHotWatch.Rules) refuses a timing source reached directly
   in the simulated modules: `Task.Run`, `Task.Delay`, `TaskScheduler.Default`, `ThreadPool`, the
   `Async` starters, `Thread.Sleep`, `CancelAsync`, `CancelAfter`, `Timer`, or a `ContinueWith`
   without `runtime.Scheduler`.
6. **Location.** The simulations live in FsHotWatch.Tests, not FsHotWatch.Simulation.Tests:
   - the components are internal to FsHotWatch;
   - FsHotWatch.Tests is the project whose coverage the floors read.

## Measured

1,000 histories per component (macOS, Debug, one class):

| Component | Time | Planted defects caught (histories caught of 1,000) |
|---|---|---|
| Store | 191 ms | 1000, 493, 994, 1000 |
| SupervisedWork | 476 ms | 579, 463, 559 |
| DebouncedWork | 481 ms | 288, 686, 756 |
| Frames | 639 ms | 935, 881, 981 |

Each component was also red against its real code, with a defect planted by hand and reverted.
The violations each defect caused:

| Component | Defect planted by hand | Violations |
|---|---|---|
| Store | a lost wakeup | 1665 |
| Store | a writer flag never cleared | SimDeadlockException in 56 ms, instead of a hang |
| SupervisedWork | a successor never started | 583 |
| SupervisedWork | a close that leaves queued receipts unsettled | 314 |
| DebouncedWork | the newest cohort admitted first | 447 |
| DebouncedWork | admissions without their serialising chain | 227 |
| Frames | the AUTOMATION-1681 short read | 947 |

## Limits

- **The atomic step is one dispatch or one continuation.** A race between two statements of one
  method, with no await or dispatch between them, is not reachable: for example, setting a reply
  before its `Volatile.Write`. Locks own that class.
- **F# `Async` can hop to the pool on its own** (the trampoline). The harness's callbacks call
  `CheckOwner`, so a hop that reaches them is an escape, not a silent pass.
- **Not driven yet** (follow-up):
  - `PluginFramework`'s `MailboxProcessor`, which F# starts on the pool;
  - the repository endpoint's opener (`CancelAfter`) and its named-pipe client;
  - `SupervisedWork.declare`'s scheduler argument.
- **A finding, since fixed.** The host could report rest while a debounced receipt was still
  unsettled (482 times in 1,000 histories): a cohort's receipts settled one continuation after the
  request they became retired. Filed as AUTOMATION-1780 (a consumer that waits for rest and then
  reads results can miss the last receipt, the AUTOMATION-1768 shape) and decided there: rest waits
  for receipts. DebouncedWork keeps a cohort owned until its receipts are set, and the simulation
  asserts it as HONEST REST (an unsettled receipt implies busy). SupervisedWork's own receipt is set
  a statement after its retirement publishes, which the atomic-step limit above leaves unreached.

## Rejected

- **Microsoft Coyote (binary rewriting).** Its F# `task` state machines are unproven, it has had no
  release since 2023, and it adds a rewriting step to the build.
- **One seam per await (the status quo).** It does not remove the class.
- **Raising timeouts or lowering floors.** That excuses the class rather than removing it.
- **A real-thread baton scheduler.** It needs blocking waits intercepted. `Runtime.Wait` as a
  capability gives the same result with no interception.
