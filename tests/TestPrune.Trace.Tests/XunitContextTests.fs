module TestPrune.Trace.Tests.XunitContextTests

open System
open Xunit
open Swensen.Unquote
open TestPrune.Trace.Recorder

[<Fact>]
let ``the xUnit source names the running test and its parents`` () =
    let src = (XunitContextSource.tryCreate ()).Value
    let info = src.Describe(src.Current())
    test <@ info.Key.StartsWith "T:" @>
    test <@ info.TestMethod = "the xUnit source names the running test and its parents" @>
    test <@ info.TestClass = "TestPrune.Trace.Tests.XunitContextTests" @>
    test <@ info.TestDisplay.EndsWith "the xUnit source names the running test and its parents" @>
    test <@ info.Parents |> Array.contains ("C:" + info.TestClass) @>
    test <@ info.Parents |> Array.last = "A:assembly" @>

[<Fact>]
let ``no source binds when the context type or its Current property is missing`` () =
    test <@ XunitContextSource.tryCreateFrom "No.Such.Type, no.such.assembly" = None @>
    test <@ XunitContextSource.tryCreateFrom "System.Object" = None @>

// Stand-ins for xUnit's context objects: only the property names matter.
type FakeTest(uid: string, display: string) =
    member _.UniqueID = uid
    member _.TestDisplayName = display

type FakeClass(name: string) =
    member _.TestClassName = name

type FakeMethod(name: string) =
    member _.MethodName = name

/// xUnit's concrete types are internal; the recorder must find properties through interfaces.
type ICollectionView =
    abstract TestCollectionDisplayName: string

type FakeCollection(name: string) =
    interface ICollectionView with
        member _.TestCollectionDisplayName = name

type FakeContext(test: obj, cls: obj, meth: obj, coll: obj) =
    member _.Test = test
    member _.TestClass = cls
    member _.TestMethod = meth
    member _.TestCollection = coll

let private describe (ctx: obj) =
    let src = XunitContextSource.ofGetter (Func<obj>(fun () -> ctx))
    test <@ Object.ReferenceEquals(src.Current(), ctx) @>
    src.Describe ctx

[<Fact>]
let ``a test context is keyed by the test's unique id under its class, collection and assembly`` () =
    let info =
        describe (FakeContext(FakeTest("u1", "Ns.C.m(1)"), FakeClass "Ns.C", FakeMethod "m", FakeCollection "coll"))

    test
        <@
            info = { Key = "T:u1"
                     TestClass = "Ns.C"
                     TestMethod = "m"
                     TestDisplay = "Ns.C.m(1)"
                     Parents = [| "C:Ns.C"; "L:coll"; "A:assembly" |] }
        @>

[<Fact>]
let ``a class fixture context is keyed by the class, a collection fixture by the collection`` () =
    let cls = describe (FakeContext(null, FakeClass "Ns.C", null, null))

    test
        <@
            cls = { Key = "C:Ns.C"
                    TestClass = "Ns.C"
                    TestMethod = null
                    TestDisplay = null
                    Parents = [| "A:assembly" |] }
        @>

    let coll = describe (FakeContext(null, null, null, FakeCollection "coll"))

    test
        <@
            coll = { Key = "L:coll"
                     TestClass = null
                     TestMethod = null
                     TestDisplay = null
                     Parents = [| "A:assembly" |] }
        @>

[<Fact>]
let ``a context with nothing known, or of an unrelated type, is the assembly`` () =
    let expected =
        { Key = "A:assembly"
          TestClass = null
          TestMethod = null
          TestDisplay = null
          Parents = [||] }

    test <@ describe (FakeContext(null, null, null, null)) = expected @>
    test <@ describe (obj ()) = expected @>
