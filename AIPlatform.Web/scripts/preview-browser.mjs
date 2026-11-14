import { chromium } from '@playwright/test'
import http from 'node:http'
import { mkdir, rm, lstat } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import readline from 'node:readline'

const requestLimit = 16384
const responseLimit = 1048576

export async function openPreviewBrowser({ origin, token, profile, expiresAt, headless = false, onBlocked = () => {} }) {
  const url = new URL(origin)
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port ||
      url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
      !/^[A-F0-9]{64}$/.test(token) || !path.isAbsolute(profile) ||
      !/^[a-f0-9]{32}$/.test(path.basename(profile)) ||
      !Number.isFinite(expiresAt) || expiresAt <= Date.now() || expiresAt - Date.now() > 1800000) {
    throw new Error('Invalid controlled browser session.')
  }
  origin = url.origin
  const parent = await lstat(path.dirname(profile))
  if (!parent.isDirectory() || parent.isSymbolicLink()) throw new Error('Unsafe browser profile root.')
  await mkdir(profile)
  let context
  let page
  let closing
  let timer
  let stopped = false
  const pending = new Set()
  let queued = 0
  let relayTail = Promise.resolve()
  const denyProxy = http.createServer((_request, response) => {
    response.writeHead(403, { Connection: 'close' })
    response.end()
  })
  denyProxy.on('connect', (_request, socket) => socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n'))
  const sockets = new Set()
  denyProxy.on('connection', socket => {
    sockets.add(socket)
    socket.on('error', error => {
      onBlocked('proxy-socket')
      console.error(`Preview deny proxy connection error: ${error.name}`)
      socket.destroy()
    })
    socket.on('close', () => sockets.delete(socket))
  })
  async function close() {
    if (closing) return closing
    stopped = true
    closing = (async () => {
      clearTimeout(timer)
      for (const request of pending) request.abort()
      if (context) await context.close()
      for (const socket of sockets) socket.destroy()
      if (denyProxy.listening) await new Promise((resolve, reject) => denyProxy.close(error => error ? reject(error) : resolve()))
      await rm(profile, { recursive: true, force: false })
    })()
    return closing
  }
  try {
    await new Promise((resolve, reject) => {
      denyProxy.once('error', reject)
      denyProxy.listen(0, '127.0.0.1', resolve)
    })
    context = await chromium.launchPersistentContext(profile, {
      channel: 'msedge', headless, chromiumSandbox: true, acceptDownloads: false, serviceWorkers: 'block',
      proxy: { server: `http://127.0.0.1:${denyProxy.address().port}`, bypass: '<-loopback>' },
      args: [
        '--enable-automation', '--disable-extensions', '--disable-quic', '--disable-background-networking',
        '--disable-sync', '--no-first-run', '--no-default-browser-check',
        '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1',
        '--force-webrtc-ip-handling-policy=disable_non_proxied_udp',
      ],
    })
    await context.clearPermissions()
    const originalPages = context.pages()
    page = originalPages[0] ?? await context.newPage()
    for (const extra of originalPages.slice(1)) await extra.close()
    context.on('page', extra => {
      if (extra !== page) extra.close().catch(error => {
        if (!stopped) console.error(`Preview popup close failed: ${error.name}`)
      })
    })
    const cdp = await context.newCDPSession(page)
    await cdp.send('Browser.setDownloadBehavior', { behavior: 'deny' })
    await context.routeWebSocket('**/*', socket => {
      onBlocked('websocket')
      socket.close()
    })
    await context.route('**/*', async route => {
      const request = route.request()
      let target
      try { target = new URL(request.url()) } catch {
        onBlocked('url')
        await route.abort('blockedbyclient')
        return
      }
      const resource = request.resourceType()
      if (stopped || Date.now() >= expiresAt || target.origin !== origin || target.protocol !== 'http:' ||
          target.username || target.password || target.hash ||
          !['GET', 'POST', 'PUT', 'DELETE'].includes(request.method()) ||
          ['worker', 'serviceworker'].includes(resource) ||
          request.frame().page() !== page ||
          request.isNavigationRequest() && request.frame() !== page.mainFrame()) {
        onBlocked(resource)
        await route.abort('blockedbyclient')
        return
      }
      const body = request.postDataBuffer()
      if (body && body.length > requestLimit || queued >= 8) {
        onBlocked('limit')
        await route.abort('blockedbyclient')
        return
      }
      queued++
      const previous = relayTail
      let release
      relayTail = new Promise(resolve => { release = resolve })
      await previous
      const controller = new AbortController()
      pending.add(controller)
      const timeout = setTimeout(() => controller.abort(), 10000)
      try {
        if (stopped || Date.now() >= expiresAt) throw new Error('Session closed.')
        // Only the helper knows the proxy token. The renderer never sends it.
        const response = await fetch(`${origin}${target.pathname}${target.search}`, {
          method: request.method(), redirect: 'manual', signal: controller.signal,
          headers: { 'X-Preview-Token': token, Origin: origin,
            ...(['POST', 'PUT'].includes(request.method()) ? { 'Content-Type': 'application/json' } : {}) },
          body: ['POST', 'PUT'].includes(request.method()) ? body : undefined,
        })
        if (response.status >= 300 && response.status < 400) throw new Error('Redirect refused.')
        const chunks = []
        let size = 0
        if (response.body) for await (const chunk of response.body) {
          size += chunk.length
          if (size > responseLimit) throw new Error('Response too large.')
          chunks.push(chunk)
        }
        const headers = {}
        for (const name of ['content-type', 'content-security-policy', 'permissions-policy', 'x-content-type-options']) {
          const value = response.headers.get(name)
          if (value) headers[name] = value
        }
        headers['cache-control'] = 'no-store'
        headers['referrer-policy'] = 'no-referrer'
        await route.fulfill({ status: response.status, headers, body: Buffer.concat(chunks) })
      } catch (error) {
        onBlocked('relay')
        console.error(`Preview browser relay failed: ${error.name}`)
        if (!stopped) await route.abort('blockedbyclient')
      } finally {
        clearTimeout(timeout)
        pending.delete(controller)
        queued--
        release()
      }
    })
    timer = setTimeout(() => close().catch(error => console.error(`Preview close failed: ${error.name}`)),
      Math.max(1, expiresAt - Date.now()))
    await page.goto(origin, { timeout: 15000, waitUntil: 'domcontentloaded' })
    return { context, page, close, profile }
  } catch (error) {
    await close()
    throw error
  }
}

async function main() {
  const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity })
  let browser
  let started = false
  let operation = Promise.resolve()
  const close = async () => {
    if (browser) await browser.close()
  }
  input.on('line', line => {
    operation = operation.then(async () => {
      if (line === 'shutdown') {
        await close()
        input.close()
        return
      }
      if (started || line.length > 4096) throw new Error('Invalid browser control message.')
      started = true
      const settings = JSON.parse(line)
      browser = await openPreviewBrowser({ ...settings, headless: false })
      browser.page.on('close', () => {
        operation = operation.then(close).then(() => input.close())
          .catch(error => { console.error(`Preview browser cleanup failed: ${error.name}`); process.exitCode = 1; input.close() })
      })
      console.log('AIPLATFORM_PREVIEW_BROWSER_READY')
    }).catch(async error => {
      console.error(`Preview browser failed: ${error.name}`)
      process.exitCode = 1
      await close()
      input.close()
    })
  })
  input.on('close', () => {
    operation = operation.then(close).catch(error => {
      console.error(`Preview browser cleanup failed: ${error.name}`)
      process.exitCode = 1
    })
  })
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main()
}
