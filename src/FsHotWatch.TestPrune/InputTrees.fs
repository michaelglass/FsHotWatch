/// The input tree a test run is bound to: its identity, its entries, and how a tree moved between launch and completion.
module FsHotWatch.TestPrune.InputTrees

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Xml.Linq
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsSuppression
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Domain
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.RuntimeCoverageSelection

type AffectedTestsState =
    | NotYetAnalyzed
    | Analyzed of AffectedSelection

/// A receipt input tree as read: its identity and the per-file entries hashed into it.
/// The identity is a content address of `Entries`, so a tree carries its own paths
/// wherever it goes — a launch binding can name what moved without finding its entries
/// in any store another reader could evict them from.
type ReceiptTree =
    { Identity: string
      Entries: (string * string) list }

module internal ReceiptInputTree =
    /// A receipt identity, not a new verdict hash scheme. Use the core input walk,
    /// conservatively including config-excluded source files too. Refuse holes and
    /// unreadable bytes rather than letting an unhashable sentinel authorize reuse.
    let readTree repoRoot : ReceiptTree option =
        try
            let walked = TreeHash.files repoRoot []

            let entries =
                walked.Files |> List.map (fun (rel, path) -> rel, ContentHash.ofFile path)

            if
                not (Directory.Exists repoRoot)
                || not walked.Skipped.IsEmpty
                || (entries |> List.exists (snd >> ContentHash.isReadable >> not))
            then
                None
            else
                let absent =
                    walked.AbsentDeclarations
                    |> List.map (fun rel -> VerdictInputs.SentinelPrefix + rel, VerdictInputs.AbsentDeclaration)

                let manifest = List.sortBy fst (entries @ absent)

                Some
                    { Identity = TreeHash.hashEntries manifest
                      Entries = manifest }
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? JsonException -> None

    /// The identity alone, for a comparison that never has to say what moved.
    let read repoRoot =
        readTree repoRoot |> Option.map _.Identity

    /// Both trees read and different: the pair a move is described from.
    let private (|Moved|_|) (launch: ReceiptTree option, current: ReceiptTree option) =
        match launch, current with
        | Some before, Some after when not (String.Equals(before.Identity, after.Identity, StringComparison.Ordinal)) ->
            Some(before, after)
        | _ -> None

    /// Which paths differ between two receipt manifests. Sorted, so a log line is stable.
    type TreeDelta =
        { Changed: string list
          Added: string list
          Removed: string list }

        static member empty =
            { Changed = []
              Added = []
              Removed = [] }

    let delta (before: (string * string) list) (after: (string * string) list) : TreeDelta =
        let was = Map.ofList before
        let now = Map.ofList after

        let pathsWhere predicate source =
            source |> Map.filter predicate |> Map.keys |> List.ofSeq |> List.sort

        { Changed =
            now
            |> pathsWhere (fun path hash -> Map.tryFind path was |> Option.exists ((<>) hash))
          Added = now |> pathsWhere (fun path _ -> not (was.ContainsKey path))
          Removed = was |> pathsWhere (fun path _ -> not (now.ContainsKey path)) }

    /// At most this many paths per kind are named; the count is always the full one.
    let private pathsNamedPerKind = 10

    let private namePaths (paths: string list) : string =
        let named = paths |> List.truncate pathsNamedPerKind |> String.concat ", "
        let left = List.length paths - pathsNamedPerKind
        if left > 0 then $"%s{named} (+%d{left} more)" else named

    let describeDelta (delta: TreeDelta) : string =
        [ "changed", delta.Changed; "added", delta.Added; "removed", delta.Removed ]
        |> List.filter (snd >> List.isEmpty >> not)
        |> List.map (fun (kind, paths) -> $"%s{kind} (%d{List.length paths}): %s{namePaths paths}")
        |> String.concat "; "

    /// The paths that moved between two trees, for a revocation log line. `None` when
    /// there is no move to explain (either side unbound, or equal).
    let movedPaths (launch: ReceiptTree option) (current: ReceiptTree option) : string option =
        match launch, current with
        | Moved(before, after) -> Some(describeDelta (delta before.Entries after.Entries))
        | _ -> None

    /// How far a file's write time may trail the clock read that preceded the write.
    /// Filesystems stamp from a coarse clock, and some store whole or even-numbered
    /// seconds.
    let private writeTimeSlack = TimeSpan.FromSeconds 2.0

    /// Whether the watcher reports an edit to a repo-relative path while a run is in
    /// flight: it reports the files it checks, which are the current model's checkable
    /// files, or (no model) the files it treats as source.
    let watcherObserves (repoRoot: string) (checkable: Set<AbsFilePath> option) (rel: string) : bool =
        match checkable with
        | Some files -> files.Contains(AbsFilePath.create (Path.Combine(repoRoot, rel)))
        | None -> FsHotWatch.Watcher.isRelevantFile rel

    /// Moved paths credited to the test run, split by what the watcher's silence about
    /// them establishes.
    type RunWrites =
        /// The watcher observes every one, so its silence rules out an edit.
        | Observed of paths: string list
        /// The watcher does not observe at least one, so its silence rules out nothing
        /// for that path: an edit to it made while the tests ran looks the same.
        | Unobserved of paths: string list

    /// The moved paths when the test run itself wrote them: every one changed or added
    /// (a removal leaves no write time to attribute), none reported by the watcher while
    /// the run was in flight, and each last written after the test host started. `None`
    /// when any path fails one of those, when there is no move, or when it cannot be named
    /// — every such case stays an ordinary edit made mid-run.
    let writtenByTestRun
        (repoRoot: string)
        (watcherObserves: string -> bool)
        (watcherReported: Set<string> option)
        (testHostStartedAt: DateTime option)
        (launch: ReceiptTree option)
        (current: ReceiptTree option)
        : RunWrites option =
        match (launch, current), watcherReported, testHostStartedAt with
        | Moved(before, after), Some reported, Some hostStarted ->
            let moved = delta before.Entries after.Entries
            let written = List.sort (moved.Changed @ moved.Added)

            let writtenWhileHostRan (rel: string) =
                not (reported.Contains rel)
                && try
                    File.GetLastWriteTimeUtc(Path.Combine(repoRoot, rel))
                    >= hostStarted - writeTimeSlack
                   with
                   | :? IOException
                   | :? UnauthorizedAccessException -> false

            if
                List.isEmpty moved.Removed
                && not (List.isEmpty written)
                && List.forall writtenWhileHostRan written
            then
                if List.forall watcherObserves written then
                    Some(Observed written)
                else
                    Some(Unobserved written)
            else
                None
        | _ -> None

    /// Whether a repo-relative receipt entry is something a BUILD consumed — and so was
    /// fixed in the test binaries before `beforeRun` began. Declarations, project and
    /// solution files, restore output and the root toolchain files always are; a source
    /// file is when the current model compiles it, or (no model) when the watcher would
    /// treat it as source. Everything else a test reads at run time, AFTER `beforeRun`.
    let feedsCompile (repoRoot: string) (checkable: Set<AbsFilePath> option) (rel: string) : bool =
        let name = Path.GetFileName(rel).ToLowerInvariant()
        let ext = Path.GetExtension(rel).ToLowerInvariant()

        rel.StartsWith(VerdictInputs.SentinelPrefix, StringComparison.Ordinal)
        || List.contains ext [ ".fsproj"; ".props"; ".targets"; ".sln"; ".slnx" ]
        || FsHotWatch.Watcher.isProjectAssetsJson rel
        || VerdictInputs.ToolKnownRootFileNames.Contains name
        || name = ".fshw.json"
        || match checkable with
           | Some files -> files.Contains(AbsFilePath.create (Path.Combine(repoRoot, rel)))
           | None -> FsHotWatch.Watcher.isRelevantFile rel

    /// The tree a run's receipt binds to, decided once `beforeRun` has finished: the tree
    /// the TESTS run against, where that is provably the launch tree plus only what the
    /// tests read at run time. A `beforeRun` that generates output under a discovery root
    /// (a bundle, a stylesheet) otherwise revokes every run it precedes, though nothing
    /// the tests were built from moved. A moved COMPILE input keeps the launch binding:
    /// the binaries predate it, so the run is revoked exactly as before. Returns the
    /// binding and, when the tree moved, a line saying which way it went and why.
    let afterBeforeRun
        (feedsCompile: string -> bool)
        (launch: ReceiptTree option)
        (afterSetup: ReceiptTree option)
        : ReceiptTree option * string option =
        match launch, afterSetup with
        | Moved(before, after) ->
            let moved = delta before.Entries after.Entries

            let compileInputs =
                moved.Changed @ moved.Added @ moved.Removed |> List.filter feedsCompile

            if List.isEmpty compileInputs then
                afterSetup,
                Some(
                    "beforeRun moved only paths no build compiles, so the receipt binds to the tree "
                    + $"the tests run against — %s{describeDelta moved}"
                )
            else
                launch,
                Some(
                    "a compile input moved between launch and the end of beforeRun, so the receipt "
                    + "keeps its launch binding and this run cannot earn one — "
                    + $"%d{List.length compileInputs} compile input(s) among them; %s{describeDelta moved}"
                )
        | Some _, None ->
            launch, Some "the input tree could not be read after beforeRun, so the receipt keeps its launch binding"
        | _ -> launch, None

    /// Extensions of files a compilation can read that neither `feedsCompile` nor the
    /// watcher's source filter names: other languages' sources and project files (their
    /// assemblies are references), MSBuild imports (`<project>.user`, `.proj`, shared
    /// `.projitems`), response files, and checked-in assemblies or packages.
    let private otherIndexInputExtensions =
        set
            [ ".cs"
              ".vb"
              ".csproj"
              ".vbproj"
              ".proj"
              ".projitems"
              ".shproj"
              ".user"
              ".rsp"
              ".dll"
              ".exe"
              ".winmd"
              ".nupkg" ]

    /// Whether a repo-relative path of a receipt manifest is an INPUT of the impact index,
    /// so that its moving can make a row stale (AUTOMATION-1682). True for the union of:
    ///
    ///   1. every source the index recorded an analysis of (`indexed`: its `file_keys`);
    ///   2. `feedsCompile`: declaration sentinels, `.fsproj`/`.props`/`.targets`/`.sln`/
    ///      `.slnx`, `project.assets.json`, the tool-known root files (`Directory.Build.*`,
    ///      `global.json`, `nuget.config`, lock files), `.fshw.json`, and the current model's
    ///      compile items (every source the watcher treats as one when no model is published);
    ///   3. `Watcher.isRelevantFile`: every F# source, signature and script and every MSBuild
    ///      project file outside `bin/`/`obj/`, whatever the model says, so a source the model
    ///      compiles but does not check, or does not name yet, still counts;
    ///   4. `otherIndexInputExtensions`.
    ///
    /// Why this cannot be narrower than what an ordinary seal already vouches for: a
    /// `BatchChecked` seals at the WHOLE tree read after folding the check results of the
    /// files the watcher reported, and the watcher reports only (3) plus file-command
    /// patterns, which feed no row. So every seal already binds the index to paths outside
    /// (3) that moved without a re-analysis. (1), (2) and (4) are margin beyond that. The
    /// analysis reads nothing else inside the tree: a row is the file's source and the used
    /// signatures of its project's references (`IndexedAnalysis.identityOf`), and those come
    /// from (1)-(4) or from build output and the package cache, which sit outside the tree and
    /// are pinned by (2). An MSBuild import or a type-provider input under any other name is
    /// outside it, as it is outside every seal.
    let indexInput (feedsCompile: string -> bool) (indexed: Set<string>) (rel: string) : bool =
        indexed.Contains rel
        || feedsCompile rel
        || FsHotWatch.Watcher.isRelevantFile rel
        || otherIndexInputExtensions.Contains(Path.GetExtension(rel).ToLowerInvariant())

    /// Whether an index sealed at `sealedAt`, with no rows written since, describes `current`
    /// too: what moved between them, or why it does not (AUTOMATION-1682). The moved paths are
    /// every manifest entry whose content hash (`ContentHash.ofFile`) differs between the two
    /// receipt manifests, plus every entry only one of them holds. The manifests are the walk
    /// both identities hash (`readTree`: the discovery roots, the configured test projects'
    /// closures, the tool-known root files and the declared inputs, config excludes NOT
    /// applied). The seal carries only when no moved path is an `indexInput`.
    let sealCarriesTo
        (indexInput: string -> bool)
        (sealedAt: ReceiptTree)
        (current: ReceiptTree)
        : Result<TreeDelta, string> =
        let moved = delta sealedAt.Entries current.Entries

        match moved.Changed @ moved.Added @ moved.Removed |> List.filter indexInput with
        | [] -> Ok moved
        | inputs -> Error $"%d{List.length inputs} index input(s) moved since the seal; %s{describeDelta moved}"

    let matches expected current =
        match expected, current with
        | Some before, Some after -> String.Equals(before, after, StringComparison.Ordinal)
        | _ -> false

    /// Why `matches` said no. It says no for THREE situations with three different
    /// remedies, and one message covered all of them — so a reader who saw a
    /// revocation had to establish by separate investigation whether the tree had
    /// actually moved. On one occasion that was an hour of checking `jj status` and
    /// the mtime of every declared verdict input, to conclude it had not.
    ///
    /// The branch is known where the message is produced. Naming it costs nothing.
    type Mismatch =
        /// The launch never bound a tree: `read` returned `None` before the run. A
        /// DEFECT in the input walk — a skipped entry or an unreadable file — and
        /// evidence earned by a fully successful run is discarded because of it.
        /// Remedy: fix the walk.
        | UnboundAtLaunch
        /// The tree cannot be read NOW, so there is nothing to compare against. The
        /// same class of defect as `UnboundAtLaunch`, at the other end of the run,
        /// and worth separating because the two implicate different moments.
        | UnreadableAtCompletion
        /// Both trees were read and they differ. CORRECT behaviour: the tree was
        /// edited while the run was in flight and the receipt must not outlive it.
        /// Remedy: none, or re-run on a settled tree.
        | MovedDuringRun
        /// Both trees were read and they differ, and every moved path is one the test
        /// run wrote (`writtenByTestRun`), each one the watcher observes. A DEFECT in the
        /// tests: they are not hermetic, and a re-run writes again. Remedy: write outside
        /// the repository.
        | TestRunWrote of paths: string list
        /// As `TestRunWrote`, except the watcher does not observe some of the paths, so
        /// its silence cannot tell the test run's write from an edit made while the
        /// tests ran. Most likely the test run; worth a re-run on a settled tree.
        | WrittenWhileTestsRan of paths: string list

    let classifyMismatch expected current (writtenByTestRun: RunWrites option) =
        match expected, current, writtenByTestRun with
        | None, _, _ -> UnboundAtLaunch
        | _, None, _ -> UnreadableAtCompletion
        | Some _, Some _, Some(Observed paths) -> TestRunWrote paths
        | Some _, Some _, Some(Unobserved paths) -> WrittenWhileTestsRan paths
        | Some _, Some _, None -> MovedDuringRun

    /// The revocation reason for a mismatch, naming the arm and its remedy.
    let describeMismatch mismatch =
        match mismatch with
        | UnboundAtLaunch ->
            "the input tree was UNBOUND at launch — the input walk returned nothing to bind to, "
            + "so evidence from this run is discarded through no fault of the tree. This is a defect in the walk"
        | UnreadableAtCompletion ->
            "the input tree could not be READ at completion, so there was nothing to compare the launch "
            + "binding against. This is a defect in the walk, not a change to the tree"
        | MovedDuringRun ->
            "the input tree MOVED between launch and completion — it was edited while the run was in flight"
        | TestRunWrote paths ->
            $"the test run wrote into the repository: %s{namePaths paths} — the watcher reported none of "
            + "these while the run was in flight, and each was written after the test host started. "
            + "The tests are not hermetic, and a re-run writes again: make them write outside the repository"
        | WrittenWhileTestsRan paths ->
            $"written while the tests ran, and no watched edit explains it (likely the test run; the watcher "
            + $"does not observe these paths): %s{namePaths paths} — each was written after the test host "
            + "started. If a re-run on a settled tree writes them again, the tests are not hermetic: "
            + "make them write outside the repository"

/// The `tests.beforeRun` a plugin is configured with, or the explicit statement that it has
/// none. Read once from the configuration, so no run asks an option what it means.
[<RequireQualifiedAccess>]
type internal BeforeRunHook =
    | NotConfigured
    /// Runs the hook under a run id. It returns only when every step passed; a failing
    /// step throws.
    | Configured of run: (Guid -> FsHotWatch.HookStep.Tracker -> unit)

module internal BeforeRunHook =
    let ofOption (hook: (Guid -> FsHotWatch.HookStep.Tracker -> unit) option) : BeforeRunHook =
        match hook with
        | Some run -> BeforeRunHook.Configured run
        | None -> BeforeRunHook.NotConfigured

/// What vouches that `tests.beforeRun` holds for a tree (AUTOMATION-1835, ADR-080): no hook
/// is configured, or the hook ran to completion and left exactly this tree.
///
/// The cases are private. `PassedOn` is built in one place, `BeforeRunWitness.settle`,
/// after the configured hook has returned; nothing else can name it. A receipt carries the
/// witness of the run that earned it, and a zero-affected green carries the witness it
/// stands on (`WitnessedTree`), so a green with nothing run over a tree no passing
/// `beforeRun` saw cannot be built.
[<RequireQualifiedAccess>]
type BeforeRunWitness =
    private
    | NoHookConfigured
    | PassedOn of tree: string

    override this.ToString() =
        match this with
        | NoHookConfigured -> "no beforeRun configured"
        | PassedOn tree -> $"beforeRun passed on tree %s{tree}"

/// A tree a run may bind, with the witness that `tests.beforeRun` holds for it. Built only
/// by `BeforeRunWitness.settle` and `BeforeRunWitness.held`, so its witness always covers
/// its tree.
type internal WitnessedTree =
    private
        { BoundTo: ReceiptTree option
          VouchedBy: BeforeRunWitness }

    member this.Tree = this.BoundTo
    member this.Witness = this.VouchedBy

/// What `tests.beforeRun` left a run.
[<RequireQualifiedAccess>]
type internal Settlement =
    /// The tests run against this tree, and the witness says `beforeRun` holds for it.
    | Settled of WitnessedTree
    /// `beforeRun` moved a compile input, or the tree could not be read after it: the
    /// receipt keeps this launch binding, and nothing vouches for it.
    | Unsettled of launchBinding: ReceiptTree option

module internal Settlement =
    /// The tree a run's receipt binds to.
    let bound (settlement: Settlement) : ReceiptTree option =
        match settlement with
        | Settlement.Settled witnessed -> witnessed.Tree
        | Settlement.Unsettled binding -> binding

    /// The witness for that tree, when there is one.
    let witness (settlement: Settlement) : BeforeRunWitness option =
        match settlement with
        | Settlement.Settled witnessed -> Some witnessed.Witness
        | Settlement.Unsettled _ -> None

module internal BeforeRunWitness =
    /// Run the configured hook and bind the tree it leaves (`ReceiptInputTree.afterBeforeRun`).
    /// The only place a `PassedOn` witness is built: after the hook returned, over the tree
    /// read once it had, and only when that is the tree the tests run against. A failing
    /// hook throws out of here, so it yields no witness. With no hook configured, nothing
    /// runs and the launch tree is settled as it is.
    let settle
        (hook: BeforeRunHook)
        (runId: Guid)
        (track: FsHotWatch.HookStep.Tracker)
        (feedsCompile: unit -> string -> bool)
        (launchTree: ReceiptTree option)
        (readTree: unit -> ReceiptTree option)
        : Settlement =
        match hook with
        | BeforeRunHook.NotConfigured ->
            Settlement.Settled
                { BoundTo = launchTree
                  VouchedBy = BeforeRunWitness.NoHookConfigured }
        | BeforeRunHook.Configured run ->
            Logging.info "test-prune" "Running beforeRun setup..."
            run runId track
            Logging.info "test-prune" "beforeRun complete"

            let afterSetup = readTree ()

            let binding, why =
                ReceiptInputTree.afterBeforeRun (feedsCompile ()) launchTree afterSetup

            why |> Option.iter (Logging.info "test-prune")

            match binding, afterSetup with
            | Some bound, Some after when String.Equals(bound.Identity, after.Identity, StringComparison.Ordinal) ->
                Settlement.Settled
                    { BoundTo = Some after
                      VouchedBy = BeforeRunWitness.PassedOn after.Identity }
            | _ -> Settlement.Unsettled binding

    /// The witness a zero-affected skip over `tree` already holds, without running anything:
    /// any tree when no hook is configured; with one, the witness of the receipt the skip
    /// stands on, when that receipt's run passed `beforeRun` on this very tree.
    let held (hook: BeforeRunHook) (receiptWitness: BeforeRunWitness option) (tree: ReceiptTree option) =
        match hook, receiptWitness with
        | BeforeRunHook.NotConfigured, _ ->
            Some
                { BoundTo = tree
                  VouchedBy = BeforeRunWitness.NoHookConfigured }
        | BeforeRunHook.Configured _, Some(BeforeRunWitness.PassedOn seen as witness) when
            ReceiptInputTree.matches (Some seen) (tree |> Option.map _.Identity)
            ->
            Some { BoundTo = tree; VouchedBy = witness }
        | BeforeRunHook.Configured _,
          (Some BeforeRunWitness.NoHookConfigured | Some(BeforeRunWitness.PassedOn _) | None) -> None

    /// Whether the witness vouches for the tree with this identity.
    let covers (tree: string option) (witness: BeforeRunWitness) : bool =
        match witness with
        | BeforeRunWitness.NoHookConfigured -> true
        | BeforeRunWitness.PassedOn seen -> ReceiptInputTree.matches (Some seen) tree
