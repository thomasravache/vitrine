import {
  infiniteQueryOptions,
  queryOptions,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
import { useEffect, useRef } from "react";
import {
  getListingsSummary,
  type ListingsAccountSummaryResponse,
  type ListingsPageResponse,
  type ListingsSummaryResponse,
  listListings,
  type MercadoLivreAccountResponse,
} from "../../api/generated/vitrine.ts";
import { ACCOUNT_STATUS, accountsQuery } from "../accounts/queries.ts";

/** Itens por página de Anúncios (padrão da SPEC-0022). */
export const PAGE_SIZE = 20;

/** Intervalo da consulta ao resumo enquanto alguma conta importa (SPEC-0025 §2). */
export const IMPORT_POLL_MS = 5_000;

export type ListingStatusFilter = "active" | "paused" | "closed" | "all";

export type ListingsFilters = { status: ListingStatusFilter; accountId?: string; q?: string };

export const summaryQuery = queryOptions({
  queryKey: ["listings", "summary"],
  queryFn: async (): Promise<ListingsSummaryResponse> => {
    const response = await getListingsSummary();
    if (response.status === 200) {
      return response.data;
    }
    throw new Error(`GET do resumo dos anúncios respondeu ${response.status}.`);
  },
});

export function listingsQuery(filters: ListingsFilters) {
  return infiniteQueryOptions({
    queryKey: ["listings", "list", filters],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }): Promise<ListingsPageResponse> => {
      const response = await listListings({
        status: filters.status,
        ...(filters.accountId ? { accountId: filters.accountId } : {}),
        ...(filters.q ? { q: filters.q } : {}),
        limit: PAGE_SIZE,
        ...(pageParam ? { cursor: pageParam } : {}),
      });
      if (response.status === 200) {
        return response.data;
      }
      throw new Error(`GET dos anúncios respondeu ${response.status}.`);
    },
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
}

/** A conta ainda está importando: sincronização em andamento ou conectada sem nenhuma sincronização concluída. */
export function isAccountImporting(
  account: MercadoLivreAccountResponse,
  summary: ListingsAccountSummaryResponse | undefined,
): boolean {
  if (account.status !== ACCOUNT_STATUS.connected) {
    return false;
  }
  if (!summary) {
    return true;
  }
  return (
    summary.syncStatus === "running" || (summary.syncStatus === "idle" && !summary.lastSyncedAt)
  );
}

export function isImporting(
  accounts: MercadoLivreAccountResponse[] | undefined,
  summary: ListingsSummaryResponse | undefined,
): boolean {
  if (!summary) {
    return false;
  }
  if (summary.accounts.some((a) => a.syncStatus === "running")) {
    return true;
  }
  const byId = new Map(summary.accounts.map((a) => [a.accountId, a]));
  return (accounts ?? []).some((account) =>
    isAccountImporting(account, byId.get(account.accountId)),
  );
}

/** Sincronização mais recente entre as contas (ou `null`). */
export function lastSyncedAt(summary: ListingsSummaryResponse | undefined): string | null {
  let latest: string | null = null;
  for (const account of summary?.accounts ?? []) {
    if (
      account.lastSyncedAt &&
      (!latest || Date.parse(account.lastSyncedAt) > Date.parse(latest))
    ) {
      latest = account.lastSyncedAt;
    }
  }
  return latest;
}

/**
 * Contas + resumo dos anúncios. Enquanto alguma conta importa, consulta o resumo a cada 5 s; quando a importação
 * termina, recarrega a lista de anúncios e as contas (o estado de uma conta pode ter mudado).
 */
export function useAccountsOverview() {
  const queryClient = useQueryClient();
  const accounts = useQuery(accountsQuery);
  const summary = useQuery({
    ...summaryQuery,
    refetchInterval: (query) =>
      isImporting(accounts.data, query.state.data) ? IMPORT_POLL_MS : false,
  });
  const importing = isImporting(accounts.data, summary.data);

  const wasImporting = useRef(importing);
  useEffect(() => {
    if (wasImporting.current && !importing) {
      void queryClient.invalidateQueries({ queryKey: ["listings", "list"] });
      void queryClient.invalidateQueries({ queryKey: accountsQuery.queryKey });
    }
    wasImporting.current = importing;
  }, [importing, queryClient]);

  const summaryByAccount = new Map(
    (summary.data?.accounts ?? []).map((account) => [account.accountId, account]),
  );

  return { accounts, summary, importing, summaryByAccount };
}
