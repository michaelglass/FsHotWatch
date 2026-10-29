#!/usr/bin/env bash
# ci-linux.sh [TREE] — run this repository's GitHub Actions CI, the Linux jobs, in a local
# Linux container, and exit with its status. `mise run ci-linux` is the usual entry point.
#
# Why it exists: every FsHotWatch CI red inspected from 2026-10-05 to 10-28 (14 runs) passed
# on macOS and failed only on GitHub's Linux runner — coverage floors, a real-process test,
# wall-clock and cancellation tests — and all four reverts of that period were Linux-only
# failures (AUTOMATION-1363). macOS `mise run ci` cannot see that class. This can, before
# the push instead of after it.
#
# TREE defaults to this checkout. It may be any checkout of the repository at any revision
# (a jj workspace or a git worktree), including one older than this script: the steps are
# derived from TREE's own .github/workflows/ci.yml by scripts/ci-linux/steps.sh from THIS
# checkout. What is shipped into the container is the tracked file set, as a checkout
# would see it — never bin/, obj/ or .fshw/ from the host.
#
# Environment:
#   CI_LINUX_JOBS      space-separated job names to run (default: every job in ci.yml)
#   CI_LINUX_PLATFORM  container platform (default: the host's, e.g. linux/arm64). GitHub's
#                      runner is linux/amd64; linux/amd64 here runs emulated and is slower.
#   CI_LINUX_CPUS      CPUs for the container (default 4, the hosted runner's count)
#   CI_LINUX_MEMORY    memory limit (default 14g, capped to what the Docker VM has)
#   CI_LINUX_OUT       where logs, coverage and fshw test runs land (default TREE/.ci-linux)
#   CI_LINUX_ACCEPT_WORKFLOW_DRIFT=1  run although an upstream reusable workflow changed
#
# Exit status: the first failing step's exit code, 0 when every job is green, 2 when
# ci-linux itself cannot run (no container runtime, unmodelled workflow, drift).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tree="$(cd "${1:-$here/..}" && pwd)"
steps="$here/ci-linux/steps.sh"
yml_rel=".github/workflows/ci.yml"
yml="$tree/$yml_rel"

die() { echo "ci-linux: $*" >&2; exit 2; }

# The reusable workflows steps.sh transcribes, as the git blob ids they were transcribed
# from. When upstream changes, re-read it, update steps.sh to match, then update these.
KNOWN_BUILD_BLOB=d5f281cc70621fdc19ceafbce81dd81b506c8fcd
KNOWN_LINT_PROJECT_BLOB=8e0d3f6edc108bb2fa6076044f65fbefd09069fa

check_upstream() {
    local file="$1" known="$2" current
    if ! command -v gh > /dev/null; then
        echo "ci-linux: UNVERIFIED — gh is not installed, so $file was not compared with the transcription" >&2
        return 0
    fi
    if ! current="$(gh api "repos/michaelglass/MichaelsWackyFsPackageTools/contents/.github/workflows/$file" --jq .sha 2> /dev/null)"; then
        echo "ci-linux: UNVERIFIED — could not fetch $file (offline or not authenticated), so it was not compared with the transcription" >&2
        return 0
    fi
    if [ "$current" != "$known" ]; then
        if [ "${CI_LINUX_ACCEPT_WORKFLOW_DRIFT:-}" = 1 ]; then
            echo "ci-linux: WARNING — $file changed upstream ($known -> $current); running anyway (CI_LINUX_ACCEPT_WORKFLOW_DRIFT=1)" >&2
        else
            die "$file changed upstream ($known -> $current). steps.sh transcribes it, so this run would not be the run GitHub makes. Update scripts/ci-linux/steps.sh to match, then KNOWN_*_BLOB in scripts/ci-linux.sh."
        fi
    fi
}

[ -f "$yml" ] || die "no $yml_rel in $tree"
command -v docker > /dev/null || die "docker is not installed. On macOS: Docker Desktop, OrbStack or colima all provide it."
docker info > /dev/null 2>&1 || die "the docker daemon is not running (start Docker Desktop / colima and retry)"

check_upstream michaels-wacky-build.yml "$KNOWN_BUILD_BLOB"
check_upstream michaels-wacky-lint-project.yml "$KNOWN_LINT_PROJECT_BLOB"

channel="$(bash "$steps" --input "$yml" dotnet-version)"
solution="$(bash "$steps" --input "$yml" solution)"
name="${solution%.*}"
[ -n "$channel" ] || die "the build job names no dotnet-version"

case "$(uname -m)" in
    arm64|aarch64) host_platform=linux/arm64 ;;
    *) host_platform=linux/amd64 ;;
esac
platform="${CI_LINUX_PLATFORM:-$host_platform}"
arch="${platform#linux/}"
image="ci-linux-dotnet:${channel}-${arch}"
out="${CI_LINUX_OUT:-$tree/.ci-linux}"

vm_bytes="$(docker info --format '{{.MemTotal}}')"
memory="${CI_LINUX_MEMORY:-14g}"
if [ -z "${CI_LINUX_MEMORY:-}" ] && [ "$vm_bytes" -lt $((14 * 1024 * 1024 * 1024)) ]; then
    memory="$((vm_bytes / 1024 / 1024 - 512))m"
    echo "ci-linux: the Docker VM has $((vm_bytes / 1024 / 1024)) MiB; the hosted runner has 16 GiB. Using $memory." >&2
fi

echo "ci-linux: image $image (dotnet channel $channel, $platform)"
docker build --quiet --platform "$platform" \
    --build-arg DOTNET_CHANNEL="$channel" \
    --build-arg CACHE_WEEK="$(date +%G-W%V)" \
    -t "$image" "$here/ci-linux" > /dev/null

# The tracked file set — what actions/checkout would materialize.
list="$(mktemp)"
trap 'rm -f "$list"' EXIT
if [ -d "$tree/.jj" ]; then
    (cd "$tree" && jj file list) > "$list"
elif git -C "$tree" rev-parse --show-toplevel > /dev/null 2>&1; then
    git -C "$tree" ls-files > "$list"
else
    die "$tree is neither a jj nor a git checkout"
fi

rm -rf "$out"
mkdir -p "$out"
echo "ci-linux: $(wc -l < "$list" | tr -d ' ') tracked files from $tree; logs -> $out"

# The `origin` URL actions/checkout would leave behind (SourceLink reads it).
remote="$( (cd "$tree" && jj git remote list 2> /dev/null || git remote -v 2> /dev/null) | awk '$1 == "origin" { print $2; exit }')"

start=$(date +%s)
set +e
tar -C "$tree" -cf - -T "$list" \
    | docker run --rm -i \
        --platform "$platform" \
        --cpus "${CI_LINUX_CPUS:-4}" \
        --memory "$memory" \
        -e CI=true -e GITHUB_ACTIONS=true -e RUNNER_OS=Linux \
        -e CI_LINUX_JOBS="${CI_LINUX_JOBS:-}" \
        -e CI_LINUX_REMOTE="$remote" \
        -v "ci-linux-nuget-$arch:/home/runner/.nuget/packages" \
        -v "$here/ci-linux:/ci-linux:ro" \
        -v "$out:/ci-out" \
        "$image" \
        bash -c 'cat > /tmp/tree.tar && exec bash /ci-linux/steps.sh --run "$0" /tmp/tree.tar "$1"' "$yml_rel" "$name"
rc=$?
set -e
echo "ci-linux: exit=$rc after $(( $(date +%s) - start ))s; steps: $out/steps.tsv"
exit "$rc"
