import AxeBuilder from "@axe-core/playwright";
import { expect, type Page } from "@playwright/test";

const BLOCKING_IMPACTS = new Set(["serious", "critical"]);

/** Falha se o axe encontrar violações de impacto sério ou crítico na página atual. */
export async function expectNoSeriousA11yViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).analyze();
  const blocking = results.violations
    .filter((violation) => BLOCKING_IMPACTS.has(violation.impact ?? ""))
    .map((violation) => `${violation.impact}: ${violation.id} — ${violation.help}`);

  expect(blocking, "violações axe sérias/críticas").toEqual([]);
}
