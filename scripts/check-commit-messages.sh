#!/usr/bin/env bash
# Refuse commit messages that carry private references into this public repository:
# agent-session trailers and links, private-network hostnames, issue-tracker URLs.
#
# Three ways to feed it:
#   (stdin)              NUL-separated records, each a commit id on its first line and
#                        the message after it — what `git log -z --format='%H%n%B'` and
#                        `jj log --no-graph -T 'commit_id ++ "\n" ++ description ++ "\0"'`
#                        print.
#   --jj-revset <revset> read those records from jj itself. `mise run ci` passes
#                        `main@origin..@`: the commits a push would publish.
#   --git-base <sha>     read the commits after <sha> up to HEAD from git itself. GitHub
#                        CI passes the push's `before` (or a pull request's base). An
#                        empty, all-zero or unknown base checks HEAD's commits that are not
#                        on origin/main instead, and with no origin/main either it exits 2
#                        rather than read all of history. Outside a git checkout (ci-linux runs the
#                        jobs on a tarball with no history) it says SKIPPED and exits 0;
#                        the jj half of `mise run ci` covers that machine.
#
# --forbid-ticket-ids also refuses bare internal ticket ids (AUTOMATION-12, NEWS-3,
# APPLIC-4). Off by default: a bare id in a commit title is existing practice here.
#
# Exit 0 when every message is clean, 1 when any is not (each offending commit and line
# is printed), 2 on a usage error. CommitMessageGuardTests drives every rule with a
# planted leak, so a green here means the rules matched nothing, not that they could not.
set -u -o pipefail

rules=(
  'agent session trailer|^[[:space:]]*Claude-Session:'
  'agent session link|claude\.ai/(code/)?(session|chat)'
  'private-network hostname|[a-z0-9-]+\.ts\.net'
  'issue-tracker URL|plane\.so/'
)

mode=stdin
git_base=""
jj_revset=""
while [ $# -gt 0 ]; do
  case "$1" in
    --forbid-ticket-ids) rules+=('internal ticket id|(^|[^A-Za-z0-9])(AUTOMATION|NEWS|APPLIC)-[0-9]+') ;;
    --git-base)
      [ $# -ge 2 ] || { echo "check-commit-messages: --git-base needs a value (it may be empty)" >&2; exit 2; }
      mode=git; git_base="$2"; shift ;;
    --jj-revset)
      [ $# -ge 2 ] && [ -n "$2" ] || { echo "check-commit-messages: --jj-revset needs a revset" >&2; exit 2; }
      mode=jj; jj_revset="$2"; shift ;;
    *) echo "check-commit-messages: unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

check_records() {
  local checked=0 bad=0 record sha message rule name pattern hits hit noun
  while IFS= read -r -d '' record || [ -n "$record" ]; do
    sha=${record%%$'\n'*}
    if [ "$sha" = "$record" ]; then message=""; else message=${record#*$'\n'}; fi
    checked=$((checked + 1))
    for rule in "${rules[@]}"; do
      name=${rule%%|*}
      pattern=${rule#*|}
      hits=$(printf '%s\n' "$message" | grep -inE -- "$pattern" || true)
      if [ -n "$hits" ]; then
        bad=$((bad + 1))
        while IFS= read -r hit; do
          echo "commit $sha: $name: line $hit"
        done <<< "$hits"
      fi
    done
  done

  if [ "$bad" -gt 0 ]; then
    echo "check-commit-messages: $bad private reference(s) in $checked commit(s). Reword the commit message (jj describe) before pushing."
    return 1
  fi
  if [ "$checked" -eq 1 ]; then noun=commit; else noun=commits; fi
  echo "check-commit-messages: $checked $noun clean."
}

case "$mode" in
  stdin) check_records; exit $? ;;
  jj)
    # pipefail: a jj that fails (unknown revset, no repository) must not read as
    # "0 commits clean".
    jj log --no-graph -r "$jj_revset" -T 'commit_id ++ "\n" ++ description ++ "\0"' | check_records
    exit $? ;;
esac

if ! git rev-parse --is-inside-work-tree > /dev/null 2>&1; then
  echo "check-commit-messages: SKIPPED: not a git checkout, so there is no history to read."
  exit 0
fi

if [ -n "$git_base" ] && [ "$git_base" != 0000000000000000000000000000000000000000 ] \
  && git cat-file -e "${git_base}^{commit}" 2> /dev/null; then
  range=("$git_base..HEAD")
elif git rev-parse --verify --quiet refs/remotes/origin/main > /dev/null; then
  # Not `--remotes=origin/main`: without a glob character git appends `/*` to that
  # pattern, matches nothing, and the walk becomes all of history.
  range=(HEAD --not refs/remotes/origin/main)
else
  echo "check-commit-messages: no usable --git-base and no origin/main to compare against; refusing to check all of history." >&2
  exit 2
fi

git log -z --format='%H%n%B' "${range[@]}" | check_records
