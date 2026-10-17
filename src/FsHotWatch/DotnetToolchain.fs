/// The .NET toolchain a `dotnet` started by this process resolves.
///
/// A cached build or test verdict is the answer of the code AND of the SDK that built
/// it. "A compatible .NET SDK was not found" is a failure of the environment, not of
/// the tree: once PATH or DOTNET_ROOT is fixed the same tree builds, so a task cache
/// keyed on the tree alone replays the old failure as `(cached)`. The identity here is
/// what `TaskCache.ToolchainScopedTaskCache` folds into every task key.
module FsHotWatch.DotnetToolchain

open System
open System.Collections.Concurrent
open System.IO
open FsHotWatch.ProcessHelper

/// The nearest `global.json` at or above `dir`: the file whose `sdk` section selects
/// the SDK a `dotnet` started there resolves.
let tryFindGlobalJson (dir: string) : string option =
    let rec walk (current: DirectoryInfo) =
        if isNull current then
            None
        else
            let candidate = Path.Combine(current.FullName, "global.json")

            if File.Exists candidate then
                Some candidate
            else
                walk current.Parent

    walk (DirectoryInfo dir)

/// The identity of a resolved toolchain: what `dotnet --version` answered (its version,
/// or how it failed), the DOTNET_ROOT it resolved under, and the content of the
/// `global.json` that selected it.
let describe (versionAnswer: string) (dotnetRoot: string option) (globalJson: string option) : string =
    let dotnetRoot = defaultArg dotnetRoot "<unset>"
    let globalJson = defaultArg globalJson "<none>"

    [ $"dotnet --version=%s{versionAnswer}"
      $"DOTNET_ROOT=%s{dotnetRoot}"
      $"global.json=%s{globalJson}" ]
    |> String.concat "\n"

let private versionLine =
    Text.RegularExpressions.Regex(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-+]*)?$")

/// The version line in what a successful `dotnet --version` printed. Its stdout and
/// stderr arrive merged, and a `dotnet` child writes diagnostics of its own beside the
/// version, nondeterministically ("CNamedPipe::Write - Writing message to named pipe
/// failed.: Bad file descriptor" on GitHub's Linux runners). Two asks under one SDK
/// must answer alike, so only the version is the answer. Output with no version line
/// in it is kept whole: what it says is all there is to tell one apart from another.
let private versionOf (printed: string) : string =
    printed.Split('\n')
    |> Array.map (fun line -> line.Trim())
    |> Array.tryFind versionLine.IsMatch
    |> Option.defaultWith (fun () -> printed.Trim())

/// `dotnet --version`, run by `run`, as the answer `describe` records: the version on
/// success, the exit code and output on failure. A failure is an identity too — it is
/// the one a fixed environment must not share — and so is a spawn that threw.
let internal answerOf (run: unit -> ProcessOutcome) : string =
    try
        match run () with
        | Succeeded output -> versionOf (renderOutput output)
        | Failed(code, output) -> $"failed (exit %d{code}): %s{(renderOutput output).Trim()}"
        | TimedOut _ as outcome -> $"failed: %s{outputOf outcome}"
    with ex ->
        $"failed: %s{ex.GetType().Name}: %s{ex.Message}"

/// `dotnet --version` in `workDir`, which a `global.json` there or above selects for.
let private askVersion (workDir: string) : string =
    answerOf (fun () -> runProcess "dotnet" "--version" workDir [] (ProcessBounds.silent (TimeSpan.FromSeconds 30.0)))

/// Resolves identities, one `dotnet --version` per SDK selection and environment.
///
/// A child of this process runs in this process's environment, which is fixed for its
/// lifetime, unless a repository host's session is in scope: then `ProcessHelper`
/// replaces the child's whole environment with the session's, PATH and DOTNET_ROOT
/// included. Answers are keyed by the `global.json` content and by that session
/// environment, so an answer resolved in one session is never served outside it or in
/// another. Every checkout without a `global.json`, or with the same one, in the same
/// environment pays for a single `dotnet --version`. `ask` runs it in a directory —
/// supplied, for tests.
type internal Resolver(ask: string -> string) =
    let resolved = ConcurrentDictionary<string, Lazy<string>>()

    member _.Identity(repoRoot: string) : string =
        let globalJsonPath = tryFindGlobalJson repoRoot
        let globalJson = globalJsonPath |> Option.map File.ReadAllText

        let session =
            SessionScope.SessionEnvironment.current ()
            |> Option.map SessionScope.SessionEnvironment.variables

        let dotnetRoot =
            match session with
            | Some variables -> Map.tryFind "DOTNET_ROOT" variables
            | None -> Environment.GetEnvironmentVariable "DOTNET_ROOT" |> Option.ofObj

        // Empty for this process's own environment, which cannot change under it.
        let environment =
            session
            |> Option.map (Map.toList >> List.map (fun (name, value) -> $"%s{name}=%s{value}"))
            |> Option.defaultValue []

        let workDir =
            globalJsonPath
            |> Option.map Path.GetDirectoryName
            |> Option.defaultValue repoRoot

        let key = describe "" dotnetRoot globalJson :: environment |> String.concat "\n"

        resolved.GetOrAdd(key, fun _ -> lazy (describe (ask workDir) dotnetRoot globalJson)).Value

let private shared = Resolver(askVersion)

/// The toolchain a `dotnet` child of this process resolves in `repoRoot`, resolved
/// once per process (see `Resolver`).
let identity (repoRoot: string) : string = shared.Identity repoRoot
