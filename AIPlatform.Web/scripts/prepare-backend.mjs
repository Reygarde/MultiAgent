import { spawnSync } from 'node:child_process'
import { cpSync, mkdirSync, readdirSync, renameSync, statSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

if (process.platform !== 'win32' || process.arch !== 'x64') {
  throw new Error('Desktop packaging currently requires Windows x64.')
}
const web = fileURLToPath(new URL('../', import.meta.url))
const output = path.join(web, 'src-tauri', 'resources', 'backend')
const browserOutput = path.join(web, 'src-tauri', 'resources', 'studio-browser')
const binaries = path.join(web, 'src-tauri', 'binaries')
mkdirSync(output, { recursive: true })
mkdirSync(path.join(browserOutput, 'node_modules'), { recursive: true })
mkdirSync(binaries, { recursive: true })
const result = spawnSync('dotnet', [
  'publish', path.join(web, '..', 'AIPlatform.API', 'AIPlatform.API.csproj'),
  '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true',
  '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:StudioDesktopPublish=true', '-p:DebugType=None', '-p:DebugSymbols=false',
  '--output', output, '--nologo',
], { stdio: 'inherit', windowsHide: true })
if (result.error) throw result.error
if (result.status !== 0) throw new Error(`Backend publishing failed (${result.status}).`)
const allowed = new Set(['AIPlatform.API.exe', 'appsettings.json', 'appsettings.Desktop.json',
  'AIPlatform.API.staticwebassets.endpoints.json', 'web.config'])
for (const entry of readdirSync(output, { withFileTypes: true })) {
  if (!entry.isFile() || !allowed.has(entry.name)) {
    throw new Error(`Unexpected published resource ${entry.name}; review the desktop packaging allowlist.`)
  }
}
renameSync(path.join(output, 'AIPlatform.API.exe'), path.join(binaries, 'aiplatform-api-x86_64-pc-windows-msvc.exe'))

const helper = path.join(web, 'scripts', 'studio-browser-tests.mjs')
if (!statSync(helper).isFile()) throw new Error('The trusted browser test helper is missing.')
cpSync(helper, path.join(browserOutput, 'studio-browser-tests.mjs'))
cpSync(path.join(web, 'scripts', 'react-source-check.mjs'), path.join(browserOutput, 'react-source-check.mjs'))
cpSync(path.join(web, 'scripts', 'react-source-repair.mjs'), path.join(browserOutput, 'react-source-repair.mjs'))
for (const packageName of ['playwright', 'playwright-core', 'typescript']) {
  const source = path.join(web, 'node_modules', packageName)
  if (!statSync(source).isDirectory()) throw new Error(`The required Studio helper package ${packageName} is missing.`)
  cpSync(source, path.join(browserOutput, 'node_modules', packageName), {
    recursive: true,
    dereference: true,
    filter: entry => !entry.includes(`${path.sep}test-results${path.sep}`) &&
      !entry.includes(`${path.sep}playwright-report${path.sep}`),
  })
}
