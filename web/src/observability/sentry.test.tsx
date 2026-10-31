import * as Sentry from "@sentry/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { initSentry } from "./sentry.ts";

vi.mock("@sentry/react", () => ({ init: vi.fn() }));

// Formas mínimas usadas pelo teste (o SDK é simulado; só as opções passadas a Sentry.init importam).
type TestEvent = {
  message?: string;
  exception?: { values?: { type?: string; value?: string }[] };
  request?: {
    url?: string;
    query_string?: unknown;
    cookies?: unknown;
    headers?: unknown;
    data?: unknown;
  };
};
type TestBreadcrumb = { category?: string; message?: string; data?: Record<string, unknown> };
type CapturedOptions = {
  dsn?: string;
  beforeSend?: (event: TestEvent, hint: unknown) => TestEvent | null;
  beforeBreadcrumb?: (breadcrumb: TestBreadcrumb, hint?: unknown) => TestBreadcrumb | null;
};

const DSN = "https://chave-publica-falsa@o0.ingest.sentry.io/0";
const EMAIL = "marina.souza@example.com";
const BEARER_SECRET = "eyJhbGciOiJIUzI1NiJ9.segredo-spa.assinatura";
const QUERY_TOKEN = "tok-consulta-spa-123";
const SECRETS = [EMAIL, BEARER_SECRET, QUERY_TOKEN];

function env(values: Record<string, string | undefined>): ImportMetaEnv {
  return { MODE: "test", ...values } as unknown as ImportMetaEnv;
}

function initWithDsn(): CapturedOptions {
  expect(initSentry(env({ VITE_SENTRY_DSN: DSN }))).toBe(true);
  const init = vi.mocked(Sentry.init);
  expect(init).toHaveBeenCalledTimes(1);
  return init.mock.calls[0]?.[0] as CapturedOptions;
}

function expectMasked(value: unknown): void {
  const text = JSON.stringify(value);
  for (const secret of SECRETS) {
    expect(text, `"${secret}" saiu para o Sentry: ${text}`).not.toContain(secret);
  }
  expect(text).toContain("***");
}

describe("Sentry da SPA", () => {
  beforeEach(() => {
    vi.mocked(Sentry.init).mockClear();
  });

  it("SPEC-0007:UT-03 — sem VITE_SENTRY_DSN o Sentry não é inicializado", () => {
    // Dado/Quando o build sem DSN (ou só com espaços)
    const semDsn = initSentry(env({}));
    const dsnEmBranco = initSentry(env({ VITE_SENTRY_DSN: "   " }));

    // Então nada é inicializado
    expect(semDsn).toBe(false);
    expect(dsnEmBranco).toBe(false);
    expect(vi.mocked(Sentry.init)).not.toHaveBeenCalled();
  });

  it("SPEC-0007:UT-03 — evento sai com e-mail e Bearer mascarados na mensagem, exceção e URL, sem cookies, cabeçalhos, corpo nem query", () => {
    // Dado o Sentry da SPA inicializado com DSN de teste
    const options = initWithDsn();
    expect(options.beforeSend, "beforeSend não configurado").toBeTypeOf("function");

    // Quando um evento traz e-mail e Bearer na mensagem, na exceção e na URL, e dados da requisição
    const event: TestEvent = {
      message: `Falha no login de ${EMAIL} com Bearer ${BEARER_SECRET}`,
      exception: {
        values: [
          { type: "Error", value: `Requisição de ${EMAIL} recusada (Bearer ${BEARER_SECRET})` },
        ],
      },
      request: {
        url: `https://vitrine.example/entrar?email=${EMAIL}&token=${QUERY_TOKEN}`,
        query_string: `email=${EMAIL}&token=${QUERY_TOKEN}`,
        cookies: { "vitrine.session": "CfDJ8sessao" },
        headers: { Authorization: `Bearer ${BEARER_SECRET}` },
        data: { email: EMAIL, password: "senha-forte-2026" },
      },
    };
    const sent = options.beforeSend?.(event, {});

    // Então mensagem, exceção e URL saem mascaradas (***)
    expect(sent).not.toBeNull();
    expectMasked(sent?.message);
    expectMasked(sent?.exception?.values?.[0]?.value);
    const url = sent?.request?.url ?? "";
    for (const secret of SECRETS) {
      expect(url).not.toContain(secret);
    }

    // E cookies, cabeçalhos, corpo e query continuam removidos
    expect(sent?.request?.cookies).toBeUndefined();
    expect(sent?.request?.headers).toBeUndefined();
    expect(sent?.request?.data).toBeUndefined();
    expect(sent?.request?.query_string).toBeUndefined();
  });

  it.each<[string, TestBreadcrumb]>([
    [
      "console",
      {
        category: "console",
        message: `login de ${EMAIL} com Bearer ${BEARER_SECRET}`,
        data: { arguments: [`login de ${EMAIL}`, `Bearer ${BEARER_SECRET}`], logger: "console" },
      },
    ],
    [
      "fetch",
      {
        category: "fetch",
        data: {
          method: "POST",
          url: `https://vitrine.example/api/v1/auth/login?email=${EMAIL}&token=${QUERY_TOKEN}`,
          status_code: 401,
        },
      },
    ],
    [
      "navegação",
      {
        category: "navigation",
        data: { from: `/entrar?email=${EMAIL}&token=${QUERY_TOKEN}`, to: "/painel" },
      },
    ],
  ])(
    "SPEC-0007:UT-03 — breadcrumb de %s sai com e-mail, Bearer e token mascarados",
    (_, breadcrumb) => {
      // Dado o Sentry da SPA inicializado com DSN de teste
      const options = initWithDsn();
      expect(options.beforeBreadcrumb, "beforeBreadcrumb não configurado").toBeTypeOf("function");

      // Quando o breadcrumb traz e-mail, Bearer ou token
      const sent = options.beforeBreadcrumb?.(structuredClone(breadcrumb), {});

      // Então sai mascarado (***), sem nenhum dos valores sensíveis
      expect(sent).not.toBeNull();
      expectMasked(sent);
    },
  );
});

// SPEC-0030: o `code` e o `state` da volta do OAuth (ML) nunca saem da SPA, nem pelos breadcrumbs nem pela URL do evento.
const OAUTH_CODE = "TG-abc123";
const OAUTH_STATE = "xyz789";
const OAUTH_RETURN = `/contas/mercado-livre/retorno?code=${OAUTH_CODE}&state=${OAUTH_STATE}`;
const OTHER_ROUTE = "/painel?code=1&state=2";
const OAUTH_PARAM_PATTERN = /(?:^|[?&#;])(code|state)=([^&#\s"]*)/g;

function expectNoOAuthValues(value: unknown): void {
  const text = JSON.stringify(value);
  for (const secret of [OAUTH_CODE, OAUTH_STATE]) {
    expect(text, `"${secret}" saiu para o Sentry: ${text}`).not.toContain(secret);
  }
  // `code=1&state=2` não dá para procurar por substring; todo `code`/`state` que sobrar tem de estar mascarado.
  const strings: string[] = [];
  JSON.stringify(value, (_key, item: unknown) => {
    if (typeof item === "string") {
      strings.push(item);
    }
    return item;
  });
  for (const item of strings) {
    for (const [, name, paramValue] of item.matchAll(OAUTH_PARAM_PATTERN)) {
      expect(paramValue, `${name} saiu sem máscara para o Sentry: ${item}`).toBe("***");
    }
  }
}

describe("Sentry da SPA — retorno do OAuth", () => {
  beforeEach(() => {
    vi.mocked(Sentry.init).mockClear();
  });

  it.each<[string, TestBreadcrumb]>([
    [
      "navegação saindo do retorno do OAuth",
      {
        category: "navigation",
        data: { from: OAUTH_RETURN, to: "/contas/mercado-livre/retorno" },
      },
    ],
    [
      "navegação entrando no retorno do OAuth",
      {
        category: "navigation",
        data: { from: "/contas", to: `https://vitrine.example${OAUTH_RETURN}` },
      },
    ],
    [
      "navegação com code/state em outra rota",
      { category: "navigation", data: { from: OTHER_ROUTE, to: "/painel" } },
    ],
    [
      "fetch",
      {
        category: "fetch",
        data: { method: "GET", url: `https://vitrine.example${OAUTH_RETURN}`, status_code: 200 },
      },
    ],
    [
      "xhr",
      {
        category: "xhr",
        data: { method: "GET", url: `https://vitrine.example${OTHER_ROUTE}`, status_code: 200 },
      },
    ],
    [
      "navegação com mensagem",
      { category: "navigation", message: `navegou de ${OAUTH_RETURN} para ${OTHER_ROUTE}` },
    ],
  ])("SPEC-0030:UT-01 — breadcrumb de %s sai sem os valores de code/state", (_, breadcrumb) => {
    // Dado o Sentry da SPA inicializado com DSN de teste
    const options = initWithDsn();
    expect(options.beforeBreadcrumb, "beforeBreadcrumb não configurado").toBeTypeOf("function");

    // Quando o breadcrumb traz code/state na URL
    const sent = options.beforeBreadcrumb?.(structuredClone(breadcrumb), {});

    // Então nenhum valor de code/state sai da SPA
    expect(sent).not.toBeNull();
    expectNoOAuthValues(sent);
  });

  it("SPEC-0030:UT-01 — evento na tela de retorno do OAuth sai sem code/state na URL", () => {
    // Dado o Sentry da SPA inicializado com DSN de teste
    const options = initWithDsn();
    expect(options.beforeSend, "beforeSend não configurado").toBeTypeOf("function");

    // Quando um erro acontece na tela de retorno do OAuth (e em outra rota com code/state)
    const noRetorno = options.beforeSend?.(
      { message: "Falha", request: { url: `https://vitrine.example${OAUTH_RETURN}` } },
      {},
    );
    const emOutraRota = options.beforeSend?.(
      { message: "Falha", request: { url: `https://vitrine.example${OTHER_ROUTE}` } },
      {},
    );

    // Então a URL do evento sai sem os valores de code/state
    expect(noRetorno).not.toBeNull();
    expect(emOutraRota).not.toBeNull();
    expectNoOAuthValues(noRetorno);
    expectNoOAuthValues(emOutraRota);
  });
});
