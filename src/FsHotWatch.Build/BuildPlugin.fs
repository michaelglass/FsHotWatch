module FsHotWatch.Build.BuildPlugin

open System
open System.IO
open System.Text.Json
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.Lifecycle
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers

/// Why a project's compiled artifact is considered stale after a "successful"
/// build. Detected by post-build verification — either the DLL never appeared
/// on disk, or the DLL's mtime is older than the newest source file.
type StaleReason =
    | DllMissing of dllPath: string
    | DllOlderThanSources of dllTime: DateTime * srcTime: DateTime
    /// A dependency assembly the build is responsible for copying into a consumer
    /// project's output directory matches NO output of the producing project on size
    /// AND mtime — which is precisely MSBuild's own `SkipUnchangedFiles` predicate, so
    /// the next real build will re-emit it and a stored `built N projects` that never
    /// ran is a claim about work still outstanding.
    ///
    /// The class the AUTOMATION-245 wedge is actually made of, and the one the plugin
    /// could not see: the compile checks above ask about a project's OWN assembly, and
    /// a working-copy flip refreshes `src/**` outputs while every test project's COPY of
    /// them is left behind. Reachable from here since the rule moved into core's
    /// `OutputCopyFreshness`.
    | CopyPendingFromOrigin of origin: string * copy: string

type StaleArtifact =
    { Project: string; Reason: StaleReason }

type BuildOutcome =
    | BuildPassed of output: string
    /// Build subprocess returned success but post-build verification found one
    /// or more stale DLLs (MSBuild's incremental cache likely lied). Demoted
    /// here so downstream plugins receive BuildFailed and never run against
    /// stale artifacts. Carries the structured stale-artifact list so cache
    /// replay reproduces the same diagnostic deterministically.
    | BuildArtifactsStale of stale: StaleArtifact list * output: string
    | BuildOutputFailed of outputs: string list

/// The build plugin has no in-flight build of its own (the framework's
/// `RunExclusive "build"` owns single-flighting). `PendingFiles` buffers
/// file changes that arrived before this plugin's `dependsOn` were satisfied.
/// Every source change — test files included — drives a real build; skipping it
/// for test-only changes leaves a stale on-disk test DLL for
/// `dotnet run --no-build` to execute (see ADR-012). `LastBuild` carries the most
/// recent build's lifecycle.
type BuildState =
    {
        LastBuild: Lifecycle<Idle, BuildOutcome option>
        PendingFiles: FileChangeKind list
        SatisfiedDeps: Set<string>
        /// Test/coverage hosts instrument binaries in place. File changes remain
        /// owed, but MSBuild must not rewrite those outputs until every active run
        /// has emitted its matching completion boundary.
        ActiveTestRuns: Set<Guid>
    }

/// Internal message posted from the async build runner back to the plugin's
/// own mailbox. Carries the outcome AND the parsed diagnostic entries so the
/// synchronous Custom handler can apply them to the error ledger and emit
/// BuildCompleted within the framework's per-event capture window — required
/// for the task cache to record errors and downstream emissions on terminal
/// status. The summary is deliberately NOT carried: the handler derives it from
/// the outcome via `buildSummary`, the same pure helper the worker logs with, so
/// the two can never disagree.
type BuildMsg = BuildDone of outcome: BuildOutcome * entries: ErrorEntry list * elapsed: TimeSpan

/// Diagnostic for the "MSBuild exited non-zero but produced no parseable
/// diagnostics" failure mode (typically a bail during evaluation/restore).
/// Surfaces exit code, output size, and any "Time Elapsed" tail to give the
/// next debugging session a starting point.
let formatSilentFailureDiagnostic (exitCode: int) (output: string) : string =
    let elapsed =
        let m =
            System.Text.RegularExpressions.Regex.Match(output, @"Time Elapsed ([\d:.]+)")

        if m.Success then $" elapsed={m.Groups.[1].Value}" else ""

    $"MSBuild aborted before producing diagnostics: exit=%d{exitCode} output=%d{output.Length} bytes%s{elapsed}"

let decideBuildOutcome (success: bool) (output: string) : BuildOutcome * ErrorEntry list =
    let parsed = BuildDiagnostics.parseMSBuildDiagnostics output

    if success then
        BuildPassed output, parsed
    else
        let entries =
            if parsed.IsEmpty then
                [ ErrorEntry.error output ]
            else
                parsed

        BuildOutputFailed [ output ], entries

/// A retryable MSBuild copy warning retains the exact files whose relationship must
/// be checked after the subprocess exits. MSB3026 is not itself a failure: MSBuild
/// commonly emits it before a later retry succeeds.
type CopyRetryWarning =
    { Source: string
      Destination: string
      Project: string option }

let private copyRetryWarningRegex =
    System.Text.RegularExpressions.Regex(
        @"warning\s+MSB3026:\s+Could not copy ""([^""]+)"" to ""([^""]+)""\.(?<tail>[^\r\n]*)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        ||| System.Text.RegularExpressions.RegexOptions.Compiled
    )

let private projectSuffixRegex =
    System.Text.RegularExpressions.Regex(
        @"\[([^\[\]]+\.(?:fs|cs|vb)proj)\]\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        ||| System.Text.RegularExpressions.RegexOptions.Compiled
    )

/// Parse only MSBuild's structured retry warning. Other warnings retain their normal
/// diagnostics policy and never enter the copy-resolution gate.
let parseCopyRetryWarnings (output: string) : CopyRetryWarning list =
    copyRetryWarningRegex.Matches(output)
    |> Seq.cast<System.Text.RegularExpressions.Match>
    |> Seq.map (fun m ->
        let projectMatch = projectSuffixRegex.Match(m.Groups.["tail"].Value)

        { Source = m.Groups.[1].Value
          Destination = m.Groups.[2].Value
          Project =
            if projectMatch.Success then
                Some projectMatch.Groups.[1].Value
            else
                None })
    |> Seq.distinct
    |> Seq.toList

let private resolveCopyRetryPath (repoRoot: string) (warning: CopyRetryWarning) (path: string) : string =
    if Path.IsPathRooted path then
        Path.GetFullPath path
    else
        let baseDirectory =
            match warning.Project with
            | Some project ->
                let projectPath =
                    if Path.IsPathRooted project then
                        project
                    else
                        Path.Combine(repoRoot, project)

                Path.GetDirectoryName(Path.GetFullPath projectPath)
            | None -> repoRoot

        Path.GetFullPath(Path.Combine(baseDirectory, path))

/// After a nominally successful build, distinguish an MSB3026 retry that recovered
/// from one that left its destination absent, unreadable, or holding different bytes.
/// Hashing is injected so the filesystem boundary is deterministic in unit tests.
let verifyCopyRetryWarningsWith
    (hashFile: string -> string)
    (repoRoot: string)
    (outcome: BuildOutcome)
    (entries: ErrorEntry list)
    : BuildOutcome * ErrorEntry list =
    match outcome with
    | BuildPassed output ->
        let unresolved =
            parseCopyRetryWarnings output
            |> List.choose (fun warning ->
                let source = resolveCopyRetryPath repoRoot warning warning.Source
                let destination = resolveCopyRetryPath repoRoot warning warning.Destination
                let sourceHash = hashFile source
                let destinationHash = hashFile destination

                if
                    not (FsHotWatch.ContentHash.isReadable sourceHash)
                    || not (FsHotWatch.ContentHash.isReadable destinationHash)
                    || sourceHash <> destinationHash
                then
                    Some(source, destination)
                else
                    None)

        match unresolved with
        | [] -> outcome, entries
        | warnings ->
            let diagnostic =
                "Build subprocess reported success, but an MSB3026 copy is still unresolved after all retries:\n"
                + (warnings
                   |> List.map (fun (source, destination) -> $"%s{source} -> %s{destination}")
                   |> String.concat "\n")

            BuildOutputFailed [ diagnostic ], entries @ [ ErrorEntry.error diagnostic ]
    | BuildArtifactsStale _
    | BuildOutputFailed _ -> outcome, entries

let private verifyCopyRetryWarnings repoRoot =
    verifyCopyRetryWarningsWith FsHotWatch.ContentHash.ofFile repoRoot

/// stable merkle key for the build cache, independent of the cold-start
/// guard. Pure function — exposed `internal` so unit tests can assert the key
/// responds to its inputs without driving a full plugin lifecycle to flip the
/// guard. Production use goes through the closure in `create` below.
let internal computeBuildCacheKey
    (buildCommand: string)
    (buildArgs: string)
    (dependsOn: string list)
    (inputsHash: string)
    : ContentHash =
    FsHotWatch.TaskCache.merkleCacheKey
        [ "plugin-version", "build-merkle-v1"
          "command", buildCommand
          "args", buildArgs
          "depends-on", String.concat "," (List.sort dependsOn)
          "inputs", inputsHash ]

/// build all-inputs merkle. Hashes the on-disk CONTENT of every source
/// file the project graph knows about, plus every .fsproj — every input, every
/// `Compute`, with no memo.
///
/// Do NOT reintroduce a memo keyed on mtime. `rsync -a`/`cp -p`/`tar -x`/a git
/// checkout restoring an old mtime all change CONTENT while PRESERVING mtime, so a
/// `(path, mtime)` key never moves, the merkle never moves, and the task cache
/// replays a stale `BuildDone` forever. `(path, size, mtime)` is fooled by the same
/// rewrite. See docs/adr-008-mtime-is-not-a-content-oracle.md.
///
/// The cost is real but small: `Compute` runs once per build TRIGGER (not per file
/// event), and SHA-256 over source text is dominated by the `dotnet build` it gates.
type internal BuildInputsHasher(graph: FsHotWatch.ProjectGraph.IProjectGraphReader) =
    // Honest "missing" sentinel for non-existent files; let real IO exceptions
    // (UnauthorizedAccessException, IOException for locked files, etc.) propagate
    // up to decideBuildOutcome instead of folding "read-error" into the merkle.
    let hashFile (path: string) : string option =
        if not (File.Exists path) then
            None
        else
            Some(FsHotWatch.CheckCache.sha256Hex (File.ReadAllText path))

    member _.Compute() : string =
        let sourceFiles = graph.GetAllFiles() |> List.map AbsFilePath.value

        let projectFiles = graph.GetAllProjects() |> List.map AbsProjectPath.value

        // AUTOMATION-303 case 2. MSBuild's IMPLICIT IMPORTS — `Directory.Build.props`,
        // `Directory.Build.targets`, `Directory.Packages.props` — are inputs to the
        // build that no project NAMES, so they appear in neither list above: they are
        // not compile items (so not in `GetAllFiles`) and not projects (so not in
        // `GetAllProjects`). A `<Compile Include=…>` in one of them adds a file to every
        // project beneath it while leaving every project file and every already-known
        // source file byte-identical — the merkle does not move, the task cache replays
        // "built N projects (cached)", and the new file is never compiled. That is case
        // 2's exact shape (a cached build hiding a real compile error), reached through
        // the one door the project-file merkle left open.
        //
        // Found per project by MSBuild's own nearest-ancestor rule
        // (`StructureFiles.implicitImportsFor`), so a file MSBuild would not import
        // cannot invalidate a build it could not have affected.
        let implicitImports =
            projectFiles
            |> List.collect FsHotWatch.StructureFiles.implicitImportsFor
            |> List.distinct

        let allInputs =
            (sourceFiles @ projectFiles @ implicitImports) |> List.distinct |> List.sort

        let sb = System.Text.StringBuilder()

        for path in allInputs do
            let h =
                match hashFile path with
                | Some h -> h
                | None -> "missing" // distinct from any real sha256 hash

            sb.Append(path.Length) |> ignore
            sb.Append(':') |> ignore
            sb.Append(path) |> ignore
            sb.Append('@') |> ignore
            sb.Append(h) |> ignore
            sb.Append('\n') |> ignore

        FsHotWatch.CheckCache.sha256Hex (sb.ToString())

/// Why a project contributed nothing — or only half — to an artifact-freshness pass.
type UnexaminedProject =
    /// The graph could name no build output for this project, so NOTHING about it is
    /// verified. `GetCanonicalDllPath` answers from MSBuild's recorded `TargetPath`, or
    /// failing that from a parsed `<TargetFramework>` — so a project lands here when
    /// discovery reported no `TargetPath` for it (an evaluation that failed) AND its
    /// project file declares no framework of its own, e.g. inheriting one from a
    /// `Directory.Build.props`.
    | NoOutputDerivable of project: string
    /// The output was found, but no source file of this project is on disk to compare
    /// it against, so only its EXISTENCE was checked, never whether it is CURRENT.
    | NoSourceToCompare of project: string

/// What ONE project contributed to an artifact-freshness pass. THE three-way answer,
/// taken once, because two consumers need different halves of it and they may not
/// disagree about where the lines fall.
///
/// `verifyArtifactsFresh` wants the STALE arm — it drives the post-build demotion and the
/// cache-REPLAY gate. `artifactCoverageGap` wants the NOT-EXAMINED arm. Those used to be
/// two hand-written walks over the same graph, each carrying its own copy of the
/// non-obvious rule: a DERIVABLE path whose DLL is missing WAS examined, because that IS
/// the finding (`DllMissing`), so it is not a gap. Two copies of a rule that has to agree
/// is exactly how this gate degrades silently — the stale list goes empty because nothing
/// could be looked at, while the floor that exists to say so has drifted about what
/// "looked at" means.
type internal ArtifactExamination =
    /// The output was located and no source on disk is newer than it. Nothing to report.
    | ExaminedFresh
    /// The output was located and found stale — the finding `verifyArtifactsFresh`
    /// returns, and the reason a cache replay is refused.
    | ExaminedStale of StaleArtifact
    /// Nothing, or only half, could be checked — see `UnexaminedProject`.
    | NotExamined of UnexaminedProject

/// Classify ONE project's build output against its sources.
///
/// mtime IS the right signal HERE, unlike the content-hashed build-input merkle,
/// because the question is strictly temporal: "was the DLL regenerated *after* the
/// newest source?" — i.e. did MSBuild's incremental cache skip relinking an artifact a
/// real edit should have rebuilt. In that failure mode the edit bumped the source mtime,
/// so a DLL older than its source is the tell. There is no "expected DLL content" to hash
/// against, so a content check is not even expressible here; the merkle is the content
/// guard and this is its temporal complement.
/// See docs/adr-008-mtime-is-not-a-content-oracle.md.
///
/// It is right only over AUTHORED sources, which is `GetMaxSourceMtime`'s contract and
/// not a free property of MSBuild's compile-item list: that list includes generated
/// files under `obj/` which every design-time evaluation restamps, and a restamp is not
/// an edit (AUTOMATION-368).
let internal examineArtifact
    (graph: FsHotWatch.ProjectGraph.IProjectGraphReader)
    (proj: AbsProjectPath)
    : ArtifactExamination =
    let stem = Path.GetFileNameWithoutExtension(AbsProjectPath.value proj)

    match graph.GetCanonicalDllPath(proj) with
    | None -> NotExamined(NoOutputDerivable stem)
    | Some dllPath when not (File.Exists dllPath) ->
        ExaminedStale
            { Project = stem
              Reason = DllMissing dllPath }
    | Some dllPath ->
        let dllTime = File.GetLastWriteTimeUtc dllPath

        match graph.GetMaxSourceMtime(proj) with
        | Some srcTime when dllTime < srcTime ->
            ExaminedStale
                { Project = stem
                  Reason = DllOlderThanSources(dllTime, srcTime) }
        | Some _ -> ExaminedFresh
        | None -> NotExamined(NoSourceToCompare stem)

/// Every project in the graph, classified once. THE walk — a caller must never look at
/// the stale projects and the unexamined ones through two separate traversals of a graph
/// (and a filesystem) that can move in between.
let internal examineArtifacts (graph: FsHotWatch.ProjectGraph.IProjectGraphReader) : ArtifactExamination list =
    graph.GetAllProjects() |> List.map (examineArtifact graph)

/// The stale findings out of a classified walk.
let internal staleArtifactsOf (examinations: ArtifactExamination list) : StaleArtifact list =
    examinations
    |> List.choose (function
        | ExaminedStale stale -> Some stale
        | ExaminedFresh
        | NotExamined _ -> None)

/// The gaps out of a classified walk.
let internal unexaminedOf (examinations: ArtifactExamination list) : UnexaminedProject list =
    examinations
    |> List.choose (function
        | NotExamined unexamined -> Some unexamined
        | ExaminedFresh
        | ExaminedStale _ -> None)

/// `artifactCoverageGap`'s sentence, over an ALREADY-classified walk — so the plugin can
/// report the gap from the very list it took its stale projects from. See
/// `artifactCoverageGap` below for what the gap MEANS and why it reports rather than
/// refuses.
let internal describeCoverageGap (examinations: ArtifactExamination list) : string option =
    let unexamined = unexaminedOf examinations

    let noOutput =
        unexamined
        |> List.choose (function
            | NoOutputDerivable p -> Some p
            | NoSourceToCompare _ -> None)

    let noSource =
        unexamined
        |> List.choose (function
            | NoSourceToCompare p -> Some p
            | NoOutputDerivable _ -> None)

    if List.isEmpty examinations then
        // The most complete degradation there is, and the quietest: with no projects the
        // stale list is empty (so the gate vouches for everything) AND the merkle hashes
        // an empty input (so every such repo shares one constant cache key). Nothing
        // downstream can tell that apart from a small, clean tree. Written while
        // measuring this very change with a path filter that excluded every project by
        // accident — the count is the only thing that showed it.
        Some
            "artifact freshness examined NOTHING: the project graph holds no projects at all, so every stale-artifact \
             check passes vacuously and the build merkle hashes an empty input. If this repo has .fsproj files, \
             project discovery is broken."
    elif List.isEmpty unexamined then
        None
    else
        let noOutputNames = String.concat ", " noOutput
        let noSourceNames = String.concat ", " noSource

        let clauses =
            [ if not (List.isEmpty noOutput) then
                  $"the graph names no build output for %s{noOutputNames} (no TargetFramework is registered for \
                    them), so NOTHING about those projects is verified"
              if not (List.isEmpty noSource) then
                  $"%s{noSourceNames} has an output but no source file on disk to compare it against, so only its \
                    existence was checked, never whether it is current" ]

        let examined = List.length examinations - List.length unexamined

        let lead =
            if examined = 0 then
                $"artifact freshness examined 0 of %d{List.length examinations} project(s) — NOT ONE build output \
                  could be located, so every stale-artifact check passes vacuously and a cache replay is gated on \
                  nothing: "
            else
                $"artifact freshness fully examined %d{examined} of %d{List.length examinations} project(s) — this \
                  tree is NOT fully protected against stale build output, on the post-build path or at cache replay: "

        Some(lead + String.concat "; " clauses + ".")

/// THE FLOOR: a freshness pass that examined nothing is not a fresh tree.
///
/// `verifyArtifactsFresh` reports the projects it found STALE, and drives both the
/// post-build demotion and — since AUTOMATION-245 — the cache-REPLAY gate. An empty
/// result is "every artifact is current". It is also, value for value, "no artifact
/// could be examined". The two are indistinguishable to every caller, so a graph that
/// stopped yielding build outputs would switch the whole guard off while every run
/// stayed green: the silent-degradation shape AUTOMATION-198 and AUTOMATION-303 removed
/// elsewhere, and that AUTOMATION-201's preflight names for its own gate.
///
/// The door is not hypothetical, and it is not narrow. It stood wide open for two
/// releases: `GetCanonicalDllPath` answered `None` without a registered TargetFramework,
/// a framework only ever reached the graph through `RegisterFromFsproj`, and nothing in
/// `src/` called it — so every live daemon examined nothing while every test stayed
/// green. Discovery now records MSBuild's `TargetPath`, but the gap still opens for a
/// project whose evaluation FAILED (no `TargetPath` reported) that also centralises
/// `<TargetFramework>` in a `Directory.Build.props` — the exact file class
/// AUTOMATION-303 established is an invisible build input — and for any host that
/// populates the graph without recording an output.
///
/// It REPORTS rather than refuses, for the reason AUTOMATION-201's floor gives: a graph
/// with no derivable outputs is a legitimate configuration, and bypassing the cache on
/// every lookup there would trade one wedge class for the rebuild-every-time regression
/// this ticket's own acceptance forbids. Naming the gap costs nothing and makes a total
/// regression loud.
///
/// `None` means every project in the graph was fully examined — there is nothing to say.
let artifactCoverageGap (graph: FsHotWatch.ProjectGraph.IProjectGraphReader) : string option =
    describeCoverageGap (examineArtifacts graph)

/// AUTOMATION-368 — `artifactGateReddens` decides whether a stale-artifact
/// finding ACTS or is merely reported.
///
/// It exists because recording MSBuild's real `TargetPath` made this gate
/// reachable for the first time. Before that, `GetCanonicalDllPath` returned
/// `None` for every project in a live daemon (the TFM it needs arrives only via
/// `RegisterFromFsproj`, which nothing in `src/` calls), so `verifyArtifactsFresh`
/// returned `[]` unconditionally and both consumers — the post-build demotion and
/// the cache-replay bypass — had never examined an artifact.
///
/// Switching a build-reddening predicate on for the first time, across every
/// consuming repo at once, is not a thing to do in the same change that makes it
/// possible: the failure mode is not "no protection", it is every build going red
/// on a false staleness reading. So the daemon passes `false` and the finding is
/// logged only, while the LOGIC stays exercised by the tests that pass `true`.
///
/// THE REPORT-ONLY WINDOW EARNED ITS KEEP. Read back over ~40 workspaces of the
/// consuming repo (2026-08-18..23), the logs held **2090** stale findings — and
/// **91%** of them fell within 90s of an `MSBuild evaluation` pass in the same
/// daemon log. Cause: `obj/<cfg>/<tfm>/<Project>.AssemblyInfo.fs` is a compile
/// item of every SDK project, every design-time evaluation rewrites it, and
/// project DISCOVERY is a design-time evaluation — so each discovery restamped
/// every project's newest "source" past the DLL the last build had just written.
/// Promoting on that day would have reddened essentially every build in every
/// workspace. `GetMaxSourceMtime` now excludes build output, which is what
/// TestPrune's `ArtifactFreshness` had always done and the source of the
/// BuildPlugin/TestPrune disagreement the promotion criteria asked about.
///
/// STILL `false`, deliberately. The corrected reading has never run against a real
/// repository either, and promoting a reddening predicate in the change that fixes
/// it repeats the mistake this flag was created to avoid. Promote after one
/// observation window in which the corrected detector and `ArtifactFreshness`
/// agree — the log line below is the instrument, and the discovery correlation
/// above is the exact statistic to re-measure.
let createWith
    (artifactGateReddens: bool)
    (command: string)
    (args: string)
    (environment: (string * string) list)
    (graph: FsHotWatch.ProjectGraph.IProjectGraphReader)
    (testProjectNames: string list)
    (buildTemplate: string option)
    (dependsOn: string list)
    (timeoutSec: int option)
    =
    let buildCommand = command
    let buildArgs = args

    /// Force the NEXT build to be real instead of a cache replay (AUTOMATION-224).
    ///
    /// The cache key below is a content merkle over SOURCE files only, so a hit
    /// asserts the OUTPUTS are current on evidence that never looked at them. That
    /// holds right up until `bin/` is changed out from under a tree whose sources
    /// are unchanged — a working-copy flip is the common way. Then the build
    /// replays "built N projects (cached)" without running, TestPrune's freshness
    /// gate correctly finds the output stale and defers ("waiting on build"), and
    /// nothing ever rebuilds: a deadlock that blocked a production deploy 3x.
    ///
    /// Set by the `force-rebuild` command, which `confirm` issues. Consumed by
    /// `cacheKey` on the LOOKUP (a `FileChanged`) and cleared once a build has
    /// actually completed, so the fresh result still gets stored normally.
    let forceRebuild = ref false

    // Cache lookup happens before Update and receives the event but not BuildState.
    // Mirror only the active-run set so a FileChanged observed while a test host owns
    // the output DLLs cannot bypass Update by replaying a cached BuildCompleted.
    // Both reads and writes happen on this plugin's serialized mailbox.
    let activeTestRunsForCache: Set<Guid> ref = ref Set.empty

    /// Canonical DLL paths that a build reported SUCCESS without producing.
    ///
    /// THE FLOOR under the missing-output half of the replay gate below. That gate
    /// refuses to replay a stored pass while a graph project's output is absent —
    /// and on its own that refusal has no termination argument. A project the build
    /// command never actually builds (outside the solution the build verb names, or
    /// carrying a `TargetPath` from an evaluation that produces nothing) is absent
    /// before the build and absent after it, so every lookup would bypass and the
    /// repo would rebuild on every `check`: the inner-loop regression AUTOMATION-245's
    /// own acceptance forbids, arrived at from the opposite direction.
    ///
    /// A build that ran and did not produce the file is proof the CACHE is not what
    /// is wrong, so the file stops being a reason to distrust one. The bypass is
    /// therefore worth at most ONE extra build per unproduced output, ever.
    ///
    /// RECOMPUTED, never accumulated, and only after a build that PASSED: a failed
    /// build proves nothing about which outputs it would produce, and an output that
    /// starts being produced has to be able to leave the set.
    let unproducedOutputs: Set<string> ref = ref Set.empty

    /// Dependency copies that a build RAN and did not settle.
    ///
    /// THE FLOOR under the copy half of the replay gate, and it exists for exactly the
    /// reason `unproducedOutputs` does: a bypass with no termination argument is a
    /// rebuild-every-time regression wearing a correctness fix's clothes.
    ///
    /// It has a second job the other floor does not. `isPending` asks MSBuild's own
    /// skip predicate, so a build normally settles every pending copy — but a copy that
    /// holds DIFFERENT bytes at the SAME size and mtime is one MSBuild will skip for
    /// ever (measured: a plain `dotnet build` leaves it byte-for-byte as it found it).
    /// Without this floor that copy would bypass the cache on every single lookup and
    /// never be repaired by the thing the bypass paid for. With it, the bypass is worth
    /// at most ONE extra build per copy — and `recordUnrepairedCopies` says out loud
    /// what the build could not do and what actually does it.
    ///
    /// RECOMPUTED, never accumulated, and only after a build that PASSED — same reasons.
    let unrepairedCopies: Set<string> ref = ref Set.empty

    let testProjectNameSet = testProjectNames |> Set.ofList

    let buildTimeout =
        match timeoutSec with
        | Some s -> TimeSpan.FromSeconds(float s)
        | None -> System.Threading.Timeout.InfiniteTimeSpan

    // A build command is a SILENT child: `dotnet build -v q` prints nothing until
    // it finishes, and a `sh -c "dotnet build 2> log; cat log"` wrapper (the shape
    // real repos use) buffers everything to the very end. So its output proves
    // nothing about liveness and a launch deadline would false-kill a healthy slow
    // build — `buildTimeout` is the bound.
    let buildBounds = ProcessBounds.silent buildTimeout

    // Path normalization happens once at the SourceChanged → AbsFilePath boundary
    // (callers inject `AbsFilePath.create` per file).
    let isTestFile (file: AbsFilePath) =
        graph.GetProjectsForFile(file)
        |> List.exists (fun proj ->
            testProjectNameSet.Contains(Path.GetFileNameWithoutExtension(AbsProjectPath.value proj)))

    let isTestProject (proj: AbsProjectPath) =
        testProjectNameSet.Contains(Path.GetFileNameWithoutExtension(AbsProjectPath.value proj))

    /// The coverage gap, at most ONCE per plugin instance.
    ///
    /// What it reports is a property of the project GRAPH — which projects declare a
    /// TargetFramework, which have sources on disk — not of a run, so it is the same
    /// sentence on every lookup. Repeating it per dispatched event would bury the one
    /// thing it exists to make loud, and the latch is set BEFORE the walk so the second
    /// caller onwards pays nothing at all.
    let coverageGapReported = ref false

    let reportCoverageGapOnce (examinations: ArtifactExamination list) =
        if not coverageGapReported.Value then
            coverageGapReported.Value <- true

            match describeCoverageGap examinations with
            | Some gap -> warn "build" gap
            | None -> ()

    /// ONE classified walk, with the coverage floor reported off it. Every gate in this
    /// plugin starts here and is handed the RESULT, so no two of them can be looking at
    /// a different filesystem, the stale projects and the coverage gap are `List.choose`d
    /// out of the same classification, and the floor cannot disagree with the gate about
    /// which projects were examined at all. See `examineArtifact` for why mtime is the
    /// right signal here.
    let examineNow () : ArtifactExamination list =
        let examinations = examineArtifacts graph
        reportCoverageGapOnce examinations
        examinations

    /// Whether the copy gate has anything to look at, said out loud ONCE per plugin
    /// instance.
    ///
    /// Not decoration. Two shipped gates in this file examined NOTHING in every live
    /// daemon for two releases and stayed green in every test, because the fixtures
    /// registered projects by a path production does not take. A count is the one
    /// reading that tells those apart from the outside, and it is what the last QA pass
    /// on AUTOMATION-245 had to reconstruct from `grep -c` over a daemon log. Zero pairs
    /// is legitimate (a single-project repo, or nothing built yet) — which is exactly why
    /// it must be reported rather than assumed.
    let copyCoverageReported = ref false

    let reportCopyCoverageOnce (pairs: FsHotWatch.OutputCopyFreshness.CopyPair list) =
        if not copyCoverageReported.Value then
            copyCoverageReported.Value <- true

            let consumers = pairs |> List.map (fun p -> p.Consumer) |> List.distinct

            info
                "build"
                $"artifact copy check: %d{List.length pairs} dependency copies across %d{List.length consumers} \
                  consumer project(s) of %d{List.length (graph.GetAllProjects())} in the graph."

    let allDependencyCopies () =
        let pairs = FsHotWatch.OutputCopyFreshness.dependencyCopies graph
        reportCopyCoverageOnce pairs
        pairs

    /// The dependency copies MSBuild's own incremental copy would still re-emit.
    ///
    /// Cheap by construction — `File.Exists` plus two `stat`s per pair, no file body is
    /// read — so it can sit on the cache-key lookup beside the mtime walk. The CONTENT
    /// question costs orders of magnitude more (157 MB across 37 pairs in one consuming
    /// repo, ~107 ms warm) and answers a question a rebuild cannot act on, so it is
    /// asked once per build in `recordUnrepairedCopies`, never here.
    let pendingCopies () : FsHotWatch.OutputCopyFreshness.CopyPair list =
        allDependencyCopies () |> List.filter FsHotWatch.OutputCopyFreshness.isPending

    let copyFinding (pair: FsHotWatch.OutputCopyFreshness.CopyPair) : StaleArtifact =
        { Project = pair.Consumer
          Reason = CopyPendingFromOrigin(pair.PrimaryOrigin, pair.Copy) }

    let pendingCopyFindings () : StaleArtifact list =
        pendingCopies () |> List.map copyFinding

    /// AUTOMATION-368's instrument: name a finding this mode is not acting on.
    let reportWithoutActing (s: StaleArtifact) =
        // Phrased inline rather than via `formatStaleArtifact`, which is declared
        // below this point.
        let detail =
            match s.Reason with
            | DllMissing path -> $"DLL missing at %s{path}"
            | DllOlderThanSources(dllTime, srcTime) -> sprintf "DLL %O older than newest source %O" dllTime srcTime
            | CopyPendingFromOrigin(origin, copy) -> $"%s{origin} not yet copied to %s{copy}"

        info "build" $"artifact-gate (report-only, AUTOMATION-368): would have reported %s{s.Project}: %s{detail}"

    /// Post-build contract enforcement, over a walk the caller already took. For every
    /// project the graph knows about, compare the canonical DLL's mtime against the max
    /// source mtime. Returns the stale projects so the worker can demote BuildPassed to
    /// BuildArtifactsStale and downstream plugins (TestPrune) receive a BuildFailed
    /// signal instead of running against artifacts MSBuild's incremental cache silently
    /// failed to update.
    ///
    /// This is the REDDENING consumer, and the only one `artifactGateReddens` governs in
    /// full — `replayBlockers` below explains why refusing a replay is a different
    /// question with a different answer.
    let verifyArtifactsFresh (examinations: ArtifactExamination list) : StaleArtifact list =
        let stale = staleArtifactsOf examinations

        // AUTOMATION-368 — REPORT-ONLY, DELIBERATELY.
        //
        // Until now this returned `[]` in every live daemon and neither of its two
        // consumers — the post-build `BuildPassed -> BuildArtifactsStale` demotion
        // and the cache-replay bypass — had ever examined a real artifact. The
        // cause was upstream: `GetCanonicalDllPath` needs a TargetFramework, and a
        // TFM only reached the graph through `RegisterFromFsproj`, which has zero
        // callers in `src/`.
        //
        // Recording MSBuild's real `TargetPath` fixes that — and by fixing it,
        // switches on a predicate that REDDENS BUILDS and has never run against a
        // real repository. The failure mode of getting that wrong is not "no
        // protection": it is every build in every consuming repo going red at once
        // on a false staleness reading.
        //
        // So the finding is LOGGED and discarded. Behaviour is byte-identical to
        // before — `[]`, as it has always effectively been — while the claim
        // becomes observable. Promote to reddening only after these logs and
        // TestPrune's independent `ArtifactFreshness` agree on a real tree over a
        // real working week, which is the comparison this mode exists to enable.
        //
        // That comparison has now been RUN once, and it disagreed: 2090 findings
        // over ~40 workspaces, 91% of them within 90s of a project-discovery pass,
        // because `GetMaxSourceMtime` counted MSBuild's own regenerated
        // `obj/**/AssemblyInfo.fs` as an edit. Fixed there. The window restarts on
        // the corrected reading — see `createWith`'s doc comment.
        if artifactGateReddens || stale.IsEmpty then
            stale
        else
            for s in stale do
                reportWithoutActing s

            []

    /// The findings that may REFUSE A CACHE REPLAY — a strictly different question
    /// from `verifyArtifactsFresh`'s, and the reason the report-only flag does not
    /// govern all of it.
    ///
    /// `artifactGateReddens` exists to stop a predicate turning builds RED across
    /// every consuming repo at once on a reading that has never run for real. Refusing
    /// a replay reddens nothing: it returns `None` from the cache key, which is the
    /// framework's documented "skip the cache, run Update" bypass — the same one
    /// `force-rebuild` uses (AUTOMATION-224). The worst case is one real build, whose
    /// own result then decides the colour. So the flag's jurisdiction is the
    /// post-build DEMOTION, and it is applied here only to the finding whose reading
    /// can actually be wrong:
    ///
    ///   * `DllOlderThanSources` is an MTIME COMPARISON, and mtime comparisons are
    ///     exactly what the report-only window caught being wrong — 2090 findings over
    ///     ~40 workspaces, 91% of them a design-time evaluation restamping
    ///     `obj/**/AssemblyInfo.fs`. It stays behind the flag.
    ///
    ///   * `DllMissing` is not a reading at all. The file the graph names as this
    ///     project's build output does not exist. There is no skew, no clock and no
    ///     generated-file class to get wrong, and no stored "built N projects" can be
    ///     true about an output that is absent. Serving that entry is precisely the
    ///     thing AUTOMATION-245 exists to stop, and it is the highest-frequency
    ///     instance of it: the first `check` in a brand-new `jj workspace add` has
    ///     byte-identical sources to the workspace whose entry it therefore hits, and
    ///     no `bin/` whatsoever — so `built N projects (cached)` gets asserted about
    ///     outputs that have never existed in that checkout, and TestPrune correctly
    ///     refuses to run `--no-build` against them. Every agent that opens a
    ///     per-ticket workspace meets it.
    ///
    ///   * `CopyPendingFromOrigin` is not a reading either — it is MSBuild's OWN
    ///     incremental-copy predicate (`SkipUnchangedFiles`: same size AND same
    ///     mtime), asked of the graph's dependency copies. If it says the copy is
    ///     pending, the next real build re-emits it; if it says the copy is settled,
    ///     no build will touch it again. That is what makes it the one copy question
    ///     a cache gate may ask: refusing over it costs exactly the build that clears
    ///     it. MEASURED both ways — a plain build of the consumer restores a
    ///     left-behind copy and leaves size and mtime equal again, and 37 of 37
    ///     dependency copies in a healthy consuming-repo tree matched on both.
    ///
    ///     The BYTE comparison (`OutputCopyFreshness.verdict`) is deliberately NOT the
    ///     gate. A copy that differs in content while matching on size and mtime is one
    ///     MSBuild skips forever — measured: a plain `dotnet build` leaves it exactly as
    ///     it found it, and only `--no-incremental` (or deleting the destination) re-emits
    ///     it. Bypassing the cache over that class would buy a rebuild that provably
    ///     cannot fix it, on every lookup, for ever: the rebuild-every-time regression
    ///     AUTOMATION-245's own acceptance forbids, reached from the opposite direction.
    ///     Content is asked on the COLD path instead — see `recordUnrepairedCopies`.
    ///
    /// `unproducedOutputs` and `unrepairedCopies` are what keep the two non-reddening
    /// arms terminating — see them for why a bypass is worth at most one extra build.
    let replayBlockers () : StaleArtifact list =
        let blocks (s: StaleArtifact) =
            match s.Reason with
            | DllMissing path -> not (unproducedOutputs.Value.Contains path)
            | DllOlderThanSources _ -> artifactGateReddens
            | CopyPendingFromOrigin(_, copy) -> not (unrepairedCopies.Value.Contains copy)

        let findings = (examineNow () |> staleArtifactsOf) @ pendingCopyFindings ()
        let acting, notActing = findings |> List.partition blocks

        for s in notActing do
            match s.Reason with
            | DllOlderThanSources _ -> reportWithoutActing s
            // Not report-only suppressions — the build itself declined to fix these,
            // which `recordUnproducedOutputs` / `recordUnrepairedCopies` have already
            // said out loud.
            | DllMissing _
            | CopyPendingFromOrigin _ -> ()

        acting

    /// After a build that PASSED: whatever outputs are still absent, it did not make.
    ///
    /// Said out loud the first time each one appears, because "the build reported
    /// success and produced no output for this project" is a finding in its own right
    /// — usually a project in the graph that the build verb's solution does not
    /// contain — and this is the only place anything notices.
    let recordUnproducedOutputs (stale: StaleArtifact list) =
        let missing =
            stale
            |> List.choose (fun s ->
                match s.Reason with
                | DllMissing path -> Some path
                | DllOlderThanSources _
                // A copy has its own floor (`unrepairedCopies`) because "the build
                // declined to settle it" has a second reading this one does not — see
                // `recordUnrepairedCopies`.
                | CopyPendingFromOrigin _ -> None)
            |> Set.ofList

        for path in Set.difference missing unproducedOutputs.Value do
            warn
                "build"
                $"the build reported success but produced no output at %s{path} — the cache is not what is stale \
                  here, so a replay will no longer be refused over it. If that project should be built, it is \
                  missing from what the build command names."

        unproducedOutputs.Value <- missing

    /// After a build that PASSED: whatever dependency copies it left pending, it did not
    /// settle — and THIS is where the byte comparison earns its cost, because a build
    /// that ran and did not settle a copy means two very different things depending on
    /// the bytes, and they need opposite words.
    ///
    ///   * The bytes MATCH an origin. The run will load correct code; the disagreement
    ///     is size-or-timestamp bookkeeping, not staleness. Said quietly, because it is
    ///     the benign half and it fires once per file at most.
    ///
    ///   * The bytes DIFFER. The build reported success while still owing a copy of code
    ///     that has moved on — so `built N projects` was true of the projects the build
    ///     command names and false of this destination. The usual cause is a consumer
    ///     outside the solution the build verb builds, which is the same finding
    ///     `recordUnproducedOutputs` reports for an output rather than a copy. Named per
    ///     file, once, with the remedies measured to work when the build itself will
    ///     not: `dotnet build --no-incremental`, or delete the named copy and build
    ///     again (a missing destination is copied unconditionally, with no size/mtime
    ///     comparison to get wrong).
    ///
    /// Either way the copy stops justifying a bypass — the bypass bought a build, the
    /// build ran, and a second one would do no more. See `unrepairedCopies`.
    let recordUnrepairedCopies () =
        let pending = pendingCopies ()
        let stillPending = pending |> List.map (fun p -> p.Copy) |> Set.ofList

        for pair in pending do
            if not (unrepairedCopies.Value.Contains pair.Copy) then
                match
                    FsHotWatch.OutputCopyFreshness.verdict
                        FsHotWatch.ContentHash.ofFile
                        pair.Copy
                        pair.PrimaryOrigin
                        pair.OtherOrigins
                with
                | FsHotWatch.OutputCopyFreshness.MatchesAnOrigin ->
                    info
                        "build"
                        $"%s{pair.Copy} already holds %s{pair.Producer}'s current bytes but disagrees with it on \
                          size or timestamp — the run loads correct code, so this is bookkeeping rather than \
                          staleness, and no longer a reason to refuse a cache replay."
                | FsHotWatch.OutputCopyFreshness.DiffersFromOrigins origin ->
                    warn
                        "build"
                        $"the build reported success but still owes the copy of %s{origin} to %s{pair.Copy}, which \
                          holds different bytes — so this build does not make that copy, and re-running it will \
                          not either. If %s{pair.Consumer} should be built, it is missing from what the build \
                          command names; failing that, `dotnet build --no-incremental` or deleting %s{pair.Copy} \
                          and building again both re-emit it. A replay will no longer be refused over it."
                | FsHotWatch.OutputCopyFreshness.CopyUnreadable path
                | FsHotWatch.OutputCopyFreshness.OriginUnreadable path ->
                    warn
                        "build"
                        $"the build left %s{pair.Copy} disagreeing with %s{pair.PrimaryOrigin}, and %s{path} could \
                          not be read to say whether the bytes differ. A replay will no longer be refused over it."

        unrepairedCopies.Value <- stillPending

    let depNames = dependsOn |> Set.ofList
    let allDepsSatisfied deps = Set.isSubset depNames deps

    let countBuiltProjects (output: string) =
        BuildDiagnostics.parseDllPaths output |> Map.count

    /// Phrase a single stale-artifact case for human-readable diagnostics.
    /// Worker-side so cache replay reproduces the same message verbatim.
    let formatStaleArtifact (s: StaleArtifact) : string =
        match s.Reason with
        | DllMissing path -> $"%s{s.Project}: DLL missing at %s{path}"
        | DllOlderThanSources(dllTime, srcTime) ->
            sprintf "%s: DLL %O older than newest source %O" s.Project dllTime srcTime
        | CopyPendingFromOrigin(origin, copy) ->
            $"%s{s.Project}: %s{origin} has not been copied to %s{copy} — the build still owes that copy"

    /// The same stale-artifact list phrased as a cache-BYPASS diagnostic.
    ///
    /// Distinct from `staleDiagnostic`, which condemns a build that just RAN. This one
    /// explains why no stored result was served, and it says the recovery out loud:
    /// the rebuild it triggers IS the fix. The wedge this closes cost ~90 minutes of a
    /// night to the belief that `dotnet fshw stop` was required (it is not, and the
    /// on-disk task cache survives a restart anyway — so that folklore was never even
    /// a reliable cure).
    let replayBypassDiagnostic (stale: StaleArtifact list) : string =
        "Build cache bypassed: a stored result may not be replayed over outputs it no\n"
        + "longer describes. Rebuilding now — no daemon restart is needed.\n\n"
        + (stale |> List.map formatStaleArtifact |> String.concat "\n")

    /// Wrap a stale-artifact list in a "MSBuild lied" diagnostic suitable for
    /// the error ledger / BuildFailed payload.
    let staleDiagnostic (stale: StaleArtifact list) : string =
        "Build subprocess reported success but post-build verification\n"
        + "found stale artifacts (MSBuild incremental cache likely lied).\n"
        + "Re-run with `dotnet build --no-incremental` (or delete bin/ and obj/).\n\n"
        + (stale |> List.map formatStaleArtifact |> String.concat "\n")

    /// The one-line human verdict for each outcome. Pure, shared by the async
    /// worker's live log line and the synchronous BuildDone handler's terminal
    /// verdict, so what the log said and what the status carries can never
    /// disagree.
    let buildSummary (outcome: BuildOutcome) (entries: ErrorEntry list) : string =
        match outcome with
        | BuildPassed out ->
            let n = countBuiltProjects out
            if n > 0 then $"built {n} projects" else "build succeeded"
        | BuildArtifactsStale(stale, _) -> $"build failed: %d{stale.Length} stale artifacts"
        | BuildOutputFailed _ ->
            let errCount =
                entries
                |> List.filter (fun e -> e.Severity = DiagnosticSeverity.Error)
                |> List.length

            $"build failed: %d{errCount} errors"

    /// Run from the async build worker. Logging happens here (live UI), but
    /// the *captured* operations (ReportErrors / ClearErrors /
    /// EmitBuildCompleted / the terminal status) are deferred to the
    /// synchronous Custom BuildDone handler so the framework's per-event
    /// capture window records them for the task cache. Returns the completion
    /// message; the framework posts it back via RunExclusive.
    let applyBuildOutcome
        (ctx: PluginCtx<BuildMsg>)
        (outcome: BuildOutcome)
        (entries: ErrorEntry list)
        (elapsed: TimeSpan)
        =
        match outcome with
        | BuildPassed _ -> ctx.Log(buildSummary outcome entries)
        | BuildArtifactsStale(stale, _) ->
            // Per-project detail, not just the count: an intermittent stale-artifact
            // failure otherwise surfaces only as "build failed: 1 stale artifacts",
            // with no way to tell which project.
            ctx.Log(staleDiagnostic stale)
            error "build" (staleDiagnostic stale)
        | BuildOutputFailed _ -> ()

        BuildDone(outcome, entries, elapsed)

    /// Run verifyArtifactsFresh on a BuildPassed outcome and demote to
    /// BuildArtifactsStale if any project's DLL is stale. Other outcomes
    /// pass through. Worker-side: keeps the per-project mtime stat calls off
    /// the synchronous handler's capture window and lets cache replay re-emit
    /// the identical structured stale list.
    let verifyAndDemote (outcome: BuildOutcome) : BuildOutcome =
        match outcome with
        | BuildPassed out ->
            // ONE walk answers both: what this build left stale, and what it declined
            // to produce at all. Taken here rather than at `BuildDone` so the stat
            // calls stay off the synchronous handler's capture window, and because a
            // build that FAILED is not evidence about either question.
            let examinations = examineNow ()
            recordUnproducedOutputs (staleArtifactsOf examinations)

            // The copy floor, on the same "a build that ran is evidence about what it
            // would do" footing. NOT folded into the demotion below: `verifyArtifactsFresh`
            // reddens, and a copy the build declined to settle is TestPrune's preflight to
            // repair (it holds the file pair and can write it) — reddening the build over
            // it would refuse a run the repair would have let through.
            recordUnrepairedCopies ()

            match verifyArtifactsFresh examinations with
            | [] -> outcome
            | stale -> BuildArtifactsStale(stale, out)
        | _ -> outcome

    let startBuild (ctx: PluginCtx<BuildMsg>) (idle: Lifecycle<Idle, BuildOutcome option>) =
        let buildStarted = DateTime.UtcNow
        ctx.Log $"Running: %s{buildCommand} %s{buildArgs}"

        // RunExclusive "build": the framework guarantees only one build runs at a
        // time (and reports Running at the claim). Concurrent
        // FileChanged-while-building triggers land on SlotBusy and are safe to
        // skip — the next FileChanged re-triggers.
        let claim =
            ctx.RunExclusive
                "build"
                (PluginCtxHelpers.withSubtask
                    ctx
                    "build"
                    "dotnet build"
                    (async {
                        try
                            let result = runProcess buildCommand buildArgs ctx.RepoRoot environment buildBounds

                            let (rawOutcome, entries) =
                                decideBuildOutcome (isSucceeded result) (outputOf result)

                            let copyVerifiedOutcome, verifiedEntries =
                                verifyCopyRetryWarnings ctx.RepoRoot rawOutcome entries

                            let outcome = verifyAndDemote copyVerifiedOutcome

                            match outcome, result with
                            | BuildOutputFailed _, TimedOut(after, _, kill) ->
                                // The full diagnostic already rides in `entries` (via
                                // `outputOf`); this is the one-liner, so it gets the short
                                // marker — a build tree we could not kill still holds the
                                // obj/ locks the next build is about to trip over.
                                let summary = $"timed out after %d{int after.TotalSeconds}s%s{renderKillBrief kill}"

                                ctx.Log "Build TIMED OUT"
                                error "build" "Build TIMED OUT"
                                ctx.CompleteWithTimeout summary
                            | BuildOutputFailed _, Failed(exitCode, output) ->
                                ctx.Log "Build FAILED"
                                error "build" "Build FAILED"

                                let parsedCount =
                                    BuildDiagnostics.parseMSBuildDiagnostics (ProcessOutput.text output)
                                    |> List.length

                                if parsedCount = 0 then
                                    // "Build FAILED / 0 diagnostics" is precisely the shape an
                                    // unfinished drain fakes — so this diagnostic renders the
                                    // capture, which NAMES the incomplete read rather than
                                    // letting a silence we never heard read as a silent build.
                                    let detail = formatSilentFailureDiagnostic exitCode (renderOutput output)
                                    ctx.Log detail
                                    error "build" detail
                            | BuildOutputFailed _, _ ->
                                ctx.Log "Build FAILED"
                                error "build" "Build FAILED"
                            | _ -> ()

                            return applyBuildOutcome ctx outcome verifiedEntries (DateTime.UtcNow - buildStarted)
                        with ex ->
                            let crashEntry = ErrorEntry.error ex.Message
                            // ReportErrors / EmitBuildCompleted belong to the synchronous
                            // BuildDone handler, not here — see `applyBuildOutcome`.
                            return
                                BuildDone(
                                    BuildOutputFailed [ ex.Message ],
                                    [ crashEntry ],
                                    DateTime.UtcNow - buildStarted
                                )
                    }))

        match claim with
        | Claimed -> ()
        | SlotBusy ->
            // The FileChanged guard normally catches this earlier; this is the
            // race-free backstop.
            info "build" "Skipping: build already in progress"

        // State carries the prior idle lifecycle. The synchronous BuildDone
        // handler advances Lifecycle.start ▸ complete when the framework posts
        // the completion message back. "is the build running" is owned by
        // ctx.IsRunning "build".
        { LastBuild = idle
          PendingFiles = []
          SatisfiedDeps = Set.empty
          ActiveTestRuns = Set.empty }

    let startTemplateBuild
        (ctx: PluginCtx<BuildMsg>)
        (idle: Lifecycle<Idle, BuildOutcome option>)
        (template: string)
        (files: AbsFilePath list)
        =
        let nonTestFiles = files |> List.filter (fun f -> not (isTestFile f))

        let affected = graph.GetAffectedProjects(nonTestFiles)

        let buildable = affected |> List.filter (fun p -> not (isTestProject p))

        if buildable.IsEmpty then
            startBuild ctx idle
        else
            let buildableSet = buildable |> Set.ofList

            let roots =
                buildable
                |> List.filter (fun proj ->
                    let dependents = graph.GetDependents(proj)
                    dependents |> List.exists (fun d -> buildableSet.Contains(d)) |> not)

            let buildStarted = DateTime.UtcNow

            let claim =
                ctx.RunExclusive
                    "build"
                    (PluginCtxHelpers.withSubtask
                        ctx
                        "build"
                        $"dotnet build ({roots.Length} roots)"
                        (async {
                            try
                                let mutable failures = []
                                let mutable outputs = []

                                for root in roots do
                                    let rootStr = AbsProjectPath.value root
                                    let rendered = template.Replace("{project}", rootStr)
                                    let (cmd, cmdArgs) = splitCommand rendered
                                    ctx.Log $"Running template: %s{cmd} %s{cmdArgs}"

                                    try
                                        let result = runProcess cmd cmdArgs ctx.RepoRoot environment buildBounds
                                        let output = outputOf result
                                        outputs <- output :: outputs

                                        match result with
                                        | Succeeded _ -> ()
                                        | TimedOut(after, _, kill) ->
                                            let summary =
                                                $"timed out after %d{int after.TotalSeconds}s%s{renderKillBrief kill}"

                                            ctx.Log $"Template build TIMED OUT for %s{rootStr}"
                                            error "build" $"Template build TIMED OUT for %s{rootStr}"
                                            ctx.CompleteWithTimeout summary
                                            failures <- output :: failures
                                        | Failed _ ->
                                            ctx.Log $"Template build FAILED for %s{rootStr}"
                                            error "build" $"Template build FAILED for %s{rootStr}"
                                            failures <- output :: failures
                                    with ex ->
                                        ctx.Log $"Template build exception for %s{rootStr}: %s{ex.Message}"
                                        error "build" $"Template build exception for %s{rootStr}: %s{ex.Message}"
                                        failures <- ex.Message :: failures

                                let failedOutputs = failures |> List.rev

                                let (rawOutcome, entries) =
                                    if failures.IsEmpty then
                                        let combinedOutput = outputs |> List.rev |> String.concat "\n"
                                        decideBuildOutcome true combinedOutput
                                    else
                                        let failedText = failedOutputs |> String.concat "\n"
                                        let parsed = BuildDiagnostics.parseMSBuildDiagnostics failedText

                                        let entries =
                                            if parsed.IsEmpty then
                                                failedOutputs |> List.map ErrorEntry.error
                                            else
                                                parsed

                                        BuildOutputFailed failedOutputs, entries

                                let copyVerifiedOutcome, verifiedEntries =
                                    verifyCopyRetryWarnings ctx.RepoRoot rawOutcome entries

                                return
                                    applyBuildOutcome
                                        ctx
                                        (verifyAndDemote copyVerifiedOutcome)
                                        verifiedEntries
                                        (DateTime.UtcNow - buildStarted)
                            with ex ->
                                error "build" $"Unexpected error: %s{ex.Message}"

                                return
                                    BuildDone(
                                        BuildOutputFailed [ ex.Message ],
                                        [ ErrorEntry.error ex.Message ],
                                        DateTime.UtcNow - buildStarted
                                    )
                        }))

            match claim with
            | Claimed -> ()
            | SlotBusy ->
                // Race-free backstop; the FileChanged guard normally catches this.
                info "build" "Skipping: build already in progress"

            { LastBuild = idle
              PendingFiles = []
              SatisfiedDeps = Set.empty
              ActiveTestRuns = Set.empty }

    let handleSourceChanged
        (ctx: PluginCtx<BuildMsg>)
        (state: BuildState)
        (idle: Lifecycle<Idle, BuildOutcome option>)
        (files: AbsFilePath list)
        =
        // A test-file-only change is NOT a build no-op: the changed test project is
        // run by test-prune via `dotnet run --no-build`, which executes the on-disk
        // assembly, and only MSBuild re-emits that assembly — FCS's in-memory
        // `BatchChecked` type-check signal does not. Skipping the build for such a
        // change leaves a stale DLL for `--no-build` to run → false green (ADR-012).
        match buildTemplate with
        | Some template ->
            { (startTemplateBuild ctx idle template files) with
                SatisfiedDeps = state.SatisfiedDeps
                ActiveTestRuns = state.ActiveTestRuns }
        | None ->
            { (startBuild ctx idle) with
                SatisfiedDeps = state.SatisfiedDeps
                ActiveTestRuns = state.ActiveTestRuns }

    let handleProjectChanged
        (ctx: PluginCtx<BuildMsg>)
        (state: BuildState)
        (idle: Lifecycle<Idle, BuildOutcome option>)
        =
        { (startBuild ctx idle) with
            SatisfiedDeps = state.SatisfiedDeps
            ActiveTestRuns = state.ActiveTestRuns }

    let launchPending (ctx: PluginCtx<BuildMsg>) (state: BuildState) =
        if
            ctx.IsRunning "build"
            || not state.ActiveTestRuns.IsEmpty
            || not (allDepsSatisfied state.SatisfiedDeps)
        then
            state
        else
            let hasProjectChange =
                state.PendingFiles
                |> List.exists (function
                    | ProjectChanged _ -> true
                    | _ -> false)

            let sourceFiles =
                state.PendingFiles
                |> List.collect (function
                    | SourceChanged files -> files
                    | _ -> [])
                |> List.map AbsFilePath.create
                |> List.distinct

            match hasProjectChange, sourceFiles with
            | true, _ -> handleProjectChanged ctx state state.LastBuild
            | _, _ :: _ -> handleSourceChanged ctx state state.LastBuild sourceFiles
            | _ -> state

    { Name = PluginName.create "build"
      Init =
        { LastBuild = Lifecycle.create None
          PendingFiles = []
          SatisfiedDeps = Set.empty
          ActiveTestRuns = Set.empty }
      Update =
        fun ctx state event ->
            async {
                match event with
                | TestRunStarted started ->
                    let activeTestRuns = Set.add started.RunId state.ActiveTestRuns
                    activeTestRunsForCache.Value <- activeTestRuns

                    return
                        { state with
                            ActiveTestRuns = activeTestRuns }

                | TestRunCompleted completed ->
                    let activeTestRuns = Set.remove completed.RunId state.ActiveTestRuns
                    activeTestRunsForCache.Value <- activeTestRuns

                    let updated =
                        { state with
                            ActiveTestRuns = activeTestRuns }

                    return launchPending ctx updated

                // --- CommandCompleted: track dependency satisfaction ---
                | CommandCompleted result when depNames.Contains(result.Name) ->
                    match result.Outcome with
                    | FsHotWatch.Events.CommandFailed _ ->
                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                $"dependency failed: %s{result.Name}"
                                $"dependency failed: %s{result.Name}"
                                TimeSpan.Zero
                        )

                        return state
                    | FsHotWatch.Events.CommandSucceeded _ ->
                        let newDeps = Set.add result.Name state.SatisfiedDeps

                        if allDepsSatisfied newDeps then
                            let updatedState = { state with SatisfiedDeps = newDeps }
                            return launchPending ctx updatedState
                        else
                            return { state with SatisfiedDeps = newDeps }

                // Coverage/test hosts instrument output DLLs in place. Preserve
                // every observed input change, but do not let the resulting cache
                // replay miss launch MSBuild into a live host.
                | FileChanged change when not state.ActiveTestRuns.IsEmpty ->
                    info "build" "Deferring file change until the active test host completes"

                    return
                        { state with
                            PendingFiles = state.PendingFiles @ [ change ] }

                // --- FileChanged: buffer if deps not yet satisfied ---
                | FileChanged change when not depNames.IsEmpty && not (allDepsSatisfied state.SatisfiedDeps) ->
                    info "build" "Buffering file change — waiting for dependencies"

                    return
                        { state with
                            PendingFiles = state.PendingFiles @ [ change ] }

                // --- FileChanged: drop while a build is in flight (framework single-flight) ---
                | FileChanged _ when ctx.IsRunning "build" ->
                    info "build" "Skipping: build already in progress"
                    return state

                // --- FileChanged: normal handling (no deps or all satisfied) ---
                | FileChanged(SourceChanged files) ->
                    return handleSourceChanged ctx state state.LastBuild (files |> List.map AbsFilePath.create)
                | FileChanged(ProjectChanged _) -> return handleProjectChanged ctx state state.LastBuild
                | Custom(BuildDone(outcome, entries, elapsed)) ->
                    // A build has now actually run, which is what `force-rebuild`
                    // asked for. Cleared HERE rather than where `cacheKey` consumed
                    // it, so a lookup that never reached a build (a suppressed or
                    // superseded dispatch) cannot silently spend the request and
                    // leave the artifacts stale anyway.
                    forceRebuild.Value <- false

                    // The completion message arrives carrying the pre-build idle
                    // lifecycle; advance it through Running ▸ Completed for
                    // activity-log bookkeeping.
                    let prevIdle = state.LastBuild

                    let idle = Lifecycle.complete (Some outcome) (Lifecycle.start prevIdle)

                    // Apply captured operations within this synchronous handler so
                    // the framework's cache-write window records them; replay of
                    // a cached BuildDone re-fires them via EmittedEvents + Errors.
                    //
                    // Contract: BuildSucceeded means every project's DLL is
                    // up-to-date with its sources. The async worker already demoted
                    // BuildPassed to BuildArtifactsStale where it wasn't, so this
                    // handler only dispatches the three terminal cases — each
                    // carrying the same `buildSummary` line the worker logged, so
                    // log and status can never disagree.
                    match outcome with
                    | BuildPassed _ ->
                        if entries.IsEmpty then
                            ctx.ClearErrors "<build>"
                        else
                            ctx.ReportErrors "<build>" entries

                        ctx.EmitBuildCompleted(BuildSucceeded)

                        PluginCtxHelpers.completeWith ctx (buildSummary outcome entries) elapsed
                    | BuildArtifactsStale(stale, _) ->
                        let detail = staleDiagnostic stale
                        let entry = ErrorEntry.error detail
                        ctx.ReportErrors "<build>" (entry :: entries)
                        ctx.EmitBuildCompleted(BuildFailed [ detail ])

                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                "Build artifact verification failed"
                                (buildSummary outcome entries)
                                elapsed
                        )
                    | BuildOutputFailed outputs ->
                        ctx.ReportErrors "<build>" entries
                        ctx.EmitBuildCompleted(BuildFailed outputs)
                        let errorDetail = outputs |> String.concat "\n" |> truncateOutput 5

                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                $"Build failed: %s{errorDetail}"
                                (buildSummary outcome entries)
                                elapsed
                        )

                    // A manual/forced test can overlap this build. Any changes observed
                    // under that live host remain owed when this older build completes;
                    // keep them until the host boundary permits a subsequent build.
                    let completedState =
                        { state with
                            LastBuild = idle
                            SatisfiedDeps =
                                if state.PendingFiles.IsEmpty then
                                    Set.empty
                                else
                                    state.SatisfiedDeps
                            ActiveTestRuns = state.ActiveTestRuns }

                    return launchPending ctx completedState

                | _ -> return state
            }
      Commands =
        [ // Idempotent and cheap: it sets a flag, it does not build. The next cache
          // LOOKUP misses, so the build runs for real and re-emits the artifacts
          // TestPrune gates on. `confirm` calls it because the merge verb does not
          // get to trust a cache when its job is to be the thing you trust.
          //
          // The literal, not a shared constant: plugins live BELOW the CLI, so
          // `IpcParsing.ForceRebuildCommand` is not visible here. Same split as
          // "set-scope"/"run-tests" in TestPrunePlugin. The CLI-side constant
          // carries the contract doc; a test pins the two spellings together.
          "force-rebuild",
          fun _ctx _state _args ->
              async {
                  forceRebuild.Value <- true
                  return JsonSerializer.Serialize({| status = "ok"; forced = true |})
              }
          "build-status",
          fun _ctx state _args ->
              async {
                  let lastResult = Lifecycle.value state.LastBuild

                  match lastResult with
                  | Some(BuildPassed output) ->
                      return
                          JsonSerializer.Serialize(
                              {| status = "passed"
                                 output = truncateOutput 200 output |}
                          )
                  | Some(BuildArtifactsStale(stale, _)) ->
                      return
                          JsonSerializer.Serialize(
                              {| status = "failed"
                                 output = staleDiagnostic stale |> truncateOutput 200 |}
                          )
                  | Some(BuildOutputFailed outputs) ->
                      return
                          JsonSerializer.Serialize(
                              {| status = "failed"
                                 output = outputs |> String.concat "\n" |> truncateOutput 200 |}
                          )
                  | None -> return JsonSerializer.Serialize({| status = "not run" |})
              } ]
      Subscriptions =
        // Deliberately NOT `BatchChecked`: every source change drives a real
        // MSBuild build, so there is no test-only-skip phase to wait on the FCS
        // cohort signal for. TestPrune keeps its own subscription (AffectedTests).
        Set.ofList (
            [ SubscribeFileChanged; SubscribeTestRunStarted; SubscribeTestRunCompleted ]
            @ (if dependsOn.IsEmpty then
                   []
               else
                   [ SubscribeCommandCompleted ])
        )
      CacheKey =
        // content-merkle key over all build-relevant files in the project graph.
        // FileChanged and Custom BuildDone share the same key so a stored result
        // is found on the next matching FileChanged. The merkle hashes EVERY source
        // file (test files included), so a test-file edit moves the key → cache miss
        // → a real build runs and re-emits the test DLL before it's executed.
        let inputsHasher = lazy BuildInputsHasher(graph)

        let merkleKey () =
            Some(computeBuildCacheKey buildCommand buildArgs dependsOn (inputsHasher.Value.Compute()))

        let cacheKey (event: PluginEvent<BuildMsg>) : ContentHash option =
            match event with
            // THE STORE, and the only event that is one. A `Custom BuildDone` is this
            // plugin's own post — the delivery of a build that HAS run — and the
            // framework never READS the cache on a `Custom` (its dispatch loop nulls the
            // replay key for them), so this arm exists solely to mint the entry the next
            // lookup hits. Both gates below therefore skip it: suppressing the write
            // would leave every recovered build permanently uncacheable, turning two
            // correctness fixes into a standing inner-loop regression.
            | Custom _ -> merkleKey ()

            // Only events that can actually launch a build are reads, and both gates
            // belong to those reads. Test lifecycle events are state notifications;
            // letting them read replays BuildCompleted and creates a test-run loop.
            //
            // These arms used to match `FileChanged` alone, which is only the whole story
            // for a plugin with no `dependsOn`. With one, a `FileChanged` arriving before
            // the dependencies are satisfied is BUFFERED into `PendingFiles` and the
            // build is launched by the `CommandCompleted` that satisfies the last one —
            // so for exactly the repos that use `dependsOn`, the event that starts the
            // build fell through to an ungated `merkleKey()`. `confirm`'s force-rebuild
            // (AUTOMATION-224) and the artifact re-verification below were both bypassed
            // on the only lookup that could have applied them.
            //
            // While `forceRebuild` is set the LOOKUP must miss so a real build runs.
            // `None` is the framework's documented "outputs missing" bypass — skip the
            // cache, run Update.
            | FileChanged _ when not activeTestRunsForCache.Value.IsEmpty -> None
            | CommandCompleted result when depNames.Contains result.Name && not activeTestRunsForCache.Value.IsEmpty ->
                None
            | FileChanged _ when forceRebuild.Value -> None
            | CommandCompleted result when depNames.Contains result.Name && forceRebuild.Value -> None

            // Re-verify the ARTIFACTS at cache-replay time, not only after a real
            // build (AUTOMATION-245).
            //
            // Two correct answers, deadlocked. The key below is a content merkle over
            // SOURCES, so it is structurally blind to what happened to the OUTPUTS —
            // a `bin/` deleted, half-written, produced by another workspace, or a
            // source rewritten to byte-identical content with a NEW mtime all leave it
            // unmoved, and the entry replays "built N projects (cached)" without
            // looking at a single artifact. TestPrune's freshness gate then compares
            // MTIMES, correctly finds the output older than the source, and refuses to
            // run `--no-build` on stale code. Both are right; together they never move,
            // and re-running `check` reproduces it verbatim because nothing in the loop
            // is a function of the previous attempt.
            //
            // `replayBlockers` is the arbiter because outputs are the ground truth for
            // a claim ABOUT outputs: a cache hit may not assert freshness it has never
            // confirmed. It is the same walk the real-build path runs
            // (`verifyAndDemote`), so cache-hit and real-build become genuinely
            // indistinguishable downstream.
            //
            // It is NOT the same MODE, and that is deliberate — see `replayBlockers`.
            // Refusing a replay cannot turn anything red, so the report-only flag that
            // holds back the mtime reading has no jurisdiction over a build output
            // that is simply ABSENT. Without that split this whole gate was dead in
            // every live daemon: production passes `artifactGateReddens = false`, so
            // the arbiter returned `[]` unconditionally and the wedge stayed exactly
            // as reachable as it was before the gate shipped.
            //
            // Cheap on the hot path, and it reads no file CONTENTS: two stats per
            // project (canonical DLL) and per graph source (`GetMaxSourceMtime` walks
            // every source of every project), so ~2 × (projects + sources), plus two
            // more per dependency COPY (37 of them in the consuming repo whose wedges
            // this ticket records). The copy check is stat-only for the same reason the
            // rest of the gate is — the byte comparison is 157 MB / ~107 ms there, and
            // it answers a question a rebuild cannot act on. See `replayBlockers`.
            //
            // MEASURED on this repo's own graph (12 projects / 135 files, min of 50
            // interleaved lookups, apple silicon): the gate alone is 0.40 ms
            // (0.404/0.395/0.411 across three runs) against a merkle that SHA-256s the
            // same tree in 9.7-11.4 ms. So a warm lookup grows by ~4% — and end to end
            // the difference is not even recoverable by subtraction, because the
            // merkle's own run-to-run spread (±1.5 ms) is wider than the whole gate.
            // An earlier note here put the merkle at 30-40 ms; that was overstated by
            // 3-4x, and the gate's 0.46 ms held up.
            //
            // Ordered BEFORE the merkle so a bypass skips that hash entirely: the
            // WEDGE path is now cheaper than the warm path, not dearer.
            | FileChanged _ ->
                match replayBlockers () with
                | [] -> merkleKey ()
                | stale ->
                    info "build" (replayBypassDiagnostic stale)
                    None

            | CommandCompleted result when depNames.Contains result.Name ->
                match result.Outcome with
                | CommandSucceeded _ ->
                    match replayBlockers () with
                    | [] -> merkleKey ()
                    | stale ->
                        info "build" (replayBypassDiagnostic stale)
                        None
                | CommandFailed _ -> None

            // Test lifecycle events exist only to maintain ActiveTestRuns. They must
            // reach Update; replaying a cached BuildCompleted here launches another
            // test run and feeds the same lifecycle events back forever. Irrelevant
            // CommandCompleted events likewise cannot trigger a build.
            | _ -> None

        Some cacheKey
      // There is NO cold-start gate in the framework — a comment here used to claim
      // one ("replay is suppressed until this plugin completes once in-session"), and
      // nothing in `PluginFramework`/`PluginHost` implements it. The task cache is
      // file-backed (`FileTaskCache`), so it also survives `fshw stop`: the FIRST
      // dispatch of a brand-new daemon can and does replay a stored build. The gate
      // above is the only thing standing between a stored verdict and a `bin/` that
      // never earned it.
      Teardown = None }

/// The reddening form. Kept as `create` so existing callers and every test that
/// asserts the AUTOMATION-224/245 behaviour are unchanged — those tests are the
/// only coverage the gate's logic has, and neutering them to ship report-only
/// would leave a future promotion with nothing to stand on.
let create
    (command: string)
    (args: string)
    (environment: (string * string) list)
    (graph: FsHotWatch.ProjectGraph.IProjectGraphReader)
    (testProjectNames: string list)
    (buildTemplate: string option)
    (dependsOn: string list)
    (timeoutSec: int option)
    =
    createWith true command args environment graph testProjectNames buildTemplate dependsOn timeoutSec
