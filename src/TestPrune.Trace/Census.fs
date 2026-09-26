/// The phase-1 census: reads a recorded run back out of the trace store and measures it
/// against the done-bar. At least 99 % of executed tests traced; unattributed (ambient)
/// hits under 0.1 % of all hits, or every ambient symbol listed so a human can explain
/// it; and a table of pool scopes with the tests that inherit them.
///
/// Only the run's `stats_json` and its stored scopes are read, so a census of the
/// newest run of a project is exact. An older run (`--run`) is partial: later runs take
/// over the traces of the tests they re-record, and store GC keeps the ambient scope for
/// each project's newest run only.
module TestPrune.Trace.Census

open System.IO
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite
open TestPrune.Trace.TraceIngest

/// A `P:` scope of a run: its joined symbols, its inputs and the tests that link it.
type PoolScope =
    { Key: string
      Symbols: int
      Inputs: int
      LinkedTests: int }

/// One project's run, as the done-bar reads it.
type ProjectCensus =
    {
        TestProject: string
        RunId: string
        /// CTRF tests that ran (every outcome except skipped).
        Executed: int
        /// Executed tests matched to a recorded test scope.
        Traced: int
        /// Stored traces with no incomplete reason.
        Complete: int
        /// Every attributed or ambient probe hit; overflow hits are not probe hits.
        TotalHits: int64
        /// Hits no test, fixture, collection, assembly or explicit scope claimed.
        AmbientHits: int64
        /// What the ambient scope joined to: symbols, then `file:<path>` file-level entries.
        AmbientSymbols: string list
        /// Tests per incomplete-reason kind (the reason code before any ':').
        ReasonCounts: Map<string, int>
        Pools: PoolScope list
    }

/// The done-bar thresholds.
type Bars =
    {
        /// Traced / Executed must be at least this.
        TracedAtLeast: float
        /// AmbientHits / TotalHits must be below this, unless the ambient symbols are listed.
        AmbientBelow: float
    }

/// A project's census with its derived numbers and verdict, as `--json` prints it.
type ProjectReport =
    { Census: ProjectCensus
      TracedRatio: float
      AmbientRatio: float
      Untraced: int
      Passes: bool }

/// The phase-1 bars: 99 % traced, ambient under 0.1 %.
let defaultBars =
    { TracedAtLeast = 0.99
      AmbientBelow = 0.001 }

/// Traced / Executed; 1.0 when nothing executed.
let tracedRatio (c: ProjectCensus) =
    if c.Executed = 0 then
        1.0
    else
        float c.Traced / float c.Executed

/// AmbientHits / TotalHits; 0.0 when nothing hit a probe.
let ambientRatio (c: ProjectCensus) =
    if c.TotalHits = 0L then
        0.0
    else
        float c.AmbientHits / float c.TotalHits

/// Executed tests with no recorded test scope. Such a test hit no probe at all, or its
/// CTRF name did not join to its scope (that test's trace then carries `no-outcome`).
/// Neither is a recorder failure: a recorder that wrote nothing stores a `failed` run.
let untraced (c: ProjectCensus) = c.Executed - c.Traced

/// Whether a project meets the bars. The ambient bar reads "under the threshold, or
/// explained": over the threshold it passes only when the ambient symbols are listed for
/// a human to sign off. The census never claims an explanation is good.
let passes (bars: Bars) (c: ProjectCensus) =
    tracedRatio c >= bars.TracedAtLeast
    && (ambientRatio c < bars.AmbientBelow || not c.AmbientSymbols.IsEmpty)

/// The census with its derived numbers and verdict under `bars`.
let report (bars: Bars) (c: ProjectCensus) : ProjectReport =
    { Census = c
      TracedRatio = tracedRatio c
      AmbientRatio = ambientRatio c
      Untraced = untraced c
      Passes = passes bars c }

let private readRows (conn: SqliteConnection) (sql: string) (ps: (string * obj) list) (f: SqliteDataReader -> 'a) =
    using (conn.CreateCommand()) (fun cmd ->
        cmd.CommandText <- sql

        for n, v in ps do
            cmd.Parameters.AddWithValue(n, v) |> ignore

        using (cmd.ExecuteReader()) (fun r ->
            let rows = ResizeArray()

            while r.Read() do
                rows.Add(f r)

            List.ofSeq rows))

let private reasonKind (code: string) = code.Split(':').[0]

let private censusOf conn (rowId: int64, project: string, runId: string, statsJson: string) =
    let stats = JsonSerializer.Deserialize<RunStats> statsJson
    let c = stats.counters
    let ps = [ "@id", box rowId ]

    let ambient =
        readRows
            conn
            """SELECT v.symbol_full_name FROM trace_scopes s JOIN trace_entries e ON e.scope_id = s.id
               JOIN symbol_versions v ON v.id = e.symbol_version_id
               WHERE s.trace_run_id = @id AND s.scope_key = @k ORDER BY 1"""
            [ "@id", box rowId; "@k", box AmbientScope ]
            (fun r -> r.GetString 0)
        @ readRows
            conn
            """SELECT 'file:' || i.key FROM trace_scopes s JOIN trace_inputs i ON i.scope_id = s.id
               WHERE s.trace_run_id = @id AND s.scope_key = @k AND i.kind = 'file-level' ORDER BY 1"""
            [ "@id", box rowId; "@k", box AmbientScope ]
            (fun r -> r.GetString 0)

    let pools =
        readRows conn """SELECT s.scope_key,
                      (SELECT COUNT(*) FROM trace_entries e WHERE e.scope_id = s.id),
                      (SELECT COUNT(*) FROM trace_inputs i WHERE i.scope_id = s.id),
                      (SELECT COUNT(*) FROM trace_test_scopes ts WHERE ts.scope_id = s.id)
               FROM trace_scopes s WHERE s.trace_run_id = @id AND s.scope_key LIKE 'P:%' ORDER BY 1""" ps (fun r ->
            { Key = r.GetString 0
              Symbols = r.GetInt32 1
              Inputs = r.GetInt32 2
              LinkedTests = r.GetInt32 3 })

    // One count per test per kind: a test with three unmapped ids is one unmapped-code test.
    let reasons =
        readRows
            conn
            "SELECT incomplete_reasons FROM trace_tests WHERE trace_run_id = @id AND complete = 0"
            ps
            (fun r -> JsonSerializer.Deserialize<string[]>(r.GetString 0))
        |> List.collect (Array.map reasonKind >> Array.distinct >> List.ofArray)
        |> List.countBy id
        |> Map.ofList

    { TestProject = project
      RunId = runId
      Executed = stats.executed
      Traced = stats.traced
      Complete = stats.complete
      TotalHits =
        c.Test
        + c.Class
        + c.Collection
        + c.Assembly
        + c.Override
        + c.StaticInit
        + c.Ambient
      AmbientHits = c.Ambient
      AmbientSymbols = ambient
      ReasonCounts = reasons
      Pools = pools }

/// One census per project, by project name: of run `runId` when given, else of the
/// project's newest run that stored traces (`recorded` or `tree-moved`). Raises
/// `FileNotFoundException` for a missing database (a census never creates one) and
/// `TraceStore.TraceSchemaNewerThanConsumer` for a newer one.
let latest (dbPath: string) (runId: string option) : ProjectCensus list =
    if not (File.Exists dbPath) then
        raise (FileNotFoundException($"no trace database at %s{dbPath}", dbPath))

    // Refuses a newer file; applies any missing migration to an older one.
    (TraceStore.Store.Open dbPath :> System.IDisposable).Dispose()

    let conn = new SqliteConnection($"Data Source=%s{dbPath};Mode=ReadOnly")

    try
        conn.Open()

        readRows
            conn
            """SELECT r.id, r.test_project, r.run_id, r.stats_json FROM trace_runs r
               WHERE r.id = (SELECT MAX(x.id) FROM trace_runs x WHERE x.test_project = r.test_project
                             AND x.status IN ('recorded', 'tree-moved') AND (@run IS NULL OR x.run_id = @run))
               ORDER BY r.test_project"""
            [ "@run",
              (match runId with
               | Some r -> box r
               | None -> box System.DBNull.Value) ]
            (fun r -> r.GetInt64 0, r.GetString 1, r.GetString 2, r.GetString 3)
        |> List.map (censusOf conn)
    finally
        SqliteConnection.ClearPool conn
        conn.Dispose()

/// The human table: per project, its verdict, the traced and ambient ratios with their
/// bars, untraced tests, each ambient symbol, tests per incomplete-reason kind and pools.
let render (cs: ProjectCensus list) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore

    for c in cs do
        let verdict = if passes defaultBars c then "PASS" else "FAIL"
        line $"%s{c.TestProject}  run %s{c.RunId}  %s{verdict}"

        line
            $"  traced     %d{c.Traced}/%d{c.Executed} (%.4f{tracedRatio c}, bar >= %g{defaultBars.TracedAtLeast})  complete %d{c.Complete}"

        if untraced c > 0 then
            line
                $"  untraced   %d{untraced c} executed test(s) recorded no test scope: hit no probe, or name not joined (see no-outcome)"

        line
            $"  ambient    %d{c.AmbientHits}/%d{c.TotalHits} (%.5f{ambientRatio c}, bar < %g{defaultBars.AmbientBelow}, or listed)"

        for s in c.AmbientSymbols do
            line $"    ambient symbol: %s{s}"

        // A list, not the map: enumerating a Map compiles a disposal null check no input takes.
        for r, n in Map.toList c.ReasonCounts do
            line $"  incomplete %-28s{r} %d{n}"

        for p in c.Pools do
            line $"  pool %s{p.Key}: %d{p.Symbols} symbols, %d{p.Inputs} inputs, %d{p.LinkedTests} tests"

    sb.ToString()
