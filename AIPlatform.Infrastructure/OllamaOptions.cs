namespace AIPlatform.Infrastructure;

public sealed record OllamaOptions
{
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public int RequestTimeoutSeconds { get; init; } = 180;
    public int KeepAliveSeconds { get; init; } = 120;
    public bool UnloadPreviousModel { get; init; } = true;

    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            !uri.IsLoopback || uri.Scheme != Uri.UriSchemeHttp ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Ollama BaseUrl must be an HTTP loopback origin.");
        if (RequestTimeoutSeconds is < 1 or > 3600 || KeepAliveSeconds is < 0 or > 3600)
            throw new ArgumentException("Invalid Ollama timeout or keep-alive configuration.");
    }
}
