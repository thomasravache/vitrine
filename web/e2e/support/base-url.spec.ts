import { expect, test } from "@playwright/test";
import { acceptsLocalCa } from "./base-url.ts";

// SPEC-0006 (UT-05): o E2E só aceita certificado não confiável para a CA interna do Caddy local,
// ou seja, `localhost` e hosts `*.localhost`. Staging, produção e hosts parecidos seguem validados.

const ACEITA: string[] = [
  "https://vitrine.localhost",
  "https://localhost:5173",
  "http://localhost",
  "https://app.vitrine.localhost/",
];

const VALIDA: (string | undefined)[] = [
  undefined,
  "",
  "https://staging.vitrine.app",
  "https://vitrine.app",
  // Hosts que só "parecem" locais: o sufixo real não é .localhost.
  "https://vitrine.localhost.evil.com",
  "https://localhost.evil.com",
  "https://evil-localhost",
];

test.describe("acceptsLocalCa", () => {
  for (const baseUrl of ACEITA) {
    test(`SPEC-0006:UT-05 — aceita a CA local para ${baseUrl}`, () => {
      expect(acceptsLocalCa(baseUrl)).toBe(true);
    });
  }

  for (const baseUrl of VALIDA) {
    test(`SPEC-0006:UT-05 — valida o certificado para ${JSON.stringify(baseUrl)}`, () => {
      expect(acceptsLocalCa(baseUrl)).toBe(false);
    });
  }

  // URL inválida não pode derrubar a configuração: só não aceita a CA local.
  test("SPEC-0006:UT-05 — URL inválida não lança e valida o certificado", () => {
    expect(() => acceptsLocalCa("não é url")).not.toThrow();
    expect(acceptsLocalCa("não é url")).toBe(false);
  });
});
