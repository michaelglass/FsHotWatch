/// a file that no longer exists is GONE, not a file that failed analysis.
///
/// A merge that took main's side deleted source files, and the gate stayed red on
/// "symbol analysis failed" warnings naming them. Removing their keys from
/// `file-freshness.json` did not hold: the next check of the vanished path stamped them
/// straight back. The tests below pin the three places that fact has to be honoured —
/// the sidecar load, the per-check stamp and the analysis-failure warning — plus the
/// project snapshot a removed compile item must not survive in.
module FsHotWatch.Tests.DeletedFileFreshnessTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open TestPrune.AstAnalyzer

let private cleanAt = DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc)

let private cleanRecord: FileFreshness.FileState =
    { FcsClean = true
      LastCleanCheckAt = Some cleanAt }

/// A real, FCS-clean check of `fileName` in `tmpDir`, with the file left on disk.
let private checkForReal (tmpDir: string) (fileName: string) (source: string) : FileCheckResult =
    let checker = sharedChecker.Value
    let pipeline = CheckPipeline(checker)
    let filePath = Path.Combine(tmpDir, fileName)
    File.WriteAllText(filePath, source)
    let projOptions = getScriptOptions checker filePath source |> Async.RunSynchronously
    pipeline.RegisterProject(filePath, projOptions)

    pipeline.CheckFile(AbsFilePath.create filePath)
    |> Async.RunSynchronously
    |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

/// A host running the TestPrune plugin. The trivial test config is what lets a
/// `BuildCompleted` reach a terminal — the clean stamp is gated on one this session.
let private newHost (tmpDir: string) : PluginHost =
    let configs =
        [ { Project = "Deleted"
            Command = "echo"
            Args = "ok"
            Group = "default"
            Environment = []
            FilterTemplate = None
            ClassJoin = " "
            TimeoutSec = None
            ReportVerificationFormat = AutoDetect } ]

    let host = createModelHost sharedChecker.Value tmpDir
    host.RegisterHandler(create (Path.Combine(tmpDir, "tp.db")) tmpDir (Some configs) None None None None [])
    host

let private analysisWarningsFor (host: PluginHost) (file: string) : string list =
    host.GetErrorsByPlugin("test-prune")
    |> Map.tryFind file
    |> Option.defaultValue []
    |> List.map (fun e -> e.Message)
    |> List.filter (fun m -> m.Contains "symbol analysis failed")

// ─── the sidecar load ───────────────────────────────────────────────────────────

[<Fact(Timeout = 5000)>]
let ``a freshness entry for a file that does not exist loads as gone — dropped, not kept`` () =
    withTempDir "load-gone" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "src", "Present.fs"), "module Present\n")

        FileFreshness.save
            tmpDir
            (Map.ofList [ "src/Present.fs", cleanRecord; "src/Domain/Types/YearMonth.fs", cleanRecord ])

        let loaded = FileFreshness.load tmpDir

        // PositiveControl: the file that IS on disk keeps its record, so the drop below
        // is about presence and not a load that returns nothing.
        test <@ Map.tryFind "src/Present.fs" loaded = Some cleanRecord @>
        test <@ not (Map.containsKey "src/Domain/Types/YearMonth.fs" loaded) @>)

// ─── the per-check stamp ────────────────────────────────────────────────────────

[<Fact(Timeout = 5000)>]
let ``stamp forgets a gone file and records a present one`` () =
    let store = Map.ofList [ "src/Gone.fs", cleanRecord ]

    test
        <@
            FileFreshness.stamp FileFreshness.Gone true cleanAt "src/Gone.fs" store
            |> Map.isEmpty
        @>

    test
        <@
            FileFreshness.stamp FileFreshness.Gone false cleanAt "src/Gone.fs" store
            |> Map.isEmpty
        @>

    // PositiveControl: the Present arms still write exactly what they did before.
    test
        <@
            FileFreshness.stamp FileFreshness.Present true cleanAt "src/New.fs" Map.empty = Map.ofList
                [ "src/New.fs", cleanRecord ]
        @>

    test
        <@
            FileFreshness.stamp FileFreshness.Present false cleanAt "src/New.fs" Map.empty = Map.ofList
                [ "src/New.fs",
                  { FcsClean = false
                    LastCleanCheckAt = None } ]
        @>

[<Fact(Timeout = 60000)>]
let ``a clean check of a file that has since vanished does not stamp it back into the sidecar`` () =
    withTempDir "stamp-gone" (fun tmpDir ->
        let result = checkForReal tmpDir "Vanished.fsx" "module Vanished\nlet n = 1\n"
        let host = newHost tmpDir
        emitBuildAndWaitTerminal host

        File.Delete(AbsFilePath.value result.File)
        emitFileAndQuiesce host result

        test <@ not (Map.containsKey "Vanished.fsx" (FileFreshness.load tmpDir)) @>
        // `load` now drops gone entries itself, so read the bytes as well: the stamp
        // must not have WRITTEN the key, not merely be hidden by the load.
        test <@ not ((File.ReadAllText(FileFreshness.sidecarPath tmpDir)).Contains "Vanished.fsx") @>

        // PositiveControl: a check of a file that is still there IS stamped clean.
        let kept = checkForReal tmpDir "Kept.fsx" "module Kept\nlet n = 1\n"
        emitFileAndQuiesce host kept
        test <@ FileFreshness.isClean "Kept.fsx" (FileFreshness.load tmpDir) @>)

// ─── the analysis-failure warning ───────────────────────────────────────────────

[<Fact(Timeout = 60000)>]
let ``an analysis failure for a file that no longer exists is not reported as a warning`` () =
    withTempDir "warn-gone" (fun tmpDir ->
        // ParseOnly is a genuine analysis failure: the handler's own `Error` arm.
        let failed =
            { checkForReal tmpDir "Deleted.fsx" "module Deleted\nlet n = 1\n" with
                CheckResults = ParseOnly }

        let file = AbsFilePath.value failed.File
        let host = newHost tmpDir

        File.Delete file
        emitFileAndQuiesce host failed

        test <@ List.isEmpty (analysisWarningsFor host file) @>
        test <@ not (Map.containsKey "Deleted.fsx" (FileFreshness.load tmpDir)) @>)

[<Fact(Timeout = 60000)>]
let ``PositiveControl: a file that exists and genuinely fails analysis still reports its warning`` () =
    withTempDir "warn-present" (fun tmpDir ->
        let failed =
            { checkForReal tmpDir "Broken.fsx" "module Broken\nlet n = 1\n" with
                CheckResults = ParseOnly }

        let file = AbsFilePath.value failed.File
        let host = newHost tmpDir

        emitFileAndQuiesce host failed

        let warned =
            waitUntilTrue (fun () -> not (analysisWarningsFor host file).IsEmpty) 10000

        test <@ warned @>)

// ─── the project snapshot ───────────────────────────────────────────────────────

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

[<Fact(Timeout = 5000)>]
let ``re-registering a project whose compile items shrank drops the removed file from the snapshot`` () =
    let pipeline = CheckPipeline(nullChecker)
    let proj = "/repo/src/App/App.fsproj"
    let kept = "/repo/src/App/Kept.fs"
    let removed = "/repo/src/Domain/Types/YearMonth.fs"
    let before = makeProjectOptions proj [ removed; kept ] []

    pipeline.RegisterProject(proj, before)
    // Guard: the file really was registered, so its absence below is the re-registration.
    test <@ pipeline.GetAllRegisteredFiles() |> List.contains (AbsFilePath.create removed) @>

    pipeline.RegisterProject(proj, { before with SourceFiles = [| kept |] })

    test <@ not (pipeline.GetAllRegisteredFiles() |> List.contains (AbsFilePath.create removed)) @>
    test <@ pipeline.GetAllRegisteredFiles() |> List.contains (AbsFilePath.create kept) @>

[<Fact(Timeout = 5000)>]
let ``PositiveControl: a file one project dropped stays registered for the project that still lists it`` () =
    let pipeline = CheckPipeline(nullChecker)
    let shared = "/repo/src/Shared/Shared.fs"
    let appProj = "/repo/src/App/App.fsproj"
    let libProj = "/repo/src/Lib/Lib.fsproj"
    let app = makeProjectOptions appProj [ shared; "/repo/src/App/App.fs" ] []
    let lib = makeProjectOptions libProj [ shared ] []

    pipeline.RegisterProject(appProj, app)
    pipeline.RegisterProject(libProj, lib)

    pipeline.RegisterProject(
        appProj,
        { app with
            SourceFiles = [| "/repo/src/App/App.fs" |] }
    )

    test <@ pipeline.GetAllRegisteredFiles() |> List.contains (AbsFilePath.create shared) @>

[<Fact(Timeout = 15000)>]
let ``a sidecar that is blank, null, or holds malformed entries loads as far as it can`` () =
    // The sidecar is derivative: whatever cannot be read is treated as never checked
    // clean, and the next clean check rewrites it. Nothing here may throw.
    withTempDir "freshness-malformed" (fun tmpDir ->
        let sidecar = FileFreshness.sidecarPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName sidecar) |> ignore

        for present in [ "Partial.fsx"; "BadDate.fsx"; "NotAnObject.fsx" ] do
            File.WriteAllText(Path.Combine(tmpDir, present), "")

        File.WriteAllText(sidecar, "   ")
        test <@ FileFreshness.load tmpDir = Map.empty @>

        File.WriteAllText(sidecar, "null")
        test <@ FileFreshness.load tmpDir = Map.empty @>

        File.WriteAllText(
            sidecar,
            """{ "Partial.fsx": {}, "BadDate.fsx": { "fcsClean": true, "lastCleanCheckAt": "not a date" }, "NotAnObject.fsx": 7 }"""
        )

        let loaded = FileFreshness.load tmpDir

        test
            <@
                loaded = Map
                    [ "Partial.fsx",
                      { FileFreshness.FcsClean = false
                        FileFreshness.LastCleanCheckAt = None }
                      "BadDate.fsx",
                      { FileFreshness.FcsClean = true
                        FileFreshness.LastCleanCheckAt = None } ]
            @>)
