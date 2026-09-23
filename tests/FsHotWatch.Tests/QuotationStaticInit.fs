/// Initialises FSharp.Core's quotation statics once, on one thread, before xUnit
/// starts running test classes in parallel.
///
/// Why: in FSharp.Core (observed on 10.1.401) the static state behind `Var` lives in
/// the file-level startup class for quotations.fs, and the two class constructors
/// depend on each other:
///
///   FSharpVar..cctor           reads  $Quotations::init@       -> needs $Quotations..cctor
///   $Quotations..cctor         writes FSharpVar::getStamp      -> needs FSharpVar..cctor
///
/// When two threads make their FIRST contact with quotations through different
/// entry points at the same moment (one constructing a `Var`, e.g. Unquote
/// deserialising `<@ fun x -> … @>`; the other touching any other quotations.fs
/// static), each holds one constructor lock and waits for the other. The CLR breaks
/// that deadlock by letting one thread proceed against a type whose constructor has
/// not run, so `FSharpVar::getStamp` is still null and `new Var(..)` throws
/// NullReferenceException at quotations.fs:109. It happens at most once per process,
/// in whichever test lost the race — hence "a different Unquote test each time".
///
/// Reproduced outside this suite: 32 threads racing `new Var(..)` against
/// `Expr.Application(..)` as the first quotation use in a fresh process threw this
/// exact NullReferenceException (FSharpVar..ctor, quotations.fs:109) in 8 of 800
/// processes; with the same warm-up done first, 0 of 800.
///
/// Running both constructors to completion here, before any test runs, removes the
/// concurrent first touch. It is not a retry and does not serialise any tests.
module FsHotWatch.Tests.QuotationStaticInit

open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Xunit.v3

/// Touches both sides of the constructor cycle on the calling thread: a quotation
/// literal with a bound variable goes through Expr.Deserialize40 (quotations.fs
/// statics) and constructs a Var (FSharpVar's statics).
let initialise () : Expr =
    let v = Var("fshwQuotationWarmup", typeof<int>)
    let quoted: Expr<int -> int> = <@ fun (x: int) -> x + 1 @>
    Expr.Let(v, Expr.Value 0, quoted.Raw)

/// Set once the warm-up has run, so a test can prove it happened before tests did.
let mutable internal ran = false

type QuotationStaticInitStartup() =
    interface ITestPipelineStartup with
        member _.StartAsync(_diagnosticMessageSink) =
            initialise () |> ignore
            ran <- true
            ValueTask.CompletedTask

        member _.StopAsync() = ValueTask.CompletedTask

[<assembly: TestPipelineStartup(typeof<QuotationStaticInitStartup>)>]
do ()

module QuotationStaticInitTests =
    open System.Reflection
    open Swensen.Unquote
    open Xunit

    [<Fact>]
    let ``the test assembly registers the quotation warm-up as its pipeline startup`` () =
        let registered =
            Assembly.GetExecutingAssembly().GetCustomAttributes()
            |> Seq.choose (fun (a: System.Attribute) ->
                match box a with
                | :? ITestPipelineStartupAttribute as s -> Some s.TestPipelineStartupType
                | _ -> None)
            |> Seq.toList

        test <@ registered = [ typeof<QuotationStaticInitStartup> ] @>

    [<Fact>]
    let ``the quotation warm-up ran before any test`` () = test <@ ran @>

    [<Fact>]
    let ``the warm-up builds a quotation that binds a variable`` () =
        match initialise () with
        | Patterns.Let(v, _, _) -> test <@ v.Name = "fshwQuotationWarmup" @>
        | other -> failwithf "expected a Let, got %A" other
