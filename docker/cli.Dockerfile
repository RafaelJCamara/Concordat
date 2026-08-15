# The `concordat` CLI as a container, so a Python, Go or Java shop can gate CI
# with no .NET installed anywhere (ADR-019, M3.3).
#
# Build from the repository root:
#   docker build -f docker/cli.Dockerfile -t concordat/cli .

# ---- build ----------------------------------------------------------------
# Pinned by digest, not just tag -- see docker/api.Dockerfile's comment on the same line for
# why. Update deliberately with `docker pull ... && docker inspect --format
# '{{index .RepoDigests 0}}'` rather than letting it drift.
FROM mcr.microsoft.com/dotnet/sdk@sha256:e1fc6e423f543119c406d24e2e687d67c569f18f04a37a8b0005d80ad0dcee80 AS build
# mcr.microsoft.com/dotnet/sdk:10.0

# NativeAOT links with clang against zlib. Not present in the SDK image.
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Central Package Management means the props files govern every restore, so they have to be
# present before it runs.
#
# .editorconfig governs analyser severity, and TreatWarningsAsErrors is on. Without it this
# build applies each rule's DEFAULT severity and turns every one into an error — so the image
# build fails on rules the repository has deliberately relaxed, and does so nowhere else.
# CA1848 is the one that caught this: set to `suggestion` with a reason, fatal without.
COPY Directory.Build.props Directory.Packages.props .editorconfig nuget.config* ./
COPY src/ src/

RUN dotnet publish src/tools/Concordat.Cli \
      -c Release \
      -r linux-x64 \
      /p:PublishAot=true \
      -o /out

# ---- runtime --------------------------------------------------------------
# runtime-deps carries the native prerequisites (libc, OpenSSL) and NO .NET
# runtime at all — the AOT binary brings everything it needs. That is what makes
# the "zero .NET installed" claim in M3.3 literally true rather than a figure of
# speech.
FROM mcr.microsoft.com/dotnet/runtime-deps@sha256:851e6e04fff95d33a02c725373dc0ac624734986f2dc9028887077347ecffa96 AS runtime
# mcr.microsoft.com/dotnet/runtime-deps:10.0

# CI runners mount the workspace here. A relative --dir resolves against it.
WORKDIR /workspace

COPY --from=build /out/concordat /usr/local/bin/concordat

# Non-root, using the account the base image already provides (same $APP_UID convention as
# api.Dockerfile — runtime-deps and aspnet both ship the same `app` user at the same UID).
# A parser bug becoming root-in-container is a larger blast radius than nothing here needs.
# GitHub's `docker` action type runs every step as root regardless of this, per its own
# documented behaviour, so action.yml is unaffected -- this only changes the default for
# `docker run` outside that context, where it means files this CLI writes into a mounted
# -v "$PWD:/workspace" are not root-owned on the host.
USER $APP_UID

# No shell wrapper: the CLI's exit code is its contract with CI (0 success,
# 1 contract violation, 2 usage, 3 registry unavailable, 4 local file error),
# and anything in between could swallow or rewrite it.
ENTRYPOINT ["/usr/local/bin/concordat"]
CMD ["--help"]
