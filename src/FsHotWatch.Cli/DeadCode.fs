/// `fshw dead-code` — run TestPrune's unreachable-symbol (dead-code) analysis
/// directly against the daemon's existing symbol DB at
/// `<repoRoot>/.fshw/test-impact.db`, with the same semantics and output format
/// as the standalone `test-prune dead-code` CLI. No DB copying, no IPC, no
/// running daemon required: this reads the SQLite graph directly (WAL allows
/// concurrent readers while the daemon holds the writer).
module FsHotWatch.Cli.DeadCode

open System
open System.IO
open TestPrune.Database
open TestPrune.DeadCode
open TestPrune.Ports

/// Default entry-point name patterns, mirroring the standalone test-prune CLI.
/// A symbol reachable (transitively, via dependency edges) from any symbol whose
/// full name matches one of these is considered live.
let defaultEntryPatterns =
    [ "*.main"; "*.Program.*"; "*.Routes.*"; "*.Scheduler.*" ]

/// Path (relative to the repo root) of the daemon's symbol database.
[<Literal>]
let DbRelativePath = ".fshw/test-impact.db"

/// TestPrune.Core's on-disk schema version that this CLI was compiled against.
/// `Database.create` would DELETE+recreate a DB whose `PRAGMA user_version` is an
/// older, incompatible value — which on the daemon's live DB would silently wipe
/// the entire symbol graph. We therefore probe `user_version` ourselves BEFORE
/// opening and refuse to touch an incompatible DB. Forward-compatible (newer or
/// equal) versions, and the pre-versioning `0` marker, are tolerated.
///
/// Sourced from TestPrune.Core (public since 4.2.1) rather than hardcoded, so the
/// probe moves in lockstep with the bundled Core: a stale local copy would silently
/// invert the protection on a schema bump (an old DB passes the stale probe, then
/// the newer open path recreates it — exactly what the probe exists to prevent).
[<Literal>]
let ExpectedSchemaVersion = TestPrune.Database.SchemaVersion

/// Resolved options for a `dead-code` run.
type DeadCodeOptions =
    { EntryPatterns: string list
      IncludeTests: bool
      Verbose: bool }

/// Resolve the effective entry patterns: explicit `--entry` values REPLACE the
/// defaults; with none given, fall back to `defaultEntryPatterns`. Pure so arg
/// parsing is unit-testable without touching a DB.
let resolveEntryPatterns (explicit: string list) : string list =
    match explicit with
    | [] -> defaultEntryPatterns
    | patterns -> patterns

/// The user-facing message shown when the daemon's symbol DB is absent.
let missingDbMessage =
    $"No symbol database at %s{DbRelativePath} — start the daemon \
      (`dotnet fshw start`) and let a scan complete first."

/// The user-facing message shown when the DB exists but its schema is too old
/// for this CLI to read. We never recreate it (that would wipe the daemon's
/// graph); the user must upgrade so both write the same schema.
let incompatibleSchemaMessage (found: int) =
    $"Symbol database at %s{DbRelativePath} has schema v%d{found}, but this CLI \
      expects v%d{ExpectedSchemaVersion}. Refusing to touch it (recreating would \
      wipe the daemon's symbol graph). Upgrade FsHotWatch so the daemon and CLI \
      share a schema, then re-scan."

/// Read `PRAGMA user_version` from the SQLite file at `dbPath` without going
/// through TestPrune's `Database.create` (which would recreate on mismatch).
let private readSchemaVersion (dbPath: string) : int =
    use conn =
        new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{dbPath};Mode=ReadOnly")

    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "PRAGMA user_version;"
    cmd.ExecuteScalar() :?> int64 |> int

/// Is `version` one this CLI can safely open? Equal or newer (forward-compat,
/// additive columns we ignore) is fine; `0` is the pre-versioning marker the
/// daemon stamps on first write and is treated as current.
let isCompatibleSchema (version: int) : bool =
    version = 0 || version >= ExpectedSchemaVersion

/// Format the dead-code report to the lines printed on stdout, matching the
/// standalone test-prune CLI's shape exactly. Pure over the analysis result so
/// the formatting is unit-testable.
let formatReport (verbose: bool) (result: VerboseDeadCodeResult) : string list =
    [ "Dead code analysis:"
      $"  Total symbols: %d{result.Base.TotalSymbols}"
      $"  Reachable: %d{result.Base.ReachableSymbols}"
      $"  Potentially unreachable: %d{result.UnreachableSymbols.Length}"
      if not result.UnreachableSymbols.IsEmpty then
          ""

          let byFile =
              result.UnreachableSymbols
              |> List.groupBy (fun u -> u.Symbol.SourceFile)
              |> List.sortBy fst

          for (file, symbols) in byFile do
              $"  %s{file} (%d{symbols.Length} unreachable):"

              for u in symbols |> List.sortBy (fun u -> u.Symbol.LineStart) do
                  if verbose then
                      let reason =
                          match u.Reason with
                          | NoIncomingEdges -> "no incoming edges"
                          | DisconnectedFromEntryPoints sources ->
                              let truncated =
                                  if sources.Length > 3 then
                                      (sources |> List.take 3 |> String.concat ", ") + ", ..."
                                  else
                                      sources |> String.concat ", "

                              $"has edges from: %s{truncated}"

                      $"    - %s{u.Symbol.FullName} (%A{u.Symbol.Kind}, line %d{u.Symbol.LineStart}) — %s{reason}"
                  else
                      $"    - %s{u.Symbol.FullName} (%A{u.Symbol.Kind}, line %d{u.Symbol.LineStart})" ]

/// Run dead-code analysis against an already-open symbol store. Pure over the
/// store so the analysis can be exercised end-to-end against a seeded temp DB
/// without the file-existence / schema plumbing. Returns the report lines.
let analyze (store: SymbolStore) (opts: DeadCodeOptions) : VerboseDeadCodeResult =
    let allSymbols = store.GetAllSymbols()
    let allNames = store.GetAllSymbolNames()
    let entryPoints = findEntryPoints allNames opts.EntryPatterns
    let reachable = store.GetReachableSymbols(entryPoints)
    let testMethodNames = store.GetTestMethodSymbolNames()

    let getIncomingBatch =
        if opts.Verbose then
            store.GetIncomingEdgesBatch
        else
            fun _ -> Map.empty

    let result, _events =
        findDeadCodeVerbose allSymbols reachable testMethodNames opts.IncludeTests getIncomingBatch

    result

/// Execute `fshw dead-code` against the daemon's symbol DB under `repoRoot`.
/// Injectable `fileExists` / `print` / `eprint` so the missing-DB and
/// incompatible-schema paths are unit-testable without a real DB or real stdout.
/// Returns the process exit code (0 on success, 1 on a missing/incompatible DB).
let run
    (fileExists: string -> bool)
    (readVersion: string -> int)
    (print: string -> unit)
    (eprint: string -> unit)
    (repoRoot: string)
    (opts: DeadCodeOptions)
    : int =
    let dbPath = Path.Combine(repoRoot, DbRelativePath)

    if not (fileExists dbPath) then
        eprint missingDbMessage
        1
    else
        let version = readVersion dbPath

        if not (isCompatibleSchema version) then
            eprint (incompatibleSchemaMessage version)
            1
        else
            // Schema-compatible: Database.create just opens it (WasRecreated stays
            // false for a compatible existing DB), so the daemon's graph is untouched.
            let db = Database.create dbPath
            let store = toSymbolStore db
            let result = analyze store opts

            for line in formatReport opts.Verbose result do
                print line

            0

/// Default `run` wiring: real filesystem, real schema probe, stdout/stderr.
let runDefault (repoRoot: string) (opts: DeadCodeOptions) : int =
    run File.Exists readSchemaVersion (printfn "%s") (eprintfn "%s") repoRoot opts
