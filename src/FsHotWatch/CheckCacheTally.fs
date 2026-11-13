/// What the check-result cache did, and why each lookup that missed did.
///
/// A scan's file count reads the same whether its answers came from FCS or from the
/// cache, and a check-start count says only how many missed. Neither tells "the cache
/// missed because a referenced DLL was rebuilt with new bytes" from "the cache does not
/// work". This tally does: every lookup and every store is counted by its outcome, and
/// a miss on project outputs names the outputs.
module FsHotWatch.CheckCacheTally

/// Why a lookup did not serve an entry.
[<RequireQualifiedAccess>]
type CacheMiss =
    /// The file could not be read, so no key was computed.
    | NoKey
    /// Nothing is held under the key: never stored, or the file, its options, its
    /// upstream sources or the toolchain changed since.
    | NoEntry
    /// The entry holds a parse-only answer, which is never served.
    | ParseOnly
    /// The entry was typed against project outputs whose bytes have moved since; names
    /// them, repository-relative.
    | OutputsMoved of outputs: string list

module CacheMiss =
    /// The reason, as the tally counts it: one label per case.
    let label (miss: CacheMiss) : string =
        match miss with
        | CacheMiss.NoKey -> "unreadable"
        | CacheMiss.NoEntry -> "no entry"
        | CacheMiss.ParseOnly -> "parse-only"
        | CacheMiss.OutputsMoved _ -> "output moved"

/// One lookup's outcome, as the tally counts it.
[<RequireQualifiedAccess>]
type CacheLookupOutcome =
    | Hit
    /// No cache entry, but a filed `check` record answered it: no FCS check ran.
    | Replayed
    | Miss of CacheMiss

/// What became of a finished check's answer.
[<RequireQualifiedAccess>]
type CacheStore =
    | Stored
    /// The answer declared a type incompatible with itself (`FcsDiagnosticFilter`).
    | Uncacheable
    /// The check's inputs moved while it ran, so its key no longer names them.
    | InputsMoved

module CacheStore =
    /// Why an answer was not stored, as the tally counts it.
    let label (store: CacheStore) : string =
        match store with
        | CacheStore.Stored -> "stored"
        | CacheStore.Uncacheable -> "uncacheable answer"
        | CacheStore.InputsMoved -> "inputs moved during the check"

/// Counts since the tally began. Two snapshots subtract (`CacheTally.since`) to what the
/// cache did in between, such as one scan.
type CacheTally =
    {
        Hits: int64
        /// Lookups a filed `check` record answered (ADR-059 §17). Not misses: no FCS check ran.
        Replayed: int64
        /// Misses per `CacheMiss.label`.
        Misses: Map<string, int64>
        /// Per moved output, the misses it caused. A miss on two outputs counts on both.
        MovedOutputs: Map<string, int64>
        Stored: int64
        /// Answers not stored, per `CacheStore.label`.
        NotStored: Map<string, int64>
    }

module CacheTally =
    let empty =
        { Hits = 0L
          Replayed = 0L
          Misses = Map.empty
          MovedOutputs = Map.empty
          Stored = 0L
          NotStored = Map.empty }

    let private bump (key: string) (counts: Map<string, int64>) =
        counts |> Map.change key (fun n -> Some(defaultArg n 0L + 1L))

    let lookedUp (outcome: CacheLookupOutcome) (tally: CacheTally) : CacheTally =
        match outcome with
        | CacheLookupOutcome.Hit -> { tally with Hits = tally.Hits + 1L }
        | CacheLookupOutcome.Replayed ->
            { tally with
                Replayed = tally.Replayed + 1L }
        | CacheLookupOutcome.Miss miss ->
            let moved =
                match miss with
                | CacheMiss.OutputsMoved outputs ->
                    outputs |> List.fold (fun counts o -> bump o counts) tally.MovedOutputs
                | _ -> tally.MovedOutputs

            { tally with
                Misses = bump (CacheMiss.label miss) tally.Misses
                MovedOutputs = moved }

    let stored (store: CacheStore) (tally: CacheTally) : CacheTally =
        match store with
        | CacheStore.Stored ->
            { tally with
                Stored = tally.Stored + 1L }
        | other ->
            { tally with
                NotStored = bump (CacheStore.label other) tally.NotStored }

    let misses (tally: CacheTally) : int64 =
        tally.Misses |> Map.fold (fun sum _ n -> sum + n) 0L

    /// Counts in `later` beyond those in `earlier`; a key with none in between is dropped.
    let since (earlier: CacheTally) (later: CacheTally) : CacheTally =
        let minus (before: Map<string, int64>) (after: Map<string, int64>) =
            after
            |> Map.toSeq
            |> Seq.choose (fun (key, n) ->
                let delta = n - (before |> Map.tryFind key |> Option.defaultValue 0L)
                if delta > 0L then Some(key, delta) else None)
            |> Map.ofSeq

        { Hits = later.Hits - earlier.Hits
          Replayed = later.Replayed - earlier.Replayed
          Misses = minus earlier.Misses later.Misses
          MovedOutputs = minus earlier.MovedOutputs later.MovedOutputs
          Stored = later.Stored - earlier.Stored
          NotStored = minus earlier.NotStored later.NotStored }

/// How many moved outputs the line names; the rest are counted.
[<Literal>]
let private NamedOutputs = 5

/// The one line a scan logs about its cache, e.g.
/// `check-result cache: 2110 hits, 318 misses — 318 output moved; outputs whose bytes
/// moved: ${repo}/src/Lib/bin/Lib.dll ×318; stored 318`.
let describeCacheTally (tally: CacheTally) : string =
    let byCount (counts: Map<string, int64>) =
        counts |> Map.toList |> List.sortBy (fun (key, n) -> -n, key)

    let reasons =
        match byCount tally.Misses with
        | [] -> ""
        | counts ->
            " — "
            + (counts
               |> List.map (fun (reason, n) -> $"%d{n} %s{reason}")
               |> String.concat ", ")

    let moved =
        match byCount tally.MovedOutputs with
        | [] -> ""
        | counts ->
            let named =
                counts
                |> List.truncate NamedOutputs
                |> List.map (fun (output, n) -> $"%s{output} ×%d{n}")
                |> String.concat ", "

            let rest =
                match counts.Length - NamedOutputs with
                | more when more > 0 -> $", and %d{more} more"
                | _ -> ""

            $"; outputs whose bytes moved: %s{named}%s{rest}"

    let notStored =
        byCount tally.NotStored
        |> List.map (fun (reason, n) -> $", %d{n} not stored (%s{reason})")
        |> String.concat ""

    let replayed =
        if tally.Replayed > 0L then
            $", %d{tally.Replayed} replayed from check records"
        else
            ""

    $"check-result cache: %d{tally.Hits} hits, %d{CacheTally.misses tally} misses%s{replayed}%s{reasons}%s{moved}; stored %d{tally.Stored}%s{notStored}"

/// A `CacheTally` many checks add to at once.
type CacheTallyCounter() =
    let lockObj = System.Threading.Lock()
    let mutable tally = CacheTally.empty

    member _.LookedUp(outcome: CacheLookupOutcome) =
        Locking.locked lockObj (fun () -> tally <- CacheTally.lookedUp outcome tally)

    member _.Stored(store: CacheStore) =
        Locking.locked lockObj (fun () -> tally <- CacheTally.stored store tally)

    /// The counts so far.
    member _.Snapshot: CacheTally = Locking.locked lockObj (fun () -> tally)

/// Misses per subject (a project, a plugin), per reason (AUTOMATION-1607): what a scan logs
/// so its misses explain themselves without comparing stored keys by hand. A reason is a
/// short label such as `key moved: options, upstream` or `behind src/Lib/A.fs`.
type MissTally = Map<string, Map<string, int64>>

module MissTally =
    let empty: MissTally = Map.empty

    /// One more miss of `subject` for `reason`.
    let add (subject: string) (reason: string) (tally: MissTally) : MissTally =
        tally
        |> Map.change subject (fun reasons ->
            defaultArg reasons Map.empty
            |> Map.change reason (fun n -> Some(defaultArg n 0L + 1L))
            |> Some)

    /// The misses in `later` beyond those in `earlier`; a subject or reason with none in
    /// between is dropped.
    let since (earlier: MissTally) (later: MissTally) : MissTally =
        later
        |> Map.toSeq
        |> Seq.choose (fun (subject, reasons) ->
            let before = earlier |> Map.tryFind subject |> Option.defaultValue Map.empty

            let delta =
                reasons
                |> Map.toSeq
                |> Seq.choose (fun (reason, n) ->
                    match n - (before |> Map.tryFind reason |> Option.defaultValue 0L) with
                    | d when d > 0L -> Some(reason, d)
                    | _ -> None)
                |> Map.ofSeq

            if Map.isEmpty delta then None else Some(subject, delta))
        |> Map.ofSeq

    let private total (reasons: Map<string, int64>) =
        reasons |> Map.fold (fun sum _ n -> sum + n) 0L

    /// How many reasons a subject's line names; the rest are counted.
    [<Literal>]
    let private NamedReasons = 5

    /// One line per subject, the most-missed first, e.g.
    /// `Tests.Unit.fsproj: 637 missed — 636 behind tests/Shared/Scrub.fs; 1 key moved: upstream`.
    let describe (tally: MissTally) : string list =
        tally
        |> Map.toList
        |> List.sortBy (fun (subject, reasons) -> -(total reasons), subject)
        |> List.map (fun (subject, reasons) ->
            let counts = reasons |> Map.toList |> List.sortBy (fun (reason, n) -> -n, reason)

            let named =
                counts
                |> List.truncate NamedReasons
                |> List.map (fun (reason, n) -> $"%d{n} %s{reason}")
                |> String.concat "; "

            let rest =
                match counts.Length - NamedReasons with
                | more when more > 0 -> $"; and %d{more} more reason(s)"
                | _ -> ""

            $"%s{subject}: %d{total reasons} missed — %s{named}%s{rest}")

/// A `MissTally` many lookups add to at once. Diagnostics only: nothing reads it to decide
/// a lookup.
type MissTallyCounter() =
    let lockObj = System.Threading.Lock()
    let mutable tally = MissTally.empty

    member _.Missed(subject: string, reason: string) =
        Locking.locked lockObj (fun () -> tally <- MissTally.add subject reason tally)

    /// The misses so far.
    member _.Snapshot: MissTally = Locking.locked lockObj (fun () -> tally)

    /// The misses since the last drain, and start counting afresh.
    member _.Drain() : MissTally =
        Locking.locked lockObj (fun () ->
            let drained = tally
            tally <- MissTally.empty
            drained)
