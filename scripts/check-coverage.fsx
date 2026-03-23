#!/usr/bin/env dotnet fsi

/// Check per-file test coverage (line + branch) from Cobertura XML report.
/// Fails if any source file is below its coverage threshold.

open System
open System.IO
open System.Xml.Linq

// ============================================================================
// Configuration
// ============================================================================

let defaultMinLine = 100.0
let defaultMinBranch = 100.0
let coverageDir = "coverage"
let includedExtensions = [| ".fs" |]
let excludedPatterns = [| "Tests.fs"; "AssemblyInfo"; "AssemblyAttributes" |]
/// Directories to exclude from coverage (test projects, etc.)
let excludedDirs = [| "/tests/" |]

/// Per-file coverage overrides for F# compiler-generated code that is
/// impossible to cover (pattern match exhaustiveness, closure classes,
/// for-loop iteration branches, unreachable defensive failwith, etc.)
///
/// When adding/improving tests, ratchet these thresholds UP toward 100.
let overrides =
    Map.ofList
        [ // FalcoRouteAnalysis.fs: Compiler-generated branches in for-loop iteration and
          // regex matching closures. Line gap from obj/bin filter path in findTestFiles.
          "FalcoRouteAnalysis.fs", (97.0, 85.0)
          // AstAnalyzer.fs: Exception handlers for InvalidOperationException in classifySymbol
          // (lines 78, 97, 102) are impossible to trigger in unit tests — they guard against
          // faulty FCS symbols. classifyDependency default arm (line 111) requires non-standard
          // FSharp symbol types. Compiler-generated branches for type-test patterns and
          // for-loop iteration add uncoverable IL branches.
          "AstAnalyzer.fs", (90.0, 74.0)
          // Database.fs: Compiler-generated branches in while-loop readers, for-loop iteration,
          // and transaction try/with rollback paths. Route handler code now tested.
          "Database.fs", (82.0, 66.0)
          // ImpactAnalysis.fs: Compiler-generated branches for fold tuple deconstruction
          // and list filtering. All logic paths are tested.
          "ImpactAnalysis.fs", (100.0, 66.0)
          // Program.fs: Pure functions, analyzeChanges, runStatusWith, runRunWith, runDeadCode,
          // showHelp, runIndexWith all tested via DI. Remaining uncovered: dotnetBuildRunner
          // (actual process exec), jjDiffProvider (actual jj), runCommand/main entry points.
          "Program.fs", (59.0, 41.0)
          // ProjectLoader.fs: parseProjectFile tested with temp files including missing-attribute
          // branches. getProjectOptions requires Ionide.ProjInfo MSBuild — not unit-testable.
          // toolsPath/msbuildLock are lazy init + lock objects.
          "ProjectLoader.fs", (55.0, 0.0)
          // TestRunner.fs: Pure functions (buildFilterArgs, normalizeExitCode, findTestDll),
          // DI variants (runAllTestsWith, runFilteredTestsWith), and discoverTestProjects
          // (including exception handler) all tested. Remaining: runProcess (actual process exec).
          "TestRunner.fs", (63.0, 50.0) ]

// ============================================================================
// Types
// ============================================================================

type FileCoverage =
    { FileName: string
      LinePct: float
      BranchPct: float
      BranchesCovered: int
      BranchesTotal: int }

type FileResult =
    { File: FileCoverage
      LineThreshold: float
      BranchThreshold: float
      LinePassed: bool
      BranchPassed: bool }

type CoverageResult =
    | AllPassed of files: FileResult list
    | SomeFailed of passed: FileResult list * failed: FileResult list

// ============================================================================
// Coverage Parsing
// ============================================================================

let findCoverageFile () : string option =
    if Directory.Exists(coverageDir) then
        Directory.GetFiles(coverageDir, "coverage.cobertura.xml", SearchOption.AllDirectories)
        |> Array.sortByDescending File.GetLastWriteTime
        |> Array.tryHead
    else
        None

let parseCoverageReport (xmlPath: string) : FileCoverage list =
    let doc = XDocument.Load(xmlPath)
    let ns = doc.Root.Name.Namespace

    let isIncluded (fileName: string) =
        let shortName = Path.GetFileName(fileName)
        let hasValidExt = includedExtensions |> Array.exists shortName.EndsWith
        let nameExcluded = excludedPatterns |> Array.exists shortName.Contains
        let dirExcluded = excludedDirs |> Array.exists fileName.Contains
        hasValidExt && not nameExcluded && not dirExcluded

    // Collect all lines from all classes, grouped by file.
    // Use line number to deduplicate (same line may appear in multiple classes).
    // A line is "hit" if any class reports hits > 0 for it.
    doc.Root.Descendants(ns + "class")
    |> Seq.choose (fun classEl ->
        let fn = classEl.Attribute(XName.Get("filename"))

        if isNull fn || not (isIncluded fn.Value) then
            None
        else
            Some(fn.Value, classEl))
    |> Seq.toList
    |> List.groupBy fst
    |> List.map (fun (fileName, items) ->
        let classElements = items |> List.map snd

        // Aggregate lines by line number: a line is covered if ANY class reports hits > 0
        let lineMap = Collections.Generic.Dictionary<int, bool>()
        let branchMap = Collections.Generic.Dictionary<int, int * int>() // lineNum -> (covered, total)

        for classEl in classElements do
            for line in classEl.Descendants(ns + "line") do
                let numAttr = line.Attribute(XName.Get("number"))
                let hitsAttr = line.Attribute(XName.Get("hits"))

                if not (isNull numAttr) && not (isNull hitsAttr) then
                    let lineNum = int numAttr.Value
                    let hits = int hitsAttr.Value
                    let wasHit = hits > 0

                    match lineMap.TryGetValue(lineNum) with
                    | true, existing -> lineMap.[lineNum] <- existing || wasHit
                    | false, _ -> lineMap.[lineNum] <- wasHit

                    // Branch coverage from condition-coverage attribute
                    let cc = line.Attribute(XName.Get("condition-coverage"))

                    if not (isNull cc) then
                        let s = cc.Value
                        let paren = s.IndexOf('(')
                        let slash = s.IndexOf('/')
                        let close = s.IndexOf(')')

                        if paren >= 0 && slash >= 0 && close >= 0 then
                            let covered = int (s.Substring(paren + 1, slash - paren - 1))
                            let total = int (s.Substring(slash + 1, close - slash - 1))

                            match branchMap.TryGetValue(lineNum) with
                            | true, (existingC, existingT) ->
                                // Take the max coverage for this line across classes
                                branchMap.[lineNum] <- (max existingC covered, max existingT total)
                            | false, _ -> branchMap.[lineNum] <- (covered, total)

        let totalLines = lineMap.Count
        let coveredLines = lineMap.Values |> Seq.filter id |> Seq.length

        let linePct =
            if totalLines > 0 then
                float coveredLines / float totalLines * 100.0
            else
                100.0

        let coveredBranches = branchMap.Values |> Seq.sumBy fst
        let totalBranches = branchMap.Values |> Seq.sumBy snd

        let branchPct =
            if totalBranches > 0 then
                float coveredBranches / float totalBranches * 100.0
            else
                100.0

        { FileName = fileName
          LinePct = linePct
          BranchPct = branchPct
          BranchesCovered = coveredBranches
          BranchesTotal = totalBranches })

// ============================================================================
// Coverage Check
// ============================================================================

let checkCoverage (files: FileCoverage list) : CoverageResult =
    let results =
        files
        |> List.map (fun f ->
            let shortName = Path.GetFileName(f.FileName)

            let lineThreshold, branchThreshold =
                overrides
                |> Map.tryFind shortName
                |> Option.defaultValue (defaultMinLine, defaultMinBranch)

            { File = f
              LineThreshold = lineThreshold
              BranchThreshold = branchThreshold
              LinePassed = f.LinePct >= lineThreshold
              BranchPassed = f.BranchPct >= branchThreshold })

    let passed, failed =
        results |> List.partition (fun r -> r.LinePassed && r.BranchPassed)

    if List.isEmpty failed then
        AllPassed passed
    else
        SomeFailed(passed, failed)

// ============================================================================
// Output
// ============================================================================

let printResults (result: CoverageResult) =
    let printFile prefix (r: FileResult) =
        let shortName = Path.GetFileName(r.File.FileName)

        let lineNote =
            if r.LineThreshold < defaultMinLine then
                $" [min: %.0f{r.LineThreshold}%%]"
            else
                ""

        let branchInfo =
            if r.File.BranchesTotal > 0 then
                let overrideNote =
                    if r.BranchThreshold < defaultMinBranch then
                        $" [min: %.0f{r.BranchThreshold}%%]"
                    else
                        ""

                $"  branch=%.1f{r.File.BranchPct}%% (%d{r.File.BranchesCovered}/%d{r.File.BranchesTotal})%s{overrideNote}"
            else
                ""

        printfn "%s %s: line=%.1f%%%s%s" prefix shortName r.File.LinePct lineNote branchInfo

    match result with
    | AllPassed files ->
        printfn "✓ All %d files meet coverage thresholds\n" files.Length
        files |> List.sortBy (fun r -> r.File.FileName) |> List.iter (printFile "  ✓")

    | SomeFailed(passed, failed) ->
        printfn "✗ %d file(s) below coverage thresholds\n" failed.Length

        printfn "Failed:"

        failed
        |> List.sortBy (fun r -> r.File.BranchPct)
        |> List.iter (fun r ->
            let shortName = Path.GetFileName(r.File.FileName)

            if not r.LinePassed then
                printfn "  ✗ %s: line=%.1f%% (need %.0f%%)" shortName r.File.LinePct r.LineThreshold

            if not r.BranchPassed then
                printfn
                    "  ✗ %s: branch=%.1f%% (%d/%d, need %.0f%%)"
                    shortName
                    r.File.BranchPct
                    r.File.BranchesCovered
                    r.File.BranchesTotal
                    r.BranchThreshold)

        if not (List.isEmpty passed) then
            printfn "\nPassed:"
            passed |> List.sortBy (fun r -> r.File.FileName) |> List.iter (printFile "  ✓")

// ============================================================================
// Main
// ============================================================================

let exitCode =
    match findCoverageFile () with
    | None ->
        eprintfn "No coverage report found in %s/" coverageDir
        eprintfn "Run 'mise run test' first to generate coverage data."
        1

    | Some xmlPath ->
        printfn "Checking coverage from: %s\n" xmlPath

        let files = parseCoverageReport xmlPath
        let result = checkCoverage files

        printResults result

        match result with
        | AllPassed _ -> 0
        | SomeFailed _ -> 1

exit exitCode
