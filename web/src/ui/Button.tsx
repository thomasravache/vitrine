import type { ButtonHTMLAttributes } from "react";

const base =
  "inline-flex min-h-12 items-center justify-center gap-2 rounded-xl px-5 font-display text-base font-semibold " +
  "transition-colors focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-primary " +
  "disabled:cursor-not-allowed";

const variants = {
  primary: "bg-primary text-white hover:bg-primary-hover disabled:bg-primary/60",
  ghost: "text-ink hover:bg-primary-soft",
} as const;

type Props = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: keyof typeof variants };

/** Botão do sistema de design: alvo de 48px, foco visível. */
export function Button({ variant = "primary", className = "", type = "button", ...props }: Props) {
  return <button type={type} className={`${base} ${variants[variant]} ${className}`} {...props} />;
}
