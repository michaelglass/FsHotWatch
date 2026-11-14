#!/usr/bin/env bash
# remote-canary-server.sh — the build server's half of scripts/remote-canary.sh (read that
# first). remote-canary.sh copies this script into a run directory on the server, beside
# feed/ (the candidates), seed/seed.csproj, pin.tar and run.json, and runs it there as
# `bash <run>/server.sh`. It:
#
#   1. fetches the repository and makes the canary workspace afresh at run.json's base
#      revision (tearing down and forgetting one left by an earlier run), then applies the
#      pin change;
#   2. inside `wrap`: evicts any cached copy of the candidates, seeds them into the NuGet
#      cache from feed/ (`<dotnet> restore seed.csproj --source feed --packages <cache>`,
#      as the tagger does), runs `prepare`, then `gate`, each through /bin/sh in the
#      workspace;
#   3. runs `teardown`, evicts the candidates again (they were never published, and the
#      real release reuses their version), copies the verdict and every failing test
#      project's CTRF receipt to <run>/evidence and prints the failing tests;
#   4. prints `remote-canary: exit=<gate exit>` as its LAST line and exits with it.
#
# Its standard input is the releaser's keepalive: EOF there means the releaser is gone
# (connection lost, tagger timeout), and the gate is stopped rather than left holding the box.
set -u -o pipefail
trap '' PIPE HUP

dir=$(cd "$(dirname "$0")" && pwd)
run="$dir/run.json"
field() { jq -r --arg k "$1" '.remote[$k] // empty' "$run"; }
nuget=${NUGET_PACKAGES:-$HOME/.nuget/packages}
ws="$HOME/$(jq -r .workspace "$run")"
candidates() { sed -n 's/.*<PackageDownload Include="\([^"]*\)" Version="\[\([^]]*\)\]".*/\1 \2/p' "$dir/seed/seed.csproj"; }
evict() {
  local id v
  while read -r id v; do
    rm -rf "$nuget/$(printf %s "$id" | tr '[:upper:]' '[:lower:]')/$(printf %s "$v" | tr '[:upper:]' '[:lower:]')"
  done < <(candidates)
}

# --- the part that runs inside `wrap` -------------------------------------------------
if [ "${1:-}" = --inside-wrap ]; then
  date +%s >"$dir/gate.started"
  cd "$ws" || exit 66
  evict
  echo "remote-canary: seeding $(candidates | tr '\n' ' ')into $nuget"
  /bin/sh -c "$(field dotnet)"' restore "$1" --source "$2" --packages "$3"' sh \
    "$dir/seed/seed.csproj" "$dir/feed" "$nuget" || { echo "remote-canary: seeding the candidates failed"; exit 2; }
  prepare=$(field prepare)
  if [ -n "$prepare" ]; then
    echo "\$ $prepare"
    /bin/sh -c "$prepare" || { rc=$?; echo "remote-canary: prepare exited $rc"; exit "$rc"; }
  fi
  touch "$dir/gate.marker"
  echo "\$ $(field gate)"
  /bin/sh -c "$(field gate)"
  exit $?
fi

# --- the run ----------------------------------------------------------------------------
report() { echo "remote-canary: exit=$1"; exit "$1"; }
started=$(date +%s)
repo="$HOME/$(jq -r .repo "$run")"
base=$(jq -r .base "$run")
name=$(basename "$ws")
# For `wrap`, e.g. a job-queue label: "canary-$CANARY_PACKAGE-$CANARY_VERSION".
CANARY_PACKAGE=$(jq -r .package "$run")
CANARY_VERSION=$(jq -r .version "$run")
export CANARY_PACKAGE CANARY_VERSION
teardown() {
  local t; t=$(field teardown)
  [ -n "$t" ] && [ -d "$ws" ] || return 0
  echo "\$ $t   (teardown, in $ws)"
  (cd "$ws" && timeout 300 /bin/sh -c "$t") || echo "remote-canary: teardown exited $? (best effort)"
}

cd "$repo" || { echo "remote-canary: no repository at $repo"; report 66; }
jj git fetch >/dev/null 2>&1 || { echo "remote-canary: jj git fetch failed in $repo"; report 66; }
if [ -d "$ws" ]; then teardown; fi
jj workspace forget "$name" >/dev/null 2>&1
rm -rf "$ws"
jj workspace add --name "$name" -r "$base" "$ws" >/dev/null 2>&1 \
  || { echo "remote-canary: cannot make workspace $ws at $base"; report 66; }
tar -xf "$dir/pin.tar" -C "$ws" || { echo "remote-canary: cannot apply the pin change"; report 66; }
echo "remote-canary: workspace $ws at $base on $(hostname -s)"

free_pct() { memory_pressure -Q 2>/dev/null | awk -F': ' '/free percentage/{gsub("%","",$2); print $2}'; }
(min=101; while sleep 5; do f=$(free_pct); [ -n "$f" ] && [ "$f" -lt "$min" ] && min=$f && echo "$min" >"$dir/min-free-pct"; done) &
sampler=$!

# The gate runs in its own process group (set -m), so the watchdog can stop all of it.
exec 3<&0
: >"$dir/gate.log"
set -m
eval "$(field wrap)"' bash "$dir/server.sh" --inside-wrap </dev/null >"$dir/gate.log" 2>&1 &'
job=$!
set +m
# The watchdog blocks on that input until EOF. Not `read -t`: macOS's bash 3.2 returns 1
# both on EOF and on a timeout, so a poll cannot tell "gone" from "quiet" (the first
# remote run stopped a healthy gate after one second that way).
(
  cat <&3 >/dev/null 2>&1
  kill -0 "$job" 2>/dev/null || exit 0
  echo "remote-canary: the releaser's connection closed; stopping the gate" >>"$dir/gate.log"
  kill -TERM -- "-$job" 2>/dev/null
  for _ in 1 2 3 4 5 6; do kill -0 "$job" 2>/dev/null || exit 0; sleep 5; done
  kill -KILL -- "-$job" 2>/dev/null
) &
watchdog=$!
exec 3<&-
tail -n +1 -f "$dir/gate.log" &
tailer=$!
# Only the gate is waited for; the helpers are killed below, and a disowned job's death is
# not reported (a "Terminated" notice would land after the exit line).
disown "$sampler" "$watchdog" "$tailer"
wait "$job"
rc=$?
# The watchdog first, then its `cat`, which would otherwise outlive this script.
kill "$watchdog" 2>/dev/null
pkill -P "$watchdog" 2>/dev/null
sleep 1
kill "$tailer" "$sampler" 2>/dev/null

teardown
evict

mkdir -p "$dir/evidence"
cp "$dir/gate.log" "$dir/evidence/" 2>/dev/null
if [ -f "$dir/gate.marker" ]; then
  if [ -f "$ws/.fshw/verdict.json" ] && [ "$ws/.fshw/verdict.json" -nt "$dir/gate.marker" ]; then
    cp "$ws/.fshw/verdict.json" "$dir/evidence/verdict.json"
  fi
  while IFS= read -r receipt; do
    failed=$(jq -r '.results.summary.failed // 0' "$receipt" 2>/dev/null)
    [ "${failed:-0}" != 0 ] || continue
    runid=$(basename "$(dirname "$receipt")")
    cp "$receipt" "$dir/evidence/$runid-$(basename "$receipt")"
    project=$(basename "$receipt" .ctrf.json)
    jq -r --arg p "$project" '.results.tests[] | select(.status == "failed")
      | "remote-canary: FAILED \($p): \(.name): \((.message // "") | split("\n")[0])"' "$receipt" 2>/dev/null | head -20
  done < <(find "$ws/.fshw/test-runs" -name '*.ctrf.json' -newer "$dir/gate.marker" 2>/dev/null)
fi

ended=$(date +%s)
gate_started=$(cat "$dir/gate.started" 2>/dev/null || echo "$started")
echo "remote-canary: queued $((gate_started - started))s, ran $((ended - gate_started))s, min free memory $(cat "$dir/min-free-pct" 2>/dev/null || echo n/a)%, evidence in $dir/evidence"
report "$rc"
