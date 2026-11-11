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
  await expect(page.getByRole('checkbox')).toBeDisabled()
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
