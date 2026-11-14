import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { verifyImageInfo, verifyInputs, verifyInstalled, writeManifest } from './inputs.mjs'

const hash = value => createHash('sha256').update(value).digest('hex')

async function fixture(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'aiplatform-build-inputs-'))
  t.after(() => rm(directory, { recursive: true, force: true }))
  const bundle = path.join(directory, 'bundle')
  await mkdir(bundle)
  const files = ['trust/archive.gpg', 'trust/debian.pem', 'trust/microsoft.pem',
    'debian/dists/trixie/InRelease', 'security/dists/trixie-security/InRelease',
    'payload/agent', 'payload/weston.so', 'payload/broker.exe', 'payload/viewer.exe',
    'payload/supervisor.exe', 'overlay/default.target', 'overlay/session.target',
    'overlay/agent.service', 'overlay/browser.service', 'debian/pool/example_1~deb13u1.deb']
  for (const file of files) {
    await mkdir(path.dirname(path.join(bundle, file)), { recursive: true })
    await writeFile(path.join(bundle, file), 'test-only bytes\n')
  }
  const lock = {
    schemaVersion: 1, distribution: 'trixie', architecture: 'amd64',
    sourceDateEpoch: 1794614400, kernelRelease: '6.12.117+deb13-amd64',
    keyring: files[0], grubSecureBootCertificate: files[1], kernelSecureBootCertificate: files[1],
    microsoftUefiCertificate: files[2],
    files: files.map(file => ({ path: file, sha256: hash('test-only bytes\n'), origin: 'Unit fixture, not signed Debian', version: '1' })),
    packages: ['base-files', 'dpkg', 'apt', 'systemd-sysv', 'shim-signed',
      'grub-efi-amd64-signed', 'chromium', 'chromium-common', 'chromium-sandbox',
      'weston', 'libweston-14-0', 'fonts-dejavu-core', 'linux-image-6.12.117+deb13-amd64']
      .map(name => ({ name, version: '1', architecture: 'amd64' })),
    builderTools: ['node', 'bash', 'uname', 'realpath', 'env', 'mkdir', 'cat', 'unshare', 'mount',
      'gpgv', 'mmdebstrap', 'chroot', 'dpkg-query', 'sfdisk', 'truncate', 'mkfs.ext4',
      'mkfs.vfat', 'mmd', 'mcopy', 'dd', 'qemu-img', 'sbverify', 'date']
      .map(name => ({ name, path: `/usr/bin/${name}`, version: '1', sha256: hash(name) })),
    prototypeComponents: ['guest-agent', 'weston-module', 'host-broker', 'host-viewer', 'host-supervisor']
      .map((role, i) => ({ role, path: files[5 + i], version: '1' })),
    overlay: [
      { source: files[5], target: 'usr/local/lib/preview/agent', mode: 493 },
      { source: files[6], target: 'usr/local/lib/preview/weston.so', mode: 420 },
      ...['default.target', 'preview-session.target', 'preview-agent.service', 'preview-browser.service']
        .map((name, i) => ({ source: files[10 + i], target: `etc/systemd/system/${name}`, mode: 420 })),
    ],
  }
  const lockFile = path.join(directory, 'lock.json')
  async function save() {
    const bytes = JSON.stringify(lock)
    await writeFile(lockFile, bytes)
    return hash(bytes)
  }
  return { directory, bundle, lock, lockFile, save }
}

test('verifies pinned bytes without running tools, and rejects changed or unlisted bundle contents', async t => {
  const f = await fixture(t)
  const digest = await f.save()
  await verifyInputs(f.bundle, f.lockFile, digest)
  await writeFile(path.join(f.bundle, 'payload', 'agent'), 'modified')
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, digest), /Input digest mismatch/)
  await writeFile(path.join(f.bundle, 'payload', 'agent'), 'test-only bytes\n')
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

test('rejects unpinned trust inputs, absent components and mismatched Chromium packages', async t => {
  const f = await fixture(t)
  f.lock.files[0].sha256 = null
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /Unpinned input/)
  f.lock.files[0].sha256 = hash('test-only bytes\n')
  f.lock.prototypeComponents[0].path = null
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /Missing prototype component/)
  f.lock.prototypeComponents[0].path = 'payload/agent'
  f.lock.packages.find(pkg => pkg.name === 'chromium-common').version = '2'
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /Chromium versions differ/)
})

test('rejects duplicate inputs and guest overlay traversal', async t => {
  const f = await fixture(t)
  f.lock.files.push(f.lock.files[0])
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /Duplicate bundle path/)
  f.lock.files.pop()
  f.lock.overlay[0].target = 'etc/../../outside'
  await assert.rejects(verifyInputs(f.bundle, f.lockFile, await f.save()), /path traversal/)
})

test('requires the exact installed dependency closure including installed status', () => {
  const lock = { packages: [{ name: 'apt', version: '1', architecture: 'amd64' }] }
  verifyInstalled(lock, 'apt\t1\tamd64\tinstalled\n')
  assert.throws(() => verifyInstalled(lock, 'apt\t2\tamd64\tinstalled\n'), /closure differs/)
  assert.throws(() => verifyInstalled(lock, 'apt\t1\tamd64\tinstalled\nextra\t1\tall\tinstalled\n'), /closure differs/)
  assert.throws(() => verifyInstalled(lock, 'apt\t1\tamd64\tunpacked\n'), /not installed/)
})

test('writes deterministic artifact hashes but never overwrites a manifest or marks it authorized', async t => {
  const f = await fixture(t)
  const file = path.join(f.bundle, 'payload', 'agent')
  const first = path.join(f.directory, 'first.json')
  const second = path.join(f.directory, 'second.json')
  await writeManifest(first, '1'.repeat(64), [file])
  await writeManifest(second, '1'.repeat(64), [file])
  assert.equal(await readFile(first, 'utf8'), await readFile(second, 'utf8'))
  assert.equal(JSON.parse(await readFile(first, 'utf8')).artifacts[0].sha256, hash('test-only bytes\n'))
  assert.equal(JSON.parse(await readFile(first, 'utf8')).executionEligible, false)
  await assert.rejects(writeManifest(first, '1'.repeat(64), [file]), /EEXIST/)
})

test('rejects wrong VHDX geometry, backing disks and files outside the fixed-size budget', () => {
  const info = { format: 'vhdx', 'virtual-size': 6442450944 }
  verifyImageInfo(info, 6442450944)
  verifyImageInfo(info, 6509559808)
  assert.throws(() => verifyImageInfo({ ...info, 'virtual-size': 6442450945 }, 6442450944), /geometry/)
  assert.throws(() => verifyImageInfo({ ...info, 'backing-filename': 'host.vhdx' }, 6442450944), /backing/)
  assert.throws(() => verifyImageInfo(info, 6509559809), /outside budget/)
  assert.throws(() => verifyImageInfo(info, 6442450943), /outside budget/)
})
