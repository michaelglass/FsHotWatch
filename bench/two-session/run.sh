#!/usr/bin/env bash
# Two-session repository-host admission harness (ADR-049, ADR-051, ADR-052, ADR-058).
#
# One repository host serves two worktrees of a synthetic repository: one project of
# 240 chained files, `r` and its jj workspace `b`. Both cold-scan. Then, for DURATION
# seconds, `b` saves one file every 0.5 s (interactive change batches, and builds), while
# every 5 s `r` edits the file at the root of the chain and forces a rescan (bulk). The
# check cache is off, so every check takes a slot. At the end the host's status is read
# once and summarised: each session's check admission per class, and its process
# admission (builds and test hosts).
#
# usage: bench/two-session/run.sh <fshw-bin-dir> <label> [out-dir]
#   <fshw-bin-dir>  a built FsHotWatch.Cli output directory. It is copied (frozen) first,
#                   so rebuilding the tree mid-run cannot change what is measured.
#   <label>         names the run's directory and the summary rows.
#   [out-dir]       where runs are kept (default ~/.local/state/fshw-two-session). Not
#                   /tmp, which a restart empties, and not inside the FsHotWatch tree,
#                   whose discovery would find the synthetic project.
# env: DURATION (default 30), FILES (default 240), LINT (default 0; 1 turns the lint plugin
# on, a `FileChecked` consumer, so the live-result bound of ADR-054 has something to bound).
# CHAINS (default 0): also a chain of CHAINS small projects of SMALL_FILES (default 5)
# files each, S1 <- S2 <- ... , beside the big one, and an `All` project referencing the big
# one and the last of the chain. The big project shares the first tier with S1, so under a
# tier barrier the chain waits for it, and under ADR-056 it does not.
# `dotnet` must be on PATH.
#
# Writes <out>/<label>/{status.json,summary.tsv,driver.log} and prints the summary. fshw's
# box-wide homes (FSHW_CACHE_HOME, FSHW_STATE_HOME) point into <out>/<label>/ too, so a
# run never touches the real ~/.cache/fshw or ~/.local/state/fshw.
set -euo pipefail

bin_src=${1:?fshw bin dir}
label=${2:?label}
out=${3:-$HOME/.local/state/fshw-two-session}
duration=${DURATION:-30}
files=${FILES:-240}
lint=$( [ "${LINT:-0}" = 1 ] && echo true || echo false )
chains=${CHAINS:-0}
small_files=${SMALL_FILES:-5}

run_dir="$out/$label"
rm -rf "$run_dir"
mkdir -p "$run_dir/bin"
cp -R "$bin_src"/. "$run_dir/bin/"
fshw="$run_dir/bin/FsHotWatch.Cli"
log="$run_dir/driver.log"

# Scratch homes, before anything runs fshw. Without them every daemon this harness starts
# writes its shared task store into the real ~/.cache/fshw (a namespace per synthetic
# repo) and the host's control state into the real ~/.local/state/fshw. They live in the
# run directory, so `rm -rf "$run_dir"` above discards the last run's along with it.
# TwoSessionHarnessTests pins both exports and their order.
export FSHW_CACHE_HOME="$run_dir/fshw-cache"
export FSHW_STATE_HOME="$run_dir/fshw-state"
say() { echo "[$(date +%H:%M:%S)] $*" | tee -a "$log"; }

r="$run_dir/r"
b="$run_dir/b"
src="$r/src/Synth"
mkdir -p "$src"

# --- the synthetic repository ---
{
  echo '<Project Sdk="Microsoft.NET.Sdk">'
  echo '  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>'
  echo '  <ItemGroup>'
  for i in $(seq -f '%03g' 1 "$files"); do echo "    <Compile Include=\"File$i.fs\" />"; done
  echo '  </ItemGroup>'
  echo '</Project>'
} > "$src/Synth.fsproj"

for n in $(seq 1 "$files"); do
  i=$(printf '%03d' "$n")
  if [ "$n" -eq 1 ]; then
    printf 'module File%s\n\nlet value = 1\n' "$i" > "$src/File$i.fs"
  else
    p=$(printf '%03d' $((n - 1)))
    printf 'module File%s\n\nlet value = File%s.value + 1\n' "$i" "$p" > "$src/File$i.fs"
  fi
done

# --- optional: a chain of small projects, and a project over both (ADR-056) ---
build_project=src/Synth/Synth.fsproj
if [ "$chains" -gt 0 ]; then
  for k in $(seq 1 "$chains"); do
    dir="$r/src/S$k"
    mkdir -p "$dir"
    {
      echo '<Project Sdk="Microsoft.NET.Sdk">'
      echo '  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>'
      echo '  <ItemGroup>'
      for j in $(seq 1 "$small_files"); do echo "    <Compile Include=\"S${k}F${j}.fs\" />"; done
      echo '  </ItemGroup>'
      [ "$k" -gt 1 ] && echo "  <ItemGroup><ProjectReference Include=\"../S$((k - 1))/S$((k - 1)).fsproj\" /></ItemGroup>"
      echo '</Project>'
    } > "$dir/S$k.fsproj"
    for j in $(seq 1 "$small_files"); do
      if [ "$j" -eq 1 ]; then
        if [ "$k" -eq 1 ]; then prev=1; else prev="S$((k - 1))F${small_files}.value"; fi
      else
        prev="S${k}F$((j - 1)).value"
      fi
      printf 'module S%sF%s\n\nlet value = %s + 1\n' "$k" "$j" "$prev" > "$dir/S${k}F${j}.fs"
    done
  done
  mkdir -p "$r/src/All"
  cat > "$r/src/All/All.fsproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="All.fs" /></ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Synth/Synth.fsproj" />
    <ProjectReference Include="../S$chains/S$chains.fsproj" />
  </ItemGroup>
</Project>
EOF
  last=$(printf '%03d' "$files")
  printf 'module All\n\nlet value = File%s.value + S%sF%s.value\n' "$last" "$chains" "$small_files" > "$r/src/All/All.fs"
  build_project=src/All/All.fsproj
fi

cat > "$r/.fshw.json" <<'JSON'
{
  "repositoryHost": true,
  "build": { "command": "dotnet", "args": "build BUILD_PROJECT -nologo -v q" },
  "format": false,
  "lint": LINT_ON
}
JSON
sed -i.bak -e "s/LINT_ON/$lint/" -e "s#BUILD_PROJECT#$build_project#" "$r/.fshw.json" && rm "$r/.fshw.json.bak"
printf 'bin/\nobj/\n.fshw/\n' > "$r/.gitignore"

(cd "$r" && jj git init --quiet . && jj commit -m "synthetic" --quiet && jj workspace add --quiet -r @- "$b")

export FSHW_CHECK_CACHE=off
export FSHW_REPOSITORY_HOST=1

# The repository status, as JSON: the agent envelope carries the host's payload as its
# one line.
host_status() { (cd "$r" && "$fshw" -a status --repository 2>/dev/null | jq -r '.lines[0]') || true; }

# Both sessions serving, their cold scans done, and no check admitted or waiting.
sessions_settled() {
  host_status | jq -e '(.sessions | length) == 2
    and all(.sessions[]; .serving and .scanGeneration >= 1
                         and .admission.running == 0 and .admission.queued == 0)' > /dev/null 2>&1
}

cleanup() {
  (cd "$r" && "$fshw" stop --repository > /dev/null 2>&1) || true
  (cd "$b" && "$fshw" stop --repository > /dev/null 2>&1) || true
}
trap cleanup EXIT

say "starting sessions r and b"
(cd "$r" && "$fshw" start >> "$log" 2>&1) &
(cd "$b" && "$fshw" start >> "$log" 2>&1) &

deadline=$(( $(date +%s) + 300 ))
until sessions_settled; do
  [ "$(date +%s)" -lt "$deadline" ] || { say "sessions never served"; host_status > "$run_dir/status.json"; exit 2; }
  sleep 1
done
say "both sessions serving; driving for ${duration}s"

end=$(( $(date +%s) + duration ))
tick=0
while [ "$(date +%s)" -lt "$end" ]; do
  # b: one save every 0.5 s, round the chain's tail half.
  n=$(( files / 2 + (tick % (files / 2)) + 1 ))
  f=$(printf '%s/src/Synth/File%03d.fs' "$b" "$n")
  printf '// save %d\n' "$tick" >> "$f"
  # r: every 5 s, edit the chain's root and force a rescan.
  if [ $(( tick % 10 )) -eq 0 ]; then
    printf '// root %d\n' "$tick" >> "$src/File001.fs"
    (cd "$r" && "$fshw" scan >> "$log" 2>&1 &)
  fi
  tick=$(( tick + 1 ))
  sleep 0.5
done

host_status > "$run_dir/status.json"
say "status written"

jq -r --arg label "$label" '
  .sessions as $s
  | ["label","session","lane","class","granted","longestWaitMs","waitedMs","heldMs"],
    ( $s[] | (.root | split("/") | last) as $who
      | ( ["checks", .admission], ["processes", (.processAdmission // {})] )
      | . as [$lane, $a]
      | ("interactive","bulk") as $class
      | [$label, $who, $lane, $class,
         ($a[$class].granted // 0), ($a[$class].longestWaitMs // 0),
         ($a[$class].waitedMs // 0), ($a.heldMs // 0)] )
  | @tsv' "$run_dir/status.json" | tee "$run_dir/summary.tsv"

# Each session's live check results (ADR-054): the bound, the most held at once, and how
# long a producer waited for a place. Absent on a build before ADR-054.
jq -r --arg label "$label" '
  ["label","session","liveBound","livePeak","liveAdmitted","liveLongestWaitMs","liveWaitedMs"],
  ( .sessions[] | (.root | split("/") | last) as $who | (.liveResults // {}) as $l
    | [$label, $who, ($l.bound // "-"), ($l.peak // "-"), ($l.admitted // "-"),
       ($l.longestWaitMs // "-"), ($l.waitedMs // "-")] )
  | @tsv' "$run_dir/status.json" | tee "$run_dir/live-results.tsv"

# Each session's cold scan, from its own scan record (`.fshw/scan-metrics.jsonl`): how
# long it took, what it checked, and the process's working set at its end. For a hosted
# session that process is the host, so the figure covers both sessions.
{
  printf 'label\tsession\tcoldScanMs\tfilesChecked\tcheckStarts\tretryRounds\trssMB\n'
  for who in r b; do
    jq -r --arg label "$label" --arg who "$who" '
      select(.kind == "cold")
      | [$label, $who, (.durationMs | floor), .filesChecked, .checkStarts, .retryRounds,
         ((.rssBytes / 1048576) | floor)]
      | @tsv' "$run_dir/$who/.fshw/scan-metrics.jsonl" 2>/dev/null | head -1
  done
} | tee "$run_dir/scan.tsv"
