import { useSuspenseQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import type { ReactNode } from "react";
import { meQuery } from "../../api/session.ts";
import { ErrorAlert } from "../../ui/Alert.tsx";
import { Button, buttonClass } from "../../ui/Button.tsx";
import { listingsCount, timeAgo } from "../../ui/format.ts";
import { ImportingNotice } from "../../ui/ImportingNotice.tsx";
import {
  ArrowRightIcon,
  ChatIcon,
  ListingsIcon,
  SparklesIcon,
  StoreIcon,
  WarningIcon,
} from "../../ui/icons.tsx";
import { useDocumentTitle } from "../../ui/useDocumentTitle.ts";
import { ACCOUNT_STATUS, MEMBER_NOTE, useIsOwner } from "../accounts/queries.ts";
import { useStartConnection } from "../accounts/useStartConnection.ts";
import { lastSyncedAt, useAccountsOverview } from "../listings/queries.ts";

/** Painel: saudação, o cartão das contas do Mercado Livre (conectar, importando ou resumo) e o que o Vitrine fará. */
export function DashboardPage() {
  useDocumentTitle("Painel");
  const { data: me } = useSuspenseQuery(meQuery);
  const firstName = me?.name.trim().split(/\s+/)[0] ?? "";

  return (
    <>
      <h1 className="text-4xl font-bold tracking-tight">Olá, {firstName}</h1>
      <AccountsCard />

      <div className="mt-6 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:gap-6">
        <Feature icon={<SparklesIcon className="size-6" />} title="Anúncios melhores">
          Sugestões de título, descrição e ficha técnica para seus anúncios aparecerem mais na
          busca.
        </Feature>
        <Feature
          icon={<ChatIcon className="size-6" />}
          title="Perguntas respondidas em até 2 minutos"
        >
          Respostas rápidas, a qualquer hora, sem inventar o que não está no anúncio.
        </Feature>
      </div>
    </>
  );
}

function AccountsCard() {
  const { accounts, summary, importing } = useAccountsOverview();

  if (accounts.isError || summary.isError) {
    return (
      <div className="mt-8">
        <ErrorAlert>
          Não foi possível carregar suas contas agora. Recarregue a página para tentar de novo.
        </ErrorAlert>
      </div>
    );
  }
  if (accounts.isPending) {
    return (
      <p role="status" className="mt-8 text-muted">
        Carregando suas contas…
      </p>
    );
  }
  if (accounts.data.length === 0) {
    return <ConnectCard />;
  }

  const needReconnect = accounts.data.filter(
    (account) => account.status === ACCOUNT_STATUS.reauthorizationRequired,
  );
  const synced = lastSyncedAt(summary.data);

  return (
    <div className="mt-8 flex flex-col gap-4">
      {needReconnect.map((account) => (
        <div
          key={account.accountId}
          className="flex flex-col gap-3 rounded-2xl border border-pending/40 bg-amber-50 px-5 py-4 sm:flex-row sm:items-center sm:justify-between"
        >
          <p className="flex items-start gap-3 font-semibold text-amber-800">
            <WarningIcon className="mt-0.5 size-5 shrink-0" />
            <span>A conta {account.nickname} precisa ser reconectada</span>
          </p>
          <Link
            to="/contas"
            className="font-semibold text-amber-800 underline underline-offset-2 focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-primary"
          >
            Ir para Contas
          </Link>
        </div>
      ))}

      <section
        aria-labelledby="listings-title"
        className="rounded-3xl border border-line-soft bg-surface px-6 py-8 shadow-sm"
      >
        <div className="flex items-start gap-4">
          <span className="flex size-12 shrink-0 items-center justify-center rounded-2xl bg-primary-soft text-primary">
            <ListingsIcon className="size-6" />
          </span>
          <div className="min-w-0 flex-1">
            <h2 id="listings-title" className="text-xl font-bold">
              Seus anúncios
            </h2>
            {importing ? (
              <ImportingNotice className="mt-3" />
            ) : (
              <p className="mt-1 text-muted">
                {listingsCount(summary.data?.total ?? 0)}
                {synced ? ` · sincronizado ${timeAgo(synced)}` : ""}
              </p>
            )}
          </div>
        </div>
        <Link to="/anuncios" className={`${buttonClass("outline")} mt-6 w-full sm:w-auto`}>
          Ver anúncios
          <ArrowRightIcon className="size-5" />
        </Link>
      </section>
    </div>
  );
}

function ConnectCard() {
  const isOwner = useIsOwner();
  const { start, starting, error } = useStartConnection();

  return (
    <section
      aria-labelledby="connect-title"
      className="mt-8 flex flex-col items-center rounded-3xl border border-line-soft bg-surface px-6 py-10 text-center shadow-sm lg:py-14"
    >
      <span className="flex size-16 items-center justify-center rounded-2xl border border-line-soft bg-canvas text-primary">
        <StoreIcon className="size-8" />
      </span>
      <h2 id="connect-title" className="mt-6 text-2xl font-bold">
        Conecte sua conta do Mercado Livre
      </h2>
      <p className="mt-3 max-w-lg text-muted">
        Assim que conectar, mostramos seus anúncios com sugestões de melhoria e respondemos as
        perguntas dos compradores.
      </p>
      {isOwner ? (
        <>
          {error && (
            <div className="mt-6 w-full max-w-sm text-left">
              <ErrorAlert>{error.message}</ErrorAlert>
            </div>
          )}
          <Button onClick={start} disabled={starting} className="mt-8 w-full max-w-sm">
            Conectar conta
          </Button>
        </>
      ) : (
        <p className="mt-6 text-sm text-muted">{MEMBER_NOTE}</p>
      )}
    </section>
  );
}

function Feature({
  icon,
  title,
  children,
}: {
  icon: ReactNode;
  title: string;
  children: ReactNode;
}) {
  return (
    <section className="rounded-3xl border border-line-soft bg-surface p-6">
      <span className="flex size-11 items-center justify-center rounded-xl bg-primary-soft text-primary">
        {icon}
      </span>
      <h3 className="mt-4 text-lg font-bold">{title}</h3>
      <p className="mt-2 text-muted">{children}</p>
    </section>
  );
}
