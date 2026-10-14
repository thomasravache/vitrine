import { randomUUID } from "node:crypto";
import { expect, type Page, test } from "@playwright/test";
import { expectNoSeriousA11yViolations } from "./support/axe.ts";

// SPEC-0006: jornada crítica "acesso" — cadastro, painel vazio, sair e entrar de novo.
// Roda nos projetos desktop e mobile (playwright.config.ts); cada execução cria o próprio vendedor.

interface Seller {
  name: string;
  email: string;
  password: string;
}

const PAINEL = /\/painel(?:[?#].*)?$/;
const ENTRAR = /\/entrar(?:[?#].*)?$/;

function newSeller(): Seller {
  const id = randomUUID().replaceAll("-", "");
  // Nome de uma palavra: a saudação do painel mostra o nome mesmo que use só o primeiro nome.
  const suffix = id.replace(/[0-9]/g, "").slice(0, 6);
  return {
    name: `Vendedora${suffix}`,
    email: `e2e.${id}@example.com`,
    password: "senha-forte-2026",
  };
}

async function fillCredentials(page: Page, seller: Seller): Promise<void> {
  await page.getByLabel("E-mail", { exact: true }).fill(seller.email);
  await page.getByLabel("Senha", { exact: true }).fill(seller.password);
}

async function expectEmptyDashboard(page: Page, seller: Seller): Promise<void> {
  await expect(page).toHaveURL(PAINEL);
  await expect(page.getByRole("heading", { name: new RegExp(seller.name) })).toBeVisible();
  await expect(page.getByText("Conectar conta do Mercado Livre").first()).toBeVisible();
  await expect(page.getByText(/em breve/i).first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Sair", exact: true })).toBeVisible();
}

test("SPEC-0006:E2E-01 — vendedor se cadastra, vê o painel vazio, sai e entra de novo [jornada: acesso] @smoke", async ({
  page,
}) => {
  const seller = newSeller();

  // Dado o navegador em /cadastro
  await page.goto("/cadastro");
  await expect(page.getByRole("button", { name: "Criar conta", exact: true })).toBeVisible();
  await expectNoSeriousA11yViolations(page);

  // Quando o vendedor se cadastra
  await page.getByLabel("Seu nome", { exact: true }).fill(seller.name);
  await fillCredentials(page, seller);
  await page.getByRole("button", { name: "Criar conta", exact: true }).click();

  // Então vê o painel vazio com seu nome e o cartão "Conectar conta do Mercado Livre (em breve)"
  await expectEmptyDashboard(page, seller);
  await expectNoSeriousA11yViolations(page);

  // Quando sai
  await page.getByRole("button", { name: "Sair", exact: true }).click();

  // Então volta para /entrar
  await expect(page).toHaveURL(ENTRAR);
  await expect(page.getByRole("button", { name: "Entrar", exact: true })).toBeVisible();
  await expectNoSeriousA11yViolations(page);

  // Quando entra de novo; então volta ao painel
  await fillCredentials(page, seller);
  await page.getByRole("button", { name: "Entrar", exact: true }).click();
  await expectEmptyDashboard(page, seller);
});

test("SPEC-0006:E2E-02 — sem sessão, abrir /painel redireciona para /entrar", async ({ page }) => {
  // Dado nenhuma sessão; quando o usuário abre /painel
  await page.goto("/painel");

  // Então é redirecionado para /entrar
  await expect(page).toHaveURL(ENTRAR);
  await expect(page.getByRole("button", { name: "Entrar", exact: true })).toBeVisible();
});
