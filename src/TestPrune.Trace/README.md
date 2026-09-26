# TestPrune.Trace

Records which code each test actually executed, so that test selection can later be
checked against what really ran rather than only against the static dependency graph.

> **Status: early alpha, record only.** Traces are recorded and stored; nothing in
> TestPrune selects tests from them yet. Behavior and APIs shift between versions.

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

A test runner needs only `TraceSession` (and `Ctrf` to read outcomes):

```fsharp
open TestPrune.Trace

match
    TraceSession.prepareProject
        { RepoRoot = repoRoot
          ProjectDir = projectDir            // holds bin/Debug/<tfm>/
          AssemblyName = "MyTests"
          TestProject = "MyTests"
          WeaveTests = Model.SitesOnly       // or Model.Full
          RunDir = runDir                    // dumps go to <RunDir>/traces/<TestProject>/
          VerifyTimeout = TimeSpan.FromMinutes 2.0 }
with
| Error reason ->
    // The project cannot be traced: record why, then run it untraced, as usual.
    TraceSession.recordRefusal store runId "MyTests" TraceStore.FullRun treeHash reason
| Ok launch ->
    // Run launch.Apphost with launch.Env added to the environment, plus your own
    // arguments; ask for a CTRF report.
    let outcomes = Ctrf.parse (File.ReadAllText ctrfPath)

    match
        TraceSession.ingestProject store repoRoot launch
            { RunId = runId; Kind = TraceStore.FullRun
              LaunchTreeHash = treeHash; CurrentTreeHash = treeHashNow
              Outcomes = outcomes; Symbols = symbolStore
              FingerprintFiles = []; FingerprintEnv = [] }
    with
    | Ok summary -> ()   // summary.Executed, .Traced, .Complete, .ReasonCounts, …
    | Error reason -> ()  // log it; the run's verdict is unaffected
```

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

```fsharp
/// Binds TestPrune.Trace.Recorder.Scopes when the process is traced; every call is a no-op otherwise.
module TraceScope =
    let private scopes =
        System.Type.GetType("TestPrune.Trace.Recorder.Scopes, TestPrune.Trace.Recorder", false)

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

The header convention:

- the test's HTTP client sends `X-Test-Trace-Scope: <TraceScope.currentKey ()>`;
- the server's middleware calls `TraceScope.enter` with the header value when it starts
  with `T:`, and otherwise `TraceScope.enter ("P:" + poolName)`, so work done for no
  particular test is recorded as the pool's. It calls `TraceScope.exit ()` when the
  request ends.

## Coverage

The recorder assembly is copied into the traced app without its PDB. MS CodeCoverage skips
modules that have no symbols, so the recorder does not appear in your coverage report. If
it ever does, exclude it in your coverage settings with a `<ModulePath>` entry matching
`TestPrune\.Trace\.Recorder\.dll`.

## Release builds are refused

The weaver refuses optimized assemblies. A Release build inlines small functions across
assemblies, so their entry probes would never fire and a trace would silently miss code.
Build the traced run in Debug. A refused project runs untraced, exactly as it would
without TestPrune.Trace.

The weaver also refuses when nothing would be woven. That happens when no assembly in the
build output has a portable PDB naming a source file under the repository root, most often
because `ContinuousIntegrationBuild`, `DeterministicSourcePaths` or a `PathMap` rewrote the
PDB paths to `/_/`.
