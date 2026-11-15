import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { createReadStream } from 'node:fs'
import {
  lstat, mkdir, open, readFile, readdir, realpath, rmdir, symlink, unlink, writeFile,
} from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { gunzipSync } from 'node:zlib'

const digestPattern = /^[a-f0-9]{64}$/
const fingerprintPattern = /^(?:[A-F0-9]{40}|[A-F0-9]{64})$/
const packagePattern = /^[a-z0-9][a-z0-9+.-]*$/
const versionPattern = /^[0-9][a-zA-Z0-9.+:~_-]{0,127}$/
const requiredTools = ['node', 'bash', 'uname', 'realpath', 'env', 'mkdir', 'cat', 'unshare', 'mount', 'gpgv', 'mmdebstrap', 'chroot',
  'dpkg-query', 'sfdisk', 'truncate', 'mkfs.ext4', 'mkfs.vfat', 'mmd', 'mcopy',
  'dd', 'qemu-img', 'sbverify', 'date']
const requiredPackages = ['base-files', 'dpkg', 'apt', 'systemd-sysv', 'shim-signed',
  'grub-efi-amd64-signed', 'chromium', 'chromium-common', 'chromium-sandbox',
  'weston', 'libweston-14-0', 'fonts-dejavu-core']
const requiredRoles = ['guest-agent', 'weston-module', 'host-broker', 'host-viewer', 'host-supervisor']
const repositoryDefinitions = [
  { root: 'debian', suite: 'trixie', inRelease: 'debian/dists/trixie/InRelease' },
  { root: 'security', suite: 'trixie-security', inRelease: 'security/dists/trixie-security/InRelease' },
]
const requiredIndexPattern = /^.+\/dists\/(trixie|trixie-security)\/main\/binary-amd64\/Packages\.gz$/
const requiredOverlayTargets = [
  'etc/systemd/system/preview-session.target',
  'etc/systemd/system/preview-agent.path',
  'etc/systemd/system/preview-agent.service',
  'etc/systemd/system/preview-browser.service',
  'etc/sysusers.d/preview-agent.conf',
]
const maximumCompressedIndexBytes = 64 * 1024 * 1024
const maximumExpandedIndexBytes = 256 * 1024 * 1024
const topLevelKeys = [
  'schemaVersion', 'distribution', 'architecture', 'sourceDateEpoch', 'kernelRelease',
  'keyring', 'grubSecureBootCertificate', 'kernelSecureBootCertificate', 'microsoftUefiCertificate',
  'repositories', 'files', 'packages', 'builderTools', 'overlay', 'prototypeComponents',
]

function requireValue(condition, message) {
  if (!condition) throw new Error(message)
}

function exactKeys(value, expected, label) {
  requireValue(value !== null && typeof value === 'object' && !Array.isArray(value), `${label} must be an object.`)
  const keys = Object.keys(value)
  requireValue(expected.every(key => Object.hasOwn(value, key)) &&
    keys.every(key => expected.includes(key)), `${label} has missing or unknown properties.`)
}

function validText(value, maximumLength = 512) {
  return typeof value === 'string' && value.length > 0 && value.length <= maximumLength &&
    !/[\u0000-\u001f\u007f]/.test(value)
}

function rejectDuplicateJsonProperties(source) {
  let position = 0
  const skipWhitespace = () => {
    while (/\s/.test(source[position] ?? '')) position++
  }
  const readString = () => {
    const start = position++
    let escaped = false
    while (position < source.length) {
      const character = source[position++]
      if (escaped) escaped = false
      else if (character === '\\') escaped = true
      else if (character === '"') return JSON.parse(source.slice(start, position))
    }
    throw new Error('Unterminated JSON string.')
  }
  const readValue = depth => {
    requireValue(depth <= 64, 'Lock JSON nesting limit exceeded.')
    skipWhitespace()
    const marker = source[position]
    if (marker === '{') {
      position++
      skipWhitespace()
      const keys = new Set()
      if (source[position] === '}') {
        position++
        return
      }
      while (position < source.length) {
        skipWhitespace()
        requireValue(source[position] === '"', 'Invalid JSON object key.')
        const key = readString()
        requireValue(!keys.has(key), `Duplicate JSON property: ${key}`)
        keys.add(key)
        skipWhitespace()
        requireValue(source[position++] === ':', 'Invalid JSON object separator.')
        readValue(depth + 1)
        skipWhitespace()
        const separator = source[position++]
        if (separator === '}') return
        requireValue(separator === ',', 'Invalid JSON object terminator.')
      }
      throw new Error('Unterminated JSON object.')
    }
    if (marker === '[') {
      position++
      skipWhitespace()
      if (source[position] === ']') {
        position++
        return
      }
      while (position < source.length) {
        readValue(depth + 1)
        skipWhitespace()
        const separator = source[position++]
        if (separator === ']') return
        requireValue(separator === ',', 'Invalid JSON array terminator.')
      }
      throw new Error('Unterminated JSON array.')
    }
    if (marker === '"') {
      readString()
      return
    }
    const start = position
    while (position < source.length && !/[\s,[\]{}:]/.test(source[position])) position++
    requireValue(position > start, 'Invalid JSON value.')
  }

  readValue(0)
  skipWhitespace()
  requireValue(position === source.length, 'Trailing content after lock JSON.')
}

export async function sha256(file) {
  const hash = createHash('sha256')
  for await (const chunk of createReadStream(file)) hash.update(chunk)
  return hash.digest('hex')
}

export function verifySignatureStatus(repository, statusOutput) {
  requireValue(Array.isArray(repository.signerFingerprints) && typeof statusOutput === 'string',
    'Invalid signature verification input.')
  const lines = statusOutput.split(/\r?\n/).filter(line => line.startsWith('[GNUPG:] '))
  requireValue(!lines.some(line => /\[GNUPG:\] (?:BADSIG|ERRSIG|REVKEYSIG|EXPKEYSIG|EXPSIG|NO_PUBKEY)\b/.test(line)),
    `Invalid signature status: ${repository.suite}`)
  const validSignatures = lines.filter(line => line.startsWith('[GNUPG:] VALIDSIG '))
  requireValue(validSignatures.length > 0, `No valid repository signature: ${repository.suite}`)
  const allowed = new Set(repository.signerFingerprints.map(fingerprint => fingerprint.toUpperCase()))
  for (const line of validSignatures) {
    const fields = line.split(/\s+/)
    const signingKey = fields[2]
    const primaryKey = fields.length === 12 ? fields[11] : undefined
    requireValue((fields.length === 11 || fields.length === 12) &&
      fingerprintPattern.test(signingKey ?? '') &&
      (!primaryKey || fingerprintPattern.test(primaryKey)) &&
      (allowed.has(signingKey.toUpperCase()) ||
       (primaryKey && allowed.has(primaryKey.toUpperCase()))),
    `Repository signature is not from an approved key: ${repository.suite}`)
  }
}

export function verifyLinuxX64Elf(bytes) {
  requireValue(Buffer.isBuffer(bytes) && bytes.length >= 64, 'Guest agent is not an ELF64 executable.')
  requireValue(bytes.subarray(0, 4).equals(Buffer.from([0x7f, 0x45, 0x4c, 0x46])) &&
    bytes[4] === 2 && bytes[5] === 1 && bytes[6] === 1 &&
    [2, 3].includes(bytes.readUInt16LE(16)) &&
    bytes.readUInt16LE(18) === 62 && bytes.readUInt32LE(20) === 1 &&
    bytes.readUInt16LE(52) >= 64,
  'Guest agent must be a little-endian Linux ELF64 x86_64 executable.')
}

export function parsePackageIndex(source) {
  requireValue(typeof source === 'string' && !source.includes('\0'), 'Invalid Debian Packages index.')
  const entries = []
  let fields = Object.create(null)
  let previousField = null
  const finishEntry = () => {
    if (Object.keys(fields).length === 0) return
    for (const field of ['Package', 'Version', 'Architecture', 'Filename', 'SHA256']) {
      requireValue(validText(fields[field]), `Incomplete Debian package index entry: ${field}`)
    }
    requireValue(packagePattern.test(fields.Package) && versionPattern.test(fields.Version) &&
      ['amd64', 'all'].includes(fields.Architecture) && digestPattern.test(fields.SHA256),
    'Invalid Debian package index entry.')
    relativeParts(fields.Filename)
    entries.push(fields)
    fields = Object.create(null)
    previousField = null
  }

  for (const line of source.split(/\r?\n/)) {
    if (line === '') {
      finishEntry()
      continue
    }
    if (/^[ \t]/.test(line)) {
      requireValue(previousField !== null, 'Invalid Debian package continuation line.')
      continue
    }
    const separator = line.indexOf(': ')
    requireValue(separator > 0 && /^[A-Za-z0-9-]+$/.test(line.slice(0, separator)),
      'Invalid Debian package index field.')
    const name = line.slice(0, separator)
    requireValue(!Object.hasOwn(fields, name), `Duplicate Debian package index field: ${name}`)
    fields[name] = line.slice(separator + 2)
    previousField = name
  }
  finishEntry()
  return entries
}

async function verifyLockedPackagesAgainstIndexes(bundle, lock) {
  const indexedPackages = new Map()
  for (const repository of lock.repositories) {
    for (const index of repository.indexes) {
      const file = await regularFile(bundle, index.path)
      const compressed = await readFile(file)
      requireValue(compressed.length > 0 && compressed.length <= maximumCompressedIndexBytes,
        `Invalid compressed package index size: ${index.path}`)
      const expanded = gunzipSync(compressed, { maxOutputLength: maximumExpandedIndexBytes })
      const text = expanded.toString('utf8')
      requireValue(Buffer.from(text, 'utf8').equals(expanded),
        `Package index is not valid UTF-8: ${index.path}`)
      for (const entry of parsePackageIndex(text)) {
        const key = `${repository.root}|${entry.Package}|${entry.Version}|${entry.Architecture}`
        const matches = indexedPackages.get(key) ?? []
        matches.push(entry)
        indexedPackages.set(key, matches)
      }
    }
  }

  for (const pkg of lock.packages) {
    const key = `${pkg.repository}|${pkg.name}|${pkg.version}|${pkg.architecture}`
    const matches = indexedPackages.get(key) ?? []
    const expectedFilename = pkg.archive.slice(`${pkg.repository}/`.length)
    requireValue(matches.length === 1 &&
      matches[0].Filename === expectedFilename &&
      matches[0].SHA256.toLowerCase() === pkg.sha256,
    `Locked package does not match exactly one signed Packages index entry: ${pkg.name}`)
  }
}

export function verifyGuestBootConfiguration(fstab, grubConfiguration, kernelRelease) {
  requireValue(typeof kernelRelease === 'string' &&
    /^6\.12\.[0-9]+(?:\+[a-z0-9.-]+)?$/.test(kernelRelease),
  'Invalid kernel release for boot configuration.')
  const rootUuid = '42506567-6510-4000-8000-000000000001'
  const expectedFstab = [
    `UUID=${rootUuid} / ext4 ro,nodev 0 1`,
    'tmpfs /tmp tmpfs rw,nosuid,nodev,size=128M,mode=1777 0 0',
    'tmpfs /var/log tmpfs rw,nosuid,nodev,noexec,size=16M 0 0',
    '',
  ].join('\n')
  const expectedGrub = [
    'set timeout=0',
    'set default=0',
    "menuentry 'AIPlatform static appliance' {",
    `    search --no-floppy --fs-uuid --set=root ${rootUuid}`,
    `    linux /boot/vmlinuz-${kernelRelease} root=UUID=${rootUuid} ro quiet`,
    `    initrd /boot/initrd.img-${kernelRelease}`,
    '}',
    '',
  ].join('\n')
  requireValue(fstab === expectedFstab, 'Unexpected guest filesystem or mount configuration.')
  requireValue(grubConfiguration === expectedGrub,
    'Unexpected GRUB entry, kernel, root UUID, or boot arguments.')
}

export function verifyEspBootConfiguration(grubConfiguration) {
  const expected = [
    'search --no-floppy --fs-uuid --set=root 42506567-6510-4000-8000-000000000001',
    'configfile /boot/grub/grub.cfg',
    '',
  ].join('\n')
  requireValue(grubConfiguration === expected, 'Unexpected EFI GRUB configuration.')
}

export function verifyPartitionTable(partitionTable) {
  const table = partitionTable?.partitiontable
  const expected = [
    { start: 2048, size: 524288, type: 'C12A7328-F81F-11D2-BA4B-00A0C93EC93B',
      uuid: '42506567-6510-4000-8000-000000000003' },
    { start: 526336, size: 12054528, type: '0FC63DAF-8483-4772-8E79-3D69D8477DE4',
      uuid: '42506567-6510-4000-8000-000000000004' },
  ]
  requireValue(table?.label === 'gpt' &&
    String(table.id).toUpperCase() === '42506567-6510-4000-8000-000000000002' &&
    table.unit === 'sectors' && table.sectorsize === 512 &&
    Array.isArray(table.partitions) && table.partitions.length === expected.length,
  'Unexpected GPT label, identifier, sector size, or partition count.')
  for (let index = 0; index < expected.length; index++) {
    const actual = table.partitions[index]
    const partition = expected[index]
    requireValue(actual.start === partition.start && actual.size === partition.size &&
      String(actual.type).toUpperCase() === partition.type &&
      String(actual.uuid).toUpperCase() === partition.uuid,
    `Unexpected GPT partition ${index + 1}.`)
  }
  requireValue(table.partitions[1].start + table.partitions[1].size === 12580864,
    'GPT partitions do not cover the expected 6 GiB disk geometry.')
}

async function verifyAgentPackaging(root, lock) {
  const agent = lock.prototypeComponents.find(component => component.role === 'guest-agent')
  const agentEntries = lock.overlay.filter(entry => entry.source === agent.path)
  requireValue(agentEntries.length === 1 &&
    agentEntries[0].target === 'usr/local/lib/preview/agent' &&
    agentEntries[0].mode === 493,
  'Guest agent must be installed as the fixed executable /usr/local/lib/preview/agent.')
  verifyLinuxX64Elf(await readFile(await regularFile(root, agent.path)))

  const expectedTemplates = [
    ['etc/systemd/system/preview-agent.service', 'preview-agent.service'],
    ['etc/systemd/system/preview-agent.path', 'preview-agent.path'],
    ['etc/sysusers.d/preview-agent.conf', 'preview-agent.sysusers'],
    ['etc/systemd/system/preview-session.target', 'preview-session.target'],
  ]
  for (const [target, template] of expectedTemplates) {
    const entry = lock.overlay.find(candidate => candidate.target === target)
    requireValue(entry && entry.mode === 420, `Missing or incorrectly permissioned guest agent file: ${target}`)
    const actual = await readFile(await regularFile(root, entry.source))
    const expected = await readFile(new URL(`./${template}`, import.meta.url))
    requireValue(actual.equals(expected), `Guest agent packaging differs from reviewed template: ${target}`)
  }
  requireValue(!lock.overlay.some(entry => entry.target === 'etc/preview/agent-runtime-approved'),
    'The guest-agent runtime approval gate must not be included in the appliance.')
}

export function verifyRepositorySignatures(bundle, lock) {
  const keyring = path.join(bundle, ...relativeParts(lock.keyring))
  for (const repository of lock.repositories) {
    const inRelease = path.join(bundle, ...relativeParts(repository.inRelease))
    const status = execFileSync('gpgv', ['--status-fd=1', '--keyring', keyring, inRelease], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'inherit'],
    })
    verifySignatureStatus(repository, status)
  }
}

function relativeParts(value) {
  requireValue(typeof value === 'string' && /^[a-zA-Z0-9_+.~/-]+$/.test(value), 'Invalid bundle path.')
  const parts = value.split('/')
  requireValue(parts.every(part => part && part !== '.' && part !== '..'), 'Bundle path traversal.')
  return parts
}

async function ensureNoSymlinkAncestors(value) {
  const absolute = path.resolve(value)
  const parsed = path.parse(absolute)
  let current = parsed.root
  for (const part of absolute.slice(parsed.root.length).split(path.sep).filter(Boolean)) {
    current = path.join(current, part)
    const stat = await lstat(current)
    requireValue(!stat.isSymbolicLink(), `Symlink path component refused: ${current}`)
  }
  return absolute
}

async function regularFile(root, relative) {
  let current = root
  const parts = relativeParts(relative)
  for (let i = 0; i < parts.length; i++) {
    current = path.join(current, parts[i])
    const stat = await lstat(current)
    requireValue(!stat.isSymbolicLink(), `Symlink refused: ${relative}`)
    requireValue(i === parts.length - 1 ? stat.isFile() : stat.isDirectory(), `Invalid file: ${relative}`)
  }
  return current
}

export async function verifyInputs(bundle, lockFile, expectedDigest) {
  requireValue(digestPattern.test(expectedDigest), 'A separately reviewed lock SHA-256 is required.')
  const absoluteLockFile = await ensureNoSymlinkAncestors(lockFile)
  const lockStat = await lstat(absoluteLockFile)
  requireValue(lockStat.isFile() && !lockStat.isSymbolicLink() && lockStat.size <= 1048576, 'Invalid lock file.')
  const bytes = await readFile(absoluteLockFile)
  requireValue(createHash('sha256').update(bytes).digest('hex') === expectedDigest, 'Lock digest mismatch.')
  const lockText = bytes.toString('utf8')
  rejectDuplicateJsonProperties(lockText)
  const lock = JSON.parse(lockText)
  exactKeys(lock, topLevelKeys, 'Lock')
  requireValue(lock.schemaVersion === 1 && lock.distribution === 'trixie' && lock.architecture === 'amd64',
    'Only reviewed Debian trixie amd64 inputs are supported.')
  requireValue(Number.isSafeInteger(lock.sourceDateEpoch) && lock.sourceDateEpoch > 0, 'Invalid source epoch.')
  requireValue(typeof lock.kernelRelease === 'string' &&
    /^6\.12\.[0-9]+(?:\+[a-z0-9.-]+)?$/.test(lock.kernelRelease), 'Invalid locked Debian kernel release.')
  for (const field of ['keyring', 'grubSecureBootCertificate', 'kernelSecureBootCertificate',
    'microsoftUefiCertificate']) {
    relativeParts(lock[field])
  }
  requireValue(Array.isArray(lock.files) && lock.files.length > 0 && lock.files.length <= 20000, 'Missing file inventory.')
  const root = path.resolve(bundle)
  const checkedRoot = await ensureNoSymlinkAncestors(root)
  const rootStat = await lstat(checkedRoot)
  requireValue(rootStat.isDirectory() && !rootStat.isSymbolicLink(), 'Bundle must be a real directory.')
  const files = new Map()
  for (const file of lock.files) {
    exactKeys(file, ['path', 'sha256', 'origin', 'version'], 'Bundle file entry')
    relativeParts(file.path)
    requireValue(!files.has(file.path), 'Duplicate bundle path.')
    requireValue(digestPattern.test(file.sha256) && validText(file.origin) &&
      versionPattern.test(file.version), `Unpinned input: ${file.path}`)
    files.set(file.path, file)
  }
  for (const key of ['keyring', 'grubSecureBootCertificate', 'kernelSecureBootCertificate',
    'microsoftUefiCertificate']) {
    requireValue(files.has(lock[key]), `Missing trust input: ${key}`)
  }
  requireValue(Array.isArray(lock.repositories) && lock.repositories.length === repositoryDefinitions.length,
    'Exactly two signed Debian repositories are required.')
  for (let index = 0; index < repositoryDefinitions.length; index++) {
    const repository = lock.repositories[index]
    exactKeys(repository, ['root', 'suite', 'inRelease', 'signerFingerprints', 'indexes'], 'Repository')
    const definition = repositoryDefinitions[index]
    requireValue(repository.root === definition.root && repository.suite === definition.suite &&
      repository.inRelease === definition.inRelease, 'Unexpected Debian repository configuration.')
    requireValue(files.has(repository.inRelease), `Missing signed repository metadata: ${repository.inRelease}`)
    requireValue(Array.isArray(repository.signerFingerprints) && repository.signerFingerprints.length > 0 &&
      repository.signerFingerprints.length <= 8 &&
      repository.signerFingerprints.every(fingerprint => fingerprintPattern.test(fingerprint)) &&
      new Set(repository.signerFingerprints).size === repository.signerFingerprints.length,
    `Missing or invalid pinned signer fingerprints: ${repository.suite}`)
    requireValue(Array.isArray(repository.indexes) && repository.indexes.length > 0 &&
      repository.indexes.length <= 64, `Missing signed package indexes: ${repository.suite}`)
    const indexPaths = new Set()
    for (const indexEntry of repository.indexes) {
      exactKeys(indexEntry, ['path', 'sha256'], 'Repository index')
      relativeParts(indexEntry.path)
      const file = files.get(indexEntry.path)
      requireValue(file && digestPattern.test(indexEntry.sha256) && file.sha256 === indexEntry.sha256 &&
        indexEntry.path.startsWith(`${repository.root}/dists/${repository.suite}/`) &&
        !indexPaths.has(indexEntry.path), 'Repository index is not pinned by the bundle inventory.')
      indexPaths.add(indexEntry.path)
    }
    requireValue([...indexPaths].some(indexPath => requiredIndexPattern.test(indexPath) &&
      indexPath.startsWith(`${repository.root}/`)), `Missing amd64 Packages index: ${repository.suite}`)
  }
  requireValue(Array.isArray(lock.packages) && lock.packages.length > 0 && lock.packages.length <= 20000,
    'Missing transitive package lock.')
  const packages = new Map()
  for (const pkg of lock.packages) {
    exactKeys(pkg, ['name', 'version', 'architecture', 'repository', 'archive', 'sha256'], 'Package entry')
    requireValue(packagePattern.test(pkg.name) && versionPattern.test(pkg.version) &&
      ['amd64', 'all'].includes(pkg.architecture) && ['debian', 'security'].includes(pkg.repository) &&
      !packages.has(pkg.name) && typeof pkg.archive === 'string' && digestPattern.test(pkg.sha256),
    'Invalid or duplicate package.')
    relativeParts(pkg.archive)
    const archive = files.get(pkg.archive)
    requireValue(archive && archive.sha256 === pkg.sha256 && archive.version === pkg.version &&
      pkg.archive.startsWith(`${pkg.repository}/pool/`), `Package archive is not pinned: ${pkg.name}`)
    packages.set(pkg.name, pkg)
  }
  for (const name of [...requiredPackages, `linux-image-${lock.kernelRelease}`]) {
    requireValue(packages.has(name), `Missing required package: ${name}`)
  }
  for (const name of ['chromium-common', 'chromium-sandbox']) {
    requireValue(packages.get(name).version === packages.get('chromium').version, 'Chromium versions differ.')
  }
  requireValue(Array.isArray(lock.builderTools) && lock.builderTools.length === requiredTools.length,
    'Builder tool inventory must be complete and contain no extra tools.')
  for (const name of requiredTools) {
    const matches = lock.builderTools.filter(tool => tool.name === name)
    requireValue(matches.length === 1, `Missing or duplicate builder tool: ${name}`)
    exactKeys(matches[0], ['name', 'path', 'version', 'sha256'], 'Builder tool')
    requireValue(digestPattern.test(matches[0].sha256) && versionPattern.test(matches[0].version) &&
      [ `/usr/bin/${name}`, `/usr/sbin/${name}` ].includes(matches[0].path),
    `Unpinned builder tool: ${name}`)
  }
  requireValue(new Set(lock.builderTools.map(tool => tool.name)).size === lock.builderTools.length, 'Duplicate builder tool.')
  requireValue(Array.isArray(lock.prototypeComponents) && lock.prototypeComponents.length === requiredRoles.length,
    'Prototype component inventory must be complete.')
  for (const role of requiredRoles) {
    const matches = lock.prototypeComponents.filter(component => component.role === role)
    requireValue(matches.length === 1, `Missing or duplicate prototype component: ${role}`)
    exactKeys(matches[0], ['role', 'path', 'version'], 'Prototype component')
    requireValue(files.has(matches[0].path) && versionPattern.test(matches[0].version),
      `Missing or unpinned prototype component: ${role}`)
  }
  requireValue(Array.isArray(lock.overlay) && lock.overlay.length > 0 && lock.overlay.length <= 4096,
    'Missing reviewed guest configuration.')
  const targets = new Set()
  for (const entry of lock.overlay) {
    exactKeys(entry, ['source', 'target', 'mode'], 'Overlay entry')
    relativeParts(entry.target)
    requireValue(files.has(entry.source) &&
      (/^etc\/systemd\/system\/preview-[a-zA-Z0-9_.@-]+$/.test(entry.target) ||
       /^etc\/sysusers\.d\/preview-[a-zA-Z0-9_.-]+\.conf$/.test(entry.target) ||
       /^etc\/preview\/[a-zA-Z0-9_.-]+$/.test(entry.target) ||
       /^usr\/local\/lib\/preview\/[a-zA-Z0-9_.+-]+$/.test(entry.target)) &&
      [384, 420, 493].includes(entry.mode) && !targets.has(entry.target), 'Invalid overlay target or mode.')
    targets.add(entry.target)
  }
  for (const target of requiredOverlayTargets) {
    requireValue(targets.has(target), `Missing runtime configuration: ${target}`)
  }
  for (const component of lock.prototypeComponents.filter(c => ['guest-agent', 'weston-module'].includes(c.role))) {
    requireValue(lock.overlay.some(entry => entry.source === component.path), `Component not installed: ${component.role}`)
  }
  await verifyLockedPackagesAgainstIndexes(root, lock)
  for (const file of files.values()) {
    requireValue(await sha256(await regularFile(root, file.path)) === file.sha256, `Input digest mismatch: ${file.path}`)
  }
  await verifyAgentPackaging(root, lock)
  let count = 0
  async function inventory(directory, prefix = '') {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      requireValue(++count <= 25000, 'Bundle inventory limit exceeded.')
      const relative = prefix + entry.name
      if (entry.isDirectory()) await inventory(path.join(directory, entry.name), relative + '/')
      else requireValue(entry.isFile() && files.has(relative), `Unlisted or nonregular bundle file: ${relative}`)
    }
  }
  await inventory(root)
  return lock
}

export function verifyInstalled(lock, inventory) {
  requireValue(typeof inventory === 'string' && !inventory.includes('\0'), 'Invalid installed package inventory.')
  const lines = inventory.split('\n')
  if (lines[lines.length - 1] === '') lines.pop()
  requireValue(lines.length > 0, 'Installed package inventory is empty.')
  const seen = new Set()
  const actual = lines.map(line => {
    const fields = line.split('\t')
    requireValue(fields.length === 4, 'Malformed installed package inventory row.')
    const [name, version, architecture, status] = fields
    requireValue(packagePattern.test(name) && versionPattern.test(version) &&
      ['amd64', 'all'].includes(architecture), 'Malformed installed package identity.')
    requireValue(status === 'installed', `Package not installed: ${name}`)
    requireValue(!seen.has(name), `Duplicate installed package: ${name}`)
    seen.add(name)
    return { name, version, architecture }
  }).sort((a, b) => a.name.localeCompare(b.name, 'en'))
  const expected = [...lock.packages].map(({ name, version, architecture }) => ({ name, version, architecture }))
    .sort((a, b) => a.name.localeCompare(b.name, 'en'))
  requireValue(JSON.stringify(actual) === JSON.stringify(expected), 'Installed package closure differs from lock.')
}

export async function applyOverlay(bundle, lock, root) {
  const absoluteRoot = await ensureNoSymlinkAncestors(root)
  const rootStat = await lstat(absoluteRoot)
  requireValue(rootStat.isDirectory() && !rootStat.isSymbolicLink(), 'Overlay root must be a real directory.')
  const createdDirectories = []
  const createdFiles = []
  try {
    for (const entry of lock.overlay) {
      let parent = absoluteRoot
      for (const part of relativeParts(entry.target).slice(0, -1)) {
        parent = path.join(parent, part)
        try {
          await mkdir(parent)
          createdDirectories.push(parent)
        } catch (error) {
          if (error.code !== 'EEXIST') throw error
        }
        const stat = await lstat(parent)
        requireValue(stat.isDirectory() && !stat.isSymbolicLink(), 'Overlay directory symlink refused.')
      }

      const target = path.join(absoluteRoot, ...relativeParts(entry.target))
      const source = await regularFile(bundle, entry.source)
      const lockedFile = lock.files.find(file => file.path === entry.source)
      requireValue(lockedFile && await sha256(source) === lockedFile.sha256, 'Overlay changed or is not pinned.')
      const sourceHandle = await open(source, 'r')
      let targetHandle
      try {
        targetHandle = await open(target, 'wx', entry.mode)
        createdFiles.push(target)
        for await (const chunk of sourceHandle.createReadStream()) {
          let offset = 0
          while (offset < chunk.length) {
            const result = await targetHandle.write(chunk, offset, chunk.length - offset)
            requireValue(result.bytesWritten > 0, 'Overlay write made no progress.')
            offset += result.bytesWritten
          }
        }
        await targetHandle.chmod(entry.mode)
        await targetHandle.sync()
      } finally {
        await sourceHandle.close()
        if (targetHandle) await targetHandle.close()
      }
    }
  } catch (error) {
    const cleanupErrors = []
    for (const file of createdFiles.reverse()) {
      try {
        await unlink(file)
      } catch (cleanupError) {
        if (cleanupError.code !== 'ENOENT') cleanupErrors.push(cleanupError)
      }
    }
    for (const directory of createdDirectories.reverse()) {
      try {
        await rmdir(directory)
      } catch (cleanupError) {
        if (cleanupError.code !== 'ENOENT') cleanupErrors.push(cleanupError)
      }
    }
    if (cleanupErrors.length > 0) {
      throw new AggregateError([error, ...cleanupErrors], 'Overlay installation failed and rollback was incomplete.')
    }
    throw error
  }
}

export async function verifyRuntimeApprovalGateAbsent(root) {
  const absoluteRoot = await ensureNoSymlinkAncestors(root)
  let parent = absoluteRoot
  for (const part of ['etc', 'preview']) {
    parent = path.join(parent, part)
    try {
      const stat = await lstat(parent)
      requireValue(stat.isDirectory() && !stat.isSymbolicLink(), 'Preview approval gate path is not a real directory.')
    } catch (error) {
      if (error.code === 'ENOENT') return
      throw error
    }
  }
  try {
    await lstat(path.join(parent, 'agent-runtime-approved'))
  } catch (error) {
    if (error.code === 'ENOENT') return
    throw error
  }
  throw new Error('Preview runtime approval gate must not be present in the built image.')
}

export async function enablePreviewSessionTarget(root, createSymlink = symlink) {
  const absoluteRoot = await ensureNoSymlinkAncestors(root)
  await regularFile(absoluteRoot, 'etc/systemd/system/preview-session.target')
  await regularFile(absoluteRoot, 'etc/systemd/system/preview-agent.path')
  const systemdDirectory = path.join(absoluteRoot, 'etc', 'systemd', 'system')
  const links = [
    {
      directory: path.join(systemdDirectory, 'multi-user.target.wants'),
      name: 'preview-session.target',
      target: '../preview-session.target',
    },
    {
      directory: path.join(systemdDirectory, 'preview-session.target.wants'),
      name: 'preview-agent.path',
      target: '../preview-agent.path',
    },
  ]
  const createdDirectories = []
  const createdLinks = []
  try {
    for (const link of links) {
      try {
        await mkdir(link.directory)
        createdDirectories.push(link.directory)
      } catch (error) {
        if (error.code !== 'EEXIST') throw error
      }
      const stat = await lstat(link.directory)
      requireValue(stat.isDirectory() && !stat.isSymbolicLink(),
        'systemd unit wants path must be a real directory.')
      const linkPath = path.join(link.directory, link.name)
      await createSymlink(link.target, linkPath, 'file')
      createdLinks.push(linkPath)
    }
  } catch (error) {
    const cleanupErrors = []
    for (const linkPath of createdLinks.reverse()) {
      try {
        await unlink(linkPath)
      } catch (cleanupError) {
        if (cleanupError.code !== 'ENOENT') cleanupErrors.push(cleanupError)
      }
    }
    for (const directory of createdDirectories.reverse()) {
      try {
        await rmdir(directory)
      } catch (cleanupError) {
        if (cleanupError.code !== 'ENOENT') cleanupErrors.push(cleanupError)
      }
    }
    if (cleanupErrors.length > 0)
      throw new AggregateError([error, ...cleanupErrors], 'Could not roll back systemd unit installation.')
    throw error
  }
}

export async function installOverlay(bundle, lock, root) {
  requireValue(process.platform === 'linux' && typeof process.getuid === 'function' && process.getuid() === 0,
    'Overlay installation requires the approved Linux builder.')
  await applyOverlay(bundle, lock, root)
}

export async function writeManifest(output, lockDigest, artifacts, lock = null) {
  requireValue(digestPattern.test(lockDigest), 'Invalid lock digest.')
  const names = new Set()
  const entries = []
  for (const file of artifacts) {
    const stat = await lstat(file)
    const name = path.basename(file)
    requireValue(stat.isFile() && !stat.isSymbolicLink() &&
      /^[A-Za-z0-9][A-Za-z0-9._+-]{0,255}$/.test(name) && !names.has(name), 'Invalid or duplicate artifact.')
    names.add(name)
    entries.push({ name, bytes: stat.size, sha256: await sha256(file) })
  }
  requireValue(entries.length > 0, 'No build artifacts.')
  entries.sort((a, b) => a.name.localeCompare(b.name, 'en'))
  const manifest = {
    schemaVersion: 1, stage: 'built-not-boot-validated', executionEligible: false,
    inputLockSha256: lockDigest, artifacts: entries,
  }
  if (lock) {
    manifest.buildInputs = {
      distribution: lock.distribution,
      architecture: lock.architecture,
      kernelRelease: lock.kernelRelease,
      sourceDateEpoch: lock.sourceDateEpoch,
    }
  }
  await writeFile(output, JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx' })
}

export function verifyImageInfo(info, fileBytes) {
  requireValue(info.format === 'vhdx' && info['virtual-size'] === 6442450944 &&
    Number.isSafeInteger(info['actual-size']) && info['actual-size'] >= info['virtual-size'] &&
    info['format-specific']?.type === 'vhdx' &&
    info['format-specific']?.data?.has_parent === false &&
    !info['backing-filename'] && !info['full-backing-filename'],
  'Unexpected VHDX geometry, backing disk, or format metadata.')
  requireValue(fileBytes >= 6442450944 && fileBytes <= 6509559808,
    'VHDX file size outside the fixed-allocation budget.')
  requireValue(info['actual-size'] <= 6509559808,
    'VHDX actual allocated size exceeds the fixed-allocation budget.')
}

export function verifyImageMap(extents, virtualSize = 6442450944) {
  requireValue(Array.isArray(extents) && extents.length > 0, 'VHDX allocation map is empty.')
  let expectedStart = 0
  for (const extent of extents) {
    requireValue(extent.start === expectedStart && Number.isSafeInteger(extent.length) &&
      extent.length > 0 && typeof extent.data === 'boolean' && extent.present === true,
    'VHDX contains a gap or unallocated extent.')
    expectedStart += extent.length
  }
  requireValue(expectedStart === virtualSize, 'VHDX allocation map does not cover its virtual geometry.')
}

export async function compareBuildManifests(firstPath, secondPath, outputPath) {
  const first = JSON.parse(await readFile(firstPath, 'utf8'))
  const second = JSON.parse(await readFile(secondPath, 'utf8'))
  for (const manifest of [first, second]) {
    requireValue(manifest.schemaVersion === 1 && manifest.stage === 'built-not-boot-validated' &&
      manifest.executionEligible === false && digestPattern.test(manifest.inputLockSha256) &&
      Array.isArray(manifest.artifacts), 'Invalid build manifest.')
    const names = new Set()
    for (const artifact of manifest.artifacts) {
      requireValue(artifact && /^[A-Za-z0-9][A-Za-z0-9._+-]{0,255}$/.test(artifact.name) &&
        Number.isSafeInteger(artifact.bytes) &&
        artifact.bytes >= 0 && digestPattern.test(artifact.sha256) && !names.has(artifact.name),
      'Invalid or duplicate manifest artifact.')
      names.add(artifact.name)
    }
    if (manifest.buildInputs !== undefined) {
      exactKeys(manifest.buildInputs,
        ['distribution', 'architecture', 'kernelRelease', 'sourceDateEpoch'], 'Manifest build inputs')
      requireValue(manifest.buildInputs.distribution === 'trixie' &&
        manifest.buildInputs.architecture === 'amd64' &&
        /^6\.12\.[0-9]+(?:\+[a-z0-9.-]+)?$/.test(manifest.buildInputs.kernelRelease) &&
        Number.isSafeInteger(manifest.buildInputs.sourceDateEpoch) &&
        manifest.buildInputs.sourceDateEpoch > 0,
      'Invalid manifest build inputs.')
    }
  }
  requireValue(first.inputLockSha256 === second.inputLockSha256 &&
    JSON.stringify(first.buildInputs ?? null) === JSON.stringify(second.buildInputs ?? null),
  'Build manifests do not share identical locked inputs.')
  const firstArtifacts = new Map(first.artifacts.map(artifact => [artifact.name, artifact]))
  const secondArtifacts = new Map(second.artifacts.map(artifact => [artifact.name, artifact]))
  const names = [...new Set([...firstArtifacts.keys(), ...secondArtifacts.keys()])].sort()
  const differences = names.filter(name => {
    const left = firstArtifacts.get(name)
    const right = secondArtifacts.get(name)
    return !left || !right || left.bytes !== right.bytes || left.sha256 !== right.sha256
  })
  const report = {
    schemaVersion: 1,
    inputLockSha256: first.inputLockSha256,
    bitwiseReproducible: differences.length === 0 && firstArtifacts.size === secondArtifacts.size,
    comparedArtifacts: names,
    differences,
  }
  await writeFile(outputPath, JSON.stringify(report, null, 2) + '\n', { flag: 'wx' })
  return report
}

async function main() {
  const [command, ...args] = process.argv.slice(2)
  if (command === 'manifest') {
    const [output, bundle, lockFile, lockDigest, ...artifacts] = args
    requireValue(artifacts.length > 0, 'Manifest requires build artifacts.')
    const lock = await verifyInputs(bundle, lockFile, lockDigest)
    await writeManifest(output, lockDigest, artifacts, lock)
    return
  }
  if (command === 'compare-manifests') {
    const [first, second, output] = args
    requireValue(first && second && output, 'Manifest comparison requires two manifests and a new report path.')
    const report = await compareBuildManifests(first, second, output)
    console.log(JSON.stringify(report))
    return
  }
  if (command === 'partition-info') {
    const [partitionInfo, summaryPath] = args
    const partitionTable = JSON.parse(await readFile(partitionInfo, 'utf8'))
    verifyPartitionTable(partitionTable)
    if (summaryPath) {
      const table = partitionTable.partitiontable
      await writeFile(summaryPath, JSON.stringify({
        label: table.label,
        id: String(table.id).toUpperCase(),
        unit: table.unit,
        sectorsize: table.sectorsize,
        partitions: table.partitions.map(partition => ({
          start: partition.start,
          size: partition.size,
          type: String(partition.type).toUpperCase(),
          uuid: String(partition.uuid).toUpperCase(),
        })),
      }, null, 2) + '\n', { flag: 'wx' })
    }
    return
  }
  if (command === 'boot-config') {
    const [fstab, grubConfiguration, kernelRelease] = args
    verifyGuestBootConfiguration(await readFile(fstab, 'utf8'),
      await readFile(grubConfiguration, 'utf8'), kernelRelease)
    return
  }
  if (command === 'esp-boot-config') {
    const [grubConfiguration] = args
    verifyEspBootConfiguration(await readFile(grubConfiguration, 'utf8'))
    return
  }
  if (command === 'image-map') {
    const [mapFile, summaryPath] = args
    const extents = JSON.parse(await readFile(mapFile, 'utf8'))
    verifyImageMap(extents)
    if (summaryPath) {
      await writeFile(summaryPath, JSON.stringify(extents.map(extent => ({
        start: extent.start,
        length: extent.length,
        data: extent.data,
        present: extent.present,
      })), null, 2) + '\n', { flag: 'wx' })
    }
    return
  }
  if (command === 'runtime-gate') {
    const [root] = args
    await verifyRuntimeApprovalGateAbsent(root)
    return
  }
  if (command === 'enable-session') {
    const [root] = args
    await enablePreviewSessionTarget(root)
    return
  }
  if (command === 'image-info') {
    const [info, image, summaryPath] = args
    const stat = await lstat(image)
    requireValue(stat.isFile() && !stat.isSymbolicLink(), 'Invalid VHDX file.')
    const imageInfo = JSON.parse(await readFile(info, 'utf8'))
    verifyImageInfo(imageInfo, stat.size)
    if (summaryPath) {
      await writeFile(summaryPath, JSON.stringify({
        format: imageInfo.format,
        virtualSize: imageInfo['virtual-size'],
        actualSize: imageInfo['actual-size'],
        formatSpecificType: imageInfo['format-specific'].type,
        hasParent: imageInfo['format-specific'].data.has_parent,
      }, null, 2) + '\n', { flag: 'wx' })
    }
    return
  }
  const [bundle, lockFile, digest, extra] = args
  requireValue(['verify', 'signatures', 'includes', 'tools', 'installed', 'overlay', 'field'].includes(command),
    'Usage: inputs.mjs verify|includes|tools|installed|overlay|field BUNDLE LOCK REVIEWED_SHA256 [argument]')
  const lock = await verifyInputs(bundle, lockFile, digest)
  if (command === 'verify') console.log('Input bytes match reviewed lock; repository and PE signatures still require build checks.')
  if (command === 'signatures') verifyRepositorySignatures(bundle, lock)
  if (command === 'includes') console.log([...lock.packages].sort((a, b) => a.name.localeCompare(b.name, 'en'))
    .map(pkg => `${pkg.name}=${pkg.version}`).join(','))
  if (command === 'tools') {
    requireValue(process.platform === 'linux', 'Builder tool verification requires Linux; WSL is not launched.')
    for (const tool of lock.builderTools) {
      requireValue(/^\/usr\/bin\/[a-zA-Z0-9.+-]+$/.test(tool.path) || /^\/usr\/sbin\/[a-zA-Z0-9.+-]+$/.test(tool.path),
        'Builder tools must use reviewed /usr/bin or /usr/sbin paths.')
      requireValue(path.posix.basename(tool.path) === tool.name, 'Builder tool name/path mismatch.')
      let selected
      for (const directory of ['/usr/sbin', '/usr/bin']) {
        const candidate = path.join(directory, tool.name)
        try {
          await lstat(candidate)
          selected = candidate
          break
        } catch (error) {
          if (error.code !== 'ENOENT') throw error
        }
      }
      requireValue(selected && await realpath(selected) === await realpath(tool.path),
        `Builder PATH differs from lock: ${tool.name}`)
      requireValue(await sha256(tool.path) === tool.sha256, `Builder tool changed: ${tool.name}`)
    }
  }
  if (command === 'installed') verifyInstalled(lock, await readFile(extra, 'utf8'))
  if (command === 'overlay') await installOverlay(bundle, lock, extra)
  if (command === 'field') {
    requireValue(['kernelRelease', 'sourceDateEpoch', 'keyring', 'grubSecureBootCertificate',
      'kernelSecureBootCertificate', 'microsoftUefiCertificate'].includes(extra), 'Unknown lock field.')
    console.log(lock[extra])
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => {
    console.error(`Appliance preparation failed: ${error.message}`)
    process.exitCode = 1
  })
}
