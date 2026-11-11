FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ddbaedfc8b774389d91a511af08b77296f2c7a8d15d86f6214d2a87166933625
COPY SandboxPackages.csproj /seed/SandboxPackages.csproj
RUN dotnet restore /seed/SandboxPackages.csproj --packages /opt/nuget \
    && mkdir -p /opt/empty-feed \
    && chmod -R a+rX /opt/nuget \
    && mkdir -p /work && chmod 755 /work
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
USER 10001:10001
WORKDIR /work
