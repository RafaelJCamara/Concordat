# The `concordat` CLI as a container, so a Python, Go or Java shop can gate CI
# with no .NET installed anywhere (ADR-019, M3.3).
#
# Build from the repository root:
#   docker build -f docker/cli.Dockerfile -t concordat/cli .

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# NativeAOT links with clang against zlib. Not present in the SDK image.
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Central Package Management means the props files govern every restore, so they
# have to be present before it runs.
COPY Directory.Build.props Directory.Packages.props nuget.config* ./
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
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime

# CI runners mount the workspace here. A relative --dir resolves against it.
WORKDIR /workspace

COPY --from=build /out/concordat /usr/local/bin/concordat

# No shell wrapper: the CLI's exit code is its contract with CI (0 success,
# 1 contract violation, 2 usage, 3 registry unavailable, 4 local file error),
# and anything in between could swallow or rewrite it.
ENTRYPOINT ["/usr/local/bin/concordat"]
CMD ["--help"]
