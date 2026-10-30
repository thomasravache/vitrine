import { describe, expect, expectTypeOf, it } from "vitest";
import {
  type CompleteConnectionRequest,
  type ConnectedAccountResponse,
  completeMercadoLivreConnection,
  disconnectMercadoLivreAccount,
  getListingsSummary,
  getListListingsUrl,
  type ListingResponse,
  type ListingsPageResponse,
  type ListingsSummaryResponse,
  type ListListingsParams,
  listListings,
  listMercadoLivreAccounts,
  type MercadoLivreAccountResponse,
  type StartConnectionResponse,
  startMercadoLivreConnection,
  syncListings,
} from "../api/generated/vitrine.ts";

// SPEC-0025:CT-01 — lado consumidor dos contratos SPEC-0020@1 e SPEC-0022@1. Os tipos do cliente orval (versionado e
// conferido pelo drift check do CI) têm os campos que as telas usam, e as telas de Contas, retorno do OAuth e
// Anúncios falam com a Api só pelo cliente gerado (sem fetch nem URLs de /api escritos à mão), para que o
// `tsc --noEmit` pegue qualquer mudança de contrato.

const screenSources = import.meta.glob<string>(
  ["./accounts/**/*.{ts,tsx}", "./listings/**/*.{ts,tsx}", "./dashboard/**/*.{ts,tsx}"],
  { query: "?raw", import: "default", eager: true },
);

const CONSUMED_OPERATIONS = [
  "startMercadoLivreConnection",
  "completeMercadoLivreConnection",
  "listMercadoLivreAccounts",
  "disconnectMercadoLivreAccount",
  "listListings",
  "getListingsSummary",
  "syncListings",
];

describe("SPEC-0025:CT-01 — cliente gerado dos contratos SPEC-0020@1 e SPEC-0022@1", () => {
  it("SPEC-0025:CT-01 — os tipos gerados têm os campos que as telas consomem", () => {
    expectTypeOf<MercadoLivreAccountResponse>().toMatchTypeOf<{
      accountId: string;
      mlUserId: number;
      nickname: string;
      siteId: string;
      status: string;
      connectedAt: string;
    }>();
    expectTypeOf<StartConnectionResponse>().toMatchTypeOf<{ authorizationUrl: string }>();
    expectTypeOf<CompleteConnectionRequest>().toEqualTypeOf<{ code: string; state: string }>();
    expectTypeOf<ConnectedAccountResponse>().toMatchTypeOf<{ nickname: string; status: string }>();
    expectTypeOf<ListingResponse>().toMatchTypeOf<{
      itemId: string;
      accountId: string;
      title: string;
      price: number;
      currencyId: string;
      availableQuantity: number;
      soldQuantity: number;
      status: string;
      permalink: string | null;
      thumbnailUrl: string | null;
    }>();
    expectTypeOf<ListingsPageResponse>().toMatchTypeOf<{
      items: ListingResponse[];
      nextCursor: string | null;
    }>();
    expectTypeOf<ListingsSummaryResponse>().toMatchTypeOf<{
      total: number;
      accounts: {
        accountId: string;
        itemCount: number;
        syncStatus: string;
        lastSyncedAt: string | null;
      }[];
    }>();
    expectTypeOf<ListListingsParams>().toMatchTypeOf<{
      status?: string;
      accountId?: string;
      q?: string;
      cursor?: string;
    }>();

    // Operações e URLs do contrato
    expect(typeof startMercadoLivreConnection).toBe("function");
    expect(typeof completeMercadoLivreConnection).toBe("function");
    expect(typeof listMercadoLivreAccounts).toBe("function");
    expect(typeof disconnectMercadoLivreAccount).toBe("function");
    expect(typeof listListings).toBe("function");
    expect(typeof getListingsSummary).toBe("function");
    expect(typeof syncListings).toBe("function");
    expect(getListListingsUrl({ status: "paused", q: "fone", cursor: "c2" })).toBe(
      "/api/v1/listings?status=paused&q=fone&cursor=c2",
    );
  });

  it("SPEC-0025:CT-01 — as telas usam o cliente gerado para cada operação, sem fetch nem /api à mão", () => {
    // Dado os arquivos de produção de src/features/{accounts,listings,dashboard}
    const sources = Object.entries(screenSources).filter(([path]) => !path.endsWith(".test.tsx"));
    for (const feature of ["accounts", "listings"]) {
      expect(
        sources.some(([path]) => path.startsWith(`./${feature}/`)),
        `nenhum arquivo de produção em src/features/${feature}`,
      ).toBe(true);
    }

    // Então cada operação dos contratos consumidos é chamada pelo cliente gerado…
    const all = sources.map(([, source]) => source).join("\n");
    for (const operation of CONSUMED_OPERATIONS) {
      expect(all, `as telas não usam ${operation} do cliente gerado`).toContain(operation);
    }

    // …e nenhuma tela fala com a Api por fora dele
    for (const [path, source] of sources) {
      expect(source, `${path} chama fetch direto`).not.toMatch(/\bfetch\s*\(/);
      expect(source, `${path} escreve URL de /api à mão`).not.toMatch(/["'`]\/api\//);
    }
  });
});
