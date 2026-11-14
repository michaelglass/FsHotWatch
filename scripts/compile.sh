#!/usr/bin/env bash
# compile: the solution build with the published reference assemblies settled around it
# (AUTOMATION-1700), by the SAME code the daemon's build plugin settles them with.
#
#   scripts/compile.sh [build command...]      (default: dotnet build)
#
# MSBuild copies a project's `obj/<cfg>/<tfm>/refint/X.dll` to `ref/X.dll`, the file every
# dependent compiles against, only when the MVID moved. The F# compiler keeps the MVID when a
# type MOVES between assemblies with its API unchanged, so `ref` keeps signature data naming
# the old assembly and every dependent fails with FS0193 until the copy is made. The daemon's
# build plugin makes it (`RefAssemblyFreshness`, `BuildPlugin.settleRefAssemblies`); `land-check`
# and `ci` run this task before the daemon does.
#
# What is stale and how it is repaired are decided ONLY by `RefAssemblyFreshness`, which
# scripts/settle-refs.fsx loads from source (no tool build needed). This script decides only
# when to ask: once before the build (one an earlier build left), then after each build, and
# while a build left any stale it builds again, at most ROUNDS builds. The exit code is the
# last build's. A build still leaving stale references after ROUNDS, or a reference that could
# not be copied, is a red with a message, never a pass.
set -uo pipefail

ROUNDS=3
settle_script="$(cd "$(dirname "$0")" && pwd)/settle-refs.fsx"

if [ $# -gt 0 ]; then
    build=("$@")
else
    build=(dotnet build)
fi

# Ask RefAssemblyFreshness; 0 nothing was stale, 10 some were and are copied. Anything else
# (a reference that could not be copied, no solution to read) ends the compile red.
settle() {
    local copied code
    copied=$(dotnet fsi "$settle_script" "$PWD")
    code=$?
    case $code in
        0) return 0 ;;
        10)
            echo "compile: these reference assemblies were not what the compiler last wrote; copied them, so dependents compile against the current API (AUTOMATION-1700):"
            printf '  %s\n' $copied
            return 10
            ;;
        *)
            echo "compile: could not settle the reference assemblies (settle-refs exit $code); refusing the build" >&2
            exit 1
            ;;
    esac
}

settle

for round in $(seq 1 "$ROUNDS"); do
    "${build[@]}"
    status=$?
    if settle; then
        exit "$status"
    fi
    if [ "$round" -lt "$ROUNDS" ]; then
        echo "compile: building again (round $((round + 1)) of $ROUNDS)"
    fi
done

echo "compile: reference assemblies still stale after $ROUNDS builds; refusing the build" >&2
exit 1
