import type { ListingResponse } from "../../api/generated/vitrine.ts";
import { Badge } from "../../ui/Badge.tsx";
import { formatPrice } from "../../ui/format.ts";
import { ExternalLinkIcon, ImageIcon } from "../../ui/icons.tsx";

const STATUS: Record<string, { label: string; tone: "primary" | "neutral" | "outline" }> = {
  active: { label: "Ativo", tone: "primary" },
  paused: { label: "Pausado", tone: "neutral" },
  closed: { label: "Encerrado", tone: "outline" },
};

function StatusBadge({ status }: { status: string }) {
  const known = STATUS[status] ?? { label: "Em revisão", tone: "outline" as const };
  return (
    <Badge tone={known.tone} dot={status === "active"}>
      {known.label}
    </Badge>
  );
}

function Thumbnail({ listing, size }: { listing: ListingResponse; size: "card" | "row" }) {
  const box = size === "card" ? "size-16" : "size-12";
  if (!listing.thumbnailUrl) {
    return (
      <span
        data-testid="listing-thumbnail-placeholder"
        className={`flex ${box} shrink-0 items-center justify-center rounded-xl border border-line-soft bg-canvas text-muted`}
      >
        <ImageIcon className="size-6" />
      </span>
    );
  }
  return (
    <img
      src={listing.thumbnailUrl}
      alt=""
      width={64}
      height={64}
      loading="lazy"
      className={`${box} shrink-0 rounded-xl border border-line-soft bg-canvas object-cover`}
    />
  );
}

function MlLink({ listing, compact }: { listing: ListingResponse; compact: boolean }) {
  if (!listing.permalink) {
    return null;
  }
  return (
    <a
      href={listing.permalink}
      target="_blank"
      rel="noopener noreferrer"
      aria-label={`Ver no Mercado Livre: ${listing.title} (abre em nova aba)`}
      className={`inline-flex min-h-11 items-center gap-1.5 rounded-lg font-semibold text-primary hover:underline focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-primary ${
        compact ? "min-w-11 justify-center" : "text-sm"
      }`}
    >
      {!compact && "Ver no Mercado Livre"}
      <ExternalLinkIcon className="size-5" />
    </a>
  );
}

/**
 * Anúncios em cartões (< 1024 px) e em tabela (≥ 1024 px), a partir dos mesmos dados; os dois ficam no DOM e o CSS
 * mostra um deles.
 */
export function ListingsList({
  listings,
  nicknames,
}: {
  listings: ListingResponse[];
  nicknames: Map<string, string>;
}) {
  return (
    <>
      <ul className="flex flex-col gap-3 lg:hidden">
        {listings.map((listing) => (
          <li
            key={listing.itemId}
            data-testid="listing"
            className="flex gap-4 rounded-2xl border border-line-soft bg-surface p-4"
          >
            <Thumbnail listing={listing} size="card" />
            <div className="min-w-0 flex-1">
              <p className="line-clamp-2 font-display font-semibold">{listing.title}</p>
              <p className="mt-0.5 font-mono text-xs text-muted">{listing.itemId}</p>
              <p className="mt-2 font-display text-lg font-bold">
                {formatPrice(listing.price, listing.currencyId)}
              </p>
              <div className="mt-1 flex flex-wrap items-center justify-between gap-2">
                <p className="text-sm text-muted">
                  Estoque {listing.availableQuantity} · {listing.soldQuantity} vendidos
                </p>
                <StatusBadge status={listing.status} />
              </div>
              <div className="mt-1">
                <MlLink listing={listing} compact={false} />
              </div>
            </div>
          </li>
        ))}
      </ul>

      <div className="hidden overflow-hidden rounded-2xl border border-line-soft bg-surface lg:block">
        <table className="w-full text-left text-sm">
          <thead className="bg-canvas text-xs font-semibold tracking-wide text-muted uppercase">
            <tr>
              <th scope="col" className="px-4 py-3">
                Miniatura
              </th>
              <th scope="col" className="px-4 py-3">
                Título / código
              </th>
              <th scope="col" className="px-4 py-3 text-right">
                Preço
              </th>
              <th scope="col" className="px-4 py-3 text-right">
                Estoque
              </th>
              <th scope="col" className="px-4 py-3 text-right">
                Vendidos
              </th>
              <th scope="col" className="px-4 py-3">
                Estado
              </th>
              <th scope="col" className="px-4 py-3">
                Conta
              </th>
              <th scope="col" className="px-4 py-3">
                <span className="sr-only">Link</span>
              </th>
            </tr>
          </thead>
          <tbody className="divide-y divide-line-soft">
            {listings.map((listing) => (
              <tr key={listing.itemId} data-testid="listing">
                <td className="px-4 py-3">
                  <Thumbnail listing={listing} size="row" />
                </td>
                <td className="max-w-80 px-4 py-3">
                  <p className="truncate font-medium text-ink" title={listing.title}>
                    {listing.title}
                  </p>
                  <p className="mt-1 font-mono text-xs text-muted">{listing.itemId}</p>
                </td>
                <td className="px-4 py-3 text-right font-semibold whitespace-nowrap">
                  {formatPrice(listing.price, listing.currencyId)}
                </td>
                <td className="px-4 py-3 text-right">{listing.availableQuantity}</td>
                <td className="px-4 py-3 text-right">{listing.soldQuantity}</td>
                <td className="px-4 py-3">
                  <StatusBadge status={listing.status} />
                </td>
                <td className="px-4 py-3 font-mono text-xs text-muted">
                  {nicknames.get(listing.accountId) ?? ""}
                </td>
                <td className="px-4 py-3">
                  <MlLink listing={listing} compact />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
