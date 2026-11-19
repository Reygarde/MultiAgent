import { chromium } from 'playwright'
import { mkdir } from 'node:fs/promises'
import path from 'node:path'
import readline from 'node:readline'
import { fileURLToPath } from 'node:url'

const resultPrefix = 'AIPLATFORM_BROWSER_TEST_RESULT '
const maxScenarios = 3
const maxSteps = 10
const allowedActions = new Set(['fill', 'select', 'click', 'expectVisible', 'expectHidden', 'reload'])
const allowedLocatorTypes = new Set(['role', 'label', 'placeholder', 'text'])
const allowedRoles = new Set(['button', 'checkbox', 'heading', 'link', 'listitem', 'option', 'radio', 'textbox'])
const essentialResourceTypes = new Set(['document', 'script', 'stylesheet'])

function safeText(value, limit = 500) {
  return String(value ?? '')
    .replace(/[\u0000-\u001f\u007f]/g, ' ')
    .replace(/Bearer\s+\S+/gi, 'Bearer [redacted]')
    .replace(/[A-Za-z]:\\[^\s]+/g, '[local path]')
    .replace(/https?:\/\/[^\s]+/gi, '[URL]')
    .trim()
    .slice(0, limit)
}

function validScenario(scenario, origin) {
  if (!scenario || !/^[a-f0-9]{32}$/i.test(scenario.id) ||
      typeof scenario.name !== 'string' || scenario.name.length < 1 || scenario.name.length > 80 ||
      typeof scenario.path !== 'string' || !scenario.path.startsWith('/') ||
      scenario.path.startsWith('//') || scenario.path.includes('\\') ||
      !Array.isArray(scenario.steps) || scenario.steps.length > maxSteps) return false
  let destination
  try { destination = new URL(scenario.path, origin) } catch { return false }
  if (destination.origin !== origin || destination.protocol !== 'http:' ||
      destination.username || destination.password) return false
  return scenario.steps.every(step => {
    if (!step || !allowedActions.has(step.action)) return false
    if (step.action === 'reload') return true
    if (!allowedLocatorTypes.has(step.locatorType) ||
        typeof step.locator !== 'string' || step.locator.length < 1 ||
        step.locator.length > 120 || /[\u0000-\u001f]/.test(step.locator) ||
        step.index != null && (!Number.isInteger(step.index) || step.index < 0 || step.index > 4)) return false
    if (step.locatorType === 'role' && !allowedRoles.has(step.role)) return false
    if ((step.action === 'fill' || step.action === 'select') &&
        (typeof step.value !== 'string' || step.value.length < 1 || step.value.length > 200 ||
         /[\u0000-\u001f]/.test(step.value))) return false
    return true
  })
}

function findLocator(page, step, testValue) {
  const name = step.locator.replaceAll('{{testValue}}', testValue)
  let locator
  switch (step.locatorType) {
    case 'role': locator = page.getByRole(step.role, { name, exact: true }); break
    case 'label': locator = page.getByLabel(name, { exact: true }); break
    case 'placeholder': locator = page.getByPlaceholder(name, { exact: true }); break
    case 'text': locator = page.getByText(name, { exact: true }); break
    default: throw new Error('Unsupported accessible locator.')
  }
  return step.index == null ? locator : locator.nth(step.index)
}

async function applyStep(page, step, testValue) {
  if (step.action === 'reload') {
    const response = await page.reload({ waitUntil: 'domcontentloaded', timeout: 10000 })
    if (!response || response.status() >= 400) throw new Error('Reload returned an HTTP error.')
    return
  }
  const locator = findLocator(page, step, testValue)
  if (step.action === 'fill') {
    const value = step.value.replaceAll('{{testValue}}', testValue)
    await locator.fill(value, { timeout: 7000 })
  } else if (step.action === 'select') {
    const value = step.value.replaceAll('{{testValue}}', testValue)
    await locator.selectOption(value, { timeout: 7000 })
  } else if (step.action === 'click') {
    await locator.click({ timeout: 7000 })
  } else if (step.action === 'expectVisible') {
    await locator.waitFor({ state: 'visible', timeout: 7000 })
  } else {
    await locator.waitFor({ state: 'hidden', timeout: 7000 })
  }
}

export async function runBrowserTests({ url, runId, scenarios, screenshotDirectory }) {
  let address
  try { address = new URL(url) } catch {
    return { status: 'InvalidScenario', error: 'The verified application URL is invalid.', scenarios: [] }
  }
  if (address.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(address.hostname) ||
      address.username || address.password || address.search || address.hash ||
      !/^[a-f0-9]{32}$/i.test(runId) || !path.isAbsolute(screenshotDirectory) ||
      !Array.isArray(scenarios) || scenarios.length < 1 || scenarios.length > maxScenarios ||
      scenarios.some(scenario => !validScenario(scenario, address.origin))) {
    return { status: 'InvalidScenario', error: 'A browser scenario exceeded the allowed action or local-URL limits.', scenarios: [] }
  }

  let browser
  try {
    browser = await chromium.launch({ channel: 'msedge', headless: true })
  } catch {
    return {
      status: 'BrowserUnavailable',
      error: 'Chromium (Microsoft Edge) is unavailable. Install Microsoft Edge or Chromium explicitly, then retry.',
      scenarios: [],
    }
  }

  const results = []
  const testValue = `AIPlatform-${runId.slice(0, 8)}`
  const allowedWebSocket = new URL(address.origin)
  allowedWebSocket.protocol = 'ws:'
  try {
    await mkdir(screenshotDirectory, { recursive: true })
    for (const scenario of scenarios) {
      const context = await browser.newContext({ acceptDownloads: false, serviceWorkers: 'block' })
      const page = await context.newPage()
      const javaScriptErrors = []
      const failedResources = []
      let failedStep = null
      let error = null
      let screenshotId = null
      await context.route('**/*', async route => {
        const request = route.request()
        let target
        try { target = new URL(request.url()) } catch {
          await route.abort('blockedbyclient')
          return
        }
        if (target.origin !== address.origin || target.protocol !== 'http:' ||
            target.username || target.password || target.hash ||
            !['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'].includes(request.method())) {
          await route.abort('blockedbyclient')
          return
        }
        await route.continue()
      })
      await context.routeWebSocket('**/*', socket => {
        let target
        try { target = new URL(socket.url()) } catch {
          socket.close()
          return
        }
        if (target.protocol !== 'ws:' || target.origin !== allowedWebSocket.origin) {
          socket.close()
          return
        }
        socket.connectToServer()
      })
      context.on('page', popup => { if (popup !== page) void popup.close() })
      page.on('pageerror', exception => {
        if (javaScriptErrors.length < 5) javaScriptErrors.push(safeText(exception.message))
      })
      page.on('console', message => {
        if (message.type() === 'error' && javaScriptErrors.length < 5)
          javaScriptErrors.push(safeText(message.text()))
      })
      page.on('requestfailed', request => {
        if (request.url().startsWith(address.origin) &&
            essentialResourceTypes.has(request.resourceType()) && failedResources.length < 5)
          failedResources.push(safeText(`${request.resourceType()} failed to load.`))
      })
      page.on('response', response => {
        const request = response.request()
        if (response.status() >= 400 && request.url().startsWith(address.origin) &&
            essentialResourceTypes.has(request.resourceType()) && failedResources.length < 5)
          failedResources.push(safeText(`${request.resourceType()} returned HTTP ${response.status()}.`))
      })

      try {
        const destination = new URL(scenario.path, address.origin).href
        const response = await page.goto(destination, { waitUntil: 'domcontentloaded', timeout: 15000 })
        if (!response || response.status() >= 400)
          throw new Error(`Page load returned HTTP ${response?.status() ?? 'no response'}.`)
        const body = (await page.locator('body').innerText({ timeout: 5000 })).trim()
        if (!body) throw new Error('The page contains no visible text.')
        for (const [index, step] of scenario.steps.entries()) {
          failedStep = `${index + 1}. ${step.action}`
          await applyStep(page, step, testValue)
        }
        failedStep = null
        if (javaScriptErrors.length)
          throw new Error(`JavaScript console errors: ${javaScriptErrors.join(' | ')}`)
        if (failedResources.length)
          throw new Error(`Essential resources failed: ${failedResources.join(' | ')}`)
        results.push({
          id: scenario.id, name: safeText(scenario.name, 80), status: 'Passed',
          failedStep: null, error: null, javaScriptErrors, screenshotId: null,
        })
      } catch (failure) {
        error = safeText(failure?.message || 'The browser scenario failed.')
        screenshotId = scenario.id
        try {
          await page.screenshot({
            path: path.join(screenshotDirectory, `${screenshotId}.png`),
            fullPage: false, timeout: 5000,
          })
        } catch { screenshotId = null }
        results.push({
          id: scenario.id, name: safeText(scenario.name, 80), status: 'Failed',
          failedStep, error, javaScriptErrors, screenshotId,
        })
      } finally {
        await context.close()
      }
    }
    const passed = results.filter(result => result.status === 'Passed').length
    return { status: passed === results.length ? 'Passed' : 'Failed', scenarios: results }
  } finally {
    await browser.close()
  }
}

async function main() {
  const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity })
  let line
  for await (const candidate of input) {
    line = candidate
    break
  }
  let result
  try {
    if (!line || line.length > 20000) throw new Error('Browser test request is missing or oversized.')
    result = await runBrowserTests(JSON.parse(line))
  } catch {
    result = { status: 'Failed', error: 'The trusted browser test runner could not complete the request.', scenarios: [] }
  }
  process.stdout.write(resultPrefix + JSON.stringify(result) + '\n')
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main()
}
