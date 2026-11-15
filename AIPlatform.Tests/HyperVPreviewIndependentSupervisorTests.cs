using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using AIPlatform.PreviewSupervisor;

namespace AIPlatform.Tests;

public sealed class HyperVPreviewIndependentSupervisorTests
{
    [Fact]
    public async Task CreatesSessionStopsExactLeaseAndRejectsCreateReplay()
    {
        using var sandbox = new TestDirectory();
        using var journal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal"));
        var control = new FakeRecoverableVmControl();
        await using var coordinator = new SupervisedPreviewCoordinator(control, journal);
        await coordinator.InitializeAsync();
        var create = CreateRequest(SupervisorCommand.CreateSession);

        var created = await coordinator.HandleAsync(create);

        Assert.True(created.Accepted);
        Assert.Equal(SupervisedSessionState.Running, created.Session!.State);
        Assert.Equal(1, control.CreateCalls);
        await Assert.ThrowsAsync<SupervisorProtocolException>(() => coordinator.HandleAsync(create));

        var stop = new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.Stop,
            created.Session.SessionId,
            1);
        var stopped = await coordinator.HandleAsync(stop);

        Assert.True(stopped.Accepted);
        Assert.Equal(SupervisedSessionState.Stopped, stopped.Session!.State);
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await control.GetStateAsync(control.Lease!, default));
    }

    [Fact]
    public async Task AbsoluteExpiryIsNotExtendedByHeartbeatAndRejectsReactivation()
    {
        using var sandbox = new TestDirectory();
        using var journal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal"));
        var clock = new ManualSupervisorClock();
        var control = new FakeRecoverableVmControl();
        await using var coordinator = new SupervisedPreviewCoordinator(
            control,
            journal,
            clock,
            new SupervisorOptions(TimeSpan.FromSeconds(10)));
        await coordinator.InitializeAsync();
        var created = await coordinator.HandleAsync(CreateRequest(SupervisorCommand.CreateSession));
        var sessionId = created.Session!.SessionId;

        clock.Advance(TimeSpan.FromSeconds(1));
        var heartbeat = new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.Heartbeat,
            sessionId,
            1);
        Assert.True((await coordinator.HandleAsync(heartbeat)).Accepted);
        clock.Advance(TimeSpan.FromSeconds(9));
        await WaitForStateAsync(coordinator, sessionId, SupervisedSessionState.Expired);

        var reactivation = heartbeat with
        {
            RequestId = Guid.NewGuid(),
            Command = SupervisorCommand.Heartbeat,
            Sequence = 2,
        };
        await Assert.ThrowsAsync<SupervisorProtocolException>(() => coordinator.HandleAsync(reactivation));
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await control.GetStateAsync(control.Lease!, default));
    }

    [Fact]
    public async Task ExpiredSessionCannotReserveAChannel()
    {
        using var sandbox = new TestDirectory();
        using var journal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal"));
        var clock = new ManualSupervisorClock();
        var control = new FakeRecoverableVmControl();
        await using var coordinator = new SupervisedPreviewCoordinator(
            control,
            journal,
            clock,
            new SupervisorOptions(TimeSpan.FromSeconds(10)));
        await coordinator.InitializeAsync();
        var created = await coordinator.HandleAsync(CreateRequest(SupervisorCommand.CreateSession));
        var sessionId = created.Session!.SessionId;

        clock.Advance(TimeSpan.FromSeconds(10));
        await WaitForStateAsync(coordinator, sessionId, SupervisedSessionState.Expired);
        var reservation = new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.ReserveSocketChannel,
            sessionId,
            1,
            control.Lease,
            HyperVPreviewChannelRole.Control);

        var error = await Assert.ThrowsAsync<SupervisorProtocolException>(
            () => coordinator.HandleAsync(reservation));
        Assert.Equal("SessionNotRunning", error.ErrorCode);
        Assert.Empty(Assert.Single(journal.ReadLatest()).ReservedChannels);
    }

    [Fact]
    public async Task SupervisorRestartRecoversOrphanedSessionAndRetainsJournalHistory()
    {
        using var sandbox = new TestDirectory();
        var journalPath = sandbox.File("sessions.journal");
        var control = new FakeRecoverableVmControl();
        var firstJournal = DurableSupervisorJournal.Open(journalPath);
        var first = new SupervisedPreviewCoordinator(
            control,
            firstJournal,
            options: new SupervisorOptions(TimeSpan.FromSeconds(30)));
        await first.InitializeAsync();
        var created = await first.HandleAsync(CreateRequest(SupervisorCommand.CreateSession));
        Assert.Equal(HyperVPreviewVmState.Running, await control.GetStateAsync(control.Lease!, default));

        await first.DisposeAsync();
        firstJournal.Dispose();

        using var secondJournal = DurableSupervisorJournal.Open(journalPath);
        await using var restarted = new SupervisedPreviewCoordinator(
            control,
            secondJournal,
            options: new SupervisorOptions(TimeSpan.FromSeconds(30)));
        await restarted.InitializeAsync();

        var status = await restarted.HandleAsync(new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.GetStatus,
            created.Session!.SessionId,
            0));
        Assert.Equal(SupervisedSessionState.Stopped, status.Session!.State);
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await control.GetStateAsync(control.Lease!, default));
        Assert.True(secondJournal.ReadLatest().Single().Revision > 1);
    }

    [Fact]
    public async Task RebootDuringActiveSessionExpiresItEvenWhenUptimeHasPassedItsOldStartValue()
    {
        using var sandbox = new TestDirectory();
        var path = sandbox.File("sessions.journal");
        var clock = new ManualSupervisorClock();
        var control = new FakeRecoverableVmControl();
        var firstJournal = DurableSupervisorJournal.Open(path);
        var first = new SupervisedPreviewCoordinator(
            control,
            firstJournal,
            clock,
            new SupervisorOptions(TimeSpan.FromSeconds(30)));
        await first.InitializeAsync();
        var created = await first.HandleAsync(CreateRequest(SupervisorCommand.CreateSession));
        await first.DisposeAsync();
        firstJournal.Dispose();

        clock.SimulateRebootWithHigherUptime();
        using var recoveredJournal = DurableSupervisorJournal.Open(path);
        await using var restarted = new SupervisedPreviewCoordinator(
            control,
            recoveredJournal,
            clock,
            new SupervisorOptions(TimeSpan.FromSeconds(30)));
        await restarted.InitializeAsync();

        var status = await restarted.HandleAsync(new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.GetStatus,
            created.Session!.SessionId,
            0));
        Assert.Equal(SupervisedSessionState.Expired, status.Session!.State);
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await control.GetStateAsync(control.Lease!, default));
    }

    [Fact]
    public async Task ExpiredProvisioningCancelsAdapterAndCleansPartialAllocation()
    {
        using var sandbox = new TestDirectory();
        using var journal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal"));
        var control = new FakeRecoverableVmControl { WaitForCreateCancellation = true };
        await using var coordinator = new SupervisedPreviewCoordinator(
            control,
            journal,
            options: new SupervisorOptions(TimeSpan.FromMilliseconds(150)));
        await coordinator.InitializeAsync();
        var timeout = Stopwatch.StartNew();

        var response = await coordinator.HandleAsync(CreateRequest(SupervisorCommand.CreateSession));

        Assert.False(response.Accepted);
        Assert.Equal(SupervisedSessionState.Expired, response.Session!.State);
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await control.GetStateAsync(control.Lease!, default));
        Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CleanupFailureRemainsRecordedAndCanBeRetriedWithNextSequence()
    {
        using var sandbox = new TestDirectory();
        using var journal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal"));
        var control = new FakeRecoverableVmControl { DestroyFailuresRemaining = 1 };
        await using var coordinator = new SupervisedPreviewCoordinator(control, journal);
        await coordinator.InitializeAsync();
        var created = await coordinator.HandleAsync(CreateRequest(SupervisorCommand.CreateSession));

        var failed = await coordinator.HandleAsync(new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.Stop,
            created.Session!.SessionId,
            1));
        Assert.False(failed.Accepted);
        Assert.Equal(SupervisedSessionState.CleanupFailed, failed.Session!.State);
        Assert.Equal(SupervisorCleanupFailure.DestroyUnconfirmed, failed.Session.CleanupFailure);
        Assert.NotEqual(HyperVPreviewVmState.Destroyed, await control.GetStateAsync(control.Lease!, default));

        var retried = await coordinator.HandleAsync(new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.RetryCleanup,
            created.Session.SessionId,
            2));
        Assert.True(retried.Accepted);
        Assert.Equal(SupervisedSessionState.Stopped, retried.Session!.State);
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await control.GetStateAsync(control.Lease!, default));
    }

    [Fact]
    public async Task ExpiredCleanupFailureCanBeRetriedAfterTheCleanupMargin()
    {
        using var sandbox = new TestDirectory();
        using var journal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal"));
        var clock = new ManualSupervisorClock();
        var control = new FakeRecoverableVmControl { DestroyFailuresRemaining = 1 };
        await using var coordinator = new SupervisedPreviewCoordinator(
            control, journal, clock, new SupervisorOptions(TimeSpan.FromSeconds(1)));
        await coordinator.InitializeAsync();
        var created = await coordinator.HandleAsync(CreateRequest(SupervisorCommand.CreateSession));
        var sessionId = created.Session!.SessionId;

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForStateAsync(coordinator, sessionId, SupervisedSessionState.CleanupFailed);
        clock.Advance(SupervisorPolicy.CleanupMargin + TimeSpan.FromSeconds(1));

        var retry = await coordinator.HandleAsync(new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.RetryCleanup,
            sessionId,
            1));

        Assert.True(retry.Accepted);
        Assert.Equal(SupervisedSessionState.Expired, retry.Session!.State);
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await control.GetStateAsync(control.Lease!, default));
    }

    [Fact]
    public async Task EscalatesToForcedStopAndConfirmsDestroyedBeforeTerminalState()
    {
        using var sandbox = new TestDirectory();
        using var journal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal"));
        var control = new FakeRecoverableVmControl { GracefulStopLeavesRunning = true };
        await using var coordinator = new SupervisedPreviewCoordinator(control, journal);
        await coordinator.InitializeAsync();
        var created = await coordinator.HandleAsync(CreateRequest(SupervisorCommand.CreateSession));

        var stopped = await coordinator.HandleAsync(new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.Stop,
            created.Session!.SessionId,
            1));

        Assert.True(stopped.Accepted);
        Assert.Equal(SupervisedSessionState.Stopped, stopped.Session!.State);
        Assert.Equal(
            new[] { HyperVPreviewStopMode.Graceful, HyperVPreviewStopMode.Force },
            control.StopModes);
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await control.GetStateAsync(control.Lease!, default));
    }

    [Fact]
    public async Task ConcurrentCreatesCannotAllocateTwoSessions()
    {
        using var sandbox = new TestDirectory();
        using var journal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal"));
        var control = new FakeRecoverableVmControl();
        await using var coordinator = new SupervisedPreviewCoordinator(control, journal);
        await coordinator.InitializeAsync();

        var responses = await Task.WhenAll(
            CaptureAsync(coordinator, CreateRequest(SupervisorCommand.CreateSession)),
            CaptureAsync(coordinator, CreateRequest(SupervisorCommand.CreateSession)));

        Assert.Single(responses, response => response.Accepted);
        Assert.Single(responses, response => response.ErrorCode == "SessionAlreadyActive");
        Assert.Equal(1, control.CreateCalls);
    }

    [Fact]
    public async Task IpcCodecRejectsUnknownTtlDuplicateFieldsAndOversizedFrames()
    {
        var requestId = Guid.NewGuid();
        var ttlRequest = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"{{requestId}}","command":"CreateSession","sessionId":null,"sequence":0,"ttlSeconds":999}""");
        Assert.Throws<System.Text.Json.JsonException>(() => SupervisorIpcCodec.DecodeRequest(ttlRequest));
        Assert.Throws<SupervisorProtocolException>(() => SupervisorIpcCodec.DecodeRequest(
            System.Text.Encoding.UTF8.GetBytes(
                $$"""{"version":1,"requestId":"{{requestId}}","command":99,"sessionId":null,"sequence":0}""")));

        var duplicateField = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"{{requestId}}","command":"CreateSession","sessionId":null,"sequence":0,"sequence":1}""");
        Assert.Throws<InvalidDataException>(() => SupervisorIpcCodec.DecodeRequest(duplicateField));

        var oversizedFrame = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            oversizedFrame,
            SupervisorPolicy.MaximumIpcFrameBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SupervisorIpcCodec.ReadFrameAsync(new MemoryStream(oversizedFrame), default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SupervisorOptions(TimeSpan.FromSeconds(171)).Validate());
    }

    [Fact]
    public void IpcCodecRequiresAnExactLeaseAndKnownRoleForChannelReservations()
    {
        var sessionId = Guid.NewGuid();
        var lease = new HyperVPreviewVmLease(sessionId, Guid.NewGuid(), "managed-test-resource");
        var request = new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.ReserveSocketChannel,
            sessionId,
            1,
            lease,
            HyperVPreviewChannelRole.Control);

        var decoded = SupervisorIpcCodec.DecodeRequest(SupervisorIpcCodec.EncodeRequest(request));
        Assert.Equal(lease, decoded.Lease);
        Assert.Equal(HyperVPreviewChannelRole.Control, decoded.ChannelRole);

        Assert.Throws<SupervisorProtocolException>(() =>
            SupervisorIpcCodec.EncodeRequest(request with { Lease = null }));
        Assert.Throws<SupervisorProtocolException>(() =>
            SupervisorIpcCodec.EncodeRequest(request with { ChannelRole = null }));
        Assert.Throws<SupervisorProtocolException>(() =>
            SupervisorIpcCodec.EncodeRequest(request with
            {
                Lease = lease with { SessionId = Guid.NewGuid() },
            }));
        Assert.Throws<SupervisorProtocolException>(() =>
            SupervisorIpcCodec.DecodeRequest(
                System.Text.Encoding.UTF8.GetBytes(
                    $$"""{"version":1,"requestId":"{{Guid.NewGuid()}}","command":"GetStatus","sessionId":"{{sessionId}}","sequence":0}""")));
    }

    [Fact]
    public async Task SupervisorNamedPipeAuthorizerPersistsAndSequencesChannelReservations()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var sandbox = new TestDirectory();
        var journalPath = sandbox.File("sessions.journal");
        var journal = DurableSupervisorJournal.Open(journalPath);
        var control = new FakeRecoverableVmControl();
        var coordinator = new SupervisedPreviewCoordinator(control, journal);
        await coordinator.InitializeAsync();
        var sessionId = (await coordinator.HandleAsync(
            CreateRequest(SupervisorCommand.CreateSession))).Session!.SessionId;
        var lease = control.Lease!;
        var pipeName = "aiplatform-test-" + Guid.NewGuid().ToString("N");
        using var serverCancellation = new CancellationTokenSource();
        var serverTask = new SupervisorNamedPipeServer(pipeName, coordinator)
            .RunAsync(serverCancellation.Token);

        try
        {
            var authorizer = new SupervisorNamedPipeLeaseAuthorizer(pipeName);
            var reservations = await Task.WhenAll(
                authorizer.TryAuthorizeAndReserveAsync(
                    lease, HyperVPreviewChannelRole.Control, default),
                authorizer.TryAuthorizeAndReserveAsync(
                    lease, HyperVPreviewChannelRole.Frames, default));
            Assert.All(reservations, accepted => Assert.True(accepted));

            var record = Assert.Single(journal.ReadLatest());
            Assert.Equal(2UL, record.LastCommandSequence);
            Assert.Equal(
                new[] { HyperVPreviewChannelRole.Control, HyperVPreviewChannelRole.Frames },
                record.ReservedChannels.OrderBy(role => role));
            Assert.False(await authorizer.TryAuthorizeAndReserveAsync(
                lease, HyperVPreviewChannelRole.Control, default));
            Assert.False(await authorizer.TryAuthorizeAndReserveAsync(
                lease with { ResourceId = "forged-resource" },
                HyperVPreviewChannelRole.Control,
                default));
            Assert.Equal(2UL, Assert.Single(journal.ReadLatest()).LastCommandSequence);
        }
        finally
        {
            serverCancellation.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested)
            {
            }
            await coordinator.DisposeAsync();
            journal.Dispose();
        }

        using var recoveredJournal = DurableSupervisorJournal.Open(journalPath);
        await using var restarted = new SupervisedPreviewCoordinator(control, recoveredJournal);
        await restarted.InitializeAsync();
        var recovered = await restarted.HandleAsync(new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.GetStatus,
            sessionId,
            0));
        Assert.Equal(SupervisedSessionState.Stopped, recovered.Session!.State);
        Assert.Equal(
            new[] { HyperVPreviewChannelRole.Control, HyperVPreviewChannelRole.Frames },
            recovered.Session.ReservedChannels.OrderBy(role => role));
    }

    [Fact]
    public async Task IpcRejectsReplayUnknownSessionsAndOutOfSequenceCommands()
    {
        using var sandbox = new TestDirectory();
        using var journal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal"));
        var control = new FakeRecoverableVmControl();
        await using var coordinator = new SupervisedPreviewCoordinator(control, journal);
        await coordinator.InitializeAsync();
        var created = await coordinator.HandleAsync(CreateRequest(SupervisorCommand.CreateSession));
        var sessionId = created.Session!.SessionId;
        var heartbeat = new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.Heartbeat,
            sessionId,
            1);
        Assert.True((await coordinator.HandleAsync(heartbeat)).Accepted);
        await Assert.ThrowsAsync<SupervisorProtocolException>(() => coordinator.HandleAsync(heartbeat));
        await Assert.ThrowsAsync<SupervisorProtocolException>(() => coordinator.HandleAsync(heartbeat with
        {
            RequestId = Guid.NewGuid(),
            Sequence = 3,
        }));
        await Assert.ThrowsAsync<SupervisorProtocolException>(() => coordinator.HandleAsync(new SupervisorIpcRequest(
            SupervisorPolicy.ProtocolVersion,
            Guid.NewGuid(),
            SupervisorCommand.GetStatus,
            Guid.NewGuid(),
            0)));
    }

    [Fact]
    public async Task IncompleteJournalTailIsDiscardedWithoutLosingCommittedSession()
    {
        using var sandbox = new TestDirectory();
        var path = sandbox.File("sessions.journal");
        var journal = DurableSupervisorJournal.Open(path);
        var coordinator = new SupervisedPreviewCoordinator(
            new FakeRecoverableVmControl(),
            journal);
        await coordinator.InitializeAsync();
        var response = await coordinator.HandleAsync(CreateRequest(SupervisorCommand.CreateSession));
        await coordinator.DisposeAsync();
        journal.Dispose();
        var committedLength = new FileInfo(path).Length;

        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            stream.Write([0xA1, 0xB2]);

        using var recoveredJournal = DurableSupervisorJournal.Open(path);
        Assert.Equal(response.Session!.SessionId, Assert.Single(recoveredJournal.ReadLatest()).SessionId);
        Assert.Equal(committedLength, new FileInfo(path).Length);
    }

    [Fact]
    public void JournalAllowsOnlyOneSupervisorOwnerAtATime()
    {
        using var sandbox = new TestDirectory();
        using var journal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal"));
        Assert.Throws<IOException>(() => DurableSupervisorJournal.Open(sandbox.File("sessions.journal")));
    }

    [Fact]
    public void JournalLoadsPreReservationRecordsWithNoChannelsReserved()
    {
        using var sandbox = new TestDirectory();
        var path = sandbox.File("sessions.journal");
        var createdAt = DateTimeOffset.UnixEpoch;
        using (var journal = DurableSupervisorJournal.Open(path))
        {
            journal.Append(new SupervisorSessionRecord(
                0,
                0,
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                SupervisedSessionState.Stopped,
                createdAt,
                createdAt + TimeSpan.FromMinutes(1),
                createdAt - TimeSpan.FromHours(1),
                1000,
                61000,
                0,
                [],
                0,
                SupervisorTerminalState.Stopped,
                null));
        }

        var originalFrame = File.ReadAllBytes(path);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(originalFrame);
        var legacyRecord = JsonNode.Parse(originalFrame.AsSpan(sizeof(int), payloadLength).ToArray())!
            .AsObject();
        legacyRecord.Remove("reservedChannels");
        var legacyPayload = JsonSerializer.SerializeToUtf8Bytes(legacyRecord);
        var legacyFrame = new byte[sizeof(int) + legacyPayload.Length + SHA256.HashSizeInBytes];
        BinaryPrimitives.WriteInt32LittleEndian(legacyFrame, legacyPayload.Length);
        legacyPayload.CopyTo(legacyFrame.AsSpan(sizeof(int)));
        SHA256.HashData(legacyPayload).CopyTo(
            legacyFrame.AsSpan(sizeof(int) + legacyPayload.Length));
        File.WriteAllBytes(path, legacyFrame);

        using var recoveredJournal = DurableSupervisorJournal.Open(path);
        Assert.Empty(Assert.Single(recoveredJournal.ReadLatest()).ReservedChannels);
    }

    [Fact]
    public async Task SeparateSupervisorProcessOutlivesDisconnectedClientAndExpiresSession()
    {
        using var sandbox = new TestDirectory();
        var pipeName = "aiplatform-test-" + Guid.NewGuid().ToString("N");
        using var process = StartSupervisor(sandbox, pipeName, ttlMilliseconds: 1200);
        Process? simulatedStudio = null;
        try
        {
            await WaitUntilReadyAsync(process);
            simulatedStudio = StartSimulatedClient(pipeName);
            var sessionText = await simulatedStudio.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Guid.TryParseExact(sessionText, "N", out var sessionId));
            simulatedStudio.Kill();
            await simulatedStudio.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(process.HasExited);

            await WaitForStateThroughPipeAsync(
                process,
                pipeName,
                sessionId,
                SupervisedSessionState.Expired);
        }
        finally
        {
            KillAndWait(process);
            if (simulatedStudio is not null)
                KillAndWait(simulatedStudio);
        }
    }

    [Fact]
    public async Task ExecutableRefusesToRunWithoutExplicitFakeAdapterMode()
    {
        var assemblyPath = typeof(SupervisorNamedPipeServer).Assembly.Location;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { assemblyPath },
        }) ?? throw new InvalidOperationException("Could not start the supervisor executable.");

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(78, process.ExitCode);
        Assert.Contains("No Hyper-V adapter is included", await process.StandardError.ReadToEndAsync());
    }

    [Fact]
    public async Task SupervisorChildOutlivesSimulatedStudioProcessCrash()
    {
        using var sandbox = new TestDirectory();
        var pipeName = "aiplatform-test-" + Guid.NewGuid().ToString("N");
        using var simulatedStudio = StartSimulatedStudio(sandbox, pipeName, ttlMilliseconds: 1200);
        Process? supervisor = null;
        try
        {
            var supervisorLine = await simulatedStudio.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            var sessionLine = await simulatedStudio.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.StartsWith("SUPERVISOR_PID ", supervisorLine);
            Assert.StartsWith("SESSION ", sessionLine);
            var supervisorId = int.Parse(supervisorLine!["SUPERVISOR_PID ".Length..]);
            var sessionId = Guid.ParseExact(sessionLine!["SESSION ".Length..], "N");
            supervisor = Process.GetProcessById(supervisorId);

            simulatedStudio.Kill();
            await simulatedStudio.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(supervisor.HasExited);
            await WaitForStateThroughPipeAsync(
                supervisor,
                pipeName,
                sessionId,
                SupervisedSessionState.Expired);
        }
        finally
        {
            KillAndWait(simulatedStudio);
            if (supervisor is not null)
                KillAndWait(supervisor);
        }
    }

    [Fact]
    public async Task SeparateSupervisorProcessCleansOrphanAfterAbruptSupervisorRestart()
    {
        using var sandbox = new TestDirectory();
        var pipeName = "aiplatform-test-" + Guid.NewGuid().ToString("N");
        using var first = StartSupervisor(sandbox, pipeName, ttlMilliseconds: 30000);
        Process? restarted = null;
        try
        {
            await WaitUntilReadyAsync(first);
            var created = await SupervisorNamedPipeClient.SendAsync(
                pipeName,
                CreateRequest(SupervisorCommand.CreateSession));
            Assert.True(created.Accepted);

            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            restarted = StartSupervisor(sandbox, pipeName, ttlMilliseconds: 30000);
            await WaitUntilReadyAsync(restarted);
            var status = await SupervisorNamedPipeClient.SendAsync(
                pipeName,
                new SupervisorIpcRequest(
                    SupervisorPolicy.ProtocolVersion,
                    Guid.NewGuid(),
                    SupervisorCommand.GetStatus,
                    created.Session!.SessionId,
                    0));

            Assert.Equal(SupervisedSessionState.Stopped, status.Session!.State);
            using var resources = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllBytesAsync(sandbox.File("resources.json")));
            var resource = resources.RootElement.EnumerateObject().Single().Value;
            Assert.Equal((int)HyperVPreviewVmState.Destroyed, resource.GetProperty("state").GetInt32());
        }
        finally
        {
            KillAndWait(first);
            if (restarted is not null)
                KillAndWait(restarted);
        }
    }

    [Fact]
    public async Task RestartRecoversResourceCreatedAfterDurableIntentButBeforeLeaseWasJournaled()
    {
        using var sandbox = new TestDirectory();
        var pipeName = "aiplatform-test-" + Guid.NewGuid().ToString("N");
        using var first = StartSupervisor(
            sandbox,
            pipeName,
            ttlMilliseconds: 30000,
            crashAfterCreate: true);
        Process? restarted = null;
        try
        {
            await WaitUntilReadyAsync(first);
            await Assert.ThrowsAnyAsync<IOException>(() => SupervisorNamedPipeClient.SendAsync(
                pipeName,
                CreateRequest(SupervisorCommand.CreateSession)));
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(86, first.ExitCode);

            SupervisorSessionRecord record;
            using (var recoveredJournal = DurableSupervisorJournal.Open(sandbox.File("sessions.journal")))
                record = Assert.Single(recoveredJournal.ReadLatest());
            restarted = StartSupervisor(sandbox, pipeName, ttlMilliseconds: 30000);
            await WaitUntilReadyAsync(restarted);

            var status = await SupervisorNamedPipeClient.SendAsync(
                pipeName,
                new SupervisorIpcRequest(
                    SupervisorPolicy.ProtocolVersion,
                    Guid.NewGuid(),
                    SupervisorCommand.GetStatus,
                    record.SessionId,
                    0));
            Assert.Equal(SupervisedSessionState.Stopped, status.Session!.State);
            using var resources = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllBytesAsync(sandbox.File("resources.json")));
            var resource = resources.RootElement.EnumerateObject().Single().Value;
            Assert.Equal((int)HyperVPreviewVmState.Destroyed, resource.GetProperty("state").GetInt32());
        }
        finally
        {
            KillAndWait(first);
            if (restarted is not null)
                KillAndWait(restarted);
        }
    }

    private static SupervisorIpcRequest CreateRequest(SupervisorCommand command) =>
        new(SupervisorPolicy.ProtocolVersion, Guid.NewGuid(), command, null, 0);

    private static async Task<SupervisorIpcResponse> CaptureAsync(
        SupervisedPreviewCoordinator coordinator,
        SupervisorIpcRequest request)
    {
        try
        {
            return await coordinator.HandleAsync(request);
        }
        catch (SupervisorProtocolException error)
        {
            return new SupervisorIpcResponse(
                SupervisorPolicy.ProtocolVersion,
                request.RequestId,
                false,
                error.ErrorCode,
                null);
        }
    }

    private static async Task WaitForStateAsync(
        SupervisedPreviewCoordinator coordinator,
        Guid sessionId,
        SupervisedSessionState expected)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            var response = await coordinator.HandleAsync(new SupervisorIpcRequest(
                SupervisorPolicy.ProtocolVersion,
                Guid.NewGuid(),
                SupervisorCommand.GetStatus,
                sessionId,
                0));
            if (response.Session!.State == expected)
                return;
            await Task.Delay(10);
        }
        Assert.Fail($"Session did not reach expected state {expected}.");
    }

    private static Process StartSupervisor(
        TestDirectory sandbox,
        string pipeName,
        int ttlMilliseconds,
        bool crashAfterCreate = false)
    {
        var assemblyPath = typeof(SupervisorNamedPipeServer).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                assemblyPath,
                "--simulate",
                "--journal",
                sandbox.File("sessions.journal"),
                "--resource-store",
                sandbox.File("resources.json"),
                "--pipe-name",
                pipeName,
                "--test-ttl-ms",
                ttlMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
        if (crashAfterCreate)
            startInfo.ArgumentList.Add("--simulate-crash-after-create");
        var process = Process.Start(startInfo);
        return process ?? throw new InvalidOperationException("Could not start the simulated supervisor process.");
    }

    private static Process StartSimulatedClient(string pipeName)
    {
        var assemblyPath = typeof(SupervisorNamedPipeServer).Assembly.Location;
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { assemblyPath, "--simulate-client", pipeName },
        });
        return process ?? throw new InvalidOperationException("Could not start the simulated Studio client.");
    }

    private static Process StartSimulatedStudio(
        TestDirectory sandbox,
        string pipeName,
        int ttlMilliseconds)
    {
        var assemblyPath = typeof(SupervisorNamedPipeServer).Assembly.Location;
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                assemblyPath,
                "--simulate-studio",
                assemblyPath,
                sandbox.File("sessions.journal"),
                sandbox.File("resources.json"),
                pipeName,
                ttlMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        });
        return process ?? throw new InvalidOperationException("Could not start the simulated Studio process.");
    }

    private static async Task WaitUntilReadyAsync(Process process)
    {
        var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("READY ", ready);
    }

    private static void KillAndWait(Process process)
    {
        if (process.HasExited)
            return;
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }

    private static async Task WaitForStateThroughPipeAsync(
        Process process,
        string pipeName,
        Guid sessionId,
        SupervisedSessionState expected)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (process.HasExited)
                Assert.Fail($"Supervisor exited unexpectedly: {await process.StandardError.ReadToEndAsync()}");
            try
            {
                var response = await SupervisorNamedPipeClient.SendAsync(
                    pipeName,
                    new SupervisorIpcRequest(
                        SupervisorPolicy.ProtocolVersion,
                        Guid.NewGuid(),
                        SupervisorCommand.GetStatus,
                        sessionId,
                        0));
                if (response.Session?.State == expected)
                    return;
            }
            catch (IOException)
            {
            }
            await Task.Delay(50);
        }
        Assert.Fail($"Separate supervisor did not reach expected state {expected}.");
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "AIPlatformSupervisorTests", Guid.NewGuid().ToString("N"));

        public TestDirectory() => Directory.CreateDirectory(_path);

        public string File(string name) => Path.Combine(_path, name);

        public void Dispose()
        {
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class ManualSupervisorClock : ISupervisorClock
    {
        private readonly List<ManualTimer> _timers = [];
        private long _uptimeMilliseconds = 10_000;

        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public long UptimeMilliseconds => _uptimeMilliseconds;
        public DateTimeOffset BootEpochUtc => UtcNow - TimeSpan.FromMilliseconds(UptimeMilliseconds);

        public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            UtcNow += amount;
            _uptimeMilliseconds += (long)amount.TotalMilliseconds;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue(_uptimeMilliseconds);
        }

        public void SimulateRebootWithHigherUptime() => _uptimeMilliseconds += 10_000;

        private sealed class ManualTimer(
            ManualSupervisorClock clock,
            TimerCallback callback,
            object? state) : ITimer
        {
            private long? _dueAt;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                    return false;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : clock.UptimeMilliseconds + (long)dueTime.TotalMilliseconds;
                return true;
            }

            public void Dispose()
            {
                _disposed = true;
                _dueAt = null;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool FireIfDue(long now)
            {
                if (_disposed || _dueAt is not { } dueAt || dueAt > now)
                    return false;
                _dueAt = null;
                callback(state);
                return true;
            }
        }
    }

    private sealed class FakeRecoverableVmControl : IRecoverableHyperVVmControl
    {
        private readonly Dictionary<Guid, (HyperVPreviewVmLease Lease, HyperVPreviewVmState State)> _resources = [];

        public HyperVPreviewVmLease? Lease { get; private set; }
        public int CreateCalls { get; private set; }
        public int DestroyFailuresRemaining { get; set; }
        public bool GracefulStopLeavesRunning { get; init; }
        public bool WaitForCreateCancellation { get; init; }
        public List<HyperVPreviewStopMode> StopModes { get; } = [];

        public async Task<HyperVPreviewVmLease> CreateAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            CreateCalls++;
            var lease = new HyperVPreviewVmLease(sessionId, Guid.NewGuid(), $"fake:{sessionId:N}");
            Lease = lease;
            _resources.Add(sessionId, (lease, HyperVPreviewVmState.Created));
            if (WaitForCreateCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return lease;
        }

        public Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            SetState(lease, HyperVPreviewVmState.Running);
            return Task.CompletedTask;
        }

        public Task StopAsync(
            HyperVPreviewVmLease lease,
            HyperVPreviewStopMode mode,
            CancellationToken cancellationToken)
        {
            StopModes.Add(mode);
            if (mode == HyperVPreviewStopMode.Force || !GracefulStopLeavesRunning)
                SetState(lease, HyperVPreviewVmState.Stopped);
            return Task.CompletedTask;
        }

        public Task DestroyAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            if (DestroyFailuresRemaining > 0)
            {
                DestroyFailuresRemaining--;
                return Task.CompletedTask;
            }
            SetState(lease, HyperVPreviewVmState.Destroyed);
            return Task.CompletedTask;
        }

        public Task<HyperVPreviewVmState> GetStateAsync(
            HyperVPreviewVmLease lease,
            CancellationToken cancellationToken)
        {
            if (!_resources.TryGetValue(lease.SessionId, out var item) || item.Lease != lease)
                throw new InvalidOperationException("Unknown fake VM lease.");
            return Task.FromResult(item.State);
        }

        public Task<HyperVPreviewVmLease?> FindBySessionIdAsync(
            Guid sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_resources.TryGetValue(sessionId, out var item)
                ? item.Lease
                : null);

        private void SetState(HyperVPreviewVmLease lease, HyperVPreviewVmState state)
        {
            if (!_resources.TryGetValue(lease.SessionId, out var item) || item.Lease != lease)
                throw new InvalidOperationException("Unknown fake VM lease.");
            _resources[lease.SessionId] = (lease, state);
        }
    }
}
