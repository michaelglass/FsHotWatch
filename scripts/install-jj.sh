#!/usr/bin/env bash
# Install jj (Jujutsu) on a Linux x86_64 CI runner: the build job (ci.yml, through the
# reusable workflow's `pre-build-cmd`) and the nightly replay. RepositoryIdentityTests'
# real-jj case needs it, and on CI a missing tool fails that test rather than skipping it
# (AUTOMATION-1025). One pinned version for every workflow; the release is public, so no
# token is needed.
set -euo pipefail

JJ_VERSION="v0.46.0"
tarball="jj-${JJ_VERSION}-x86_64-unknown-linux-musl.tar.gz"
work="$(mktemp -d)"

curl -fsSL --retry 3 -o "${work}/${tarball}" \
  "https://github.com/jj-vcs/jj/releases/download/${JJ_VERSION}/${tarball}"
tar -xzf "${work}/${tarball}" -C "${work}"

if [ "$(id -u)" = "0" ]; then
  install -m 0755 "${work}/jj" /usr/local/bin/jj
else
  sudo install -m 0755 "${work}/jj" /usr/local/bin/jj
fi

jj --version
