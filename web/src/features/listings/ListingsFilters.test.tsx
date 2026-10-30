import { fireEvent, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import {
  ACCOUNT_ID,
  ACCOUNTS_PATH,
  account,
  accountSummary,
  FakeApi,
  LISTINGS_PATH,
  OTHER_ACCOUNT_ID,
  registerFakeApiCleanup,
  renderApp,
  SUMMARY_PATH,
  summary,
} from "../test-support/fakeApi.tsx";

// SPEC-0025:UT-03 — busca com espera de 300 ms (uma consulta por pausa de digitação) e filtros (status, conta, q)
// refletidos na URL de /anuncios (search params do TanStack Router: status, conta, q).

registerFakeApiCleanup();

const DEBOUNCE_MS = 300;

function twoAccountsApi(): FakeApi {
  return new FakeApi()
    .on("GET", ACCOUNTS_PATH, {
      status: 200,
      body: [
        account(),
        account({ accountId: OTHER_ACCOUNT_ID, mlUserId: 3_000_001_001, nickname: "LOJA_Y" }),
      ],
    })
    .on("GET", SUMMARY_PATH, {
      status: 200,
      body: summary([accountSummary(), accountSummary({ accountId: OTHER_ACCOUNT_ID })]),
    });
}

function searchParam(name: string): string | null {
  return new URLSearchParams(window.location.search).get(name);
}

function listingQueries(api: FakeApi) {
  return api.calls("GET", LISTINGS_PATH).map((request) => ({
    q: request.url.searchParams.get("q"),
    status: request.url.searchParams.get("status"),
    accountId: request.url.searchParams.get("accountId"),
    at: request.at,
  }));
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

describe("SPEC-0025:UT-03 — busca e filtros de Anúncios", () => {
  it("SPEC-0025:UT-03 — digitar 'fone' rápido gera uma única consulta, 300 ms depois da última tecla, e q vai para a URL", async () => {
    // Dado Anúncios carregado
    const api = twoAccountsApi();
    renderApp("/anuncios", api);
    const search = await screen.findByLabelText("Buscar por título ou código");
    await waitFor(() => expect(listingQueries(api).length).toBeGreaterThan(0));

    // Quando o usuário digita "fone" rapidamente
    for (const text of ["f", "fo", "fon", "fone"]) {
      fireEvent.change(search, { target: { value: text } });
    }
    const lastKey = performance.now();

    // Então nenhuma consulta com q sai antes da pausa…
    expect(listingQueries(api).filter((query) => query.q)).toHaveLength(0);

    // …e uma única sai depois de 300 ms, com q=fone
    await waitFor(
      () => expect(listingQueries(api).some((query) => query.q === "fone")).toBe(true),
      {
        timeout: 2_000,
      },
    );
    await wait(DEBOUNCE_MS + 200);
    const withQ = listingQueries(api).filter((query) => query.q);
    expect(withQ.map((query) => query.q)).toEqual(["fone"]);
    expect((withQ[0]?.at ?? 0) - lastKey).toBeGreaterThanOrEqual(DEBOUNCE_MS - 10);
    expect(searchParam("q")).toBe("fone");
  });

  it("SPEC-0025:UT-03 — chips de status são botões com aria-pressed e o status vai para a URL e para a consulta", async () => {
    // Dado Anúncios no filtro padrão (Ativos)
    const api = twoAccountsApi();
    renderApp("/anuncios", api);
    const active = await screen.findByRole("button", { name: "Ativos" });
    expect(active.getAttribute("aria-pressed")).toBe("true");
    for (const name of ["Pausados", "Encerrados", "Todos"]) {
      expect(screen.getByRole("button", { name }).getAttribute("aria-pressed")).toBe("false");
    }
    await waitFor(() => expect(listingQueries(api).length).toBeGreaterThan(0));
    expect(["active", null]).toContain(listingQueries(api)[0]?.status ?? null);

    // Quando escolhe "Pausados"
    fireEvent.click(screen.getByRole("button", { name: "Pausados" }));

    // Então o chip fica pressionado, a URL ganha status=paused e a consulta usa status=paused
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Pausados" }).getAttribute("aria-pressed")).toBe(
        "true",
      ),
    );
    expect(screen.getByRole("button", { name: "Ativos" }).getAttribute("aria-pressed")).toBe(
      "false",
    );
    expect(searchParam("status")).toBe("paused");
    await waitFor(() =>
      expect(listingQueries(api).some((query) => query.status === "paused")).toBe(true),
    );
  });

  it("SPEC-0025:UT-03 — com 2+ contas, o seletor Conta filtra e grava conta={accountId} na URL", async () => {
    // Dado duas contas conectadas
    const api = twoAccountsApi();
    renderApp("/anuncios", api);
    const select = await screen.findByRole("combobox", { name: "Conta" });

    // Quando escolhe a conta LOJA_Y
    fireEvent.change(select, { target: { value: OTHER_ACCOUNT_ID } });

    // Então a URL ganha conta=<id> e a consulta usa accountId=<id>
    await waitFor(() => expect(searchParam("conta")).toBe(OTHER_ACCOUNT_ID));
    await waitFor(() =>
      expect(listingQueries(api).some((query) => query.accountId === OTHER_ACCOUNT_ID)).toBe(true),
    );
  });

  it("SPEC-0025:UT-03 — com uma conta só, não há seletor Conta", async () => {
    // Dado uma conta conectada
    const api = new FakeApi()
      .on("GET", ACCOUNTS_PATH, { status: 200, body: [account()] })
      .on("GET", SUMMARY_PATH, { status: 200, body: summary([accountSummary()]) });
    renderApp("/anuncios", api);

    // Então não há seletor "Conta"
    await screen.findByRole("button", { name: "Ativos" });
    expect(screen.queryByRole("combobox", { name: "Conta" })).toBeNull();
  });

  it("SPEC-0025:UT-03 — abrir um link com filtros na URL aplica status, conta e q na tela e na consulta", async () => {
    // Dado um link /anuncios com filtros
    const api = twoAccountsApi();
    renderApp(`/anuncios?status=closed&conta=${ACCOUNT_ID}&q=caneca`, api);

    // Então a tela mostra os filtros do link e a consulta usa os mesmos valores
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Encerrados" }).getAttribute("aria-pressed")).toBe(
        "true",
      ),
    );
    const search = screen.getByLabelText("Buscar por título ou código") as HTMLInputElement;
    expect(search.value).toBe("caneca");
    expect((screen.getByRole("combobox", { name: "Conta" }) as HTMLSelectElement).value).toBe(
      ACCOUNT_ID,
    );
    await waitFor(() =>
      expect(listingQueries(api)).toContainEqual(
        expect.objectContaining({ status: "closed", accountId: ACCOUNT_ID, q: "caneca" }),
      ),
    );
  });
});
