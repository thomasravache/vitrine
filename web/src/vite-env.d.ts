/// <reference types="vite/client" />

/** Variáveis de build da SPA (prefixo `VITE_`); todas opcionais. */
interface ImportMetaEnv {
  /** DSN do Sentry da SPA (SPEC-0007); ausente = Sentry desligado. */
  readonly VITE_SENTRY_DSN?: string;
  /** Release reportada ao Sentry: SHA do commit do build. */
  readonly VITE_SENTRY_RELEASE?: string;
  /** Ambiente reportado ao Sentry (padrão: modo do Vite). */
  readonly VITE_SENTRY_ENVIRONMENT?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
