/// The environment strings a dump records. The dump writer and a host's fingerprint
/// prediction both use these, so a prediction compares like with like.
module TestPrune.Trace.Recorder.RuntimeIdentity

open System.Runtime.InteropServices

/// The OS name, given a platform test (injected so every branch is testable).
let osNameOf (isOs: OSPlatform -> bool) =
    if isOs OSPlatform.OSX then "OSX"
    elif isOs OSPlatform.Linux then "Linux"
    elif isOs OSPlatform.Windows then "Windows"
    else "Other"

/// `RuntimeInformation.FrameworkDescription`, e.g. ".NET 10.0.0".
let runtime () = RuntimeInformation.FrameworkDescription

/// This process's OS, as a dump's `os` field records it.
let os () =
    osNameOf RuntimeInformation.IsOSPlatform

/// This process's architecture, e.g. "Arm64".
let arch () =
    string RuntimeInformation.ProcessArchitecture
