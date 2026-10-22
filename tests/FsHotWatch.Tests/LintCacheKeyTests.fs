/// The lint cache must never replay a typed FSharpLint verdict a fresh run would not
/// reach, and must still hit when nothing that decides the verdict changed.
///
/// Each MISS test holds the file's own bytes fixed and changes only the shape of a type it
/// resolved upstream, so a typed rule answers differently while the file still compiles
/// cleanly. Each HIT control changes nothing a caller can see, so the fix cannot pass by
/// turning the cache off.
module FsHotWatch.Tests.LintCacheKeyTests

open System.IO
open Xunit
open Swensen.Unquote
open FSharpLint.Application
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TaskCache
open FsHotWatch.Lint.LintPlugin
open FsHotWatch.Tests.TestHelpers

/// Enables only NoPartialFunctions, a rule that reads the typed tree: it flags `x.Value`
/// when `x` is an option, and not when `x` is a record with a `Value` field.
let private typedRuleConfig =
    """{ "noPartialFunctions": { "enabled": true, "config": { "allowedPartials": [], "additionalPartials": [] } } }"""

let private configIn (root: string) =
    let path = Path.Combine(root, "fsharplint.json")
    File.WriteAllText(path, typedRuleConfig)
    path

let private keyFor (root: string) (result: FileCheckResult) =
    let handler = create (Some root) (Some(configIn root)) None None
    handler.CacheKey.Value handler.Init (FileChecked result)

/// What a fresh lint of `result` reports under `typedRuleConfig`: the rule's warnings.
let private typedLint (root: string) (result: FileCheckResult) =
    let typeCheckResults =
        match result.CheckResults with
        | FullCheck r -> Some r
        | ParseOnly -> None

    let lintParams =
        { Lint.OptionalLintParameters.Default with
            Configuration = Lint.ConfigurationParam.FromFile(configIn root) }

    match
        Lint.lintParsedSource
            lintParams
            { Ast = result.ParseResults.ParseTree
              Source = result.Source
              TypeCheckResults = typeCheckResults
              ProjectCheckResults = None }
    with
    | Lint.LintResult.Success warnings -> warnings |> List.map _.Details.Message
    | Lint.LintResult.Failure failure -> failwith $"lint failed: %s{failure.Description}"

/// Write under `a`'s key in a store, then look it up under `b`'s: the framework's own
/// hit-or-miss decision, not an inference from two hashes.
let private lookupAcross (a: string) (keyA: ContentHash) (b: string) (keyB: ContentHash) (file: string) =
    withTempDir "store" (fun store ->
        let composite root =
            { Plugin = "lint"
              File = Some(CachePathIdentity.keyOf (Some root) (Path.Combine(root, file))) }

        let entry =
            { CacheKey = keyA
              Errors = []
              Status = CachedFileCompleted(System.TimeSpan.FromMilliseconds 1.0)
              EmittedEvents = [] }

        (FileTaskCache.FileTaskCache(store, repoRoot = a) :> ITaskCache).Set (composite a) keyA entry
        (FileTaskCache.FileTaskCache(store, repoRoot = b) :> ITaskCache).Lookup (composite b) keyB)

// The rule types a bound identifier, so `Down` binds `X` before reading `.Value`. An
// abbreviation (`type T = int option`) is not seen through by the rule, hence a field.

/// `Up.T.X` is an option: `x.Value` is a partial function.
let private option =
    "module Up\ntype T = { X: int option }\nlet make () = { X = Some 1 }\n"

/// `Up.T.X` is a record whose field is named `Value`: `x.Value` is total.
let private record =
    "module Up\ntype V = { Value: int }\ntype T = { X: V }\nlet make () = { X = { Value = 1 } }\n"

/// Names `Up.T` in an annotation.
let private naming =
    "module Down\nlet read (t: Up.T) =\n    let x = t.X\n    x.Value\n"

/// Never names `Up.T`: it reaches the file only as the inferred type of `make ()`.
let private inferring =
    "module Down\nlet v = (Up.make ()).X\nlet read () = v.Value\n"

/// Key `Down.fs` checked in `rootA` against `upA` and in `rootB` against `upB`, and look
/// B's key up in a store holding A's entry. Also returns what a fresh lint reports in each.
let private crossCheck layout downSource (upA: string) (upB: string) =
    withTempDir "lint-xfile-a" (fun rootA ->
        withTempDir "lint-xfile-b" (fun rootB ->
            let resultA, checkA = checkDownstream rootA layout upA downSource
            let resultB, checkB = checkDownstream rootB layout upB downSource
            // Every pair compiles cleanly: the diagnostics cannot tell them apart.
            test <@ Array.isEmpty checkA.Diagnostics && Array.isEmpty checkB.Diagnostics @>
            let keyA = keyFor rootA resultA
            let keyB = keyFor rootB resultB
            test <@ keyA.IsSome && keyB.IsSome @>

            (typedLint rootA resultA, typedLint rootB resultB),
            lookupAcross rootA keyA.Value rootB keyB.Value "Down.fs"))

/// Same file bytes, a different typed lint verdict, and a miss naming only `used-signatures`.
let private assertShapeChangeMisses layout downSource =
    let (lintA, lintB), outcome = crossCheck layout downSource option record
    test <@ not (List.isEmpty lintA) @>
    test <@ List.isEmpty lintB @>

    match outcome with
    | CacheHit _ -> failwith "an upstream type that changed shape must never replay a typed lint result"
    | CacheMiss reason -> test <@ reason = CacheMissReason.InputsChanged [ "used-signatures" ] @>

let private assertHits layout downSource upA upB =
    match snd (crossCheck layout downSource upA upB) with
    | CacheHit _ -> ()
    | CacheMiss reason -> failwith $"expected a hit, got %A{reason}"

[<Fact(Timeout = 120000)>]
let ``the lint key differs when an earlier file changes the shape of a type this file names`` () =
    assertShapeChangeMisses EarlierFile naming

[<Fact(Timeout = 120000)>]
let ``the lint key differs when an earlier file changes the shape of a type this file only infers`` () =
    assertShapeChangeMisses EarlierFile inferring

[<Fact(Timeout = 120000)>]
let ``the lint key differs when a referenced project changes the shape of a type this file uses`` () =
    assertShapeChangeMisses ReferencedProject naming

[<Fact(Timeout = 120000)>]
let ``an unchanged upstream in a second checkout still hits the lint cache`` () =
    assertHits EarlierFile inferring option option

[<Fact(Timeout = 120000)>]
let ``an upstream body-only edit still hits the lint cache`` () =
    // A different body, a comment and shifted lines: nothing a caller can see.
    let edited =
        "module Up\n// moved down a line\ntype T = { X: int option }\nlet make () = { X = Some(1 + 1) }\n"

    assertHits EarlierFile inferring option edited
    assertHits ReferencedProject inferring option edited

[<Fact(Timeout = 120000)>]
let ``an upstream shape change this file cannot see still hits the lint cache`` () =
    let withUnusedType = option + "type Unused = { Y: string }\n"
    let changedUnusedType = option + "type Unused = | Y of string\n"
    assertHits EarlierFile inferring withUnusedType changedUnusedType

[<Fact(Timeout = 15000)>]
let ``a file whose used signatures cannot be described has no lint key`` () =
    withTempDir "lint-undescribable" (fun root ->
        let file = Path.Combine(root, "A.fs")
        File.WriteAllText(file, "let x = 1\n")
        // A check result with no state throws from every query.
        let broken =
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof<FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults>
            )
            :?> FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults

        let result =
            { fakeFileCheckResult file with
                Source = "let x = 1\n"
                CheckResults = FullCheck broken }

        test <@ keyFor root result = None @>)
