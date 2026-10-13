import { defineConfig, devices } from "@playwright/test";

// SPEC-0003: E2E contra a aplicação inteira. Com BASE_URL roda contra o ambiente informado (ex.: staging);
// sem BASE_URL o global-setup sobe a aplicação pelo AppHost e define BASE_URL para os workers.
export default defineConfig({
  testDir: "./e2e",
  testMatch: "**/*.spec.ts",
  globalSetup: "./e2e/global-setup.ts",
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  use: {
    ...(process.env.BASE_URL ? { baseURL: process.env.BASE_URL } : {}),
    trace: "retain-on-failure",
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"] } },
    { name: "mobile", use: { ...devices["Pixel 7"] } },
  ],
});
