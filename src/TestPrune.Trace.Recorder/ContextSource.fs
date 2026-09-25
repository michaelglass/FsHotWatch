namespace TestPrune.Trace.Recorder

open System
open System.Collections.Concurrent
open System.Reflection
open System.Runtime.CompilerServices

/// What the recorder needs to know about one attribution context.
type ContextInfo =
    {
        /// Scope key the context's hits go to.
        Key: string
        /// Test class full name, or null.
        TestClass: string
        /// Test method name, or null when the context is not a test.
        TestMethod: string
        /// Test display name, or null when the context is not a test.
        TestDisplay: string
        /// Enclosing scope keys, innermost first.
        Parents: string[]
    }

/// Where "the current test" comes from. Production binds xUnit v3 by reflection
/// (no compile-time dependency); tests substitute a fake.
type IContextSource =
    /// The current context object, compared by reference for caching; null when none.
    abstract Current: unit -> obj

    /// Names the scope a context object stands for.
    abstract Describe: context: obj -> ContextInfo

/// Binds xUnit v3's `TestContext.Current` by reflection.
module XunitContextSource =
    let private props = ConcurrentDictionary<struct (Type * string), PropertyInfo>()

    let private find (t: Type) (name: string) : PropertyInfo =
        Seq.append [ t ] (t.GetInterfaces())
        |> Seq.tryPick (fun i -> i.GetProperty name |> Option.ofObj)
        |> Option.toObj

    /// Read `name` from `o` through its type or any interface it implements. xUnit's
    /// concrete context types are internal; their interfaces are the public surface.
    let private read (o: obj) (name: string) : obj =
        if isNull o then
            null
        else
            let p =
                props.GetOrAdd(struct (o.GetType(), name), (fun (struct (t, n)) -> find t n))

            if isNull p then null else p.GetValue o

    // Not inlined: the optimized build would copy the null branch into every caller.
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let private str (o: obj) = if isNull o then null else o.ToString()

    let private describe (ctx: obj) : ContextInfo =
        let testObj = read ctx "Test"
        let className = str (read (read ctx "TestClass") "TestClassName")
        let collName = str (read (read ctx "TestCollection") "TestCollectionDisplayName")
        let collParent = if isNull collName then [||] else [| "L:" + collName |]

        if not (isNull testObj) then
            { Key = "T:" + str (read testObj "UniqueID")
              TestClass = className
              TestMethod = str (read (read ctx "TestMethod") "MethodName")
              TestDisplay = str (read testObj "TestDisplayName")
              Parents = Array.concat [ [| "C:" + className |]; collParent; [| "A:assembly" |] ] }
        elif not (isNull className) then
            { Key = "C:" + className
              TestClass = className
              TestMethod = null
              TestDisplay = null
              Parents = Array.append collParent [| "A:assembly" |] }
        elif not (isNull collName) then
            { Key = "L:" + collName
              TestClass = null
              TestMethod = null
              TestDisplay = null
              Parents = [| "A:assembly" |] }
        else
            { Key = "A:assembly"
              TestClass = null
              TestMethod = null
              TestDisplay = null
              Parents = [||] }

    type private Source(current: Func<obj>) =
        interface IContextSource with
            member _.Current() = current.Invoke()
            member _.Describe(ctx) = describe ctx

    /// A source reading xUnit-shaped context objects from `current`.
    let internal ofGetter (current: Func<obj>) : IContextSource = Source current :> IContextSource

    /// Bind the static `Current` property of `typeName`. None when the type or property is missing.
    let internal tryCreateFrom (typeName: string) : IContextSource option =
        match Type.GetType(typeName, false) with
        | null -> None
        | t ->
            match t.GetProperty("Current", BindingFlags.Public ||| BindingFlags.Static) with
            | null -> None
            | p ->
                // A bound delegate, not MethodInfo.Invoke: this runs on every probe hit. Valid
                // because the getter returns a reference type and delegate returns are covariant.
                let f = Delegate.CreateDelegate(typeof<Func<obj>>, p.GetGetMethod()) :?> Func<obj>
                Some(ofGetter f)

    /// Bind `Xunit.TestContext.Current` once. None when xUnit v3 is not loaded.
    let tryCreate () : IContextSource option =
        tryCreateFrom "Xunit.TestContext, xunit.v3.core"
