import test from 'node:test'
import assert from 'node:assert/strict'
import http from 'node:http'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { runBrowserTests } from './studio-browser-tests.mjs'

async function startServer(handler) {
  const server = http.createServer(handler)
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolve)
  })
  return server
}

async function stopServer(server) {
  server.closeAllConnections()
  await new Promise((resolve, reject) =>
    server.close(error => error ? reject(error) : resolve()))
}

function scenario(steps) {
  return {
    id: '0123456789abcdef0123456789abcdef',
    name: 'Task list workflow',
    path: '/',
    steps,
  }
}

async function runWithEdge(t, request) {
  const report = await runBrowserTests(request)
  if (report.status === 'BrowserUnavailable')
    t.skip(report.error)
  return report
}

test('Edge can add, persist, and delete an accessible task', { timeout: 45000 }, async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'AIPlatform-studio-browser-'))
  const page = `<!doctype html><html><body>
    <main><h1>Tasks</h1><label for="task">Task title</label>
    <input id="task" placeholder="Add a task"><button id="add">Add task</button><ul id="tasks"></ul></main>
    <script>
      const input = document.querySelector('#task')
      const list = document.querySelector('#tasks')
      const render = () => {
        list.replaceChildren(...JSON.parse(localStorage.getItem('tasks') || '[]').map(task => {
          const item = document.createElement('li')
          item.append(document.createTextNode(task))
          const remove = document.createElement('button')
          remove.textContent = 'Delete ' + task
          remove.setAttribute('aria-label', 'Delete ' + task)
          remove.onclick = () => {
            localStorage.setItem('tasks', JSON.stringify(
              JSON.parse(localStorage.getItem('tasks') || '[]').filter(value => value !== task)))
            render()
          }
          item.append(remove)
          return item
        }))
      }
      document.querySelector('#add').onclick = () => {
        localStorage.setItem('tasks', JSON.stringify([
          ...JSON.parse(localStorage.getItem('tasks') || '[]'), input.value]))
        input.value = ''
        render()
      }
      render()
    </script>
  </body></html>`
  const server = await startServer((_request, response) => {
    response.writeHead(200, { 'Content-Type': 'text/html' })
    response.end(page)
  })
  try {
    const report = await runWithEdge(t, {
      url: `http://127.0.0.1:${server.address().port}/`,
      runId: 'abcdef0123456789abcdef0123456789',
      scenarios: [scenario([
        { action: 'fill', locatorType: 'placeholder', locator: 'Add a task', value: '{{testValue}}' },
        { action: 'click', locatorType: 'role', role: 'button', locator: 'Add task' },
        { action: 'expectVisible', locatorType: 'role', role: 'button', locator: 'Delete {{testValue}}' },
        { action: 'reload' },
        { action: 'expectVisible', locatorType: 'role', role: 'button', locator: 'Delete {{testValue}}' },
        { action: 'click', locatorType: 'role', role: 'button', locator: 'Delete {{testValue}}' },
        { action: 'expectHidden', locatorType: 'role', role: 'button', locator: 'Delete {{testValue}}' },
      ])],
      screenshotDirectory: root,
    })
    assert.equal(report.status, 'Passed', JSON.stringify(report))
    assert.equal(report.scenarios[0].status, 'Passed')
    assert.deepEqual(report.scenarios[0].javaScriptErrors, [])
  } finally {
    await stopServer(server)
    await rm(root, { recursive: true })
  }
})

test('browser runner captures JavaScript failures and rejects external scenario paths', { timeout: 45000 }, async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'AIPlatform-studio-browser-errors-'))
  const server = await startServer((_request, response) => {
    response.writeHead(200, { 'Content-Type': 'text/html' })
    response.end('<!doctype html><html><body><main>Diagnostic test</main><script>console.error("intentional test error")</script></body></html>')
  })

  try {
    const url = `http://127.0.0.1:${server.address().port}/`
    const runId = '1234567890abcdef1234567890abcdef'
    const failed = await runWithEdge(t, {
      url, runId, scenarios: [scenario([])], screenshotDirectory: root,
    })
    assert.equal(failed.status, 'Failed')
    assert.match(failed.scenarios[0].javaScriptErrors.join(' '), /intentional test error/)
    assert.equal(failed.scenarios[0].screenshotId, scenario([]).id)
    const screenshot = await readFile(path.join(root, `${scenario([]).id}.png`))
    assert.deepEqual([...screenshot.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10])

    const rejected = await runBrowserTests({
      url, runId, scenarios: [{ ...scenario([]), path: '//example.com/' }],
      screenshotDirectory: root,
    })
    assert.equal(rejected.status, 'InvalidScenario')
    assert.deepEqual(rejected.scenarios, [])
  } finally {
    await stopServer(server)
    await rm(root, { recursive: true })
  }
})

test('browser runner selects a native filter by option value', { timeout: 45000 }, async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'AIPlatform-studio-browser-select-'))
  const server = await startServer((_request, response) => {
    response.writeHead(200, { 'Content-Type': 'text/html' })
    response.end(`<!doctype html><html><body>
      <label for="filter">Filter</label><select id="filter">
        <option value="all">All</option><option value="completed">Completed</option>
      </select><p id="result">All tasks</p>
      <script>document.getElementById('filter').onchange = event => {
        document.getElementById('result').textContent = event.target.value === 'completed' ? 'Done tasks' : 'All tasks'
      }</script></body></html>`)
  })
  try {
    const report = await runWithEdge(t, {
      url: `http://127.0.0.1:${server.address().port}/`,
      runId: 'abcdef0123456789abcdef0123456789',
      scenarios: [scenario([
        { action: 'select', locatorType: 'label', locator: 'Filter', value: 'completed' },
        { action: 'expectVisible', locatorType: 'text', locator: 'Done tasks' },
        { action: 'expectHidden', locatorType: 'text', locator: 'All tasks' },
      ])],
      screenshotDirectory: root,
    })
    assert.equal(report.status, 'Passed', JSON.stringify(report))
  } finally {
    await stopServer(server)
    await rm(root, { recursive: true })
  }
})
