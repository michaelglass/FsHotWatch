/// AUTOMATION-1587 step 2c: two decisions that read `SealedTree` must still need a run
/// completed in THIS session, now that `SealedTree` is read on every seal (step 2).
///
///   * The covered boot-scan discharge sheds a BootScan cohort's debt only when the last
///     run of this session passed the very tree it sealed (`CoveredFullSuite`, which only a
///     completion here sets). A restarted daemon holds the previous session's evidence on
///     disk, never this; it must launch.
///   * The symbol-free-edit narrowing needs BOTH a run completed here (`hasCachedResults`)
///     and the manifest of the tree its receipt is bound to. Today the manifest is held only
///     from a completion here, so either guard alone would do; 1587 step 6 makes a manifest
///     come from a baseline RECORD, and then only `hasCachedResults` stands between a cold
///     daemon and a narrowing that rests on no run. Each guard is pinned on its own.
module FsHotWatch.Tests.TestPruneSessionGuardTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Database
open FsHotWatch.Events
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open FsHotWatch.Tests.TestPruneModelChangeTests

let private write (f: Fixture) (rel: string) (text: string) =
    let path = Path.Combine(f.Ctx.RepoRoot, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

/// The fixture over a tree holding `src/Lib.fs` (whose `Lib.f` only `ProjATests` covers),
/// with a model that compiles it and publishes identities for both projects.
let private setUp () =
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    write f "src/Lib.fs" "module Lib\n\n// original\nlet f x = x\n"
    let lib = AbsFilePath.create (Path.Combine(f.Ctx.RepoRoot, "src", "Lib.fs"))

    { f with
        Ctx =
            { f.Ctx with
                ProjectGraph =
                    { f.Ctx.ProjectGraph with
                        ObserveCheckableFiles = fun () -> Some(f.Generation.Value, Set.singleton lib) } } }

/// Both projects passed in full over the tree as it is on disk: a session's own green.
let private fullGreen (f: Fixture) =
    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L
            InputTree = ReceiptInputTree.readTree f.Ctx.RepoRoot }

    let verified = update f f.Handler.Init (finishedGreen full)
    Assert.True(verified.LastResults.IsSome, "positive control: the run completed in this session")
    Assert.True(verified.CoveredFullSuite.IsSome, "positive control: the full run covered its tree")
    verified

let private owingLibF (state: TestPruneState) =
    { state with
        ChangedSymbols = [ "Lib.f" ]
        Debt =
            { state.Debt with
                PendingQueue = Set.singleton "Lib.f" } }

let private bootScan: BatchChecked =
    { Trigger = BootScan
      Files = []
      Generation = 1L
      ModelGeneration = Some 1L
      Retained = None
      StartedAt = DateTime.UtcNow
      CompletedAt = DateTime.UtcNow }

[<Fact(Timeout = 30000)>]
let ``a boot scan over the tree this session's full run passed is discharged, and a restarted daemon's is not`` () =
    let f = setUp ()
    let verified = fullGreen f

    // Positive control: in the session whose run covered this tree, the cohort's debt is
    // covered by that run and shed without a launch.
    let covered = update f (owingLibF verified) (BatchChecked bootScan)
    test <@ Set.isEmpty covered.Debt.PendingQueue @>

    // The same debt and the same tree after a restart: the previous session's evidence is
    // on disk, but no completion of THIS session set `CoveredFullSuite`, so nothing may be
    // shed on its strength.
    publish f f.Handler.Init verified
    let next = restarted f
    test <@ next.Handler.Init.CoveredFullSuite.IsNone @>
    let afterRestart = update next (owingLibF next.Handler.Init) (BatchChecked bootScan)
    test <@ afterRestart.Debt.PendingQueue = Set.singleton "Lib.f" @>

/// What a symbol-free edit's launch selected for ProjA, after a comment-only edit to
/// `src/Lib.fs` sealed at the edited tree.
let private launchAfterCommentEdit (f: Fixture) (state: TestPruneState) =
    write f "src/Lib.fs" "module Lib\n\n// edited\nlet f x = x\n"

    let sealedAtEdit =
        { state with
            SealedTree = ReceiptInputTree.readTree f.Ctx.RepoRoot }

    let _, launch = buildAndLaunch f sealedAtEdit
    Map.tryFind "ProjA" launch.Selection

[<Fact(Timeout = 30000)>]
let ``a symbol-free edit narrows only after a run of this session over the receipt's own manifest`` () =
    // Positive control: this session's run, its receipt and the manifest of its tree are all
    // held, so a comment-only edit runs only the class covering the edited file.
    let f = setUp ()
    test <@ launchAfterCommentEdit f (fullGreen f) = Some(ProjectClasses(Set.singleton "ProjATests")) @>

[<Fact(Timeout = 30000)>]
let ``a symbol-free edit with a manifest from no session run never narrows (1587 step 6 must change this deliberately)``
    ()
    =
    // The state 1587 step 6 makes reachable: the evidence tree's manifest is held, but no
    // run has completed in this session. `hasCachedResults` is all that refuses it here.
    let f = setUp ()
    let verified = fullGreen f

    let noSessionRun = { verified with LastResults = None }
    test <@ (TestRunInputs.ofState noSessionRun).EvidenceManifest.IsSome @>
    test <@ launchAfterCommentEdit f noSessionRun = Some ProjectInFull @>

[<Fact(Timeout = 30000)>]
let ``a symbol-free edit with a manifest of another tree than the receipt's never narrows`` () =
    // A run of this session completed, but the manifest held is not the receipt's tree: the
    // narrowing could only name what moved against the wrong tree.
    let f = setUp ()
    let verified = fullGreen f

    let stale =
        match verified.Receipt with
        | HeldReceipt.Standing(receipt, Some manifest) ->
            let entries =
                manifest.Entries
                |> List.map (fun (path, hash) -> path, (if path = "src/Lib.fs" then "sha256:stale" else hash))

            { verified with
                Receipt =
                    HeldReceipt.Standing(
                        receipt,
                        Some
                            { Identity = "sha256:another-tree"
                              Entries = entries }
                    ) }
        | other -> failwith $"positive control: the run left a standing receipt with its manifest, got %A{other}"

    test <@ stale.LastResults.IsSome @>
    test <@ launchAfterCommentEdit f stale = Some ProjectInFull @>
    // Why: the receipt's inputs hold no manifest that is not its own tree's.
    test <@ (TestRunInputs.ofState stale).EvidenceManifest.IsNone @>
