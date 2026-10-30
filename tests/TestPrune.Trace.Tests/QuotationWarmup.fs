/// FSharp.Core initializes its quotation types lazily, and two test classes deserializing
/// their first Unquote quotation at the same moment can observe `FSharpVar` before its
/// static init completes (a NullReferenceException inside the Var constructor). This
/// fixture builds one quotation with a variable before any test class runs.
namespace TestPrune.Trace.Tests

open Xunit

type QuotationWarmup() =
    do
        match <@ fun (x: int) -> x + 1 @> with
        | Quotations.Patterns.Lambda(v, _) -> ignore v.Name
        | _ -> ()

[<assembly: AssemblyFixture(typeof<QuotationWarmup>)>]
do ()
