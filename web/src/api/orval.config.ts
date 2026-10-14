import { defineConfig } from "orval";

// Cliente da API gerado do documento OpenAPI do host (ADR-0013): `npm run api:generate`.
// Não edite src/api/generated à mão; gere de novo quando o contrato mudar.
export default defineConfig({
  vitrine: {
    input: { target: "./openapi.json" },
    output: {
      target: "./generated/vitrine.ts",
      client: "fetch",
      mode: "single",
      override: {
        header: (info) => [
          "biome-ignore-all lint: código gerado pelo orval a partir de src/api/openapi.json",
          `${info.title ?? "API"} ${info.version ?? ""}`.trim(),
        ],
        mutator: { path: "./http.ts", name: "http" },
      },
    },
  },
});
