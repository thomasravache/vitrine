import * as Sentry from "@sentry/react";

/**
 * Sentry da SPA (SPEC-0007, ADR-0009). Opcional: só inicializa com `VITE_SENTRY_DSN` definido no build (secret do
 * Environment); sem ele — ambiente local, testes, E2E — nada é carregado nem enviado.
 * Release = SHA do build (`VITE_SENTRY_RELEASE`); PII desligada (ADR-0006). No SDK 11 o antigo
 * `sendDefaultPii: false` virou `dataCollection`: nada de dados do usuário, cookies, cabeçalhos, corpos, query string
 * nem variáveis locais.
 */
export function initSentry(env: ImportMetaEnv = import.meta.env): boolean {
  const dsn = env.VITE_SENTRY_DSN?.trim();
  if (!dsn) {
    return false;
  }

  const release = env.VITE_SENTRY_RELEASE?.trim();
  Sentry.init({
    dsn,
    environment: env.VITE_SENTRY_ENVIRONMENT?.trim() || env.MODE,
    ...(release ? { release } : {}),
    dataCollection: {
      userInfo: false,
      cookies: false,
      httpHeaders: false,
      httpBodies: [],
      urlQueryParams: false,
      stackFrameVariables: false,
    },
    // Só erros: sem tracing nem replay no plano gratuito (ADR-0009).
    tracesSampleRate: 0,
    beforeSend: (event) => {
      // Nada de cookies, cabeçalhos nem corpo da requisição (ADR-0006).
      if (event.request) {
        delete event.request.cookies;
        delete event.request.headers;
        delete event.request.data;
      }
      return event;
    },
  });
  return true;
}
