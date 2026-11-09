/// What a daemon's scan is doing, as a value: the `ScanStatus` answer, typed.
///
/// The daemon builds a `Progress` from its published scan state and its live coverage, and
/// serves it over IPC as JSON (`toJson`); a client parses that back (`parse`) and formats the
/// line it shows (`render`). Nobody scrapes a rendered line for its numbers, so a wording
/// change cannot move what a client reads.
module FsHotWatch.ScanProgress

open System
open System.Text.Json
open System.Text.Json.Nodes

/// What the check-result cache has done for a running scan, counted from its start.
type CacheCounts =
    {
        Hits: int64
        Misses: int64
        /// Files a filed `check` record answered: neither a cache hit nor a re-check.
        Replayed: int64
    }

[<RequireQualifiedAccess>]
type Progress =
    /// No scan is running and none has completed.
    | Idle
    /// A scan is running and no file is registered yet: projects are being discovered.
    | Discovering of elapsed: TimeSpan
    /// A scan is checking files. `cache` is `None` when this daemon runs no check-result cache.
    | Checking of completed: int * total: int * elapsed: TimeSpan * cache: CacheCounts option
    /// The last scan finished. `registered` and `unchecked` are read live, at the answer.
    | Complete of registered: int * unchecked: int * elapsed: TimeSpan

/// "42s" under a minute, "6m 12s" from there.
let private describeElapsed (elapsed: TimeSpan) : string =
    let seconds = max 0 (int elapsed.TotalSeconds)

    if seconds < 60 then
        $"%d{seconds}s"
    else
        let minutes, rest = Math.DivRem(seconds, 60)
        $"%d{minutes}m %d{rest}s"

/// The line a progress is shown as. The prefixes are load-bearing: `status`, `WaitForScan`
/// and external tooling read `complete:`, `incomplete:` and `scanning:`.
let render (progress: Progress) : string =
    match progress with
    | Progress.Idle -> "idle"
    | Progress.Discovering elapsed ->
        // No file is registered until discovery finishes: "0/0 files" would read as an
        // empty tree.
        $"scanning: discovering projects, %s{describeElapsed elapsed} elapsed"
    | Progress.Checking(completed, total, elapsed, cache) ->
        let pct = if total > 0 then completed * 100 / total else 0

        let cacheText =
            match cache with
            | Some cache when cache.Replayed > 0L ->
                $"cache %d{cache.Hits} hit / %d{cache.Misses} miss / %d{cache.Replayed} replayed"
            | Some cache -> $"cache %d{cache.Hits} hit / %d{cache.Misses} miss"
            | None -> "check-result cache off"

        $"scanning: %d{completed}/%d{total} files (%d{pct}%%), %s{describeElapsed elapsed} elapsed, %s{cacheText}"
    | Progress.Complete(registered, unchecked, elapsed) ->
        if unchecked > 0 then
            // Non-ok: a truncated or stale-incomplete scan must not present as clean. The
            // checked count is (registered - unchecked), so the discrepancy is explicit.
            $"incomplete: %d{registered - unchecked} files checked, %d{unchecked} unchecked in %.1f{elapsed.TotalSeconds}s"
        else
            $"complete: %d{registered} files checked in %.1f{elapsed.TotalSeconds}s"

/// The line a client shows while a scan runs; `None` when none is running.
let progressLine (progress: Progress) : string option =
    match progress with
    | Progress.Discovering _
    | Progress.Checking _ -> Some(render progress)
    | Progress.Idle
    | Progress.Complete _ -> None

/// Names the wire format. A reply that does not name it is not one.
[<Literal>]
let Schema = "fshw-scan-status-v1"

/// The `ScanStatus` answer: `progress`, and the daemon's wedge report when it has one.
let toJson (wedge: string option) (progress: Progress) : string =
    let o = JsonObject()
    o["schema"] <- Schema

    match progress with
    | Progress.Idle -> o["state"] <- "idle"
    | Progress.Discovering elapsed ->
        o["state"] <- "discovering"
        o["elapsedTicks"] <- elapsed.Ticks
    | Progress.Checking(completed, total, elapsed, cache) ->
        o["state"] <- "checking"
        o["completed"] <- completed
        o["total"] <- total
        o["elapsedTicks"] <- elapsed.Ticks

        cache
        |> Option.iter (fun cache ->
            o["cacheHits"] <- cache.Hits
            o["cacheMisses"] <- cache.Misses

            if cache.Replayed > 0L then
                o["cacheReplayed"] <- cache.Replayed)
    | Progress.Complete(registered, unchecked, elapsed) ->
        o["state"] <- "complete"
        o["registered"] <- registered
        o["unchecked"] <- unchecked
        o["elapsedTicks"] <- elapsed.Ticks

    wedge |> Option.iter (fun report -> o["wedge"] <- report)
    o.ToJsonString()

/// What a client made of a `ScanStatus` answer.
[<RequireQualifiedAccess>]
type Reading =
    /// The daemon's progress, and its wedge report when it has one.
    | Reported of wedge: string option * progress: Progress
    /// Not an answer in this format: a daemon from another fshw build (its answer was a
    /// line of text), or a malformed reply. Never read as any progress.
    | Unrecognised of answer: string

/// The progress a `ScanStatus` answer's object states; `None` for one that states none.
let private progressOf (o: JsonElement) : Progress option =
    let elapsed () =
        Json.tryInt64 o "elapsedTicks" |> Option.map TimeSpan.FromTicks

    let cache () =
        match Json.tryInt64 o "cacheHits", Json.tryInt64 o "cacheMisses" with
        | Some hits, Some misses ->
            Some
                { Hits = hits
                  Misses = misses
                  Replayed = Json.tryInt64 o "cacheReplayed" |> Option.defaultValue 0L }
        | _ -> None

    match Json.tryString o "state" with
    | Some "idle" -> Some Progress.Idle
    | Some "discovering" -> elapsed () |> Option.map Progress.Discovering
    | Some "checking" ->
        match Json.tryInt o "completed", Json.tryInt o "total", elapsed () with
        | Some completed, Some total, Some elapsed -> Some(Progress.Checking(completed, total, elapsed, cache ()))
        | _ -> None
    | Some "complete" ->
        match Json.tryInt o "registered", Json.tryInt o "unchecked", elapsed () with
        | Some registered, Some unchecked, Some elapsed -> Some(Progress.Complete(registered, unchecked, elapsed))
        | _ -> None
    | _ -> None

/// The `ScanStatus` answer `text` parsed; anything that is not exactly this format is
/// `Unrecognised`.
let parse (text: string) : Reading =
    try
        use doc = JsonDocument.Parse text
        let o = doc.RootElement

        let progress =
            if o.ValueKind = JsonValueKind.Object && Json.tryString o "schema" = Some Schema then
                progressOf o
            else
                None

        match progress with
        | Some progress -> Reading.Reported(Json.tryString o "wedge", progress)
        | None -> Reading.Unrecognised text
    with :? JsonException ->
        Reading.Unrecognised text
