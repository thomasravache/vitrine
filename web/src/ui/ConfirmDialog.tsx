import { type KeyboardEvent, useEffect, useId, useRef } from "react";
import { Button } from "./Button.tsx";

type Props = {
  title: string;
  description: string;
  confirmLabel: string;
  cancelLabel?: string;
  busy?: boolean;
  error?: string | undefined;
  /** Elemento que recebe o foco ao fechar (padrão: o que estava focado ao abrir). */
  returnFocusTo?: HTMLElement | null | undefined;
  onConfirm: () => void;
  onCancel: () => void;
};

const FOCUSABLE =
  'button:not([disabled]), a[href], input:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * Diálogo de confirmação modal (role="alertdialog"): foco inicial no botão de cancelar, Tab/Shift+Tab presos no
 * diálogo, Esc cancela e, ao fechar, o foco volta para o elemento que o abriu.
 */
export function ConfirmDialog({
  title,
  description,
  confirmLabel,
  cancelLabel = "Cancelar",
  busy = false,
  error,
  returnFocusTo,
  onConfirm,
  onCancel,
}: Props) {
  const titleId = useId();
  const descriptionId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);

  const returnTarget = useRef(returnFocusTo);

  useEffect(() => {
    const opener =
      returnTarget.current ??
      (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    cancelRef.current?.focus();
    return () => {
      if (opener?.isConnected) {
        opener.focus();
      }
    };
  }, []);

  function handleKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === "Escape") {
      event.preventDefault();
      if (!busy) {
        onCancel();
      }
      return;
    }
    if (event.key !== "Tab" || !dialogRef.current) {
      return;
    }
    const focusable = Array.from(dialogRef.current.querySelectorAll<HTMLElement>(FOCUSABLE));
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (!first || !last) {
      event.preventDefault();
      return;
    }
    const active = document.activeElement;
    if (event.shiftKey && (active === first || !dialogRef.current.contains(active))) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && (active === last || !dialogRef.current.contains(active))) {
      event.preventDefault();
      first.focus();
    }
  }

  return (
    <div className="fixed inset-0 z-30 flex items-end justify-center bg-ink/50 p-4 sm:items-center">
      <div
        ref={dialogRef}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={descriptionId}
        onKeyDown={handleKeyDown}
        className="w-full max-w-md rounded-3xl bg-surface p-6 shadow-xl"
      >
        <h2 id={titleId} className="text-xl font-bold">
          {title}
        </h2>{" "}
        <p id={descriptionId} className="mt-2 text-muted">
          {description}
        </p>
        {error && (
          <p role="alert" className="mt-4 text-sm text-danger">
            {error}
          </p>
        )}
        <div className="mt-6 flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <Button ref={cancelRef} variant="outline" onClick={onCancel}>
            {cancelLabel}
          </Button>
          <Button onClick={onConfirm} disabled={busy}>
            {confirmLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}
