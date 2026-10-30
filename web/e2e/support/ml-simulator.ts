import { randomUUID } from "node:crypto";
import { type APIRequestContext, expect, type Page } from "@playwright/test";
import { warnSeriousA11yViolations } from "./axe.ts";

// SPEC-0025: apoio do E2E da jornada conectar-conta contra o simulador do Mercado Livre (SPEC-0024). Cada teste cria
// o próprio vendedor pelo controle /__sim; nada depende da massa inicial compartilhada.

export interface SimSeller {
  id: number;
  nickname: string;
  siteId: string;
}

/** URL do simulador: exportada pelo global-setup (AppHost) ou informada no ambiente (smoke local). */
export function simulatorUrl(): string {
  const url = process.env.ML_SIMULATOR_URL;
  if (!url) {
    throw new Error(
      "ML_SIMULATOR_URL não definida: o E2E da jornada conectar-conta precisa do simulador.",
    );
  }
  return url.replace(/\/$/, "");
}

/** Cria um vendedor novo no simulador (padrão: MLB com 15 ativos, 6 pausados e 4 encerrados). */
export async function createSimSeller(
  request: APIRequestContext,
  options: { siteId?: string; active?: number; paused?: number; closed?: number } = {},
): Promise<SimSeller> {
  const nickname = `LOJA_E2E_${randomUUID().replaceAll("-", "").slice(0, 10).toUpperCase()}`;
  const response = await request.post(`${simulatorUrl()}/__sim/sellers`, {
    data: { nickname, ...options },
  });
  expect(response.status(), await response.text()).toBe(201);
  return (await response.json()) as SimSeller;
}

/** Revoga os tokens do vendedor no simulador (o refresh passa a falhar com invalid_grant). */
export async function revokeSeller(request: APIRequestContext, sellerId: number): Promise<void> {
  const revoked = await request.post(`${simulatorUrl()}/__sim/sellers/${sellerId}/revoke`);
  expect(revoked.status()).toBe(204);
  const expired = await request.post(
    `${simulatorUrl()}/__sim/sellers/${sellerId}/expire-access-token`,
  );
  expect(expired.status()).toBe(204);
}

/** Muda o título de um anúncio no simulador e envia a notificação do ML para a Api. */
export async function changeTitleAndNotify(
  request: APIRequestContext,
  sellerId: number,
  itemId: string,
  title: string,
): Promise<void> {
  const changed = await request.post(
    `${simulatorUrl()}/__sim/sellers/${sellerId}/items/${itemId}`,
    {
      data: { title },
    },
  );
  expect(changed.status(), await changed.text()).toBe(200);
  const notified = await request.post(`${simulatorUrl()}/__sim/notify`, {
    data: { userId: sellerId, itemId },
  });
  expect(notified.status(), await notified.text()).toBe(202);
}

/**
 * Na página de autorização do simulador, escolhe o vendedor e decide ("Autorizar" ou "Cancelar"). A página não é do
 * produto: o axe roda nela só como aviso.
 */
export async function decideOnSimulator(
  page: Page,
  seller: SimSeller,
  decision: "Autorizar" | "Cancelar",
): Promise<void> {
  await page.waitForURL((url) => url.href.startsWith(`${simulatorUrl()}/authorization`));
  await warnSeriousA11yViolations(page, "página de autorização do simulador");
  await page.getByRole("radio", { name: new RegExp(`\\(id ${seller.id},`) }).check();
  await page.getByRole("button", { name: decision, exact: true }).click();
}

// PNG 1x1 transparente: as miniaturas do simulador apontam para caminhos que não existem no CDN real do ML.
const PNG_1X1 = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=",
  "base64",
);

/** Sem rede externa: toda requisição a *.mlstatic.com recebe um PNG fixo. */
export async function stubMlThumbnails(page: Page): Promise<void> {
  await page.route(/^https?:\/\/[^/]*\.mlstatic\.com\//, (route) =>
    route.fulfill({ status: 200, contentType: "image/png", body: PNG_1X1 }),
  );
}
