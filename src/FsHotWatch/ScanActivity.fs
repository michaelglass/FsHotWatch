/// Explicit, typed accounting of full-repository scan work that is IN FLIGHT.
///
/// AUTOMATION-609 — before this module the daemon's only "am I working?" signals
/// were plugin-mailbox inflight counts and connected verdict waits. A cold
/// `fshw check` spends its first minutes inside `Daemon.performScan` — project
/// discovery, build settlement, and the FCS check tiers — where NEITHER signal is
/// raised: the FCS tiers are not plugin mailbox work, and the client's verdict
/// wait is only bracketed AFTER discovery admission. The idle-exit scheduler
/// therefore read a hard-working daemon as idle and, under the memory-pressure
/// floor, terminated it mid-scan.
///
/// A lease is taken for the WHOLE span of a scan and released on every exit —
/// completion, exception, and cancellation — so a failed scan can never keep the
/// daemon alive forever.
module FsHotWatch.ScanActivity

open System
open System.Threading

/// Which kind of full-repository scan is in flight. The distinction is reported,
/// not decided upon: both inhibit idle-exit identically, but a log line naming
/// "cold" against one naming "forced" is the difference between reading a
/// startup stall and reading a `fshw scan` the user asked for.
[<RequireQualifiedAccess>]
type ScanKind =
    /// The daemon's first full scan (generation 0). Cold FCS analysis of the
    /// whole project graph — the longest phase a daemon ever has.
    | Cold
    /// Any subsequent full scan: `fshw scan`, or a re-scan requested over IPC.
    | Forced

/// Rendering for `ScanKind`, kept beside the type so log lines and test
/// assertions cannot drift apart.
module ScanKind =
    /// Lower-case wire/log name: `cold` / `forced`.
    let describe (kind: ScanKind) : string =
        match kind with
        | ScanKind.Cold -> "cold"
        | ScanKind.Forced -> "forced"

/// Live counts of in-flight scans, by kind. Mutable and shared: the scan agent
/// writes, the idle-exit timer and the heartbeat read, from other threads.
/// All mutation goes through `Interlocked`.
[<NoComparison; NoEquality>]
type ScanLeases =
    {
        /// In-flight cold scans. At most one in practice (the scan agent is a
        /// mailbox, so scans serialize) but counted rather than flagged so a
        /// double-release can never leave the daemon pinned alive.
        mutable Cold: int
        /// In-flight forced scans.
        mutable Forced: int
    }

/// Acquire/release/read over `ScanLeases`.
module ScanLeases =
    /// A fresh, empty lease set.
    let create () : ScanLeases = { Cold = 0; Forced = 0 }

    /// Take a lease for `kind`. The returned disposable releases it, and is
    /// idempotent: disposing twice decrements once, so a `use` inside a `try`
    /// that also disposes cannot drive the count negative.
    let acquire (leases: ScanLeases) (kind: ScanKind) : IDisposable =
        match kind with
        | ScanKind.Cold -> Interlocked.Increment(&leases.Cold) |> ignore
        | ScanKind.Forced -> Interlocked.Increment(&leases.Forced) |> ignore

        let released = ref 0

        { new IDisposable with
            member _.Dispose() =
                if Interlocked.CompareExchange(&released.contents, 1, 0) = 0 then
                    match kind with
                    | ScanKind.Cold -> Interlocked.Decrement(&leases.Cold) |> ignore
                    | ScanKind.Forced -> Interlocked.Decrement(&leases.Forced) |> ignore }

    /// The kinds currently in flight, cold first. Empty when no scan is running.
    /// A defensively-negative count (impossible via `acquire`, but cheap to be
    /// honest about) reads as not-in-flight rather than as a phantom scan.
    let inFlight (leases: ScanLeases) : ScanKind list =
        [ if Volatile.Read(&leases.Cold) > 0 then
              yield ScanKind.Cold
          if Volatile.Read(&leases.Forced) > 0 then
              yield ScanKind.Forced ]

    /// True when any scan is in flight.
    let anyInFlight (leases: ScanLeases) : bool = not (List.isEmpty (inFlight leases))

/// Run `work` holding a lease for `kind`. `try/finally` inside the async CE means
/// the release also runs on cancellation and on exception — the acceptance
/// criterion that a failed scan cannot pin the daemon alive.
let withLease (leases: ScanLeases) (kind: ScanKind) (work: Async<'a>) : Async<'a> =
    async {
        let lease = ScanLeases.acquire leases kind

        try
            return! work
        finally
            lease.Dispose()
    }
