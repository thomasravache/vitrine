import { randomUUID } from "node:crypto";
import { expect, type Page, test } from "@playwright/test";
import { expectNoSeriousA11yViolations } from "./support/axe.ts";
import {
  changeTitleAndNotify,
  createSimSeller,
  decideOnSimulator,
  revokeSeller,
  type SimSeller,
  stubMlThumbnails,
} from "./support/ml-simulator.ts";
import {
  action,
  mainNav,
  newUser,
  signUpThroughApi,
  signUpThroughUi,
  visibleListings,
} from "./support/vitrine.ts";

// SPEC-0025: jornada crítica "conectar-conta" contra o simulador do Mercado Livre (SPEC-0024). Roda nos projetos
// desktop e mobile; cada teste cria o próprio vendedor no simulador e o próprio usuário no Vitrine. Miniaturas do
// CDN do ML são respondidas localmente (sem rede externa).

// Margem para o rate limit do cadastro (espera do Retry-After) e para a importação dos anúncios.
test.describe.configure({ timeout: 240_000 });
// Cada passo falha cedo (a margem longa do teste é só para o rate limit e a importação).
test.use({ actionTimeout: 15_000, navigationTimeout: 30_000 });

const CONTAS = /\/contas(?:[?#].*)?$/;
const RETORNO = /\/contas\/mercado-livre\/retorno/;
const IMPORT_TIMEOUT = 45_000;
const PAGE_SIZE = 20;

test.beforeEach(async ({ page }) => {
  await stubMlThumbnails(page);
});

/** Cartão da conta em /contas. */
function accountCard(page: Page, seller: SimSeller) {
  return page.getByTestId("account-card").filter({ hasText: seller.nickname });
}

function searchParam(page: Page, name: string): string | null {
  return new URL(page.url()).searchParams.get(name);
}

/** Em /contas: conecta pelo botão primário e autoriza o vendedor no simulador. */
async function connectFromAccounts(page: Page, seller: SimSeller): Promise<void> {
  await page.goto("/contas");
  await page.getByRole("button", { name: "Conectar conta do Mercado Livre", exact: true }).click();
  await decideOnSimulator(page, seller, "Autorizar");
  await expectConnected(page, seller);
}

async function expectConnected(page: Page, seller: SimSeller): Promise<void> {
  await expect(page).toHaveURL(CONTAS);
  await expect(
    page.getByText(`Conta ${seller.nickname} conectada. Estamos importando seus anúncios.`),
  ).toBeVisible();
  await expect(accountCard(page, seller).getByText("Conectada", { exact: true })).toBeVisible();
  expect(page.url()).not.toContain("code=");
}

/** Espera, em /contas (que consulta o resumo a cada 5 s enquanto importa), a importação dos 25 anúncios. */
async function waitForImport(page: Page, seller: SimSeller, total = 25): Promise<void> {
  await expect(accountCard(page, seller)).toContainText(`Anúncios: ${total}`, {
    timeout: IMPORT_TIMEOUT,
  });
}

/** Clica em "Sincronizar agora" e espera a resposta do POST /listings/sync (navegar antes abortaria o pedido). */
async function clickSyncNow(page: Page): Promise<number> {
  const [response] = await Promise.all([
    page.waitForResponse(
      (r) =>
        new URL(r.url()).pathname === "/api/v1/listings/sync" && r.request().method() === "POST",
    ),
    page.getByRole("button", { name: "Sincronizar agora", exact: true }).click(),
  ]);
  return response.status();
}

async function openListings(page: Page): Promise<void> {
  await mainNav(page).getByRole("link", { name: "Anúncios", exact: true }).click();
  await expect(page).toHaveURL(/\/anuncios(?:[?#].*)?$/);
  await expect(page.getByRole("heading", { level: 1, name: "Anúncios" })).toBeVisible();
}

test("SPEC-0025:E2E-01 — vendedor conecta a conta do ML, vê a conta conectada e os anúncios, filtra, busca e carrega mais [jornada: conectar-conta] @smoke", async ({
  page,
  request,
}) => {
  // Dado um vendedor novo no simulador (15 ativos, 6 pausados, 4 encerrados)
  const seller = await createSimSeller(request);

  // Quando o usuário se cadastra
  await signUpThroughUi(page, newUser());

  // Então o painel convida a conectar
  await expect(
    page.getByRole("heading", { name: "Conecte sua conta do Mercado Livre", exact: true }),
  ).toBeVisible();
  await expectNoSeriousA11yViolations(page);

  // Quando clica em "Conectar conta" e autoriza o vendedor no simulador (o retorno fica em "Concluindo" até o axe rodar)
  let releaseComplete: () => void = () => {};
  const completeGate = new Promise<void>((resolve) => {
    releaseComplete = resolve;
  });
  await page.route("**/api/v1/mercadolivre/connections/complete", async (route) => {
    await completeGate;
    await route.continue();
  });
  await page.getByRole("button", { name: "Conectar conta", exact: true }).click();
  await decideOnSimulator(page, seller, "Autorizar");

  // Então o retorno mostra "Concluindo a conexão…" já sem code/state na URL
  await expect(page).toHaveURL(RETORNO);
  await expect(page.getByText("Concluindo a conexão…")).toBeVisible();
  expect(page.url()).not.toContain("code=");
  expect(page.url()).not.toContain("state=");
  await expectNoSeriousA11yViolations(page);
  releaseComplete();

  // E volta para Contas com a conta "Conectada"
  await expectConnected(page, seller);
  await expect(
    page.getByRole("heading", { level: 1, name: "Contas do Mercado Livre" }),
  ).toBeVisible();
  await expectNoSeriousA11yViolations(page);
  await waitForImport(page, seller);

  // Quando abre Anúncios; então vê os 15 ativos, com o total e a miniatura do ML
  await openListings(page);
  await expect(page.getByText(/25 anúncios · sincronizado há/)).toBeVisible();
  await expect(page.getByRole("button", { name: "Ativos", exact: true })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await expect(visibleListings(page)).toHaveCount(15);
  const thumbnail = visibleListings(page).first().locator('img[src*="mlstatic.com"]');
  await expect(thumbnail).toBeVisible();
  await expect(thumbnail).toHaveJSProperty("naturalWidth", 1); // o PNG 1x1 da interceptação carregou
  await expectNoSeriousA11yViolations(page);

  // Quando filtra "Pausados"; então vê 6, e o filtro vai para a URL
  await page.getByRole("button", { name: "Pausados", exact: true }).click();
  await expect(page.getByRole("button", { name: "Pausados", exact: true })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await expect(visibleListings(page)).toHaveCount(6);
  expect(searchParam(page, "status")).toBe("paused");

  // Quando busca pelo título de um anúncio pausado; então só ele aparece, e a busca vai para a URL
  const paused = await page.request.get("/api/v1/listings?status=paused&limit=1");
  const [target] = ((await paused.json()) as { items: { title: string }[] }).items;
  expect(target).toBeDefined();
  const title = target?.title ?? "";
  await page.getByLabel("Buscar por título ou código").fill(title);
  await expect(visibleListings(page)).toHaveCount(1);
  await expect(visibleListings(page).first()).toContainText(title);
  await expect.poll(() => searchParam(page, "q")).toBe(title);

  // Quando limpa a busca e escolhe "Todos"; então vê a 1ª página e "Carregar mais" traz o resto
  await page.getByLabel("Buscar por título ou código").fill("");
  await page.getByRole("button", { name: "Todos", exact: true }).click();
  await expect(visibleListings(page)).toHaveCount(PAGE_SIZE);
  await page.getByRole("button", { name: "Carregar mais", exact: true }).click();
  await expect(visibleListings(page)).toHaveCount(25);
  await expect(page.getByRole("button", { name: "Carregar mais", exact: true })).toHaveCount(0);
  await expectNoSeriousA11yViolations(page);
});

test("SPEC-0025:E2E-02 — conta revogada no ML aparece como 'Reconectar necessário' e volta a 'Conectada' depois de reconectar", async ({
  page,
  request,
}) => {
  // Dado uma conta conectada e importada
  const seller = await createSimSeller(request);
  await signUpThroughApi(page, newUser());
  await connectFromAccounts(page, seller);
  await waitForImport(page, seller);

  // Quando o simulador revoga os tokens e expira o access token, e o usuário clica em "Sincronizar agora"
  await revokeSeller(request, seller.id);
  await openListings(page);
  expect(await clickSyncNow(page), "POST /listings/sync").toBe(202);

  // Então a conta aparece em Contas com "Reconectar necessário"
  await page.goto("/contas");
  await expect(async () => {
    await page.reload();
    await expect(accountCard(page, seller).getByText("Reconectar necessário")).toBeVisible({
      timeout: 2_000,
    });
  }).toPass({ timeout: IMPORT_TIMEOUT });
  await expect(
    accountCard(page, seller).getByText("O acesso expirou. Conecte de novo para continuar."),
  ).toBeVisible();

  // E o painel avisa, com o link para Contas
  await page.goto("/painel");
  await expect(page.getByText(`A conta ${seller.nickname} precisa ser reconectada`)).toBeVisible();
  await page.getByRole("link", { name: "Ir para Contas", exact: true }).click();
  await expect(page).toHaveURL(CONTAS);

  // Quando clica em "Reconectar" e autoriza de novo no simulador
  await accountCard(page, seller)
    .getByRole("button", { name: /^Reconectar/ })
    .click();
  await decideOnSimulator(page, seller, "Autorizar");

  // Então a conta volta a "Conectada"
  await expectConnected(page, seller);
  await expect(accountCard(page, seller).getByText("Reconectar necessário")).toHaveCount(0);
});

test("SPEC-0025:E2E-03 — desconectar pelo diálogo tira a conta de Contas e deixa Anúncios vazio", async ({
  page,
  request,
}) => {
  // Dado uma conta conectada com anúncios
  const seller = await createSimSeller(request);
  await signUpThroughApi(page, newUser());
  await connectFromAccounts(page, seller);
  await waitForImport(page, seller);

  // Quando clica em "Desconectar"
  const trigger = accountCard(page, seller).getByRole("button", { name: /^Desconectar/ });
  await trigger.click();

  // Então o diálogo de confirmação abre, com foco em "Cancelar" e foco preso nele; axe com o diálogo aberto
  const dialog = page.getByRole("dialog").or(page.getByRole("alertdialog"));
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(
    `Desconectar ${seller.nickname}? Os anúncios dessa conta saem do Vitrine. Você pode conectar de novo depois.`,
  );
  await expect(dialog.getByRole("button", { name: "Cancelar", exact: true })).toBeFocused();
  for (const key of ["Tab", "Tab", "Tab", "Shift+Tab", "Shift+Tab", "Shift+Tab"]) {
    await page.keyboard.press(key);
    await expect(dialog.locator(":focus"), `foco saiu do diálogo depois de ${key}`).toHaveCount(1);
  }
  await expectNoSeriousA11yViolations(page);

  // Quando confirma
  await dialog.getByRole("button", { name: "Desconectar", exact: true }).click();

  // Então a conta some de Contas
  await expect(dialog).toHaveCount(0);
  await expect(accountCard(page, seller)).toHaveCount(0);

  // E Anúncios mostra o estado vazio com "Conectar conta"
  await openListings(page);
  await expect(
    page.getByText(
      "Nenhum anúncio ainda. Conecte uma conta do Mercado Livre para ver seus anúncios aqui.",
    ),
  ).toBeVisible();
  await expect(action(page, "Conectar conta")).toBeVisible();
  await expect(visibleListings(page)).toHaveCount(0);
  await expectNoSeriousA11yViolations(page);
});

test("SPEC-0025:E2E-04 — cancelar no ML e vendedor de outro país mostram a mensagem certa, sem code na URL", async ({
  page,
  request,
}) => {
  // Dado um vendedor MLB e um MLA no simulador
  const seller = await createSimSeller(request);
  const foreign = await createSimSeller(request, {
    siteId: "MLA",
    active: 3,
    paused: 0,
    closed: 0,
  });
  await signUpThroughApi(page, newUser());

  // Quando o usuário clica em "Cancelar" na página do simulador
  await page.goto("/contas");
  await page.getByRole("button", { name: "Conectar conta do Mercado Livre", exact: true }).click();
  await decideOnSimulator(page, seller, "Cancelar");

  // Então vê a mensagem de cancelamento, sem code na URL, e volta para Contas
  await expect(page).toHaveURL(RETORNO);
  await expect(page.getByText("Você cancelou a autorização no Mercado Livre.")).toBeVisible();
  expect(page.url()).not.toContain("code=");
  await expectNoSeriousA11yViolations(page);
  await action(page, "Voltar para Contas").click();
  await expect(page).toHaveURL(CONTAS);

  // Quando autoriza um vendedor MLA
  await page.getByRole("button", { name: "Conectar conta do Mercado Livre", exact: true }).click();
  await decideOnSimulator(page, foreign, "Autorizar");

  // Então vê que só contas do Brasil funcionam, sem "Tentar de novo" e sem code na URL
  await expect(page).toHaveURL(RETORNO);
  await expect(
    page.getByText("Por enquanto o Vitrine funciona só com contas do Mercado Livre Brasil."),
  ).toBeVisible();
  await expect(page.getByRole("button", { name: "Tentar de novo" })).toHaveCount(0);
  expect(page.url()).not.toContain("code=");
});

test("SPEC-0025:E2E-05 — anúncio alterado no ML aparece após a notificação; 2º 'Sincronizar agora' pede para esperar", async ({
  page,
  request,
}) => {
  // Dado uma conta conectada e importada
  const seller = await createSimSeller(request);
  await signUpThroughApi(page, newUser());
  await connectFromAccounts(page, seller);
  await waitForImport(page, seller);

  // Quando o simulador muda o título de um anúncio ativo e envia a notificação
  const active = await page.request.get("/api/v1/listings?status=active&limit=1");
  const [item] = ((await active.json()) as { items: { itemId: string }[] }).items;
  expect(item).toBeDefined();
  const newTitle = `Título alterado no ML ${randomUUID().slice(0, 8)}`;
  await changeTitleAndNotify(request, seller.id, item?.itemId ?? "", newTitle);

  // Então o novo título aparece em Anúncios depois de recarregar (até 15 s)
  await openListings(page);
  await expect(async () => {
    await page.reload();
    await expect(visibleListings(page).filter({ hasText: newTitle })).toHaveCount(1, {
      timeout: 2_000,
    });
  }).toPass({ timeout: 15_000 });

  // Quando sincroniza agora e a sincronização manual termina
  const syncedAfter = Date.now();
  expect(await clickSyncNow(page), "1º POST /listings/sync").toBe(202);
  await expect
    .poll(
      async () => {
        const response = await page.request.get("/api/v1/listings/summary");
        const summary = (await response.json()) as {
          accounts: { syncStatus: string; lastSyncedAt: string | null }[];
        };
        const [account] = summary.accounts;
        return (
          account !== undefined &&
          account.syncStatus !== "running" &&
          account.lastSyncedAt !== null &&
          Date.parse(account.lastSyncedAt) >= syncedAfter - 1_000
        );
      },
      { timeout: IMPORT_TIMEOUT },
    )
    .toBe(true);

  // E clica de novo em "Sincronizar agora"; então a tela pede para esperar
  await page.reload();
  expect(await clickSyncNow(page), "2º POST /listings/sync").toBe(429);
  await expect(page.getByText(/Tente de novo em \d+ min/)).toBeVisible();
});
