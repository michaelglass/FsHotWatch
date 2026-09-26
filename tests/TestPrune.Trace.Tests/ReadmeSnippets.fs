/// The code blocks of `src/TestPrune.Trace/README.md`, compiled. SyncDocs renders each
/// `// sync:<name>:start` … `// sync:<name>:end` region into the README block of the same
/// name, and `mise run sync-docs-check` fails when the two drift. The binding test swaps the
/// process-wide recorder, so these run in the non-parallel counters collection.
[<Xunit.Collection("recorder-counters")>]
module TestPrune.Trace.Tests.ReadmeSnippets

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace

/// The README's host flow, as a function of what a host already has.
let hostOneProject
    (repoRoot: string)
    (projectDir: string)
    (runDir: string)
    (store: TraceStore.Store)
    (runId: string)
    (treeHash: string)
    (treeHashNow: unit -> string)
    (symbolStore: TestPrune.Ports.SymbolStore)
    (runTests: TraceSession.TraceLaunch -> string)
    =
    // sync:trace-hosting:start
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
// sync:trace-hosting:end

// sync:trace-scope:start
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
// sync:trace-scope:end

[<Fact>]
let ``the README's reflection binding finds every Scopes method and is inert untraced`` () =
    let scopes =
        Type.GetType("TestPrune.Trace.Recorder.Scopes, TestPrune.Trace.Recorder", false)

    test <@ not (isNull scopes) @>

    for name in [ "Enter"; "Exit"; "CurrentKey"; "LinkCurrentTo" ] do
        test <@ not (isNull (scopes.GetMethod name)) @>

    // Loaded but inactive, as in an untraced process (even when this suite itself runs traced).
    let saved = TestPrune.Trace.Recorder.Runtime.state
    TestPrune.Trace.Recorder.Runtime.state <- null

    try
        TraceScope.enter "T:readme"
        TraceScope.linkCurrentTo "P:readme"
        TraceScope.exit ()
        test <@ isNull (TraceScope.currentKey ()) @>
    finally
        TestPrune.Trace.Recorder.Runtime.state <- saved
