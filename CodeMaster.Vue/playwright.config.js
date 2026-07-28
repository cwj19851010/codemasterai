import { defineConfig } from '@playwright/test'

const baseURL = process.env.CODEMASTER_E2E_BASE_URL || 'http://localhost:5173'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  timeout: 60_000,
  expect: {
    timeout: 10_000
  },
  reporter: [['list']],
  use: {
    baseURL,
    channel: 'chromium',
    headless: true,
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure'
  },
  webServer: [
    {
      command:
        'dotnet run --project ../CodeMaster.WebApi/CodeMaster.WebApi.csproj --no-launch-profile -- --urls http://localhost:5000',
      url: 'http://localhost:5000/api/health/check',
      reuseExistingServer: true,
      timeout: 120_000
    },
    {
      command: 'npm run dev -- --host 127.0.0.1 --port 5173',
      url: baseURL,
      reuseExistingServer: true,
      timeout: 120_000
    }
  ]
})
