import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { gzipSync } from 'node:zlib'
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import {
  applyOverlay,
  compareBuildManifests,
  enablePreviewSessionTarget,
  parsePackageIndex,
  verifyEspBootConfiguration,
  verifyGuestBootConfiguration,
  verifyImageInfo,
  verifyImageMap,
  verifyInputs,
  verifyInstalled,
  verifyLinuxX64Elf,
  verifyPartitionTable,
  verifySignatureStatus,
  verifyRuntimeApprovalGateAbsent,
  writeManifest,
} from './inputs.mjs'

const hash = value => createHash('sha256').update(value).digest('hex')

async function fixture(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'aiplatform-build-inputs-'))
  t.after(() => rm(directory, { recursive: true, force: true }))
  const bundle = path.join(directory, 'bundle')
  await mkdir(bundle)
  const packageNames = ['base-files', 'dpkg', 'apt', 'systemd-sysv', 'shim-signed',
    'grub-efi-amd64-signed', 'chromium', 'chromium-common', 'chromium-sandbox',
    'weston', 'libweston-14-0', 'fonts-dejavu-core', 'linux-image-6.12.117+deb13-amd64']
  const files = ['trust/archive.gpg', 'trust/debian.pem', 'trust/microsoft.pem',
    'debian/dists/trixie/InRelease', 'security/dists/trixie-security/InRelease',
    'payload/agent', 'payload/weston.so', 'payload/broker.exe', 'payload/viewer.exe',
    'payload/supervisor.exe', 'overlay/session.target', 'overlay/agent.path', 'overlay/agent.service',
    'overlay/browser.service', 'overlay/agent.sysusers',
    'debian/dists/trixie/main/binary-amd64/Packages.gz',
    'security/dists/trixie-security/main/binary-amd64/Packages.gz',
    ...packageNames.map(name => `debian/pool/${name}_1_amd64.deb`)]
  const sampleBytes = Buffer.from('test-only bytes\n')
  const agentBytes = Buffer.alloc(64)
  Buffer.from([0x7f, 0x45, 0x4c, 0x46]).copy(agentBytes)
  agentBytes[4] = 2
  agentBytes[5] = 1
  agentBytes[6] = 1
  agentBytes.writeUInt16LE(2, 16)
  agentBytes.writeUInt16LE(62, 18)
  agentBytes.writeUInt32LE(1, 20)
  agentBytes.writeUInt16LE(64, 52)
  const archiveDigest = hash(sampleBytes)
  const packagesIndex = gzipSync(packageNames.map(name => [
    `Package: ${name}`,
    'Version: 1',
    'Architecture: amd64',
    `Filename: pool/${name}_1_amd64.deb`,
    `SHA256: ${archiveDigest}`,
  ].join('\n')).join('\n\n') + '\n\n')
  const contents = new Map(files.map(file => [file, sampleBytes]))
  contents.set('payload/agent', agentBytes)
  contents.set('overlay/session.target', await readFile(new URL('./preview-session.target', import.meta.url)))
  contents.set('overlay/agent.path', await readFile(new URL('./preview-agent.path', import.meta.url)))
  contents.set('overlay/agent.service', await readFile(new URL('./preview-agent.service', import.meta.url)))
  contents.set('overlay/agent.sysusers', await readFile(new URL('./preview-agent.sysusers', import.meta.url)))
  contents.set(files[files.length - packageNames.length - 2], packagesIndex)
  contents.set(files[files.length - packageNames.length - 1], gzipSync(''))
  for (const file of files) {
    await mkdir(path.dirname(path.join(bundle, file)), { recursive: true })
    await writeFile(path.join(bundle, file), contents.get(file))
  }
  const lock = {
    schemaVersion: 1, distribution: 'trixie', architecture: 'amd64',
    sourceDateEpoch: 1794614400, kernelRelease: '6.12.117+deb13-amd64',
    keyring: files[0], grubSecureBootCertificate: files[1], kernelSecureBootCertificate: files[1],
    microsoftUefiCertificate: files[2],
    files: files.map(file => ({
      path: file, sha256: hash(contents.get(file)), origin: 'Unit fixture, not signed Debian', version: '1',
    })),
    repositories: [
      {
        root: 'debian', suite: 'trixie', inRelease: files[3], signerFingerprints: ['A'.repeat(40)],
        indexes: [{ path: files[files.length - packageNames.length - 2], sha256: hash(packagesIndex) }],
      },
      {
        root: 'security', suite: 'trixie-security', inRelease: files[4], signerFingerprints: ['B'.repeat(40)],
        indexes: [{ path: files[files.length - packageNames.length - 1], sha256: hash(gzipSync('')) }],
      },
    ],
    packages: packageNames.map(name => ({
      name, version: '1', architecture: 'amd64', repository: 'debian',
      archive: `debian/pool/${name}_1_amd64.deb`, sha256: archiveDigest,
    })),
    builderTools: ['node', 'bash', 'uname', 'realpath', 'env', 'mkdir', 'cat', 'unshare', 'mount',
      'gpgv', 'mmdebstrap', 'chroot', 'dpkg-query', 'sfdisk', 'truncate', 'mkfs.ext4',
      'mkfs.vfat', 'mmd', 'mcopy', 'dd', 'qemu-img', 'sbverify', 'date']
      .map(name => ({ name, path: `/usr/bin/${name}`, version: '1', sha256: hash(name) })),
    prototypeComponents: [
      { role: 'guest-agent', path: 'payload/agent', version: '1' },
      { role: 'weston-module', path: 'payload/weston.so', version: '1' },
      { role: 'host-broker', path: 'payload/broker.exe', version: '1' },
      { role: 'host-viewer', path: 'payload/viewer.exe', version: '1' },
      { role: 'host-supervisor', path: 'payload/supervisor.exe', version: '1' },
    ],
    overlay: [
      { source: 'payload/agent', target: 'usr/local/lib/preview/agent', mode: 493 },
      { source: 'payload/weston.so', target: 'usr/local/lib/preview/weston.so', mode: 420 },
      { source: 'overlay/session.target', target: 'etc/systemd/system/preview-session.target', mode: 420 },
      { source: 'overlay/agent.path', target: 'etc/systemd/system/preview-agent.path', mode: 420 },
      { source: 'overlay/agent.service', target: 'etc/systemd/system/preview-agent.service', mode: 420 },
      { source: 'overlay/browser.service', target: 'etc/systemd/system/preview-browser.service', mode: 420 },
      { source: 'overlay/agent.sysusers', target: 'etc/sysusers.d/preview-agent.conf', mode: 420 },
    ],
  }
  const lockFile = path.join(directory, 'lock.json')
  async function save() {
    const bytes = JSON.stringify(lock)
    await writeFile(lockFile, bytes)
    return hash(bytes)
  }
  return { directory, bundle, lock, lockFile, save, contents }
}

test('verifies pinned bytes without running tools, and rejects changed or unlisted bundle contents', async t => {
  const f = await fixture(t)
  const digest = await f.save()
  await verifyInputs(f.bundle, f.lockFile, digest)
  await writeFile(path.join(f.bundle, 'payload', 'agent'), 'modified')
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, digest), /Input digest mismatch/)
  await writeFile(path.join(f.bundle, 'payload', 'agent'), f.contents.get('payload/agent'))
  await writeFile(path.join(f.bundle, 'unexpected'), 'unlisted')
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, digest), /Unlisted or nonregular/)
})

test('rejects a lock not bound to the separately supplied digest and traversal paths', async t => {
  const f = await fixture(t)
  await f.save()
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, '0'.repeat(64)), /Lock digest mismatch/)
  f.lock.files[0].path = '../outside'
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /path traversal/)
})

test('keeps the example lock invalid until real versions and input pins are supplied', async t => {
  const f = await fixture(t)
  const example = await readFile(new URL('./inputs.example.json', import.meta.url), 'utf8')
  await writeFile(f.lockFile, example)
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, hash(example)), /Invalid source epoch/)

  f.lock.files[0].version = 'unknown version'
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /Unpinned input/)
})

test('rejects unpinned trust inputs, absent components and mismatched Chromium packages', async t => {
  const f = await fixture(t)
  f.lock.files[0].sha256 = null
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /Unpinned input/)
  f.lock.files[0].sha256 = hash('test-only bytes\n')
  f.lock.prototypeComponents[0].path = null
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /prototype component/)
  f.lock.prototypeComponents[0].path = 'payload/agent'
  f.lock.packages.find(pkg => pkg.name === 'chromium-common').version = '2'
  f.lock.files.find(file => file.path === f.lock.packages.find(pkg => pkg.name === 'chromium-common').archive).version = '2'
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /Chromium versions differ/)
})

test('requires a complete closed schema with signed repository indexes and pinned package archives', async t => {
  const f = await fixture(t)
  let digest = await f.save()
  await verifyInputs(f.bundle, f.lockFile, digest)

  delete f.lock.repositories[1].signerFingerprints
  digest = await f.save()
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, digest), /missing or unknown properties/)
  f.lock.repositories[1].signerFingerprints = ['B'.repeat(40)]

  f.lock.repositories[0].indexes[0].sha256 = '0'.repeat(64)
  digest = await f.save()
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, digest), /not pinned by the bundle inventory/)
  f.lock.repositories[0].indexes[0].sha256 = hash(f.contents.get(f.lock.repositories[0].indexes[0].path))

  f.lock.packages[0].sha256 = '0'.repeat(64)
  digest = await f.save()
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, digest), /Package archive is not pinned/)
})

test('rejects duplicate JSON keys, extra lock fields and malformed signature status', async t => {
  const f = await fixture(t)
  const duplicate = '{"schemaVersion":1,"schemaVersion":1}'
  await writeFile(f.lockFile, duplicate)
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, hash(duplicate)), /Duplicate JSON property/)

  f.lock.unreviewedField = true
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /missing or unknown properties/)

  const repository = { suite: 'trixie', signerFingerprints: ['A'.repeat(40)] }
  verifySignatureStatus(repository, `[GNUPG:] VALIDSIG ${'A'.repeat(40)} 20261114 1 0 4 0 1 8 00\n`)
  verifySignatureStatus(repository,
    `[GNUPG:] VALIDSIG ${'C'.repeat(40)} 20261114 1 0 4 0 1 8 00 ${'A'.repeat(40)}\n`)
  assert.throws(() => verifySignatureStatus(repository, '[GNUPG:] GOODSIG key\n'), /No valid repository signature/)
  assert.throws(() => verifySignatureStatus(repository,
    `[GNUPG:] VALIDSIG ${'C'.repeat(40)} 20261114 1 0 4 0 1 8 00\n`), /not from an approved key/)
  assert.throws(() => verifySignatureStatus(repository,
    `[GNUPG:] BADSIG key\n[GNUPG:] VALIDSIG ${'A'.repeat(40)} 20261114 1 0 4 0 1 8 00\n`),
  /Invalid signature status/)
})

test('rejects duplicate inputs and guest overlay traversal', async t => {
  const f = await fixture(t)
  f.lock.files.push(f.lock.files[0])
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /Duplicate bundle path/)
  f.lock.files.pop()
  f.lock.overlay[0].target = 'etc/../../outside'
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /path traversal/)
})

test('matches locked Debian package identity and hashes to the compressed signed-index contents', async t => {
  const f = await fixture(t)
  let digest = await f.save()
  await verifyInputs(f.bundle, f.lockFile, digest)
  const indexPath = f.lock.repositories[0].indexes[0].path
  const index = await readFile(path.join(f.bundle, indexPath))
  const corrupted = Buffer.from(index)
  corrupted[corrupted.length - 1] ^= 0xff
  await writeFile(path.join(f.bundle, indexPath), corrupted)
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, digest))

  await writeFile(path.join(f.bundle, indexPath), index)
  f.lock.packages[0].version = '2'
  f.lock.files.find(file => file.path === f.lock.packages[0].archive).version = '2'
  digest = await f.save()
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, digest), /does not match exactly one/)
})

test('rejects a missing locked dependency and an agent installed at an unauthorized target', async t => {
  const f = await fixture(t)
  const indexPath = f.lock.repositories[0].indexes[0].path
  const indexText = gzipSync(f.lock.packages.filter(pkg => pkg.name !== 'apt').map(pkg => [
    `Package: ${pkg.name}`,
    `Version: ${pkg.version}`,
    `Architecture: ${pkg.architecture}`,
    `Filename: ${pkg.archive.slice(`${pkg.repository}/`.length)}`,
    `SHA256: ${pkg.sha256}`,
  ].join('\n')).join('\n\n') + '\n\n')
  await writeFile(path.join(f.bundle, indexPath), indexText)
  const indexEntry = f.lock.files.find(file => file.path === indexPath)
  indexEntry.sha256 = hash(indexText)
  f.lock.repositories[0].indexes[0].sha256 = hash(indexText)
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /does not match exactly one/)

  await writeFile(path.join(f.bundle, indexPath), f.contents.get(indexPath))
  indexEntry.sha256 = hash(f.contents.get(indexPath))
  f.lock.repositories[0].indexes[0].sha256 = hash(f.contents.get(indexPath))
  f.lock.overlay.find(entry => entry.source === 'payload/agent').target = 'etc/preview/agent'
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /fixed executable/)
})

test('rejects an agent service template altered together with its updated digest', async t => {
  const f = await fixture(t)
  const servicePath = 'overlay/agent.service'
  const altered = Buffer.concat([f.contents.get(servicePath), Buffer.from('\n# unreviewed\n')])
  await writeFile(path.join(f.bundle, servicePath), altered)
  const serviceFile = f.lock.files.find(file => file.path === servicePath)
  serviceFile.sha256 = hash(altered)
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /differs from reviewed template/)
})

test('parses package index stanzas strictly and rejects duplicate or incomplete fields', () => {
  const entry = [
    'Package: example',
    'Version: 1.2-3',
    'Architecture: amd64',
    'Filename: pool/main/e/example.deb',
    `SHA256: ${'a'.repeat(64)}`,
    '',
    '',
  ].join('\n')
  assert.equal(parsePackageIndex(entry).length, 1)
  assert.throws(() => parsePackageIndex(entry.replace('Version: 1.2-3\n', '')), /Incomplete/)
  assert.throws(() => parsePackageIndex(entry.replace('Version: 1.2-3\n',
    'Version: 1.2-3\nVersion: 1.2-3\n')), /Duplicate/)
  assert.throws(() => parsePackageIndex(entry.replace('Filename: pool/main/e/example.deb',
    'Filename: ../example.deb')), /path traversal/)
})

test('validates agent architecture, reviewed systemd packaging, boot configuration and GPT geometry', async t => {
  const f = await fixture(t)
  await verifyInputs(f.bundle, f.lockFile, await f.save())
  const expectedFstab = [
    'UUID=42506567-6510-4000-8000-000000000001 / ext4 ro,nodev 0 1',
    'tmpfs /tmp tmpfs rw,nosuid,nodev,size=128M,mode=1777 0 0',
    'tmpfs /var/log tmpfs rw,nosuid,nodev,noexec,size=16M 0 0',
    '',
  ].join('\n')
  const expectedGrub = [
    'set timeout=0',
    'set default=0',
    "menuentry 'AIPlatform static appliance' {",
    '    search --no-floppy --fs-uuid --set=root 42506567-6510-4000-8000-000000000001',
    '    linux /boot/vmlinuz-6.12.117+deb13-amd64 root=UUID=42506567-6510-4000-8000-000000000001 ro quiet',
    '    initrd /boot/initrd.img-6.12.117+deb13-amd64',
    '}',
    '',
  ].join('\n')
  verifyGuestBootConfiguration(expectedFstab, expectedGrub, '6.12.117+deb13-amd64')
  assert.throws(() => verifyGuestBootConfiguration(expectedFstab, expectedGrub.replace('quiet', 'init=/bin/sh'),
    '6.12.117+deb13-amd64'), /GRUB/)
  const expectedEspGrub = [
    'search --no-floppy --fs-uuid --set=root 42506567-6510-4000-8000-000000000001',
    'configfile /boot/grub/grub.cfg',
    '',
  ].join('\n')
  verifyEspBootConfiguration(expectedEspGrub)
  assert.throws(() => verifyEspBootConfiguration(expectedEspGrub.replace('configfile ', 'chainloader ')),
    /EFI GRUB/)
  const partitionTable = {
    partitiontable: {
      label: 'gpt', id: '42506567-6510-4000-8000-000000000002', unit: 'sectors', sectorsize: 512,
      partitions: [
        { start: 2048, size: 524288, type: 'C12A7328-F81F-11D2-BA4B-00A0C93EC93B',
          uuid: '42506567-6510-4000-8000-000000000003' },
        { start: 526336, size: 12054528, type: '0FC63DAF-8483-4772-8E79-3D69D8477DE4',
          uuid: '42506567-6510-4000-8000-000000000004' },
      ],
    },
  }
  verifyPartitionTable(partitionTable)
  assert.throws(() => verifyPartitionTable({
    ...partitionTable,
    partitiontable: { ...partitionTable.partitiontable, partitions: partitionTable.partitiontable.partitions.slice(0, 1) },
  }), /partition count/)
  const elf = f.contents.get('payload/agent')
  verifyLinuxX64Elf(elf)
  assert.throws(() => verifyLinuxX64Elf(Buffer.from('not an executable')), /ELF64/)
})

test('applies overlay files without overwrites and rolls back only files created by the failed operation', async t => {
  const f = await fixture(t)
  const root = path.join(f.directory, 'rootfs')
  await mkdir(root)
  const entry = { source: 'overlay/session.target', target: 'etc/systemd/system/preview-session.target', mode: 420 }
  const lock = { files: f.lock.files, overlay: [entry] }
  await applyOverlay(f.bundle, lock, root)
  const installed = path.join(root, 'etc', 'systemd', 'system', 'preview-session.target')
  assert.deepEqual(await readFile(installed), f.contents.get(entry.source))
  await assert.rejects(applyOverlay(f.bundle, lock, root), /EEXIST/)
  assert.deepEqual(await readFile(installed), f.contents.get(entry.source))

  const rollbackRoot = path.join(f.directory, 'rollback-root')
  await mkdir(rollbackRoot)
  await assert.rejects(applyOverlay(f.bundle, {
    files: f.lock.files,
    overlay: [
      entry,
      { source: 'missing/input', target: 'etc/preview/after-failure', mode: 420 },
    ],
  }, rollbackRoot), /ENOENT|Invalid file/)
  await assert.rejects(readFile(path.join(rollbackRoot, 'etc', 'systemd', 'system', 'preview-session.target')),
    { code: 'ENOENT' })
  await assert.rejects(readFile(path.join(rollbackRoot, 'etc')), { code: 'ENOENT' })
})

test('requires the agent approval gate to remain absent from the packaged image', async t => {
  const f = await fixture(t)
  const root = path.join(f.directory, 'rootfs')
  await mkdir(root)
  await verifyRuntimeApprovalGateAbsent(root)
  const gate = path.join(root, 'etc', 'preview', 'agent-runtime-approved')
  await mkdir(path.dirname(gate), { recursive: true })
  await writeFile(gate, '')
  await assert.rejects(verifyRuntimeApprovalGateAbsent(root), /must not be present/)
})

test('enables only the reviewed session target in the offline guest root', async t => {
  const f = await fixture(t)
  const root = path.join(f.directory, 'rootfs')
  const systemd = path.join(root, 'etc', 'systemd', 'system')
  await mkdir(systemd, { recursive: true })
  await writeFile(path.join(systemd, 'preview-session.target'), f.contents.get('overlay/session.target'))
  await writeFile(path.join(systemd, 'preview-agent.path'), f.contents.get('overlay/agent.path'))
  const linkRequests = []
  await enablePreviewSessionTarget(root, async (target, linkPath, type) => {
    linkRequests.push({ target, linkPath, type })
    await mkdir(path.dirname(linkPath), { recursive: true })
    await writeFile(linkPath, target)
  })
  assert.deepEqual(linkRequests.map(request => request.target),
    ['../preview-session.target', '../preview-agent.path'])
  assert.ok(linkRequests.every(request => request.type === 'file'))
  assert.equal(await readFile(linkRequests[0].linkPath, 'utf8'), '../preview-session.target')
  assert.equal(await readFile(linkRequests[1].linkPath, 'utf8'), '../preview-agent.path')

  const invalidRoot = path.join(f.directory, 'invalid-root')
  const invalidSystemd = path.join(invalidRoot, 'etc', 'systemd', 'system')
  const invalidWants = path.join(invalidSystemd, 'preview-session.target.wants')
  await mkdir(invalidWants, { recursive: true })
  await writeFile(path.join(invalidSystemd, 'preview-session.target'), 'unit')
  await writeFile(path.join(invalidSystemd, 'preview-agent.path'), 'path')
  const existingLinkPath = path.join(invalidWants, 'preview-agent.path')
  await writeFile(existingLinkPath, 'pre-existing')
  await assert.rejects(enablePreviewSessionTarget(invalidRoot, async (_, linkPath) => {
    if (linkPath !== existingLinkPath) {
      await mkdir(path.dirname(linkPath), { recursive: true })
      await writeFile(linkPath, 'created')
      return
    }
    const error = new Error('Already exists.')
    error.code = 'EEXIST'
    throw error
  }), { code: 'EEXIST' })
  await assert.rejects(readFile(path.join(invalidSystemd, 'multi-user.target.wants', 'preview-session.target')),
    { code: 'ENOENT' })
})

test('compares independent build manifests without claiming boot or execution validation', async t => {
  const f = await fixture(t)
  const artifact = path.join(f.bundle, 'payload', 'agent')
  const inputLock = { distribution: 'trixie', architecture: 'amd64', kernelRelease: '6.12.117+deb13-amd64',
    sourceDateEpoch: 1794614400 }
  const first = path.join(f.directory, 'build-one.json')
  const second = path.join(f.directory, 'build-two.json')
  const reportPath = path.join(f.directory, 'comparison.json')
  await writeManifest(first, '1'.repeat(64), [artifact], inputLock)
  await writeManifest(second, '1'.repeat(64), [artifact], inputLock)
  const report = await compareBuildManifests(first, second, reportPath)
  assert.equal(report.bitwiseReproducible, true)
  assert.deepEqual(report.differences, [])
  assert.equal(JSON.parse(await readFile(first, 'utf8')).executionEligible, false)

  const differentInput = path.join(f.directory, 'different-input.json')
  await writeManifest(differentInput, '1'.repeat(64), [path.join(f.bundle, 'payload', 'weston.so')], inputLock)
  const mismatch = await compareBuildManifests(first, differentInput, path.join(f.directory, 'mismatch.json'))
  assert.equal(mismatch.bitwiseReproducible, false)
  assert.deepEqual(mismatch.differences.sort(), ['agent', 'weston.so'])
  const wrongLock = path.join(f.directory, 'wrong-lock.json')
  await writeManifest(wrongLock, '2'.repeat(64), [artifact], inputLock)
  await assert.rejects(compareBuildManifests(first, wrongLock, path.join(f.directory, 'wrong-lock-report.json')),
    /identical locked inputs/)
})

test('requires a fully present VHDX allocation map with exact contiguous coverage', () => {
  verifyImageMap([
    { start: 0, length: 1024, data: true, present: true },
    { start: 1024, length: 1024, data: true, present: true },
  ], 2048)
  assert.throws(() => verifyImageMap([
    { start: 0, length: 1024, data: true, present: true },
    { start: 2048, length: 1024, data: true, present: true },
  ], 3072), /gap or unallocated/)
  assert.throws(() => verifyImageMap([{ start: 0, length: 1024, data: false, present: false }], 1024),
    /gap or unallocated/)
})

test('requires the exact installed dependency closure including installed status', () => {
  const lock = { packages: [{ name: 'apt', version: '1', architecture: 'amd64' }] }
  verifyInstalled(lock, 'apt\t1\tamd64\tinstalled\n')
  assert.throws(() => verifyInstalled(lock, 'apt\t2\tamd64\tinstalled\n'), /closure differs/)
  assert.throws(() => verifyInstalled(lock, 'apt\t1\tamd64\tinstalled\nextra\t1\tall\tinstalled\n'), /closure differs/)
  assert.throws(() => verifyInstalled(lock, 'apt\t1\tamd64\tunpacked\n'), /not installed/)
  assert.throws(() => verifyInstalled(lock, 'apt\t1\tamd64\tinstalled\textra\n'), /Malformed/)
  assert.throws(() => verifyInstalled(lock, 'apt\t1\tamd64\tinstalled\napt\t1\tamd64\tinstalled\n'), /Duplicate/)
  assert.throws(() => verifyInstalled(lock, ' apt\t1\tamd64\tinstalled\n'), /Malformed/)
})

test('writes deterministic artifact hashes but never overwrites a manifest or marks it authorized', async t => {
  const f = await fixture(t)
  const file = path.join(f.bundle, 'payload', 'agent')
  const first = path.join(f.directory, 'first.json')
  const second = path.join(f.directory, 'second.json')
  await writeManifest(first, '1'.repeat(64), [file])
  await writeManifest(second, '1'.repeat(64), [file])
  assert.equal(await readFile(first, 'utf8'), await readFile(second, 'utf8'))
  assert.equal(JSON.parse(await readFile(first, 'utf8')).artifacts[0].sha256, hash(f.contents.get('payload/agent')))
  assert.equal(JSON.parse(await readFile(first, 'utf8')).executionEligible, false)
  await assert.rejects(writeManifest(first, '1'.repeat(64), [file]), /EEXIST/)
})

test('rejects wrong VHDX geometry, backing disks and files outside the fixed-size budget', () => {
  const info = {
    format: 'vhdx', 'virtual-size': 6442450944, 'actual-size': 6442450944,
    'format-specific': { type: 'vhdx', data: { has_parent: false } },
  }
  verifyImageInfo(info, 6442450944)
  verifyImageInfo(info, 6509559808)
  assert.throws(() => verifyImageInfo({ ...info, 'virtual-size': 6442450945 }, 6442450944), /geometry/)
  assert.throws(() => verifyImageInfo({ ...info, 'backing-filename': 'host.vhdx' }, 6442450944), /backing/)
  assert.throws(() => verifyImageInfo(info, 6509559809), /outside the fixed-allocation budget/)
  assert.throws(() => verifyImageInfo(info, 6442450943), /outside the fixed-allocation budget/)
  assert.throws(() => verifyImageInfo({
    ...info, 'format-specific': { type: 'vhdx', data: { has_parent: true } },
  }, 6442450944), /backing/)
  assert.throws(() => verifyImageInfo({ ...info, 'actual-size': 6509559809 }, 6442450944), /actual allocated/)
})
