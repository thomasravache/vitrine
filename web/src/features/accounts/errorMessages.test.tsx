import { fireEvent, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import {
  account,
  accountSummary,
  COMPLETE_PATH,
  CONNECTIONS_PATH,
  FakeApi,
  problem,
  registerFakeApiCleanup,
  renderApp,
  SUMMARY_PATH,
  SYNC_PATH,
  summary,
} from "../test-support/fakeApi.tsx";

// SPEC-0025:UT-01 — cada `code` dos contratos SPEC-0020@1 e SPEC-0022@1 vira a mensagem da seção 6, com ou sem
// "Tentar de novo". O mapeamento é verificado pela tela que mostra cada código: o retorno do OAuth
// (POST /connections/complete) e o "Sincronizar agora" de Anúncios (POST /listings/sync).

registerFakeApiCleanup();

const RETURN_URL = "/contas/mercado-livre/retorno?code=TG-abc&state=st-123";

const KNOWN_MESSAGES = [
  "O link de autorização expirou ou já foi usado. Tente conectar de novo.",
  "Essa conta do Mercado Livre já está conectada em outro workspace do Vitrine.",
  "Por enquanto o Vitrine funciona só com contas do Mercado Livre Brasil.",
  "O Mercado Livre não respondeu. Tente de novo em alguns minutos.",
  "Conexão com o Mercado Livre indisponível no momento.",
  "Só o dono do workspace pode conectar contas.",
];

describe("SPEC-0025:UT-01 — mensagens de erro por code", () => {
  it.each([
    [400, "OAUTH_STATE_INVALID", true, KNOWN_MESSAGES[0]],
    [400, "OAUTH_CODE_INVALID", true, KNOWN_MESSAGES[0]],
    [409, "ML_ACCOUNT_IN_USE", false, KNOWN_MESSAGES[1]],
    [422, "ML_SITE_NOT_SUPPORTED", false, KNOWN_MESSAGES[2]],
    [502, "ML_UNAVAILABLE", true, KNOWN_MESSAGES[3]],
    [503, "ML_NOT_CONFIGURED", false, KNOWN_MESSAGES[4]],
    [403, "FORBIDDEN", false, KNOWN_MESSAGES[5]],
  ] as const)(
    "SPEC-0025:UT-01 — retorno com %i %s mostra a mensagem certa (Tentar de novo: %s)",
    async (status, code, canRetry, message) => {
      // Dado o retorno do ML com code+state e a Api respondendo com o code
      const api = new FakeApi().on("POST", COMPLETE_PATH, problem(status, code));

      // Quando a página de retorno conclui a conexão
      renderApp(RETURN_URL, api);

      // Então mostra a mensagem do contrato, com ou sem "Tentar de novo"
      expect(await screen.findByText(message as string)).toBeDefined();
      expect(screen.queryByRole("button", { name: "Tentar de novo" }) !== null).toBe(canRetry);
      expect(screen.queryByText(code)).toBeNull();
    },
  );

  it("SPEC-0025:UT-01 — code desconhecido recebe uma mensagem genérica, sem expor o code", async () => {
    // Dado um code que a SPA não conhece
    const api = new FakeApi().on("POST", COMPLETE_PATH, problem(500, "ALGO_NOVO_DO_SERVIDOR"));

    // Quando a página de retorno recebe o erro
    renderApp(RETURN_URL, api);

    // Então mostra um alerta genérico (não uma das mensagens específicas, nem o code cru)
    const alert = await screen.findByRole("alert");
    const text = alert.textContent?.trim() ?? "";
    expect(text.length).toBeGreaterThan(0);
    expect(text).not.toContain("ALGO_NOVO_DO_SERVIDOR");
    for (const known of KNOWN_MESSAGES) {
      expect(text).not.toContain(known);
    }
    expect(screen.queryByText("Concluindo a conexão…")).toBeNull();
  });

  it("SPEC-0025:UT-01 — Tentar de novo reinicia a conexão (POST /connections e ida ao ML)", async () => {
    // Dado o retorno com ML_UNAVAILABLE
    const authorizationUrl = "https://auth.mercadolivre.com.br/authorization?state=novo";
    const api = new FakeApi()
      .on("POST", COMPLETE_PATH, problem(502, "ML_UNAVAILABLE"))
      .on("POST", CONNECTIONS_PATH, { status: 200, body: { authorizationUrl } });
    const assign = vi.spyOn(window.location, "assign").mockImplementation(() => {});
    renderApp(RETURN_URL, api);

    // Quando o usuário clica em "Tentar de novo"
    fireEvent.click(await screen.findByRole("button", { name: "Tentar de novo" }));

    // Então a conexão recomeça: POST /connections e redirecionamento para a authorizationUrl
    await waitFor(() => expect(assign).toHaveBeenCalledWith(authorizationUrl));
    expect(api.calls("POST", CONNECTIONS_PATH)).toHaveLength(1);
  });

  it.each([
    [
      "429 SYNC_TOO_SOON",
      problem(429, "SYNC_TOO_SOON", { retryAfterSeconds: 240 }),
      "Você sincronizou há pouco. Tente de novo em 4 min.",
    ],
    [
      "409 ACCOUNT_REAUTHORIZATION_REQUIRED",
      problem(409, "ACCOUNT_REAUTHORIZATION_REQUIRED"),
      "Reconecte a conta para sincronizar.",
    ],
  ])(
    "SPEC-0025:UT-01 — Sincronizar agora com %s mostra a mensagem certa",
    async (_label, response, message) => {
      // Dado Anúncios com uma conta conectada e a Api respondendo ao sync com o code
      const api = new FakeApi()
        .on("GET", "/api/v1/mercadolivre/accounts", { status: 200, body: [account()] })
        .on("GET", SUMMARY_PATH, { status: 200, body: summary([accountSummary()]) })
        .on("POST", SYNC_PATH, response);
      renderApp("/anuncios", api);

      // Quando o usuário clica em "Sincronizar agora"
      fireEvent.click(await screen.findByRole("button", { name: "Sincronizar agora" }));

      // Então mostra a mensagem do contrato; o corpo enviado é {} (todas as contas)
      expect(await screen.findByText(message)).toBeDefined();
      expect(api.calls("POST", SYNC_PATH)[0]?.body).toEqual({});
    },
  );
});
