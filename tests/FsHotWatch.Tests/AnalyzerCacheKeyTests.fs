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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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
