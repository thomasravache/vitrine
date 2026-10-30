// Formatação em pt-BR compartilhada pelas telas (preço, tempo relativo).

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** Preço na moeda do anúncio, em pt-BR ("R$ 1.234,56"). */
export function formatPrice(value: number, currency: string): string {
  try {
    return new Intl.NumberFormat("pt-BR", { style: "currency", currency }).format(value);
  } catch {
    // Moeda desconhecida para o Intl: número em pt-BR com o código da moeda.
    return `${currency} ${new Intl.NumberFormat("pt-BR", { minimumFractionDigits: 2 }).format(value)}`;
  }
}

/** Tempo desde `iso` em texto curto: "há 5 min", "há 2 h", "há 3 dias" (menos de 1 min: "há menos de 1 min"). */
export function timeAgo(iso: string, now: number = Date.now()): string {
  const elapsed = Math.max(0, now - Date.parse(iso));
  if (elapsed < MINUTE) {
    return "há menos de 1 min";
  }
  if (elapsed < HOUR) {
    return `há ${Math.floor(elapsed / MINUTE)} min`;
  }
  if (elapsed < DAY) {
    return `há ${Math.floor(elapsed / HOUR)} h`;
  }
  const days = Math.floor(elapsed / DAY);
  return days === 1 ? "há 1 dia" : `há ${days} dias`;
}

/** "{n} anúncio(s)". */
export function listingsCount(total: number): string {
  return total === 1 ? "1 anúncio" : `${total} anúncios`;
}
