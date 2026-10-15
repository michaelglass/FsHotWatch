/// How many project bootstraps a checker has computed.
///
/// A bootstrap (the TransparentCompiler's `BootstrapInfoStatic`) is where FCS imports a
/// project's referenced assemblies. Two sessions whose projects share a bootstrap key
/// share one import of every assembly; each bootstrap computed again is another import
/// graph. So the count of bootstraps started, against the count of distinct projects
/// they were started for, says whether a second session cost a second import graph.
///
/// FCS keeps its caches internal. The counter reads them by name, as FCS 43.12 spells
/// them, and when a name is missing it logs that once and counts nothing: a measurement
/// must never take the daemon down.
module FsHotWatch.FcsBootstraps

open System
open System.Collections.Concurrent
open System.Reflection
open System.Runtime.CompilerServices
open System.Threading
open FSharp.Compiler.CodeAnalysis
open Microsoft.FSharp.Reflection

/// A checker's bootstraps so far.
type Counts =
    {
        /// Bootstraps computed, as opposed to served from the cache.
        Started: int64
        /// Distinct projects (by the label FCS gives a project) they were computed for.
        Projects: int
    }

type private Counter() =
    let mutable started = 0L
    let projects = ConcurrentDictionary<string, unit>()

    member _.Record(label: string) =
        Interlocked.Increment &started |> ignore
        projects.TryAdd(label, ()) |> ignore

    member _.Counts =
        { Started = Interlocked.Read &started
          Projects = projects.Count }

let private instance =
    BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

/// The member `name` of `target`: a property or a field.
let private read (name: string) (target: obj) : obj =
    let t = target.GetType()

    match t.GetProperty(name, instance) with
    | null ->
        match t.GetField(name, instance) with
        | null -> failwith $"%s{t.FullName} has no member %s{name}"
        | field -> field.GetValue target
    | property -> property.GetValue target

/// Call `onStarted label` whenever `checker` starts computing a bootstrap.
let private subscribe (checker: FSharpChecker) (onStarted: string -> unit) =
    let event =
        checker
        |> read "backgroundCompiler"
        |> read "Caches"
        |> read "BootstrapInfoStatic"
        |> read "Event"

    // An event is a tuple, (JobEvent, (label, key, version)): a reference type, so the
    // event is an observable of objects.
    (event :?> IObservable<obj>)
        .Subscribe(fun e ->
            let fields = FSharpValue.GetTupleFields e

            if fields[0].ToString() = "Started" then
                onStarted ((FSharpValue.GetTupleFields fields[1])[0] :?> string))
    |> ignore

let private counters = ConditionalWeakTable<FSharpChecker, Counter>()

let private attachTo (checker: FSharpChecker) =
    let counter = Counter()

    try
        subscribe checker (fun label ->
            counter.Record label
            Logging.debug "fcs" $"bootstrap started: %s{label}")
    with ex ->
        Logging.warn "fcs" $"bootstrap counter unavailable, counting nothing: %s{ex.Message}"

    counter

/// Start counting `checker`'s bootstraps. Idempotent; call it before the first check.
let attach (checker: FSharpChecker) : unit =
    if not (isNull (box checker)) then
        counters.GetValue(checker, attachTo) |> ignore

/// `checker`'s bootstraps since `attach`; zero for a checker never attached.
let countsOf (checker: FSharpChecker) : Counts =
    if isNull (box checker) then
        { Started = 0L; Projects = 0 }
    else
        match counters.TryGetValue checker with
        | true, counter -> counter.Counts
        | false, _ -> { Started = 0L; Projects = 0 }
