/// This assembly's xUnit pipeline startup: isolates the shared cache and state homes,
/// runs the quotation warm-up (`QuotationWarmup`) once on one thread before xUnit
/// starts running test classes in parallel, and in a traced run installs the recorder
/// for sources the compiler service reads itself.
module FsHotWatch.Tests.QuotationStaticInit

open System.Threading.Tasks
open Xunit.v3

type QuotationStaticInitStartup() =
    interface ITestPipelineStartup with
        member _.StartAsync(_diagnosticMessageSink) =
            // Before anything a test can start resolves a cache or state home.
            UserHomeIsolation.install ()
            QuotationWarmup.initialise () |> ignore
            QuotationWarmup.ran <- true
            // The one pipeline startup this assembly may have also installs, in a traced
            // run only, the recorder for sources the compiler service reads itself.
            TraceFcsReads.installIfTraced ()
            ValueTask.CompletedTask

        member _.StopAsync() =
            UserHomeIsolation.remove ()
            ValueTask.CompletedTask

[<assembly: TestPipelineStartup(typeof<QuotationStaticInitStartup>)>]
do ()
