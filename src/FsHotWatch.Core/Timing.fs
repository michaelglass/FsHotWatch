/// Times the phases of a computation and counts what each covered (experiment E5,
/// AUTOMATION-1696). A caller that does not report phases passes `PhaseTimer.untimed`.
module FsHotWatch.Timing

/// Times named phases and records counts.
type PhaseTimer =
    /// Run `work` as the phase `phase`.
    abstract Time<'T> : phase: string * work: (unit -> 'T) -> 'T
    /// Record that the work covered `count` of `name` (files, requirements).
    abstract Count: name: string * count: int -> unit

module PhaseTimer =
    /// Times and counts nothing.
    let untimed =
        { new PhaseTimer with
            member _.Time(_, work) = work ()
            member _.Count(_, _) = () }
