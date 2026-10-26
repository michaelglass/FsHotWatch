/// What a repository host has lived through: the sessions it attached and how, the
/// clients it refused and why, the sessions that ended and how, and whether the host
/// before it exited without saying so.
///
/// Two readers want this. `status --repository` shows the counts of the running host
/// (`Tally`). A rollout of host mode wants them across hosts: a host that ends by a
/// crash or an out-of-memory kill takes every attached session with it, and leaves no
/// line saying so. So every event is also appended, one JSON line each, to the
/// repository's `host-events.jsonl` in its control directory (`eventLogPath`). A host
/// that starts after one whose last line is not its stop records `previousHostUnclean`,
/// naming that host's pid when the line says it.
///
/// A client that cannot use the host (it did not start, or stopped answering) and falls
/// back to its worktree's own daemon appends a `fellBack` line to the same log
/// (`recordFallback`), so a rollout can count how often the host failed its worktrees.
///
/// Measurement must never take the host down: a log that cannot be written still
/// counts, and every failure to append is swallowed after one warning.
module FsHotWatch.HostLifecycle

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open FsHotWatch.AttachHandshake
open FsHotWatch.RepositoryIdentity
open FsHotWatch.SessionRegistry

/// Why a host stopped.
[<RequireQualifiedAccess>]
type StopReason =
    /// No session was attached for the whole idle grace.
    | Idle
    /// Asked to: `fshw stop --repository`, or a signal.
    | Requested

module StopReason =
    let describe (reason: StopReason) : string =
        match reason with
        | StopReason.Idle -> "idle"
        | StopReason.Requested -> "requested"

/// One thing that happened to a host. Roots are the worktree's canonical root, or for a
/// refusal the root the client claimed.
[<RequireQualifiedAccess>]
type HostEvent =
    | HostStarted
    | HostStopped of StopReason
    /// The host before this one never recorded its stop; its pid, when its last line
    /// could be read.
    | PreviousHostUnclean of previousPid: int option
    | Attached of session: SessionId * root: string * disposition: AttachDisposition
    /// `kind` is `AttachRefusal.kind`.
    | Refused of kind: string * root: string
    | SessionEnded of session: SessionId * root: string * outcome: SessionEnd
    /// An attach whose answer never reached its client (`RepositoryHost`'s `undelivered`).
    | Undelivered of session: SessionId

module HostEvent =
    /// The event's `event` field.
    let name (event: HostEvent) : string =
        match event with
        | HostEvent.HostStarted -> "hostStarted"
        | HostEvent.HostStopped _ -> "hostStopped"
        | HostEvent.PreviousHostUnclean _ -> "previousHostUnclean"
        | HostEvent.Attached _ -> "attached"
        | HostEvent.Refused _ -> "refused"
        | HostEvent.SessionEnded _ -> "sessionEnded"
        | HostEvent.Undelivered _ -> "undelivered"

    let private disposition (d: AttachDisposition) : string =
        match d with
        | AttachDisposition.NewSession -> "new"
        | AttachDisposition.Rejoined -> "rejoined"
        | AttachDisposition.RejoinedConfigChanged -> "reloaded"

    /// One line of the event log: when, which host, what, and the event's own fields.
    let toJsonLine (at: DateTime) (hostPid: int) (event: HostEvent) : string =
        let node = JsonObject()
        node["at"] <- JsonValue.Create(at.ToString "o")
        node["hostPid"] <- JsonValue.Create hostPid
        node["event"] <- JsonValue.Create(name event)
        let set (key: string) (value: string) = node[key] <- JsonValue.Create value

        match event with
        | HostEvent.HostStarted -> ()
        | HostEvent.HostStopped reason -> set "reason" (StopReason.describe reason)
        | HostEvent.PreviousHostUnclean previous ->
            previous |> Option.iter (fun pid -> node["previousPid"] <- JsonValue.Create pid)
        | HostEvent.Attached(session, root, d) ->
            set "session" (SessionId.render session)
            set "root" root
            set "disposition" (disposition d)
        | HostEvent.Refused(kind, root) ->
            set "kind" kind
            set "root" root
        | HostEvent.SessionEnded(session, root, outcome) ->
            set "session" (SessionId.render session)
            set "root" root

            match outcome with
            | SessionEnd.Stopped -> set "outcome" "stopped"
            | SessionEnd.Faulted message ->
                set "outcome" "faulted"
                set "detail" message
        | HostEvent.Undelivered session -> set "session" (SessionId.render session)

        node.ToJsonString()

/// A host's counts since it started.
type Tally =
    {
        /// Sessions started for a worktree that had none.
        Attached: int64
        /// Attaches answered with the worktree's live session, unchanged.
        Rejoined: int64
        /// Attaches that replaced the live session because its configuration changed.
        Reloaded: int64
        /// Refused attaches, by `AttachRefusal.kind`.
        Refused: Map<string, int64>
        /// Sessions that ended by a stop: a detach, a reload, idle-exit, the host stopping.
        Stopped: int64
        /// Sessions whose run ended by a fault.
        Faulted: int64
        /// Attaches whose answer never reached the client.
        Undelivered: int64
        /// `Some` when the previous host never recorded its stop, holding its pid when
        /// known.
        PreviousUnclean: int option option
    }

module Tally =
    let empty =
        { Attached = 0L
          Rejoined = 0L
          Reloaded = 0L
          Refused = Map.empty
          Stopped = 0L
          Faulted = 0L
          Undelivered = 0L
          PreviousUnclean = None }

    /// `tally` with `event` counted.
    let add (tally: Tally) (event: HostEvent) : Tally =
        match event with
        | HostEvent.HostStarted
        | HostEvent.HostStopped _ -> tally
        | HostEvent.PreviousHostUnclean previous ->
            { tally with
                PreviousUnclean = Some previous }
        | HostEvent.Attached(_, _, AttachDisposition.NewSession) ->
            { tally with
                Attached = tally.Attached + 1L }
        | HostEvent.Attached(_, _, AttachDisposition.Rejoined) ->
            { tally with
                Rejoined = tally.Rejoined + 1L }
        | HostEvent.Attached(_, _, AttachDisposition.RejoinedConfigChanged) ->
            { tally with
                Reloaded = tally.Reloaded + 1L }
        | HostEvent.Refused(kind, _) ->
            let n = tally.Refused |> Map.tryFind kind |> Option.defaultValue 0L

            { tally with
                Refused = tally.Refused.Add(kind, n + 1L) }
        | HostEvent.SessionEnded(_, _, SessionEnd.Stopped) ->
            { tally with
                Stopped = tally.Stopped + 1L }
        | HostEvent.SessionEnded(_, _, SessionEnd.Faulted _) ->
            { tally with
                Faulted = tally.Faulted + 1L }
        | HostEvent.Undelivered _ ->
            { tally with
                Undelivered = tally.Undelivered + 1L }

    /// The `lifecycle` object of `status --repository`; `eventLog` names the log file.
    let toJson (eventLog: string) (tally: Tally) : JsonObject =
        let node = JsonObject()
        node["attached"] <- JsonValue.Create tally.Attached
        node["rejoined"] <- JsonValue.Create tally.Rejoined
        node["reloaded"] <- JsonValue.Create tally.Reloaded
        let refused = JsonObject()

        tally.Refused |> Map.iter (fun kind n -> refused[kind] <- JsonValue.Create n)

        node["refused"] <- refused
        let ended = JsonObject()
        ended["stopped"] <- JsonValue.Create tally.Stopped
        ended["faulted"] <- JsonValue.Create tally.Faulted
        node["ended"] <- ended
        node["undelivered"] <- JsonValue.Create tally.Undelivered
        node["previousHostUnclean"] <- JsonValue.Create tally.PreviousUnclean.IsSome

        tally.PreviousUnclean
        |> Option.flatten
        |> Option.iter (fun pid -> node["previousHostPid"] <- JsonValue.Create pid)

        node["eventLog"] <- JsonValue.Create eventLog
        node

/// The repository's event log, beside its host's lock and pid.
let eventLogPath (control: RepositoryControlPaths) : string =
    Path.Combine(control.Directory, "host-events.jsonl")

/// The size past which `Start` moves the log aside to `<log>.1`, replacing the
/// generation before it.
[<Literal>]
let DefaultMaxBytes = 1048576L

/// Why a client used its worktree's own daemon instead of the repository host.
[<RequireQualifiedAccess>]
type FallbackReason =
    /// Nothing answered the repository's endpoint within the startup bound.
    | HostDidNotStart
    /// The host answered the liveness probe, then not the attach, even when asked again.
    | HostStoppedAnswering

module FallbackReason =
    /// The line's `reason` field.
    let kind (reason: FallbackReason) : string =
        match reason with
        | FallbackReason.HostDidNotStart -> "host-did-not-start"
        | FallbackReason.HostStoppedAnswering -> "host-stopped-answering"

/// A client that could not use the repository host and used its worktree's own daemon.
type Fallback =
    {
        Reason: FallbackReason
        /// What the client told its user.
        Detail: string
        /// The worktree's canonical root.
        Root: string
        /// The host the repository's pid file names, if it names one.
        HostPid: int option
    }

module Fallback =
    /// The line's `event` field. A client writes it, not a host, so it never marks a
    /// host's start or stop.
    [<Literal>]
    let EventName = "fellBack"

    /// One line of the event log: when, which client, the host it could not use (when
    /// the repository's pid file named one), why, and for which worktree.
    let toJsonLine (at: DateTime) (clientPid: int) (fallback: Fallback) : string =
        let node = JsonObject()
        node["at"] <- JsonValue.Create(at.ToString "o")
        node["event"] <- JsonValue.Create EventName
        node["clientPid"] <- JsonValue.Create clientPid

        fallback.HostPid
        |> Option.iter (fun pid -> node["hostPid"] <- JsonValue.Create pid)

        node["reason"] <- JsonValue.Create(FallbackReason.kind fallback.Reason)
        node["detail"] <- JsonValue.Create fallback.Detail
        node["root"] <- JsonValue.Create fallback.Root
        node.ToJsonString()

/// Append `fallback`, as client `clientPid` saw it at `at`, to the event log at `path`,
/// creating its directory: a host that never started made none. False when it cannot be
/// written; a fallback is never kept from its daemon by its record.
let recordFallback (path: string) (at: DateTime) (clientPid: int) (fallback: Fallback) : bool =
    // MGA-ERROR-REPORT-001:ok — the caller reports a record it could not write; measurement
    // never stops the command it measures.
    try
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, Fallback.toJsonLine at clientPid fallback + "\n")
        true
    with _ ->
        false

/// A line's event and the pid of the host it names; `None` for a line that does not parse.
// MGA-ERROR-REPORT-001:ok — a line that does not parse is a torn write, and marks nothing.
let private eventOf (line: string) : (string * int option) option =
    let parsed =
        try
            Some(JsonDocument.Parse line)
        with _ ->
            None

    parsed
    |> Option.bind (fun doc ->
        try
            Json.tryString doc.RootElement "event"
            |> Option.map (fun event -> event, Json.tryInt doc.RootElement "hostPid")
        finally
            doc.Dispose())

/// A line that marks a host's start or stop, read as its event and the host's pid.
let private marker (line: string) : (string * int option) option =
    eventOf line
    |> Option.filter (fun (event, _) -> event = "hostStarted" || event = "hostStopped")

/// A line a client wrote (`recordFallback`), which says nothing of how any host ended.
let private byClient (line: string) : bool =
    eventOf line |> Option.exists (fun (event, _) -> event = Fallback.EventName)

/// How the host that last wrote a log ended, read from its last start or stop line, so
/// that a session end recorded late, or a torn last line, cannot hide it: `None` when it
/// recorded its stop or no host wrote anything; `Some pid` when its last mark is a start;
/// `Some None` when a host wrote lines but none of them marks it. Clients' fallbacks are
/// not a host's lines: a repository whose host never started has only those.
let private previousExit (path: string) : int option option =
    let written =
        if File.Exists path then
            File.ReadAllLines path
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l || byClient l))
        else
            [||]

    if Array.isEmpty written then
        None
    else
        match written |> Array.rev |> Array.tryPick marker with
        | Some("hostStopped", _) -> None
        | Some(_, pid) -> Some pid
        | None -> Some None

/// One host's lifecycle: its tally, and, given a `path`, the repository's event log.
/// Thread-safe: attaches and session ends record from different threads.
type EventLog(path: string option, hostPid: int, now: unit -> DateTime, maxBytes: int64) =
    let gate = Lock()
    let mutable tally = Tally.empty
    let mutable warned = false

    let append (line: string) =
        path
        |> Option.iter (fun file ->
            // MGA-ERROR-REPORT-001:ok — reported once by the warning; measurement never takes
            // the host down.
            try
                File.AppendAllText(file, line + "\n")
            with ex ->
                if not warned then
                    warned <- true
                    Logging.warn "host" $"host event log %s{file} cannot be written: %s{ex.Message}")

    let record (event: HostEvent) =
        Locking.locked gate (fun () ->
            tally <- Tally.add tally event
            append (HostEvent.toJsonLine (now ()) hostPid event))

    new(path, hostPid, now) = EventLog(path, hostPid, now, DefaultMaxBytes)

    /// The log file, when this log writes one.
    member _.Path = path

    /// Begin this host's record: read whether the previous host recorded its stop,
    /// rotate a log past its bound, then record the start.
    member _.Start() =
        let previous =
            path
            |> Option.bind (fun file ->
                try
                    let previous = previousExit file

                    if File.Exists file && FileInfo(file).Length > maxBytes then
                        File.Move(file, file + ".1", true)

                    previous
                // MGA-ERROR-REPORT-001:ok — an unreadable log tells nothing about the last host,
                // and must not keep this one from starting.
                with ex ->
                    Logging.warn "host" $"host event log %s{file} cannot be read: %s{ex.Message}"
                    None)

        previous |> Option.iter (HostEvent.PreviousHostUnclean >> record)
        record HostEvent.HostStarted

    /// Count `event`, and append it to the log.
    member _.Record(event: HostEvent) = record event

    /// The counts so far.
    member _.Tally = Locking.locked gate (fun () -> tally)
