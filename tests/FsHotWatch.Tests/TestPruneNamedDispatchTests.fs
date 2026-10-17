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
      ReportVerificationFormat = AutoDetect
      AlwaysClasses = [] }

/// Index Lib + Tests with `extensions`, edit the handler's body, and return the
/// `affected-tests` answer once the edit has been handled.
let private affectedAfterHandlerEdit (tmpDir: string) (extensions: ITestPruneExtension list) =
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
    // Each wait below follows real checks of both scripts: test-prune folds their symbol
    // results into the impact index, work with no product bound short of
    // `ImpactSelectionDeadline`, so the waits take the real-check margin.
    waitForPluginIdle host "test-prune" (float RealCheckWaitMs / 1000.0)
    // The batch flush indexes both files; the extension refreshes against that index.
    host.EmitBatchChecked(fakeBatchChecked [ libFile; testsFile ])
    waitForQuiescent host RealCheckWaitMs

    File.WriteAllText(libFile, libSource "2")
    checkAndEmit libFile

    // The edit's FileChecked has committed, so `affected-tests` answers from its changes.
    // Both the positive case and the negative control read that same settled state, so
    // the control's absence is not a wait that gave up early.
    waitForQuiescent host RealCheckWaitMs

    host.RunCommand("affected-tests", [||])
    |> Async.RunSynchronously
    |> Option.defaultValue ""

[<Fact(Timeout = RealCheckTestCapMs)>]
let ``a configured named-dispatch extension selects the test that dispatches an edited handler`` () =
    withTempDir "tp-named-dispatch" (fun tmpDir ->
        let affected =
            affectedAfterHandlerEdit tmpDir [ TestPrune.NamedDispatch.NamedDispatchExtension() ]

        test <@ affected.Contains "purgeJobTest" @>)

/// The negative control: the same tree and edit with no extension selects nothing, so the
/// positive result above is the extension's edge and not an AST path.
[<Fact(Timeout = RealCheckTestCapMs)>]
let ``without the extension the dispatching test is not selected`` () =
    withTempDir "tp-named-dispatch-none" (fun tmpDir ->
        let affected = affectedAfterHandlerEdit tmpDir []
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
