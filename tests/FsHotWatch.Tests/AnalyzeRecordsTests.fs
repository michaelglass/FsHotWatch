/// AUTOMATION-1585: an analysis is filed box-wide as an `analyze` record, many keys per
/// file, so daemons at different trees sharing one store stop evicting each other's
/// analyses. Before, the box-wide task cache kept ONE entry per file: a start at tree T2
/// deleted the entry tree T1 needed, and the next start at T1 demanded a live check and
/// re-analysed every file the trees differ in, on every alternation.
module FsHotWatch.Tests.AnalyzeRecordsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.Analyzers
open FsHotWatch.Analyzers.AnalyzersPlugin
open FsHotWatch.Tests.TestHelpers

// --- the key ---------------------------------------------------------------------------

let private checkerOf (fcs: string) (fsharpCore: string) (schema: string) =
    CheckCache.CheckerBuild.keyIdentity
        { CheckCache.CheckerBuild.current with
            Fcs = fcs
            FSharpCore = fsharpCore
            RecordSchema = schema }

/// Fixed components: the golden pins how a key is folded from them, not this process's.
let private goldenParts: AnalyzeRecords.KeyParts =
    { RecordSchema = "fshw-analyze-semantics-v1"
      Checker =
        checkerOf
            "FSharp.Compiler.Service@43.10.0.0/fcs-mvid"
            "FSharp.Core@10.1.0.0/core-mvid"
            "fshw-check-semantics-v1"
      AnalyzerSdk = "FSharp.Analyzers.SDK@0.39.2"
      Toolchain = "dotnet-sdk 10.0.400"
      Options = "options-hash"
      AnalyzerPaths = "paths-hash"
      AnalyzerInputs = "inputs-hash"
      FailOnSeverity = "warning"
      AnalyzerConfig = "config-hash"
      File = "repo:src/P.fs"
      Source = "source-hash"
      UsedSignatures = "used-hash"
      FcsSignature = "fcs-hash" }

[<Fact>]
let ``golden: a fixed analysis's key and every component it is folded from`` () =
    let key = AnalyzeRecords.keyWith goldenParts

    let expectedLabels =
        [ "analyzer-config"
          "analyzer-inputs"
          "analyzer-paths"
          "analyzer-sdk"
          "checker"
          "fail-on-severity"
          "fcs-signature"
          "file"
          "options"
          "recipe"
          "source"
          "toolchain"
          "used-signatures" ]

    let golden = "ak1:418bd81c520afce8d139328d9c7d1a5f29505bec3eab312bd1127bbedac147c1"

    if key.Value.Text <> golden || (key.Inputs |> List.map fst) <> expectedLabels then
        failwith
            $"the analyze record key changed (%s{key.Value.Text}, labels %A{key.Inputs |> List.map fst}): \
              bump AnalyzeRecords.AnalyzeRecordSchema, then update the golden"

    test <@ key.Kind = "analyze" @>

/// Each label, perturbed alone, must re-key: a replayed analysis is exactly the analysis
/// its key names. The cases are enumerated from the key's own labels, so a label added
/// without a case here fails.
[<Fact>]
let ``every key component, perturbed alone, re-keys the analysis`` () =
    let perturb: Map<string, AnalyzeRecords.KeyParts -> AnalyzeRecords.KeyParts> =
        Map.ofList
            [ "recipe",
              (fun p ->
                  { p with
                      RecordSchema = p.RecordSchema + "x" })
              "checker", (fun p -> { p with Checker = p.Checker + "x" })
              "analyzer-sdk",
              (fun p ->
                  { p with
                      AnalyzerSdk = p.AnalyzerSdk + "x" })
              "toolchain", (fun p -> { p with Toolchain = p.Toolchain + "x" })
              "options", (fun p -> { p with Options = p.Options + "x" })
              "analyzer-paths",
              (fun p ->
                  { p with
                      AnalyzerPaths = p.AnalyzerPaths + "x" })
              "analyzer-inputs",
              (fun p ->
                  { p with
                      AnalyzerInputs = p.AnalyzerInputs + "x" })
              "fail-on-severity", (fun p -> { p with FailOnSeverity = "error" })
              "analyzer-config",
              (fun p ->
                  { p with
                      AnalyzerConfig = p.AnalyzerConfig + "x" })
              "file", (fun p -> { p with File = "repo:src/Q.fs" })
              "source", (fun p -> { p with Source = p.Source + "x" })
              "used-signatures",
              (fun p ->
                  { p with
                      UsedSignatures = p.UsedSignatures + "x" })
              "fcs-signature",
              (fun p ->
                  { p with
                      FcsSignature = p.FcsSignature + "x" }) ]

    let key = AnalyzeRecords.keyWith goldenParts

    for label, _ in key.Inputs do
        match Map.tryFind label perturb with
        | None -> failwith $"key label %s{label} has no perturbation case: add one"
        | Some change ->
            let moved = AnalyzeRecords.keyWith (change goldenParts)
            test <@ moved.Value <> key.Value @>
            test <@ ActionKey.moved key.Inputs moved.Inputs = [ label ] @>

    // The control can fail: a key that dropped a label would not move with it.
    let truncated =
        ActionKey.create "analyze" (key.Inputs |> List.filter (fun (label, _) -> label <> "options"))

    let truncatedMoved =
        ActionKey.create
            "analyze"
            ((AnalyzeRecords.keyWith (perturb["options"]goldenParts)).Inputs
             |> List.filter (fun (label, _) -> label <> "options"))

    test <@ truncated.Value = truncatedMoved.Value @>

[<Fact>]
let ``a schema bump re-keys every analysis`` () =
    let bumped =
        AnalyzeRecords.keyWith
            { goldenParts with
                RecordSchema = "fshw-analyze-semantics-v2" }

    test <@ bumped.Value <> (AnalyzeRecords.keyWith goldenParts).Value @>

[<Fact>]
let ``an FCS or FSharp.Core change re-keys every analysis; fshw's own build does not`` () =
    let partsUnder (build: CheckCache.CheckerBuild) =
        { goldenParts with
            Checker = CheckCache.CheckerBuild.keyIdentity build }

    let current = CheckCache.CheckerBuild.current
    let key = AnalyzeRecords.keyWith (partsUnder current)

    let fcs =
        AnalyzeRecords.keyWith (
            partsUnder
                { current with
                    Fcs = "FSharp.Compiler.Service@44.0.0.0/other" }
        )

    let core =
        AnalyzeRecords.keyWith (
            partsUnder
                { current with
                    FSharpCore = "FSharp.Core@10.1.404.0/other" }
        )

    let fshw =
        AnalyzeRecords.keyWith (
            partsUnder
                { current with
                    Fshw = "FsHotWatch@9.9.9.9/another-build" }
        )

    test <@ fcs.Value <> key.Value @>
    test <@ core.Value <> key.Value @>
    test <@ fshw.Value = key.Value @>
    // This process's checker term names FCS and FSharp.Core, never fshw's assembly.
    test <@ not (AnalyzeRecords.checkerIdentity.Contains "FsHotWatch@") @>
    test <@ AnalyzeRecords.checkerIdentity.Contains "FSharp.Compiler.Service@" @>
    test <@ AnalyzeRecords.checkerIdentity.Contains "FSharp.Core@" @>

[<Fact>]
let ``a project-options change re-keys the analysis`` () =
    let options = fixtureOptions ()

    let withDefine =
        { options with
            OtherOptions = Array.append options.OtherOptions [| "--define:EXTRA" |] }

    let root = Path.GetDirectoryName options.ProjectFileName

    test
        <@
            AnalyzeRecords.optionsHashOf root options
            <> AnalyzeRecords.optionsHashOf root withDefine
        @>
    // Same options, same hash: the term is a function of the options alone.
    test <@ AnalyzeRecords.optionsHashOf root options = AnalyzeRecords.optionsHashOf root options @>

// --- the record --------------------------------------------------------------------------

let private storeIn (dir: string) =
    OutcomeStore.Store(
        { Root = Path.Combine(dir, "outcomes")
          Ttl = OutcomeStore.defaultTtl
          MaxBytes = OutcomeStore.defaultMaxBytes
          Log = ignore }
    )

let private finding (root: string) =
    { Message = $"bad thing in %s{root}/src/P.fs"
      Severity = DiagnosticSeverity.Error
      Line = 3
      Column = 4
      Code = Some "FIX-001"
      Detail = Some "detail" }

[<Fact>]
let ``a filed analysis reads back exactly, under another checkout's root`` () =
    withTempDir "analyze-roundtrip" (fun dir ->
        let store = storeIn dir
        let key = AnalyzeRecords.keyWith goldenParts
        let a = Path.Combine(dir, "a")
        let b = Path.Combine(dir, "b")
        test <@ AnalyzeRecords.lookup store a key = None @>
        test <@ AnalyzeRecords.file store a "src/P.fs" key [ finding a ] @>
        test <@ AnalyzeRecords.lookup store a key = Some [ finding a ] @>
        // A path under the filing root is named relatively, so a sibling reads its own.
        test <@ AnalyzeRecords.lookup store b key = Some [ finding b ] @>
        // A finding files the record as a fail: a cached red is never read as a pass.
        test <@ (store.Lookup key).Value.Outcome = OutcomeStore.Outcome.Fail @>)

[<Fact>]
let ``a clean analysis files a pass with no entries`` () =
    withTempDir "analyze-clean" (fun dir ->
        let store = storeIn dir
        let key = AnalyzeRecords.keyWith goldenParts
        test <@ AnalyzeRecords.file store dir "src/P.fs" key [] @>
        test <@ AnalyzeRecords.lookup store dir key = Some [] @>
        test <@ (store.Lookup key).Value.Outcome = OutcomeStore.Outcome.Pass @>)

[<Fact>]
let ``a corrupt analyze record is a miss where it is read, and is quarantined`` () =
    withTempDir "analyze-corrupt" (fun dir ->
        let store = storeIn dir
        let key = AnalyzeRecords.keyWith goldenParts
        test <@ AnalyzeRecords.file store dir "src/P.fs" key [ finding dir ] @>
        let filed = Directory.GetFiles(store.DirectoryOf key, "*.json") |> Array.exactlyOne
        File.WriteAllText(filed, "{ not a record")
        // The key-only probe says held; it reads nothing.
        test <@ AnalyzeRecords.mayAnswerLocally (Some store) (Some key) @>
        test <@ AnalyzeRecords.lookup store dir key = None @>
        test <@ not (File.Exists filed) @>
        test <@ Directory.GetFiles(Path.Combine(store.Root, "quarantine")).Length = 1 @>)

[<Fact>]
let ``outputs of another schema are a miss`` () =
    test <@ AnalyzeRecords.tryOfJson "/r" """{"schema":"fshw-analyze-v0","entries":[]}""" = None @>
    test <@ AnalyzeRecords.tryOfJson "/r" """{"schema":"fshw-analyze-v1"}""" = None @>
    test <@ AnalyzeRecords.tryOfJson "/r" "not json" = None @>

[<Fact>]
let ``fileIfAbsent files once per key`` () =
    withTempDir "analyze-once" (fun dir ->
        let store = storeIn dir
        let key = AnalyzeRecords.keyWith goldenParts
        test <@ AnalyzeRecords.fileIfAbsent store dir "src/P.fs" key [] @>
        test <@ not (AnalyzeRecords.fileIfAbsent store dir "src/P.fs" key []) @>
        test <@ (store.History key).Length = 1 @>)

[<Fact>]
let ``the shadow comparison names the first field that differs`` () =
    let entry = finding "/r"
    test <@ AnalyzeRecords.disagreement [ entry ] [ entry ] = None @>
    test <@ AnalyzeRecords.disagreement [] [] = None @>
    test <@ AnalyzeRecords.disagreement [ entry ] [] = Some "entry count: record 1, live 0" @>

    test
        <@
            AnalyzeRecords.disagreement
                [ entry ]
                [ { entry with
                      Severity = DiagnosticSeverity.Warning } ] = Some "entry 0 severity: record error, live warning"
        @>

    test <@ AnalyzeRecords.disagreement [ entry ] [ { entry with Line = 9 } ] = Some "entry 0 line: record 3, live 9" @>

    test
        <@ AnalyzeRecords.disagreement [ entry ] [ { entry with Column = 9 } ] = Some "entry 0 column: record 4, live 9" @>

    test
        <@
            AnalyzeRecords.disagreement [ entry ] [ { entry with Code = None } ] = Some
                "entry 0 code: record FIX-001, live (none)"
        @>

    test
        <@ (AnalyzeRecords.disagreement [ entry ] [ { entry with Message = "m" } ]).Value.StartsWith "entry 0 message:" @>

    test
        <@ (AnalyzeRecords.disagreement [ entry ] [ { entry with Detail = None } ]).Value.StartsWith "entry 0 detail:" @>

[<Fact>]
let ``the mode parses off, shadow and on, and nothing else`` () =
    test <@ AnalyzeRecords.RecordMode.tryParse "off" = Some AnalyzeRecords.Off @>
    test <@ AnalyzeRecords.RecordMode.tryParse "shadow" = Some AnalyzeRecords.Shadow @>
    test <@ AnalyzeRecords.RecordMode.tryParse "on" = Some AnalyzeRecords.On @>
    test <@ AnalyzeRecords.RecordMode.tryParse "yes" = None @>
    test <@ AnalyzeRecords.RecordMode.Default = AnalyzeRecords.Shadow @>

[<Fact>]
let ``the analyzer SDK is named by release, never by build`` () =
    let version = Version(0, 39, 2, 0)

    test
        <@
            AnalyzeRecords.sdkIdentityOf "FSharp.Analyzers.SDK" version (Some "0.39.2+abc123") = "FSharp.Analyzers.SDK@0.39.2"
        @>

    test <@ AnalyzeRecords.sdkIdentityOf "FSharp.Analyzers.SDK" version None = "FSharp.Analyzers.SDK@0.39.2.0" @>
    test <@ AnalyzeRecords.analyzerSdkIdentity.StartsWith "FSharp.Analyzers.SDK@" @>
    test <@ not (AnalyzeRecords.analyzerSdkIdentity.Contains "+") @>

[<Fact>]
let ``an entry with no code and no detail reads back as it was filed`` () =
    let plain =
        { Message = "plain"
          Severity = DiagnosticSeverity.Hint
          Line = 1
          Column = 0
          Code = None
          Detail = None }

    test <@ AnalyzeRecords.tryOfJson "/r" (AnalyzeRecords.toJson "/r" [ plain ]) = Some [ plain ] @>

[<Fact>]
let ``an entry that does not read is a miss, never a partial answer`` () =
    let entries (row: string) =
        $"""{{"schema":"fshw-analyze-v1","entries":[{row}]}}"""

    // A null message, an unknown severity, a field of the wrong type, a missing field.
    test
        <@ AnalyzeRecords.tryOfJson "/r" (entries """{"message":null,"severity":"error","line":1,"column":0}""") = None @>

    test
        <@ AnalyzeRecords.tryOfJson "/r" (entries """{"message":"m","severity":"loud","line":1,"column":0}""") = None @>

    test <@ AnalyzeRecords.tryOfJson "/r" (entries """{"message":1,"severity":"error","line":1,"column":0}""") = None @>
    test <@ AnalyzeRecords.tryOfJson "/r" (entries """{"message":"m","severity":"error","column":0}""") = None @>

    test
        <@
            AnalyzeRecords.tryOfJson "/r" (entries """{"message":"m","severity":"error","line":"one","column":0}""") = None
        @>

[<Fact>]
let ``every mode names itself as it is configured`` () =
    for mode in [ AnalyzeRecords.Off; AnalyzeRecords.Shadow; AnalyzeRecords.On ] do
        test <@ AnalyzeRecords.RecordMode.tryParse (AnalyzeRecords.RecordMode.toString mode) = Some mode @>

/// AUTOMATION-1583's per-kind fair share, applied to the new kind with no new rule: a store
/// over budget because of another kind keeps every `analyze` record within its share.
[<Fact>]
let ``GC gives the analyze kind its fair share`` () =
    withTempDir "analyze-gc" (fun dir ->
        let probe = storeIn dir

        let analyzeKeys =
            [ for i in 1..5 ->
                  AnalyzeRecords.keyWith
                      { goldenParts with
                          File = $"repo:src/F%d{i}.fs" } ]

        for key in analyzeKeys do
            test <@ AnalyzeRecords.file probe dir "src/F.fs" key [] @>

        let analyzeBytes =
            analyzeKeys
            |> List.sumBy (fun key ->
                Directory.GetFiles(probe.DirectoryOf key)
                |> Array.sumBy (fun f -> FileInfo(f).Length))

        // Another kind, much larger, pushes the store over a budget that holds the analyses
        // several times over.
        let big = String('x', 4096)

        for i in 1..40 do
            let key = ActionKey.create "check" [ "file", $"F%d{i}.fs" ]

            probe.RecordWithOutputs(
                OutcomeStore.Record.ofAction
                    key
                    $"F%d{i}.fs"
                    OutcomeStore.Outcome.Pass
                    "test"
                    dir
                    (DateTimeOffset.UtcNow.AddMinutes(float -i)),
                Some $"{{\"blob\":\"%s{big}\"}}"
            )
            |> ignore

        let store =
            OutcomeStore.Store(
                { Root = probe.Root
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = analyzeBytes * 4L
                  Log = ignore }
            )

        let evicted = store.Gc(DateTimeOffset.UtcNow, Set.empty)
        test <@ not evicted.IsEmpty @>
        test <@ analyzeKeys |> List.forall (fun key -> (store.Lookup key).IsSome) @>)

// --- the plugin ---------------------------------------------------------------------------

/// A replayed check of `rel` under `root` with `source`: no FCS objects, its filed
/// signatures those of the shared fixture's real check, which a demand hands back.
let private replayedAt (root: string) (rel: string) (source: string) : FileCheckResult =
    let full = fixtureFullCheck ()
    let options = fixtureOptions ()

    { fakeFileCheckResult (Path.Combine(root, rel)) with
        Source = source
        ProjectOptions = options
        CheckResults =
            Replayed
                { Diagnostics = []
                  FcsSignature = CheckCache.fcsCheckSignature full
                  UsedSignatures = CacheInputs.usedSignaturesHash (Some root) options full
                  ApiSignature = None } }

let private liveOf (replayed: FileCheckResult) =
    { replayed with
        CheckResults = fixtureFullCheck () }

/// One Error finding per line of a source that contains "BAD".
let private findingsFor (source: string) : FSharp.Analyzers.SDK.Message list =
    if source.Contains "BAD" then
        [ { Type = "Fixture"
            Message = "BAD is bad"
            Code = "FIX-BAD"
            Severity = FSharp.Analyzers.SDK.Severity.Error
            Range =
              FSharp.Compiler.Text.Range.mkRange
                  "Fixture.fs"
                  (FSharp.Compiler.Text.Position.mkPos 1 0)
                  (FSharp.Compiler.Text.Position.mkPos 1 3)
            Fixes = [] } ]
    else
        []

/// What one daemon start did.
type private Start =
    { Analyzed: string list
      Demanded: string list
      Entries: Map<string, ErrorEntry list>
      Log: string list }

/// The box-wide state daemons at any tree share: the task-cache directory and the
/// outcome store.
type private Box =
    { TaskCacheDir: string
      StoreRoot: string }

/// The box with a task cache of its own: a workspace whose entry for each file is gone
/// (a fresh checkout, or one whose entries another tree evicted), so only the records can
/// answer it.
let private withOwnTaskCache (dir: string) (name: string) (box: Box) =
    { box with
        TaskCacheDir = Path.Combine(dir, name, "tasks") }

let private boxIn (dir: string) =
    { TaskCacheDir = Path.Combine(dir, "box", "tasks")
      StoreRoot = Path.Combine(dir, "box", "outcomes") }

/// A daemon start at `root` over the tree `files` (repo-relative path → source): a new task
/// cache over the box's directory (which reads the siblings on disk, as a daemon does), a
/// new handler, every file handed over replayed from its check record.
let private startWith
    (asEvent: FileCheckResult -> FileCheckResult)
    (box: Box)
    (mode: AnalyzeRecords.RecordMode)
    (root: string)
    (files: (string * string) list)
    =
    let analyzed = Collections.Concurrent.ConcurrentQueue<string>()
    let demanded = Collections.Concurrent.ConcurrentQueue<string>()
    let log = Collections.Concurrent.ConcurrentQueue<string>()

    let sources =
        files |> List.map (fun (rel, src) -> Path.Combine(root, rel), src) |> Map.ofList

    let cache =
        FsHotWatch.FileTaskCache.FileTaskCache(box.TaskCacheDir, repoRoot = root) :> FsHotWatch.TaskCache.ITaskCache

    let host = PluginHost(Unchecked.defaultof<_>, root, taskCache = cache)
    host.WorkStore.PublishProjectModel fixtureModel

    host.SetCheckDemand(fun replay ->
        async {
            demanded.Enqueue(Path.GetRelativePath(root, AbsFilePath.value replay.File))
            return Some(liveOf replay)
        })

    let runner: AnalyzerRunner =
        fun _clients context ->
            async {
                analyzed.Enqueue(Path.GetRelativePath(root, context.FileName))

                return
                    [ { AnalyzerName = "Fixture"
                        Output = Result.Ok(findingsFor sources[context.FileName]) } ]
            }

    let store =
        OutcomeStore.Store(
            { Root = box.StoreRoot
              Ttl = OutcomeStore.defaultTtl
              MaxBytes = OutcomeStore.defaultMaxBytes
              Log = log.Enqueue }
        )

    let wiring: AnalyzeRecords.Wiring =
        { Store = store
          RecordMode = mode
          Toolchain = "test-toolchain"
          Log = log.Enqueue
          Trace = log.Enqueue }

    host.RegisterHandler(
        createRecordedWithSeams (Some wiring) (Some root) [] Map.empty None DiagnosticSeverity.Warning None runner
    )

    try
        for rel, source in files do
            let before = committedBy host "analyzers"
            host.EmitFileChecked(asEvent (replayedAt root rel source))
            test <@ waitForCommitted host "analyzers" before 1L 15000 @>

        waitForQuiescent host 15000

        { Analyzed = List.ofSeq analyzed |> List.sort
          Demanded = List.ofSeq demanded |> List.sort
          Entries =
            host.GetErrorsByPlugin "analyzers"
            |> Map.toList
            |> List.map (fun (file, entries) -> Path.GetRelativePath(root, file), entries)
            |> Map.ofList
          Log = List.ofSeq log }
    finally
        host.Teardown()

/// `startWith`, every file handed over replayed from its check record.
let private startAt = startWith id

/// `startWith`, every file handed over as a live check.
let private startLiveAt = startWith liveOf

/// The records of `kind` the box's store holds, per subject.
let private copiesBySubject (box: Box) =
    (OutcomeStore.readAll box.StoreRoot).Records
    |> List.filter (fun r -> r.Key.Kind = AnalyzeRecords.Kind)
    |> List.countBy _.Subject
    |> Map.ofList

let private unchanged =
    [ "src/A.fs", "module A\nlet a = 1\n"
      "src/B.fs", "module B\nlet b = BAD\n"
      "src/C.fs", "module C\nlet c = 3\n"
      "src/D.fs", "module D\nlet d = 4\n" ]

/// T1 and T2 share A..D and differ in E (a body edit) and F (a fix of a finding).
let private t1 =
    unchanged
    @ [ "src/E.fs", "module E\nlet e = 5\n"; "src/F.fs", "module F\nlet f = BAD\n" ]

let private t2 =
    unchanged
    @ [ "src/E.fs", "module E\nlet e = 55\n"; "src/F.fs", "module F\nlet f = 6\n" ]

/// E3 (the acceptance test): daemons start alternately at two trees, X at T1 and Y at T2,
/// sharing one box. After each tree has been analysed once, a further start at either
/// tree analyses nothing and demands nothing: every key either tree needs is filed. Before
/// AUTOMATION-1585 each start at the other tree re-analysed the files the trees differ in
/// (E and F here), because the one task-cache entry per file was the other tree's.
[<Fact(Timeout = 120000)>]
let ``E3: alternating starts between two trees re-analyse nothing once both have run`` () =
    withTempDir "analyze-e3" (fun dir ->
        let box = boxIn dir
        let x = Path.Combine(dir, "x")
        let y = Path.Combine(dir, "y")

        let first = startAt box AnalyzeRecords.On x t1
        test <@ first.Analyzed = (t1 |> List.map fst |> List.sort) @>

        // Y differs from T1 in E and F only: the rest is answered from the box.
        let second = startAt box AnalyzeRecords.On y t2
        test <@ second.Analyzed = [ "src/E.fs"; "src/F.fs" ] @>

        let third = startAt box AnalyzeRecords.On x t1
        let fourth = startAt box AnalyzeRecords.On y t2

        test <@ List.isEmpty third.Analyzed @>
        test <@ List.isEmpty third.Demanded @>
        test <@ List.isEmpty fourth.Analyzed @>
        test <@ List.isEmpty fourth.Demanded @>

        // The replayed answers are the analyses: T1's finding in F replays red at X, and
        // T2's fix of it replays clean at Y.
        test
            <@
                third.Entries
                |> Map.tryFind "src/F.fs"
                |> Option.exists (List.exists (fun e -> e.Severity = DiagnosticSeverity.Error))
            @>

        test
            <@
                third.Entries
                |> Map.tryFind "src/B.fs"
                |> Option.exists (List.exists (fun e -> e.Severity = DiagnosticSeverity.Error))
            @>

        test
            <@
                fourth.Entries
                |> Map.tryFind "src/F.fs"
                |> Option.defaultValue []
                |> List.isEmpty
            @>

        // One record per (file, key): each analysis was filed once, never again.
        let copies = copiesBySubject box
        test <@ copies |> Map.forall (fun _ n -> n = 1 || n = 2) @>
        test <@ copies["src/E.fs"] = 2 && copies["src/F.fs"] = 2 && copies["src/A.fs"] = 1 @>

        // A fresh workspace at an already-analysed tree re-analyses nothing.
        let fresh =
            startAt (withOwnTaskCache dir "z" box) AnalyzeRecords.On (Path.Combine(dir, "z")) t1

        test <@ List.isEmpty fresh.Analyzed @>
        test <@ List.isEmpty fresh.Demanded @>)

/// The positive control for E3: without the records (`Off`), the same alternation
/// re-analyses the two files the trees differ in on every start after the first two.
[<Fact(Timeout = 120000)>]
let ``E3 control: with analyze records off the alternation thrashes`` () =
    withTempDir "analyze-e3-off" (fun dir ->
        let box = boxIn dir
        let x = Path.Combine(dir, "x")
        let y = Path.Combine(dir, "y")
        startAt box AnalyzeRecords.Off x t1 |> ignore
        startAt box AnalyzeRecords.Off y t2 |> ignore
        let third = startAt box AnalyzeRecords.Off x t1
        test <@ third.Analyzed = [ "src/E.fs"; "src/F.fs" ] @>
        test <@ third.Demanded = [ "src/E.fs"; "src/F.fs" ] @>
        test <@ copiesBySubject box |> Map.isEmpty @>)

/// A cached red is never answered green: a finding replayed from a record lands in the
/// ledger as the Error the live analysis reported, with no analysis and no demand.
[<Fact(Timeout = 120000)>]
let ``a replayed analysis carrying a finding stays red`` () =
    withTempDir "analyze-red" (fun dir ->
        let box = boxIn dir
        let tree = [ "src/B.fs", "module B\nlet b = BAD\n" ]
        let live = startAt box AnalyzeRecords.On (Path.Combine(dir, "x")) tree

        let replay =
            startAt (withOwnTaskCache dir "y" box) AnalyzeRecords.On (Path.Combine(dir, "y")) tree

        test <@ live.Analyzed = [ "src/B.fs" ] @>
        test <@ List.isEmpty replay.Analyzed && List.isEmpty replay.Demanded @>
        test <@ replay.Entries["src/B.fs"] = live.Entries["src/B.fs"] @>

        test
            <@
                replay.Entries["src/B.fs"]
                |> List.exists (fun e -> e.Severity = DiagnosticSeverity.Error)
            @>)

/// AUTOMATION-1609's rule for the new kind: a workspace whose task cache answers every file
/// still leaves every analysis filed, so a fresh workspace demands nothing. Seed: the box's
/// records are lost while the task cache stays warm.
[<Fact(Timeout = 120000)>]
let ``a task-cache-warm workspace still files every analysis, so a fresh workspace demands none`` () =
    withTempDir "analyze-1609" (fun dir ->
        let box = boxIn dir
        let x = Path.Combine(dir, "x")
        startAt box AnalyzeRecords.Off x t1 |> ignore
        test <@ copiesBySubject box |> Map.isEmpty @>

        // The task cache answers every file at X, but the store holds none of them, so the
        // cache may not answer: each file is analysed once more, and filed.
        let warm = startAt box AnalyzeRecords.On x t1
        test <@ warm.Analyzed = (t1 |> List.map fst |> List.sort) @>
        test <@ copiesBySubject box = (t1 |> List.map (fun (rel, _) -> rel, 1) |> Map.ofList) @>

        // Filed once: the next warm start is answered by its cache and files nothing more.
        let again = startAt box AnalyzeRecords.On x t1
        test <@ List.isEmpty again.Analyzed && List.isEmpty again.Demanded @>
        test <@ copiesBySubject box = (t1 |> List.map (fun (rel, _) -> rel, 1) |> Map.ofList) @>

        let fresh =
            startAt (withOwnTaskCache dir "z" box) AnalyzeRecords.On (Path.Combine(dir, "z")) t1

        test <@ List.isEmpty fresh.Analyzed && List.isEmpty fresh.Demanded @>)

/// A corrupt record is a miss where it is read: quarantined, demanded live, analysed and
/// filed again.
[<Fact(Timeout = 120000)>]
let ``a corrupt analyze record is re-analysed where it is read`` () =
    withTempDir "analyze-corrupt-plugin" (fun dir ->
        let box = boxIn dir
        let tree = [ "src/B.fs", "module B\nlet b = BAD\n" ]
        startAt box AnalyzeRecords.On (Path.Combine(dir, "x")) tree |> ignore

        let key = ((OutcomeStore.readAll box.StoreRoot).Records |> List.exactlyOne).Key

        let store =
            OutcomeStore.Store(
                { Root = box.StoreRoot
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        let filed = Directory.GetFiles(store.DirectoryOf key, "*.json") |> Array.exactlyOne

        File.WriteAllText(filed, "{ not a record")

        let fresh =
            startAt (withOwnTaskCache dir "y" box) AnalyzeRecords.On (Path.Combine(dir, "y")) tree

        test <@ fresh.Demanded = [ "src/B.fs" ] @>
        test <@ fresh.Analyzed = [ "src/B.fs" ] @>
        test <@ Directory.GetFiles(Path.Combine(box.StoreRoot, "quarantine")).Length = 1 @>
        test <@ copiesBySubject box = Map.ofList [ "src/B.fs", 1 ] @>)

/// Shadow: the record never replaces the live analysis. A replayed check is demanded and
/// analysed live, the record it would have answered with is compared field by field, and
/// the comparison is logged.
[<Fact(Timeout = 120000)>]
let ``shadow analyses live, compares with the record and logs agreement`` () =
    withTempDir "analyze-shadow" (fun dir ->
        let box = boxIn dir
        let tree = [ "src/B.fs", "module B\nlet b = BAD\n" ]
        let first = startAt box AnalyzeRecords.Shadow (Path.Combine(dir, "x")) tree
        test <@ first.Analyzed = [ "src/B.fs" ] @>
        test <@ copiesBySubject box = Map.ofList [ "src/B.fs", 1 ] @>

        let second =
            startAt (withOwnTaskCache dir "y" box) AnalyzeRecords.Shadow (Path.Combine(dir, "y")) tree

        test <@ second.Analyzed = [ "src/B.fs" ] && second.Demanded = [ "src/B.fs" ] @>

        test
            <@
                second.Log
                |> List.exists (fun line -> line.Contains "agree" && line.Contains "src/B.fs")
            @>

        test <@ second.Log |> List.forall (fun line -> not (line.Contains "disagree")) @>
        test <@ copiesBySubject box = Map.ofList [ "src/B.fs", 1 ] @>)

[<Fact(Timeout = 120000)>]
let ``shadow logs a disagreement with the field that differs`` () =
    withTempDir "analyze-shadow-disagree" (fun dir ->
        let box = boxIn dir
        let tree = [ "src/B.fs", "module B\nlet b = BAD\n" ]
        startAt box AnalyzeRecords.Shadow (Path.Combine(dir, "x")) tree |> ignore

        // Rewrite the record's finding as a warning, as if an analysis had changed under an
        // unchanged key: the shadow must say so, and still act on the live answer.
        let store =
            OutcomeStore.Store(
                { Root = box.StoreRoot
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        let record, outputs =
            (OutcomeStore.readAll box.StoreRoot).Records
            |> List.exactlyOne
            |> fun r -> r, (store.LookupWithOutputs r.Key).Value |> snd

        let filed =
            Directory.GetFiles(store.DirectoryOf record.Key, "*.json") |> Array.exactlyOne

        File.Delete filed

        store.RecordWithOutputs(record, outputs |> Option.map (fun text -> text.Replace("\"error\"", "\"warning\"")))
        |> ignore

        let second =
            startAt (withOwnTaskCache dir "y" box) AnalyzeRecords.Shadow (Path.Combine(dir, "y")) tree


        test
            <@
                second.Log
                |> List.exists (fun line ->
                    line.Contains "disagree"
                    && line.Contains "src/B.fs"
                    && line.Contains "severity: record warning, live error")
            @>

        test
            <@
                second.Entries["src/B.fs"]
                |> List.exists (fun e -> e.Severity = DiagnosticSeverity.Error)
            @>)

/// AUTOMATION-1596: a live analysis files only on a miss. An analysis answered by the task
/// cache or by a record files nothing new.
[<Fact(Timeout = 120000)>]
let ``duplicate filing does not happen`` () =
    withTempDir "analyze-dup" (fun dir ->
        let box = boxIn dir
        let x = Path.Combine(dir, "x")
        startAt box AnalyzeRecords.On x t1 |> ignore
        startAt box AnalyzeRecords.On x t1 |> ignore

        startAt (withOwnTaskCache dir "y" box) AnalyzeRecords.On (Path.Combine(dir, "y")) t1
        |> ignore

        startAt (withOwnTaskCache dir "z" box) AnalyzeRecords.Shadow (Path.Combine(dir, "z")) t1
        |> ignore

        test <@ copiesBySubject box = (t1 |> List.map (fun (rel, _) -> rel, 1) |> Map.ofList) @>)

/// A LIVE check whose analysis is filed is answered from the record too, when records are
/// on: the analyzers do not run, and nothing is filed again. In shadow the analysis runs.
[<Fact(Timeout = 120000)>]
let ``a live check whose analysis is filed is answered from the record when records are on`` () =
    withTempDir "analyze-live" (fun dir ->
        let box = boxIn dir
        let tree = [ "src/B.fs", "module B\nlet b = BAD\n" ]
        let first = startLiveAt box AnalyzeRecords.On (Path.Combine(dir, "x")) tree
        test <@ first.Analyzed = [ "src/B.fs" ] @>

        let answered =
            startLiveAt (withOwnTaskCache dir "y" box) AnalyzeRecords.On (Path.Combine(dir, "y")) tree

        test <@ List.isEmpty answered.Analyzed @>
        test <@ answered.Entries["src/B.fs"] = first.Entries["src/B.fs"] @>

        let shadowed =
            startLiveAt (withOwnTaskCache dir "z" box) AnalyzeRecords.Shadow (Path.Combine(dir, "z")) tree

        test <@ shadowed.Analyzed = [ "src/B.fs" ] @>
        test <@ copiesBySubject box = Map.ofList [ "src/B.fs", 1 ] @>)

/// Records need a repository root to name paths under: a handler without one files nothing
/// and guards nothing, whatever its mode.
[<Fact(Timeout = 120000)>]
let ``a handler with no repository root files no record`` () =
    withTempDir "analyze-noroot" (fun dir ->
        let store =
            OutcomeStore.Store(
                { Root = Path.Combine(dir, "outcomes")
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        let wiring: AnalyzeRecords.Wiring =
            { Store = store
              RecordMode = AnalyzeRecords.On
              Toolchain = "t"
              Log = ignore
              Trace = ignore }

        let host = createModelHost (Unchecked.defaultof<_>) dir
        let analyzed = Collections.Concurrent.ConcurrentQueue<string>()

        let runner: AnalyzerRunner =
            fun _ context ->
                async {
                    analyzed.Enqueue context.FileName
                    return []
                }

        host.RegisterHandler(
            createRecordedWithSeams (Some wiring) None [] Map.empty None DiagnosticSeverity.Warning None runner
        )

        try
            let before = committedBy host "analyzers"
            host.EmitFileChecked(liveOf (replayedAt dir "src/A.fs" "module A"))
            test <@ waitForCommitted host "analyzers" before 1L 15000 @>
            waitForQuiescent host 15000
            test <@ analyzed.Count = 1 @>
            test <@ (OutcomeStore.readAll store.Root).Records.IsEmpty @>
        finally
            host.Teardown())
