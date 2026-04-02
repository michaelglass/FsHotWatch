module TestPrune.Benchmarks.Program

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open FSharp.Compiler.CodeAnalysis
open TestPrune.AuditSink
open TestPrune.Orchestration
open TestPrune.Program
open TestPrune.ProjectLoader

type IndexMetrics =
    { Mode: string
      Symbols: int
      Dependencies: int
      TestMethods: int
      ProjectsTotal: int
      ProjectsSkipped: int
      FilesAnalyzed: int
      FilesSkipped: int }

/// Find the repo root by walking up from startDir looking for TestPrune.slnx.
let findTestPruneRoot (startDir: string) : string option =
    let rec walk (dir: string) =
        if File.Exists(Path.Combine(dir, "TestPrune.slnx")) then
            Some dir
        else
            let parent = Directory.GetParent(dir)

            if isNull parent then None else walk parent.FullName

    walk startDir

/// Create an FSharpChecker with optional experiment flags.
/// Defaults match production createChecker() in Orchestration.fs.
let createBenchChecker (useTransparent: bool) (keepSymbolUses: bool) (captureIds: bool) =
    FSharpChecker.Create(
        projectCacheSize = 200,
        keepAssemblyContents = true,
        keepAllBackgroundResolutions = true,
        keepAllBackgroundSymbolUses = keepSymbolUses,
        parallelReferenceResolution = true,
        captureIdentifiersWhenParsing = captureIds,
        useTransparentCompiler = useTransparent
    )

/// Run runIndexWith, capturing stderr output. Returns (exitCode, stderrText).
let runIndexCapturingStderr (repoRoot: string) (checker: FSharpChecker) (parallelism: int) =
    let oldErr = Console.Error
    use sw = new StringWriter()
    Console.SetError(sw)

    try
        let exitCode =
            runIndexWith dotnetBuildRunner getProjectOptions repoRoot checker parallelism (createNoopSink ())

        Console.SetError(oldErr)
        let output = sw.ToString()
        oldErr.Write(output)
        exitCode, output
    with ex ->
        Console.SetError(oldErr)
        oldErr.WriteLine($"Exception during index: %s{ex.Message}")
        reraise ()

/// Parse a single integer from a regex match group.
let private tryParseInt (m: Match) (groupName: string) =
    if m.Success then
        match Int32.TryParse(m.Groups.[groupName].Value) with
        | true, v -> Some v
        | _ -> None
    else
        None

let private indexedPattern =
    Regex(@"Indexed (?<symbols>\d+) symbols, (?<deps>\d+) dependencies, (?<tests>\d+) test methods")

let private foundPattern = Regex(@"Found (?<total>\d+) projects")
let private skippedProjectsPattern = Regex(@"Skipped (?<n>\d+) unchanged project\(s\)")
let private skippedFilesPattern = Regex(@"Skipped (?<n>\d+) unchanged file\(s\)")

let private perProjectPattern =
    Regex(@"^\s+\S+: \d+ symbols, \d+ deps, \d+ tests \((?<analyzed>\d+)/(?<total>\d+) files analyzed\)", RegexOptions.Multiline)

/// Extract metrics from captured stderr output.
let parseMetrics (mode: string) (stderrOutput: string) : IndexMetrics =
    let indexedMatch = indexedPattern.Match(stderrOutput)
    let foundMatch = foundPattern.Match(stderrOutput)
    let skippedProjectsMatch = skippedProjectsPattern.Match(stderrOutput)
    let skippedFilesMatch = skippedFilesPattern.Match(stderrOutput)

    let symbols = tryParseInt indexedMatch "symbols" |> Option.defaultValue 0
    let deps = tryParseInt indexedMatch "deps" |> Option.defaultValue 0
    let tests = tryParseInt indexedMatch "tests" |> Option.defaultValue 0
    let projectsTotal = tryParseInt foundMatch "total" |> Option.defaultValue 0

    let projectsSkipped =
        tryParseInt skippedProjectsMatch "n" |> Option.defaultValue 0

    let skippedFiles = tryParseInt skippedFilesMatch "n" |> Option.defaultValue 0

    // Sum up files analyzed from per-project lines
    let perProjectMatches = perProjectPattern.Matches(stderrOutput)

    let filesAnalyzed =
        if perProjectMatches.Count > 0 then
            perProjectMatches
            |> Seq.cast<Match>
            |> Seq.sumBy (fun m ->
                match Int32.TryParse(m.Groups.["analyzed"].Value) with
                | true, v -> v
                | _ -> 0)
        else
            0

    { Mode = mode
      Symbols = symbols
      Dependencies = deps
      TestMethods = tests
      ProjectsTotal = projectsTotal
      ProjectsSkipped = projectsSkipped
      FilesAnalyzed = filesAnalyzed
      FilesSkipped = skippedFiles }

let private jsonOptions = JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

[<EntryPoint>]
let main argv =
    let testPruneRoot =
        match findTestPruneRoot AppContext.BaseDirectory with
        | Some root -> root
        | None ->
            eprintfn "Could not find TestPrune repo root (no TestPrune.slnx found)"
            exit 1

    let sampleSolutionRoot =
        match argv |> Array.tryFind (fun a -> not (a.StartsWith("--"))) with
        | Some path -> Path.GetFullPath(path)
        | None -> Path.Combine(testPruneRoot, "examples", "SampleSolution")

    if not (Directory.Exists(sampleSolutionRoot)) then
        eprintfn $"Sample solution not found at: %s{sampleSolutionRoot}"
        exit 1

    let dbPath = Path.Combine(sampleSolutionRoot, ".test-prune.db")

    let useTransparent = not (argv |> Array.contains "--no-transparent-compiler")
    let keepSymbolUses = argv |> Array.contains "--keep-all-background-symbol-uses"
    let captureIds = argv |> Array.contains "--capture-identifiers-when-parsing"

    if not useTransparent then
        eprintfn "Experiment: useTransparentCompiler = false"

    if keepSymbolUses then
        eprintfn "Experiment: keepAllBackgroundSymbolUses = true"

    if captureIds then
        eprintfn "Experiment: captureIdentifiersWhenParsing = true"

    let parallelism = Environment.ProcessorCount
    let checker = createBenchChecker useTransparent keepSymbolUses captureIds

    eprintfn "=== Cold index ==="

    if File.Exists(dbPath) then
        File.Delete(dbPath)
        eprintfn "Deleted existing DB"

    let coldExit, coldStderr = runIndexCapturingStderr sampleSolutionRoot checker parallelism

    if coldExit <> 0 then
        eprintfn $"Cold index failed with exit code %d{coldExit}"
        exit 1

    let coldMetrics = parseMetrics "cold" coldStderr

    eprintfn ""
    eprintfn "=== Warm index ==="

    let warmExit, warmStderr = runIndexCapturingStderr sampleSolutionRoot checker parallelism

    if warmExit <> 0 then
        eprintfn $"Warm index failed with exit code %d{warmExit}"
        exit 1

    let warmMetrics = parseMetrics "warm" warmStderr

    let json = JsonSerializer.Serialize([| coldMetrics; warmMetrics |], jsonOptions)
    printfn "%s" json
    0
