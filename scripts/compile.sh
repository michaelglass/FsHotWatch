#!/usr/bin/env bash
# compile: the solution build with the published reference assemblies settled around it
# (AUTOMATION-1700), as the daemon's build plugin settles them (RefAssemblyFreshness).
#
#   scripts/compile.sh [build command...]      (default: dotnet build)
#
# MSBuild copies a project's `obj/<cfg>/<tfm>/refint/X.dll` to `ref/X.dll`, the file every
# dependent compiles against, only when the MVID moved. The F# compiler keeps the MVID when a
# type MOVES between assemblies with its API unchanged, so `ref` keeps signature data naming
# the old assembly and every dependent fails with FS0193 until the copy is made. A raw
# `dotnet build` never makes it; `land-check` and `ci` run this script before the daemon does.
#
# So: copy every `ref` whose bytes differ from its `refint` (one an earlier build left), build,
# and while the build left any stale, copy them and build again, at most ROUNDS builds. The
# exit code is the last build's; a build still leaving stale references after ROUNDS fails.
# The copy keeps the intermediate's timestamp, newer than every dependent's output, so the
# next build recompiles the dependents.
set -uo pipefail

ROUNDS=3

if [ $# -gt 0 ]; then
    build=("$@")
else
    build=(dotnet build)
fi

# Copy each stale published reference assembly; print each one copied.
settle() {
    local intermediate published
    # Not into checkouts nested in this one, version control or the dev environment's links.
    while IFS= read -r intermediate; do
        published="$(dirname "$(dirname "$intermediate")")/ref/$(basename "$intermediate")"
        if [ -f "$published" ] && ! cmp -s "$intermediate" "$published"; then
            cp -p "$intermediate" "$published"
            echo "${published#./}"
        fi
    done < <(find . \( -name .git -o -name .jj -o -name .workspaces -o -name .devenv -o -name node_modules \) -prune \
        -o -path '*/obj/*/refint/*.dll' -type f -print)
}

report() {
    local copied
    copied=$1
    [ -n "$copied" ] || return 0
    echo "compile: these reference assemblies were not what the compiler last wrote; copied them, so dependents compile against the current API (AUTOMATION-1700):"
    printf '  %s\n' $copied
}

report "$(settle)"

for round in $(seq 1 "$ROUNDS"); do
    "${build[@]}"
    status=$?
    copied=$(settle)
    if [ -z "$copied" ]; then
        exit "$status"
    fi
    report "$copied"
    if [ "$round" -lt "$ROUNDS" ]; then
        echo "compile: building again (round $((round + 1)) of $ROUNDS)"
    fi
done

echo "compile: reference assemblies still stale after $ROUNDS builds; refusing the build" >&2
exit 1
