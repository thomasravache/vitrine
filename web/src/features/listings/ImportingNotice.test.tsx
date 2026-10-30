import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import {
  ACCOUNTS_PATH,
  account,
  accountSummary,
  FakeApi,
  registerFakeApiCleanup,
  renderApp,
  SUMMARY_PATH,
  summary,
} from "../test-support/fakeApi.tsx";

// SPEC-0025:UT-04 — nota do G4: "Importando seus anúncios…" fica numa região viva
// (role="status"), para leitores de tela anunciarem o início e o fim da importação.

registerFakeApiCleanup();

function importingApi(): FakeApi {
  return new FakeApi()
    .on("GET", ACCOUNTS_PATH, { status: 200, body: [account()] })
    .on("GET", SUMMARY_PATH, {
      status: 200,
      body: summary([accountSummary({ itemCount: 0, syncStatus: "running", lastSyncedAt: null })]),
    });
}

describe("SPEC-0025:UT-04 — 'Importando seus anúncios…' em região viva", () => {
  it.each([
    ["Painel", "/painel"],
    ["Anúncios", "/anuncios"],
  ])(
    "SPEC-0025:UT-04 — no %s, o aviso de importação está dentro de role=status",
    async (_screen, path) => {
      // Dado a única conta importando
      renderApp(path, importingApi());

      // Então todo "Importando seus anúncios…" visível está dentro de uma região role="status"
      const notices = await screen.findAllByText("Importando seus anúncios…");
      for (const notice of notices) {
        expect(notice.closest('[role="status"]'), "aviso fora de role=status").not.toBeNull();
      }
    },
  );
});
