/** Página inicial pública: apresenta o Vitrine e leva ao cadastro ou ao login. */
export function App() {
  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-4 p-6 text-center">
      <h1 className="font-display text-4xl font-bold text-primary">Vitrine</h1>
      <p className="max-w-md text-lg text-muted">
        Assistente para vendedores do Mercado Livre: anúncios melhores e perguntas respondidas.
      </p>
      <div className="mt-4 flex w-full max-w-xs flex-col gap-3">
        <a
          href="/cadastro"
          className="inline-flex min-h-12 items-center justify-center rounded-xl bg-primary px-5 font-display font-semibold text-white hover:bg-primary-hover focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-primary"
        >
          Criar conta
        </a>
        <a
          href="/entrar"
          className="inline-flex min-h-12 items-center justify-center rounded-xl border border-line bg-surface px-5 font-display font-semibold text-ink hover:bg-primary-soft focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-primary"
        >
          Entrar
        </a>
      </div>
    </main>
  );
}
