namespace global

open System
open System.Reflection
open TestPrune.Trace.Recorder

/// Loaded through DOTNET_STARTUP_HOOKS; the runtime requires exactly this shape (a type named
/// `StartupHook` in no namespace with a public static `Initialize`). Inert unless
/// TESTPRUNE_TRACE_VERIFY names a report path: then it JIT-verifies the listed methods in
/// this app's runtime, writes the report and exits before the app's Main runs.
[<AbstractClass; Sealed>]
type StartupHook =
    /// The hook body with its effects passed in.
    static member internal Run(getEnv: string -> string, exit: int -> unit) =
        if JitVerify.runFromEnvironment getEnv (fun name -> Assembly.Load(AssemblyName name)) then
            exit 0

    /// Called by the runtime before Main.
    static member Initialize() =
        StartupHook.Run(Environment.GetEnvironmentVariable, Environment.Exit)
