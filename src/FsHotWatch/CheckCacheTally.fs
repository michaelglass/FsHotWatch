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

/// What a `NoEntry` miss would have done under the alternative keys AUTOMATION-1816
/// measures (step 1, measurement only: nothing is served by them). Each compares the file's
/// upstream now with the upstream recorded with the last entry stored for the same bytes,
/// options and toolchain (`AlternativeKeys`):
/// - by SIGNATURE: every earlier file and every source of every referenced F# project, each
///   named by its API signature (`ApiSignature`), or by its content where it has none;
/// - by REFERENCES: only the files the entry's check resolved symbols in
///   (`CacheInputs.usedDeclarationFiles`), named the same way.
/// The references are a subset of the signature upstream, so a signature-unchanged miss is
/// a references-unchanged miss too.
[<RequireQualifiedAccess>]
type NoEntryAlternative =
    /// Nothing recorded to compare with: no entry for these bytes and options was stored
    /// by an fshw that records alternative upstreams (an older fshw's entry, or bytes never
    /// stored), the stored check's references could not be described, or the file moved
    /// between the miss and the comparison.
    | Unknown
    /// The signature upstream is unchanged: both alternative keys would have served it.
    | SignatureUnchanged
    /// Only the referenced files' signatures are unchanged.
    | OnlyReferencesUnchanged
    /// Neither alternative upstream is unchanged.
    | NeitherUnchanged

module NoEntryAlternative =
    /// The outcome, as the tally counts it.
    let label (outcome: NoEntryAlternative) : string =
        match outcome with
        | NoEntryAlternative.Unknown -> "unknown"
        | NoEntryAlternative.SignatureUnchanged -> "signature unchanged"
        | NoEntryAlternative.OnlyReferencesUnchanged -> "only references unchanged"
        | NoEntryAlternative.NeitherUnchanged -> "neither unchanged"

/// One part of a check's verdict, as the consumers of a served check read it
/// (AUTOMATION-1816 step 2). The references key in shadow compares each part of the verdict
/// it would have served with the cold check's.
[<RequireQualifiedAccess>]
type VerdictFacet =
    /// The file's own diagnostics: the compiler's verdict on it.
    | Diagnostics
    /// `CheckCache.fcsCheckSignature`: what every plugin keys a file's result by, with the
    /// file's source and `UsedSignatures`.
    | FcsSignature
    /// `CacheInputs.usedSignaturesHash`: the shape of everything the check resolved outside
    /// the file, which every plugin keys a file's result by.
    | UsedSignatures
    /// `ApiSignature.ofResult`: what the file declares, which later files are keyed and
    /// typed against.
    | ApiSignature

/// A check whose answer a served verdict would decide.
[<RequireQualifiedAccess>]
type VerdictCheck =
    | Compiler
    | Analyzers
    | Lint
    | TestPrune

module VerdictCheck =
    let label (check: VerdictCheck) : string =
        match check with
        | VerdictCheck.Compiler -> "compiler"
        | VerdictCheck.Analyzers -> "analyzers"
        | VerdictCheck.Lint -> "lint"
        | VerdictCheck.TestPrune -> "test-prune"

module VerdictFacet =
    /// Every facet, in the order a verdict compares them.
    let all =
        [ VerdictFacet.Diagnostics
          VerdictFacet.FcsSignature
          VerdictFacet.UsedSignatures
          VerdictFacet.ApiSignature ]

    /// The facet as `scan-metrics.jsonl` names it.
    let label (facet: VerdictFacet) : string =
        match facet with
        | VerdictFacet.Diagnostics -> "diagnostics"
        | VerdictFacet.FcsSignature -> "fcsSignature"
        | VerdictFacet.UsedSignatures -> "usedSignatures"
        | VerdictFacet.ApiSignature -> "apiSignature"

    let private facetLabel = label

    let tryParse (label: string) : VerdictFacet option =
        all |> List.tryFind (fun facet -> facetLabel facet = label)

    /// The checks whose answer a served verdict decides through this facet. The analyzers,
    /// lint and TestPrune key each file's result by its source, `FcsSignature` and
    /// `UsedSignatures` (`AnalyzersPlugin`, `LintPlugin`, `IndexedAnalysis.identity`): a
    /// served check that differs in either hands each of them other inputs than a cold one.
    let checks (facet: VerdictFacet) : VerdictCheck list =
        match facet with
        | VerdictFacet.Diagnostics
        | VerdictFacet.ApiSignature -> [ VerdictCheck.Compiler ]
        | VerdictFacet.FcsSignature
        | VerdictFacet.UsedSignatures -> [ VerdictCheck.Analyzers; VerdictCheck.Lint; VerdictCheck.TestPrune ]

/// A file whose verdict under the references key differs from a cold check's.
type ShadowDisagreement =
    {
        /// The file, as the check-result cache names it (`${repo}/...` inside the repository).
        File: string
        /// The facets that differ, in `VerdictFacet.all` order; never empty.
        Facets: VerdictFacet list
    }

module ShadowDisagreement =
    /// Every check a disagreement decides, in `VerdictCheck` order.
    let checks (disagreement: ShadowDisagreement) : VerdictCheck list =
        disagreement.Facets
        |> List.collect VerdictFacet.checks
        |> List.distinct
        |> List.sort

/// Why a would-be-served verdict could not be compared.
[<RequireQualifiedAccess>]
type ShadowUnverifiedReason =
    /// The record for its inputs holds no verdict: written with the references key off, or
    /// by an fshw from before it.
    | NoCachedVerdict
    /// The cold check left no verdict to compare: not stored (its inputs moved during it,
    /// or its answer was uncacheable), or the file moved before the comparison.
    | NoColdVerdict

/// What the references key in shadow found for one miss it would have served.
[<RequireQualifiedAccess>]
type ShadowOutcome =
    | Agreed
    | Disagreed of ShadowDisagreement
    | Unverified of ShadowUnverifiedReason

/// What the references key in shadow did, since the tally began. Disagreements and the
/// bytes held at each settle are kept newest first with their counts, so `since` takes a
/// scan's share of them.
type ShadowTally =
    {
        Agreed: int64
        NoCachedVerdict: int64
        NoColdVerdict: int64
        Disagreements: int64
        /// Every disagreement, newest first.
        DisagreementsNewestFirst: ShadowDisagreement list
        Settles: int64
        /// At each settle, the bytes the shadow held for its cold verdicts until then
        /// (`CheckVerdict.heldBytes`), newest first.
        HeldBytesNewestFirst: int64 list
    }

module ShadowTally =
    let empty =
        { Agreed = 0L
          NoCachedVerdict = 0L
          NoColdVerdict = 0L
          Disagreements = 0L
          DisagreementsNewestFirst = []
          Settles = 0L
          HeldBytesNewestFirst = [] }

    let shadowed (outcome: ShadowOutcome) (tally: ShadowTally) : ShadowTally =
        match outcome with
        | ShadowOutcome.Agreed ->
            { tally with
                Agreed = tally.Agreed + 1L }
        | ShadowOutcome.Disagreed disagreement ->
            { tally with
                Disagreements = tally.Disagreements + 1L
                DisagreementsNewestFirst = disagreement :: tally.DisagreementsNewestFirst }
        | ShadowOutcome.Unverified ShadowUnverifiedReason.NoCachedVerdict ->
            { tally with
                NoCachedVerdict = tally.NoCachedVerdict + 1L }
        | ShadowOutcome.Unverified ShadowUnverifiedReason.NoColdVerdict ->
            { tally with
                NoColdVerdict = tally.NoColdVerdict + 1L }

    let held (bytes: int64) (tally: ShadowTally) : ShadowTally =
        { tally with
            Settles = tally.Settles + 1L
            HeldBytesNewestFirst = bytes :: tally.HeldBytesNewestFirst }

    let since (earlier: ShadowTally) (later: ShadowTally) : ShadowTally =
        let newest (count: int64) (items: 'a list) = List.truncate (int count) items
        let disagreements = later.Disagreements - earlier.Disagreements
        let settles = later.Settles - earlier.Settles

        { Agreed = later.Agreed - earlier.Agreed
          NoCachedVerdict = later.NoCachedVerdict - earlier.NoCachedVerdict
          NoColdVerdict = later.NoColdVerdict - earlier.NoColdVerdict
          Disagreements = disagreements
          DisagreementsNewestFirst = newest disagreements later.DisagreementsNewestFirst
          Settles = settles
          HeldBytesNewestFirst = newest settles later.HeldBytesNewestFirst }

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
        /// `NoEntry` misses per `NoEntryAlternative.label`, counted when the pipeline settles
        /// (`CheckPipeline.SettleApiIndex`), not at the lookup.
        NoEntryAlternatives: Map<string, int64>
        /// What the references key in shadow found (AUTOMATION-1816 step 2), counted when the
        /// pipeline settles. Empty with the references key off.
        ReferencesShadow: ShadowTally
    }

module CacheTally =
    let empty =
        { Hits = 0L
          Replayed = 0L
          Misses = Map.empty
          MovedOutputs = Map.empty
          Stored = 0L
          NotStored = Map.empty
          NoEntryAlternatives = Map.empty
          ReferencesShadow = ShadowTally.empty }

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

    let comparedAlternative (outcome: NoEntryAlternative) (tally: CacheTally) : CacheTally =
        { tally with
            NoEntryAlternatives = bump (NoEntryAlternative.label outcome) tally.NoEntryAlternatives }

    let shadowed (outcome: ShadowOutcome) (tally: CacheTally) : CacheTally =
        { tally with
            ReferencesShadow = ShadowTally.shadowed outcome tally.ReferencesShadow }

    let shadowHeld (bytes: int64) (tally: CacheTally) : CacheTally =
        { tally with
            ReferencesShadow = ShadowTally.held bytes tally.ReferencesShadow }

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
          NotStored = minus earlier.NotStored later.NotStored
          NoEntryAlternatives = minus earlier.NoEntryAlternatives later.NoEntryAlternatives
          ReferencesShadow = ShadowTally.since earlier.ReferencesShadow later.ReferencesShadow }

/// One output whose moved bytes made lookups miss, and how many.
type MovedOutputMisses = { Output: string; Misses: int64 }

/// A tally's misses per `CacheMiss` reason, and the outputs that moved most
/// (AUTOMATION-1816): what a scan's `scan-metrics.jsonl` record carries, so a series of
/// scans that all missed says why without reading the log.
type CacheMissReasons =
    {
        NoKey: int64
        NoEntry: int64
        ParseOnly: int64
        /// Lookups that missed on moved outputs. A lookup naming two outputs counts once
        /// here and once on each output in `TopMovedOutputs`.
        OutputsMoved: int64
        /// At most `CacheMissReasons.TopMovedOutputCount`, most misses first, ties by output.
        TopMovedOutputs: MovedOutputMisses list
    }

module CacheMissReasons =
    /// How many moved outputs a record names.
    [<Literal>]
    let TopMovedOutputCount = 10

    /// The reasons `tally` counted. They sum to `CacheTally.misses tally`: the tally keys its
    /// misses by `CacheMiss.label`, and these are its four cases.
    let ofTally (tally: CacheTally) : CacheMissReasons =
        let count (miss: CacheMiss) =
            tally.Misses |> Map.tryFind (CacheMiss.label miss) |> Option.defaultValue 0L

        { NoKey = count CacheMiss.NoKey
          NoEntry = count CacheMiss.NoEntry
          ParseOnly = count CacheMiss.ParseOnly
          // The label is the case's, whatever outputs it names.
          OutputsMoved = count (CacheMiss.OutputsMoved [])
          TopMovedOutputs =
            tally.MovedOutputs
            |> Map.toList
            |> List.sortBy (fun (output, misses) -> -misses, output)
            |> List.truncate TopMovedOutputCount
            |> List.map (fun (output, misses) -> { Output = output; Misses = misses }) }

/// A tally's `NoEntry` misses as a scan's metrics record them (AUTOMATION-1816):
/// `NoEntry = Unknown + ReferencesUnchanged + (misses neither key would have served)`.
type NoEntryAlternatives =
    {
        /// Misses the signature key would have served.
        SignatureUnchanged: int64
        /// Misses the references key would have served; includes `SignatureUnchanged`.
        ReferencesUnchanged: int64
        /// Misses with nothing recorded to compare with (`NoEntryAlternative.Unknown`).
        Unknown: int64
    }

module NoEntryAlternatives =
    let ofTally (tally: CacheTally) : NoEntryAlternatives =
        let count (outcome: NoEntryAlternative) =
            tally.NoEntryAlternatives
            |> Map.tryFind (NoEntryAlternative.label outcome)
            |> Option.defaultValue 0L

        let signature = count NoEntryAlternative.SignatureUnchanged

        { SignatureUnchanged = signature
          ReferencesUnchanged = signature + count NoEntryAlternative.OnlyReferencesUnchanged
          Unknown = count NoEntryAlternative.Unknown }

/// Would-be-served misses whose verdicts could not be compared, per reason.
type ShadowUnverified =
    {
        /// `ShadowUnverifiedReason.NoCachedVerdict`.
        NoCachedVerdict: int64
        /// `ShadowUnverifiedReason.NoColdVerdict`.
        NoColdVerdict: int64
    }

/// What the references key in shadow found in one scan, as its `scan-metrics.jsonl` record
/// carries it (AUTOMATION-1816 step 2). The misses it would have served are
/// `Agreed + Disagreements + Unverified` (`ReferencesShadowScan.wouldServe`), which is the
/// scan's `noEntryReferencesUnchanged`.
type ReferencesShadowScan =
    {
        Agreed: int64
        /// Every disagreement, in the order found.
        Disagreements: ShadowDisagreement list
        Unverified: ShadowUnverified
        /// The most bytes the shadow held for cold verdicts at any settle in the scan: its
        /// whole addition to the daemon's memory, by exact accounting (`CheckVerdict.heldBytes`).
        HeldBytesPeak: int64
    }

module ReferencesShadowScan =
    let ofTally (tally: CacheTally) : ReferencesShadowScan =
        let shadow = tally.ReferencesShadow

        { Agreed = shadow.Agreed
          Disagreements = List.rev shadow.DisagreementsNewestFirst
          Unverified =
            { NoCachedVerdict = shadow.NoCachedVerdict
              NoColdVerdict = shadow.NoColdVerdict }
          HeldBytesPeak =
            match shadow.HeldBytesNewestFirst with
            | [] -> 0L
            | held -> List.max held }

    /// The misses the references key would have served.
    let wouldServe (scan: ReferencesShadowScan) : int64 =
        scan.Agreed
        + int64 scan.Disagreements.Length
        + scan.Unverified.NoCachedVerdict
        + scan.Unverified.NoColdVerdict

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

    member _.ComparedAlternative(outcome: NoEntryAlternative) =
        Locking.locked lockObj (fun () -> tally <- CacheTally.comparedAlternative outcome tally)

    member _.Shadowed(outcome: ShadowOutcome) =
        Locking.locked lockObj (fun () -> tally <- CacheTally.shadowed outcome tally)

    member _.ShadowHeld(bytes: int64) =
        Locking.locked lockObj (fun () -> tally <- CacheTally.shadowHeld bytes tally)

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
