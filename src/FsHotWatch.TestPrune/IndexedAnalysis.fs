/// The one rule for TestPrune's `FileChecked` task cache.
///
/// SOURCE OF TRUTH: the impact index (`test-impact.db`). Each flush writes, in the same
/// transaction as a file's symbols, edges and tests, the identity of the analysis it
/// wrote (`file_keys`, through `RebuildProjects`' `fileKeys`). A recreated index starts
/// with none.
///
/// DERIVED: the `FileChecked` task-cache entry. It records that this file's analysis was
/// done, so an identical later check can skip redoing it. A hit skips the analysis
/// entirely, so it is valid ONLY where the index already holds exactly that analysis
/// and nothing newer for the file is waiting to be flushed (`mayReplay`). Anywhere else
/// the lookup is refused, and the file is analysed again: on disagreement the index wins, and
/// a disagreement costs a re-analysis, never a skip. The entry itself is written whenever
/// the file is analysed (ADR-059 §17): only reading it is guarded (`Guard`). Refusing the
/// write too left a first analysis uncached, so a restarted daemon demanded a live check of
/// every file whose analysis it had indexed only once. An analysis-only daemon selects no
/// tests from the index, so it keeps the plain key.
///
/// Why the read is guarded: the entry is written when the analysis is only pending. An analysis that never
/// reached a flush (retired by a project-model change, lost to a restart) left an entry
/// claiming work the index never received, and every identical check replayed it. The
/// file then stayed out of the index, and a change to what it tests read as covered by
/// nothing.
module FsHotWatch.TestPrune.IndexedAnalysis

open FsHotWatch

/// The identity of one file's analysis: the file's source, its compiler signature, and
/// the public shape of everything its check resolved outside the file
/// (`CacheInputs.usedSignaturesHash`).
///
/// The shape is in it because the analysis reads it. A type or value in an earlier file
/// or a referenced project can change while this file's bytes and diagnostics do not: an
/// auto-opened module that now shadows a name this file uses moves the dependency the
/// index records to a different symbol, and test selection would follow the stale edge
/// (AUTOMATION-1151). A body-only edit upstream leaves the shape, and so the identity,
/// alone.
let identity (source: string) (fcsSignature: string) (usedSignatures: string) : string =
    CheckCache.sha256Hex $"%s{fcsSignature}\n%s{usedSignatures}\n%s{source}"

/// The identity of `result`'s analysis (`identity`). `None` when what the file's check
/// resolved upstream cannot be described: no index entry can then vouch for the analysis.
let identityOf (repoRoot: string) (result: FsHotWatch.Events.FileCheckResult) : string option =
    CacheInputs.usedSignaturesHash (Some repoRoot) result.ProjectOptions result.CheckResults
    |> Option.map (identity result.Source (CheckCache.fcsCheckSignature result.CheckResults))

/// What a flush records for an analysis `identityOf` cannot describe: unique, so no
/// later check's identity ever matches it and the analysis is never replayed.
let undescribed () : string =
    "undescribed:" + System.Guid.NewGuid().ToString "N"

/// Whether a `FileChecked` cache hit may stand in for analysing a file whose check has
/// `identity`: the index holds that analysis (`indexed`, its `file_keys` entry), and no
/// other analysis of the file is pending a flush (`pending`).
let mayReplay (indexed: string option) (pending: string option) (identity: string) : bool =
    indexed = Some identity && pending |> Option.forall ((=) identity)

/// `mayReplay` over the index, as the plugin state carries it: what decides whether a
/// `FileChecked` entry may be READ (`PluginFramework.IFileCacheReadGuard`). The entry is
/// WRITTEN whenever a file is analysed (ADR-059 §17), so a restart over an unchanged tree
/// finds one for every file; writing it costs nothing unsound, since a read still needs
/// the index to hold exactly that analysis. A class, compared by reference, so the state
/// that holds it keeps its structural equality.
[<Sealed>]
type Guard(mayReplay: Map<string, string> -> FsHotWatch.Events.FileCheckResult -> bool) =
    /// Whether an entry for `result` may stand in for analysing it, given the analyses
    /// pending a flush (`TestPruneState.PendingIdentities`).
    member _.MayReplay(pending: Map<string, string>, result: FsHotWatch.Events.FileCheckResult) =
        mayReplay pending result
