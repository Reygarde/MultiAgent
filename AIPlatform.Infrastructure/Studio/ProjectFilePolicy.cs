namespace AIPlatform.Infrastructure.Studio;

public static class ProjectFilePolicy
{
    public static bool IsExcluded(string path)
    {
        var segments = path.Split('/');
        if (segments.Any(segment => new[] { "bin", "obj", "node_modules", ".git", ".vs", ".idea",
                "dist", "coverage", "target", ".aws", ".azure", ".ssh", ".docker", ".kube", ".gnupg" }
                .Contains(segment, StringComparer.OrdinalIgnoreCase))) return true;
        var name = segments[^1];
        return name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
            new[] { ".npmrc", ".yarnrc", ".yarnrc.yml", ".pypirc", "NuGet.Config", "id_rsa", "id_ed25519" }
                .Contains(name, StringComparer.OrdinalIgnoreCase) ||
            new[] { "secret", "credential", "password", "token" }
                .Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
            new[] { ".pem", ".key", ".p12", ".pfx", ".crt", ".cer" }
                .Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }
}
