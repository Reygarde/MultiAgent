using System.Security.Cryptography;
using System.Text.Json;
using AIPlatform.Core.Studio;

namespace AIPlatform.PreviewGuestAgent;

public sealed class HyperVPreviewGuestAgentConfiguration : IDisposable
{
    public const int SchemaVersion = 1;
    public const int MaximumConfigurationBytes = 4096;
    public static readonly TimeSpan MaximumSessionLifetime = TimeSpan.FromSeconds(170);

    private HyperVPreviewGuestAgentConfiguration(
        Guid sessionId,
        Guid vmId,
        HyperVPreviewConnectionChallenges challenges,
        long expiresAtUnixTimeMilliseconds)
    {
        SessionId = sessionId;
        VmId = vmId;
        Challenges = challenges;
        ExpiresAtUnixTimeMilliseconds = expiresAtUnixTimeMilliseconds;
    }

    public Guid SessionId { get; }
    public Guid VmId { get; }
    public HyperVPreviewConnectionChallenges Challenges { get; }
    public long ExpiresAtUnixTimeMilliseconds { get; }

    public static async Task<HyperVPreviewGuestAgentConfiguration> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Agent credential must be an existing regular file.");

        if (OperatingSystem.IsLinux())
            ValidateCredentialPermissions(path);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumConfigurationBytes)
            throw new InvalidDataException("Agent credential size is invalid.");

        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
            throw new InvalidDataException("Agent credential changed while being read.");
        return Parse(bytes);
    }

    public static HyperVPreviewGuestAgentConfiguration Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumConfigurationBytes)
            throw new InvalidDataException("Agent configuration size is invalid.");

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Agent configuration must be a JSON object.");

        var expectedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion",
            "sessionId",
            "vmId",
            "controlChallenge",
            "framesChallenge",
            "expiresAtUnixTimeMilliseconds",
        };
        foreach (var property in root.EnumerateObject())
        {
            if (!expectedProperties.Remove(property.Name))
                throw new InvalidDataException("Agent configuration has duplicate or unknown properties.");
        }
        if (expectedProperties.Count != 0)
            throw new InvalidDataException("Agent configuration is incomplete.");

        if (!root.GetProperty("schemaVersion").TryGetInt32(out var schemaVersion) ||
            schemaVersion != SchemaVersion)
            throw new InvalidDataException("Agent configuration schema version is unsupported.");

        var sessionId = ParseGuid(root.GetProperty("sessionId"), "sessionId");
        var vmId = ParseGuid(root.GetProperty("vmId"), "vmId");
        byte[]? controlChallenge = null;
        byte[]? framesChallenge = null;
        var transferred = false;
        try
        {
            controlChallenge = ParseChallenge(root.GetProperty("controlChallenge"), "controlChallenge");
            framesChallenge = ParseChallenge(root.GetProperty("framesChallenge"), "framesChallenge");
            if (!root.GetProperty("expiresAtUnixTimeMilliseconds")
                    .TryGetInt64(out var expiresAtUnixTimeMilliseconds))
                throw new InvalidDataException("Agent session expiration is invalid.");

            var configuration = new HyperVPreviewGuestAgentConfiguration(
                sessionId,
                vmId,
                new HyperVPreviewConnectionChallenges(controlChallenge, framesChallenge),
                expiresAtUnixTimeMilliseconds);
            configuration.Validate(TimeProvider.System);
            transferred = true;
            return configuration;
        }
        finally
        {
            if (!transferred)
            {
                if (controlChallenge is not null)
                    CryptographicOperations.ZeroMemory(controlChallenge);
                if (framesChallenge is not null)
                    CryptographicOperations.ZeroMemory(framesChallenge);
            }
        }
    }

    public TimeSpan GetRemainingLifetime(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Validate(clock);
        return TimeSpan.FromMilliseconds(
            ExpiresAtUnixTimeMilliseconds - clock.GetUtcNow().ToUnixTimeMilliseconds());
    }

    public HyperVPreviewGuestAgentOptions CreateAgentOptions(
        ReadOnlySpan<byte> measuredAgentDigest,
        TimeProvider clock)
    {
        if (measuredAgentDigest.Length != 32)
            throw new ArgumentException("Measured agent digest must be SHA-256.", nameof(measuredAgentDigest));
        return new HyperVPreviewGuestAgentOptions(
            SessionId,
            VmId,
            new HyperVPreviewConnectionChallenges(
                Challenges.Control.ToArray(),
                Challenges.Frames.ToArray()),
            measuredAgentDigest.ToArray(),
            GetRemainingLifetime(clock));
    }

    public void Validate(TimeProvider clock)
    {
        if (SessionId == Guid.Empty || VmId == Guid.Empty)
            throw new InvalidDataException("Agent configuration requires non-empty session and VM ids.");

        try
        {
            Challenges.Validate();
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Agent channel challenges are invalid.", error);
        }

        if (IsAllZero(Challenges.Control) || IsAllZero(Challenges.Frames))
            throw new InvalidDataException("Agent channel challenges must not be empty.");

        long remainingMilliseconds;
        try
        {
            remainingMilliseconds = checked(
                ExpiresAtUnixTimeMilliseconds - clock.GetUtcNow().ToUnixTimeMilliseconds());
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("Agent session expiration is invalid.", error);
        }
        if (remainingMilliseconds <= 0 ||
            remainingMilliseconds > (long)MaximumSessionLifetime.TotalMilliseconds)
            throw new InvalidDataException("Agent session is expired or exceeds the 170-second TTL.");
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Challenges.Control);
        CryptographicOperations.ZeroMemory(Challenges.Frames);
    }

    private static Guid ParseGuid(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(value.GetString(), "D", out var result) ||
            result == Guid.Empty)
            throw new InvalidDataException($"Agent configuration {name} is invalid.");
        return result;
    }

    private static byte[] ParseChallenge(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Agent configuration {name} is invalid.");
        byte[] challenge;
        try
        {
            challenge = Convert.FromBase64String(value.GetString()!);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException($"Agent configuration {name} is invalid.", error);
        }
        if (challenge.Length != 32)
        {
            CryptographicOperations.ZeroMemory(challenge);
            throw new InvalidDataException($"Agent configuration {name} must be 32 bytes.");
        }
        return challenge;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        byte combined = 0;
        foreach (var item in value)
            combined |= item;
        return combined == 0;
    }

    private static void ValidateCredentialPermissions(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode forbidden =
            UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & UnixFileMode.UserRead) == 0 || (mode & forbidden) != 0)
            throw new UnauthorizedAccessException("Agent credential must be owner-readable and otherwise inaccessible.");
    }
}
