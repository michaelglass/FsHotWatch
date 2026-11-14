#!/bin/bash
# The ADR-059 §9 deletion count behind the re-architecture's Phase 1 and Phase 3 gates
# (AUTOMATION-1362; Phase 1 gate ticket filed with this script).
#
#   scripts/phase1-deletions/count.sh [revision]     (default: d231e6cb, the baseline)
#
# Prints, per §9 item, the raw and code lines (code = non-blank, not a // or (* *) comment)
# of the source ranges that item deletes, read AT `revision` through jj, so the baseline
# reproduces from any checkout. Ranges are pinned to the baseline revision d231e6cb; at a
# later revision they no longer line up, so the gate is measured as: the baseline count of
# the Phase 1 rows, minus whatever of those symbols still exists (`survivors`, below).
#
# PHASE 1 gate rows: item 1 (shadow log and comparison) and item 5's non-test uses of the
# legacy keys. Items 2-4 and the test half of item 5 count toward Phase 3, beside item 6.
# Test-side counts use blocks.sh with the regexes below.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
rev=${1:-d231e6cb}
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

show() { mkdir -p "$tmp/$(dirname "$1")" && jj file show -r "$rev" "$1" >"$tmp/$1"; }

count() {
    local title=$1
    shift
    echo "== $title"
    local specs=()
    for spec in "$@"; do
        show "${spec%%:*}"
        specs+=("$tmp/$spec")
    done
    bash "$here/loc.sh" "${specs[@]}" | sed "s|$tmp/||"
}

echo "revision: $rev"
count "Phase 1 / item 1: the shadow log and its comparison" \
    src/FsHotWatch.Cli/ShadowVerdict.fs:26-34 src/FsHotWatch.Cli/ShadowVerdict.fs:38-39 \
    src/FsHotWatch.Cli/ShadowVerdict.fs:43-101 src/FsHotWatch.Cli/ShadowVerdict.fs:408-492 \
    src/FsHotWatch.Cli/OutcomeAnalytics.fs:31-49 src/FsHotWatch.Cli/OutcomeAnalytics.fs:90-100 \
    src/FsHotWatch.Cli/OutcomeAnalytics.fs:119-180
count "item 5 (net): the legacy key recipes and their duplicates (non-test uses: Phase 1; the rest: Phase 3)" \
    src/FsHotWatch/TreeHash.fs:268-284 src/FsHotWatch/TestEvidenceKey.fs:274-353 \
    src/FsHotWatch/TaskCache.fs:123-212 src/FsHotWatch/TaskCache.fs:345-375 src/FsHotWatch/TaskCache.fs:430-458
count "Phase 3 / item 2: ledgers #8-#14 (lower bound: scattered call sites not counted)" \
    src/FsHotWatch.TestPrune/FullSuiteBaseline.fs src/FsHotWatch.TestPrune/PendingVerification.fs \
    src/FsHotWatch.TestPrune/OutstandingFailures.fs src/FsHotWatch.TestPrune/WholeProjectEvidence.fs \
    src/FsHotWatch.TestPrune/CoverageNotIngested.fs src/FsHotWatch.TestPrune/RuntimeCoverageSelection.fs:41-204 \
    src/FsHotWatch.TestPrune/PluginStartup.fs:64-257 src/FsHotWatch.TestPrune/PluginRuns.fs:265-373
count "Phase 3 / item 3: receipts, Earned evidence, CohortSeal, settled-read grading (lower bound)" \
    src/FsHotWatch.TestPrune/EvidenceReceipts.fs src/FsHotWatch.TestPrune/ReceiptTransitions.fs \
    src/FsHotWatch/Events.fs:874-1140 src/FsHotWatch/Events.fs:1149-1292 src/FsHotWatch/Events.fs:1324-1332 \
    src/FsHotWatch.Cli/IpcParsing.fs:946-1032 src/FsHotWatch.Cli/IpcParsing.fs:1544-1632 \
    src/FsHotWatch.Cli/IpcOutput.fs:1421-1459 src/FsHotWatch.Cli/IpcOutput.fs:1525-1594 \
    src/FsHotWatch.Cli/IpcOutput.fs:1650-1659 src/FsHotWatch.Cli/CheckVerdict.fs:463-583
count "Phase 3 / item 4: the whole-run test-prune task cache" \
    src/FsHotWatch.TestPrune/IndexMaintenance.fs:276-494 src/FsHotWatch.TestPrune/PluginFold.fs:430-563

cat <<'NOTE'

Test-side blocks (run over the test files at the same revision with blocks.sh):
  R2='PendingVerification\.|OutstandingFailure\.|FullSuiteBaseline\.|WholeProjectEvidence\.|CoverageNotIngested\.|RuntimeCoverageObligations|runtimeCoverageObligations|owner-publication-pending|prepareCommitIn|restorePersisted|debtFromRestored'
  R3='EvidenceReceipt|receiptSpeaksFor|CohortSeal|ReceiptTransition|EarnedEvidence|Earned[A-Z(. ]|AnalysisEvidence|ModelReceipt|ReceiptLedger|describeReceiptReads'
  R4='cacheKeyFor|cacheKeyWith|sessionHasTestEvidence'
  R5='merkleCacheKey|scopeToToolchain|ToolchainScopedTaskCache|KeyFingerprints|missReasonForKeys|TreeHash\.(hashEntries|ofWalked|compute)|TestEvidenceKey\.(compute|ofLabels|labels|moved|toolchainOf)|ProjectInputs\.|toolchainIdentity'
  e.g. bash scripts/phase1-deletions/blocks.sh "$R2|$R3|$R4" $(find tests -name '*.fs')
NOTE
