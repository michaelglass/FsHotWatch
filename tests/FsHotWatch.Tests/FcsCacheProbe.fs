/// Test-side probes into the TransparentCompiler's caches, which FCS keeps internal.
///
/// Each probe reads FCS's members by name and fails loudly when one is missing, so an
/// FCS that renames them breaks these tests instead of letting them pass vacuously.
/// Names are those of FSharp.Compiler.Service 43.12.401: `FSharpChecker`'s
/// `backgroundCompiler`, `TransparentCompiler.Caches`, `CompilerCaches`' per-cache
/// properties, and `AsyncMemoize`'s `Event`.
module FsHotWatch.Tests.FcsCacheProbe

open System
open System.IO
open System.Reflection
open FSharp.Compiler.CodeAnalysis
open Microsoft.FSharp.Reflection

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

/// The TransparentCompiler cache `cache` (`TcIntermediate`, `BootstrapInfo`, ...).
let private memoOf (checker: FSharpChecker) (cache: string) =
    checker |> read "backgroundCompiler" |> read "Caches" |> read cache

/// The label FCS gives a project: its file name under its directory's name.
let projectLabel (projectFileName: string) =
    let directory = Path.GetFileName(Path.GetDirectoryName projectFileName)
    $"%s{directory}/%s{Path.GetFileName projectFileName}"

type private Subscribe =
    static member To<'T>(event: IEvent<Handler<'T>, 'T>, handler: obj -> unit) : unit =
        event.Add(fun args -> handler (box args))

/// Call `handler jobEvent label` for every job event of the cache `cache`: `jobEvent`
/// is FCS's `JobEvent` case name (`Started`, `Finished`, `Weakened`, `Collected`, ...),
/// `label` the cache key's label. A per-file key is labelled
/// `<file path> (<project label>)`, a per-project key `<project label>`.
let onJobEvent (checker: FSharpChecker) (cache: string) (handler: string -> string -> unit) : unit =
    let event = memoOf checker cache |> read "Event"

    let args =
        event.GetType().GetInterfaces()
        |> Array.pick (fun i ->
            if
                i.IsGenericType
                && i.GetGenericTypeDefinition() = typedefof<IEvent<Handler<obj>, obj>>
            then
                Some(i.GetGenericArguments()[1])
            else
                None)

    let subscribe =
        typeof<Subscribe>
            .GetMethod("To", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
            .MakeGenericMethod(args)

    let onEvent (e: obj) =
        let fields = FSharpValue.GetTupleFields e
        let label = (FSharpValue.GetTupleFields fields[1])[0] :?> string
        handler (string fields[0]) label

    subscribe.Invoke(null, [| event; box onEvent |]) |> ignore
