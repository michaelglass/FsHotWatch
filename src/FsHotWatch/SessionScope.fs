/// What a worktree session carries in its ExecutionContext when several sessions share
/// one process.
///
/// A per-worktree daemon inherits the environment of the shell that launched it, and
/// its children inherit that in turn. In a repository host, the process environment
/// belongs to whichever worktree happened to launch the host. So a session carries its
/// own client's environment, installed as an `AsyncLocal` exactly like the process
/// registry and the log sink, and every child it spawns starts from that.
///
/// The one consumer that cannot be scoped is in-process MSBuild evaluation, which reads
/// the process environment directly. MSBuild turns environment variables into
/// properties, so a session whose MSBuild-relevant variables differ from the host's
/// cannot be evaluated faithfully in this process. `msbuildMismatches` names those
/// differences, and the host refuses such a session.
module FsHotWatch.SessionScope

open System
open System.Collections
open System.Threading
open System.Threading.Tasks

/// Run `work` on a fresh ExecutionContext that carries none of the caller's
/// AsyncLocals, and let none of the AsyncLocals it sets reach the caller.
///
/// A session is constructed this way, so the registry and sinks it installs are its
/// own. An `AsyncLocal` set in a synchronous method would otherwise remain set on the
/// constructing thread and be captured by the next session built on it.
///
/// A dedicated thread, not a pool task: waiting on a task may run it inline on the
/// waiting thread, in the caller's context.
let isolated (work: unit -> 'T) : 'T =
    let outcome = TaskCompletionSource<'T>()

    let thread =
        Thread(fun () ->
            try
                outcome.SetResult(work ())
            with ex ->
                outcome.SetException ex)

    if ExecutionContext.IsFlowSuppressed() then
        thread.Start()
    else
        use _ = ExecutionContext.SuppressFlow()
        thread.Start()

    outcome.Task.GetAwaiter().GetResult()

/// How a variable name is matched. MSBuild reads the environment as properties, and
/// property names are case-insensitive, so both cases compare case-insensitively.
[<RequireQualifiedAccess>]
type EnvironmentKey =
    | Exact of name: string
    | Prefix of prefix: string

/// The variables whose value can change what in-process MSBuild evaluation produces.
///
/// - `DOTNET_*`, `MSBuild*` and `NUGET_*` select the SDK, MSBuild's own behaviour and
///   the package store.
/// - The rest are properties the SDK reads straight from the environment:
///   configuration and platform selection, and the CI markers that turn on
///   deterministic, CI-specific builds.
///
/// `PATH` is deliberately absent. It does not reach an evaluation that is already
/// loaded. It does reach children, and they get the session's own `PATH`.
let msbuildRelevant: EnvironmentKey list =
    [ EnvironmentKey.Prefix "DOTNET_"
      EnvironmentKey.Prefix "MSBuild"
      EnvironmentKey.Prefix "NUGET_"
      EnvironmentKey.Exact "Configuration"
      EnvironmentKey.Exact "Platform"
      EnvironmentKey.Exact "TargetFramework"
      EnvironmentKey.Exact "RuntimeIdentifier"
      EnvironmentKey.Exact "ContinuousIntegrationBuild"
      EnvironmentKey.Exact "CI"
      EnvironmentKey.Exact "TF_BUILD"
      EnvironmentKey.Exact "GITHUB_ACTIONS" ]

/// True when a variable named `name` can change in-process MSBuild evaluation.
let isMsbuildRelevant (name: string) : bool =
    msbuildRelevant
    |> List.exists (fun key ->
        match key with
        | EnvironmentKey.Exact exact -> String.Equals(name, exact, StringComparison.OrdinalIgnoreCase)
        | EnvironmentKey.Prefix prefix -> name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))

/// One MSBuild-relevant variable whose host and client values differ. `None` means
/// that side has it unset.
type EnvironmentMismatch =
    { Name: string
      Host: string option
      Client: string option }

/// A client's environment and the worktree root it was captured in.
type SessionEnvironment =
    private
        { Root: string
          Variables: Map<string, string> }

module SessionEnvironment =
    let create (root: string) (variables: Map<string, string>) : SessionEnvironment =
        { Root = root; Variables = variables }

    /// This process's own environment, as captured in `root`.
    let ofProcess (root: string) : SessionEnvironment =
        let variables =
            Environment.GetEnvironmentVariables()
            |> Seq.cast<DictionaryEntry>
            |> Seq.map (fun e -> unbox<string> e.Key, unbox<string> e.Value)
            |> Map.ofSeq

        create root variables

    let root (env: SessionEnvironment) = env.Root
    let variables (env: SessionEnvironment) = env.Variables

    /// Each value with its own worktree root replaced by a placeholder, so that a
    /// value naming "my worktree" compares equal across worktrees.
    let private rebased (env: SessionEnvironment) =
        env.Variables
        |> Map.filter (fun name _ -> isMsbuildRelevant name)
        |> Map.map (fun _ value -> value.Replace(env.Root, "<worktree>", StringComparison.Ordinal))

    /// The MSBuild-relevant variables whose values differ between `host` and
    /// `client`, after each side's worktree root is abstracted away, ordered by name.
    let msbuildMismatches (host: SessionEnvironment) (client: SessionEnvironment) : EnvironmentMismatch list =
        let h = rebased host
        let c = rebased client

        Set.union (h |> Map.keys |> Set.ofSeq) (c |> Map.keys |> Set.ofSeq)
        |> Set.toList
        |> List.choose (fun name ->
            let hv = Map.tryFind name h
            let cv = Map.tryFind name c

            if hv = cv then
                None
            else
                Some { Name = name; Host = hv; Client = cv })

    let describeMismatch (mismatch: EnvironmentMismatch) : string =
        let show = Option.defaultValue "unset"
        $"%s{mismatch.Name} is %s{show mismatch.Host} in the host but %s{show mismatch.Client} here"

    let private current' = AsyncLocal<SessionEnvironment option>()

    /// The session environment in scope, if any.
    let current () : SessionEnvironment option = current'.Value

    /// Put `env` in scope until the result is disposed, when the previous one returns.
    let install (env: SessionEnvironment) : IDisposable =
        let prior = current'.Value
        current'.Value <- Some env

        { new IDisposable with
            member _.Dispose() = current'.Value <- prior }
