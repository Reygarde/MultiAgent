import fs from 'node:fs'
import http from 'node:http'
import net from 'node:net'
import dgram from 'node:dgram'
import dns from 'node:dns'
import { spawn } from 'node:child_process'

const [configFile, role, generation = 'parent'] = process.argv.slice(2)
const config = JSON.parse(fs.readFileSync(configFile, 'utf8'))
const label = `${role}-${generation}`
const attempts = []
function tcp(name, host, port, upgrade = false) {
  return new Promise(resolve => {
    const socket = net.connect({ host, port })
    const done = result => { attempts.push({ name, result }); socket.destroy(); resolve() }
    socket.setTimeout(800, () => done('timeout'))
    socket.on('error', error => done(error.code))
    socket.on('connect', () => {
      socket.write(`GET /${label}/${name} HTTP/1.1\r\nHost: ${host}:${port}\r\n${
        upgrade ? 'Connection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGVzdHRlc3R0ZXN0dGVzdA==\r\nSec-WebSocket-Version: 13\r\n' : ''}\r\n`)
      setTimeout(() => done('connected'), 100)
    })
  })
}
function udp(name, port, payload) {
  return new Promise(resolve => {
    const socket = dgram.createSocket('udp4')
    socket.on('error', error => { attempts.push({ name, result: error.code }); socket.close(); resolve() })
    socket.send(payload, port, '127.0.0.1', error => {
      attempts.push({ name, result: error?.code ?? 'send-completed-not-proof-of-delivery' })
      socket.close()
      resolve()
    })
  })
}
function lookup() {
  return new Promise(resolve => {
    const resolver = new dns.Resolver({ timeout: 500, tries: 1 })
    resolver.setServers([`127.0.0.1:${config.dns}`])
    resolver.resolve4(`${label}.aiplatform.invalid`, error => {
      attempts.push({ name: 'dns', result: error?.code ?? 'resolved' })
      resolve()
    })
  })
}
function redirect() {
  return new Promise(resolve => {
    const request = http.get(`http://127.0.0.1:${config.http}/${label}/redirect`, response => {
      response.resume()
      const follow = http.get(response.headers.location, followed => {
        followed.resume()
        attempts.push({ name: 'redirect', result: 'followed' })
        resolve()
      })
      follow.setTimeout(800, () => follow.destroy())
      follow.on('error', error => { attempts.push({ name: 'redirect', result: error.code }); resolve() })
    })
    request.setTimeout(800, () => request.destroy())
    request.on('error', error => { attempts.push({ name: 'redirect', result: error.code }); resolve() })
  })
}
let child
if (generation === 'parent') {
  try {
    child = spawn(process.execPath, [...process.execArgv, process.argv[1], configFile, role, 'child'],
      { stdio: 'ignore', windowsHide: true })
  } catch (error) {
    if (!['EPERM', 'EACCES'].includes(error.code)) throw error
    attempts.push({ name: 'child-start', result: error.code })
    process.exitCode = 2
  }
}
if (child) child.on('error', error => {
  attempts.push({ name: 'child-start', result: error.code })
  process.exitCode = 2
})
await Promise.all([
  tcp('http', '127.0.0.1', config.http),
  tcp('localhost', 'localhost', config.http),
  tcp('other-port', '127.0.0.1', config.other),
  tcp('ipv6', '::1', config.ipv6),
  tcp('websocket', '127.0.0.1', config.http, true),
  udp('stun', config.stun, Buffer.from(label)),
  lookup(), redirect(),
])
// Keep both generations alive long enough for the trusted controller to inspect their tokens.
await new Promise(resolve => setTimeout(resolve, 1500))
fs.writeFileSync(`${config.directory}\\${label}.json`, JSON.stringify({ pid: process.pid, attempts }))
if (child) await new Promise(resolve => child.exitCode !== null ? resolve() : child.once('exit', resolve))
