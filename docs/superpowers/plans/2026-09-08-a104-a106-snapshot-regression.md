# Snapshot Regression Execution Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to execute this bounded regression phase. Steps use checkbox syntax for tracking.

**Goal:** Establish actual current-API failure evidence for the approved nonblocking snapshot-read requirement before changing production code.

**Architecture:** Exercise the real registered plugin and command path while a test-owned update is deliberately blocked. Read the last committed state during that block and explicitly observe event completion before requiring the new state. This plan ends at an observed regression result; it does not complete or narrow the shared implementation.

**Tech Stack:** F#, MailboxProcessor, xUnit, FsHotWatch supported runner, Jujutsu, mise.

**Spec:** Approved and the tracked issue, one owner/one change. Local source mapping: `docs/source-preparation.md`.

## Global Constraints

- Full ticket completion still requires one immutable single-writer PluginCore, one WorkLedger, typed phase/slots, published snapshots, checked evidence verdicts, nonblocking supervised work, and preserved bounded failure/shutdown handling.
- Do not land a snapshot-only compatibility patch that retains every competing authority and claims the broader tickets complete.
- Existing green requires complete test/scan/coverage obligations, including the meaning of skipped or unrunnable work; UI terminal status is not proof.
- Do not remove PendingRerun or the 200ms quiescence heuristic until the replacement ledger proves the handoff obligations they currently protect.
- Net-negative line count is not an acceptance criterion. No STM/Akka dependency. The tracked issue remains Needs Approval.
- workspaces are separately owned and must be preserved. No candidate source or consumer edits.
- No heavy command before the candidate owner explicitly releases capacity and a fresh headroom result admits this phase.
- Never count compile failure, missing artifacts, preexecution refusal, absent/stale verdict, or interrupted client as assertion-red evidence.

## Task 1: Compile and observe the snapshot-read regression

**Files:**
- Modify: `tests/FsHotWatch.Tests/PluginFrameworkTests.fs` (prepared test below).
- Read: `src/FsHotWatch/PluginFramework.fs` (registerHandler command registration and dispatch completion).
- Update evidence: `docs/source-preparation.md`.

**Interfaces:**
- Uses existing `registerWith`, `PluginHandler<int, unit>`, `RegisteredPlugin.Dispatch`, `RegisteredPlugin.IsBusy`, and the registered `CommandHandler`.
- Produces a test receipt and expected-failure classification; no production API in this phase.

- [x] Prepare this real-framework regression (uncompiled and unexecuted at plan creation):

```fsharp
[<Fact(Timeout = 20000)>]
let ``read command returns committed snapshot while an update is blocked`` () =
    // A read queued behind Update hangs until unrelated work finishes. The approved
    // snapshot contract requires the last committed state to remain readable.
    let entered =
        System.Threading.Tasks.TaskCompletionSource<unit>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let release =
        System.Threading.Tasks.TaskCompletionSource<unit>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let mutable registeredCmd: (string * CommandHandler) option = None

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "snapshot-during-update"
          Init = 42
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        entered.TrySetResult(()) |> ignore
                        do! release.Task |> Async.AwaitTask
                        return state + 1
                    | _ -> return state
                }
          Commands = [ "read-count", fun _ctx state _args -> async { return $"%d{state}" } ]
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          Teardown = None }

    let registration = registerWith handler (Some(fun command -> registeredCmd <- Some command))
    let (_, readCount) = registeredCmd.Value
    registration.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Snapshot.fs" ]))

    try
        Assert.True(entered.Task.Wait(5000), "the update must enter before probing the read")
        let read = readCount [||] |> Async.StartAsTask
        Assert.True(read.Wait(2000), "read command waited behind the blocked update instead of reading a snapshot")
        Assert.Equal("42", read.Result)
    finally
        release.TrySetResult(()) |> ignore
        waitUntil (fun () -> not (registration.IsBusy())) 5000

    // Completion is observed explicitly; the read itself is no longer a barrier.
    Assert.False(registration.IsBusy())
    let committed = readCount [||] |> Async.RunSynchronously
    Assert.Equal("43", committed)
```

- [ ] After explicit phase release, run the repository headroom command for one declared agent. Stop on defer/refusal and report the binding measurement; do not launch a second client against an active run.
- [ ] Enter the isolated FsHotWatch workspace before selecting its tool environment. Run `mise run compile` and record the command exit. Fix only genuine compilation findings in the prepared regression before continuing.
- [ ] Run the supported class runner from this workspace:

```sh
mise exec -- dotnet run --project src/FsHotWatch.Cli --no-build -- test-rerun --project FsHotWatch.Tests --filter-class FsHotWatch.Tests.PluginFrameworkTests
```

The current source registers read commands using `agent.PostAndAsyncReply(Choice2Of2)`. Expected assertion failure: `read command waited behind the blocked update instead of reading a snapshot`. The update must have entered first; an entry-timeout is a different failure. The finally block releases the update even when the expected read assertion fails. Capture the actual CTRF test entry and command exit. A cold daemon may run queued work before the class request; correlate each report with its actual run rather than assuming the requested filter ran.

- [ ] Record the current commit/tree identity, exact command, test run ID, assertion message, pass/fail/skip counts, and cleanup outcome. If the test passes, investigate the current implementation instead of weakening the test or assuming it reproduced a defect.
- [ ] Checkpoint the evidence with an explicit `jj describe -m` message. Do not publish, merge or move the issue to QA on a red receipt.

## Task 2: Observe the synchronous RPC-prefix deadline regression

**Files:** `tests/FsHotWatch.Tests/IpcTests.fs`; production boundary
`src/FsHotWatch/Ipc.fs` trackedTask. Uses existing defaultRpcConfig and
DaemonRpcTarget with its injectable finite deadline. Produces a second actual
failure receipt, not a production implementation.

- [x] Prepare this boundary regression, still uncompiled and unexecuted:

```fsharp
[<Fact(Timeout = 20000)>]
let ``RPC deadline includes a synchronous callback before its task is returned`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    use entered = new System.Threading.ManualResetEventSlim(false)
    use release = new System.Threading.ManualResetEventSlim(false)
    let callbackExited = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let config =
        { defaultRpcConfig host with
            WaitForScanGeneration =
                fun _ ->
                    entered.Set()
                    try
                        release.Wait()
                        Task.FromResult(())
                    finally
                        callbackExited.TrySetResult(()) |> ignore }

    let target = DaemonRpcTarget(config, deadline = TimeSpan.FromMilliseconds 100.0)

    // Invoke off the test thread: the current callback blocks before returning
    // its task, which is precisely the part the RPC deadline must also bound.
    let invocation: Task<string> =
        Task.Run<string>(System.Func<Task<string>>(fun () -> target.WaitForScan(-1L)))

    try
        Assert.True(entered.Wait(5000), "the callback must enter before observing its deadline")

        let winner =
            Task.WhenAny([| invocation :> Task; Task.Delay(2000) |])
                .GetAwaiter()
                .GetResult()

        Assert.True(
            obj.ReferenceEquals(invocation, winner),
            "RPC deadline did not cover the synchronous callback before it returned a task"
        )

        let failure =
            Assert.Throws<TimeoutException>(fun () -> invocation.GetAwaiter().GetResult() |> ignore)

        Assert.Contains("WaitForScan", failure.Message)
    finally
        // Neither the intended red nor a future passing timeout may strand work.
        release.Set()
        Assert.True(callbackExited.Task.Wait(5000), "the released callback must leave its blocking prefix")

        let drained =
            Task.WhenAny([| invocation :> Task; Task.Delay(5000) |])
                .GetAwaiter()
                .GetResult()

        Assert.True(obj.ReferenceEquals(invocation, drained), "the released callback must drain")
```

- [ ] In the same admitted phase, compile the prepared sources through `mise run compile`.
- [ ] Run the supported class route after the prior client is terminal:

```sh
mise exec -- dotnet run --project src/FsHotWatch.Cli --no-build -- test-rerun --project FsHotWatch.Tests --filter-class FsHotWatch.Tests.IpcTests
```

- [ ] Require the specific assertion `RPC deadline did not cover the synchronous callback before it returned a task`; entry/cleanup timeouts or compile errors are different failures to investigate. Save the actual CTRF and run identity.
- [ ] Preserve the neighboring existing never-completing-task, successful-within-deadline and infinite-deadline-refusal tests. They test different branches; the new synchronous-prefix case does not replace them.
- [ ] Checkpoint the observed result without a QA/merge claim. No timeout wrapper is to be shipped independently as a substitute for the full nonblocking state-owner and ledger work.

## Required next design work after the real red

The read change must be integrated with the state ownership design, not blindly applied as a separate volatile variable. In the current loop, inflightCount is decremented in a finally before tail-recursing to nextState. Publishing after that finally would let `IsBusy=false` race an old snapshot. Publish the coherent state/evidence before releasing its final obligation in the replacement owner.

Existing tests use a command query as an implicit FIFO completion barrier (PluginFrameworkTests originally350–376). They need an explicit processed-event witness when commands become snapshot reads. Audit production command consumers for the same assumption. A generic mutable plugin state cannot automatically be called an immutable snapshot: the new PluginCore must enforce its own immutable state and evidence ownership.

Framework preservation tests already cover shared FIFO handoff, start/factory/classifier exceptions, dispatch faults during live runs, backlog progress, dead mailboxes, downstream cascades, subscriber reentrancy and shutdown. The full implementation plan must bind those behaviors to the new ledger before deleting the old accounting. The full regression/CI and real Intelligence consumer qualification remain required before either ticket enters QA.

## Self-review

The experiment directly covers snapshot-read responsiveness and committed-state visibility, with cleanup and an explicit post-update check. It does not claim coverage of the remaining ledger, coverage, supervisor, host-completion or migration requirements. Those remain listed above and in the shared source mapping. Execution is inline under the existing user authorization; no additional planning approval is required.
