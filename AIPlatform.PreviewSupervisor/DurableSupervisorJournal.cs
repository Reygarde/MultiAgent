using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIPlatform.PreviewSupervisor;

public sealed class DurableSupervisorJournal : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _path;
    private readonly FileStream _processLock;
    private readonly Dictionary<Guid, SupervisorSessionRecord> _sessions = [];
    private long _journalSequence;
    private bool _disposed;

    private DurableSupervisorJournal(string path)
    {
        _path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("The journal path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        RejectReparsePoint(directory);
        RejectExistingFileLink(_path);

        var lockPath = _path + ".lock";
        RejectExistingFileLink(lockPath);
        _processLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        try
        {
            Load();
        }
        catch
        {
            _processLock.Dispose();
            throw;
        }
    }

    public static DurableSupervisorJournal Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new DurableSupervisorJournal(path);
    }

    public IReadOnlyList<SupervisorSessionRecord> ReadLatest()
    {
        ThrowIfDisposed();
        return _sessions.Values.OrderBy(record => record.JournalSequence).ToArray();
    }

    public SupervisorSessionRecord Append(SupervisorSessionRecord record)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record);

        var hasPrevious = _sessions.TryGetValue(record.SessionId, out var previous);
        var expectedRevision = hasPrevious ? checked(previous!.Revision + 1) : 1;
        if (hasPrevious && record.Revision != previous!.Revision ||
            !hasPrevious && record.Revision != 0)
            throw new InvalidOperationException("Supervisor journal revision is not consecutive.");
        if (record.JournalSequence != (previous?.JournalSequence ?? 0))
            throw new InvalidOperationException("Supervisor journal sequence is stale.");
        if (!hasPrevious && _sessions.Values.Any(
                existing => existing.CreationRequestId == record.CreationRequestId))
            throw new InvalidOperationException("Supervisor create request id was already used.");
        if (previous is not null)
            ValidateUpdate(previous, record);

        var persisted = record with
        {
            JournalSequence = checked(_journalSequence + 1),
            Revision = expectedRevision,
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions);
        if (payload.Length == 0 || payload.Length > SupervisorPolicy.MaximumJournalRecordBytes)
            throw new InvalidDataException("Supervisor journal record exceeds its size limit.");

        var frameLength = checked(sizeof(int) + payload.Length + SHA256.HashSizeInBytes);
        var info = new FileInfo(_path);
        if (info.Exists && info.Length + frameLength > SupervisorPolicy.MaximumJournalBytes)
            throw new IOException("Supervisor journal reached its configured size limit.");

        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        var checksum = SHA256.HashData(payload);
        using (var stream = new FileStream(
                   _path,
                   FileMode.OpenOrCreate,
                   FileAccess.Write,
                   FileShare.Read,
                   4096,
                   FileOptions.WriteThrough))
        {
            stream.Seek(0, SeekOrigin.End);
            stream.Write(length);
            stream.Write(payload);
            stream.Write(checksum);
            stream.Flush(flushToDisk: true);
        }

        _journalSequence = persisted.JournalSequence;
        _sessions[record.SessionId] = persisted;
        return persisted;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _processLock.Dispose();
    }

    private void Load()
    {
        RejectExistingFileLink(_path);
        if (!File.Exists(_path))
            return;

        var bytes = File.ReadAllBytes(_path);
        if (bytes.Length > SupervisorPolicy.MaximumJournalBytes)
            throw new InvalidDataException("Supervisor journal exceeds its size limit.");

        var offset = 0;
        var validLength = 0;
        while (offset < bytes.Length)
        {
            var remaining = bytes.Length - offset;
            if (remaining < sizeof(int))
                break;

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
            if (payloadLength <= 0 || payloadLength > SupervisorPolicy.MaximumJournalRecordBytes)
                throw new InvalidDataException("Supervisor journal has an invalid record length.");

            var totalLength = checked(sizeof(int) + payloadLength + SHA256.HashSizeInBytes);
            if (remaining < totalLength)
                break;

            var payloadOffset = offset + sizeof(int);
            var payload = bytes.AsSpan(payloadOffset, payloadLength);
            var expectedChecksum = bytes.AsSpan(payloadOffset + payloadLength, SHA256.HashSizeInBytes);
            var actualChecksum = SHA256.HashData(payload);
            if (!CryptographicOperations.FixedTimeEquals(actualChecksum, expectedChecksum))
                throw new InvalidDataException("Supervisor journal checksum mismatch.");

            RejectDuplicateProperties(payload);
            var record = JsonSerializer.Deserialize<SupervisorSessionRecord>(payload, JsonOptions)
                ?? throw new InvalidDataException("Supervisor journal contains an empty record.");
            ValidateRecord(record);
            ApplyLoadedRecord(record);

            offset += totalLength;
            validLength = offset;
        }

        if (validLength != bytes.Length)
        {
            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);
            stream.SetLength(validLength);
            stream.Flush(flushToDisk: true);
        }
    }

    private void ApplyLoadedRecord(SupervisorSessionRecord record)
    {
        var expectedJournalSequence = checked(_journalSequence + 1);
        if (record.JournalSequence != expectedJournalSequence)
            throw new InvalidDataException("Supervisor journal sequence is not consecutive.");

        var expectedRevision = _sessions.TryGetValue(record.SessionId, out var previous)
            ? checked(previous.Revision + 1)
            : 1;
        if (record.Revision != expectedRevision)
            throw new InvalidDataException("Supervisor session revision is not consecutive.");
        if (previous is null && _sessions.Values.Any(
                existing => existing.CreationRequestId == record.CreationRequestId))
            throw new InvalidDataException("Supervisor journal reuses a create request id.");
        if (previous is not null && previous.CreationRequestId != record.CreationRequestId)
            throw new InvalidDataException("Supervisor session creation identity changed.");
        if (previous is not null)
            ValidateUpdate(previous, record);

        _journalSequence = record.JournalSequence;
        _sessions[record.SessionId] = record;
    }

    private static void ValidateUpdate(
        SupervisorSessionRecord previous,
        SupervisorSessionRecord next)
    {
        if (next.SessionId != previous.SessionId ||
            next.CreationRequestId != previous.CreationRequestId ||
            next.CreatedAtUtc != previous.CreatedAtUtc ||
            next.ExpiresAtUtc != previous.ExpiresAtUtc ||
            next.BootEpochUtc != previous.BootEpochUtc ||
            next.CreatedUptimeMilliseconds != previous.CreatedUptimeMilliseconds ||
            next.ExpiresAtUptimeMilliseconds != previous.ExpiresAtUptimeMilliseconds ||
            previous.Lease is not null && next.Lease != previous.Lease ||
            previous.RequestedTerminalState is { } previousTarget &&
            next.RequestedTerminalState != previousTarget ||
            next.LastHeartbeatUptimeMilliseconds < previous.LastHeartbeatUptimeMilliseconds ||
            next.LastHeartbeatUptimeMilliseconds != 0 &&
            (next.LastHeartbeatUptimeMilliseconds < next.CreatedUptimeMilliseconds ||
             next.LastHeartbeatUptimeMilliseconds >= next.ExpiresAtUptimeMilliseconds))
            throw new InvalidDataException("Supervisor journal changed immutable session data.");

        if (next.ReservedChannels is null ||
            next.ReservedChannels.Count > 2 ||
            next.ReservedChannels.Any(role => !Enum.IsDefined(role)) ||
            next.ReservedChannels.Distinct().Count() != next.ReservedChannels.Count)
            throw new InvalidDataException("Supervisor journal contains invalid channel reservations.");

        var transitionAllowed = (previous.State, next.State) switch
        {
            (SupervisedSessionState.Provisioning, SupervisedSessionState.Provisioned or
                SupervisedSessionState.Stopping) => true,
            (SupervisedSessionState.Provisioned, SupervisedSessionState.Running or
                SupervisedSessionState.Stopping) => true,
            (SupervisedSessionState.Running, SupervisedSessionState.Running or
                SupervisedSessionState.Stopping) => true,
            (SupervisedSessionState.Stopping, SupervisedSessionState.Stopping or
                SupervisedSessionState.CleanupFailed or SupervisedSessionState.Stopped or
                SupervisedSessionState.Expired) => true,
            (SupervisedSessionState.CleanupFailed, SupervisedSessionState.Stopping) => true,
            _ => false,
        };
        if (!transitionAllowed)
            throw new InvalidDataException("Supervisor journal contains an invalid state transition.");

        if (next.LastCommandSequence == previous.LastCommandSequence)
        {
            if (!next.RecentCommandIds.SequenceEqual(previous.RecentCommandIds) ||
                next.LastHeartbeatUptimeMilliseconds != previous.LastHeartbeatUptimeMilliseconds ||
                !next.ReservedChannels.SequenceEqual(previous.ReservedChannels))
                throw new InvalidDataException("Supervisor journal changed command data without a sequence.");
        }
        else if (next.LastCommandSequence != previous.LastCommandSequence + 1 ||
                 next.RecentCommandIds.Count != previous.RecentCommandIds.Count + 1 ||
                 !next.RecentCommandIds.Take(previous.RecentCommandIds.Count)
                     .SequenceEqual(previous.RecentCommandIds) ||
                 next.RecentCommandIds[^1] == Guid.Empty ||
                 next.ReservedChannels.Count < previous.ReservedChannels.Count ||
                 next.ReservedChannels.Count > previous.ReservedChannels.Count + 1 ||
                 !next.ReservedChannels.Take(previous.ReservedChannels.Count)
                     .SequenceEqual(previous.ReservedChannels))
        {
            throw new InvalidDataException("Supervisor journal command sequence is not consecutive.");
        }
    }

    private static void ValidateRecord(SupervisorSessionRecord record)
    {
        if (record.JournalSequence < 0 || record.Revision < 0 ||
            record.SessionId == Guid.Empty || record.CreationRequestId == Guid.Empty ||
            !Enum.IsDefined(record.State) ||
            record.CreatedAtUtc.Offset != TimeSpan.Zero ||
            record.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            record.BootEpochUtc.Offset != TimeSpan.Zero ||
            record.ExpiresAtUtc <= record.CreatedAtUtc ||
            record.ExpiresAtUtc - record.CreatedAtUtc > SupervisorPolicy.MaximumLifetime ||
            record.CreatedUptimeMilliseconds < 0 ||
            record.ExpiresAtUptimeMilliseconds <= record.CreatedUptimeMilliseconds ||
            record.ExpiresAtUptimeMilliseconds - record.CreatedUptimeMilliseconds >
            (long)SupervisorPolicy.MaximumLifetime.TotalMilliseconds ||
            record.LastHeartbeatUptimeMilliseconds < 0 ||
            record.LastCommandSequence > (ulong)SupervisorPolicy.MaximumRecentCommands ||
            record.RecentCommandIds is null ||
            record.RecentCommandIds.Count > SupervisorPolicy.MaximumRecentCommands ||
            record.RecentCommandIds.Any(id => id == Guid.Empty) ||
            record.RecentCommandIds.Distinct().Count() != record.RecentCommandIds.Count ||
            record.RecentCommandIds.Count != (int)record.LastCommandSequence ||
            record.ReservedChannels is null || record.ReservedChannels.Count > 2 ||
            record.ReservedChannels.Any(role => !Enum.IsDefined(role)) ||
            record.ReservedChannels.Distinct().Count() != record.ReservedChannels.Count ||
            record.RequestedTerminalState is { } terminal && !Enum.IsDefined(terminal) ||
            record.CleanupFailure is { } failure && !Enum.IsDefined(failure))
            throw new InvalidDataException("Supervisor journal record failed validation.");

        if (record.Lease is { } lease &&
            (lease.SessionId != record.SessionId || lease.VmId == Guid.Empty ||
             string.IsNullOrWhiteSpace(lease.ResourceId) || lease.ResourceId.Length > 256 ||
             lease.ResourceId.Any(char.IsControl)))
            throw new InvalidDataException("Supervisor journal contains an invalid VM lease.");

        if (record.State is SupervisedSessionState.Stopping or SupervisedSessionState.CleanupFailed or
                SupervisedSessionState.Stopped or SupervisedSessionState.Expired &&
            record.RequestedTerminalState is null)
            throw new InvalidDataException("Supervisor cleanup record has no terminal target.");
        if (record.State == SupervisedSessionState.CleanupFailed && record.CleanupFailure is null)
            throw new InvalidDataException("Failed cleanup record has no failure code.");
        if (record.State != SupervisedSessionState.CleanupFailed && record.CleanupFailure is not null)
            throw new InvalidDataException("Non-failing supervisor record has a cleanup failure code.");
        if (record.State == SupervisedSessionState.Stopped &&
            record.RequestedTerminalState != SupervisorTerminalState.Stopped ||
            record.State == SupervisedSessionState.Expired &&
            record.RequestedTerminalState != SupervisorTerminalState.Expired)
            throw new InvalidDataException("Supervisor terminal state does not match its target.");
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> payload)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        Visit(document.RootElement);

        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("Supervisor journal contains duplicate JSON properties.");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Visit(item);
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Supervisor journal directory cannot be a reparse point.");
    }

    private static void RejectExistingFileLink(string path)
    {
        if (File.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Supervisor journal files cannot be reparse points.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
