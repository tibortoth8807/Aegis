import { defineConfig, devices } from '@playwright/test';

/**
 * UI tests run against the built demo (the in-browser API), so they need no backend.
 * Point AEGIS_UI_URL at a running instance (e.g. the app wired to the .NET API) to test that instead.
 */
const external = process.env.AEGIS_UI_URL;

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  expect: { timeout: 7_000 },
  fullyParallel: true,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: external ?? 'http://localhost:4173/',
    trace: 'retain-on-failure',
    // optional: a locally installed Chrome instead of Playwright's own browser
    launchOptions: process.env.PW_CHROME ? { executablePath: process.env.PW_CHROME } : {},
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'] }, grep: /@mobile/ },
  ],
  webServer: external ? undefined : {
    command: 'npm run build && npm run preview',
    url: 'http://localhost:4173/',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
