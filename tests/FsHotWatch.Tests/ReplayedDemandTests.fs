/// ADR-059 §17: a plugin's `Update` never sees a replayed check. With no cached answer
/// for one, the framework demands a real check of the file and runs `Update` on that; with
/// none to be had, the plugin fails loudly rather than skipping the file.
module FsHotWatch.Tests.ReplayedDemandTests

open System.Collections.Concurrent
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.PluginFrameworkFixtures

let private outputs: CheckOutputs =
    { Diagnostics = []
      FcsSignature = "fcs"
      UsedSignatures = Some "used"
      ApiSignature = None }

let private replayedResult file =
    { TestHelpers.fakeFileCheckResult file with
        CheckResults = Replayed outputs }

/// A plugin recording the check state of every `FileChecked` its `Update` receives.
let private recordingHandler (seen: ConcurrentQueue<string>) : PluginHandler<unit, unit> =
    { Name = PluginName.create "recorder"
      Init = ()
      Update =
        fun _ state event ->
            async {
                match event with
                | FileChecked result ->
                    seen.Enqueue(
                        match result.CheckResults with
                        | FullCheck _ -> "full"
                        | ParseOnly -> "parse-only"
                        | Replayed _ -> "replayed"
                    )
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.singleton SubscribeFileChecked
      CacheKey = None
      PrepareCommit = None
      Teardown = None }

[<Fact(Timeout = 30000)>]
let ``a replayed check with no cached answer is demanded live, and Update sees only the live check`` () =
    let seen = ConcurrentQueue<string>()
    let demanded = ConcurrentQueue<string>()
    let file = "/tmp/repo/A.fs"

    let services =
        { defaultServices with
            DemandCheck =
                fun replayed ->
                    async {
                        demanded.Enqueue(AbsFilePath.value replayed.File)

                        return
                            Some
                                { replayed with
                                    CheckResults = FullCheck Unchecked.defaultof<_> }
                    } }

    let registration = registerHandler services (recordingHandler seen)
    dispatchAndAwait registration (DispatchFileChecked(replayedResult file))
    test <@ List.ofSeq demanded = [ file ] @>
    test <@ List.ofSeq seen = [ "full" ] @>

[<Fact(Timeout = 30000)>]
let ``a demanded check that does not complete fails the plugin, never skips the file`` () =
    let seen = ConcurrentQueue<string>()
    let statuses = ConcurrentQueue<PluginStatus>()

    let services =
        { defaultServices with
            ReportStatus = fun _ status -> statuses.Enqueue status
            DemandCheck = fun _ -> async { return None } }

    let registration = registerHandler services (recordingHandler seen)

    // The fault reaches whoever waits on the event, and the plugin reports it.
    raises<ReplayedResultException>
        <@ dispatchAndAwait registration (DispatchFileChecked(replayedResult "/tmp/repo/A.fs")) @>

    test <@ seen.IsEmpty @>

    let failures =
        statuses
        |> Seq.choose (function
            | Failed(message, _, _) -> Some message
            | _ -> None)
        |> List.ofSeq

    test
        <@
            failures
            |> List.exists (fun m -> m.Contains "its demanded check did not complete")
        @>

[<Fact(Timeout = 30000)>]
let ``a host with no checker installed refuses a demand, loudly; the installed one answers it`` () =
    let host =
        FsHotWatch.PluginHost.PluginHost.create TestHelpers.sharedChecker.Value "/tmp/repo"

    let seen = ConcurrentQueue<string>()
    host.RegisterHandler(recordingHandler seen)

    // No checker installed: the plugin fails rather than skipping the file.
    host.EmitFileChecked(replayedResult "/tmp/repo/A.fs")

    TestHelpers.waitUntil
        (fun () ->
            match host.GetStatus "recorder" with
            | Some(Failed _) -> true
            | _ -> false)
        10000

    test <@ seen.IsEmpty @>

    host.SetCheckDemand(fun replayed ->
        async {
            return
                Some
                    { replayed with
                        CheckResults = FullCheck Unchecked.defaultof<_> }
        })

    host.EmitFileChecked(replayedResult "/tmp/repo/B.fs")
    TestHelpers.waitUntil (fun () -> not seen.IsEmpty) 10000
    test <@ List.ofSeq seen = [ "full" ] @>

[<Fact>]
let ``replayed diagnostics are rebuilt at every severity, and an unknown one is refused`` () =
    let diagnostic severity : StoredDiagnostic =
        { FileName = "/r/A.fs"
          StartLine = 2
          StartColumn = 3
          EndLine = 2
          EndColumn = 9
          ErrorNumber = 25
          Severity = severity
          Subcategory = "typecheck"
          Message = "m" }

    let rebuilt =
        FileCheckResult.diagnosticsOfOutputs
            { outputs with
                Diagnostics = [ for s in [ "Error"; "Warning"; "Info"; "Hidden" ] -> diagnostic s ] }

    test <@ rebuilt |> Array.map (fun d -> $"%A{d.Severity}") = [| "Error"; "Warning"; "Info"; "Hidden" |] @>

    test
        <@
            rebuilt
            |> Array.forall (fun d ->
                d.FileName = "/r/A.fs"
                && d.StartLine = 2
                && d.StartColumn = 3
                && d.EndLine = 2
                && d.EndColumn = 9
                && d.ErrorNumber = 25
                && d.Subcategory = "typecheck"
                && d.Message = "m")
        @>

    raises<System.InvalidOperationException>
        <@
            FileCheckResult.diagnosticsOfOutputs
                { outputs with
                    Diagnostics = [ diagnostic "Loud" ] }
        @>

[<Fact>]
let ``an aborted check has no diagnostics and no typed tree; a replay's outputs are its own`` () =
    let aborted = TestHelpers.fakeFileCheckResult "/tmp/repo/A.fs"
    test <@ FileCheckResult.diagnostics aborted |> Option.isNone @>
    test <@ FileCheckResult.requireTypedTree "a test" aborted |> Option.isNone @>
    test <@ FsHotWatch.CheckRecords.CheckOutputs.ofResult None (replayedResult "/tmp/repo/A.fs") = Some outputs @>

    test
        <@
            (FileCheckResult.diagnostics (replayedResult "/tmp/repo/A.fs")).Value
            |> Array.isEmpty
        @>

[<Fact>]
let ``a refused replay names the consumer that asked and the file`` () =
    let ex =
        Assert.Throws<ReplayedResultException>(fun () ->
            FileCheckResult.requireTypedTree "a test" (replayedResult "/tmp/repo/A.fs")
            |> ignore)

    test <@ ex.Consumer = "a test" @>
    test <@ ex.File = "/tmp/repo/A.fs" @>
