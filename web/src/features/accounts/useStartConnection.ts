import { useEffect, useState } from "react";
import { startMercadoLivreConnection } from "../../api/generated/vitrine.ts";
import type { Problem } from "../../api/session.ts";
import { connectionError, type ErrorView, NETWORK_ERROR } from "./errorMessages.ts";

/**
 * Conectar ou reconectar uma conta: pede a URL de autorização (POST /mercadolivre/connections) e leva o navegador ao
 * Mercado Livre. Em erro, devolve a mensagem para a tela.
 */
export function useStartConnection() {
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<ErrorView>();

  // Voltando do ML pelo botão voltar (página restaurada do cache), o botão volta a ficar disponível.
  useEffect(() => {
    const reset = (event: PageTransitionEvent) => {
      if (event.persisted) {
        setStarting(false);
      }
    };
    window.addEventListener("pageshow", reset);
    return () => window.removeEventListener("pageshow", reset);
  }, []);

  async function start() {
    setStarting(true);
    setError(undefined);
    try {
      const response = await startMercadoLivreConnection();
      if (response.status === 200) {
        window.location.assign(response.data.authorizationUrl);
        return;
      }
      setError(connectionError(response.data as Problem));
    } catch {
      setError(NETWORK_ERROR);
    }
    setStarting(false);
  }

  return { start, starting, error };
}
