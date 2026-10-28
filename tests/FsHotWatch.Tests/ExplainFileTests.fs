/// `fshw explain <file>`: why does editing a file select the tests it selects?
///
/// A symbol-free edit to a compiled source file (a comment, whitespace) runs the classes
/// covering every symbol the file holds (`SymbolFreeEdit`). The explanation is that same
/// selection, computed by the same code, so the answer cannot drift from what `check`
/// does; these tests pin both halves.
module FsHotWatch.Tests.ExplainFileTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Cli
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open TestPrune.AstAnalyzer
open TestPrune.Database

let private method project cls : TestMethodInfo =
    { SymbolFullName = $"%s{cls}.m"
      TestProject = project
      TestClass = cls
      TestMethod = "m" }

let private index: SymbolFreeEdit.Index =
    { SymbolsIn =
        function
        | "src/Lib.fs" -> [ "Lib.f"; "Lib.g" ]
        | _ -> []
      TestsIn =
        function
        | "src/Lib.fs" -> [ method "P1" "InFileTests" ]
        | _ -> []
      Affected =
        fun seeds ->
            [ if List.contains "Lib.f" seeds then
                  method "P1" "LibTests"
              if List.contains "Lib.g" seeds then
                  method "P2" "GTests"
                  method "P2" "MoreGTests"
                  method "Unrun" "Elsewhere" ] }

let private runnable project = project <> "Unrun"

// --- the pure explanation ---------------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``explaining a file names the classes covering its symbols, by project, and which symbol pulls in most`` () =
    match SymbolFreeEdit.explainFile runnable index "src/Lib.fs" with
    | Some explained ->
        test <@ explained.Seeds = [ "Lib.f"; "Lib.g" ] @>

        test
            <@ explained.Classes = Map.ofList [ "P1", [ "InFileTests"; "LibTests" ]; "P2", [ "GTests"; "MoreGTests" ] ] @>

        test <@ explained.CoveringTests = 4 @>

        test
            <@ explained.Fanout = SeedFanoutReading.Top [ { Seed = "Lib.g"; Tests = 2 }; { Seed = "Lib.f"; Tests = 1 } ] @>
    | None -> Assert.Fail "expected an explanation"

[<Fact(Timeout = 10000)>]
let ``a file the index holds no symbol for has no explanation`` () =
    test <@ SymbolFreeEdit.explainFile runnable index "src/Unknown.fs" = None @>

[<Fact(Timeout = 10000)>]
let ``the explanation is the selection a symbol-free edit to the file makes`` () =
    let tree entries : ReceiptTree =
        { Identity = entries |> List.map (fun (p, h) -> $"%s{p}=%s{h}") |> String.concat ";"
          Entries = entries }

    let before = tree [ "src/Lib.fs", "h1" ]
    let after = tree [ "src/Lib.fs", "h2" ]

    let selected =
        SymbolFreeEdit.select (Some before) (Some after) (Some after.Identity) (Some(fun _ -> true)) runnable index

    let explained = SymbolFreeEdit.explainFile runnable index "src/Lib.fs"

    match selected, explained with
    | SymbolFreeEdit.Covering(_, seeds, classes), Some e ->
        test <@ e.Seeds = seeds @>
        test <@ e.Classes = classes @>
    | other -> Assert.Fail $"expected both to cover the file, got %A{other}"

// --- the CLI verb, over a seeded index --------------------------------------------

let private seeded (repoRoot: string) =
    let dbPath = Path.Combine(repoRoot, DeadCode.DbRelativePath)
    Directory.CreateDirectory(Path.GetDirectoryName dbPath) |> ignore
    let db = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol db "Lib.f" "src/Lib.fs" "ProjA" "ProjA.FreshTests" "covers f"
    db

let private tests: Explain.TestsScope =
    { Runnable = Set.ofList [ "ProjA"; "ProjB" ]
      AlwaysClasses = Map.ofList [ "ProjB", [ "ProjB.GuardTests" ] ]
      Selection = TestPrune.Domain.WalkPolicy.FollowAll }

let private runIn repoRoot (opts: Explain.ExplainOptions) =
    let out = ResizeArray<string>()
    let err = ResizeArray<string>()

    let code =
        Explain.run File.Exists DeadCode.readSchemaVersion (fun _ -> Some tests) out.Add err.Add repoRoot opts

    code, List.ofSeq out, List.ofSeq err

[<Fact(Timeout = 30000)>]
let ``fshw explain names the classes and alwaysClasses a check would run for an edit to the file`` () =
    withTempDir "explain-file" (fun repoRoot ->
        seeded repoRoot |> ignore

        let code, out, _ =
            runIn repoRoot ({ File = "src/Lib.fs"; Json = false }: Explain.ExplainOptions)

        let text = String.concat "\n" out
        test <@ code = 0 @>
        test <@ text.Contains "src/Lib.fs" @>
        test <@ text.Contains "ProjA: ProjA.FreshTests" @>
        test <@ text.Contains "ProjB: ProjB.GuardTests" @>
        test <@ text.Contains "Lib.f" @>)

[<Fact(Timeout = 30000)>]
let ``fshw explain --json writes the selection as a document`` () =
    withTempDir "explain-file-json" (fun repoRoot ->
        seeded repoRoot |> ignore

        // An absolute path inside the repository names the same file.
        let code, out, _ =
            runIn
                repoRoot
                ({ File = Path.Combine(repoRoot, "src", "Lib.fs")
                   Json = true }
                : Explain.ExplainOptions)

        test <@ code = 0 @>
        use doc = JsonDocument.Parse(String.concat "\n" out)
        let root = doc.RootElement
        let file = root.GetProperty("file").GetString()
        let indexed = root.GetProperty("indexed").GetBoolean()
        let classes = root.GetProperty("classes").[0]
        let project = classes.GetProperty("project").GetString()
        let first = classes.GetProperty("classes").[0].GetString()

        let always =
            root.GetProperty("alwaysClasses").[0].GetProperty("project").GetString()

        let fanout = root.GetProperty("seedFanout").GetProperty("kind").GetString()
        let testCount = root.GetProperty("coveringTests").GetInt32()
        test <@ file = "src/Lib.fs" @>
        test <@ indexed @>
        test <@ project = "ProjA" @>
        test <@ first = "ProjA.FreshTests" @>
        test <@ always = "ProjB" @>
        test <@ fanout = "top" @>
        test <@ testCount = 1 @>)

[<Fact(Timeout = 30000)>]
let ``a file the index holds no symbol for is explained as a whole-suite edit`` () =
    withTempDir "explain-file-unindexed" (fun repoRoot ->
        seeded repoRoot |> ignore

        let code, out, _ =
            runIn repoRoot ({ File = "src/Other.fs"; Json = false }: Explain.ExplainOptions)

        let text = String.concat "\n" out
        test <@ code = 0 @>
        test <@ text.Contains "holds no symbol for src/Other.fs" @>

        let _, json, _ =
            runIn repoRoot ({ File = "src/Other.fs"; Json = true }: Explain.ExplainOptions)

        use doc = JsonDocument.Parse(String.concat "\n" json)
        let indexed = doc.RootElement.GetProperty("indexed").GetBoolean()
        test <@ not indexed @>)

[<Fact(Timeout = 15000)>]
let ``fshw explain without an index, or without tests configured, exits 1 and says why`` () =
    withTempDir "explain-file-missing" (fun repoRoot ->
        let code, out, err =
            runIn repoRoot ({ File = "src/Lib.fs"; Json = false }: Explain.ExplainOptions)

        test <@ code = 1 @>
        test <@ List.isEmpty out @>
        test <@ err = [ DeadCode.missingDbMessage ] @>

        seeded repoRoot |> ignore
        let out2 = ResizeArray<string>()
        let err2 = ResizeArray<string>()

        let code2 =
            Explain.run
                File.Exists
                DeadCode.readSchemaVersion
                (fun _ -> None)
                out2.Add
                err2.Add
                repoRoot
                ({ File = "src/Lib.fs"; Json = false }: Explain.ExplainOptions)

        test <@ code2 = 1 @>
        test <@ err2 |> Seq.exists (fun e -> e.Contains "tests") @>

        let code3 =
            Explain.run
                File.Exists
                (fun _ -> DeadCode.ExpectedSchemaVersion - 1)
                (fun _ -> Some tests)
                ignore
                err2.Add
                repoRoot
                ({ File = "src/Lib.fs"; Json = false }: Explain.ExplainOptions)

        test <@ code3 = 1 @>)

// --- the human answer's other shapes ----------------------------------------------

let private selection classes fanout : SymbolFreeEdit.FileSelection =
    { Seeds = [ "Lib.f" ]
      Classes = classes
      CoveringTests = 0
      Fanout = fanout }

[<Fact(Timeout = 10000)>]
let ``a file no configured test covers is said to select no class`` () =
    let lines =
        Explain.format
            { File = "src/Lib.fs"
              Selection = Some(selection Map.empty SeedFanoutReading.Narrow)
              AlwaysClasses = Map.empty
              Price = None }

    test <@ lines |> List.exists (fun l -> l.Contains "selects no class") @>
    test <@ not (lines |> List.exists (fun l -> l.Contains "alwaysClasses")) @>

[<Fact(Timeout = 10000)>]
let ``a file with too many symbols to rank says so`` () =
    let lines =
        Explain.format
            { File = "src/Lib.fs"
              Selection = Some(selection (Map.ofList [ "P", [ "P.T" ] ]) (SeedFanoutReading.OverBudget 300))
              AlwaysClasses = Map.empty
              Price = None }

    test <@ lines |> List.exists (fun l -> l.Contains "300 symbols exceed") @>

[<Fact(Timeout = 10000)>]
let ``a path is named as the index names it, relative to the repository`` () =
    let root = Path.Combine(Path.GetTempPath(), "explain-root")
    test <@ Explain.relativeTo root "src/Lib.fs" = "src/Lib.fs" @>
    test <@ Explain.relativeTo root (Path.Combine(root, "src", "Lib.fs")) = "src/Lib.fs" @>
    test <@ Explain.relativeTo root "./src/../src/Lib.fs" = "src/Lib.fs" @>

// --- the default wiring -----------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``the tests scope is read from .fshw.json, and is absent when no tests are configured`` () =
    withTempDir "explain-scope" (fun repoRoot ->
        File.WriteAllText(
            Path.Combine(repoRoot, ".fshw.json"),
            """{"tests": {"projects": [{"project": "ProjA"}, {"project": "ProjB", "alwaysClasses": ["ProjB.GuardTests"]}]}}"""
        )

        match Explain.TestsScope.load repoRoot with
        | Some scope ->
            test <@ scope.Runnable = Set.ofList [ "ProjA"; "ProjB" ] @>
            test <@ scope.AlwaysClasses = Map.ofList [ "ProjB", [ "ProjB.GuardTests" ] ] @>
        | None -> Assert.Fail "expected the configured tests")

    withTempDir "explain-scope-none" (fun repoRoot ->
        File.WriteAllText(Path.Combine(repoRoot, ".fshw.json"), "{}")
        test <@ Explain.TestsScope.load repoRoot = None @>)

[<Fact(Timeout = 30000)>]
let ``with no projects named, every indexed project counts as runnable`` () =
    withTempDir "explain-file-any" (fun repoRoot ->
        let db = seeded repoRoot

        let explained =
            Explain.explain
                db
                ({ Classes = Map.empty }: SelectionPrice.Book)
                { tests with Runnable = Set.empty }
                "src/Lib.fs"

        test <@ explained.Selection |> Option.map _.Classes = Some(Map.ofList [ "ProjA", [ "ProjA.FreshTests" ] ]) @>)

[<Fact(Timeout = 15000)>]
let ``the default wiring refuses a repository with no index`` () =
    withTempDir "explain-default" (fun repoRoot ->
        test <@ Explain.runDefault repoRoot ({ File = "src/Lib.fs"; Json = false }: Explain.ExplainOptions) = 1 @>)

/// A recorded run in which `ProjA.FreshTests` cost `ms`.
let private recordRun (repoRoot: string) (ms: int64) =
    let dir = FsHotWatch.Ctrf.runDir repoRoot (System.Guid.NewGuid())
    Directory.CreateDirectory dir |> ignore

    let report =
        $"""{{"results":{{"summary":{{"tests":1,"passed":1,"failed":0,"pending":0,"skipped":0,"other":0}},"tests":[{{"name":"ProjA.FreshTests.covers f","status":"passed","duration":%d{ms},"start":1000,"stop":%d{1000L + ms},"extra":{{"type":"ProjA.FreshTests"}}}}]}}}}"""

    File.WriteAllText(Path.Combine(dir, "ProjA" + FsHotWatch.Ctrf.ReportSuffix), report)

[<Fact(Timeout = 30000)>]
let ``fshw explain prices the selection from the newest recorded runs`` () =
    withTempDir "explain-file-priced" (fun repoRoot ->
        seeded repoRoot |> ignore
        recordRun repoRoot 4000L

        let code, out, _ =
            runIn repoRoot ({ File = "src/Lib.fs"; Json = false }: Explain.ExplainOptions)

        let text = String.concat "\n" out
        test <@ code = 0 @>
        // ProjA.FreshTests is recorded; ProjB's alwaysClass never ran, so it is named.
        test <@ text.Contains "~4.0s of test time over 1 recorded class(es)" @>
        test <@ text.Contains "no recorded run of ProjB" @>

        let _, json, _ =
            runIn repoRoot ({ File = "src/Lib.fs"; Json = true }: Explain.ExplainOptions)

        use doc = JsonDocument.Parse(String.concat "\n" json)

        let estimate =
            doc.RootElement.GetProperty("price").GetProperty("estimateMs").GetDouble()

        test <@ estimate = 4000.0 @>)
