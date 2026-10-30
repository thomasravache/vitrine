import { Link, useRouterState } from "@tanstack/react-router";
import type { ReactNode } from "react";
import type { MeResponse } from "../../api/generated/vitrine.ts";
import { Button } from "../../ui/Button.tsx";
import {
  AccountsIcon,
  DashboardIcon,
  ListingsIcon,
  LogoutIcon,
  StoreIcon,
} from "../../ui/icons.tsx";

type Props = {
  me: MeResponse;
  onSignOut: () => void;
  signingOut: boolean;
  children: ReactNode;
};

const sections = [
  { label: "Painel", to: "/painel", icon: DashboardIcon },
  { label: "Anúncios", to: "/anuncios", icon: ListingsIcon },
  { label: "Contas", to: "/contas", icon: AccountsIcon },
] as const;

type Section = (typeof sections)[number];

function currentSection(pathname: string): Section | undefined {
  return sections.find(({ to }) => pathname === to || pathname.startsWith(`${to}/`));
}

/**
 * Moldura das telas autenticadas: no celular, cabeçalho + navegação inferior (Painel, Anúncios, Contas); a partir de
 * 1024px, menu lateral com as mesmas seções e "Sair".
 */
export function AppShell({ me, onSignOut, signingOut, children }: Props) {
  const initial = me.name.trim().charAt(0).toUpperCase();
  const pathname = useRouterState({ select: (state) => state.location.pathname });
  const current = currentSection(pathname);

  return (
    <div className="min-h-svh lg:flex">
      <aside className="sticky top-0 hidden h-svh w-64 shrink-0 flex-col border-r border-line-soft bg-canvas px-5 py-6 lg:flex">
        <p className="flex items-center gap-3 px-1">
          <span className="flex size-11 items-center justify-center rounded-xl bg-primary text-white">
            <StoreIcon className="size-6" />
          </span>
          <span className="font-display text-xl font-bold text-primary">Vitrine</span>
        </p>
        <nav aria-label="Principal" className="mt-8">
          <SectionList variant="sidebar" current={current} />
        </nav>
        <div className="mt-auto border-t border-line-soft pt-4">
          <Button
            variant="ghost"
            onClick={onSignOut}
            disabled={signingOut}
            className="w-full justify-start gap-3 px-4 font-sans font-medium"
          >
            <LogoutIcon className="size-6" />
            Sair
          </Button>
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-10 flex h-16 items-center justify-between gap-4 border-b border-line-soft bg-surface px-4 lg:h-[4.5rem] lg:px-8">
          <p className="font-display text-2xl font-bold text-primary lg:hidden">Vitrine</p>
          <p className="hidden font-display text-lg font-semibold lg:block">
            {current?.label ?? ""}
          </p>
          <div className="flex items-center gap-3">
            <p className="hidden text-sm font-semibold lg:block">{me.name}</p>
            <Button
              variant="ghost"
              onClick={onSignOut}
              disabled={signingOut}
              className="min-h-11 px-3 text-sm lg:hidden"
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
        <SectionList variant="bottom" current={current} />
      </nav>
    </div>
  );
}

function SectionList({
  variant,
  current,
}: {
  variant: "sidebar" | "bottom";
  current: Section | undefined;
}) {
  const list = variant === "sidebar" ? "flex flex-col gap-1" : "grid grid-cols-3";
  const item =
    variant === "sidebar"
      ? "flex min-h-12 items-center gap-3 rounded-xl px-4 font-medium"
      : "flex min-h-16 flex-col items-center justify-center gap-1 text-xs font-medium";
  const active = variant === "sidebar" ? "bg-primary text-white" : "text-primary font-semibold";
  const inactive = variant === "sidebar" ? "text-ink hover:bg-primary-soft" : "text-muted";

  return (
    <ul className={list}>
      {sections.map((section) => {
        const isCurrent = section === current;
        const { label, to, icon: SectionIcon } = section;
        return (
          <li key={label}>
            <Link
              to={to}
              aria-current={isCurrent ? "page" : undefined}
              className={`${item} ${isCurrent ? active : inactive} focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-primary`}
            >
              <SectionIcon className="size-6" />
              {label}
            </Link>
          </li>
        );
      })}
    </ul>
  );
}
