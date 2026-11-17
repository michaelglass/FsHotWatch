/// A warm daemon that sees a VCS operation move its tree goes quiet until a client asks
/// (AUTOMATION-1705).
///
/// A rebase, checkout or `jj new` in a workspace whose daemon is warm rewrites files the
/// way an edit does, and a daemon that reacted to it would start checks and test runs
/// nobody asked for, outside whatever schedules heavy work on the box. So each change
/// batch is classified by the checkout's VCS position (`VcsPosition`): an edit checks at
/// once, as it always has; a batch that coincides with a VCS operation is held, with
/// every later one, until a client request (`fshw check`) wakes the daemon.
module FsHotWatch.VcsQuiet

open FsHotWatch.Events
open FsHotWatch.VcsPosition

/// Reads the checkout's position now.
type Reader = unit -> Result<Position, string>

/// A client request that wakes a quiet daemon, named for the log.
type ClientAsked = ClientAsked of request: string

/// What a quiet daemon is holding: the move that quieted it, and every change since.
type QuietSpell =
    { Before: Position
      After: Position
      Held: FileChangeKind list }

/// Whether change batches check at once.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type Gate =
    /// The checkout has no VCS position to read: every batch is an edit.
    | Ungated
    /// The position has not been readable yet, so there is nothing to compare with:
    /// batches check, and the first readable position anchors the gate.
    | Unanchored of Reader
    /// Batches check; each is compared with the position the previous one left.
    | Live of Reader * baseline: Position
    /// A VCS operation moved the tree: batches are held until a client asks.
    | Quiet of Reader * QuietSpell

/// What to do with a batch.
[<RequireQualifiedAccess>]
type Admission =
    /// Check these changes now.
    | Check of FileChangeKind list
    /// Held: the daemon is quiet.
    | Hold

[<Literal>]
let private Tag = "vcs"

let private anchor (read: Reader) : Gate =
    match read () with
    | Ok position -> Gate.Live(read, position)
    | Error reason ->
        Logging.warn Tag $"cannot read the checkout's VCS position ({reason}); change batches check until it reads"
        Gate.Unanchored read

/// The gate a daemon starts with: anchored at the position its checkout has now.
let start (probe: Probe) : Gate =
    match probe with
    | Probe.Untracked reason ->
        Logging.info Tag $"no VCS position to watch ({reason}); every change batch checks"
        Gate.Ungated
    | Probe.Tracked read -> anchor read

/// Admit a batch of changes: check them now, or hold them while the daemon is quiet.
let admit (gate: Gate) (changes: FileChangeKind list) : Gate * Admission =
    match gate with
    | Gate.Ungated -> gate, Admission.Check changes
    | Gate.Unanchored read -> anchor read, Admission.Check changes
    | Gate.Live(read, baseline) ->
        match read () with
        | Error reason ->
            Logging.warn Tag $"cannot read the checkout's VCS position ({reason}); checking this batch as an edit"
            gate, Admission.Check changes
        | Ok now ->
            match classify baseline now with
            | BatchOrigin.Edit now -> Gate.Live(read, now), Admission.Check changes
            | BatchOrigin.VcsOperation(before, after) ->
                Logging.info
                    Tag
                    $"a VCS operation moved the tree ({Position.describe before} -> {Position.describe after}); waiting for a client before checking"

                Gate.Quiet(
                    read,
                    { Before = before
                      After = after
                      Held = changes }
                ),
                Admission.Hold
    | Gate.Quiet(read, spell) ->
        Gate.Quiet(
            read,
            { spell with
                Held = spell.Held @ changes }
        ),
        Admission.Hold

/// A client asked for work: a quiet gate releases what it held and anchors at the
/// position the checkout has now. Any other gate has nothing to release.
let wake (gate: Gate) (ClientAsked request) : Gate * FileChangeKind list =
    match gate with
    | Gate.Quiet(read, spell) ->
        Logging.info
            Tag
            $"a client asked ({request}); checking the %d{spell.Held.Length} change(s) held since {Position.describe spell.Before} -> {Position.describe spell.After}"

        anchor read, spell.Held
    | Gate.Ungated
    | Gate.Unanchored _
    | Gate.Live _ -> gate, []
