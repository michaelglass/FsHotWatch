// The spawn helper as a CLI mode, and the daemon's opt-in launch of it. The launch and
// loss paths log, so the class shares the serialized logging collection.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.SpawnHelperModeTests

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Threading
open Xunit
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestHelpers

let private env (pairs: (string * string) list) : string -> string =
    let values = Map.ofList pairs
    fun name -> values |> Map.tryFind name |> Option.toObj

[<Fact>]
let ``only FSHW_SPAWN_HELPER=1 enables the helper`` () =
    Assert.True(SpawnHelperMode.enabled (env [ SpawnHelperMode.EnableVar, " 1 " ]))
    Assert.False(SpawnHelperMode.enabled (env [ SpawnHelperMode.EnableVar, "0" ]))
    Assert.False(SpawnHelperMode.enabled (env []))

[<Fact>]
let ``only the exact hidden argument vector serves as the helper`` () =
    let unused () : Stream =
        failwith "an ordinary command line must not open the protocol streams"

    Assert.Equal(None, SpawnHelperMode.tryRunWith unused unused [| "check" |])
    Assert.Equal(None, SpawnHelperMode.tryRunWith unused unused [| SpawnHelperMode.Flag; "extra" |])
    Assert.Equal(None, SpawnHelperMode.tryRun [| "check" |])

    // An empty request stream is a daemon that has already gone: the helper returns.
    let served =
        SpawnHelperMode.tryRunWith
            (fun () -> new MemoryStream())
            (fun () -> new MemoryStream())
            [| SpawnHelperMode.Flag |]

    Assert.Equal(Some 0, served)

[<Fact>]
let ``the helper runs this CLI with suspend injection off unless the user chose`` () =
    let psi =
        SpawnHelperMode.startInfo (env []) "/host/dotnet" "/cli/FsHotWatch.Cli.dll"

    Assert.Equal("/host/dotnet", psi.FileName)
    Assert.Equal<string list>([ "/cli/FsHotWatch.Cli.dll"; SpawnHelperMode.Flag ], List.ofSeq psi.ArgumentList)

    Assert.True(
        psi.RedirectStandardInput
        && psi.RedirectStandardOutput
        && not psi.RedirectStandardError
    )

    Assert.Equal("0", psi.Environment[ThreadSuspendInjection.RuntimeVariable])

    let chosen =
        SpawnHelperMode.startInfo
            (env [ ThreadSuspendInjection.RuntimeVariable, "1" ])
            "/host/dotnet"
            "/cli/FsHotWatch.Cli.dll"

    // The user's own value is inherited unchanged, not overridden.
    let inherited =
        Environment.GetEnvironmentVariable ThreadSuspendInjection.RuntimeVariable

    match chosen.Environment.TryGetValue ThreadSuspendInjection.RuntimeVariable with
    | true, value -> Assert.Equal(inherited, value)
    | false, _ -> Assert.Null(inherited)

[<Fact>]
let ``a helper that cannot start is an error, not an exception`` () =
    match
        SpawnHelperMode.launch
            (fun () -> ProcessStartInfo("/nonexistent/fshw-no-such-host", UseShellExecute = false))
            ()
    with
    | Error reason -> Assert.Contains(nameof Win32Exception, reason)
    | Ok _ -> failwith "a missing host must not launch"

let private connectionToNowhere () =
    new SpawnHelper.Connection(new MemoryStream(), new MemoryStream())

[<Fact>]
let ``a disabled helper is never launched`` () =
    use _ =
        SpawnHelperMode.installWith false (fun () -> failwith "a disabled helper must not launch")

    Assert.True((SpawnHelper.current ()).IsNone)

    // This test process does not set FSHW_SPAWN_HELPER.
    use _ = SpawnHelperMode.installForDaemon ()
    Assert.True((SpawnHelper.current ()).IsNone)

[<Fact>]
let ``a helper that fails to launch leaves spawns direct`` () =
    use _ = SpawnHelperMode.installWith true (fun () -> Error "no host")
    Assert.True((SpawnHelper.current ()).IsNone)

[<Fact>]
let ``an installed helper is uninstalled and stopped on dispose`` () =
    let connection = connectionToNowhere ()
    let mutable stopped = false

    let installed =
        SpawnHelperMode.installWith true (fun () ->
            Ok
                { Pid = 1
                  Connection = connection
                  Stop = fun () -> stopped <- true })

    Assert.True(obj.ReferenceEquals(connection, (SpawnHelper.current ()).Value))
    installed.Dispose()
    Assert.True((SpawnHelper.current ()).IsNone)
    Assert.True(stopped)

// ---------------------------------------------------------------------------
// The launch, against a stand-in helper (AUTOMATION-1651). The real helper is a copy of
// this CLI, which runs the coverage-instrumented assembly, so what it executed was graded
// as this suite's coverage on some runs and not others; the tests that start it are in
// FsHotWatch.IntegrationTests (SpawnHelperProcessTests). `/bin/cat` stands in here: like
// the helper, it reads the request pipe until it closes and then exits.
// ---------------------------------------------------------------------------

[<Fact>]
let ``the default helper is this CLI's own assembly under a dotnet host`` () =
    let psi = SpawnHelperMode.defaultStartInfo ()
    Assert.True(File.Exists psi.FileName, $"the dotnet host must exist: %s{psi.FileName}")
    Assert.EndsWith("FsHotWatch.Cli.dll", psi.ArgumentList[0])
    Assert.Equal(SpawnHelperMode.Flag, psi.ArgumentList[1])

[<Fact(Timeout = 60000)>]
let ``a launched helper is connected at once, and stopping it closes its pipe and waits for it`` () =
    let standIn () =
        ProcessStartInfo(
            "/bin/cat",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        )

    match SpawnHelperMode.launch standIn () with
    | Error reason -> failwith $"the stand-in must start: %s{reason}"
    | Ok running ->
        let alive = isProcessAlive running.Pid = Liveness.Alive
        // `Stop` closes the request pipe; the stand-in reads its end, exits, and is waited for.
        running.Stop()
        Assert.True(alive, "a launched helper runs until it is stopped")

        Assert.True(
            SpinWait.SpinUntil((fun () -> isProcessAlive running.Pid = Liveness.Gone), HangWaitMs),
            "a stopped helper must exit"
        )
