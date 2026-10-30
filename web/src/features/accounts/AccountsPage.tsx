import { useQueryClient } from "@tanstack/react-query";
import { useNavigate, useRouter } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import {
  disconnectMercadoLivreAccount,
  type ListingsAccountSummaryResponse,
  type MercadoLivreAccountResponse,
} from "../../api/generated/vitrine.ts";
import { ErrorAlert } from "../../ui/Alert.tsx";
import { Badge } from "../../ui/Badge.tsx";
import { Button } from "../../ui/Button.tsx";
import { ConfirmDialog } from "../../ui/ConfirmDialog.tsx";
import { timeAgo } from "../../ui/format.ts";
import { CheckCircleIcon, ListingsIcon, LockIcon, SyncIcon, WarningIcon } from "../../ui/icons.tsx";
import { useDocumentTitle } from "../../ui/useDocumentTitle.ts";
import { isAccountImporting, useAccountsOverview } from "../listings/queries.ts";
import { ACCOUNT_STATUS, accountsQuery, MEMBER_NOTE, useIsOwner } from "./queries.ts";
import { useStartConnection } from "./useStartConnection.ts";

/** Contas do Mercado Livre: conectar, reconectar e desconectar (Owner); estado de cada conta e da importação. */
export function AccountsPage() {
  useDocumentTitle("Contas do Mercado Livre");
  const isOwner = useIsOwner();
  const router = useRouter();
  const navigate = useNavigate();
  // O aviso vem do retorno do OAuth no history.state: é lido uma vez e tirado do histórico, para não voltar num F5.
  const [connected] = useState(() => router.state.location.state.connectedNickname);
  useEffect(() => {
    if (connected) {
      void navigate({ to: "/contas", replace: true, state: {} });
    }
  }, [connected, navigate]);
  const { accounts, summaryByAccount } = useAccountsOverview();
  const { start, starting, error } = useStartConnection();
  const [toDisconnect, setToDisconnect] = useState<{
    account: MercadoLivreAccountResponse;
    trigger: HTMLElement;
  }>();

  return (
    <div className="mx-auto max-w-[720px]">
      <h1 className="text-3xl font-bold tracking-tight lg:text-4xl">Contas do Mercado Livre</h1>
      <p className="mt-3 text-lg text-muted">
        Conecte as contas da sua loja para o Vitrine ler seus anúncios. Você pode desconectar quando
        quiser.
      </p>

      {connected && (
        <p
          role="status"
          className="mt-6 flex items-start gap-3 rounded-xl border border-primary/30 bg-primary-soft px-4 py-3 text-sm font-semibold text-primary"
        >
          <CheckCircleIcon className="mt-0.5 size-5 shrink-0" />
          <span>Conta {connected} conectada. Estamos importando seus anúncios.</span>
        </p>
      )}

      {error && (
        <div className="mt-6">
          <ErrorAlert>{error.message}</ErrorAlert>
        </div>
      )}

      {accounts.isError && (
        <div className="mt-6">
          <ErrorAlert>
            Não foi possível carregar suas contas agora. Recarregue a página para tentar de novo.
          </ErrorAlert>
        </div>
      )}
      {accounts.isPending && (
        <p role="status" className="mt-8 text-muted">
          Carregando suas contas…
        </p>
      )}

      {accounts.data && accounts.data.length > 0 && (
        <ul className="mt-8 flex flex-col gap-4">
          {accounts.data.map((account) => (
            <li key={account.accountId}>
              <AccountCard
                account={account}
                summary={summaryByAccount.get(account.accountId)}
                isOwner={isOwner}
                reconnecting={starting}
                onReconnect={start}
                onDisconnect={(trigger) => setToDisconnect({ account, trigger })}
              />
            </li>
          ))}
        </ul>
      )}

      {isOwner ? (
        <div className="mt-10 flex flex-col items-center gap-3">
          <Button onClick={start} disabled={starting} className="w-full">
            Conectar conta do Mercado Livre
          </Button>
          <p className="flex items-start gap-2 text-center text-sm text-muted">
            <LockIcon className="mt-0.5 size-4 shrink-0" />
            Você será levado ao Mercado Livre para autorizar. O Vitrine nunca vê sua senha.
          </p>
        </div>
      ) : (
        <p className="mt-10 flex items-start gap-2 text-sm text-muted">
          <LockIcon className="mt-0.5 size-4 shrink-0" />
          <span>{MEMBER_NOTE}</span>
        </p>
      )}

      {toDisconnect && (
        <DisconnectDialog
          account={toDisconnect.account}
          trigger={toDisconnect.trigger}
          onClose={() => setToDisconnect(undefined)}
        />
      )}
    </div>
  );
}

function AccountCard({
  account,
  summary,
  isOwner,
  reconnecting,
  onReconnect,
  onDisconnect,
}: {
  account: MercadoLivreAccountResponse;
  summary: ListingsAccountSummaryResponse | undefined;
  isOwner: boolean;
  reconnecting: boolean;
  onReconnect: () => void;
  onDisconnect: (trigger: HTMLElement) => void;
}) {
  const needsReconnect = account.status === ACCOUNT_STATUS.reauthorizationRequired;

  return (
    <article
      data-testid="account-card"
      aria-label={account.nickname}
      className={`rounded-3xl border bg-surface p-6 ${needsReconnect ? "border-pending/50" : "border-line-soft"}`}
    >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-xs font-semibold tracking-wide text-muted uppercase">Apelido</p>
          <p className="mt-1 truncate font-display text-xl font-bold">{account.nickname}</p>
        </div>
        {needsReconnect ? (
          <Badge tone="pending">
            <WarningIcon className="size-4" />
            Reconectar necessário
          </Badge>
        ) : (
          <Badge tone="primary" dot>
            Conectada
          </Badge>
        )}
      </div>

      {needsReconnect && (
        <p className="mt-4 rounded-r-lg border-l-4 border-amber-800 bg-amber-50 px-4 py-3 text-sm text-amber-800">
          O acesso expirou. Conecte de novo para continuar.
        </p>
      )}

      <p className="mt-4 flex items-center gap-3 rounded-xl bg-canvas px-4 py-3 text-sm text-muted">
        <ListingsIcon className="size-5 shrink-0" />
        <span>{syncLine(account, summary)}</span>
      </p>

      {isOwner && (
        <div className="mt-4 flex flex-wrap items-center justify-between gap-3">
          <Button
            variant="ghost"
            onClick={(event) => onDisconnect(event.currentTarget)}
            aria-label={`Desconectar ${account.nickname}`}
            className="px-3 text-muted"
          >
            Desconectar
          </Button>
          {needsReconnect && (
            <Button
              variant="outline"
              onClick={onReconnect}
              disabled={reconnecting}
              aria-label={`Reconectar ${account.nickname}`}
            >
              <SyncIcon className="size-5" />
              Reconectar
            </Button>
          )}
        </div>
      )}
    </article>
  );
}

/** "Anúncios: {n} · sincronizado há {tempo}" (ou importando, pausada ou falha), num único texto. */
function syncLine(
  account: MercadoLivreAccountResponse,
  summary: ListingsAccountSummaryResponse | undefined,
): string {
  const count = `Anúncios: ${summary?.itemCount ?? 0}`;
  if (isAccountImporting(account, summary)) {
    return `${count} · Importando…`;
  }
  if (
    account.status === ACCOUNT_STATUS.reauthorizationRequired ||
    summary?.syncStatus === "paused"
  ) {
    return `${count} · sincronização pausada até reconectar`;
  }
  if (summary?.syncStatus === "failed") {
    return summary.lastSyncedAt
      ? `${count} · Última sincronização falhou (a anterior foi ${timeAgo(summary.lastSyncedAt)})`
      : `${count} · Última sincronização falhou`;
  }
  return summary?.lastSyncedAt ? `${count} · sincronizado ${timeAgo(summary.lastSyncedAt)}` : count;
}

function DisconnectDialog({
  account,
  trigger,
  onClose,
}: {
  account: MercadoLivreAccountResponse;
  trigger: HTMLElement;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  async function confirm() {
    setBusy(true);
    setError(undefined);
    try {
      const response = await disconnectMercadoLivreAccount(account.accountId);
      if (response.status === 204 || response.status === 404) {
        await Promise.all([
          queryClient.invalidateQueries({ queryKey: accountsQuery.queryKey }),
          queryClient.invalidateQueries({ queryKey: ["listings"] }),
        ]);
        onClose();
        return;
      }
      setError(
        response.status === 403
          ? MEMBER_NOTE
          : "Não foi possível desconectar a conta agora. Tente de novo.",
      );
    } catch {
      setError("Não foi possível falar com o Vitrine. Verifique sua conexão e tente de novo.");
    }
    setBusy(false);
  }

  return (
    <ConfirmDialog
      title={`Desconectar ${account.nickname}?`}
      description="Os anúncios dessa conta saem do Vitrine. Você pode conectar de novo depois."
      confirmLabel="Desconectar"
      busy={busy}
      error={error}
      returnFocusTo={trigger}
      onConfirm={confirm}
      onCancel={onClose}
    />
  );
}
