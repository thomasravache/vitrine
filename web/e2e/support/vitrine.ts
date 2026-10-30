import { randomUUID } from "node:crypto";
import { type APIResponse, expect, type Page, type Response } from "@playwright/test";

// SPEC-0025: usuário do Vitrine por teste (e-mail único) e cadastro que respeita o rate limit de 10/min por IP de
// /api/v1/auth/* (SPEC-0008): num 429, espera o Retry-After e tenta de novo. A jornada inteira roda em dois projetos
// (desktop e mobile) junto com a jornada acesso, então passar do limite é esperado numa execução completa.

export interface VitrineUser {
  name: string;
  email: string;
  password: string;
}

const REGISTER_PATH = "/api/v1/auth/register";
const MAX_ATTEMPTS = 4;

export function newUser(): VitrineUser {
  const id = randomUUID().replaceAll("-", "");
  return {
    name: `Vendedora${id.replace(/[0-9]/g, "").slice(0, 6)}`,
    email: `e2e.${id}@example.com`,
    password: "senha-forte-2026",
  };
}

function retryAfterMs(response: Response | APIResponse): number {
  const seconds = Number(response.headers()["retry-after"] ?? "60");
  return (Number.isFinite(seconds) && seconds > 0 ? seconds : 60) * 1_000 + 500;
}

/** Cadastro pela tela /cadastro (com espera no 429); termina no /painel. */
export async function signUpThroughUi(page: Page, user: VitrineUser): Promise<void> {
  await page.goto("/cadastro");
  await page.getByLabel("Seu nome", { exact: true }).fill(user.name);
  await page.getByLabel("E-mail", { exact: true }).fill(user.email);
  await page.getByLabel("Senha", { exact: true }).fill(user.password);

  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    const [response] = await Promise.all([
      page.waitForResponse(
        (r) => new URL(r.url()).pathname === REGISTER_PATH && r.request().method() === "POST",
      ),
      page.getByRole("button", { name: "Criar conta", exact: true }).click(),
    ]);
    if (response.status() !== 429) {
      expect(response.status(), "cadastro").toBe(201);
      await expect(page).toHaveURL(/\/painel(?:[?#].*)?$/);
      return;
    }
    await page.waitForTimeout(retryAfterMs(response));
  }
  throw new Error("Cadastro recusado pelo rate limit em todas as tentativas.");
}

/** Cadastro direto na Api, na sessão do navegador (cookie compartilhado com a página), com espera no 429. */
export async function signUpThroughApi(page: Page, user: VitrineUser): Promise<void> {
  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    const antiforgery = await page.request.get("/api/v1/auth/antiforgery");
    const token = antiforgery.headers()["x-xsrf-token"];
    expect(token, "token antiforgery").toBeTruthy();
    const response = await page.request.post(REGISTER_PATH, {
      headers: { "X-XSRF-TOKEN": token ?? "" },
      data: { name: user.name, email: user.email, password: user.password },
    });
    if (response.status() !== 429) {
      expect(response.status(), await response.text()).toBe(201);
      return;
    }
    await page.waitForTimeout(retryAfterMs(response));
  }
  throw new Error("Cadastro recusado pelo rate limit em todas as tentativas.");
}

/** Navegação principal (inferior no celular, lateral no desktop): só a visível na largura atual é acessível. */
export function mainNav(page: Page) {
  return page.getByRole("navigation", { name: "Principal" });
}

/** Itens da lista de Anúncios visíveis na largura atual (cartões < 1024 px, linhas da tabela ≥ 1024 px). */
export function visibleListings(page: Page) {
  return page.getByTestId("listing").filter({ visible: true });
}

/** Botão ou link com o rótulo (o contrato fixa o rótulo, não o elemento). */
export function action(page: Page, name: string) {
  return page
    .getByRole("button", { name, exact: true })
    .or(page.getByRole("link", { name, exact: true }));
}
