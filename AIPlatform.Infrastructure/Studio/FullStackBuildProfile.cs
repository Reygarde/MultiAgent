using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static class FullStackBuildProfile
{
    private const string NodeModules =
        "mkdir /work/frontend/node_modules && " +
        "ln -s /opt/aiplatform/node_modules/* /opt/aiplatform/node_modules/.bin /work/frontend/node_modules/ && ";

    private const string OfflineRestore =
        "-p:RestoreSources=/opt/empty-feed -p:NuGetAudit=false -p:RestorePackagesPath=/opt/nuget " +
        "-p:RestoreLockedMode=true";

    // Load the approved SQLite binary from the immutable cache, not the noexec /work tmpfs.
    private const string NativeLibraryEnvironment =
        "LD_LIBRARY_PATH=/opt/nuget/sqlitepclraw.lib.e_sqlite3/2.1.13/runtimes/linux-x64/native ";

    public static bool IsStage(string stage) => stage is
        "BackendBuild" or "BackendTests" or "TypeScript" or "FrontendBuild" or "FrontendTests" or "Integration";

    public static string Command(string stage) => stage switch
    {
        "BackendBuild" => DotNetBuild("Generated.slnx"),
        "BackendTests" => DotNetTests("backend/Api.Tests/Api.Tests.csproj"),
        "TypeScript" => NodeModules + "/opt/aiplatform/node_modules/.bin/tsc --noEmit --project /work/frontend/tsconfig.json",
        "FrontendBuild" => NodeModules + "cd /work/frontend && /opt/aiplatform/node_modules/.bin/vite build --config vite.config.ts",
        "FrontendTests" => NodeModules + Vitest("cd /work/frontend && /opt/aiplatform/node_modules/.bin/vitest run tests --config vite.config.ts"),
        "Integration" => IntegrationCommand(),
        _ => throw new StudioValidationException("Unauthorized full-stack validation stage.")
    };

    public static (int Total, int Failed)? TestCounts(string output) => ReactBuildProfile.TestCounts(output);

    private static string DotNetBuild(string project) =>
        $"dotnet build {project} --configuration Release --nologo {OfflineRestore}";

    private static string DotNetTests(string project) =>
        NativeLibraryEnvironment + $"dotnet test {project} --configuration Release --nologo {OfflineRestore} " +
        "--logger 'trx;LogFileName=verification.trx' --results-directory /work/results " +
        "> /work/test.log 2>&1; result=$?; head -c 24000 /work/test.log; " +
        "if [ $(wc -c < /work/test.log) -gt 24000 ]; then printf '\\n[Test log truncated at 24000 bytes.]\\n'; fi; " +
        "printf '\\nAIPLATFORM_TRX_BEGIN\\n'; " +
        "if [ -f /work/results/verification.trx ]; then cat /work/results/verification.trx; fi; " +
        "printf '\\nAIPLATFORM_TRX_END\\n'; exit \"$result\"";

    private static string Vitest(string command) =>
        command + " --reporter=json --outputFile=/work/vitest.json > /work/test.log 2>&1; result=$?; " +
        "head -c 24000 /work/test.log; " +
        "printf '\\nAIPLATFORM_VITEST_BEGIN\\n'; " +
        "if [ -f /work/vitest.json ]; then head -c 65536 /work/vitest.json; fi; " +
        "printf '\\nAIPLATFORM_VITEST_END\\n'; exit \"$result\"";

    private static string IntegrationCommand() =>
        DotNetBuild("backend/Api/Api.csproj") + " && " +
        "( " + NativeLibraryEnvironment + "ASPNETCORE_URLS=http://127.0.0.1:5080 " +
        "ConnectionStrings__Default='Data Source=/tmp/fullstack-integration.db' " +
        "dotnet /work/backend/Api/bin/Release/net10.0/Api.dll >/tmp/fullstack-api.log 2>&1 & server=$!; " +
        "trap 'kill \"$server\" 2>/dev/null || true; wait \"$server\" 2>/dev/null || true' EXIT; " +
        "node --input-type=module -e 'const url=\"http://127.0.0.1:5080/api/tasks\"; " +
        "for(let i=0;i<60;i++){try{const r=await fetch(url);if(r.ok)process.exit(0)}catch{} " +
        "await new Promise(resolve=>setTimeout(resolve,250))} console.error(\"API did not become ready\");process.exit(1)' " +
        "|| { cat /tmp/fullstack-api.log; exit 1; }; " +
        "mkdir /work/frontend/node_modules && " +
        "ln -s /opt/aiplatform/node_modules/* /opt/aiplatform/node_modules/.bin /work/frontend/node_modules/ && " +
        "cd /work/frontend && FULLSTACK_API_BASE_URL=http://127.0.0.1:5080 " +
        Vitest("/opt/aiplatform/node_modules/.bin/vitest run integration --config vite.integration.config.ts") + ")";
}
