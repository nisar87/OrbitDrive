import { defineConfig, devices } from '@playwright/test';

// PW_LOCAL_CHROME=1 uses an installed Chrome (for machines that cannot download Playwright browsers).
const local = !!process.env.PW_LOCAL_CHROME;
const channel = local ? { channel: 'chrome' } : {};

export default defineConfig({
  testDir: './e2e',
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 90_000,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: { baseURL: 'http://localhost:5173', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  webServer: {
    command: 'npx vite --mode e2e',
    url: 'http://localhost:5173',
    env: { VITE_GATEWAY_URL: 'http://127.0.0.1:5199', VITE_AUTH_MODE: 'dev' },
    reuseExistingServer: false,
  },
  projects: local
    ? [
        { name: 'desktop-chrome', use: { ...devices['Desktop Chrome'], ...channel } },
        { name: 'mobile-chrome-emulated', use: { ...devices['Pixel 7'], ...channel } },
      ]
    : [
        { name: 'desktop-chromium', use: { ...devices['Desktop Chrome'] } },
        { name: 'desktop-firefox', use: { ...devices['Desktop Firefox'] } },
        { name: 'desktop-webkit', use: { ...devices['Desktop Safari'] } },
        { name: 'mobile-chromium-emulated', use: { ...devices['Pixel 7'] } },
        { name: 'mobile-webkit-emulated', use: { ...devices['iPhone 15'] } },
      ],
});
