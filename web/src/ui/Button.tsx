import type { ButtonHTMLAttributes, Ref } from "react";

const base =
  "inline-flex min-h-12 items-center justify-center gap-2 rounded-xl px-5 font-display text-base font-semibold " +
  "transition-colors focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-primary " +
  "disabled:cursor-not-allowed";

const variants = {
  primary: "bg-primary text-white hover:bg-primary-hover disabled:bg-primary/60",
  ghost: "text-ink hover:bg-primary-soft",
  outline: "border border-line bg-surface text-ink hover:bg-primary-soft disabled:text-muted",
} as const;

export const buttonClass = (variant: keyof typeof variants = "primary") =>
  `${base} ${variants[variant]}`;

type Props = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: keyof typeof variants;
  ref?: Ref<HTMLButtonElement>;
};

/** Botão do sistema de design: alvo de 48px, foco visível. */
export function Button({ variant = "primary", className = "", type = "button", ...props }: Props) {
  return <button type={type} className={`${base} ${variants[variant]} ${className}`} {...props} />;
}
