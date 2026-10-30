import { useEffect, useState } from "react";
import { startMercadoLivreConnection } from "../../api/generated/vitrine.ts";
import type { Problem } from "../../api/session.ts";
import {
  connectionError,
  type ErrorView,
  NETWORK_ERROR,
  UNSAFE_AUTHORIZATION_URL_ERROR,
} from "./errorMessages.ts";

/**
 * Só leva o navegador a uma URL de autorização https: (http: apenas em desenvolvimento, para o simulador local sem
 * TLS). O ambiente é lido na hora da chamada.
 */
export function isSafeAuthorizationUrl(raw: string): boolean {
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    return false;
  }
  return url.protocol === "https:" || (url.protocol === "http:" && import.meta.env.DEV === true);
}

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
        if (isSafeAuthorizationUrl(response.data.authorizationUrl)) {
          window.location.assign(response.data.authorizationUrl);
          return;
        }
        setError(UNSAFE_AUTHORIZATION_URL_ERROR);
      } else {
        setError(connectionError(response.data as Problem));
      }
    } catch {
      setError(NETWORK_ERROR);
    }
    setStarting(false);
  }

  return { start, starting, error };
}
