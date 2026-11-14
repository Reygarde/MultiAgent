ARG BASE_IMAGE=aiplatform-sandbox:net10
FROM ${BASE_IMAGE} AS packages
USER 10001:10001
ENV HOME=/tmp DOTNET_CLI_HOME=/tmp DOTNET_CLI_TELEMETRY_OPTOUT=1
COPY --chown=10001:10001 DiscordSandboxPackages.csproj DiscordNuGet.Config /tmp/seed/
RUN dotnet restore /tmp/seed/DiscordSandboxPackages.csproj \
    --configfile /tmp/seed/DiscordNuGet.Config --packages /tmp/discord-packages \
    -p:NuGetAudit=false \
    && dotnet list /tmp/seed/DiscordSandboxPackages.csproj package --include-transitive --no-restore

FROM ${BASE_IMAGE}
COPY --from=packages /tmp/discord-packages/ /opt/nuget/
