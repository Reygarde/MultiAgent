using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed record PreviewHttpResponse(int Status, string ContentType, byte[] Body);

public sealed class DockerPreviewHttpTransport(IContainerProcess process)
{
    public const int MaximumRequestBytes = 16384;
    public const int MaximumResponseBytes = 1048576;
    public const int TimeoutSeconds = 10;
    public static PreviewTransport Policy { get; } = new("docker-exec-json-v1", MaximumRequestBytes,
        MaximumResponseBytes, TimeoutSeconds, false, "X-Preview-Token; no cookies",
        "edge-helper-v1; fresh-profile; host-only-token; deny-proxy; no-workers-or-websockets; OS-network-containment-required");

    internal static bool IsAllowed(string method, string path)
    {
        if (path.Length > 256 || path.Contains('%') || path.Contains('\\') ||
            path.Any(char.IsControl) || path.Contains("..", StringComparison.Ordinal))
            return false;
        if (Regex.IsMatch(path, @"^/api/tasks(\?status=(all|active|completed))?$", RegexOptions.CultureInvariant))
            return method == "GET" || path == "/api/tasks" && method == "POST";
        if (Regex.IsMatch(path, @"^/api/tasks/[a-fA-F0-9-]{36}$", RegexOptions.CultureInvariant) &&
            Guid.TryParseExact(path["/api/tasks/".Length..], "D", out _))
            return method is "GET" or "PUT" or "DELETE";
        return method == "GET" && (path == "/" || path == "/favicon.ico" ||
            Regex.IsMatch(path, @"^/assets/[A-Za-z0-9_-]+\.(js|css|png|jpg|jpeg|ico|woff2)$", RegexOptions.CultureInvariant));
    }

    public async Task<PreviewHttpResponse> SendAsync(string container, string method, string path,
        byte[] body, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(container, @"^aiplatform-preview-[a-f0-9]{16}-[a-f0-9]{32}$", RegexOptions.CultureInvariant) ||
            !IsAllowed(method, path) || body.Length > MaximumRequestBytes ||
            method is "GET" or "DELETE" && body.Length != 0)
            throw new StudioValidationException("Unauthorized Preview transport request.");
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            method, path, body = Convert.ToBase64String(body)
        }));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var result = await process.RunAsync(
            ["exec", container, "node", "--input-type=module", "-e", Relay, payload],
            timeout.Token, 1500000);
        if (result.ExitCode != 0 || result.Output.Length > 1500000)
            throw new IOException("Preview HTTP relay failed.");
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;
            var status = root.GetProperty("status").GetInt32();
            var contentType = root.GetProperty("contentType").GetString() ?? "";
            var bytes = Convert.FromBase64String(root.GetProperty("body").GetString() ?? "");
            if (status is < 200 or > 599 || status is >= 300 and < 400 ||
                bytes.Length > MaximumResponseBytes || contentType.Length > 128 ||
                contentType.Any(char.IsControl) || !new[]
                {
                    "", "text/html", "text/css", "text/javascript", "application/javascript",
                    "application/json", "application/problem+json", "text/plain", "image/png",
                    "image/jpeg", "image/x-icon", "image/vnd.microsoft.icon", "font/woff2"
                }.Contains(contentType.Split(';')[0].Trim(), StringComparer.OrdinalIgnoreCase))
                throw new IOException("The Preview response or redirect is forbidden.");
            return new(status, contentType, bytes);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                           KeyNotFoundException or InvalidOperationException)
        {
            throw new IOException("Invalid Preview relay response.", exception);
        }
    }

    private const string Relay = """
        const q=JSON.parse(Buffer.from(process.argv[1],"base64").toString("utf8"));
        const api=q.path.startsWith("/api/tasks");
        const url=new URL(q.path,api?"http://127.0.0.1:5080":"http://127.0.0.1:5081");
        if(url.hostname!=="127.0.0.1" || url.port!==(api?"5080":"5081"))process.exit(2);
        try {
          const r=await fetch(url,{method:q.method,redirect:"manual",signal:AbortSignal.timeout(9000),
            headers:q.method==="POST"||q.method==="PUT"?{"Content-Type":"application/json"}:{},
            body:q.method==="POST"||q.method==="PUT"?Buffer.from(q.body,"base64"):undefined});
          if(r.status>=300&&r.status<400)throw new Error("redirect forbidden");
          const chunks=[];let size=0;
          if(r.body)for await(const chunk of r.body){size+=chunk.length;if(size>1048576)throw new Error("response too large");chunks.push(chunk);}
          console.log(JSON.stringify({status:r.status,contentType:r.headers.get("content-type")??"",
            body:Buffer.concat(chunks).toString("base64")}));
        } catch { console.error("Preview relay failed"); process.exit(1); }
        """;
}
