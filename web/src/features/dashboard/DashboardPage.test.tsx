import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import {
  ACCOUNTS_PATH,
  account,
  accountSummary,
  FakeApi,
  findAction,
  registerFakeApiCleanup,
  renderApp,
  SUMMARY_PATH,
  summary,
} from "../test-support/fakeApi.tsx";

// SPEC-0025:UT-04 (painel) — o cartão certo para cada resumo: sem contas, com contas (total + "Ver anúncios"),
// importando (progresso indeterminado) e conta que precisa ser reconectada (alerta + "Ir para Contas").

registerFakeApiCleanup();

describe("SPEC-0025:UT-04 — cartão do painel", () => {
  it("SPEC-0025:UT-04 — sem contas: 'Conecte sua conta do Mercado Livre' e 'Conectar conta' habilitado para o Owner", async () => {
    // Dado um Owner sem contas
    renderApp("/painel", new FakeApi("Owner"));

    // Então o cartão convida a conectar, com o botão ativo
    expect(
      await screen.findByRole("heading", { name: "Conecte sua conta do Mercado Livre" }),
    ).toBeDefined();
    const connect = await screen.findByRole("button", { name: "Conectar conta" });
    expect((connect as HTMLButtonElement).disabled).toBe(false);
    expect(screen.queryByText(/em breve/i)).toBeNull();
  });

  it("SPEC-0025:UT-04 — com contas: '{total} anúncios · sincronizado há 5 min' e link 'Ver anúncios'", async () => {
    // Dado uma conta com 25 anúncios sincronizados há 5 minutos
    const api = new FakeApi()
      .on("GET", ACCOUNTS_PATH, { status: 200, body: [account()] })
      .on("GET", SUMMARY_PATH, { status: 200, body: summary([accountSummary({ itemCount: 25 })]) });
    renderApp("/painel", api);

    // Então mostra o resumo e o link para Anúncios
    expect(await screen.findByText(/25 anúncios · sincronizado há 5 min/)).toBeDefined();
    const link = await screen.findByRole("link", { name: "Ver anúncios" });
    expect(link.getAttribute("href")).toBe("/anuncios");
    expect(
      screen.queryByRole("heading", { name: "Conecte sua conta do Mercado Livre" }),
    ).toBeNull();
  });

  it("SPEC-0025:UT-04 — importando: 'Importando seus anúncios…' com progresso indeterminado", async () => {
    // Dado a única conta em running
    const api = new FakeApi()
      .on("GET", ACCOUNTS_PATH, { status: 200, body: [account()] })
      .on("GET", SUMMARY_PATH, {
        status: 200,
        body: summary([
          accountSummary({ itemCount: 0, syncStatus: "running", lastSyncedAt: null }),
        ]),
      });
    renderApp("/painel", api);

    // Então mostra a importação em andamento, com um progressbar sem valor
    expect((await screen.findAllByText("Importando seus anúncios…")).length).toBeGreaterThan(0);
    const progress = screen.getByRole("progressbar");
    expect(progress.getAttribute("aria-valuenow")).toBeNull();
  });

  it("SPEC-0025:UT-04 — conta em reauthorization_required: alerta 'A conta {nickname} precisa ser reconectada' + 'Ir para Contas'", async () => {
    // Dado a conta LOJA_X precisando de reconexão
    const api = new FakeApi()
      .on("GET", ACCOUNTS_PATH, {
        status: 200,
        body: [account({ status: "reauthorization_required" })],
      })
      .on("GET", SUMMARY_PATH, {
        status: 200,
        body: summary([accountSummary({ syncStatus: "paused" })]),
      });
    renderApp("/painel", api);

    // Então o alerta cita a conta e leva para Contas
    expect(await screen.findByText("A conta LOJA_X precisa ser reconectada")).toBeDefined();
    const goToAccounts = await findAction("Ir para Contas");
    expect(goToAccounts.getAttribute("href")).toBe("/contas");
  });
});
