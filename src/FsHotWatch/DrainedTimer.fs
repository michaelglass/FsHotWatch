/// A periodic timer that has stopped once its disposal returns: no tick is still
/// running, and none starts later.
///
/// `Timer.Dispose()` makes neither promise. It returns while a tick may still be
/// running, and a tick the timer had already queued can start after it. A tick that
/// acts on something its owner disposes next then acts on it disposed. The daemon's
/// timers cancel the daemon's `CancellationTokenSource`, which the daemon's owner
/// disposes once the daemon has stopped. Cancelling a disposed source throws, on a
/// timer thread where nothing catches it, and that takes the process down.
module internal FsHotWatch.DrainedTimer

open System
open System.Threading

/// The ticks of one timer, and the point after which none runs. Each tick holds
/// the gate while it runs, and `Stop` takes the same gate, so `Stop` returns only
/// once the running tick has finished, and every tick after it does nothing. A tick
/// that stops its own timer holds the gate already, and stops without waiting.
type Ticks(tick: unit -> unit) =
    let gate = Lock()
    let stopped = ref false

    /// Run one tick, unless the timer has stopped.
    member _.Run() =
        Locking.locked gate (fun () ->
            if not stopped.Value then
                tick ())

    /// Stop the ticks, once the tick running now, if any, has finished.
    member _.Stop() =
        Locking.locked gate (fun () -> stopped.Value <- true)

/// Run `tick` every `period`, from `period` from now, until the returned timer is
/// disposed. Ticks run one at a time.
let every (period: TimeSpan) (tick: unit -> unit) : IDisposable =
    let ticks = Ticks tick
    let timer = new Timer(TimerCallback(fun _ -> ticks.Run()), null, period, period)

    { new IDisposable with
        member _.Dispose() =
            ticks.Stop()
            timer.Dispose() }
