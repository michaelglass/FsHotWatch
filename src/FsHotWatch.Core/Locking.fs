/// Mutual exclusion without the branch F#'s `lock` leaves behind.
///
/// `lock` compiles to `Monitor.Enter(gate, &taken)` and a `finally` that exits only
/// `if taken`. `taken` is false only when `Enter` itself threw, and then the
/// `finally` is never reached with it, so every `lock` adds a branch that no input
/// can take. Since SDK 10.0.4xx the compiler instruments that branch, and each caller
/// carries it as permanently uncovered. `Lock.Enter` before the `try` has no such
/// check: if it throws, nothing was acquired and there is nothing to release.
module internal FsHotWatch.Locking

open System.Threading

/// Run `work` while holding `gate`, releasing it however `work` ends.
let locked (gate: Lock) (work: unit -> 'T) : 'T =
    gate.Enter()

    try
        work ()
    finally
        gate.Exit()

/// `locked` for a monitor: for a gate that `Monitor.Wait` and `Monitor.Pulse` also
/// use, which a `Lock` does not support.
let monitored (gate: obj) (work: unit -> 'T) : 'T =
    Monitor.Enter gate

    try
        work ()
    finally
        Monitor.Exit gate
