/// Fails the test after which the process stands in a directory that no longer exists,
/// and moves the process somewhere that does (AUTOMATION-1295).
///
/// Every test in this host shares one working directory. Once it is deleted, every
/// relative path and every `Process.Start` fails (`Interop.Sys.GetCwd` raises
/// `FileNotFoundException`), so one test that deletes the directory the process stands in
/// used to turn into ~146 failures across unrelated classes, with the culprit hidden among
/// them. With this guard it is one red test, and the tests after it run normally.
module FsHotWatch.Tests.ProcessWorkingDirectoryGuard

open Xunit.v3
open FsHotWatch.Tests.TestHelpers

/// After `testName` ends: None when the process stands somewhere that exists. Otherwise
/// the process is moved out (`moveProcessToRefuge`) and the failure that names the test
/// is returned. Tests outside the LogGlobal collection run in parallel, so the test that
/// ended first is the one named; it is not always the one that deleted the directory.
let check (testName: string) : string option =
    if processWorkingDirectoryExists () then
        None
    else
        // An evaluation in flight would put back the directory it found over the move.
        FsHotWatch.Daemon.whileNoEvaluationRuns (fun () ->
            if processWorkingDirectoryExists () then
                None
            else
                let refuge = moveProcessToRefuge ()

                Some
                    $"the process working directory no longer existed when '%s{testName}' ended (this test, or one running beside it, deleted the directory the process stood in). The process was moved to %s{refuge} so the tests after it can run.")

/// Runs `check` after every test in the assembly and fails the test it names.
type ProcessWorkingDirectoryGuardAttribute() =
    inherit BeforeAfterTestAttribute()

    override _.After(_methodUnderTest, test) =
        match check test.TestDisplayName with
        | Some failure -> invalidOp failure
        | None -> ()

[<assembly: ProcessWorkingDirectoryGuard>]
do ()
