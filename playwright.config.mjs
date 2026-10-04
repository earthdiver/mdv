import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './renderer/test/browser',
  timeout: 30000,
  use: { baseURL: 'http://127.0.0.1:4179', headless: true, viewport: { width: 1080, height: 900 } },
  webServer: { command: 'node scripts/test-server.mjs', url: 'http://127.0.0.1:4179', reuseExistingServer: false },
  reporter: 'list',
});
