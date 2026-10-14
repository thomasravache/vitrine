import { Link } from "@tanstack/react-router";
import type { ReactNode } from "react";

type Props = {
  children: ReactNode;
  /** Rodapé com o atalho para a outra tela de acesso. */
  footer: ReactNode;
};

/**
 * Telas de acesso: no celular, marca no topo e formulário em tela cheia; a partir de 1024px, um cartão centralizado
 * com a marca dentro (Stitch: Criar Conta, Entrar).
 */
export function AuthLayout({ children, footer }: Props) {
  return (
    <div className="flex min-h-svh flex-col lg:items-center lg:justify-center lg:px-6 lg:py-12">
      <header className="flex justify-center px-4 pt-5 pb-2 lg:hidden">
        <Link
          to="/"
          className="rounded-md font-display text-2xl font-bold text-primary focus-visible:outline-3 focus-visible:outline-primary"
        >
          Vitrine
        </Link>
      </header>
      <main className="w-full flex-1 px-5 pt-6 pb-10 lg:max-w-md lg:flex-none lg:rounded-2xl lg:border lg:border-line-soft lg:bg-surface lg:px-11 lg:py-10 lg:shadow-sm">
        <Link
          to="/"
          className="mb-8 hidden rounded-md font-display text-2xl font-bold text-primary focus-visible:outline-3 focus-visible:outline-primary lg:inline-block"
        >
          Vitrine
        </Link>
        {children}
      </main>
      <footer className="border-t border-line-soft px-4 py-5 text-center text-muted lg:border-0 lg:pt-6">
        {footer}
      </footer>
    </div>
  );
}

export const footerLinkClass =
  "ml-1 rounded font-semibold text-primary underline underline-offset-2 hover:text-primary-hover focus-visible:outline-3 focus-visible:outline-primary";
