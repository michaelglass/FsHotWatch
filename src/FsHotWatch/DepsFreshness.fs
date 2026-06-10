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
///
/// This is an mtime-based FAST PATH, not a content oracle: a preserved-mtime dep
/// rewrite (rsync -a / git checkout) leaves a changed `paket.lock` looking older
/// than the assets, so this would report Fresh. That residual gap is closed by
/// `evaluateProject`'s content-drift cross-check against the content-hashed
/// `staleSignature` — a `Fresh` verdict here whose dep-content signature drifted
/// is still re-restored. See docs/adr-008-mtime-is-not-a-content-oracle.md.
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

/// Directories from a project's directory up to (and including) `repoRoot`.
/// Stops if the project dir is not under root (defensive — then just the
/// project directory is scanned). Shared by the dep-file enumeration and the
/// tools-manifest probe so both walk the same ancestor chain.
let private ancestorDirs (fsprojPath: string) (repoRoot: string) : string list =
    let projDir = Path.GetFullPath(Path.GetDirectoryName(fsprojPath: string))
    let root = Path.GetFullPath(repoRoot)

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

/// Enumerate the dependency-declaring files that govern `fsprojPath`, walking
/// from the project directory up to (and including) `repoRoot` for the
/// ancestor-scoped files (MSBuild import + paket semantics) and always including
/// the project's own `.fsproj`. Only files that exist on disk are returned. The
/// nearest match per name wins so we don't double-count the same logical file
/// from multiple levels.
///
/// `.config/dotnet-tools.json` is deliberately NOT included: a project's
/// `obj/project.assets.json` is derived only from the fsproj + paket/nuget
/// inputs — the dotnet-tools manifest never participates in a project's package
/// graph. Counting it here made every project look stale on any dotnet-tool
/// version bump, triggering per-project restore recovery and skipped scans.
/// (See `toolsManifest` below for the restore-runner's separate, freshness-
/// independent need to know whether a tool manifest is in scope.)
let dependencyFiles (fsprojPath: string) (repoRoot: string) : string list =
    let dirs = ancestorDirs fsprojPath repoRoot

    let ancestorMatches =
        ancestorDepFileNames
        |> List.choose (fun name -> dirs |> List.map (fun d -> Path.Combine(d, name)) |> List.tryFind File.Exists)

    let projFile = if File.Exists fsprojPath then [ fsprojPath ] else []

    projFile @ ancestorMatches

/// The nearest `.config/dotnet-tools.json` governing `fsprojPath`, if any.
/// Used ONLY by the restore runner to decide whether to run `dotnet tool
/// restore` — it is intentionally absent from `dependencyFiles` because it does
/// not affect a project's package graph / assets freshness.
let toolsManifest (fsprojPath: string) (repoRoot: string) : string option =
    ancestorDirs fsprojPath repoRoot
    |> List.map (fun d -> Path.Combine(d, ".config", "dotnet-tools.json"))
    |> List.tryFind File.Exists

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

/// Compute the stale signature for a project: a content hash over every
/// dependency file (path + on-disk bytes). This debounces recovery — the same
/// unchanged stale state keeps the same signature (no re-restore), while ANY
/// content change to a dep file moves it forward and re-arms recovery.
///
/// mtime is NOT a content oracle: the previous implementation used max dep-file
/// mtime ticks, which `rsync -a`/`cp -p`/`tar -x`/a git checkout that restores
/// an old mtime leaves byte-identical after a content rewrite — so a genuinely
/// changed `paket.lock`/`Directory.Packages.props` would never re-arm recovery.
/// Content-hashing matches the BuildInputsHasher (Bug 1) and
/// CheckCache.TimestampCacheKeyProvider precedents. See
/// docs/adr-008-mtime-is-not-a-content-oracle.md.
let internal staleSignature (repoRoot: string) (fsprojPath: string) : string =
    let parts =
        dependencyFiles fsprojPath repoRoot
        |> List.sort
        |> List.map (fun path ->
            let hash =
                try
                    FsHotWatch.CheckCache.sha256Hex (File.ReadAllText path)
                with _ ->
                    // A dep file that vanished between enumeration and read, or a
                    // transient lock: fold a distinct sentinel rather than a real
                    // hash so the state is still observably different.
                    "unreadable"

            $"%s{path}@%s{hash}")

    FsHotWatch.CheckCache.sha256Hex (String.concat "\n" parts)

/// Debounce tracker: remembers, per project, the last stale signature recovery
/// was attempted for. Thread-safe; shared across scan cycles by the daemon.
type RecoveryTracker() =
    let attempted = ConcurrentDictionary<string, string>()
    let freshSignatures = ConcurrentDictionary<string, string>()

    /// True when a restore should be attempted for this (project, signature) —
    /// i.e. we have not already attempted recovery for this exact stale state.
    member _.ShouldAttempt(proj: string, sig_: string) : bool =
        match attempted.TryGetValue proj with
        | true, prev -> prev <> sig_
        | false, _ -> true

    /// Record that recovery was attempted for (project, signature).
    member _.MarkAttempted(proj: string, sig_: string) = attempted[proj] <- sig_

    /// True when the dep-content signature differs from the last one this project
    /// was observed Fresh / recovered at. A first sighting is NOT drift (nothing
    /// to compare against yet). This is the content-aware escape hatch for the
    /// mtime probe, which a preserved-mtime dep rewrite can fool into reporting
    /// Fresh. See docs/adr-008-mtime-is-not-a-content-oracle.md.
    member _.HasContentDrifted(proj: string, sig_: string) : bool =
        match freshSignatures.TryGetValue proj with
        | true, prev -> prev <> sig_
        | false, _ -> false

    /// Record the dep-content signature a project was last observed fresh at, so
    /// a later preserved-mtime content change is detectable as drift.
    member _.RecordFreshSignature(proj: string, sig_: string) = freshSignatures[proj] <- sig_

    /// Forget any recorded attempt for a project (e.g. after it recovered) so a
    /// future regression is treated freshly. The fresh-content baseline is kept
    /// so subsequent drift remains detectable.
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
///
/// Content-drift escape hatch: the `probe` is mtime-based (assets vs dep
/// mtimes) and a preserved-mtime dep rewrite (rsync -a / git checkout) can fool
/// it into reporting Fresh while the dep CONTENT actually changed. So a
/// `Fresh` verdict is cross-checked against the dep-content signature: if it
/// drifted from the value the project was last observed fresh/recovered at, we
/// treat it as Stale (debounced like any other stale state) rather than
/// proceeding on an artifact built against different deps. See
/// docs/adr-008-mtime-is-not-a-content-oracle.md.
let evaluateProject
    (probe: string -> Freshness)
    (signatureOf: string -> string)
    (assetsPresent: string -> bool)
    (runner: RestoreRunner)
    (tracker: RecoveryTracker)
    (proj: string)
    : GateResult =
    // Shared stale handling: debounce, attempt restore, record the new fresh
    // content baseline on success so subsequent drift is detectable.
    let handleStale (sig_: string) : GateResult =
        if not (tracker.ShouldAttempt(proj, sig_)) then
            SkipAlreadyAttempted
        else
            tracker.MarkAttempted(proj, sig_)
            let outcome = runner proj

            match outcome with
            | Succeeded _ ->
                if assetsPresent proj then
                    tracker.Clear proj
                    tracker.RecordFreshSignature(proj, sig_)
                    RecoveredOk
                else
                    let msg, detail = restoreFailureMessage proj outcome
                    FailFast(msg, detail)
            | Failed _
            | TimedOut _ ->
                let msg, detail = restoreFailureMessage proj outcome
                FailFast(msg, detail)

    let sig_ = signatureOf proj

    match probe proj with
    | Fresh when tracker.HasContentDrifted(proj, sig_) ->
        // mtime says fresh, but dep content changed under a preserved mtime —
        // re-restore against the real current deps.
        handleStale sig_
    | Fresh ->
        // Genuinely healthy: forget any prior attempt so a future regression
        // re-arms, and record the content baseline for drift detection.
        tracker.Clear proj
        tracker.RecordFreshSignature(proj, sig_)
        Proceed
    | Stale -> handleStale sig_

/// Per-step restore timeout. A hung `dotnet`/`paket` restore (seen in practice:
/// a `paket restore` wedged ~17 min at 0% CPU) would otherwise block the whole
/// scan indefinitely; bounding each step surfaces it as a `TimedOut` fail-fast
/// instead. Generous enough for a cold restore of a large solution.
let restoreStepTimeout = TimeSpan.FromMinutes 5.0

/// Group names declared in a `paket.lock`. The first/implicit group is always
/// "Main" (it has no `GROUP` header); any additional groups appear as
/// `GROUP <Name>` lines. Used to restore paket sources/git-deps per group, which
/// lets `paket restore` skip its full-repo project-discovery walk. Falls back to
/// just "Main" when the lock is missing/unreadable.
let internal paketGroupsFromLock (lockPath: string) : string list =
    try
        let explicit =
            File.ReadAllLines lockPath
            |> Array.choose (fun line ->
                let t = line.Trim()

                if t.StartsWith("GROUP ", StringComparison.OrdinalIgnoreCase) then
                    Some(t.Substring(6).Trim())
                else
                    None)
            |> Array.filter (fun g -> g <> "")
            |> Array.toList

        "Main" :: explicit
    with _ ->
        [ "Main" ]

/// One step in a project's restore sequence: the `dotnet` sub-command `args` to
/// run (e.g. `restore "<fsproj>"`, `paket restore --group Main`, `tool restore`),
/// a `purpose` label for diagnostics, and the working directory the step runs in.
/// The executable is always `dotnet`, so it is not carried per-step.
type RestoreStep =
    { Purpose: string
      Args: string
      WorkingDir: string }

/// Pure composition of the ordered restore steps for a project — the branchy
/// "which steps, in what order, with what args" decision, separated from process
/// execution so it is unit-testable without shelling out. `productionRestoreRunner`
/// composes this with `runRestoreSteps`.
///
/// Ordering & inclusion (unchanged from the shell-out it was extracted from):
///   1. always `dotnet restore "<fsproj>"` in the project directory;
///   2. when a `paket.dependencies` is in scope, one `dotnet paket restore
///      --group <g>` per group enumerated from the in-scope `paket.lock` (via
///      `paketGroupsFromLock`, falling back to just `Main` when no lock is found).
///      Passing an explicit `--group` makes paket skip its full-repo
///      project-discovery walk (`FindAllProjects`), which otherwise recurses
///      forever through symlink loops such as a Nix `.devenv` profile's macOS-SDK
///      ncurses links (a bare `paket restore` wedged on exactly that). Per-project
///      reference injection — for repos that use `paket.references` — is already
///      handled by the `restore` step above via Paket.Restore.targets'
///      `paket restore --project`.
///   3. when a `.config/dotnet-tools.json` is in scope, a final `dotnet tool
///      restore`. The tools manifest is not a freshness dep input, so it is probed
///      separately from `dependencyFiles` — but a stale-recovery restore should
///      still refresh the tool manifest when one is in scope.
let internal restoreSteps (repoRoot: string) (fsprojPath: string) : RestoreStep list =
    let projDir = Path.GetDirectoryName(fsprojPath: string)
    let depFiles = dependencyFiles fsprojPath repoRoot

    let hasName (name: string) =
        depFiles
        |> List.exists (fun f -> String.Equals(Path.GetFileName f, name, StringComparison.OrdinalIgnoreCase))

    let step purpose args =
        { Purpose = purpose
          Args = args
          WorkingDir = projDir }

    [ yield step "restore" $"restore \"%s{fsprojPath}\""
      if hasName "paket.dependencies" then
          let groups =
              depFiles
              |> List.tryFind (fun f ->
                  String.Equals(Path.GetFileName f, "paket.lock", StringComparison.OrdinalIgnoreCase))
              |> Option.map paketGroupsFromLock
              |> Option.defaultValue [ "Main" ]

          for g in groups do
              yield step "paket-restore" $"paket restore --group %s{g}"
      if (toolsManifest fsprojPath repoRoot).IsSome then
          yield step "tool-restore" "tool restore" ]

/// Execute an ordered list of restore steps. Each step runs `dotnet <args>` in
/// its working directory, bounded by `restoreStepTimeout`. The first non-success
/// short-circuits and is returned; a fully-successful chain returns `Succeeded ""`
/// (the final step's stdout is not used by the caller — it only matches
/// `Succeeded _`). Uses ProcessHelper so stdout/stderr are drained concurrently
/// (no deadlock).
let private runRestoreSteps (steps: RestoreStep list) : ProcessOutcome =
    let rec run remaining =
        match remaining with
        | [] -> Succeeded ""
        | step :: rest ->
            match runProcessWithTimeout "dotnet" step.Args step.WorkingDir [] restoreStepTimeout with
            | Succeeded _ -> run rest
            | other -> other

    run steps

/// Production restore runner. Composes the pure `restoreSteps` plan with
/// `runRestoreSteps`: `dotnet restore` in the project directory, then per-group
/// `dotnet paket restore` when a `paket.dependencies` is in scope, then
/// `dotnet tool restore` when a `.config/dotnet-tools.json` is in scope. The
/// first non-success short-circuits and is returned.
let productionRestoreRunner (repoRoot: string) : RestoreRunner =
    fun fsprojPath -> restoreSteps repoRoot fsprojPath |> runRestoreSteps
