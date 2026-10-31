import * as Sentry from "@sentry/react";

/** Valor que substitui o dado sensível (mesma regra do backend — ADR-0006). */
export const MASK = "***";

// Trechos de nome (sem caixa nem separadores) que marcam um parâmetro/campo como sensível.
const SENSITIVE_NAME_FRAGMENTS = [
  "email",
  "password",
  "passwd",
  "senha",
  "secret",
  "token",
  "authorization",
  "cookie",
  "apikey",
  "credential",
];
const EMAIL_PATTERN = /[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}/g;
const CREDENTIAL_PATTERN = /\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*/gi;
const NAME_VALUE_PATTERN = /([A-Za-z0-9_.%\-[\]]+)=([^&\s#;,"']+)/g;
// Parâmetros do OAuth (SPEC-0030): mascarados pelo nome exato, para não pegar `status_code`, `postcode` etc.
const OAUTH_PARAM_NAMES = new Set(["code", "state"]);
// Volta do OAuth do ML (SPEC-0025), absoluta ou relativa: a query inteira some (SPEC-0030).
const OAUTH_RETURN_QUERY_PATTERN = /(\/contas\/mercado-livre\/retorno)\?[^\s#"']*/g;

export function isSensitiveName(name: string): boolean {
  const normalized = name.toLowerCase().replace(/[^\p{L}\p{N}]/gu, "");
  return SENSITIVE_NAME_FRAGMENTS.some((fragment) => normalized.includes(fragment));
}

function decode(name: string): string {
  try {
    return decodeURIComponent(name);
  } catch {
    return name;
  }
}

function isSensitiveParam(name: string): boolean {
  const decoded = decode(name);
  return OAUTH_PARAM_NAMES.has(decoded) || isSensitiveName(decoded);
}

/**
 * Mascara e-mails, credenciais Bearer/Basic e o valor de pares `nome=valor` de nome sensível (query, URL), inclusive
 * `code`/`state` do OAuth; a query da volta do OAuth do ML é removida inteira.
 */
export function maskText(text: string): string {
  return text
    .replace(OAUTH_RETURN_QUERY_PATTERN, "$1")
    .replace(NAME_VALUE_PATTERN, (pair, name: string) =>
      isSensitiveParam(name) ? `${name}=${MASK}` : pair,
    )
    .replace(EMAIL_PATTERN, MASK)
    .replace(CREDENTIAL_PATTERN, `$1 ${MASK}`);
}

/** Mascara qualquer valor (texto, lista, objeto); campos de nome sensível viram `***`. */
function maskValue(value: unknown, depth = 0): unknown {
  if (typeof value === "string") {
    return maskText(value);
  }
  if (depth > 5 || value === null || typeof value !== "object") {
    return value;
  }
  if (Array.isArray(value)) {
    return value.map((item) => maskValue(item, depth + 1));
  }
  return Object.fromEntries(
    Object.entries(value).map(([key, item]) => [
      key,
      isSensitiveName(key) && item != null ? MASK : maskValue(item, depth + 1),
    ]),
  );
}

/**
 * Sentry da SPA (SPEC-0007, ADR-0009). Opcional: só é inicializado com `VITE_SENTRY_DSN` definido no build (secret do
 * Environment); sem ele — ambiente local, testes, E2E — o SDK entra no bundle (import estático), mas não é
 * inicializado e nada é enviado.
 * Release = SHA do build (`VITE_SENTRY_RELEASE`); PII desligada (ADR-0006). No SDK 11 o antigo
 * `sendDefaultPii: false` virou `dataCollection`: nada de dados do usuário, cookies, cabeçalhos, corpos, query string
 * nem variáveis locais. Mensagens, exceções, URLs e breadcrumbs ainda passam pela máscara de e-mail, credenciais e
 * parâmetros sensíveis.
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
      if (event.message) {
        event.message = maskText(event.message);
      }
      for (const exception of event.exception?.values ?? []) {
        if (exception.value) {
          exception.value = maskText(exception.value);
        }
      }
      if (event.request) {
        // Nada de cookies, cabeçalhos, corpo nem query string (ADR-0006); a URL sai mascarada.
        delete event.request.cookies;
        delete event.request.headers;
        delete event.request.data;
        delete event.request.query_string;
        if (event.request.url) {
          event.request.url = maskText(event.request.url);
        }
      }
      return event;
    },
    // Breadcrumbs de console, fetch/xhr e navegação: mensagem e dados (url, from/to, argumentos) mascarados.
    beforeBreadcrumb: (breadcrumb) => {
      if (breadcrumb.message) {
        breadcrumb.message = maskText(breadcrumb.message);
      }
      if (breadcrumb.data) {
        breadcrumb.data = maskValue(breadcrumb.data) as typeof breadcrumb.data;
      }
      return breadcrumb;
    },
  });
  return true;
}
