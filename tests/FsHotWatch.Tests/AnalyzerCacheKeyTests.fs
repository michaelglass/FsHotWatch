/// the analyzers cache must never replay a verdict a fresh run would not
/// reach, and must still hit when nothing that decides the verdict changed.
///
/// Each MISS test holds every other input fixed and changes ONE thing that decides what
/// the gate concludes. Each HIT control changes nothing that decides it, so the fix
/// cannot pass by turning the cache off.
module FsHotWatch.Tests.AnalyzerCacheKeyTests

open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.Symbols
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TaskCache
open FsHotWatch.Analyzers.AnalyzersPlugin
open FsHotWatch.Tests.TestHelpers

let private keyFor (repoRoot: string option) (threshold: DiagnosticSeverity) (result: FileCheckResult) =
    let handler = create repoRoot [] None threshold
    handler.CacheKey.Value handler.Init (FileChecked result)

/// A file under `root/src` whose project lists only it.
let private resultIn (root: string) =
    let file = Path.Combine(root, "src", "A.fs")
    Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
    File.WriteAllText(file, "let x = 1\n")

    { fakeFileCheckResult file with
        Source = "let x = 1\n" }

/// Write under `a`'s key in a store, then look it up under `b`'s — the framework's own
/// hit-or-miss decision, not an inference from two hashes.
let private lookupAcross (a: string) (keyA: ContentHash) (b: string) (keyB: ContentHash) (file: string) =
    withTempDir "store" (fun store ->
        let composite root =
            { Plugin = "analyzers"
              File = Some(CachePathIdentity.keyOf (Some root) (Path.Combine(root, file))) }

        let entry =
            { CacheKey = keyA
              Errors = []
              Status = CachedFileCompleted(System.TimeSpan.FromMilliseconds 1.0)
              EmittedEvents = [] }

        (FileTaskCache.FileTaskCache(store, repoRoot = a) :> ITaskCache).Set (composite a) keyA entry
        (FileTaskCache.FileTaskCache(store, repoRoot = b) :> ITaskCache).Lookup (composite b) keyB)

// ---------------------------------------------------------------------------
// fail-on-severity: entries are stored after promotion
// ---------------------------------------------------------------------------

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the analyzers key differs when failOnSeverity changes what a finding means`` () =
    withTempDir "severity" (fun root ->
        let result = resultIn root

        // The SAME Hint finding, as each daemon stores it in the cache entry ...
        let hint =
            { ErrorEntry.error "unbounded read" with
                Severity = DiagnosticSeverity.Hint }
        // ... one of which fails the gate and one of which does not.
        test <@ not (ErrorEntry.isFailing false (promoteIfFailing DiagnosticSeverity.Error hint)) @>
        test <@ ErrorEntry.isFailing false (promoteIfFailing DiagnosticSeverity.Hint hint) @>

        test
            <@
                keyFor (Some root) DiagnosticSeverity.Error result
                <> keyFor (Some root) DiagnosticSeverity.Hint result
            @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an unchanged failOnSeverity still hits`` () =
    withTempDir "severity-hit" (fun root ->
        let result = resultIn root
        let key = keyFor (Some root) DiagnosticSeverity.Warning result
        test <@ key.IsSome @>

        match
            lookupAcross root key.Value root (keyFor (Some root) DiagnosticSeverity.Warning result).Value "src/A.fs"
        with
        | CacheHit _ -> ()
        | CacheMiss reason -> failwith $"expected a hit, got %A{reason}")

// ---------------------------------------------------------------------------
// analyzer-config: files analyzers discover by walking up
// ---------------------------------------------------------------------------

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the analyzers key differs when the editorconfig the analyzers read differs`` () =
    withTempDir "ec" (fun root ->
        let result = resultIn root
        let editorconfig = Path.Combine(root, ".editorconfig")

        File.WriteAllText(editorconfig, "[*.fs]\nmga_error_reporting_functions = captureError, logError\n")
        let before = keyFor (Some root) DiagnosticSeverity.Hint result
        // MichaelGlass.FSharp.Analyzers' ErrorReportingAnalyzer now reports every
        // try/with that only called logError.
        File.WriteAllText(editorconfig, "[*.fs]\nmga_error_reporting_functions = captureError\n")
        let after = keyFor (Some root) DiagnosticSeverity.Hint result

        test <@ before.IsSome @>
        test <@ before <> after @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the analyzers key differs when a nested editorconfig or fsharplint json appears`` () =
    withTempDir "config-nested" (fun root ->
        let result = resultIn root
        let bare = keyFor (Some root) DiagnosticSeverity.Hint result
        File.WriteAllText(Path.Combine(root, "src", ".editorconfig"), "[*.fs]\nmga_banned_functions = printfn\n")
        let withEditorConfig = keyFor (Some root) DiagnosticSeverity.Hint result
        File.WriteAllText(Path.Combine(root, "fsharplint.json"), """{ "conventions": {} }""")
        let withLintConfig = keyFor (Some root) DiagnosticSeverity.Hint result

        test <@ bare.IsSome @>
        test <@ bare <> withEditorConfig @>
        test <@ withEditorConfig <> withLintConfig @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``identical analyzer config in a second checkout still hits`` () =
    withTempDir "config-hit-a" (fun a ->
        withTempDir "config-hit-b" (fun b ->
            for root in [ a; b ] do
                File.WriteAllText(Path.Combine(root, ".editorconfig"), "[*.fs]\nmga_banned_functions = printfn\n")
                File.WriteAllText(Path.Combine(root, "fsharplint.json"), "{}")

            let keyA = keyFor (Some a) DiagnosticSeverity.Hint (resultIn a)
            let keyB = keyFor (Some b) DiagnosticSeverity.Hint (resultIn b)
            test <@ keyA.IsSome && keyB.IsSome @>

            match lookupAcross a keyA.Value b keyB.Value "src/A.fs" with
            | CacheHit _ -> ()
            | CacheMiss reason -> failwith $"expected a cross-checkout hit, got %A{reason}"))

// ---------------------------------------------------------------------------
// used-signatures: the shape of what this file resolved upstream (real FCS)
// ---------------------------------------------------------------------------

/// Check `Down.fs` against `Up.fs` laid out as `layout`. Returns the result the analyzers
/// plugin receives, the check's diagnostics, and what a typed analyzer reads about `Up.T`:
/// is it a union?
let private checkDown (root: string) (layout: UpstreamLayout) (upSource: string) (downSource: string) =
    let result, check = checkDownstream root layout upSource downSource

    // What a typed analyzer asks (cf. a wildcard-match rule's union lookup): is the type
    // this file's values have a union? Read off the typed tree, the way analyzers do.
    let rec exprTypes (e: FSharpExpr) =
        seq {
            yield e.Type

            for child in e.ImmediateSubExpressions do
                yield! exprTypes child
        }

    let rec declTypes (d: FSharpImplementationFileDeclaration) =
        match d with
        | FSharpImplementationFileDeclaration.Entity(_, ds) -> Seq.collect declTypes ds
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(v, _, body) ->
            Seq.append [ v.FullType ] (exprTypes body)
        | FSharpImplementationFileDeclaration.InitAction e -> exprTypes e

    let rec named (t: FSharpType) =
        seq {
            if t.HasTypeDefinition then
                yield t.TypeDefinition

            for a in t.GenericArguments do
                yield! named a
        }

    let tIsUnion =
        check.ImplementationFile.Value.Declarations
        |> Seq.collect declTypes
        |> Seq.collect named
        |> Seq.pick (fun e -> if e.DisplayName = "T" then Some e.IsFSharpUnion else None)

    result, check.Diagnostics, tIsUnion

let private record = "module Up\ntype T = { X: int }\nlet make () = { X = 1 }\n"
let private union = "module Up\ntype T = | X of int\nlet make () = X 1\n"

/// Names `Up.T` in an annotation.
let private naming = "module Down\nlet describe (t: Up.T) = string t\n"

/// Never names `Up.T`: it reaches the file only as the inferred type of `v`.
let private inferring =
    "module Down\nlet v = Up.make ()\nlet describe () = string v\n"

/// Key `Down.fs` checked in `rootA` against `upA` and in `rootB` against `upB`, and
/// look B's key up in a store holding A's entry.
let private crossCheck layout downSource (upA: string) (upB: string) =
    withTempDir "xfile-a" (fun rootA ->
        withTempDir "xfile-b" (fun rootB ->
            let resultA, diagsA, unionA = checkDown rootA layout upA downSource
            let resultB, diagsB, unionB = checkDown rootB layout upB downSource
            let keyA = keyFor (Some rootA) DiagnosticSeverity.Hint resultA
            let keyB = keyFor (Some rootB) DiagnosticSeverity.Hint resultB
            test <@ keyA.IsSome && keyB.IsSome @>
            // Every pair compiles cleanly: the diagnostics cannot tell them apart.
            test <@ Array.isEmpty diagsA && Array.isEmpty diagsB @>

            (unionA, unionB), lookupAcross rootA keyA.Value rootB keyB.Value "Down.fs"))

/// Same file bytes, different typed answer — and a miss naming only `used-signatures`.
let private assertShapeChangeMisses layout downSource =
    let (unionA, unionB), outcome = crossCheck layout downSource record union
    test <@ unionA <> unionB @>

    match outcome with
    | CacheHit _ -> failwith "an upstream type that changed shape must never hit"
    | CacheMiss reason -> test <@ reason = CacheMissReason.InputsChanged [ "used-signatures" ] @>

let private assertHits layout downSource upA upB =
    match snd (crossCheck layout downSource upA upB) with
    | CacheHit _ -> ()
    | CacheMiss reason -> failwith $"expected a hit, got %A{reason}"

[<Fact(Timeout = 120000)>]
let ``the analyzers key differs when an earlier file changes the shape of a type this file names`` () =
    assertShapeChangeMisses EarlierFile naming

[<Fact(Timeout = 120000)>]
let ``the analyzers key differs when an earlier file changes the shape of a type this file only infers`` () =
    assertShapeChangeMisses EarlierFile inferring

[<Fact(Timeout = 120000)>]
let ``the analyzers key differs when a referenced project changes the shape of a type this file uses`` () =
    assertShapeChangeMisses ReferencedProject naming

[<Fact(Timeout = 120000)>]
let ``an unchanged upstream in a second checkout still hits`` () =
    assertHits EarlierFile inferring record record

[<Fact(Timeout = 120000)>]
let ``an upstream body-only edit still hits`` () =
    // A different body, a comment and shifted lines: nothing a caller can see.
    let edited =
        "module Up\n// moved down a line\ntype T = { X: int }\nlet make () = { X = 1 + 1 }\n"

    assertHits EarlierFile inferring record edited
    assertHits ReferencedProject inferring record edited

[<Fact(Timeout = 120000)>]
let ``an upstream shape change this file cannot see still hits`` () =
    let withUnusedType = record + "type Unused = { Y: string }\n"
    let changedUnusedType = record + "type Unused = | Y of string\n"
    assertHits EarlierFile inferring withUnusedType changedUnusedType

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a file whose used signatures cannot be described has no analyzers key`` () =
    withTempDir "undescribable" (fun root ->
        // Fault injection: a check result with no state, under no project options, so the
        // walk that describes its used signatures throws.
        let broken =
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof<FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults>
            )
            :?> FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults

        let result =
            { resultIn root with
                CheckResults = FullCheck(fixtureParse (), broken) }
            |> withUndescribableOptions

        test <@ keyFor (Some root) DiagnosticSeverity.Hint result = None @>)

// ---------------------------------------------------------------------------
// the host: what fshw contributes to a key (AUTOMATION-1629, the 1616 rule)
// ---------------------------------------------------------------------------

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the host's terms name the checker build, the analyzer SDK release and the task schema`` () =
    let terms = AnalyzerHost.keyTerms AnalyzerHost.current
    test <@ terms |> List.map fst = [ "plugin-version"; "checker"; "analyzer-sdk" ] @>
    test <@ List.contains ("plugin-version", AnalyzerTaskSchema) terms @>
    // The analyzers read the typed tree FCS built: an FCS or FSharp.Core build can change
    // what they see without moving the file's diagnostics, so the key names it exactly.
    test <@ List.contains ("checker", CheckCache.toolchainIdentity) terms @>
    // The SDK builds the context the analyzers run against; it is named by release, not
    // by module version id, so a rebuild of the same release replays.
    let sdk = terms |> List.find (fst >> (=) "analyzer-sdk") |> snd
    test <@ sdk.StartsWith "FSharp.Analyzers.SDK@" @>
    test <@ not (sdk.Contains "+") && not (sdk.Contains "/") @>
    // fshw's own build is never a term: it moves with every release (AUTOMATION-1616).
    test <@ terms |> List.forall (fun (_, value) -> not (value.Contains "FsHotWatch")) @>

/// A host with every component spelled out, so a key under it is the same on every
/// machine and every build of fshw.
let private hostN: AnalyzerHost =
    { TaskSchema = AnalyzerTaskSchema
      Checker =
        "FSharp.Compiler.Service@43.12.402.0/11111111-1111-1111-1111-111111111111;\
         FSharp.Core@10.0.0.0/22222222-2222-2222-2222-222222222222;fshw-check-schema=golden"
      AnalyzerSdk = "FSharp.Analyzers.SDK@0.39.2" }

let private keyUnder (host: AnalyzerHost) (root: string) (result: FileCheckResult) =
    let handler =
        createHostedWithSeams host None (Some root) [] Map.empty None DiagnosticSeverity.Warning None runSafely

    handler.CacheKey.Value handler.Init (FileChecked result)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an entry filed under one host is not replayed after a schema bump, an FCS or FSharp.Core build, or an SDK release``
    ()
    =
    withTempDir "host-moved" (fun root ->
        let result = resultIn root
        let filed = (keyUnder hostN root result).Value

        // Control: the same host replays, so the misses below are the host's doing.
        match lookupAcross root filed root (keyUnder hostN root result).Value "src/A.fs" with
        | CacheHit _ -> ()
        | CacheMiss reason -> failwith $"expected a hit under an unchanged host, got %A{reason}"

        let moved =
            [ "schema",
              { hostN with
                  TaskSchema = AnalyzerTaskSchema + "-bumped" }
              "FCS",
              { hostN with
                  Checker = hostN.Checker.Replace("43.12.402.0", "43.12.403.0") }
              "FSharp.Core",
              { hostN with
                  Checker = hostN.Checker.Replace("10.0.0.0", "10.0.1.0") }
              "SDK",
              { hostN with
                  AnalyzerSdk = "FSharp.Analyzers.SDK@0.40.0" } ]

        for what, host in moved do
            match lookupAcross root filed root (keyUnder host root result).Value "src/A.fs" with
            | CacheMiss _ -> ()
            | CacheHit _ -> failwith $"an entry filed before the %s{what} change was replayed after it")

/// Fails with the instruction a changed key needs.
let private golden (what: string) (expected: 'T) (actual: 'T) =
    if actual <> expected then
        failwith
            $"%s{what} changed. An analyzers key is a contract with every entry an earlier fshw \
              filed: bump AnalyzerTaskSchema, then update the golden.\nexpected: %A{expected}\nactual:   %A{actual}"

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``golden: the analyzers task-cache key of a fixed file under a fixed host`` () =
    golden
        "The host's key terms"
        [ "plugin-version"; "checker"; "analyzer-sdk" ]
        (AnalyzerHost.keyTerms hostN |> List.map fst)

    golden
        "taskKeyWith"
        "f8a9a2eb5780ba6296fe0bfb57e6a98abd2b94583645e0726b3c377ea0227562"
        (ContentHash.value (taskKeyWith hostN [ "file", "src/A.fs"; "source", "let x = 1\n" ]))

    withTempDir "golden" (fun root ->
        golden
            "The plugin's key for the fixture file"
            "59f16753d53665fc56910dd58c0134bd0c9021989a70bfb78b0c12e014d4a667"
            (ContentHash.value (keyUnder hostN root (resultIn root)).Value))

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an SDK is named by its release, build metadata dropped, or by its assembly version when it states none`` () =
    let version = System.Version(0, 39, 2, 0)
    test <@ AnalyzerHost.sdkIdentityOf "SDK" version (Some "0.39.2+abc123") = "SDK@0.39.2" @>
    test <@ AnalyzerHost.sdkIdentityOf "SDK" version None = "SDK@0.39.2.0" @>
