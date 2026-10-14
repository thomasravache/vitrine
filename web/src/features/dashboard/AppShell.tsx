import type { ReactNode } from "react";
import type { MeResponse } from "../../api/generated/vitrine.ts";
import { Button } from "../../ui/Button.tsx";
import { ChatIcon, DashboardIcon, ListingsIcon, StoreIcon } from "../../ui/icons.tsx";

type Props = {
  me: MeResponse;
  onSignOut: () => void;
  signingOut: boolean;
  children: ReactNode;
};

const sections = [
  { label: "Painel", icon: DashboardIcon, current: true },
  { label: "Anúncios", icon: ListingsIcon, current: false },
  { label: "Perguntas", icon: ChatIcon, current: false },
] as const;

/**
 * Moldura das telas autenticadas: no celular, cabeçalho + navegação inferior; a partir de 1024px, menu lateral.
 * Só o Painel existe por enquanto; Anúncios e Perguntas aparecem desabilitados ("em breve").
 */
export function AppShell({ me, onSignOut, signingOut, children }: Props) {
  const initial = me.name.trim().charAt(0).toUpperCase();

  return (
    <div className="min-h-svh lg:flex">
      <aside className="hidden w-64 shrink-0 flex-col border-r border-line-soft bg-canvas px-5 py-6 lg:flex">
        <p className="flex items-center gap-3 px-1">
          <span className="flex size-11 items-center justify-center rounded-xl bg-primary text-white">
            <StoreIcon className="size-6" />
          </span>
          <span className="font-display text-xl font-bold text-primary">Vitrine</span>
        </p>
        <nav aria-label="Principal" className="mt-8">
          <SectionList variant="sidebar" />
        </nav>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-10 flex h-16 items-center justify-between gap-4 border-b border-line-soft bg-surface px-4 lg:h-[4.5rem] lg:px-8">
          <p className="font-display text-2xl font-bold text-primary lg:hidden">Vitrine</p>
          <p className="hidden font-display text-lg font-semibold lg:block">Painel</p>
          <div className="flex items-center gap-3">
            <p className="hidden text-right leading-tight lg:block">
              <span className="block text-sm font-semibold">{me.name}</span>
              <span className="block text-xs text-muted">{me.workspace.name}</span>
            </p>
            <Button
              variant="ghost"
              onClick={onSignOut}
              disabled={signingOut}
              className="min-h-11 px-3 text-sm"
            >
              Sair
            </Button>
            <span
              aria-hidden="true"
              className="flex size-10 items-center justify-center rounded-full bg-primary-soft font-display font-bold text-primary lg:order-first"
            >
              {initial}
            </span>
          </div>
        </header>

        <main className="mx-auto w-full max-w-[1120px] flex-1 px-4 pt-8 pb-28 lg:px-8 lg:pb-12">
          {children}
        </main>
      </div>

      <nav
        aria-label="Principal"
        className="fixed inset-x-0 bottom-0 z-10 border-t border-line-soft bg-surface pb-[env(safe-area-inset-bottom)] lg:hidden"
      >
        <SectionList variant="bottom" />
      </nav>
    </div>
  );
}

function SectionList({ variant }: { variant: "sidebar" | "bottom" }) {
  const list = variant === "sidebar" ? "flex flex-col gap-1" : "grid grid-cols-3";
  const item =
    variant === "sidebar"
      ? "flex min-h-12 items-center gap-3 rounded-xl px-4 font-medium"
      : "flex min-h-16 flex-col items-center justify-center gap-1 text-xs font-medium";

  return (
    <ul className={list}>
      {sections.map(({ label, icon: SectionIcon, current }) => (
        <li key={label}>
          {current ? (
            <a
              href="/painel"
              aria-current="page"
              className={`${item} text-primary focus-visible:outline-3 focus-visible:outline-primary ${
                variant === "sidebar" ? "bg-primary-soft" : ""
              }`}
            >
              <SectionIcon className="size-6" />
              {label}
            </a>
          ) : (
            // Seção que ainda não existe: botão desabilitado (leitores de tela anunciam "indisponível").
            <button
              type="button"
              disabled
              className={`${item} w-full cursor-not-allowed text-muted`}
            >
              <SectionIcon className="size-6" />
              {label}
            </button>
          )}
        </li>
      ))}
    </ul>
  );
}
