module TestPrune.Trace.Tests.ScaffoldTests

open System.IO
open System.Reflection
open Xunit
open Swensen.Unquote
open TestPrune.Trace.Recorder
open TestPrune.Trace.Tests

[<Fact>]
let ``recorder exposes every probe the contract names, with the contract signature`` () =
    let t = typeof<Probes>
    test <@ t.FullName = Contract.ProbesType @>

    let has name (args: System.Type[]) =
        t.GetMethod(name, BindingFlags.Public ||| BindingFlags.Static, null, args, null)
        |> isNull
        |> not

    test <@ has Contract.Hit [| typeof<int> |] @>
    test <@ has Contract.HitIfNotNull [| typeof<obj>; typeof<int> |] @>
    test <@ has Contract.HitIfTrue [| typeof<bool>; typeof<int> |] @>
    test <@ has Contract.HitTag [| typeof<int>; typeof<int> |] @>
    test <@ has Contract.EnterStatic [||] @>
    test <@ has Contract.ExitStatic [||] @>

[<Fact>]
let ``recorder targets net8 and carries no package dependency but a low FSharp.Core`` () =
    let asm = typeof<Probes>.Assembly
    let refs = asm.GetReferencedAssemblies()

    let isFramework (n: string) =
        n = "netstandard" || n.StartsWith "System." || n = "System"

    // Anything that is neither the framework nor FSharp.Core would be a package the
    // consumer's test process has to resolve.
    let foreign =
        refs
        |> Array.filter (fun a -> a.Name <> "FSharp.Core" && not (isFramework a.Name))

    let fsCore = refs |> Array.find (fun a -> a.Name = "FSharp.Core")

    let targetFramework =
        asm.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>().FrameworkName

    test <@ Array.isEmpty foreign @>
    test <@ fsCore.Version.Major = 8 @>
    test <@ targetFramework = ".NETCoreApp,Version=v8.0" @>

[<Fact>]
let ``probe and scope stubs are inert until the recorder core exists`` () =
    Probes.Hit 1
    Probes.HitIfNotNull(box "x", 2)
    Probes.HitIfTrue(true, 3)
    Probes.HitTag(1, 4)
    Probes.EnterStatic()
    Probes.ExitStatic()
    Scopes.Enter "T:x"
    Scopes.LinkCurrentTo "C:y"
    test <@ isNull (Scopes.CurrentKey()) @>
    Scopes.Exit()

[<Fact>]
let ``the trace cli prints its usage and exits 2 without a verb`` () =
    test <@ TestPrune.Trace.Cli.Program.main [||] = 2 @>

[<Fact>]
let ``the fixtures are built next to the tests`` () =
    test <@ File.Exists(Path.Combine(Fixtures.fxLibDir, "FxLib.dll")) @>
    test <@ File.Exists(Path.Combine(Fixtures.fxDriverDir, "FxDriver.dll")) @>
    test <@ File.Exists(Path.Combine(Fixtures.fxTestsDir, "FxTests.dll")) @>
