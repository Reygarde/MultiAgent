import { defineConfig } from '@playwright/test'
import config from './playwright.config'

export default defineConfig({
  ...config,
  testMatch: '**/*.live.ts',
  timeout: 420000,
  workers: 1,
  projects: [{ name: 'live-desktop', use: { browserName: 'chromium', channel: 'msedge', viewport: { width: 1440, height: 1000 } } }],
})
