/// Scan progress while `check` and `confirm` wait for their scan.
///
/// The scan wait is one blocking call, and a cold scan holds it for minutes. Before this,
/// all a reader saw for those minutes was "Scanning...": a slow scan and a stuck one looked
/// the same, and the progress the daemon had was in `daemon.log` only. While the wait
/// runs, the daemon's `ScanStatus` answer (files checked of registered, elapsed time,
/// check-result cache hits and misses) is printed at a fixed interval.
module FsHotWatch.Cli.ScanHeartbeat

open System
open System.Threading.Tasks
open CommandTree

/// How often a running scan is reported: inside the 30 s a reader should never go
/// without hearing from a long scan.
let interval = TimeSpan.FromSeconds 15.0

/// Run `wait`, and every `every` it is still running, `emit` the scan-progress line of
/// `readStatus`'s answer (`IpcOutput.scanProgressLine`). An answer with no such line (no
/// scan running, a finished scan) emits nothing. A failed read is skipped: progress is
/// reporting, and must never fail or end the wait a verdict depends on. Returns what
/// `wait` returns, or raises what it raised.
let waitReporting (every: TimeSpan) (readStatus: unit -> string) (emit: string -> unit) (wait: unit -> 'a) : 'a =
    let waiting = Task.Run(fun () -> wait ())

    // The wait handle, not `Task.Wait`: that throws the wait's failure wrapped in an
    // AggregateException, and the callers match on the failure itself.
    while not ((waiting :> IAsyncResult).AsyncWaitHandle.WaitOne every) do
        let answer =
            try
                Some(readStatus ())
            with ex ->
                FsHotWatch.Logging.debug "check" $"scan progress unavailable: %s{ex.Message}"
                None

        answer |> Option.bind IpcOutput.scanProgressLine |> Option.iter emit

    waiting.GetAwaiter().GetResult()

/// Write one progress line. On a terminal (`interactive`) it first clears the spinner's
/// line, which the spinner then redraws beneath it.
let emitLineTo (interactive: bool) (write: string -> unit) (line: string) : unit =
    if interactive then
        write $"\r\u001b[2K  %s{line}\n"
    else
        write $"  %s{line}\n"

/// Print one progress line to stderr.
let emitLine: string -> unit = emitLineTo UI.isInteractive Console.Error.Write
