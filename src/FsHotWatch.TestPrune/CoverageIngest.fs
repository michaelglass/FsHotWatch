/// Ingesting a run's runtime coverage into the index, the per-project ratchet coverage table, and the shared Cobertura emitted from it.
module FsHotWatch.TestPrune.CoverageIngest

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
open FsHotWatch.FcsSuppression
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
open FsHotWatch.TestPrune.CoverageArtifacts

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

/// A ratchet table an older fshw created keyed on `symbol_id`, with no `occurrence_id`. A
/// TestPrune.Core schema bump rebuilds only core's own tables, so the table outlives the
/// upgrade, and `CREATE TABLE IF NOT EXISTS` would leave it as it is: every later run then
/// aborts on "no such column: occurrence_id" (AUTOMATION-1434). Its rows name ids the core
/// rebuild renumbered, so none of them can be kept: drop it, and let the next coverage run
/// earn the points again. The table's shape is the evidence; its metadata row says
/// schema 1 in both shapes.
let private dropOutdatedProjectRatchetCoverageTable (conn: Microsoft.Data.Sqlite.SqliteConnection) =
    use probe = conn.CreateCommand()

    probe.CommandText <-
        $"""SELECT EXISTS (SELECT 1 FROM pragma_table_info('%s{ProjectRatchetCoverageTable}'))
                   AND NOT EXISTS (SELECT 1 FROM pragma_table_info('%s{ProjectRatchetCoverageTable}')
                                   WHERE name = 'occurrence_id');"""

    if Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture) = 1L then
        Logging.warn
            "test-prune"
            $"%s{ProjectRatchetCoverageTable} has an older fshw's shape (no occurrence_id); dropping it, so the ratchet coverage is earned again by the next coverage run"

        use drop = conn.CreateCommand()
        drop.CommandText <- $"DROP TABLE %s{ProjectRatchetCoverageTable};"
        drop.ExecuteNonQuery() |> ignore

let private ensureProjectRatchetCoverageTable (conn: Microsoft.Data.Sqlite.SqliteConnection) =
    dropOutdatedProjectRatchetCoverageTable conn
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

/// Whether `project` holds any runtime-coverage row (a source file its tests reached).
let private holdsRuntimeCoverage (db: Database) (project: string) =
    use conn = db.OpenConnection()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT EXISTS(SELECT 1 FROM runtime_coverage WHERE test_project = @project);"
    cmd.Parameters.AddWithValue("@project", project) |> ignore
    Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) <> 0L

/// A FULL receipt with no `<line>` at all (an empty `<packages />`) from a green run, for a
/// project that holds runtime coverage, measured nothing: the coverage is MISSING, not
/// "covers nothing" (AUTOMATION-1371). Ingesting it would replace the project's rows with
/// none, and selection would then treat the project as reaching no source file. So it is
/// not ingested: the prior runtime and ratchet rows are kept, and the run is flagged. The
/// baseline's watermark does not advance, so kept rows age into a stale baseline and widen
/// selection to the whole project rather than standing in for a measurement for ever.
///
/// Not this case: a project with no rows (one whose runs never reach a production file
/// writes an empty report every time, and that ingests as before); a partial receipt (it
/// only merges, so an empty one removes nothing); a MALFORMED receipt (its policy is to
/// revoke the prior evidence, and the ingest does that).
let internal coverageMissingFromFullReceipt
    (db: Database)
    (input: CoverageInput)
    (xml: string)
    : CoverageIngestFailure option =
    let holdsNoLines () =
        try
            List.isEmpty (parseCobertura xml)
        with :? System.Xml.XmlException ->
            false

    if
        input.Scope = CoverageRunScope.Full
        && holdsNoLines ()
        && holdsRuntimeCoverage db input.Project
    then
        Some
            { Project = input.Project
              Reason =
                $"coverage missing: successful full test run wrote a runtime coverage report with no lines at %s{input.RawPath}" }
    else
        None

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

    let accepted, failures, coverageMissing =
        (([], [], []), existing)
        ||> List.fold (fun (accepted, failures, coverageMissing) input ->
            try
                let xml = File.ReadAllText input.RawPath

                match coverageMissingFromFullReceipt db input xml with
                | Some missing ->
                    Logging.warn
                        "test-prune"
                        $"%s{input.Project}: %s{missing.Reason}; its prior runtime coverage is kept"

                    accepted, failures, missing :: coverageMissing
                | None ->
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

                    (input, ratchetResult) :: accepted, failures, coverageMissing
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

                accepted, failure :: failures, coverageMissing)

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

    { Failures = List.rev failures
      CoverageMissing = List.rev coverageMissing }

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
