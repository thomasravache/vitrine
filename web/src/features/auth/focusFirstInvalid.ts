/** Depois de um envio com erro, leva o foco ao primeiro campo inválido (ou ao alerta geral). */
export function focusFirstInvalid(form: HTMLFormElement): void {
  requestAnimationFrame(() => {
    form.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus();
  });
}
