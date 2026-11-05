/// This assembly's xUnit pipeline startup: points the shared cache and state homes at
/// a per-run directory before any test runs, and removes it after (see
/// `UserHomeIsolation`), and warms the quotation statics first (`QuotationWarmup`).
module FsHotWatch.Tests.UserHomeIsolationStartup

open System.Threading.Tasks
open Xunit.v3

type UserHomeIsolationStartup() =
    interface ITestPipelineStartup with
        member _.StartAsync(_diagnosticMessageSink) =
            UserHomeIsolation.install ()
            // Before any class runs: a concurrent first touch of the quotation statics
            // throws in whichever Unquote test loses the race (AUTOMATION-1020).
            QuotationWarmup.initialise () |> ignore
            QuotationWarmup.ran <- true
            ValueTask.CompletedTask

        member _.StopAsync() =
            UserHomeIsolation.remove ()
            ValueTask.CompletedTask

[<assembly: TestPipelineStartup(typeof<UserHomeIsolationStartup>)>]
do ()
