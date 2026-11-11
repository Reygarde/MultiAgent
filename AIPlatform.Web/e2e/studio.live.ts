import { expect, test } from '@playwright/test'

test('real API and Ollama: chat mission is routed and completed', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('textbox', { name: 'Votre mission' }).fill('Quelle est la capitale de la France ? Réponds en une phrase.')
  const creation = page.waitForResponse(response => response.url().endsWith('/api/missions') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Envoyer la mission', exact: true }).click()
  const response = await creation
  expect(response.status()).toBe(202)
  const { id } = await response.json() as { id: string }
  await expect.poll(async () => (await (await page.request.get(`/api/missions/${id}`)).json()).status,
    { timeout: 180000, intervals: [1000, 2000] }).toBe('Completed')
  await expect(page.locator(`[data-mission-id="${id}"] .markdown`)).toContainText('Paris', { timeout: 10000 })
  const result = await (await page.request.get(`/api/missions/${id}`)).json()
  expect(result.selectedAgent).toBe('GeneralAgent')
  expect(result.selectedModel).toBe('qwen3:4b')
  expect(result.usedFallback).toBe(false)
})

test('real project generation writes files but never runs code without Docker approval', async ({ page }, testInfo) => {
  await page.goto('/')
  await page.getByRole('combobox', { name: 'Mode de mission' }).selectOption('Project')
  await page.getByRole('textbox', { name: 'Votre mission' }).fill('Crée une API REST .NET 10 avec CRUD utilisateurs et tests unitaires. Reste concis : un modèle User, un service testable en mémoire, des endpoints minimal API et deux tests xUnit.');
  const creation = page.waitForResponse(response => response.url().endsWith('/api/missions') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Envoyer la mission', exact: true }).click()
  const response = await creation
  expect(response.status()).toBe(202)
  const { id } = await response.json() as { id: string }
  let result: { status: string; selectedAgent: string; selectedModel: string; usedFallback: boolean; verification: { exitCode: number | null }[]; error: string | null }
  await expect.poll(async () => {
    result = await (await page.request.get(`/api/missions/${id}`)).json()
    return result.status === 'Queued' || result.status === 'Running' ? 'active' : result.status
  }, { timeout: 360000, intervals: [2000] }).toBe('GeneratedUnverified')
  result = await (await page.request.get(`/api/missions/${id}`)).json()
  expect(result.selectedAgent).toBe('DeveloperAgent')
  expect(result.selectedModel).toBe('qwen2.5-coder:7b')
  expect(result.usedFallback).toBe(false)
  expect(result.verification.length).toBe(2)
  expect(result.verification.every(item => item.exitCode === null)).toBe(true)
  await page.getByRole('button', { name: 'Projects', exact: true }).click()
  await expect(page.getByRole('button', { name: 'src/App/Program.cs', exact: true })).toBeVisible({ timeout: 10000 })
  await expect(page.locator('.file-preview code')).toContainText('WebApplication', { timeout: 10000 })
  await expect(page.getByText(/Build · (Unavailable|Skipped) · sortie non exécuté/)).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('studio-projects.png'), fullPage: true })
})
