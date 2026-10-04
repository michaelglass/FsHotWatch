/// The words test-prune's `test-scope` reply uses to explain its scope, with their wire
/// tokens.
///
/// ONE definition for the writer (the plugin's reply) and every reader (the CLI's reply
/// parser and the verdict file), so a token cannot be spelled one way where it is written
/// and another where it is read. Each vocabulary is closed: a reader that meets a token
/// not listed here has met a reply from another version, and `tryOfToken` answers `None`
/// so that reader can fail closed.
namespace FsHotWatch.TestPrune

/// Why a run covered every configured test project in full.
///
/// A run can have several of these reasons at once; it reports the FIRST that applies, in
/// declaration order. The order runs from the widenings that force the whole suite
/// regardless of the change to the ones that only add projects to it.
[<RequireQualifiedAccess>]
type FullSuiteCause =
    /// `set-scope full` turned impact filtering off (`confirm`).
    | Requested
    /// A `run-tests` force run with no filter launched every configured project.
    | ForceRun
    /// The pending-verification ledger could not be read, so what is owed is unknown and
    /// no selection can be trusted.
    | LedgerUnreadable
    /// There is no valid full-suite baseline (a cold repository, or `tests.projects` grew),
    /// so the tests a filtered run would skip have nothing to be equivalent to.
    | NoFullSuiteBaseline
    /// A file could not be analysed, an extension refresh failed, or a file's analysis is
    /// missing from the impact index: the selection has a hole, so every project runs.
    | CoarseFallback
    /// No project has a whole-project run under the current project model.
    | EvidenceGap
    /// Nothing forced the whole suite: the projects the change selected, together with
    /// dependency fanout, runtime obligations and quarantined reds, were every project.
    | SelectionReachedEveryProject

module FullSuiteCause =
    /// Every cause, in precedence order.
    let all: FullSuiteCause list =
        [ FullSuiteCause.Requested
          FullSuiteCause.ForceRun
          FullSuiteCause.LedgerUnreadable
          FullSuiteCause.NoFullSuiteBaseline
          FullSuiteCause.CoarseFallback
          FullSuiteCause.EvidenceGap
          FullSuiteCause.SelectionReachedEveryProject ]

    /// The wire token.
    let token (cause: FullSuiteCause) : string =
        match cause with
        | FullSuiteCause.Requested -> "requested"
        | FullSuiteCause.ForceRun -> "force-run"
        | FullSuiteCause.LedgerUnreadable -> "unreadable-ledger"
        | FullSuiteCause.NoFullSuiteBaseline -> "no-full-suite-baseline"
        | FullSuiteCause.CoarseFallback -> "coarse-fallback"
        | FullSuiteCause.EvidenceGap -> "evidence-gap"
        | FullSuiteCause.SelectionReachedEveryProject -> "selection-reached-every-project"

    /// The cause a wire token names; `None` for a token this build does not know.
    let tryOfToken (wire: string) : FullSuiteCause option =
        all |> List.tryFind (fun cause -> token cause = wire)

    /// A clause for a sentence about the run: "... ran in full because <describe>".
    let describe (cause: FullSuiteCause) : string =
        match cause with
        | FullSuiteCause.Requested -> "impact filtering was turned off"
        | FullSuiteCause.ForceRun -> "a force run launched every project"
        | FullSuiteCause.LedgerUnreadable -> "the pending-verification ledger could not be read"
        | FullSuiteCause.NoFullSuiteBaseline -> "there was no valid full-suite baseline"
        | FullSuiteCause.CoarseFallback -> "the impact graph had a hole (an unanalysable or unindexed file)"
        | FullSuiteCause.EvidenceGap -> "no project had a whole-project run under the current project model"
        | FullSuiteCause.SelectionReachedEveryProject -> "the change selected every project"
