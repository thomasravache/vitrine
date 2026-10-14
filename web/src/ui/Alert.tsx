import type { ReactNode } from "react";
import { AlertIcon } from "./icons.tsx";

/** Erro geral de formulário (vermelho só para erros), anunciado por leitores de tela. */
export function ErrorAlert({ children }: { children: ReactNode }) {
  return (
    <div
      role="alert"
      className="flex items-start gap-3 rounded-xl border border-danger/30 bg-danger-soft px-4 py-3 text-sm text-danger"
    >
      <AlertIcon className="mt-0.5 size-5 shrink-0" />
      <p>{children}</p>
    </div>
  );
}
