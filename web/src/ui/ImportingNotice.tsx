/** "Importando seus anúncios…" com barra de progresso indeterminada (sem aria-valuenow). */
export function ImportingNotice({ className = "" }: { className?: string }) {
  return (
    <div className={`flex flex-col gap-3 ${className}`}>
      <p className="font-semibold text-ink">Importando seus anúncios…</p>
      <div
        role="progressbar"
        aria-label="Importando seus anúncios"
        className="h-2 w-full overflow-hidden rounded-full bg-primary-soft"
      >
        <div className="h-full w-full animate-pulse rounded-full bg-primary/70 motion-reduce:animate-none" />
      </div>
    </div>
  );
}
