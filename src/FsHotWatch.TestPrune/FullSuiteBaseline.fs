/// The full-suite WATERMARK (AUTOMATION-110): the last run that executed every
/// configured test project unfiltered and left every project with an accounted-for
/// outcome. An impact-filtered green is only a claim about the whole suite RELATIVE to
/// this run — the tests it skipped were last executed here — so a check with no valid
/// watermark has nothing to be green relative to, and must earn one.
///
/// What makes the watermark a baseline for the tests a later run SKIPS is the pair of
/// durable ledgers beside it:
///
///   * `pending-verification.json` — every symbol changed since, not yet passed by a
///     covering run. A skipped test whose covered code changed is owed through this.
///   * `outstanding-failures.json` — every test red in the last run that executed it. A
///     skipped test that was RED here stays owed through this, and is re-selected
///     (quarantined) on every run until it passes.
///
/// A skipped test is therefore discharged by ONE of: it passed in this run and nothing it
/// covers has changed since; or it is queued/quarantined and will run. Never by silence.
/// That is why the watermark is written for a full-suite run whose every project is
/// accounted for — passed, or red and recorded — and not only for a green one: a red
/// full suite still proves what every OTHER test did, and its reds are carried.
///
/// Staleness the watermark can DETECT is the configured project set growing: a project
/// added to `tests.projects` since has never been in a full-suite run, so the watermark
/// cannot vouch for it. Everything else that could break the chain — an unreadable
/// ledger (AUTOMATION-150) — already widens the next run to the full suite, which
/// re-earns the watermark.
///
/// On-disk shape: one JSON object under `.fshw/test-prune/`, beside the ledgers it
/// composes with. Atomic write (tmp + rename). Same reading rule as the pending
/// queue: a file that EXISTS but cannot be read is `Unreadable`, never "no baseline",
/// because the two force the same recovery (a full-suite run) and a silent downgrade
/// would hide a torn write.
module FsHotWatch.TestPrune.FullSuiteBaseline

open System
open System.IO
open System.Text.Json.Nodes
open FsHotWatch

/// The last accounted-for full-suite run.
type Baseline =
    {
        /// The run whose CTRF reports sit at `.fshw/test-runs/<runId>/`. The verdict
        /// names it, so an impact-filtered green is auditable back to the run its
        /// skipped tests were last executed in.
        RunId: Guid
        EarnedAt: DateTime
        /// The configured projects that run covered. Validity for a later run is
        /// `runnable ⊆ Projects` — see `staleness`.
        Projects: Set<string>
    }

[<RequireQualifiedAccess>]
type LoadedBaseline =
    /// The file was read in full — `None` when no full-suite run has been recorded here
    /// (a fresh clone), `Some` otherwise.
    | Loaded of Baseline option
    /// The file EXISTS and could not be read. The same recovery as no baseline, but
    /// said out loud.
    | Unreadable of reason: string

let sidecarPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "full-suite-baseline.json")

let load (repoRoot: string) : LoadedBaseline =
    let path = sidecarPath repoRoot

    if not (File.Exists path) then
        LoadedBaseline.Loaded None
    else
        try
            match JsonNode.Parse(File.ReadAllText path) with
            | null -> LoadedBaseline.Unreadable "the file holds a bare JSON `null`"
            | root ->
                let obj = root.AsObject()

                let runId =
                    match obj.["runId"] with
                    | null -> None
                    | node ->
                        match Guid.TryParse(node.GetValue<string>()) with
                        | true, g -> Some g
                        | _ -> None

                let earnedAt =
                    match obj.["earnedAt"] with
                    | null -> None
                    | node ->
                        match
                            DateTime.TryParse(node.GetValue<string>(), null, Globalization.DateTimeStyles.RoundtripKind)
                        with
                        | true, d -> Some d
                        | _ -> None

                let projects =
                    match obj.["projects"] with
                    | null -> None
                    | node -> node.AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> Set.ofSeq |> Some

                match runId, earnedAt, projects with
                | Some runId, Some earnedAt, Some projects ->
                    LoadedBaseline.Loaded(
                        Some
                            { RunId = runId
                              EarnedAt = earnedAt
                              Projects = projects }
                    )
                | _ ->
                    LoadedBaseline.Unreadable
                        "the file is missing `runId`, `earnedAt` or `projects`, or one of them is malformed"
        with ex ->
            LoadedBaseline.Unreadable $"%s{ex.GetType().Name}: %s{ex.Message}"

let save (repoRoot: string) (baseline: Baseline) : unit =
    let obj = JsonObject()
    obj.["runId"] <- JsonValue.Create(baseline.RunId.ToString("N"))
    obj.["earnedAt"] <- JsonValue.Create(baseline.EarnedAt.ToString("o"))
    let arr = JsonArray()

    for p in baseline.Projects |> Set.toList |> List.sort do
        arr.Add(JsonValue.Create(p))

    obj.["projects"] <- arr
    FsHwPaths.atomicWriteAllText (sidecarPath repoRoot) (obj.ToJsonString())

/// Why `baseline` cannot vouch for the tests a run over `runnable` skips — `None` when it
/// can. The one staleness this module can decide: a configured project the watermark
/// never executed.
let staleness (runnable: Set<string>) (baseline: Baseline) : string option =
    let missing = Set.difference runnable baseline.Projects

    if Set.isEmpty missing then
        None
    else
        let names = missing |> Set.toList |> String.concat ", "
        let runId = baseline.RunId.ToString("N")
        let earnedAt = baseline.EarnedAt.ToString("u")

        Some
            $"the full-suite baseline (run %s{runId}, %s{earnedAt}) never executed %s{names} — added to \
              `tests.projects` since — so it cannot vouch for what a filtered run skips there"

/// Why there is NO baseline at all, in the words the verdict carries.
let absentReason: string =
    "no full-suite run has been recorded on this ledger yet — a cold repository must earn one before an \
     impact-filtered run can be green relative to it"
