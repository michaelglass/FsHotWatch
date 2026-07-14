module FsHotWatch.ProcessRegistry

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading

/// F19/F20 (audit 2026-05-02): exception classes treated as benign when
/// observing or killing a tracked Process. HasExited and Kill both throw
/// InvalidOperationException (no process associated / already exited) and
/// Win32Exception (access denied). Both are tolerated here. NullReferenceException
/// and other CLR-level bugs propagate.
let isExpectedProcessException (ex: exn) : bool =
    match ex with
    | :? InvalidOperationException -> true
    | :? System.ComponentModel.Win32Exception -> true
    | _ -> false

/// Per-scope process tracker. Scoped via AsyncLocal so a daemon's spawned children
/// register against that daemon's registry, not a process-wide global. This keeps
/// `killAll` from clobbering unrelated work in parallel test runs.
type Registry() =
    let live = ConcurrentDictionary<int, Process>()
    // Track pids alongside Process so Untrack can clean up even if the Process
    // handle has been disposed and `proc.Id` would throw.
    let pidByProc = ConcurrentDictionary<Process, int>(HashIdentity.Reference)

    member _.Track(p: Process) =
        pidByProc.TryAdd(p, p.Id) |> ignore
        live.TryAdd(p.Id, p) |> ignore

    member _.Untrack(p: Process) =
        match pidByProc.TryRemove(p) with
        | true, pid -> live.TryRemove(pid) |> ignore
        | false, _ -> ()

    member _.Snapshot() : Process list =
        [ for kv in live do
              let p = kv.Value

              // F19 (audit 2026-05-02): see isExpectedProcessException — any
              // tolerated exception means "can't observe; treat as not alive".
              let alive =
                  try
                      not p.HasExited
                  with ex when isExpectedProcessException ex ->
                      false

              if alive then
                  yield p ]

    /// KillAll is a shutdown-only operation. Tracks added concurrently with
    /// iteration may be missed and silently dropped from `live` by the final
    /// Clear — accept that for daemon shutdown; do not call from steady-state.
    member _.KillAll() : unit =
        // F20 (audit 2026-05-02): see isExpectedProcessException — both
        // HasExited and Kill can race with natural exit (InvalidOperationException)
        // or fail with Win32Exception access-denied. Both tolerated here so
        // daemon shutdown can proceed across the whole live set; other
        // classes propagate as they indicate real bugs.
        for kv in live do
            try
                let p = kv.Value

                if not p.HasExited then
                    p.Kill(entireProcessTree = true)
            with ex when isExpectedProcessException ex ->
                ()

        live.Clear()
        pidByProc.Clear()

let private currentRegistry = AsyncLocal<Registry>()

/// Install `r` as the current scope's tracker. Returns an IDisposable that
/// restores the prior registry, so callers can `use _ = install r` and have
/// the scope unwind cleanly when the work completes.
let install (r: Registry) : IDisposable =
    let prior = currentRegistry.Value
    currentRegistry.Value <- r

    { new IDisposable with
        member _.Dispose() = currentRegistry.Value <- prior }

let private currentOpt () =
    let r = currentRegistry.Value
    if isNull (box r) then None else Some r

/// Register `p` with the current scope's registry so daemon shutdown can tear it
/// down. A child spawned with NO registry in scope can never be reaped — it will
/// outlive the daemon as an init-reparented orphan — so the miss is WARNED, never
/// swallowed. It used to be a silent `| None -> ()`, and that silence is exactly
/// how every plugin child came to leak (AUTOMATION-147): the daemon installed its
/// registry too late for the dispatching ExecutionContext to see it, `track`
/// dropped every child without a word, and `KillAll` truthfully reported nothing
/// to kill. A leak you cannot see is a leak you cannot fix.
let track (p: Process) =
    match currentOpt () with
    | Some r -> r.Track p
    | None ->
        Logging.warn
            "process-registry"
            $"spawned pid %d{p.Id} with no registry in scope — it cannot be reaped on shutdown and will be orphaned"

let untrack (p: Process) =
    match currentOpt () with
    | Some r -> r.Untrack p
    | None -> ()

let killAll () =
    match currentOpt () with
    | Some r -> r.KillAll()
    | None -> ()

let snapshot () : Process list =
    match currentOpt () with
    | Some r -> r.Snapshot()
    | None -> []
