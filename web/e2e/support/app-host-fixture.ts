/** Aplicação subida pelo AppHost (Aspire.Hosting.Testing) para o E2E. */
export interface AppHostHandle {
  /** URL pública da SPA servida pela aplicação. */
  baseURL: string;
  /** Para a aplicação e libera os recursos. */
  stop: () => Promise<void>;
}

/** AppHostFixture: sobe o AppHost e devolve a URL da aplicação. */
export async function startAppHost(): Promise<AppHostHandle> {
  throw new Error(
    "SPEC-0003: AppHostFixture ainda não implementada (sem BASE_URL não há aplicação).",
  );
}
