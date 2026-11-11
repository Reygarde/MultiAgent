import { expect, test } from '@playwright/test'
import { agents, mission, models } from '../src/test/fixtures'

test('navigates Studio, sends a mission and explores the generated files', async ({ page }) => {
  let submitted = false
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url())
    const path = url.pathname
    let json: unknown
    if (path === '/api/agents') json = agents
    else if (path === '/api/models') json = models
    else if (path === '/api/models/local') json = { available: true, error: null, models: [{ name: 'custom:4b', installed: true, loaded: false, inUse: false }] }
    else if (path === '/api/sandbox') json = { available: false, reason: 'Docker absent.' }
    else if (path === '/api/missions' && route.request().method() === 'POST') {
      expect(route.request().postDataJSON()).toMatchObject({ kind: 'Project', approveSandboxExecution: false })
      submitted = true; json = mission
    } else if (path === '/api/missions') json = submitted ? [mission] : []
    else if (path.endsWith('/files')) json = [{ path: 'src/App/Program.cs', size: 22 }]
    else if (path.endsWith('/file')) json = { path: 'src/App/Program.cs', content: 'Console.WriteLine("Studio");' }
    else throw new Error(`Unexpected request ${path}`)
    await route.fulfill({ json })
  })

  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Construisons la suite.' })).toBeVisible()
  await page.getByRole('button', { name: 'Créer un projet .NET' }).click()
  await expect(page.getByRole('checkbox')).toHaveCount(0)
  await expect(page.getByText(/Après génération, consultez les fichiers/)).toBeVisible()
  await page.getByRole('button', { name: 'Envoyer la mission' }).click()
  await expect(page.getByText('Fichiers disponibles. Compilation et tests NON exécutés.')).toBeVisible()
  await page.getByRole('button', { name: 'Agents', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'CustomCoder' })).toBeVisible()
  await page.getByRole('button', { name: 'Models', exact: true }).click()
  await expect(page.getByText('Installé : oui')).toBeVisible()
  await expect(page.getByText('Chargé : non')).toBeVisible()
  await page.getByRole('button', { name: 'Projects', exact: true }).click()
  await expect(page.getByText('Console.WriteLine("Studio");')).toBeVisible()
  await page.getByRole('button', { name: 'Missions', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Toutes les missions' })).toBeVisible()
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)
  expect(overflow).toBe(false)
})

for (const approved of [true, false]) {
  test(`reviews an execution plan and ${approved ? 'authorizes' : 'refuses'} it`, async ({ page }) => {
    let status: 'AwaitingApproval' | 'Verified' | 'GeneratedUnverified' = 'AwaitingApproval'
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname
      let json: unknown
      if (path === '/api/agents') json = agents
      else if (path === '/api/models') json = models
      else if (path === '/api/models/local') json = { available: true, error: null, models: [] }
      else if (path === '/api/sandbox') json = { available: true, reason: 'Offline image ready.' }
      else if (path === '/api/missions') json = [{ ...mission, status, verification: [] }]
      else if (path.endsWith('/execution-plan')) json = {
        fingerprint: 'reviewed-workspace-and-settings', image: 'sha256:fixed', memoryMegabytes: 2048,
        cpuCount: 2, timeoutSeconds: 120, maxCorrectionAttempts: 3, operations: ['Offline build and tests only'],
      }
      else if (path.endsWith('/approval')) {
        expect(route.request().postDataJSON()).toEqual({ approved, fingerprint: 'reviewed-workspace-and-settings' })
        status = approved ? 'Verified' : 'GeneratedUnverified'
        await route.fulfill({ status: 202 }); return
      } else throw new Error(`Unexpected request ${path}`)
      await route.fulfill({ json })
    })
    await page.goto('/')
    await expect(page.getByRole('region', { name: 'Autorisation de compilation' })).toBeVisible()
    await expect(page.getByText('Offline build and tests only')).toBeVisible()
    await expect(page.getByText(/3 corrections maximum/)).toBeVisible()
    await page.getByRole('button', { name: approved ? 'Autoriser compilation et tests' : 'Refuser l’exécution' }).click()
    await expect(page.getByRole('region', { name: 'Autorisation de compilation' })).toHaveCount(0)
    await expect(page.locator('.mission-details .badge').first()).toHaveText(
      approved ? 'Vérifié · compilation et tests réussis' : 'Généré · non vérifié')
    expect(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)).toBe(false)
  })
}
