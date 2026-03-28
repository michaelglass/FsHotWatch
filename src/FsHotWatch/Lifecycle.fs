module FsHotWatch.Lifecycle

/// Phantom type markers — no runtime representation.
type Idle = private | Idle
type Running = private | Running

/// A value tagged with a lifecycle phase. The phantom type parameter
/// prevents calling start on a Running lifecycle or complete on an Idle one.
[<Struct>]
type Lifecycle<'Phase, 'T> = private { Value: 'T }

module Lifecycle =
    /// Create a new Idle lifecycle with the given initial value.
    let create (value: 'T) : Lifecycle<Idle, 'T> = { Value = value }

    /// Transition from Idle to Running, preserving the value.
    let start (lc: Lifecycle<Idle, 'T>) : Lifecycle<Running, 'T> = { Value = lc.Value }

    /// Transition from Running to Idle with a new value.
    let complete (value: 'T) (_lc: Lifecycle<Running, 'T>) : Lifecycle<Idle, 'T> = { Value = value }

    /// Extract the value (works in any phase).
    let value (lc: Lifecycle<_, 'T>) : 'T = lc.Value
