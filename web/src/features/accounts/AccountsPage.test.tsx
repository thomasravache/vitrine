import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import {
  ACCOUNT_ID,
  ACCOUNTS_PATH,
  account,
  accountSummary,
  CONNECTIONS_PATH,
  FakeApi,
  OTHER_ACCOUNT_ID,
  queryAction,
  registerFakeApiCleanup,
  renderApp,
  SUMMARY_PATH,
  summary,
} from "../test-support/fakeApi.tsx";

// SPEC-0025:UT-05 — tela Contas: ações de Owner ocultas para Member (com texto explicativo); com Owner, "Desconectar"
// abre o diálogo acessível (foco em "Cancelar", Esc fecha e devolve o foco) e confirmar chama o DELETE.

registerFakeApiCleanup();

const MEMBER_NOTE = "Só o dono do workspace pode conectar ou desconectar contas.";

function dialog(): HTMLElement {
  return screen.queryByRole("alertdialog") ?? screen.getByRole("dialog");
}

describe("SPEC-0025:UT-05 — Contas", () => {
  it("SPEC-0025:UT-05 — Member não vê conectar, reconectar nem desconectar, e vê o texto explicativo", async () => {
    // Dado um Member com uma conta conectada e outra que precisa de reconexão
    const api = new FakeApi("Member")
      .on("GET", ACCOUNTS_PATH, {
        status: 200,
        body: [
          account(),
          account({
            accountId: OTHER_ACCOUNT_ID,
            mlUserId: 3_000_001_001,
            nickname: "LOJA_Y",
            status: "reauthorization_required",
          }),
        ],
      })
      .on("GET", SUMMARY_PATH, {
        status: 200,
        body: summary([
          accountSummary(),
          accountSummary({ accountId: OTHER_ACCOUNT_ID, syncStatus: "paused" }),
        ]),
      });
    renderApp("/contas", api);

    // Então vê as contas e o texto, mas nenhuma ação de Owner
    expect(await screen.findByText("LOJA_X")).toBeDefined();
    expect(screen.getByText("LOJA_Y")).toBeDefined();
    expect(await screen.findByText(MEMBER_NOTE)).toBeDefined();
    expect(queryAction("Conectar conta do Mercado Livre")).toBeNull();
    expect(queryAction(/^Reconectar/)).toBeNull();
    expect(queryAction(/^Desconectar/)).toBeNull();
  });

  it("SPEC-0025:UT-05 — Owner vê selos com texto, contagem e o botão de conectar com a nota", async () => {
    // Dado um Owner com uma conta conectada e outra que precisa de reconexão
    const api = new FakeApi("Owner")
      .on("GET", ACCOUNTS_PATH, {
        status: 200,
        body: [
          account(),
          account({
            accountId: OTHER_ACCOUNT_ID,
            mlUserId: 3_000_001_001,
            nickname: "LOJA_Y",
            status: "reauthorization_required",
          }),
        ],
      })
      .on("GET", SUMMARY_PATH, {
        status: 200,
        body: summary([
          accountSummary({ itemCount: 25 }),
          accountSummary({ accountId: OTHER_ACCOUNT_ID, syncStatus: "paused" }),
        ]),
      });
    renderApp("/contas", api);

    // Então os selos têm texto e a conta que expirou pede reconexão
    expect(
      await screen.findByRole("heading", { level: 1, name: "Contas do Mercado Livre" }),
    ).toBeDefined();
    expect(await screen.findByText("Conectada")).toBeDefined();
    expect(screen.getByText("Reconectar necessário")).toBeDefined();
    expect(screen.getByText("O acesso expirou. Conecte de novo para continuar.")).toBeDefined();
    expect(await screen.findByText(/Anúncios: 25 · sincronizado há 5 min/)).toBeDefined();
    expect(queryAction(/^Reconectar/)).not.toBeNull();
    expect(screen.getByRole("button", { name: "Conectar conta do Mercado Livre" })).toBeDefined();
    expect(
      screen.getByText(
        "Você será levado ao Mercado Livre para autorizar. O Vitrine nunca vê sua senha.",
      ),
    ).toBeDefined();
    expect(screen.queryByText(MEMBER_NOTE)).toBeNull();
  });

  it("SPEC-0025:UT-05 — 'Conectar conta do Mercado Livre' pede a URL de autorização e vai ao ML", async () => {
    // Dado um Owner sem contas
    const authorizationUrl = "https://auth.mercadolivre.com.br/authorization?state=abc";
    const api = new FakeApi("Owner").on("POST", CONNECTIONS_PATH, {
      status: 200,
      body: { authorizationUrl },
    });
    const assign = vi.spyOn(window.location, "assign").mockImplementation(() => {});
    renderApp("/contas", api);

    // Quando clica em conectar
    fireEvent.click(await screen.findByRole("button", { name: "Conectar conta do Mercado Livre" }));

    // Então POST /connections e window.location.assign(authorizationUrl)
    await waitFor(() => expect(assign).toHaveBeenCalledWith(authorizationUrl));
    expect(api.calls("POST", CONNECTIONS_PATH)).toHaveLength(1);
  });

  it("SPEC-0025:UT-05 — Desconectar abre o diálogo com foco em Cancelar; Esc fecha e devolve o foco; confirmar chama o DELETE", async () => {
    // Dado um Owner com a conta LOJA_X
    let accounts = [account()];
    const api = new FakeApi("Owner")
      .on("GET", ACCOUNTS_PATH, () => ({ status: 200, body: accounts }))
      .on("GET", SUMMARY_PATH, { status: 200, body: summary([accountSummary()]) })
      .on("DELETE", `${ACCOUNTS_PATH}/${ACCOUNT_ID}`, () => {
        accounts = [];
        return { status: 204 };
      });
    renderApp("/contas", api);
    const trigger = await screen.findByRole("button", { name: /^Desconectar/ });

    // Quando clica em "Desconectar"
    fireEvent.click(trigger);

    // Então abre o diálogo de confirmação, nomeado pela pergunta, com foco no "Cancelar"
    await waitFor(() => expect(dialog()).toBeDefined());
    const confirmDialog = dialog();
    expect(confirmDialog.textContent).toContain("Desconectar LOJA_X?");
    expect(confirmDialog.textContent).toContain(
      "Os anúncios dessa conta saem do Vitrine. Você pode conectar de novo depois.",
    );
    expect(
      screen.queryByRole("dialog", { name: /Desconectar LOJA_X\?/ }) ??
        screen.queryByRole("alertdialog", { name: /Desconectar LOJA_X\?/ }),
    ).not.toBeNull();
    const cancel = within(confirmDialog).getByRole("button", { name: "Cancelar" });
    await waitFor(() => expect(document.activeElement).toBe(cancel));

    // Quando aperta Esc; então o diálogo fecha e o foco volta para "Desconectar"
    fireEvent.keyDown(document.activeElement ?? confirmDialog, { key: "Escape", code: "Escape" });
    await waitFor(() =>
      expect(screen.queryByRole("dialog") ?? screen.queryByRole("alertdialog")).toBeNull(),
    );
    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByRole("button", { name: /^Desconectar/ })),
    );
    expect(api.calls("DELETE", `${ACCOUNTS_PATH}/${ACCOUNT_ID}`)).toHaveLength(0);

    // Quando abre de novo e confirma
    fireEvent.click(screen.getByRole("button", { name: /^Desconectar/ }));
    await waitFor(() => expect(dialog()).toBeDefined());
    fireEvent.click(within(dialog()).getByRole("button", { name: "Desconectar" }));

    // Então chama o DELETE da conta, e ela some da lista
    await waitFor(() =>
      expect(api.calls("DELETE", `${ACCOUNTS_PATH}/${ACCOUNT_ID}`)).toHaveLength(1),
    );
    await waitFor(() => expect(screen.queryByText("LOJA_X")).toBeNull());
  });
});
