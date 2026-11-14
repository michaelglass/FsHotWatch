#!/usr/bin/env bash
# Run `mise run ci`'s two halves and say, per half, what ran and what was reused.
#
#   bash scripts/ci-halves.sh '<confirm command>' '<integration command>'
#
# The two commands are CI's own, verbatim: the build job's `lint-cmd` and the `integration`
# job's test step in .github/workflows/ci.yml. mise.toml passes them in, and CiParityTests
# pins them to that file, so this script only orders and reports; it never restates either.
#
# The integration half ALWAYS runs, after the confirm half (never beside it: those tests
# assert on wall-clock behaviour) and whatever the confirm half did. Chained with `&&`, a red
# confirm skipped it and hid whether the integration suite would also have failed.
#
# The summary exists because the log alone misled (AUTOMATION-1689). A confirm on an unchanged
# tree answers from the verdict an EARLIER run earned and runs no tests, and every confirm logs
# the config notice "NOT RUN — tests/FsHotWatch.IntegrationTests" (the declared exclusion this
# script exists to cover). A spot check printed both and exited 0, the integration suite had in
# fact run, and nothing said so. The last lines now state each half:
#
#   ci: confirm      ran N tests in run X[; K suite(s) reused from earlier runs: P (run Y), ...]
#   ci: confirm      REUSED run X (<producedAt>) — this ci ran no confirm tests
#   ci: confirm      produced no verdict of this run — FAILED (exit N)
#   ci: integration  ran N tests, F failed
#   ci: integration  NOT RUN — no test summary in its output (exit N)
#
# with " — FAILED (exit N)" appended to a half that failed.
#
# "Reused" is the rule `fshw` itself applies (AgentEnvelope.reusedBy): the verdict on disk was
# produced before this confirm started. The confirm fast path leaves verdict.json byte-identical,
# so a verdict stamped earlier is the earlier run's; one stamped at or after the start is this
# run's. The counts come from verdict.json, not from screen-scraping confirm's output.
#
# Exit: 0 only when the confirm half exited 0 with a verdict AND the integration half exited 0
# having reported tests. Otherwise the confirm half's non-zero exit, else the integration
# half's, else 1. CiHalvesTests drives every case with faked commands.
set -u

if [ $# -ne 2 ]; then
  echo "usage: $0 '<confirm command>' '<integration command>'" >&2
  exit 2
fi

confirm_cmd=$1
integration_cmd=$2
verdict=.fshw/verdict.json

started=$(date -u +%s)
bash -c "$confirm_cmd"
confirm_rc=$?

output=$(mktemp "${TMPDIR:-/tmp}/ci-halves.XXXXXX")
trap 'rm -f "$output"' EXIT
bash -c "$integration_cmd" 2>&1 | tee "$output"
integration_rc=${PIPESTATUS[0]}

failed_suffix() {
  if [ "$1" -ne 0 ]; then printf ' — FAILED (exit %s)' "$1"; fi
}

# Whose verdict is on disk: `this-run`, `earlier` or `none`.
verdict_owner=none
if [ -f "$verdict" ]; then
  # Fractional seconds dropped: jq's fromdateiso8601 parses whole seconds only.
  if produced=$(jq -er '.producedAt | sub("\\.[0-9]+"; "") | fromdateiso8601' "$verdict" 2>/dev/null); then
    if [ "$produced" -lt "$started" ]; then verdict_owner=earlier; else verdict_owner=this-run; fi
  fi
fi

confirm_ok=1
if [ "$verdict_owner" = this-run ]; then
  confirm_line=$(jq -r '
    ([.suites[]? | select(.reusedFrom == null) | .total] | add // 0) as $ran
    | [.suites[]? | select(.reusedFrom != null) | "\(.project) (run \(.reusedFrom.runId))"] as $reused
    | "ran \($ran) tests in run \(.runId)"
      + (if ($reused | length) > 0
         then "; \($reused | length) suite(s) reused from earlier runs: \($reused | join(", "))"
         else "" end)' "$verdict")$(failed_suffix "$confirm_rc")
  [ "$confirm_rc" -eq 0 ] || confirm_ok=0
elif [ "$verdict_owner" = earlier ] && [ "$confirm_rc" -eq 0 ]; then
  confirm_line=$(jq -r '"REUSED run \(.runId) (\(.producedAt)) — this ci ran no confirm tests"' "$verdict")
else
  # A failing pre-step (check-msbuild-deps, package floors) exits before confirm writes
  # anything, so an earlier verdict still on disk says nothing about this run.
  confirm_line="produced no verdict of this run$(failed_suffix "$confirm_rc")"
  confirm_ok=0
fi

# Microsoft.Testing.Platform's closing block: "  total: N" and "  failed: F". Colour codes
# are stripped first; the last block wins if the runner printed more than one.
esc=$(printf '\033')
plain=$(sed "s/${esc}\[[0-9;]*m//g" "$output")
total=$(printf '%s\n' "$plain" | sed -n 's/^[[:space:]]*total:[[:space:]]*\([0-9][0-9]*\).*/\1/p' | tail -n 1)
failed=$(printf '%s\n' "$plain" | sed -n 's/^[[:space:]]*failed:[[:space:]]*\([0-9][0-9]*\).*/\1/p' | tail -n 1)

integration_ok=1
if [ -n "$total" ]; then
  integration_line="ran $total tests, ${failed:-0} failed$(failed_suffix "$integration_rc")"
  [ "$integration_rc" -eq 0 ] || integration_ok=0
else
  integration_line="NOT RUN — no test summary in its output (exit $integration_rc)"
  integration_ok=0
fi

echo
echo "ci: confirm      $confirm_line"
echo "ci: integration  $integration_line"

if [ "$confirm_ok" -eq 1 ] && [ "$integration_ok" -eq 1 ]; then exit 0; fi
if [ "$confirm_rc" -ne 0 ]; then exit "$confirm_rc"; fi
if [ "$integration_rc" -ne 0 ]; then exit "$integration_rc"; fi
exit 1
