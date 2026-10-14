import { type InputHTMLAttributes, type Ref, useId, useState } from "react";
import { AlertIcon, EyeIcon, EyeOffIcon, InfoIcon } from "./icons.tsx";

type Props = Omit<InputHTMLAttributes<HTMLInputElement>, "id"> & {
  label: string;
  error?: string | undefined;
  hint?: string | undefined;
  ref?: Ref<HTMLInputElement>;
};

/** Campo com rótulo acima, dica e erro abaixo (ligados por aria-describedby); senha com mostrar/ocultar. */
export function TextField({
  label,
  error,
  hint,
  type = "text",
  className = "",
  ref,
  ...props
}: Props) {
  const id = useId();
  const [revealed, setRevealed] = useState(false);
  const isPassword = type === "password";
  const describedBy = [error ? `${id}-error` : null, hint ? `${id}-hint` : null]
    .filter(Boolean)
    .join(" ");

  return (
    <div className={`flex flex-col gap-2 ${className}`}>
      <label htmlFor={id} className="text-sm font-semibold text-ink">
        {label}
      </label>
      <div className="relative">
        <input
          ref={ref}
          id={id}
          type={isPassword && revealed ? "text" : type}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy || undefined}
          className={`min-h-12 w-full rounded-xl border bg-surface px-4 text-base text-ink placeholder:text-muted focus-visible:outline-3 focus-visible:outline-offset-1 focus-visible:outline-primary ${
            error ? "border-danger" : "border-line"
          } ${isPassword ? "pr-14" : ""}`}
          {...props}
        />
        {isPassword && (
          <button
            type="button"
            onClick={() => setRevealed((value) => !value)}
            aria-label={revealed ? "Ocultar senha" : "Mostrar senha"}
            aria-pressed={revealed}
            className="absolute inset-y-0 right-1 my-auto flex size-11 items-center justify-center rounded-lg text-muted hover:text-ink focus-visible:outline-3 focus-visible:outline-primary"
          >
            {revealed ? <EyeOffIcon className="size-5" /> : <EyeIcon className="size-5" />}
          </button>
        )}
      </div>
      {error && (
        <p id={`${id}-error`} className="flex items-start gap-1.5 text-sm text-danger">
          <AlertIcon className="mt-0.5 size-4 shrink-0" />
          {error}
        </p>
      )}
      {hint && (
        <p id={`${id}-hint`} className="flex items-start gap-1.5 text-sm text-muted">
          <InfoIcon className="mt-0.5 size-4 shrink-0" />
          {hint}
        </p>
      )}
    </div>
  );
}
