import { spawn } from 'node:child_process'
import { createHash, randomBytes } from 'node:crypto'
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { createServer } from 'node:net'
import { createInterface } from 'node:readline'
import { fileURLToPath, pathToFileURL } from 'node:url'
import path from 'node:path'
import assert from 'node:assert/strict'
import { test } from 'node:test'

const executable = fileURLToPath(new URL('../src-tauri/binaries/aiplatform-api-x86_64-pc-windows-msvc.exe', import.meta.url))
const cwd = fileURLToPath(new URL('../src-tauri/resources/backend/', import.meta.url))

function start() {
  const token = randomBytes(32).toString('hex')
  const child = spawn(executable, [], {
    cwd, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
    env: { ...process.env, DOTNET_ENVIRONMENT: 'Desktop', ASPNETCORE_ENVIRONMENT: 'Desktop',
      AIPLATFORM_DESKTOP_TOKEN: token, ASPNETCORE_URLS: 'http://0.0.0.0:5167',
      Kestrel__Endpoints__Untrusted__Url: 'http://0.0.0.0:5167' },
  })
  let diagnostics = ''
  child.stderr.on('data', chunk => { diagnostics = (diagnostics + chunk).slice(-6000) })
  const exited = new Promise((resolve, reject) => {
    child.once('error', reject)
    child.once('exit', (code, signal) => resolve({ code, signal }))
  })
  const ready = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`Backend startup timeout. ${diagnostics}`)), 45000)
    const lines = createInterface({ input: child.stdout })
    lines.on('line', line => {
      if (line.startsWith('AIPLATFORM_READY ')) {
        clearTimeout(timer)
        try { resolve(JSON.parse(line.slice('AIPLATFORM_READY '.length))) }
        catch (error) { reject(error) }
      } else diagnostics = (diagnostics + '\n' + line).slice(-6000)
    })
    exited.then(() => {
      clearTimeout(timer)
      reject(new Error(`Backend exited before readiness. ${diagnostics}`))
    }, reject)
  })
  return { child, token, ready, exited }
}

async function stop(instance, eof = false) {
  if (instance.child.exitCode !== null) return
  instance.child.stdin.end(eof ? undefined : 'shutdown\n')
  let timer
  try {
    const result = await Promise.race([
      instance.exited,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Backend graceful shutdown timed out.')), 22000) }),
    ])
    assert.equal(result.code, 0)
  } finally { clearTimeout(timer) }
}

test('published Desktop backend: occupied port, local auth, CORS, history integrity, shutdown/owner EOF and token rotation',
  { timeout: 100000 }, async context => {
    const historyDirectory = path.join(process.env.LOCALAPPDATA, 'AIPlatformStudio', 'StudioData', 'missions')
    const history = () => Object.fromEntries((existsSync(historyDirectory) ? readdirSync(historyDirectory) : [])
      .filter(name => name.endsWith('.json')).sort().map(name => {
        const bytes = readFileSync(path.join(historyDirectory, name))
        const mission = JSON.parse(bytes.toString('utf8'))
        assert.ok(!['Queued', 'Running'].includes(mission.status) &&
          !['Queued', 'Running'].includes(mission.workflow?.reviewerCorrectionResume?.status),
        'Close active missions before testing the packaged Desktop backend; restart recovery must not change history.')
        return [name, createHash('sha256').update(bytes).digest('hex')]
      }))
    const before = history()
    const occupied = createServer()
    let ownsPort = false
    await new Promise((resolve, reject) => {
      occupied.once('error', error => error.code === 'EADDRINUSE' ? resolve() : reject(error))
      occupied.listen(5167, '127.0.0.1', () => { ownsPort = true; resolve() })
    })
    context.after(() => { if (ownsPort) occupied.close() })
    let previousToken
    for (const eof of [false, true]) {
      const instance = start()
      context.after(async () => {
        if (instance.child.exitCode === null) {
          instance.child.stdin.end()
          await stop(instance, true).catch(error => {
            instance.child.kill()
            throw error
          })
        }
      })
      const ready = await instance.ready
      assert.match(ready.url, /^http:\/\/127\.0\.0\.1:\d+$/)
      assert.notEqual(new URL(ready.url).port, '5167')
      assert.equal(ready.processId, instance.child.pid)
      const call = (path, options = {}) => fetch(`${ready.url}/api${path}`, {
        ...options, headers: { Authorization: `Bearer ${instance.token}`, ...options.headers },
        signal: AbortSignal.timeout(15000),
      })
      assert.equal((await call('/health', { headers: { Authorization: '' } })).status, 401)
      assert.equal((await call('/health', { headers: { Authorization: 'Bearer wrong' } })).status, 401)
      if (previousToken) assert.equal((await call('/health', { headers: { Authorization: `Bearer ${previousToken}` } })).status, 401)
      assert.equal((await call('/health', { headers: { Origin: 'https://evil.example' } })).status, 403)
      const preflight = await call('/missions', { method: 'OPTIONS', headers: {
        Authorization: '', Origin: 'http://tauri.localhost',
        'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'authorization,content-type',
      } })
      assert.equal(preflight.status, 204)
      assert.equal(preflight.headers.get('Access-Control-Allow-Origin'), 'http://tauri.localhost')
      const health = await (await call('/health', { headers: { Origin: 'http://tauri.localhost' } })).json()
      assert.deepEqual(health, { ready: true, processId: instance.child.pid })
      const agents = await (await call('/agents')).json()
      assert.ok(agents.some(agent => agent.id === 'GeneralAgent'))
      assert.ok(agents.some(agent => agent.role === 'Planner'))
      assert.ok(agents.some(agent => agent.role === 'Review'))
      const missions = await (await call('/missions')).json()
      assert.ok(Array.isArray(missions))
      assert.ok(missions.every(mission => Object.hasOwn(before, mission.id.replaceAll('-', '') + '.json')))
      await stop(instance, eof)
      assert.deepEqual(history(), before)
      context.diagnostic(`Owned backend stopped with code 0 using ${eof ? 'owner EOF' : 'shutdown command'}.`)
      context.diagnostic('Historical missions unchanged. No Ollama or Docker service was contacted.')
      previousToken = instance.token
    }
  })

test('packaged React helpers resolve their bundled TypeScript without executing application sources', async () => {
  const helperDirectory = path.resolve(cwd, '..', 'studio-browser')
  const { checkReactSources } = await import(pathToFileURL(path.join(helperDirectory, 'react-source-check.mjs')))
  const { repairReactSources } = await import(pathToFileURL(path.join(helperDirectory, 'react-source-repair.mjs')))
  const files = [
    { path: 'package.json', content: '{"dependencies":{},"devDependencies":{"vitest":"4.1.12"}}' },
    { path: 'src/App.test.ts', content: 'const save = jest.fn();' },
  ]
  assert.ok(checkReactSources(files).some(issue => issue.code === 'REACT_JEST'))
  const repaired = repairReactSources({ files, allowedPaths: ['src/App.test.ts'], trustedSetup: '' })
  assert.ok(repaired.edits.length > 0)
  assert.ok(!repaired.issues.some(issue => issue.code === 'REACT_JEST'))
  assert.equal(files[1].content, 'const save = jest.fn();')
})
