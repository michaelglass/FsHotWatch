/// The host facade: everything a test runner needs to record traces for one test
/// project. A host calls `prepareProject`, launches `TraceLaunch.Apphost` with
/// `TraceLaunch.Env` added to its own environment and arguments, then calls
/// `ingestProject` with the run's CTRF outcomes. Neither throws: on `Error` the host
/// records the refusal and runs the project untraced, exactly as it would without traces.
module TestPrune.Trace.TraceSession

open System
open System.IO
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

/// One test project to trace.
type PrepareRequest =
    {
        /// Repository root: assemblies whose PDBs name documents under it are woven, and
        /// the recorder keeps only file inputs under it.
        RepoRoot: string
        /// The test project's directory (holding `bin/Debug/<tfm>/`).
        ProjectDir: string
        /// The test app's assembly name.
        AssemblyName: string
        /// The test project's name, used for its dump directory and its stored traces.
        TestProject: string
        /// Weave mode for the test assembly itself.
        WeaveTests: WeaveMode
        /// The run's working directory; dumps go under `traces/<TestProject>/`.
        RunDir: string
        /// How long JIT verification of a new weave may take.
        VerifyTimeout: TimeSpan
    }

/// How to launch a prepared project, and what ingestion needs afterwards.
type TraceLaunch =
    {
        TestProject: string
        /// The woven app to run instead of the one in `bin/Debug`.
        Apphost: string
        /// Environment to add to the launch: the recorder's dump directory, id count and
        /// repository root, and `DOTNET_ROOT`.
        Env: (string * string) list
        /// Where the run's processes write their dumps (created empty by `prepareProject`).
        DumpDir: string
        /// The root the recorder filters file inputs against.
        InputRoot: string
        Shadow: ShadowBin.Shadow
    }

/// What the host knows once the traced run finished.
type Completion =
    {
        RunId: string
        Kind: TraceStore.RunKind
        /// The input tree hash when the run was launched.
        LaunchTreeHash: string
        /// The input tree hash now.
        CurrentTreeHash: string
        /// The project's CTRF outcomes (see `Ctrf.parse`).
        Outcomes: TestOutcome list
        Symbols: TestPrune.Ports.SymbolStore
        /// Repo-relative files folded into the environment fingerprint.
        FingerprintFiles: string list
        /// Environment variables folded into the environment fingerprint.
        FingerprintEnv: string list
    }

/// Build the project's shadow bin and a fresh dump directory. `Error` carries a one-line
/// reason: the project cannot be traced (no build output, a refused weave, failed JIT
/// verification, or nothing woven at all) and runs untraced. Never throws.
let prepareProject (req: PrepareRequest) : Result<TraceLaunch, string> =
    try
        let shadow =
            ShadowBin.prepare
                { RepoRoot = req.RepoRoot
                  ProjectDir = req.ProjectDir
                  AssemblyName = req.AssemblyName
                  WeaveTests = req.WeaveTests
                  Passes = [ SiteProbes.pass (); Redirects.pass () ]
                  VerifyTimeout = req.VerifyTimeout }

        match shadow with
        | Error refusal -> Error(ShadowBin.describeRefusal refusal)
        // Nothing probed: every test would look traced with an empty trace.
        | Ok shadow when shadow.Manifest.Rows.Length = 0 -> Error(TraceIngest.nothingWovenReason req.RepoRoot)
        | Ok shadow ->
            let dumpDir = Path.Combine(req.RunDir, "traces", req.TestProject)

            // A dump left by an earlier run would be ingested as this run's.
            if Directory.Exists dumpDir then
                Directory.Delete(dumpDir, true)

            Directory.CreateDirectory dumpDir |> ignore

            Ok
                { TestProject = req.TestProject
                  Apphost = shadow.Apphost
                  DumpDir = dumpDir
                  InputRoot = req.RepoRoot
                  Shadow = shadow
                  Env =
                    [ Contract.OutEnv, dumpDir
                      Contract.IdsEnv, string shadow.Manifest.IdCount
                      Contract.RepoRootEnv, req.RepoRoot
                      "DOTNET_ROOT", Launch.dotnetRoot () ] }
    with ex ->
        Error $"trace preparation failed: %s{ex.Message}"

/// Store the traces of a finished run. `repoRoot` is the root the joiner resolves PDB
/// documents against (the repository root in production). Never throws.
let ingestProject
    (store: TraceStore.Store)
    (repoRoot: string)
    (launch: TraceLaunch)
    (c: Completion)
    : Result<TraceIngest.IngestSummary, string> =
    try
        Ok(
            TraceIngest.ingest
                store
                { RunId = c.RunId
                  TestProject = launch.TestProject
                  RepoRoot = repoRoot
                  InputRoot = launch.InputRoot
                  Kind = c.Kind
                  LaunchTreeHash = c.LaunchTreeHash
                  CurrentTreeHash = c.CurrentTreeHash
                  DumpDir = launch.DumpDir
                  Shadow = launch.Shadow
                  Outcomes = c.Outcomes
                  Symbols = c.Symbols
                  FingerprintFiles = c.FingerprintFiles
                  FingerprintEnv = c.FingerprintEnv
                  RecordedAt = DateTimeOffset.UtcNow }
        )
    with ex ->
        Error $"trace ingestion failed: %s{ex.Message}"

/// Store a project the host could not trace (a `prepareProject` error) as a refused run,
/// so its absence of traces is explained rather than silent.
let recordRefusal
    (store: TraceStore.Store)
    (runId: string)
    (testProject: string)
    (kind: TraceStore.RunKind)
    (treeHash: string)
    (reason: string)
    : unit =
    store.RecordRunWithoutTraces
        { RunId = runId
          TestProject = testProject
          TreeHash = treeHash
          EnvFingerprint = ""
          RecordedAt = DateTimeOffset.UtcNow
          Kind = kind
          Status = TraceStore.Refused
          Reason = reason
          StatsJson = "{}" }
