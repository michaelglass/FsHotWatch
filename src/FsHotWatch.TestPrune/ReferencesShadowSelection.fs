/// AUTOMATION-1816 step 2b, test-prune: the selection the stale analyses would have made.
///
/// For each file the references key would have served, the analysis reuse would have kept is
/// the `testprune-analysis` record under `IndexedAnalysis.identity` of the file's bytes and the
/// stored check's signatures. Where it differs from this run's, S′ can differ from S (the selection for C
/// over this run's index): S′ is the selection for C re-derived under the reused analyses
/// (`changedUnder`), over an index holding them. Substituting changes both C and the edges.
///
/// S′ is computed on a COPY of the impact DB, taken with SQLite's backup API (a consistent
/// snapshot, WAL included) under the run's own temporary directory and deleted in a `finally`:
/// the index the gate selects from is never written. Record and compare only.
module FsHotWatch.TestPrune.ReferencesShadowSelection

open System
open System.Diagnostics
open System.IO
open Microsoft.Data.Sqlite
open FsHotWatch
open FsHotWatch.ReferencesShadowReport
open TestPrune.AstAnalyzer
open TestPrune.Database

/// A servable file's analysis under reuse, against this run's.
[<RequireQualifiedAccess>]
type internal ReusedAnalysis =
    /// Reuse would have kept this run's analysis.
    | Same
    /// Reuse would have kept another analysis of `file` (repository-relative), substituted for S′.
    | Differs of file: string * AnalysisResult
    /// No reused analysis can be read: no record under its identity, a record naming no used
    /// signatures, or a file gone from disk since the scan named it.
    | Unverifiable

/// What reuse would have kept for `servable`. `fileKeys` are this run's indexed identities by
/// repository-relative path; `store` holds the filed analyses.
let internal reusedAnalysisOf
    (store: OutcomeRecords.IOutcomeStore)
    (repoRoot: string)
    (fileKeys: Map<string, string>)
    (servable: ServableFile)
    : ReusedAnalysis =
    let path = Path.Combine(repoRoot, servable.File)

    match servable.UsedSignatures with
    | UsedSignaturesKey.Undescribed -> ReusedAnalysis.Unverifiable
    // Gone since the scan named it: no bytes to name the reused identity by.
    | UsedSignaturesKey.Described _ when not (File.Exists path) -> ReusedAnalysis.Unverifiable
    | UsedSignaturesKey.Described used ->
        let source = File.ReadAllText path
        let reusedIdentity = IndexedAnalysis.identity source servable.FcsSignature used
        let project = AnalysisRecords.projectNameOf servable.Project

        let lookup identity =
            AnalysisRecords.lookup store repoRoot (AnalysisRecords.keyOf identity servable.File project)

        match Map.tryFind servable.File fileKeys with
        | Some fresh when fresh = reusedIdentity -> ReusedAnalysis.Same
        | fresh ->
            match lookup reusedIdentity with
            | None -> ReusedAnalysis.Unverifiable
            | Some reused when Some reused = (fresh |> Option.bind lookup) -> ReusedAnalysis.Same
            | Some reused -> ReusedAnalysis.Differs(servable.File, reused)

/// A test as both selections name it.
let internal testName (t: TestMethodInfo) = $"%s{t.TestClass}.%s{t.TestMethod}"

/// C under the stale analyses: which of a file's symbols count as changed depends on the analysis
/// diffed. A symbol on which the stale analysis and this run's (the rows `copy` holds for its file
/// before the substitution) disagree, in content or presence, counts the other way from this run:
/// reuse serves the analysis the index already agreed with, so a change this run saw goes unseen,
/// and content this run saw unchanged reads as changed. Symbols both analyses agree on keep their
/// status. (Exact when, for each disputed symbol, the index held one of the two analyses' versions;
/// a third version reads here as unchanged under reuse, the side that can only add attributable
/// tests, never hide them.)
let private changedUnder
    (copy: Database)
    (changed: string list)
    (reused: (string * AnalysisResult) list)
    : string list =
    let disputed =
        reused
        |> List.collect (fun (file, analysis) ->
            TestPrune.SymbolDiff.detectChanges analysis.Symbols (copy.GetSymbolsInFile file)
            |> fst
            |> TestPrune.SymbolDiff.changedSymbolNames)
        |> Set.ofList

    let before = Set.ofList changed

    Set.union (Set.difference before disputed) (Set.difference disputed before)
    |> Set.toList

let private select (db: Database) (changed: string list) : Set<string> =
    if changed.IsEmpty then
        Set.empty
    else
        (db.QueryAffectedSelection changed).Tests |> List.map testName |> Set.ofList

/// S′: `changed`, re-derived under the stale analyses (`changedUnder`), selected over a copy of `db` with `reused` re-indexed, the copy in the new
/// directory `dir`, deleted before this returns, whatever happens.
let private selectOnCopy
    (db: Database)
    (dbPath: string)
    (dir: string)
    (changed: string list)
    (reused: (string * AnalysisResult) list)
    : Set<string> * ImpactDbCopy =
    Directory.CreateDirectory dir |> ignore
    let copyPath = Path.Combine(dir, "test-impact.db")

    try
        let watch = Stopwatch.StartNew()

        do
            use source = new SqliteConnection(ImpactDbPool.connectionString dbPath)
            use target = new SqliteConnection(ImpactDbPool.connectionString copyPath)
            source.Open()
            target.Open()
            source.BackupDatabase target

        watch.Stop()
        let bytes = FileInfo(copyPath).Length
        let copy = Database(copyPath, db.WalkPolicy)
        let changedPrime = changedUnder copy changed reused
        copy.RebuildProjects(reused |> List.map snd)
        let sPrime = select copy changedPrime
        sPrime, ImpactDbCopy.Copied(bytes, watch.ElapsedMilliseconds)
    finally
        ImpactDbPool.clear copyPath
        Directory.Delete(dir, true)

/// S and S′ for `changed` over `db` (the index at `dbPath`), given each servable file's reused analysis; the copy S′ needs
/// is made in the new directory `copyDir` and deleted. No copy is made when no analysis differs:
/// S′ is then S.
let internal selections
    (db: Database)
    (dbPath: string)
    (copyDir: string)
    (changed: string list)
    (reused: ReusedAnalysis list)
    : Selections =
    let differing =
        reused
        |> List.choose (fun r ->
            match r with
            | ReusedAnalysis.Differs(file, analysis) -> Some(file, analysis)
            | ReusedAnalysis.Same
            | ReusedAnalysis.Unverifiable -> None)

    let unverifiable =
        reused |> List.filter (fun r -> r = ReusedAnalysis.Unverifiable) |> List.length

    let s = select db changed

    let sPrime, copy =
        if differing.IsEmpty then
            s, ImpactDbCopy.NotCopied
        else
            selectOnCopy db dbPath copyDir changed differing

    { S = s
      SPrime = sPrime
      DifferingAnalyses = differing.Length
      UnverifiableAnalyses = unverifiable
      Copy = copy }
