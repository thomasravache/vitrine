// SPEC-0025: apoio dos testes de componente das telas autenticadas. Monta a aplicação inteira (router + React Query,
// como main.tsx, em StrictMode) numa URL e troca o `fetch` global por uma Api simulada que responde no formato dos
// contratos SPEC-0020@1 e SPEC-0022@1 e registra cada requisição. Assim os testes dependem só das rotas, dos textos
// e dos papéis ARIA das telas, não de como os componentes são organizados.
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { StrictMode } from "react";
import { afterEach, vi } from "vitest";
import type {
  ListingResponse,
  ListingsAccountSummaryResponse,
  ListingsSummaryResponse,
  MeResponse,
  MercadoLivreAccountResponse,
} from "../../api/generated/vitrine.ts";
import { createAppRouter } from "../../router.tsx";

export type FakeResponse = { status: number; body?: unknown; headers?: Record<string, string> };

export type RecordedRequest = { method: string; url: URL; body: unknown; at: number };

type Handler = (request: RecordedRequest) => FakeResponse | Promise<FakeResponse>;

export const ACCOUNTS_PATH = "/api/v1/mercadolivre/accounts";
export const CONNECTIONS_PATH = "/api/v1/mercadolivre/connections";
export const COMPLETE_PATH = "/api/v1/mercadolivre/connections/complete";
export const LISTINGS_PATH = "/api/v1/listings";
export const SUMMARY_PATH = "/api/v1/listings/summary";
export const SYNC_PATH = "/api/v1/listings/sync";

/** Api simulada: rotas por "MÉTODO /caminho" (sem query); sem rota, 404 com Problem Details. */
export class FakeApi {
  readonly requests: RecordedRequest[] = [];
  private readonly routes = new Map<string, Handler>();

  constructor(role: "Owner" | "Member" = "Owner") {
    this.on("GET", "/api/v1/me", { status: 200, body: me(role) });
    this.on("GET", "/api/v1/auth/antiforgery", {
      status: 204,
      headers: { "X-XSRF-TOKEN": "token-de-teste" },
    });
    this.on("GET", ACCOUNTS_PATH, { status: 200, body: [] });
    this.on("GET", SUMMARY_PATH, { status: 200, body: summary([]) });
    this.on("GET", LISTINGS_PATH, { status: 200, body: { items: [], nextCursor: null } });
  }

  on(method: string, path: string, handler: Handler | FakeResponse): this {
    this.routes.set(`${method} ${path}`, typeof handler === "function" ? handler : () => handler);
    return this;
  }

  calls(method: string, path: string): RecordedRequest[] {
    return this.requests.filter((r) => r.method === method && r.url.pathname === path);
  }

  readonly fetch = async (input: RequestInfo | URL, init: RequestInit = {}): Promise<Response> => {
    const raw = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const url = new URL(raw, window.location.origin);
    const method = (init.method ?? "GET").toUpperCase();
    const body = typeof init.body === "string" && init.body ? JSON.parse(init.body) : init.body;
    const request: RecordedRequest = { method, url, body, at: performance.now() };
    this.requests.push(request);

    const handler = this.routes.get(`${method} ${url.pathname}`);
    const response = handler
      ? await handler(request)
      : { status: 404, body: { status: 404, code: "NOT_FOUND" } };
    const hasBody = response.body !== undefined && ![204, 205, 304].includes(response.status);
    return new Response(hasBody ? JSON.stringify(response.body) : null, {
      status: response.status,
      headers: {
        ...(hasBody
          ? {
              "Content-Type":
                response.status >= 400 ? "application/problem+json" : "application/json",
            }
          : {}),
        ...response.headers,
      },
    });
  };
}

/** Problem Details do Vitrine com `code` (e campos extras, como `retryAfterSeconds`). */
export function problem(status: number, code: string, extra: Record<string, unknown> = {}) {
  return { status, body: { status, title: code, code, ...extra } };
}

/** Desmonta a tela e devolve `fetch` ao original depois de cada teste do arquivo. */
export function registerFakeApiCleanup(): void {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    window.history.replaceState(null, "", "/");
  });
}

/** Abre a aplicação em `path` com a Api simulada. */
export function renderApp(path: string, api: FakeApi) {
  window.history.replaceState(null, "", path);
  vi.stubGlobal("fetch", api.fetch);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter(queryClient);
  const view = render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </StrictMode>,
  );
  return { ...view, router, queryClient };
}

export function me(role: "Owner" | "Member" = "Owner"): MeResponse {
  return {
    userId: "11111111-1111-4111-8111-111111111111",
    name: "Ana Souza",
    email: "ana@example.com",
    workspace: { id: "22222222-2222-4222-8222-222222222222", name: "Loja da Ana", role },
  };
}

export const ACCOUNT_ID = "33333333-3333-4333-8333-333333333333";
export const OTHER_ACCOUNT_ID = "44444444-4444-4444-8444-444444444444";

export function account(
  overrides: Partial<MercadoLivreAccountResponse> = {},
): MercadoLivreAccountResponse {
  return {
    accountId: ACCOUNT_ID,
    mlUserId: 3_000_001_000,
    nickname: "LOJA_X",
    siteId: "MLB",
    status: "connected",
    connectedAt: minutesAgo(60),
    ...overrides,
  };
}

export function accountSummary(
  overrides: Partial<ListingsAccountSummaryResponse> = {},
): ListingsAccountSummaryResponse {
  return {
    accountId: ACCOUNT_ID,
    itemCount: 25,
    syncStatus: "idle",
    lastSyncedAt: minutesAgo(5),
    lastError: null,
    ...overrides,
  };
}

export function summary(
  accounts: ListingsAccountSummaryResponse[],
  total = accounts.reduce((sum, a) => sum + a.itemCount, 0),
): ListingsSummaryResponse {
  return { total, byStatus: { active: total, paused: 0, closed: 0, other: 0 }, accounts };
}

export function listing(overrides: Partial<ListingResponse> = {}): ListingResponse {
  return {
    itemId: "MLB9000000001",
    accountId: ACCOUNT_ID,
    title: "Fone de ouvido bluetooth",
    price: 99.9,
    currencyId: "BRL",
    availableQuantity: 5,
    soldQuantity: 1,
    status: "active",
    permalink: "https://produto.mercadolivre.com.br/MLB-9000000001-fone-_JM",
    thumbnailUrl: "https://http2.mlstatic.com/D_NQ_NP_9000000001-O.jpg",
    lastUpdated: minutesAgo(10),
    ...overrides,
  };
}

/** Instante de `minutes` minutos atrás (alguns segundos a mais, para "há 5 min" valer com arredondamento ou piso). */
export function minutesAgo(minutes: number): string {
  return new Date(Date.now() - minutes * 60_000 - 10_000).toISOString();
}

/** Promessa controlada pelo teste (ex.: segurar a resposta de `complete`). */
export function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

/** Ação pelo nome acessível, seja botão ou link (o contrato fixa o rótulo, não o elemento). */
export function queryAction(name: string | RegExp): HTMLElement | null {
  return screen.queryByRole("button", { name }) ?? screen.queryByRole("link", { name });
}

export function findAction(name: string | RegExp): Promise<HTMLElement> {
  return waitFor(() => {
    const action = queryAction(name);
    if (!action) {
      throw new Error(`Nenhum botão ou link com o nome ${String(name)}.`);
    }
    return action;
  });
}
