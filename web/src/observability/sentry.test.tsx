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
