/// Extension edges under TestPrune's whole-tree contract: a configured named-dispatch
/// extension's edges reach impact selection, and an extension that fails is reported to
/// the ledger until it answers again.
module FsHotWatch.Tests.TestPruneNamedDispatchTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open TestPrune.AstAnalyzer
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.Extensions
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// The handler is registered under a name and reached only by that name: the test never
/// references `runPurge`, so the AST graph has no path from it to the handler.
let private libSource (purgeBody: string) =
    $"""module Lib

type DispatchedAsAttribute(channel: string, name: string) =
    inherit System.Attribute()

type DispatchTemplateAttribute(channel: string, template: string) =
    inherit System.Attribute()

[<DispatchedAs("job", "Purge")>]
let runPurge () = %s{purgeBody}

[<DispatchTemplate("job", "/admin/jobs/{{action}}/{{name}}")>]
let run (name: string) = name.Length
"""

let private testsSource =
    """module Tests

type FactAttribute() =
    inherit System.Attribute()

[<Fact>]
let purgeJobTest () =
    let url = "/admin/jobs/run/Purge"
    assert (url.Length > 0)
"""

let private echoConfig: TestConfig =
    { Project = "Lib"
      Command = "echo"
      Args = "ok"
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }

/// Index Lib + Tests with `extensions`, edit the handler's body, and return the
/// `affected-tests` answer once it names the dispatching test or `waitMs` elapses.
let private affectedAfterHandlerEdit (tmpDir: string) (extensions: ITestPruneExtension list) (waitMs: int) =
    let dbPath = Path.Combine(tmpDir, "tp.db")
    let libFile = Path.Combine(tmpDir, "Lib.fsx")
    let testsFile = Path.Combine(tmpDir, "Tests.fsx")
    File.WriteAllText(libFile, libSource "1")
    File.WriteAllText(testsFile, testsSource)

    let checker = sharedChecker.Value
    let pipeline = CheckPipeline(checker)
    let host = createModelHost checker tmpDir

    let handler =
        create dbPath tmpDir (Some [ echoConfig ]) (Some(fun _db -> extensions)) None None None []

    host.RegisterHandler(handler)

    let libOptions =
        getScriptOptions checker libFile (libSource "1") |> Async.RunSynchronously

    pipeline.RegisterProject(
        libFile,
        { libOptions with
            SourceFiles = [| libFile; testsFile |] }
    )

    let checkAndEmit (file: string) =
        match pipeline.CheckFile(AbsFilePath.create file) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(stampFixture r)
        | None -> failwith $"CheckFile failed for %s{file}"

    emitBuildAndWaitTerminal host
    checkAndEmit libFile
    checkAndEmit testsFile
    waitForPluginIdle host "test-prune" 10.0
    // The batch flush indexes both files; the extension refreshes against that index.
    emitBatchAndQuiesce host [ libFile; testsFile ]

    File.WriteAllText(libFile, libSource "2")
    checkAndEmit libFile

    let mutable affected = ""

    waitUntil
        (fun () ->
            match host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously with
            | Some v -> affected <- v
            | None -> ()

            affected.Contains "purgeJobTest")
        waitMs

    affected

[<Fact(Timeout = 60000)>]
let ``a configured named-dispatch extension selects the test that dispatches an edited handler`` () =
    withTempDir "tp-named-dispatch" (fun tmpDir ->
        let affected =
            affectedAfterHandlerEdit tmpDir [ TestPrune.NamedDispatch.NamedDispatchExtension() ] 10000

        test <@ affected.Contains "purgeJobTest" @>)

/// The negative control: the same tree and edit with no extension selects nothing, so the
/// positive result above is the extension's edge and not an AST path.
[<Fact(Timeout = 60000)>]
let ``without the extension the dispatching test is not selected`` () =
    withTempDir "tp-named-dispatch-none" (fun tmpDir ->
        let affected = affectedAfterHandlerEdit tmpDir [] 3000
        test <@ not (affected.Contains "purgeJobTest") @>)

[<Fact(Timeout = 30000)>]
let ``an extension failure stays reported until the extension answers again`` () =
    withTempDir "tp-ext-recover" (fun tmpDir ->
        let broken = ref true

        let flaky =
            { new ITestPruneExtension with
                member _.Name = "flaky-extension"

                member _.AnalyzeEdges _symbolStore _repoRoot =
                    if broken.Value then failwith "not yet" else [] }

        let host = createModelHost (Unchecked.defaultof<_>) tmpDir

        let handler =
            // A file, not `:memory:`: this test flushes twice, and a second pooled
            // connection to `:memory:` opens a different, empty database.
            create
                (Path.Combine(tmpDir, "tp.db"))
                tmpDir
                (Some [ echoConfig ])
                (Some(fun _db -> [ flaky ]))
                None
                None
                None
                []

        let state = registerWithStateObserver host handler

        let reported () =
            host.GetErrorsByPlugin("test-prune")
            |> Map.tryFind (extensionLedgerKey "flaky-extension")
            |> Option.defaultValue []

        emitBuildAndWaitTerminal host
        // Reported after the run's ledger rewrite, and held in state so the next run widens.
        test <@ reported () |> List.exists (fun e -> e.Message.Contains "not yet") @>
        test <@ (state ()).FailedExtensions = Map.ofList [ "flaky-extension", "not yet" ] @>

        // Nothing new was indexed, but a failed refresh is owed a retry on the next flush.
        broken.Value <- false
        emitBuildAndWaitTerminal host
        test <@ reported () |> List.isEmpty @>
        test <@ (state ()).FailedExtensions.IsEmpty @>)
