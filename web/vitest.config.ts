import { defineConfig } from "vitest/config";

// SPEC-0003: harness Vitest da SPA — ambiente de DOM ainda não configurado (scaffold).
export default defineConfig({
  test: {
    include: ["src/**/*.test.tsx"],
  },
});
