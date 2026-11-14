#!/bin/bash
# The ADR-059 §9 deletion count behind the re-architecture's Phase 1 and Phase 3 gates
# (AUTOMATION-1699, under AUTOMATION-1362).
#
#   scripts/phase1-deletions/count.sh [revision]     (default: the working copy, @)
#
# Counts each §9 item's machinery BY NAMED DEFINITION, read at `revision` through jj, so the
# count survives code moving within a file. A definition is a top-level `let`, `type` or
# `module` in the named file, with the `///` comments and `[<...>]` attributes directly above
# it, up to the next top-level element. Prints raw and code lines (code = non-blank, not a `//`
# or `(* *)` comment) per definition and per item, and `GONE` for a definition the revision no
# longer holds: what was deleted.
#
# The gate (1699): Phase 1 deletes at least 177 CODE lines of item 1 (247 raw by the original range
# count, which also swept up the blank lines between definitions; by definition the raw baseline is
# 222 and the code baseline is unchanged at 177), measured
# as the baseline count (revision d231e6cb, FsHotWatch alpha.148) minus the count at the end of
# Phase 1. Items 2-4 and the test half of item 5 count toward Phase 3, beside item 6.
set -uo pipefail
rev=${1:-@}

# One definition's raw and code lines in `file` at `rev`; "GONE" when the file or the
# definition is not there.
count_def() {
    local file=$1 name=$2
    jj file show -r "$rev" "$file" 2>/dev/null | awk -v name="$name" '
        function toplevel(l) { return l ~ /^(let|type|module|and|open|namespace|exception)[ \t]/ || l ~ /^\[</ || l ~ /^\/\/\// || l ~ /^#/ }
        function head(l) {
            return l ~ ("^(let|type|module)( (private|internal|inline|rec|mutable))* " name "([^A-Za-z0-9_\x27]|$)")
        }
        { line[NR] = $0 }
        END {
            # Every top-level definition with the name: a type and its companion module both count.
            found = 0; raw = 0; code = 0
            for (start = 1; start <= NR; start++) {
                if (!head(line[start])) continue
                found = 1
                first = start
                while (first > 1 && (line[first - 1] ~ /^\/\/\// || line[first - 1] ~ /^\[</)) first--
                last = NR
                for (i = start + 1; i <= NR; i++) if (toplevel(line[i])) { last = i - 1; break }
                while (last > start && line[last] ~ /^[ \t]*$/) last--
                inb = 0
                for (i = first; i <= last; i++) {
                    raw++
                    l = line[i]; sub(/^[ \t]+/, "", l)
                    if (inb) { if (l ~ /\*\)/) inb = 0; continue }
                    if (l == "" || l ~ /^\/\//) continue
                    if (l ~ /^\(\*/ && l !~ /^\(\*\)/) { if (l !~ /\*\)/) inb = 1; continue }
                    code++
                }
            }
            if (!found) print "GONE"; else print raw, code
        }'
}

count_item() {
    local title=$1
    shift
    local tr=0 tc=0
    echo "== $title"
    for spec in "$@"; do
        local file=${spec%%:*} name=${spec#*:}
        local result
        result=$(count_def "$file" "$name")
        if [ "$result" = GONE ]; then
            printf '  %6s %6s  %s %s\n' GONE "" "$file" "$name"
        else
            read -r raw code <<<"$result"
            printf '  %6d %6d  %s %s\n' "$raw" "$code" "$file" "$name"
            tr=$((tr + raw))
            tc=$((tc + code))
        fi
    done
    printf '  %6d %6d  TOTAL\n' "$tr" "$tc"
}

echo "revision: $(jj log -r "$rev" --no-graph -T 'commit_id.short()' 2>/dev/null)"

SV=src/FsHotWatch.Cli/ShadowVerdict.fs
OA=src/FsHotWatch.Cli/OutcomeAnalytics.fs
count_item "Phase 1 / item 1: the shadow log and its comparison" \
    $SV:KillSwitchEnvVar $SV:enabledBy $SV:LogSchema $SV:logPath $SV:Class $SV:Direction \
    $SV:classOf $SV:direction $SV:lineWith $SV:lineAs $SV:notComparedLineAs $SV:appendLine $SV:line \
    $OA:ShadowLine $OA:ShadowRead $OA:ShadowDay $OA:LookupReads \
    $OA:parseShadowLine $OA:readShadowLogs $OA:shadowLogsFor

count_item "item 5 (net): the legacy key recipes and their duplicates (non-test uses: Phase 1; the rest: Phase 3)" \
    src/FsHotWatch/TreeHash.fs:hashEntries \
    src/FsHotWatch/TestEvidenceKey.fs:digestEntries src/FsHotWatch/TestEvidenceKey.fs:labels \
    src/FsHotWatch/TestEvidenceKey.fs:ofLabels src/FsHotWatch/TestEvidenceKey.fs:compute \
    src/FsHotWatch/TestEvidenceKey.fs:moved src/FsHotWatch/TestEvidenceKey.fs:toolchainOf \
    src/FsHotWatch/TaskCache.fs:KeyFingerprints src/FsHotWatch/TaskCache.fs:missReasonForKeys \
    src/FsHotWatch/TaskCache.fs:ToolchainLabel src/FsHotWatch/TaskCache.fs:scopeToToolchain \
    src/FsHotWatch/TaskCache.fs:merkleCacheKey

cat <<'NOTE'

Items 2-4 (Phase 3) are counted by whole file and range, at the baseline only, in 1699's first
comment; they are re-pinned by definition when Phase 3 starts. Test-side blocks: blocks.sh with
the regexes there.
NOTE
