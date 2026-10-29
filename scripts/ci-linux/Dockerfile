# The image `scripts/ci-linux.sh` runs this repository's GitHub Actions Linux CI in.
#
# It stands in for `runs-on: ubuntu-latest` + `actions/setup-dotnet`: the same Ubuntu
# release as the hosted runner, the SDK installed the way setup-dotnet installs it (the
# official dotnet-install script, resolving the workflow's `dotnet-version` channel to its
# latest patch), and a non-root `runner` user with the hosted runner's uid. It is NOT the
# runner image itself (that is tens of GB of preinstalled tools); it carries what these
# workflows' steps call. A step that needs a tool missing here fails loudly with
# "command not found", and the fix is a package line below, not a workaround in the step.
FROM ubuntu:24.04

ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      bash ca-certificates curl git jq procps psmisc tar unzip xz-utils zstd tzdata \
      libicu74 libssl3t64 zlib1g libgssapi-krb5-2 libstdc++6 \
 && rm -rf /var/lib/apt/lists/*

# The hosted runner runs steps as `runner` (uid 1001), never root. Tests that touch file
# permissions or signal handling behave differently as root.
RUN userdel -r ubuntu 2>/dev/null || true; \
    useradd -m -u 1001 -s /bin/bash runner \
 && mkdir -p /home/runner/.nuget/packages /home/runner/work /ci-out \
 && chown -R runner:runner /home/runner /ci-out

# setup-dotnet resolves a channel such as `10.0.4xx` to that band's latest patch on every
# run. CACHE_WEEK (set by ci-linux.sh to the ISO week) re-resolves it weekly instead of
# freezing the first patch this machine ever installed.
ARG DOTNET_CHANNEL
ARG CACHE_WEEK
RUN echo "dotnet channel ${DOTNET_CHANNEL}, resolved in week ${CACHE_WEEK}" \
 && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel "${DOTNET_CHANNEL}" --install-dir /usr/share/dotnet \
 && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
 && rm /tmp/dotnet-install.sh \
 && dotnet --version

ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    PATH=/home/runner/.dotnet/tools:/usr/share/dotnet:$PATH

USER runner
WORKDIR /home/runner
