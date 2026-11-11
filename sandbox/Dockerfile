FROM mcr.microsoft.com/dotnet/sdk:10.0
COPY SandboxPackages.csproj /seed/SandboxPackages.csproj
RUN dotnet restore /seed/SandboxPackages.csproj --packages /opt/nuget \
    && mkdir -p /opt/empty-feed \
    && chmod -R a+rX /opt/nuget
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
USER 10001:10001
WORKDIR /work
