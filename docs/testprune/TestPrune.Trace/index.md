<!-- sync:testprune-trace-readme -->
# TestPrune.Trace

Records which code each test actually executed, so that test selection can later be
checked against what really ran rather than only against the static dependency graph.

> **Status: early alpha, record only.** Traces are recorded and stored; nothing in
> TestPrune selects tests from them yet. Behavior and APIs shift between versions.

Three packages release together under one `trace-v` tag:

| Package | What it is |
|---------|------------|
| [`TestPrune.Trace`](https://www.nuget.org/packages/TestPrune.Trace) | The library a test runner hosts: weaver, shadow bin, ingestion, trace store, measurements |
| [`TestPrune.Trace.Recorder`](https://www.nuget.org/packages/TestPrune.Trace.Recorder) | The recorder loaded into the traced test process. You never reference it: the weaver adds it to the traced copy of your app |
| [`TestPrune.Trace.Cli`](https://www.nuget.org/packages/TestPrune.Trace.Cli) | The `test-prune-traces` tool: census, isolation audit, CPU overhead and file census |

```bash
dotnet add package TestPrune.Trace
dotnet tool install TestPrune.Trace.Cli
```

For a Debug build of a test project, TestPrune.Trace:

1. writes a woven copy of the build output to `bin/Traced/<tfm>/`, beside `bin/Debug/<tfm>/`.
   Every unchanged file is a hardlink. Each assembly built from your repository gets probes
   on method entry, union-case and type use, and static constructors, plus redirected file
   reads and process starts. A small recorder assembly is added to the app;
2. JIT-verifies every woven method in the test app's own runtime before the copy is used;
3. after the run, reads the dump each process wrote at exit and joins its probe ids to
   TestPrune's symbols at their current content hashes. It then stores one trace per test
   in a separate SQLite file: the symbols the test executed, the repository files it read,
   and the fixture scopes it inherited.

A trace is either complete or stored with the reasons it is not. Examples: the test did
not pass, a source file changed since the build, or the test started a child process that
recorded nothing. Nothing is silently partial.

## Hosting a traced run

A test runner needs only `TraceSession` (and `Ctrf` to read outcomes), all under
`open TestPrune.Trace`:

<!-- sync:trace-hosting:start src=tests/TestPrune.Trace.Tests/ReadmeSnippets.fs -->
```fsharp
match
    TraceSession.prepareProject
        { RepoRoot = repoRoot
          ProjectDir = projectDir // holds bin/Debug/<tfm>/
          AssemblyName = "MyTests"
          TestProject = "MyTests"
          WeaveTests = Model.SitesOnly // or Model.Full
          RunDir = runDir // dumps go to <RunDir>/traces/<TestProject>/
          VerifyTimeout = TimeSpan.FromMinutes 2.0 }
with
| Error reason ->
    // The project cannot be traced: record why, then run it untraced, as usual.
    TraceSession.recordRefusal store runId "MyTests" TraceStore.FullRun treeHash reason
| Ok launch ->
    // Run launch.Apphost with launch.Env added to the environment, plus your own
    // arguments, and ask for a CTRF report. runTests returns the report's path.
    let ctrfPath = runTests launch
    let outcomes = Ctrf.parse (File.ReadAllText ctrfPath)

    match
        TraceSession.ingestProject
            store
            repoRoot
            launch
            { RunId = runId
              Kind = TraceStore.FullRun
              LaunchTreeHash = treeHash
              CurrentTreeHash = treeHashNow ()
              Outcomes = outcomes
              Symbols = symbolStore
              FingerprintFiles = []
              FingerprintEnv = [] }
    with
    | Ok summary -> printfn $"traced %d{summary.Traced} of %d{summary.Executed}, %d{summary.Complete} complete"
    | Error reason -> eprintfn $"traces not stored: %s{reason}" // the run's verdict is unaffected
```
<!-- sync:trace-hosting:end -->

`store` is `TraceStore.Store.Open path`. `prepareProject` and `ingestProject` never throw.
A trace failure never changes a test run's verdict.

`launch.Env` holds exactly `TESTPRUNE_TRACE_OUT` (the dump directory, created empty),
`TESTPRUNE_TRACE_IDS` (the probe id count), `TESTPRUNE_TRACE_REPO_ROOT` (file inputs outside
it are ignored) and `DOTNET_ROOT`.

## The scope contract, for tests that call an in-process server

The recorder attributes each probe hit to the current xUnit v3 test through
`TestContext.Current`. Code that runs on a server's own threads, such as a request handler
in an in-process web host, is not inside any test's async flow, so its hits are attributed
to no test. A server shared by many tests should tell the recorder whose request it is
serving. The recorder exposes four static methods on `TestPrune.Trace.Recorder.Scopes`:

| Method | Does |
|---|---|
| `Enter(key: string)` | Overrides the scope of the current async flow with `key`. |
| `Exit()` | Ends that override. |
| `CurrentKey() : string` | The current scope key (`T:<test id>` inside a test), or `null` when the recorder is inactive. |
| `LinkCurrentTo(scopeKey: string)` | Makes the current scope inherit everything recorded under `scopeKey`. |

Bind them by reflection, so your code needs no package reference and does nothing in an
untraced process:

<!-- sync:trace-scope:start src=tests/TestPrune.Trace.Tests/ReadmeSnippets.fs -->
```fsharp
/// Binds TestPrune.Trace.Recorder.Scopes when the process is traced; every call is a no-op otherwise.
module TraceScope =
    let private scopes =
        Type.GetType("TestPrune.Trace.Recorder.Scopes, TestPrune.Trace.Recorder", false)

    let private methodOf (name: string) =
        if isNull scopes then null else scopes.GetMethod name

    let private enterMethod = methodOf "Enter"
    let private exitMethod = methodOf "Exit"
    let private currentKeyMethod = methodOf "CurrentKey"
    let private linkMethod = methodOf "LinkCurrentTo"

    let enter (key: string) =
        if not (isNull enterMethod) then
            enterMethod.Invoke(null, [| box key |]) |> ignore

    let exit () =
        if not (isNull exitMethod) then
            exitMethod.Invoke(null, [||]) |> ignore

    let currentKey () : string =
        if isNull currentKeyMethod then
            null
        else
            currentKeyMethod.Invoke(null, [||]) :?> string

    let linkCurrentTo (key: string) =
        if not (isNull linkMethod) then
            linkMethod.Invoke(null, [| box key |]) |> ignore
```
<!-- sync:trace-scope:end -->

The header convention:

- the test's HTTP client sends `X-Test-Trace-Scope: <TraceScope.currentKey ()>`;
- the server's middleware calls `TraceScope.enter` with the header value when it starts
  with `T:`, and otherwise `TraceScope.enter ("P:" + poolName)`, so work done for no
  particular test is recorded as the pool's. It calls `TraceScope.exit ()` when the
  request ends.

## Measuring traces: `test-prune-traces`

Four verbs measure a project against the phase-1 bars. Each prints a report (or JSON with
`--json`) and exits **0** when the bar is met, **1** when it is not, and **2** on a usage
error or when it cannot measure at all (no database, a project that cannot be prepared, a
run that wrote no CTRF report).

| Verb | Measures | Bar |
|---|---|---|
| `census [--db <path>] [--run <runId>] [--json]` | A recorded run in the trace store: traced / executed tests, unattributed (ambient) hits, tests per incomplete reason, pool scopes | traced ≥ 0.99; ambient < 0.1 % of hits, or every ambient symbol listed for review |
| `audit … [--sample 0.01] [--seed 1]` | Runs the project once in parallel, then a seeded sample of its tests one at a time, and compares each test's own probe ids | no id attributed in parallel that the test never hits alone |
| `overhead … [--reps 3]` | Interleaved untraced and traced launches, median CPU (user + system) of each | traced median ≤ 1.15 × untraced median, and every launch exits the same way |
| `file-census …` | Tests whose trace holds a repository file input vs tests that fail when the build output runs outside the repository | the two sets differ by ≤ 5 % of their union |

`census` reads `.fshw/test-traces.db` when it exists, else `.test-prune-traces.db`. A
missing database is refused (exit 2), never created. With no recorded run to measure it
exits 1.

The three process-driving verbs (`audit`, `overhead`, `file-census`) take the same
options:

```text
--project-dir <dir>   the test project's directory (holds bin/Debug/<tfm>/); required
--assembly <name>     the test app's assembly name; required
--repo <root>         the repository root (default: the current directory)
--timeout-min 30      how long one launch may run before its process tree is killed
--json                print the report as JSON
-- <app args>         everything after -- is passed to every launch of the test app
```

Build the project in Debug first. Each verb prepares its own woven copy and works in a
scratch run directory that it deletes afterwards.

What to know before trusting a number:

- **`audit` adds `--filter-display-name <name>`** to each isolated launch. Microsoft Testing
  Platform refuses to mix filter kinds, so a filter of your own after `--` conflicts with
  it. A display name containing `*` acts as a wildcard in that filter and can select more
  than the one test.
- **`audit` samples only tests that recorded a test scope** in the parallel run. A test
  that hit no probe is not sampled, so it cannot fail the audit; `census` is where an
  untraced test shows up.
- **Ids missing in parallel are expected for once-per-process code**: static constructors,
  closure singletons and memoised values that another test ran first. The audit lists them
  by kind and never fails on them; only extra ids fail it.
- **Untraced child processes are the most common incomplete reason** on real suites.
  A test that starts `dotnet`, `sh`, `git` or any process that is not a woven .NET app
  gets `child-process-untraced:<file>`: its trace cannot include what that process read or
  ran. That is the sound answer, not a defect.
- **`overhead` reads `getrusage(RUSAGE_CHILDREN)`**, so it runs on macOS and Linux only,
  not on Windows. The counter covers every child process the tool has reaped, so run
  nothing else from the same shell meanwhile. Grandchildren a test starts count on both
  sides. The reported peak RSS is a running maximum over every launch so far, an upper
  bound on each launch's own peak.
- **`file-census` copies the untraced build output outside the repository and runs it
  there.** It needs a suite that completes in that copy and writes its CTRF report. A test
  that ends the process (`Environment.Exit`) or crashes the runner outside the repository
  makes the verb exit 2 with the tail of the run's output.

### Measured on TestPrune's own suite

TestPrune's suite on macOS arm64, when the verbs landed:

| Measurement | Result | Bar |
|---|---|---|
| CPU overhead (median traced / untraced) | 1.039 | ≤ 1.15 |
| Isolation audit, extra ids in parallel | 0 | 0 |
| Isolation audit, ids missing in parallel | static constructors only | explained |
| Incomplete, by untraced child | `dotnet` 9, `sleep` 5, `sh` 2, `chmod` 1 | reported |
| File census | 0.062 | ≤ 0.05 |

The file census was measured on a filtered subset, before the `test-prune` CLI stopped
ending its process on a "not in a repository" error. A full-suite measurement is part of
the phase-1 done-bar.

## Coverage

The recorder assembly is copied into the traced app without its PDB. MS CodeCoverage skips
modules that have no symbols, so the recorder does not appear in your coverage report. If
it ever does, exclude it in your coverage settings with a `<ModulePath>` entry matching
`TestPrune\.Trace\.Recorder\.dll`.

## What is refused, and what is not recorded

A refused project runs untraced, exactly as it would without TestPrune.Trace.

- **Release builds are refused.** A Release build inlines small functions across
  assemblies, so their entry probes would never fire and a trace would silently miss code.
  Build the traced run in Debug.
- **Nothing to weave is refused at prepare time.** That happens when no assembly in the
  build output has a portable PDB naming a source file under the repository root, most
  often because `ContinuousIntegrationBuild`, `DeterministicSourcePaths` or a `PathMap`
  rewrote the PDB paths to `/_/`.
- **A test app without FSharp.Core fails verification.** The recorder is written in F# and
  loads FSharp.Core 8.0 or later from the app, so a C#-only test app cannot load it. The
  verification error says so.
- **`[<Literal>]` values and `inline` functions are not recorded.** The compiler copies
  them into their callers, so there is no call to probe.
- **A process that is killed leaves no dump**, so its tests keep no trace. The recorder
  writes each process's dump when the process exits normally.

The decisions behind these, and the options measured and deferred, are in the repository's
decision records:
[exit-time dump](https://github.com/michaelglass/TestPrune/blob/main/docs/adr/0005-recorded-traces-exit-dump.md),
[separate trace store](https://github.com/michaelglass/TestPrune/blob/main/docs/adr/0006-trace-store-separate-file.md),
[packaging and release tag](https://github.com/michaelglass/TestPrune/blob/main/docs/adr/0007-trace-packaging-and-release-tag.md),
[deferred and rejected options](https://github.com/michaelglass/TestPrune/blob/main/docs/adr/0008-trace-deferred-options.md).
<!-- sync:testprune-trace-readme:end -->
