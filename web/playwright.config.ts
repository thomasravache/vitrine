import { defineConfig, devices } from "@playwright/test";

// SPEC-0006: o ambiente local prod-like (*.localhost, RFC 6761 = sempre loopback) usa a CA interna do Caddy,
// que o navegador do Playwright não conhece. Só nesses hosts o certificado é ignorado; staging e produção seguem validando.
const baseHost = process.env.BASE_URL ? new URL(process.env.BASE_URL).hostname : undefined;
const isLocalhost =
  baseHost !== undefined && (baseHost === "localhost" || baseHost.endsWith(".localhost"));

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
    ...(isLocalhost ? { ignoreHTTPSErrors: true } : {}),
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"] } },
    { name: "mobile", use: { ...devices["Pixel 7"] } },
  ],
});
