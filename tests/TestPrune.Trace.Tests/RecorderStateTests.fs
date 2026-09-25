/// Counters are summed over every thread in the process, so every test that asserts on them, or that
/// swaps the process-wide recorder, runs in this collection, outside parallelization.
namespace TestPrune.Trace.Tests

open Xunit

[<CollectionDefinition("recorder-counters", DisableParallelization = true)>]
type RecorderCountersCollection() = class end

[<Collection("recorder-counters")>]
module RecorderStateTests =

    open System.Threading
    open System.Threading.Tasks
    open Swensen.Unquote
    open TestPrune.Trace.Recorder

    /// A context source whose "current test" is an AsyncLocal the test sets, exactly
    /// like xUnit's TestContext.Current.
    type FakeSource() =
        let current = AsyncLocal<obj>()
        member _.Set(ctx: obj) = current.Value <- ctx

        interface IContextSource with
            member _.Current() = current.Value

            member _.Describe(ctx) =
                let name = string ctx

                { Key = "T:" + name
                  TestClass = "Ns.C"
                  TestMethod = name
                  TestDisplay = "Ns.C." + name
                  Parents = [| "C:Ns.C"; "A:assembly" |] }

    let private idsOf (state: RecorderState) key =
        state.Scopes
        |> Seq.find (fun s -> s.Key = key)
        |> (fun s -> s.Ids() |> Set.ofArray)

    [<Fact>]
    let ``hits go to the current test, and parallel tests never contaminate each other`` () =
        let src = FakeSource()
        let state = RecorderState(1000, Some(src :> IContextSource), null)

        let work (name: string) (ids: int list) =
            Task.Run(fun () ->
                src.Set(box name)

                for _ in 1..200 do
                    for id in ids do
                        state.Hit id)

        Task.WaitAll([| work "a" [ 1; 2; 3 ]; work "b" [ 3; 4 ]; work "c" [ 999 ] |])
        test <@ idsOf state "T:a" = set [ 1; 2; 3 ] @>
        test <@ idsOf state "T:b" = set [ 3; 4 ] @>
        test <@ idsOf state "T:c" = set [ 999 ] @>

    [<Fact>]
    let ``attribution flows through awaits and Task.Run`` () =
        let src = FakeSource()
        let state = RecorderState(64, Some(src :> IContextSource), null)

        let t =
            task {
                src.Set(box "flow")
                do! Task.Yield()
                do! Task.Run(fun () -> state.Hit 7)
                state.Hit 8
            }

        t.Wait()
        test <@ idsOf state "T:flow" = set [ 7; 8 ] @>

    [<Fact>]
    let ``static init wins over the test, an explicit scope wins over the test`` () =
        let src = FakeSource()
        let state = RecorderState(64, Some(src :> IContextSource), null)
        src.Set(box "t")
        state.EnterStatic()
        state.Hit 1
        state.ExitStatic()
        state.EnterScope "P:pool"
        state.Hit 2
        state.ExitScope()
        state.Hit 3
        test <@ idsOf state "S:static-init" = set [ 1 ] @>
        test <@ idsOf state "P:pool" = set [ 2 ] @>
        test <@ idsOf state "T:t" = set [ 3 ] @>

    [<Fact>]
    let ``no context means ambient, or the parent scope in a child process`` () =
        let lone = RecorderState(64, None, null)
        lone.Hit 5
        test <@ idsOf lone "A:ambient" = set [ 5 ] @>
        let child = RecorderState(64, None, "T:parent")
        child.Hit 6
        test <@ idsOf child "T:parent" = set [ 6 ] @>

    [<Fact>]
    let ``a source with no current context falls back to ambient, or to the parent scope`` () =
        let src = FakeSource() :> IContextSource
        let lone = RecorderState(64, Some src, null)
        lone.Hit 5
        test <@ idsOf lone "A:ambient" = set [ 5 ] @>
        let child = RecorderState(64, Some src, "T:parent")
        child.Hit 6
        test <@ idsOf child "T:parent" = set [ 6 ] @>

    [<Fact>]
    let ``an id outside the manifest is counted as overflow and never indexes out of range`` () =
        let state = RecorderState(10, None, null)
        let before = state.Counters()
        state.Hit 10
        state.Hit(-1)
        test <@ state.Counters().[7] - before.[7] = 2L @>
        test <@ state.Scopes |> Seq.forall (fun s -> s.IsEmpty) @>

    [<Fact>]
    let ``counters split hits by bucket`` () =
        let src = FakeSource()
        let state = RecorderState(64, Some(src :> IContextSource), null)
        let before = state.Counters()
        state.Hit 1 // ambient
        src.Set(box "t")
        state.Hit 1 // test
        state.EnterStatic()
        state.Hit 1 // static
        state.ExitStatic()
        state.EnterScope "P:pool"
        state.Hit 1 // override
        state.ExitScope()
        let c = Array.map2 (-) (state.Counters()) before
        test <@ (c.[0], c.[4], c.[5], c.[6]) = (1L, 1L, 1L, 1L) @>

    /// Describes a context string "<prefix>:<name>" as a scope of that kind.
    type KindedSource() =
        let current = AsyncLocal<obj>()
        member _.Set(ctx: obj) = current.Value <- ctx

        interface IContextSource with
            member _.Current() = current.Value

            member _.Describe(ctx) =
                { Key = string ctx
                  TestClass = null
                  TestMethod = null
                  TestDisplay = null
                  Parents = [| "A:assembly" |] }

    [<Fact>]
    let ``class, collection and assembly contexts count in their own buckets`` () =
        let src = KindedSource()
        let state = RecorderState(64, Some(src :> IContextSource), "T:parent")
        let before = state.Counters()

        for key in [ "C:Ns.C"; "L:coll"; "A:assembly" ] do
            src.Set(box key)
            state.Hit 2

        src.Set null
        state.Hit 3 // the parent scope counts as override
        let c = Array.map2 (-) (state.Counters()) before
        test <@ c.[1..4] = [| 1L; 1L; 1L; 1L |] @>
        test <@ idsOf state "C:Ns.C" = set [ 2 ] @>
        test <@ idsOf state "T:parent" = set [ 3 ] @>

    [<Fact>]
    let ``a context is described once, and a second recorder never reuses the first one's scope`` () =
        let mutable described = 0
        let ctx = obj ()

        let src =
            { new IContextSource with
                member _.Current() = ctx

                member _.Describe(_) =
                    described <- described + 1

                    { Key = "T:same"
                      TestClass = "Ns.C"
                      TestMethod = "m"
                      TestDisplay = "Ns.C.m"
                      Parents = [| "C:Ns.C" |] } }

        let first = RecorderState(64, Some src, null)
        first.Hit 1
        first.Hit 2
        let second = RecorderState(64, Some src, null)
        second.Hit 3
        // The per-thread cache is keyed on the recorder too, and the weak table survives a cache miss.
        first.Hit 4
        test <@ described = 2 @>
        test <@ idsOf first "T:same" = set [ 1; 2; 4 ] @>
        test <@ idsOf second "T:same" = set [ 3 ] @>
        let s = first.Scopes |> Seq.find (fun s -> s.Key = "T:same")
        test <@ (s.TestClass, s.TestMethod, s.TestDisplay, s.Parents) = ("Ns.C", "m", "Ns.C.m", [| "C:Ns.C" |]) @>

    [<Fact>]
    let ``two contexts describing the same key share a scope and keep the first identity`` () =
        let src = FakeSource()
        let state = RecorderState(64, Some(src :> IContextSource), null)
        src.Set(box "t")
        state.Hit 1
        src.Set(box (System.String('t', 1))) // another context object with the same key
        state.Hit 2
        test <@ idsOf state "T:t" = set [ 1; 2 ] @>

    [<Fact>]
    let ``current scope follows the override, the test, or the parent scope`` () =
        let src = FakeSource()
        let state = RecorderState(64, Some(src :> IContextSource), null)
        test <@ isNull (state.CurrentScope()) @>
        src.Set(box "t")
        test <@ state.CurrentScope().Key = "T:t" @>
        state.EnterScope "P:pool"
        test <@ state.CurrentScope().Key = "P:pool" @>
        state.ExitScope()
        test <@ state.CurrentScope().Key = "T:t" @>
        test <@ isNull (RecorderState(64, None, null).CurrentScope()) @>
        test <@ RecorderState(64, None, "T:p").CurrentScope().Key = "T:p" @>
        test <@ RecorderState(64, Some(FakeSource() :> IContextSource), "T:p").CurrentScope().Key = "T:p" @>

    [<Fact>]
    let ``the recorder exposes its id count and parent scope`` () =
        let state = RecorderState(42, None, "T:p")
        test <@ (state.IdCount, state.ParentScope) = (42, "T:p") @>

    [<Fact>]
    let ``a scope lists its ids in ascending order across words`` () =
        let s = Scope("T:x", 200)
        s.Set 130
        s.Set 0
        s.Set 63
        s.Set 64
        s.Set 63
        test <@ s.Ids() = [| 0; 63; 64; 130 |] @>
        test <@ s.Key = "T:x" && not s.IsEmpty @>

    [<Fact>]
    let ``a scope is empty only with no ids, links, inputs or children`` () =
        test <@ Scope("A", 8).IsEmpty @>
        let linked = Scope("A", 8)
        linked.Links.TryAdd("P:pool", 0uy) |> ignore
        let read = Scope("A", 8)
        read.Inputs.TryAdd(struct ("read", "/x"), 0uy) |> ignore
        let parent = Scope("A", 8)
        parent.Children.Enqueue(struct (1, "dotnet", true))
        test <@ not linked.IsEmpty && not read.IsEmpty && not parent.IsEmpty @>

    [<Fact>]
    let ``a hit allocates nothing once its thread and context are known`` () =
        let src = FakeSource()
        let state = RecorderState(64, Some(src :> IContextSource), null)
        src.Set(box "t")
        state.Hit 1 // first hit on this thread and context: registers and resolves
        let before = System.GC.GetAllocatedBytesForCurrentThread()

        for i in 0..9999 do
            state.Hit(i % 64) // test scope
            state.Hit 64 // overflow
            state.EnterStatic()
            state.Hit 2 // static init
            state.ExitStatic()

        let allocated = System.GC.GetAllocatedBytesForCurrentThread() - before
        test <@ allocated = 0L @>
