import { spawn } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import { createServer } from 'node:net'
import { createInterface } from 'node:readline'
import { fileURLToPath } from 'node:url'
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

test('published Desktop backend: occupied port, local auth, CORS, services, shutdown/owner EOF and token rotation',
  { timeout: 100000 }, async context => {
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
      assert.equal((await (await call('/agents')).json()).length, 3)
      const models = await (await call('/models/local')).json()
      const sandbox = await (await call('/sandbox')).json()
      context.diagnostic(`Real services: Ollama=${models.available}; Docker engine=${sandbox.engineAvailable}; sandbox=${sandbox.available}`)
      await stop(instance, eof)
      context.diagnostic(`Owned backend stopped with code 0 using ${eof ? 'owner EOF' : 'shutdown command'}.`)
      previousToken = instance.token
    }
  })
