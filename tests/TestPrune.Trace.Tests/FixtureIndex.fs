/// TestPrune's real index of the fixture library, built once per test run.
module TestPrune.Trace.Tests.FixtureIndex

open System
open System.IO
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Orchestration
open TestPrune.ProjectLoader

/// Project options whose sources are the project's own compile list (no MSBuild).
let private compileListOptions: ProjectOptionsProvider =
    fun checker fsprojPath ->
        let compileFiles, _ = parseProjectFile fsprojPath
        let first = List.head compileFiles

        let options =
            getScriptOptions checker first (File.ReadAllText first)
            |> Async.RunSynchronously

        { options with
            SourceFiles = List.toArray compileFiles }

/// The fixture library's project directory, relative to the fixture root.
let private fxLib = Path.Combine("src", "FxLib")

/// The fixture xUnit v3 suite's project directory, relative to the fixture root.
let private fxTests = Path.Combine("tests", "FxTests")

/// Index a scratch COPY of the given fixture projects' sources, each passed through
/// `edit fileName text`, so the real tree gets no .test-prune.db. The copy keeps the
/// fixture root's relative layout, so a PDB document path made relative to the REAL
/// fixture root matches the index's paths.
let private indexProjects (projects: string list) (edit: string -> string -> string) =
    let scratch =
        Path.Combine(Path.GetTempPath(), "tp-fixture-index-" + Guid.NewGuid().ToString "N")

    for project in projects do
        let source = Path.Combine(Fixtures.fixtureRoot, project)

        for f in Directory.EnumerateFiles(source, "*.fs*", SearchOption.TopDirectoryOnly) do
            let dst = Path.Combine(scratch, project, Path.GetFileName f)
            Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
            File.WriteAllText(dst, edit (Path.GetFileName f) (File.ReadAllText f))

    let code =
        runIndexWith
            (fun _ -> 0)
            compileListOptions
            scratch
            (createChecker ())
            1
            (TestPrune.AuditSink.createNoopSink ())

    if code <> 0 then
        failwith $"indexing the fixtures failed: %d{code}"

    Database.create (Path.Combine(scratch, ".test-prune.db"))

let private indexCopy = indexProjects [ fxLib ]

/// The index of the fixture library as built.
let build = lazy (indexCopy (fun _ text -> text))

/// The index of the fixture library and its xUnit v3 suite as built: what a repository
/// index holds, where test methods are symbols too.
let withTests = lazy (indexProjects [ fxLib; fxTests ] (fun _ text -> text))

/// Lines `driftedLogic` inserts above `Logic.fs`'s first binding.
let driftLines = 4

/// The index of the fixture library after an edit the binary has not seen: `Logic.fs`
/// gains `driftLines` comment lines above its first binding, so every Logic symbol is
/// indexed `driftLines` lines below where the woven binary's sequence points put it.
let drifted =
    lazy
        (indexCopy (fun name text ->
            if name = "Logic.fs" then
                text.Replace("open FxLib\n", "open FxLib\n" + String.replicate driftLines "// drift\n")
            else
                text))
