/// The environment fingerprint a traced launch will record, computed before it starts, so a
/// selection made at launch reads only traces recorded in the environment about to run
/// (plan D4). The runtime comes from `RuntimeResolution` over the build's runtimeconfig; the
/// deps.json hash from the same build output the shadow bin weaves; everything else is what
/// ingestion gathers from this process.
module TestPrune.Trace.FingerprintPrediction

open System.IO
open TestPrune.Trace.Model

/// What a prediction reads.
type Request =
    {
        RepoRoot: string
        ProjectDir: string
        AssemblyName: string
        /// `tests.traces.fingerprintInputs`.
        Files: string list
        /// `tests.traces.fingerprintEnv`.
        Env: string list
        WeaveTests: WeaveMode
        /// The DOTNET_ROOT the traced apphost is launched with.
        DotnetRoot: string
        /// `DOTNET_ROLL_FORWARD` as the launch will see it.
        RollForwardEnv: string option
    }

/// The hash a fingerprint keys an app's original deps.json text by (`ShadowBin`'s).
let depsJsonSha256 (text: string) = ShadowBin.depsJsonSha256 text

/// The fingerprint inputs the launch will record, or why they cannot be predicted (no
/// single build output, or no installed runtime satisfies the runtimeconfig).
let predict (req: Request) : Result<Fingerprint.Inputs, string> =
    match ShadowBin.buildOutput req.ProjectDir req.AssemblyName with
    | Error refusal -> Error(ShadowBin.describeRefusal refusal)
    | Ok dir ->
        let config = Path.Combine(dir, req.AssemblyName + ".runtimeconfig.json")
        let deps = Path.Combine(dir, req.AssemblyName + ".deps.json")

        match RuntimeResolution.predictRuntime req.DotnetRoot config req.RollForwardEnv, File.Exists deps with
        | None, _ -> Error $"no installed runtime under %s{req.DotnetRoot} satisfies %s{config}"
        | Some _, false -> Error $"no deps.json at %s{deps}"
        | Some runtime, true ->
            Ok(
                Fingerprint.ofEnvironment
                    req.RepoRoot
                    req.Files
                    req.Env
                    (runtime,
                     TestPrune.Trace.Recorder.RuntimeIdentity.os (),
                     TestPrune.Trace.Recorder.RuntimeIdentity.arch ())
                    (depsJsonSha256 (File.ReadAllText deps))
                    req.WeaveTests
            )
