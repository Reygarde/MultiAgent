import test from 'node:test'
import assert from 'node:assert/strict'
import http from 'node:http'
import dgram from 'node:dgram'
import { mkdtemp, rm, access, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { randomBytes } from 'node:crypto'
import { pathToFileURL } from 'node:url'
import { openPreviewBrowser } from './preview-browser.mjs'

async function server(handler, address = '127.0.0.1') {
  const value = http.createServer(handler)
  await new Promise((resolve, reject) => {
    value.once('error', reject)
    value.listen(0, address, resolve)
  })
  return value
}

async function closeServer(value) {
  value.closeAllConnections()
  await new Promise((resolve, reject) => value.close(error => error ? reject(error) : resolve()))
}

test('dedicated Edge denies bypass attempts observed by fake services and removes its profile', { timeout: 45000 }, async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'AIPlatform-browser-security-'))
  const profile = path.join(root, randomBytes(16).toString('hex'))
  const token = randomBytes(32).toString('hex').toUpperCase()
  const victimHits = []
  const loopback2Hits = []
  const ipv6Hits = []
  const approvedHits = []
  const blocked = []
  const victim = await server((request, response) => { victimHits.push(request.url); response.end('victim') })
  const alternative = await server((request, response) => { loopback2Hits.push(request.url); response.end('alternative') }, '127.0.0.2')
  const ipv6 = await server((request, response) => { ipv6Hits.push(request.url); response.end('ipv6') }, '::1')
  const udp = dgram.createSocket('udp4')
  let udpHits = 0
  udp.on('message', () => udpHits++)
  await new Promise(resolve => udp.bind(0, '127.0.0.1', resolve))
  const sentinel = path.join(root, 'sentinel.txt')
  await writeFile(sentinel, 'OWNED_TEST_SENTINEL')
  let origin
  const approved = await server((request, response) => {
    approvedHits.push({ url: request.url, headers: request.headers })
    assert.equal(request.headers['x-preview-token'], token)
    assert.equal(request.headers.cookie, undefined)
    assert.equal(request.headers.authorization, undefined)
    if (request.url === '/redirect') {
      response.writeHead(302, { Location: `http://127.0.0.1:${victim.address().port}/redirect-target` })
      response.end()
      return
    }
    if (request.url === '/download') {
      response.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Disposition': 'attachment; filename=forbidden.txt' })
      response.end('download')
      return
    }
    if (request.url === '/large') {
      response.writeHead(200, { 'Content-Type': 'text/plain' })
      response.end(Buffer.alloc(1048577))
      return
    }
    if (request.url === '/slow') {
      setTimeout(() => response.end('too late'), 11000)
      return
    }
    response.writeHead(200, { 'Content-Type': 'text/html',
      'Set-Cookie': 'StudioCookie=must-not-enter-renderer' })
    response.end('<!doctype html><html><body>controlled test page</body></html>')
  })
  origin = `http://127.0.0.1:${approved.address().port}`
  let browser
  try {
    browser = await openPreviewBrowser({ origin, token, profile, expiresAt: Date.now() + 40000,
      headless: true, onBlocked: kind => blocked.push(kind) })
    const cdp = await browser.context.newCDPSession(browser.page)
    const commandLine = await cdp.send('Browser.getBrowserCommandLine')
    assert.ok(!commandLine.arguments.includes('--no-sandbox'), 'Playwright must not disable the browser sandbox')
    assert.ok(!commandLine.arguments.some(argument => argument.includes(token)), 'Token must not appear in browser arguments')
    const targets = [
      `http://127.0.0.1:${victim.address().port}/api/missions`,
      `http://localhost:${victim.address().port}/local`,
      `http://127.0.0.2:${alternative.address().port}/alternative`,
      `http://[::1]:${ipv6.address().port}/ipv6`,
      'http://192.168.1.123/private', 'http://10.0.0.1/private',
      'http://169.254.169.254/latest/meta-data/', 'https://example.invalid/external',
    ]
    await browser.page.evaluate(({ targets, stunPort }) => {
      for (const url of targets) {
        fetch(url, { mode: 'no-cors' }).catch(() => {})
        const xhr = new XMLHttpRequest()
        xhr.open('GET', url)
        xhr.send()
        const image = new Image()
        image.src = url
        document.body.append(image)
      }
      const frame = document.createElement('iframe')
      frame.src = targets[0]
      document.body.append(frame)
      try { new Worker(targets[0]) } catch {}
      try { new Worker(URL.createObjectURL(new Blob([`fetch(${JSON.stringify(targets[0])}).catch(()=>{})`],
        { type: 'text/javascript' }))) } catch {}
      try { new WebSocket(targets[0].replace('http:', 'ws:')) } catch {}
      window.open(targets[0], '_blank')
      const form = document.createElement('form')
      form.method = 'POST'; form.action = targets[0]; form.target = 'hidden'
      document.body.append(form); form.submit()
      const style = document.createElement('style')
      style.textContent = `body { background-image: url("${targets[0]}") }`
      document.head.append(style)
      const link = document.createElement('link')
      link.rel = 'stylesheet'; link.href = targets[0]; document.head.append(link)
      try {
        const peer = new RTCPeerConnection({ iceServers: [{ urls: `stun:127.0.0.1:${stunPort}` }] })
        peer.createDataChannel('test')
        peer.createOffer().then(offer => peer.setLocalDescription(offer)).catch(() => {})
        window.testPeer = peer
      } catch {}
    }, { targets, stunPort: udp.address().port })
    await browser.page.waitForTimeout(1800)
    assert.ok(blocked.length > 0)
    assert.deepEqual(victimHits, [])
    assert.deepEqual(loopback2Hits, [])
    assert.deepEqual(ipv6Hits, [])
    const state = await browser.page.evaluate(() => ({
      cookie: document.cookie, storage: Object.keys(localStorage),
      tauri: typeof window.__TAURI_INTERNALS__, node: typeof window.require,
    }))
    assert.equal(state.cookie, '')
    assert.deepEqual(state.storage, [])
    assert.equal(state.tauri, 'undefined')
    assert.equal(state.node, 'undefined')
    const relayResults = await browser.page.evaluate(async () => {
      const concurrent = await Promise.all(['one', 'two', 'three'].map(name => fetch(`/assets/${name}.js`)))
      const oversized = await fetch('/api/tasks', { method: 'POST', body: 'x'.repeat(16385) })
        .then(() => 'allowed', () => 'blocked')
      const largeResponse = await fetch('/large').then(() => 'allowed', () => 'blocked')
      const slow = await fetch('/slow').then(() => 'allowed', () => 'blocked')
      await fetch('/api/tasks', { headers: { Authorization: 'Bearer StudioMock' } })
      return { concurrent: concurrent.map(response => response.status), oversized, largeResponse, slow }
    })
    assert.deepEqual(relayResults.concurrent, [200, 200, 200])
    assert.equal(relayResults.oversized, 'blocked')
    assert.equal(relayResults.largeResponse, 'blocked')
    assert.equal(relayResults.slow, 'blocked')
    await browser.page.goto(`${origin}/redirect`).catch(() => {})
    await browser.page.goto(`http://127.0.0.1:${victim.address().port}/direct`).catch(() => {})
    await assert.rejects(browser.page.goto(pathToFileURL(sentinel).href))
    await browser.page.goto('aiplatform-preview-test://forbidden').catch(() => {})
    await browser.page.goto(`${origin}/download`).catch(() => {})
    await assert.rejects(access(path.join(profile, 'forbidden.txt')))
    assert.deepEqual(victimHits, [])
    assert.deepEqual(loopback2Hits, [])
    assert.deepEqual(ipv6Hits, [])
    assert.ok(approvedHits.length >= 2)
    await browser.close()
    await assert.rejects(access(profile))
    assert.equal(udpHits, 0, 'WebRTC must not send packets to the fake STUN endpoint')
  } finally {
    if (browser) await browser.close()
    udp.close()
    await Promise.all([victim, alternative, ipv6, approved].map(closeServer))
    await rm(root, { recursive: true, force: false })
  }
})

test('two dedicated profiles cannot access each other or reuse session tokens', { timeout: 30000 }, async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'AIPlatform-browser-sessions-'))
  const firstHits = []
  const secondHits = []
  const firstToken = 'A'.repeat(64)
  const secondToken = 'B'.repeat(64)
  const first = await server((request, response) => {
    firstHits.push(request.headers['x-preview-token'])
    response.writeHead(200, { 'Content-Type': 'text/html' }); response.end('<html>first</html>')
  })
  const second = await server((request, response) => {
    secondHits.push(request.headers['x-preview-token'])
    response.writeHead(200, { 'Content-Type': 'text/html' }); response.end('<html>second</html>')
  })
  let a
  let b
  try {
    const aOrigin = `http://127.0.0.1:${first.address().port}`
    const bOrigin = `http://127.0.0.1:${second.address().port}`
    const settings = { headless: true, expiresAt: Date.now() + 20000 }
    a = await openPreviewBrowser({ ...settings, origin: aOrigin, token: firstToken,
      profile: path.join(root, randomBytes(16).toString('hex')) })
    b = await openPreviewBrowser({ ...settings, origin: bOrigin, token: secondToken,
      profile: path.join(root, randomBytes(16).toString('hex')) })
    assert.equal(await a.page.evaluate(url => fetch(url).then(() => 'allowed', () => 'blocked'), bOrigin), 'blocked')
    assert.equal(await b.page.evaluate(url => fetch(url).then(() => 'allowed', () => 'blocked'), aOrigin), 'blocked')
    assert.deepEqual(firstHits, [firstToken])
    assert.deepEqual(secondHits, [secondToken])
    await a.close(); await b.close()
    await assert.rejects(access(a.profile)); await assert.rejects(access(b.profile))
  } finally {
    if (a) await a.close()
    if (b) await b.close()
    await Promise.all([first, second].map(closeServer))
    await rm(root, { recursive: true, force: false })
  }
})

test('expiry closes the dedicated browser and invalid startup leaves no profile', { timeout: 30000 }, async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'AIPlatform-browser-lifecycle-'))
  const profile = path.join(root, randomBytes(16).toString('hex'))
  const approved = await server((_request, response) => {
    response.writeHead(200, { 'Content-Type': 'text/html' })
    response.end('<html>expiry</html>')
  })
  let browser
  try {
    const options = { origin: `http://127.0.0.1:${approved.address().port}`,
      token: 'A'.repeat(64), profile, expiresAt: Date.now() + 5000, headless: true }
    browser = await openPreviewBrowser(options)
    await new Promise(resolve => browser.context.once('close', resolve))
    await browser.close()
    await assert.rejects(access(profile))
    const invalid = path.join(root, randomBytes(16).toString('hex'))
    await assert.rejects(openPreviewBrowser({ ...options, profile: invalid,
      origin: 'file:///not-allowed', expiresAt: Date.now() + 10000 }))
    await assert.rejects(access(invalid))
  } finally {
    if (browser) await browser.close()
    await closeServer(approved)
    await rm(root, { recursive: true, force: false })
  }
})
