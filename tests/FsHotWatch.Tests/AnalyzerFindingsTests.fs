[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.AnalyzerFindingsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.Analyzers.AnalyzersPlugin
open FsHotWatch.Analyzers.AnalyzerFindings
open FsHotWatch.Tests.TestHelpers

// --- the daemon side: what the analyzers plugin records and serves ---

let private message (code: string) (text: string) : FSharp.Analyzers.SDK.Message =
    { Type = "Fixture"
      Message = text
      Code = code
      Severity = FSharp.Analyzers.SDK.Severity.Warning
      Range =
        FSharp.Compiler.Text.Range.mkRange
            "Fixture.fs"
            (FSharp.Compiler.Text.Position.mkPos 3 4)
            (FSharp.Compiler.Text.Position.mkPos 3 9)
      Fixes = [] }

let private reporting (messages: FSharp.Analyzers.SDK.Message list) : AnalyzerRunner =
    fun _client _context ->
        async {
            return
                [ { FSharp.Analyzers.SDK.AnalysisResult.AnalyzerName = "Fixture"
                    Output = Result.Ok messages } ]
        }

let private fixtureProjectOptions (repoRoot: string) : FSharp.Compiler.CodeAnalysis.FSharpProjectOptions =
    { ProjectFileName = Path.Combine(repoRoot, "Fixture.fsproj")
      ProjectId = None
      SourceFiles = [||]
      OtherOptions = [||]
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UtcNow
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

/// One real typed check, shared: the plugin only needs a `FullCheck` to stamp a file
/// as typed, and the runner seam ignores what the check says.
let private typedCheck =
    lazy
        (withTempDir "findings-typed" (fun tmpDir ->
            let checker = FSharp.Compiler.CodeAnalysis.FSharpChecker.Create()
            let file = Path.Combine(tmpDir, "Typed.fsx")
            let source = "module Typed\nlet answer = 42\n"
            File.WriteAllText(file, source)
            let sourceText = FSharp.Compiler.Text.SourceText.ofString source

            let options, _ =
                checker.GetProjectOptionsFromScript(file, sourceText, assumeDotNetFramework = false)
                |> Async.RunSynchronously

            match
                checker.ParseAndCheckFileInProject(file, 0, sourceText, options)
                |> Async.RunSynchronously
            with
            | _, FSharp.Compiler.CodeAnalysis.FSharpCheckFileAnswer.Succeeded results -> FullCheck results
            | _, FSharp.Compiler.CodeAnalysis.FSharpCheckFileAnswer.Aborted -> failwith "FCS aborted the fixture check"))

/// A check of `file` as it is on disk now, typed or parse-only.
let private checkedFromDisk (repoRoot: string) (typed: bool) (file: string) =
    { fakeFileCheckResult file with
        Source = File.ReadAllText file
        ParseResults = Unchecked.defaultof<_>
        CheckResults = if typed then typedCheck.Value else ParseOnly
        ProjectOptions = fixtureProjectOptions repoRoot }

/// A host whose model's checkable files are `files`, running the analyzers plugin over
/// `runner`, after analyzing each file as it is on disk.
let private analyzed (repoRoot: string) (typed: bool) (runner: AnalyzerRunner) (files: string list) =
    let host = PluginHost.create (Unchecked.defaultof<_>) repoRoot

    host.WorkStore.PublishProjectModelWithFiles(fixtureModel, files |> List.map AbsFilePath.create |> Set.ofList)

    host.RegisterHandler(createWithSeams (Some repoRoot) [] None DiagnosticSeverity.Warning None runner)

    for file in files do
        host.EmitFileChecked(checkedFromDisk repoRoot typed file)

    waitForQuiescent host 30000
    host

let private replyOf (host: PluginHost) : Reply =
    match host.RunCommand(CommandName, [||]) |> Async.RunSynchronously with
    | None -> failwith $"the analyzers plugin serves no %s{CommandName} command"
    | Some json ->
        match tryParseReply json with
        | Result.Ok reply -> reply
        | Result.Error why -> failwith why

let private writeSource (repoRoot: string) (relative: string) (text: string) =
    let path = Path.Combine(repoRoot, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)
    path

[<Fact(Timeout = 30000)>]
let ``an analyzer finding reaches the ledger with its rule code`` () =
    withTempDir "findings-code-ledger" (fun repoRoot ->
        let file = writeSource repoRoot "src/A.fs" "module A\nlet x = []\n"

        let host =
            analyzed repoRoot false (reporting [ message "FL0065" "Use List.isEmpty" ]) [ file ]

        let entries =
            host.GetErrorsByPlugin "analyzers" |> Map.tryFind file |> Option.defaultValue []

        test <@ entries |> List.map _.Code = [ Some "FL0065" ] @>)

[<Fact(Timeout = 30000)>]
let ``the findings command serves each finding with its code and the source it was analyzed from`` () =
    withTempDir "findings-command" (fun repoRoot ->
        let text = "module A\nlet x = []\n"
        let file = writeSource repoRoot "src/A.fs" text

        let host =
            analyzed repoRoot false (reporting [ message "FL0065" "Use List.isEmpty" ]) [ file ]

        let reply = replyOf host

        test <@ reply.ModelGeneration = Some fixtureModelGeneration @>
        test <@ reply.Expected = [ file ] @>
        test <@ reply.FailOnSeverity = DiagnosticSeverity.Warning @>

        let served = reply.Files |> List.find (fun f -> f.File = file)

        test <@ served.Analyzed.SourceHash = sourceHashOf text @>
        test <@ served.Analyzed.ConfigDigest = configDigestOf (configChain (Some repoRoot) file) @>
        // A parse-only check is recorded as one: its typed rules saw nothing.
        test <@ not served.Analyzed.Typed @>
        // Stored after promotion: a warning at the warning threshold is an error.
        test
            <@ served.Entries |> List.map (fun e -> e.Code, e.Severity) = [ Some "FL0065", DiagnosticSeverity.Error ] @>)

[<Fact(Timeout = 60000)>]
let ``an edit the daemon has not picked up is stale, never a clean answer about the old tree`` () =
    // The stale green this verb exists to close (AUTOMATION-1248): the daemon analyzed
    // the file clean, the file was then edited to contain a finding, and until the
    // daemon picks the edit up its status still says `analyzers: ok`.
    withTempDir "findings-stale-green" (fun repoRoot ->
        let file =
            writeSource repoRoot "src/AccountStatus.fs" "module AccountStatus\nlet ok = 1\n"

        let host = analyzed repoRoot true (reporting []) [ file ]

        // Analyzed clean, from the bytes on disk: the honest answer is Current and empty.
        test <@ judge (diskView (Some repoRoot)) (replyOf host) = Current [] @>

        File.WriteAllText(file, "module AccountStatus\nlet x = []\n")

        // The daemon has seen nothing new. Its reply is the same; the tree is not.
        test <@ judge (diskView (Some repoRoot)) (replyOf host) = Stale [ SourceChanged file ] @>)

[<Fact(Timeout = 60000)>]
let ``a file the daemon analyzed from a parse-only check is not a complete answer`` () =
    withTempDir "findings-parse-only" (fun repoRoot ->
        let typedFile = writeSource repoRoot "src/A.fs" "module A\n"
        let parseOnly = writeSource repoRoot "src/B.fs" "module B\n"
        let host = PluginHost.create (Unchecked.defaultof<_>) repoRoot

        host.WorkStore.PublishProjectModelWithFiles(
            fixtureModel,
            [ typedFile; parseOnly ] |> List.map AbsFilePath.create |> Set.ofList
        )

        host.RegisterHandler(createWithSeams (Some repoRoot) [] None DiagnosticSeverity.Warning None (reporting []))
        host.EmitFileChecked(checkedFromDisk repoRoot true typedFile)
        host.EmitFileChecked(checkedFromDisk repoRoot false parseOnly)
        waitForQuiescent host 30000

        test <@ judge (diskView (Some repoRoot)) (replyOf host) = Incomplete [ parseOnly ] @>)

[<Fact(Timeout = 30000)>]
let ``an expected file the analyzers never saw makes the answer incomplete`` () =
    withTempDir "findings-unseen" (fun repoRoot ->
        let seen = writeSource repoRoot "src/A.fs" "module A\n"
        let unseen = writeSource repoRoot "src/B.fs" "module B\n"
        let host = PluginHost.create (Unchecked.defaultof<_>) repoRoot

        host.WorkStore.PublishProjectModelWithFiles(
            fixtureModel,
            [ seen; unseen ] |> List.map AbsFilePath.create |> Set.ofList
        )

        host.RegisterHandler(createWithSeams (Some repoRoot) [] None DiagnosticSeverity.Warning None (reporting []))
        host.EmitFileChecked(checkedFromDisk repoRoot false seen)
        waitForQuiescent host 30000

        let reply = replyOf host
        test <@ reply.Expected = List.sort [ seen; unseen ] @>
        test <@ reply.Files |> List.map _.File = [ seen ] @>)

[<Fact(Timeout = 30000)>]
let ``with no available project model the reply names no generation and no expected files`` () =
    withTempDir "findings-no-model" (fun repoRoot ->
        let host = PluginHost.create (Unchecked.defaultof<_>) repoRoot
        host.RegisterHandler(createWithSeams (Some repoRoot) [] None DiagnosticSeverity.Warning None (reporting []))

        let reply = replyOf host
        test <@ reply.ModelGeneration = None @>
        test <@ List.isEmpty reply.Expected @>
        test <@ judge (diskView (Some repoRoot)) reply = Stale [ ModelUnavailable ] @>)

// --- the reader side: `judge` against a disk it is handed ---

let private stamp (text: string) : AnalyzedFile =
    { SourceHash = sourceHashOf text
      ModelGeneration = Some 1L
      AnalyzerSet = Some "set-1"
      ConfigDigest = "cfg-1"
      Typed = true }

let private finding (code: string) (line: int) : ErrorEntry =
    { Message = $"rule %s{code}"
      Severity = DiagnosticSeverity.Error
      Line = line
      Column = 0
      Code = Some code
      Detail = None }

let private replyFor (files: (string * AnalyzedFile * ErrorEntry list) list) : Reply =
    { ModelGeneration = Some 1L
      Expected = files |> List.map (fun (f, _, _) -> f) |> List.sort
      AnalyzerSet = Result.Ok "set-1"
      FailOnSeverity = DiagnosticSeverity.Hint
      ScopeRoot = Some "/repo"
      AnalyzerCount = 3
      Files =
        files
        |> List.map (fun (f, analyzed, entries) ->
            { File = f
              Analyzed = analyzed
              Entries = entries }) }

let private diskOf (texts: Map<string, string>) : DiskView =
    { SourceHash = fun file -> Map.tryFind file texts |> Option.map sourceHashOf
      ConfigDigest = fun _ -> "cfg-1" }

[<Fact>]
let ``judge answers Current with every expected file's findings, sorted, codes intact`` () =
    let reply =
        replyFor
            [ "/repo/src/B.fs", stamp "b", [ finding "FL0065" 9 ]
              "/repo/src/A.fs", stamp "a", [ finding "MGA-ERROR-REPORT-001" 5; finding "FL0065" 2 ] ]

    let disk = diskOf (Map.ofList [ "/repo/src/A.fs", "a"; "/repo/src/B.fs", "b" ])

    test
        <@
            judge disk reply = Current
                [ "/repo/src/A.fs", finding "FL0065" 2
                  "/repo/src/A.fs", finding "MGA-ERROR-REPORT-001" 5
                  "/repo/src/B.fs", finding "FL0065" 9 ]
        @>

[<Fact>]
let ``judge refuses as stale when a file's source moved since its analysis`` () =
    let reply = replyFor [ "/repo/src/A.fs", stamp "old text", [] ]
    let disk = diskOf (Map.ofList [ "/repo/src/A.fs", "new text" ])
    test <@ judge disk reply = Stale [ SourceChanged "/repo/src/A.fs" ] @>

[<Fact>]
let ``judge refuses as stale when a file is gone from disk`` () =
    let reply = replyFor [ "/repo/src/A.fs", stamp "a", [] ]
    test <@ judge (diskOf Map.empty) reply = Stale [ Unreadable "/repo/src/A.fs" ] @>

[<Fact>]
let ``judge refuses as stale when a file was analyzed by another analyzer set`` () =
    let reply =
        replyFor
            [ "/repo/src/A.fs",
              { stamp "a" with
                  AnalyzerSet = Some "set-0" },
              [] ]

    let disk = diskOf (Map.ofList [ "/repo/src/A.fs", "a" ])
    test <@ judge disk reply = Stale [ AnalyzerSetChanged "/repo/src/A.fs" ] @>

[<Fact>]
let ``judge refuses as stale when a file's analyzer config changed`` () =
    let reply =
        replyFor
            [ "/repo/src/A.fs",
              { stamp "a" with
                  ConfigDigest = "cfg-0" },
              [] ]

    let disk = diskOf (Map.ofList [ "/repo/src/A.fs", "a" ])
    test <@ judge disk reply = Stale [ ConfigChanged "/repo/src/A.fs" ] @>

[<Fact>]
let ``judge refuses as incomplete when an expected file has no analysis`` () =
    let reply =
        { replyFor [ "/repo/src/A.fs", stamp "a", [] ] with
            Expected = [ "/repo/src/A.fs"; "/repo/src/B.fs" ] }

    let disk = diskOf (Map.ofList [ "/repo/src/A.fs", "a"; "/repo/src/B.fs", "b" ])
    test <@ judge disk reply = Incomplete [ "/repo/src/B.fs" ] @>

[<Fact>]
let ``staleness outranks incompleteness, because waiting can resolve it`` () =
    let reply =
        { replyFor [ "/repo/src/A.fs", stamp "a", [] ] with
            Expected = [ "/repo/src/A.fs"; "/repo/src/B.fs" ] }

    let disk = diskOf (Map.ofList [ "/repo/src/A.fs", "edited" ])
    test <@ judge disk reply = Stale [ SourceChanged "/repo/src/A.fs" ] @>

[<Fact>]
let ``judge ignores a file the model no longer compiles`` () =
    let reply =
        { replyFor
              [ "/repo/src/A.fs", stamp "a", []
                "/repo/src/Gone.fs", stamp "g", [ finding "FL0065" 1 ] ] with
            Expected = [ "/repo/src/A.fs" ] }

    let disk = diskOf (Map.ofList [ "/repo/src/A.fs", "a" ])
    test <@ judge disk reply = Current [] @>

[<Fact>]
let ``judge refuses outright when the analyzer set has no identity`` () =
    let reply =
        { replyFor [ "/repo/src/A.fs", stamp "a", [] ] with
            AnalyzerSet = Result.Error "x.dll: no portable PDB" }

    match judge (diskOf (Map.ofList [ "/repo/src/A.fs", "a" ])) reply with
    | Refused why -> test <@ why.Contains "x.dll: no portable PDB" @>
    | other -> failwith $"expected a refusal, got %A{other}"

[<Fact>]
let ``a reply survives the wire with its codes and stamps`` () =
    let reply =
        { replyFor [ "/repo/src/A.fs", stamp "a", [ finding "FL0065" 2 ] ] with
            ScopeRoot = None }

    test <@ tryParseReply (serializeReply reply) = Result.Ok reply @>

[<Fact>]
let ``a reply with an unknown schema is refused, not misread`` () =
    match tryParseReply """{"schema":"fshw-analyzer-findings-reply-v0"}""" with
    | Result.Error why -> test <@ why.Contains "fshw-analyzer-findings-reply-v0" @>
    | Result.Ok _ -> failwith "an unknown schema was read"

[<Fact>]
let ``a reply with an unidentified analyzer set and unstamped details survives the wire`` () =
    let reply =
        { replyFor
              [ "/repo/src/A.fs",
                { stamp "a" with
                    ModelGeneration = None
                    AnalyzerSet = None },
                [ { finding "x" 1 with Code = None } ] ] with
            AnalyzerSet = Result.Error "x.dll: no portable PDB" }

    test <@ tryParseReply (serializeReply reply) = Result.Ok reply @>

[<Fact>]
let ``a reply with no schema, or that is not JSON, is refused`` () =
    test <@ Result.isError (tryParseReply """{"files":[]}""") @>
    test <@ Result.isError (tryParseReply "fshw-unknown-command:analyzer-findings") @>

[<Fact>]
let ``every stale reason names its file, except a missing model`` () =
    let reasons =
        [ SourceChanged "/a"
          Unreadable "/a"
          AnalyzerSetChanged "/a"
          ConfigChanged "/a"
          ModelUnavailable ]

    test <@ reasons |> List.map StaleReason.file = [ Some "/a"; Some "/a"; Some "/a"; Some "/a"; None ] @>

    test
        <@
            reasons
            |> List.forall (StaleReason.describe >> String.IsNullOrWhiteSpace >> not)
        @>

[<Fact>]
let ``the disk view reads source and config chain from disk, and an absent file as unreadable`` () =
    withTempDir "findings-disk" (fun repoRoot ->
        let file = writeSource repoRoot "src/A.fs" "module A\n"
        File.WriteAllText(Path.Combine(repoRoot, ".editorconfig"), "root = true\n")
        let disk = diskView (Some repoRoot)

        test <@ disk.SourceHash file = Some(sourceHashOf "module A\n") @>
        test <@ disk.SourceHash(Path.Combine(repoRoot, "src/Gone.fs")) = None @>
        test <@ disk.ConfigDigest file = configDigestOf (configChain (Some repoRoot) file) @>)
