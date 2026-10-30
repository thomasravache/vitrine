import { fireEvent, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import {
  ACCOUNTS_PATH,
  account,
  COMPLETE_PATH,
  deferred,
  FakeApi,
  type FakeResponse,
  findAction,
  registerFakeApiCleanup,
  renderApp,
} from "../test-support/fakeApi.tsx";

// SPEC-0025:UT-02 — página de retorno do OAuth (/contas/mercado-livre/retorno): só aceita code/state/error da query,
// chama `complete` uma única vez (mesmo em StrictMode), tira code/state da URL com history.replaceState antes de
// qualquer outra coisa e, sem parâmetros, volta para /contas.

registerFakeApiCleanup();

describe("SPEC-0025:UT-02 — retorno do OAuth", () => {
  it("SPEC-0025:UT-02 — com code e state, conclui a conexão uma vez, limpa a URL e volta para Contas com aviso", async () => {
    // Dado o retorno do ML com code e state, e o `complete` ainda em andamento
    const pending = deferred<FakeResponse>();
    const api = new FakeApi().on("POST", COMPLETE_PATH, () => pending.promise);
    renderApp("/contas/mercado-livre/retorno?code=TG-abc&state=st-123", api);

    // Então mostra "Concluindo a conexão…" e a URL já não tem code nem state
    expect(await screen.findByText("Concluindo a conexão…")).toBeDefined();
    await waitFor(() => expect(api.calls("POST", COMPLETE_PATH)).toHaveLength(1));
    expect(window.location.pathname).toBe("/contas/mercado-livre/retorno");
    expect(window.location.search).not.toContain("code=");
    expect(window.location.search).not.toContain("state=");
    expect(api.calls("POST", COMPLETE_PATH)[0]?.body).toEqual({ code: "TG-abc", state: "st-123" });

    // Quando o Vitrine responde 201 (conta nova)
    api.on("GET", ACCOUNTS_PATH, { status: 200, body: [account()] });
    pending.resolve({
      status: 201,
      body: {
        accountId: account().accountId,
        mlUserId: 3_000_001_000,
        nickname: "LOJA_X",
        status: "connected",
      },
    });

    // Então vai para /contas com o aviso de sucesso, e `complete` foi chamado uma única vez
    expect(
      await screen.findByText("Conta LOJA_X conectada. Estamos importando seus anúncios."),
    ).toBeDefined();
    expect(window.location.pathname).toBe("/contas");
    expect(window.location.search).not.toContain("code=");
    expect(api.calls("POST", COMPLETE_PATH)).toHaveLength(1);
  });

  it("SPEC-0025:UT-02 — com error=access_denied, não chama complete, avisa o cancelamento e limpa a URL", async () => {
    // Dado o retorno do ML depois de "Cancelar"
    const api = new FakeApi();
    renderApp("/contas/mercado-livre/retorno?error=access_denied&state=st-123", api);

    // Então mostra a mensagem de cancelamento com "Voltar para Contas"; complete nunca é chamado
    expect(await screen.findByText("Você cancelou a autorização no Mercado Livre.")).toBeDefined();
    expect(window.location.search).not.toContain("state=");
    expect(api.calls("POST", COMPLETE_PATH)).toHaveLength(0);

    // Quando clica em "Voltar para Contas"; então vai para /contas
    fireEvent.click(await findAction("Voltar para Contas"));
    await waitFor(() => expect(window.location.pathname).toBe("/contas"));
    expect(api.calls("POST", COMPLETE_PATH)).toHaveLength(0);
  });

  it("SPEC-0025:UT-02 — sem code, state nem error, redireciona para /contas sem chamar complete", async () => {
    // Dado a página de retorno aberta sem parâmetros
    const api = new FakeApi();
    renderApp("/contas/mercado-livre/retorno", api);

    // Então vai para /contas
    expect(
      await screen.findByRole("heading", { level: 1, name: "Contas do Mercado Livre" }),
    ).toBeDefined();
    expect(window.location.pathname).toBe("/contas");
    expect(api.calls("POST", COMPLETE_PATH)).toHaveLength(0);
  });
});
