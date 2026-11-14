#!/usr/bin/env bash
# E5 (AUTOMATION-1696): wall time and peak resident memory of a pure-lookup `fshw verdict`.
#
#   scripts/e5-verdict-cost.sh <repo-dir> [runs] [-- command...]
#
# Runs `command` (default: `dotnet fshw verdict`) in <repo-dir>, `runs` times (default 10, at
# least 10) in each of two modes, and prints every sample, then the median and the maximum:
#
#   cold  each run follows an eviction of the command's files from the page cache when the
#         box allows one (`vmtouch -e`, or Linux drop_caches as root), and an idle second.
#         When it cannot evict, the mode is reported as `cold-unevicted`: a fresh process
#         after an idle second, which still pays JIT and assembly load but not disk.
#   warm  back-to-back runs, after one discarded run.
#
# Gate: both medians at most 150 ms and 120 MB. Exit 0 when they are, 1 when either is not,
# 2 on a usage or tooling error. The command's own exit status is recorded per sample and
# never decides the gate: a verdict read exits 3 on "no verdict", and that read costs the same.
#
# Measured with /usr/bin/time (-l on macOS, -v on Linux); never with the shell's `time`,
# which does not report memory. Run it on a quiet box (the server under heavy-run).
set -uo pipefail

GATE_MS=150
GATE_MB=120

usage() {
    echo "usage: $0 <repo-dir> [runs] [-- command...]" >&2
    exit 2
}

[ $# -ge 1 ] || usage
repo=$1
shift
runs=10
if [ $# -ge 1 ] && [ "$1" != "--" ]; then
    runs=$1
    shift
fi
[ "$runs" -ge 10 ] 2>/dev/null || { echo "runs must be a number of at least 10" >&2; exit 2; }
if [ $# -ge 1 ]; then
    [ "$1" = "--" ] || usage
    shift
fi
if [ $# -ge 1 ]; then
    command=("$@")
else
    command=(dotnet fshw verdict)
fi
[ -d "$repo" ] || { echo "no directory: $repo" >&2; exit 2; }

case "$(uname -s)" in
    Darwin) time_flag=-l ;;
    Linux) time_flag=-v ;;
    *) echo "unsupported platform: $(uname -s)" >&2; exit 2 ;;
esac
[ -x /usr/bin/time ] || { echo "/usr/bin/time is required" >&2; exit 2; }

# The files a cold run must read again: the resolved executable and, for a framework-dependent
# tool, its directory (the managed assemblies sit beside the apphost).
binary=$(command -v "${command[0]}" || true)
evict() {
    if command -v vmtouch >/dev/null 2>&1 && [ -n "$binary" ]; then
        vmtouch -qe "$(dirname "$binary")" >/dev/null 2>&1 && return 0
    fi
    if [ "$(uname -s)" = Linux ] && [ -w /proc/sys/vm/drop_caches ]; then
        sync && echo 3 >/proc/sys/vm/drop_caches && return 0
    fi
    return 1
}

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# Milliseconds since the epoch, to the microsecond: /usr/bin/time on macOS reports wall time
# in hundredths of a second, too coarse for a 150 ms gate.
now_ms() { perl -MTime::HiRes=time -e 'printf "%.3f", time * 1000'; }

# One sample: prints "<wall ms> <peak rss kB> <exit>". Wall time is taken around the whole
# measured process; memory is /usr/bin/time's peak resident set.
sample() {
    local out="$tmp/time.out" status start end
    start=$(now_ms)
    (cd "$repo" && /usr/bin/time "$time_flag" "${command[@]}" >/dev/null 2>"$out")
    status=$?
    end=$(now_ms)
    awk -v status="$status" -v os="$(uname -s)" -v ms="$(echo "$end - $start" | bc -l)" '
        os == "Darwin" && /maximum resident set size/  { kb = $1 / 1024 }
        os == "Linux" && /Maximum resident set size/   { kb = $NF }
        END {
            if (ms == "" || kb == "") exit 1
            printf "%d %d %d\n", ms, kb, status
        }' "$out" || { echo "could not read /usr/bin/time output:" >&2; cat "$out" >&2; exit 2; }
}

median() { sort -n | awk '{ a[NR] = $1 } END { if (NR % 2) print a[(NR + 1) / 2]; else print int((a[NR / 2] + a[NR / 2 + 1]) / 2) }'; }
maximum() { sort -n | tail -1; }

echo "E5 verdict cost: ${command[*]}"
echo "repo: $repo   host: $(hostname)   runs per mode: $runs   load: $(uptime | sed 's/.*load average[s]*: //')"

fail=0
measure() {
    local mode=$1 file="$tmp/$1.samples"
    : >"$file"
    for i in $(seq 1 "$runs"); do
        if [ "$mode" != warm ]; then
            sleep 1
            evict || mode=cold-unevicted
        fi
        local line
        line=$(sample) || exit 2
        echo "$line" >>"$file"
        read -r ms kb status <<<"$line"
        printf '  %-15s run %2d: %6d ms %8.1f MB  exit %d\n' "$mode" "$i" "$ms" "$(echo "$kb / 1024" | bc -l)" "$status"
    done
    local med_ms max_ms med_kb max_kb
    med_ms=$(cut -d' ' -f1 "$file" | median)
    max_ms=$(cut -d' ' -f1 "$file" | maximum)
    med_kb=$(cut -d' ' -f2 "$file" | median)
    max_kb=$(cut -d' ' -f2 "$file" | maximum)
    local med_mb max_mb
    med_mb=$(echo "$med_kb / 1024" | bc -l)
    max_mb=$(echo "$max_kb / 1024" | bc -l)
    local verdict=MEETS
    if [ "$med_ms" -gt "$GATE_MS" ] || [ "$(echo "$med_mb > $GATE_MB" | bc -l)" = 1 ]; then
        verdict=MISSES
        fail=1
    fi
    printf '%-15s median %6d ms %8.1f MB   max %6d ms %8.1f MB   gate %d ms / %d MB: %s\n' \
        "$mode" "$med_ms" "$med_mb" "$max_ms" "$max_mb" "$GATE_MS" "$GATE_MB" "$verdict"
}

measure cold
sample >/dev/null || exit 2 # discarded: the warm mode starts warm
measure warm
exit $fail
