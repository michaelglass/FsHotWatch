module FsHotWatch.Tests.TestHostTimeoutTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers

// A test host killed at its timeout must be stopped in a way that lets coverage clean up.
// A coverage run is `dotnet run` → the test platform's controller → the test host, and it
// is the CONTROLLER that restores the assemblies it instrumented (`X.dll.orig` → `X.dll`),
// once the host below it has exited. Measured against a real xunit.v3 + Microsoft Testing
// Platform + CodeCoverage host: a SIGTERM (or even a SIGKILL) to the host alone left no
// `.orig` behind; any signal that also reached the controller — an immediate tree kill,
// or SIGTERM to the whole tree — left them all.
//
// The stand-in here is that controller: it starts its "host" (a `sleep`), "restores"
// (touches a file) once the host has exited, and exits without restoring if it is
// signalled itself. The file exists only if the host was stopped and the controller was
// left to finish.
//
// Real children and a real 1s timeout, so this lives in the integration suite (no
// coverage package): the moment the kill lands is OS scheduling.

let private writeController (path: string) (started: string) (restored: string) =
    File.WriteAllText(
        path,
        $"trap 'exit 143' TERM INT\n\
          sleep 600 &\n\
          touch \"%s{started}\"\n\
          wait $!\n\
          touch \"%s{restored}\"\n"
    )

[<Fact(Timeout = 60000)>]
let ``a test host killed at its timeout is stopped from the bottom up, so its controller restores and a marker names the timeout``
    ()
    =
    withTempDir "tp-timeout-graceful" (fun tmpDir ->
        let controller = Path.Combine(tmpDir, "controller.sh")
        let started = Path.Combine(tmpDir, "started")
        let restored = Path.Combine(tmpDir, "restored")
        writeController controller started restored

        let configs =
            [ { Project = "SlowTests"
                Command = "sh"
                Args = $"\"%s{controller}\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = Some 1
                ReportVerificationFormat = AutoDetect
                Starvation = None
                AlwaysClasses = [] } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        host.RegisterHandler(create ":memory:" tmpDir (Some configs) None None None None [])
        host.EmitBuildCompleted(BuildSucceeded)

        waitForTerminalStatus host "test-prune" 45000

        test <@ File.Exists started @>
        test <@ File.Exists restored @>

        let markers =
            allFilesUnder ("SlowTests" + TimeoutMarker.Suffix) (Ctrf.reportsDir tmpDir)

        test <@ markers.Length = 1 @>
        test <@ (File.ReadAllText markers[0]).Contains "TIMED OUT after 1s" @>)
