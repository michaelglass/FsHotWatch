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
open System.Text.RegularExpressions
open System.Xml.Linq
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
/// This is an mtime-based fast path, not a content oracle; both of its blind spots
/// are closed in `evaluateProject` by the content-hashed `depRelevantSignature`.
/// See docs/adr-008-mtime-is-not-a-content-oracle.md.
let compareFreshness (assetsMtime: DateTime option) (depFileMtimes: DateTime list) : Freshness =
    match assetsMtime with
    | None -> Stale
    | Some assets ->
        if depFileMtimes |> List.exists (fun m -> m > assets) then
            Stale
        else
            Fresh

/// Names of dependency-declaring files that live alongside / above a project.
/// `*.fsproj` is handled separately (it is the project file itself).
///
/// Sourced from `VerdictInputs`, not restated here. "Is the restore stale?" and
/// "can changing this file change what a check concludes?" are two questions with
/// one answer, and two copies of the list is how they come to disagree — with the
/// disagreement invisible, because each copy looks complete on its own.
let private ancestorDepFileNames = VerdictInputs.DependencyFileNames

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

/// The ancestor-scoped dep files that exist for `fsprojPath` (nearest match per
/// name, so the same logical file is not counted from multiple levels), WITHOUT
/// the project's own `.fsproj`. The walk runs from the project directory up to and
/// including `repoRoot` (MSBuild import + paket semantics).
///
/// `.config/dotnet-tools.json` is deliberately excluded — see `toolsManifest`.
let private ancestorDepFiles (fsprojPath: string) (repoRoot: string) : string list =
    let dirs = ancestorDirs fsprojPath repoRoot

    ancestorDepFileNames
    |> List.choose (fun name -> dirs |> List.map (fun d -> Path.Combine(d, name)) |> List.tryFind File.Exists)

let dependencyFiles (fsprojPath: string) (repoRoot: string) : string list =
    let projFile = if File.Exists fsprojPath then [ fsprojPath ] else []

    projFile @ ancestorDepFiles fsprojPath repoRoot

/// The nearest `.config/dotnet-tools.json` governing `fsprojPath`, if any.
/// Used ONLY by the restore runner to decide whether to run `dotnet tool restore`.
/// It is absent from `dependencyFiles` because it never participates in a
/// project's package graph, so counting it would make every project look stale on
/// any dotnet-tool version bump.
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

/// fsproj element local-names that DECLARE the dependency graph — a change to any can
/// alter what `dotnet restore` produces in `project.assets.json`. `Import`/`Sdk` pull
/// in external targets/props that can inject references; `TargetFramework(s)` selects
/// the framework the graph is resolved for. Source-item names (`Compile`/`Content`/
/// `None`/`EmbeddedResource`) are deliberately ABSENT: they do not participate in the
/// package graph, so a compile-item-only edit must not perturb the signature.
let private depRelevantElementNames =
    set
        [ "PackageReference"
          "ProjectReference"
          "PackageVersion"
          "PackageDownload"
          "FrameworkReference"
          "Import"
          "Sdk"
          "TargetFramework"
          "TargetFrameworks" ]

/// Canonicalize an XML element to a whitespace- and order-insensitive string:
/// local-name, attributes sorted `name=value`, child elements canonicalized and
/// sorted, own (non-child) text collapsed. Namespaces are ignored (LocalName) so an
/// old-style MSBuild-namespaced fsproj canonicalizes the same as an SDK-style one, and
/// order-insensitivity means MOVING a `<PackageReference>` does not perturb the
/// signature — only a genuine content change does.
let rec private canonicalizeElement (el: XElement) : string =
    let attrs =
        el.Attributes()
        |> Seq.map (fun a -> $"%s{a.Name.LocalName}=%s{a.Value}")
        |> Seq.sort
        |> String.concat " "

    let children =
        el.Elements() |> Seq.map canonicalizeElement |> Seq.sort |> String.concat ";"

    let ownText =
        el.Nodes()
        |> Seq.choose (fun n ->
            match n with
            | :? XText as t -> Some t.Value
            | _ -> None)
        |> String.concat ""
        |> fun s -> Regex.Replace(s, @"\s+", " ").Trim()

    $"%s{el.Name.LocalName}(%s{attrs}){{%s{children}}}[%s{ownText}]"

/// The dep-relevant digest of a single `.fsproj`: the canonicalized dep-declaring
/// elements (see `depRelevantElementNames`) plus the root `<Project Sdk="...">`
/// attribute, sorted for order-independence. Source-item elements are NOT collected, so
/// a compile-item-only edit yields the SAME digest. A parse/read failure folds a
/// content-derived sentinel (never throws) so a later fix still moves the digest
/// forward. Conditional references (inside `<Choose>`/`<When>` or with a `Condition=`
/// attribute) ARE captured, condition text and all.
let private depRelevantFsprojDigest (fsprojPath: string) : string =
    // Read once and parse from the string so the malformed-parse fallback can reuse
    // the same content for its sentinel hash instead of re-reading.
    match
        (try
            Some(File.ReadAllText fsprojPath)
         with _ ->
             None)
    with
    | None -> "fsproj-unparseable@unreadable"
    | Some content ->
        try
            let root = (XDocument.Parse content).Root

            let sdkAttr =
                match root.Attribute(XName.Get "Sdk") with
                | null -> []
                | a -> [ $"Project@Sdk=%s{a.Value}" ]

            let elems =
                root.Descendants()
                |> Seq.filter (fun el -> depRelevantElementNames.Contains el.Name.LocalName)
                |> Seq.map canonicalizeElement
                |> Seq.sort
                |> List.ofSeq

            String.concat "\n" (sdkAttr @ elems)
        with _ ->
            // Malformed but readable: key the sentinel on the content bytes so an
            // unparseable→parseable transition (a content change) still moves the
            // signature. Matches the never-throw contract.
            $"fsproj-unparseable@%s{FsHotWatch.CheckCache.sha256Hex content}"

/// Content signature over ONLY a project's dependency-declaring inputs: the
/// fsproj's dep-relevant element subset (`depRelevantFsprojDigest`) plus the full
/// on-disk bytes of every ancestor dep file (`Directory.Packages.props`,
/// `Directory.Build.props`, `paket.lock`, `paket.dependencies`). Two roles:
///
///   1. Debounce + drift baseline — an unchanged dep state keeps the same signature
///      (no re-restore loop), any package-graph change moves it forward and re-arms
///      recovery. Hashes CONTENT, not mtime, because `rsync -a` / `cp -p` / `git
///      checkout` restore an old mtime after a content rewrite (matching
///      BuildInputsHasher and CheckCache.TimestampCacheKeyProvider).
///   2. False-positive suppression — it EXCLUDES fsproj source items, so a
///      compile-item-only fsproj edit (which bumps the mtime → the probe reads
///      Stale) leaves the signature unchanged.
///
/// See docs/adr-008-mtime-is-not-a-content-oracle.md.
let internal depRelevantSignature (repoRoot: string) (fsprojPath: string) : string =
    let fsprojPart = depRelevantFsprojDigest fsprojPath

    let ancestorParts =
        ancestorDepFiles fsprojPath repoRoot
        |> List.sort
        |> List.map (fun path ->
            let hash =
                try
                    FsHotWatch.CheckCache.sha256Hex (File.ReadAllText path)
                with _ ->
                    // Vanished between enumeration and read, or transiently locked:
                    // a distinct sentinel keeps the state observably different.
                    "unreadable"

            $"%s{path}@%s{hash}")

    FsHotWatch.CheckCache.sha256Hex (String.concat "\n" ($"fsproj:%s{fsprojPart}" :: ancestorParts))

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
    /// was observed Fresh / recovered at. A first sighting is NOT drift (nothing to
    /// compare against yet). This is the escape hatch for the mtime probe, which a
    /// preserved-mtime dep rewrite can fool into reporting Fresh.
    member _.HasContentDrifted(proj: string, sig_: string) : bool =
        match freshSignatures.TryGetValue proj with
        | true, prev -> prev <> sig_
        | false, _ -> false

    /// Inverse of `HasContentDrifted`: the signature EQUALS the last fresh/recovered
    /// baseline (a first sighting is not a match). Used to suppress a phantom `Stale`
    /// mtime verdict whose dep-relevant content is unchanged — a compile-item-only
    /// fsproj edit bumped the mtime without touching the package graph.
    ///
    /// Speaks about DECLARED DEPS ONLY, never about the restore output. Nothing in the
    /// signature comes from `obj/`, so a match says the inputs are unchanged and says
    /// nothing at all about whether the assets file still exists. Callers must check
    /// presence themselves — see `evaluateProject`.
    member _.MatchesFreshBaseline(proj: string, sig_: string) : bool =
        match freshSignatures.TryGetValue proj with
        | true, prev -> prev = sig_
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
        // `renderOutput`, not the raw capture: a drain that could not finish must say
        // so, or a human debugging this reads a short tail as the whole output.
        let tail = StringHelpers.truncateOutput 10 (renderOutput out)

        $"deps: project.assets.json stale for %s{projName} and 'dotnet restore' failed (exit %d{code}). Fix deps manually, then re-run.",
        tail
    | TimedOut(after, tail, kill) ->
        // `renderKill` too: a `dotnet restore` tree we failed to kill still holds the
        // NuGet locks the reader is about to fight.
        $"deps: project.assets.json stale for %s{projName} and 'dotnet restore' timed out after %d{int after.TotalSeconds}s. Fix deps manually, then re-run.",
        renderOutput tail + renderKill kill
    | Succeeded _ ->
        // Restore reported success but assets are still absent — a different
        // failure mode (e.g. restore didn't regenerate assets for this project).
        $"deps: project.assets.json still missing for %s{projName} after 'dotnet restore' succeeded. Fix deps manually, then re-run.",
        ""

/// Pure gate orchestration over an injected freshness probe + restore runner +
/// debounce tracker. Attempts recovery at most once per distinct stale state.
///
/// `probe` returns the current freshness; `signatureOf` yields the dep-relevant
/// content signature used for debounce, drift detection AND false-positive
/// suppression (production injects `depRelevantSignature`); `runner` performs the
/// restore; `assetsPresent` reports whether `obj/project.assets.json` exists.
///
/// Post-restore policy: a successful restore means the package graph is consistent,
/// so we proceed without re-running the mtime probe. `dotnet restore` is a no-op
/// (exit 0, assets untouched) when packages are already up-to-date, so re-probing
/// would report stale whenever the trigger was a benign `.fsproj` source-list edit.
/// The only post-success failure is assets still missing entirely.
///
/// The mtime `probe` has two blind spots, and the content signature closes both
/// symmetrically: a `Fresh` verdict whose signature DRIFTED is re-restored, and a
/// `Stale` verdict whose signature MATCHES the last fresh/recovered baseline AND
/// still has assets on disk is proceeded on. A first sighting has no baseline, so a
/// genuinely-cold Stale still restores. See
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
    | Stale when assetsPresent proj && tracker.MatchesFreshBaseline(proj, sig_) ->
        // mtime says stale but the dep content is unchanged from the last baseline:
        // a compile-item-only fsproj edit. The existing assets are still valid.
        //
        // `assetsPresent` FIRST, and it is not redundant. `Stale` is two different
        // facts wearing one label — "the assets are older than a dep file" and "there
        // are no assets at all" — and the baseline can only vouch for the first. The
        // signature is derived from the `.fsproj` and the ancestor dep files, none of
        // which a `rm -rf obj/` touches, so a cleaned workspace arrives here with a
        // matching baseline and would be certified fresh with NO restore output on
        // disk at all. FCS would then type-check against an empty reference set and
        // produce exactly the phantom "namespace/type not found" storm this module
        // exists to prevent (AUTOMATION-528).
        tracker.Clear proj
        tracker.RecordFreshSignature(proj, sig_)
        Proceed
    | Stale -> handleStale sig_

/// Per-step restore timeout. A hung `dotnet`/`paket` restore would otherwise block the
/// whole scan; bounding each step surfaces it as a `TimedOut` fail-fast instead.
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
/// Ordering & inclusion:
///   1. always `dotnet restore "<fsproj>"` in the project directory;
///   2. when a `paket.dependencies` is in scope, one `dotnet paket restore
///      --group <g>` per group enumerated from the in-scope `paket.lock` (via
///      `paketGroupsFromLock`, falling back to just `Main` when no lock is found).
///      An explicit `--group` makes paket skip its full-repo project-discovery walk
///      (`FindAllProjects`), which otherwise recurses forever through symlink loops
///      such as a Nix `.devenv` profile's macOS-SDK ncurses links. Per-project
///      reference injection (for `paket.references` repos) is already handled by the
///      `restore` step above via Paket.Restore.targets' `paket restore --project`.
///   3. when a `.config/dotnet-tools.json` is in scope, a final `dotnet tool
///      restore`. It is not a freshness input, but a stale-recovery restore should
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
/// (callers only match `Succeeded _`). Uses ProcessHelper so stdout/stderr are
/// drained concurrently — otherwise a full pipe buffer deadlocks the child.
let private runRestoreSteps (steps: RestoreStep list) : ProcessOutcome =
    let rec run remaining =
        match remaining with
        // No steps left: `Drained ""` because no process ran, so the empty output was
        // observed rather than unread.
        | [] -> Succeeded(ProcessOutput.Drained "")
        | step :: rest ->
            // `dotnet restore --verbosity quiet` prints nothing on success, so a
            // launch deadline could not tell a healthy slow restore from a wedged
            // one — `restoreStepTimeout` is the bound (see ProcessBounds.silent).
            match runProcess "dotnet" step.Args step.WorkingDir [] (ProcessBounds.silent restoreStepTimeout) with
            | Succeeded _ -> run rest
            | other -> other

    run steps

/// Production restore runner: composes the pure `restoreSteps` plan with
/// `runRestoreSteps`.
let productionRestoreRunner (repoRoot: string) : RestoreRunner =
    fun fsprojPath -> restoreSteps repoRoot fsprojPath |> runRestoreSteps
