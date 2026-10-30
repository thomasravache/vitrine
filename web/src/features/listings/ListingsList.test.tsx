import { fireEvent, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import {
  ACCOUNTS_PATH,
  account,
  accountSummary,
  FakeApi,
  findAction,
  LISTINGS_PATH,
  listing,
  registerFakeApiCleanup,
  renderApp,
  SUMMARY_PATH,
  summary,
} from "../test-support/fakeApi.tsx";

// SPEC-0025:UT-04 (lista) — cartões (< 1024 px) e tabela (≥ 1024 px) saem dos mesmos dados e ficam os dois no DOM,
// alternados por CSS: preço em R$ pt-BR, "Estoque {n} · {m} vendidos", selo de estado com texto, miniatura do ML ou
// ícone neutro, link "Ver no Mercado Livre", "Carregar mais" só com nextCursor e o tempo relativo do resumo.

registerFakeApiCleanup();

const WITH_THUMBNAIL = listing({
  itemId: "MLB9000000001",
  title: "Fone de ouvido bluetooth — anúncio simulado",
  price: 1234.56,
  availableQuantity: 12,
  soldQuantity: 34,
  status: "active",
  thumbnailUrl: "https://http2.mlstatic.com/D_NQ_NP_9000000001-O.jpg",
});

const WITHOUT_THUMBNAIL = listing({
  itemId: "MLB9000000002",
  title: "Caneca de cerâmica — sem foto",
  price: 19.9,
  availableQuantity: 0,
  soldQuantity: 3,
  status: "paused",
  thumbnailUrl: null,
});

const SECOND_PAGE = listing({ itemId: "MLB9000000003", title: "Mouse óptico — 2ª página" });

function apiWithListings(): FakeApi {
  return new FakeApi()
    .on("GET", ACCOUNTS_PATH, { status: 200, body: [account()] })
    .on("GET", SUMMARY_PATH, { status: 200, body: summary([accountSummary({ itemCount: 3 })]) })
    .on("GET", LISTINGS_PATH, (request) =>
      request.url.searchParams.get("cursor") === "cursor-2"
        ? { status: 200, body: { items: [SECOND_PAGE], nextCursor: null } }
        : {
            status: 200,
            body: { items: [WITH_THUMBNAIL, WITHOUT_THUMBNAIL], nextCursor: "cursor-2" },
          },
    );
}

describe("SPEC-0025:UT-04 — lista de anúncios", () => {
  it("SPEC-0025:UT-04 — mostra preço em R$, estoque e vendidos, selo com texto, miniatura ou ícone neutro e o link do ML", async () => {
    // Dado anúncios com e sem miniatura
    const { container } = renderApp("/anuncios", apiWithListings());

    // Então os dados aparecem formatados em pt-BR
    expect((await screen.findAllByText(WITH_THUMBNAIL.title)).length).toBeGreaterThan(0);
    expect(screen.getAllByText("R$ 1.234,56").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Estoque 12 · 34 vendidos").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Ativo").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Pausado").length).toBeGreaterThan(0);
    expect(screen.getAllByText("MLB9000000001").length).toBeGreaterThan(0);

    // A miniatura do ML aparece como imagem; sem miniatura, o ícone neutro
    expect(
      container.querySelectorAll(`img[src="${WITH_THUMBNAIL.thumbnailUrl}"]`).length,
    ).toBeGreaterThan(0);
    expect(screen.getAllByTestId("listing-thumbnail-placeholder").length).toBeGreaterThan(0);

    // O link do ML abre em nova aba, sem opener, com nome acessível que cita o anúncio
    const links = screen.getAllByRole("link", {
      name: (name) => name.includes("Mercado Livre") && name.includes(WITH_THUMBNAIL.title),
    });
    expect(links.length).toBeGreaterThan(0);
    for (const link of links) {
      expect(link.getAttribute("href")).toBe(WITH_THUMBNAIL.permalink);
      expect(link.getAttribute("target")).toBe("_blank");
      expect(link.getAttribute("rel") ?? "").toContain("noopener");
    }
    expect(screen.getAllByText("Ver no Mercado Livre").length).toBeGreaterThan(0);
  });

  it("SPEC-0025:UT-04 — 'Carregar mais' só aparece com nextCursor e anexa a próxima página", async () => {
    // Dado a 1ª página com nextCursor
    const api = apiWithListings();
    renderApp("/anuncios", api);
    const loadMore = await findAction("Carregar mais");

    // Quando clica em "Carregar mais"
    fireEvent.click(loadMore);

    // Então a próxima página (cursor) é anexada, sem perder a primeira, e o botão some (nextCursor null)
    expect((await screen.findAllByText(SECOND_PAGE.title)).length).toBeGreaterThan(0);
    expect(screen.getAllByText(WITH_THUMBNAIL.title).length).toBeGreaterThan(0);
    expect(
      api.calls("GET", LISTINGS_PATH).some((r) => r.url.searchParams.get("cursor") === "cursor-2"),
    ).toBe(true);
    await waitFor(() => expect(screen.queryByRole("button", { name: "Carregar mais" })).toBeNull());
  });

  it("SPEC-0025:UT-04 — cabeçalho com total e 'sincronizado há 5 min'", async () => {
    // Dado o resumo com 3 anúncios, sincronizado há 5 minutos
    renderApp("/anuncios", apiWithListings());

    // Então o cabeçalho mostra o total e o tempo relativo
    expect(await screen.findByRole("heading", { level: 1, name: "Anúncios" })).toBeDefined();
    expect(await screen.findByText(/3 anúncios · sincronizado há 5 min/)).toBeDefined();
  });

  it("SPEC-0025:UT-04 — sem contas: convite para conectar com 'Conectar conta'", async () => {
    // Sem contas: convite para conectar
    renderApp("/anuncios", new FakeApi());
    expect(
      await screen.findByText(
        "Nenhum anúncio ainda. Conecte uma conta do Mercado Livre para ver seus anúncios aqui.",
      ),
    ).toBeDefined();
    expect(await findAction("Conectar conta")).toBeDefined();
  });

  it("SPEC-0025:UT-04 — sem resultado para os filtros: mensagem e 'Limpar filtros'", async () => {
    // Dado uma conta, mas nenhum anúncio para a busca
    const api = new FakeApi()
      .on("GET", ACCOUNTS_PATH, { status: 200, body: [account()] })
      .on("GET", SUMMARY_PATH, { status: 200, body: summary([accountSummary()]) });
    renderApp("/anuncios?status=paused&q=inexistente", api);

    // Então mostra o vazio de filtros; "Limpar filtros" volta ao padrão
    expect(await screen.findByText("Nenhum anúncio encontrado para esses filtros.")).toBeDefined();
    fireEvent.click(await findAction("Limpar filtros"));
    await waitFor(() => expect(new URLSearchParams(window.location.search).get("q")).toBeNull());
  });

  it("SPEC-0025:UT-04 — conta importando: 'Importando seus anúncios…'", async () => {
    // Dado a única conta em running, ainda sem anúncios
    const api = new FakeApi()
      .on("GET", ACCOUNTS_PATH, { status: 200, body: [account()] })
      .on("GET", SUMMARY_PATH, {
        status: 200,
        body: summary([
          accountSummary({ itemCount: 0, syncStatus: "running", lastSyncedAt: null }),
        ]),
      });
    renderApp("/anuncios", api);

    // Então mostra o estado de importação
    expect((await screen.findAllByText("Importando seus anúncios…")).length).toBeGreaterThan(0);
  });
});
