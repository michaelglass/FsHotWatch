/// Keeping the plugin's index: FCS error counts, flushing pending analysis, schema-drift repair and quarantine, the check cache, and the plugin's cache key.
module FsHotWatch.TestPrune.IndexMaintenance

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
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.RuntimeCoverageSelection
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.ExternalInputs
open FsHotWatch.TestPrune.RunResults
open FsHotWatch.TestPrune.ReceiptTransitions
open FsHotWatch.TestPrune.SelectionAssembly
open FsHotWatch.TestPrune.OutstandingFailures
open FsHotWatch.TestPrune.TestFilters
open FsHotWatch.TestPrune.FailureReports
open FsHotWatch.TestPrune.RunnerArgs
open FsHotWatch.TestPrune.TestReports
open FsHotWatch.TestPrune.PluginQueries
open FsHotWatch.TestPrune.PluginCommands

/// Count of Error-severity diagnostics not suppressed (`FcsSuppression.blockingErrors`) —
/// used only for the skip-log message so operators have a number to correlate against the
/// FCS-error stream. `hasFcsErrors` is defined by it, so the count matches the gate.
let internal fcsErrorCount (suppression: Suppression) (state: FileCheckState) : int =
    match state with
    | FullCheck(_, cr) -> (blockingErrors suppression cr.Diagnostics).Length
    // The diagnostics the filed check reported (ADR-059 §17).
    | Replayed outputs -> (blockingErrors suppression (FileCheckResult.diagnosticsOfOutputs outputs)).Length
    | ParseOnly _ -> 0

/// FCS cache-poisoning gate. A `FileChecked` whose FCS result reports any
/// Error-severity diagnostic is untrustworthy: cold-start FCS sometimes returns
/// "expected type X but here has type X" for files that compile cleanly once warm, and
/// flushing those poisoned symbols overwrites the prior good DB snapshot. Gated by
/// SEVERITY, not message text, so the cold-start race and the user-broke-their-code case
/// are handled identically — both hold the prior DB row. `ParseOnly` (check aborted)
/// counts as "no observable errors".
///
/// `suppression` (`FileCheckResult.suppression`) is applied by
/// `FcsSuppression.blockingErrors`, so the gate applies the same filter as the
/// user-visible error stream in `Daemon.reportFcsDiagnostics` and the analysis evidence. Without that symmetry the
/// gate trips on codes the user has already silenced (e.g. FS1182 promoted to Error by
/// `<TreatWarningsAsErrors>` but suppressed via `#nowarn "1182"`), killing cache-replay
/// across daemon restarts on every cold scan.
let internal hasFcsErrors (suppression: Suppression) (state: FileCheckState) : bool =
    fcsErrorCount suppression state > 0

/// The label the test-impact DB write declares over itself: how much it writes and where,
/// so a wait or wedge line names the database rather than the plugin alone.
let internal dbWriteLabel (dbPath: string) (fileCount: int) =
    $"test-impact DB write: %d{fileCount} file(s) to %s{dbPath}"

/// How long this daemon's connections to its own test-impact DB wait on a lock another
/// connection holds. A cold indexer flush holds the write lock in one transaction for as long
/// as the flush takes (58s for 2523 files on a loaded box), and the ratchet writer queues
/// behind it by design (AUTOMATION-784). TestPrune.Core's 30s default refused that writer
/// with `database is locked` during exactly that flush, so this is set well past it. A holder
/// that outlasts even this is reported as a held lock (`namedDbWriteFailure`), never a wedge.
let internal ImpactDbBusyTimeout = TimeSpan.FromMinutes 5.0

/// The busy timeout the test-impact DB's connections carry: how long a write waits on a lock
/// another connection holds before SQLite refuses it. Read from a connection the database
/// opened, so the bound a blocked write reports is the one it ran out of, not a restatement.
let internal dbBusyTimeout (db: Database) : TimeSpan =
    use conn = db.OpenConnection()
    TimeSpan.FromSeconds(float conn.DefaultTimeout)

/// A test-impact DB write SQLite refused (`SQLITE_BUSY` 5 / `SQLITE_LOCKED` 6) because another
/// connection held the lock for the whole busy timeout (`dbBusyTimeout`) is re-raised naming
/// the database, the write, how long it waited and the bound that expired. SQLite's own
/// "database is locked" names none of them, and an expired busy timeout is a held lock, not a
/// wedge. Every other failure passes through untouched, so schema-drift repair still reads
/// the exception it raised.
let internal namedDbWriteFailure
    (dbPath: string)
    (busyTimeout: TimeSpan)
    (fileCount: int)
    (waited: TimeSpan)
    (ex: exn)
    : exn =
    match ex with
    | :? Microsoft.Data.Sqlite.SqliteException as sqlite when sqlite.SqliteErrorCode = 5 || sqlite.SqliteErrorCode = 6 ->
        TimeoutException(
            $"test-impact DB write blocked for %s{PluginWedge.formatElapsed waited}: SQLite busy timeout (%s{PluginWedge.formatElapsed busyTimeout}) expired — %s{dbPath} is locked by another connection, so %d{fileCount} file(s) were not written ({sqlite.Message}). "
            + "Another fshw daemon or CLI on the same .fshw/ directory, or any process holding a write transaction on that file, holds it.",
            ex
        )
        :> exn
    | other -> other

/// Flush accumulated per-file analysis results to the DB in a single RebuildProjects
/// call. Pure function: takes state, returns updated state.
///
/// The results go to `RebuildProjects` ONE PER FILE, never merged per project.
/// TestPrune.Core attributes each edge, test method and attribute to the file whose
/// `AnalysisResult` carried it, so re-indexing a file replaces exactly that file's facts.
/// A merged result carrying a signature (`.fsi`) and its implementation would credit the
/// signature's facts to whichever file declared the shared name last, and a later flush
/// of that file alone would delete them.
let internal flushPendingAnalysis
    (db: Database)
    (dbPath: string)
    (busyTimeout: TimeSpan)
    (declare: string -> IDisposable)
    (state: TestPruneState)
    =
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
        // Each file's analysis identity goes in the same transaction as its symbols, so
        // the index never claims an analysis it rolled back (`IndexedAnalysis`).
        //
        // Declared over itself, inside the impact selection's declaration: a write blocked on
        // a lock or crawling through contention then reads as THIS write, with its size, its
        // database and how far into its bound it is, rather than as a fold that never
        // returned. Timed and logged when it returns, so daemon.log shows where the time went.
        let label = dbWriteLabel dbPath allResults.Count
        let clock = Diagnostics.Stopwatch.StartNew()

        do
            use _declaration = declare label

            try
                db.RebuildProjects(Seq.toList allResults, fileKeys = Map.toList state.PendingIdentities)
            with ex ->
                match namedDbWriteFailure dbPath busyTimeout allResults.Count clock.Elapsed ex with
                | named when obj.ReferenceEquals(named, ex) -> reraise ()
                | named -> raise named

        Logging.info
            "test-prune"
            $"Wrote %d{allResults.Count} file(s) to %s{dbPath} in %s{PluginWedge.formatElapsed clock.Elapsed}"

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

/// Where a daemon sets aside its own index when a NEWER TestPrune.Core wrote it:
/// `test-impact.db.schema-v<N>`, beside the original. The suffix names the schema that
/// wrote it, so a consumer at that schema or newer can recognise the file and reuse it
/// (renaming it back); this consumer never opens it.
let internal quarantinedIndexPath (dbPath: string) (foundVersion: int) : string = $"%s{dbPath}.schema-v%d{foundVersion}"

/// Open the daemon's own impact index at `dbPath`. When a NEWER TestPrune.Core wrote it
/// (`SchemaNewerThanConsumerException` naming this very file), the index is a cache this
/// consumer can rebuild, so it is moved aside to `quarantinedIndexPath` with its SQLite
/// sidecars, kept intact, and a fresh index is opened in its place. Before this, the
/// refusal escaped plugin registration: a land worktree left forward-migrated by a failed
/// pin bump stopped every later, older-pinned daemon from starting at all.
///
/// TestPrune.Core's refusal itself stands: it must never downgrade a newer file in place.
let internal openOwnIndex (dbPath: string) (walkPolicy: WalkPolicy) : Database =
    try
        Database.create (dbPath, walkPolicy, ImpactDbBusyTimeout)
    with SchemaNewerThanConsumerException(_, found, supported) ->
        let aside = quarantinedIndexPath dbPath found

        for ext in [ ""; "-wal"; "-shm" ] do
            if File.Exists(dbPath + ext) then
                File.Move(dbPath + ext, aside + ext, true)
            elif File.Exists(aside + ext) then
                // A sidecar left by an earlier quarantine must not pair with this file.
                File.Delete(aside + ext)

        Logging.warn
            "test-prune"
            $"%s{dbPath} was written by TestPrune.Core schema v%d{found}; this daemon supports schema v%d{supported}. Moved it aside to %s{aside} (kept for a consumer at schema v%d{found} or newer) and starting on a fresh test-impact index."

        let fresh = Database.create (dbPath, walkPolicy, ImpactDbBusyTimeout)

        Logging.info
            "test-prune"
            $"Started a fresh test-impact index at %s{dbPath} (schema v%d{supported}); it rebuilds as files are checked."

        fresh

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
    // The content identity of the tree this event is about (`ReceiptInputTree.read`): the
    // term that PINS THE TREE (ADR-059 §4). The changed-symbols term cannot, since on a cold
    // scan this event is dispatched before the FCS pass; so two clean-building trees used to
    // compute one key. `None` (a hole, an unreadable file) makes the event uncacheable rather
    // than keying it on a placeholder two different trees would share.
    (inputTree: unit -> string option)
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
    let outcomeKey (buildOutcome: string) : ContentHash option =
        inputTree ()
        |> Option.map (fun tree ->
            FsHotWatch.TaskCache.merkleCacheKey (
                [ "plugin-version", "test-prune-merkle-v2"
                  "event", "BuildCompleted"
                  "input-tree", tree
                  "changed-symbols", changedSymbolsHash ()
                  // The one term that pins the SHAPE of the tree. The
                  // changed-symbols term cannot: on a scan this event is dispatched before
                  // the FCS pass, so it is empty whatever the tree holds.
                  "project-structure", projectStructureHash ()
                  "build-outcome", buildOutcome ]
                @ optionalEntry "pending-queue" (pendingQueueHash ())
                @ optionalEntry "depends-on" (dependsOnHash ())
                @ optionalEntry "full-suite-scope" (fullSuiteScopeHash ())
            ))

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
        | None, false, true -> outcomeKey "succeeded"
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
        let provenFullSuite =
            completed.Verification = Ran FullSuite
            && noProjectRefutedOrUnrun
            && notAborted
            && not allZeroMatchRun

        if
            provenFullSuite
            && (pendingQueueHash ()).IsNone
            && not (hasOutstandingFailures ())
        then
            outcomeKey "succeeded"
        else
            None
    | BuildCompleted(BuildFailed errs) ->
        // Shares `outcomeKey` with the BuildSucceeded arm so the salt and the
        // pending-queue/dependsOn entries can never split across the two.
        outcomeKey ("failed:" + String.concat "|" (List.sort errs))
    | FileChecked r ->
        // `fcs-signature` captures cross-file FCS state so upstream symbol changes
        // invalidate this file's cached symbol-diff.
        //
        // Note what this arm does NOT read: not the changed symbols, not the pending
        // queue, not the dependsOn globs. It is a pure function of THIS file — which is
        // why all three are thunks.
        let fcsSignature = FsHotWatch.CheckCache.fcsCheckSignature r

        // A per-file cache hit derives a terminal summary from the plugin's whole live
        // ledger and labels it `(cached)`. While a fresh test failure is outstanding,
        // that would falsely relabel the test run as replayed merely because a helper
        // file's earlier symbol analysis hit its cache. Refuse the per-file replay until
        // a covering test run clears the red; healthy trees keep the hot path.
        if hasOutstandingFailures () then
            None
        // The entry is written for every analysis and READ only where the index holds it
        // (`IndexedAnalysis`, through the state's `IFileCacheReadGuard`).
        else
            Some(
                FsHotWatch.TaskCache.merkleCacheKey
                    [ "plugin-version", "test-prune-merkle-v2"
                      "event", "FileChecked"
                      "file", AbsFilePath.value r.File
                      "source", r.Source
                      "fcs-signature", fcsSignature ]
            )
    | _ -> None
