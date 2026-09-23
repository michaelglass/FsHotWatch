/// A repository host's checker partitions: sessions whose checker configuration is equal
/// check through one checker, so they hold one copy of the framework imports and of
/// `TcGlobals`, which FCS keys by the framework references rather than the project.
module FsHotWatch.Tests.CheckerPartitionsTests

// TransparentCompiler.CacheSizes is marked experimental; it is the checker's configuration.
#nowarn "57"

open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch

let private sizes (factor: int) =
    TransparentCompiler.CacheSizes.Create factor

/// Partitions over stand-in checkers, and how many were built.
let private counting () =
    let built = ref 0

    let partitions =
        CheckerPartitions.Partitions(fun (_: TransparentCompiler.CacheSizes) ->
            Interlocked.Increment &built.contents |> ignore
            obj ())

    partitions, built

[<Fact(Timeout = 5000)>]
let ``sessions asking for the same configuration share one checker`` () =
    let partitions, built = counting ()
    let first = partitions.For(sizes 100)
    let second = partitions.For(sizes 100)
    test <@ obj.ReferenceEquals(first, second) @>
    test <@ built.Value = 1 @>

[<Fact(Timeout = 5000)>]
let ``a different configuration is a different checker`` () =
    let partitions, built = counting ()
    let bounded = partitions.For(sizes 40)
    let default' = partitions.For(sizes 100)
    test <@ not (obj.ReferenceEquals(bounded, default')) @>
    test <@ built.Value = 2 @>
    test <@ partitions.Count = 2 @>

[<Fact(Timeout = 10000)>]
let ``sessions attaching at once still share one checker`` () =
    let partitions, built = counting ()

    let checkers =
        Array.init 32 (fun _ -> Task.Run(fun () -> partitions.For(sizes 100)))
        |> Task.WhenAll
        |> fun all -> all.Result

    test <@ checkers |> Array.forall (fun c -> obj.ReferenceEquals(c, checkers[0])) @>
    test <@ built.Value = 1 @>
