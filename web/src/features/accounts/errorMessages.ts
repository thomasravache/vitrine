import type { Problem } from "../../api/session.ts";

/** Mensagem de erro de uma tela, com ou sem a ação "Tentar de novo". */
export type ErrorView = { message: string; canRetry: boolean };

const EXPIRED: ErrorView = {
  message: "O link de autorização expirou ou já foi usado. Tente conectar de novo.",
  canRetry: true,
};

// Códigos dos contratos SPEC-0020@1 (conexão) → textos da seção 6 da SPEC-0025.
const CONNECTION_ERRORS: Record<string, ErrorView> = {
  OAUTH_STATE_INVALID: EXPIRED,
  OAUTH_CODE_INVALID: EXPIRED,
  ML_ACCOUNT_IN_USE: {
    message: "Essa conta do Mercado Livre já está conectada em outro workspace do Vitrine.",
    canRetry: false,
  },
  ML_SITE_NOT_SUPPORTED: {
    message: "Por enquanto o Vitrine funciona só com contas do Mercado Livre Brasil.",
    canRetry: false,
  },
  ML_UNAVAILABLE: {
    message: "O Mercado Livre não respondeu. Tente de novo em alguns minutos.",
    canRetry: true,
  },
  ML_NOT_CONFIGURED: {
    message: "Conexão com o Mercado Livre indisponível no momento.",
    canRetry: false,
  },
  FORBIDDEN: { message: "Só o dono do workspace pode conectar contas.", canRetry: false },
};

/** Código que a SPA não conhece: mensagem genérica, sem expor o código. */
export const UNKNOWN_CONNECTION_ERROR: ErrorView = {
  message: "Não foi possível concluir a conexão agora. Volte para Contas e tente mais tarde.",
  canRetry: false,
};

/** Falha de rede (a Api não respondeu). */
export const NETWORK_ERROR: ErrorView = {
  message: "Não foi possível falar com o Vitrine. Verifique sua conexão e tente de novo.",
  canRetry: true,
};

export const CANCELLED_MESSAGE = "Você cancelou a autorização no Mercado Livre.";

/** Mensagem para o `code` de um erro de conexão (POST /connections ou /connections/complete). */
export function connectionError(problem: Problem | undefined): ErrorView {
  const code = problem?.code;
  return (code && CONNECTION_ERRORS[code]) || UNKNOWN_CONNECTION_ERROR;
}

/** Mensagem do "Sincronizar agora" (SPEC-0022@1: 429 SYNC_TOO_SOON, 409 ACCOUNT_REAUTHORIZATION_REQUIRED). */
export function syncErrorMessage(
  status: number,
  problem: Problem | undefined,
  headers?: Headers,
): string {
  if (status === 429 || problem?.code === "SYNC_TOO_SOON") {
    const header = Number(headers?.get("Retry-After"));
    const seconds =
      problem?.retryAfterSeconds ?? (Number.isFinite(header) && header > 0 ? header : 60);
    return `Você sincronizou há pouco. Tente de novo em ${Math.max(1, Math.ceil(seconds / 60))} min.`;
  }
  if (status === 409 || problem?.code === "ACCOUNT_REAUTHORIZATION_REQUIRED") {
    return "Reconecte a conta para sincronizar.";
  }
  if (status === 403 || problem?.code === "FORBIDDEN") {
    return "Você não tem permissão para sincronizar as contas deste workspace.";
  }
  return "Não foi possível sincronizar agora. Tente de novo em alguns minutos.";
}
