import { expect, test } from "@playwright/test";
import { expectNoSeriousA11yViolations } from "./support/axe.ts";

test("SPEC-0003:E2E-01 — página inicial carrega o elemento raiz sem violações axe sérias ou críticas", async ({
  page,
}) => {
  await page.goto("/");

  await expect(page.locator("#root")).not.toBeEmpty();
  await expect(page.getByRole("heading", { level: 1, name: "Vitrine" })).toBeVisible();
  await expectNoSeriousA11yViolations(page);
});
