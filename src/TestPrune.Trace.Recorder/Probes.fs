namespace TestPrune.Trace.Recorder

open System
open System.IO

/// The process-wide recorder and its bootstrap from the environment.
module internal Runtime =
    /// Build the recorder the environment asks for, registering its exit dump with
    /// `onExit`. Null when `TESTPRUNE_TRACE_OUT` is unset or empty.
    let install (getEnv: string -> string) (onExit: EventHandler -> unit) : RecorderState =
        let outDir = getEnv Contract.OutEnv

        if String.IsNullOrEmpty outDir then
            null
        else
            let ids =
                match Int32.TryParse(getEnv Contract.IdsEnv) with
                | true, n when n > 0 -> n
                | _ -> 1 <<< 20

            let s =
                RecorderState(ids, XunitContextSource.tryCreate (), getEnv Contract.ParentScopeEnv)

            s.RepoRoot <- getEnv Contract.RepoRootEnv

            onExit (
                EventHandler(fun _ _ ->
                    Directory.CreateDirectory outDir |> ignore
                    DumpWriter.write (DumpWriter.pathIn outDir) s)
            )

            s

    /// The process's recorder, or null when this process is not being traced. Built
    /// once, on first use; mutable only so tests can install their own.
    let mutable state: RecorderState =
        install Environment.GetEnvironmentVariable AppDomain.CurrentDomain.ProcessExit.AddHandler

/// Static entry points the weaver calls. Each is a null check when the process is not traced.
[<AbstractClass; Sealed>]
type Probes =
    /// Records probe `id` against the current scope.
    static member Hit(id: int) : unit =
        let s = Runtime.state

        if not (isNull s) then
            s.Hit id

    /// Records probe `id` when `value` is not null.
    static member HitIfNotNull(value: obj, id: int) : unit =
        if not (isNull value) then
            Probes.Hit id

    /// Records probe `id` when `value` is true.
    static member HitIfTrue(value: bool, id: int) : unit =
        if value then
            Probes.Hit id

    /// Records probe `baseId + tag`.
    static member HitTag(tag: int, baseId: int) : unit = Probes.Hit(baseId + tag)

    /// Marks entry into a static constructor.
    static member EnterStatic() : unit =
        let s = Runtime.state

        if not (isNull s) then
            s.EnterStatic()

    /// Marks exit from a static constructor.
    static member ExitStatic() : unit =
        let s = Runtime.state

        if not (isNull s) then
            s.ExitStatic()

/// The consumer-facing scope contract. Inert when the recorder is inactive.
[<AbstractClass; Sealed>]
type Scopes =
    /// Overrides the scope of the current async flow with `key`.
    static member Enter(key: string) : unit =
        let s = Runtime.state

        if not (isNull s) then
            s.EnterScope key

    /// Ends the override set by `Enter`.
    static member Exit() : unit =
        let s = Runtime.state

        if not (isNull s) then
            s.ExitScope()

    /// The current scope key, or null when the recorder is inactive.
    static member CurrentKey() : string =
        let s = Runtime.state

        if isNull s then
            null
        else
            match s.CurrentScope() with
            | null -> null
            | scope -> scope.Key

    /// Makes the current scope inherit everything recorded under `scopeKey`.
    static member LinkCurrentTo(scopeKey: string) : unit =
        let s = Runtime.state

        if not (isNull s) then
            match s.CurrentScope() with
            | null -> ()
            | scope -> scope.Links.TryAdd(scopeKey, 0uy) |> ignore
