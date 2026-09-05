/// AUTOMATION-555 (rework). WHERE the daemon's own wall time goes, as named,
/// timed phases — the evidence a `check`/`confirm` verdict needs to account for the
/// time it spent WAITING on the daemon rather than on a plugin's final run.
///
/// The first landing attributed only each plugin's LAST run and the hook steps.
/// On a real gate that left 50–94% of the wall clock unexplained, because the
/// clock goes to phases no run record ever described:
///
///   - the cold scan — MSBuild discovery plus the FCS check tiers, ~12 min on a
///     22-project repository — which `WaitForScan` blocks on but no plugin owns;
///   - a re-discovery provoked mid-run by a project-file change (~47 s);
///   - the plugin runs a check WAITED FOR but that were then SUPERSEDED — a
///     test-prune run already in flight when the check arrived, re-run twice as
///     files changed underneath it — of which only the last survived as `lastRun`;
///   - the time a plugin was `Running` BEFORE its own measured run began
///     (test-prune's symbol analysis and selection: 6m41s on that gate).
///
/// This ledger records every one of those as a `PhaseRecord` with a wall-clock
/// start and an elapsed time, bounded in size, and served to the CLI beside the
/// plugin statuses so the verdict's `timingSpans` can cover what the run observed.
/// A record is written on EVERY exit from a phase — completion, exception,
/// cancellation — because the wall time was spent either way.
module FsHotWatch.DaemonPhases

open System
open System.Collections.Generic

/// A phase the daemon spends wall time in. Closed, so a scope name cannot be
/// invented at a call site and drift from the CLI's rendering.
[<RequireQualifiedAccess>]
type Phase =
    /// Process start up to the IPC pipe listening: runtime boot, config load,
    /// analyzer loading, the singleton lock.
    | Startup
    /// Project discovery — MSBuild evaluation and registration.
    | Discover
    /// A full-repository scan: discovery admission, build settlement, the FCS check
    /// tiers. `kind` is `ScanActivity.ScanKind.describe` — `cold` / `forced`.
    | Scan of kind: string
    /// An incremental change batch: the FCS re-check of the files a watcher event
    /// (or a plugin's own writes) touched.
    | Check
    /// One plugin's `Running` → terminal interval, INCLUDING runs a later run
    /// superseded. Scoped `plugin.<name>` so it unions with the CLI's own
    /// `plugin.<name>` span for the surviving `lastRun`.
    | PluginRun of plugin: string

module Phase =
    /// The `timingSpans[].scope` token. Daemon phases are `daemon.*`; plugin runs
    /// share the `plugin.*` namespace the verdict already uses.
    let scope (phase: Phase) : string =
        match phase with
        | Phase.Startup -> "daemon.startup"
        | Phase.Discover -> "daemon.discover"
        | Phase.Scan _ -> "daemon.scan"
        | Phase.Check -> "daemon.check"
        | Phase.PluginRun plugin -> "plugin." + plugin

/// One completed (or, in a snapshot, still-running) phase.
type PhaseRecord =
    {
        Scope: string
        /// UTC.
        StartedAt: DateTime
        Elapsed: TimeSpan
        Detail: string option
    }

/// Bound on retained completed phases. A single `check` produces a few dozen after
/// coalescing (below); the bound only stops a long-lived daemon growing without limit.
[<Literal>]
let MaxRetained = 256

/// Two records of the SAME scope whose gap is at most this are one phase: a plugin
/// that reports a terminal status per file (the analyzers plugin, ~130 transitions in
/// a few seconds) would otherwise flood the ledger and evict the startup and
/// discovery records the verdict needs — which is exactly how the first dogfood run
/// lost its first 33 s. The union is what the verdict measures anyway; the coalesced
/// record keeps the latest detail.
[<Literal>]
let CoalesceGapMs = 250.0

/// A phase that has begun and not yet been recorded. `Complete` records it with a
/// detail; `Dispose` records it without one if `Complete` never ran — so a `use`
/// binding turns every exit path into a record.
type PhaseHandle internal (record: string option -> unit) =
    let mutable recorded = false

    member _.Complete(detail: string option) : unit =
        if not recorded then
            recorded <- true
            record detail

    interface IDisposable with
        member this.Dispose() = this.Complete None

/// The ledger. Thread-safe: phases begin and end on plugin agents, the scan
/// agent, the status agent and the RPC thread.
type Ledger() =
    let gate = obj ()
    let completed = Queue<PhaseRecord>()
    let inFlight = Dictionary<int64, string * DateTime>()
    let mutable nextId = 0L

    // The queue holds records oldest-first; the newest of a scope is what a new
    // record can coalesce with. Rebuilding the queue for a merge is O(n) over at
    // most `MaxRetained` entries and happens once per plugin transition.
    let append (record: PhaseRecord) =
        lock gate (fun () ->
            let items = completed.ToArray()

            let mergeInto =
                items
                |> Array.tryFindIndexBack (fun existing -> existing.Scope = record.Scope)
                |> Option.filter (fun i ->
                    let existing = items[i]
                    let existingEnd = existing.StartedAt + existing.Elapsed
                    let gap = (record.StartedAt - existingEnd).TotalMilliseconds
                    gap <= CoalesceGapMs && record.StartedAt + record.Elapsed >= existing.StartedAt)

            match mergeInto with
            | Some i ->
                let existing = items[i]
                let startedAt = min existing.StartedAt record.StartedAt

                let endedAt =
                    max (existing.StartedAt + existing.Elapsed) (record.StartedAt + record.Elapsed)

                items[i] <-
                    { Scope = record.Scope
                      StartedAt = startedAt
                      Elapsed = endedAt - startedAt
                      Detail =
                        (match record.Detail with
                         | Some _ -> record.Detail
                         | None -> existing.Detail) }

                completed.Clear()

                for item in items do
                    completed.Enqueue item
            | None -> completed.Enqueue record

            while completed.Count > MaxRetained do
                completed.Dequeue() |> ignore)

    /// Record a phase whose bounds are already known (a startup measured from the
    /// process start time, a plugin run measured by the status agent).
    member _.Record(phase: Phase, startedAt: DateTime, elapsed: TimeSpan, detail: string option) : unit =
        append
            { Scope = Phase.scope phase
              StartedAt = startedAt
              Elapsed = if elapsed < TimeSpan.Zero then TimeSpan.Zero else elapsed
              Detail = detail }

    /// Begin a phase now. The handle records it on `Complete` or `Dispose`.
    member _.Begin(phase: Phase) : PhaseHandle =
        let startedAt = DateTime.UtcNow
        let scope = Phase.scope phase

        let id =
            lock gate (fun () ->
                nextId <- nextId + 1L
                inFlight[nextId] <- (scope, startedAt)
                nextId)

        new PhaseHandle(fun detail ->
            let endedAt = DateTime.UtcNow
            lock gate (fun () -> inFlight.Remove id |> ignore)

            append
                { Scope = scope
                  StartedAt = startedAt
                  Elapsed = endedAt - startedAt
                  Detail = detail })

    /// Every retained phase, oldest first, PLUS every phase still in flight
    /// clipped at `now` and marked as such — a reader taking the snapshot mid-phase
    /// must not see that time vanish.
    member _.Snapshot(now: DateTime) : PhaseRecord list =
        lock gate (fun () ->
            let running =
                [ for KeyValue(_, (scope, startedAt)) in inFlight ->
                      { Scope = scope
                        StartedAt = startedAt
                        Elapsed = (if now > startedAt then now - startedAt else TimeSpan.Zero)
                        Detail = Some "in flight" } ]

            (List.ofSeq completed) @ running)
        |> List.sortBy (fun r -> r.StartedAt)
