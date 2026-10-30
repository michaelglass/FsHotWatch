namespace TestPrune.Trace.Recorder

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.CompilerServices

/// The process-wide recorder and its bootstrap from the environment.
module internal Runtime =
    /// The exit handler that writes `s`'s dump into `outDir`. It never throws: an exception
    /// out of a ProcessExit handler kills its host, and the host is the app under test. A
    /// write that fails says so on `err` in one line; a dump it left half-written stays a
    /// `.tmp`, which every reader treats as unfinished.
    let exitDump (outDir: string) (s: RecorderState) (err: TextWriter) : EventHandler =
        EventHandler(fun _ _ ->
            try
                Directory.CreateDirectory outDir |> ignore
                DumpWriter.write (DumpWriter.pathIn outDir) s
            with e ->
                err.WriteLine("testprune-trace: dump write failed: " + e.GetType().FullName + ": " + e.Message))

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

            let handler = exitDump outDir s Console.Error
            // Compiled now, while the module's IL is readable: a coverage collector that
            // instruments this assembly in place on disk can leave its image unreadable by
            // exit, and a handler first compiled then would throw outside its own `try`.
            RuntimeHelpers.PrepareMethod handler.Method.MethodHandle
            onExit handler

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

    /// The CLR name (`Outer+Nested`, generic arity kept) of the type whose static
    /// constructor is on the calling stack, innermost first; null when none is.
    static member internal InitializingType(frames: StackFrame[]) : string =
        frames
        |> Array.tryPick (fun f ->
            match f.GetMethod() with
            | :? ConstructorInfo as c when c.IsStatic ->
                let t = c.DeclaringType
                Some (if t.IsGenericType then t.GetGenericTypeDefinition() else t).FullName
            | _ -> None)
        |> Option.toObj

    /// Marks entry into a static constructor. It runs once per type, so naming the type
    /// from the stack costs one stack walk per initialized type.
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member EnterStatic() : unit =
        let s = Runtime.state

        if not (isNull s) then
            s.EnterStatic(Probes.InitializingType(StackTrace(1, false).GetFrames()))

    /// Marks exit from a static constructor.
    static member ExitStatic() : unit =
        let s = Runtime.state

        if not (isNull s) then
            s.ExitStatic()
