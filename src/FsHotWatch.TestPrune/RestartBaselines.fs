/// `tests.restartFromBoxBaseline` (AUTOMATION-1793 step 2, intelligence ADR 0651): what a
/// restarted daemon whose checkout has no full pass of its own may diff from.
namespace FsHotWatch.TestPrune

/// The baselines a restart may diff from. Off (`OwnCheckoutOnly`) unless the configuration
/// opts in.
[<RequireQualifiedAccess>]
type RestartBaselines =
    /// Only a full pass of this checkout's own, with the index sealed at its tree (ADR-071).
    /// The default: exactly the behaviour before the flag existed.
    | OwnCheckoutOnly
    /// When this checkout has no such pass, also the box's retained green baselines: the
    /// newest `TestBaseline.RetainedTrees` trees any checkout passed in full, rebuilt into
    /// this checkout's index from the analysis records their manifests pin (`BoxBaseline`).
    | RetainedBoxBaselines
