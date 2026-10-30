import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";

const BLOCKING_IMPACTS = new Set(["serious", "critical"]);

/** Falha se o axe encontrar violações de impacto sério ou crítico na página atual. */
export async function expectNoSeriousA11yViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).analyze();
  const blocking = results.violations
    .filter((violation) => BLOCKING_IMPACTS.has(violation.impact ?? ""))
    .map((violation) => `${violation.impact}: ${violation.id} — ${violation.help}`);

  expect(blocking, "violações axe sérias/críticas").toEqual([]);
}

/**
 * Roda o axe e registra as violações sérias/críticas como aviso (anotação do teste), sem falhar. Para páginas fora do
 * produto, como a página de autorização do simulador do Mercado Livre (SPEC-0025).
 */
export async function warnSeriousA11yViolations(page: Page, label: string): Promise<void> {
  const results = await new AxeBuilder({ page }).analyze();
  const blocking = results.violations
    .filter((violation) => BLOCKING_IMPACTS.has(violation.impact ?? ""))
    .map((violation) => `${violation.impact}: ${violation.id} — ${violation.help}`);
  if (blocking.length > 0) {
    test.info().annotations.push({
      type: "warning",
      description: `axe em ${label}: ${blocking.join("; ")}`,
    });
  }
}
