# MVP 6.5-G Implementation

## Scope and safety

This is an offline implementation increment only. No Linux appliance was built,
no VM or HvSocket service was created, and Docker, Ollama, generated projects,
Preview authorization, and privileged Windows operations were not run.
HTTP 503 remains enabled. `PreviewPlan` schema 1, its Docker image digest,
`IsolationVerified`, the canonical Verified v9 files, and Preview endpoint
registration were not changed.

The requested `MVP-6.5-G-HANDOFF.md` was not present in the repository. Work
used the available MVP 6.5-E/F/G documents, particularly the 6.5-F protocol
and lifecycle constraints.

## Files changed

- `tools/HyperVApplianceBuild/inputs.mjs`
- `tools/HyperVApplianceBuild/inputs.example.json`
- `tools/HyperVApplianceBuild/inputs.test.mjs`
- `tools/HyperVApplianceBuild/build-offline.sh`
- `AIPlatform.Core/Studio/HyperVPreviewContracts.cs` (new)
- `AIPlatform.Tests/HyperVPreviewContractsTests.cs` (new)
- `MVP-6.5-G-IMPLEMENTATION.md` (new)

## Appliance lock contract

The input verifier now:

- Requires an exact top-level lock shape and exact properties for nested
  entries; duplicate JSON object keys and trailing content are rejected.
- Validates the lock digest before parsing, constrains paths to relative,
  traversal-free ASCII paths, rejects symlinked path components and rejects
  unlisted/non-regular bundle entries.
- Requires both expected Debian repositories, their pinned signer fingerprints,
  signed `InRelease` files, amd64 `Packages` indexes, and index hashes that
  match the bundle file inventory.
- Pins every locked package to a repository archive path, SHA-256, and version;
  validates the complete required package set, including matching Chromium
  package versions. The build still checks APT's signed repository metadata
  and the exact installed package closure.
- Requires an exact builder-tool inventory, constrained executable paths,
  syntactically valid version labels, and SHA-256 values. On the approved
  Linux builder, the executable bytes are checked against the pinned hashes.
- Requires all prototype component roles and tightly scoped overlay targets.
- Verifies GPG `VALIDSIG` status against the independently pinned signer
  fingerprints. The offline build script now calls this verifier before using
  repository contents.

`inputs.example.json` deliberately has null/empty values for unknown data and
remains incomplete and invalid. Synthetic versions and hashes in unit fixtures
are test data only, not candidate Debian inputs.

Limitations: builder-tool version labels are format-checked but are not queried
from each executable; tool identity is enforced by its binary SHA-256. A complete
reviewed lock, official bundle, key fingerprints, package closure, and final
component hashes still need to be collected and independently approved. No
signature verification was performed against a real Debian keyring here.

## Hyper-V contracts and controller

The new Core file is isolated from the production Preview runtime and DI. It
defines:

- VM runtime, supervisor, HvSocket broker, and typed guest-agent interfaces.
- Per-session lease identity, power states, lifecycle states, and legal state
  transitions.
- A one-session controller that provisions, arms the supervisor for the
  180-second ceiling before start, limits readiness to 90 seconds, and cleans up
  after failed readiness or an explicit stop.
- Cleanup that closes the guest agent, requests graceful stop, escalates to
  forced stop, requires confirmed power-off before removal, and disarms the
  supervisor. Cleanup failures are surfaced and leave the session
  `CleanupFailed`, blocking another session.
- Separate random 32-byte control/frame challenges, typed input and frame
  contracts, and the 6.5-F protocol size/rate constants.

The real Hyper-V adapter, HvSocket transport/48-byte codec, independent
supervisor process, Linux guest agent, Weston module, viewer, and production
timeout enforcement remain unimplemented. The controller's interfaces and
behavior were tested only with in-memory fakes; no Windows resource is touched.
A production supervisor must enforce the 180-second ceiling independently of
the Studio process, and the adapter must make provisioning cancellation-safe
and operate only on the returned exact VM lease.

## Validation

- `node --test .\\tools\\HyperVApplianceBuild\\inputs.test.mjs`: **10 passed**.
- `dotnet test .\\AIPlatform.Tests\\AIPlatform.Tests.csproj --no-restore`: **357
  passed, 9 skipped, 0 failed**. The skipped tests are pre-existing Docker/manual
  integration cases; none were run against Docker.
- Focused Hyper-V contract tests: **5 passed**.
- Native Git Bash `-n` syntax check for `build-offline.sh`: passed. The script
  itself was not executed.
- `git diff --check`: passed.

These checks do not prove package provenance, a real signature, Hyper-V
isolation, guest networking, cleanup after host crash, or VM lifecycle timing.

## Remaining gates before the first Hyper-V start

1. Provide the missing handoff if it contains additional authoritative
   constraints; otherwise reconcile this report with the current E/F/G plans.
2. Produce a complete lock from reviewed, official inputs and verify all
   signatures, index/archive relationships, tool hashes, and final component
   versions. Do not populate the example with guessed data.
3. Implement and test the independent supervisor, the real two-channel broker
   and codec, the guest agent, and the Windows VM runtime. Exercise hostile
   frames, sequence/session mismatches, backpressure, process crash, forced
   stop, and exact-resource cleanup using fakes before any VM adapter is enabled.
4. Recheck RAM, Windows version, Hyper-V rights, and operator approvals in the
   execution context. Historical measurements or elevated shells from another
   session are not current authorization.
5. Submit a new canonical plan with real image/component digests, exact
   resource scope and tests for a separate explicit authorization. The 6.5-F
   plan remains non-executable and is not authorization.

Recommended next increment: implement and fuzz-test the broker message codec
and independent-supervisor state machine using only fake transports and fake
VM identities. Keep the production Preview path unavailable until those
components and the first isolated VM campaign have been separately reviewed
and authorized.
