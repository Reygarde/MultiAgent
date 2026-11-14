set -eu

cp -R /opt/fullstack-seed /tmp/fullstack-seed
dotnet restore /tmp/fullstack-seed/FullStackSandboxPackages.csproj \
    --configfile /tmp/fullstack-seed/FullStackNuGet.Config \
    --source "$1" --packages /tmp/fullstack-nuget \
    -p:NuGetAudit=false -p:RestoreLockedMode=true \
    -p:BaseIntermediateOutputPath=/tmp/fullstack-obj/
