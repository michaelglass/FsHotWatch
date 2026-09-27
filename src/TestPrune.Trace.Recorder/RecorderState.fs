namespace TestPrune.Trace.Recorder

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading

/// Per-thread hot-path cache. One instance per thread, registered so the exit dump
/// can sum its counters.
[<Sealed; AllowNullLiteral>]
type internal ThreadState() =
    /// The static-init scope of the type initializer running on this thread, or null.
    member val StaticScope: Scope = null with get, set
    /// The static-init scopes of the initializers the running one interrupted, innermost first.
    member val Interrupted = Stack<Scope>()
    member val LastCtx: obj = null with get, set
    member val LastScope: Scope = null with get, set
    member val LastOwner: obj = null with get, set
    /// test, class, collection, assembly, override, staticInit, ambient, overflow
    member val Counts: int64[] = Array.zeroCreate 8

/// Every `ThreadState` ever created, so counters can be summed process-wide.
module internal ThreadRegistry =
    let all = ConcurrentBag<ThreadState>()

/// The calling thread's `ThreadState`.
[<AbstractClass; Sealed>]
type internal Threads =
    [<ThreadStatic; DefaultValue>]
    static val mutable private current: ThreadState

    static member Get() =
        let t = Threads.current

        if isNull t then
            let fresh = ThreadState()
            Threads.current <- fresh
            ThreadRegistry.all.Add fresh
            fresh
        else
            t

/// A traced process's recorder: resolves each hit to a scope and marks it there.
/// Precedence: static init (the running type initializer's own scope), then an explicit scope, then the context source's
/// test/class/collection/assembly, then ambient (or the parent scope in a child process).
[<Sealed; AllowNullLiteral>]
type RecorderState(idCount: int, source: IContextSource option, parentScope: string) =
    let byKey = ConcurrentDictionary<string, Scope>()
    let byCtx = ConditionalWeakTable<obj, Scope>()

    let newScope key =
        byKey.GetOrAdd(key, (fun k -> Scope(k, idCount)))

    let hasParent = not (String.IsNullOrEmpty parentScope)
    let ambient = newScope (if hasParent then parentScope else "A:ambient")
    /// A parent scope is an explicit scope set by the host, so it counts as override.
    let ambientBucket = if hasParent then 4 else 6
    let overrideScope = AsyncLocal<Scope>()
    let mutable anyOverride = false

    let bucketOf (s: Scope) =
        match s.Key.[0] with
        | 'T' -> 0
        | 'C' -> 1
        | 'L' -> 2
        | _ -> 3

    let resolve (src: IContextSource) (ctx: obj) =
        match byCtx.TryGetValue ctx with
        | true, s -> s
        | _ ->
            let info = src.Describe ctx
            let s = newScope info.Key

            if isNull s.TestClass then
                s.TestClass <- info.TestClass
                s.TestMethod <- info.TestMethod
                s.TestDisplay <- info.TestDisplay
                s.Parents <- info.Parents

            byCtx.AddOrUpdate(ctx, s)
            s

    let unattributed = if hasParent then ambient else null

    /// Number of probe ids; any id outside `[0, IdCount)` is counted as overflow.
    member _.IdCount = idCount

    /// The scope a traced parent passed down, or null.
    member _.ParentScope = parentScope

    /// Absolute repository root; file inputs outside it are not recorded. Null records none.
    member val RepoRoot: string = null with get, set

    /// Where a file read or a child process is noted: static init, then the current
    /// scope, then ambient. Never null.
    member this.NoteScope() : Scope =
        match Threads.Get().StaticScope with
        | null ->
            match this.CurrentScope() with
            | null -> ambient
            | s -> s
        | s -> s

    /// Every scope created so far.
    member _.Scopes: seq<Scope> = byKey.Values :> seq<Scope>

    /// The scope a hit would go to now (ignoring static init); null when unattributed.
    member _.CurrentScope() : Scope =
        let o = if anyOverride then overrideScope.Value else null

        if not (isNull o) then
            o
        else
            match source with
            | Some src ->
                match src.Current() with
                | null -> unattributed
                | ctx -> resolve src ctx
            | None -> unattributed

    /// Overrides the scope of the current async flow with `key`.
    member _.EnterScope(key: string) =
        anyOverride <- true
        overrideScope.Value <- newScope key

    /// Ends the override set by `EnterScope`.
    member _.ExitScope() = overrideScope.Value <- null

    /// Entered at the start of the static constructor of `typeName` on this thread; its
    /// scope is `S:<typeName>`, or `S:static-init` when the type is unknown. An initializer
    /// that runs inside another is linked from it: the outer one's work depends on it.
    member _.EnterStatic(typeName: string) =
        let t = Threads.Get()

        let s =
            newScope (
                if String.IsNullOrEmpty typeName then
                    "S:static-init"
                else
                    "S:" + typeName
            )

        match t.StaticScope with
        | null -> ()
        | outer -> outer.Links.TryAdd(s.Key, 0uy) |> ignore

        t.Interrupted.Push t.StaticScope
        t.StaticScope <- s

    /// Leaves a static constructor on this thread.
    member _.ExitStatic() =
        let t = Threads.Get()

        t.StaticScope <-
            match t.Interrupted.TryPop() with
            | true, outer -> outer
            | _ -> null

    /// Records probe `id`. Allocation-free once the thread's state and the context's scope exist.
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member this.Hit(id: int) =
        let t = Threads.Get()

        if uint32 id >= uint32 idCount then
            t.Counts.[7] <- t.Counts.[7] + 1L
        elif not (isNull t.StaticScope) then
            t.StaticScope.Set id
            t.Counts.[5] <- t.Counts.[5] + 1L
        else
            let o = if anyOverride then overrideScope.Value else null

            if not (isNull o) then
                o.Set id
                t.Counts.[4] <- t.Counts.[4] + 1L
            else
                let ctx =
                    match source with
                    | Some src -> src.Current()
                    | None -> null

                if isNull ctx then
                    ambient.Set id
                    t.Counts.[ambientBucket] <- t.Counts.[ambientBucket] + 1L
                else
                    let s =
                        if
                            Object.ReferenceEquals(ctx, t.LastCtx)
                            && Object.ReferenceEquals(this, t.LastOwner)
                        then
                            t.LastScope
                        else
                            let r = resolve source.Value ctx
                            t.LastCtx <- ctx
                            t.LastScope <- r
                            t.LastOwner <- (this :> obj)
                            r

                    s.Set id
                    let b = bucketOf s
                    t.Counts.[b] <- t.Counts.[b] + 1L

    /// Sum of every thread's counters, in the order test, class, collection, assembly,
    /// override, staticInit, ambient, overflow. Counters are process-wide, not per recorder.
    member _.Counters() : int64[] =
        let sum = Array.zeroCreate 8

        for t in ThreadRegistry.all.ToArray() do
            for i in 0..7 do
                sum.[i] <- sum.[i] + t.Counts.[i]

        sum
