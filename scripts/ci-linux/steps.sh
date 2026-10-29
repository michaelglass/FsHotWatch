#!/usr/bin/env bash
# steps.sh — the steps of this repository's GitHub Actions CI, derived from
# .github/workflows/ci.yml, either PRINTED (`--plan`) or RUN inside the ci-linux container.
#
#   steps.sh --plan <ci.yml> <plan-file>       write every job's steps; needs no dotnet
#   steps.sh --input <ci.yml> <key>            print one input of the `build` job
#   steps.sh --run <ci.yml> <tree.tar> <name>  (container only) run the jobs
#
# WHY THIS DERIVES INSTEAD OF RESTATING. The point of ci-linux is that a local green and a
# GitHub green are the same claim. A script that re-typed the CI commands would drift from
# ci.yml the first time somebody edited one of them, and nothing would notice. So the
# repository-specific half — `lint-cmd`, `test-extra-args`, `fantomas-paths`, the local
# `integration` job's commands — is READ from ci.yml every run. The half that lives in the
# shared reusable workflows (michaelglass/MichaelsWackyFsPackageTools) is transcribed
# below, and ci-linux.sh refuses to run when either upstream file's blob differs from the
# one this transcription was made from (KNOWN_BUILD_BLOB / KNOWN_LINT_PROJECT_BLOB there).
#
# Anything in ci.yml this script does not model — an unknown `with:` input, a step that
# `uses:` an action other than checkout/setup-dotnet/cache, a multi-line `run: |` — is a
# hard error naming it, never a silently skipped step.
#
# FsHotWatch and TestPrune carry byte-identical copies of scripts/ci-linux/.
set -euo pipefail

BUILD_WORKFLOW='michaelglass/MichaelsWackyFsPackageTools/.github/workflows/michaels-wacky-build.yml@main'
LINT_PROJECT_WORKFLOW='michaelglass/MichaelsWackyFsPackageTools/.github/workflows/michaels-wacky-lint-project.yml@main'

die() { echo "ci-linux: $*" >&2; exit 2; }

# The lines of top-level job $2 in workflow $1, from its header to the next job header.
job_block() {
    awk -v header="  $2:" '
        $0 == header { inside = 1; next }
        inside && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { exit }
        inside { print }' "$1"
}

# Every top-level job name, in file order.
job_names() {
    awk '/^jobs:[[:space:]]*$/ { inside = 1; next }
         inside && /^[^[:space:]#]/ { exit }
         inside && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { sub(/^  /, ""); sub(/:.*/, ""); print }' "$1"
}

# A YAML scalar as GitHub reads it: '...' and "..." unquoted, a bare value without its comment.
unquote() {
    local v="$1"
    case "$v" in
        \'*) v="${v#\'}"; v="${v%\'*}"; v="${v//\'\'/\'}" ;;
        \"*) v="${v#\"}"; v="${v%\"*}" ;;
        *) v="${v%%[[:space:]]#*}" ;;
    esac
    printf '%s' "$v" | sed -E 's/[[:space:]]+$//'
}

# The `with:` inputs the `build` job passes the reusable workflow, as KEY<TAB>VALUE lines.
build_inputs() {
    job_block "$1" build | awk '
        /^    with:[[:space:]]*$/ { inside = 1; next }
        inside && /^    [^[:space:]]/ { exit }
        inside && /^      [A-Za-z0-9_-]+:/ {
            line = $0; sub(/^      /, "", line)
            key = line; sub(/:.*/, "", key)
            value = line; sub(/^[^:]*:[[:space:]]*/, "", value)
            printf "%s\t%s\n", key, value
        }'
}

input() {
    local yml="$1" key="$2" default="${3-}" line
    line="$(build_inputs "$yml" | awk -F '\t' -v k="$key" '$1 == k { print $2; exit }')"
    if [ -z "$line" ]; then printf '%s' "$default"; else unquote "$line"; fi
}

# Fail on anything in ci.yml that this transcription does not model.
validate() {
    local yml="$1" job uses key value
    [ -f "$yml" ] || die "no workflow at $yml"
    while IFS=$'\t' read -r key value; do
        case "$key" in
            solution|dotnet-version|fantomas-paths|example-projects|check-docs|syncdocs-cmd|\
            coverageratchet-cmd|coverage-exclude|paket-restore|pre-build-cmd|test-extra-args|lint-cmd) ;;
            # These shape GitHub-only behaviour: extra OS jobs and artifact upload.
            test-windows|test-macos|failure-artifacts) continue ;;
            *) die "the build job passes input '$key', which ci-linux does not model" ;;
        esac
        case "$value" in
            '|'*|'>'*) die "input '$key' is a multi-line scalar, which ci-linux does not model" ;;
        esac
    done < <(build_inputs "$yml")
    for job in $(job_names "$yml"); do
        uses="$(job_block "$yml" "$job" | sed -nE 's/^    uses:[[:space:]]*(.*)$/\1/p')"
        case "$uses" in
            "$BUILD_WORKFLOW") [ "$job" = build ] || die "job '$job' calls the build workflow; ci-linux expects it as 'build'" ;;
            "$LINT_PROJECT_WORKFLOW") ;;
            "") local_job_runs "$yml" "$job" > /dev/null ;;
            *) die "job '$job' calls $uses, which ci-linux does not model" ;;
        esac
    done
}

# The `run:` commands of a job defined in ci.yml itself, one per line, after checking that
# every `uses:` step is one ci-linux emulates and that the job runs on ubuntu-latest.
local_job_runs() {
    local yml="$1" job="$2" block runs_on action
    block="$(job_block "$yml" "$job")"
    runs_on="$(printf '%s\n' "$block" | sed -nE 's/^    runs-on:[[:space:]]*(.*)$/\1/p')"
    [ "$runs_on" = ubuntu-latest ] || die "job '$job' runs on '$runs_on'; ci-linux models ubuntu-latest only"
    while read -r action; do
        case "$action" in
            actions/checkout@*|actions/setup-dotnet@*|actions/cache@*) ;;
            *) die "job '$job' uses $action, which ci-linux does not model" ;;
        esac
    done < <(printf '%s\n' "$block" | sed -nE 's/^[[:space:]]*(- )?uses:[[:space:]]*([^[:space:]]+).*$/\2/p')
    printf '%s\n' "$block" | sed -nE 's/^[[:space:]]*(- )?run:[[:space:]]*(.*)$/\2/p' | while IFS= read -r run; do
        case "$run" in
            '|'*|'>'*) die "job '$job' has a multi-line run step, which ci-linux does not model" ;;
        esac
        printf '%s\n' "$run"
    done
}

# ---------------------------------------------------------------------------------------
# The plan: JOB, STEP NAME and COMMAND, appended to the arrays below. Each command runs as
# GitHub runs a `run:` step: `bash --noprofile --norc -eo pipefail`, from the checkout root.
# ---------------------------------------------------------------------------------------
JOBS=(); NAMES=(); CMDS=(); TIMEOUTS=()
# step JOB NAME COMMAND [TIMEOUT-SECONDS] — the timeout mirrors a step's `timeout-minutes`.
step() { JOBS+=("$1"); NAMES+=("$2"); CMDS+=("$3"); TIMEOUTS+=("${4:-0}"); }

# michaels-wacky-build.yml, job `build`, transcribed with its inputs substituted. Steps that
# only publish (ReportGenerator HTML, PR comment, artifact uploads) are omitted: none of
# them can turn the job red on a green tree. `test-os` is skipped: it runs only for
# test-windows/test-macos, which are not Linux.
plan_build() {
    local yml="$1" ws="$2"
    local solution dotnet_version fantomas_paths example_projects check_docs syncdocs_cmd
    local coverageratchet_cmd coverage_exclude paket_restore pre_build_cmd test_extra_args lint_cmd
    solution="$(input "$yml" solution)"
    [ -n "$solution" ] || die "the build job passes no 'solution'"
    fantomas_paths="$(input "$yml" fantomas-paths 'src/ tests/')"
    example_projects="$(input "$yml" example-projects '')"
    check_docs="$(input "$yml" check-docs true)"
    syncdocs_cmd="$(input "$yml" syncdocs-cmd 'dotnet tool run syncdocs')"
    coverageratchet_cmd="$(input "$yml" coverageratchet-cmd 'dotnet tool run coverageratchet')"
    coverage_exclude="$(input "$yml" coverage-exclude '')"
    paket_restore="$(input "$yml" paket-restore false)"
    pre_build_cmd="$(input "$yml" pre-build-cmd '')"
    test_extra_args="$(input "$yml" test-extra-args '')"
    lint_cmd="$(input "$yml" lint-cmd '')"
    [ -n "$lint_cmd" ] || lint_cmd="dotnet fsharplint lint $solution"

    step build "dotnet tool restore" "dotnet tool restore"
    [ "$paket_restore" = true ] && step build "Paket restore" "dotnet tool run paket restore"
    step build "dotnet restore" "dotnet restore"
    [ -n "$pre_build_cmd" ] && step build "Pre-build" "$pre_build_cmd"
    step build "Format check" "dotnet fantomas $fantomas_paths --check"
    step build "Sync docs check" "$syncdocs_cmd check"
    step build "Build" "dotnet build --warnaserror --no-restore"
    step build "Build (Release)" "dotnet build -c Release --no-restore -p:ReleaseBuild=true"
    step build "Lint" "$lint_cmd" 900
    [ "$check_docs" = true ] && step build "Generate docs" "dotnet fsdocs build --output output/docs --clean"
    step build "Test with coverage" "exclude=\"$coverage_exclude\"
for proj in \$(find tests -name '*.Tests.fsproj' | sort); do
  project_name=\$(basename \"\$(dirname \"\$proj\")\" | sed 's/\\.Tests\$//')
  skip=0
  for ex in \$exclude; do
    [ \"\$ex\" = \"\$project_name\" ] && skip=1
  done
  if [ \"\$skip\" = 1 ]; then
    echo \"Testing \$proj (coverage SKIPPED — '\$project_name' in coverage-exclude)\"
    dotnet test --project \"\$proj\" --no-build --no-restore $test_extra_args
    continue
  fi
  echo \"Testing \$proj (coverage -> coverage/\$project_name/)\"
  dotnet test --project \"\$proj\" --no-build --no-restore --coverage --coverage-output-format cobertura --coverage-output \"$ws/coverage/\$project_name/coverage.cobertura.xml\" $test_extra_args
done"
    step build "Coverage summary" 'shopt -s nullglob
files=(coverage/*/coverage.cobertura.xml)
if [ ${#files[@]} -eq 0 ]; then echo "No coverage files were produced."; exit 0; fi
total_covered=0; total_valid=0; empty_found=0
for f in "${files[@]}"; do
  pkg=$(basename "$(dirname "$f")")
  classes=$(grep -c "<class " "$f" || true)
  covered=$(grep -oE "lines-covered=\"[0-9]+\"" "$f" | head -1 | grep -oE "[0-9]+" || true)
  valid=$(grep -oE "lines-valid=\"[0-9]+\"" "$f" | head -1 | grep -oE "[0-9]+" || true)
  covered=${covered:-0}; valid=${valid:-0}
  if [ "${classes:-0}" -eq 0 ] || [ "$valid" -eq 0 ]; then
    echo "error: empty coverage for $pkg: tests instrumented no product code"; empty_found=1; continue
  fi
  echo "coverage $pkg $(awk "BEGIN { printf \"%.1f\", (100*$covered/$valid) }")% ($covered/$valid lines)"
  total_covered=$((total_covered + covered)); total_valid=$((total_valid + valid))
done
[ "$total_valid" -gt 0 ] && echo "coverage overall $(awk "BEGIN { printf \"%.1f\", (100*$total_covered/$total_valid) }")% ($total_covered/$total_valid lines)"
[ "$empty_found" -eq 0 ]'
    step build "Coverage ratchet check" "for config in coverage-ratchet-*.json; do
  [ -f \"\$config\" ] || continue
  project=\$(echo \"\$config\" | sed 's/coverage-ratchet-\\(.*\\)\\.json/\\1/')
  echo \"Checking \$project...\"
  (cd \"coverage/\$project\" && $coverageratchet_cmd check \"$ws/\$config\")
done"
    step build "Build examples" "if [ \"$example_projects\" = none ]; then echo 'Skipping examples (explicitly disabled)'; exit 0; fi
if [ -n \"$example_projects\" ]; then projects=\"$example_projects\"; else projects=\$(find examples -name '*.fsproj' -o -name '*.csproj' 2>/dev/null | sort); fi
if [ -z \"\$projects\" ]; then echo 'No example projects found'; exit 0; fi
for proj in \$projects; do echo \"Building example: \$proj\"; dotnet build \"\$proj\" --warnaserror; done"
}

plan_all() {
    local yml="$1" ws="$2" job uses run
    validate "$yml"
    for job in $(job_names "$yml"); do
        uses="$(job_block "$yml" "$job" | sed -nE 's/^    uses:[[:space:]]*(.*)$/\1/p')"
        case "$uses" in
            "$BUILD_WORKFLOW") plan_build "$yml" "$ws" ;;
            # michaels-wacky-lint-project.yml, transcribed.
            "$LINT_PROJECT_WORKFLOW")
                step "$job" "dotnet tool restore" "dotnet tool restore"
                step "$job" "Lint project" "dotnet tool run fsprojlint" ;;
            "")
                while IFS= read -r run; do step "$job" "$run" "$run"; done < <(local_job_runs "$yml" "$job") ;;
        esac
    done
}

write_plan() {
    local i
    for i in "${!CMDS[@]}"; do
        printf '## %s / %s\n%s\n\n' "${JOBS[$i]}" "${NAMES[$i]}" "${CMDS[$i]}"
    done
}

# ---------------------------------------------------------------------------------------
# Run (inside the container). Every job starts from a FRESH extraction of the tree, as each
# GitHub job starts from its own checkout; the NuGet cache is shared, as actions/cache
# shares it. Jobs run one after another (GitHub runs them in parallel on separate hosts),
# and all of them run even when one fails, so one ci-linux run reports every red job.
# CI_LINUX_JOBS (space-separated job names) restricts the run to those jobs.
# ---------------------------------------------------------------------------------------
checkout() {
    local tar="$1" ws="$2"
    rm -rf "$ws"; mkdir -p "$ws"
    tar -xf "$tar" -C "$ws"
    # actions/checkout leaves a git repository at the commit; some steps and tests call git.
    git -C "$ws" init -q
    git -C "$ws" add -A
    git -C "$ws" -c user.name=ci-linux -c user.email=ci-linux@localhost commit -qm ci-linux --no-verify
    # actions/checkout also leaves `origin` pointing at the GitHub repository; SourceLink fails
    # the build ("has no remote") without one.
    git -C "$ws" remote add origin "${CI_LINUX_REMOTE:-https://github.com/michaelglass/$(basename "$ws").git}"
}

collect() {
    local ws="$1" job="$2" dest="/ci-out/$2"
    mkdir -p "$dest"
    [ -d "$ws/coverage" ] && cp -R "$ws/coverage" "$dest/" 2>/dev/null || true
    [ -d "$ws/.fshw/test-runs" ] && mkdir -p "$dest/fshw" && cp -R "$ws/.fshw/test-runs" "$dest/fshw/" 2>/dev/null || true
    if [ -d "$ws/.fshw" ]; then
        (cd "$ws/.fshw" && find . -name '*.log' -print0 | while IFS= read -r -d '' f; do
            mkdir -p "$dest/fshw/$(dirname "$f")"; cp "$f" "$dest/fshw/$f"; done) || true
    fi
}

run_all() {
    local yml_rel="$1" tar="$2" name="$3"
    local ws="/home/runner/work/$name/$name" rc=0 i job="" job_rc=0 started step_rc t0 t1 n=0
    local -a results=()
    export GITHUB_WORKSPACE="$ws"
    checkout "$tar" "$ws"
    plan_all "$ws/$yml_rel" "$ws"
    write_plan > /ci-out/plan.txt
    local want=" ${CI_LINUX_JOBS:-} "
    started=$(date +%s)
    for i in "${!CMDS[@]}"; do
        if [ -n "${CI_LINUX_JOBS:-}" ] && [[ "$want" != *" ${JOBS[$i]} "* ]]; then continue; fi
        if [ "${JOBS[$i]}" != "$job" ]; then
            if [ -n "$job" ]; then collect "$ws" "$job"; results+=("$job=$job_rc"); fi
            job="${JOBS[$i]}"; job_rc=0
            echo "::: ci-linux job $job (fresh checkout)"
            checkout "$tar" "$ws"
        fi
        # GitHub stops a job at its first failing step.
        [ "$job_rc" -eq 0 ] || continue
        n=$((n + 1))
        echo "::: ci-linux $job / ${NAMES[$i]}"
        t0=$(date +%s)
        set +e
        (cd "$ws" && timeout "${TIMEOUTS[$i]}" bash --noprofile --norc -eo pipefail -c "${CMDS[$i]}") 2>&1 \
            | tee "/ci-out/step-$(printf '%02d' "$n")-$job.log"
        step_rc=${PIPESTATUS[0]}
        set -e
        t1=$(date +%s)
        echo "::: ci-linux $job / ${NAMES[$i]}: exit=$step_rc ($((t1 - t0))s)"
        echo "$job	${NAMES[$i]}	exit=$step_rc	$((t1 - t0))s" >> /ci-out/steps.tsv
        if [ "$step_rc" -ne 0 ]; then job_rc=$step_rc; [ "$rc" -ne 0 ] || rc=$step_rc; fi
    done
    if [ -n "$job" ]; then collect "$ws" "$job"; results+=("$job=$job_rc"); fi
    echo "::: ci-linux jobs: ${results[*]:-none}  total $(( $(date +%s) - started ))s  exit=$rc"
    return "$rc"
}

case "${1:-}" in
    --plan)
        [ $# -eq 3 ] || die "usage: steps.sh --plan <ci.yml> <plan-file>"
        plan_all "$2" '${{ github.workspace }}'
        write_plan > "$3" ;;
    --input)
        [ $# -eq 3 ] || die "usage: steps.sh --input <ci.yml> <key>"
        validate "$2"
        input "$2" "$3" ;;
    --run)
        [ $# -eq 4 ] || die "usage: steps.sh --run <ci.yml path in the tree> <tree.tar> <repo name>"
        run_all "$2" "$3" "$4" ;;
    *) die "usage: steps.sh --plan <ci.yml> <plan-file> | --input <ci.yml> <key> | --run <ci.yml> <tree.tar> <name>" ;;
esac
