import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { StrictMode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createAppRouter } from "../../router.tsx";
import {
  ACCOUNT_ID,
  ACCOUNTS_PATH,
  account,
  accountSummary,
  COMPLETE_PATH,
  CONNECTIONS_PATH,
  deferred,
  FakeApi,
  type FakeResponse,
  problem,
  registerFakeApiCleanup,
  renderApp,
  SUMMARY_PATH,
  summary,
} from "../test-support/fakeApi.tsx";

// SPEC-0025:UT-05 e SPEC-0025:UT-02 — notas do G4: o aviso de conta conectada aparece uma vez só (não volta ao
// recarregar /contas), o diálogo de desconexão trava os dois botões enquanto o DELETE está pendente, e a SPA só leva
// o navegador para uma authorizationUrl https: (em produção; em DEV aceita http:).
//
// Ambiente: no Vitest, import.meta.env.DEV é true (modo "test"). O comportamento de produção é simulado com
// vi.stubEnv("DEV", false) / vi.stubEnv("PROD", true); por isso a checagem do ambiente tem de ser lida na hora do
// redirecionamento, não congelada no carregamento do módulo. Esquemas que não são http(s) (ex.: javascript:) são
// recusados em qualquer ambiente.

registerFakeApiCleanup();
afterEach(() => {
  vi.unstubAllEnvs();
});

const CONNECTED_NOTICE = "Conta LOJA_X conectada. Estamos importando seus anúncios.";

function dialog(): HTMLElement {
  return screen.queryByRole("alertdialog") ?? screen.getByRole("dialog");
}

/** "Recarrega" a SPA na URL atual, mantendo window.history.state (como o navegador faz num F5). */
function reloadKeepingHistoryState(api: FakeApi) {
  cleanup();
  vi.stubGlobal("fetch", api.fetch);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter(queryClient);
  render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </StrictMode>,
  );
}

function ownerWithAccount(): FakeApi {
  return new FakeApi("Owner")
    .on("GET", ACCOUNTS_PATH, { status: 200, body: [account()] })
    .on("GET", SUMMARY_PATH, { status: 200, body: summary([accountSummary()]) });
}

describe("SPEC-0025:UT-05 — notas do G4 em Contas", () => {
  it("SPEC-0025:UT-05 — o aviso 'Conta X conectada' aparece uma vez e não volta ao recarregar /contas", async () => {
    // Dado o retorno do OAuth concluído com sucesso, que leva a /contas com o aviso
    const api = ownerWithAccount().on("POST", COMPLETE_PATH, {
      status: 201,
      body: {
        accountId: ACCOUNT_ID,
        mlUserId: 3_000_001_000,
        nickname: "LOJA_X",
        status: "connected",
      },
    });
    renderApp("/contas/mercado-livre/retorno?code=TG-abc&state=st-123", api);
    expect(await screen.findByText(CONNECTED_NOTICE)).toBeDefined();
    expect(window.location.pathname).toBe("/contas");

    // Quando o usuário recarrega /contas (o navegador preserva history.state)
    reloadKeepingHistoryState(api);

    // Então a tela volta, mas sem o aviso de conta conectada
    expect(
      await screen.findByRole("heading", { level: 1, name: "Contas do Mercado Livre" }),
    ).toBeDefined();
    expect(await screen.findByText("LOJA_X")).toBeDefined();
    expect(screen.queryByText(CONNECTED_NOTICE)).toBeNull();
  });

  it("SPEC-0025:UT-05 — com o DELETE pendente, 'Cancelar' e 'Desconectar' do diálogo ficam desabilitados", async () => {
    // Dado o diálogo de desconexão aberto e o DELETE sem resposta
    const pending = deferred<FakeResponse>();
    const api = ownerWithAccount().on(
      "DELETE",
      `${ACCOUNTS_PATH}/${ACCOUNT_ID}`,
      () => pending.promise,
    );
    renderApp("/contas", api);
    fireEvent.click(await screen.findByRole("button", { name: /^Desconectar/ }));
    await waitFor(() => expect(dialog()).toBeDefined());

    // Quando confirma
    fireEvent.click(within(dialog()).getByRole("button", { name: "Desconectar" }));
    await waitFor(() =>
      expect(api.calls("DELETE", `${ACCOUNTS_PATH}/${ACCOUNT_ID}`)).toHaveLength(1),
    );

    // Então, enquanto espera, os dois botões ficam desabilitados (e Esc não fecha)
    const cancel = within(dialog()).getByRole("button", { name: "Cancelar" }) as HTMLButtonElement;
    const confirm = within(dialog()).getByRole("button", {
      name: "Desconectar",
    }) as HTMLButtonElement;
    await waitFor(() => expect(cancel.disabled).toBe(true));
    expect(confirm.disabled).toBe(true);
    fireEvent.keyDown(dialog(), { key: "Escape", code: "Escape" });
    expect(dialog()).toBeDefined();

    // E, quando o DELETE responde, o diálogo fecha
    api.on("GET", ACCOUNTS_PATH, { status: 200, body: [] });
    pending.resolve({ status: 204 });
    await waitFor(() =>
      expect(screen.queryByRole("alertdialog") ?? screen.queryByRole("dialog")).toBeNull(),
    );
  });

  it.each([
    ["javascript: (qualquer ambiente)", "javascript:alert(1)", false],
    ["http: em produção", "http://auth.mercadolivre.com.br/authorization?state=abc", true],
  ])(
    "SPEC-0025:UT-05 — authorizationUrl %s não redireciona e mostra o erro genérico",
    async (_label, authorizationUrl, production) => {
      // Dado a Api devolvendo uma authorizationUrl que não é https:
      if (production) {
        vi.stubEnv("DEV", false);
        vi.stubEnv("PROD", true);
      }
      const api = new FakeApi("Owner").on("POST", CONNECTIONS_PATH, {
        status: 200,
        body: { authorizationUrl },
      });
      const assign = vi.spyOn(window.location, "assign").mockImplementation(() => {});
      renderApp("/contas", api);

      // Quando clica em conectar
      fireEvent.click(
        await screen.findByRole("button", { name: "Conectar conta do Mercado Livre" }),
      );

      // Então não sai da SPA e mostra um erro, com o botão de novo disponível
      expect(await screen.findByRole("alert")).toBeDefined();
      expect(api.calls("POST", CONNECTIONS_PATH)).toHaveLength(1);
      expect(assign).not.toHaveBeenCalled();
      await waitFor(() =>
        expect(
          (
            screen.getByRole("button", {
              name: "Conectar conta do Mercado Livre",
            }) as HTMLButtonElement
          ).disabled,
        ).toBe(false),
      );
    },
  );

  it("SPEC-0025:UT-05 — authorizationUrl http: em DEV é aceita (simulador local sem TLS)", async () => {
    // Dado o ambiente de desenvolvimento (padrão do Vitest) e uma URL http: do simulador
    const authorizationUrl = "http://localhost:5199/authorization?state=abc";
    const api = new FakeApi("Owner").on("POST", CONNECTIONS_PATH, {
      status: 200,
      body: { authorizationUrl },
    });
    const assign = vi.spyOn(window.location, "assign").mockImplementation(() => {});
    renderApp("/contas", api);

    // Quando clica em conectar; então vai para o simulador
    fireEvent.click(await screen.findByRole("button", { name: "Conectar conta do Mercado Livre" }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith(authorizationUrl));
  });
});

describe("SPEC-0025:UT-02 — Tentar de novo no retorno com authorizationUrl insegura", () => {
  it("SPEC-0025:UT-02 — em produção, 'Tentar de novo' com authorizationUrl http: não redireciona e mostra erro", async () => {
    // Dado o retorno com ML_UNAVAILABLE, em produção, e a Api devolvendo uma URL http:
    vi.stubEnv("DEV", false);
    vi.stubEnv("PROD", true);
    const api = new FakeApi("Owner")
      .on("POST", COMPLETE_PATH, problem(502, "ML_UNAVAILABLE"))
      .on("POST", CONNECTIONS_PATH, {
        status: 200,
        body: { authorizationUrl: "http://auth.mercadolivre.com.br/authorization?state=x" },
      });
    const assign = vi.spyOn(window.location, "assign").mockImplementation(() => {});
    renderApp("/contas/mercado-livre/retorno?code=TG-abc&state=st-123", api);

    // Quando clica em "Tentar de novo"
    fireEvent.click(await screen.findByRole("button", { name: "Tentar de novo" }));

    // Então pede a URL, mas não sai da SPA, e mostra um erro além do ML_UNAVAILABLE
    await waitFor(() => expect(api.calls("POST", CONNECTIONS_PATH)).toHaveLength(1));
    await waitFor(() =>
      expect(
        screen
          .getAllByRole("alert")
          .some((alert) => !alert.textContent?.includes("O Mercado Livre não respondeu")),
      ).toBe(true),
    );
    expect(assign).not.toHaveBeenCalled();
  });
});
