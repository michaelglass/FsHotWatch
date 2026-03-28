# Transition to MailboxProcessor throughout

Replace shared mutable state + Volatile.Read/Write with F# MailboxProcessor agents
everywhere — plugins AND daemon — to eliminate concurrency race conditions structurally.

## Why

The current model (mutable fields + Volatile) is error-prone:
- Bug 1: OnFileChecked read empty DB during concurrent RebuildProjects flush
- Bug 2: pendingRerun write not visible across threads on ARM64
- Bug 3: ScanAll returned results before re-discovery cleared stale errors

All three bugs were caused by concurrent access to shared mutable state.
With MailboxProcessor, each plugin processes messages sequentially —
these races become impossible by construction.

## Migration plan (incremental, one plugin at a time)

### Phase 1: TestPrunePlugin (highest value — most complex state)

Current mutable state:
- `testsRunning: bool`
- `testsCompleted: bool`
- `pendingRerun: bool`
- `analysisRan: bool`
- `lastAffectedTests: TestMethodInfo list`
- `lastChangedFiles: string list`
- `lastTestResults: TestResults option`
- `pendingAnalysis: ConcurrentDictionary`
- `symbolSnapshot: ConcurrentDictionary`

Becomes a single agent with message types:
- `FileChecked of FileCheckResult`
- `BuildCompleted of BuildResult`
- `GetStatus of AsyncReplyChannel<PluginStatus>`
- `GetAffectedTests of AsyncReplyChannel<string>`
- `GetChangedFiles of AsyncReplyChannel<string>`
- `GetTestResults of AsyncReplyChannel<string>`
- `RunTests of string[] * AsyncReplyChannel<string>`

All state moves into the agent's recursive loop parameter (immutable record).
No Volatile, no ConcurrentDictionary, no races.

Key design decision: keep analysis (analyzeSource) parallel but post results
to the agent. The agent serializes state updates, not the CPU-bound work.

### Phase 2: BuildPlugin

Current mutable state:
- `building: bool`
- `lastResult: (bool * string) option`

Simple agent with:
- `FileChanged of FileChangeKind`
- `GetStatus of AsyncReplyChannel<PluginStatus>`

### Phase 3: AnalyzersPlugin

Current mutable state:
- `diagnosticsByFile: Map`
- `loadedCount: int`
- `processedCount: int`
- `errorCount: int`

Agent with:
- `FileChecked of FileCheckResult`
- `GetDiagnostics of AsyncReplyChannel<...>`

### Phase 4: LintPlugin

Current mutable state:
- `warningsByFile: Map`

Simplest plugin — straightforward conversion.

### Phase 5: FileCommandPlugin

Current mutable state:
- `lastResult: (bool * string) option`

Simplest plugin — straightforward conversion.

### Phase 6: Daemon processChanges

The CAS-based `processingChanges` mutex + debounce timer + ConcurrentBag
is a hand-rolled single-consumer pattern. Replace with an agent that receives
file change events and batches/debounces them internally. Eliminates:
- `Interlocked.CompareExchange` / `Interlocked.Exchange` mutex
- `ConcurrentBag<FileChangeKind>` pending queue
- `suppressedFiles: ConcurrentDictionary`
- debounce timer lifecycle managed via lock

Agent message types:
- `FileChanged of FileChangeKind` (posted by file watcher callback)
- `ScanRequested of force: bool * AsyncReplyChannel<unit>`

The agent's loop handles debouncing with `inbox.TryReceive(debounceMs)` —
if no new messages arrive within the window, it processes the batch.

### Phase 7: ScanSignal

`ScanSignal` uses lock + mutable waiter list. Replace with an agent:
- `WaitForGeneration of afterGen: int64 * TaskCompletionSource<unit>`
- `SignalGeneration of int64`

### Phase 8: ErrorLedger

`ErrorLedger` uses `ConcurrentDictionary` with version-guarded updates.
Replace with an agent that serializes all reads/writes:
- `Report of plugin: string * file: string * errors: ErrorEntry list * version: int64`
- `Clear of plugin: string * file: string`
- `GetErrors of AsyncReplyChannel<...>`

## What stays as-is

**PluginHost event dispatch**: This is pub/sub fan-out to multiple plugins.
Wrapping it in a single agent would unnecessarily serialize cross-plugin work.
The host stays as events + parallel dispatch — each consumer (plugin agent)
receives messages via `Post` from the host's event handlers.

**CheckPipeline**: Already uses `SemaphoreSlim` for file-level cancellation
tokens and `ConcurrentDictionary` for project options. These are stable
registrations (written at discovery, read during checks) — no races to fix.

**InMemoryCheckCache**: Uses a lock around Dictionary + LinkedList for LRU.
Could become an agent but the lock is simple and correct. Low value.

## PluginHost integration

PluginHost event dispatch would `Post` to plugin agents instead of invoking
handlers directly. `RegisterCommand` handlers would use `PostAndAsyncReply`
for request/response patterns (e.g., affected-tests, test-results).

## Type-level state machines

Use phantom types to make invalid state transitions a compile error.
Complements MailboxProcessor — the agent serializes access at runtime,
the type system enforces valid transitions at compile time.

### TestPrunePlugin: test runner lifecycle

```fsharp
type Idle = Idle
type Running = Running

type TestRunner<'State> = private { Config: TestConfig list; Results: TestResults option }

let startTests (runner: TestRunner<Idle>) : Async<TestRunner<Running>> = ...
let awaitCompletion (runner: TestRunner<Running>) : Async<TestResults * TestRunner<Idle>> = ...
// startTests on a Running runner is a compile error
```

Eliminates the `testsRunning` bool entirely — you can't call `startTests`
when tests are already running because the types don't unify.

### BuildPlugin: build lifecycle

```fsharp
type BuildRunner<'State> = private { LastResult: (bool * string) option }

let startBuild (runner: BuildRunner<Idle>) : Async<BuildRunner<Running>> = ...
let awaitBuild (runner: BuildRunner<Running>) : Async<BuildResult * BuildRunner<Idle>> = ...
```

Eliminates the `building` bool guard.

### Daemon: scan lifecycle

```fsharp
type Scanner<'State> = private { Generation: int64 }

let beginScan (scanner: Scanner<Idle>) : Scanner<Running> = ...
let completeScan (scanner: Scanner<Running>) : Scanner<Idle> = ...
```

Replaces `ScanSemaphore` — the type system prevents starting a scan
while one is running. The semaphore currently enforces this at runtime;
the phantom type moves it to compile time.

### Where it doesn't fit

State machines with many transitions or dynamic branching (e.g., the full
plugin status lifecycle: Pending → Running → Completed/Failed) are awkward
as phantom types — too many type parameters. Use a regular DU for those
and let the agent's pattern match enforce valid transitions.

## Risks and tradeoffs

- MailboxProcessor has no backpressure — unbounded mailbox. Not a concern
  here since event rates are low (file changes are debounced).
- `PostAndAsyncReply` adds latency for IPC commands (message round-trip vs
  direct Volatile.Read). Negligible for this use case.
- Error handling: unhandled exceptions in the agent loop silently kill it.
  Need a wrapper that logs and restarts, or use a try/with in the loop.
