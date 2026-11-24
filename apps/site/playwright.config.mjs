import { defineConfig, devices } from '@playwright/test';
const channel = process.env.PW_LOCAL_CHROME ? { channel: 'chrome' } : {};
export default defineConfig({
  testDir: './tests', workers: 1, retries: 0, timeout: 60_000, reporter: [['list']],
  use: { baseURL: 'http://127.0.0.1:5180', ...channel },
  webServer: { command: 'python3 -m http.server 5180', url: 'http://127.0.0.1:5180', reuseExistingServer: true, cwd: '.' },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], ...channel } }, { name: 'phone', use: { ...devices['Pixel 7'], ...channel } }],
});
