#!/usr/bin/env bash
# remote-canary.sh <package>
#
# A fssemantictagger consumer-canary GATE that runs the consumer's real gate on another
# machine. fssemantictagger packs the release candidate into its local feed, makes the
# consumer workspace, rewrites the pin and seeds NuGet's cache on the releaser's machine,
# then runs `gate` through /bin/sh in that workspace; exit 0 is the only pass. With this
# script as the gate, the heavy part moves to a build server:
#
#   1. The plan's candidates (the PackageDownloads of the tagger's seed project for the
#      pinned version) are copied from the local feed, with the workspace's pin change,
#      the seed project and run.json, into a per-run directory on the server
#      (~/.fssemantictagger/remote-canary/<package>-<version>-<UTC stamp>).
#   2. scripts/remote-canary-server.sh, shipped beside them, makes a fresh workspace of the
#      same repository at the same base revision, applies the pin change, seeds the
#      candidates into the server's NuGet cache, runs `prepare` and then `gate` (inside
#      `wrap`, e.g. a box-wide heavy-job queue), runs `teardown`, evicts the candidates
#      again and copies the CTRF receipts of failing test projects to <run>/evidence.
#   3. Its output streams into the tagger's log; the evidence is copied back to
#      ~/.fssemantictagger/logs/remote-canary/<run>/.
#
# The consumer's entry in ~/.fssemantictagger.json carries the remote half:
#
#   "gate": "\"$HOME/<FsHotWatch checkout>/scripts/remote-canary.sh\" FsHotWatch.Cli",
#   "remote": {
#     "host": "<ssh host>",
#     "dotnet": "<the command that is `dotnet` in the server workspace, for the seed>",
#     "prepare": "<optional: run in the workspace before the gate>",
#     "wrap": "<optional: a command prefix the seed, prepare and gate run under>",
#     "gate": "<the gate, exactly as it would run locally>",
#     "teardown": "<optional: run in the workspace after the gate>"
#   }
#
# The repository and workspace must sit at the same path below $HOME on both machines.
# Only the pin of a dotnet tool manifest (.config/dotnet-tools.json) is supported.
#
# Exit: the remote gate's exit code when the server half reported one and ssh agrees; 2
# when something is missing here (nothing reaches the server); 255 when the connection
# was lost; 70 when the server half ended without reporting a gate exit, or reported one
# ssh disagrees with. Every one of those except 0 refuses the release.
set -u -o pipefail

refuse() {
  local code=$1; shift
  echo "remote-canary: REFUSED: $*" >&2
  exit "$code"
}

pkg=${1:-}
[ -n "$pkg" ] || refuse 2 "usage: remote-canary.sh <package>"
here=$(cd "$(dirname "$0")" && pwd)
lower() { printf %s "$1" | tr '[:upper:]' '[:lower:]'; }
pkg_lc=$(lower "$pkg")

config="$HOME/.fssemantictagger.json"
remote=$(jq -c --arg p "$pkg_lc" '[.consumers[]? | select((.package | ascii_downcase) == $p) | .remote | select(. != null)]' "$config" 2>/dev/null) \
  || refuse 2 "cannot read $config"
[ "$(jq length <<<"$remote")" = 1 ] || refuse 2 "$config needs exactly one consumer of $pkg with a \"remote\" object, found $(jq length <<<"$remote")"
remote=$(jq -c '.[0]' <<<"$remote")
host=$(jq -r '.host // empty' <<<"$remote")
[ -n "$host" ] || refuse 2 "the remote of $pkg in $config names no host"
[ -n "$(jq -r '.gate // empty' <<<"$remote")" ] || refuse 2 "the remote of $pkg in $config names no gate"

manifest=.config/dotnet-tools.json
ver=$(jq -r --arg id "$pkg_lc" '.tools[$id].version // empty' "$manifest" 2>/dev/null)
[ -n "$ver" ] || refuse 2 "$PWD/$manifest pins no $pkg_lc"

tmp=${TMPDIR:-/tmp}
seed="${tmp%/}/fssemantictagger-canary-seed/$pkg_lc-$ver/seed.csproj"
[ -f "$seed" ] || refuse 2 "no seed project at $seed: the tagger writes it before the gate, so this is not a tagger run of $pkg $ver"
candidates=$(sed -n 's/.*<PackageDownload Include="\([^"]*\)" Version="\[\([^]]*\)\]".*/\1 \2/p' "$seed")
[ -n "$candidates" ] || refuse 2 "$seed names no PackageDownload"

feed=$(jq -r '.localFeed // empty' "$config")
feed=${feed:-~/.fssemantictagger/feed}
feed=${feed/#\~/$HOME}

# The repository this workspace belongs to: a jj workspace's .jj/repo is a file naming it.
if [ -f .jj/repo ]; then repo_store=$(cat .jj/repo); else repo_store="$PWD/.jj/repo"; fi
# Physical paths throughout: on macOS /tmp and /var are symlinks into /private.
home=$(cd "$HOME" && pwd -P)
ws=$(pwd -P)
# jj writes that path relative to the .jj directory; `cd .jj` first resolves both forms.
repo=$(cd .jj 2>/dev/null && cd "$repo_store/../.." 2>/dev/null && pwd -P) || refuse 2 "cannot find the repository of $ws"
case "$repo/" in "$home"/*) ;; *) refuse 2 "$repo is not below \$HOME, so it has no path on the server" ;; esac
case "$ws/" in "$home"/*) ;; *) refuse 2 "$ws is not below \$HOME, so it has no path on the server" ;; esac

stage=$(mktemp -d "${tmp%/}/remote-canary.XXXXXX") || refuse 2 "cannot make a staging directory"
trap 'rm -rf "$stage"' EXIT
run_id="$pkg_lc-$ver-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$stage/$run_id/feed" "$stage/$run_id/seed"
out="$stage/$run_id"

while read -r id v; do
  [ -f "$feed/$id.$v.nupkg" ] || refuse 2 "candidate $id $v is not in $feed"
  cp "$feed/$id.$v.nupkg" "$out/feed/"
done <<<"$candidates"
cp "$seed" "$out/seed/seed.csproj"
cp "$here/remote-canary-server.sh" "$out/server.sh"

base=$(jj log -r @- --no-graph -T commit_id) && [ -n "$base" ] || refuse 2 "cannot read the workspace's base revision"
changed=$(jj diff --name-only) && [ -n "$changed" ] || refuse 2 "the workspace has no pin change to ship"
printf '%s\n' "$changed" | tar -cf "$out/pin.tar" -T - || refuse 2 "cannot pack the pin change: $changed"

jq -n --arg package "$pkg" --arg version "$ver" --arg base "$base" \
  --arg repo "${repo#"$home"/}" --arg workspace "${ws#"$home"/}" --argjson remote "$remote" \
  '{package: $package, version: $version, base: $base, repo: $repo, workspace: $workspace, remote: $remote}' \
  >"$out/run.json" || refuse 2 "cannot write run.json"

ssh_opts=(-o ConnectTimeout=10 -o BatchMode=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=8)
remote_dir=".fssemantictagger/remote-canary/$run_id"
started=$(date +%s)
echo "remote-canary: $pkg $ver at $base on $host ($remote_dir); candidates: $(tr '\n' ' ' <<<"$candidates")"
ssh "${ssh_opts[@]}" "$host" "mkdir -p .fssemantictagger/remote-canary" </dev/null \
  || refuse 255 "cannot reach $host"
rsync -a -e "ssh ${ssh_opts[*]}" "$out" "$host:.fssemantictagger/remote-canary/" </dev/null \
  || refuse 255 "cannot ship the candidates to $host"

# The server half reads EOF on its standard input as "the releaser is gone" and stops the
# gate. Hold that input open for exactly as long as this script lives: a FIFO whose only
# writer is fd 3 here. The tagger's timeout kills this process tree, ssh closes, the
# server stops the gate and evicts the candidates.
mkfifo "$stage/keepalive"
exec 3<>"$stage/keepalive"
log="$stage/remote.out"
ssh "${ssh_opts[@]}" "$host" "bash $remote_dir/server.sh" <"$stage/keepalive" 3>&- | tee "$log" 3>&-
rc=${PIPESTATUS[0]}
exec 3>&-

evidence="$HOME/.fssemantictagger/logs/remote-canary/$run_id"
mkdir -p "$evidence"
if rsync -a -e "ssh ${ssh_opts[*]}" "$host:$remote_dir/evidence/" "$evidence/" </dev/null; then
  echo "remote-canary: evidence copied to $evidence"
else
  echo "remote-canary: could not copy the evidence; it is on $host at $remote_dir/evidence"
fi

reported=$(sed -n 's/^remote-canary: exit=\([0-9][0-9]*\)$/\1/p' "$log" | tail -1)
echo "remote-canary: wall $(($(date +%s) - started))s on $host, ssh exit $rc, gate exit ${reported:-none reported}"
[ "$rc" != 255 ] || refuse 255 "the connection to $host was lost; the gate's outcome is unknown"
[ -n "$reported" ] || refuse 70 "the server half ended (ssh exit $rc) without reporting a gate exit"
[ "$reported" = "$rc" ] || refuse 70 "the server half reported gate exit $reported but ssh exited $rc"
[ "$rc" = 0 ] || echo "remote-canary: REFUSED: the gate on $host exited $rc" >&2
exit "$rc"
