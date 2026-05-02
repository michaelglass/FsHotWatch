module FsHotWatch.ProcessRegistry

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading

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

              // TODO(error-audit F19): see docs/plans/2026-05-02-error-handling-audit.md
              // — bare _ broader than warranted. Process.HasExited throws only
              // InvalidOperationException + Win32Exception; narrow to those.
              let alive =
                  try
                      not p.HasExited
                  with _ ->
                      false

              if alive then
                  yield p ]

    /// KillAll is a shutdown-only operation. Tracks added concurrently with
    /// iteration may be missed and silently dropped from `live` by the final
    /// Clear — accept that for daemon shutdown; do not call from steady-state.
    member _.KillAll() : unit =
        // TODO(error-audit F20): see docs/plans/2026-05-02-error-handling-audit.md
        // — bare _ catches Win32Exception (real failures) with InvalidOperation
        // (already-exited). Comment names the dictionary race but the catch
        // covers process-state exceptions; align comment + narrow types.
        for kv in live do
            try
                let p = kv.Value

                if not p.HasExited then
                    p.Kill(entireProcessTree = true)
            with _ ->
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

let track (p: Process) =
    match currentOpt () with
    | Some r -> r.Track p
    | None -> ()

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
