/// ADR-010 increment 1 — seed `test-impact.db` on daemon start so a freshly
/// created workspace runs PRUNED tests from its first change instead of paying
/// the full-suite cold-start tax.
///
/// When a daemon starts in a workspace whose `.fshw/test-impact.db` is absent or
/// empty (0 indexed test methods), and a donor db with a matching schema
/// `user_version` is available, we copy the donor's symbol/dependency/test/
/// coverage graph into place BEFORE the TestPrune plugin opens the db for its
/// first selection. The portable graph carries over verbatim (`source_file` is
/// repo-relative, symbol identity is the fully-qualified name); the
/// workspace-local invalidation rows (`file_keys`/`project_keys`, which embed
/// absolute path + mtime) stay stale and simply force a SAFE re-index of those
/// files — over-indexing, never a stale verdict. See ADR-010.
///
/// Donor = the repo's DEFAULT (main) jj workspace `.fshw/test-impact.db`,
/// auto-detected via jj (overridable with `tests.seedTestImpactFrom` in
/// `.fshw.json`). If the current workspace IS the default, jj isn't present, or
/// no donor db exists → no-op (must never break non-jj repos or the default
/// workspace).
///
/// Mirrors `DeadCode.fs`'s seam style: pure decision functions
/// (`isCompatibleSchema`/`shouldSeed`/`resolveDonor`) over injected facts, plus a
/// thin `run` that wires real filesystem / jj / sqlite and is itself driven by
/// `seedDefault` at the call site.
module FsHotWatch.Cli.SeedTestImpact

open System.IO
open FsHotWatch

/// The local schema version this daemon writes. Sourced from TestPrune.Core's
/// public `Database.SchemaVersion` (same source as `DeadCode.ExpectedSchemaVersion`)
/// so the donor-compatibility guard moves in lockstep with the bundled Core: a
/// hardcoded copy would silently pass a stale donor through on a schema bump.
let LocalSchemaVersion = TestPrune.Database.SchemaVersion

/// The result of the seed decision — what we did and why, so the caller can log
/// a single legible line and tests can assert on the branch without inspecting
/// side effects.
type SeedDecision =
    /// Local db already has indexed test methods — nothing to do.
    | AlreadyPopulated of localCount: int
    /// No usable donor (current IS default workspace, jj absent, donor db
    /// missing). Carries a short reason for the log line.
    | NoDonor of reason: string
    /// Donor exists but its schema `user_version` doesn't match the local
    /// schema — skip rather than copy an unreadable graph.
    | SchemaMismatch of donorVersion: int * localVersion: int
    /// Seed it: copy `DonorDbPath` over the local db. `DonorTestMethodCount` is
    /// carried for the log line.
    | Seed of donorDbPath: string * donorTestMethodCount: int

/// Facts the seed decision is a pure function of. Gathered by the IO wrapper
/// (filesystem probes, jj invocation, sqlite reads) so the decision itself has
/// no dependencies and is exhaustively unit-testable with hand-built records.
type SeedFacts =
    {
        /// Does the LOCAL `.fshw/test-impact.db` exist?
        LocalExists: bool
        /// Count of indexed test methods in the LOCAL db (0 when absent/empty).
        LocalTestMethodCount: int
        /// Resolved donor db path, if a donor is available at all. `None` when the
        /// current workspace is the default, jj isn't present, or no donor file
        /// exists. Carries `Reason` for the log line in the `None` case.
        Donor: Result<DonorFacts, string>
    }

/// Facts about a resolved, on-disk donor db.
and DonorFacts =
    {
        DbPath: string
        /// `PRAGMA user_version` read from the donor file.
        SchemaVersion: int
        /// Count of indexed test methods in the donor (for the log line).
        TestMethodCount: int
    }

/// Pure schema guard, mirroring `DeadCode.isCompatibleSchema` semantics but
/// stricter: for SEEDING we require an EXACT match. A donor written by a
/// different schema version may have columns we can't read or be missing ones we
/// expect; copying it would corrupt the local graph. `0` (pre-versioning marker)
/// only matches a local `0`.
let isCompatibleDonorSchema (donorVersion: int) (localVersion: int) : bool = donorVersion = localVersion

/// The pure seed decision over gathered facts. The precedence — already-populated
/// beats no-donor beats schema guard — keeps the common steady-state case (a
/// populated local db) a cheap early no-op and never lets a donor problem mask
/// the fact that we didn't need a donor at all.
let shouldSeed (facts: SeedFacts) : SeedDecision =
    // A populated local db is authoritative: never overwrite a live graph.
    if facts.LocalExists && facts.LocalTestMethodCount > 0 then
        AlreadyPopulated facts.LocalTestMethodCount
    else
        match facts.Donor with
        | Error reason -> NoDonor reason
        | Ok donor ->
            if not (isCompatibleDonorSchema donor.SchemaVersion LocalSchemaVersion) then
                SchemaMismatch(donor.SchemaVersion, LocalSchemaVersion)
            else
                Seed(donor.DbPath, donor.TestMethodCount)

/// Resolve the `tests.seedTestImpactFrom` config value to an absolute donor
/// path. A rooted path is used verbatim; a relative one is resolved against
/// `repoRoot`. `None` → `None` (auto-detect the default workspace donor). Pure
/// over its inputs so both the rooted and relative branches are unit-testable
/// without touching the call site.
let resolveSeedDonorOverride (repoRoot: string) (configValue: string option) : string option =
    configValue
    |> Option.map (fun p ->
        if Path.IsPathRooted(p) then
            p
        else
            Path.GetFullPath(Path.Combine(repoRoot, p)))

/// Resolve the donor db path from the explicit config override (if any) else by
/// auto-detecting the default jj workspace. Pure over injected effects:
///   - `configOverride`: `tests.seedTestImpactFrom` from `.fshw.json`, already
///     made absolute by the caller. Takes precedence over auto-detection.
///   - `defaultWorkspaceRoot`: `Some <root>` when jj reports a default workspace
///     root DISTINCT from the current repo root; `None` when jj is absent, the
///     current workspace IS the default, or detection failed. (The caller folds
///     "is current the default" into this `None` so the decision stays pure.)
///   - `fileExists`: probe for the resolved donor db file.
/// Returns the donor db path (existence-checked) or a short reason it's absent.
let resolveDonorPath
    (configOverride: string option)
    (defaultWorkspaceRoot: string option)
    (fileExists: string -> bool)
    : Result<string, string> =
    match configOverride with
    | Some path ->
        if fileExists path then
            Ok path
        else
            Error $"seedTestImpactFrom donor db not found at %s{path}"
    | None ->
        match defaultWorkspaceRoot with
        | None -> Error "no default jj workspace donor (not a jj repo, or this IS the default workspace)"
        | Some root ->
            let donorPath = Path.Combine(FsHotWatch.FsHwPaths.root root, "test-impact.db")

            if fileExists donorPath then
                Ok donorPath
            else
                Error $"default workspace has no test-impact.db at %s{donorPath}"

/// Read `PRAGMA user_version` from the sqlite file at `dbPath` in read-only mode,
/// without going through TestPrune's `Database.create` (which would RECREATE on a
/// mismatch — exactly what we must avoid when probing a donor we may not copy).
/// Mirrors `DeadCode.readSchemaVersion`.
let readSchemaVersion (dbPath: string) : int =
    use conn =
        new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{dbPath};Mode=ReadOnly")

    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "PRAGMA user_version;"
    cmd.ExecuteScalar() :?> int64 |> int

/// Count indexed test methods in the sqlite file at `dbPath` (read-only). The
/// `test_methods` table is the canonical source — the same rows `DeadCode`'s
/// `GetTestMethodSymbolNames` reads. A db with 0 rows here is "empty" for seeding
/// purposes (it has no impact map, so prune would fall back to all-tests).
let countTestMethods (dbPath: string) : int =
    use conn =
        new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{dbPath};Mode=ReadOnly")

    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT count(*) FROM test_methods;"
    cmd.ExecuteScalar() :?> int64 |> int

/// Copy the donor db file over the local path (atomic temp-file + rename so a
/// crash mid-copy can't leave a half-written db). The directory is assumed to
/// exist (the call site creates `.fshw/` before invoking us). We do NOT touch the
/// donor's `file_keys`/`project_keys` rows — leaving them stale is intentional
/// and correctness-safe (they force a re-index; see ADR-010).
let copyDonor (copyFile: string -> string -> unit) (donorDbPath: string) (localDbPath: string) : unit =
    copyFile donorDbPath localDbPath

/// Run the seed step. Pure decision (`shouldSeed`/`resolveDonorPath`) wrapped in
/// injected IO so the missing-donor / schema-mismatch / already-populated /
/// seed-now branches are all unit-testable without a real jj repo or real sqlite
/// files. Logs a single legible line per branch and performs the copy on `Seed`.
///
/// Injected effects:
///   - `fileExists`           — local + donor existence probes
///   - `readVersion`          — donor `PRAGMA user_version`
///   - `countMethods`         — `count(*)` of `test_methods` in a db
///   - `defaultWorkspaceRoot` — resolved default-workspace root or `None`
///   - `copyFile`             — donor → local copy
///   - `info`                 — log sink (tag, message)
/// Returns the `SeedDecision` so the call site / tests can observe the outcome.
let run
    (fileExists: string -> bool)
    (readVersion: string -> int)
    (countMethods: string -> int)
    (copyFile: string -> string -> unit)
    (info: string -> string -> unit)
    (configOverride: string option)
    (defaultWorkspaceRoot: string option)
    (localDbPath: string)
    : SeedDecision =
    let localExists = fileExists localDbPath

    let localCount =
        if localExists then
            try
                countMethods localDbPath
            with _ ->
                // An unreadable/locked local db is treated as empty: the worst
                // case is we attempt a seed that the copy step then handles, and
                // we never overwrite a db that genuinely has test methods.
                0
        else
            0

    let donor =
        match resolveDonorPath configOverride defaultWorkspaceRoot fileExists with
        | Error reason -> Error reason
        | Ok donorPath ->
            try
                Ok
                    { DbPath = donorPath
                      SchemaVersion = readVersion donorPath
                      TestMethodCount = countMethods donorPath }
            with ex ->
                Error $"donor db at %s{donorPath} could not be read: %s{ex.Message}"

    let facts =
        { LocalExists = localExists
          LocalTestMethodCount = localCount
          Donor = donor }

    let decision = shouldSeed facts

    match decision with
    | AlreadyPopulated count ->
        info "seed-test-impact" $"Local test-impact.db already has %d{count} indexed test methods — not seeding."
    | NoDonor reason -> info "seed-test-impact" $"Not seeding test-impact.db: %s{reason}."
    | SchemaMismatch(donorVersion, localVersion) ->
        info
            "seed-test-impact"
            $"Not seeding test-impact.db: donor schema v%d{donorVersion} ≠ local schema v%d{localVersion} (would copy an unreadable graph)."
    | Seed(donorDbPath, donorCount) ->
        copyDonor copyFile donorDbPath localDbPath

        info
            "seed-test-impact"
            $"Seeded test-impact.db from %s{donorDbPath} (%d{donorCount} test methods) — fresh workspace will run pruned from its first change. Invalidation rows stay workspace-local (safe re-index)."

    decision

/// Resolve the DEFAULT jj workspace root, or `None` when this isn't a jj repo,
/// jj isn't on PATH, or the current workspace IS the default (so there's no donor
/// distinct from us). Shells out to `jj workspace root` (current) and
/// `jj workspace root --name default`; any failure → `None` (never break a
/// non-jj repo). The two roots are canonicalized before comparison so symlinked
/// temp paths (macOS `/var` → `/private/var`) don't spuriously differ.
let resolveDefaultWorkspaceRoot (repoRoot: string) : string option =
    let runJj (args: string list) : string option =
        try
            let psi = System.Diagnostics.ProcessStartInfo()
            psi.FileName <- "jj"
            args |> List.iter psi.ArgumentList.Add
            psi.WorkingDirectory <- repoRoot
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            use p = System.Diagnostics.Process.Start(psi)
            let out = p.StandardOutput.ReadToEnd()
            p.WaitForExit()

            if p.ExitCode = 0 then
                let trimmed = out.Trim()

                if System.String.IsNullOrWhiteSpace trimmed then
                    None
                else
                    Some trimmed
            else
                None
        with _ ->
            None

    let canonical (path: string) =
        try
            Path.GetFullPath(path)
        with _ ->
            path

    match runJj [ "workspace"; "root"; "--name"; "default" ] with
    | None -> None
    | Some defaultRoot ->
        // If the current repo root IS the default workspace root, there's no
        // distinct donor — return None so the decision short-circuits to NoDonor.
        let currentRoot =
            runJj [ "workspace"; "root" ] |> Option.defaultValue repoRoot |> canonical

        if canonical defaultRoot = currentRoot then
            None
        else
            Some defaultRoot

/// Default wiring: real filesystem, real sqlite probes, real jj detection, real
/// file copy, `Logging.info`. Called at daemon start (DaemonConfig) before the
/// TestPrune plugin opens the db. `configOverride` is `tests.seedTestImpactFrom`
/// made absolute by the caller (or `None`).
let seedDefault (repoRoot: string) (localDbPath: string) (configOverride: string option) : SeedDecision =
    let copyFile (src: string) (dst: string) =
        let tmp = dst + ".seed.tmp"
        File.Copy(src, tmp, overwrite = true)
        File.Move(tmp, dst, overwrite = true)

    run
        File.Exists
        readSchemaVersion
        countTestMethods
        copyFile
        Logging.info
        configOverride
        (resolveDefaultWorkspaceRoot repoRoot)
        localDbPath
