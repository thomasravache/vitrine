import { defineConfig } from "vitest/config";

// SPEC-0003: harness Vitest da SPA. happy-dom fornece o DOM para os testes de componente.
export default defineConfig({
  test: {
    environment: "happy-dom",
    include: ["src/**/*.test.tsx"],
  },
});
