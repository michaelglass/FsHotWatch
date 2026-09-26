/// Opt-in per-test trace recording: the `tests.traces` settings from `.fshw.json`.
/// Parsed and carried here; nothing in this file launches or records anything.
namespace FsHotWatch.TestPrune

/// When a test run records per-test traces (`tests.traces.record`).
type TraceRecordPolicy =
    /// Never record. The default when the key is absent.
    | RecordOff
    /// Record only runs that execute every project in full (`confirm`, nightly).
    | RecordFullRuns
    /// Record every run; an impact-selected run refreshes only the tests it ran.
    | RecordEveryRun

/// How the test project's own assembly is woven (`tests.traces.weaveTests`).
type TraceWeaveTests =
    /// Call sites only (`"sites"`, the default).
    | WeaveTestSites
    /// Method-entry probes as well (`"full"`).
    | WeaveTestFull

/// The `tests.traces` block of `.fshw.json`.
type TraceSettings =
    {
        /// When to record.
        Record: TraceRecordPolicy
        /// Repo-relative as written in .fshw.json; resolved against the repo root at registration.
        DbPath: string
        /// How the test assembly itself is woven.
        WeaveTests: TraceWeaveTests
        /// Repo-relative files whose content keys a recorded trace's fingerprint.
        FingerprintInputs: string list
        /// Environment variable names whose values key a recorded trace's fingerprint.
        FingerprintEnv: string list
        /// Upper bound, in seconds, on verifying a woven copy before launching it.
        VerifyTimeoutSec: int
    }

/// Defaults and value parsing for `TraceSettings`.
[<RequireQualifiedAccess>]
module TraceSettings =
    /// Where the trace database lives when `tests.traces.db` is absent.
    [<Literal>]
    let defaultDbPath = ".fshw/test-traces.db"

    /// The verify timeout when `tests.traces.verifyTimeoutSec` is absent.
    [<Literal>]
    let defaultVerifyTimeoutSec = 300

    /// Parse the `record` value (case-insensitive). Unknown text is `None`: the caller
    /// refuses the configuration.
    let parseRecord (s: string) : TraceRecordPolicy option =
        match s.ToLowerInvariant() with
        | "off" -> Some RecordOff
        | "full-runs" -> Some RecordFullRuns
        | "every-run" -> Some RecordEveryRun
        | _ -> None

/// How a traced project is launched: its woven apphost, run directly.
type TracedLaunchSpec =
    {
        /// The shadow apphost, `<projectDir>/bin/Traced/<tfm>/<AssemblyName>`.
        Command: string
        /// The app's own arguments, unquoted; `TracedLaunch.argsLine` joins them.
        Args: string list
        /// The project's configured environment plus `DOTNET_ROOT`.
        Environment: (string * string) list
    }

/// Deriving a traced launch from a project's `dotnet run` configuration. Pure: nothing
/// here touches the file system or starts a process.
///
/// A traced project runs its woven apphost from `bin/Traced/<tfm>/` directly, because
/// `dotnet run --no-build` resolves the ORIGINAL output path and would run the unwoven
/// binary. So the `dotnet run` options are consumed and the app's own arguments kept; an
/// option this does not understand refuses the traced launch rather than guessing, and
/// the project then runs untraced exactly as it does today.
[<RequireQualifiedAccess>]
module TracedLaunch =
    /// `dotnet run` options consumed together with the value that follows them.
    let private withValue =
        set [ "--project"; "-p"; "-c"; "--configuration"; "-f"; "--framework" ]

    /// `dotnet run` flags consumed on their own.
    let private flags = set [ "--no-build"; "--no-restore" ]

    let private tokens (s: string) =
        FsHotWatch.ProcessHelper.splitArgs s |> Option.map List.ofArray

    /// The app arguments a `dotnet run …` config line passes to the app, or why they
    /// cannot be derived. `extraArgs` (filter, CTRF and coverage arguments, each a
    /// command-line fragment) are appended in order; every standalone `--` is dropped.
    let appArgs (configCommand: string) (configArgs: string) (extraArgs: string list) : Result<string list, string> =
        let rec consume (ts: string list) (acc: string list) =
            match ts with
            | [] -> Ok(List.rev acc)
            | "--" :: rest -> Ok(List.rev acc @ (rest |> List.filter ((<>) "--")))
            | t :: _ :: rest when withValue.Contains t -> consume rest acc
            | t :: rest when flags.Contains t -> consume rest acc
            | t :: _ -> Error $"unrecognized-run-option:%s{t}"

        let extra =
            extraArgs
            |> List.map tokens
            |> List.fold
                (fun acc next ->
                    match acc, next with
                    | Some a, Some n -> Some(a @ n)
                    | _ -> None)
                (Some [])

        match configCommand, tokens configArgs, extra with
        | _, None, _
        | _, _, None -> Error "unparseable-args"
        | "dotnet", Some("run" :: rest), Some extra ->
            consume rest []
            |> Result.map (fun head -> head @ (extra |> List.filter ((<>) "--")))
        | _ -> Error "not-a-dotnet-run-command"

    /// The shadow apphost: `<projectDir>/bin/Traced/<tfm>/<assemblyName>`, with `.exe` on
    /// Windows. A sibling of `bin/Debug/<tfm>/` at the same depth, so tests that probe
    /// upward for a repository marker behave identically.
    let apphostPath (isWindows: bool) (projectDir: string) (tfm: string) (assemblyName: string) =
        let name = if isWindows then assemblyName + ".exe" else assemblyName
        System.IO.Path.Combine(projectDir, "bin", "Traced", tfm, name)

    /// The `DOTNET_ROOT` a directly launched apphost needs; `dotnet run` sets it for its
    /// child, an apphost started by fshw does not get it. In order:
    ///
    /// 1. a non-empty `DOTNET_ROOT` in the environment, as is;
    /// 2. the directory of the resolved `dotnet` muxer (`DOTNET_HOST_PATH`), if a runtime
    ///    lives there (a wrapper script's directory, as on Nix, has none);
    /// 3. the root of the runtime `runtimeDir` names (`…/shared/Microsoft.NETCore.App/<v>/`,
    ///    three levels up), if a runtime lives there.
    ///
    /// `None` when no candidate holds a runtime: the caller refuses the traced launch.
    let dotnetRoot (getEnv: string -> string option) (hasRuntime: string -> bool) (runtimeDir: string) : string option =
        let nonEmpty key =
            getEnv key |> Option.filter (System.String.IsNullOrEmpty >> not)

        match nonEmpty "DOTNET_ROOT" with
        | Some root -> Some root
        | None ->
            let muxerDir =
                nonEmpty "DOTNET_HOST_PATH"
                |> Option.bind (System.IO.Path.GetDirectoryName >> Option.ofObj)

            let runtimeRoot =
                System.IO.Path.GetFullPath(System.IO.Path.Combine(runtimeDir, "..", "..", ".."))

            [ yield! Option.toList muxerDir; runtimeRoot ] |> List.tryFind hasRuntime

    /// Read `key` through `read` (null when unset). `DOTNET_HOST_PATH` is read as its
    /// final link target, so its directory is the real install rather than a wrapper's.
    let readEnvResolvingHost (read: string -> string) (key: string) : string option =
        match read key with
        | null -> None
        | value when key = "DOTNET_HOST_PATH" && value <> "" ->
            // A missing or unreadable path throws; it is read as is (as ProcessHelper does
            // for a child's DOTNET_HOST_PATH), and `dotnetRoot` then finds no runtime beside it.
            try
                match System.IO.File.ResolveLinkTarget(value, returnFinalTarget = true) with
                | null -> Some value
                | target -> Some target.FullName
            with _ ->
                Some value
        | value -> Some value

    /// `dotnetRoot` for this process: its environment (with `DOTNET_HOST_PATH` realpath'd),
    /// a runtime present when `shared/Microsoft.NETCore.App` exists, and the runtime this
    /// process itself runs on.
    let dotnetRootOfThisProcess () : string option =
        let getEnv = readEnvResolvingHost System.Environment.GetEnvironmentVariable

        let hasRuntime (root: string) =
            System.IO.Directory.Exists(System.IO.Path.Combine(root, "shared", "Microsoft.NETCore.App"))

        dotnetRoot getEnv hasRuntime (System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())

    /// A traced launch's environment: the project's own, plus `dotnetRoot` as `DOTNET_ROOT`
    /// unless the project sets one itself.
    let environment (dotnetRoot: string) (projectEnv: (string * string) list) =
        if projectEnv |> List.exists (fun (k, _) -> k = "DOTNET_ROOT") then
            projectEnv
        else
            projectEnv @ [ "DOTNET_ROOT", dotnetRoot ]

    /// The launch of `apphost` for a project configured as `configCommand configArgs`
    /// with `projectEnv`. The project's own `DOTNET_ROOT`, if it sets one, is kept.
    let spec
        (apphost: string)
        (dotnetRoot: string)
        (configCommand: string)
        (configArgs: string)
        (extraArgs: string list)
        (projectEnv: (string * string) list)
        : Result<TracedLaunchSpec, string> =
        appArgs configCommand configArgs extraArgs
        |> Result.map (fun args ->
            { Command = apphost
              Args = args
              Environment = environment dotnetRoot projectEnv })

    /// The argument line for `ProcessStartInfo.Arguments`, each argument quoted by
    /// `ProcessHelper.quoteArg` so `splitArgs` yields it back verbatim.
    let argsLine (args: string list) =
        args |> List.map FsHotWatch.ProcessHelper.quoteArg |> String.concat " "
