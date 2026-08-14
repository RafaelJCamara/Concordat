# The Concordat registry as a container (M1.7).
#
# Build from the repository root:
#   docker build -f docker/api.Dockerfile -t concordat/api .
#
# THE MIGRATOR IS IN THIS IMAGE TOO, AND THAT IS THE POINT.
#
# The API deliberately does not migrate on startup (M1.5): every replica would race to
# alter the schema, and a failed migration would take the app down rather than failing a
# step somebody is watching. So migrations run as a separate invocation — a Container Apps
# job, a compose service, a pipeline step.
#
# Shipping the migrator as its own image would let the two drift. A migrator built from a
# different commit than the API is exactly how a deployment ends up with a schema the code
# does not expect, and nothing would report it: the migration succeeds, the API starts, and
# the first request touching the changed table fails. One image, one build, one commit —
# override the command to choose which program runs:
#
#   docker run concordat/api                                  # the registry
#   docker run --entrypoint dotnet concordat/api Concordat.Migrator.dll   # the migrations

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

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

# Both hosts into one output directory. They share every dependency, so publishing them
# together costs almost nothing over publishing the API alone.
RUN dotnet publish src/hosts/Concordat.Api -c Release -o /out \
 && dotnet publish src/hosts/Concordat.Migrator -c Release -o /out

# ---- runtime --------------------------------------------------------------
# The ASP.NET image rather than runtime-deps: unlike the CLI (M3.3), this is not NativeAOT.
# The registry loads schema-format libraries and EF Core's provider through reflection-heavy
# paths, and an AOT build of it would be a much larger piece of work than an image size.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app
COPY --from=build /out .

# Non-root, using the account the base image already provides. Container Apps does not
# require it; running as root when nothing needs it is just a larger blast radius.
USER $APP_UID

# 8080 because that is what the base image defaults to for a non-root user — binding 80
# would need a capability this container deliberately does not have.
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# No shell form: the API has to receive SIGTERM directly so it can drain in-flight requests
# and stop the outbox pump cleanly. A shell wrapper would swallow the signal and leave the
# platform to kill it after the grace period.
ENTRYPOINT ["dotnet", "Concordat.Api.dll"]
