/// How many check results a daemon's plugins hold at once (AUTOMATION-683, ADR-054).
///
/// A `FileCheckResult` carries FCS's parse and check results for its file, and stays
/// reachable until every plugin that subscribes to `FileChecked` has committed it. The
/// checks themselves are bounded (ADR-038), but a scan used to dispatch each result the
/// moment its check returned. When the plugins are slower than the checks, which is the
/// normal shape of a cold scan, their mailboxes held up to a result per file.
///
/// A producer now takes a place under the bound before it publishes a result, with
/// `Bound.Hold`, and hands the place to the publication (`PluginHost.EmitFileChecked`),
/// which gives it back once every subscriber has committed the result or failed it. A
/// producer that finds every place taken waits. It is a check of the scan or the change
/// batch, so the wait holds back the next check too: the checks go at the plugins' pace.
///
/// The places are slots of a one-lane `WorkScheduler.Scheduler`, so a wait is FIFO, a
/// producer that gives up while waiting leaves the queue, and the waits are measured.
module FsHotWatch.LiveResults

open System
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FsHotWatch.WorkScheduler

/// The bound when nothing configures one: twice the processor count. The checks in
/// flight are bounded by the processor count, so this lets the plugins fall one round of
/// checks behind before a check waits for them.
let DefaultBound = 2 * Environment.ProcessorCount

/// The environment variable that overrides a daemon's bound.
[<Literal>]
let OverrideEnvVar = "FSHW_LIVE_RESULTS"

/// What `FSHW_LIVE_RESULTS` holds: `Ok None` when unset or blank, `Ok (Some n)` for a
/// positive integer, and an `Error` naming the value otherwise.
let parseOverride (raw: string) : Result<int option, string> =
    if String.IsNullOrWhiteSpace raw then
        Ok None
    else
        match Int32.TryParse(raw.Trim()) with
        | true, bound when bound > 0 -> Ok(Some bound)
        | _ -> Error $"%s{OverrideEnvVar}=%s{raw} is not a positive integer; using the default bound %d{DefaultBound}"

/// A daemon's bound, from `FSHW_LIVE_RESULTS` read through `getEnv` over `DefaultBound`,
/// with the warning to log when the variable holds something that is not a bound.
let resolveBound (getEnv: string -> string) : int * string option =
    match parseOverride (getEnv OverrideEnvVar) with
    | Ok bound -> defaultArg bound DefaultBound, None
    | Error warning -> DefaultBound, Some warning

/// The live results of one daemon now, and how its producers waited. `Live` is the
/// places held now and `Peak` the most ever held at once. `Queued` is the producers
/// waiting now. `Admitted` and `Abandoned` count the places granted, and the waits given
/// up. `Waited` sums, and `LongestWait` is the most of, the time a granted place waited.
type LiveResultsStatus =
    { Bound: int
      Live: int
      Peak: int
      Queued: int
      Admitted: int64
      Abandoned: int64
      Waited: TimeSpan
      LongestWait: TimeSpan }

module LiveResultsStatus =
    let private wholeMs (t: TimeSpan) = int64 t.TotalMilliseconds

    /// A session's `liveResults` object of `status --repository`, times in whole ms.
    let payload (status: LiveResultsStatus) : JsonObject =
        let node = JsonObject()
        node["bound"] <- JsonValue.Create status.Bound
        node["live"] <- JsonValue.Create status.Live
        node["peak"] <- JsonValue.Create status.Peak
        node["queued"] <- JsonValue.Create status.Queued
        node["admitted"] <- JsonValue.Create status.Admitted
        node["abandoned"] <- JsonValue.Create status.Abandoned
        node["waitedMs"] <- JsonValue.Create(wholeMs status.Waited)
        node["longestWaitMs"] <- JsonValue.Create(wholeMs status.LongestWait)
        node

/// One live result's place under the bound. It is held from its grant until it is given
/// back, once: by the publication it was handed to, or by `Bound.Hold` when the producer
/// published nothing with it.
[<Sealed>]
type Place internal (giveBack: unit -> unit) =
    // 1 while the place is held. Set under the scheduler's lock at the grant.
    let mutable held = 0
    // 1 once a publication has taken charge of giving the place back.
    let mutable handedOver = 0

    member internal _.Granted() = Volatile.Write(&held, 1)

    member internal _.HandedOver: bool = Volatile.Read &handedOver = 1

    /// Give the place back. Only the first call gives it back.
    member _.Release() : unit =
        if Interlocked.Exchange(&held, 0) = 1 then
            giveBack ()

    /// Give the place back once every one of `completions` has completed, however each
    /// ends; at once when there are none.
    member internal this.ReleaseWhen(completions: Task list) : unit =
        Volatile.Write(&handedOver, 1)

        match completions with
        | [] -> this.Release()
        | _ ->
            Task.WhenAll(completions).ContinueWith(fun (_: Task) -> this.Release())
            |> ignore

/// At most `capacity` live results. `clock` is the monotonic time waits are measured by.
[<Sealed>]
type Bound internal (capacity: int, clock: unit -> TimeSpan) =
    let scheduler = Scheduler(capacity, ignore, clock)
    let lane = scheduler.Lane "live results"
    let mutable live = 0
    let mutable peak = 0

    // Runs under the scheduler's lock at a grant, so it is the only writer of `peak`.
    let granted () =
        let now = Interlocked.Increment &live

        if now > Volatile.Read &peak then
            Volatile.Write(&peak, now)

    let giveBack () =
        Interlocked.Decrement &live |> ignore
        lane.Release()

    /// At most `capacity` (at least one) live results, waits measured by the system's
    /// monotonic clock.
    new(capacity: int) = Bound(max 1 capacity, fun () -> Stopwatch.GetElapsedTime 0L)

    /// How many results may be live at once.
    member _.Capacity: int = capacity

    /// Wait for a place, then run `publish` with it. `publish` hands the place to the
    /// publication that holds it (`PluginHost.EmitFileChecked`); a place it does not hand
    /// over is given back when it returns or throws. Cancelled while waiting, the wait is
    /// given up, no place is taken, and the cancellation is raised.
    member _.Hold(publish: Place -> unit) : Async<unit> =
        async {
            let place = Place giveBack

            try
                let! ct = Async.CancellationToken

                do!
                    Async.FromContinuations(fun (admitted, _, cancelled) ->
                        scheduler.Admit(
                            lane,
                            JobSpec.interactive,
                            ct,
                            (fun () ->
                                place.Granted()
                                granted ()),
                            fun ok ->
                                if ok then
                                    admitted ()
                                else
                                    cancelled (OperationCanceledException ct)
                        ))

                publish place
            finally
                if not place.HandedOver then
                    place.Release()
        }

    /// The live results now.
    member _.Status() : LiveResultsStatus =
        let admission = lane.Status()

        { Bound = capacity
          Live = Volatile.Read &live
          Peak = Volatile.Read &peak
          Queued = admission.Queued
          Admitted = admission.Granted
          Abandoned = admission.Abandoned
          Waited = admission.Waited
          LongestWait = admission.LongestWait }
