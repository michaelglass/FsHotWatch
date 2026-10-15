/// This assembly's xUnit pipeline startup: points the shared cache and state homes at
/// a per-run directory before any test runs, and removes it after. See
/// `UserHomeIsolation`.
module FsHotWatch.Tests.UserHomeIsolationStartup

open System.Threading.Tasks
open Xunit.v3

type UserHomeIsolationStartup() =
    interface ITestPipelineStartup with
        member _.StartAsync(_diagnosticMessageSink) =
            UserHomeIsolation.install ()
            ValueTask.CompletedTask

        member _.StopAsync() =
            UserHomeIsolation.remove ()
            ValueTask.CompletedTask

[<assembly: TestPipelineStartup(typeof<UserHomeIsolationStartup>)>]
do ()
