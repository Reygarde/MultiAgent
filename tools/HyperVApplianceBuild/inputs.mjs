import { createHash } from 'node:crypto'
import { createReadStream } from 'node:fs'
import { chmod, copyFile, lstat, mkdir, readFile, readdir, realpath, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const digestPattern = /^[a-f0-9]{64}$/
const packagePattern = /^[a-z0-9][a-z0-9+.-]*$/
const versionPattern = /^[0-9][a-zA-Z0-9.+:~_-]*$/
const requiredTools = ['node', 'bash', 'uname', 'realpath', 'env', 'mkdir', 'cat', 'unshare', 'mount', 'gpgv', 'mmdebstrap', 'chroot',
  'dpkg-query', 'sfdisk', 'truncate', 'mkfs.ext4', 'mkfs.vfat', 'mmd', 'mcopy',
  'dd', 'qemu-img', 'sbverify', 'date']
const requiredPackages = ['base-files', 'dpkg', 'apt', 'systemd-sysv', 'shim-signed',
  'grub-efi-amd64-signed', 'chromium', 'chromium-common', 'chromium-sandbox',
  'weston', 'libweston-14-0', 'fonts-dejavu-core']
const requiredRoles = ['guest-agent', 'weston-module', 'host-broker', 'host-viewer', 'host-supervisor']

function requireValue(condition, message) {
  if (!condition) throw new Error(message)
}

export async function sha256(file) {
  const hash = createHash('sha256')
  for await (const chunk of createReadStream(file)) hash.update(chunk)
  return hash.digest('hex')
}

function relativeParts(value) {
  requireValue(typeof value === 'string' && /^[a-zA-Z0-9_+.~/-]+$/.test(value), 'Invalid bundle path.')
  const parts = value.split('/')
  requireValue(parts.every(part => part && part !== '.' && part !== '..'), 'Bundle path traversal.')
  return parts
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
  const lockStat = await lstat(lockFile)
  requireValue(lockStat.isFile() && !lockStat.isSymbolicLink() && lockStat.size <= 1048576, 'Invalid lock file.')
  const bytes = await readFile(lockFile)
  requireValue(createHash('sha256').update(bytes).digest('hex') === expectedDigest, 'Lock digest mismatch.')
  const lock = JSON.parse(bytes.toString('utf8'))
  requireValue(lock.schemaVersion === 1 && lock.distribution === 'trixie' && lock.architecture === 'amd64',
    'Only reviewed Debian trixie amd64 inputs are supported.')
  requireValue(Number.isSafeInteger(lock.sourceDateEpoch) && lock.sourceDateEpoch > 0, 'Invalid source epoch.')
  requireValue(/^[0-9][a-zA-Z0-9.+-]+$/.test(lock.kernelRelease), 'Invalid kernel release.')
  requireValue(Array.isArray(lock.files) && lock.files.length > 0 && lock.files.length <= 20000, 'Missing file inventory.')
  const root = path.resolve(bundle)
  const rootStat = await lstat(root)
  requireValue(rootStat.isDirectory() && !rootStat.isSymbolicLink(), 'Bundle must be a real directory.')
  const files = new Map()
  for (const file of lock.files) {
    relativeParts(file.path)
    requireValue(!files.has(file.path), 'Duplicate bundle path.')
    requireValue(digestPattern.test(file.sha256) && typeof file.origin === 'string' && file.origin.length > 0 &&
      typeof file.version === 'string' && file.version.length > 0, `Unpinned input: ${file.path}`)
    files.set(file.path, file)
  }
  for (const key of ['keyring', 'grubSecureBootCertificate', 'kernelSecureBootCertificate', 'microsoftUefiCertificate']) {
    requireValue(files.has(lock[key]), `Missing trust input: ${key}`)
  }
  for (const release of ['debian/dists/trixie/InRelease', 'security/dists/trixie-security/InRelease']) {
    requireValue(files.has(release), `Missing signed repository metadata: ${release}`)
  }
  requireValue(Array.isArray(lock.packages) && lock.packages.length > 0, 'Missing transitive package lock.')
  const packages = new Map()
  for (const pkg of lock.packages) {
    requireValue(packagePattern.test(pkg.name) && versionPattern.test(pkg.version) &&
      ['amd64', 'all'].includes(pkg.architecture) && !packages.has(pkg.name), 'Invalid or duplicate package.')
    packages.set(pkg.name, pkg)
  }
  for (const name of [...requiredPackages, `linux-image-${lock.kernelRelease}`]) {
    requireValue(packages.has(name), `Missing required package: ${name}`)
  }
  for (const name of ['chromium-common', 'chromium-sandbox']) {
    requireValue(packages.get(name).version === packages.get('chromium').version, 'Chromium versions differ.')
  }
  requireValue(Array.isArray(lock.builderTools), 'Missing builder tool lock.')
  for (const name of requiredTools) {
    const matches = lock.builderTools.filter(tool => tool.name === name)
    requireValue(matches.length === 1 && digestPattern.test(matches[0].sha256) &&
      typeof matches[0].version === 'string' && matches[0].version.length > 0, `Unpinned builder tool: ${name}`)
  }
  requireValue(new Set(lock.builderTools.map(tool => tool.name)).size === lock.builderTools.length, 'Duplicate builder tool.')
  requireValue(Array.isArray(lock.prototypeComponents), 'Missing prototype components.')
  for (const role of requiredRoles) {
    const matches = lock.prototypeComponents.filter(component => component.role === role)
    requireValue(matches.length === 1 && files.has(matches[0].path) &&
      typeof matches[0].version === 'string' && matches[0].version.length > 0, `Missing prototype component: ${role}`)
  }
  requireValue(Array.isArray(lock.overlay) && lock.overlay.length > 0, 'Missing reviewed guest configuration.')
  const targets = new Set()
  for (const entry of lock.overlay) {
    relativeParts(entry.target)
    requireValue(files.has(entry.source) && /^(etc\/|usr\/local\/lib\/preview\/)/.test(entry.target) &&
      entry.target !== 'etc/fstab' &&
      [384, 420, 493].includes(entry.mode) && !targets.has(entry.target), 'Invalid overlay target or mode.')
    targets.add(entry.target)
  }
  for (const target of ['etc/systemd/system/default.target', 'etc/systemd/system/preview-session.target',
    'etc/systemd/system/preview-agent.service', 'etc/systemd/system/preview-browser.service']) {
    requireValue(targets.has(target), `Missing runtime configuration: ${target}`)
  }
  for (const component of lock.prototypeComponents.filter(c => ['guest-agent', 'weston-module'].includes(c.role))) {
    requireValue(lock.overlay.some(entry => entry.source === component.path), `Component not installed: ${component.role}`)
  }
  for (const file of files.values()) {
    requireValue(await sha256(await regularFile(root, file.path)) === file.sha256, `Input digest mismatch: ${file.path}`)
  }
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
  const actual = inventory.trim().split('\n').map(line => {
    const [name, version, architecture, status] = line.trim().split('\t')
    requireValue(status === 'installed', `Package not installed: ${name}`)
    return { name, version, architecture }
  }).sort((a, b) => a.name.localeCompare(b.name, 'en'))
  const expected = [...lock.packages].map(({ name, version, architecture }) => ({ name, version, architecture }))
    .sort((a, b) => a.name.localeCompare(b.name, 'en'))
  requireValue(JSON.stringify(actual) === JSON.stringify(expected), 'Installed package closure differs from lock.')
}

export async function installOverlay(bundle, lock, root) {
  requireValue(process.platform === 'linux' && process.getuid() === 0, 'Overlay installation requires the approved Linux builder.')
  for (const entry of lock.overlay) {
    let parent = root
    for (const part of relativeParts(entry.target).slice(0, -1)) {
      parent = path.join(parent, part)
      try {
        await mkdir(parent)
      } catch (error) {
        if (error.code !== 'EEXIST') throw error
      }
      const stat = await lstat(parent)
      requireValue(stat.isDirectory() && !stat.isSymbolicLink(), 'Overlay directory symlink refused.')
    }
    const target = path.join(root, ...relativeParts(entry.target))
    try {
      const stat = await lstat(target)
      requireValue(stat.isFile() && !stat.isSymbolicLink(), 'Overlay target symlink refused.')
    } catch (error) {
      if (error.code !== 'ENOENT') throw error
    }
    const source = await regularFile(bundle, entry.source)
    requireValue(await sha256(source) === lock.files.find(file => file.path === entry.source).sha256, 'Overlay changed.')
    await copyFile(source, target)
    await chmod(target, entry.mode)
  }
}

export async function writeManifest(output, lockDigest, artifacts) {
  requireValue(digestPattern.test(lockDigest), 'Invalid lock digest.')
  const names = new Set()
  const entries = []
  for (const file of artifacts) {
    const stat = await lstat(file)
    requireValue(stat.isFile() && !stat.isSymbolicLink() && !names.has(path.basename(file)), 'Invalid or duplicate artifact.')
    names.add(path.basename(file))
    entries.push({ name: path.basename(file), bytes: stat.size, sha256: await sha256(file) })
  }
  requireValue(entries.length > 0, 'No build artifacts.')
  entries.sort((a, b) => a.name.localeCompare(b.name, 'en'))
  await writeFile(output, JSON.stringify({
    schemaVersion: 1, stage: 'built-not-boot-validated', executionEligible: false,
    inputLockSha256: lockDigest, artifacts: entries,
  }, null, 2) + '\n', { flag: 'wx' })
}

export function verifyImageInfo(info, fileBytes) {
  requireValue(info.format === 'vhdx' && info['virtual-size'] === 6442450944 &&
    !info['backing-filename'] && !info['full-backing-filename'], 'Unexpected VHDX geometry or backing disk.')
  requireValue(fileBytes >= 6442450944 && fileBytes <= 6509559808, 'Fixed VHDX file size outside budget.')
}

async function main() {
  const [command, ...args] = process.argv.slice(2)
  if (command === 'manifest') {
    const [output, lockDigest, ...artifacts] = args
    await writeManifest(output, lockDigest, artifacts)
    return
  }
  if (command === 'image-info') {
    const [info, image] = args
    const stat = await lstat(image)
    requireValue(stat.isFile() && !stat.isSymbolicLink(), 'Invalid VHDX file.')
    verifyImageInfo(JSON.parse(await readFile(info, 'utf8')), stat.size)
    return
  }
  const [bundle, lockFile, digest, extra] = args
  requireValue(['verify', 'includes', 'tools', 'installed', 'overlay', 'field'].includes(command),
    'Usage: inputs.mjs verify|includes|tools|installed|overlay|field BUNDLE LOCK REVIEWED_SHA256 [argument]')
  const lock = await verifyInputs(bundle, lockFile, digest)
  if (command === 'verify') console.log('Input bytes match reviewed lock; repository and PE signatures still require build checks.')
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
