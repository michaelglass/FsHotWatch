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
