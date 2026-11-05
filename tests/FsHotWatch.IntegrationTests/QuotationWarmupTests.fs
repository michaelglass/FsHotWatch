/// This suite runs test classes in parallel and uses Unquote throughout, so a concurrent
/// first touch of FSharp.Core's quotation statics can throw NullReferenceException in
/// whichever test loses the race (see `QuotationWarmup`). The pipeline startup runs the
/// warm-up before any test (AUTOMATION-1020).
module FsHotWatch.Tests.QuotationWarmupTests

open Xunit
open Swensen.Unquote

[<Fact(Timeout = 5000)>]
let ``the quotation statics are warmed before any test runs`` () = test <@ QuotationWarmup.ran @>
