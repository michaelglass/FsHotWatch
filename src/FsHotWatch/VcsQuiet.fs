/// A warm daemon that sees a VCS operation move its tree goes quiet until a client asks
/// (AUTOMATION-1705).
///
/// A rebase, checkout or `jj new` in a workspace whose daemon is warm rewrites files the
/// way an edit does, and a daemon that reacted to it would start checks and test runs
/// nobody asked for, outside whatever schedules heavy work on the box. So each change
/// batch is classified by the checkout's VCS position (`VcsPosition`): an edit checks at
/// once, as it always has; a batch that coincides with a VCS operation is held until a
/// client request (`fshw check`) wakes the daemon. A save after the operation is an edit
/// again: the gate compares the next batch with the position the operation left, so it
/// checks at once, while what the operation rewrote stays held.
///
/// A repository opts in with `afterVcsOperation` (`AfterVcsOperation`); by default every
/// batch checks as it arrives.
///
/// Known limits, each pinned by a test:
/// - The position is read when a batch is decided, and a VCS writes it after the files.
///   An operation slower than the debounce window can have its first part decided while
///   the position has not moved yet: that part checks as an edit, and only what follows
///   the move is held.
/// - A jj operation that only deleted files is read as one that wrote none, so its batch
///   checks as an edit.
/// - In a colocated jj+git checkout the jj position is read, and a git command moves it
///   only when the next jj command imports it: a `git checkout` checks as an edit.
module FsHotWatch.VcsQuiet

open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch.Events
open FsHotWatch.VcsPosition

/// What a warm daemon does with the changes a VCS operation (a rebase, checkout or
/// `jj new`) rewrote: `.fshw.json` `afterVcsOperation`, decided per repository.
///
/// The trade, measured on AUTOMATION-1705: waiting takes the run a rebase started on its
/// own from 1 to 0; a check after the daemon has settled, which answered in 24-40 ms from
/// the run already made, then pays for that run itself (about 5 minutes for a suite the
/// size of intelligence's); a check made right after the rebase costs the same either way.
/// It suits a repository whose gates rebase and then check, where the run should happen
/// inside the gate's own admission rather than beside it.
[<RequireQualifiedAccess>]
type AfterVcsOperation =
    /// Check them at once, as an edit is checked (`"checkUnasked"`, the default).
    | CheckUnasked
    /// Hold them until a client asks (`"waitForClient"`): `fshw check` wakes the daemon,
    /// and `status` says it waits.
    | WaitForClient

module AfterVcsOperation =
    /// The `.fshw.json` key.
    [<Literal>]
    let ConfigKey = "afterVcsOperation"

    /// Every value the key takes, as `.fshw.json` spells it.
    let spellings: (string * AfterVcsOperation) list =
        [ "checkUnasked", AfterVcsOperation.CheckUnasked
          "waitForClient", AfterVcsOperation.WaitForClient ]

    /// The setting `text` spells, or `None` for any other text.
    let tryParse (text: string) : AfterVcsOperation option =
        spellings
        |> List.tryPick (fun (spelling, value) -> if spelling = text then Some value else None)

/// Reads the checkout's position now.
type Reader = unit -> Result<Position, string>

/// A client request that wakes a waiting daemon, named for the log.
type ClientAsked = ClientAsked of request: string

/// What a waiting daemon holds: the move that stopped it, and the changes the
/// operations rewrote.
type QuietSpell =
    { Before: Position
      After: Position
      Held: FileChangeKind list }

/// Whether change batches check at once.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type Gate =
    /// Every batch checks: the repository checks unasked after a VCS operation, or its
    /// checkout has no VCS position to read.
    | Ungated
    /// The position has not been readable yet, so there is nothing to compare with:
    /// batches check, and the first readable position anchors the gate.
    | Unanchored of Reader
    /// Batches check; each is compared with the position the previous one left.
    | Live of Reader * baseline: Position
    /// A VCS operation moved the tree, and what it rewrote waits for a client. Later
    /// batches are compared with `baseline`, the position the last one left: an edit
    /// checks at once, another operation joins what is held.
    | Waiting of Reader * baseline: Position * QuietSpell

/// What to do with a batch.
[<RequireQualifiedAccess>]
type Admission =
    /// Check these changes now.
    | Check of FileChangeKind list
    /// Held until a client asks: a VCS operation rewrote them.
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

/// The gate a repository's setting gives: one that never holds anything when it checks
/// unasked, without reading the checkout's layout; otherwise `start` on what `probe` finds.
let startFor (setting: AfterVcsOperation) (probe: unit -> Probe) : Gate =
    match setting with
    | AfterVcsOperation.CheckUnasked -> Gate.Ungated
    | AfterVcsOperation.WaitForClient -> start (probe ())

/// The position now, or `None` (logged) when it cannot be read: the batch then checks.
let private positionNow (read: Reader) : Position option =
    match read () with
    | Ok now -> Some now
    | Error reason ->
        Logging.warn Tag $"cannot read the checkout's VCS position ({reason}); checking this batch as an edit"
        None

let private logMove (before: Position) (after: Position) =
    Logging.info
        Tag
        $"a VCS operation moved the tree ({Position.describe before} -> {Position.describe after}); waiting for a client before checking what it rewrote"

/// Admit a batch of changes: check them now, or hold them because a VCS operation
/// rewrote them.
let admit (gate: Gate) (changes: FileChangeKind list) : Gate * Admission =
    match gate with
    | Gate.Ungated -> gate, Admission.Check changes
    | Gate.Unanchored read -> anchor read, Admission.Check changes
    | Gate.Live(read, baseline) ->
        match positionNow read |> Option.map (classify baseline) with
        | None -> gate, Admission.Check changes
        | Some(BatchOrigin.Edit now) -> Gate.Live(read, now), Admission.Check changes
        | Some(BatchOrigin.VcsOperation(before, after)) ->
            logMove before after

            Gate.Waiting(
                read,
                after,
                { Before = before
                  After = after
                  Held = changes }
            ),
            Admission.Hold
    | Gate.Waiting(read, baseline, spell) ->
        match positionNow read |> Option.map (classify baseline) with
        | None -> gate, Admission.Check changes
        | Some(BatchOrigin.Edit now) -> Gate.Waiting(read, now, spell), Admission.Check changes
        | Some(BatchOrigin.VcsOperation(before, after)) ->
            logMove before after

            Gate.Waiting(
                read,
                after,
                { spell with
                    After = after
                    Held = spell.Held @ changes }
            ),
            Admission.Hold

/// A client asked for work: a waiting gate releases what it held and anchors at the
/// position the checkout has now. Any other gate has nothing to release.
let wake (gate: Gate) (ClientAsked request) : Gate * FileChangeKind list =
    match gate with
    | Gate.Waiting(read, _, spell) ->
        Logging.info
            Tag
            $"a client asked ({request}); checking the %d{spell.Held.Length} change(s) held since {Position.describe spell.Before} -> {Position.describe spell.After}"

        anchor read, spell.Held
    | Gate.Ungated
    | Gate.Unanchored _
    | Gate.Live _ -> gate, []

/// One change request against the gate: its changes are admitted first, and only then
/// does a client request in the same request wake the gate. So a client that asks while
/// the operation's changes are still debouncing, and so arrive in one request with them,
/// is answered for them too: they are held, then released at once.
let decide (gate: Gate) (changes: FileChangeKind list) (asked: ClientAsked list) : Gate * Admission =
    let admitted, admission = admit gate changes

    match asked with
    | [] -> admitted, admission
    | _ ->
        let woken, released =
            asked
            |> List.fold
                (fun (gate, released) request ->
                    let gate, more = wake gate request
                    gate, released @ more)
                (admitted, [])

        let checkedNow =
            match admission with
            | Admission.Check now -> now
            // Held, they are in what the wake released.
            | Admission.Hold -> []

        woken, Admission.Check(released @ checkedNow)

/// What `status` reports about the gate, as it travels between daemon and client.
[<RequireQualifiedAccess>]
type Notice =
    /// Batches check as they arrive.
    | Checking
    /// A VCS operation moved the tree, and the changes it rewrote wait for a client.
    /// The positions are as `Position.describe` names them, operation ids included.
    | Waiting of before: string * after: string * held: int

module Notice =
    /// The notice a gate gives.
    let ofGate (gate: Gate) : Notice =
        match gate with
        | Gate.Waiting(_, _, spell) ->
            Notice.Waiting(Position.describe spell.Before, Position.describe spell.After, spell.Held.Length)
        | Gate.Ungated
        | Gate.Unanchored _
        | Gate.Live _ -> Notice.Checking

    /// The `vcs` object a `GetDiagnostics` reply carries.
    let payload (notice: Notice) : JsonObject =
        let node = JsonObject()

        match notice with
        | Notice.Checking -> node["state"] <- JsonValue.Create "checking"
        | Notice.Waiting(before, after, held) ->
            node["state"] <- JsonValue.Create "waiting"
            node["before"] <- JsonValue.Create before
            node["after"] <- JsonValue.Create after
            node["held"] <- JsonValue.Create held

        node

    /// Read a `vcs` object. `None` when it is absent or malformed.
    let tryRead (el: JsonElement) : Notice option =
        if el.ValueKind <> JsonValueKind.Object then
            None
        else
            match Json.tryString el "state" with
            | Some "checking" -> Some Notice.Checking
            | Some "waiting" ->
                match Json.tryString el "before", Json.tryString el "after", Json.tryInt64 el "held" with
                | Some before, Some after, Some held when held >= 0L -> Some(Notice.Waiting(before, after, int held))
                | _ -> None
            | _ -> None

    /// The line `status` prints, or `None` while batches check as they arrive.
    let line (notice: Notice) : string option =
        match notice with
        | Notice.Checking -> None
        | Notice.Waiting(before, after, held) ->
            Some
                $"vcs: tree changed by a VCS operation ({before} -> {after}); waiting for a client. Not hung: the next `fshw check` checks the %d{held} change(s) it rewrote"
