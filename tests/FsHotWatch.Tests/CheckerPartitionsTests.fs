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

// AUTOMATION-1656: FCS shares one `TcGlobals` across every project on the same
// framework references, built from the options of whichever project it bootstrapped
// first. Projects whose `TcGlobals` options differ need checkers of their own.

[<Fact(Timeout = 5000)>]
let ``globalsOptions keeps only the options FCS reads into TcGlobals, sorted`` () =
    let options =
        [| "--define:DEBUG"
           "--realsig-"
           "--nowarn:25"
           "--reflectionfree"
           "--warnon:1182"
           "--warnaserror+"
           "--quotations-debug+"
           "--pathmap:/src=/_"
           "-r:/lib/A.dll"
           "--nodebugdata"
           "--compiling-fslib"
           "--reflectionfree" |]

    test
        <@
            CheckerPartitions.globalsOptions options = [ "--compiling-fslib"
                                                         "--nodebugdata"
                                                         "--pathmap:/src=/_"
                                                         "--quotations-debug+"
                                                         "--realsig-"
                                                         "--reflectionfree" ]
        @>

[<Fact(Timeout = 5000)>]
let ``projects with no globals options have an empty key`` () =
    test <@ List.isEmpty (CheckerPartitions.globalsOptions [| "--define:DEBUG"; "--nowarn:25"; "-r:/lib/A.dll" |]) @>

/// A family over stand-in checkers, and how many siblings were built.
let private family () =
    let built = ref 0
    let first = obj ()

    let family =
        CheckerPartitions.Family(
            first,
            fun () ->
                Interlocked.Increment &built.contents |> ignore
                obj ()
        )

    family, first, built

[<Fact(Timeout = 5000)>]
let ``the first globals key asked for checks through the family's own checker`` () =
    let family, first, built = family ()
    test <@ obj.ReferenceEquals(family.For [| "--reflectionfree" |], first) @>
    test <@ built.Value = 0 @>

[<Fact(Timeout = 5000)>]
let ``projects whose globals options differ check through different checkers`` () =
    let family, first, built = family ()
    let plain = family.For [| "--define:A" |]
    let reflectionFree = family.For [| "--reflectionfree"; "--define:B" |]
    test <@ obj.ReferenceEquals(plain, first) @>
    test <@ not (obj.ReferenceEquals(reflectionFree, first)) @>
    test <@ built.Value = 1 @>
    test <@ family.Members.Length = 2 @>

[<Fact(Timeout = 5000)>]
let ``projects whose globals options are equal share a checker whatever else differs`` () =
    let family, _, built = family ()
    let a = family.For [| "--reflectionfree"; "--define:A"; "--nowarn:25" |]
    let b = family.For [| "--define:B"; "--reflectionfree"; "--warnaserror+" |]
    test <@ obj.ReferenceEquals(a, b) @>
    test <@ built.Value = 0 @>

[<Fact(Timeout = 5000)>]
let ``a sibling is built once per key and listed in the order it was built`` () =
    let family, first, built = family ()
    let plain = family.For [| "--define:A" |]
    let realsig = family.For [| "--realsig-" |]
    let reflectionFree = family.For [| "--reflectionfree" |]
    test <@ obj.ReferenceEquals(plain, first) @>
    test <@ obj.ReferenceEquals(family.For [| "--realsig-" |], realsig) @>
    test <@ obj.ReferenceEquals(family.For [| "--define:B" |], first) @>
    test <@ built.Value = 2 @>
    test <@ family.Members = [ first; realsig; reflectionFree ] @>

// The module's functions over real checkers: one is never asked to check, so creating
// it costs nothing.

let private noChecker: FSharpChecker = Unchecked.defaultof<FSharpChecker>

[<Fact(Timeout = 15000)>]
let ``a null checker is never adopted and stands for itself`` () =
    CheckerPartitions.adopt noChecker (fun () -> failwith "a null checker builds no sibling")
    test <@ isNull (box (CheckerPartitions.forOptions noChecker [| "--reflectionfree" |])) @>
    test <@ CheckerPartitions.membersOf noChecker |> List.forall (fun c -> isNull (box c)) @>
    test <@ (CheckerPartitions.membersOf noChecker).Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``a checker never adopted checks every project itself`` () =
    let checker = FSharpChecker.Create()
    test <@ obj.ReferenceEquals(CheckerPartitions.forOptions checker [| "--define:A" |], checker) @>
    test <@ obj.ReferenceEquals(CheckerPartitions.forOptions checker [| "--reflectionfree" |], checker) @>
    test <@ CheckerPartitions.membersOf checker = [ checker ] @>

[<Fact(Timeout = 15000)>]
let ``an adopted checker checks other globals options through siblings, and a second adopt changes nothing`` () =
    let checker = FSharpChecker.Create()
    let built = ref 0

    let make () =
        Interlocked.Increment &built.contents |> ignore
        FSharpChecker.Create()

    CheckerPartitions.adopt checker make
    CheckerPartitions.adopt checker (fun () -> failwith "the first adopt's make stays")
    test <@ obj.ReferenceEquals(CheckerPartitions.forOptions checker [| "--define:A" |], checker) @>
    let sibling = CheckerPartitions.forOptions checker [| "--reflectionfree" |]
    test <@ not (obj.ReferenceEquals(sibling, checker)) @>
    test <@ built.Value = 1 @>
    test <@ CheckerPartitions.membersOf checker = [ checker; sibling ] @>
