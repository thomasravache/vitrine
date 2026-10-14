import { useEffect } from "react";

/** Título da aba por tela ("Entrar · Vitrine"): leitores de tela anunciam a troca de página. */
export function useDocumentTitle(title: string): void {
  useEffect(() => {
    document.title = `${title} · Vitrine`;
  }, [title]);
}
