/// The stale-artifact PREFLIGHT: one decision about the whole run, taken BEFORE any
/// suite launches.
///
/// `ArtifactFreshness.stale` has always been RIGHT about what is stale. It was asked
/// too late: its only call site sat inside the per-config body of the PARALLEL run
/// loop, so a group-A project had already written its CTRF before group B was even
/// examined. The observed shape is a three-minute partial-execution red that reads
/// like progress (AUTOMATION-201, hit live three times in one day). The comparison is
/// pure file I/O and is knowable in seconds, so this module asks it about EVERY
/// config first and lets nothing launch until it has an answer.
///
/// HEAL vs REFUSE — heal exactly one case, and only that one.
///
/// `CopyDiffersFromOrigin` IS healed. It names both files, and the repair is provably
/// complete rather than a guess: the build's job at that destination was to copy
/// `origin` to `copy`; writing the origin's bytes across leaves precisely the tree a
/// build would have left. Nothing is inferred, and the destination is regenerable
/// build output, so the repair is idempotent and destroys nothing.
///
/// The other three cases are NOT healed, deliberately:
///   * `AssemblyOlderThanSource` — the work that did not happen is a COMPILE. There
///     is no file anywhere on disk holding the bytes that compile would produce, so
///     any "repair" would be fabrication.
///   * `DepsManifestOlderThanRestore` — likewise. The manifest is GENERATED from the
///     restore by MSBuild's own target; nothing on disk holds the bytes it would
///     produce, and writing a plausible one would put this module in the business of
///     inventing a reference closure.
///   * `InputsUndeterminable` — we could not establish what this run's inputs ARE.
///     Repairing a tree we cannot describe is how silent degradation starts.
/// All three refuse, and all three name `dotnet build` — the remedy that actually works.
///
/// A heal is NEVER silent. Repeated origin/copy disagreements mean something upstream
/// keeps rebuilding origins without their consumers, and a repair that absorbs that forever
/// destroys the only signal it leaves. So every heal is logged AND recorded to a
/// durable ledger, and the ledger drives a circuit breaker: past `Threshold` repairs
/// of ONE file inside `Window`, the preflight stops repairing and refuses instead,
/// naming the file and the count.
///
/// The breaker gates the HEAL, not the run. A tripped breaker on a clean tree changes
/// nothing — nothing is stale, so nothing asks to be repaired and the suite runs.
/// That is what keeps the breaker from becoming the very wedge class this ticket
/// exists to remove, and it is why its refusal message names its own reset.
module FsHotWatch.TestPrune.StaleArtifactPreflight

open System
open System.IO
open System.Text.Json.Nodes
open FsHotWatch

/// One repair, as it happened: when, and which file in a test project's output dir
/// was overwritten with its origin's bytes.
type HealRecord =
    {
        /// UTC instant of the repair.
        At: DateTime
        /// Absolute path of the COPY that was repaired (the destination, not the
        /// origin) — the identity the breaker counts by, because it is the thing
        /// that keeps going stale.
        File: string
    }

/// How far back the breaker looks. Michael's starting point (AUTOMATION-201 approval):
/// ~10 repairs in 2 days is no longer a repair, it is a finding.
let Window = TimeSpan.FromDays 2.0

/// Repairs of ONE file inside `Window` that stop being a repair and start being a
/// finding. Counted per destination file, not run-wide: ten different files repaired
/// once each is a busy day, one file repaired ten times is a broken build graph.
[<Literal>]
let Threshold = 10

/// Absolute path to the heal ledger for this repo. Alongside the other fshw-owned
/// plugin state under `.fshw/test-prune/`, next to `pending-verification.json`.
let ledgerPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "stale-heals.json")

/// Read the ledger. NEVER throws, and — unlike `PendingVerification.load` — an
/// unreadable ledger is deliberately treated as EMPTY rather than as an unknown that
/// blocks the run.
///
/// The asymmetry is the point. That sidecar records outstanding test DEBT, so "I
/// could not read it" must widen to a full suite or tests get skipped. This one
/// records repair HISTORY, and the only thing a lost history can do is delay a
/// breaker trip. Failing closed here would mean a corrupt diagnostic file refuses
/// every run — a brand-new wedge class, in the ticket that exists to delete one.
/// Nothing unsafe rides on it either way: every launch is still gated by the
/// post-repair re-verification, which reads the actual bytes.
let loadLedger (repoRoot: string) : HealRecord list =
    let path = ledgerPath repoRoot

    if not (File.Exists path) then
        []
    else
        try
            match JsonNode.Parse(File.ReadAllText path) with
            | null -> []
            | root ->
                root.AsArray()
                |> Seq.choose (fun node ->
                    match node with
                    | null -> None
                    | n ->
                        try
                            Some
                                { At = n["at"].GetValue<DateTime>().ToUniversalTime()
                                  File = n["file"].GetValue<string>() }
                        with _ ->
                            None)
                |> List.ofSeq
        with ex ->
            Logging.warn
                "test-prune"
                $"stale-artifact heal ledger at %s{path} could not be read (%s{ex.GetType().Name}: %s{ex.Message}) — \
                  treating it as empty. Repairs stay safe (each is re-verified against the bytes); only the \
                  repeat-repair circuit breaker loses its history."

            []

/// Persist the ledger atomically, dropping everything older than `Window` — the
/// breaker only ever asks about that window, so anything outside it is dead weight
/// and the file cannot grow without bound.
let saveLedger (repoRoot: string) (now: DateTime) (records: HealRecord list) : unit =
    let arr = JsonArray()

    for r in
        records
        |> List.filter (fun r -> now - r.At <= Window)
        |> List.sortBy (fun r -> r.At) do
        let o = JsonObject()
        o.Add("at", JsonValue.Create(r.At.ToUniversalTime().ToString("o")))
        o.Add("file", JsonValue.Create(r.File))
        arr.Add(o)

    FsHwPaths.atomicWriteAllText (ledgerPath repoRoot) (arr.ToJsonString())

/// How many times `file` has been repaired inside `Window`.
let healsInWindow (now: DateTime) (records: HealRecord list) (file: string) : int =
    records
    |> List.filter (fun r -> r.File = file && now - r.At <= Window)
    |> List.length

/// The repair for a stale verdict, as `(origin, copy)` — `None` when this module
/// refuses to guess. See the module header for why only one case is healable.
let repairFor (stale: ArtifactFreshness.StaleInput) : (string * string) option =
    match stale with
    | ArtifactFreshness.CopyDiffersFromOrigin(origin, copy) -> Some(origin, copy)
    | ArtifactFreshness.AssemblyOlderThanSource _
    | ArtifactFreshness.DepsManifestOlderThanRestore _
    | ArtifactFreshness.InputsUndeterminable _ -> None

/// What to actually DO about a stale verdict this module would not repair. Names a
/// command, never a ritual.
///
/// Deliberately does NOT say "stop the daemon". That is folk knowledge and it is
/// wrong as a remedy: the task cache is file-backed and survives a restart, so a
/// restart alone clears nothing. `dotnet build` is what re-emits the file pair every
/// verdict names.
let remedyFor (stale: ArtifactFreshness.StaleInput) : string =
    match stale with
    | ArtifactFreshness.CopyDiffersFromOrigin _ ->
        "build the consumer (normally with a plain `dotnet build`) so its copy target runs. If that consumer build \
         reports success and the bytes still differ, run `dotnet build --no-incremental` or delete the named copy \
         and build again"
    | ArtifactFreshness.AssemblyOlderThanSource _ ->
        "run `dotnet build` — the compile has not run since that edit, so there is nothing on disk to copy from"
    | ArtifactFreshness.DepsManifestOlderThanRestore _ ->
        "run `dotnet build` — only a build regenerates the manifest from the restore. A `dotnet restore` will NOT: \
         it rewrites `obj/project.assets.json` and never touches `bin/**/*.deps.json`, which is why this state \
         outlives the automatic recovery that repairs the compile. Do NOT add a direct `ProjectReference` to \
         whatever failed to load — that puts an entry in the manifest and makes the symptom vanish while the \
         superseded restore stays in place"
    | ArtifactFreshness.InputsUndeterminable _ ->
        "run `dotnet build` and read its error — it fails loudly on the same project file this gate could not read"

/// The words EVERY refusal this module produces begins with, and the only thing any
/// downstream surface uses to recognise one.
///
/// It exists because "waiting on build" is TWO causes wearing one label, and they need
/// opposite remedies. The build-ordering race — a project's artifact was not produced
/// yet — settles by itself, so "re-run once the build settles" is right for it. THIS
/// one does not settle: the artifact exists and holds the wrong bytes, so re-running
/// returns the identical refusal, which is the exact defect AUTOMATION-201 exists to
/// delete. A reader given the wrong half of that pair loses a gate cycle to it.
///
/// Prose, not a bracketed tag, so the marker is also the sentence the operator reads;
/// and one `[<Literal>]` shared by the producers below and `isStaleOutputDeferral`,
/// so the recogniser cannot drift from what is actually emitted. `refusalMessages`
/// exists to keep that honest under test: it enumerates every refusal shape this
/// module can build, and each must be recognised.
[<Literal>]
let StaleOutputMarker = "stale build output — "

/// Does this deferral message describe one of THIS module's refusals?
///
/// `Contains`, not `StartsWith`: by the time the CLI reads it the plugin has prefixed
/// the project name (`"P: waiting on build — …"`). Ordinal, because this is a marker,
/// not prose to be compared culturally.
let isStaleOutputDeferral (message: string) : bool =
    not (isNull message)
    && message.Contains(StaleOutputMarker, StringComparison.Ordinal)

/// A project the run must not launch, and everything a reader needs in order to act.
type Refusal =
    {
        /// The test project, named in full — never abbreviated into a truncated list.
        Project: string
        /// Cause AND remedy, in one string, so the actionable half cannot be lost by
        /// a surface that only shows one line. Always begins with `StaleOutputMarker`.
        Reason: string
    }

/// The preflight's verdict about the whole run.
type Outcome =
    {
        /// Copies repaired this run, absolute paths. Always reported, even on success:
        /// a repair that fires every run is itself the finding.
        Healed: string list
        /// EMPTY means every target is certified fresh and the suite may launch.
        /// Non-empty means nothing launches.
        Refusals: Refusal list
    }

/// THE FLOOR: a preflight that examined nothing is not a certified-fresh tree.
///
/// The caller resolves every runnable config to a build-output target and DROPS the
/// ones it cannot resolve — `deriveProjectBin` answers `None` for a runner command with
/// no `--project`, which is a legitimate configuration. The drop is silent by
/// construction: an unexamined project raises no refusal, and an `Outcome` with no
/// refusals is byte-for-byte the same value as one over a tree that was actually
/// checked. So a derivation that regressed — a renamed flag, an args-shape change —
/// would switch this entire gate off while every run stayed green. That is the shape of
/// bug this repo spent AUTOMATION-198 and AUTOMATION-303 removing, and the preflight
/// added by AUTOMATION-201 reintroduced a door to it.
///
/// It REPORTS rather than refuses, deliberately. Refusing would wedge every repo whose
/// runners legitimately take no `--project`, and this ticket's approval comment forbids
/// trading one wedge class for another. Naming the gap costs nothing and makes a total
/// regression loud.
///
/// `None` means every runnable project was examined — there is nothing to say.
let coverageReport (runnable: string list) (examined: string list) : string option =
    let missed = runnable |> List.filter (fun p -> not (List.contains p examined))
    let missedNames = String.concat ", " missed

    if List.isEmpty missed then
        None
    elif List.isEmpty examined then
        Some
            $"stale-artifact preflight examined 0 of %d{List.length runnable} project(s) that are about to run — no \
              build-output target could be derived for ANY of them (%s{missedNames}), so this run is NOT protected \
              against stale build output. If those runners take a `--project` argument this is a defect in the \
              gate, not in your tree."
    else
        Some
            $"stale-artifact preflight examined %d{List.length examined} of %d{List.length runnable} project(s) — no \
              build-output target could be derived for %s{missedNames} (a runner command with no `--project` \
              argument), so those are unprotected against stale build output."

/// Overwrite `copy` with `origin`'s bytes — the copy the build meant to make.
let private applyRepair (origin: string) (copy: string) : Result<unit, string> =
    try
        File.Copy(origin, copy, overwrite = true)
        Ok()
    with ex ->
        Error $"%s{ex.GetType().Name}: %s{ex.Message}"

/// EVERY refusal reason this module can produce, built in ONE place.
///
/// Four shapes used to be four inline interpolations scattered through `runWithBudget`,
/// which is how three of them ended up phrased differently enough that no single
/// predicate could recognise them. They are here so `StaleOutputMarker` is applied
/// exactly once per shape and `refusalMessages` can enumerate them for a test that
/// proves the recogniser sees all four.
module Reason =

    /// Stale, and this module will not guess at the repair.
    let stale (s: ArtifactFreshness.StaleInput) : string =
        $"%s{StaleOutputMarker}%s{ArtifactFreshness.describe s}; would run --no-build on stale code. \
          Remedy: %s{remedyFor s}"

    /// Repaired as far as the budget allowed and still not certifiable.
    let stillStaleAfterRepairs (rounds: int) (s: ArtifactFreshness.StaleInput) : string =
        $"%s{StaleOutputMarker}STILL stale after %d{rounds} automatic repair round(s) — \
          %s{ArtifactFreshness.describe s}; refusing to run --no-build on bytes this gate could not certify. \
          Remedy: %s{remedyFor s}"

    /// The repair was attempted and the write itself failed.
    let repairFailed (error: string) (s: ArtifactFreshness.StaleInput) : string =
        $"%s{StaleOutputMarker}the automatic repair FAILED (%s{error}) — %s{ArtifactFreshness.describe s}. \
          Remedy: %s{remedyFor s}"

    /// The breaker's refusal: what tripped, how often, and — required, not optional —
    /// how to get moving again. A hard fail with no stated way out would re-create the
    /// "re-run the identical command, get the identical failure" defect this ticket is
    /// about.
    let breakerTripped (repoRoot: string) (file: string) (count: int) : string =
        $"%s{StaleOutputMarker}auto-repair has already fired %d{count} times for %s{file} within the last \
          %.0f{Window.TotalDays} days, so this run REFUSES to repair it again. Something upstream keeps rebuilding \
          origins without their consumers, and repairing that build-scope gap silently forever would hide it — \
          root-cause the producing build scope and make it include the consumer. To resume auto-repair: delete %s{ledgerPath repoRoot} (the count also ages out on its \
          own %.0f{Window.TotalDays} days after the last repair)."

/// One sample of every refusal shape `Reason` can build, for a test that asserts each
/// is recognised by `isStaleOutputDeferral`.
///
/// A recogniser is only as good as its coverage of what is actually emitted, and a
/// fifth shape added without a marker would be invisible to every surface that keys
/// off one. Adding a constructor to `Reason` without adding it here leaves that test
/// passing on stale evidence, so this list is named in `Reason`'s own doc.
let refusalMessages (repoRoot: string) : string list =
    let sample = ArtifactFreshness.CopyDiffersFromOrigin("/o.dll", "/c.dll")

    [ Reason.stale sample
      Reason.stillStaleAfterRepairs 8 sample
      Reason.repairFailed "IOException: locked" sample
      Reason.breakerTripped repoRoot "/c.dll" 10 ]

/// How many detect→repair rounds a run may spend before it gives up and refuses.
///
/// A round repairs every stale copy it can SEE, but `ArtifactFreshness.stale` reports
/// the FIRST finding per project, so a project holding several inverted copies needs
/// several rounds. Each round strictly reduces the set (a repaired copy is
/// byte-identical to its origin), so this bound is a safety net against a pathological
/// tree, not the normal exit — and exhausting it REFUSES rather than proceeding,
/// because bytes this module could not certify are exactly what the gate exists to
/// keep out of a test run.
[<Literal>]
let MaxRepairRounds = 8

/// `run` with an explicit repair budget. See `MaxRepairRounds`.
///
/// `targets` is every config that would actually launch, already resolved to its
/// build-output target — impact-skipped projects are excluded by the caller, since a
/// project that is not going to run has no artifacts worth walking.
///
/// Takes `(string * ArtifactFreshness.RunnerTarget) list` rather than `TestConfig`s so
/// this module stays free of the plugin's config shape and is drivable from a test
/// with three lines of setup.
let runWithBudget
    (budget: int)
    (repoRoot: string)
    (now: DateTime)
    (targets: (string * ArtifactFreshness.RunnerTarget) list)
    : Outcome =
    let refusalOf (project: string) (stale: ArtifactFreshness.StaleInput) =
        { Project = project
          Reason = Reason.stale stale }

    /// One detect→repair round. A FRESH `ArtifactFreshness.Cache` every time is
    /// mandatory, never an optimisation to reclaim: the cache memoises content hashes
    /// by path, so reusing it after a repair would replay the PRE-repair hash and
    /// certify the tree on evidence this module itself invalidated — the exact
    /// self-certification the gate exists to prevent.
    let detect () =
        let cache = ArtifactFreshness.Cache()

        targets
        |> List.choose (fun (project, target) ->
            ArtifactFreshness.stale cache target |> Option.map (fun s -> project, s))

    /// `healed` accumulates FORWARD, in repair order, so every exit can report it as-is.
    /// It is bounded by the number of stale copies in one run and appended once per
    /// round (`budget` rounds at most), so the append costs nothing worth a reversal
    /// invariant that three exits would each have to unwind correctly.
    let rec round (remaining: int) (ledger: HealRecord list) (healed: string list) : Outcome =
        match detect () with
        | [] ->
            // Certified fresh — by a read of the bytes taken AFTER the last repair.
            { Healed = healed; Refusals = [] }
        | detected when remaining <= 0 ->
            { Healed = healed
              Refusals =
                detected
                |> List.map (fun (project, s) ->
                    { Project = project
                      Reason = Reason.stillStaleAfterRepairs budget s }) }
        | detected ->
            // Split by what this module is WILLING to do, not by what went wrong — and
            // carry the repair pair through with the entry that has one. Re-asking
            // `repairFor` further down would reintroduce a `None` arm the split has
            // already made impossible, i.e. an unreachable branch that has to be
            // written, read and covered forever.
            let repairable =
                detected
                |> List.choose (fun (project, s) -> repairFor s |> Option.map (fun pair -> project, s, pair))

            // AUTOMATION-358 Case 3 — INSTRUMENT, do not build the capability.
            //
            // `CopyDiffersFromOrigin` is the one artifact-staleness wedge
            // AUTOMATION-245 left open, and only TestPrune can see it. Whether the
            // framework needs a capability for it was decided the honest way: ONE
            // observed occurrence does not justify one, so count it and let the
            // answer come from data. If this line never appears in a working week's
            // logs, the capability question closes as a NO — a finding, not a guess.
            //
            // Logged over `repairable`, which `repairFor` has already narrowed to
            // exactly the `CopyDiffersFromOrigin` cases. A fresh `match` here would
            // add two arms nothing reaches — the coverage ratchet caught precisely
            // that on the first attempt, and an unreachable branch written to
            // satisfy a log is worse than the log is worth.
            //
            // Per occurrence, not an aggregate: the open question is WHICH project
            // and WHICH file pair, since that distinguishes one build's timestamp
            // inversion from something structural.
            for (project, _, (origin, copy)) in repairable do
                Logging.info
                    "test-prune"
                    $"artifact-copy-differs (AUTOMATION-358): %s{project} — origin %s{origin} differs from copy %s{copy}"

            let unrepairable =
                detected |> List.filter (fun (_, s) -> Option.isNone (repairFor s))

            // The breaker is consulted ONLY for files this run is about to repair, so a
            // tripped breaker never blocks a run with nothing stale in it. That is what
            // stops it becoming a new wedge class.
            let tripped, toRepair =
                repairable
                |> List.partition (fun (_, _, (_, copy)) -> healsInWindow now ledger copy >= Threshold)

            if not (List.isEmpty unrepairable) || not (List.isEmpty tripped) then
                // Nothing is repaired in a round that is going to refuse anyway: a
                // repair whose run never launches is a ledger entry bought for nothing.
                { Healed = healed
                  Refusals =
                    (unrepairable |> List.map (fun (project, s) -> refusalOf project s))
                    @ (tripped
                       |> List.map (fun (project, _, (_, copy)) ->
                           { Project = project
                             Reason = Reason.breakerTripped repoRoot copy (healsInWindow now ledger copy) }))
                    @ (toRepair |> List.map (fun (project, s, _) -> refusalOf project s)) }
            else
                // Repair. Every outcome is logged BY NAME whether it worked or not — a
                // repair nobody can see is a signal destroyed.
                let outcomes =
                    toRepair
                    |> List.map (fun (project, s, (origin, copy)) ->
                        match applyRepair origin copy with
                        | Ok() ->
                            Logging.warn
                                "test-prune"
                                $"%s{project}: REPAIRED a stale build-output copy BEFORE running any suite — wrote \
                                  %s{origin} over %s{copy}, which held different bytes. The build should have made \
                                  this copy and its incremental check skipped it (equal timestamps). Recorded to \
                                  %s{ledgerPath repoRoot}; %d{Threshold} repairs of one file inside \
                                  %.0f{Window.TotalDays} days refuse instead of repairing."

                            Ok copy
                        | Error e ->
                            Error
                                { Project = project
                                  Reason = Reason.repairFailed e s })

                let repaired =
                    outcomes
                    |> List.choose (function
                        | Ok copy -> Some copy
                        | Error _ -> None)

                let failures =
                    outcomes
                    |> List.choose (function
                        | Ok _ -> None
                        | Error refusal -> Some refusal)

                let ledger' = ledger @ (repaired |> List.map (fun f -> { At = now; File = f }))

                if not (List.isEmpty repaired) then
                    saveLedger repoRoot now ledger'

                if not (List.isEmpty failures) then
                    { Healed = healed @ repaired
                      Refusals = failures }
                else
                    round (remaining - 1) ledger' (healed @ repaired)

    round budget (loadLedger repoRoot) []

/// Decide the run, before any of it starts. `Refusals` empty means every target was
/// certified fresh by a post-repair read of the actual bytes, and the suite may launch.
let run (repoRoot: string) (now: DateTime) (targets: (string * ArtifactFreshness.RunnerTarget) list) : Outcome =
    runWithBudget MaxRepairRounds repoRoot now targets
