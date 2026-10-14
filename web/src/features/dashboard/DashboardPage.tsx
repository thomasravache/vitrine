import { useQueryClient, useSuspenseQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useId, useState } from "react";
import { logout } from "../../api/generated/vitrine.ts";
import { meQuery } from "../../api/session.ts";
import { Button } from "../../ui/Button.tsx";
import { ChatIcon, LockIcon, SparklesIcon, StoreIcon } from "../../ui/icons.tsx";
import { useDocumentTitle } from "../../ui/useDocumentTitle.ts";
import { AppShell } from "./AppShell.tsx";

/** Painel vazio: saudação, convite para conectar a conta do Mercado Livre (em breve) e o que o Vitrine fará. */
export function DashboardPage() {
  useDocumentTitle("Painel");
  const { data: me } = useSuspenseQuery(meQuery);
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [signingOut, setSigningOut] = useState(false);
  const comingSoonId = useId();

  if (!me) {
    return null; // a rota redireciona para /entrar antes de chegar aqui
  }

  async function signOut() {
    setSigningOut(true);
    try {
      await logout();
    } finally {
      queryClient.setQueryData(meQuery.queryKey, null);
      await navigate({ to: "/entrar" });
    }
  }

  const firstName = me.name.trim().split(/\s+/)[0] ?? me.name;

  return (
    <AppShell me={me} onSignOut={signOut} signingOut={signingOut}>
      <h1 className="text-4xl font-bold tracking-tight">Olá, {firstName}</h1>

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
        <Button disabled aria-describedby={comingSoonId} className="mt-8 w-full max-w-sm">
          Conectar conta do Mercado Livre
        </Button>
        <p
          id={comingSoonId}
          className="mt-3 inline-flex items-center gap-1.5 rounded-full bg-primary-soft px-3 py-1 text-xs font-semibold tracking-wide text-muted uppercase"
        >
          <LockIcon className="size-3.5" />
          Em breve
        </p>
      </section>

      <div className="mt-6 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:gap-6">
        <Feature icon={<SparklesIcon className="size-6" />} title="Anúncios melhores">
          Sugestões claras de títulos, fotos e atributos para aumentar suas vendas.
        </Feature>
        <Feature icon={<ChatIcon className="size-6" />} title="Respostas em 2 min">
          Responda compradores com rapidez e nunca perca uma venda por demora.
        </Feature>
      </div>
    </AppShell>
  );
}

function Feature({
  icon,
  title,
  children,
}: {
  icon: React.ReactNode;
  title: string;
  children: React.ReactNode;
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
