/// Deps-freshness gate: detect when a project's restored dependency state
/// (`obj/project.assets.json`) is out of sync with its declared deps, attempt a
/// one-shot restore to recover, and — only if recovery fails — fail fast with a
/// single actionable diagnostic so the type-checker never produces the phantom
/// "namespace/type not found" error-storm a stale restore otherwise causes.
///
/// The detection and orchestration are kept pure (injected runner + freshness
/// probe) so the branch logic is unit-testable without shelling out or touching
/// FCS. See docs/plans/2026-06-02-deps-freshness-gate.md.
module FsHotWatch.DepsFreshness

open System
open System.Collections.Concurrent
open System.IO
open FsHotWatch.ProcessHelper

/// Whether a project's restored assets are in sync with its declared deps.
type Freshness =
    | Fresh
    | Stale

/// Plugin name under which the gate reports its fail-fast diagnostic. Shows up
/// in `fshw errors` / `check` and feeds the aggregate verdict like any plugin.
[<Literal>]
let pluginName = "deps"

/// Pure freshness comparator. Stale when assets are missing, or older than any
/// dependency-declaring file. No dep files → Fresh (nothing can invalidate the
/// assets). Exposed for unit testing without disk.
let compareFreshness (assetsMtime: DateTime option) (depFileMtimes: DateTime list) : Freshness =
    match assetsMtime with
    | None -> Stale
    | Some assets ->
        // List.exists over [] is false, so no-dep-files correctly yields Fresh.
        if depFileMtimes |> List.exists (fun m -> m > assets) then
            Stale
        else
            Fresh

/// Names of dependency-declaring files that live alongside / above a project.
/// `*.fsproj` is handled separately (it is the project file itself).
let private ancestorDepFileNames =
    [ "Directory.Packages.props"
      "Directory.Build.props"
      "paket.lock"
      "paket.dependencies" ]

/// Enumerate the dependency-declaring files that govern `fsprojPath`, walking
/// from the project directory up to (and including) `repoRoot` for the
/// ancestor-scoped files (MSBuild import + paket / dotnet-tools semantics) and
/// always including the project's own `.fsproj`. Only files that exist on disk
/// are returned. The nearest match per name wins so we don't double-count the
/// same logical file from multiple levels.
let dependencyFiles (fsprojPath: string) (repoRoot: string) : string list =
    let projDir = Path.GetFullPath(Path.GetDirectoryName(fsprojPath: string))
    let root = Path.GetFullPath(repoRoot)

    // Directories from projDir up to root (inclusive). Stops if projDir is not
    // under root (defensive — then just the project directory is scanned).
    let dirs =
        let rec walk (dir: string) acc =
            let acc = dir :: acc

            if String.Equals(dir, root, StringComparison.Ordinal) then
                acc
            else
                let parent = Path.GetDirectoryName(dir)

                if
                    String.IsNullOrEmpty parent
                    || String.Equals(parent, dir, StringComparison.Ordinal)
                then
                    acc
                else
                    walk parent acc

        walk projDir [] |> List.rev

    let ancestorMatches =
        ancestorDepFileNames
        |> List.choose (fun name -> dirs |> List.map (fun d -> Path.Combine(d, name)) |> List.tryFind File.Exists)

    let toolsJson =
        dirs
        |> List.map (fun d -> Path.Combine(d, ".config", "dotnet-tools.json"))
        |> List.tryFind File.Exists
        |> Option.toList

    let projFile = if File.Exists fsprojPath then [ fsprojPath ] else []

    projFile @ ancestorMatches @ toolsJson

/// Absolute path to a project's restored assets file.
let assetsPath (fsprojPath: string) : string =
    let projDir = Path.GetDirectoryName(fsprojPath: string)
    Path.Combine(projDir, "obj", "project.assets.json")

/// True when the project's `obj/project.assets.json` exists on disk. Used as the
/// post-restore "did recovery actually produce assets?" check.
let assetsPresent (fsprojPath: string) : bool = File.Exists(assetsPath fsprojPath)

/// Read a file's last-write mtime (UTC), or None when it does not exist.
let private tryMtime (path: string) : DateTime option =
    if File.Exists path then
        Some(File.GetLastWriteTimeUtc path)
    else
        None

/// Disk-backed freshness probe for one project. Reads the assets mtime and the
/// dep-file mtimes, then defers to the pure `compareFreshness`.
let detectProjectFreshness (repoRoot: string) (fsprojPath: string) : Freshness =
    let assetsMtime = tryMtime (assetsPath fsprojPath)
    let depMtimes = dependencyFiles fsprojPath repoRoot |> List.choose tryMtime
    compareFreshness assetsMtime depMtimes

/// Compute the stale signature for a project: max dep-file mtime ticks (0 when
/// no dep files exist). This debounces recovery — the same unchanged stale state
/// keeps the same signature (no re-restore), while bumping a dep file moves it
/// forward and re-arms recovery.
let internal staleSignature (repoRoot: string) (fsprojPath: string) : int64 =
    dependencyFiles fsprojPath repoRoot
    |> List.choose tryMtime
    |> function
        | [] -> 0L
        | mtimes -> mtimes |> List.map (fun m -> m.Ticks) |> List.max

/// Debounce tracker: remembers, per project, the last stale signature recovery
/// was attempted for. Thread-safe; shared across scan cycles by the daemon.
type RecoveryTracker() =
    let attempted = ConcurrentDictionary<string, int64>()

    /// True when a restore should be attempted for this (project, signature) —
    /// i.e. we have not already attempted recovery for this exact stale state.
    member _.ShouldAttempt(proj: string, sig_: int64) : bool =
        match attempted.TryGetValue proj with
        | true, prev -> prev <> sig_
        | false, _ -> true

    /// Record that recovery was attempted for (project, signature).
    member _.MarkAttempted(proj: string, sig_: int64) = attempted[proj] <- sig_

    /// Forget any recorded attempt for a project (e.g. after it recovered) so a
    /// future regression is treated freshly.
    member _.Clear(proj: string) = attempted.TryRemove proj |> ignore

/// Injected restore runner: given a project directory, runs the restore and
/// returns the outcome. Production shells `dotnet restore` (+ paket / tool
/// restore as applicable); tests inject a fake.
type RestoreRunner = string -> ProcessOutcome

/// Outcome of the gate for a single project.
type GateResult =
    /// Assets fresh — analyze normally.
    | Proceed
    /// Was stale, restore recovered it — analyze, and clear any prior gate diagnostic.
    | RecoveredOk
    /// Still stale but recovery already attempted for this state — skip FCS, keep prior diagnostic.
    | SkipAlreadyAttempted
    /// Recovery failed or assets still stale after a clean restore — emit ONE
    /// diagnostic (message + detail) and skip FCS for this project.
    | FailFast of message: string * detail: string

/// Build the fail-fast message tail from a restore outcome.
let private restoreFailureMessage (proj: string) (outcome: ProcessOutcome) : string * string =
    let projName = Path.GetFileName proj

    match outcome with
    | Failed(code, out) ->
        let tail = StringHelpers.truncateOutput 10 out

        $"deps: project.assets.json stale for %s{projName} and 'dotnet restore' failed (exit %d{code}). Fix deps manually, then re-run.",
        tail
    | TimedOut(after, tail) ->
        $"deps: project.assets.json stale for %s{projName} and 'dotnet restore' timed out after %d{int after.TotalSeconds}s. Fix deps manually, then re-run.",
        tail
    | Succeeded _ ->
        // Restore reported success but assets are still absent — a different
        // failure mode (e.g. restore didn't regenerate assets for this project).
        $"deps: project.assets.json still missing for %s{projName} after 'dotnet restore' succeeded. Fix deps manually, then re-run.",
        ""

/// Pure gate orchestration over an injected freshness probe + restore runner +
/// debounce tracker. Attempts recovery at most once per distinct stale state.
///
/// `probe` returns the current freshness; `signatureOf` yields the stale
/// signature used for debounce; `runner` performs the restore; `assetsPresent`
/// reports whether `obj/project.assets.json` exists at all.
///
/// Post-restore policy: a **successful** restore means the package graph is
/// consistent by definition, so we trust it and proceed — we do NOT re-run the
/// mtime probe, because `dotnet restore` is a no-op (exit 0, assets untouched)
/// when packages are already up-to-date. Re-probing would falsely report stale
/// whenever the trigger was a benign `.fsproj` source-list edit rather than a
/// dependency bump. The only post-success failure is assets *still missing
/// entirely* (restore couldn't produce them) — a genuine broken state.
let evaluateProject
    (probe: string -> Freshness)
    (signatureOf: string -> int64)
    (assetsPresent: string -> bool)
    (runner: RestoreRunner)
    (tracker: RecoveryTracker)
    (proj: string)
    : GateResult =
    match probe proj with
    | Fresh ->
        // Healthy: forget any prior attempt so a future regression re-arms.
        tracker.Clear proj
        Proceed
    | Stale ->
        let sig_ = signatureOf proj

        if not (tracker.ShouldAttempt(proj, sig_)) then
            SkipAlreadyAttempted
        else
            tracker.MarkAttempted(proj, sig_)
            let outcome = runner proj

            match outcome with
            | Succeeded _ ->
                if assetsPresent proj then
                    tracker.Clear proj
                    RecoveredOk
                else
                    let msg, detail = restoreFailureMessage proj outcome
                    FailFast(msg, detail)
            | Failed _
            | TimedOut _ ->
                let msg, detail = restoreFailureMessage proj outcome
                FailFast(msg, detail)

/// Per-step restore timeout. A hung `dotnet`/`paket` restore (seen in practice:
/// a `paket restore` wedged ~17 min at 0% CPU) would otherwise block the whole
/// scan indefinitely; bounding each step surfaces it as a `TimedOut` fail-fast
/// instead. Generous enough for a cold restore of a large solution.
let restoreStepTimeout = TimeSpan.FromMinutes 5.0

/// Production restore runner. Runs `dotnet restore` in the project directory,
/// then `dotnet paket restore` when a `paket.dependencies` is in scope and
/// `dotnet tool restore` when a `.config/dotnet-tools.json` is in scope. The
/// first non-success short-circuits and is returned. Each step is bounded by
/// `restoreStepTimeout`. Uses ProcessHelper so stdout/stderr are drained
/// concurrently (no deadlock).
let productionRestoreRunner (repoRoot: string) : RestoreRunner =
    fun fsprojPath ->
        let projDir = Path.GetDirectoryName(fsprojPath: string)
        let depFiles = dependencyFiles fsprojPath repoRoot

        let hasName (name: string) =
            depFiles
            |> List.exists (fun f -> String.Equals(Path.GetFileName f, name, StringComparison.OrdinalIgnoreCase))

        let steps =
            [ yield ("restore", $"restore \"%s{fsprojPath}\"")
              if hasName "paket.dependencies" then
                  yield ("paket-restore", "paket restore")
              if hasName "dotnet-tools.json" then
                  yield ("tool-restore", "tool restore") ]

        // Run each step in order; the first non-success short-circuits and is
        // returned. A fully-successful chain returns `Succeeded ""` (the final
        // step's stdout is not used by the caller — it only matches `Succeeded _`).
        let rec run remaining =
            match remaining with
            | [] -> Succeeded ""
            | (_label, args) :: rest ->
                match runProcessWithTimeout "dotnet" args projDir [] restoreStepTimeout with
                | Succeeded _ -> run rest
                | other -> other

        run steps
