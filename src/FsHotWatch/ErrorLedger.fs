module FsHotWatch.ErrorLedger

/// Diagnostic severity levels for error entries.
type DiagnosticSeverity =
    | Error
    | Warning
    | Info
    | Hint
    /// NOT a defect and NOT a pass: the work this entry describes DID NOT RUN (a test
    /// project deferred because its build artifact wasn't produced yet — the "waiting
    /// on build" case). It denies a green verdict without counting as a failure: the
    /// verdict routes it to `Incomplete`/exit 2, never `FailuresFound`/exit 1. Hence
    /// distinct from green-compatible `Info`/`Hint` and from `Error`/`Warning`.
    | Deferred
    /// NOT a defect and NOT a pass: the RUNNER DIED. A test host that was killed
    /// mid-run (the box ran out of CPU or memory, something reaped it, the runtime
    /// aborted) verified nothing, and nothing it half-wrote is a finding about the
    /// code. Like `Deferred` it denies a green without counting as a failure —
    /// `RunnerAbort` routes it to `CheckOutcome.RunnerAborted`/exit 2, never
    /// `FailuresFound`/exit 1.
    ///
    /// Its own case rather than a reuse of `Deferred`, because the remedies differ and
    /// `Deferred`'s words are false here: "waiting on build — re-run once the build
    /// settles" describes a race that settles on its own, and a host killed under load
    /// does not settle, it needs a quieter box.
    | HostAborted

/// A single diagnostic entry from a plugin.
type ErrorEntry =
    {
        Message: string
        Severity: DiagnosticSeverity
        Line: int
        Column: int
        /// Optional full output (e.g. complete test stdout for println debugging).
        Detail: string option
    }

/// Interface for receiving error ledger mutation notifications.
type IErrorReporter =
    abstract Report: plugin: string -> file: string -> entries: ErrorEntry list -> unit
    abstract Clear: plugin: string -> file: string -> unit
    abstract ClearPlugin: plugin: string -> unit
    abstract ClearAll: unit -> unit

module DiagnosticSeverity =
    let toString (severity: DiagnosticSeverity) =
        match severity with
        | Error -> "error"
        | Warning -> "warning"
        | Info -> "info"
        | Hint -> "hint"
        | Deferred -> "deferred"
        | HostAborted -> "aborted"

    let fromString (s: string) =
        match s with
        | "error" -> Some Error
        | "warning" -> Some Warning
        | "info" -> Some Info
        | "hint" -> Some Hint
        | "deferred" -> Some Deferred
        | "aborted" -> Some HostAborted
        | _ -> None

    let order (severity: DiagnosticSeverity) =
        match severity with
        | Hint -> 0
        | Info -> 1
        // Deferred ranks between Info and Warning: louder than informational (it
        // denies a green) but not a defect (nothing failed).
        | Deferred -> 2
        // Ranks with `Deferred`, and for the same reason: both say "this did not run".
        // Louder than informational, quieter than a defect — because nothing failed.
        | HostAborted -> 2
        | Warning -> 3
        | Error -> 4

module ErrorEntry =
    /// True if the entry counts as a failure given the warningsAreFailures flag.
    /// Neither `Deferred` nor `HostAborted` is ever a failure — see the DU cases.
    let isFailing (warningsAreFailures: bool) (e: ErrorEntry) : bool =
        match e.Severity with
        | Error -> true
        | Warning -> warningsAreFailures
        | Info
        | Hint
        | Deferred
        | HostAborted -> false

    /// True iff this entry is a "waiting on build" deferral: tests did not run because
    /// a build artifact wasn't ready. Not a failure; see `isFailing`.
    let isWaitingOnBuild (e: ErrorEntry) : bool = e.Severity = Deferred

    /// True iff this entry records a RUNNER that died: the test host was killed
    /// mid-run, so nothing it was asked to verify was verified. Not a failure; see
    /// `isFailing`.
    let isRunnerAbort (e: ErrorEntry) : bool = e.Severity = HostAborted

    /// Create an Error-severity entry with no source location.
    let error (message: string) : ErrorEntry =
        { Message = message
          Severity = Error
          Line = 0
          Column = 0
          Detail = None }

    /// Create an Error-severity entry with detail (e.g. full test output).
    let errorWithDetail (message: string) (detail: string) : ErrorEntry =
        { Message = message
          Severity = Error
          Line = 0
          Column = 0
          Detail = Some detail }

    /// Create a Warning-severity entry with detail. For conditions that deny a
    /// clean verdict under the default warn-fail policy but are not themselves a
    /// failed check — e.g. a source file the symbol analyser could not read, which
    /// leaves a hole in the impact graph the gate must not silently paper over.
    let warningWithDetail (message: string) (detail: string) : ErrorEntry =
        { Message = message
          Severity = Warning
          Line = 0
          Column = 0
          Detail = Some detail }

    /// Create a `Deferred`-severity entry with detail — a test project whose build
    /// artifact wasn't ready, so its tests did NOT run.
    let deferredWithDetail (message: string) (detail: string) : ErrorEntry =
        { Message = message
          Severity = Deferred
          Line = 0
          Column = 0
          Detail = Some detail }

    /// Create a `HostAborted`-severity entry with detail — a test host that was KILLED
    /// mid-run, so its project verified nothing and reported no finding.
    let abortedWithDetail (message: string) (detail: string) : ErrorEntry =
        { Message = message
          Severity = HostAborted
          Line = 0
          Column = 0
          Detail = Some detail }

/// How much of the ledger a single mirror of it — a file on disk, an IPC reply — is
/// allowed to carry.
///
/// `Message` and `Detail` are plugin-supplied text with no natural bound, and one
/// plugin makes that unboundedness MULTIPLICATIVE: the test-prune plugin attaches the
/// whole captured project output to EVERY parsed per-test failure, so a broadly red
/// project contributes `failures × |output|` characters, not `failures + |output|`.
/// The run this module exists because of had 753 failing tests in one project whose
/// captured output was 48 MB: 36 GB of `detail` for that project alone, from a ledger
/// holding one shared string. Nothing can serialize that, and nothing can hold it.
///
/// `FileErrorReporter` has capped its own fields since the same product crashed the
/// on-disk mirror. The IPC mirror had no cap at all, so the identical defect survived
/// one mirror over and killed the merge gate instead of the reporter. The bound
/// therefore lives HERE, above every mirror, rather than in the mirror that noticed
/// it first — a cap that one writer applies and another forgets is the shape of this
/// whole bug.
///
/// Entries are never DROPPED, only their text trimmed: the entry COUNT and the
/// severities are what the exit code and the verdict's `reddenedBy` are computed
/// from, so a bound that removed entries would buy memory by corrupting the answer.
module Transport =
    /// Per-field cap. System.Text.Json's UTF-8 transcoder throws `OverflowException`
    /// on a single string token beyond ~700M chars, so this is far below the point
    /// where any one field is even representable; it is sized for READABILITY — an
    /// excerpt an operator can scan — because the full text always exists elsewhere
    /// (the plugin's own run log, `.fshw/test-runs/<runId>/<project>.output.log` for
    /// a test failure).
    [<Literal>]
    let MaxFieldChars = 20000

    /// Cap on the total `detail` text in ONE response, across every entry in it.
    ///
    /// The per-field cap alone does not bound a response: 2,414 entries each holding a
    /// 20,000-char excerpt is still ~48 MB of pure `detail`, and `detail` is the one
    /// field no consumer of this ledger reads — the terminal renders `Message`, the
    /// verdict records `Message`. So the response budget is deliberately much smaller
    /// than `MaxFieldChars × entries`: the first few details are worth carrying, the
    /// two-thousandth is not, and the marker says so rather than pretending the entry
    /// had none.
    [<Literal>]
    let MaxDetailCharsPerResponse = 262144

    /// Stands in for a `detail` this response had no budget left to carry. Says which
    /// bound was hit and where the untruncated text lives, because "detail: null" and
    /// "detail: elided" are different facts and only one of them means "go and look".
    /// Short on purpose: it is repeated once per entry past the budget, and a paragraph
    /// repeated two thousand times is the very shape being bounded.
    [<Literal>]
    let DetailBudgetSpentMarker =
        "… [detail omitted: this response's detail budget was spent by earlier entries; see the plugin's own run log]"

    /// Trim one field to `MaxFieldChars`, naming how much was dropped. Total: a null
    /// or already-short field is returned unchanged.
    let truncateField (s: string) : string =
        if isNull s || s.Length <= MaxFieldChars then
            s
        else
            let dropped = s.Length - MaxFieldChars
            s.Substring(0, MaxFieldChars) + $"… [truncated %d{dropped} chars]"

    /// What one entry's `detail` may carry, given the characters earlier entries in
    /// the SAME response have already spent, and what the budget stands at afterwards.
    ///
    /// Pure and total, so the bound is assertable without a socket: fold it over a
    /// response's entries in wire order and the response can never exceed
    /// `MaxDetailCharsPerResponse` plus one field's worth of overshoot from the entry
    /// that crossed the line.
    let takeDetail (spent: int) (detail: string option) : string option * int =
        match detail with
        | None -> None, spent
        | Some d when isNull d -> None, spent
        | Some _ when spent >= MaxDetailCharsPerResponse -> Some DetailBudgetSpentMarker, spent
        | Some d ->
            let capped = truncateField d
            Some capped, spent + capped.Length

/// Per-plugin tally of ledger entries by severity — a lightweight projection of the ledger
/// used by the status renderer to decide the "completed-with-issues" glyph without pulling
/// the full entry list across the IPC wire.
type DiagnosticCounts = { Errors: int; Warnings: int }

module DiagnosticCounts =
    let empty = { Errors = 0; Warnings = 0 }

    let private bumpOne (d: DiagnosticCounts) (entry: ErrorEntry) =
        match entry.Severity with
        | Error -> { d with Errors = d.Errors + 1 }
        | Warning -> { d with Warnings = d.Warnings + 1 }
        | _ -> d

    /// Fold a sequence of ledger entries into counts.
    let ofEntries (entries: seq<ErrorEntry>) : DiagnosticCounts = entries |> Seq.fold bumpOne empty

    /// True if these counts should be treated as a failure under the current policy.
    let isFailing (warningsAreFailures: bool) (d: DiagnosticCounts) =
        d.Errors > 0 || (warningsAreFailures && d.Warnings > 0)

    /// Human-readable summary, omitting zero components. Empty string when both zero.
    let summary (d: DiagnosticCounts) =
        match d.Errors, d.Warnings with
        | 0, 0 -> ""
        | e, 0 -> $"%d{e} error(s)"
        | 0, w -> $"%d{w} warning(s)"
        | e, w -> $"%d{e} error(s), %d{w} warning(s)"

type private LedgerState =
    { Errors: Map<struct (string * string), ErrorEntry list>
      Versions: Map<struct (string * string), int64>
      EntryRevisions: Map<struct (string * string), int64>
      NextRevision: int64 }

type internal LedgerKeyRevision =
    { Plugin: string
      File: string
      Revision: int64 }

/// Plugin name under which the ledger self-reports its own reporter failures.
/// A reporter that throws while persisting a diagnostic means "we could not
/// record the errors" — which must read as non-clean, never as silence. The
/// synthetic Error entry lands in the same `state.Errors` map that GetAll /
/// FailingReasons / HasFailingReasons (and thus the CLI exit code) consult.
[<Literal>]
let reporterFailurePlugin = "error-ledger"

let private isFailing warningsAreFailures e =
    ErrorEntry.isFailing warningsAreFailures e

/// Build a synthetic Error entry describing reporters that threw while a given
/// plugin's diagnostics were being recorded, naming the failing plugin and the
/// exception(s) so the verdict and the daemon log agree.
let private syntheticReporterFailure (plugin: string) (entryCount: int) (failures: exn list) : ErrorEntry =
    let detail = failures |> List.map (fun ex -> ex.ToString()) |> String.concat "\n\n"

    let exSummary =
        failures
        |> List.map (fun ex -> ex.GetType().Name)
        |> List.distinct
        |> String.concat ", "

    ErrorEntry.errorWithDetail $"failed to record %d{entryCount} diagnostic(s) from %s{plugin}: %s{exSummary}" detail

/// Check version and advance if accepted. Returns (accepted, newState).
let private tryAcceptVersion key (v: int64) (state: LedgerState) =
    match Map.tryFind key state.Versions with
    | Some last when v < last -> false, state
    | _ ->
        true,
        { state with
            Versions = Map.add key v state.Versions }

let private markPresent key (state: LedgerState) =
    let revision = state.NextRevision + 1L

    { state with
        EntryRevisions = Map.add key revision state.EntryRevisions
        NextRevision = revision }

let private markAbsent key (state: LedgerState) =
    { state with
        EntryRevisions = Map.remove key state.EntryRevisions }

/// Accumulates per-file errors from plugins. Errors auto-clear when a file
/// is re-checked and passes. Supports optional version-guarded updates: when a
/// version is provided, stale updates (version < last accepted) are silently ignored.
///
/// Writes are serialized under a lock and applied on the writer's own thread; each
/// publishes a new immutable `LedgerState` with a volatile write. Reads take no lock:
/// they answer from the last published state. The verdict, the RPC status and the
/// wedge monitor read the ledger on the gate's path, so no read depends on a
/// thread-pool thread being scheduled for it: under a saturated pool that dependence
/// turns a healthy daemon's reads into timeouts. Because a write is applied before
/// `Report`/`Clear` returns, it is visible to the next read on any thread: a plugin
/// that reports findings and then goes terminal is never read as terminal and clean.
type ErrorLedger(?reporters: IErrorReporter list, ?logError: string -> string -> unit) =
    let reporters = defaultArg reporters []

    // Reporter-failure log sink. Injectable so a test can capture it without
    // redirecting process-global `System.Console.Error`: emission happens on
    // whichever thread wrote, so a `Console.Error` capture would race any
    // concurrent `Console.SetError`.
    let logError = defaultArg logError Logging.error

    // IErrorReporter is a third-party-extension boundary, so the broad catch keeps a
    // misbehaving reporter from stopping the ledger. Logs ex.ToString(), not
    // ex.Message, to preserve the stack trace. Surviving must not erase the verdict, so
    // the exceptions are returned (empty when all succeeded) and the caller
    // self-reports them — see `syntheticReporterFailure`.
    let notifyReporters action : exn list =
        let mutable failures = []

        for r in reporters do
            try
                action r
            with ex ->
                logError "error-ledger" $"Reporter failed: %s{ex.ToString()}"
                failures <- ex :: failures

        List.rev failures

    let writeGate = obj ()

    let mutable state: LedgerState =
        { Errors = Map.empty
          Versions = Map.empty
          EntryRevisions = Map.empty
          NextRevision = 0L }

    // A write that throws is a programming bug. It latches here and every later read
    // raises it: a ledger that lost a write must never answer as a ledger with
    // nothing to say, since that would be a green verdict over unrecorded findings.
    let mutable fault: exn option = None
    let crashed = Event<exn>()

    let report plugin file (entries: ErrorEntry list) version (state: LedgerState) =
        let key = struct (plugin, file)

        let accepted, state' =
            match version with
            | Some v -> tryAcceptVersion key v state
            | None -> true, state

        // The synthetic key under which a reporter-failure for this plugin/file is
        // tracked, so a later clean re-report of the same file clears the stale alarm.
        let failureKey = struct (reporterFailurePlugin, file)

        if not accepted then
            state'
        elif entries.IsEmpty then
            notifyReporters (fun r -> r.Clear plugin file) |> ignore

            { state' with
                Errors = state'.Errors |> Map.remove key |> Map.remove failureKey }
            |> markAbsent key
            |> markAbsent failureKey
        else
            let failures = notifyReporters (fun r -> r.Report plugin file entries)

            let errors = Map.add key entries state'.Errors

            let errors =
                if List.isEmpty failures then
                    // Reporters persisted cleanly: drop any prior failure alarm.
                    Map.remove failureKey errors
                else
                    // A reporter could not persist these diagnostics. Self-report so the
                    // aggregate verdict / exit code is non-clean rather than falsely
                    // green (the diagnostics may be lost on disk).
                    Map.add failureKey [ syntheticReporterFailure plugin entries.Length failures ] errors

            let next = { state' with Errors = errors } |> markPresent key

            if List.isEmpty failures then
                next |> markAbsent failureKey
            else
                next |> markPresent failureKey

    let clear plugin file version (state: LedgerState) =
        let key = struct (plugin, file)

        let accepted, state' =
            match version with
            | Some v -> tryAcceptVersion key v state
            | None -> true, state

        if accepted then
            // A failed Clear is logged but not self-reported: unlike a failed Report, it
            // can only leave a stale-red on-disk file — never a false-green — and the
            // verdict reads the in-memory ledger (cleared here) regardless.
            notifyReporters (fun r -> r.Clear plugin file) |> ignore

            { state' with
                Errors = Map.remove key state'.Errors }
            |> markAbsent key
        else
            state'

    let clearPlugin plugin (state: LedgerState) =
        // See the Clear symmetry note: a failed ClearPlugin is stale-red, not
        // false-green, so it is logged but not self-reported.
        notifyReporters (fun r -> r.ClearPlugin plugin) |> ignore

        { state with
            Errors = state.Errors |> Map.filter (fun (struct (p, _)) _ -> p <> plugin)
            Versions = state.Versions |> Map.filter (fun (struct (p, _)) _ -> p <> plugin)
            EntryRevisions = state.EntryRevisions |> Map.filter (fun (struct (p, _)) _ -> p <> plugin) }

    let pruneIfCurrent (candidates: LedgerKeyRevision list) (state: LedgerState) =
        let mutable next = state
        let mutable removed = 0

        for candidate in candidates do
            let key = struct (candidate.Plugin, candidate.File)

            if Map.tryFind key next.EntryRevisions = Some candidate.Revision then
                notifyReporters (fun r -> r.Clear candidate.Plugin candidate.File) |> ignore

                next <-
                    { next with
                        Errors = Map.remove key next.Errors }
                    |> markAbsent key

                removed <- removed + 1

        next, removed

    /// Apply one write under the lock and publish its result. `None` when the ledger
    /// has latched a fault, before or during this write.
    let write (mutation: LedgerState -> LedgerState * 'r) : 'r option =
        let outcome =
            lock writeGate (fun () ->
                match fault with
                | Some _ -> None
                | None ->
                    // No inner recovery: anything that throws here is a programming bug.
                    // It latches, so every later read raises it rather than answering.
                    try
                        let next, result = mutation state
                        System.Threading.Volatile.Write(&state, next)
                        Some(Result.Ok result)
                    with ex ->
                        System.Threading.Volatile.Write(&fault, Some ex)
                        Some(Result.Error ex))

        match outcome with
        | None -> None
        | Some(Result.Ok result) -> Some result
        | Some(Result.Error ex) ->
            // Log loudly with the full stack trace so the bug is debuggable, then tell
            // subscribers, outside the lock.
            Logging.error "error-ledger" $"Ledger write failed (programming bug, ledger stopped): %s{ex.ToString()}"
            crashed.Trigger ex
            None

    let writeState (mutation: LedgerState -> LedgerState) =
        write (fun s -> mutation s, ()) |> ignore

    /// The last published state, or the latched fault.
    let read () : LedgerState =
        match System.Threading.Volatile.Read(&fault) with
        | Some ex -> raise (System.InvalidOperationException("The error ledger stopped after a failed write.", ex))
        | None -> System.Threading.Volatile.Read(&state)

    /// Set errors for a plugin + file. Replaces previous. Empty list clears.
    /// When version is provided, updates with version < last accepted are ignored.
    member _.Report(pluginName: string, filePath: string, entries: ErrorEntry list, ?version: int64) =
        writeState (report pluginName filePath entries version)

    /// Clear all errors for a plugin + file.
    /// When version is provided, clears with version < last accepted are ignored.
    member _.Clear(pluginName: string, filePath: string, ?version: int64) =
        writeState (clear pluginName filePath version)

    /// Clear all errors for a plugin.
    member _.ClearPlugin(pluginName: string) = writeState (clearPlugin pluginName)

    /// Snapshot each currently-present plugin/file key with an opaque mutation
    /// revision for a later compare-and-remove operation.
    member internal _.SnapshotKeys() : LedgerKeyRevision list =
        read().EntryRevisions
        |> Map.toList
        |> List.map (fun (struct (plugin, file), revision) ->
            { Plugin = plugin
              File = file
              Revision = revision })

    /// Remove only keys that have not been reported or cleared since the caller's
    /// snapshot. One write makes the comparison and removal atomic.
    member internal _.PruneIfCurrent(candidates: LedgerKeyRevision list) : int =
        match write (pruneIfCurrent candidates) with
        | Some removed -> removed
        | None ->
            // Refused because the ledger stopped: `read` raises the latched fault rather
            // than letting "nothing removed" stand for it.
            read () |> ignore
            0

    /// Get all errors grouped by file path. Each entry includes the plugin name.
    member _.GetAll() : Map<string, (string * ErrorEntry) list> =
        read().Errors
        |> Map.toSeq
        |> Seq.collect (fun (struct (plugin, file), entries) -> entries |> List.map (fun e -> file, (plugin, e)))
        |> Seq.groupBy fst
        |> Seq.map (fun (file, entries) -> file, entries |> Seq.map snd |> Seq.toList)
        |> Map.ofSeq

    /// Get errors for a specific plugin only.
    member _.GetByPlugin(pluginName: string) : Map<string, ErrorEntry list> =
        read().Errors
        |> Map.toSeq
        |> Seq.choose (fun (struct (p, file), entries) -> if p = pluginName then Some(file, entries) else None)
        |> Map.ofSeq

    /// Get per-plugin error/warning counts from one published state.
    /// Plugins with no ledger entries are absent from the map.
    member _.GetCountsByPlugin() : Map<string, DiagnosticCounts> =
        read().Errors
        |> Map.fold
            (fun acc (struct (plugin, _)) entries ->
                let prev = Map.tryFind plugin acc |> Option.defaultValue DiagnosticCounts.empty

                let next =
                    entries
                    |> List.fold
                        (fun (d: DiagnosticCounts) e ->
                            match e.Severity with
                            | Error -> { d with Errors = d.Errors + 1 }
                            | Warning -> { d with Warnings = d.Warnings + 1 }
                            | _ -> d)
                        prev

                Map.add plugin next acc)
            Map.empty

    /// Get all failing entries grouped by file path, filtered by severity.
    /// When warningsAreFailures is true, both Error and Warning entries are included.
    /// When false, only Error entries are included.
    member _.FailingReasons(warningsAreFailures: bool) : Map<string, (string * ErrorEntry) list> =
        read().Errors
        |> Map.toSeq
        |> Seq.collect (fun (struct (plugin, file), entries) ->
            entries
            |> List.filter (isFailing warningsAreFailures)
            |> List.map (fun e -> file, (plugin, e)))
        |> Seq.groupBy fst
        |> Seq.map (fun (file, entries) -> file, entries |> Seq.map snd |> Seq.toList)
        |> Map.ofSeq

    /// True if any failing entries exist (Error, or Warning when warningsAreFailures=true).
    member _.HasFailingReasons(warningsAreFailures: bool) =
        read().Errors
        |> Map.values
        |> Seq.exists (List.exists (isFailing warningsAreFailures))

    /// A write that threw surfaces here, once. The ledger then refuses every read.
    member _.AgentCrashed: IEvent<exn> = crashed.Publish

    /// Test seam: deterministically raise inside a write, to verify the "a failed
    /// write surfaces and stops the ledger" contract. Production writes have no natural
    /// failure mode (Map/list ops don't throw on valid state), so without this the
    /// contract is unobservable. Internal, via `InternalsVisibleTo`.
    member internal _.RaiseFaultForTest(ex: exn) = writeState (fun _ -> raise ex)

    /// Test seam: hold the write lock on another thread until `release` is set, and
    /// return once it is held — a writer that cannot finish, for proving that reads
    /// do not wait for one.
    member internal _.HoldWritesForTest(release: System.Threading.ManualResetEventSlim) =
        use held = new System.Threading.ManualResetEventSlim(false)

        let holder =
            System.Threading.Thread(
                (fun () ->
                    lock writeGate (fun () ->
                        held.Set()
                        release.Wait())),
                IsBackground = true
            )

        holder.Start()
        held.Wait()
