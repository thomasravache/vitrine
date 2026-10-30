import type { ReactNode } from "react";

const tones = {
  primary: "bg-primary-soft text-primary",
  pending: "bg-amber-50 text-amber-800",
  neutral: "bg-line-soft text-muted",
  outline: "border border-line text-muted",
} as const;

/** Selo de estado: sempre com texto (a cor só reforça). */
export function Badge({
  tone,
  dot = false,
  children,
}: {
  tone: keyof typeof tones;
  dot?: boolean;
  children: ReactNode;
}) {
  return (
    <span
      className={`inline-flex shrink-0 items-center gap-1.5 rounded-full px-3 py-1 text-xs font-semibold ${tones[tone]}`}
    >
      {dot && <span aria-hidden="true" className="size-1.5 rounded-full bg-current" />}
      {children}
    </span>
  );
}
