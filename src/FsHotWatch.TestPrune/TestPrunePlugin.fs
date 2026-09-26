module FsHotWatch.TestPrune.TestPrunePlugin

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Xml.Linq
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsDiagnosticFilter
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Domain
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff

/// Above this many selected tests the query is effectively a full run, so the per-seed
/// attribution below is worth paying for. Under it the breakdown is noise and is never
/// computed.
[<Literal>]
let WideSelectionTests = 500

/// Attribution re-queries each seed ALONE, so its cost is linear in seed count.
/// Past this many seeds the breakdown is skipped — and said to be skipped, so an
/// absent breakdown is never misread as "no single seed dominated".
[<Literal>]
let MaxSeedsToAttribute = 200

/// The bound `flushAndQueryAffected` declares over itself.
///
/// Impact selection is one long unit of work inside ONE event fold: it finishes no
/// plugin event while it runs, and in a workspace with no impact database it does its
/// largest possible version of that — every symbol in the tree is a seed and the
/// selection is the whole suite. To the daemon's stall detector that is byte-for-byte
/// the signature of a handler that never returned, and an undeclared fold is named
/// WEDGED after five minutes. A cold attribution can legitimately exceed that, and did:
/// a check was failed at five minutes on work that completed seventeen seconds later.
///
/// So the fold declares itself instead, and this is the price of the declaration. It is
/// generous on purpose — roughly twice the whole observed cold-start window, of which
/// selection was only a part — because failing a green tree costs more than waiting.
/// It is still FINITE, and well inside the hour-long `WaitForComplete` timeout, so a
/// selection that genuinely hangs is reported by name rather than swallowed.
let ImpactSelectionDeadline = System.TimeSpan.FromMinutes 20.0

/// How many CONSECUTIVE flush cycles a symbol must sit in the
/// needs-testing queue before its persistence is itself evidence of a problem.
///
/// One cycle is ordinary; two is explicable (an aborted run, a red project mid-fix); by
/// three the symbol has outlived several complete verify attempts while still dragging in
/// a quarter of the suite. Deliberately late — this drives a human-visible warning, and
/// one that cries wolf during an ordinary red-to-green cycle gets tuned out.
[<Literal>]
let PoisonSeedRuns = 3

/// The share of a run's selected tests one seed must account for before it reads as
/// a graph hub or a mis-qualified symbol rather than an ordinary dependency. Matches
/// the existing per-seed attribution threshold (`affected.Length / 4`) so the two
/// diagnostics agree about what "dominant" means.
[<Literal>]
let PoisonSeedSharePercent = 25

/// Is this queued symbol behaving like the poisoned seed — pinned
/// across runs AND selecting a large fraction of the suite every time?
///
/// A symbol only leaves the queue when every runnable project covering it passes, so ONE
/// persistently-red project pins it forever, and while pinned it re-seeds its whole
/// selection on every subsequent run. A single mis-qualified symbol (`name`, `kind`) plus
/// one red project is therefore a permanent, silent, near-full suite that looks exactly
/// like ordinary impact analysis from the outside.
///
/// A conjunction because neither half is sufficient: a pinned symbol selecting three
/// tests is just a slow fix, and a genuine graph hub selecting half the suite for one run
/// is just an expensive edit. Integer arithmetic is deliberate — `alone * 100 >= affected
/// * share` avoids the rounding that would let a seed sit just under the line forever.
let isPoisonSuspect (consecutiveRuns: int) (affectedCount: int) (aloneCount: int) : bool =
    consecutiveRuns >= PoisonSeedRuns
    && affectedCount > 0
    && aloneCount * 100 >= affectedCount * PoisonSeedSharePercent

/// Advance the consecutive-appearance counters for the symbols seeding THIS cycle.
///
/// Rebuilt from `Map.empty` rather than updated in place, so a symbol absent from
/// this cycle's seeds loses its history entirely. That is what makes the count
/// CONSECUTIVE: a symbol that clears and is later re-queued starts again at one, and
/// cannot accumulate its way to a false accusation across unrelated edits.
let bumpSeedAges (previous: Map<string, int>) (seeds: string list) : Map<string, int> =
    seeds
    |> List.fold (fun acc s -> Map.add s ((previous |> Map.tryFind s |> Option.defaultValue 0) + 1) acc) Map.empty

/// Per-project raw cobertura written by a FULL (unfiltered) test run.
[<Literal>]
let BaselineName = "coverage.baseline.cobertura.xml"

/// Per-project raw cobertura written by an impact-FILTERED test run.
[<Literal>]
let PartialName = "coverage.partial.cobertura.xml"

/// The single shared cobertura emitted from the full TestPrune DB and consumed
/// by downstream gating (coverageratchet).
[<Literal>]
let CoberturaName = "coverage.cobertura.xml"

/// How many completed runs the session ledger keeps, and therefore how
/// many `test-scope` declares.
///
/// Generous against the worst check anyone has recorded (four batches) so the bound
/// never truncates a real one, and small enough that the reply stays a reply: 64 ids is
/// ~2 KB on a path that earns verdicts, and a growing-without-bound diagnostic in that
/// path is a new failure mode.
[<Literal>]
let SessionRunLedger = 64

/// Per-project raw-coverage artifact paths + the command-line template used to
/// produce them. The runner writes Cobertura XML to `Baseline` (full run) or
/// `Partial` (impact-filtered run); the plugin ingests whichever this run wrote
/// into the TestPrune DB and emits the whole DB once to `Cobertura`. Callers
/// (DaemonConfig) decide the directory layout and the arg template; the plugin
/// treats the paths as opaque absolute paths and substitutes `{output}` in
/// `ArgsTemplate`.
///
/// The file format is Cobertura regardless of `ArgsTemplate` — the template
/// is responsible for telling its runner to write Cobertura to `{output}`.
/// For Microsoft Testing Platform, use `defaultCoverageArgsTemplate`; for
/// other runners (coverlet.collector, AltCover, OpenCover) supply your own.
type CoveragePaths =
    {
        Baseline: string
        Partial: string
        /// The SHARED cobertura the plugin emits the whole DB to — set identically
        /// for every project by DaemonConfig (the DB unions coverage across them),
        /// so the daemon writes one run-wide artifact, not one per project.
        Cobertura: string
        /// Whether this project's measurements are eligible for the consumer
        /// coverage ratchet. Impact collection is independent: a project may
        /// populate TestPrune while remaining outside the consumer's gate.
        IncludeInRatchet: bool
        ArgsTemplate: string
    }

/// One raw coverage artifact produced by one test project. Keeping the project
/// beside the path prevents the collection boundary from erasing provenance
/// before TestPrune gains a project-aware coverage ingest API.
type CoverageInput =
    { Project: string
      RawPath: string
      IncludeInRatchet: bool
      Scope: CoverageRunScope }

let coverageInput (project: string) (includeInRatchet: bool) (rawPath: string) : CoverageInput =
    { Project = project
      RawPath = rawPath
      IncludeInRatchet = includeInRatchet
      Scope =
        if Path.GetFileName(rawPath) = PartialName then
            CoverageRunScope.Partial
        else
            CoverageRunScope.Full }

type CoverageArtifactFingerprint =
    { Length: int64
      LastWriteUtc: DateTime
      Sha256: string }

type CoverageArtifactState =
    | Missing
    | Fingerprinted of CoverageArtifactFingerprint
    | Unreadable of reason: string

type CoverageArtifactLaunch =
    { Project: string
      IncludeInRatchet: bool
      RawPath: string
      Scope: CoverageRunScope
      Before: CoverageArtifactState
      DeletionProven: bool }

type CoverageIngestFailure = { Project: string; Reason: string }

type CoverageReceiptOutcome =
    | CoverageReceiptAccepted of CoverageInput
    | CoverageReceiptAbsent
    | CoverageReceiptFailed of CoverageIngestFailure

let private coverageArtifactState (path: string) : CoverageArtifactState =
    try
        File.GetAttributes path |> ignore
        use stream = File.OpenRead path

        Fingerprinted
            { Length = stream.Length
              LastWriteUtc = File.GetLastWriteTimeUtc path
              Sha256 = Convert.ToHexString(SHA256.HashData stream) }
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> Missing
    | :? IOException as ex -> Unreadable ex.Message
    | :? UnauthorizedAccessException as ex -> Unreadable ex.Message

/// Remove the prior runner artifact before launch. If removal is refused, retain
/// a content fingerprint so a stable stale file can never masquerade as this run's
/// receipt merely because it still exists afterward.
let internal prepareCoverageArtifact
    (project: string)
    (includeInRatchet: bool)
    (wasFiltered: bool)
    (rawPath: string)
    : CoverageArtifactLaunch =
    let before = coverageArtifactState rawPath

    try
        File.Delete rawPath
    with
    | :? IOException
    | :? UnauthorizedAccessException -> ()

    let afterDelete = coverageArtifactState rawPath

    { Project = project
      IncludeInRatchet = includeInRatchet
      RawPath = rawPath
      Scope =
        if wasFiltered then
            CoverageRunScope.Partial
        else
            CoverageRunScope.Full
      Before = before
      DeletionProven = afterDelete = Missing }

let internal coverageInputFromObservedState
    (verifiedGreen: bool)
    (launch: CoverageArtifactLaunch)
    (after: CoverageArtifactState)
    : CoverageInput option =
    let isNewArtifact =
        match launch.Before, after with
        | _, Missing
        | _, Unreadable _ -> false
        | Unreadable _, Fingerprinted _ -> launch.DeletionProven
        | Missing, Fingerprinted _ -> true
        | Fingerprinted before, Fingerprinted after -> launch.DeletionProven || before <> after

    if verifiedGreen && isNewArtifact then
        Some
            { Project = launch.Project
              RawPath = launch.RawPath
              IncludeInRatchet = launch.IncludeInRatchet
              Scope = launch.Scope }
    else
        None

let internal coverageReceiptFromObservedState
    (verifiedGreen: bool)
    (launch: CoverageArtifactLaunch)
    (after: CoverageArtifactState)
    : CoverageReceiptOutcome =
    match coverageInputFromObservedState verifiedGreen launch after with
    | Some input -> CoverageReceiptAccepted input
    | None when verifiedGreen && launch.Scope = CoverageRunScope.Full ->
        let reason =
            match after with
            | Missing ->
                $"successful full test run produced no fresh runtime coverage receipt: %s{launch.RawPath} is missing"
            | Unreadable detail ->
                $"successful full test run produced no fresh readable runtime coverage receipt at %s{launch.RawPath}: %s{detail}"
            | Fingerprinted _ ->
                $"successful full test run left an unchanged stale runtime coverage receipt at %s{launch.RawPath}"

        CoverageReceiptFailed
            { Project = launch.Project
              Reason = reason }
    | None -> CoverageReceiptAbsent

/// Turn a launch into an ingest receipt only when the runner finished green and
/// wrote a new artifact. Full receipts are therefore trustworthy replacement
/// baselines; failed, aborted, timed-out and stable-artifact launches contribute
/// no runtime evidence.
let internal coverageInputFromReceipt (verifiedGreen: bool) (launch: CoverageArtifactLaunch) : CoverageInput option =
    coverageInputFromObservedState verifiedGreen launch (coverageArtifactState launch.RawPath)

let internal coverageReceiptFromReceipt
    (verifiedGreen: bool)
    (launch: CoverageArtifactLaunch)
    : CoverageReceiptOutcome =
    coverageReceiptFromObservedState verifiedGreen launch (coverageArtifactState launch.RawPath)

/// Default coverage args template for Microsoft Testing Platform hosts
/// (xUnit v3, MSTest v3 — anything invoked as `dotnet run --project <test>
/// --no-build -- ...`). `{output}` is replaced with the target file path.
[<Literal>]
let defaultCoverageArgsTemplate =
    "--coverage --coverage-output-format cobertura --coverage-output \"{output}\""

[<Literal>]
let private OutputPlaceholder = "{output}"

/// Substitute `{output}` in `paths.ArgsTemplate` with either Baseline or
/// Partial depending on `wasFiltered`. Creates the output dir if missing.
/// Raises if the template is missing the placeholder, rather than silently
/// emitting args the runner will ignore.
let buildCoverageArgs (paths: CoveragePaths) (wasFiltered: bool) : string =
    let target = if wasFiltered then paths.Partial else paths.Baseline

    let dir = Path.GetDirectoryName(target)

    if not (String.IsNullOrEmpty dir) then
        Directory.CreateDirectory(dir) |> ignore

    if not (paths.ArgsTemplate.Contains(OutputPlaceholder)) then
        invalidArg
            "ArgsTemplate"
            (sprintf "coverage args template must contain %s placeholder; got %A" OutputPlaceholder paths.ArgsTemplate)

    paths.ArgsTemplate.Replace(OutputPlaceholder, target)

/// True when most of a run's coverage lines failed to attribute to a symbol — a sign the
/// symbol graph is still being indexed (e.g. the first run after a schema bump recreated the
/// TestPrune DB, before the daemon's scan reached the covered files). A healthy run maps the
/// vast majority of lines (the only misses are rare inter-symbol lines), so requiring at
/// least half to map cleanly separates "still indexing" from a real run.
let internal symbolGraphLooksIncomplete (ingested: int) (skipped: int) : bool =
    ingested + skipped > 0 && ingested < skipped

/// Serially ingest each project's raw runner cobertura into the TestPrune DB
/// for runtime selection and into the plugin-owned project-attributed ratchet
/// table, then emit the eligible projects' aggregate ONCE to the shared
/// cobertura file that downstream gating reads.
///
/// `inputs` retains the producing test project and whether that project belongs
/// to the consumer ratchet. TestPrune.Core's line ratchet stores coverage without
/// project identity, so this local table is the compatibility layer that lets a later
/// configuration transition subtract one project's historical contribution.
///
/// Invariants:
/// - An empty / aborted raw cobertura parses to zero rows → ingests nothing →
///   cannot clobber the DB or the emitted file.
/// - If NO raw inputs exist on disk, the shared cobertura is NOT written, so a
///   prior good emission is never overwritten with nothing.
type CoverageIngestOutcome =
    { Failures: CoverageIngestFailure list }

let internal coverageIngestFailures outcome = outcome.Failures

let internal applyCoverageIngestFailures
    (failures: CoverageIngestFailure list)
    (results: Map<string, TestResult>)
    : Map<string, TestResult> =
    failures
    |> List.fold
        (fun current failure ->
            Map.add
                failure.Project
                (TestsErrored $"runtime coverage receipt could not be ingested: %s{failure.Reason}")
                current)
        results

let internal armRuntimeCoverageUnknownDebt
    (setUnknown: unit -> unit)
    (recoveryPath: string)
    (failure: CoverageIngestFailure)
    =
    let message = $"%s{failure.Project}: %s{failure.Reason}"
    FsHwPaths.atomicWriteAllText recoveryPath message
    setUnknown ()

[<Literal>]
let private ProjectRatchetCoverageTable = "fshw_project_ratchet_coverage"

[<Literal>]
let private ProjectRatchetMetadataTable = "fshw_project_ratchet_metadata"

[<Literal>]
let private ProjectRatchetSchemaVersion = 1

/// A covered line, stored relative to the TestPrune.Core symbol OCCURRENCE (one per
/// declaring file) that it falls under, so the point follows that declaration's moves
/// and is deleted with it.
type private ProjectRatchetCoveragePoint =
    { OccurrenceId: int64
      LineOffset: int
      Hits: int }

type private ProjectRatchetCoverageMapping =
    { Points: ProjectRatchetCoveragePoint list
      Ingested: int
      Skipped: int }

let private ensureProjectRatchetCoverageTable (conn: Microsoft.Data.Sqlite.SqliteConnection) =
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
        $"""CREATE TABLE IF NOT EXISTS %s{ProjectRatchetCoverageTable} (
                project TEXT NOT NULL,
                occurrence_id INTEGER NOT NULL REFERENCES symbol_occurrences(id) ON DELETE CASCADE,
                line_offset INTEGER NOT NULL,
                hits INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (project, occurrence_id, line_offset)
            );
            CREATE INDEX IF NOT EXISTS idx_fshw_project_ratchet_coverage_occurrence
                ON %s{ProjectRatchetCoverageTable} (occurrence_id);
            CREATE TABLE IF NOT EXISTS %s{ProjectRatchetMetadataTable} (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                schema_version INTEGER NOT NULL,
                legacy_output_invalidated INTEGER NOT NULL CHECK (legacy_output_invalidated IN (0, 1))
            );
            INSERT OR IGNORE INTO %s{ProjectRatchetMetadataTable}
                (singleton, schema_version, legacy_output_invalidated)
            SELECT 1,
                   %d{ProjectRatchetSchemaVersion},
                   CASE WHEN EXISTS (SELECT 1 FROM coverage_points LIMIT 1) THEN 0 ELSE 1 END;
            UPDATE %s{ProjectRatchetMetadataTable}
            SET schema_version = %d{ProjectRatchetSchemaVersion},
                legacy_output_invalidated = 0
            WHERE schema_version <> %d{ProjectRatchetSchemaVersion};"""

    cmd.ExecuteNonQuery() |> ignore

/// The f311 ratchet had no project identity. If its Core high-water mark still
/// exists when this plugin table first appears, the shared file cannot be
/// subtracted honestly. Delete it once; a healthy attributed aggregate may be
/// written later in the same call. A Core schema recreation has no legacy
/// coverage points, so the marker starts settled and the cold no-clobber path
/// remains intact.
let private invalidateLegacyRatchetOutput (db: Database) (coverageOutput: string option) =
    match coverageOutput with
    | None -> false
    | Some output ->
        use conn = db.OpenConnection()
        ensureProjectRatchetCoverageTable conn
        use query = conn.CreateCommand()

        query.CommandText <-
            $"SELECT legacy_output_invalidated FROM %s{ProjectRatchetMetadataTable} WHERE singleton = 1;"

        let pending =
            Convert.ToInt32(query.ExecuteScalar(), CultureInfo.InvariantCulture) = 0

        if pending then
            // Commit the marker only AFTER deletion. A crash between these two
            // operations retries the idempotent delete instead of accepting a
            // stale projectless artifact as migrated.
            File.Delete output
            use update = conn.CreateCommand()

            update.CommandText <-
                $"UPDATE %s{ProjectRatchetMetadataTable} SET legacy_output_invalidated = 1 WHERE singleton = 1;"

            update.ExecuteNonQuery() |> ignore

        pending

let private mapProjectRatchetCoverage
    (conn: Microsoft.Data.Sqlite.SqliteConnection)
    (transaction: Microsoft.Data.Sqlite.SqliteTransaction)
    (repoRoot: string)
    (xml: string)
    =
    let rows = parseCobertura xml
    use lookup = conn.CreateCommand()
    lookup.Transaction <- transaction

    let normalizeSourceFile (filename: string) =
        (if Path.IsPathRooted filename then
             Path.GetRelativePath(repoRoot, filename)
         else
             filename)
            .Replace('\\', '/')

    // Match TestPrune.Core's line-to-symbol anchor: the nearest declaration at
    // or before the covered line, in THIS file (a symbol declared in a signature and
    // an implementation has one occurrence in each). The stored offset then follows
    // that occurrence's moves.
    lookup.CommandText <-
        """SELECT id, @line - line_start
           FROM symbol_occurrences
           WHERE source_file = @file AND line_start <= @line
           ORDER BY line_start DESC
           LIMIT 1;"""

    let points, ingested, skipped =
        ((Map.empty, 0, 0), rows)
        ||> List.fold (fun (points, ingested, skipped) (filename, line, hits) ->
            let sourceFile = normalizeSourceFile filename
            lookup.Parameters.Clear()
            lookup.Parameters.AddWithValue("@file", sourceFile) |> ignore
            lookup.Parameters.AddWithValue("@line", line) |> ignore

            use reader = lookup.ExecuteReader()

            if reader.Read() then
                let key = reader.GetInt64(0), reader.GetInt32(1)
                let prior = Map.tryFind key points |> Option.defaultValue 0
                Map.add key (max prior hits) points, ingested + 1, skipped
            else
                points, ingested, skipped + 1)

    { Points =
        points
        |> Map.toList
        |> List.map (fun ((occurrenceId, lineOffset), hits) ->
            { OccurrenceId = occurrenceId
              LineOffset = lineOffset
              Hits = hits })
      Ingested = ingested
      Skipped = skipped }

let internal persistProjectRatchetCoverageWithMapped
    (afterMapped: unit -> unit)
    (db: Database)
    (repoRoot: string)
    (input: CoverageInput)
    (xml: string)
    =
    use conn = db.OpenConnection()
    ensureProjectRatchetCoverageTable conn
    // The ids `mapProjectRatchetCoverage` returns are foreign keys into
    // `symbol_occurrences`. Look them up inside the same IMMEDIATE (write-locked)
    // transaction that persists them: on a separate read, a concurrent graph rebuild
    // could delete a mapped occurrence before the write, and the insert then fails with
    // SQLite error 19 (FOREIGN KEY constraint).
    use transaction = conn.BeginTransaction(deferred = false)
    let mapped = mapProjectRatchetCoverage conn transaction repoRoot xml
    afterMapped ()

    let replaceFull =
        input.Scope = CoverageRunScope.Full
        && mapped.Ingested + mapped.Skipped > 0
        && not (symbolGraphLooksIncomplete mapped.Ingested mapped.Skipped)

    if replaceFull || input.Scope = CoverageRunScope.Partial then
        if replaceFull then
            use delete = conn.CreateCommand()
            delete.Transaction <- transaction
            delete.CommandText <- $"DELETE FROM %s{ProjectRatchetCoverageTable} WHERE project = @project;"
            delete.Parameters.AddWithValue("@project", input.Project) |> ignore
            delete.ExecuteNonQuery() |> ignore

        use upsert = conn.CreateCommand()
        upsert.Transaction <- transaction

        upsert.CommandText <-
            $"""INSERT INTO %s{ProjectRatchetCoverageTable} (project, occurrence_id, line_offset, hits)
                VALUES (@project, @occurrence, @offset, @hits)
                ON CONFLICT(project, occurrence_id, line_offset)
                DO UPDATE SET hits = MAX(hits, excluded.hits);"""

        for point in mapped.Points do
            upsert.Parameters.Clear()
            upsert.Parameters.AddWithValue("@project", input.Project) |> ignore
            upsert.Parameters.AddWithValue("@occurrence", point.OccurrenceId) |> ignore
            upsert.Parameters.AddWithValue("@offset", point.LineOffset) |> ignore
            upsert.Parameters.AddWithValue("@hits", point.Hits) |> ignore
            upsert.ExecuteNonQuery() |> ignore

    transaction.Commit()

let private persistProjectRatchetCoverage (db: Database) (repoRoot: string) (input: CoverageInput) (xml: string) =
    persistProjectRatchetCoverageWithMapped ignore db repoRoot input xml

let private removeProjectRatchetCoverage (db: Database) (project: string) =
    use conn = db.OpenConnection()
    ensureProjectRatchetCoverageTable conn
    use cmd = conn.CreateCommand()
    cmd.CommandText <- $"DELETE FROM %s{ProjectRatchetCoverageTable} WHERE project = @project;"
    cmd.Parameters.AddWithValue("@project", project) |> ignore
    cmd.ExecuteNonQuery()

let private pruneProjectRatchetCoverage (db: Database) (enabledProjects: Set<string>) =
    use conn = db.OpenConnection()
    ensureProjectRatchetCoverageTable conn

    let storedProjects =
        use query = conn.CreateCommand()
        query.CommandText <- $"SELECT DISTINCT project FROM %s{ProjectRatchetCoverageTable};"
        use reader = query.ExecuteReader()
        let projects = ResizeArray<string>()

        while reader.Read() do
            projects.Add(reader.GetString 0)

        projects |> Seq.toList

    storedProjects
    |> List.filter (fun project -> not (Set.contains project enabledProjects))
    |> List.sumBy (fun project ->
        use delete = conn.CreateCommand()
        delete.CommandText <- $"DELETE FROM %s{ProjectRatchetCoverageTable} WHERE project = @project;"
        delete.Parameters.AddWithValue("@project", project) |> ignore
        delete.ExecuteNonQuery())

let private projectRatchetCobertura (db: Database) : string option =
    use conn = db.OpenConnection()
    ensureProjectRatchetCoverageTable conn
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
        $"""SELECT o.source_file,
                   o.line_start + c.line_offset AS absolute_line,
                   MAX(c.hits)
            FROM %s{ProjectRatchetCoverageTable} c
            JOIN symbol_occurrences o ON o.id = c.occurrence_id
            GROUP BY o.source_file, o.line_start + c.line_offset
            ORDER BY o.source_file, absolute_line;"""

    use reader = cmd.ExecuteReader()
    let points = ResizeArray<string * int * int>()

    while reader.Read() do
        points.Add(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2))

    if points.Count = 0 then
        None
    else
        let element name = XElement(XName.Get name)

        let setAttr name value (node: XElement) =
            node.SetAttributeValue(XName.Get name, value)

        let root = element "coverage"
        let packages = element "packages"
        let package = element "package"
        let classes = element "classes"
        let total = points.Count
        let covered = points |> Seq.filter (fun (_, _, hits) -> hits > 0) |> Seq.length

        let rate = (float covered / float total).ToString("R", CultureInfo.InvariantCulture)

        setAttr "line-rate" rate root
        setAttr "branch-rate" "0" root
        setAttr "lines-covered" covered root
        setAttr "lines-valid" total root
        setAttr "name" "runtime" package
        setAttr "line-rate" rate package
        setAttr "branch-rate" "0" package

        points
        |> Seq.groupBy (fun (file, _, _) -> file)
        |> Seq.iter (fun (file, filePoints) ->
            let filePoints = filePoints |> Seq.toList
            let classNode = element "class"
            let linesNode = element "lines"

            let fileCovered =
                filePoints |> List.filter (fun (_, _, hits) -> hits > 0) |> List.length

            let fileRate =
                (float fileCovered / float filePoints.Length).ToString("R", CultureInfo.InvariantCulture)

            setAttr "name" file classNode
            setAttr "filename" file classNode
            setAttr "line-rate" fileRate classNode
            setAttr "branch-rate" "0" classNode

            for _, line, hits in filePoints do
                let lineNode = element "line"
                setAttr "number" line lineNode
                setAttr "hits" hits lineNode
                setAttr "branch" "false" lineNode
                linesNode.Add lineNode

            classNode.Add linesNode
            classes.Add classNode)

        package.Add classes
        packages.Add package
        root.Add packages
        Some(XDocument(root).ToString(SaveOptions.DisableFormatting))

let internal invalidateCoverageReceiptFailure (db: Database) (failure: CoverageIngestFailure) =
    db.InvalidateRuntimeCoverage failure.Project
    removeProjectRatchetCoverage db failure.Project |> ignore

let internal ingestAndEmitCoverageForProjects
    (db: Database)
    (repoRoot: string)
    (runId: string)
    (enabledRatchetProjects: Set<string>)
    (coverageOutput: string option)
    (inputs: CoverageInput list)
    : CoverageIngestOutcome =
    let existing = inputs |> List.filter (fun input -> File.Exists input.RawPath)

    let legacyOutputInvalidated =
        if coverageOutput.IsNone && inputs.IsEmpty && Set.isEmpty enabledRatchetProjects then
            false
        else
            invalidateLegacyRatchetOutput db coverageOutput

    let removedContributions =
        if coverageOutput.IsNone && inputs.IsEmpty && Set.isEmpty enabledRatchetProjects then
            0
        else
            pruneProjectRatchetCoverage db enabledRatchetProjects

    let accepted, failures =
        (([], []), existing)
        ||> List.fold (fun (accepted, failures) input ->
            try
                let xml = File.ReadAllText input.RawPath

                // Runtime selection needs every configured project's provenance,
                // including projects excluded from the consumer ratchet.
                ingestRuntimeCoverage db (Some repoRoot) input.Project input.Scope runId xml
                |> ignore

                let ratchetResult =
                    if input.IncludeInRatchet && Set.contains input.Project enabledRatchetProjects then
                        let result = ingestCobertura db (Some repoRoot) xml
                        persistProjectRatchetCoverage db repoRoot input xml
                        Some result
                    else
                        removeProjectRatchetCoverage db input.Project |> ignore
                        None

                (input, ratchetResult) :: accepted, failures
            with ex ->
                // A malformed FULL receipt has the same replacement semantics as
                // a missing one: neither runtime selection nor the consumer ratchet
                // may retain this project's prior complete evidence.
                if input.Scope = CoverageRunScope.Full then
                    try
                        removeProjectRatchetCoverage db input.Project |> ignore
                    with cleanupEx ->
                        Logging.error
                            "test-prune"
                            $"failed to revoke prior ratchet coverage for %s{input.Project}: %s{cleanupEx.Message}"

                let failure =
                    { Project = input.Project
                      Reason = $"%s{ex.GetType().Name}: %s{ex.Message}" }

                Logging.error
                    "test-prune"
                    $"runtime coverage post-processing failed for %s{input.Project}: %s{failure.Reason}"

                accepted, failure :: failures)

    let ratchetInputs =
        accepted
        |> List.choose (fun (input, result) -> result |> Option.map (fun r -> input, r))

    let results = ratchetInputs |> List.map snd

    try

        let totalIngested = results |> List.sumBy (fun r -> r.Ingested)
        let totalSkipped = results |> List.sumBy (fun r -> r.Skipped)

        // Cold-start guard: emitting while the graph is still indexing writes a partial
        // cobertura that DROPS every not-yet-indexed file's coverage, clobbering a prior good
        // emission and failing the ratchet. Skip — the DB persists and max-merges, so a later
        // warm run emits in full.
        match coverageOutput with
        | None -> ()
        | Some _ when
            not legacyOutputInvalidated
            && removedContributions = 0
            && not ratchetInputs.IsEmpty
            && symbolGraphLooksIncomplete totalIngested totalSkipped
            ->
            Logging.warn
                "test-prune"
                $"coverage: only %d{totalIngested} of %d{totalIngested + totalSkipped} lines mapped to a symbol — symbol graph still indexing; skipping emit to avoid a partial snapshot (will emit once warm)."
        | Some out ->
            match projectRatchetCobertura db with
            | Some xml ->
                let dir = Path.GetDirectoryName(out)

                if not (String.IsNullOrEmpty dir) then
                    Directory.CreateDirectory(dir) |> ignore

                File.WriteAllText(out, xml)
            | None when
                legacyOutputInvalidated
                || Set.isEmpty enabledRatchetProjects
                || removedContributions > 0
                ->
                // An old shared file has no project identity. Once the eligible set
                // shrinks, preserving that file would preserve the removed project's
                // contribution. Absence is the only honest empty aggregate.
                File.Delete out
            | None -> ()
    with ex ->
        Logging.error "test-prune" $"coverage emission failed: %s{ex.Message}"

    { Failures = List.rev failures }

let internal ingestAndEmitCoverage
    (db: Database)
    (repoRoot: string)
    (runId: string)
    (coverageOutput: string option)
    (inputs: CoverageInput list)
    : CoverageIngestOutcome =
    let enabledProjects =
        inputs
        |> List.choose (fun input -> if input.IncludeInRatchet then Some input.Project else None)
        |> Set.ofList

    ingestAndEmitCoverageForProjects db repoRoot runId enabledProjects coverageOutput inputs

/// How fshw obtains the structured pass/fail report a test verdict is derived
/// from. The report (CTRF) — not the process exit code — is authoritative, but
/// only a runner that actually emits a parseable report can be trusted that
/// way, and an UNSUPPORTED `--report-*` flag is fatal (the runner exits
/// "invalid command line" and runs nothing). So injection of the report flag is
/// scoped by this setting.
type ReportVerificationFormat =
    /// Inject the matching CTRF switches iff the restored project graph resolves
    /// to a supported xUnit 3 or 4 runner. The default.
    | AutoDetect
    /// Always inject CTRF switches (force-on for a capable custom runner the
    /// detector misses). Unknown custom runners retain the xUnit 3 switch family.
    | Ctrf
    /// Never inject a report flag — the process exit code is authoritative
    /// (force-off; e.g. a custom runner that would error on `--report-ctrf`).
    | Disabled

// ─────────────────────────────────────────────────────────────────────────────
// a run may clear ONLY what it covered.
//
// "No failures reported by THIS run" is not "no failures". A full run failed project X;
// a queued impact-filtered re-run then executed a NARROWER selection, passed, and — via
// `ClearAllErrors` + last-cycle-wins — superseded X's red. X never re-ran, yet the check
// went green.
//
// So: a run carries the SELECTION it was launched against, a completed run's COVERAGE is
// that selection intersected with what actually executed, and clearing is a total
// function over that coverage. A filtered run cannot express "clear everything". A red
// survives every run that did not execute it, and dies the moment one that did executes
// it green.
// ─────────────────────────────────────────────────────────────────────────────

/// What a run was LAUNCHED against, per test project — captured at dispatch
/// (`TestRunLaunch.Selection`), never re-derived at completion, because by then the
/// selection inputs have moved on. A project ABSENT from the selection map was not
/// launched at all: impact analysis skipped it (and `executeTests` records the skip
/// as `TestsPassed("", filtered, 0)`, a pass that proves precisely nothing).
type ProjectSelection =
    /// Launched with no class filter — every test in the project was asked to run.
    | ProjectInFull
    /// Launched under a class filter — only these classes were asked to run.
    | ProjectClasses of Set<string>

/// Changed symbols that DO have covering tests — in a test project
/// `tests.projects` does not list, so this daemon can never run them. Keyed by symbol,
/// valued by the unlisted projects holding its only covering tests.
///
/// Dropped from the pending queue by the same rule as a symbol with no test at all
/// (retaining it wedges the queue forever), but never SILENTLY: the
/// obligation is written off here, and the write-off is what the verdict has to say.
/// A reader who sees "no covering test" for a symbol that has one in an unlisted
/// project concludes the analyzer is broken; a reader who sees the project name can
/// decide whether to list it or declare it excluded.
type UnrunnableCoverage = Map<string, Set<string>>

module UnrunnableCoverage =
    let projects (unrunnable: UnrunnableCoverage) : Set<string> =
        unrunnable |> Map.values |> Set.unionMany

[<RequireQualifiedAccess>]
type UncoveredChanges =
    | No
    /// Every symbol was dropped: `symbols` names them all, `unrunnable` the subset
    /// that had covering tests this daemon cannot run.
    | AllUncovered of symbols: string list * unrunnable: UnrunnableCoverage

module UncoveredChanges =
    let isAll =
        function
        | UncoveredChanges.No -> false
        | UncoveredChanges.AllUncovered _ -> true

[<RequireQualifiedAccess>]
type ZeroSelection =
    | NotAZero
    | AlreadyVerified
    | ChangesUncovered of symbols: string list * unrunnable: UnrunnableCoverage

module ZeroSelection =
    let token =
        function
        | ZeroSelection.NotAZero -> None
        | ZeroSelection.AlreadyVerified -> Some "already-verified"
        | ZeroSelection.ChangesUncovered _ -> Some "changes-uncovered"

    let symbols =
        function
        | ZeroSelection.ChangesUncovered(symbols, _) -> symbols
        | ZeroSelection.NotAZero
        | ZeroSelection.AlreadyVerified -> []

    let unrunnable =
        function
        | ZeroSelection.ChangesUncovered(_, unrunnable) -> unrunnable
        | ZeroSelection.NotAZero
        | ZeroSelection.AlreadyVerified -> Map.empty

[<RequireQualifiedAccess>]
type MissCause =
    | ProjectNotSelected
    | ClassNotInFilter

type MissedFailure =
    { Project: string
      Class: string
      Cause: MissCause }

/// Would the impact selection `check` WOULD have used have EXECUTED a
/// test this run saw fail?
///
/// The question `confirm` destroys by construction. `confirm` sends `set-scope full`
/// BEFORE the scan that provokes the run, so the run is unfiltered and the impact
/// selection — still computed, still correct — is discarded at the widening. Retaining
/// it and asking this one question of the run's OWN failures turns every confirm into a
/// same-tree, same-daemon, same-instant sample of the question impact selection rests on: does
/// the selector choose the test that fails?
///
/// It answers ONLY about REACH. A test that fails here failed in a full suite; whether it
/// would also have failed alone — order, isolation, a shared fixture — is not observable
/// from one run and is never claimed. That is why the wire records the sample's BASIS.
type CheckReach =
    /// At least one test this run saw fail is inside the retained selection. `check`
    /// would have executed it, so `check` would have been red too.
    | ReachedAFailure of projects: string list
    /// This run saw tests fail and the retained selection reaches NONE of them. The
    /// selector did not choose a failing test — the case the whole record exists for.
    | ReachedNoFailure of missed: MissedFailure list
    /// This run saw no test fail at all, so there was no failure for a selection to
    /// reach. NOT the same as `ReachedNoFailure`: nothing was missed, because nothing
    /// was there.
    | NoFailuresToReach
    /// The reach could not be decided. A project-level red (a timeout, an errored host,
    /// unparseable failure output) names no class, and a class-FILTERED selection cannot
    /// be asked whether it reaches a failure with no class — so it is refused rather
    /// than guessed. Every unknown must stay an unknown: a guess in the reaching
    /// direction manufactures an agreement that was never compared.
    | ReachUnknown of reason: string

/// Exact recall among failures observed by one full `confirm` run. This is deliberately
/// not called general test-selection recall: passing tests provide no oracle for whether
/// they were behaviorally relevant. The correctness threshold is 100% because one
/// observed full-suite failure outside `check`'s retained selection is a stale green.
type FailureRecall =
    | RecallMeasured of reached: int * total: int * threshold: float * acceptable: bool
    | RecallNotMeasurable of reason: string

/// What a completed run may VINDICATE in one project — the honest reach of the
/// evidence it produced. Absent from `RunCoverage` ⇒ this run says nothing at all
/// about the project (it was skipped, it never ran, or it ran under a filter whose
/// reach we cannot know).
type ProjectCoverage =
    /// The project executed with NO filter: its green speaks for every test in it,
    /// so it may clear any red the project holds.
    | CoveredWholeProject
    /// The project executed only these classes: its green speaks for them and
    /// nothing else.
    | CoveredClasses of Set<string>

/// What a completed run is entitled to clear, keyed by test project.
type RunCoverage = Map<string, ProjectCoverage>

/// A file the symbol analyser could not read, retained until it
/// analyses cleanly. Carries everything its ledger entry needs, because the entry has to
/// be RE-REPORTED after every test run: the run's ledger rewrite clears this plugin's
/// whole slice, and a warning erased by a cycle that never addressed it is the same
/// defect as a red erased by a run that never executed it.
type UnanalyzableFile =
    {
        /// Repo-relative path — what the diagnostic names.
        RelPath: string
        /// Absolute path — the ledger key.
        File: string
        /// Why analysis failed (the FCS/parse error), carried into the diagnostic.
        Reason: string
    }

/// Drop the unanalysable-file entries whose file is no longer on disk.
///
/// An entry leaves `UnanalyzableFiles` when the file analyses
/// CLEANLY — and a DELETED file never analyses again, because no `FileChecked` will
/// ever arrive for a path that is gone. So one deleted file left its warning in the
/// ledger for the rest of the daemon's life, and under the default warn-fail policy
/// that warning denied every subsequent check its green while ALSO widening every run
/// to the whole suite (the unanalysable-file coarse fallback). Deleting a file is not a
/// defect in the tree, and there is nothing left to fix.
///
/// This is the ONLY other way out, and it is deliberately narrow: the condition
/// "TestPrune cannot see this file's symbols" is discharged by the file ceasing to
/// exist, not merely by time passing. An entry whose file is still there survives
/// untouched, however old.
///
/// `exists` is a parameter so the rule is testable without a filesystem, and so the
/// production caller names the one predicate it uses.
let internal pruneDeletedUnanalyzable
    (exists: string -> bool)
    (files: Map<string, UnanalyzableFile>)
    : Map<string, UnanalyzableFile> =
    files |> Map.filter (fun _ u -> exists u.File)

/// A red this plugin still OWES the user: a test failure (or a project that produced
/// no verdict at all) that no run COVERING it has passed since.
///
/// The shared `ErrorLedger` is a pure projection of the outstanding list — the
/// `TestsFinished` handler rewrites it wholesale each cycle (`ClearAllErrors` +
/// re-report the whole set) — so `fshw errors` shows exactly what is outstanding:
/// never a superseded red, and never a laundered one.
type OutstandingFailure =
    {
        /// The test project the red belongs to.
        Project: string
        /// The failing test CLASS when the runner named one; `None` for a
        /// project-level red (unparseable failure output, timeout, errored,
        /// deferred). A project-level red is only clearable by a run that executed
        /// the project in FULL.
        Class: string option
        /// Exact failing method from the runner receipt. Filtered reconciliation
        /// requires this same method to appear as passed in a complete CTRF report;
        /// a different passing method in the class cannot contradict this red.
        Method: string option
        /// The ledger key: the class's source file, or the synthetic
        /// `<tests/Project>` bucket when no source file is known.
        File: string
        Entry: ErrorLedger.ErrorEntry
    }

/// Configuration for a test project to run.
type TestConfig =
    {
        Project: string
        Command: string
        Args: string
        Group: string
        Environment: (string * string) list
        /// Template for class-based test filtering. {classes} is replaced with
        /// the joined class names. Example: "-- --filter-class {classes}"
        FilterTemplate: string option
        /// Separator for joining class names in the filter. Default: " "
        /// Example: "|ClassName=" for dotnet test --filter "ClassName=A|ClassName=B"
        ClassJoin: string
        /// Per-project timeout in seconds. None → use top-level default.
        TimeoutSec: int option
        /// How to obtain the structured test report for the verdict. Default
        /// `AutoDetect`. Override via `.fshw.json` `reportVerificationFormat`.
        ReportVerificationFormat: ReportVerificationFormat
    }

/// Why runtime evidence could not safely exclude a configured project.
type RuntimeCoverageWideningReason =
    | MissingBaseline
    | StaleBaseline of observedAt: DateTimeOffset

type RuntimeCoverageSelection =
    { ProjectsByFile: Map<string, Set<string>>
      Widenings: (string * RuntimeCoverageWideningReason) list }

[<CLIMutable>]
type RuntimeCoverageObligationProjectDto = { Project: string; Generation: int64 }

[<CLIMutable>]
type RuntimeCoverageObligationDto =
    { SourceFile: string
      Projects: RuntimeCoverageObligationProjectDto array }

type RuntimeCoverageObligations = Map<string, Map<string, int64>>

let internal runtimeCoverageObligationsPath repoRoot =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "runtime-coverage-obligations.json")

let internal runtimeCoverageRecoveryPath repoRoot =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "runtime-coverage-obligations.recovery")

let internal loadRuntimeCoverageObligations repoRoot : Result<RuntimeCoverageObligations, string> =
    let path = runtimeCoverageObligationsPath repoRoot

    if File.Exists(runtimeCoverageRecoveryPath repoRoot) then
        Error "a prior runtime obligation write did not complete; the debt is unknown"
    elif not (File.Exists path) then
        Ok Map.empty
    else
        try
            let rows =
                JsonSerializer.Deserialize<RuntimeCoverageObligationDto array>(File.ReadAllText path)

            if isNull rows then
                Error "the runtime coverage obligation ledger is JSON null"
            else
                rows
                |> Array.map (fun row ->
                    row.SourceFile,
                    (row.Projects
                     |> Array.map (fun project -> project.Project, project.Generation)
                     |> Map.ofArray))
                |> Map.ofArray
                |> Ok
        with ex ->
            Error $"%s{ex.GetType().Name}: %s{ex.Message}"

let internal saveRuntimeCoverageObligations repoRoot (obligations: RuntimeCoverageObligations) =
    let rows =
        obligations
        |> Map.toArray
        |> Array.map (fun (file, projects) ->
            { SourceFile = file
              Projects =
                projects
                |> Map.toArray
                |> Array.sortBy fst
                |> Array.map (fun (project, generation) ->
                    { Project = project
                      Generation = generation }) })

    FsHwPaths.atomicWriteAllText (runtimeCoverageObligationsPath repoRoot) (JsonSerializer.Serialize rows)

let internal persistRuntimeCoverageObligationsWith
    (writeRecoveryMarker: unit -> unit)
    (save: unit -> unit)
    (clearRecoveryMarker: unit -> unit)
    : Result<unit, exn> =
    try
        writeRecoveryMarker ()
        save ()
        clearRecoveryMarker ()
        Ok()
    with ex ->
        Error ex

let internal persistRuntimeCoverageTransitionWith
    (writeRecoveryMarker: unit -> unit)
    (save: RuntimeCoverageObligations -> unit)
    (clearRecoveryMarker: unit -> unit)
    (current: RuntimeCoverageObligations)
    (transition: RuntimeCoverageObligations -> RuntimeCoverageObligations)
    : Result<RuntimeCoverageObligations, RuntimeCoverageObligations * exn> =
    try
        // The recovery signal must be durable before the transition is even
        // computed, let alone accepted in memory. If phase one fails, restart
        // may only know the prior ledger, so the prior state is the only state
        // this daemon is allowed to retain.
        writeRecoveryMarker ()
        let next = transition current
        save next
        clearRecoveryMarker ()
        Ok next
    with ex ->
        Error(current, ex)

/// An obligation that names NO project is not a debt, and admitting one
/// is the silent full-suite escalation this guard closes.
///
/// `selectByRuntimeCoverage` maps EVERY changed file — `Set.union current
/// widenedProjects` is empty whenever nothing attributes runtime coverage to that file
/// and every configured project's baseline is current, which is the ordinary case for
/// an edit to a file no runtime tracer ever reached. The caller merges the selection
/// whenever the MAP is non-empty, so those files used to enter the ledger as
/// `file -> map []`. The result satisfies neither of the two questions asked of it:
///
///   * `nothingOwed` asks `Map.isEmpty`, and reads a non-empty map — so for the rest of
///     the session the zero-affected green skip and the stale-rerun guard are both
///     refused, and every cycle that selects nothing runs tests anyway;
///   * `runtimeForceProjects` asks for the projects the ledger NAMES, and gets none — so
///     the run it forces has an EMPTY selection, and an empty selection means every
///     configured project, in full.
///
/// A debt nothing can select and nothing can discharge, costing a whole suite. The
/// observation that a file changed is not itself an obligation: only a file with at
/// least one obligated project may enter the ledger, and only the run that covered it
/// may take it out again.
let internal mergeRuntimeCoverageObligations
    (existing: RuntimeCoverageObligations)
    (incoming: Map<string, Set<string>>)
    =
    (existing, incoming)
    ||> Map.fold (fun acc file projects ->
        if Set.isEmpty projects then
            // Nothing to owe for this file. Leave any PRIOR obligation for it exactly
            // as it stands: a real debt is discharged by the run that covers it
            // (`retireRuntimeCoverageObligations`), never by a later cycle observing
            // the same file and finding nothing to add.
            acc
        else
            let prior = Map.tryFind file acc |> Option.defaultValue Map.empty

            let next =
                (prior, projects)
                ||> Set.fold (fun obligations project ->
                    let generation = Map.tryFind project obligations |> Option.defaultValue 0L
                    Map.add project (generation + 1L) obligations)

            Map.add file next acc)

let internal retireRuntimeCoverageObligations
    (current: RuntimeCoverageObligations)
    (launched: RuntimeCoverageObligations)
    (projectPassed: string -> bool)
    =
    (current, launched)
    ||> Map.fold (fun obligations file launchedProjects ->
        match Map.tryFind file obligations with
        | None -> obligations
        | Some currentProjects ->
            let remaining =
                (currentProjects, launchedProjects)
                ||> Map.fold (fun projects project launchedGeneration ->
                    match Map.tryFind project projects with
                    | Some currentGeneration when currentGeneration = launchedGeneration && projectPassed project ->
                        Map.remove project projects
                    | _ -> projects)

            if Map.isEmpty remaining then
                Map.remove file obligations
            else
                Map.add file remaining obligations)

let internal pruneRuntimeCoverageObligations (allowedProjects: Set<string>) (obligations: RuntimeCoverageObligations) =
    obligations
    |> Map.map (fun _ projects -> projects |> Map.filter (fun project _ -> Set.contains project allowedProjects))
    |> Map.filter (fun _ projects -> not (Map.isEmpty projects))

/// A complete runtime baseline remains useful across ordinary incremental runs,
/// but not indefinitely. Missing or older evidence widens to the whole project.
let internal RuntimeCoverageMaxAge = TimeSpan.FromDays 7.0

let internal selectByRuntimeCoverage
    (db: Database)
    (expectedProjects: string list)
    (changedFiles: string list)
    (staleBefore: DateTimeOffset)
    : RuntimeCoverageSelection =
    let widenings =
        db.GetRuntimeCoverageAvailability(expectedProjects, staleBefore)
        |> List.choose (fun (project, availability) ->
            match availability with
            | RuntimeCoverageAvailability.Current -> None
            | RuntimeCoverageAvailability.Missing -> Some(project, MissingBaseline)
            | RuntimeCoverageAvailability.Stale observedAt -> Some(project, StaleBaseline observedAt))

    let expectedProjectSet = Set.ofList expectedProjects
    let widenedProjects = widenings |> List.map fst |> Set.ofList

    let currentByFile =
        db.GetRuntimeCoverageAttributions(changedFiles)
        |> List.filter (fun (_, project) -> Set.contains project expectedProjectSet)
        |> List.groupBy fst
        |> List.map (fun (file, edges) -> file, edges |> List.map snd |> Set.ofList)
        |> Map.ofList

    let projectsByFile =
        changedFiles
        |> List.distinct
        |> List.map (fun file ->
            let current = Map.tryFind file currentByFile |> Option.defaultValue Set.empty
            file, Set.union current widenedProjects)
        |> Map.ofList

    { ProjectsByFile = projectsByFile
      Widenings = widenings }

let internal reportRuntimeCoverageWidenings (warn: string -> unit) (selection: RuntimeCoverageSelection) =
    for project, reason in selection.Widenings do
        match reason with
        | MissingBaseline ->
            warn
                $"runtime coverage: project '%s{project}' has no complete baseline; widening to every test in that configured project"
        | StaleBaseline observedAt ->
            warn
                $"runtime coverage: project '%s{project}' complete baseline from %O{observedAt} is older than %O{RuntimeCoverageMaxAge}; widening to every test in that configured project"

/// Why a cycle that found ZERO symbol-affected classes is running tests
/// anyway — one arm per debt that can refuse the zero-affected green skip.
///
/// The single sentence this replaces ("No affected classes (cold start / pending queue)
/// — running all tests") named two causes for five, was printed for runs that were
/// neither, and claimed "all tests" for runs that were a handful of force-run projects.
/// A widening nobody can attribute is a widening nobody measures: 404 of 1,221 launches
/// in one measured log sample took that line, and the line is the only record any of
/// them left.
[<RequireQualifiedAccess>]
type internal ZeroAffectedWidening =
    /// No run has completed in this session, so there is nothing for this tree to be
    /// test-equivalent TO. Expected exactly once per session — the baseline run.
    | NoSessionBaseline
    /// Symbols are still awaiting a green covering run.
    | QueuedSymbols of count: int
    /// Runtime-coverage obligations are outstanding over `files` file(s), naming
    /// `projects` project(s) between them.
    | RuntimeCoverageDebt of files: int * projects: int
    /// The pending-verification ledger could not be read, so what is owed is unknown
    /// and only a full suite can prove it.
    | UnreadableLedger
    /// A prior red is outstanding and must be re-executed before anything goes green.
    | OutstandingFailures of count: int
    /// No full-suite baseline vouches for what a filtered run would
    /// skip — none recorded, or it never executed a project configured since.
    | NoFullSuiteBaseline of reason: string

module internal ZeroAffectedWidening =
    let describe (cause: ZeroAffectedWidening) =
        match cause with
        | ZeroAffectedWidening.NoSessionBaseline ->
            "no run has completed in this session yet (no baseline to be equivalent to)"
        | ZeroAffectedWidening.NoFullSuiteBaseline reason -> reason
        | ZeroAffectedWidening.QueuedSymbols count -> $"%d{count} symbol(s) still awaiting a green covering run"
        | ZeroAffectedWidening.RuntimeCoverageDebt(files, projects) ->
            $"runtime-coverage debt over %d{files} file(s) naming %d{projects} project(s)"
        | ZeroAffectedWidening.UnreadableLedger ->
            "the pending-verification ledger could not be read, so what is owed is UNKNOWN"
        | ZeroAffectedWidening.OutstandingFailures count -> $"%d{count} outstanding test failure(s) from a prior run"

    let describeMany (causes: ZeroAffectedWidening list) =
        causes |> List.map describe |> String.concat "; "

/// Every reason this run is not taking the zero-affected skip, named and counted.
///
/// Returns EVERY applicable cause rather than the first: two debts outstanding at once
/// is the case where a reader picks the wrong one and goes looking for a bug in the
/// selector. An EMPTY list is the finding that matters most — nothing is owed and a
/// baseline exists, so the skip should have fired and did not. The caller says that out
/// loud instead of running a whole suite quietly, which is how the phantom
/// obligation survived 12 full-suite reruns without leaving a single attributable line.
let internal zeroAffectedWidening
    (hasSessionBaseline: bool)
    (ledgerUnreadable: bool)
    (queuedSymbols: int)
    (runtimeObligations: RuntimeCoverageObligations)
    (outstandingFailures: int)
    (fullSuiteBaselineInvalid: string option)
    : ZeroAffectedWidening list =
    [ if not hasSessionBaseline then
          ZeroAffectedWidening.NoSessionBaseline
      if ledgerUnreadable then
          ZeroAffectedWidening.UnreadableLedger
      // An unreadable ledger already invalidates the baseline; naming it twice would
      // report two debts for one cause.
      match fullSuiteBaselineInvalid with
      | Some reason when not ledgerUnreadable -> ZeroAffectedWidening.NoFullSuiteBaseline reason
      | _ -> ()
      if queuedSymbols > 0 then
          ZeroAffectedWidening.QueuedSymbols queuedSymbols
      // Counted through the projects it NAMES, never through `Map.isEmpty`: a file
      // entry naming no project selects nothing, so reporting it as debt would restate
      // the very confusion this function exists to end.
      let namedProjects =
          runtimeObligations |> Map.values |> Seq.collect Map.keys |> Set.ofSeq

      if not (Set.isEmpty namedProjects) then
          let files =
              runtimeObligations |> Map.filter (fun _ ps -> not (Map.isEmpty ps)) |> Map.count

          ZeroAffectedWidening.RuntimeCoverageDebt(files, Set.count namedProjects)

      if outstandingFailures > 0 then
          ZeroAffectedWidening.OutstandingFailures outstandingFailures ]

type AffectedTestsState =
    | NotYetAnalyzed
    | Analyzed of TestMethodInfo list

module internal ReceiptInputTree =
    /// A receipt identity, not a new verdict hash scheme. Use the core input walk,
    /// conservatively including config-excluded source files too. Refuse holes and
    /// unreadable bytes rather than letting an unhashable sentinel authorize reuse.
    let read repoRoot =
        try
            let walked = TreeHash.files repoRoot []

            let entries =
                walked.Files |> List.map (fun (rel, path) -> rel, ContentHash.ofFile path)

            if
                not (Directory.Exists repoRoot)
                || not walked.Skipped.IsEmpty
                || (entries |> List.exists (snd >> ContentHash.isReadable >> not))
            then
                None
            else
                let absent =
                    walked.AbsentDeclarations
                    |> List.map (fun rel -> VerdictInputs.SentinelPrefix + rel, VerdictInputs.AbsentDeclaration)

                Some(TreeHash.hashEntries (List.sortBy fst (entries @ absent)))
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? JsonException -> None

    let matches expected current =
        match expected, current with
        | Some before, Some after -> String.Equals(before, after, StringComparison.Ordinal)
        | _ -> false

    /// Why `matches` said no. It says no for THREE situations with three different
    /// remedies, and one message covered all of them — so a reader who saw a
    /// revocation had to establish by separate investigation whether the tree had
    /// actually moved. On one occasion that was an hour of checking `jj status` and
    /// the mtime of every declared verdict input, to conclude it had not.
    ///
    /// The branch is known where the message is produced. Naming it costs nothing.
    type Mismatch =
        /// The launch never bound a tree: `read` returned `None` before the run. A
        /// DEFECT in the input walk — a skipped entry or an unreadable file — and
        /// evidence earned by a fully successful run is discarded because of it.
        /// Remedy: fix the walk.
        | UnboundAtLaunch
        /// The tree cannot be read NOW, so there is nothing to compare against. The
        /// same class of defect as `UnboundAtLaunch`, at the other end of the run,
        /// and worth separating because the two implicate different moments.
        | UnreadableAtCompletion
        /// Both trees were read and they differ. CORRECT behaviour: the tree was
        /// edited while the run was in flight and the receipt must not outlive it.
        /// Remedy: none, or re-run on a settled tree.
        | MovedDuringRun

    let classifyMismatch expected current =
        match expected, current with
        | None, _ -> UnboundAtLaunch
        | _, None -> UnreadableAtCompletion
        | Some _, Some _ -> MovedDuringRun

    /// The revocation reason for a mismatch, naming the arm and its remedy.
    let describeMismatch mismatch =
        match mismatch with
        | UnboundAtLaunch ->
            "the input tree was UNBOUND at launch — the input walk returned nothing to bind to, "
            + "so evidence from this run is discarded through no fault of the tree. This is a defect in the walk"
        | UnreadableAtCompletion ->
            "the input tree could not be READ at completion, so there was nothing to compare the launch "
            + "binding against. This is a defect in the walk, not a change to the tree"
        | MovedDuringRun ->
            "the input tree MOVED between launch and completion — it was edited while the run was in flight"

type TestEvidenceReceipt =
    { InputTreeHash: string option
      RunId: Guid
      Coverage: RunCoverage
      Seeds: string list
      ZeroSelection: ZeroSelection }

/// What is still owed before a green is test-equivalent to the last full suite. It is
/// published with the run results it depends on, so a reader of one snapshot reads one
/// consistent answer, and a completion whose fold failed leaves it unchanged.
type VerificationDebt =
    {
        /// Changed symbols not yet verified by a covering run. Durable
        /// (`PendingVerification`).
        PendingQueue: PendingVerification.Queue
        /// The revision at which each queued symbol was last enqueued in this session. A
        /// symbol loaded from disk and never re-enqueued is at revision 0.
        SymbolRevisions: Map<string, int64>
        /// The last revision issued. Every enqueue takes the next one, so an edit to a
        /// symbol already queued is still a newer revision.
        Revision: int64
        /// A ledger could not be read, or a durable publication did not finish: what is
        /// owed is unknown. Every run widens to the full suite and the cache is refused
        /// until a full suite passes every configured project.
        RecoveryOutstanding: bool
        /// The full-suite watermark impact-filtered greens are relative to.
        Baseline: FullSuiteBaseline.Baseline option
        /// File-granular runtime coverage obligations, per project generation. Durable.
        RuntimeObligations: RuntimeCoverageObligations
    }

/// What a launched run was going to execute, known when it claims the "tests" key.
type LaunchScope =
    /// Every configured project, unfiltered: requested, or widened to earn a baseline.
    | LaunchedFullSuite
    | LaunchedSelection

/// The run holding the "tests" key, as its launch here claimed it.
type InFlightRun =
    {
        Scope: LaunchScope
        /// The mode it was launched under. Its fold ends a pass-through it ran for.
        Mode: TestMode
    }

type TestPruneState =
    {
        Debt: VerificationDebt
        /// Set by `set-scope`. Pass-through lasts for the run launched under it: that
        /// run's fold, or its failure to launch, returns the daemon to impact selection
        /// (`TestMode.afterRun`). A request, not evidence; `test-scope` reports what
        /// actually ran.
        Mode: TestMode
        /// The run holding the "tests" key whose result is not folded yet, if a launch
        /// here claimed it. Cleared by that run's completion fold.
        InFlight: InFlightRun option
        /// Every run this session completed, newest first, bounded at
        /// `SessionRunLedger`. `test-scope` declares them so a check can name each batch
        /// it ran.
        CompletedRuns: Guid list
        /// The last completion's check-vs-confirm projection: its run, the selection
        /// `check` would have used, and whether that reached a failure the run saw.
        /// `None` until a run completes.
        CheckReach: (Guid * Map<string, ProjectSelection> option * CheckReach * FailureRecall) option
        /// Command replies this state owes, resolved only after the state is published
        /// (`PrepareCommit`'s `Finalize`). Each event starts with none.
        Replies: (Tasks.TaskCompletionSource<string> * string) list
        PendingAnalysis: Map<string, AnalysisResult list>
        /// The project-model generation `PendingAnalysis` was accepted under. Analysis
        /// from a replaced model is retired before any flush can persist it.
        AnalysisModelGeneration: int64 option
        /// What each file's completed analysis concluded, for the model in
        /// `AnalysisModelGeneration`. An analysis-only daemon's evidence is minted from
        /// these at the cohort seal; a daemon that runs tests never reads them.
        AnalysisFiles: Map<AbsFilePath, AnalysisFileEvidence>
        /// The analysis-only evidence the last sealed cohort earned. `None` until a
        /// cohort seals, and whenever tests are configured.
        AnalysisReceipt: AnalysisEvidence option
        SymbolSnapshot: Map<string, SymbolInfo list>
        AffectedTests: AffectedTestsState
        ChangedSymbols: string list
        ChangedFiles: string list
        /// Last completed test run's results, if any. `ctx.IsRunning "tests"` is the
        /// source of truth for "currently running", so this carries no phase.
        LastResults: TestResults option
        /// The id of the run that produced `LastResults` — i.e. the directory its
        /// CTRF reports live in (`.fshw/test-runs/<runId>/`). Reported to the CLI by
        /// `test-scope` so the verdict can DECLARE which reports are this run's,
        /// instead of inferring membership from mtimes. `None` until a run completes.
        LastRunId: Guid option
        /// The seed symbols that SELECTED the last completed run — i.e. the change
        /// that caused those tests to run. Empty for an unfiltered run (nothing
        /// selected it; everything ran) and until a run completes.
        ///
        /// Retained because a later check that selects NOTHING has to be able to
        /// answer "then what was the last change that did trigger tests?". The
        /// seeds are computed per flush and were previously only logged, so that
        /// question had no answer outside a daemon log — and a reader who cannot
        /// tell "nothing needed running" from "nothing ran" goes looking for a bug
        /// in the selector.
        LastSeeds: string list
        /// Symbols that became owed while a full-suite run was already in flight and were
        /// attached to it rather than queueing another run, each with the debt revision
        /// captured when it attached. Under `ImpactSelection` only a BootScan cohort
        /// attaches; under `PassThrough` every arrival does. The run covers the built tree
        /// being baselined, but these symbols are absent from its immutable launch
        /// snapshot. They may be committed only after that run produces genuinely green
        /// full-suite evidence over the input tree it launched against, and only while the
        /// symbol is still at its captured revision: a later edit is new debt the held run
        /// never built, even if the edit restores the sealed bytes.
        DebtDuringFullRun: Map<string, int64>
        /// Maps test class name → absolute source file path (built during FileChecked analysis).
        TestClassFiles: Map<string, string>
        /// True after the plugin has observed at least one `BuildCompleted
        /// BuildSucceeded` in this daemon session. The `FileChecked` handler uses this
        /// to decide whether a clean FCS check may promote the freshness sidecar to
        /// `fcsClean = true`.
        ///
        /// The cold-scan pipeline guarantees BuildCompleted reaches the TestPrune
        /// mailbox before any FileChecked (Daemon.fs `performScan` awaits BuildPlugin
        /// terminal before the FCS tier), so the gate is effective on the very first
        /// cold start — no two-session warm-up. Resets on plugin restart by design: a
        /// restart clears the in-process "I've seen warm FCS this session" assertion.
        BuildCompletedInThisSession: bool
        /// Per-test-project dependency fingerprint observed at the last
        /// `BuildCompleted`. A project whose fingerprint moves between builds had
        /// a dependency/binary change the symbol diff can't see, so its tests are
        /// force-run (dependency-fanout). Empty until the first build establishes
        /// the baseline. See `DependencyFanout`.
        PriorProjectFingerprints: Map<string, string>
        /// Test projects whose dependency fingerprint changed but whose force-run
        /// has not launched: a run was already in flight when the build landed, or the
        /// launch found its artifacts or test host unavailable. The next launch consumes
        /// (and clears) this, so a dependency change is never lost.
        PendingForceRunProjects: Set<string>
        /// True when the most recent `flushAndQueryAffected` had changed/queued symbols
        /// but EVERY one proved to have NO covering test, leaving an empty affected set.
        /// A definitive "nothing to verify" green, so the zero-affected skip in
        /// `runTestsWithImpact` completes immediately even on a cold daemon with no
        /// session baseline. Distinct from a genuine cold start with NO pending symbols,
        /// which must still run the full-suite baseline to establish one (guarded by
        /// `hasCachedResults`). Recomputed on every flush; only read right after one.
        ChangedSymbolsAllUncovered: UncoveredChanges
        /// Repo-relative paths of files whose symbol analysis FAILED and has not
        /// since succeeded. These files contribute NO symbols, so they are invisible
        /// to the impact graph — an edit to one has nothing to diff and selects
        /// nothing on its own. While this set is non-empty the run
        /// falls back to the coarse selection (`coarseFallbackProjects`: every test
        /// project, in full), because "I cannot analyse this file" means the SELECTOR
        /// cannot know what to select, and a superset is safe where a gap is not.
        ///
        /// A file leaves the map as soon as it analyses cleanly, so the fallback is
        /// self-clearing. NOT persisted: a cold scan re-checks every file and
        /// repopulates the map from scratch.
        UnanalyzableFiles: Map<string, UnanalyzableFile>
        /// Extensions whose last refresh THREW, by name, with the reason. Their stored
        /// edges describe an older tree, so while this is non-empty the run takes the same
        /// coarse fallback as `UnanalyzableFiles`, and the ledger carries an error for each.
        /// An extension leaves the map when a refresh succeeds; a failed one is retried on
        /// every flush. NOT persisted: a new process refreshes every extension on its
        /// first flush.
        FailedExtensions: Map<string, string>
        /// The reds no COVERING run has passed since. Rewritten on
        /// every `TestsFinished`: a red leaves ONLY when a run that actually executed
        /// it passes. The shared error ledger is a projection of this list.
        ///
        /// DURABLE: persisted beside the pending-verification queue
        /// and loaded at construction, so a red survives a daemon restart and is
        /// quarantined into the next run exactly as it would have been in the session
        /// that found it. The earlier "a restart runs the full suite anyway" argument
        /// was false whenever the durable queue was non-empty at restart — see
        /// `OutstandingFailure.load`.
        OutstandingFailures: OutstandingFailure list
        /// What the last completed run actually COVERED — the receipt that goes with
        /// `LastResults` (which says what it FOUND). Read together: a green result means
        /// "nothing failed IN WHAT THIS COVERED", never "nothing failed".
        ///
        /// Kept in state rather than only in the `TestsFinished` closure so consumers
        /// outside the handler (IPC commands, the verdict writer) ask this instead of
        /// inventing a parallel notion of scope that could drift from the one the ledger
        /// clears by. Empty until the first run completes.
        LastCoverage: RunCoverage
        LastZeroSelection: ZeroSelection
        /// Atomic receipt exposed by `test-scope`. A queued narrower drain may update
        /// status and failure state, but cannot split or downgrade a full-suite receipt
        /// earned earlier in the same top-level verification episode.
        EvidenceReceipt: TestEvidenceReceipt option
        /// What the last completion EARNED for the current project model: the run, what it
        /// covered in full, and every reason it cannot support a green. Minted only by the
        /// completion fold, and published with this state (`IEarnedEvidenceState`).
        Earned: EarnedEvidence option
    }

    interface IEarnedEvidenceState with
        member this.EarnedEvidence = this.Earned

    interface IAnalysisEvidenceState with
        member this.AnalysisEvidence = this.AnalysisReceipt

    // A replayed `FileChecked` is recorded by the rule a live fold applies: only a result
    // published against the model published now describes it (see `notCurrent`).
    interface IFileReplayState<TestPruneState> with
        member this.Replayed currentModel result analysis =
            if result.ModelGeneration.IsNone || result.ModelGeneration <> currentModel then
                this
            else
                { this with
                    AnalysisFiles =
                        Map.add result.File (AnalysisFileEvidence.fromResult result analysis) this.AnalysisFiles }

/// The slice of `TestPruneState` a test RUN reads — and nothing else.
///
/// The run is an `Async` handed to `RunExclusive` and lives as long as the suite does:
/// minutes, on a full run. Whatever it closes over it PINS for that whole time, so
/// closing over the state RECORD pins the entire generation — including `SymbolSnapshot`
/// (the repo-wide symbol table), which no run touches — while the agent loop keeps
/// folding new `FileChecked` events into fresh generations. The peak lands exactly when
/// the suite is running and FCS is at its own peak, and FsHotWatch is ~85% native FCS
/// memory. Copying only what the run reads lets the rest of each generation die on
/// schedule; the type is the enforcement.
///
/// `LastResults` is deliberately absent: the run's interest in it is one bit — "does a
/// baseline exist" — which the caller computes and passes as `hasCachedResults`.
type TestRunInputs =
    {
        /// The impact selection: which test classes the changed symbols reach.
        AffectedTests: AffectedTestsState
        /// The in-memory hot view of the pending-verification queue, unioned with the
        /// durable queue to form the snapshot this run is launched against.
        ChangedSymbols: string list
        /// Every changed symbol proved to have no covering test — the "nothing to
        /// verify" green.
        ChangedSymbolsAllUncovered: UncoveredChanges
        /// Files whose symbol analysis failed: while non-empty, the run widens to
        /// every test project.
        UnanalyzableFiles: Map<string, UnanalyzableFile>
        /// Extensions whose last refresh failed: while non-empty, the run widens to every
        /// test project.
        FailedExtensions: Map<string, string>
        /// Prior reds captured at launch. They are quarantined into this run's
        /// selection even when the current graph reaches different tests.
        OutstandingFailures: OutstandingFailure list
        /// The debt the run launches against, captured at dispatch.
        Debt: VerificationDebt
        /// The mode in effect at dispatch.
        Mode: TestMode
        /// Source files whose symbols changed in this launch snapshot. Runtime
        /// project attribution is file-granular, so it consumes this alongside
        /// the symbol-precise AST selection.
        ChangedFiles: string list
        /// Seed receipt captured at dispatch. A later BatchChecked may replace the
        /// live state's LastSeeds while this run is still executing.
        Seeds: string list
    }

module TestRunInputs =
    /// Project the state down to what a run reads, at LAUNCH time.
    let ofState (state: TestPruneState) : TestRunInputs =
        { AffectedTests = state.AffectedTests
          ChangedSymbols = state.ChangedSymbols
          ChangedSymbolsAllUncovered = state.ChangedSymbolsAllUncovered
          UnanalyzableFiles = state.UnanalyzableFiles
          FailedExtensions = state.FailedExtensions
          OutstandingFailures = state.OutstandingFailures
          Debt = state.Debt
          Mode = state.Mode
          ChangedFiles = state.ChangedFiles
          Seeds = state.LastSeeds }

/// Custom message posted from the async test runner back to the synchronous Custom
/// handler. Carries the completed lifecycle event so the handler can emit it inside the
/// framework's per-event capture window — required for the cache to record it on terminal
/// status, which `tryReplayCache` re-fires to downstream subscribers (FileCommandPlugin
/// keys off TestRunCompleted) on a hit. The start is emitted before the host launches;
/// cache replay synthesizes a matching start for completion-only entries.
///
/// Live `TestProgress` events still fire from the async because cache replay
/// deliberately skips per-group progress and goes straight from Started to Completed.
///
/// `launch` is what the run was LAUNCHED against, captured at dispatch: the queue
/// snapshot (`Symbols`) and, per symbol, the test PROJECTS covering it
/// (`CoveringProjectsBySymbol`). The `TestsFinished` handler decides per-symbol
/// green-commit from THIS, not from live `state.AffectedTests`/`state.ChangedSymbols`,
/// which mid-run `BatchChecked` flushes overwrite. A symbol leaves the queue only when
/// EVERY project covering it passed; a symbol with no covering projects is committed
/// unconditionally at flush time.
///
/// `Selection` is the run's SCOPE and the input to `RunCoverage.ofRun`,
/// so it decides what this run's green may CLEAR. A project absent from it was never
/// launched and vindicates nothing, whatever its result says — an impact-skip is
/// recorded as a filtered PASS. Empty for the zero-affected skip and the aborted-run
/// lifecycle: they executed nothing, so they clear nothing.
type TestRunLaunch =
    {
        /// Immutable input identity captured before this run executes.
        InputTreeHash: string option
        /// The available project-model generation this run was selected under. A
        /// completion observed under a different model discharges nothing.
        ModelGeneration: int64 option
        Symbols: Set<string>
        /// The revision of each launched symbol at dispatch. A completion retires a
        /// symbol only while it is still at this revision: an edit made during the run
        /// is newer debt the run never built.
        SymbolRevisions: Map<string, int64>
        CoveringProjectsBySymbol: Map<string, Set<string>>
        /// Durable file-granular runtime obligations launched independently of
        /// the symbol queue. Each project runs unfiltered; only those exact file
        /// obligations may be retired by its green result.
        RuntimeProjectsByFile: RuntimeCoverageObligations
        Selection: Map<string, ProjectSelection>
        /// The selection this run would have been launched against had
        /// `confirm` not widened the scope to full — i.e. what `check` would have run over
        /// the same tree, same daemon, same instant.
        ///
        /// `Some` ONLY where an impact selection was actually computed and then widened
        /// past. `None` — a `run-tests` force run, an aborted lifecycle, the zero-affected
        /// skip — means THERE IS NO SELECTION TO PROJECT THROUGH, and the projection must
        /// refuse rather than fall back to `Selection` (which under full-suite scope is
        /// every project in full, and would read as "check would have run everything": a
        /// perfect, permanent, false agreement).
        WouldHaveRun: Map<string, ProjectSelection> option
        /// The seeds that selected this run, captured atomically with its scope.
        Seeds: string list
        ZeroSelection: ZeroSelection
        /// The changed files this run launched against. Its completion clears only
        /// these; a file that changed during the run still selects the next one.
        ChangedFiles: string list
    }

module CheckReach =
    let private recallThreshold = 1.0

    /// The wire token. Total.
    let token (r: CheckReach) : string =
        match r with
        | ReachedAFailure _ -> "reached-a-failure"
        | ReachedNoFailure _ -> "reached-no-failure"
        | NoFailuresToReach -> "no-failures-to-reach"
        | ReachUnknown _ -> "unknown"

    /// Does the retained selection reach `failure`?
    ///
    /// `None` where it cannot be decided. A project ABSENT from the selection is decided
    /// — `check` would not have launched it at all — but a project selected under a
    /// CLASS filter cannot be asked about a failure that names no class.
    type private Reach =
        | Reached
        | Missed of MissCause
        | Undecidable

    let private reaches (selection: Map<string, ProjectSelection>) (failure: OutstandingFailure) : Reach =
        match Map.tryFind failure.Project selection with
        | None -> Missed MissCause.ProjectNotSelected
        | Some ProjectInFull -> Reached
        | Some(ProjectClasses classes) ->
            match failure.Class with
            | Some cls when Set.contains cls classes -> Reached
            | Some _ -> Missed MissCause.ClassNotInFilter
            | None -> Undecidable

    /// Measure the retained selection against every distinct failure the full run
    /// observed. Zero failures is no sample, not perfect recall. One undecidable
    /// project-level failure makes the denominator unknown, so the whole measurement
    /// is refused rather than silently shrinking the denominator.
    let measure
        (wouldHaveRun: Map<string, ProjectSelection> option)
        (failures: OutstandingFailure list)
        : FailureRecall =
        match wouldHaveRun with
        | None -> RecallNotMeasurable "this run carries no retained impact selection"
        | Some _ when List.isEmpty failures ->
            RecallNotMeasurable "the full run observed no failures, so the recall denominator is zero"
        | Some selection ->
            let distinctFailures =
                failures
                |> List.distinctBy (fun failure -> failure.Project, failure.Class, failure.Method)

            match
                distinctFailures
                |> List.tryFind (fun failure -> Option.isNone failure.Class || Option.isNone failure.Method)
            with
            | Some failure ->
                RecallNotMeasurable
                    $"the %s{failure.Project} failure names no exact test class and method, so an exact failing-test denominator cannot be formed"
            | None ->
                let decisions =
                    distinctFailures |> List.map (fun failure -> failure, reaches selection failure)

                match decisions |> List.tryFind (fun (_, decision) -> decision = Undecidable) with
                | Some(failure, _) ->
                    RecallNotMeasurable
                        $"the %s{failure.Project} failure cannot be projected through the retained selection"
                | None ->
                    let total = List.length decisions

                    let reached =
                        decisions
                        |> List.sumBy (fun (_, decision) -> if decision = Reached then 1 else 0)

                    let recall = float reached / float total
                    RecallMeasured(reached, total, recallThreshold, recall >= recallThreshold)

    /// Classify one completed run against the selection `check` would have used.
    ///
    /// `failures` is `failuresOf` — the very list the outstanding ledger is rewritten
    /// from, so the reds this asks about are exactly the reds the verdict reports.
    let classify (wouldHaveRun: Map<string, ProjectSelection> option) (failures: OutstandingFailure list) : CheckReach =
        match wouldHaveRun with
        | None ->
            ReachUnknown
                "this run carries no retained impact selection (a forced re-run, an aborted run, or a skip), so \
                 there is nothing to project the result through"
        | Some _ when List.isEmpty failures -> NoFailuresToReach
        | Some selection ->
            let decided = failures |> List.map (fun f -> f, reaches selection f)

            match decided |> List.tryFind (fun (_, r) -> r = Undecidable) with
            | Some(undecidable, _) ->
                ReachUnknown
                    $"the %s{undecidable.Project} red names no test class (a timeout, an errored host, or \
                       unparseable failure output) and the retained selection runs that project under a CLASS \
                       filter, so whether `check` would have executed it cannot be decided"
            | None ->
                let reached =
                    decided
                    |> List.choose (fun (f, r) -> if r = Reached then Some f.Project else None)
                    |> List.distinct
                    |> List.sort

                if List.isEmpty reached then
                    decided
                    |> List.choose (fun (failure, reach) ->
                        match failure.Class, reach with
                        | Some className, Missed cause ->
                            Some
                                { Project = failure.Project
                                  Class = className
                                  Cause = cause }
                        | _ -> None)
                    |> ReachedNoFailure
                else
                    ReachedAFailure reached

    /// A selection alarm and its recall metric must be based on the same complete,
    /// per-test receipt. A runner/host/infrastructure failure that leaves no report,
    /// an incomplete report, or an unreadable report is typed as `Error` by
    /// `failedTestsOfRun`; it is evidence that the run failed, not evidence that the
    /// selector missed each apparent failure.
    let classifyEvidence
        (wouldHaveRun: Map<string, ProjectSelection> option)
        (failures: Result<OutstandingFailure list, string>)
        : CheckReach * FailureRecall =
        match failures with
        | Ok exactFailures -> classify wouldHaveRun exactFailures, measure wouldHaveRun exactFailures
        | Error reason -> ReachUnknown reason, RecallNotMeasurable reason

[<NoComparison; NoEquality>]
type TestPruneMsg =
    | TestsFinished of started: TestRunStarted * completed: TestRunCompleted * launch: TestRunLaunch
    /// The launch found the build artifacts unusable. `owed` is the dependency fanout the
    /// launch consumed and did not run, which stays owed.
    | ArtifactsUnavailable of reason: string * owed: Set<string> * reply: Tasks.TaskCompletionSource<string> option
    /// The test host could not start. `owed` as for `ArtifactsUnavailable`.
    | TestHostUnavailable of reason: string * owed: Set<string> * reply: Tasks.TaskCompletionSource<string> option
    /// A `run-tests` force-run completed. Folded exactly like `TestsFinished`; `reply` is
    /// resolved with `response` once that fold is published.
    | CommandTestsFinished of
        started: TestRunStarted *
        completed: TestRunCompleted *
        launch: TestRunLaunch *
        reply: Tasks.TaskCompletionSource<string> *
        response: string
    /// A run owed after the one holding the "tests" key: queued as an intent behind it,
    /// coalesced, and decided against the state it is delivered into.
    | ImpactRunRequested
    /// `set-scope`: the mode every later launch runs under.
    | ScopeRequested of TestMode
    /// A run could not ingest its runtime coverage receipt. Its durable recovery marker is
    /// already written; this makes the debt unknown in owner state too.
    | RuntimeCoverageFailed of project: string
    /// A `run-tests` IPC command asking the MAILBOX to launch its force-run under the
    /// "tests" key. The command must never execute tests
    /// on the IPC thread itself: a run outside the slot is invisible to the daemon's
    /// runtime model — `IsRunning "tests"` reads false (so a concurrent FileChecked
    /// stamps a terminal status over it), the plugin never reports Running, and
    /// `AnyPluginBusy()` reads false, letting a concurrent `fshw check` resolve its
    /// verdict wait and exit 0 while the test process is still alive. `reply` carries
    /// the results JSON back to the awaiting command; every completion path must
    /// resolve it.
    | RunTestsRequested of configs: TestConfig list * filter: string option * reply: Tasks.TaskCompletionSource<string>
    /// `run-tests --only-failed`. Which projects failed is resolved by `Update` from the
    /// state it folds this message into, never from a snapshot the command read earlier.
    | RunFailedTestsRequested of filter: string option * reply: Tasks.TaskCompletionSource<string>

/// The test projects `run-tests --only-failed` re-runs: every non-green project of the
/// last run, plus every project still owing an outstanding failure.
///
/// "Failed" is the OUTSTANDING set, not merely the last run's results: after an
/// impact-filtered run the failing project is not in `LastResults` at all (it wasn't
/// selected), and `--only-failed` would have re-run nothing — "no matching test
/// projects" — while a red sat there. The outstanding ledger is what still owes a
/// re-run, so it is what this verb must re-run.
let internal failedTestConfigs
    (allConfigs: TestConfig list)
    (lastResults: TestResults option)
    (outstandingFailures: OutstandingFailure list)
    : Result<TestConfig list, string> =
    let outstandingProjects =
        outstandingFailures |> List.map (fun f -> f.Project) |> Set.ofList

    let lastRunFailed =
        match lastResults with
        | Some prev ->
            prev.Results
            |> Map.toList
            |> List.choose (fun (name, r) ->
                match r with
                | TestsFailed _
                | TestsTimedOut _
                // A deferred project never ran, and an errored one aborted without a
                // verdict — both are non-green, so `--only-failed` must pick them up.
                | TestsDeferred _
                | TestsErrored _ -> Some name
                | _ -> None)
            |> Set.ofList
        | None -> Set.empty

    let failedNames = Set.union lastRunFailed outstandingProjects

    if lastResults.IsNone && Set.isEmpty failedNames then
        Error "no previous results — cannot determine failed projects"
    else
        Ok(allConfigs |> List.filter (fun c -> failedNames.Contains(c.Project)))

/// Build the degenerate Started→Aborted lifecycle a faulted run posts back so the
/// synchronous `TestsFinished` handler drives the plugin to a NON-green terminal status.
/// A `beforeRun` throw / `executeTests` fault means the suite it guards NEVER RAN — that
/// must surface as a failure, never a stale prior green. Shared by the
/// impact path and the manual `run-tests` command so the two stay in lockstep.
/// `Results = Map.empty` ⇒ the handler commits nothing from the pending queue; `reason`
/// carries the hook's failure output so `fshw check` / `fshw errors` shows WHY.
let private abortedRunLifecycle
    (emittedStart: TestRunStarted option)
    (reason: string)
    : TestRunStarted * TestRunCompleted =
    let started =
        emittedStart
        |> Option.defaultWith (fun () ->
            { RunId = Guid.NewGuid()
              StartedAt = DateTime.UtcNow })

    let completed: TestRunCompleted =
        { RunId = started.RunId
          TotalElapsed = TimeSpan.Zero
          Outcome = Aborted reason
          Results = Map.empty
          // No project was invoked, so that is what it says. Under the old `bool`
          // this had to pick between claiming a suite that never ran and claiming a
          // filtering that never happened — the comment here read "a lie either
          // way". There is now a case for what actually occurred.
          Verification = NoProjectsSelected }

    started, completed

/// Translate a repo-root-relative glob (`*`, `?`, `**`, `/`) into a regex
/// anchored against a repo-relative path. `**` matches across directory
/// separators (including none); a single `*`/`?` does NOT cross `/`. A trailing
/// `dir/**` also matches `dir` itself (zero segments). Paths are normalised to
/// `/` before matching so the same config works on every OS.
let internal dependsOnGlobToRegex (glob: string) : System.Text.RegularExpressions.Regex =
    // Collapse a trailing `/**` to a sentinel so the leading `/` becomes
    // optional (`dir/**` matches `dir`, `dir/x`, `dir/x/y`). Done before the
    // char scan so the `/` isn't emitted as a mandatory literal.
    let normalized = glob.Replace('\\', '/').TrimStart('/')

    let normalized, trailingDoubleStar =
        if normalized.EndsWith("/**") then
            normalized.Substring(0, normalized.Length - 3), true
        else
            normalized, false

    let sb = System.Text.StringBuilder()
    sb.Append('^') |> ignore
    let mutable i = 0

    while i < normalized.Length do
        let c = normalized.[i]

        if c = '*' then
            if i + 1 < normalized.Length && normalized.[i + 1] = '*' then
                // `**` — match any chars including `/`. A `**/ ` (cross-dir,
                // zero-or-more leading segments) makes the following separator
                // optional so `a/**/b` matches `a/b` too.
                if i + 2 < normalized.Length && normalized.[i + 2] = '/' then
                    sb.Append("(?:.*/)?") |> ignore
                    i <- i + 3
                else
                    sb.Append(".*") |> ignore
                    i <- i + 2
            else
                // single `*` — any run of non-separator chars
                sb.Append("[^/]*") |> ignore
                i <- i + 1
        elif c = '?' then
            sb.Append("[^/]") |> ignore
            i <- i + 1
        else
            sb.Append(System.Text.RegularExpressions.Regex.Escape(string c)) |> ignore
            i <- i + 1

    // Re-attach the trailing `/**`: an OPTIONAL `/<anything>` so the bare dir
    // and any descendant both match.
    if trailingDoubleStar then
        sb.Append("(?:/.*)?") |> ignore

    sb.Append('$') |> ignore

    System.Text.RegularExpressions.Regex(
        sb.ToString(),
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        ||| System.Text.RegularExpressions.RegexOptions.CultureInvariant
    )

/// Resolve the on-disk files under `repoRoot` whose repo-relative path matches
/// any of the `dependsOn` globs. Deterministic: returns sorted, distinct
/// absolute paths. Globs that match nothing contribute nothing; a glob that is
/// a plain existing file path resolves to that one file. Directory enumeration
/// errors are swallowed (best-effort) so a transient IO hiccup can't crash the
/// cache-key computation.
let internal resolveDependsOnFiles (repoRoot: string) (dependsOn: string list) : string list =
    if dependsOn.IsEmpty then
        []
    else
        let rootFull = Path.GetFullPath(repoRoot)

        let toRel (abs: string) =
            Path.GetRelativePath(rootFull, abs).Replace('\\', '/')

        // A plain glob with no wildcard meta is a direct file reference — resolve
        // it without walking the whole tree (cheap + handles files outside any
        // enumerable subdir uniformly).
        let isLiteral (g: string) =
            not (g.Contains('*') || g.Contains('?'))

        let literalHits =
            dependsOn
            |> List.filter isLiteral
            |> List.choose (fun g ->
                let abs =
                    Path.GetFullPath(Path.Combine(rootFull, g.Replace('\\', '/').TrimStart('/')))

                if File.Exists abs then Some abs else None)

        let globPatterns =
            dependsOn |> List.filter (isLiteral >> not) |> List.map dependsOnGlobToRegex

        let globHits =
            if globPatterns.IsEmpty then
                []
            elif not (Directory.Exists rootFull) then
                []
            else
                // Repo-root-rooted walk of EVERY file. `SearchOption.AllDirectories`
                // here would follow `.devenv/profile` into the /nix/store symlink
                // cycle, so SafeWalk owns the recursion (no symlinked-dir descent,
                // depth-capped), and its per-subtree IO errors are already swallowed
                // internally.
                SafeWalk.bestEffortFilePaths SafeWalk.ToolingExcludedDirs "*" rootFull
                |> Seq.filter (fun abs ->
                    let rel = toRel abs
                    globPatterns |> List.exists (fun rx -> rx.IsMatch(rel)))
                |> Seq.toList

        (literalHits @ globHits) |> List.distinct |> List.sort

/// Deterministic content hash of the files matched by the `dependsOn` globs.
/// Editing, adding, or deleting a matched file changes this hash, which salts
/// the test cache key so an external input (a DB migration, a generated file,
/// a schema) that test-prune's symbol diff can't see still invalidates a stale
/// cached test verdict. Empty `dependsOn` → empty string (NO salt: the key is
/// byte-identical to the pre-feature key, so existing caches keep hitting).
/// Missing files are skipped; a glob matching nothing contributes nothing.
let internal externalDependencyHash (repoRoot: string) (dependsOn: string list) : string =
    let files = resolveDependsOnFiles repoRoot dependsOn

    if files.IsEmpty then
        ""
    else
        let sb = System.Text.StringBuilder()

        for path in files do
            let rel = Path.GetRelativePath(Path.GetFullPath(repoRoot), path).Replace('\\', '/')

            let h =
                try
                    FsHotWatch.CheckCache.sha256Hex (System.Text.Encoding.UTF8.GetString(File.ReadAllBytes path))
                with
                | :? IOException
                | :? UnauthorizedAccessException -> "unreadable"

            sb.Append(rel.Length) |> ignore
            sb.Append(':') |> ignore
            sb.Append(rel) |> ignore
            sb.Append('@') |> ignore
            sb.Append(h) |> ignore
            sb.Append('\n') |> ignore

        FsHotWatch.CheckCache.sha256Hex (sb.ToString())

/// The files that DECLARE what gets compiled — `FsHotWatch.StructureFiles.allPatterns`,
/// not a second copy of it.
///
/// It WAS a second copy, and it was already wrong: `Directory.Build.targets` and
/// `Directory.Packages.props` are implicit MSBuild imports exactly as
/// `Directory.Build.props` is, and a `<Compile Include=…>` in either adds a file to
/// every project in the repo without moving one project file. Sharing the list with the
/// BUILD plugin's inputs merkle is the point: two caches that disagree about what
/// "structural" means is how one misses while the other replays.
let private structureFilePatterns = FsHotWatch.StructureFiles.allPatterns

/// Content merkle of the files that decide WHAT IS COMPILED.
///
/// The `BuildCompleted` cache key is a merkle over the CHANGED
/// SYMBOLS, and on a scan `BuildCompleted` is dispatched BEFORE the FCS pass — so that
/// term is empty whatever the tree holds. A tree that has just GAINED a test file and
/// its `<Compile Include=…>` therefore computes the SAME key as the tree without it,
/// hits the entry an earlier green wrote, and replays it: the handler is skipped, no
/// test process starts, `LastCoverage` still describes the earlier run, and the verdict
/// reports that run's full-suite green over a tree it never saw. Observed 2026-08-12 —
/// 21 new tests, none executed, `outcome: green`, `scope: {kind: full, 6/6}`.
///
/// Hashing the project files closes exactly that hole: a compile item cannot be added,
/// removed, or reordered without moving this hash, so a STRUCTURAL change is a
/// guaranteed cache MISS and the handler runs.
///
/// DELIBERATELY NOT the whole tree. A source EDIT is already covered by the symbol-diff
/// pipeline that runs after `BuildCompleted` and supersedes the entry; what that
/// pipeline cannot rescue is a file it has never seen. Hashing source content here would
/// invalidate every cached verdict on every keystroke for a guarantee already held.
///
/// TOTAL, and fails toward a re-run: `ContentHash.ofFile` answers with its unreadable
/// sentinel rather than throwing, and that sentinel differs from the file's readable
/// hash — so a project file we cannot read MOVES the key (a miss, a genuine run) rather
/// than being skipped as if it did not exist. Build output (`bin`/`obj`) is excluded via
/// `SourceExcludedDirs`, so a restore that regenerates project files under `obj/` cannot
/// invalidate every entry in the repo.
///
/// The same holds one level up, for a DIRECTORY the walk could not see: it is an entry
/// under its own path plus a trailing `/` (a relative path no file can have), hashed to
/// the same unreadable sentinel — `TreeHash.compute`'s rule for a hole. Built on the
/// best-effort walk, an unreadable directory contributed nothing and left this hash
/// unchanged, so the scan-skip guard replayed a project graph built from a tree it never
/// fully saw — the exact hazard this hash exists to close.
let internal projectStructureHash (repoRoot: string) : string =
    let rootFull = Path.GetFullPath repoRoot

    let relativeTo (abs: string) =
        Path.GetRelativePath(rootFull, abs).Replace('\\', '/')

    let walks =
        structureFilePatterns
        |> List.map (fun pattern -> SafeWalk.walk SafeWalk.SourceExcludedDirs pattern rootFull)

    let hashedFiles =
        walks
        |> List.collect (fun w -> w.Files |> List.map (fun f -> f.FullName))
        |> List.distinct
        |> List.map (fun abs -> relativeTo abs, ContentHash.ofFile abs)

    // Distinct: every pattern walks the same tree, so each one reports the same hole.
    let hashedHoles =
        walks
        |> List.collect (fun w -> w.Skipped |> List.map (fun s -> relativeTo s.Path + "/"))
        |> List.distinct
        |> List.map (fun rel -> rel, ContentHash.UnhashableContent)

    let entries =
        hashedFiles @ hashedHoles
        // Ordinal, so the merkle is reproducible across machines and locales.
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

    let sb = System.Text.StringBuilder()

    for (rel, hash) in entries do
        // Length-prefixed, like every other merkle here: a separator that can occur
        // inside a field lets two different trees produce one byte stream.
        sb.Append(rel.Length) |> ignore
        sb.Append(':') |> ignore
        sb.Append(rel) |> ignore
        sb.Append('@') |> ignore
        sb.Append(hash) |> ignore
        sb.Append('\n') |> ignore

    FsHotWatch.CheckCache.sha256Hex (sb.ToString())

/// What a run actually VERIFIED.
///
/// Three cases rather than one case with a flag, because they carry different EVIDENCE.
/// `AllZeroMatch` knows a filter ran against discovered tests and how many projects it
/// was applied to, so remediation can be specific. `NoProjectsSelected` has no
/// discovered names at all — nothing ran to discover them — so the only honest report is
/// that the scope was empty.
///
/// Takes the RESULT MAP, not `TestResults`, so the cache key (which holds a
/// `TestRunCompleted`) can call it instead of open-coding the fold. The derivation lives
/// in core (`RunVerification.ofResults`) because the CLI needs the same tokens and a
/// second copy is how the two ends drift; this alias just reads better at TestPrune's
/// call sites and is what the analyzer allow-list names.
let internal verificationOf (results: Map<string, TestResult>) : RunVerification = RunVerification.ofResults results

/// Refine the result-shaped receipt with the configured-suite boundary. An unfiltered
/// result map can only mean FullSuite when it names every configured project; manual
/// `--project` runs are unfiltered inside the selected projects but partial overall.
let internal verificationWithin (configuredProjects: Set<string>) (results: Map<string, TestResult>) =
    match verificationOf results with
    | Ran FullSuite when results |> Map.keys |> Set.ofSeq = configuredProjects -> Ran FullSuite
    | Ran FullSuite -> Ran RunScope.Partial
    | other -> other

/// "Projects ran, and every one of them matched nothing." The predicate the aggregators
/// want; `NoProjectsSelected` is deliberately NOT this, because no project is not the
/// same claim as every project matching nothing.
let internal allZeroMatchOf (results: Map<string, TestResult>) : bool =
    match verificationOf results with
    | AllZeroMatch _ -> true
    | NoProjectsSelected
    | NothingExecuted
    | Ran _ -> false

/// Retained for the existing wire field, which older CLIs still read. Prefer
/// `verificationOf`: this cannot distinguish an empty run from a real one.
let internal allZeroMatch (results: TestResults) : bool = allZeroMatchOf results.Results

/// `activeFilter` is the raw `--filter-*` passthrough the run was launched with, when
/// there was one. It rides on the wire so a refusal can ECHO IT: "no tests matched the
/// filter" without saying WHICH filter leaves the reader to guess between a typo, a
/// renamed class, and a filter aimed at a project that does not contain it — the exact
/// three-way ambiguity that cost one investigation a wrong conclusion.
/// `None` for an unfiltered run, and for the `test-results`
/// command, which reads a stored result set that does not remember what launched it.
///
/// `runReports` is the per-project CTRF SUMMARY this run produced, keyed by project.
/// Its whole job is criterion 3: the CLI must state total /
/// succeeded / failed for each project it ran, because a missing summary line is the
/// tell that separates a real pass from a vacuous one, and it used to live only in
/// `daemon.log`. A project ABSENT from this map produced no readable report — an
/// unknown runner we never asked one from, or a host that aborted before flushing —
/// and the consumer says so rather than printing zeros, which would read as a suite
/// that ran cleanly.
let private formatTestResultsJson
    (activeFilter: string option)
    (runReports: Map<string, FsHotWatch.Ctrf.Summary>)
    (results: TestResults)
    =
    let projects =
        results.Results
        |> Map.toList
        |> List.map (fun (name, result) ->
            let (status, output) =
                match result with
                // A zero-match-under-filter result gets a DISTINCT status so a consumer
                // can tell "ran, all green" from "matched nothing". The CLI's `coverage`
                // fallback for older daemons parses these per-project statuses, so
                // renaming any of these wire strings breaks it.
                | TestsNoMatch(o, _) -> ("no-tests-matched", o)
                | TestsPassed(o, _, _) -> ("passed", o)
                | TestsFailed(o, _, _) -> ("failed", o)
                | TestsTimedOut(o, _, _, _) -> ("timed-out", o)
                | TestsDeferred reason -> ("deferred", reason)
                | TestsErrored reason -> ("errored", reason)

            // `null`, not zeros. `total: 0, failed: 0` reads as "this suite ran
            // cleanly", so manufacturing counts from an absent report is the very
            // vacuous green this `null` avoids (the same rule `Verdict.parseSuites`
            // holds for the verdict file).
            let counts: obj =
                match Map.tryFind name runReports with
                | Some(summary: FsHotWatch.Ctrf.Summary) ->
                    box
                        {| total = summary.Total
                           succeeded = summary.Passed
                           failed = summary.Failed
                           skipped = summary.Skipped
                           other = summary.Other |}
                | None -> null

            {| project = name
               status = status
               output = truncateOutput 200 output
               elapsedMs = (TestResult.elapsed result).TotalMilliseconds
               counts = counts |})

    let verification = verificationOf results.Results

    JsonSerializer.Serialize(
        {| elapsed = $"%.1f{results.Elapsed.TotalSeconds}s"
           // The filter this run was launched with, or `null` when there was none.
           // Written so a "matched nothing" refusal can quote it back; a consumer that
           // does not know the field simply says less.
           filter = Option.toObj activeFilter
           // `noTestsMatched` is true iff EVERY project matched zero tests under
           // the active filter. RETAINED for CLIs older than `coverage`; it
           // cannot express "no project was selected", which is why the field
           // below exists.
           noTestsMatched = allZeroMatch results
           // The run-level answer to "did this verify anything?", stated by the
           // producer rather than reconstructed by the consumer from array
           // lengths. A consumer that does not know this field falls back to the
           // counts — an ABSENT field must never be read as "ran".
           coverage = RunVerification.token verification
           projects = projects |}
    )

/// Default slot-wait budget (ms) for the manual `run-tests` command when the
/// payload carries no `waitSec` — generous so a long `tests.beforeRun` chain
/// (90 s+) held by a prior in-flight run can't make an explicit `test-rerun` give
/// up and report `busy` before the slot frees. The
/// CLI always sends `waitSec` (default `DefaultTestRerunWaitSec`); this fallback
/// covers a missing/malformed field (an older CLI or hand-crafted payload).
[<Literal>]
let internal DefaultRunTestsWaitMs = 600_000

/// Read the `waitSec` slot-wait budget (seconds) from a `run-tests` argument
/// JSON object and convert it to milliseconds, falling back to `fallbackMs` when
/// the argument is absent, unparseable, or lacks a numeric `waitSec` field. Pure
/// so the wait budget is unit-testable without round-tripping the IPC command.
let internal parseRunTestsWaitMs (argStr: string) (fallbackMs: int) : int =
    try
        use doc = JsonDocument.Parse(argStr)

        match doc.RootElement.TryGetProperty("waitSec") with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with
            | true, secs when secs > 0 -> secs * 1000
            | _ -> fallbackMs
        | _ -> fallbackMs
    with _ ->
        fallbackMs

/// Every configured test project, each with an EMPTY affected-class list — which
/// `buildFilterArgs` reads as "no filter, run this project in full". This is the
/// unfiltered scope: the whole suite, no selection, nothing chosen.
///
/// Used by the two callers that may not trust a selection: `fshw confirm`
/// (impact filtering is a latency optimization for the inner loop,
/// never the basis of a correctness claim) and the unanalysable-file fallback
/// (a file the analyser cannot read has no symbols to select by).
let internal fullSuiteProjects (configs: TestConfig list) : Set<string> =
    configs |> List.map (fun c -> c.Project) |> Set.ofList

/// The coarse fallback for files the symbol analyser could not read.
///
/// A file whose analysis FAILED contributes no symbols, so the symbol diff finds nothing
/// changed in it and the selection it earns is EMPTY — an edit to it would select zero
/// tests and the check would go green having run nothing relevant. An unanalysable file
/// means "I cannot tell you what is affected", not "nothing is affected", so the answer
/// is to run EVERY test project: a superset is safe where a gap is not. Same rule
/// `EdgeEmission.resolveTargets` follows for an unresolvable seed.
///
/// Returned as force-run projects: a project present with an empty class list runs IN
/// FULL, and a non-empty force-run set also disables the zero-affected skip gate, so an
/// unanalysable file cannot reach the "0 affected, green, 0 ran" verdict either.
let internal coarseFallbackProjects
    (configs: TestConfig list)
    (unanalyzableFiles: Set<string>)
    (fanout: Set<string>)
    : Set<string> =
    if Set.isEmpty unanalyzableFiles then
        fanout
    else
        Set.union fanout (fullSuiteProjects configs)


/// The single answer to "what did this run actually cover?", per project and per suite.
/// Public so the verdict writer asks it rather than keeping a parallel notion of scope.
module RunCoverage =

    /// Nothing was executed, so nothing may be cleared. The verdict of an aborted
    /// run, and of the zero-affected skip (which runs no tests at all).
    let none: RunCoverage = Map.empty

    /// The projects this run executed at all (in full, or a class subset of).
    let coveredProjects (coverage: RunCoverage) : Set<string> = coverage |> Map.keys |> Set.ofSeq

    /// Did this run execute EVERY configured project, each in FULL? The only scope
    /// from which a whole-suite claim can be made — and the question `fshw confirm` is
    /// really asking. A run that filtered ANY project, or skipped one, covered less
    /// than the suite, whatever its result counts say. An empty project list is not a
    /// covered suite (there is no evidence in a run of nothing).
    let coversWholeSuite (projects: string list) (coverage: RunCoverage) : bool =
        not projects.IsEmpty
        && projects
           |> List.forall (fun p ->
               match Map.tryFind p coverage with
               | Some CoveredWholeProject -> true
               | Some(CoveredClasses _)
               | None -> false)

    /// Does this run's evidence reach the given red? `cls = None` is a
    /// PROJECT-level red (unparseable failure output, a timeout, an errored or
    /// deferred project): no class-filtered run can speak for it — only a run that
    /// executed the whole project can.
    let covers (project: string) (cls: string option) (coverage: RunCoverage) : bool =
        match Map.tryFind project coverage, cls with
        | None, _ -> false
        | Some CoveredWholeProject, _ -> true
        | Some(CoveredClasses _), None -> false
        | Some(CoveredClasses classes), Some c -> Set.contains c classes

    /// Derive the coverage of a completed run: what it was LAUNCHED against
    /// (`selection`), intersected with what it actually EXECUTED — the results, plus
    /// the run's own per-test report (`passedClasses`, see `passedClassesOfRun`).
    ///
    /// Per project, in order:
    ///   * no result / deferred / errored / zero-match-under-filter → NO coverage.
    ///     Nothing ran, so nothing is vindicated (a deferred project's `wasFiltered`
    ///     is `true` by convention and its output is empty — neither is evidence).
    ///   * `wasFiltered = false` → the runner executed the project with no filter,
    ///     whatever the selection asked for (a project with selected classes but no
    ///     `filterTemplate` runs in FULL) → `CoveredWholeProject`. The RESULT, not
    ///     the request, is the receipt.
    ///   * absent from the selection → NO coverage, unconditionally. The project was
    ///     never LAUNCHED (impact-skipped, recorded as a filtered pass), so no file on
    ///     disk may speak for it — this is the laundering guard and it is
    ///     checked BEFORE any report evidence.
    ///   * `wasFiltered = true` and the selection named classes → `CoveredClasses`.
    ///   * `wasFiltered = true` otherwise → the `run-tests --filter <raw>` passthrough:
    ///     an arbitrary filter string whose reach the LAUNCH REQUEST cannot express
    ///     (every project goes down as `ProjectInFull`). Ask the run's own evidence
    ///     instead — the classes its CTRF report shows actually RAN AND PASSED.
    ///     Otherwise `test-rerun --filter-class X` could re-run X,
    ///     pass, and still leave X's red standing forever. Fail-closed wherever the
    ///     report is absent, unreadable or incomplete (`passedClassesOfReport`).
    ///   * a TIMED-OUT project is excluded from the evidence path: a report flushed by
    ///     a process we killed is not a receipt for anything.
    let ofRun
        (selection: Map<string, ProjectSelection>)
        (results: Map<string, TestResult>)
        (passedClasses: Map<string, Set<string>>)
        : RunCoverage =
        results
        |> Map.toList
        |> List.choose (fun (project, result) ->
            // Exhaustive by construction — see `TestResult.executedTests`. Do not
            // reintroduce a wildcard here: a new non-executing case would fall through
            // and be counted as having run.
            let ran = TestResult.executedTests result

            let fromEvidence () =
                if TestResult.isTimedOut result then
                    None
                else
                    match Map.tryFind project passedClasses with
                    | Some classes when not (Set.isEmpty classes) -> Some(project, CoveredClasses classes)
                    | _ -> None

            if not ran then
                None
            elif not (TestResult.wasFiltered result) then
                Some(project, CoveredWholeProject)
            else
                match Map.tryFind project selection with
                | None -> None
                | Some(ProjectClasses classes) when not (Set.isEmpty classes) -> Some(project, CoveredClasses classes)
                | Some(ProjectClasses _)
                | Some ProjectInFull -> fromEvidence ())
        |> Map.ofList

/// The SCOPE `fshw confirm` reads, as a pure PROJECTION of `RunCoverage`.
///
/// Deliberately not an independent derivation: a second answer to "what did this run
/// cover?" can disagree with the one the ledger clears by, and `confirm` would then go
/// green on a scope the ledger never granted.
type internal ScopeReport =
    /// Every configured project executed, each in FULL. The only scope a whole-suite
    /// claim can be made from.
    | ScopeFull of projects: int
    /// Some project ran, but not the whole suite in full.
    | ScopeFiltered of ran: int * total: int
    /// NOTHING executed. Not a scope — an absence of evidence, which the CLI reads as
    /// `NoTestsRun` and refuses to call green in either mode.
    | ScopeNone of total: int

let internal scopeOf (projects: string list) (coverage: RunCoverage) : ScopeReport =
    let covered = RunCoverage.coveredProjects coverage
    let total = List.length projects

    if RunCoverage.coversWholeSuite projects coverage then
        ScopeFull total
    elif Set.isEmpty covered then
        ScopeNone total
    else
        ScopeFiltered(Set.count covered, total)

/// What ONE completed run does to the stored `EvidenceReceipt`.
///
/// The store used to have a single replace arm: every completion that was not the
/// exact AlreadyVerified no-op wrote a fresh receipt, and a zero-test completion on an
/// unchanged tree whose launch happened to be UNBOUND (`ReceiptInputTree.read` returned
/// `None` — a skipped entry, an unreadable file, the aborted-launch path) replaced an
/// earned, servable receipt with one that can never match any tree. The evidence the
/// earlier attempt had produced was gone, and `test-scope` then served no run id at all.
///
/// A transition names what the run establishes, and the fold below is total over it:
/// there is no arm that constructs a receipt from a run that verified nothing, so
/// "replaced by an unbound receipt" is unrepresentable rather than guarded against.
[<RequireQualifiedAccess>]
type ReceiptTransition =
    /// Executed at least one project to a verdict, `Outcome = Normal`, and the input
    /// tree it launched against is the tree read at completion. The only case that
    /// constructs a receipt.
    | Earned of TestEvidenceReceipt
    /// A quiet completion that adds nothing and takes nothing away: the selector chose
    /// zero tests because the change was already verified, the run covered nothing and
    /// produced no results, and the tree the previous receipt was earned on is the tree
    /// read now. ALSO the full-to-narrower case: a narrower executed run on a tree the
    /// previous receipt already covers in FULL says nothing the receipt does not.
    ///
    /// Deliberately blind to the outstanding-failure ledger: failures are the LEDGER's
    /// claim (they keep the plugin red on their own), and a retained receipt cannot
    /// mask them — `IpcOutputTests` proves retained coverage under a red plugin exits 1.
    | Noop
    /// The stored receipt no longer describes the tree, or this run could not stand
    /// behind one: aborted, launched unbound, the tree or the project model changed
    /// between launch and completion, or zero tests ran for a reason that is not "already verified".
    | Revoked of reason: string

module ReceiptTransition =
    let private allResultsCompleted (completed: TestRunCompleted) =
        completed.Results
        |> Map.forall (fun _ result ->
            match result with
            | TestsPassed _
            | TestsFailed _
            | TestsNoMatch _ -> true
            | _ -> false)

    /// Classify a completion. `previous` is consulted ONLY to decide whether a quiet
    /// completion is a `Noop` (it must have something to keep, on the same tree) —
    /// never to construct a receipt.
    let classify
        (runnableProjects: string list)
        (previous: TestEvidenceReceipt option)
        (currentInputTree: string option)
        (currentModelGeneration: int64 option)
        (launch: TestRunLaunch)
        (completed: TestRunCompleted)
        (coverage: RunCoverage)
        : ReceiptTransition =
        let previousBoundToCurrentTree =
            match previous with
            | Some prior -> ReceiptInputTree.matches prior.InputTreeHash currentInputTree
            | None -> false

        let executed =
            completed.Results
            |> Map.exists (fun _ result -> TestResult.executedTests result)

        let quietAlreadyVerified =
            launch.ZeroSelection = ZeroSelection.AlreadyVerified
            && Map.isEmpty coverage
            && Map.isEmpty completed.Results

        match completed.Outcome with
        | Aborted reason -> ReceiptTransition.Revoked $"the run aborted: %s{reason}"
        | Normal when launch.ModelGeneration <> currentModelGeneration ->
            ReceiptTransition.Revoked "the project model was replaced between launch and completion"
        | Normal when quietAlreadyVerified ->
            if previousBoundToCurrentTree then
                ReceiptTransition.Noop
            else
                ReceiptTransition.Revoked "already verified, but no receipt is bound to the current tree"
        | Normal when not executed -> ReceiptTransition.Revoked "the run executed no project to a verdict"
        | Normal when not (ReceiptInputTree.matches launch.InputTreeHash currentInputTree) ->
            ReceiptInputTree.classifyMismatch launch.InputTreeHash currentInputTree
            |> ReceiptInputTree.describeMismatch
            |> ReceiptTransition.Revoked
        | Normal ->
            let narrowerThanPrevious =
                previousBoundToCurrentTree
                && allResultsCompleted completed
                && (previous
                    |> Option.exists (fun prior -> RunCoverage.coversWholeSuite runnableProjects prior.Coverage))
                && not (RunCoverage.coversWholeSuite runnableProjects coverage)

            if narrowerThanPrevious then
                ReceiptTransition.Noop
            else
                ReceiptTransition.Earned
                    { InputTreeHash = currentInputTree
                      RunId = completed.RunId
                      Coverage = coverage
                      Seeds = launch.Seeds
                      ZeroSelection = launch.ZeroSelection }

    /// Fold one transition into the store. Total, and the only place the store changes
    /// on a completion.
    let apply (previous: TestEvidenceReceipt option) (transition: ReceiptTransition) : TestEvidenceReceipt option =
        match transition with
        | ReceiptTransition.Earned receipt -> Some receipt
        | ReceiptTransition.Noop -> previous
        | ReceiptTransition.Revoked _ -> None

/// The launch selection `executeTests` will actually honour, from the per-project class
/// map. ONE derivation, so the run's real selection and the one projects
/// through cannot read the same map differently.
///
/// An EMPTY map means "no selection" → every project runs in full; a project present with
/// `[]` runs in full; present with classes runs filtered; ABSENT is skipped entirely.
let internal selectionOf
    (configs: TestConfig list)
    (byProject: Map<string, string list>)
    : Map<string, ProjectSelection> =
    if Map.isEmpty byProject then
        configs |> List.map (fun c -> c.Project, ProjectInFull) |> Map.ofList
    else
        configs
        |> List.choose (fun c ->
            match Map.tryFind c.Project byProject with
            | None -> None
            | Some [] -> Some(c.Project, ProjectInFull)
            | Some classes -> Some(c.Project, ProjectClasses(Set.ofList classes)))
        |> Map.ofList

/// What `check` would have launched over this tree — the run's own
/// selection with the FULL-SUITE widening taken back out, and only that one.
///
/// The other widenings stay, and that is the whole care in this function. The coarse
/// fallback for unanalysable files and the unreadable-ledger fallback
/// fire in the inner loop too, so a projection that dropped them would
/// model a `check` narrower than the one that actually runs — and would then report
/// misses the selector never made. Only `set-scope full`, which is `confirm`'s own doing,
/// is removed.
let internal wouldHaveRunSelection
    (configs: TestConfig list)
    (symbolAffectedByProject: Map<string, string list>)
    (coarseWidened: Set<string>)
    (runtimeCoverageProjects: Set<string>)
    (ledgerUnreadable: bool)
    : Map<string, ProjectSelection> =
    let impactWidened = Set.union coarseWidened runtimeCoverageProjects

    let checkForceRunProjects =
        if ledgerUnreadable then
            Set.union impactWidened (fullSuiteProjects configs)
        else
            impactWidened

    checkForceRunProjects
    |> Set.fold (fun acc proj -> Map.add proj [] acc) symbolAffectedByProject
    |> selectionOf configs

/// The same `ScopeReport`, for a selection that was never LAUNCHED — the
/// one `check` would have used before `confirm` widened it.
///
/// Reads the REQUEST, where `scopeOf` reads the receipt, and that is the whole difference
/// between them: there is no run to produce evidence for a run that did not happen. It is
/// used only to describe the projected reading's scope in the verdict file, never to
/// grant coverage — `RunCoverage` still comes from what executed.
let internal scopeOfSelection (projects: string list) (selection: Map<string, ProjectSelection>) : ScopeReport =
    let total = List.length projects
    let selected = projects |> List.filter (fun p -> Map.containsKey p selection)

    if List.isEmpty selected then
        ScopeNone total
    elif
        List.length selected = total
        && selected |> List.forall (fun p -> selection.[p] = ProjectInFull)
    then
        ScopeFull total
    else
        ScopeFiltered(List.length selected, total)

module internal OutstandingFailure =

    /// Prior reds are verification debt, not merely verdict decoration. Add every
    /// configured red scope to the next ordinary impact selection so a failing test
    /// cannot disappear just because the next edit reaches a different part of the
    /// graph. A failure without a class is project-scoped and therefore promotes the
    /// whole project to an unfiltered run.
    let quarantine
        (configured: Set<string>)
        (failures: OutstandingFailure list)
        (affectedByProject: Map<string, string list>)
        : Map<string, string list> =
        failures
        |> List.filter (fun failure -> Set.contains failure.Project configured)
        |> List.groupBy (fun failure -> failure.Project)
        |> List.fold
            (fun selected (project, projectFailures) ->
                if projectFailures |> List.exists (fun failure -> Option.isNone failure.Class) then
                    Map.add project [] selected
                else
                    let quarantinedClasses =
                        projectFailures |> List.choose (fun failure -> failure.Class)

                    match Map.tryFind project selected with
                    | Some [] -> selected
                    | Some affectedClasses ->
                        Map.add project (affectedClasses @ quarantinedClasses |> List.distinct) selected
                    | None -> Map.add project (quarantinedClasses |> List.distinct) selected)
            affectedByProject

    /// Identity for de-duplication: the same class failing the same way twice is one
    /// red, not two. (`Entry` is compared by message only — the detail is the full
    /// runner output, which differs by timing/ordering between otherwise identical
    /// failures.)
    let private identity (f: OutstandingFailure) =
        f.Project, f.Class, f.Method, f.File, f.Entry.Message

    /// The reds a run CARRIES: prior failures it did not cover, and so cannot speak
    /// for. A filtered class selection covers an exact prior method only when this
    /// run's complete CTRF receipt contains that method passing; a sibling green is
    /// not contradictory evidence. These deny an otherwise-passing run its verdict.
    ///
    /// `configured` prunes reds for projects the daemon no longer runs (a project
    /// removed from `tests.projects` could otherwise never be covered again, and its
    /// red would wedge the verdict forever — the unconfigured-project stuck-red, rebuilt). Empty
    /// ⇒ analysis-only, nothing to prune by.
    let carriedOver
        (configured: Set<string>)
        (coverage: RunCoverage)
        (passedTests: Map<string, Set<string * string>>)
        (prior: OutstandingFailure list)
        : OutstandingFailure list =
        let stillConfigured (f: OutstandingFailure) =
            Set.isEmpty configured || Set.contains f.Project configured

        let covered (failure: OutstandingFailure) =
            match failure.Class, failure.Method, Map.tryFind failure.Project coverage with
            | _, _, Some CoveredWholeProject -> true
            | Some cls, Some methodName, Some(CoveredClasses classes) ->
                Set.contains cls classes
                && (passedTests
                    |> Map.tryFind failure.Project
                    |> Option.exists (Set.contains (cls, methodName)))
            | _, None, _ -> RunCoverage.covers failure.Project failure.Class coverage
            | _ -> false

        prior |> List.filter (fun f -> stillConfigured f && not (covered f))

    /// The outstanding set after a run: what it carried, plus what it found. A red the
    /// run COVERED is not carried — if it failed again, `found` re-adds it from THIS
    /// run's evidence; if it passed, it is gone, which is the whole point (no permanent
    /// stuck-red). Defined in terms of `carriedOver` so the status verdict and the
    /// ledger can never disagree about what is still red.
    let carry
        (configured: Set<string>)
        (coverage: RunCoverage)
        (passedTests: Map<string, Set<string * string>>)
        (found: OutstandingFailure list)
        (prior: OutstandingFailure list)
        : OutstandingFailure list =
        carriedOver configured coverage passedTests prior @ found
        |> List.distinctBy identity

    /// Human-readable "what is still red that this run did not look at", for the
    /// status verdict. Names the projects, not every class — the ledger has the detail.
    let summarize (failures: OutstandingFailure list) : string =
        failures
        |> List.map (fun f -> f.Project)
        |> List.distinct
        |> List.sort
        |> String.concat ", "

    // -----------------------------------------------------------------------
    // The reds are DURABLE.
    //
    // They used to be session-scoped, on the argument that a restarted daemon has no
    // `LastResults` and so runs the full suite, which re-finds any red. That argument
    // holds only when the durable pending queue is EMPTY at restart. With symbols
    // queued — the ordinary case after a red run, because a failing project blocks the
    // commit of every symbol it covers — the restarted daemon's first run is
    // impact-FILTERED, `hasCachedResults` is then true, and a red from the previous
    // session that the filter does not reach is never selected again: the
    // never-selected-red shape, reproduced by a restart. Quarantine is memory; memory that
    // does not survive the process is not memory.
    //
    // Same rules as `PendingVerification`: a missing file is an honest empty; a file
    // that exists and cannot be read is `Unreadable`, and the caller treats that as
    // debt of unknown membership (the unreadable-ledger recovery: widen to the full suite,
    // which re-executes every test and rebuilds this list from evidence).
    //
    // `Entry.Detail` is deliberately NOT persisted. It is the captured runner output
    // — unbounded, and already on disk under `.fshw/test-runs/<runId>/` — and a
    // quarantined red is re-executed by the very next run, which re-derives it.
    // -----------------------------------------------------------------------

    [<RequireQualifiedAccess>]
    type LoadedFailures =
        | Loaded of OutstandingFailure list
        | Unreadable of reason: string

    let sidecarPath (repoRoot: string) : string =
        Path.Combine(FsHwPaths.root repoRoot, "test-prune", "outstanding-failures.json")

    let private toJson (f: OutstandingFailure) : Nodes.JsonObject =
        let obj = Nodes.JsonObject()
        obj.["project"] <- Nodes.JsonValue.Create(f.Project)

        obj.["class"] <-
            (match f.Class with
             | Some c -> Nodes.JsonValue.Create(c)
             | None -> null)

        obj.["method"] <-
            (match f.Method with
             | Some m -> Nodes.JsonValue.Create(m)
             | None -> null)

        obj.["file"] <- Nodes.JsonValue.Create(f.File)
        obj.["message"] <- Nodes.JsonValue.Create(f.Entry.Message)
        obj.["severity"] <- Nodes.JsonValue.Create(ErrorLedger.DiagnosticSeverity.toString f.Entry.Severity)
        obj.["line"] <- Nodes.JsonValue.Create(f.Entry.Line)
        obj.["column"] <- Nodes.JsonValue.Create(f.Entry.Column)
        obj

    let private ofJson (node: Nodes.JsonNode) : Result<OutstandingFailure, string> =
        if isNull node then
            Error "a `null` entry where a failure was expected"
        else
            try
                let obj = node.AsObject()

                let str (name: string) =
                    match obj.[name] with
                    | null -> None
                    | v -> Some(v.GetValue<string>())

                let int (name: string) =
                    match obj.[name] with
                    | null -> 0
                    | v -> v.GetValue<int>()

                match str "project", str "file", str "message", str "severity" with
                | Some project, Some file, Some message, Some severity ->
                    match ErrorLedger.DiagnosticSeverity.fromString severity with
                    | None -> Error $"unknown severity '%s{severity}'"
                    | Some severity ->
                        Ok
                            { Project = project
                              Class = str "class"
                              Method = str "method"
                              File = file
                              Entry =
                                { Message = message
                                  Severity = severity
                                  Line = int "line"
                                  Column = int "column"
                                  Detail = None } }
                | _ -> Error $"an entry missing `project`, `file`, `message` or `severity` (%s{node.ToJsonString()})"
            with ex ->
                Error $"%s{ex.GetType().Name}: %s{ex.Message}"

    let load (repoRoot: string) : LoadedFailures =
        let path = sidecarPath repoRoot

        if not (File.Exists path) then
            LoadedFailures.Loaded []
        else
            try
                let json = File.ReadAllText path

                if String.IsNullOrWhiteSpace json then
                    LoadedFailures.Unreadable
                        "the file is empty — `save` always writes at least `[]`, so this is a torn write"
                else
                    match Nodes.JsonNode.Parse(json) with
                    | null -> LoadedFailures.Unreadable "the file holds a bare JSON `null`, not an array of failures"
                    | root ->
                        let read = root.AsArray() |> Seq.map ofJson |> List.ofSeq

                        match
                            read
                            |> List.tryPick (function
                                | Error reason -> Some reason
                                | Ok _ -> None)
                        with
                        | Some reason -> LoadedFailures.Unreadable reason
                        | None ->
                            read
                            |> List.choose (function
                                | Ok f -> Some f
                                | Error _ -> None)
                            |> LoadedFailures.Loaded
            with ex ->
                LoadedFailures.Unreadable $"%s{ex.GetType().Name}: %s{ex.Message}"

    let save (repoRoot: string) (failures: OutstandingFailure list) : unit =
        let arr = Nodes.JsonArray()

        for f in failures do
            arr.Add(toJson f)

        FsHwPaths.atomicWriteAllText (sidecarPath repoRoot) (arr.ToJsonString())

/// The ledger diagnostic for a file the symbol analyser could not read. A WARNING keyed
/// to the file itself, so it surfaces in `fshw check` output and — under the default
/// warn-fail policy — denies the check a green verdict. A log line would not: the
/// plugin's status is overwritten by the very next file's `Completed`.
let internal unanalyzableFileDiagnostic (relPath: string) (reason: string) : ErrorLedger.ErrorEntry =
    ErrorLedger.ErrorEntry.warningWithDetail
        $"%s{relPath}: symbol analysis failed — %s{reason}"
        $"TestPrune could not extract symbols from this file, so it is INVISIBLE to the impact graph: a change to it \
           has no symbols to diff and would select no tests on its own. Every test project is being run in full for \
           this cycle (safe over-selection) until the file analyses cleanly. Fix the reported parse/check error — a \
           misplaced `///` doc comment (FS3520) is the usual cause."

/// The ledger key an extension's refresh failure is reported under. Not a file: the
/// `<...>` form other plugins use for a non-file source, so it can never collide with one.
let internal extensionLedgerKey (extensionName: string) : string = $"<extension:%s{extensionName}>"

/// The ledger diagnostic for an extension whose `AnalyzeEdges` threw. An ERROR, not a log
/// line: TestPrune keeps the extension's previously stored edges, which describe an older
/// tree, so selection over them can silently miss a test the current tree couples in.
let internal extensionFailedDiagnostic (extensionName: string) (reason: string) : ErrorLedger.ErrorEntry =
    ErrorLedger.ErrorEntry.errorWithDetail
        $"test-impact extension '%s{extensionName}' failed: %s{reason}"
        "The extension could not compute its dependency edges for the current tree, so the edges it stored on its \
         last successful refresh describe an older one. Until it answers, every test project runs in full (safe \
         over-selection) rather than trusting a selection made over those edges. It is retried on the next flush; \
         this entry clears once it answers."

/// Build the filter arg string for a config given affected classes. Each class name is
/// quoted with `ProcessHelper.quoteArg` before the join, so a name containing spaces
/// (a backticked sentence-style test module) reaches the runner as ONE argument instead
/// of word-splitting into several that match nothing. An unspaced
/// name is unchanged.
let internal buildFilterArgs (config: TestConfig) (classesByProject: Map<string, string list>) : string option =
    let classes =
        classesByProject |> Map.tryFind config.Project |> Option.defaultValue []

    match classes, config.FilterTemplate with
    | [], _ -> None
    | _, None ->
        Logging.debug "test-prune" $"No filterTemplate configured — running all tests for %s{config.Project}"
        None
    | classes, Some template ->
        let joined =
            classes |> List.map ProcessHelper.quoteArg |> String.concat config.ClassJoin

        let result = template.Replace("{classes}", joined)
        Logging.info "test-prune" $"Filter: %s{result}"
        Some result

/// Microsoft.Testing.Platform exit code for "no tests matched / zero tests ran".
[<Literal>]
let internal zeroTestsExitCode = 8

/// True when a *filtered* run matched no tests in this project. An explicit
/// `--filter-*` passthrough (run-tests / test-rerun) is fanned out to EVERY test
/// project; a project that has no test matching the filter runs zero tests and
/// the runner exits non-zero (MTP uses exit code 8, `ZeroTests`). That is NOT a
/// test failure — the tests simply don't exist for this filter — so it must be
/// treated like an impact-skip (passed/filtered, contributing no coverage),
/// exactly as a template-filtered project with no affected classes already is.
///
/// Gated on `wasFiltered`: an UNFILTERED project that runs zero tests is a real
/// problem (misconfigured runner, empty suite) and must still surface, so this
/// returns false for it. Detection is structural (the canonical exit code) with
/// a text fallback for runners that exit non-zero without emitting code 8 but
/// still print MTP's zero-tests summary line.
let internal isZeroTestsUnderFilter (wasFiltered: bool) (outcome: ProcessOutcome) : bool =
    wasFiltered
    && match outcome with
       | ProcessOutcome.Failed(code, _) when code = zeroTestsExitCode -> true
       | ProcessOutcome.Failed(_, output) ->
           // A text SEARCH for a marker: a capture cut short by an unfinished drain
           // can only cost us the hit (falling back to the exit code above), never
           // invent one. Sound to search the untagged text.
           (ProcessOutput.text output).Contains("Zero tests ran", StringComparison.OrdinalIgnoreCase)
       | _ -> false

/// THE CAUSE OF A FAILED RUN THAT NAMED NO TEST — never empty.
///
/// A run refused by the shard-pool guard ("Test shard pool '…' is already in use by
/// PID 6862 …") printed its cause as line 1 of a 351-byte output. That line reached
/// `logs/daemon.log` and the run's `.output.log`; the ledger entry — and so the verdict's
/// `reddenedBy`, the surface agents are told to read — said only "Tests failed in X", and
/// a reader of it concluded a wedged daemon and reaped daemons that were fine. The tool
/// knew the cause and told the reader where to look instead of what it knew.
///
/// So the message CARRIES the cause: the HEAD of the captured output (the first
/// `HeadLines` non-blank lines, bounded by `HeadMaxChars`), then the path of the full
/// log. Content first, pointer second. The head, not the tail, because the head is where
/// a killed, wedged or refused run states its cause — the same claim the daemon-log
/// sentence had always made while printing the tail.
///
/// And it is a TYPE with a private constructor: the only two builders each yield a
/// sentence, so a blank output cannot produce a blank message — it produces
/// `unknownPointing`, which SAYS nothing was captured. "Unexplained" and "explained
/// elsewhere" are different facts, and an empty string states neither.
type FailureCause = private FailureCause of string

/// The runner's console output as words. A terminal-aware runner (MTP colours `failed`
/// red and a duration grey) prints a failing line whose BYTES start with an escape
/// sequence, not with `failed`; every matcher below reads the line with the colour
/// removed, so what it matches is what a reader sees.
module ConsoleText =
    let private ansiSequence =
        System.Text.RegularExpressions.Regex(
            @"\u001b\[[0-9;?]*[ -/]*[@-~]",
            System.Text.RegularExpressions.RegexOptions.Compiled
        )

    /// `text` with every ANSI CSI sequence (colour, cursor movement, erase) removed.
    let stripAnsi (text: string) : string =
        if isNull text then "" else ansiSequence.Replace(text, "")

    /// The output's lines, colour removed, `\r` dropped.
    let lines (output: string) : string[] =
        (stripAnsi output).Split('\n') |> Array.map (fun l -> l.TrimEnd('\r'))

    /// The runner's summary block — its `Test run summary:` line and the count lines
    /// under it — when the output has one. A run that printed it ran to completion;
    /// a run killed, wedged or refused never reaches it.
    let summaryOf (output: string) : string list =
        let all = lines output

        match
            all
            |> Array.tryFindIndexBack (fun l -> l.TrimStart().StartsWith("Test run summary:"))
        with
        | None -> []
        | Some index ->
            let isCount (l: string) =
                let t = l.TrimStart()

                [ "total:"; "failed:"; "succeeded:"; "skipped:"; "duration:" ]
                |> List.exists (fun prefix -> t.StartsWith prefix)

            all.[index].Trim()
            :: (all.[index + 1 ..]
                |> Array.takeWhile isCount
                |> Array.map (fun l -> l.Trim())
                |> Array.toList)

module FailureCause =
    /// How many non-blank lines of the head are quoted. Twenty: an MTP runner's banner
    /// (version, discovery, the first migrations) is under ten lines, so a cause stated
    /// at the start of the run is inside the excerpt with room to spare, while a verdict
    /// listing `MaxRedCauses` such entries stays a page, not a log.
    [<Literal>]
    let HeadLines = 20

    /// The byte bound on the head, so twenty lines of a verbose runner cannot turn the
    /// verdict file into the run log. 4 KB × ten causes is the most `reddenedBy` grows.
    [<Literal>]
    let HeadMaxChars = 4096

    /// The first `HeadLines` non-blank lines of `output`, trimmed, cut to `HeadMaxChars`
    /// in total. Empty only when the output has no non-blank line.
    let headOf (output: string) : string list =
        let lines =
            (if isNull output then "" else output).Split('\n')
            |> Array.map (fun l -> l.TrimEnd('\r').TrimEnd())
            |> Array.filter (fun l -> not (System.String.IsNullOrWhiteSpace l))
            |> Array.truncate HeadLines
            |> Array.toList

        // Spend the character budget line by line (one char per line for the newline);
        // the line that crosses it is cut and marked, and nothing follows it.
        let rec take (budget: int) (acc: string list) (rest: string list) =
            match rest with
            | [] -> List.rev acc
            | l :: tail when l.Length + 1 <= budget -> take (budget - l.Length - 1) (l :: acc) tail
            | l :: _ when budget > 1 -> List.rev ((l.Substring(0, budget - 1) + "…") :: acc)
            | _ -> List.rev acc

        take HeadMaxChars [] lines

    /// Where the full output is, or why it is nowhere. ONE rendering of the `RunLog.Ref`,
    /// so both builders point the same way.
    let private pointer (runLog: RunLog.Ref) : string =
        match runLog with
        | RunLog.Ref.Written path -> $"see %s{path}"
        | RunLog.Ref.Unavailable reason -> $"no output log was saved (%s{reason})"

    /// The message when the run produced NOTHING to quote. Says so explicitly, and
    /// points at the log (or at the reason there is none).
    let unknownPointing (project: string) (runLog: RunLog.Ref) : FailureCause =
        FailureCause
            $"%s{project}: run failed, no per-test 'failed' line was parsed, and no cause captured — the runner \
              produced no output to quote; %s{pointer runLog}"

    /// Where the full output is, for a message that has just quoted part of it.
    let private fullOutput (runLog: RunLog.Ref) : string =
        match runLog with
        | RunLog.Ref.Written path -> $"full output: %s{path}"
        | RunLog.Ref.Unavailable reason ->
            $"full output was NOT saved (%s{reason}); the lines above are all that was kept"

    /// The message for a run whose output named no failing test, and whose CTRF report
    /// named none either. A run that printed its summary ran to completion, so the
    /// summary is quoted: the head of such a run is the daemon's banner and says nothing
    /// about the red. A run with no summary was killed, wedged or refused, and the head
    /// is where it stated its cause. Routes a blank output to `unknownPointing`, so this
    /// is the only door from captured text and cannot yield an empty message.
    let ofOutput (project: string) (runLog: RunLog.Ref) (output: string) : FailureCause =
        let quote (excerpt: string list) =
            excerpt |> List.map (fun l -> "  | " + l) |> String.concat "\n"

        match ConsoleText.summaryOf output with
        | _ :: _ as summary ->
            FailureCause
                $"%s{project}: run failed and ran to completion, but neither the runner's console nor its CTRF report named a failing test. The runner's summary:\n%s{quote summary}\n%s{fullOutput runLog}"
        | [] ->
            match headOf output with
            | [] -> unknownPointing project runLog
            | head ->
                FailureCause
                    $"%s{project}: run failed but no per-test 'failed' line was parsed. The run's output begins (first %d{head.Length} non-blank lines; the head is where a killed, wedged or refused run states its cause):\n%s{quote head}\n%s{fullOutput runLog}"

    /// The sentence. Total; never empty by construction.
    let render (FailureCause s) : string = s

/// Build the human-readable error lines for a FAILED test project run, parsed
/// from the runner's captured `output`. The header line plus the per-test
/// `failed ...` lines plus the MTP summary lines (`total:`/`failed:`/
/// `succeeded:`).
///
/// CRITICAL for CI observability: a red run that only reports "failed: 1" with
/// NO test name is undiagnosable when the on-disk `.fshw/test-runs` log isn't
/// uploaded as an artifact. MTP prints a failing test as a line whose TRIMMED
/// form starts with `failed ` — INCLUDING `failed (canceled) <name> (Nms)` for a
/// test killed by its `[<Fact(Timeout=...)>]` under CI load (the documented
/// daemon-load flake class). We match the trimmed prefix so leading indentation
/// (which varies by MTP version / capture path) never hides the name. As a
/// backstop, when the run failed but NO `failed ` line parsed (a crash, an
/// OOM-kill, or an output shape the matcher doesn't yet recognise), the tail of
/// the captured output is echoed so the failure is ALWAYS visible from the CI
/// console alone — never silently swallowed into "0 test(s) failed".
///
/// The tail is a SUMMARY and stays one. It is also, structurally, the wrong end of
/// the output for a whole class of failure: a suite killed at its timeout printed
/// its cause in the first seconds and its noise for the fifteen minutes since, so
/// forty lines of tail are forty lines of noise. `runLog` is the answer to that —
/// the full, head-included, streamed capture — and this message NAMES it.
///
/// The path comes from a `RunLog.Ref`, never a formatted guess: a path is printed only
/// when something actually opened it, and otherwise the REASON there is no file takes
/// its place. A message that points at a log nobody wrote is worse than none.
///
/// The summary is taken from BOTH ends. The sentence above already said
/// the head is where a killed or wedged run states its cause — and then printed only the
/// tail, so the one thing it claimed to know was the one thing it withheld. A refused run
/// (the shard-pool guard) says everything on line 1; a run killed mid-way says how far it
/// got on the last line. The head is `FailureCause.headOf`, the same excerpt the ledger
/// entry (and so `reddenedBy`) carries, so the daemon log and the verdict agree.
let internal formatFailureReport (projectName: string) (runLog: RunLog.Ref) (output: string) : string list =
    let lines = ConsoleText.lines output

    let isFailedLine (l: string) = l.TrimStart().StartsWith("failed ")

    let failedTests = lines |> Array.filter isFailedLine |> Array.toList

    // The runner's own summary lines, by their leading word: a daemon-log line that
    // happens to CONTAIN `failed:` (`P: 0 test(s) failed:`) is not one of them.
    let summaryLines =
        lines
        |> Array.filter (fun l ->
            let t = l.TrimStart()

            t.StartsWith("Test run summary:")
            || t.StartsWith("total:")
            || t.StartsWith("failed:")
            || t.StartsWith("succeeded:"))
        |> Array.toList

    [ $"%s{projectName}: %d{failedTests.Length} test(s) failed:"
      yield! failedTests |> List.map (fun l -> $"  %s{l.TrimEnd()}")
      yield! summaryLines |> List.map (fun l -> $"  %s{l.TrimEnd()}")
      if List.isEmpty failedTests && not (List.isEmpty (ConsoleText.summaryOf output)) then
          // The summary above is the runner's own: the run ran to completion, so its
          // head is a banner and its tail is the summary already printed.
          match runLog with
          | RunLog.Ref.Written path ->
              $"%s{projectName}: run failed and ran to completion, but no per-test 'failed' line was parsed from \
                its console; the CTRF report beside the output log names the tests. Full output: %s{path}"
          | RunLog.Ref.Unavailable reason ->
              $"%s{projectName}: run failed and ran to completion, but no per-test 'failed' line was parsed from \
                its console, and NO output log was saved (%s{reason})"
      elif List.isEmpty failedTests then
          let nonBlank =
              lines |> Array.filter (fun l -> not (System.String.IsNullOrWhiteSpace l))

          let head = FailureCause.headOf output
          let tail = nonBlank.[max head.Length (nonBlank.Length - 40) ..]

          match runLog with
          | RunLog.Ref.Written path ->
              $"%s{projectName}: run failed but no per-test 'failed' line was parsed. The FULL output was streamed \
                to %s{path}. The first %d{head.Length} of its %d{nonBlank.Length} non-blank lines follow — the \
                HEAD is where a killed, wedged or refused run states its cause:"
          | RunLog.Ref.Unavailable reason ->
              $"%s{projectName}: run failed but no per-test 'failed' line was parsed, and NO output log was saved \
                (%s{reason}) — so the lines below are ALL there is. The first %d{head.Length} of %d{nonBlank.Length} \
                non-blank lines — the HEAD is where a killed, wedged or refused run states its cause:"

          yield! head |> List.map (fun l -> $"  | %s{l}")

          if not (Array.isEmpty tail) then
              $"%s{projectName}: … and the last %d{tail.Length} lines, where a run killed mid-way says how far it got:"
              yield! tail |> Array.map (fun l -> $"  | %s{l.TrimEnd()}") |> Array.toList ]

/// The human-readable report for a run whose HOST DIED — the counterpart to
/// `formatFailureReport`, and deliberately NOT it.
///
/// `formatFailureReport` counts the `failed ...` lines it can parse and heads them
/// "N test(s) failed". Run over a KILLED host's transcript that header is the whole
/// defect this report exists to fix: a runner cut off mid-suite still has per-test rows
/// in its captured output — rows at 0ms, with no assertion message, for tests that
/// never executed — and printing them under "N test(s) failed" turns a non-result
/// into a definite negative. Every hour that symptom cost was spent investigating
/// code that was fine.
///
/// So an abort gets its OWN words: what killed the host, that NOTHING was verified,
/// and that the lines below are a TRANSCRIPT of a run that was killed rather than a
/// list of findings. The rows are still printed — they are the only evidence of how
/// far the run got — but they are never counted, and never called failures.
let internal formatAbortReport
    (projectName: string)
    (runLog: RunLog.Ref)
    (reason: string)
    (output: string)
    : string list =
    let lines = output.Split('\n')

    let transcript =
        lines
        |> Array.filter (fun l -> not (System.String.IsNullOrWhiteSpace l))
        |> (fun ls -> ls.[max 0 (ls.Length - 40) ..])

    [ $"%s{projectName}: RUN ABORTED — %s{reason}"
      $"%s{projectName}: NOTHING WAS VERIFIED. This is NOT a test failure: no test was shown to fail, and no         test was shown to pass. Do not read the lines below as a regression — a runner killed mid-suite still         has rows in its capture for tests it never executed (0ms, no assertion message). Re-run on a machine         with headroom (e.g. `dotnet fshw test-rerun`); if it aborts again, the host is dying for a reason that         is not load."
      match runLog with
      | RunLog.Ref.Written path ->
          $"%s{projectName}: the FULL output — including the HEAD, which is where a dying host states its             cause — was streamed to %s{path}. READ THAT FIRST; the transcript tail follows only as a summary:"
      | RunLog.Ref.Unavailable why ->
          $"%s{projectName}: NO output log was saved (%s{why}), so the transcript tail below is ALL there is             and the head of the run is gone:"
      yield! transcript |> Array.map (fun l -> $"  | %s{l.TrimEnd()}") |> Array.toList ]

/// What is known about the structured test report for a run — the verdict
/// input. Modelled as a DU rather than a `bool * report option` so the
/// meaningless "no report was requested yet somehow a parsed report exists"
/// state cannot be constructed.
type ReportEvidence =
    /// No report was requested (an unknown / unsupported runner) — the process
    /// exit code is the only pass/fail signal available.
    | NoReportRequested
    /// A report WAS requested from a capable runner. `Ok` carries a report that passed
    /// `Ctrf.tryVerdictReport`; `Error` says why there is none — the file was absent or
    /// unreadable (the host aborted before flushing), or what it held is not evidence (a
    /// truncated report, malformed counters, a clean summary its rows do not account for).
    | ReportRequested of report: Result<Ctrf.VerdictReport, string>

/// Decide a single project's verdict. The structured test report (when present and
/// parseable) is AUTHORITATIVE for pass/fail; the process exit code is only a tie-break
/// when there is no usable report. Exit-code-only produced false REDs: a test host that
/// exits non-zero during a dirty shutdown (the Microsoft.Testing.Platform exit-7 flake)
/// after flushing a clean report reported "Tests failed" with zero named tests, while
/// `test-rerun` came back green.
///
/// Precedence (apphost-missing / zero-match-under-filter are handled by the
/// caller BEFORE this — they are not test outcomes):
///   1. report has any failed/other result → `TestsFailed` (red). Exit irrelevant.
///   2. report is all-clear (no failed/other) AND ran ≥1 test → `TestsPassed`
///      (green) EVEN IF the process exited non-zero — the flake case.
///   3. a report WAS requested from a capable runner and there is no usable one
///      (absent, unreadable, or not coherent evidence) → `TestsErrored` on ANY exit:
///      nothing was verified. Never green, never the misleading "tests failed". A
///      clean exit is not a substitute for the report that was asked for.
///   4. no report was requested (unknown runner) → the exit code is the only signal
///      there is → `TestsPassed` or `TestsFailed`.
///
///   0. AND BEFORE ALL OF THEM: the host was TERMINATED BY A SIGNAL → `TestsErrored`.
///      A killed host did not finish, so nothing it wrote is a result — including a
///      CTRF report it managed to flush on the way down, whose rows for tests that
///      never executed are exactly the mass 0ms "failures" seen under CPU load.
///      This arm is why the report is not consulted there: outcome 1 would read that
///      partial report and call a machine that ran out of CPU a mass regression.
///   A `summary.tests == 0` report that reaches here is an UNFILTERED zero-test
///   run (the filtered case was handled upstream) — a real misconfiguration. It is
///   never green: a non-zero exit keeps it red, and a clean exit verified nothing.
///
/// Outcome 2 deliberately does NOT also require a whitelisted shutdown exit code: the
/// benign codes are runner/version-specific, and a report positively showing zero
/// failures is stronger evidence than the exit number.
///
/// Outcome 0 is the one direction where the exit code OUTRANKS the report, and only
/// because of what that particular exit code means: `TerminatingSignal` recognises
/// codes no runner CHOOSES, so it can only ever fire for a host that was killed. A
/// suite that genuinely goes red exits with a code the runner picked (MTP's are single
/// digits) and still reaches outcome 1 — the assertion this must survive in BOTH
/// directions, because a real mass failure dressed as an abort is the same lie with
/// the sign flipped.
let internal classifyTestOutcome
    (evidence: ReportEvidence)
    (wasFiltered: bool)
    (elapsed: System.TimeSpan)
    (outcome: ProcessOutcome)
    : TestResult =
    match outcome, terminatingSignalOf outcome with
    | ProcessOutcome.TimedOut(after, output, kill), _ ->
        // A timeout KILL is a real "stuck" signal; a partial report it may have flushed
        // must not override it.
        //
        // This arm renders the tail itself rather than going through `outputOf`, so it
        // must append `renderKill` explicitly — otherwise a test-runner tree we FAILED
        // to kill would be reported as a plain "stuck" timeout while it kept running,
        // holding the test DB / the port / the lock that makes the NEXT run fail too.
        TestsTimedOut(renderOutput output + renderKill kill, after, wasFiltered, elapsed)
    | _, Some signal ->
        // Outcome 0. The host was KILLED. `TestsErrored` — `NothingVerified`, never a
        // pass and never a reported failure — because a process that did not reach its
        // own exit did not finish measuring anything.
        //
        // The report state is NAMED rather than consulted: a reader who sees a partial
        // CTRF beside this message needs to be told the rows in it are a transcript,
        // not a verdict, or they will open it and find their "mass regression".
        let reportNote =
            match evidence with
            | ReportRequested(Ok report) ->
                let r = Ctrf.VerdictReport.summary report

                $" It had flushed a PARTIAL report ({r.Total} row(s), {r.Failed} of them marked failed) before it                    died; those rows are a transcript of a killed run, NOT results — a test the host never reached                    is written out the same way as one that ran."
            | ReportRequested(Error reason) -> $" It wrote no usable report: %s{reason}."
            | NoReportRequested -> " No structured report was requested from this runner."

        TestsErrored(
            $"test host was KILLED by %s{signal.Name} (exit %d{TerminatingSignal.SignalExitBase + signal.Number})               — it never reached its own exit, so NOTHING was verified.%s{reportNote}"
        )
    | _, None ->
        let output = outputOf outcome
        let succeeded = isSucceeded outcome

        match evidence with
        | ReportRequested(Ok report) ->
            let r = Ctrf.VerdictReport.summary report

            if r.Failed > 0 || r.Other > 0 then
                // Outcome 1.
                TestsFailed(output, wasFiltered, elapsed)
            elif r.Total > 0 then
                // Outcome 2 — green even on a non-zero exit (the dirty-shutdown flake).
                TestsPassed(output, wasFiltered, elapsed)
            elif succeeded then
                // An unfiltered zero-test run that exited cleanly. The report is coherent
                // and proves only that the runner wrote it.
                TestsErrored "test host exited cleanly but its report counts zero tests — nothing verified"
            else
                // An unfiltered zero-test run that exited non-zero: an empty suite stays red.
                TestsFailed(output, wasFiltered, elapsed)
        | ReportRequested(Error reason) ->
            // Outcome 3 — the report that was asked for is not evidence, whatever the exit.
            let exit = if succeeded then "exited cleanly" else "exited non-zero"
            TestsErrored $"test host %s{exit} but left no usable report (%s{reason}) — nothing verified"
        | NoReportRequested when succeeded ->
            // Outcome 4. Unknown runner we never asked for a report: the exit code is all there is.
            TestsPassed(output, wasFiltered, elapsed)
        | NoReportRequested -> TestsFailed(output, wasFiltered, elapsed)

/// Tokenize the `ProcessStartInfo.Arguments` string far enough to discover a project
/// path — the one word-splitting rule in `ProcessHelper.splitArgs`. An unfinished quote
/// is not a partial command line, so discovery fails closed.
let private argTokens (args: string) : string[] option = ProcessHelper.splitArgs args

/// The value following `--project`/`-p` in the quote-aware tokenized args.
/// Shared by `tryApphostPresent` and `detectCtrfRunnerFamily` (both derive a project
/// from the runner command line the same way).
let private projectFlagValue (tokens: string[]) : string option =
    tokens
    |> Array.tryFindIndex (fun t -> t = "--project" || t = "-p")
    |> Option.bind (fun i -> if i + 1 < tokens.Length then Some tokens.[i + 1] else None)

/// Derive the runner's build-output target (project file, dir, assembly name,
/// `bin/Debug`) from its `--project` arg, or `None` when no `--project`/`-p`
/// token is present (a custom, non-`dotnet run` command). The `--project` value
/// may point at a `.fsproj`/`.csproj` file OR a directory; the assembly name
/// defaults to the project/dir leaf — matching `ProjectGraph.GetCanonicalDllPath`,
/// which uses the project file's base name. Shared by `tryApphostPresent`
/// (presence) and `ArtifactFreshness.stale` (freshness) so this
/// fsproj-or-directory derivation has ONE definition.
let internal deriveProjectBin (args: string) (repoRoot: string) : ArtifactFreshness.RunnerTarget option =
    argTokens args
    |> Option.bind projectFlagValue
    |> Option.map (fun proj ->
        // Resolve to an absolute path (relative paths are repoRoot-relative).
        let abs =
            if Path.IsPathRooted proj then
                proj
            else
                Path.Combine(repoRoot, proj)

        let projectFile, projDir, assemblyName =
            if
                File.Exists abs
                && (abs.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || abs.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            then
                Some abs, Path.GetDirectoryName(abs), Path.GetFileNameWithoutExtension(abs)
            else
                // Treat as a directory. The assembly name conventionally matches
                // the directory leaf; if a single project file lives there, prefer
                // that file's base name.
                let dir = abs.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

                let projFile =
                    if Directory.Exists dir then
                        Directory.GetFiles(dir, "*.fsproj")
                        |> Array.append (Directory.GetFiles(dir, "*.csproj"))
                        |> Array.tryHead
                    else
                        None

                projFile,
                dir,
                (projFile
                 |> Option.map Path.GetFileNameWithoutExtension
                 |> Option.defaultValue (Path.GetFileName dir))

        { ArtifactFreshness.ProjectFile = projectFile
          ArtifactFreshness.ProjectDir = projDir
          ArtifactFreshness.AssemblyName = assemblyName
          ArtifactFreshness.BinDir = Path.Combine(projDir, "bin", "Debug") })

/// STRUCTURAL apphost-missing detection. On a cold daemon a `dotnet run --project
/// <proj> --no-build` can be launched before the build plugin produced that project's
/// apphost binary; `dotnet run` then fails to spawn it and exits non-zero. That is an
/// ORDERING bug, never a test failure.
///
/// Derived from the runner's `--project` arg and a `File.Exists`, rather than sniffing
/// localized OS error text out of the runner output — that is fragile to locale and SDK
/// phrasing (`looksLikeApphostMissing` keeps it only as a fallback). The apphost is the
/// extension-less sibling of `<projDir>/bin/Debug/<tfm>/<assemblyName>.dll` (`.exe` on
/// Windows); the TFM is unknown without the project graph, so every `bin/Debug/*/` dir
/// is globbed. Presence only — `ArtifactFreshness.stale` is the freshness complement.
///
/// Returns:
///   Some true  — project derivable AND apphost present
///   Some false — project derivable AND apphost absent (the deferred signal)
///   None       — could not derive a project from args (e.g. a non-`dotnet run`
///                custom command), OR its `bin/Debug` could not be listed; caller
///                falls back to the output sniff.
///
/// An unlistable `bin/Debug` is `None`, not `Some false`: `Some false` DEFERS the run as
/// "waiting on build", which would dress a failure we cannot diagnose from the
/// filesystem as a build that has not landed yet. `None` is this function's existing
/// "the filesystem cannot answer" — and the sniff reads the runner's own output instead.
let internal tryApphostPresent (args: string) (repoRoot: string) : bool option =
    deriveProjectBin args repoRoot
    |> Option.bind (fun target ->
        // The apphost lives at bin/Debug/<tfm>/<assemblyName>(.exe). We don't
        // know the TFM, so scan every TFM output dir for the extension-less
        // binary (Unix) or the `.exe` (Windows); no build output yet ⇒ no TFM
        // dirs ⇒ apphost definitionally absent.
        match ArtifactFreshness.tfmOutputDirs target.BinDir with
        | Error _ -> None
        | Ok tfmDirs ->
            tfmDirs
            |> Array.exists (fun tfmDir ->
                File.Exists(Path.Combine(tfmDir, target.AssemblyName))
                || File.Exists(Path.Combine(tfmDir, target.AssemblyName + ".exe")))
            |> Some)


/// The xUnit runner major determines the names of its CTRF report switches.
/// xUnit 4 names them `--report-xunit-ctrf*`; xUnit 3 used
/// `--report-ctrf*`. Passing the other family's switches is fatal and runs zero
/// tests, so unknown majors are deliberately not treated as a best guess.
type internal CtrfRunnerFamily =
    | Xunit3
    | Xunit4

/// Render only the switches owned by the resolved runner family. The results
/// directory is an MTP switch and is shared by both supported families.
let internal ctrfArguments (family: CtrfRunnerFamily) (reportName: string) (resultsDirectory: string) : string =
    match family with
    | Xunit3 -> $"--report-ctrf --report-ctrf-filename %s{reportName} --results-directory \"%s{resultsDirectory}\""
    | Xunit4 ->
        $"--report-xunit-ctrf --report-xunit-ctrf-filename %s{reportName} --results-directory \"%s{resultsDirectory}\""

/// Resolve the actual xUnit runner family from NuGet's restored graph. The
/// project file can contain central versions, properties, ranges, or stale
/// text, while `obj/project.assets.json` records the exact package version that
/// will run. Missing/malformed assets and unsupported majors return `None` so
/// auto-detection fails closed rather than giving a runner fatal arguments.
let internal detectCtrfRunnerFamily (args: string) (repoRoot: string) : CtrfRunnerFamily option =
    let tokens = argTokens args

    let looksLikeProjectFile (t: string) =
        t.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || t.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)

    // The project hint: the value after --project/-p, else any token that is
    // itself a project file path (e.g. `dotnet test path/to/Proj.fsproj`).
    let projArg =
        tokens
        |> Option.bind (fun parsed ->
            projectFlagValue parsed
            |> Option.orElse (parsed |> Array.tryFind looksLikeProjectFile))

    let resolveProjectFile (proj: string) : string option =
        let abs =
            if Path.IsPathRooted proj then
                proj
            else
                Path.Combine(repoRoot, proj)

        if File.Exists abs && looksLikeProjectFile abs then
            Some abs
        elif Directory.Exists abs then
            Directory.GetFiles(abs, "*.fsproj")
            |> Array.append (Directory.GetFiles(abs, "*.csproj"))
            |> Array.tryHead
        else
            None

    projArg
    |> Option.bind resolveProjectFile
    |> Option.bind (fun projFile ->
        try
            let assetsPath =
                Path.Combine(Path.GetDirectoryName(projFile), "obj", "project.assets.json")

            use assets = JsonDocument.Parse(File.ReadAllText assetsPath)
            let mutable libraries = Unchecked.defaultof<JsonElement>

            if assets.RootElement.TryGetProperty("libraries", &libraries) then
                let xunitLibraries =
                    libraries.EnumerateObject()
                    |> Seq.choose (fun package ->
                        let slash = package.Name.IndexOf('/')

                        if
                            slash <= 0
                            || not (
                                package.Name.AsSpan(0, slash).Equals("xunit.v3", StringComparison.OrdinalIgnoreCase)
                            )
                        then
                            None
                        else
                            Some package)
                    |> Seq.toList

                let completeNuGetVersion =
                    System.Text.RegularExpressions.Regex(
                        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:\\.(0|[1-9][0-9]*))?(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant
                    )

                let tryFamily (package: JsonProperty) =
                    let mutable libraryType = Unchecked.defaultof<JsonElement>

                    if
                        not (package.Value.TryGetProperty("type", &libraryType))
                        || libraryType.ValueKind <> JsonValueKind.String
                        || not (String.Equals(libraryType.GetString(), "package", StringComparison.Ordinal))
                    then
                        None
                    else
                        let slash = package.Name.IndexOf('/')

                        // NuGet library keys use `<id>/<normalized-version>`. We only
                        // need the SemVer major; System.Version deliberately rejects
                        // valid NuGet prerelease suffixes such as `4.0.0-pre.12`.
                        let version = package.Name.Substring(slash + 1)
                        let matched = completeNuGetVersion.Match version

                        let numericComponents =
                            if matched.Success then
                                [ 1..4 ]
                                |> List.choose (fun group ->
                                    if matched.Groups[group].Success then
                                        Some matched.Groups[group].Value
                                    else
                                        None)
                                |> List.map Int32.TryParse
                            else
                                []

                        let prereleaseIdentifiersAreValid =
                            if matched.Success && matched.Groups[5].Success then
                                matched.Groups[5].Value.Split('.')
                                |> Array.forall (fun identifier ->
                                    let numeric = identifier |> Seq.forall Char.IsAsciiDigit
                                    not (numeric && identifier.Length > 1 && identifier[0] = '0'))
                            else
                                matched.Success

                        match numericComponents, prereleaseIdentifiersAreValid with
                        | (true, 3) :: remaining, true when remaining |> List.forall fst -> Some Xunit3
                        | (true, 4) :: remaining, true when remaining |> List.forall fst -> Some Xunit4
                        | _ -> None

                let parsedFamilies = xunitLibraries |> List.map tryFamily

                if List.isEmpty parsedFamilies || List.exists Option.isNone parsedFamilies then
                    None
                else
                    let families = parsedFamilies |> List.choose id |> Set.ofList

                    // Multiple patch/minor versions from one major are harmless, but two
                    // runner majors make the accepted command line ambiguous. Do not let
                    // JSON property order decide which suite gets fatal flags.
                    if families.Count = 1 then
                        families |> Seq.exactlyOne |> Some
                    else
                        None
            else
                None
        with _ ->
            None)

/// Fallback apphost-missing classifier, used ONLY when `tryApphostPresent` cannot derive
/// a project from the runner args (custom, non-`dotnet run` commands).
///
/// Distinguishes an apphost launch failure from a genuine non-zero test exit: a real
/// xUnit/MTP failure carries `failed <name>` lines and a `failed:`/`Test run summary`
/// block, while the launch failure carries the host's "An error occurred trying to start
/// process …" / "No such file or directory" signature and NO test-summary block.
/// Deliberately conservative — in doubt, treat the output as a real failure and never
/// silence a red.
let internal looksLikeApphostMissing (output: string) : bool =
    if String.IsNullOrWhiteSpace output then
        false
    else
        let lower = output.ToLowerInvariant()

        // Signatures the .NET host emits when it cannot launch the apphost the
        // build was supposed to produce.
        let hasStartProcessFailure =
            lower.Contains("an error occurred trying to start process")
            || (lower.Contains("no such file or directory")
                && (lower.Contains("apphost")
                    || lower.Contains("/bin/")
                    || lower.Contains("\\bin\\")))
            || lower.Contains("apphost_version not found")

        // A genuine test run always emits a summary / per-test `failed ` lines.
        // Their PRESENCE means the runner actually executed tests, so this is a
        // real failure, not a launch race — don't misclassify it.
        let looksLikeRealTestFailure =
            lower.Contains("test run summary")
            || lower.Contains("failed:")
            || (output.Split('\n')
                |> Array.exists (fun l -> l.TrimStart().StartsWith("failed ")))

        hasStartProcessFailure && not looksLikeRealTestFailure

/// Split a fully-qualified test name into (class, method): the LAST dotted segment is
/// the method and the COMPLETE prefix is the class. A name with no dot is its own class.
/// Keeping the prefix is load-bearing: launch selections use fully qualified class
/// names, so truncating `A.B.Tests.method` to `Tests` makes a test run and pass while
/// its retained red remains permanently "not covered" (d223).
///
/// ONE derivation, used by BOTH sides of the ledger: the class a red is FILED under
/// (`parseFailedTests`, off the runner's `failed <name>` console lines) and the class a
/// run's report VINDICATES (`passedClassesOfReport`, off the CTRF `name` field). The two
/// read the same runner's rendering of the same fully-qualified name, so sharing the
/// split is what makes retirement match filing — an xUnit display name that defeats the
/// heuristic defeats it identically on both sides, and a key that fails to match simply
/// leaves the red standing.
let internal splitTestName (name: string) : string * string =
    let separator = name.LastIndexOf('.')

    if separator > 0 && separator < name.Length - 1 then
        name.Substring(0, separator), name.Substring(separator + 1)
    else
        name, name

/// Parse "failed Namespace.Class.Method (Xms)" lines from test output, colour removed —
/// `failed (canceled) <name> (Xms)` and a multi-unit duration `(10s 112ms)` included.
/// Returns (className, methodName, fullLine) tuples; `fullLine` is the line as words.
let parseFailedTests (output: string) : (string * string * string) list =
    ConsoleText.lines output
    |> Array.choose (fun line ->
        let trimmed = line.Trim()

        if trimmed.StartsWith("failed ") then
            // Strip the "failed " prefix, a parenthesised qualifier such as
            // "(canceled)" that MTP puts before the name, and the trailing duration.
            let rest =
                let afterVerb = trimmed.Substring(7).Trim()

                if afterVerb.StartsWith("(") then
                    match afterVerb.IndexOf(')') with
                    | -1 -> afterVerb
                    | close -> afterVerb.Substring(close + 1).Trim()
                else
                    afterVerb

            let name =
                match rest.LastIndexOf(" (") with
                | -1 -> rest
                | i -> rest.Substring(0, i)

            let className, methodName = splitTestName name
            Some(className, methodName, trimmed)
        else
            None)
    |> Array.toList

/// The classes ONE project's CTRF report proves RAN AND PASSED in this run
/// — the receipt `RunCoverage.ofRun` reads for a raw `--filter`
/// passthrough, whose launch REQUEST claims nothing.
///
/// A class is claimed only when the report holds at least one PASSED test for it and
/// NO failed/other test for it. Everything else fails CLOSED — an empty set, which
/// leaves every red exactly as it was:
///
///   * no parseable SUMMARY block → the report was truncated or never flushed;
///   * per-test array shorter (or longer) than the summary's total → the array is
///     INCOMPLETE. A real report omits per-test entries for tests that threw a raw
///     (non-assertion) exception while still counting them in the summary, so a class
///     could look all-green here while one of its tests exploded. Counting is the only
///     way to see the omission, so an array that does not account for every test in the
///     summary is not evidence about ANY class in it;
///   * a class with a failed/other entry, or with no passed entry at all (all skipped)
///     → not claimed.
///
/// Skips are neutral rather than disqualifying, because the unfiltered arm they must
/// agree with is: a full run whose report contains skips still returns
/// `CoveredWholeProject` and clears everything. A rule that let a skip block a class
/// here would be stricter than the whole-project path it is a refinement of.
let internal passedTestsOfReport (json: string) : Set<string * string> =
    match Ctrf.trySummary json with
    | None -> Set.empty
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        if records.IsEmpty || records.Length <> summary.Total then
            Set.empty
        else
            records
            |> List.groupBy (fun r -> splitTestName r.Name)
            |> List.choose (fun (testIdentity, forTest) ->
                let anyPassed = forTest |> List.exists (fun r -> r.Outcome = Flakiness.Passed)

                let anyUnvindicated =
                    forTest
                    |> List.exists (fun r ->
                        match r.Outcome with
                        | Flakiness.Failed
                        | Flakiness.Other -> true
                        | Flakiness.Passed
                        | Flakiness.Skipped -> false)

                if anyPassed && not anyUnvindicated then
                    Some testIdentity
                else
                    None)
            |> Set.ofList

let internal passedClassesOfReport (json: string) : Set<string> =
    match Ctrf.trySummary json with
    | None -> Set.empty
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        if records.IsEmpty || records.Length <> summary.Total then
            Set.empty
        else
            records
            |> List.groupBy (fun r -> fst (splitTestName r.Name))
            |> List.choose (fun (cls, forClass) ->
                let anyPassed = forClass |> List.exists (fun r -> r.Outcome = Flakiness.Passed)

                let anyUnvindicated =
                    forClass
                    |> List.exists (fun r ->
                        match r.Outcome with
                        | Flakiness.Failed
                        | Flakiness.Other -> true
                        | Flakiness.Passed
                        | Flakiness.Skipped -> false)

                if anyPassed && not anyUnvindicated then Some cls else None)
            |> Set.ofList

/// The per-project passed-class evidence for a completed run, read back from the CTRF
/// reports THAT RUN wrote (`.fshw/test-runs/<runId>/<Project>.ctrf.json`).
///
/// Scoped by RUN DIRECTORY, so a previous run's report can never be mistaken for this
/// one's: the directory IS the run. A project with no readable report contributes no
/// entry, and a project absent from the map claims nothing — the absence of evidence is
/// never evidence of a pass.
let internal passedClassesOfRun (repoRoot: string) (runId: Guid) : Map<string, Set<string>> =
    Ctrf.reportsForRun repoRoot runId
    |> List.choose (fun report ->
        let json =
            try
                Some(File.ReadAllText report.Path)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None

        match json |> Option.map passedClassesOfReport with
        | Some classes when not (Set.isEmpty classes) -> Some(report.Project, classes)
        | _ -> None)
    |> Map.ofList

let internal passedTestsOfRun (repoRoot: string) (runId: Guid) : Map<string, Set<string * string>> =
    Ctrf.reportsForRun repoRoot runId
    |> List.choose (fun report ->
        let json =
            try
                Some(File.ReadAllText report.Path)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None

        match json |> Option.map passedTestsOfReport with
        | Some tests when not (Set.isEmpty tests) -> Some(report.Project, tests)
        | _ -> None)
    |> Map.ofList

/// Exact failed rows from a COMPLETE CTRF report. The summary's failed count is the
/// denominator authority; if the per-test array omits even one failure, this refuses
/// the sample rather than shrinking recall's denominator to the rows that happened to
/// parse.
let internal failedTestsOfReport (json: string) : (string * string) list option =
    match Ctrf.trySummary json with
    | None -> None
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        let failed =
            records |> List.filter (fun record -> record.Outcome = Flakiness.Failed)

        if records.Length <> summary.Total || failed.Length <> summary.Failed then
            None
        else
            failed |> List.map (fun record -> splitTestName record.Name) |> Some

/// Every failed row of a CTRF report, by fully-qualified name, whether or not the rows
/// reconcile to the summary. Naming is not counting: a report that omitted a raw-throw
/// row still names the assertions that failed, and each name is a red the console did
/// not spell out.
let internal failedRowsOfReport (json: string) : string list =
    Flakiness.parseCtrfTests json
    |> List.filter (fun record -> record.Outcome = Flakiness.Failed)
    |> List.map (fun record -> record.Name)

let internal sharedInfrastructureFailureOfReport (json: string) : string option =
    try
        use document = JsonDocument.Parse json
        let mutable results = Unchecked.defaultof<JsonElement>
        let mutable tests = Unchecked.defaultof<JsonElement>

        if
            document.RootElement.TryGetProperty("results", &results)
            && results.TryGetProperty("tests", &tests)
            && tests.ValueKind = JsonValueKind.Array
        then
            let failedRows =
                tests.EnumerateArray()
                |> Seq.filter (fun row ->
                    let mutable status = Unchecked.defaultof<JsonElement>
                    row.TryGetProperty("status", &status) && status.GetString() = "failed")
                |> Seq.toList

            let rec stringDescendants (element: JsonElement) =
                seq {
                    match element.ValueKind with
                    | JsonValueKind.String -> yield element.GetString()
                    | JsonValueKind.Object ->
                        for property in element.EnumerateObject() do
                            yield! stringDescendants property.Value
                    | JsonValueKind.Array ->
                        for item in element.EnumerateArray() do
                            yield! stringDescendants item
                    | _ -> ()
                }

            let exceptionTexts (row: JsonElement) =
                seq {
                    for property in row.EnumerateObject() do
                        if
                            property.Name.Equals("trace", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Equals("stack", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Equals("exception", StringComparison.OrdinalIgnoreCase)
                        then
                            yield! stringDescendants property.Value
                }

            let containsTypeIdentity (identity: string) (text: string) =
                let isIdentifierCharacter c =
                    Char.IsLetterOrDigit c || c = '_' || c = '.' || c = '`'

                let rec search start =
                    let index = text.IndexOf(identity, start, StringComparison.Ordinal)

                    if index < 0 then
                        false
                    else
                        let beforeIsBoundary = index = 0 || not (isIdentifierCharacter text[index - 1])
                        let after = index + identity.Length
                        let afterIsBoundary = after = text.Length || not (isIdentifierCharacter text[after])

                        if beforeIsBoundary && afterIsBoundary then
                            true
                        else
                            search (index + 1)

                search 0

            let causesOf (row: JsonElement) =
                exceptionTexts row
                |> Seq.collect (fun text ->
                    [ if containsTypeIdentity "Npgsql.NpgsqlException" text then
                          "NpgsqlException"
                      if containsTypeIdentity "System.Net.Sockets.SocketException" text then
                          "SocketException" ])
                |> Seq.toList

            match failedRows with
            | [] -> None
            | rows ->
                match rows |> List.collect causesOf |> List.distinct with
                | [] -> None
                | causes -> Some(String.concat "/" causes)
        else
            None
    with
    | :? JsonException
    | :? InvalidOperationException -> None

let internal failedTestsOfRun
    (repoRoot: string)
    (runId: Guid)
    (results: TestResults)
    : Result<OutstandingFailure list, string> =
    let reports =
        Ctrf.reportsForRun repoRoot runId
        |> List.map (fun report -> report.Project, report)
        |> Map.ofList

    results.Results
    |> Map.toList
    |> List.fold
        (fun state (project, result) ->
            state
            |> Result.bind (fun accumulated ->
                if TestResult.isTimedOut result then
                    Error $"%s{project} timed out, so no complete failing-test denominator exists"
                else
                    match result, Map.tryFind project reports with
                    | TestsErrored reason, _ ->
                        Error $"%s{project} did not produce an executable test result: %s{reason}"
                    | TestsDeferred reason, _ -> Error $"%s{project} tests were deferred: %s{reason}"
                    | TestsNoMatch _, _ -> Error $"%s{project} executed no tests"
                    | TestsPassed _, _ -> Ok accumulated
                    | (TestsFailed _ | TestsTimedOut _), None ->
                        Error $"%s{project} failed but wrote no current-run CTRF report"
                    | _, Some report ->
                        try
                            let json = File.ReadAllText report.Path

                            match sharedInfrastructureFailureOfReport json, failedTestsOfReport json with
                            | Some cause, _ -> Error $"%s{project} failed through shared infrastructure (%s{cause})"
                            | None, None -> Error $"%s{project}'s CTRF failed rows do not reconcile to its summary"
                            | None, Some identities ->
                                identities
                                |> List.map (fun (className, methodName) ->
                                    { Project = project
                                      Class = Some className
                                      Method = Some methodName
                                      File = report.Path
                                      Entry = ErrorLedger.ErrorEntry.error "observed full-run failure" })
                                |> List.append accumulated
                                |> Ok
                        with
                        | :? IOException
                        | :? UnauthorizedAccessException -> Error $"%s{project}'s CTRF report could not be read"))
        (Ok [])

/// The reds a completed run FOUND, as outstanding failures. Pure:
/// the same run always yields the same set, so the ledger projection is a function
/// of the evidence and nothing else.
///
/// A red is attributed to a CLASS only when the runner named one AND the result is a
/// genuine test failure. A TIMEOUT is deliberately project-level (`Class = None`)
/// even when its output names tests: a project killed for being stuck is a fact
/// about the PROJECT, and a later class-filtered green must not be allowed to
/// vindicate it. Deferred/errored projects are project-level for the same reason —
/// nothing about them was verified.
///
/// `runLogOf` names the run log a project's output was streamed to: a
/// project-level red carries the head of its output AND the path, so the verdict says
/// what the tool knows and where the rest is.
let internal failuresOf
    (runLogOf: string -> RunLog.Ref)
    (reportFailuresOf: string -> string list)
    (classFiles: Map<string, string>)
    (results: TestResults)
    : OutstandingFailure list =
    let synthetic (project: string) = $"<tests/%s{project}>"

    results.Results
    |> Map.toList
    |> List.collect (fun (project, result) ->
        let projectLevel (entry: ErrorLedger.ErrorEntry) =
            { Project = project
              Class = None
              Method = None
              File = synthetic project
              Entry = entry }

        match result with
        | TestsFailed(output, _, _)
        | TestsTimedOut(output, _, _, _) ->
            // A timeout is never attributable to one class (see above).
            let isTimeout = TestResult.isTimedOut result

            // The console names the reds; when it names none, the CTRF report the
            // runner wrote beside the output log names them instead. The report row is
            // the same fully-qualified name the console prints, so a red filed from it
            // is retired by the same passing row.
            let parsed =
                match parseFailedTests output with
                | [] ->
                    reportFailuresOf project
                    |> List.map (fun name ->
                        let className, methodName = splitTestName name

                        className,
                        methodName,
                        $"failed %s{name} — named by the run's CTRF report; the console printed no line the parser recognised")
                | fromConsole -> fromConsole

            if parsed.IsEmpty then
                // ONE entry for the project, so the whole captured output is carried
                // ONCE. This is the only arm where that text is the entry's own
                // subject: no test was named, so the run itself is what the reader has
                // to go on.
                //
                // The MESSAGE carries the head of that output and the
                // log path — the verdict records only `Message`, and "Tests failed in X"
                // with the cause hidden in a `Detail` no surface prints sent a reader to
                // reap daemons over a shard-pool refusal stated on line 1.
                [ projectLevel (
                      ErrorLedger.ErrorEntry.errorWithDetail
                          (FailureCause.render (FailureCause.ofOutput project (runLogOf project) output))
                          output
                  ) ]
            else
                parsed
                |> List.map (fun (className, methodName, line) ->
                    let file =
                        classFiles |> Map.tryFind className |> Option.defaultValue (synthetic project)

                    { Project = project
                      Class = (if isTimeout then None else Some className)
                      Method = (if isTimeout then None else Some methodName)
                      File = file
                      // The failing LINE, and no detail.
                      //
                      // This used to attach `output` — the whole captured project run —
                      // to every parsed failure, which made the ledger's size the
                      // PRODUCT of two unbounded numbers rather than their sum. 753
                      // failing tests against a 48 MB capture is 36 GB once a mirror of
                      // the ledger writes each entry's copy out, and the merge gate died
                      // building exactly that reply after the daemon had already
                      // finished the run.
                      //
                      // Nothing is lost by dropping it, because it was never this
                      // entry's fact: it was the same project-wide string repeated per
                      // test, no renderer has ever printed a ledger `Detail`, and the
                      // untruncated capture is on disk at
                      // `.fshw/test-runs/<runId>/<project>.output.log`. The transport
                      // bound in `ErrorLedger.Transport` still stands behind this — a
                      // plugin must not be able to do it again — but the bound is a
                      // backstop, and this is the defect.
                      Entry = ErrorLedger.ErrorEntry.error line })
        | TestsDeferred reason ->
            // NOT a test failure — surface an honest "waiting on build / did not
            // run" diagnostic at `Deferred` severity so the verdict is NON-green
            // (nothing was verified) yet NOT a red: the CLI routes any
            // `Deferred`-severity entry to `Incomplete`/exit 2, distinct from the
            // exit 1 a real failure earns. This entry still joins the Outstanding
            // failure list, so cache participation stays refused (a deferred run is
            // never replayed as a green) — the severity governs the VERDICT, the
            // outstanding LIST governs the CACHE, and the two are decoupled.
            // The DETAIL used to assert one cause for every defer: "its build artifact
            // (apphost) was not produced … a build-ordering issue". That is false for a
            // stale-artifact refusal, where the artifact WAS produced and holds bytes
            // that do not match its sources — and it is false in the expensive
            // direction, because a build-ordering race settles on its own while stale
            // output does not. A reader told to wait it out re-runs and gets the
            // identical refusal, which is the defect the stale-artifact preflight exists to delete.
            // So the detail is derived from the reason, not asserted over it.
            let detail =
                if StaleArtifactPreflight.isStaleOutputDeferral reason then
                    $"The %s{project} test project did not run because its build OUTPUT is stale: the artifact \
                      exists, but its bytes do not match the sources it was built from, so running the suite would \
                      test code you did not write. This is NOT a build-ordering race and re-running will not settle \
                      it — the reason above names the file and the command that repairs it."
                else
                    $"The %s{project} test project did not run because its build artifact (apphost) was not \
                      produced. Tests were NOT executed, so this cycle cannot be reported as passing. This is a \
                      build-ordering issue, not a test failure."

            [ projectLevel (
                  ErrorLedger.ErrorEntry.deferredWithDetail $"%s{project}: waiting on build — %s{reason}" detail
              ) ]
        | TestsErrored reason ->
            // NOT a test failure (no test was shown to fail) and NOT a pass
            // (nothing was verified) — an honest "aborted" diagnostic so the
            // verdict is non-green without the misleading "Tests failed in X".
            //
            // It is reported at `HostAborted` SEVERITY, which is what makes that honesty
            // reach the verdict. At `Error` it was counted by `failingDiagnostics`,
            // and every surface downstream — the exit code, `verdict.json`'s outcome,
            // the terminal — then said "failures found" about a run in which nothing
            // failed. The severity routes it to `CheckOutcome.RunnerAborted`/exit 2
            // instead, exactly as `Deferred` routes a defer to `WaitingOnBuild`.
            //
            // It still joins the OUTSTANDING list, so cache participation stays
            // refused and an abort is never replayed as a green: the severity governs
            // the VERDICT, the outstanding list governs the CACHE.
            [ projectLevel (
                  ErrorLedger.ErrorEntry.abortedWithDetail
                      $"%s{project}: aborted — %s{reason}"
                      $"The %s{project} test host did not finish, so NO pass/fail verdict could be derived — nothing was verified. This is NOT a reported test failure and NOT a pass. Any per-test lines in the captured output are a TRANSCRIPT of a killed run, not findings: a test the host never reached is written out the same way as one that ran, which is why an abort must never be counted as failures. Re-run on a machine with headroom (e.g. `dotnet fshw test-rerun`). A run that only goes green on retry is itself a real failure, so this stays non-green."
              ) ]
        // No ledger entry, same as a pass. A filter matching nothing in THIS project is
        // not this project's error — the run-level verdict is where a workspace-wide
        // zero match is refused, and duplicating it here would put a
        // red on every project an ordinary impact selection happened not to name.
        | TestsNoMatch _
        | TestsPassed _ -> [])

/// Rewrite this plugin's whole slice of the error ledger to be exactly what is still
/// OUTSTANDING.
///
/// `ClearAllErrors` (== `ClearPlugin "test-prune"`) wipes the slate, and MUST always be
/// paired with a re-report of everything the plugin still owes: every outstanding red,
/// including ones carried from earlier runs this one did not cover, and every
/// unanalysable-file warning. Re-reporting only THIS run's findings
/// lets a narrower run erase reds it never executed, and drops the warning that is
/// supposed to deny the check its green verdict.
///
/// Clearing-and-re-reporting is the ONLY path to the ledger, so an entry can disappear
/// only by leaving the outstanding set: a red needs a run that COVERED it, a warning
/// needs the file to analyse cleanly.
let private reportOutstanding
    (ctx: PluginCtx<TestPruneMsg>)
    (unanalyzable: Map<string, UnanalyzableFile>)
    (failedExtensions: Map<string, string>)
    (outstanding: OutstandingFailure list)
    =
    ctx.ClearAllErrors()

    let failureEntries = outstanding |> List.map (fun f -> f.File, f.Entry)

    let unanalyzableEntries =
        unanalyzable
        |> Map.toList
        |> List.map (fun (_, u) -> u.File, unanalyzableFileDiagnostic u.RelPath u.Reason)

    let extensionEntries =
        failedExtensions
        |> Map.toList
        |> List.map (fun (name, reason) -> extensionLedgerKey name, extensionFailedDiagnostic name reason)

    failureEntries @ unanalyzableEntries @ extensionEntries
    |> List.groupBy fst
    |> List.iter (fun (file, entries) -> ctx.ReportErrors file (entries |> List.map snd))

let private flakinessHistoryPath (repoRoot: string) =
    Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "test-history.json")

/// What a run's `executeTests` needs to record traces: the run's trace settings and
/// mode, the decision function, and the plugin's activity log and subtasks.
type internal TraceRunHost =
    { TraceRuntime: TraceRuntime
      TraceWiring: TraceWiring
      TraceLog: string -> unit
      HoldSubtask: string -> string -> IDisposable }

/// Execute test configs with optional affected classes for filtering. Handles beforeRun,
/// coveragePaths, process execution, result storage. `rawFilter` is a passthrough filter
/// string (from the run-tests command) that bypasses the template.
///
/// Emission contract (when `ctx` is Some):
///   1. `TestRunStarted` once, before any group begins.
///   2. `TestProgress` once per group as it completes, carrying only that
///      group's projects as a delta.
///   3. `TestRunCompleted` once, after all groups finish, carrying the full
///      cumulative Results plus an Outcome.
/// All three share a single RunId generated at the start of the run.
///
/// `ctx = None` (a one-off command) fires no lifecycle events — the caller just gets the
/// final TestResults — and also disables the skip-on-stale shortcut, so a manual run is
/// never deadlocked by a stuck dirty bit. See the staleness branch below.
let private executeTests
    (db: Database)
    (ctx: PluginCtx<'msg> option)
    (emitStarted: TestRunStarted -> unit)
    (repoRoot: string)
    (launchDeadline: TimeSpan)
    // `tests.traces`, resolved for this run's mode, with the plugin's activity log (which
    // a force run's `ctx = None` would otherwise lose); `None` when not configured, which
    // leaves every launch exactly as it is without traces.
    (traces: TraceRunHost option)
    // The input tree hash the run was launched against, for the traces it records.
    (launchTreeHash: string option)
    // Receives the run id so the hook's own timings can be filed
    // under this run, beside the CTRF reports the verdict already reads.
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    // Where each step `beforeRun` runs is held while it runs: the plugin's subtasks, on
    // every path, so a wait or wedge inside the setup names the step it is on.
    (trackHookStep: HookStep.Tracker)
    (coveragePaths: (string -> CoveragePaths option) option)
    (coverageIngestFailed: CoverageIngestFailure -> unit)
    (afterRun: (TestResults -> unit) option)
    (configuredCoverageProjects: TestConfig list)
    (configs: TestConfig list)
    (affectedClassesByProject: Map<string, string list>)
    (rawFilter: string option)
    =
    async {
        Logging.info "test-prune" $"executeTests starting with %d{configs.Length} configs"
        let sw = Stopwatch.StartNew()
        let runId = Guid.NewGuid()

        // The run's own directory. Created NOW — before anything runs — so that a run
        // which executes but reports nothing is distinguishable from a run that never
        // happened: the first leaves an empty directory, the second leaves none.
        let runDir = Ctrf.runDir repoRoot runId

        try
            Directory.CreateDirectory(runDir) |> ignore
        with
        | :? IOException
        | :? UnauthorizedAccessException as ex ->
            Logging.warn "test-prune" $"could not create the run directory %s{runDir}: %s{ex.Message}"

        let isFilteredRun = not affectedClassesByProject.IsEmpty || Option.isSome rawFilter

        let primaryLabel =
            if isFilteredRun then
                $"running %d{configs.Length} selected test projects"
            else
                $"running full suite (%d{configs.Length} projects)"

        let startedAt = DateTime.UtcNow

        ctx |> Option.iter (fun c -> c.StartSubtask PrimarySubtaskKey primaryLabel)
        // `TestRunStarted` is emitted by the CALLER (which receives `started` in the
        // returned tuple), so the synchronous `TestsFinished` handler can fire it inside
        // the cache-write capture window.
        let started: TestRunStarted = { RunId = runId; StartedAt = startedAt }
        emitStarted started

        match beforeRun with
        | Some setup ->
            Logging.info "test-prune" "Running beforeRun setup..."
            setup runId trackHookStep
            Logging.info "test-prune" "beforeRun complete"
        | None -> ()

        let groups = configs |> List.groupBy (fun c -> c.Group)

        let coveragePathsByProject =
            configuredCoverageProjects
            |> List.choose (fun config ->
                coveragePaths
                |> Option.bind (fun pathsForProject -> pathsForProject config.Project)
                |> Option.map (fun paths -> config.Project, paths))
            |> Map.ofList

        let enabledRatchetProjects =
            coveragePathsByProject
            |> Map.toSeq
            |> Seq.choose (fun (project, paths) -> if paths.IncludeInRatchet then Some project else None)
            |> Set.ofSeq

        let configuredCoverageOutput =
            coveragePathsByProject
            |> Map.toSeq
            |> Seq.tryHead
            |> Option.map (fun (_, paths) -> paths.Cobertura)

        // Impact analysis may have decided a project has no affected classes. Such a
        // project never launches, so it is neither preflighted (no artifacts worth
        // walking) nor deferred (nothing was going to run).
        let skipProjectOf (config: TestConfig) =
            not affectedClassesByProject.IsEmpty
            && not (affectedClassesByProject |> Map.containsKey config.Project)

        /// What an impact-skipped project reports. Skipped-due-to-impact-analysis is the
        /// strongest form of filtering — hence `wasFiltered = true`, so no caller mistakes
        /// it for a full run — and its coverage contribution is "nothing new". Elapsed is
        /// Zero because the test runner never started.
        ///
        /// Beside `skipProjectOf` rather than at either call site: the refusal path and the
        /// run loop both answer for skipped projects, and they have to answer the same way.
        let skippedResultOf (config: TestConfig) =
            config.Project, TestsPassed("", true, TimeSpan.Zero)

        // Cumulative results built up as groups complete. Mutable under a lock
        // so concurrent group completions see a consistent prefix-chain. Per-
        // group deltas are emitted via TestProgress; the final cumulative is
        // carried by TestRunCompleted (and returned to non-daemon callers).
        let mutable cumulative: Map<string, TestResult> = Map.empty
        let accumulatorLock = obj ()

        // Raw-cobertura ingest inputs collected across the parallel per-project
        // runs. Each entry retains the producing project and ratchet intent.
        // Ingest+emit runs SERIALLY after
        // Async.Parallel completes so concurrent group completions never race on
        // the DB write or the single shared output file.
        let mutable coverageInputs: CoverageInput list = []
        let mutable coverageReceiptFailures: CoverageIngestFailure list = []
        let coverageRawPathsLock = obj ()

        // Per-test flakiness records, COLLECTED here and written ONCE after the
        // parallel section — exactly like `coverageRawPaths` above, and for both of
        // the same reasons. `Flakiness.appendRecords` is a full parse + full rewrite
        // of the whole history file, so a per-config call from inside the parallel
        // group body would mean one parse+rewrite cycle per project, AND a
        // read-modify-write racing itself across parallel groups (two projects
        // finishing together each load the same history, and the second writer drops
        // the first's records).
        let mutable flakinessRecords: Flakiness.TestRunRecord list = []
        let flakinessLock = obj ()

        // Each launched project's trace decision, ingested SERIALLY after the parallel
        // section, beside coverage and for the same reason: one writer to one store.
        let mutable traceRuns: TracedProjectRun list = []
        let traceRunsLock = obj ()

        /// Write a line to the plugin's activity log when there is a host to write to.
        /// One binding for the whole run: the preflight, the refusal path and the per-
        /// config runner all report through it, and it is allocated once rather than
        /// per config.
        let logToCtx msg = ctx |> Option.iter (fun c -> c.Log msg)

        let foldAndEmit (groupOutput: (string * TestResult) list) =
            lock accumulatorLock (fun () ->
                for (k, v) in groupOutput do
                    cumulative <- Map.add k v cumulative

                ctx
                |> Option.iter (fun c ->
                    c.EmitTestProgress
                        { RunId = runId
                          NewResults = Map.ofList groupOutput }))

        // STALE-ARTIFACT PREFLIGHT. The freshness question is pure
        // file I/O and is answerable in seconds, so it is asked about EVERY config
        // HERE — before a single suite launches — rather than inside the parallel
        // per-config body, where a group-A project wrote its CTRF before group B was
        // examined and the refusal surfaced three minutes into a partial run that
        // read like progress.
        //
        // The preflight also REPAIRS what is provably repairable (a build-output copy
        // whose origin exists on disk), re-verifies the bytes afterwards, and records
        // every repair to a durable ledger that trips a circuit breaker on repetition.
        // See `StaleArtifactPreflight` for why exactly one stale case is healed.
        let runnableConfigs = configs |> List.filter (fun c -> not (skipProjectOf c))

        let preflightTargets =
            runnableConfigs
            |> List.choose (fun c -> deriveProjectBin c.Args repoRoot |> Option.map (fun t -> c.Project, t))

        // THE FLOOR. `List.choose` above DROPS every config whose build-output target
        // could not be derived, and a dropped project is indistinguishable downstream
        // from a project that was checked and found fresh — so a derivation that
        // regressed would silently reduce this gate to checking nothing while every run
        // stayed green. Say what was covered; see `coverageReport` for why it reports
        // instead of refusing.
        match
            StaleArtifactPreflight.coverageReport
                (runnableConfigs |> List.map (fun c -> c.Project))
                (preflightTargets |> List.map fst)
        with
        | Some gap ->
            Logging.warn "test-prune" gap
            logToCtx gap
        | None -> ()

        let preflight = StaleArtifactPreflight.run repoRoot DateTime.UtcNow preflightTargets

        for repaired in preflight.Healed do
            logToCtx $"repaired stale build output before running: {repaired}"

        // ALL-OR-NOTHING on a refusal, as two named alternatives. A run whose tree is
        // provably not built cannot reach a green verdict, so launching the projects that
        // happen to be fresh buys minutes of partial execution for signal the verdict
        // cannot use — which is the "reads like progress" half of the defect.
        //
        // ADR-016 kept this and changed the layer below it: the refusal
        // is still run-wide, but the preflight now repairs every copy its breaker did not
        // name, so a refused run leaves a better tree than it found and the refusal set
        // shrinks run over run. `preflight.Healed` above can therefore be non-empty on
        // this path — repairs happened, the launch did not.
        //
        // Both arms are bound as functions rather than inlined into the `if`, so the
        // 390-line group loop keeps the indentation it has always had. A run-level
        // guard should cost one line here, not re-flow every line it guards.

        /// Nothing spawns. Every configured project comes back deferred, naming its own
        /// reason where the preflight found one and the run-wide cause where it did not.
        let refuseWholeRun () =
            async {
                let refused =
                    preflight.Refusals |> List.map (fun r -> r.Project, r.Reason) |> Map.ofList

                // Every affected project, named in full — the headline may be
                // shortened by a fixed-width surface, but this list never is.
                let names =
                    preflight.Refusals |> List.map (fun r -> r.Project) |> String.concat ", "

                for refusal in preflight.Refusals do
                    Logging.warn "test-prune" $"%s{refusal.Project}: %s{refusal.Reason}"
                    logToCtx $"{refusal.Project}: waiting on build ({refusal.Reason})"

                let results =
                    configs
                    |> List.map (fun config ->
                        if skipProjectOf config then
                            skippedResultOf config
                        else
                            match refused.TryFind config.Project with
                            | Some reason -> config.Project, TestsDeferred reason
                            | None ->
                                config.Project,
                                TestsDeferred
                                    // The remedy points at the projects
                                    // that carry one rather than restating a generic
                                    // build command. Each named project's own deferral
                                    // holds the remedy for ITS cause, and those causes
                                    // need different actions — a raw `dotnet build`
                                    // fixes neither a tripped repair breaker nor a
                                    // byte-differing copy, and some repositories refuse
                                    // that command outright.
                                    $"not run — the whole run was refused before any suite launched because \
                                      %d{preflight.Refusals.Length} project(s) have stale build output: \
                                      %s{names}. Remedy: read those projects' own deferrals — each names its \
                                      cause and what to do about it — then re-run.")

                foldAndEmit results
                return [| results |]
            }

        /// The normal path: every target certified fresh, so the groups launch in
        /// parallel and fold their results into the shared accumulator as they finish.
        let runAllGroups () =
            groups
            |> List.map (fun (_, groupConfigs) ->
                async {
                    let mutable results = []

                    for config in groupConfigs do
                        // Collect extra args (filter + coverage) to append
                        let extraArgs = ResizeArray<string>()

                        // FRESHNESS IS ALREADY SETTLED. The preflight above
                        // asked `ArtifactFreshness.stale` about every config in this run before
                        // the first spawn and refused the whole run if any answer was stale, so
                        // reaching this line means the bits match the sources. There is
                        // deliberately NO second freshness check here: a per-config gate inside
                        // the parallel body is precisely what let one group write its CTRF
                        // before another group's staleness had even been looked at.
                        //
                        // Template-based class filter (from impact analysis). When the map is
                        // non-empty but has no classes for this project, skip the project
                        // entirely (impact analysis found no relevant tests).
                        match skipProjectOf config with
                        | true ->
                            Logging.info "test-prune" $"Skipping %s{config.Project} — no affected classes"
                            results <- skippedResultOf config :: results
                        | false ->
                            let filterArgs = buildFilterArgs config affectedClassesByProject

                            match filterArgs with
                            | Some f -> extraArgs.Add(f)
                            | None -> ()

                            // Raw passthrough filter (from run-tests command)
                            match rawFilter with
                            | Some f -> extraArgs.Add(f)
                            | None -> ()

                            let wasFiltered = Option.isSome filterArgs || Option.isSome rawFilter

                            // Resolve per-project coverage paths (if coverage is configured for
                            // this project). wasFiltered determines which file coverlet writes
                            // to; the post-test step reads those files back to produce cobertura.
                            let projectCoveragePaths = Map.tryFind config.Project coveragePathsByProject

                            let projectCoverageLaunch =
                                projectCoveragePaths
                                |> Option.map (fun paths ->
                                    let rawPath = if wasFiltered then paths.Partial else paths.Baseline

                                    let launch =
                                        prepareCoverageArtifact
                                            config.Project
                                            paths.IncludeInRatchet
                                            wasFiltered
                                            rawPath

                                    extraArgs.Add(buildCoverageArgs paths wasFiltered)
                                    launch)

                            // xUnit's runner supports a major-specific CTRF switch, which fshw
                            // reads back as the AUTHORITATIVE pass/fail verdict (and for
                            // flakiness history). An UNSUPPORTED `--report-*` flag is
                            // FATAL (the runner exits "invalid command line" and runs
                            // zero tests), so injection is scoped: `Disabled` never
                            // injects, `Ctrf` always does, `AutoDetect` injects iff the
                            // runner's restored package graph resolves to a supported xUnit
                            // major. Forced `Ctrf` keeps the xUnit 3 switches as its fallback
                            // for explicitly-configured custom runners, but uses the resolved
                            // xUnit 4 switches whenever the project graph identifies v4.
                            let ctrfRunnerFamily =
                                match config.ReportVerificationFormat with
                                | Disabled -> None
                                | Ctrf -> detectCtrfRunnerFamily config.Args repoRoot |> Option.orElse (Some Xunit3)
                                | AutoDetect -> detectCtrfRunnerFamily config.Args repoRoot

                            // ONE DIRECTORY PER RUN. A run's reports live in
                            // `.fshw/test-runs/<runId>/` and nothing else does, so membership is
                            // a fact about where a file IS, never an inference from its mtime.
                            //
                            // The run-dir is created whether or not any project reports, so an
                            // executed run that produced nothing leaves an EMPTY DIRECTORY —
                            // distinguishable from a run that never happened.
                            let ctrfPath =
                                match ctrfRunnerFamily with
                                | Some family ->
                                    Directory.CreateDirectory(runDir) |> ignore
                                    // The dir already names the run, so the file need only name
                                    // the project. No guid to guess at, nothing to parse.
                                    let ctrfName = $"{config.Project}{Ctrf.ReportSuffix}"

                                    extraArgs.Add(ctrfArguments family ctrfName runDir)

                                    Some(Path.Combine(runDir, ctrfName))
                                | None -> None

                            let finalArgs =
                                if extraArgs.Count > 0 then
                                    let extra = String.concat " " extraArgs
                                    $"%s{config.Args} %s{extra}"
                                else
                                    config.Args

                            // TRACES. Decided here, after the preflight certified the build
                            // output fresh and before the spawn: a traced project launches its
                            // woven copy; a refused one launches exactly as configured, and the
                            // refusal is stored with its reason after the run.
                            let decided =
                                match traces with
                                | None -> Untraced None
                                | Some host ->
                                    // Weaving and JIT verification can take minutes: held as a
                                    // subtask, so a wait line names it rather than the run.
                                    use _ =
                                        host.HoldSubtask
                                            $"{config.Project}:traces"
                                            $"preparing traces for {config.Project}"

                                    host.TraceWiring.Decide
                                        host.TraceRuntime
                                        { Project = config.Project
                                          Command = config.Command
                                          Args = config.Args
                                          Environment = config.Environment
                                          Target = deriveProjectBin config.Args repoRoot
                                          CtrfPath = ctrfPath }
                                        runDir
                                        (List.ofSeq extraArgs)

                            match decided with
                            | Untraced(Some reason) ->
                                Logging.warn
                                    "test-prune"
                                    $"%s{config.Project}: not tracing this run, launching as configured — %s{reason}"
                            | Traced(spec, _) ->
                                Logging.info "test-prune" $"%s{config.Project}: tracing, launching %s{spec.Command}"
                            | Untraced None -> ()

                            // A ref: the untraced retry below replaces it from inside `runOnce`.
                            let traceDecision = ref decided

                            Logging.info "test-prune" $"Running: %s{config.Command} %s{finalArgs}"

                            let timeoutSpan =
                                match config.TimeoutSec with
                                | Some s -> TimeSpan.FromSeconds(float s)
                                | None -> System.Threading.Timeout.InfiniteTimeSpan

                            let projectSw = Stopwatch.StartNew()

                            // THE RUN LOG. Opened for EVERY project on every
                            // run, before the spawn — which project will need explaining is not
                            // knowable in advance and the artifact costs a file handle.
                            //
                            // STREAMED, not buffered (see `RunLog`): the failure that needs it
                            // most is the suite SIGKILLed at its timeout, which reaches no
                            // writer at all and whose in-memory capture the kill truncates.
                            let runLog = RunLog.openFor runDir config.Project

                            match runLog.Ref with
                            | RunLog.Ref.Written path ->
                                Logging.info "test-prune" $"%s{config.Project}: streaming run output to %s{path}"
                            | RunLog.Ref.Unavailable reason ->
                                Logging.warn
                                    "test-prune"
                                    $"%s{config.Project}: NOT saving a run log — %s{reason}. The run proceeds; only \
                                  the console tail will be available if it fails."

                            let outputSink =
                                match runLog.Ref with
                                | RunLog.Ref.Written _ -> Some runLog.Write
                                | RunLog.Ref.Unavailable _ -> None

                            // A test runner STREAMS (discovery banner, progress, per-test
                            // lines), so its first byte is a sound liveness proof and the
                            // launch deadline can bound the spawn even when the config sets
                            // no TimeoutSec at all.
                            let runOnce =
                                async {
                                    let command, args, environment =
                                        TraceRun.launchOf
                                            traceDecision.Value
                                            (config.Command, finalArgs, config.Environment)

                                    return
                                        runProcessTo
                                            outputSink
                                            command
                                            args
                                            repoRoot
                                            environment
                                            (ProcessBounds.streaming timeoutSpan launchDeadline)
                                }

                            // A traced launch that verified nothing is repeated untraced
                            // (see `TraceRun.untracedRetry`), so tracing cannot turn a run red.
                            let runOnceTracedOrNot =
                                async {
                                    let! first = runOnce

                                    match
                                        TraceRun.untracedRetry
                                            traceDecision.Value
                                            (isSucceeded first)
                                            (ctrfPath |> Option.exists File.Exists)
                                    with
                                    | Some reason ->
                                        Logging.warn "test-prune" $"%s{config.Project}: %s{reason}"

                                        RunLog.note
                                            runLog
                                            "the traced launch failed without writing a test report; relaunching \
                                         untraced. Everything above is the TRACED attempt, everything below the \
                                         untraced one."

                                        traceDecision.Value <- Untraced(Some reason)
                                        return! runOnce
                                    | None -> return first
                                }

                            // See `tryApphostPresent`; `looksLikeApphostMissing` is the
                            // fallback for a command with no derivable project.
                            let detectApphostMissing (outcome: ProcessOutcome) : bool =
                                // A clean exit means the apphost ran — never a
                                // launch-ordering problem, regardless of artifacts.
                                if isSucceeded outcome then
                                    false
                                else
                                    match tryApphostPresent config.Args repoRoot with
                                    | Some present -> not present
                                    | None ->
                                        // Not derivable — fall back to the text sniff.
                                        match outcome with
                                        | ProcessOutcome.Failed(_, out) ->
                                            looksLikeApphostMissing (ProcessOutput.text out)
                                        | _ -> false

                            // Cold-start apphost-missing retry. The BuildCompleted→TestPrune
                            // ordering already gates the launch on a successful build, but a
                            // narrow race can still fire `--no-build` before the apphost
                            // lands. Retry ONCE after a short wait; a still-missing apphost
                            // is DEFERRED ("waiting on build"), never FAILED.
                            let runTestWithRetry =
                                async {
                                    let! first = runOnceTracedOrNot

                                    if detectApphostMissing first then
                                        Logging.warn
                                            "test-prune"
                                            $"%s{config.Project}: apphost missing at launch (build not settled yet); retrying once after a short wait"

                                        // Both attempts stream into the SAME log, so mark the
                                        // seam — otherwise two runs read as one confusing run.
                                        RunLog.note
                                            runLog
                                            "apphost missing at launch; relaunching once. Everything above is the \
                                         FIRST attempt, everything below the second."

                                        do! Async.Sleep 750
                                        let! second = runOnce
                                        return second
                                    else
                                        return first
                                }

                            // `finally`, not a close after the bind: a launch stall RE-RAISES
                            // out of this block, and a run log whose handle leaked on the one
                            // path where the child never came back is a log of nothing.
                            let! processResult =
                                async {
                                    try
                                        try
                                            return!
                                                match ctx with
                                                | Some c ->
                                                    PluginCtxHelpers.withSubtask
                                                        c
                                                        config.Project
                                                        $"testing {config.Project}"
                                                        runTestWithRetry
                                                | None -> runTestWithRetry
                                        with LaunchStalledException reason ->
                                            // The watchdog killed a child that never showed a
                                            // sign of life within the launch deadline. Re-raise
                                            // NAMING the config and elapsed so the run's Aborted
                                            // lifecycle (built by the caller's `with ex ->`)
                                            // carries a legible diagnostic. A launch stall means
                                            // this project NEVER RAN, so the whole run must
                                            // abort → PluginStatus.Failed → `check` exits
                                            // non-green rather than wedging at Running. A child
                                            // that EXITS is not a stall — the poll observes the
                                            // exit and classifies it normally.
                                            return
                                                raise (
                                                    LaunchStalledException
                                                        $"%s{config.Project}: %s{reason} (after %.0f{projectSw.Elapsed.TotalSeconds}s)"
                                                )
                                    finally
                                        // The pumps are done by the time `runProcessTo`
                                        // returns (it drains them), so nothing is still
                                        // writing when this closes.
                                        runLog.Close()
                                }

                            projectSw.Stop()
                            let projectElapsed = projectSw.Elapsed

                            let apphostMissing = detectApphostMissing processResult

                            // A filtered run that matched zero tests in this project is
                            // not a failure (see `isZeroTestsUnderFilter`) — treat it
                            // like an impact-skip.
                            let zeroTestsUnderFilter = isZeroTestsUnderFilter wasFiltered processResult

                            let output = outputOf processResult

                            // Read the structured report ONCE (when one was requested
                            // from a capable runner). It is BOTH the authoritative
                            // pass/fail signal (summary counts) AND the flakiness
                            // source (per-test records). Read BEFORE the verdict so
                            // the REPORT — not the exit code — decides green/red.
                            let reportJson =
                                match ctrfPath with
                                | Some p ->
                                    try
                                        Some(File.ReadAllText p)
                                    with
                                    | :? IOException
                                    | :? UnauthorizedAccessException -> None
                                | None -> None

                            let reportEvidence =
                                match ctrfPath, reportJson with
                                | None, _ -> NoReportRequested
                                | Some _, Some json -> ReportRequested(Ctrf.tryVerdictReport json)
                                | Some p, None -> ReportRequested(Error $"no readable report at %s{p}")

                            let result =
                                if apphostMissing then
                                    // Tests NEVER RAN — the apphost wasn't produced.
                                    // A dedicated `TestsDeferred`, NOT a pass
                                    // (`verdict` = `NothingVerified` → never a silent
                                    // false-green) and NOT a real failure: surfaced as an honest
                                    // "waiting on build" diagnostic. Carries no
                                    // elapsed/wasFiltered, so it never lowers a
                                    // coverage baseline.
                                    TestsDeferred "apphost not produced; tests did not run"
                                elif zeroTestsUnderFilter then
                                    // Not a failure — per project, a filter selecting
                                    // nothing is not that project's fault, so it is never
                                    // reported as one. Its own case (rather than a passing
                                    // result wearing a marker prefix) so that every fold
                                    // must decide it; see `TestResult.verdict` and
                                    // `verificationOf`.
                                    TestsNoMatch(output, projectElapsed)
                                else
                                    classifyTestOutcome reportEvidence wasFiltered projectElapsed processResult

                            // Log driven off the AUTHORITATIVE verdict (not the raw
                            // exit code) so the console line can never disagree with
                            // the recorded result.
                            match result with
                            | TestsDeferred _ ->
                                logToCtx $"{config.Project}: waiting on build (apphost not yet produced)"

                                Logging.warn
                                    "test-prune"
                                    $"%s{config.Project}: apphost still missing after retry — surfacing as 'waiting on build', not FAILED (a build-ordering issue, never a test failure)"
                            | TestsNoMatch _ ->
                                logToCtx $"{config.Project}: no tests matched the filter — skipped"

                                Logging.info
                                    "test-prune"
                                    $"%s{config.Project}: no tests matched the active filter — skipped, not FAILED (a filtered run that selects nothing here is not a test failure)"
                            | TestsPassed _ ->
                                logToCtx $"{config.Project}: passed"
                                Logging.info "test-prune" $"%s{config.Project}: PASSED"
                            | TestsErrored reason ->
                                logToCtx $"{config.Project}: ABORTED (nothing verified) — {reason}"

                                Logging.error
                                    "test-prune"
                                    $"%s{config.Project}: ABORTED — %s{reason} Nothing was verified; this is NOT a test failure and NOT a pass — re-run (e.g. `dotnet fshw test-rerun`)."
                            | TestsFailed _
                            | TestsTimedOut _ ->
                                logToCtx $"{config.Project}: failed"
                                Logging.error "test-prune" $"%s{config.Project}: FAILED"

                            // Report the outcome in full. The two reports are DIFFERENT
                            // documents on purpose: a failure report
                            // counts the runner's `failed ...` lines and calls them
                            // failures, which is true of a run that finished and false —
                            // expensively, alarmingly false — of one that was killed
                            // holding a half-written transcript.
                            match result with
                            | TestsFailed _
                            | TestsTimedOut _ ->
                                for line in formatFailureReport config.Project runLog.Ref output do
                                    Logging.error "test-prune" line
                            | TestsErrored reason ->
                                for line in formatAbortReport config.Project runLog.Ref reason output do
                                    Logging.error "test-prune" line
                            | TestsPassed _
                            | TestsDeferred _
                            | TestsNoMatch _ -> ()

                            // Collect this project's raw runner cobertura for SERIAL
                            // ingest after Async.Parallel (a parallel DB write +
                            // shared-file write would race). A run that never executed
                            // (apphost missing) contributes NO input, so a partial file
                            // cannot lower coverage.
                            match
                                projectCoverageLaunch
                                |> Option.map (coverageReceiptFromReceipt (TestResult.verifiedGreen result))
                            with
                            | Some(CoverageReceiptAccepted input) ->
                                lock coverageRawPathsLock (fun () -> coverageInputs <- input :: coverageInputs)
                            | Some(CoverageReceiptFailed failure) ->
                                lock coverageRawPathsLock (fun () ->
                                    coverageReceiptFailures <- failure :: coverageReceiptFailures)
                            | Some CoverageReceiptAbsent
                            | None -> ()

                            // Per-test flakiness tracking: reuse the report content
                            // already read for the verdict; COLLECT this project's
                            // per-test records for the single post-parallel write (see
                            // `flakinessRecords`). Best-effort — exceptions never fail
                            // the run.
                            //
                            // The report is RETAINED — the verdict file POINTS at these
                            // reports rather than deleting them once their records are
                            // folded into the flakiness history. `Ctrf.tidyRunsDir`
                            // (post-run) keeps the newest few per project, so retention
                            // stays bounded.
                            match ctrfPath, reportJson with
                            | Some _, Some json ->
                                try
                                    let records = Flakiness.parseCtrfTests json

                                    if not records.IsEmpty then
                                        lock flakinessLock (fun () -> flakinessRecords <- flakinessRecords @ records)
                                with
                                | :? IOException
                                | :? UnauthorizedAccessException
                                | :? JsonException as ex ->
                                    Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"
                            | Some p, None ->
                                // Report requested but unreadable (missing — the host
                                // aborted before flushing — or locked). Nothing to retain;
                                // its ABSENCE already drove the Errored verdict.
                                try
                                    File.Delete p
                                with
                                | :? IOException
                                | :? UnauthorizedAccessException -> ()
                            | None, _ -> ()

                            if Option.isSome traces then
                                let traced: TracedProjectRun =
                                    { Project = config.Project
                                      Decision = traceDecision.Value
                                      Filtered = wasFiltered
                                      CtrfPath = ctrfPath }

                                lock traceRunsLock (fun () -> traceRuns <- traced :: traceRuns)

                            results <- (config.Project, result) :: results

                    // Atomically fold this group's results into the shared
                    // accumulator and emit a cumulative snapshot. Groups that
                    // complete later will extend (never contradict) this one.
                    foldAndEmit results
                    return results
                })
            |> Async.Parallel

        let! groupResults =
            if List.isEmpty preflight.Refusals then
                runAllGroups ()
            else
                refuseWholeRun ()

        // groupResults is the per-group return values; we ignore it because
        // `cumulative` (populated under the lock inside foldAndEmit) is the
        // canonical run-wide aggregate.
        groupResults |> ignore

        // Coverage: now that ALL projects have finished, serially ingest each
        // project's raw runner cobertura into the TestPrune DB (max-merged,
        // symbol-relative) and emit the FULL DB ONCE to the single shared
        // cobertura file. Done here, outside Async.Parallel, so there is no
        // DB-write contention and no file-write race on the shared output.
        let collectedInputs, receiptFailures =
            lock coverageRawPathsLock (fun () -> List.rev coverageInputs, List.rev coverageReceiptFailures)

        let receiptFailures =
            receiptFailures
            |> List.map (fun failure ->
                try
                    invalidateCoverageReceiptFailure db failure
                    failure
                with ex ->
                    { failure with
                        Reason =
                            $"%s{failure.Reason}; prior coverage invalidation failed (%s{ex.GetType().Name}: %s{ex.Message})" })

        let coverageOutcome =
            ingestAndEmitCoverageForProjects
                db
                repoRoot
                (runId.ToString("N"))
                enabledRatchetProjects
                configuredCoverageOutput
                collectedInputs

        let coverageFailures = receiptFailures @ coverageIngestFailures coverageOutcome

        if not coverageFailures.IsEmpty then
            for failure in coverageFailures do
                coverageIngestFailed failure

            lock accumulatorLock (fun () -> cumulative <- applyCoverageIngestFailures coverageFailures cumulative)

        // Flakiness: ONE parse + ONE rewrite of the history file for the whole run,
        // with every project's records — not one per project, and not racing itself
        // from inside Async.Parallel. Best-effort: the history is a diagnostic, so a
        // failure to record it must never fail the run that produced it.
        let collectedFlakiness = lock flakinessLock (fun () -> flakinessRecords)

        if not collectedFlakiness.IsEmpty then
            try
                Flakiness.appendRecords (flakinessHistoryPath repoRoot) 20 collectedFlakiness
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? JsonException as ex -> Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"

        // Traces: after every project finished, before `tidyRunsDir` can rotate the run
        // directory holding the dumps. `ingestAll` never throws, and nothing it does
        // reaches `cumulative`: a trace failure is a log line and a stored row, never a
        // changed result.
        match traces with
        | Some host ->
            let runs = lock traceRunsLock (fun () -> List.rev traceRuns)

            TraceRun.ingestAll
                host.TraceRuntime
                (TestPrune.Ports.toSymbolStore db)
                (runId.ToString("N"))
                launchTreeHash
                (fun () -> ReceiptInputTree.read repoRoot)
                runs
                host.TraceLog
        | None -> ()

        // Bound what `.fshw/test-runs/` retains, and purge the DEAD `.log` format.
        // Runs AFTER this run's reports were written, so the
        // evidence the verdict is about to point at is always among the survivors.
        Ctrf.tidyRunsDir repoRoot Ctrf.RetainedRuns

        sw.Stop()

        let finalResults = lock accumulatorLock (fun () -> cumulative)

        let testResults =
            { Results = finalResults
              Elapsed = sw.Elapsed }

        ctx |> Option.iter (fun c -> c.EndSubtask PrimarySubtaskKey)

        // `TestRunCompleted` is emitted by the CALLER (the synchronous Custom handler) so
        // it lands in EmittedEvents for cache replay; returned in the tuple instead. The
        // matching start was emitted before any host could mutate its input artifacts.
        // `Outcome = Normal` means the run completed naturally — per-project pass/fail
        // lives in Results, and Aborted is reserved for cancellation/timeout/crash.
        let completed: TestRunCompleted =
            { RunId = runId
              TotalElapsed = sw.Elapsed
              Outcome = Normal
              Results = finalResults
              Verification =
                verificationWithin
                    (configuredCoverageProjects |> List.map (fun c -> c.Project) |> Set.ofList)
                    finalResults }

        match afterRun with
        | Some hook -> hook testResults
        | None -> ()

        Logging.info
            "test-prune"
            $"Tests complete: %d{testResults.Results.Count} projects, %.1f{testResults.Elapsed.TotalSeconds}s"

        return testResults, started, completed
    }

/// FCS cache-poisoning gate. A `FileChecked` whose FCS result reports any
/// Error-severity diagnostic is untrustworthy: cold-start FCS sometimes returns
/// "expected type X but here has type X" for files that compile cleanly once warm, and
/// flushing those poisoned symbols overwrites the prior good DB snapshot. Gated by
/// SEVERITY, not message text, so the cold-start race and the user-broke-their-code case
/// are handled identically — both hold the prior DB row. `ParseOnly` (check aborted)
/// counts as "no observable errors".
///
/// `suppressedCodes` is merged with per-file `#nowarn` directives via
/// `FcsDiagnosticFilter.allSuppressedCodes` so the gate applies the same filter as the
/// user-visible error stream in `Daemon.reportFcsDiagnostics`. Without that symmetry the
/// gate trips on codes the user has already silenced (e.g. FS1182 promoted to Error by
/// `<TreatWarningsAsErrors>` but suppressed via `#nowarn "1182"`), killing cache-replay
/// across daemon restarts on every cold scan.
let internal hasFcsErrors (suppressedCodes: Set<int>) (source: string) (state: FileCheckState) : bool =
    match state with
    | FullCheck cr ->
        let allSuppressed = allSuppressedCodes suppressedCodes source

        cr.Diagnostics
        |> Array.exists (fun d ->
            d.Severity = FSharpDiagnosticSeverity.Error
            && not (allSuppressed.Contains d.ErrorNumber))
    | ParseOnly -> false

/// Count of Error-severity diagnostics not in the effective suppression set —
/// used only for the skip-log message so operators have a number to
/// correlate against the FCS-error stream. Must apply the same filter as
/// `hasFcsErrors` so the count matches what the gate decided on.
let internal fcsErrorCount (suppressedCodes: Set<int>) (source: string) (state: FileCheckState) : int =
    match state with
    | FullCheck cr ->
        let allSuppressed = allSuppressedCodes suppressedCodes source

        cr.Diagnostics
        |> Array.filter (fun d ->
            d.Severity = FSharpDiagnosticSeverity.Error
            && not (allSuppressed.Contains d.ErrorNumber))
        |> Array.length
    | ParseOnly -> 0

/// Flush accumulated per-file analysis results to the DB in a single RebuildProjects
/// call. Pure function: takes state, returns updated state.
///
/// The results go to `RebuildProjects` ONE PER FILE, never merged per project.
/// TestPrune.Core attributes each edge, test method and attribute to the file whose
/// `AnalysisResult` carried it, so re-indexing a file replaces exactly that file's facts.
/// A merged result carrying a signature (`.fsi`) and its implementation would credit the
/// signature's facts to whichever file declared the shared name last, and a later flush
/// of that file alone would delete them.
let private flushPendingAnalysis (db: Database) (state: TestPruneState) =
    let allResults = ResizeArray<AnalysisResult>()

    let mutable newPending = state.PendingAnalysis

    for projectName in state.PendingAnalysis |> Map.toList |> List.map fst do
        match Map.tryFind projectName newPending with
        | Some items ->
            newPending <- Map.remove projectName newPending
            Logging.info "test-prune" $"Flushing %d{items.Length} files for %s{projectName} to DB"
            allResults.AddRange(items)
        | None -> ()

    if allResults.Count > 0 then
        db.RebuildProjects(Seq.toList allResults)

    // Update in-memory snapshot so subsequent FileChecked reads see the
    // new symbols instead of hitting the DB mid-rebuild.
    let mutable newSnapshot = state.SymbolSnapshot

    for result in allResults do
        for (file, symbols) in result.Symbols |> List.groupBy (fun s -> s.SourceFile) do
            newSnapshot <- Map.add file symbols newSnapshot

    { state with
        PendingAnalysis = newPending
        SymbolSnapshot = newSnapshot }

/// Detect schema-drift errors (stale cache DB lacking a column the current
/// `TestPrune.Core` requires). These surface as SQLite "no such column" /
/// "no column named" messages. Deliberately pure / internal so the caller
/// can unit-test both branches without needing a corrupt DB on disk.
let internal looksLikeSchemaDrift (ex: exn) =
    let msg = ex.Message.ToLowerInvariant()
    msg.Contains("no such column") || msg.Contains("no column named")

/// If `ex` looks like schema drift, delete the cache DB at `dbPath` so the next run
/// rebuilds from scratch. The cache is derivative and safe to regenerate, and a user
/// should never have to know which file to delete.
let internal tryRepairSchemaDrift (dbPath: string) (ex: exn) =
    if looksLikeSchemaDrift ex && File.Exists dbPath then
        try
            // Delegate to TestPrune.Core — it owns the SQLite-sidecar
            // invariant. Deleting only the main file leaves stale `-wal` /
            // `-shm` sidecars that SQLite may try to "recover" against a
            // freshly created empty DB, producing a 0-byte main DB with no
            // tables — every subsequent INSERT then hits "no such column:
            // <name>".
            TestPrune.Database.deleteCacheFiles dbPath

            Logging.warn
                "test-prune"
                $"Deleted stale cache DB %s{dbPath} after schema-drift error: %s{ex.Message}. Next run will rebuild from scratch."
        with deleteEx ->
            Logging.error
                "test-prune"
                $"Could not delete stale cache DB %s{dbPath}: %s{deleteEx.Message}. Delete it manually and restart the daemon."

/// The line an extension refresh writes per extension that answered: its edge count and
/// how long the whole refresh (every extension, one call) took.
let internal extensionStoredLine (name: string) (edgeCount: int) (refreshMs: int64) : string =
    $"Extension '%s{name}' stored %d{edgeCount} edge(s) (extension refresh took %d{refreshMs}ms)"

/// Delete the FCS check cache (`.fshw/cache/*.json`) for `repoRoot`, returning the number
/// of entries removed. Called when the TestPrune symbol DB was recreated (a schema bump):
/// the persisted FCS cache would otherwise let unchanged files hit the cache and SKIP
/// re-checking, so their symbols never re-flush into the freshly-emptied DB — leaving the
/// symbol graph (and therefore coverage + impact analysis) permanently partial. Clearing it
/// forces the next scan to re-check, and thus re-index, every file. Pure path logic so it
/// is unit-testable without a daemon.
let internal clearFcsCheckCache (repoRoot: string) : int =
    let cacheDir = Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "cache")

    if Directory.Exists cacheDir then
        let files = Directory.GetFiles(cacheDir, "*.json")

        for f in files do
            File.Delete f

        files.Length
    else
        0

/// Build the TestPrune task-cache key for one event, from its three state inputs.
///
/// Every input is a THUNK, and that is load-bearing. `FileChecked` is the per-FILE,
/// highest-frequency probe — one event per file on every scan — and it uses NONE of the
/// three. By value, the `dependsOn` hash (a full-repo `SafeWalk` plus a SHA256 of every
/// matched file) is computed once per checked file for a value that arm discards.
/// "cacheKey runs once per event, not per file" is true of
/// BuildCompleted and false of FileChecked.
///
/// Lifted out of the `create` closure so the property is STRUCTURAL: an arm cannot pay
/// for an input it does not name, and a test can prove it by counting calls.
///
/// `pendingQueueHash`/`dependsOnHash` return `None` for "nothing to contribute",
/// which keeps the corresponding merkle entry OMITTED — the empty-queue,
/// no-dependsOn key stays byte-identical to the pre-feature key, so existing
/// on-disk caches keep hitting.
let internal cacheKeyFor
    (changedSymbolsHash: unit -> string)
    (pendingQueueHash: unit -> string option)
    (dependsOnHash: unit -> string option)
    // The content merkle of the repo's PROJECT FILES — the files
    // that declare what is compiled (`projectStructureHash`). Not optional and not
    // omittable: every repo has a structure, and an omitted entry is what let a tree
    // that had just gained a `<Compile Include=…>` compute the key of the tree without
    // it and replay a green that never ran the new tests.
    //
    // Adding this entry ORPHANS every `outcomeKey` entry written before it, which is
    // exactly right — those entries assert a verdict over a structure they never
    // recorded — so no `plugin-version` bump is needed on top.
    (projectStructureHash: unit -> string)
    // `Some "full"` while the caller has asked for the whole suite;
    // `None` for the impact-filtered inner loop. Without it, `confirm` on an unchanged
    // tree HITS the entry written by an earlier impact-filtered run and replays a
    // filtered green as a merge verdict, with no test process ever starting.
    //
    // `None` rather than "impact" for the inner loop keeps the ordinary key
    // byte-identical to the pre-feature one, so existing on-disk caches keep hitting.
    (fullSuiteScopeHash: unit -> string option)
    // True while a failure no covering run has passed is outstanding.
    // While it is, this plugin does not participate in the task cache AT ALL:
    //   * no REPLAY — a cached green served on a BuildCompleted would skip the handler,
    //     skip the run, and hand back exactly the laundered verdict this flag
    //     prevents (the same reasoning that makes a non-empty pending queue refuse);
    //   * no WRITE — the terminal status of such a run carries a red the run itself
    //     did not produce, and pinning that to a content merkle would let it replay on
    //     a tree that has since been fixed (in reverse).
    // Read at DISPATCH time, so on a `TestsFinished` it is the PRIOR outstanding set —
    // which is the sound one to gate on: `allPassed` with an empty prior set implies an
    // empty post-run set, so a genuinely green run is still cacheable, and a run that
    // CLEARS a red merely forgoes one cache write.
    (hasOutstandingFailures: unit -> bool)
    // Has a run in THIS PROCESS produced test evidence — i.e. does this
    // session's `RunCoverage` cover any project at all?
    //
    // Serving a cached BuildCompleted skips the handler, so no run happens, no
    // `TestsFinished` lands, and `LastCoverage` stays empty. Every consumer reading the
    // plugin's STATE (`test-scope`, and through it the verdict file) then hears "NO TESTS
    // RAN" while the status line reports "1 passed (cached)" — one run, two surfaces,
    // opposite answers, and both `check` and `confirm` exit 3.
    //
    // The key cannot rescue this because it does not pin the TREE: on a cold scan
    // `BuildCompleted` is dispatched BEFORE the FCS pass, so `changed-symbols` is empty
    // whatever the tree contains, and two different clean-building trees compute the SAME
    // key. What makes the cache sound in a warm daemon is the symbol-diff pipeline that
    // runs after it and supersedes the entry; across a process boundary there is no such
    // run.
    //
    // So fail closed — no replay AND no write — as a non-empty pending queue and an
    // outstanding failure already do. This also restores `hasCachedResults`: a cold start
    // with no session baseline must run the full suite, and a cache hit skipped the
    // handler that rule lives in. The warm inner loop is untouched — once this session's
    // first run lands there IS coverage, and later BuildCompleteds replay as before.
    (sessionHasTestEvidence: unit -> bool)
    (event: PluginEvent<TestPruneMsg>)
    : ContentHash option =
    let optionalEntry (name: string) (value: string option) =
        match value with
        | Some v -> [ name, v ]
        | None -> []

    // Reuses the same merkle for BuildCompleted and Custom TestsFinished so the
    // cache writes on TestsFinished (synchronous handler — captures EmittedEvents)
    // and the next BuildCompleted hits via the matching key. TestsFinished only
    // fires after BuildSucceeded (BuildFailed short-circuits earlier), so
    // outcome="succeeded" is correct for the Custom path.
    //
    // The `-v2` salt orphans entries written, which cached FAILED
    // verdicts and could replay them on a now-green tree. Bump it again for any change
    // that makes an old entry unsound, rather than asking users to wipe the cache.
    let outcomeKey (buildOutcome: string) =
        FsHotWatch.TaskCache.merkleCacheKey (
            [ "plugin-version", "test-prune-merkle-v2"
              "event", "BuildCompleted"
              "changed-symbols", changedSymbolsHash ()
              // The one term that pins the SHAPE of the tree. The
              // changed-symbols term cannot: on a scan this event is dispatched before
              // the FCS pass, so it is empty whatever the tree holds.
              "project-structure", projectStructureHash ()
              "build-outcome", buildOutcome ]
            @ optionalEntry "pending-queue" (pendingQueueHash ())
            @ optionalEntry "depends-on" (dependsOnHash ())
            @ optionalEntry "full-suite-scope" (fullSuiteScopeHash ())
        )

    match event with
    | BuildCompleted BuildSucceeded ->
        // A cache HIT replays the cached terminal status and SKIPS the handler
        // (`PluginFramework.tryReplayCache`) — but this handler is the drain trigger for
        // the pending-verification queue. The key folds in a queue
        // hash, but that is read at DISPATCH time and on a scan the queue is mutated
        // afterwards by the FCS pass, so the key cannot be trusted to notice outstanding
        // work. `None` refuses the cache entirely — no replay AND no write — so the
        // handler always runs and always gets its chance to drain.
        //
        // The three refusals below are the same rule from three directions; see the
        // parameter docs. The empty-queue green fast-path is untouched.
        match pendingQueueHash (), hasOutstandingFailures (), sessionHasTestEvidence () with
        | Some _, _, _
        | _, true, _
        | _, _, false -> None
        | None, false, true -> Some(outcomeKey "succeeded")
    | Custom(TestsFinished(_, completed, _)) ->
        // A FAILED test outcome must never be served from cache as a current verdict.
        // Unlike BuildPlugin — whose result is a pure function of its content-merkle
        // inputs — a test outcome is NOT pinned by the changed-symbols merkle: the same
        // key recurs after the tree is fixed (or for a flaky test), so a cached `Failed`
        // replays as a stale red on a green tree. Observed: an 08:35 failure replayed at
        // 10:19 and 10:49 and through four deploy-preflights on a `failed: 0` tree.
        // `None` makes a non-passing run UNCACHEABLE, so `runAndCache` skips the write
        // and the next matching BuildCompleted re-runs.
        //
        // A green must ALSO leave the queue empty to be cacheable — a green with symbols
        // still queued is not a "safe to skip" verdict. And the outcome must be
        // non-Aborted: an aborted run has empty Results, which the all-passed fold treats
        // as trivially passing.
        // Written out PER CASE, at the site, because the JUSTIFICATION
        // differs per case:
        //   * `Verified`        — ran and green. The only positive evidence there is.
        //   * `Refuted`         — a real red. Never cacheable, for the reason above.
        //   * `NothingVerified` — a zero MATCH is admitted here ONLY because the
        //     `allZeroMatchRun` conjunct below refuses the run where EVERY project
        //     matched nothing, so what survives is a mixed run in which some project
        //     did verify something. A Deferred/Errored project is refused outright:
        //     a build-ordering race or a host crash must never be replayable as a
        //     green.
        //
        // The per-result VALUES, unlike the reasons, are not independent: this match is
        // the exact negation of the `nonGreen` match in the `TestsFinished` handler's
        // status ladder (a result admitted here is one that match excludes, and the
        // reverse). Nothing enforces that — the two are kept in agreement BY HAND, so a
        // case changed at one site must be changed at the other. A shared helper would
        // not make them provably agree either, because what surrounds them differs: here
        // the fold is ANDed with `Ran FullSuite`, `notAborted`, `not allZeroMatchRun`, an
        // empty pending queue and no outstanding failures, and it decides a cache key;
        // there it runs only once the aborted, zero-projects-with-queued-symbols and
        // all-zero-match arms have been ruled out, and it decides a status line.
        let noProjectRefutedOrUnrun =
            completed.Results
            |> Map.forall (fun _ r ->
                match TestResult.verdict r with
                | Verified -> true
                | Refuted -> false
                | NothingVerified -> TestResult.isNoMatch r)

        let notAborted =
            match completed.Outcome with
            | Aborted _ -> false
            | Normal -> true

        // A run that matched NO tests must not mint a cacheable green.
        // The fold above admits a zero-match project (see its comment), so on its own it
        // would write a replayable entry: a later BuildCompleted on the same tree would
        // hit a green produced by executing zero tests.
        //
        // Deliberately NOT extended to an empty result set — that is the "nothing to
        // verify" skip, decided separately. This covers only a run where projects ran and
        // matched nothing.
        let allZeroMatchRun = allZeroMatchOf completed.Results

        // Third condition: a run that passed everything IT ran while an
        // earlier, uncovered failure is outstanding is NOT green — its terminal status is
        // a Failed carrying the carried-over red.
        //
        // Deliberately NOT gated on `sessionHasTestEvidence`. This arm is the WRITE, read
        // at DISPATCH time, when the run this message carries has not yet been folded
        // into state and there is no evidence to see. Its key is never used for a LOOKUP:
        // the framework does not replay over a `Custom` message at all, since a Custom's
        // payload is not in its key.
        // this write feeds the key an ordinary whole-tree check reads.
        // The terminal receipt therefore has to establish the whole configured suite;
        // a green project-scoped/manual or impact-scoped run cannot mint broader proof.
        if
            completed.Verification = Ran FullSuite
            && noProjectRefutedOrUnrun
            && notAborted
            && not allZeroMatchRun
            && (pendingQueueHash ()).IsNone
            && not (hasOutstandingFailures ())
        then
            Some(outcomeKey "succeeded")
        else
            None
    | BuildCompleted(BuildFailed errs) ->
        // Shares `outcomeKey` with the BuildSucceeded arm so the salt and the
        // pending-queue/dependsOn entries can never split across the two.
        Some(outcomeKey ("failed:" + String.concat "|" (List.sort errs)))
    | FileChecked r ->
        // A per-file cache hit derives a terminal summary from the plugin's whole live
        // ledger and labels it `(cached)`. While a fresh test failure is outstanding,
        // that would falsely relabel the test run as replayed merely because a helper
        // file's earlier symbol analysis hit its cache. Refuse the per-file replay until
        // a covering test run clears the red; healthy trees keep the hot path.
        if hasOutstandingFailures () then
            None
        else
            // `fcs-signature` captures cross-file FCS state so upstream symbol changes
            // invalidate this file's cached symbol-diff.
            //
            // Note what this arm does NOT read: not the changed symbols, not the pending
            // queue, not the dependsOn globs. It is a pure function of THIS file — which is
            // why all three are thunks.
            let fcsSignature = FsHotWatch.CheckCache.fcsCheckSignature r.CheckResults

            Some(
                FsHotWatch.TaskCache.merkleCacheKey
                    [ "plugin-version", "test-prune-merkle-v2"
                      "event", "FileChecked"
                      "file", AbsFilePath.value r.File
                      "source", r.Source
                      "fcs-signature", fcsSignature ]
            )
    | _ -> None

/// The impact queries the plugin runs against its index, in one place, so a test can
/// count them per handler instance.
type internal ImpactQueries =
    {
        /// The tests `QueryAffectedTests` selects for a set of changed symbols.
        AffectedTests: string list -> TestMethodInfo list
        /// For each symbol, the test projects its single-seed query would select from.
        CoveringProjectsBySeed: string list -> Map<string, Set<string>>
        /// Every test project the index holds a test method for. Not a covering query:
        /// one scan, whatever is queued. `None` when the index cannot be read this way
        /// (an in-memory index is private to the connection that made it).
        IndexedTestProjects: unit -> Set<string> option
    }

module internal ImpactQueries =
    let private indexedTestProjects (db: Database) () : Set<string> option =
        try
            use conn = db.OpenConnection()
            use query = conn.CreateCommand()
            query.CommandText <- "SELECT DISTINCT test_project FROM test_methods;"
            use reader = query.ExecuteReader()

            [ while reader.Read() do
                  reader.GetString 0 ]
            |> Set.ofList
            |> Some
        with :? Microsoft.Data.Sqlite.SqliteException ->
            None

    let ofDatabase (db: Database) : ImpactQueries =
        { AffectedTests = db.QueryAffectedTests
          CoveringProjectsBySeed = db.QueryCoveringProjectsBySeed
          IndexedTestProjects = indexedTestProjects db }

/// Create a TestPrune plugin handler using the declarative plugin framework.
/// `buildExtensions` receives the plugin's own `Database` so extensions that
/// need a `RouteStore`/`SymbolStore` derive it from the same DB the plugin
/// queries against — structurally prevents the caller from wiring an extension
/// to a different DB than the plugin's. `queriesOf` builds the impact queries over that
/// same DB.
let internal createWithQueries
    (queriesOf: Database -> ImpactQueries)
    (launchDeadline: TimeSpan)
    // The declared exclusions: indexed test-project name -> written reason. Called once
    // per debt classification (flush, launch, completion), so a caller can re-resolve
    // them against the current project inventory. A throw is a refusal to classify, and
    // leaves the debt owed.
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    // `dependsOn`: repo-root-relative globs naming EXTERNAL inputs (DB
    // migrations, generated files, schemas) that the symbol-diff cache key can't
    // see. Their content hash salts the BuildCompleted cache key so editing one
    // forces a genuine re-run instead of replaying a stale verdict. `[]` → no
    // salt (key byte-identical to the pre-feature key).
    (dependsOn: string list)
    // `tests.traces`: `None` records nothing and launches every project as configured.
    (traces: TraceWiring option)
    =
    /// The traces of a run launched under `mode`, when `tests.traces` is configured, with
    /// the plugin's activity log and subtasks: a force run's `executeTests` has no `ctx`.
    let tracesFor (ctx: PluginCtx<TestPruneMsg>) (mode: TestMode) : TraceRunHost option =
        traces
        |> Option.map (fun wiring ->
            { TraceRuntime =
                { Settings = wiring.Policy
                  RepoRoot = repoRoot
                  ExcludedProjects = wiring.OptedOut
                  Mode = mode }
              TraceWiring = wiring
              TraceLog = ctx.Log
              HoldSubtask =
                fun key label ->
                    ctx.StartSubtask key label

                    { new IDisposable with
                        member _.Dispose() = ctx.EndSubtask key } })

    let db = Database.create dbPath
    let queries = queriesOf db
    let configuredTestProjects = testConfigs |> Option.defaultValue []

    /// Claim the "tests" key and the shared artifact lease for `work`. `owed` is the
    /// dependency fanout this launch consumes; an unavailable launch hands it back.
    ///
    /// Result-first: the completion fold sheds only the symbols the run launched with, so
    /// a file check or build dispatched during the run stays owed whether it folds before
    /// the result or after it. Folding after can only cost a run: a BootScan cohort that
    /// would have joined a full run in flight (`joinsFullRun`) finds none and queues its
    /// own. It never lets less evidence discharge anything. The run emits test lifecycle
    /// events this plugin does not subscribe to. Without this, a result stuck behind a
    /// storm of checks holds the "tests" key, and its green, for as long as those folds
    /// take.
    let runTestHostExclusive
        (ctx: PluginCtx<TestPruneMsg>)
        (owed: Set<string>)
        (reply: Tasks.TaskCompletionSource<string> option)
        work
        =
        let workFor =
            function
            | Ready -> PluginWork.resultFirst work
            | Invalid reason -> PluginWork.resultFirst (async { return ArtifactsUnavailable(reason, owed, reply) })

        // What this launch releases into the shared "build-artifacts" lease, which the
        // next claimant is handed. Only a verdict ABOUT the artifacts may change it.
        // A refusal that found them unusable carries that invalidity forward, so a
        // failed build stays sticky until one succeeds. A test host that would not
        // start says nothing about the build output: it releases the lease as valid,
        // so the next launch on an unchanged tree runs instead of being refused — with
        // the build named as the culprit — until something forces a real rebuild.
        let classify =
            function
            | ArtifactsUnavailable(reason, _, _) -> Invalid reason
            | TestHostUnavailable _ -> Ready
            | _ -> Ready

        let failureMessage (ex: exn) =
            TestHostUnavailable(ex.Message, owed, reply)

        match ctx.RunExclusiveShared "tests" "build-artifacts" workFor classify failureMessage with
        | SharedClaimed
        | SharedQueued -> Claimed
        | LocalSlotBusy -> SlotBusy

    // A recreated DB (schema bump) leaves the FCS check cache stale — see
    // `clearFcsCheckCache`.
    if db.WasRecreated then
        try
            let cleared = clearFcsCheckCache repoRoot

            Logging.warn
                "test-prune"
                $"TestPrune DB was recreated (schema change) — cleared %d{cleared} FCS check-cache entries so every file re-indexes on this scan."
        with ex ->
            Logging.error "test-prune" $"failed to clear the FCS check cache after a DB recreate: %s{ex.Message}"

    let extensions = buildExtensions |> Option.map (fun f -> f db)

    let tryRepairSchemaDrift ex = tryRepairSchemaDrift dbPath ex

    // Durable "needs-testing" queue (plugin-owned sidecar). The set of changed
    // symbols not yet proven test-equivalent to the last green run. Loaded once
    // at construction so a restart with a non-empty queue re-flags those
    // symbols. The live queue is `state.Debt.PendingQueue`; this file is its durable
    // copy, written by `PrepareCommit`. A symbol leaves ONLY when a covering test
    // run passed (or it has no covering test). See PendingVerification.fs.
    let loadedQueue = PendingVerification.load repoRoot

    /// `Some reason` when the sidecar EXISTS but could not be read
    /// (a torn write, corrupt JSON, a non-string entry). What was owed is then
    /// UNKNOWN — which is NOT the same fact as "nothing is owed", and must never
    /// again be spelled with the same value. A MISSING file is not this: it is a
    /// provable `Loaded empty` (fresh clone), and stays a fast no-op.
    let ledgerUnreadableReason =
        match loadedQueue with
        | PendingVerification.LoadedQueue.Loaded _ -> None
        | PendingVerification.LoadedQueue.Unreadable reason -> Some reason

    /// Present while a durable debt publication is between its first write and its
    /// publication. A restart that finds it cannot tell which sidecars describe the
    /// published state, so what is owed is unknown. Only `Finalize` removes it.
    let debtPublicationPath =
        Path.Combine(FsHwPaths.root repoRoot, "test-prune", "owner-publication-pending")

    let interruptedPublication = File.Exists debtPublicationPath

    if interruptedPublication then
        Logging.warn
            "test-prune"
            $"a verification-debt publication did not finish before the previous daemon stopped (%s{debtPublicationPath}). The sidecars may describe a state that was never published, so what is owed is UNKNOWN. Every test run is widened to every configured project in full until a full suite passes."

    /// The reds carried in from the previous session — quarantined into
    /// the first run exactly as in-session reds are. An UNREADABLE file is debt of unknown
    /// membership and takes the unreadable-ledger road: widen to the full suite, which
    /// re-executes every test and rebuilds the list from evidence.
    let loadedFailures, failuresUnreadable =
        match OutstandingFailure.load repoRoot with
        | OutstandingFailure.LoadedFailures.Loaded failures -> failures, false
        | OutstandingFailure.LoadedFailures.Unreadable reason ->
            Logging.warn
                "test-prune"
                $"the outstanding-failures ledger (%s{OutstandingFailure.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. It records every test still red from earlier runs, so which tests are owed is now UNKNOWN. Every test run is widened to every configured project in full until a full suite has re-executed them all."

            [], true

    /// The full-suite watermark this ledger's impact-filtered greens are
    /// relative to. `None` until a full-suite run has accounted for every configured
    /// project; an unreadable file is the same recovery, said out loud.
    let loadedBaseline =
        match FullSuiteBaseline.load repoRoot with
        | FullSuiteBaseline.LoadedBaseline.Loaded baseline -> baseline
        | FullSuiteBaseline.LoadedBaseline.Unreadable reason ->
            Logging.warn
                "test-prune"
                $"the full-suite baseline (%s{FullSuiteBaseline.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. The next test run is widened to the full suite to earn a new one."

            None

    let loadedRuntimeObligations, runtimeObligationsUnreadable =
        match loadRuntimeCoverageObligations repoRoot with
        | Ok obligations -> obligations, false
        | Error reason ->
            Logging.warn
                "test-prune"
                $"the runtime coverage obligation ledger could not be read: %s{reason}. Widening to the full suite until a verified full run recovers the unknown debt."

            Map.empty, true

    /// Arms the durable unknown-debt marker from the run that failed to ingest coverage,
    /// before the run returns, then tells the owner through a message. A marker that
    /// cannot be written is logged; the message still makes this session's debt unknown.
    let coverageIngestFailed (ctx: PluginCtx<TestPruneMsg>) (failure: CoverageIngestFailure) =
        try
            armRuntimeCoverageUnknownDebt
                (fun () -> ctx.Post(RuntimeCoverageFailed failure.Project))
                (runtimeCoverageRecoveryPath repoRoot)
                failure
        with ex ->
            ctx.Post(RuntimeCoverageFailed failure.Project)

            Logging.error
                "test-prune"
                $"failed to durably record unknown runtime coverage debt for %s{failure.Project}: %s{ex.Message}"

    /// How many CONSECUTIVE flush cycles each currently-queued symbol
    /// has seeded, so a symbol that is pinned AND selecting wide can be named out loud
    /// (see `isPoisonSuspect`).
    ///
    /// In-memory and per-session on purpose. Persisting it needs either a second sidecar
    /// or a shape change to `pending-verification.json` — and that file's reader treats
    /// ANY unparseable content as unknown debt that widens every run to the full suite,
    /// so a format change hands every existing checkout one gratuitous
    /// full-suite recovery. Forgetting on restart costs three cycles of re-arming.
    ///
    /// A diagnostic only: no selection, cache or debt decision reads it.
    let mutable pendingAgeRef: Map<string, int> = Map.empty

    // Say it out loud — a silent recovery here reads as a green.
    match ledgerUnreadableReason with
    | Some reason ->
        Logging.warn
            "test-prune"
            $"the pending-verification ledger (%s{PendingVerification.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. It records every symbol still awaiting a green test run, so what is owed is now UNKNOWN — which is NOT the same as nothing owed. Until a FULL-SUITE run passes, every test run is widened to every configured project in full and no cached verdict may be replayed."
    | None -> ()

    /// The test projects this daemon can actually RUN — i.e. the ones in
    /// `testConfigs`. Empty when the plugin is analysis-only.
    ///
    /// The symbol DB indexes test methods from EVERY test project it analyzed, which is
    /// not the same set. A symbol covered by a project outside this set is NOT covered by
    /// a test we can run, and it is not thereby verified either: see `debtScope` for the
    /// only way such a project stops owing.
    let runnableProjects: Set<string> =
        match testConfigs with
        | Some configs -> configs |> List.map (fun c -> c.Project) |> Set.ofList
        | None -> Set.empty

    /// Why the full-suite baseline cannot vouch for what a filtered run
    /// over the configured projects would skip — `None` when it can. Analysis-only
    /// daemons make no test claim and so owe no baseline.
    ///
    /// Unknown debt is folded in: the baseline composes with the queue (the queue names
    /// what changed since the baseline), so a baseline beside a ledger that cannot be
    /// read vouches for nothing until a full suite re-earns both.
    let baselineInvalidReason (debt: VerificationDebt) : string option =
        if Set.isEmpty runnableProjects then
            None
        elif debt.RecoveryOutstanding then
            Some
                "the verification ledger could not be read, so the full-suite baseline cannot say what changed since it was earned"
        else
            match debt.Baseline with
            | None -> Some FullSuiteBaseline.absentReason
            | Some baseline -> FullSuiteBaseline.staleness runnableProjects baseline

    /// The one question every skip in this plugin is really asking: is the
    /// needs-testing queue PROVABLY empty? Unknown debt is never `true` here — an empty
    /// queue we could not read is not an empty queue.
    ///
    /// An absent or stale full-suite baseline is owed work too — the
    /// tests a filtered run skips have nothing to be equivalent TO until one exists —
    /// so it is folded in here rather than checked beside this at each skip site.
    let nothingOwed (debt: VerificationDebt) =
        Set.isEmpty debt.PendingQueue
        && Map.isEmpty debt.RuntimeObligations
        && not debt.RecoveryOutstanding
        && Option.isNone (baselineInvalidReason debt)

    /// What a drain is FOR, in words. Unknown debt cannot be counted, so it is named.
    let owedDescription (debt: VerificationDebt) =
        let queued = Set.count debt.PendingQueue

        if debt.RecoveryOutstanding then
            $"an UNREADABLE pending-verification ledger (what is owed is UNKNOWN, so only a full suite can prove it) + %d{queued} newly-queued symbol(s)"
        else
            // A drain owed only to the baseline says so, or a reader sees
            // "0 symbol(s) awaiting verification — draining now" and goes looking for the
            // bug in the queue.
            match baselineInvalidReason debt with
            | Some reason ->
                $"%d{queued} symbol(s) awaiting verification + a full suite to earn the baseline (%s{reason})"
            | None -> $"%d{queued} symbol(s) awaiting verification"

    /// Write the queue before the analysis flush advances the durable symbol snapshot:
    /// once the snapshot advances, a symbol missing from the durable queue can no longer
    /// be re-detected after a crash. Only ever ADDS to what is on disk — the state it
    /// writes is the published queue plus this event's enqueues — so writing it before
    /// publication can over-test, never under-test.
    ///
    /// Unknown debt is not written: the unreadable file is the honest record until the
    /// recovering full suite publishes a new one.
    let persistQueueAdditions (debt: VerificationDebt) =
        if not debt.RecoveryOutstanding then
            try
                PendingVerification.save repoRoot debt.PendingQueue
            with ex ->
                Logging.warn
                    "test-prune"
                    $"failed to persist pending-verification queue before the analysis flush: %s{ex.Message}; the queue is published with this event's state"

    /// The debt revision of `symbol`. A symbol loaded from the durable queue and never
    /// re-enqueued is at revision 0.
    let revisionOf (debt: VerificationDebt) (symbol: string) : int64 =
        debt.SymbolRevisions |> Map.tryFind symbol |> Option.defaultValue 0L

    /// Add `symbols` to the queue at one new revision, including a re-edit of a symbol
    /// already queued: a run captured before it cannot have built it.
    let enqueuePending (symbols: string list) (debt: VerificationDebt) =
        if symbols.IsEmpty then
            debt
        else
            let revision = debt.Revision + 1L

            { debt with
                PendingQueue = (debt.PendingQueue, symbols) ||> List.fold (fun q s -> Set.add s q)
                Revision = revision
                SymbolRevisions =
                    (debt.SymbolRevisions, symbols)
                    ||> List.fold (fun revisions s -> Map.add s revision revisions) }

    /// Remove `symbols` from the queue: a covering run passed them, or they have no
    /// covering test. Durable once the state carrying it is prepared.
    let commitPending (symbols: Set<string>) (debt: VerificationDebt) =
        if symbols.IsEmpty then
            debt
        else
            { debt with
                PendingQueue = Set.difference debt.PendingQueue symbols
                SymbolRevisions = debt.SymbolRevisions |> Map.filter (fun s _ -> not (Set.contains s symbols)) }

    let expectedRuntimeCoverageProjects =
        match testConfigs, coveragePaths with
        | Some configs, Some pathsForProject ->
            configs
            |> List.choose (fun config ->
                if pathsForProject config.Project |> Option.isSome then
                    Some config.Project
                else
                    None)
        | _ -> []

    let allowedRuntimeProjects = Set.ofList expectedRuntimeCoverageProjects

    let loadedDebt =
        { PendingQueue =
            match loadedQueue with
            | PendingVerification.LoadedQueue.Loaded queue -> queue
            // The owed symbols cannot be NAMED, so none are seeded; `RecoveryOutstanding`
            // carries the debt instead and widens every run until a full suite proves the
            // tree. Seeding `empty` is safe ONLY because that flag is set.
            | PendingVerification.LoadedQueue.Unreadable _ -> PendingVerification.empty
          SymbolRevisions = Map.empty
          Revision = 0L
          RecoveryOutstanding =
            ledgerUnreadableReason.IsSome
            || failuresUnreadable
            || runtimeObligationsUnreadable
            || interruptedPublication
          Baseline = loadedBaseline
          RuntimeObligations = pruneRuntimeCoverageObligations allowedRuntimeProjects loadedRuntimeObligations }

    // A project that no longer produces runtime coverage cannot retire an obligation, so
    // its obligations are pruned at load. Written here, before any event, because no
    // publication will otherwise carry an unchanged debt to disk.
    if
        not loadedDebt.RecoveryOutstanding
        && loadedDebt.RuntimeObligations <> loadedRuntimeObligations
    then
        match
            persistRuntimeCoverageObligationsWith
                (fun () ->
                    FsHwPaths.atomicWriteAllText
                        (runtimeCoverageRecoveryPath repoRoot)
                        "runtime obligation write in progress")
                (fun () -> saveRuntimeCoverageObligations repoRoot loadedDebt.RuntimeObligations)
                (fun () -> File.Delete(runtimeCoverageRecoveryPath repoRoot))
        with
        | Ok() -> ()
        | Error ex ->
            Logging.warn
                "test-prune"
                $"failed to persist pruned runtime coverage obligations: %s{ex.Message}; the next publication writes them"

    let runtimeCoverageSelection changedFiles =
        selectByRuntimeCoverage
            db
            expectedRuntimeCoverageProjects
            changedFiles
            (DateTimeOffset.UtcNow - RuntimeCoverageMaxAge)

    /// The covering test projects a symbol's debt waits on, under ONE resolution of the
    /// declared exclusions. Empty ⇒ nothing is owed for it and it may leave the queue.
    ///
    /// A covering project counts unless it is unconfigured AND declared excluded with a
    /// written reason. That is the whole policy, applied at every place debt is
    /// classified — the flush that drops uncovered symbols, the launch that captures each
    /// symbol's coverers, the completion that retires them (launch and BootScan cohorts
    /// alike), and the residual diagnostics that name what is still owed:
    ///   * a CONFIGURED project always counts, whatever an exclusion says;
    ///   * an unconfigured project with no declaration counts. Nothing can run it here,
    ///     so the symbol stays owed and the verdict stays red, naming the project. The
    ///     earlier rule dropped such a symbol, which let a configured-suite green
    ///     discharge tests that never ran;
    ///   * a blank reason declares nothing.
    /// Excluding a project is never evidence that its tests passed: the declaration only
    /// removes that project from THIS gate's claim.
    ///
    /// `excluded` is lazy so a classification that never meets an unconfigured coverer
    /// never resolves the declarations. Analysis-only daemons make no test claim, so every
    /// covering project counts and the declarations are never consulted. `covering` is a
    /// pass's `coveringOf`.
    let debtScope (excluded: Lazy<Map<string, string>>) (covering: string -> Set<string>) : string -> Set<string> =
        let declaredExcluded (project: string) =
            match Map.tryFind project excluded.Value with
            | Some reason -> not (String.IsNullOrWhiteSpace reason)
            | None -> false

        fun symbol ->
            let projects = covering symbol

            if Set.isEmpty runnableProjects then
                projects
            else
                projects
                |> Set.filter (fun project -> Set.contains project runnableProjects || not (declaredExcluded project))

    /// `debtScope` resolving the declarations only if a classification needs them.
    let lazyDebtScope (covering: string -> Set<string>) =
        debtScope (lazy (resolveExcludedProjects ())) covering

    /// The test projects covering each symbol a classification pass is about: what its
    /// single-seed `QueryAffectedTests` would select from. One grouped query answers all
    /// of `symbols`, run the first time any symbol is asked about and never when none is.
    /// A symbol outside `symbols` is answered by a grouped query of its own.
    let coveringOf (symbols: string seq) : string -> Set<string> =
        let known = symbols |> Seq.distinct |> List.ofSeq
        let grouped = lazy (queries.CoveringProjectsBySeed known)

        fun symbol ->
            match Map.tryFind symbol grouped.Value with
            | Some projects -> projects
            | None ->
                queries.CoveringProjectsBySeed [ symbol ]
                |> Map.tryFind symbol
                |> Option.defaultValue Set.empty

    /// The unconfigured projects `owedTo` still waits on for `symbols`, for a message
    /// that has to name them.
    let owedElsewhere (owedTo: string -> Set<string>) (symbols: string seq) : Set<string> =
        symbols
        |> Seq.map (fun s -> Set.difference (owedTo s) runnableProjects)
        |> Set.unionMany

    // True until this process's first extension refresh: the stored edges were written by
    // an earlier process, over a tree that may differ. After that, a refresh is owed when a
    // flush indexes something or an extension failed (`FailedExtensions`).
    let extensionsUnrefreshed = ref true

    // Flush pending analysis to DB and merge the runtime obligations it names.
    // Extensions (if any) then replace their COMPLETE edge sets via
    // `refreshExtensionEdges` — after the AST results are written, so they resolve
    // against current symbols, and before QueryAffectedTests, so their edges
    // participate in impact traversal. They re-run only when this flush indexed
    // something or a refresh is owed: their answer is a function of the tree.
    let flushPending (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        // Capture OLD literal coupling before `RebuildProjects`
        // replaces a changed producer's outgoing graph. The unchanged test still
        // points at that old literal, but after the rebuild the producer does not;
        // querying only the changed producer would therefore select nothing.
        //
        // Queue the literal NODE, not a stale producer edge. It then follows the
        // ordinary pending-verification lifecycle and disappears after its covering
        // tests pass, while the rebuilt graph remains an exact description of current
        // source. This is bounded evidence carried across one destructive boundary,
        // not permanent false-positive coupling.
        let preRebuildSymbols =
            Set.union state.Debt.PendingQueue (Set.ofList state.ChangedSymbols)
            |> Set.toList

        let priorLiteralSeeds = db.GetPriorSharedLiteralSeeds preRebuildSymbols

        // Only a seed not already owed is new debt. Re-enqueuing an owed one would issue
        // it a newer revision and deny a covering run in flight its discharge.
        let newlyOwedLiterals =
            priorLiteralSeeds
            |> List.filter (fun symbol -> not (Set.contains symbol state.Debt.PendingQueue))

        let state =
            { state with
                Debt = enqueuePending newlyOwedLiterals state.Debt
                ChangedSymbols = (priorLiteralSeeds @ state.ChangedSymbols) |> List.distinct }

        if not priorLiteralSeeds.IsEmpty then
            Logging.info
                "test-prune"
                $"Preserved %d{priorLiteralSeeds.Length} pre-rebuild literal coupling seed(s) for impact selection"

        // Persist the pending queue BEFORE flushPendingAnalysis advances the
        // durable analysis snapshot. One write per flush (vs per FileChecked) — same
        // crash-safety, batch-size fewer disk writes. See `persistQueueAdditions`.
        persistQueueAdditions state.Debt

        let indexedSomething = not state.PendingAnalysis.IsEmpty
        let flushedState = flushPendingAnalysis db state

        let flushedState =
            match extensions with
            | Some exts when
                not exts.IsEmpty
                && (indexedSomething
                    || extensionsUnrefreshed.Value
                    || not flushedState.FailedExtensions.IsEmpty)
                ->
                extensionsUnrefreshed.Value <- false

                // Timed as a whole: the refresh runs every extension in one call. At info,
                // because a refresh that holds the fold for minutes is otherwise invisible.
                let refreshClock = Diagnostics.Stopwatch.StartNew()
                let outcomes = refreshExtensionEdges db repoRoot exts
                let refreshMs = refreshClock.ElapsedMilliseconds

                let failedExtensions =
                    outcomes
                    |> List.fold
                        (fun failed outcome ->
                            match outcome with
                            | ExtensionRefresh.Refreshed(name, edgeCount) ->
                                ctx.ClearErrors(extensionLedgerKey name)
                                Logging.info "test-prune" (extensionStoredLine name edgeCount refreshMs)
                                Map.remove name failed
                            | ExtensionRefresh.Failed(name, ex) ->
                                Logging.error
                                    "test-prune"
                                    $"Extension '%s{name}' failed: %s{ex.ToString()}; its previously stored edges are kept and every test project runs in full until it answers"

                                ctx.ReportErrors
                                    (extensionLedgerKey name)
                                    [ extensionFailedDiagnostic name ex.Message ]

                                Map.add name ex.Message failed)
                        flushedState.FailedExtensions

                { flushedState with
                    FailedExtensions = failedExtensions }
            | _ -> flushedState

        let runtimeSelection = runtimeCoverageSelection flushedState.ChangedFiles

        let runtimeObligations =
            if Map.isEmpty runtimeSelection.ProjectsByFile then
                flushedState.Debt.RuntimeObligations
            else
                mergeRuntimeCoverageObligations flushedState.Debt.RuntimeObligations runtimeSelection.ProjectsByFile

        if not (Map.isEmpty runtimeSelection.ProjectsByFile) then
            for KeyValue(file, projects) in runtimeSelection.ProjectsByFile do
                if not (Set.isEmpty projects) then
                    let projectNames = projects |> Set.toList |> String.concat ", "

                    Logging.info
                        "test-prune"
                        $"runtime coverage selected %s{file} -> %s{projectNames} (project-in-full)"

        reportRuntimeCoverageWidenings (Logging.warn "test-prune") runtimeSelection

        flushedState, runtimeObligations

    /// The impact selection over a flushed state, and the classification of every queued
    /// symbol by the projects covering it.
    let selectAffected (flushedState: TestPruneState) (runtimeObligations: RuntimeCoverageObligations) =
        // Affected tests must be computed from the WHOLE needs-testing queue —
        // the in-memory hot view UNION the durable sidecar — not just the latest
        // diff. The persisted queue holds symbols a green run hasn't yet cleared
        // (e.g. carried across a restart, or left behind by an Aborted/failed
        // run); they must keep selecting tests until a covering run passes.
        let symbols =
            Set.union flushedState.Debt.PendingQueue (Set.ofList flushedState.ChangedSymbols)
            |> Set.toList

        let affectedTests =
            if symbols.IsEmpty then
                []
            else
                // Scoped to projects this daemon actually runs. Selecting tests in an
                // unconfigured project would put classes in the run map that never
                // execute — and make `allChangesUncovered` (and so the zero-affected
                // skip) disagree with the commit rule. See `debtScope`.
                let queryRunnable (seeds: string list) =
                    queries.AffectedTests seeds
                    |> fun ts ->
                        if Set.isEmpty runnableProjects then
                            ts
                        else
                            ts |> List.filter (fun t -> Set.contains t.TestProject runnableProjects)

                let affected = queryRunnable symbols
                let sortedSeeds = List.sort symbols

                // Count the INPUT explicitly, not just the output. `symbols` is the
                // union of the durable pending-verification queue and the in-memory
                // hot view, so it grows monotonically across aborted runs until a
                // green run clears it — "the queue is wedged and growing" and "a
                // small, precise selection" look identical without this number, and
                // `%A` truncated it away at 100.
                //
                // The seed list is logged in FULL and sorted (`describeAll`, not
                // `%A`): when a handful of junk seeds drags in thousands of tests,
                // the offending seed is the whole diagnosis, and a sample that
                // happens to omit it costs hours.
                Logging.info
                    "test-prune"
                    $"QueryAffectedTests: %d{symbols.Length} seed(s) → %d{affected.Length} affected tests"

                Logging.info "test-prune" $"  seeds: %s{describeAll sortedSeeds}"

                // How many tests ONE seed selects on its own — a full recursive
                // reverse-walk each time, so it is memoised. Two diagnostics below ask
                // this same question (the poisoned-seed guard and the per-seed
                // attribution), and the seeds they ask about OVERLAP: `agedSeeds` is a
                // subset of `sortedSeeds`, so in the case that matters most — a wide
                // selection AND pinned seeds, which is exactly the shape both exist to
                // catch — every aged seed was paying for the same query twice in one
                // flush. Only `.Length` is ever used, so nothing needs the rows.
                let aloneCounts = System.Collections.Generic.Dictionary<string, int>()

                let aloneCount (seed: string) : int =
                    match aloneCounts.TryGetValue seed with
                    | true, n -> n
                    | false, _ ->
                        let n = (queryRunnable [ seed ]).Length
                        aloneCounts[seed] <- n
                        n

                // The poisoned-seed guard.
                //
                // The per-seed attribution below asks "is one seed dominating THIS run?",
                // a question about a moment. The failure that happened was about TIME:
                // `name` and `kind` dominated every run for days, and each run looked
                // like a legitimately expensive edit. Only width that PERSISTS separates
                // them.
                //
                // This only READS the ages — they advance once per test RUN, at the
                // launch point (see `pendingAgeRef`). Do not bump them here: this
                // function runs 2-3 times per edit-save cycle, so the counter would
                // measure flushes, and two edits to one function on a green repo would
                // trip a guard designed to fire late.
                //
                // Independent of `WideSelectionTests`: a seed pinning 200 tests of a
                // 400-test suite is the same disease as one pinning 3,000, and gating on
                // absolute width would miss every smaller repo.
                let ages = Volatile.Read(&pendingAgeRef)

                let ageOf s =
                    ages |> Map.tryFind s |> Option.defaultValue 0

                // Each check below is a full recursive reverse-walk, so both the count and
                // the decision to run at all are gated:
                //
                //  * an EMPTY selection can never yield a suspect (`isPoisonSuspect`
                //    requires `affectedCount > 0`), so the loop is skipped — the common
                //    case on a no-op cycle;
                //  * the same `MaxSeedsToAttribute` budget the attribution loop uses.
                //    `agedSeeds` grows precisely when the queue is wedged — the situation
                //    this guard exists to report — so an unbudgeted loop would pay N graph
                //    walks per build exactly when the daemon is already struggling. A
                //    suspect must account for >=25% of the selection, so at most four
                //    seeds can qualify and a large aged list is waste by construction.
                //    The cap is never silent.
                let agedSeeds = sortedSeeds |> List.filter (fun s -> ageOf s >= PoisonSeedRuns)

                if not affected.IsEmpty && agedSeeds.Length > MaxSeedsToAttribute then
                    Logging.warn
                        "test-prune"
                        $"%d{agedSeeds.Length} seed(s) have been queued across %d{PoisonSeedRuns}+ consecutive \
                          runs, which exceeds the %d{MaxSeedsToAttribute}-seed budget — the poisoned-seed check \
                          is SKIPPED this cycle. A pending queue that size is itself the finding; the full seed \
                          list is logged above."

                for seed in
                    (if affected.IsEmpty || agedSeeds.Length > MaxSeedsToAttribute then
                         []
                     else
                         agedSeeds) do
                    let runs = ageOf seed
                    let alone = aloneCount seed

                    if isPoisonSuspect runs affected.Length alone then
                        let pct = alone * 100 / (max 1 affected.Length)

                        // Deliberately a WARNING and not a quarantine: dropping the
                        // symbol would be under-testing on a guess. The failure this
                        // addresses is that nobody could SEE the pattern.
                        Logging.warn
                            "test-prune"
                            $"POSSIBLE POISONED SEED: '%s{seed}' has been queued for verification across \
                              %d{runs} consecutive runs and alone selects %d{alone} of %d{affected.Length} \
                              tests (%d{pct}%%). A symbol only leaves the queue once every runnable project \
                              covering it passes, so one persistently-failing project pins it and it re-seeds \
                              this selection every run. Check whether it is a real dependency-graph hub, a \
                              mis-qualified symbol, or a test project that has been red for a \
                              while."

                // Per-seed attribution, but ONLY when the selection is already wide.
                // A single seed accounting for most of a run is either a genuine
                // graph hub or a mis-qualified symbol, and is the only thing worth
                // looking at; below the threshold this costs nothing because the
                // re-query loop never runs.
                if affected.Length > WideSelectionTests then
                    if sortedSeeds.Length > MaxSeedsToAttribute then
                        // Never cap silently: say the attribution was skipped and why,
                        // so an absent breakdown is not read as "no seed dominated".
                        Logging.warn
                            "test-prune"
                            $"%d{affected.Length} tests selected, but %d{sortedSeeds.Length} seeds exceeds the \
                              %d{MaxSeedsToAttribute}-seed attribution budget — per-seed breakdown SKIPPED"
                    else
                        // Derived from the same constant `isPoisonSuspect` uses, never a
                        // second spelling of it: a `/ 4` here beside a `25` there drifts,
                        // and the two diagnostics then disagree about "dominant".
                        let dominantShare = affected.Length * PoisonSeedSharePercent / 100

                        for seed in sortedSeeds do
                            let alone = aloneCount seed

                            if alone > dominantShare then
                                let pct = alone * 100 / affected.Length

                                Logging.warn
                                    "test-prune"
                                    $"seed '%s{seed}' alone selects %d{alone} of %d{affected.Length} tests \
                                      (%d{pct}%%) — a dependency-graph hub, or a mis-qualified symbol"

                affected

        // Classify every considered symbol ONCE, under one resolution of the declared
        // exclusions (see `debtScope`):
        //
        //  * NOTHING OWED — no covering test at all, or only coverers that `tests.excluded`
        //    declares out of scope with a reason. Dropped from the durable queue now:
        //    retaining it would re-select zero tests forever and never empty. Only ever
        //    REMOVES from the queue. A declaration-covered drop is a WRITE-OFF, not a
        //    discharge, so it is REPORTED, naming the project, in the log and on the verdict
        //    (`UncoveredChanges`).
        //  * OWED ELSEWHERE — a covering project this daemon does not run and nothing
        //    declares excluded. Nothing here can discharge it, and dropping it would let a
        //    configured-suite green retire tests that never ran. It STAYS owed; the verdict
        //    stays red and names the project until the config lists or excludes it.
        let covering = coveringOf symbols
        let owedTo = lazyDebtScope covering
        let owing = symbols |> List.map (fun s -> s, owedTo s) |> Map.ofList

        let uncovered =
            owing
            |> Map.filter (fun _ projects -> Set.isEmpty projects)
            |> Map.keys
            |> Set.ofSeq

        let unrunnable: UnrunnableCoverage =
            if Set.isEmpty uncovered || Set.isEmpty runnableProjects then
                Map.empty
            else
                uncovered
                |> Set.toList
                |> List.choose (fun s ->
                    match covering s with
                    | projects when Set.isEmpty projects -> None
                    | projects -> Some(s, projects))
                |> Map.ofList

        let owedToUnrunnable =
            owing
            |> Map.map (fun _ projects -> Set.difference projects runnableProjects)
            |> Map.filter (fun _ projects -> not (Set.isEmpty projects))

        if not (Set.isEmpty uncovered) then
            Logging.info
                "test-prune"
                $"Dropping %d{Set.count uncovered} queued symbol(s) with no covering test in a governed project from pending-verification queue"

            if not (Map.isEmpty unrunnable) then
                let projects =
                    UnrunnableCoverage.projects unrunnable |> Set.toList |> String.concat ", "

                Logging.warn
                    "test-prune"
                    $"%d{Map.count unrunnable} of them ARE covered — only by tests in %s{projects}, which \
                      `tests.excluded` declares out of this gate's scope. The obligation is written off by that \
                      declaration, not discharged by a test. Symbols: \
                      %s{describeAll (unrunnable |> Map.keys |> List.ofSeq)}"

        if not (Map.isEmpty owedToUnrunnable) then
            let projects =
                UnrunnableCoverage.projects owedToUnrunnable |> Set.toList |> String.concat ", "

            Logging.warn
                "test-prune"
                $"%d{Map.count owedToUnrunnable} queued symbol(s) stay OWED to tests in %s{projects}, which this \
                  daemon does not run and `tests.excluded` does not declare. No run here can discharge them, so \
                  the verdict stays red: list the project in `tests.projects`, or declare it in `tests.excluded` \
                  with a reason. Symbols: %s{describeAll (owedToUnrunnable |> Map.keys |> List.ofSeq)}"

        // Keep the in-memory hot view aligned with the durable queue so the
        // ChangedSymbols carried in state (and the cache-key snapshot) don't
        // re-select the uncovered symbols on the next event.
        let remainingSymbols =
            flushedState.ChangedSymbols
            |> List.filter (fun s -> not (Set.contains s uncovered))

        // There WERE symbols to consider this cycle, yet the affected set is empty — so
        // every one of them was just dropped as uncovered (a union query returning zero
        // tests means every per-symbol query did too). That is a definitive "nothing to
        // verify" green, which the run-trigger reads to complete immediately instead of
        // running the full suite. An EMPTY `symbols` (genuine cold start, nothing
        // pending) leaves this false so the baseline still runs.
        //
        // The flag buys a green that executes NOTHING, so it must rest
        // on proof — and an empty `QueryAffectedTests` proves "no test covers this" only
        // for a symbol the index KNOWS. For a name it has never heard of, the identical
        // empty result means "I cannot answer". So ask the index what it KNOWS
        // (`unknownToIndex`), never how it came to be in that state.
        //
        // The two files drift apart in practice: a `SchemaVersion` bump deletes and
        // recreates `test-impact.db`, but the pending-verification sidecar beside it
        // carries no version and SURVIVES. Every name in the queue then resolves to
        // nothing, is dropped just above as "no runnable covering test", and the cycle
        // completes GREEN with ZERO tests run — the debt discharged by the schema bump
        // rather than by a test. A stale queue entry naming a since-renamed symbol takes
        // the same route with no recreate involved.
        //
        // The symbols are still dropped from the queue above, so a permanently-absent
        // name cannot wedge it; the run happens once and discharges the debt.
        //
        // `GetAllSymbolNames` is a full read of the `symbols` table, so it sits behind
        // `noCoveringTest` — already the rare branch (queued symbols AND a zero-length
        // selection). On the ordinary path the index is never consulted.
        let noCoveringTest = not symbols.IsEmpty && List.isEmpty affectedTests

        let unknownToIndex =
            if not noCoveringTest then
                []
            else
                let known = db.GetAllSymbolNames()
                symbols |> List.filter (fun s -> not (known.Contains s)) |> List.sort

        let indexCannotVouch = noCoveringTest && not (List.isEmpty unknownToIndex)

        if indexCannotVouch then
            Logging.warn
                "test-prune"
                $"%d{symbols.Length} queued symbol(s) resolved to no covering test, but %d{unknownToIndex.Length} \
                  of them are NOT KNOWN to the symbol index — so for those that is 'the index cannot answer', not \
                  proof they are untested. Refusing the zero-test green; this run verifies them for real. \
                  Unknown: %s{describeAll unknownToIndex}"

        // Symbols still owed elsewhere are not uncovered: the zero-test green must not
        // retire the red they hold.
        let allChangesUncovered =
            if noCoveringTest && List.isEmpty unknownToIndex && Map.isEmpty owedToUnrunnable then
                UncoveredChanges.AllUncovered(List.sort symbols, unrunnable)
            else
                UncoveredChanges.No

        // Remember the seeds ONLY when this selection actually has tests to run.
        // The question the report has to answer is "what was the last change that
        // DID trigger tests?" — a selection that chose nothing did not, and
        // recording it would answer that question with the one change guaranteed
        // to be irrelevant. Carrying the previous value forward is the point: it
        // is what survives to be reported by a later check that selects nothing.
        let seedsThatSelectedTests =
            if List.isEmpty affectedTests then
                flushedState.LastSeeds
            else
                List.sort symbols

        { flushedState with
            Debt =
                { commitPending uncovered flushedState.Debt with
                    RuntimeObligations = runtimeObligations }
            ChangedSymbols = remainingSymbols
            AffectedTests = Analyzed affectedTests
            ChangedSymbolsAllUncovered = allChangesUncovered
            LastSeeds = seedsThatSelectedTests }

    /// Flush, then select, unless the mode skips `FlushSelection`: pass-through records
    /// the debt without selecting or classifying it, and only a full run's green
    /// discharges it.
    let flushAndQueryAffected (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        let flushedState, runtimeObligations = flushPending ctx state

        if TestMode.skips FlushSelection flushedState.Mode then
            { flushedState with
                Debt =
                    { flushedState.Debt with
                        RuntimeObligations = runtimeObligations }
                AffectedTests = Analyzed []
                ChangedSymbolsAllUncovered = UncoveredChanges.No }
        else
            selectAffected flushedState runtimeObligations

    /// `flushAndQueryAffected` under the bound it declares over itself
    /// (`ImpactSelectionDeadline`). Every caller goes through this: the fold is the same
    /// work whichever event drove it, and a caller that forgot the declaration would be
    /// the one the stall detector failed.
    let flushAndQueryAffectedBounded (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        use _declaration = ctx.DeclareBoundedWork "impact selection" ImpactSelectionDeadline
        flushAndQueryAffected ctx state

    // Per-file FCS freshness sidecar, loaded once at plugin construction from
    // `.fshw/test-prune/file-freshness.json` and updated incrementally on each
    // FileChecked. Survives daemon restarts so a cross-restart replay can decide which
    // files' stored symbols are trustworthy enough to run detectChanges against.
    //
    // Closure-local mutable cell + Volatile: only the Update handler reads and writes it.
    let mutable freshnessRef: FileFreshness.Store = FileFreshness.load repoRoot

    /// The clock `storedRowsExist: bool` did not have. Rows written
    /// seconds ago by this session's own scan are not a baseline; diffing the current
    /// extraction against them compares the file to itself, always reports "nothing
    /// changed", and selects zero test projects — on a diff that ADDS tests. See
    /// `FileFreshness.PriorRowLedger`, which owns the rule.
    let priorRows = FileFreshness.PriorRowLedger()

    let updateFreshness (newStore: FileFreshness.Store) =
        Volatile.Write(&freshnessRef, newStore)

        try
            FileFreshness.save repoRoot newStore
        with ex ->
            Logging.warn
                "test-prune"
                $"failed to persist file-freshness sidecar: %s{ex.Message}; in-memory state still updated"

    let hasTestConfigs =
        testConfigs |> Option.map (List.isEmpty >> not) |> Option.defaultValue false

    // Seed the in-memory hot view from the durable queue so a restart with a non-empty
    // queue re-flags those symbols. Without this, a restart diffs current symbols against
    // the already-advanced analysis snapshot → "nothing changed" → zero tests run → false
    // green.
    let initialState =
        { Debt = loadedDebt
          Mode = TestMode.initial
          InFlight = None
          CompletedRuns = []
          CheckReach = None
          Replies = []
          PendingAnalysis = Map.empty
          AnalysisModelGeneration = None
          AnalysisFiles = Map.empty
          AnalysisReceipt = None
          SymbolSnapshot = Map.empty
          AffectedTests = NotYetAnalyzed
          ChangedSymbols = loadedDebt.PendingQueue |> Set.toList
          ChangedFiles = []
          LastResults = None
          LastRunId = None
          LastSeeds = []
          DebtDuringFullRun = Map.empty
          TestClassFiles = Map.empty
          BuildCompletedInThisSession = false
          PriorProjectFingerprints = Map.empty
          PendingForceRunProjects = Set.empty
          ChangedSymbolsAllUncovered = UncoveredChanges.No
          UnanalyzableFiles = Map.empty
          FailedExtensions = Map.empty
          // The previous session's reds, quarantined into the first run.
          OutstandingFailures = loadedFailures
          LastCoverage = RunCoverage.none
          LastZeroSelection = ZeroSelection.NotAZero
          EvidenceReceipt = None
          Earned = None }

    /// The generation of the model the host currently publishes, when it is available.
    let observeModelGeneration (ctx: PluginCtx<TestPruneMsg>) =
        match ctx.ProjectGraph.ObserveModel() with
        | FsHotWatch.ProjectModel.Observation.Available model -> Some model.Generation
        | FsHotWatch.ProjectModel.Observation.Unobserved
        | FsHotWatch.ProjectModel.Observation.Rediscovering _
        | FsHotWatch.ProjectModel.Observation.Unavailable _ -> None

    /// Returns the `TestsFinished` message the framework's RunExclusive posts back to the
    /// agent; the synchronous `Custom(TestsFinished)` handler emits `TestRunCompleted`
    /// inside the cache-write capture window. `TestRunStarted` fires before the host starts.
    /// Catches its own exceptions to produce an `Aborted` lifecycle — letting RunExclusive
    /// eat the message would free the slot with no completion posted, stranding
    /// `LastResults` and the debt the run launched against.
    let runTestsWithImpact
        (ctx: PluginCtx<TestPruneMsg>)
        (configs: TestConfig list)
        // The four fields a run reads — NOT the state record. The async this
        // returns outlives the state generation it was launched from; see
        // `TestRunInputs`.
        (inputs: TestRunInputs)
        // `hasCachedResults` (`state.LastResults.IsSome`, computed by the caller)
        // means a run already completed THIS session — i.e. a green baseline exists
        // to be "test-equivalent" to. The zero-affected skip needs this AS WELL AS an
        // empty queue: a cold daemon with an empty queue but no prior run has no
        // baseline yet and must run the full suite once to establish one. See
        // the skip gate below.
        (hasCachedResults: bool)
        // Dependency-fanout (DependencyFanout): test projects whose dependency
        // fingerprint changed since the last build. Their tests run in FULL
        // (project-coarse), UNIONED with the symbol-precise selection — a binary
        // change the symbol diff can't see still re-runs the dependent tests.
        // Empty in the common case (no dependency change), so ordinary
        // source-symbol edits keep their precise, minimal selection.
        (fanoutProjects: Set<string>)
        : Async<TestPruneMsg> =
        async {
            let mutable emittedStart: TestRunStarted option = None

            let emitStarted started =
                emittedStart <- Some started
                ctx.EmitTestRunStarted started

            // The single chokepoint every launch path funnels through (BatchChecked
            // drain, BuildCompleted, deferred rerun). Both widenings live here rather
            // than at each call site, so no future launch path can forget one and
            // quietly reintroduce a filtered run where an unfiltered one was owed.
            //
            //  * full-suite scope: run EVERY project, unfiltered.
            //  * unanalysable files: run every project, because a
            //    selection made without them cannot be trusted.
            //  * an UNREADABLE pending-verification ledger: run every
            //    project, because the ledger names what is still owed, and a selection
            //    made without it cannot be trusted either. Same shape as 113: the
            //    missing input is a SAFETY input, so its absence widens rather than
            //    narrows.
            //  * NO VALID FULL-SUITE BASELINE: run every project,
            //    because the tests a filtered run skips have nothing to be equivalent
            //    to. A cold repository earns its baseline here; a repository whose
            //    `tests.projects` grew re-earns it. Same shape as 150.
            let scopeIsFullSuite = TestMode.requestsFullSuite inputs.Mode
            let ledgerUnreadable = inputs.Debt.RecoveryOutstanding
            let baselineInvalid = baselineInvalidReason inputs.Debt

            // The coarse fallback only needs to know WHICH files are unanalysable; the
            // map's values exist so the ledger projection can re-report their
            // diagnostics.
            let unanalyzablePaths = inputs.UnanalyzableFiles |> Map.keys |> Set.ofSeq

            // A failed extension is a hole in the graph exactly as an unanalysable file
            // is: its stored edges describe an older tree. Same coarse fallback.
            let coarseGaps =
                inputs.FailedExtensions
                |> Map.keys
                |> Seq.map extensionLedgerKey
                |> Set.ofSeq
                |> Set.union unanalyzablePaths

            let launchedRuntimeObligations = inputs.Debt.RuntimeObligations

            let runtimeForceProjects =
                launchedRuntimeObligations |> Map.values |> Seq.collect Map.keys |> Set.ofSeq

            let forceRunProjects =
                let widened = coarseFallbackProjects configs coarseGaps fanoutProjects
                let widened = Set.union widened runtimeForceProjects

                if scopeIsFullSuite || ledgerUnreadable || Option.isSome baselineInvalid then
                    Set.union widened (fullSuiteProjects configs)
                else
                    widened

            if scopeIsFullSuite then
                Logging.info
                    "test-prune"
                    "Scope: FULL SUITE — impact filtering is disabled for this run; every configured test project runs in full"

            match baselineInvalid with
            | Some reason when not ledgerUnreadable ->
                Logging.info
                    "test-prune"
                    $"Scope: FULL SUITE (no valid full-suite baseline) — %s{reason}. Every configured test project runs in full; impact filtering resumes once this run has accounted for every project."
            | _ -> ()

            if ledgerUnreadable then
                Logging.warn
                    "test-prune"
                    "Scope: FULL SUITE (unreadable pending-verification ledger) — the record of what still needs testing could not be read, so this run cannot know what it owes. It runs EVERY configured test project in full rather than trust an impact selection made without the ledger. Impact filtering resumes once a full suite passes."

            if not (Map.isEmpty inputs.FailedExtensions) then
                let names = inputs.FailedExtensions |> Map.keys |> String.concat ", "

                Logging.warn
                    "test-prune"
                    $"%d{inputs.FailedExtensions.Count} test-impact extension(s) failed their last refresh (%s{names}) — their edges describe an older tree, so this run falls back to EVERY test project in full rather than trusting a selection made over them"

            if not (Set.isEmpty unanalyzablePaths) then
                let names = unanalyzablePaths |> Set.toList |> String.concat ", "

                Logging.warn
                    "test-prune"
                    $"%d{Set.count unanalyzablePaths} file(s) could not be analysed (%s{names}) — their symbols are missing from the impact graph, so this run falls back to EVERY test project in full rather than trusting a selection made without them"

            // The queue snapshot this run is LAUNCHED against — the durable
            // queue UNION the in-memory hot view. Captured here (not read from
            // state at completion time) because mid-run BatchChecked flushes
            // mutate both; the synchronous TestsFinished handler commits ONLY
            // these symbols and leaves mid-run arrivals queued for the rerun.
            let launchedSymbols =
                Set.union inputs.Debt.PendingQueue (Set.ofList inputs.ChangedSymbols)

            let launchedRevisions =
                launchedSymbols
                |> Set.toList
                |> List.map (fun symbol -> symbol, revisionOf inputs.Debt symbol)
                |> Map.ofList

            // Advance the poisoned-seed counters HERE, at the launch of
            // a test RUN, so the count means what `PoisonSeedRuns` and the warning text
            // claim. `flushAndQueryAffected` runs several times per edit-save cycle.
            if not (TestMode.skips SeedAgeing inputs.Mode) then
                Volatile.Write(
                    &pendingAgeRef,
                    bumpSeedAges (Volatile.Read(&pendingAgeRef)) (Set.toList launchedSymbols)
                )

            try
                // For each launched symbol, the set of test PROJECTS whose tests
                // cover it. An empty set ⇒ no covering test. Queried per-symbol so
                // a symbol commits ONLY when every project covering IT passed (a
                // run-wide union would over-couple unrelated symbols). Empty queue ⇒
                // no queries. Kept INSIDE the try: these are DB reads that can throw
                // transiently on a cold/contended box (SQLITE_BUSY, schema drift). A
                // throw here must produce the Aborted lifecycle below (an honest,
                // re-runnable "tests did not run" verdict) rather than escaping to
                // the framework's `runOne`, which would only log-and-strand the run.
                // The projects gating a symbol's commit are `debtScope`'s — the same
                // rule `flushAndQueryAffected` uses to drop symbols, so the two cannot
                // disagree. An unconfigured, undeclared coverer blocks the commit on
                // purpose: its tests never ran, so they verified nothing.
                //
                // A pass-through run asks nothing: it runs every project, and its
                // completion discharges by the full run's green (see `TestsFinished`).
                let coveringProjectsBySymbol =
                    if TestMode.skips LaunchCoveringCapture inputs.Mode then
                        Map.empty
                    else
                        let owedTo = lazyDebtScope (coveringOf launchedSymbols)

                        launchedSymbols |> Set.toList |> List.map (fun s -> s, owedTo s) |> Map.ofList

                // Extension-contributed edges were already written to the DB by
                // flushAndQueryAffected, so `inputs.AffectedTests` already includes tests
                // reachable through extension edges (sql, sql-hydra, falco, etc.).
                //
                // A pass-through flush selects nothing, so its launch asks the one grouped
                // question the check-reach sample needs: what `check` would have selected.
                let affectedTestsList =
                    if TestMode.skips FlushSelection inputs.Mode then
                        if Set.isEmpty launchedSymbols then
                            []
                        else
                            queries.AffectedTests(Set.toList launchedSymbols)
                            |> List.filter (fun t ->
                                Set.isEmpty runnableProjects || Set.contains t.TestProject runnableProjects)
                    else
                        match inputs.AffectedTests with
                        | Analyzed tests -> tests
                        | NotYetAnalyzed -> []

                let symbolAffectedByProject =
                    affectedTestsList
                    |> List.groupBy (fun t -> t.TestProject)
                    |> List.map (fun (proj, tests) -> proj, tests |> List.map (fun t -> t.TestClass) |> List.distinct)
                    |> Map.ofList

                // A prior red is mandatory verification debt. The
                // graph for today's edit may not reach yesterday's failing class, but
                // the next ordinary run must still execute it. Unknown class scope is
                // conservatively the whole project.
                let quarantinedAffectedByProject =
                    OutstandingFailure.quarantine
                        (fullSuiteProjects configs)
                        inputs.OutstandingFailures
                        symbolAffectedByProject

                // UNION the dependency-fanout: each force-run project enters the
                // map with an EMPTY class list, which `buildFilterArgs` treats as
                // "no filter → run ALL tests in this project" (a project ABSENT
                // from a non-empty map is skipped; present-with-[] runs in full).
                // We don't overwrite a project that already has symbol-affected
                // classes — its precise selection plus a forced full run are the
                // same "run this project"; [] (full) is the safe superset, so a
                // fanout hit promotes a partially-selected project to full.
                let affectedByProject =
                    forceRunProjects
                    |> Set.fold (fun acc proj -> Map.add proj [] acc) quarantinedAffectedByProject

                /// The run's SCOPE, in the same shape `executeTests`
                /// will actually honour, captured on the launch so the completion
                /// handler knows what this run is entitled to clear.
                ///
                /// Mirrors `executeTests`' own reading of `affectedClassesByProject`
                /// EXACTLY, which is why it is derived from that map and not
                /// re-decided: an EMPTY map means "no selection" → every project runs
                /// in full; a NON-empty map means a project present with `[]` runs in
                /// full, a project present with classes runs filtered, and a project
                /// ABSENT is skipped entirely (and recorded as a filtered pass that
                /// proves nothing — the laundering vector).
                let selection: Map<string, ProjectSelection> = selectionOf configs affectedByProject

                // The SAME derivation with the full-suite widening taken
                // back out — what `check` would have launched over this tree, this
                // instant. Every OTHER widening stays: the coarse fallback
                // for unanalysable files and the unreadable-ledger fallback
                // apply to the inner loop too, so removing them would project a selection
                // narrower than the one `check` actually uses and manufacture misses the
                // selector never made.
                //
                // Computed here, at the chokepoint, and NOT re-derived later: by
                // completion the symbol queue has moved on, which is exactly why the
                // reading was previously unrecoverable.
                let wouldHaveRun: Map<string, ProjectSelection> option =
                    if not scopeIsFullSuite then
                        // Nothing was widened past — the run IS the impact selection, and
                        // there is no second reading to project.
                        None
                    else
                        wouldHaveRunSelection
                            configs
                            quarantinedAffectedByProject
                            (coarseFallbackProjects configs coarseGaps fanoutProjects)
                            runtimeForceProjects
                            ledgerUnreadable
                        |> Some

                let launch =
                    { InputTreeHash = ReceiptInputTree.read repoRoot
                      ModelGeneration = observeModelGeneration ctx
                      Symbols = launchedSymbols
                      SymbolRevisions = launchedRevisions
                      ChangedFiles = inputs.ChangedFiles
                      CoveringProjectsBySymbol = coveringProjectsBySymbol
                      RuntimeProjectsByFile = launchedRuntimeObligations
                      Selection = selection
                      WouldHaveRun = wouldHaveRun
                      Seeds = inputs.Seeds
                      ZeroSelection = ZeroSelection.NotAZero }

                // The skip gate counts symbol-affected classes only. A pure
                // dependency-fanout (force-run projects, zero symbol classes) must
                // NOT be counted as "0 affected" and skipped — so the gate below
                // also checks `forceRunProjects` is empty.
                let totalClasses =
                    quarantinedAffectedByProject |> Map.values |> Seq.sumBy List.length

                let hasQuarantinedFailures = not (List.isEmpty inputs.OutstandingFailures)

                // Two independent routes to the degenerate zero-affected skip. Both
                // terminate as a clean green via the same lifecycle, differing from
                // "tests exist and all passed" only in that zero ran:
                //
                //  (1) Baseline-equivalent. Queue PROVABLY empty AND a session baseline
                //      exists. An empty queue means "test-equivalent to the last green
                //      run", so "0 affected tests" is a sound green. Both halves are
                //      load-bearing: a NON-empty queue with 0 affected classes (covered
                //      symbols whose tests aren't indexed yet) must run the suite rather
                //      than silent-green, and the first run of a session has no baseline
                //      to be equivalent TO, so it must run the full suite to establish
                //      one. Reads `nothingOwed`, not `Set.isEmpty` — an unreadable ledger
                //      owes an unknown debt and can never be baseline-equivalent
                //
                //  (2) Nothing-to-verify. This cycle HAD changed/queued symbols and every
                //      one proved to have no covering test (`ChangedSymbolsAllUncovered`,
                //      set by `flushAndQueryAffected` as it dropped them), so even a
                //      cold-start full suite would verify nothing about them. Sound
                //      WITHOUT a session baseline, unlike route 1, because it is gated on
                //      symbols having existed and provably lacking any test. Without it
                //      an all-uncovered cold run falls through to the full suite and
                //      hangs, never resolving WaitForComplete. A genuine cold start with
                //      NO pending symbols leaves the flag false, so the baseline runs.
                let baselineEquivalent = nothingOwed inputs.Debt && hasCachedResults

                let nothingToVerify = UncoveredChanges.isAll inputs.ChangedSymbolsAllUncovered

                if
                    totalClasses = 0
                    && Set.isEmpty forceRunProjects
                    && not hasQuarantinedFailures
                    && (baselineEquivalent || nothingToVerify)
                then
                    if nothingToVerify then
                        Logging.info
                            "test-prune"
                            "Every changed symbol has no covering test — nothing to verify, skipping tests (green, 0 ran)"
                    else
                        Logging.info
                            "test-prune"
                            "No affected classes, no dependency fanout, empty pending queue, baseline exists — skipping tests"

                    // Build a degenerate lifecycle (Started → Completed with empty
                    // Results). Start fires immediately; the synchronous Custom handler
                    // emits completion inside the cache-write capture window.
                    let runId = Guid.NewGuid()

                    let started: TestRunStarted =
                        { RunId = runId
                          StartedAt = DateTime.UtcNow }

                    emitStarted started

                    let completed: TestRunCompleted =
                        { RunId = runId
                          TotalElapsed = TimeSpan.Zero
                          Outcome = Normal
                          Results = Map.empty
                          // Impact analysis selected no project, so none was invoked.
                          // Stated rather than inferred, and the
                          // outcome is `Normal`, so it reaches consumers that filter
                          // on `Outcome`.
                          Verification = NoProjectsSelected }

                    // The skip EXECUTES NOTHING, so it covers nothing and may clear
                    // nothing. Empty results already yield an empty
                    // `RunCoverage`; the empty selection says so at the source too, so
                    // the "0 affected, green, 0 ran" path can never be mistaken for
                    // evidence about a project.
                    return
                        TestsFinished(
                            started,
                            completed,
                            { launch with
                                Selection = Map.empty
                                // Executed nothing, so it neither covers nor projects
                                // anything.
                                WouldHaveRun = None
                                ZeroSelection =
                                    (match inputs.ChangedSymbolsAllUncovered with
                                     | UncoveredChanges.AllUncovered(symbols, unrunnable) ->
                                         ZeroSelection.ChangesUncovered(symbols, unrunnable)
                                     | UncoveredChanges.No -> ZeroSelection.AlreadyVerified) }
                        )
                else
                    if totalClasses = 0 then
                        // NAME the debt that refused the zero-affected
                        // skip, and say what will actually run. Both halves were wrong
                        // before: the cause was hard-coded to two of five possibilities,
                        // and "running all tests" was printed over runs that were a
                        // couple of force-run projects.
                        let widenings =
                            zeroAffectedWidening
                                hasCachedResults
                                ledgerUnreadable
                                (Set.count inputs.Debt.PendingQueue)
                                inputs.Debt.RuntimeObligations
                                (List.length inputs.OutstandingFailures)
                                baselineInvalid

                        // An EMPTY selection is not "a few projects": `selectionOf` reads
                        // it as no selection at all and every configured project runs in
                        // full. That is the expensive outcome, so it is the one named.
                        let willRun =
                            if Map.isEmpty affectedByProject then
                                "EVERY configured project, in full"
                            else
                                $"%d{affectedByProject.Count} force-run project(s)"

                        match widenings with
                        | [] ->
                            // Nothing is owed and a baseline exists, so the zero-affected
                            // skip should have discharged this cycle for free — and did
                            // not. That is a defect in whichever arm consumed the signal,
                            // and it costs `willRun`. The phantom
                            // runtime-coverage obligation was exactly this shape and left
                            // no attributable line at all; this one is the alarm that a
                            // DIFFERENT arm has re-opened the same hole.
                            Logging.warn
                                "test-prune"
                                $"No affected classes, and NOTHING is owed — the zero-affected skip should have discharged \
                                  this cycle without running anything, yet it is running %s{willRun}. Some verification \
                                  debt is being reported as outstanding while naming nothing that can select or discharge \
                                  it."
                        | causes ->
                            Logging.info
                                "test-prune"
                                $"No affected classes, but the zero-affected skip is refused — \
                                  %s{ZeroAffectedWidening.describeMany causes}; running %s{willRun}"
                    else
                        for (proj, classes) in affectedByProject |> Map.toList do
                            // Never `%A` here: it caps the list at 100, so a
                            // 1,500-class blowout reads like a 100-class one, and it
                            // renders an EMPTY list as `[]` — which here means the
                            // project runs UNFILTERED, the exact opposite of "nothing
                            // selected". `describeMany` leads with the exact count.
                            let rendered =
                                if List.isEmpty classes then
                                    "ALL (unfiltered — force-run)"
                                else
                                    describeMany classes

                            Logging.info "test-prune" $"Affected classes for %s{proj}: %s{rendered}"

                    // Run-level selectivity in ONE line, since the per-project lines
                    // above can be interleaved or truncated. Separates the two ways a
                    // run can be wide: many classes named, versus projects unfiltered.
                    let unfilteredProjects =
                        affectedByProject |> Map.filter (fun _ cs -> List.isEmpty cs) |> Map.count

                    Logging.info
                        "test-prune"
                        $"Selectivity: %d{affectedByProject.Count} project(s) selected, \
                          %d{unfilteredProjects} of them UNFILTERED (whole-project), \
                          %d{totalClasses} class(es) named in total"

                    let! results, started, completed =
                        executeTests
                            db
                            (Some ctx)
                            emitStarted
                            repoRoot
                            launchDeadline
                            (tracesFor ctx inputs.Mode)
                            launch.InputTreeHash
                            beforeRun
                            (HookStep.asSubtasks ctx.StartSubtask ctx.EndSubtask)
                            coveragePaths
                            (coverageIngestFailed ctx)
                            afterRun
                            configuredTestProjects
                            configs
                            affectedByProject
                            None

                    // `executeTests` emits Started before launching the host and still
                    // emits per-group TestProgress live; the synchronous handler captures
                    // Completed for cache replay.
                    ignore results
                    return TestsFinished(started, completed, launch)
            with ex ->
                Logging.error "test-prune" $"runTests failed: %s{ex.Message}"

                // Build an Aborted lifecycle so subscribers see a coherent end
                // to this run rather than hanging at TestRunStarted.
                let started, completed = abortedRunLifecycle emittedStart ex.Message

                if emittedStart.IsNone then
                    emitStarted started

                // launch carries the queue snapshot this aborted run was
                // launched against; the TestsFinished handler commits NOTHING
                // for an Aborted outcome, so those symbols stay queued. Rebuilt
                // from `launchedSymbols` here (rather than the in-try `launch`,
                // which may not exist if the per-symbol coverage query itself
                // threw) with an empty covering map — an Aborted run commits
                // nothing, so the covering map is never consulted on this path.
                // The empty SELECTION says the same thing about the ledger: an
                // aborted run executed nothing, so it clears nothing.
                let launch =
                    { InputTreeHash = None
                      ModelGeneration = observeModelGeneration ctx
                      Symbols = launchedSymbols
                      SymbolRevisions = launchedRevisions
                      ChangedFiles = inputs.ChangedFiles
                      CoveringProjectsBySymbol = Map.empty
                      RuntimeProjectsByFile = launchedRuntimeObligations
                      Selection = Map.empty
                      WouldHaveRun = None
                      Seeds = inputs.Seeds
                      ZeroSelection = ZeroSelection.NotAZero }

                return TestsFinished(started, completed, launch)
        }

    /// The `run-tests` force-run work async, launched under the "tests" key by the
    /// `RunTestsRequested` fold. Every exit that returns yields a `CommandTestsFinished`,
    /// whose fold delivers the earned terminal status and resolves `reply` once that is
    /// published; cancellation resolves `reply` itself (the IPC command is awaiting it,
    /// bounded).
    ///
    /// Empty launch set: `run-tests` is a manual FORCE run (optionally
    /// filtered to a subset / only-failed). It is NOT the impact-analysis
    /// queue-draining path, and a filtered force-run may not cover every
    /// queued symbol — so it commits NOTHING from the pending-verification
    /// queue (over-testing is the safe direction). The queue drains through
    /// the normal BuildCompleted impact flow.
    let commandForceRun
        (ctx: PluginCtx<TestPruneMsg>)
        // The mode the force run launches under: it decides whether it records traces.
        (mode: TestMode)
        (configs: TestConfig list)
        (filter: string option)
        (reply: Tasks.TaskCompletionSource<string>)
        : Async<TestPruneMsg> =
        // A force-run launches exactly `configs`, each with NO class selection (it
        // passes `Map.empty` to `executeTests`), so each runs IN FULL — a plain
        // `dotnet fshw test-rerun` is therefore the unfiltered run that can clear ANY
        // outstanding red (the escape hatch that keeps the clear-only-what-it-covered rule
        // from wedging into a permanent stuck-red).
        //
        // A `--filter` passthrough is a different matter: `RunCoverage.ofRun` sees
        // `wasFiltered = true` on the results and declines to claim coverage for a
        // filter string whose reach it cannot compute. Projects NOT in `configs`
        // (`--only-failed`, `--projects`) are absent from the selection and so are
        // covered by nothing — exactly right, they did not run.
        let commandLaunch: TestRunLaunch =
            { InputTreeHash = None
              ModelGeneration = observeModelGeneration ctx
              Symbols = Set.empty
              SymbolRevisions = Map.empty
              // A force-run is not launched from the changed files, so it consumes none.
              ChangedFiles = []
              CoveringProjectsBySymbol = Map.empty
              RuntimeProjectsByFile = Map.empty
              Selection = configs |> List.map (fun c -> c.Project, ProjectInFull) |> Map.ofList
              // A FORCE run has no impact selection behind it — nothing was widened past,
              // so there is no `check` reading to project. This is the
              // ESCALATING `confirm`'s path, and it already carries an EXECUTED
              // impact-scoped reading taken before the escalation.
              WouldHaveRun = None
              Seeds = []
              ZeroSelection = ZeroSelection.NotAZero }

        async {
            let commandLaunch =
                { commandLaunch with
                    InputTreeHash = ReceiptInputTree.read repoRoot }

            let mutable emittedStart: TestRunStarted option = None
            let mutable returned = false

            let emitStarted started =
                emittedStart <- Some started
                ctx.EmitTestRunStarted started

            try
                try
                    let! results, started, completed =
                        executeTests
                            db
                            None
                            emitStarted
                            repoRoot
                            launchDeadline
                            (tracesFor ctx mode)
                            commandLaunch.InputTreeHash
                            beforeRun
                            (HookStep.asSubtasks ctx.StartSubtask ctx.EndSubtask)
                            coveragePaths
                            (coverageIngestFailed ctx)
                            afterRun
                            configuredTestProjects
                            configs
                            Map.empty
                            filter

                    // The counts come from the CTRF reports THIS RUN wrote, located by
                    // the run id the daemon just handed back — declared membership, never
                    // an mtime scan of a shared pile. An empty list from an existing
                    // run-dir means the run executed no tests, and that is exactly the
                    // fact the CLI has to be able to state. Only verdict evidence is
                    // read, so the counts printed beside a status are the ones that
                    // decided it.
                    let runReports =
                        FsHotWatch.Ctrf.verdictReportsForRun repoRoot started.RunId
                        |> List.map (fun r -> r.Project, r.Summary)
                        |> Map.ofList

                    // Returned (not Posted) so the framework's completion path
                    // delivers it: the synchronous handler does the error reporting and
                    // status updates a bare emit would skip, and the reply is resolved
                    // only once that completion is published.
                    returned <- true

                    return
                        CommandTestsFinished(
                            started,
                            completed,
                            commandLaunch,
                            reply,
                            formatTestResultsJson filter runReports results
                        )
                with ex ->
                    // A `beforeRun` throw / `executeTests` fault
                    // means the suite it guards NEVER RAN — that must surface
                    // as a failure, never a stale prior green. The Aborted
                    // lifecycle drives the TestsFinished handler to a Failed
                    // status.
                    Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                    let started, completed = abortedRunLifecycle emittedStart ex.Message

                    if emittedStart.IsNone then
                        emitStarted started

                    returned <- true

                    return
                        CommandTestsFinished(
                            started,
                            completed,
                            commandLaunch,
                            reply,
                            JsonSerializer.Serialize({| error = ex.Message |})
                        )
            finally
                // Cancellation — daemon teardown, or every client that asked for this run
                // has gone — skips `with` but runs `finally`, and no completion will fold.
                if not returned then
                    let reason = "the run was cancelled before it completed"

                    // Close the run this work opened. Subscribers track every started run
                    // until its completion (Build defers every build while one is live),
                    // so a started run must end. `Aborted` with no results is evidence of
                    // nothing: it is never a pass.
                    match emittedStart with
                    | Some started ->
                        let _, completed = abortedRunLifecycle (Some started) reason
                        ctx.EmitTestRunCompleted completed
                    | None -> ()

                    // Never leave a client that is still waiting on a reply that cannot come.
                    reply.TrySetResult(JsonSerializer.Serialize({| error = reason |})) |> ignore
        }

    let commands =
        [ "affected-tests",
          PluginCommand.Observe(fun (_ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
              async {
                  // Compute on demand from state.ChangedSymbols against current DB
                  // state. ChangedSymbols accumulates across FileChecked events and
                  // is reset by flushAndQueryAffected on BuildCompleted.
                  let symbols = state.ChangedSymbols |> List.distinct

                  let tests =
                      if symbols.IsEmpty then
                          []
                      else
                          queries.AffectedTests symbols

                  let testsData =
                      tests
                      |> List.map (fun t ->
                          {| project = t.TestProject
                             ``class`` = t.TestClass
                             ``method`` = t.TestMethod |})

                  return JsonSerializer.Serialize(testsData)
              })

          "changed-files",
          PluginCommand.Observe(fun (_ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
              async { return JsonSerializer.Serialize(state.ChangedFiles) })

          "test-results",
          PluginCommand.Observe(fun (ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
              async {
                  if ctx.IsRunning "tests" then
                      return JsonSerializer.Serialize({| status = "running" |})
                  else
                      match state.LastResults with
                      // No filter: `test-results` reports the LAST stored result set,
                      // which does not remember what launched it. Saying nothing is
                      // correct here; inventing "(none)" would claim it was unfiltered.
                      | Some results -> return formatTestResultsJson None Map.empty results
                      | None -> return JsonSerializer.Serialize({| status = "not run" |})
              })

          "flaky-tests",
          PluginCommand.Observe(fun (_ctx: CommandReadCtx) (_state: TestPruneState) (_args: string array) ->
              async {
                  let history = Flakiness.loadHistory (flakinessHistoryPath repoRoot)
                  let top = Flakiness.topFlaky 10 history

                  let payload =
                      top
                      |> List.map (fun (name, score) ->
                          let runs =
                              history |> Map.tryFind name |> Option.map List.length |> Option.defaultValue 0

                          {| name = name
                             flakiness = score
                             runs = runs |})

                  return JsonSerializer.Serialize({| tests = payload |})
              }) ]

    // run-tests / scope commands (only if testConfigs are provided). The scope verbs
    // live behind the same condition on purpose: a repo with no test projects has no suite
    // to run in full, so `fshw confirm` finds no `set-scope` command, cannot establish
    // the full-suite scope, and refuses to produce a merge verdict — rather than
    // silently issuing a green one it never earned.
    let allCommands =
        match testConfigs with
        | Some allConfigs when not allConfigs.IsEmpty ->
            commands
            @ [ "set-scope",
                PluginCommand.Request(fun (ctx: CommandCtx<TestPruneMsg>) (args: string array) ->
                    async {
                        // `fshw confirm` calls this BEFORE triggering its
                        // scan, so the test run the scan provokes is already unfiltered.
                        // It replies once the owner has committed the scope, so the scan
                        // it triggers next is folded against it.
                        let requested =
                            let argStr = if args.Length > 0 then args.[0].Trim() else "{}"

                            try
                                use doc = JsonDocument.Parse(argStr)

                                match doc.RootElement.TryGetProperty("scope") with
                                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                | _ -> "impact"
                            with _ ->
                                "impact"

                        match requested with
                        | "full"
                        | "impact" ->
                            // Its own intent key: a scope change queued behind a running
                            // suite would hold `confirm` for that suite's whole length.
                            do!
                                ctx.EnqueueExclusiveIntent "scope" None (ScopeRequested(TestMode.ofScope requested))
                                |> Async.AwaitTask

                            return JsonSerializer.Serialize({| scope = requested |})
                        | other ->
                            return
                                JsonSerializer.Serialize(
                                    {| error = $"unknown scope '%s{other}' (expected 'full' or 'impact')" |}
                                )
                    })

                "test-scope",
                PluginCommand.Observe(fun (ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
                    async {
                        // What the last completed run ACTUALLY covered — the evidence a
                        // merge verdict is computed from. Never a restatement of what was
                        // requested: `set-scope full` is a request, this is the receipt.
                        // A run still in flight reports `running`, which `confirm` treats
                        // as "no verdict yet" rather than as a scope.
                        //
                        // The reply carries the RUN ID as well, so the CLI can DECLARE
                        // which CTRF reports belong to this run
                        // (`.fshw/test-runs/<runId>/`) instead of inferring membership
                        // from mtimes.
                        // Check again at the read boundary: inputs may have changed
                        // after completion, even before the next watcher event arrives.
                        let receipt =
                            match ctx.IsRunning "tests", state.EvidenceReceipt with
                            | false, Some evidence ->
                                let current = ReceiptInputTree.read repoRoot

                                if ReceiptInputTree.matches evidence.InputTreeHash current then
                                    Some evidence
                                else
                                    None
                            | _ -> None

                        let runId =
                            match receipt with
                            | Some receipt -> box (receipt.RunId.ToString("N"))
                            | None -> null

                        // The scope is a PROJECTION of `LastCoverage` — the very value the
                        // ledger uses to decide what a run is entitled to CLEAR.
                        // See `scopeOf`.
                        let projects = allConfigs |> List.map (fun c -> c.Project)

                        // The change that selected the last run's tests. Sent so a
                        // check that selects NOTHING can still say what the last
                        // change that DID trigger tests was — otherwise that fact
                        // exists only in a daemon log, which is exactly where a
                        // reader will not look before concluding the selector is
                        // broken.
                        //
                        // Truncated on the WIRE, with the full count beside it: a
                        // pathological flush can carry thousands of seeds, and a
                        // reply that grows without bound to serve a diagnostic line
                        // is a new failure mode in the path that earns verdicts.
                        //
                        // A zero-test completion no longer writes a
                        // receipt (`ReceiptTransition`), so the seeds and the zero
                        // reason it used to carry are read from the state the same
                        // completion wrote: `LastSeeds` is the change that last
                        // selected tests, `LastZeroSelection` why this one selected
                        // none. A retained receipt still speaks for itself.
                        let evidenceSeeds =
                            receipt
                            |> Option.map (fun receipt -> receipt.Seeds)
                            |> Option.defaultValue state.LastSeeds

                        let seeds = evidenceSeeds |> List.truncate 8 |> List.toArray
                        let seedCount = List.length evidenceSeeds

                        // Every run this session has completed, newest
                        // first. Sent on EVERY branch — including `running`, which is
                        // what a check reads when it takes its baseline against a busy
                        // daemon, and a baseline that came back silent would hand the
                        // check its predecessor's runs.
                        let runIds =
                            state.CompletedRuns |> List.map (fun id -> id.ToString("N")) |> List.toArray

                        // The full-suite baseline this ledger's greens are
                        // relative to, on EVERY branch — a `running` reply still names the
                        // baseline a later reading will be judged against. `baseline` is
                        // the reference; `baselineAbsent` says why there is none. Both
                        // null only for an analysis-only daemon, which makes no test claim.
                        let baseline, baselineAbsent =
                            match baselineInvalidReason state.Debt, state.Debt.Baseline with
                            | None, Some b when not (Set.isEmpty runnableProjects) ->
                                box
                                    {| runId = b.RunId.ToString("N")
                                       earnedAt = b.EarnedAt.ToString("o")
                                       projects = b.Projects |> Set.toArray |},
                                null
                            | None, _ -> null, null
                            | Some reason, _ -> null, box reason

                        if ctx.IsRunning "tests" then
                            return
                                JsonSerializer.Serialize(
                                    {| scope = "running"
                                       runId = runId
                                       runIds = runIds
                                       baseline = baseline
                                       baselineAbsent = baselineAbsent |}
                                )
                        else
                            let evidenceCoverage =
                                receipt
                                |> Option.map (fun receipt -> receipt.Coverage)
                                |> Option.defaultValue RunCoverage.none

                            match scopeOf projects evidenceCoverage with
                            | ScopeFull n ->
                                return
                                    JsonSerializer.Serialize(
                                        {| scope = "full"
                                           runIds = runIds
                                           baseline = baseline
                                           baselineAbsent = baselineAbsent
                                           ranProjects = n
                                           totalProjects = n
                                           runId = runId
                                           seeds = seeds
                                           seedCount = seedCount |}
                                    )
                            | ScopeFiltered(ran, total) ->
                                return
                                    JsonSerializer.Serialize(
                                        {| scope = "filtered"
                                           runIds = runIds
                                           baseline = baseline
                                           baselineAbsent = baselineAbsent
                                           ranProjects = ran
                                           totalProjects = total
                                           runId = runId
                                           seeds = seeds
                                           seedCount = seedCount |}
                                    )
                            | ScopeNone total ->
                                let zero =
                                    receipt
                                    |> Option.map (fun receipt -> receipt.ZeroSelection)
                                    |> Option.defaultValue state.LastZeroSelection

                                return
                                    JsonSerializer.Serialize(
                                        {| scope = "none"
                                           runIds = runIds
                                           baseline = baseline
                                           baselineAbsent = baselineAbsent
                                           ranProjects = 0
                                           totalProjects = total
                                           runId = runId
                                           seeds = seeds
                                           seedCount = seedCount
                                           noTestsReason =
                                            (match ZeroSelection.token zero with
                                             | Some token -> box token
                                             | None -> null)
                                           uncoveredSymbols =
                                            (ZeroSelection.symbols zero |> List.truncate 8 |> List.toArray)
                                           uncoveredSymbolCount = List.length (ZeroSelection.symbols zero)
                                           // Of the uncovered symbols, how many
                                           // HAVE covering tests this daemon cannot run, and where.
                                           unrunnableSymbolCount = Map.count (ZeroSelection.unrunnable zero)
                                           unrunnableProjects =
                                            (ZeroSelection.unrunnable zero
                                             |> UnrunnableCoverage.projects
                                             |> Set.toArray) |}
                                    )
                    })

                // What `check` WOULD have concluded about the run
                // `confirm` just widened to full — the sample `confirm` used to destroy.
                //
                // A SEPARATE command from `test-scope`, not another field on it. The two
                // answer opposite questions (`test-scope`: what DID run; this: what would
                // NOT have) and `test-scope` is on the path that earns every verdict,
                // where a new field is a new way for the reply to fail to parse. A daemon
                // that has never heard of this command returns the unknown-command
                // sentinel, which the CLI reads as "no sample" — never as agreement.
                "check-reach",
                PluginCommand.Observe(fun (_ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
                    async {
                        match state.CheckReach with
                        | None ->
                            return
                                JsonSerializer.Serialize(
                                    {| recorded = false
                                       reason =
                                        "no test run has completed in this session, so there is no result to project" |}
                                )
                        | Some(runId, wouldHaveRun, reach, recall) ->
                            let projects = allConfigs |> List.map (fun c -> c.Project)

                            let scope, ranProjects, totalProjects =
                                match wouldHaveRun |> Option.map (scopeOfSelection projects) with
                                | Some(ScopeFull n) -> box "full", n, n
                                | Some(ScopeFiltered(ran, total)) -> box "filtered", ran, total
                                | Some(ScopeNone total) -> box "none", 0, total
                                // No retained selection ⇒ no scope to describe. `null`,
                                // never a count: a zero here would read as "check would
                                // have run nothing", which is a claim, and this is an
                                // absence.
                                | None -> null, 0, List.length projects

                            let failingSuites =
                                match reach with
                                | ReachedAFailure ps -> List.toArray ps
                                | ReachedNoFailure _
                                | NoFailuresToReach
                                | ReachUnknown _ -> [||]

                            let missed =
                                match reach with
                                | ReachedNoFailure failures ->
                                    failures
                                    |> List.map (fun failure ->
                                        {| project = failure.Project
                                           ``class`` = failure.Class
                                           cause =
                                            (match failure.Cause with
                                             | MissCause.ProjectNotSelected -> "project-not-selected"
                                             | MissCause.ClassNotInFilter -> "class-not-in-filter") |})
                                    |> List.toArray
                                | _ -> [||]

                            let reason =
                                match reach with
                                | ReachUnknown r -> box r
                                | ReachedAFailure _
                                | ReachedNoFailure _
                                | NoFailuresToReach -> null

                            let (recallMeasured,
                                 recallReached,
                                 recallTotal,
                                 recallThreshold,
                                 recallAcceptable,
                                 recallReason) =
                                match recall with
                                | RecallMeasured(reached, total, threshold, acceptable) ->
                                    true, reached, total, threshold, box acceptable, null
                                | RecallNotMeasurable why -> false, 0, 0, 1.0, null, box why

                            return
                                JsonSerializer.Serialize(
                                    {| recorded = true
                                       runId = runId.ToString("N")
                                       scope = scope
                                       ranProjects = ranProjects
                                       totalProjects = totalProjects
                                       reach = CheckReach.token reach
                                       failingSuites = failingSuites
                                       missed = missed
                                       reason = reason
                                       conditionalFailureRecall =
                                        {| measured = recallMeasured
                                           reached = recallReached
                                           total = recallTotal
                                           threshold = recallThreshold
                                           acceptable = recallAcceptable
                                           reason = recallReason |} |}
                                )
                    })

                "run-tests",
                PluginCommand.Request(fun (ctx: CommandCtx<TestPruneMsg>) (args: string array) ->
                    async {
                        // FORCE semantics: `test-rerun` is the explicit "prove it
                        // ran" verb. The run NEVER executes here — it is an intent on
                        // the "tests" key, queued behind the run in flight (see
                        // `RunTestsRequested`). A force-run is owed work, never
                        // refused. The only thing bounded here is the WAIT:
                        // `waitSec` caps queue time plus run time, and on expiry
                        // this reports a DISTINCT `busy` status so the CLI exits
                        // non-zero rather than reporting a verdict no run produced.
                        try
                            let argStr = if args.Length > 0 then args.[0].Trim() else "{}"
                            let waitForResultMs = parseRunTestsWaitMs argStr DefaultRunTestsWaitMs

                            let parseResult =
                                try
                                    Ok(JsonDocument.Parse(argStr))
                                with ex ->
                                    Error ex.Message

                            match parseResult with
                            | Error msg -> return JsonSerializer.Serialize({| error = $"invalid JSON: %s{msg}" |})
                            | Ok doc ->

                                use doc = doc
                                let root = doc.RootElement

                                let filter =
                                    match root.TryGetProperty("filter") with
                                    | true, v -> Some(v.GetString())
                                    | false, _ -> None

                                let onlyFailed =
                                    match root.TryGetProperty("only-failed") with
                                    | true, v -> v.GetBoolean()
                                    | false, _ -> false

                                let projectFilter =
                                    match root.TryGetProperty("projects") with
                                    | true, v ->
                                        v.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq |> Some
                                    | false, _ -> None

                                // `--only-failed` depends on what failed, which is plugin state. A
                                // request never reads state, so the choice is made by `Update`
                                // against the state it folds the request into.
                                let selection =
                                    if onlyFailed then
                                        None
                                    else
                                        match projectFilter with
                                        | Some names ->
                                            Some(allConfigs |> List.filter (fun c -> names.Contains(c.Project)))
                                        | None -> Some allConfigs

                                match selection with
                                | Some configs when configs.IsEmpty ->
                                    // Name what was asked for and what exists. A bare
                                    // "no matching test projects" is unactionable for the
                                    // one case that actually produces it — a mistyped or
                                    // renamed `--project` — and the configured names are
                                    // right here.
                                    let msg =
                                        match projectFilter with
                                        | Some names ->
                                            let asked = names |> Set.toList |> List.sort |> String.concat ", "

                                            let known =
                                                allConfigs
                                                |> List.map (fun c -> c.Project)
                                                |> List.sort
                                                |> String.concat ", "

                                            $"no test project matches --project %s{asked}. Configured test projects: %s{known}"
                                        | None -> "no matching test projects"

                                    return JsonSerializer.Serialize({| error = msg |})
                                | _ ->
                                    let reply =
                                        Tasks.TaskCompletionSource<string>(
                                            Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                                        )

                                    // An intent on the "tests" key: it waits behind the run
                                    // in flight, owned, and launches from the fold it
                                    // becomes. A receipt that fails means no fold will
                                    // launch it, so the wait below ends with that failure.
                                    let request =
                                        match selection with
                                        | Some configs -> RunTestsRequested(configs, filter, reply)
                                        | None -> RunFailedTestsRequested(filter, reply)

                                    let receipt = ctx.EnqueueExclusiveIntent "tests" None request

                                    receipt.ContinueWith(
                                        (fun (admitted: Tasks.Task) ->
                                            if admitted.IsFaulted then
                                                reply.TrySetResult(
                                                    JsonSerializer.Serialize(
                                                        {| error = admitted.Exception.GetBaseException().Message |}
                                                    )
                                                )
                                                |> ignore),
                                        Tasks.TaskContinuationOptions.ExecuteSynchronously
                                    )
                                    |> ignore

                                    // Bounded await: the reply resolves
                                    // when the run finishes — behind the test-prune
                                    // mailbox and possibly behind a run already in
                                    // flight — so an unbounded wait here could pin the
                                    // IPC caller for as long as the daemon is wedged.
                                    let! winner =
                                        Tasks.Task.WhenAny(reply.Task, Tasks.Task.Delay(waitForResultMs))
                                        |> Async.AwaitTask

                                    if winner = (reply.Task :> Tasks.Task) then
                                        return reply.Task.Result
                                    else
                                        return
                                            JsonSerializer.Serialize(
                                                {| status = "busy"
                                                   message =
                                                    $"the test run did not produce a result within %d{waitForResultMs / 1000}s (still queued or running); retry, or raise --wait-sec" |}
                                            )
                        with ex ->
                            // Command-local faults only (the run itself executes in
                            // the mailbox-launched work, which owns run faults per
                            // and always resolves the reply + posts
                            // the Aborted lifecycle). Nothing to post here: no run
                            // was launched.
                            Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                            return JsonSerializer.Serialize({| error = ex.Message |})
                    }) ]
        | _ -> commands

    /// Owe one impact run after whatever holds the "tests" key. Coalesced: however many
    /// triggers arrive while the key is held, one run is queued, and it decides against
    /// the state it is delivered into (`impactRun`).
    let enqueueImpactRun (ctx: PluginCtx<TestPruneMsg>) =
        ctx.EnqueueExclusiveIntent "tests" (Some "impact") ImpactRunRequested |> ignore

    /// What a launch from `inputs` will execute: the widenings `runTestsWithImpact`
    /// applies to every project.
    let launchScopeOf (inputs: TestRunInputs) =
        if
            TestMode.requestsFullSuite inputs.Mode
            || inputs.Debt.RecoveryOutstanding
            || Option.isSome (baselineInvalidReason inputs.Debt)
        then
            LaunchedFullSuite
        else
            LaunchedSelection

    /// Claim the "tests" key for an impact launch from `launchState`. `Some` carries the
    /// state recording what the claimed run will execute; `None` means the key is held.
    let launchImpactRun
        (ctx: PluginCtx<TestPruneMsg>)
        (configs: TestConfig list)
        (launchState: TestPruneState)
        (hasCachedResults: bool)
        (fanout: Set<string>)
        =
        let inputs = TestRunInputs.ofState launchState

        match runTestHostExclusive ctx fanout None (runTestsWithImpact ctx configs inputs hasCachedResults fanout) with
        | Claimed ->
            Some
                { launchState with
                    InFlight =
                        Some
                            { Scope = launchScopeOf inputs
                              Mode = inputs.Mode } }
        | SlotBusy -> None

    /// Whether debt found while the key is held joins the run holding it instead of
    /// queueing another. Only a full-suite run can take it. Under pass-through it takes
    /// every arrival, since no run may follow it. Under impact selection it takes a
    /// BootScan cohort, which scanned the tree that run is testing; an in-session cohort
    /// is an edit the run never built, and queues its own run.
    let joinsFullRun (state: TestPruneState) (bootScan: bool) =
        (state.InFlight |> Option.exists (fun run -> run.Scope = LaunchedFullSuite))
        && (TestMode.skips RerunIntents state.Mode || bootScan)

    /// Attach what is owed to the full run in flight. The FIRST captured revision stands:
    /// a later attach must not advance a symbol past an edit the held run never built.
    let attachToFullRun (state: TestPruneState) =
        { state with
            DebtDuringFullRun =
                (state.DebtDuringFullRun, state.Debt.PendingQueue)
                ||> Set.fold (fun captured symbol ->
                    if Map.containsKey symbol captured then
                        captured
                    else
                        Map.add symbol (revisionOf state.Debt symbol) captured) }

    /// The run in flight has concluded: forget it, and end the pass-through it ran for.
    let endRun (state: TestPruneState) =
        { state with
            InFlight = None
            Mode =
                match state.InFlight with
                | Some run -> TestMode.afterRun run.Mode state.Mode
                | None -> state.Mode }

    /// A launch that found its artifacts or test host unavailable ran nothing: it revokes
    /// the receipt, hands back the fanout it consumed, and fails.
    let unavailableRun
        (ctx: PluginCtx<TestPruneMsg>)
        (state: TestPruneState)
        (message: string)
        (owed: Set<string>)
        (reply: Tasks.TaskCompletionSource<string> option)
        =
        ctx.ReportStatus(PluginStatus.failedNow message message TimeSpan.Zero)

        { endRun state with
            EvidenceReceipt = None
            PendingForceRunProjects = Set.union state.PendingForceRunProjects owed
            Replies =
                reply
                |> Option.map (fun target -> target, JsonSerializer.Serialize {| error = message |})
                |> Option.toList }

    // Launched from the mailbox so it is serialised with every other launch site and
    // holds the "tests" key for its whole duration — see the `RunTestsRequested` case
    // for why that matters.
    //
    // Cooperative-safe: a force-run exists only for the client that asked for it, so the
    // framework may cancel it once that client is gone. Its test hosts run in its own
    // process scope (reaped), its result publishes only through the fold it returns
    // (never folded when cancelled), and its `finally` closes the run it opened.
    let requestTestRun (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) configs filter reply =
        let work =
            PluginWork.cooperativeSafe (commandForceRun ctx state.Mode configs filter reply)

        match runTestHostExclusive ctx Set.empty (Some reply) work with
        | Claimed ->
            { state with
                EvidenceReceipt = None
                InFlight =
                    Some
                        { Scope = LaunchedSelection
                          Mode = state.Mode } }
        | SlotBusy ->
            // A busy key QUEUES the run, never refuses it: a refusal that reads as
            // success is a vacuous green. The intent waits behind the holder, owned, and
            // the IPC command bounds its own wait on `reply`.
            ctx.Log "  ↳ queued run-tests force-run (tests already running)"

            ctx.EnqueueExclusiveIntent "tests" None (RunTestsRequested(configs, filter, reply))
            |> ignore

            state

    /// The owed run an `ImpactRunRequested` intent stands for, decided against the state it
    /// is delivered into. Flush first, so changes that landed while the previous run held
    /// the key are selected; then launch only if something is still owed. An intent is
    /// queued while a run is in flight, and that run may have discharged exactly the debt
    /// that queued it: launching blindly would produce a zero-project run whose result
    /// erases the passing evidence.
    let impactRun (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        match testConfigs with
        | Some configs when not configs.IsEmpty ->
            match
                (try
                    Ok(flushAndQueryAffectedBounded ctx state)
                 with ex ->
                     Error ex)
            with
            | Error ex ->
                Logging.error "test-prune" $"flushAndQueryAffected (rerun) failed: %s{ex.Message}"
                tryRepairSchemaDrift ex
                ctx.ReportStatus(PluginStatus.failedNow ex.Message $"rerun flush failed: %s{ex.Message}" TimeSpan.Zero)
                state
            | Ok rerunState when nothingOwed rerunState.Debt && Set.isEmpty rerunState.PendingForceRunProjects ->
                Logging.info
                    "test-prune"
                    "Queued impact rerun is stale — the completed run cleared all verification debt and no dependency fanout remains"

                rerunState
            | Ok rerunState ->
                Logging.info "test-prune" "Re-running tests (queued during previous run)"
                let fanout = rerunState.PendingForceRunProjects

                let launchState =
                    { rerunState with
                        PendingForceRunProjects = Set.empty }

                match launchImpactRun ctx configs launchState rerunState.LastResults.IsSome fanout with
                | Some launched -> launched
                | None when joinsFullRun rerunState false -> attachToFullRun rerunState
                | None ->
                    enqueueImpactRun ctx
                    rerunState
        | _ -> state

    /// Make a candidate's durable debt the record a restart reads, then publish it.
    ///
    /// The marker goes first and comes off only in `Finalize`, after the candidate is
    /// published: a restart that finds it cannot know which sidecars describe a published
    /// state, and treats what is owed as unknown. Only what changed is written, unless an
    /// earlier publication left the marker, in which case every sidecar is rewritten from
    /// this candidate. While the debt is unknown nothing is written: the unreadable
    /// record is the honest one until a full suite recovers it.
    ///
    /// Replies owed by the candidate are resolved last, so a command never acknowledges an
    /// outcome that is not published.
    let prepareCommit (prior: TestPruneState) (candidate: TestPruneState) =
        async {
            let changed (read: TestPruneState -> 'T) =
                not (obj.ReferenceEquals(read prior, read candidate))
                && read prior <> read candidate

            let queueChanged = changed (fun state -> state.Debt.PendingQueue)
            let failuresChanged = changed (fun state -> state.OutstandingFailures)
            let obligationsChanged = changed (fun state -> state.Debt.RuntimeObligations)
            let baselineChanged = changed (fun state -> state.Debt.Baseline)
            let recoveryChanged = changed (fun state -> state.Debt.RecoveryOutstanding)
            let interrupted = File.Exists debtPublicationPath

            if
                interrupted
                || queueChanged
                || failuresChanged
                || obligationsChanged
                || baselineChanged
                || recoveryChanged
            then
                FsHwPaths.atomicWriteAllText debtPublicationPath "verification debt publication in progress"

                if not candidate.Debt.RecoveryOutstanding then
                    let rewrite = interrupted || recoveryChanged

                    if rewrite || queueChanged then
                        PendingVerification.save repoRoot candidate.Debt.PendingQueue

                    if rewrite || failuresChanged then
                        OutstandingFailure.save repoRoot candidate.OutstandingFailures

                    if rewrite || obligationsChanged then
                        saveRuntimeCoverageObligations repoRoot candidate.Debt.RuntimeObligations

                    match candidate.Debt.Baseline with
                    | Some baseline when rewrite || baselineChanged -> FullSuiteBaseline.save repoRoot baseline
                    | _ -> ()

            return
                { Finalize =
                    async {
                        if not candidate.Debt.RecoveryOutstanding then
                            // The runtime marker is written by a run before its failure
                            // reaches this owner, so only the state that recovered from
                            // unknown debt may remove it.
                            let markers =
                                if recoveryChanged then
                                    [ runtimeCoverageRecoveryPath repoRoot; debtPublicationPath ]
                                else
                                    [ debtPublicationPath ]

                            // `File.Delete` of an absent file is a no-op, but not in an
                            // absent directory: a repository that never wrote debt has none.
                            for marker in markers do
                                if File.Exists marker then
                                    File.Delete marker

                        for reply, response in candidate.Replies do
                            reply.TrySetResult response |> ignore
                    } }
        }

    { Name = PluginName.create FsHotWatch.PluginActivity.TestPrunePluginName
      Init = initialState
      Update =
        fun ctx state event ->
            async {
                // Replies belong to the event that owes them; the previous event's were
                // resolved when its state was published.
                let state = { state with Replies = [] }
                let modelGeneration = observeModelGeneration ctx

                let state =
                    if
                        state.AnalysisModelGeneration <> modelGeneration
                        && not state.PendingAnalysis.IsEmpty
                    then
                        // Pending compiler results belong to the model they were accepted
                        // under. Retire them before a flush can write them into another
                        // model's symbol index. Their changed symbols stay owed, and the
                        // index can no longer vouch for what covers them, so every runnable
                        // project owes a run.
                        Logging.info
                            "test-prune"
                            $"project model changed (%A{state.AnalysisModelGeneration} -> %A{modelGeneration}); retiring %d{state.PendingAnalysis.Count} project(s) of pending analysis"

                        { state with
                            PendingAnalysis = Map.empty
                            AnalysisModelGeneration = modelGeneration
                            AnalysisFiles = Map.empty
                            AnalysisReceipt = None
                            PendingForceRunProjects = Set.union state.PendingForceRunProjects runnableProjects }
                    else
                        state

                // Analysis folds only a result or seal published against the model this host
                // publishes now. One from a replaced model was admitted before the
                // replacement, and its cohort is retried against the current model. One
                // with no model was captured against none, so it cannot describe this one.
                let notCurrent (published: int64 option) =
                    published.IsNone || published <> modelGeneration

                match event with
                | PluginEvent.FileChecked result when notCurrent result.ModelGeneration ->
                    Logging.debug
                        "test-prune"
                        $"ignoring FileChecked for %s{AbsFilePath.value result.File} from model %A{result.ModelGeneration}; current %A{modelGeneration}"

                    return state
                | PluginEvent.BatchChecked batch when notCurrent batch.ModelGeneration ->
                    Logging.debug
                        "test-prune"
                        $"ignoring BatchChecked from model %A{batch.ModelGeneration}; current %A{modelGeneration}"

                    return state
                | PluginEvent.FileChecked result ->
                    let analysisStarted = DateTime.UtcNow
                    let fileStr = AbsFilePath.value result.File
                    let relPath = Path.GetRelativePath(repoRoot, fileStr).Replace('\\', '/')

                    // The ONE treatment for a file whose symbol analysis
                    // failed (an `analyzeSourceFromResults` Error and a handler fault are the same
                    // condition). The file is REMEMBERED as unanalysable, with three
                    // consequences, none silent:
                    //   1. a WARNING lands in the error ledger, keyed to the file, so
                    //      `fshw check` prints it and — under the default warn-fail
                    //      policy — refuses a green verdict;
                    //   2. `runTestsWithImpact` falls back to EVERY test project in full
                    //      while the set is non-empty;
                    //   3. the non-empty force-run set disables the zero-affected skip
                    //      gate, so this can never end as "0 affected — green, 0 ran".
                    // The file leaves the set the moment it analyses cleanly.
                    //
                    // The Failed stamp needs no idle guard: the framework's ReportStatus
                    // funnel drops any terminal stamped while an exclusive run is in
                    // flight (the run owns the status), so a mid-run analysis failure
                    // cannot manufacture a terminal. The ledger entry and the
                    // force-full-suite consequence persist either way.
                    let markUnanalysable (reason: string) (detail: string) (logDetail: string) : TestPruneState =
                        match FileFreshness.resolvePresence repoRoot relPath with
                        | FileFreshness.Gone ->
                            // A path that no longer exists is GONE, not a
                            // file that failed analysis: there is no source to be invisible
                            // and no parse error to fix. Warning here named files a merge had
                            // deleted, and the warning denied the gate its green. Forget the
                            // file everywhere this plugin remembers it instead.
                            Logging.info
                                "test-prune"
                                $"%s{relPath} no longer exists; dropping it rather than reporting '%s{reason}' (%s{detail})"

                            updateFreshness (
                                FileFreshness.stamp
                                    FileFreshness.Gone
                                    false
                                    DateTime.UtcNow
                                    relPath
                                    (Volatile.Read(&freshnessRef))
                            )

                            { state with
                                AnalysisFiles = Map.remove result.File state.AnalysisFiles
                                UnanalyzableFiles = Map.remove relPath state.UnanalyzableFiles }
                        | FileFreshness.Present ->
                            Logging.error
                                "test-prune"
                                $"%s{reason} for %s{relPath}: %s{logDetail} — this file is INVISIBLE to the impact graph (no symbols), so every test project will be run in full until it analyses cleanly"

                            ctx.ReportErrors fileStr [ unanalyzableFileDiagnostic relPath detail ]

                            ctx.ReportStatus(
                                PluginStatus.Failed(
                                    $"%s{reason}: %s{detail}",
                                    DateTime.UtcNow,
                                    RunVerdict.create $"%s{reason}: %s{detail}" (DateTime.UtcNow - analysisStarted)
                                )
                            )

                            { state with
                                AnalysisFiles =
                                    Map.add
                                        result.File
                                        (AnalysisFileEvidence.fromResult result (Error detail))
                                        state.AnalysisFiles
                                UnanalyzableFiles =
                                    Map.add
                                        relPath
                                        { RelPath = relPath
                                          File = fileStr
                                          Reason = detail }
                                        state.UnanalyzableFiles }

                    try
                        // Canonical project identity. For real .fsproj files, FCS
                        // gives "MyProject.fsproj" → "MyProject". For .fsx scripts
                        // FCS synthesizes "Lib.fsx.fsproj" → "Lib.fsx" after one
                        // strip; drop the trailing ".fsx" so config that specifies
                        // `"Lib"` matches both cases.
                        let projectName =
                            match result.CheckResults with
                            | ParseOnly ->
                                // Analysis below fails explicitly before this identity is
                                // consumed. Do not inspect ProjectOptions: an aborted FCS
                                // check may not carry usable options either.
                                ""
                            | FullCheck _ ->
                                let raw = result.ProjectOptions.ProjectFileName |> Path.GetFileNameWithoutExtension

                                if raw.EndsWith(".fsx") then
                                    raw.Substring(0, raw.Length - 4)
                                else
                                    raw

                        // The per-file freshness sidecar gates the `detectChanges` call
                        // site, not the symbol-DB write: dirty FCS results are still
                        // persisted (cold-scan rows must go in), and the sidecar records
                        // `fcsClean = false` so a cross-restart replay treats those rows
                        // as untrusted-for-diff. The next clean recheck overwrites the
                        // rows and flips the sidecar back.
                        let currentClean =
                            not (hasFcsErrors ctx.FcsSuppressedCodes result.Source result.CheckResults)

                        let storedFreshness =
                            let store = Volatile.Read(&freshnessRef)
                            FileFreshness.classify relPath store

                        if not currentClean then
                            let errCount =
                                fcsErrorCount ctx.FcsSuppressedCodes result.Source result.CheckResults

                            Logging.warn
                                "test-prune"
                                $"FCS reported %d{errCount} error(s) for %s{relPath}; persisting symbols but marking file dirty in freshness sidecar (Phase B detectChanges will fall back for this file)"

                        // FileChecked is the completion payload from the daemon's warm FCS
                        // pass. Re-entering FSharpChecker here duplicated that expensive
                        // work and could observe a different compiler state than the event
                        // being handled. TestPrune 8.1 accepts the existing parse/check
                        // results directly. ParseOnly is not analysable: spelling that as
                        // Error keeps the established fail-closed full-suite fallback.
                        // Checked under a virtual root, the results name the file and
                        // every symbol by virtual path: analysis asks for the file by that
                        // name, and repository-relative paths come from the virtual root,
                        // the same in every worktree.
                        let analysisRoot =
                            result.Frame
                            |> Option.fold (fun _ (frame: PathFrame.PathFrame) -> frame.Virtual) repoRoot

                        let analysisFile = PathFrame.nameIn result.Frame fileStr

                        let analysisResult =
                            match result.CheckResults with
                            | FullCheck checkResults ->
                                analyzeSourceFromResults
                                    analysisFile
                                    result.Source
                                    result.ParseResults
                                    checkResults
                                    projectName
                            | ParseOnly ->
                                Error
                                    "FCS produced parse-only results; full type-check results are required for impact analysis"

                        match analysisResult with
                        | Ok analysisResult ->
                            let normalizedSymbols = normalizeSymbolPaths analysisRoot analysisResult.Symbols

                            let fileAnalysis =
                                { Symbols = normalizedSymbols
                                  Dependencies = analysisResult.Dependencies
                                  TestMethods =
                                    analysisResult.TestMethods
                                    |> List.map (fun t -> { t with TestProject = projectName })
                                  Attributes = analysisResult.Attributes
                                  ParentLinks = analysisResult.ParentLinks
                                  Diagnostics = analysisResult.Diagnostics }

                            // Read stored symbols from the in-memory snapshot (populated after
                            // each flush). Falls back to DB for warm starts where the snapshot
                            // hasn't been populated yet.
                            let storedSymbols =
                                match Map.tryFind relPath state.SymbolSnapshot with
                                | Some symbols -> symbols
                                | None -> db.GetSymbolsInFile(relPath)

                            // WHOSE rows are these? `storedSymbols` is
                            // non-empty both when a prior run indexed this file and when
                            // THIS run did, seconds ago — and those mean opposite things.
                            // The ledger remembers the first look, which is necessarily
                            // taken before this session wrote anything for the file, so
                            // it survives the snapshot short-circuit above.
                            let storedRows = priorRows.Classify(relPath, not storedSymbols.IsEmpty)

                            // Accumulate per-project; flush on BuildCompleted.
                            // Replace any prior analysis for this file to avoid double-counting
                            // when a file is checked more than once before the flush (e.g. initial
                            // scan followed by a file-change recheck).
                            let existingForProject =
                                state.PendingAnalysis |> Map.tryFind projectName |> Option.defaultValue []

                            let filteredExisting =
                                existingForProject
                                |> List.filter (fun a ->
                                    not (a.Symbols |> List.exists (fun s -> s.SourceFile = relPath)))

                            let newPending =
                                state.PendingAnalysis
                                |> Map.add projectName (filteredExisting @ [ fileAnalysis ])

                            // Can the stored rows be diffed against? The CURRENT
                            // extraction must be FCS-clean (a dirty current result means
                            // the just-extracted symbols are themselves suspect); given
                            // that, `FileFreshness.trustStoredRows` decides, from the
                            // sidecar's verdict plus what the index HOLDS for this file
                            // and SINCE WHEN. Every arm of that pair is load-bearing and
                            // all are documented there —
                            // `EverySymbolIsNew`, which is what a `Clean` stamp means once
                            // a schema recreate has emptied the index underneath it, and
                            // `RowsFromThisRun`, which is what rows mean
                            // when this run is the one that wrote them.
                            let storedTrust = FileFreshness.trustStoredRows storedFreshness storedRows

                            // `planLook` is the whole decision. Its only way of
                            // contributing nothing is the one that HIDES a file's changes,
                            // and it goes out at warn. It replaced a `(names, bool)` pair
                            // whose bool this call site computed and then `ignore`d.
                            let changedNames =
                                match FileFreshness.planLook currentClean storedTrust with
                                | FileFreshness.Diffable baseline ->
                                    // WHAT to diff against is the plan's decision, not
                                    // `storedSymbols`'s. This call site used to pass the
                                    // stored rows for BOTH diffable arms, which made
                                    // `EverySymbolIsNew` behave exactly like
                                    // `DiffAgainstStored` — and whenever the rows were
                                    // this run's own, that is the self-comparison
                                    // the `RowsFromThisRun` clock was introduced to replace.
                                    // A self-comparison reports zero changes every time.
                                    let priorSymbols = FileFreshness.baselineRows baseline storedSymbols

                                    let (changes, _events) = detectChanges normalizedSymbols priorSymbols

                                    // A trustworthy extraction has now
                                    // been taken AND consumed, so the rows it leaves
                                    // behind are a real "before" for the next look —
                                    // which is what keeps a missing baseline costing ONE
                                    // widening per file rather than one on every save.
                                    // Marked only here: the bypass arms below discard
                                    // their extraction, and a baseline they never
                                    // established must not be claimed.
                                    priorRows.MarkBaselineEstablished relPath

                                    Logging.info
                                        "test-prune"
                                        $"detectChanges for %s{relPath} (stored=%A{storedFreshness}, rows=%A{storedRows}, trust=%A{storedTrust}, baseline=%A{baseline}): %d{changes.Length} changes, %d{priorSymbols.Length} diffed against, %d{normalizedSymbols.Length} current"

                                    changedSymbolNames changes
                                | FileFreshness.FileUnverified ->
                                    // The one arm that DROPS a file's changes. At warn,
                                    // and stating the consequence rather than the
                                    // mechanism, because "detectChanges bypassed" at info
                                    // is what this defect looked like for three runs.
                                    Logging.warn
                                        "test-prune"
                                        $"NOT SELECTED: %s{relPath} changed but FCS reported errors for it on this check, so its symbols may be partial and no tests were selected from it (stored=%A{storedFreshness}, rows=%A{storedRows}, storedRowCount=%d{storedSymbols.Length}). The next FCS-clean check of this file widens it back in."

                                    []

                            let newChangedSymbols, newDebt =
                                if not changedNames.IsEmpty then
                                    // These feed straight into `enqueuePending`, so this
                                    // line is the primary evidence when diagnosing over-
                                    // or under-selection. `describeMany`, not `%A` —
                                    // the count must be exact and uncapped.
                                    Logging.info "test-prune" $"Changed symbols: %s{describeMany changedNames}"

                                    // Queued at the SAME point the in-memory hot view
                                    // accumulates, and written durably before the BatchChecked
                                    // analysis flush advances the symbol snapshot. They leave
                                    // the queue only when a covering test run passes
                                    // (TestsFinished) or they prove to have no covering test
                                    // (flushAndQueryAffected).
                                    (state.ChangedSymbols @ changedNames) |> List.distinct,
                                    enqueuePending changedNames state.Debt
                                else
                                    state.ChangedSymbols, state.Debt

                            // Only track file as changed if its AST actually changed.
                            // Comment-only changes produce the same symbol hashes, so they
                            // should not trigger extension-based tests (e.g. Falco routes).
                            let newChangedFiles =
                                let runtimeRelevantChange = not changedNames.IsEmpty || fileAnalysis.Symbols.IsEmpty

                                if runtimeRelevantChange && not (state.ChangedFiles |> List.contains relPath) then
                                    relPath :: state.ChangedFiles
                                else
                                    state.ChangedFiles

                            // Update class→file mapping for test methods found in this file
                            let newClassFiles =
                                fileAnalysis.TestMethods
                                |> List.fold (fun acc t -> Map.add t.TestClass fileStr acc) state.TestClassFiles

                            // `AffectedTests` is set exclusively by `flushAndQueryAffected`
                            // on BuildCompleted and consumed by `runTestsWithImpact`; the
                            // `affected-tests` IPC command computes its own answer on
                            // demand from `ChangedSymbols` against the current DB.
                            let newState =
                                { state with
                                    Debt = newDebt
                                    ChangedFiles = newChangedFiles
                                    PendingAnalysis = newPending
                                    AnalysisModelGeneration = modelGeneration
                                    AnalysisFiles =
                                        Map.add
                                            result.File
                                            (AnalysisFileEvidence.fromResult result (Ok()))
                                            state.AnalysisFiles
                                    ChangedSymbols = newChangedSymbols
                                    TestClassFiles = newClassFiles
                                    // The file analysed cleanly, so it is back in the impact
                                    // graph and no longer owes the coarse fallback. The
                                    // framework already cleared this plugin's ledger entries
                                    // for the file when the FileChecked arrived, and dropping
                                    // it here means the next test run's ledger rewrite stops
                                    // re-reporting the warning: the entry
                                    // leaves the ledger because the CONDITION cleared, which
                                    // is the only reason any entry may leave it.
                                    UnanalyzableFiles = Map.remove relPath state.UnanalyzableFiles }

                            // Stamp the freshness sidecar with the result of THIS check.
                            // After analysis, not at the top, so a failed `analyzeSourceFromResults`
                            // cannot lock in a clean stamp for a file we have no symbols
                            // for. `markClean` only with a BuildCompleted this session
                            // (see `BuildCompletedInThisSession`) AND a clean FCS result;
                            // otherwise `markUnverified`, which will not downgrade a
                            // previously-clean entry to dirty.
                            let now = DateTime.UtcNow

                            // A check of a path that has since vanished
                            // forgets the record rather than re-stamping it — see
                            // `FileFreshness.stamp`.
                            let updatedFreshness =
                                FileFreshness.stamp
                                    (FileFreshness.resolvePresence repoRoot relPath)
                                    (currentClean && state.BuildCompletedInThisSession)
                                    now
                                    relPath
                                    (Volatile.Read(&freshnessRef))

                            updateFreshness updatedFreshness

                            // A helper analysis is not new evidence about a test red. Keep
                            // the fresh run's terminal provenance until a covering run
                            // clears the outstanding failure.
                            let analysisFinished = DateTime.UtcNow

                            if List.isEmpty state.OutstandingFailures then
                                ctx.ReportStatus(
                                    Completed(
                                        analysisFinished,
                                        RunVerdict.create
                                            $"symbol analysis: %s{relPath}, %d{List.length changedNames} changed symbol(s); no run due"
                                            (analysisFinished - analysisStarted)
                                    )
                                )

                            return newState
                        | Error msg ->
                            // On analysis failure the file must NOT be dropped: a
                            // dropped file contributes no symbols, a change to it diffs
                            // against nothing and selects NO tests, and the check reports
                            // green having run nothing relevant. Silent under-selection:
                            // the one failure mode a test-impact tool must not have. See
                            // `markUnanalysable` for the treatment.
                            return markUnanalysable "Analysis failed" msg msg

                    with ex ->
                        // A fault ANYWHERE in this handler — not just an `analyzeSourceFromResults`
                        // Error — leaves the file unanalysed, the same condition and so
                        // the same treatment.
                        return markUnanalysable "FileChecked handler failed" ex.Message (ex.ToString())

                | PluginEvent.BatchChecked batch ->
                    // Cohort-complete flush. The mailbox is FIFO and the daemon emits
                    // BatchChecked strictly after the last FileChecked, so every
                    // FileChecked from this cohort is already folded into
                    // `ChangedSymbols`/`PendingAnalysis` by now.
                    //
                    // This is the canonical DB persistence point, NOT BuildCompleted. On a
                    // cold scan `Daemon.performScan` awaits BuildPlugin terminal BEFORE
                    // the FCS tier checks, so BuildCompleted reaches this mailbox before
                    // any FileChecked — a BuildCompleted-only flush would always fire
                    // against an empty `PendingAnalysis` and leave the symbol DB
                    // permanently empty. BuildCompleted's flush stays as an idempotent
                    // re-run plus the test-trigger.
                    let flushed =
                        try
                            Ok(flushAndQueryAffectedBounded ctx state)
                        with ex ->
                            Error ex

                    match flushed with
                    | Error ex ->
                        Logging.error "test-prune" $"BatchChecked flushAndQueryAffected failed: %s{ex.Message}"
                        tryRepairSchemaDrift ex
                        return state
                    | Ok flushedState ->
                        // The seal is the moment an analysis-only daemon has an answer: every
                        // checkable file of this model either analysed or did not. A daemon
                        // that runs tests earns test evidence instead, and mints none here
                        // (`AnalysisEvidence.fromCompleted` refuses when tests are configured).
                        let flushedState =
                            // A seal that re-discovered the model on the scoped path names
                            // the files it did not re-check because their inputs did not
                            // change. Their outcomes stand for this model.
                            let analysisFiles =
                                match batch.Retained, modelGeneration with
                                | Some carried, Some generation ->
                                    AnalysisFileEvidence.carryInto generation carried flushedState.AnalysisFiles
                                | _ -> flushedState.AnalysisFiles

                            let membership =
                                match ctx.ProjectGraph.ObserveCheckableFiles() with
                                | Some(generation, files) when Some generation = modelGeneration -> Some files
                                | Some _
                                | None -> None

                            { flushedState with
                                AnalysisFiles = analysisFiles
                                AnalysisReceipt =
                                    membership
                                    |> Option.bind (fun files ->
                                        AnalysisEvidence.fromCompleted
                                            modelGeneration
                                            files
                                            runnableProjects
                                            analysisFiles) }

                        // ── DRAIN THE PENDING QUEUE ────────────────
                        // The cohort seal is the first moment this scan's symbols are
                        // known. `BuildCompleted` cannot be the only test trigger: on a
                        // scan it fires BEFORE the FCS pass, so scan-discovered symbols
                        // would never be verified by the run it launched and would
                        // accumulate silently while `check` reported a stale terminal
                        // status. If symbols remain unverified here, RUN the tests that
                        // verify them — a verdict is only ever earned by a run.
                        //
                        // The skip asks `nothingOwed`, not `Set.isEmpty`: an UNREADABLE
                        // ledger leaves the in-memory queue empty because we cannot name
                        // what it held, and reading that as "nothing to drain" lets a
                        // corrupt sidecar run ZERO tests and still go green.
                        if nothingOwed flushedState.Debt then
                            return flushedState
                        else
                            match testConfigs with
                            | Some configs when not configs.IsEmpty ->
                                // Drain UNCONDITIONALLY when the slot is free. Never defer
                                // to "the BuildCompleted that is surely coming": several
                                // `FileChanged` shapes never produce one at all
                                // (BuildPlugin ignores `SolutionChanged`, and DROPS a
                                // FileChanged arriving while a build is running), so the
                                // deferral can wait forever. CI caught exactly that — 3
                                // symbols deferred to a BuildCompleted that never came,
                                // zero tests run, exit 0.
                                //
                                // Draining on a half-built tree is safe: the
                                // apphost-freshness gate in `executeTests` refuses
                                // `--no-build` against an artifact older than its sources
                                // and defers that project WITHOUT spawning a test process,
                                // so the stale case costs a status flip, not a run.
                                //
                                // The claim is ATTEMPTED, not pre-checked: `RunExclusive`
                                // returns the outcome, so there is no TOCTOU between an
                                // `IsRunning` read and the launch.
                                let hasCachedResults = flushedState.LastResults.IsSome
                                let forceRunProjects = flushedState.PendingForceRunProjects

                                let drainedState =
                                    { flushedState with
                                        PendingForceRunProjects = Set.empty }

                                match launchImpactRun ctx configs drainedState hasCachedResults forceRunProjects with
                                | Some launched ->
                                    Logging.info
                                        "test-prune"
                                        $"BatchChecked: %s{owedDescription flushedState.Debt} — draining now"

                                    return launched
                                | None when joinsFullRun flushedState (batch.Trigger = BootScan) ->
                                    // The full-suite run in flight covers the built tree
                                    // this cohort describes, so it takes the debt instead of
                                    // a duplicate run. TestsFinished may discharge it only
                                    // from actual green full-suite evidence over an
                                    // unchanged input tree; failure, partial scope, or a
                                    // moved tree keeps the durable queue outstanding.
                                    Logging.info
                                        "test-prune"
                                        $"BatchChecked: %s{owedDescription flushedState.Debt} discovered during a full-suite run — attaching debt to that run"

                                    return attachToFullRun flushedState
                                | None ->
                                    // A run is in flight but was launched against an older
                                    // queue snapshot, so it cannot clear these symbols.
                                    // Queue the rerun behind it as an intent. The pending
                                    // fanout is retained (the work was NOT consumed).
                                    Logging.info
                                        "test-prune"
                                        $"BatchChecked: %s{owedDescription flushedState.Debt} still outstanding while a run is in flight — queueing re-run"

                                    enqueueImpactRun ctx
                                    return flushedState
                            | _ ->
                                // Analysis-only (no test configs): nothing can verify
                                // these symbols, so there is nothing to drain.
                                return flushedState

                | PluginEvent.BuildCompleted buildResult ->
                    match buildResult with
                    | BuildSucceeded ->
                        // Record that BuildCompleted has fired this session, so subsequent
                        // FileChecked events may promote the freshness sidecar to clean.
                        // Set unconditionally across both the queued-rerun and run-now
                        // branches: the gate asks whether the build has realized the
                        // reference graph, not whether tests are queued.
                        let state =
                            { state with
                                BuildCompletedInThisSession = true }

                        // ── Dependency-fanout fingerprint (computed for EVERY
                        // build, before the running/idle split, so the prior
                        // fingerprint always advances and a mid-run dependency
                        // change is never lost). Fingerprint each test project from
                        // its referenced-project DLL hashes + own package versions
                        // (DependencyFanout). A project whose fingerprint moved
                        // since the last build had a dependency/binary change the
                        // symbol diff can't see → force-run it. An empty graph
                        // (tests / no graph wired) yields no fanout, so the
                        // symbol-precise path is unchanged.
                        let fsprojByName =
                            ctx.ProjectGraph.GetAllProjects()
                            |> List.map (fun p -> Path.GetFileNameWithoutExtension p, p)
                            |> Map.ofList

                        let currentFingerprints =
                            match testConfigs with
                            | Some configs ->
                                configs
                                |> List.choose (fun c ->
                                    Map.tryFind c.Project fsprojByName
                                    |> Option.map (fun fsproj -> c.Project, fsproj))
                                |> List.map (fun (name, fsproj) ->
                                    name, DependencyFanout.computeProjectFingerprint ctx.ProjectGraph fsproj)
                                |> Map.ofList
                            | None -> Map.empty

                        let fanoutNow =
                            DependencyFanout.changedProjects state.PriorProjectFingerprints currentFingerprints

                        if not (Set.isEmpty fanoutNow) then
                            Logging.info
                                "test-prune"
                                $"Dependency fanout: %d{Set.count fanoutNow} test project(s) had a \
                                  dependency-fingerprint change — force-running: \
                                  %s{describeAll (Set.toList fanoutNow)}"
                        else
                            // Say that nothing fanned out, and say what was examined to
                            // reach that conclusion. "No dependency changed" and
                            // "fingerprinting never ran" otherwise produce identical
                            // logs, so an inert `computeProjectFingerprint` — the safety
                            // net against binary-only changes silently OFF — reads as a
                            // healthy quiet run. Zero fingerprints computed, or zero
                            // graph projects, means inert rather than clean.
                            Logging.info
                                "test-prune"
                                $"Dependency fanout: none — \
                                  %d{currentFingerprints.Count} project fingerprint(s) computed, \
                                  %d{fsprojByName.Count} project(s) in the graph, \
                                  %d{state.PriorProjectFingerprints.Count} prior fingerprint(s) to compare against"

                        // Advance the baseline on every build; carry any not-yet-run
                        // fanout in the pending set (consumed by the queued rerun).
                        let state =
                            { state with
                                PriorProjectFingerprints = currentFingerprints }

                        // Asked BEFORE selection: a held key refuses the claim, and
                        // selection is the expensive step (a flush and an impact query
                        // over the whole index), so selecting only to be refused is
                        // wasted work. A fold of a finished run holds the key as surely
                        // as a live run does. The claim below still handles `SlotBusy`
                        // for a key taken after this read.
                        let heldBy =
                            match ctx.SlotHolder "tests" with
                            | SlotHolder.Free -> None
                            | SlotHolder.LiveRun -> Some "tests already running"
                            | SlotHolder.Fold -> Some "tests key held by an uncommitted fold"

                        match heldBy with
                        | Some reason when joinsFullRun state false ->
                            // A pass-through full run takes what is owed: no run follows
                            // it. The fanout is kept for whatever launches next.
                            ctx.Log $"  ↳ attached to the full run (%s{reason})"

                            Logging.info
                                "test-prune"
                                $"BuildSucceeded received during a full-suite run (%s{reason}) — attaching debt to that run"

                            return
                                { attachToFullRun state with
                                    PendingForceRunProjects = Set.union state.PendingForceRunProjects fanoutNow }
                        | Some reason ->
                            // The leading two spaces nest this under the in-flight test
                            // run in the activity-fold `recent:` view (the renderer
                            // already indents every tail entry by 8), so it does not read
                            // as a sibling of the test-result lines.
                            ctx.Log $"  ↳ queued re-run (%s{reason})"

                            Logging.info
                                "test-prune"
                                $"BuildSucceeded received but %s{reason} — will re-run after, without selecting now"

                            // Stash the fanout so the rerun runs it (don't lose a
                            // mid-run dependency change).
                            enqueueImpactRun ctx

                            return
                                { state with
                                    PendingForceRunProjects = Set.union state.PendingForceRunProjects fanoutNow }
                        | None ->
                            Logging.info "test-prune" "BuildSucceeded: starting test run"

                            // Flush/query before announcing Running so the reported status never
                            // lies: announcing Running before the flush would flash Running even
                            // on a schema-drifted DB.
                            // The framework catches uncaught throws and forces Failed as a
                            // defense-in-depth net; we still trap locally here so we can run
                            // the schema-drift self-heal and preserve the idle transition.
                            match
                                (try
                                    Ok(flushAndQueryAffectedBounded ctx state)
                                 with ex ->
                                     Error ex)
                            with
                            | Error ex ->
                                Logging.error "test-prune" $"flushAndQueryAffected failed: %s{ex.Message}"
                                tryRepairSchemaDrift ex

                                ctx.ReportStatus(
                                    PluginStatus.failedNow ex.Message $"flush failed: %s{ex.Message}" TimeSpan.Zero
                                )

                                return state
                            | Ok stateWithAffected ->
                                match testConfigs with
                                | Some configs when not configs.IsEmpty ->
                                    let hasCachedResults = state.LastResults.IsSome

                                    // Union this build's fanout with any pending
                                    // fanout deferred from a prior mid-run build,
                                    // then clear the pending set (it's being run).
                                    let forceRunProjects = Set.union fanoutNow stateWithAffected.PendingForceRunProjects

                                    let launchState =
                                        { stateWithAffected with
                                            PendingForceRunProjects = Set.empty }

                                    match launchImpactRun ctx configs launchState hasCachedResults forceRunProjects with
                                    | Some launched -> return launched
                                    | None when joinsFullRun stateWithAffected false ->
                                        // The key is held by a pass-through full run whose
                                        // result is not folded yet: it takes the debt.
                                        Logging.info
                                            "test-prune"
                                            "BuildSucceeded: tests slot held by a full-suite run — attaching debt to that run"

                                        return
                                            { attachToFullRun stateWithAffected with
                                                PendingForceRunProjects = forceRunProjects }
                                    | None ->
                                        // The key is held without a live run: a result fold
                                        // or an intent. Same treatment: queue the rerun behind
                                        // it, retain the un-consumed fanout.
                                        Logging.info
                                            "test-prune"
                                            "BuildSucceeded: tests slot already held — queueing re-run"

                                        enqueueImpactRun ctx

                                        return
                                            { stateWithAffected with
                                                PendingForceRunProjects = forceRunProjects }
                                | _ ->
                                    // No test configs — flush only; nothing to run.
                                    return stateWithAffected
                    | BuildFailed _ -> return state

                | Custom(TestsFinished(started, completed, launch) as message)
                | Custom(CommandTestsFinished(started, completed, launch, _, _) as message) ->
                    // The declarations this completion retires debt under, resolved ONCE and
                    // BEFORE any side effect. A resolution that throws (an ambiguous or
                    // unobserved excluded project) fails this handler before it has emitted,
                    // recorded or committed anything, so the framework settles the work as a
                    // failure and every symbol stays owed.
                    // Every symbol this completion can ask about: what the run launched,
                    // what was attached to it, and what is queued. One grouped query,
                    // and only if something asks.
                    let covering =
                        coveringOf (
                            Seq.concat
                                [ Set.toSeq launch.Symbols
                                  Map.keys state.DebtDuringFullRun
                                  Set.toSeq state.Debt.PendingQueue ]
                        )

                    let excluded =
                        if Set.isEmpty runnableProjects then
                            lazy (resolveExcludedProjects ())
                        else
                            Lazy<_>.CreateFromValue(resolveExcludedProjects ())

                    let owedTo = debtScope excluded covering

                    // Whether the index names a test project no run here can discharge:
                    // neither configured nor declared excluded. If not, no symbol can be
                    // owed elsewhere, so nothing needs asking per symbol. One scan of the
                    // index, not a covering query, and only if something asks. An index
                    // that cannot be scanned may name one.
                    let noUnrunnableCoverers =
                        lazy
                            (match queries.IndexedTestProjects() with
                             | Some indexed -> Set.isSubset (debtScope excluded (fun _ -> indexed) "") runnableProjects
                             | None -> false)

                    // Emit the lifecycle events synchronously here, inside the framework's
                    // per-event capture window, so they land in the cached EmittedEvents
                    // and re-fire on cache replay — subscribers that key off
                    // TestRunCompleted (FileCommandPlugin) must see it on a hit.
                    ctx.EmitTestRunCompleted completed

                    // This run joins the session ledger HERE, so every state this fold
                    // returns carries it. A run that completed is a run whose directory a
                    // reader may need, whatever the handler goes on to decide about its
                    // results.
                    let completedRuns =
                        completed.RunId
                        :: (state.CompletedRuns
                            |> List.filter (fun id -> id <> completed.RunId)
                            |> List.truncate (SessionRunLedger - 1))

                    // Apply error reporting synchronously here too — live emission from
                    // the async wouldn't be captured for cache replay.
                    let testResults: TestResults =
                        { Results = completed.Results
                          Elapsed = completed.TotalElapsed }

                    // A run may clear ONLY what it COVERED.
                    //
                    // Two properties must hold together: the ledger is REWRITTEN each
                    // cycle (so a superseded red cannot linger), and it is
                    // rewritten from the OUTSTANDING set — what this run found PLUS every
                    // earlier red it did not cover. Rewriting from this run's failures
                    // alone lets a narrower run's green erase reds it never executed.
                    // Coverage comes from the run's own launch selection, so a red dies
                    // only to evidence that executed it, and dies the moment that evidence
                    // exists (no stuck-red).
                    //
                    // The launch selection cannot express the reach of a raw `--filter`
                    // passthrough — it records every project as `ProjectInFull` — so hand
                    // `ofRun` the classes the run's OWN report shows passing.
                    // Read from THIS run's directory only, and empty
                    // whenever the report is missing or incomplete.
                    let coverage =
                        RunCoverage.ofRun
                            launch.Selection
                            completed.Results
                            (passedClassesOfRun repoRoot completed.RunId)

                    let passedTests = passedTestsOfRun repoRoot completed.RunId

                    // The run log a project-level cause points at. Named
                    // only when it is on disk: `Written` is otherwise reserved for the
                    // code holding the open handle, and a message must never point at
                    // a log nobody wrote.
                    let runLogOf (project: string) : RunLog.Ref =
                        let path = RunLog.pathIn (Ctrf.runDir repoRoot completed.RunId) project

                        if File.Exists path then
                            RunLog.Ref.Written path
                        else
                            RunLog.Ref.Unavailable "no output log is on disk for this run"

                    // The failed rows of the CTRF report each project wrote in this run's
                    // directory, for naming a red the console line did not.
                    let reportFailuresOf =
                        let reports =
                            Ctrf.reportsForRun repoRoot completed.RunId
                            |> List.map (fun report -> report.Project, report.Path)
                            |> Map.ofList

                        fun (project: string) ->
                            match Map.tryFind project reports with
                            | None -> []
                            | Some path ->
                                try
                                    failedRowsOfReport (File.ReadAllText path)
                                with
                                | :? IOException
                                | :? UnauthorizedAccessException -> []

                    let foundFailures =
                        failuresOf runLogOf reportFailuresOf state.TestClassFiles testResults

                    let checkReach, conditionalFailureRecall =
                        failedTestsOfRun repoRoot completed.RunId testResults
                        |> CheckReach.classifyEvidence launch.WouldHaveRun

                    // Classified HERE, against THIS run's failures and the
                    // selection retained at its launch. Nothing extra runs and nothing is
                    // re-read: both inputs are already in hand.

                    let carriedFailures =
                        OutstandingFailure.carriedOver runnableProjects coverage passedTests state.OutstandingFailures

                    let outstandingFailures =
                        OutstandingFailure.carry
                            runnableProjects
                            coverage
                            passedTests
                            foundFailures
                            state.OutstandingFailures

                    if not carriedFailures.IsEmpty then
                        Logging.info
                            "test-prune"
                            $"%d{carriedFailures.Length} failure(s) from an earlier run were NOT covered by this one (%s{OutstandingFailure.summarize carriedFailures}) — they stay RED until a run that executes them passes"

                    // Discharged HERE, immediately before the ledger
                    // rewrite, because this is the one place the outstanding set is
                    // recomputed — pruning anywhere else would leave the ledger and the
                    // state disagreeing about what is still owed.
                    let unanalyzable = pruneDeletedUnanalyzable File.Exists state.UnanalyzableFiles

                    // The ONLY path to the ledger: clear the slate, re-report the whole
                    // outstanding set. There is no wholesale clear a filtered run can
                    // reach for.
                    reportOutstanding ctx unanalyzable state.FailedExtensions outstandingFailures

                    // `LastCoverage` is the moment the process acquires test evidence — a
                    // run completed and we know what it covered. Until a state carrying it
                    // is supplied, the cache key refuses to let a cached BuildCompleted
                    // assert a result this process never ran.
                    let debtDuringFullRun = state.DebtDuringFullRun

                    let currentInputTree = ReceiptInputTree.read repoRoot
                    let currentModelGeneration = observeModelGeneration ctx

                    // A typed transition, not a candidate-then-guard: see
                    // `ReceiptTransition`. `Noop` keeps whatever was earned, `Revoked`
                    // clears it, and only `Earned` can write.
                    let receiptTransition =
                        ReceiptTransition.classify
                            (Set.toList runnableProjects)
                            state.EvidenceReceipt
                            currentInputTree
                            currentModelGeneration
                            launch
                            completed
                            coverage

                    match receiptTransition with
                    | ReceiptTransition.Revoked reason -> ctx.Log $"  ↳ test evidence receipt revoked: %s{reason}"
                    | ReceiptTransition.Earned _
                    | ReceiptTransition.Noop -> ()

                    let evidenceReceipt =
                        ReceiptTransition.apply state.EvidenceReceipt receiptTransition

                    let state =
                        { state with
                            CompletedRuns = completedRuns
                            CheckReach =
                                Some(completed.RunId, launch.WouldHaveRun, checkReach, conditionalFailureRecall)
                            OutstandingFailures = outstandingFailures
                            LastCoverage = coverage
                            LastZeroSelection = launch.ZeroSelection
                            EvidenceReceipt = evidenceReceipt
                            // Debt is scoped to exactly the run that was active when the
                            // BootScan cohort sealed. Failure keeps it durable, but must not
                            // let a later unrelated run claim it implicitly.
                            DebtDuringFullRun = Map.empty
                            InFlight = None
                            Mode = (endRun state).Mode
                            // Carried with them: the pruned map is what the ledger was
                            // just written from, so the next run's coarse-fallback
                            // widening reads the same set the user was shown.
                            UnanalyzableFiles = unanalyzable }


                    // Outcome-conditional, per-project green-commit. A launched
                    // symbol leaves the needs-testing queue ONLY when the run
                    // genuinely covered it green:
                    //   - the run did NOT abort (a beforeRun throw / crash gives
                    //     Outcome = Aborted, Results = Map.empty → commit nothing), AND
                    //   - EVERY project covering the symbol produced a PASSED result.
                    // A project counts as passed only if it appears in completed.Results
                    // with `TestResult.verifiedGreen` — a covering project ABSENT from the
                    // results (didn't run this cycle) blocks the commit (we can't claim
                    // it green). A symbol with NO covering project was already dropped at
                    // flush time, but if one slipped through it commits here (nothing to
                    // wait on). Genuine in-session mid-run arrivals are NOT in
                    // launch.Symbols, and a launched symbol edited again during the run is
                    // at a newer revision than the launch captured: both stay queued and
                    // the queued impact run re-runs them. BootScan debt may join the candidate set only when
                    // this completion proves the run was actually full-suite.
                    //
                    // This fold read `TestResult.isPassed`, which was TRUE
                    // for `TestsNoMatch` — so a symbol whose covering project ran under an
                    // impact-derived class filter that matched ZERO tests had its test debt
                    // DISCHARGED by a project that executed nothing, and left
                    // pending-verification.json unverified. `verifiedGreen` is `Verified`
                    // only, so a project that ran nothing can no longer retire anything.
                    // A run selected under a model this host has since replaced verified
                    // that model, not the current one: it discharges nothing, like an abort.
                    let aborted =
                        launch.ModelGeneration <> currentModelGeneration
                        || match completed.Outcome with
                           | Aborted _ -> true
                           | Normal -> false

                    let projectPassed (proj: string) =
                        match Map.tryFind proj completed.Results with
                        | Some r -> TestResult.verifiedGreen r
                        | None -> false

                    // A run that executed every runnable project in full, over the input tree
                    // it launched against, and passed all of them, covered every symbol that
                    // tree holds: there is nothing left to ask per symbol, unless the index
                    // names a coverer this daemon neither runs nor declares excluded.
                    let coveredByConstruction =
                        not aborted
                        && not (Set.isEmpty runnableProjects)
                        && completed.Verification = Ran FullSuite
                        && ReceiptInputTree.matches launch.InputTreeHash currentInputTree
                        && runnableProjects |> Set.forall projectPassed
                        && noUnrunnableCoverers.Value

                    let committedSymbols =
                        if aborted then
                            Set.empty
                        else
                            // An attached symbol may borrow this run only when the run was
                            // actually full, over the input tree it launched against, and the
                            // symbol is still at the revision captured when it attached.
                            let attachedCandidates =
                                match completed.Verification with
                                | Ran FullSuite when ReceiptInputTree.matches launch.InputTreeHash currentInputTree ->
                                    debtDuringFullRun
                                    |> Map.filter (fun symbol captured -> revisionOf state.Debt symbol = captured)
                                    |> Map.keys
                                    |> Set.ofSeq
                                | _ -> Set.empty

                            let launchedCurrent =
                                launch.Symbols
                                |> Set.filter (fun symbol ->
                                    revisionOf state.Debt symbol = (Map.tryFind symbol launch.SymbolRevisions
                                                                    |> Option.defaultValue 0L))

                            let candidates = Set.union launchedCurrent attachedCandidates

                            if coveredByConstruction then
                                candidates
                            else
                                candidates
                                |> Set.filter (fun s ->
                                    match Map.tryFind s launch.CoveringProjectsBySymbol with
                                    | Some projs when not (Set.isEmpty projs) -> projs |> Set.forall projectPassed
                                    | Some _ -> true
                                    | None -> owedTo s |> Set.forall projectPassed)

                    if not (Set.isEmpty committedSymbols) then
                        Logging.info
                            "test-prune"
                            $"Committing %d{Set.count committedSymbols} verified symbol(s) — removing from pending-verification queue"

                    let debt = commitPending committedSymbols state.Debt

                    if not aborted && not (Map.isEmpty launch.RuntimeProjectsByFile) then
                        for KeyValue(file, launchedProjects) in launch.RuntimeProjectsByFile do
                            let passed = launchedProjects |> Map.keys |> Seq.filter projectPassed |> Set.ofSeq

                            if not (Set.isEmpty passed) then
                                let projectNames = passed |> Set.toList |> String.concat ", "

                                Logging.info "test-prune" $"runtime coverage verified %s{file} by %s{projectNames}"

                    // A run covered by construction discharges every obligation owed now,
                    // not only those it launched with: what attached to it arrived over the
                    // tree it ran.
                    let retiredRuntime =
                        if coveredByConstruction then
                            debt.RuntimeObligations
                        else
                            launch.RuntimeProjectsByFile

                    let debt =
                        if aborted || Map.isEmpty retiredRuntime then
                            debt
                        else
                            { debt with
                                RuntimeObligations =
                                    retireRuntimeCoverageObligations
                                        debt.RuntimeObligations
                                        retiredRuntime
                                        projectPassed }

                    // Discharge an UNREADABLE ledger's debt.
                    //
                    // The debt is owed in FULL, because its membership is unknown: the only
                    // run that can retire it is one that executed EVERY runnable project,
                    // unfiltered, and passed. At that point every symbol the lost ledger
                    // could possibly have held has been verified by an actual test run, so
                    // there is nothing left for it to owe.
                    //
                    // Each conjunct is load-bearing:
                    //  * `not aborted` — an aborted run has empty Results and verified nothing.
                    //  * every RUNNABLE project passed — `projectPassed` demands the project
                    //    be PRESENT in the results AND green, so a project that never ran
                    //    cannot be counted.
                    //  * `Ran FullSuite` — the run EXECUTED and none of it was
                    //    impact-FILTERED. A case, not a bool: scope is unreachable unless
                    //    something ran, so emptiness needs no separate check.
                    //  * a non-empty `runnableProjects` — an analysis-only daemon runs no
                    //    tests, so it can never prove anything and must not discharge. It
                    //    asks about the SELECTION, not the results.
                    //
                    // Only a state that has discharged it rewrites the ledger: `PrepareCommit`
                    // leaves the corrupt file untouched while the debt is unknown, so that a
                    // crash mid-recovery leaves the next session the same honest "unknown"
                    // rather than a clean, empty, WRONG ledger.
                    let executedFullSuite =
                        not aborted
                        && not (Set.isEmpty runnableProjects)
                        && completed.Verification = Ran FullSuite

                    let recovers =
                        debt.RecoveryOutstanding
                        && executedFullSuite
                        && runnableProjects |> Set.forall projectPassed

                    // Obligations this run launched were retired above; any left are newer
                    // than the run and stay owed.
                    let debt =
                        if recovers then
                            { debt with
                                RecoveryOutstanding = false }
                        else
                            debt

                    if recovers then
                        Logging.info
                            "test-prune"
                            "A full suite passed every configured project — the unreadable pending-verification ledger has been rewritten and its unknown debt discharged. Impact filtering resumes."

                    // The full-suite WATERMARK. Written when a full-suite
                    // run has ACCOUNTED for every configured project: passed, or red with
                    // the red recorded in the durable outstanding list. Not only for a
                    // green run — a red full suite still proves what every other test did,
                    // and its reds are quarantined until they pass — so the inner loop can
                    // resume impact filtering (plus quarantine) after one full run rather
                    // than running the whole suite until the last red is fixed. A project
                    // that produced no accountable outcome (deferred, errored) is neither,
                    // and `Ran FullSuite` is already false for it.
                    //
                    // The same `executedFullSuite` the unreadable-ledger discharge reads: two
                    // readings of what a full-suite run is would let one recover a ledger
                    // the other refused to baseline.
                    let accountedFor (proj: string) =
                        projectPassed proj
                        || outstandingFailures |> List.exists (fun f -> f.Project = proj)

                    let earnsBaseline = executedFullSuite && runnableProjects |> Set.forall accountedFor

                    let debt =
                        if earnsBaseline then
                            { debt with
                                Baseline =
                                    Some
                                        { RunId = completed.RunId
                                          EarnedAt = DateTime.UtcNow
                                          Projects = runnableProjects } }
                        else
                            debt

                    // What this completion EARNED. What it left OWED is a refusal it
                    // carries: a proof that records a red is still a proof of what ran.
                    //
                    // Owed is what THIS run did not cover, not everything the queue holds
                    // at this instant. A cold tree queues obligations while the run is
                    // already executing — the build fires the full suite first and the
                    // scan's file events land during it — so they are absent from the
                    // launch snapshot and survive the launch-scoped retirement above. A run
                    // that executed every runnable project IN FULL and passed, over the
                    // tree it launched against, covered those files whenever they were
                    // queued; refusing them turned a green check into exit 2 with nothing
                    // failing anywhere. A run that did not (a project absent, failed, or
                    // filtered) covers nothing extra, and every one of them still refuses.
                    let coveredEverythingRunnable =
                        executedFullSuite
                        && ReceiptInputTree.matches launch.InputTreeHash currentInputTree
                        && runnableProjects |> Set.forall projectPassed

                    let owedSymbols =
                        if coveredEverythingRunnable then
                            Set.empty
                        else
                            debt.PendingQueue

                    let owedRuntimeObligations =
                        if coveredEverythingRunnable then
                            0
                        else
                            debt.RuntimeObligations |> Map.values |> Seq.sumBy Map.count

                    let pendingObligations =
                        Set.count owedSymbols
                        + owedRuntimeObligations
                        + unanalyzable.Count
                        + outstandingFailures.Length
                        + (if debt.RecoveryOutstanding then 1 else 0)

                    let earned =
                        match completed.Verification with
                        | NoProjectsSelected when
                            pendingObligations = 0
                            && not aborted
                            && launch.ZeroSelection <> ZeroSelection.NotAZero
                            && launch.ModelGeneration = currentModelGeneration
                            ->
                            // Nothing needed running, so this completion proves nothing new.
                            // The evidence it was already verified by stands, while it belongs
                            // to the current model.
                            EarnedEvidence.retainedForZeroSelection currentModelGeneration state.Earned
                        | NoProjectsSelected
                        | AllZeroMatch _
                        | NothingExecuted
                        | Ran _ ->
                            EarnedEvidence.fromCompletion
                                started.RunId
                                launch.ModelGeneration
                                currentModelGeneration
                                runnableProjects
                                pendingObligations
                                state.Earned
                                completed

                    let state =
                        { state with
                            Debt = debt
                            Earned = earned }

                    if earnsBaseline then
                        let runId = completed.RunId.ToString("N")

                        Logging.info
                            "test-prune"
                            $"Full-suite baseline recorded: run %s{runId} accounted for every configured project (%d{Set.count runnableProjects}); impact-filtered greens are now relative to it"

                    // The in-memory hot view must shed ONLY the committed symbols,
                    // never the whole list — symbols left in the queue (mid-run
                    // arrivals, projects that failed/aborted) must keep selecting
                    // tests until a covering run passes. `queueAfterCommit` is the
                    // post-commit queue; it drives the cleared ChangedSymbols.
                    let queueAfterCommit = debt.PendingQueue
                    let remainingChangedSymbols = queueAfterCommit |> Set.toList

                    // Why the queue is still non-empty, in words. Symbols owed to projects
                    // this daemon cannot run are not "waiting on build": no build will
                    // discharge them, so the message names the projects and the way out.
                    // A function: it queries each queued symbol, and only non-green
                    // branches ask.
                    let pendingDescription () =
                        let elsewhere =
                            if noUnrunnableCoverers.Value then
                                Set.empty
                            else
                                owedElsewhere owedTo queueAfterCommit

                        match Set.toList elsewhere with
                        | [] -> $"%d{Set.count queueAfterCommit} symbol(s) waiting on build (tests did not run)"
                        | projects ->
                            let names = projects |> String.concat ", "

                            $"%d{Set.count queueAfterCommit} symbol(s) still owed to tests in %s{names}, which this \
                              daemon does not run: list them in `tests.projects`, or declare them in \
                              `tests.excluded` with a reason"

                    // Pushing a terminal Completed/Failed status is what appends the
                    // run to history; both rerun and final-idle branches must call this.
                    let recordRunOutcome (results: TestResults) =
                        let total = results.Results.Count

                        // Reds this run did not cover are still RED —
                        // they must deny it a green verdict exactly as its own failures
                        // would, or `check` exits 0 with a failing test outstanding
                        // (the whole defect). Named in every non-green message below so
                        // the reason a passing run is not green is never a mystery.
                        let carriedCount = carriedFailures.Length

                        let carriedNote =
                            if carriedCount = 0 then
                                ""
                            else
                                $" (+%d{carriedCount} still red from an earlier run, not covered by this one: %s{OutstandingFailure.summarize carriedFailures})"

                        // Consult `completed.Outcome` FIRST. An Aborted run (beforeRun
                        // threw, runner crashed, run cancelled) must be non-green
                        // regardless of result counts — empty results trivially satisfy
                        // "failed = 0 && deferred = 0" and would otherwise false-green.
                        // Likewise a run that executed ZERO projects while the pending
                        // queue still holds symbols verified nothing, so it takes the
                        // honest "waiting on build (tests did not run)" path.
                        let abortMessage =
                            match completed.Outcome with
                            | Aborted reason -> Some reason
                            | Normal -> None

                        match abortMessage with
                        | Some reason ->
                            ctx.ReportStatus(
                                PluginStatus.failedNow
                                    $"test run aborted (tests did not run): %s{reason}%s{carriedNote}"
                                    $"test run aborted: %s{reason}%s{carriedNote}"
                                    results.Elapsed
                            )
                        | None when total = 0 && not (Set.isEmpty queueAfterCommit) ->
                            // Zero projects executed but symbols still await
                            // verification — honest non-green, same wording/path as a
                            // deferred (never-ran) project.
                            ctx.ReportStatus(
                                PluginStatus.failedNow
                                    $"%s{pendingDescription ()}%s{carriedNote}"
                                    $"0 projects ran; symbols still awaiting verification%s{carriedNote}"
                                    results.Elapsed
                            )
                        // Projects RAN and every one matched zero tests, so
                        // nothing executed and nothing was verified — not a green. The
                        // ladder below counts zero matches OUT of both `passed` and
                        // `failed`, so without this check `failed = 0 && deferred = 0`
                        // holds, the green branch fires, and it reports "0 passed, 0
                        // failed in N projects" about N projects that ran no test at all.
                        //
                        // Scoped to ALL projects deliberately: a zero match in ONE project
                        // is a correct pass for it (an impact selection naming no class in
                        // the Integration project must not fail that project). Only the
                        // run-level verdict changes, and only when nothing matched
                        // anywhere — a mis-aimed filter, never a verified pass. An
                        // empty-results run stays on its existing "nothing to verify" path
                        // because `Map.forall` is vacuously true for an empty map.
                        | None when allZeroMatchOf results.Results ->
                            ctx.ReportStatus(
                                PluginStatus.failedNow
                                    $"%d{total} project(s) ran and matched ZERO tests — nothing was verified (not a pass)%s{carriedNote}"
                                    $"%d{total} project(s) discovered their tests; the active filter matched none of them, so no test executed%s{carriedNote}"
                                    results.Elapsed
                            )
                        | None ->

                            // Non-green = anything this run did not verify green, MINUS
                            // the zero matches (counted separately below). Split further
                            // into genuine failures vs deferred (never-ran) so the verdict
                            // can be honest: deferred is non-green but is "could not run /
                            // waiting on build", NOT "failed".
                            //
                            // Written out per case, at the site. `not verifiedGreen`
                            // would sweep the zero matches in here and report them as
                            // failures; matching only `Refuted` would drop the
                            // Deferred/Errored projects, which ARE non-green — they owed
                            // a result and produced none.
                            //
                            // Per result, this match is the exact negation of
                            // `noProjectRefutedOrUnrun` in `cacheKeyFor`, kept in agreement
                            // BY HAND — see that site for why a shared helper would not
                            // enforce it (the conditions around the two differ).
                            let nonGreen =
                                results.Results
                                |> Map.toList
                                |> List.filter (fun (_, r) ->
                                    match TestResult.verdict r with
                                    | Verified -> false
                                    | Refuted -> true
                                    | NothingVerified -> not (TestResult.isNoMatch r))

                            let deferredList = nonGreen |> List.filter (fun (_, r) -> TestResult.isDeferred r)

                            // An ABORTED project — its test host died
                            // mid-run — is counted OUT of `failed`, which used to sweep
                            // it in (`not isDeferred`). Nothing failed there: the runner
                            // never finished asking. Reporting it as "N failed: X" is a
                            // non-result rendered as a definite negative, and it is the
                            // exact shape a reader mistakes for a mass regression.
                            let abortedList = nonGreen |> List.filter (fun (_, r) -> TestResult.isErrored r)

                            let failedList =
                                nonGreen
                                |> List.filter (fun (_, r) ->
                                    not (TestResult.isDeferred r) && not (TestResult.isErrored r))

                            let failed = failedList.Length
                            let deferred = deferredList.Length
                            let aborted = abortedList.Length

                            // Zero-match projects are counted OUT of `passed`. `passed` is
                            // derived by exclusion, and the `nonGreen` fold above
                            // deliberately excludes them (a zero match is not a failure to
                            // report), so without this subtraction a project that executed
                            // no test is reported as one that passed — and the status line
                            // then disagrees with the CLI, which counts them separately.
                            let noMatch =
                                results.Results |> Map.filter (fun _ r -> TestResult.isNoMatch r) |> Map.count

                            let passed = total - failed - deferred - aborted - noMatch

                            let noMatchSuffix = if noMatch = 0 then "" else $", %d{noMatch} matched nothing"

                            let anyFiltered =
                                results.Results |> Map.exists (fun _ r -> TestResult.wasFiltered r)

                            let selectedSuffix = if anyFiltered then "yes" else "no"

                            let timedOutProjects =
                                failedList
                                |> List.choose (fun (name, r) -> if TestResult.isTimedOut r then Some name else None)

                            // When 2+ projects ran and at least one has recorded elapsed,
                            // surface the slowest in the summary so a bottlenecked project
                            // is visible without having to query test-results JSON.
                            let slowestSuffix =
                                if total < 2 then
                                    ""
                                else
                                    let withElapsed =
                                        results.Results
                                        |> Map.toList
                                        |> List.choose (fun (name, r) ->
                                            let e = TestResult.elapsed r
                                            if e > TimeSpan.Zero then Some(name, e) else None)

                                    match withElapsed with
                                    | [] -> ""
                                    | _ ->
                                        let (n, e) = withElapsed |> List.maxBy snd
                                        $", slowest: %s{n} %.1f{e.TotalSeconds}s"

                            let deferredSuffix =
                                if deferred > 0 then
                                    $", %d{deferred} waiting on build"
                                else
                                    ""

                            // Never folded into the `failed` number it sits beside: the
                            // summary is the one line most readers see, so it is the one
                            // line that must not call a dead host a failure.
                            let abortedSuffix =
                                if aborted > 0 then
                                    $", %d{aborted} ABORTED (host died, nothing verified)"
                                else
                                    ""

                            let abortedNames = abortedList |> List.map fst |> String.concat ", "

                            if not timedOutProjects.IsEmpty then
                                let names = timedOutProjects |> String.concat ", "
                                // Flip the recorded outcome to TimedOut; the verdict on
                                // the Failed below carries the summary (one channel).
                                ctx.CompleteWithTimeout $"test project(s): {names}"

                                ctx.ReportStatus(
                                    PluginStatus.failedNow
                                        $"%d{timedOutProjects.Length} timed out: %s{names}%s{carriedNote}"
                                        $"%d{timedOutProjects.Length} timed out: %s{names}%s{carriedNote}"
                                        results.Elapsed
                                )
                            else
                                let runSummary =
                                    $"%d{passed} passed, %d{failed} failed%s{abortedSuffix}%s{deferredSuffix}%s{noMatchSuffix} in %d{total} projects (selected: %s{selectedSuffix}%s{slowestSuffix})"

                                // EVERY terminal below CARRIES the run's evidence —
                                // `runSummary` + measured duration — on the status
                                // itself. There is no separate summary channel left to
                                // forget or contradict.
                                if
                                    failed = 0
                                    && aborted = 0
                                    && deferred = 0
                                    && Set.isEmpty queueAfterCommit
                                    && carriedCount = 0
                                then
                                    // Nothing failed, nothing is owed — and on a
                                    // run that EXECUTED NOTHING that is not a pass, it is an
                                    // absence of evidence. `runSummary` would report it as
                                    // "0 passed, 0 failed in 0 projects", which is how a plugin
                                    // line goes `✓` for a check that verified nothing while the
                                    // verdict layer is (correctly) refusing it exit 0.
                                    //
                                    // The STATUS stays `Completed`: nothing failed, and a
                                    // `Failed` here would claim one — turning `check`'s honest
                                    // exit 3 "NO VERDICT" into an exit 1 "failures found". It is
                                    // the VERDICT that carries the fact — the run record becomes
                                    // `RunOutcome.VerifiedNothing` — and every renderer keys its
                                    // glyph off that case (`ParsedPluginStatus.verifiedNothing`).
                                    //
                                    // Asked of `RunVerification`, THE derivation, so this is a
                                    // question about the RUN ("did anything execute?") and not
                                    // about one selection bug: any future path that lands an
                                    // executed-nothing run here is covered without a new arm.
                                    let verdict =
                                        if RunVerification.verifiedNothing (verificationOf results.Results) then
                                            RunVerdict.verifiedNothing
                                                $"%d{total} test project(s) ran, no test executed"
                                                results.Elapsed
                                        else
                                            RunVerdict.create runSummary results.Elapsed

                                    ctx.ReportStatus(Completed(DateTime.UtcNow, verdict))
                                elif failed = 0 && aborted = 0 && deferred = 0 && Set.isEmpty queueAfterCommit then
                                    // Everything this run RAN passed, the
                                    // queue is drained — and yet an earlier failure it did
                                    // not execute is still outstanding. A narrower run
                                    // cannot vindicate a test it never ran, so this is
                                    // NON-green: the red survives until a run that COVERS
                                    // it passes (`dotnet fshw test-rerun` runs every
                                    // project unfiltered and will clear it if it is fixed).
                                    ctx.ReportStatus(
                                        PluginStatus.failedNow
                                            $"%d{carriedCount} still red from an earlier run, not covered by this one: %s{OutstandingFailure.summarize carriedFailures}"
                                            $"%s{runSummary}%s{carriedNote}"
                                            results.Elapsed
                                    )
                                elif failed = 0 && aborted = 0 && deferred = 0 then
                                    // Everything that RAN passed, but the pending queue
                                    // still holds symbols this (e.g. filtered) run did not
                                    // cover green — NOT test-equivalent to a green run yet.
                                    // Non-green with the honest "waiting on build" wording;
                                    // the next BuildCompleted re-selects and runs them.
                                    ctx.ReportStatus(
                                        PluginStatus.failedNow
                                            $"%s{pendingDescription ()}%s{carriedNote}"
                                            $"%s{runSummary}%s{carriedNote}"
                                            results.Elapsed
                                    )
                                elif failed = 0 && aborted = 0 then
                                    // Only deferred projects — nothing FAILED, but
                                    // nothing was verified either. Non-green, honest
                                    // "waiting on build" (never "failed").
                                    let names = deferredList |> List.map fst |> String.concat ", "

                                    if carriedCount = 0 then
                                        // PURE defer: no red this run or carried. A
                                        // NON-failing terminal, so the verdict reads the
                                        // `Deferred`-severity ledger entry and routes it to
                                        // `Incomplete`/exit 2, not the exit 1 a red earns —
                                        // a build-ordering race left one project unrun.
                                        // A defer's `verdict` is `NothingVerified`, so its
                                        // symbols never commit (the next build re-runs them)
                                        // and the result is uncacheable.
                                        ctx.ReportStatus(
                                            PluginStatus.completedNow
                                                $"%s{runSummary} — %d{deferred} waiting on build (tests did not run): %s{names}"
                                                results.Elapsed
                                        )
                                    else
                                        // A carried RED from an earlier run is still
                                        // outstanding — that dominates a defer. Stay
                                        // Failed/red (exit 1); the ledger's carried Error
                                        // entry independently keeps the verdict red.
                                        ctx.ReportStatus(
                                            PluginStatus.failedNow
                                                $"%d{deferred} waiting on build (tests did not run): %s{names}%s{carriedNote}"
                                                $"%s{runSummary}%s{carriedNote}"
                                                results.Elapsed
                                        )
                                elif failed = 0 then
                                    // Nothing failed — a test HOST DIED.
                                    // Killed by a signal under load, or gone before it
                                    // could write a report. No test was shown to fail and
                                    // no test was shown to pass, so this is the third
                                    // answer and it must LOOK like the third answer: a
                                    // dead host reported as "N failed: X" sends the reader
                                    // hunting a regression that is not there, which is the
                                    // hours this arm exists to stop burning.
                                    //
                                    // A NON-failing terminal, exactly like the pure-defer
                                    // arm above: the `HostAborted`-severity ledger entries
                                    // route the verdict to `RunnerAborted`/exit 2 rather
                                    // than the exit 1 a red earns. The verdict is a
                                    // verified-nothing one (`RunOutcome.VerifiedNothing` on
                                    // the run record), so no renderer can give this run a
                                    // bare `✓` either.
                                    let deferredNote =
                                        if deferred > 0 then
                                            let dn = deferredList |> List.map fst |> String.concat ", "
                                            $" (+%d{deferred} waiting on build: %s{dn})"
                                        else
                                            ""

                                    let abortLine =
                                        $"%d{aborted} test host(s) ABORTED — killed mid-run, nothing verified (NOT a test failure): %s{abortedNames}%s{deferredNote}"

                                    if carriedCount = 0 then
                                        ctx.ReportStatus(PluginStatus.verifiedNothingNow abortLine results.Elapsed)
                                    else
                                        // A carried RED from an earlier run outranks an
                                        // abort — same rule the defer arm applies, and for
                                        // the same reason: an aborted run cannot vindicate
                                        // a test it never ran. Stay red (exit 1); the
                                        // carried Error entry keeps the ledger red anyway.
                                        ctx.ReportStatus(
                                            PluginStatus.failedNow
                                                $"%s{abortLine}%s{carriedNote}"
                                                $"%s{runSummary}%s{carriedNote}"
                                                results.Elapsed
                                        )
                                else
                                    let names = failedList |> List.map fst |> String.concat ", "

                                    let deferredNote =
                                        if deferred > 0 then
                                            let dn = deferredList |> List.map fst |> String.concat ", "
                                            $" (%d{deferred} waiting on build: %s{dn})"
                                        else
                                            ""

                                    // A real red DOMINATES an abort beside it — but the
                                    // abort is still NAMED, and named as an abort: those
                                    // projects proved nothing, and a reader must not add
                                    // them to the failure count.
                                    let abortNote =
                                        if aborted > 0 then
                                            $" (+%d{aborted} ABORTED, nothing verified: %s{abortedNames})"
                                        else
                                            ""

                                    ctx.ReportStatus(
                                        PluginStatus.failedNow
                                            $"%d{failed} failed: %s{names}%s{abortNote}%s{deferredNote}%s{carriedNote}"
                                            $"%s{runSummary}%s{carriedNote}"
                                            results.Elapsed
                                    )

                    // Whatever this run left owed is queued behind it as an intent by
                    // the trigger that owed it (`enqueueImpactRun`, a queued `run-tests`),
                    // and is delivered only after this fold is committed. Nothing is
                    // launched from here.
                    recordRunOutcome testResults

                    let replies =
                        match message with
                        | CommandTestsFinished(_, _, _, reply, response) -> [ reply, response ]
                        | _ -> []

                    return
                        { state with
                            LastResults = Some testResults
                            LastRunId = Some completed.RunId
                            // Only the files this run launched against are consumed; a
                            // file that changed during the run selects the next one.
                            ChangedFiles =
                                if coveredByConstruction then
                                    []
                                else
                                    state.ChangedFiles
                                    |> List.filter (fun file -> not (List.contains file launch.ChangedFiles))
                            ChangedSymbols = remainingChangedSymbols
                            AffectedTests = Analyzed []
                            Replies = replies }

                // An unavailable launch ran nothing. Its receipt is revoked and the fanout
                // it consumed is owed again. No run is queued: the next build or cohort
                // seal is what can make the artifacts or the host available, and it
                // launches with this debt.
                | Custom(ArtifactsUnavailable(reason, owed, reply)) ->
                    let message =
                        $"Tests did not run because the preceding build left invalid artifacts: %s{reason}"

                    return unavailableRun ctx state message owed reply

                | Custom(TestHostUnavailable(reason, owed, reply)) ->
                    let message = $"Tests did not run because the test host could not start: %s{reason}"
                    return unavailableRun ctx state message owed reply

                | Custom(ScopeRequested mode) ->
                    if TestMode.requestsFullSuite mode then
                        Logging.info
                            "test-prune"
                            "Scope set to FULL SUITE — impact filtering disabled for subsequent runs in this daemon session"
                    else
                        Logging.info "test-prune" "Scope set to IMPACT-FILTERED (inner-loop default)"

                    return { state with Mode = mode }

                | Custom(RuntimeCoverageFailed project) ->
                    Logging.warn
                        "test-prune"
                        $"runtime coverage for %s{project} could not be ingested; what is owed is unknown until a full suite passes"

                    return
                        { state with
                            Debt =
                                { state.Debt with
                                    RecoveryOutstanding = true } }

                | Custom ImpactRunRequested -> return impactRun ctx state

                | Custom(RunTestsRequested(configs, filter, reply)) ->
                    return requestTestRun ctx state configs filter reply

                | Custom(RunFailedTestsRequested(filter, reply)) ->
                    match failedTestConfigs (defaultArg testConfigs []) state.LastResults state.OutstandingFailures with
                    | Error msg ->
                        reply.TrySetResult(JsonSerializer.Serialize({| error = msg |})) |> ignore
                        return state
                    | Ok [] ->
                        reply.TrySetResult(JsonSerializer.Serialize({| error = "no matching test projects" |}))
                        |> ignore

                        return state
                    | Ok configs -> return requestTestRun ctx state configs filter reply

                | _ -> return state
            }
      PrepareCommit = Some prepareCommit
      Commands = allCommands
      Subscriptions =
        Set.ofList (
            // BatchChecked is the cohort-complete flush signal: it fires after the last
            // FileChecked of a batch and before any subsequent BuildCompleted racing the
            // same change, so by the time the agent processes it every FileChecked update
            // has been folded in to `state.ChangedSymbols`.
            //
            // BuildCompleted is subscribed UNCONDITIONALLY so the freshness-stamp gate
            // works even when the plugin is analysis-only: with no testConfigs the handler
            // still runs `flushAndQueryAffected` (idempotent on empty PendingAnalysis) and
            // skips the test-run path.
            [ SubscribeFileChecked; SubscribeBatchChecked; SubscribeBuildCompleted ]
        )
      CacheKey =
        // Pure-content cache key, built by the lifted `cacheKeyFor` so the per-arm input
        // dependencies are structural rather than a convention. The thunks are this
        // closure's live state; `cacheKeyFor` decides which arm forces which — and
        // `FileChecked`, the per-file probe, forces none.
        let cacheKey (state: TestPruneState) (event: PluginEvent<TestPruneMsg>) : ContentHash option =
            let changedSymbolsHash () =
                state.ChangedSymbols
                |> List.distinct
                |> List.sort
                |> String.concat "|"
                |> FsHotWatch.CheckCache.sha256Hex

            // The persisted needs-testing queue. `None` = empty, which both omits the
            // merkle entry (keeping the empty-queue green fast-path key byte-stable) and,
            // on BuildCompleted, is what makes the event cacheable at all: a green that
            // left symbols queued must re-run, never replay.
            let pendingQueueHash () =
                if state.Debt.RecoveryOutstanding then
                    // An unreadable ledger is outstanding debt whose
                    // membership is unknown. `None` would assert "provably nothing owed"
                    // and make BuildCompleted cacheable, so a cached green written over
                    // this same content merkle would replay with no test process running.
                    // `Some` refuses cache participation outright, as a non-empty queue
                    // does. A constant rather than a hash — there is nothing to hash.
                    Some "unreadable-ledger"
                elif Set.isEmpty state.Debt.PendingQueue then
                    None
                else
                    Some(PendingVerification.hash state.Debt.PendingQueue)

            // External-dependency salt: a content hash of the files matched by the
            // configured `dependsOn` globs. Editing a matched file (a DB migration
            // that changes the TEST database schema but no test SOURCE) changes this
            // hash → cache miss → genuine re-run. `None` when unconfigured or when
            // the globs match nothing, so the entry is omitted and existing on-disk
            // caches keep hitting.
            let dependsOnHash () =
                match externalDependencyHash repoRoot dependsOn with
                | "" -> None
                | h -> Some h

            // A full-suite run must never REPLAY an impact-filtered run's
            // cached verdict. Salting the key with the requested scope makes that
            // impossible rather than merely unlikely.
            let fullSuiteScopeHash () =
                // A run widened by a missing baseline is a full-suite
                // run too, and must not replay a filtered run's cached verdict.
                if
                    TestMode.requestsFullSuite state.Mode
                    || Option.isSome (baselineInvalidReason state.Debt)
                then
                    Some "full"
                else
                    None

            // No cache participation while a red no covering run has
            // passed is outstanding.
            let hasOutstandingFailures () =
                not (List.isEmpty state.OutstandingFailures)

            // No cache participation on BuildCompleted until a run in
            // THIS process has covered something.
            //
            // ANALYSIS-ONLY IS EXEMPT. With no runnable test projects this plugin makes no
            // test claim at all — its terminal status is a symbol-analysis summary — so
            // there is no verdict to launder, and gating it would throw away a working
            // cache to guard an assertion it never makes.
            let sessionHasTestEvidence () =
                Set.isEmpty runnableProjects
                || not (Set.isEmpty (RunCoverage.coveredProjects state.LastCoverage))

            // A full-repo walk of the project files, so it is a thunk
            // like the rest: `FileChecked` fires once per file on every scan and must
            // not pay for an input it never splices.
            let structureHash () = projectStructureHash repoRoot

            match event with
            // Dependency fanout still owed: the launch that runs it is this handler's, so
            // no replay AND no write, as for a non-empty pending queue. A replayed green
            // would skip the only event that launches it, and the fanout is not a key
            // input, so a later lookup could not tell the entry from one that ran it.
            | BuildCompleted _
            | Custom(TestsFinished _) when not (Set.isEmpty state.PendingForceRunProjects) -> None
            // A full-suite request earns its evidence from a real run, never a replay. The
            // run's own entry is still written, from its `TestsFinished` window.
            | BuildCompleted _ when TestMode.requestsFullSuite state.Mode -> None
            | _ ->
                cacheKeyFor
                    changedSymbolsHash
                    pendingQueueHash
                    dependsOnHash
                    structureHash
                    fullSuiteScopeHash
                    hasOutstandingFailures
                    sessionHasTestEvidence
                    event

        Some cacheKey
      Teardown = None }

/// `createWithQueries` over the plugin's own index.
let internal createWithLaunchDeadline
    (launchDeadline: TimeSpan)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    (traces: TraceWiring option)
    =
    createWithQueries
        ImpactQueries.ofDatabase
        launchDeadline
        resolveExcludedProjects
        dbPath
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn
        traces

/// Create a TestPrune handler that honors declared test-scope exclusions and records
/// per-test traces as `tests.traces` asks.
///
/// `traces` is the parsed `tests.traces` block; `None` records nothing and launches every
/// project exactly as `createWithScope` does. `untracedProjects` names the projects that
/// opted out with `"traces": false`. A project tracing refuses (no build output, a refused
/// weave, a failed JIT verification, an unknown `dotnet run` option) runs untraced, and
/// the refusal is stored in the trace database and logged with its reason. No trace
/// failure changes a test result or the verdict.
///
/// See `createWithScope` for `resolveExcludedProjects` and the launch policy.
let createWithTraces
    (traces: TraceSettings option)
    (untracedProjects: Set<string>)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    let launchDeadline =
        Environment.GetEnvironmentVariable "FSHW_LAUNCH_DEADLINE_SEC"
        |> Option.ofObj
        |> resolveLaunchDeadline

    createWithLaunchDeadline
        launchDeadline
        resolveExcludedProjects
        dbPath
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn
        (traces
         |> Option.map (fun settings ->
             { Policy = settings
               OptedOut = untracedProjects
               Decide = TraceRun.decide }))

/// Create a TestPrune handler that honors declared test-scope exclusions.
///
/// `resolveExcludedProjects` returns indexed test-project name (the project file's name
/// without extension) -> written reason. A covering project that is not configured and
/// is declared here with a non-blank reason no longer holds a symbol's verification
/// debt; every other covering project does, configured or not. It is called at each
/// debt classification; a throw refuses that classification and leaves the debt owed.
///
/// The launch policy is captured at construction. Environment configuration is
/// process-global, so reading it lazily at run time can retroactively change
/// already-created handlers (and made a short-deadline regression kill unrelated
/// in-flight test children under parallel CI). A handler's policy is immutable for its
/// lifetime; tests inject it through `createWithLaunchDeadline` without mutating
/// process-global state.
let createWithScope
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    createWithTraces
        None
        Set.empty
        resolveExcludedProjects
        dbPath
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn

/// Create a TestPrune handler with no declared exclusions: every covering test project
/// holds its symbols' verification debt. See `createWithScope`.
let create
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    createWithScope
        (fun () -> Map.empty)
        dbPath
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn
