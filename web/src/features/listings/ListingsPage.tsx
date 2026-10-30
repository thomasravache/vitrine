import { useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { type ReactNode, useEffect, useId, useRef, useState } from "react";
import { syncListings } from "../../api/generated/vitrine.ts";
import type { Problem } from "../../api/session.ts";
import { ErrorAlert } from "../../ui/Alert.tsx";
import { Button, buttonClass } from "../../ui/Button.tsx";
import { listingsCount, timeAgo } from "../../ui/format.ts";
import { ImportingNotice } from "../../ui/ImportingNotice.tsx";
import { InfoIcon, SearchIcon, SyncIcon } from "../../ui/icons.tsx";
import { useDocumentTitle } from "../../ui/useDocumentTitle.ts";
import { syncErrorMessage } from "../accounts/errorMessages.ts";
import { ListingsList } from "./ListingsList.tsx";
import {
  type ListingStatusFilter,
  type ListingsFilters,
  lastSyncedAt,
  listingsQuery,
  summaryQuery,
  useAccountsOverview,
} from "./queries.ts";

/** Search params de /anuncios (filtros na URL: o link funciona e o voltar respeita a navegação). */
export type ListingsSearch = {
  status?: ListingStatusFilter | undefined;
  conta?: string | undefined;
  q?: string | undefined;
};

const STATUSES: ListingStatusFilter[] = ["active", "paused", "closed", "all"];

export function validateListingsSearch(search: Record<string, unknown>): ListingsSearch {
  const status = STATUSES.find((value) => value === search.status);
  const conta = typeof search.conta === "string" && search.conta ? search.conta : undefined;
  const q = typeof search.q === "string" && search.q.trim() ? search.q : undefined;
  return {
    ...(status && status !== "active" ? { status } : {}),
    ...(conta ? { conta } : {}),
    ...(q ? { q } : {}),
  };
}

const CHIPS: { value: ListingStatusFilter; label: string }[] = [
  { value: "active", label: "Ativos" },
  { value: "paused", label: "Pausados" },
  { value: "closed", label: "Encerrados" },
  { value: "all", label: "Todos" },
];

/** Espera depois da última tecla antes de buscar (SPEC-0025 §4). */
export const SEARCH_DEBOUNCE_MS = 300;

/** Anúncios: busca, filtros por estado e conta, "Carregar mais" e "Sincronizar agora". */
export function ListingsPage({ search }: { search: ListingsSearch }) {
  useDocumentTitle("Anúncios");
  const navigate = useNavigate({ from: "/anuncios" });
  const { accounts, summary, importing } = useAccountsOverview();
  const status = search.status ?? "active";

  const filters: ListingsFilters = {
    status,
    ...(search.conta ? { accountId: search.conta } : {}),
    ...(search.q ? { q: search.q } : {}),
  };
  const listings = useInfiniteQuery(listingsQuery(filters));

  const accountList = accounts.data ?? [];
  const nicknames = new Map(accountList.map((a) => [a.accountId, a.nickname]));
  const synced = lastSyncedAt(summary.data);
  const items = listings.data?.pages.flatMap((page) => page.items) ?? [];
  const hasFilters = Boolean(search.q || search.conta || search.status);

  function setStatus(value: ListingStatusFilter) {
    void navigate({
      search: (prev) => ({ ...prev, status: value === "active" ? undefined : value }),
    });
  }

  function setAccount(value: string) {
    void navigate({ search: (prev) => ({ ...prev, conta: value || undefined }) });
  }

  function clearFilters() {
    void navigate({ search: {} });
  }

  return (
    <>
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold tracking-tight lg:text-4xl">Anúncios</h1>
          {summary.data && (
            <p className="mt-2 text-muted">
              {listingsCount(summary.data.total)}
              {synced ? ` · sincronizado ${timeAgo(synced)}` : ""}
            </p>
          )}
        </div>
        {accountList.length > 0 && <SyncButton />}
      </div>

      {accounts.isError && (
        <div className="mt-6">
          <ErrorAlert>
            Não foi possível carregar seus anúncios agora. Recarregue a página para tentar de novo.
          </ErrorAlert>
        </div>
      )}

      {accounts.isPending && (
        <p role="status" className="mt-6 text-muted">
          Carregando anúncios…
        </p>
      )}

      {!accounts.data ? null : accountList.length === 0 ? (
        <EmptyState
          message="Nenhum anúncio ainda. Conecte uma conta do Mercado Livre para ver seus anúncios aqui."
          action={
            <Link to="/contas" className={buttonClass("primary")}>
              Conectar conta
            </Link>
          }
        />
      ) : (
        <>
          <Filters
            search={search}
            status={status}
            accounts={accountList}
            onStatus={setStatus}
            onAccount={setAccount}
          />

          {importing && (
            <div className="mt-6 rounded-2xl border border-line-soft bg-surface p-5">
              <ImportingNotice />
            </div>
          )}

          {summary.data?.accounts.some((a) => a.syncStatus === "failed") && (
            <p className="mt-6 flex items-start gap-3 rounded-xl border border-pending/40 bg-amber-50 px-4 py-3 text-sm text-amber-800">
              <InfoIcon className="mt-0.5 size-5 shrink-0" />A última sincronização falhou.
              Mostramos os anúncios da sincronização anterior.
            </p>
          )}

          <div className="mt-6">
            {listings.isError && (
              <ErrorAlert>
                Não foi possível carregar seus anúncios agora. Tente de novo em alguns minutos.
              </ErrorAlert>
            )}
            {listings.isPending && (
              <p role="status" className="text-muted">
                Carregando anúncios…
              </p>
            )}
            {listings.isSuccess && items.length === 0 && !importing && (
              <EmptyState
                message="Nenhum anúncio encontrado para esses filtros."
                action={
                  hasFilters ? (
                    <Button variant="outline" onClick={clearFilters}>
                      Limpar filtros
                    </Button>
                  ) : null
                }
              />
            )}
            {items.length > 0 && <ListingsList listings={items} nicknames={nicknames} />}
            {listings.hasNextPage && (
              <Button
                variant="outline"
                onClick={() => void listings.fetchNextPage()}
                disabled={listings.isFetchingNextPage}
                className="mt-4 w-full"
              >
                Carregar mais
              </Button>
            )}
          </div>
        </>
      )}
    </>
  );
}

function Filters({
  search,
  status,
  accounts,
  onStatus,
  onAccount,
}: {
  search: ListingsSearch;
  status: ListingStatusFilter;
  accounts: { accountId: string; nickname: string }[];
  onStatus: (value: ListingStatusFilter) => void;
  onAccount: (value: string) => void;
}) {
  const searchId = useId();
  const accountId = useId();

  return (
    <div className="mt-6 flex flex-col gap-4 rounded-2xl border border-line-soft bg-surface p-4 lg:p-5">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-end">
        <div className="flex flex-1 flex-col gap-2">
          <label htmlFor={searchId} className="text-sm font-semibold text-ink">
            Buscar por título ou código
          </label>
          <SearchInput id={searchId} value={search.q ?? ""} />
        </div>
        {accounts.length >= 2 && (
          <div className="flex flex-col gap-2 lg:w-72">
            <label htmlFor={accountId} className="text-sm font-semibold text-ink">
              Conta
            </label>
            <select
              id={accountId}
              value={search.conta ?? ""}
              onChange={(event) => onAccount(event.target.value)}
              className="min-h-12 w-full rounded-xl border border-line bg-surface px-4 text-base text-ink focus-visible:outline-3 focus-visible:outline-offset-1 focus-visible:outline-primary"
            >
              <option value="">Todas as contas</option>
              {accounts.map((account) => (
                <option key={account.accountId} value={account.accountId}>
                  {account.nickname}
                </option>
              ))}
            </select>
          </div>
        )}
      </div>
      {/* biome-ignore lint/a11y/useSemanticElements: grupo de botões de filtro (aria-pressed), não um fieldset de formulário. */}
      <div role="group" aria-label="Estado dos anúncios" className="flex flex-wrap gap-2">
        {CHIPS.map((chip) => {
          const pressed = chip.value === status;
          return (
            <button
              key={chip.value}
              type="button"
              aria-pressed={pressed}
              onClick={() => onStatus(chip.value)}
              className={`min-h-11 rounded-full px-4 text-sm font-semibold focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-primary ${
                pressed
                  ? "bg-primary text-white"
                  : "border border-line bg-surface text-ink hover:bg-primary-soft"
              }`}
            >
              {chip.label}
            </button>
          );
        })}
      </div>
    </div>
  );
}

/**
 * Campo de busca: o texto fica local e vai para a URL (e para a consulta) 300 ms depois da última tecla; se a URL
 * mudar por fora (link, "Limpar filtros", voltar), o campo acompanha.
 */
function SearchInput({ id, value }: { id: string; value: string }) {
  const navigate = useNavigate({ from: "/anuncios" });
  const [text, setText] = useState(value);
  const pushed = useRef(value);

  useEffect(() => {
    if (value !== pushed.current) {
      pushed.current = value;
      setText(value);
    }
  }, [value]);

  useEffect(() => {
    const next = text.trim();
    if (next === pushed.current) {
      return;
    }
    const timer = window.setTimeout(() => {
      pushed.current = next;
      void navigate({
        search: (prev) => ({ ...prev, q: next || undefined }),
        replace: true,
      });
    }, SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [text, navigate]);

  return (
    <div className="relative">
      <SearchIcon className="pointer-events-none absolute inset-y-0 left-4 my-auto size-5 text-muted" />
      <input
        id={id}
        type="search"
        value={text}
        onChange={(event) => setText(event.target.value)}
        placeholder="Ex.: Fone Bluetooth, MLB123456789"
        autoComplete="off"
        maxLength={100}
        className="min-h-12 w-full rounded-xl border border-line bg-surface pr-4 pl-12 text-base text-ink placeholder:text-muted focus-visible:outline-3 focus-visible:outline-offset-1 focus-visible:outline-primary"
      />
    </div>
  );
}

function SyncButton() {
  const queryClient = useQueryClient();
  const [syncing, setSyncing] = useState(false);
  const [message, setMessage] = useState<{ text: string; error: boolean }>();

  async function sync() {
    setSyncing(true);
    setMessage(undefined);
    try {
      // keepalive: o pedido segue mesmo se o usuário sair da tela logo depois de clicar.
      const response = await syncListings({}, { keepalive: true });
      if (response.status === 202) {
        setMessage({ text: "Sincronização iniciada.", error: false });
        await queryClient.invalidateQueries({ queryKey: summaryQuery.queryKey });
      } else {
        setMessage({
          text: syncErrorMessage(response.status, response.data as Problem, response.headers),
          error: true,
        });
      }
    } catch {
      setMessage({
        text: "Não foi possível falar com o Vitrine. Verifique sua conexão e tente de novo.",
        error: true,
      });
    } finally {
      setSyncing(false);
    }
  }

  return (
    <div className="flex flex-col items-end gap-2">
      <Button variant="outline" onClick={sync} disabled={syncing}>
        <SyncIcon className="size-5" />
        Sincronizar agora
      </Button>
      <p role="status" className="max-w-xs text-right text-sm text-muted">
        {message && !message.error ? message.text : ""}
      </p>
      {message?.error && (
        <p role="alert" className="max-w-xs text-right text-sm text-danger">
          {message.text}
        </p>
      )}
    </div>
  );
}

function EmptyState({ message, action }: { message: string; action: ReactNode }) {
  return (
    <div className="mt-6 flex flex-col items-center gap-4 rounded-2xl border border-line-soft bg-surface px-6 py-10 text-center">
      <p className="max-w-md text-muted">{message}</p>
      {action}
    </div>
  );
}
