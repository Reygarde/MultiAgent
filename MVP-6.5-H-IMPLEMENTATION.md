# MVP 6.5-H Implementation

## Scope and safety

This increment implements and tests Hyper-V Preview lifecycle and protocol
components using in-memory fakes only. No Hyper-V command, VM, HvSocket
connection, Docker, Ollama, Linux build, generated project, or privileged
operation was run.

The Hyper-V runtime and supervisor are not registered in production DI and are
not connected to the Preview endpoints. HTTP 503 remains active and
`IsolationVerified` remains false. `PreviewPlan` schema 1, its Docker digest,
and the canonical Verified v9 files were not changed.

## Files created or modified

- Updated `AIPlatform.Core/Studio/HyperVPreviewContracts.cs` with the VM-control
  abstraction, VM lifecycle state model, and the 170-second supervisory
  deadline used by the session controller. This file was introduced in MVP
  6.5-G; this increment extends it.
- Added `AIPlatform.Core/Studio/HyperVPreviewProtocol.cs`.
- Added `AIPlatform.Infrastructure/Studio/HyperVPreviewVmRuntime.cs`.
- Added `AIPlatform.Infrastructure/Studio/HyperVPreviewSupervisor.cs`.
- Updated `AIPlatform.Tests/HyperVPreviewContractsTests.cs`.
- Added `AIPlatform.Tests/HyperVPreviewProtocolTests.cs`.
- Added `AIPlatform.Tests/HyperVPreviewSupervisorTests.cs`.
- Added `AIPlatform.Tests/HyperVPreviewVmRuntimeTests.cs`.
- Added this report.

The uncommitted appliance-lock files and MVP 6.5-G report are pre-existing
worktree changes and were not modified by MVP 6.5-H.

## Implemented and tested

### VM runtime

- `IHyperVVmControl` defines create, start, stop, destroy, and state-query
  operations. Its contract requires rollback of partial creation if it cannot
  return the exact VM lease.
- The runtime serializes operations, validates lease identity, rejects reused
  VM IDs, and enforces legal transitions among `Created`, `Starting`, `Running`,
  `Stopping`, `Stopped`, `Failed`, and `Destroyed`.
- Stop and destroy are idempotent where the adapter reports the corresponding
  terminal state. Destruction is not reported as successful until the adapter
  confirms `Destroyed`; removal is rejected unless power-off is confirmed.
- Graceful-stop failure can be escalated to forced stop. Errors and unconfirmed
  states remain visible to the caller.

### Supervision and cleanup

- The supervisor uses an injected `TimeProvider`, accepts only positive
  lifetimes up to 180 seconds, invokes expiry once, reports callback failures,
  supports disarming, and disposes pending timers.
- The session controller arms its timer for 170 seconds before starting the VM
  and cleans up the associated lease on expiry. Cleanup closes/disposes the
  guest agent, attempts graceful then forced stop, verifies power-off and
  destruction, and disarms the supervisor. It reports resources released only
  when the guest agent is disposed, VM removal is confirmed, and disarming
  succeeds.
- Fake-clock tests cover expiry, cancellation, callback failure, disposal,
  startup cancellation/failure, and expiry-driven cleanup. Simulated failures
  are not evidence of recovery from an operating-system or Studio process
  crash.

### HvSocket protocol preparation

- Added a version-1 `AP65` binary codec with the planned 48-byte header,
  bounded payloads, session binding, positive sequence numbers, zeroed
  reserved bytes, and per-channel message-type restrictions.
- Added handshake validation bound to the exact session, VM, channel role, and
  distinct 32-byte challenges, plus a monotonic sequence guard.
- Deterministic malformed-input fuzz tests exercise 512 seeded byte sequences.
  No socket or transport implementation exists yet.

## Validation

- Focused Hyper-V tests:
  `dotnet test .\AIPlatform.Tests\AIPlatform.Tests.csproj --no-restore --filter "FullyQualifiedName~HyperVPreview"`
  — **25 passed, 0 failed**.
- Full backend test project:
  `dotnet test .\AIPlatform.Tests\AIPlatform.Tests.csproj --no-restore`
  — **377 passed, 9 skipped, 0 failed**.
- The skipped tests are pre-existing manual/Docker integration tests. No Docker
  or Ollama tests were activated.

The tests exercise managed code and fakes only; they do not establish actual
Hyper-V isolation, HvSocket behavior, guest networking restrictions, or
resource cleanup on a real host.

## Remaining limitations and gates

- There is no Windows Hyper-V adapter, real HvSocket broker, Linux guest agent,
  frame/input transport, or external supervisor process.
- The supervisor runs in-process and cannot guarantee cleanup after Studio
  crashes or the host shuts down.
- The 170-second timer is armed before VM start, leaving nominal margin under
  the 180-second ceiling, but callback scheduling and adapter stop/destroy
  operations are not independently bounded by a process or OS watchdog. This
  does **not** prove that the VM reaches confirmed `Off` before 180 seconds.
- The lifecycle and failure paths are verified only against fakes. A real
  adapter must validate exact VM identity, cancellation-safe provisioning,
  stop escalation, and deletion of the exact temporary resources.
- The real protocol still needs transport framing, authentication/channel
  establishment, backpressure, rate enforcement, and adversarial guest tests.
- No first VM start is authorized by this implementation. Before that step,
  implement and review the real adapter and independent watchdog, complete the
  appliance lock and artifact provenance, and obtain a separate explicit
  authorization with current resource and privilege checks.

Recommended next increment: implement the Windows VM-control adapter and an
independent supervisor behind testable command/process abstractions, without
production DI registration or a VM start. Add adapter contract tests for
partial creation, exact-resource deletion, cancellation, and a watchdog that
can confirm power-off independently of Studio.
