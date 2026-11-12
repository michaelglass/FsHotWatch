/// AUTOMATION-1482: TestPrune's analysis of a file, filed in the outcome store, reads back
/// exactly in any checkout, and is found only under the identity it was made for.
module FsHotWatch.Tests.AnalysisRecordsTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer

let private analysisUnder (root: string) : AnalysisResult =
    let kinds = [ Function; Type; DuCase; Module; Value; Property; ExternRef ]

    { Symbols =
        [ for i, kind in List.indexed kinds ->
              { FullName = $"P.S%d{i}"
                Kind = kind
                Location =
                  if kind = ExternRef then
                      Extern None
                  else
                      InSource(Path.Combine(root, "src", "P.fs"), i, i + 1, $"h%d{i}") } ]
        // A DllImport: extern, declared in a repository file (AUTOMATION-1455).
        @ [ { FullName = "P.native"
              Kind = Function
              Location = Extern(Some(Path.Combine(root, "src", "P.fs"))) } ]
      Dependencies =
        [ for i, kind in List.indexed [ Calls; UsesType; PatternMatches; References; SharedState; SharedLiteral ] ->
              { FromSymbol = $"P.S%d{i}"
                ToSymbol = "P.S0"
                Kind = kind
                Source = CoreSource } ]
      TestMethods =
        [ { SymbolFullName = "P.S0"
            TestProject = "P"
            TestClass = "P.Tests"
            TestMethod = "works" } ]
      Attributes =
        [ { SymbolFullName = "P.S0"
            AttributeName = "Fact"
            ArgsJson = "[]" } ]
      ParentLinks = [ { Child = "P.S0"; Parent = "P" } ]
      Diagnostics =
        { DroppedEdges = 1
          FilteredSymbols = 2
          TotalDefinitions = 3 } }

[<Fact>]
let ``an analysis reads back exactly, under the reading checkout's root`` () =
    let rootA = "/checkouts/a"
    let rootB = "/checkouts/somewhere-else"
    let text = AnalysisRecords.toJson rootA (analysisUnder rootA)
    test <@ not (text.Contains rootA) @>
    test <@ AnalysisRecords.tryOfJson rootA text = Some(analysisUnder rootA) @>
    test <@ AnalysisRecords.tryOfJson rootB text = Some(analysisUnder rootB) @>

[<Fact>]
let ``anything but an analysis of this schema is a miss`` () =
    let text = AnalysisRecords.toJson "/r" (analysisUnder "/r")
    test <@ AnalysisRecords.tryOfJson "/r" "{" = None @>
    test <@ AnalysisRecords.tryOfJson "/r" (text.Replace(AnalysisRecords.Schema, "other")) = None @>
    test <@ AnalysisRecords.tryOfJson "/r" (text.Replace("\"du-case\"", "\"union\"")) = None @>
    test <@ AnalysisRecords.tryOfJson "/r" (text.Replace("\"uses-type\"", "\"borrows\"")) = None @>
    test <@ AnalysisRecords.tryOfJson "/r" (text.Replace("\"P\",\"P.Tests\"", "\"P\"")) = None @>
    test <@ AnalysisRecords.tryOfJson "/r" (text.Replace("[\"P.S0\",\"P\"]", "[\"P.S0\",null]")) = None @>
    test <@ AnalysisRecords.tryOfJson "/r" (text.Replace("\"name\":\"P.S1\"", "\"name\":null")) = None @>

[<Fact>]
let ``the analyser identity names TestPrune.Core's release and the analysis schema, not either assembly's build`` () =
    // Both assemblies' module version ids move with every fshw release: TestPrune.Core's
    // informational version carries the release commit (AUTOMATION-1616).
    let core = typeof<AnalysisResult>.Assembly.GetName()
    test <@ AnalysisRecords.analyzerIdentity.Contains $"%s{core.Name}@%O{core.Version}" @>
    test <@ AnalysisRecords.analyzerIdentity.Contains "fshw-testprune-analysis-schema=" @>
    test <@ not (AnalysisRecords.analyzerIdentity.Contains "FsHotWatch.TestPrune@") @>

    test
        <@
            not (
                AnalysisRecords.analyzerIdentity.Contains(
                    string typeof<AnalysisResult>.Assembly.ManifestModule.ModuleVersionId
                )
            )
        @>

[<Fact>]
let ``a key names the analysis, the file, the project and the analyser`` () =
    let key = AnalysisRecords.keyOf "id" "src/P.fs" "P"
    test <@ key.Kind = AnalysisRecords.Kind @>
    test <@ key.Inputs |> List.contains ("analyzer", AnalysisRecords.analyzerIdentity) @>
    test <@ AnalysisRecords.keyOf "other" "src/P.fs" "P" <> key @>
    test <@ AnalysisRecords.keyOf "id" "src/Q.fs" "P" <> key @>
    test <@ AnalysisRecords.keyOf "id" "src/P.fs" "Q" <> key @>
    test <@ AnalysisRecords.projectNameOf "/r/Lib.fsx.fsproj" = "Lib" @>
    test <@ AnalysisRecords.projectNameOf "/r/Lib.fsproj" = "Lib" @>

let private replayed (root: string) (fcsSignature: string) =
    { fakeFileCheckResult (Path.Combine(root, "src", "P.fs")) with
        Source = "module P"
        ProjectOptions =
            { (fakeFileCheckResult (Path.Combine(root, "src", "P.fs"))).ProjectOptions with
                ProjectFileName = Path.Combine(root, "P.fsproj") }
        CheckResults =
            Replayed
                { Diagnostics = []
                  FcsSignature = fcsSignature
                  UsedSignatures = Some "used"
                  ApiSignature = None } }

[<Fact>]
let ``a filed analysis is held for the check it was made from, and for no other`` () =
    withTempDir "analysis-records" (fun dir ->
        let store =
            OutcomeStore.Store(
                { Root = Path.Combine(dir, "outcomes")
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        let root = Path.Combine(dir, "repo")
        let result = replayed root "sig-1"
        let key = (AnalysisRecords.keyFor root result).Value
        test <@ not (AnalysisRecords.holds store root result) @>
        test <@ AnalysisRecords.file store root "src/P.fs" key (analysisUnder root) @>
        test <@ AnalysisRecords.holds store root result @>
        test <@ AnalysisRecords.lookup store root key = Some(analysisUnder root) @>
        // Another compiler answer for the same bytes is another analysis.
        test <@ not (AnalysisRecords.holds store root (replayed root "sig-2")) @>
        // A check whose upstream cannot be described has no key, and is never held.
        let undescribed =
            { result with
                CheckResults =
                    Replayed
                        { Diagnostics = []
                          FcsSignature = "sig-1"
                          UsedSignatures = None
                          ApiSignature = None } }

        test <@ AnalysisRecords.keyFor root undescribed = None @>
        test <@ not (AnalysisRecords.holds store root undescribed) @>)
