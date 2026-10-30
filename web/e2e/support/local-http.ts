import http from "node:http";
import https from "node:https";
import type { LookupFunction } from "node:net";
import type { Page } from "@playwright/test";
import { acceptsLocalCa } from "./base-url.ts";

// SPEC-0025: chamadas HTTP feitas pelo Node no E2E (controle /__sim do simulador). O navegador resolve `*.localhost`
// para loopback (RFC 6761), mas o resolvedor do sistema nem sempre: no ambiente local prod-like
// (https://ml-simulador.vitrine.localhost) o APIRequestContext do Playwright falhava com ENOTFOUND. Aqui, hosts
// `localhost`/`*.localhost` conectam em 127.0.0.1 mantendo SNI e Host originais, e só eles aceitam a CA interna do
// Caddy — mesmo critério do `ignoreHTTPSErrors` do navegador (acceptsLocalCa). Os demais hosts seguem o DNS e a
// validação de certificado normais.

export interface LocalResponse {
  status: number;
  headers: http.IncomingHttpHeaders;
  text: string;
}

const loopbackLookup: LookupFunction = (_hostname, options, callback) => {
  if (options.all) {
    (callback as (error: null, addresses: { address: string; family: number }[]) => void)(null, [
      { address: "127.0.0.1", family: 4 },
    ]);
    return;
  }
  callback(null, "127.0.0.1", 4);
};

/** Requisição JSON pelo Node, com `*.localhost` resolvido para loopback. */
export function nodeRequest(
  method: string,
  url: string,
  body?: unknown,
  timeoutMs = 15_000,
): Promise<LocalResponse> {
  const target = new URL(url);
  const local = acceptsLocalCa(url);
  const payload = body === undefined ? undefined : JSON.stringify(body);
  const client = target.protocol === "https:" ? https : http;

  return new Promise((resolve, reject) => {
    const request = client.request(
      target,
      {
        method,
        headers: {
          Accept: "application/json",
          ...(payload === undefined
            ? {}
            : { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(payload) }),
        },
        timeout: timeoutMs,
        ...(local ? { lookup: loopbackLookup, rejectUnauthorized: false } : {}),
      },
      (response) => {
        const chunks: Buffer[] = [];
        response.on("data", (chunk: Buffer) => chunks.push(chunk));
        response.on("end", () =>
          resolve({
            status: response.statusCode ?? 0,
            headers: response.headers,
            text: Buffer.concat(chunks).toString("utf8"),
          }),
        );
        response.on("error", reject);
      },
    );
    request.on("timeout", () => request.destroy(new Error(`${method} ${url}: timeout`)));
    request.on("error", reject);
    request.end(payload);
  });
}

export interface PageResponse {
  status: number;
  text: string;
  headers: Record<string, string>;
}

/**
 * Requisição à Api do Vitrine feita pelo navegador (fetch na página, mesma origem e cookie de sessão), para não
 * depender do resolvedor do Node. A página precisa estar na origem da aplicação.
 */
export function pageRequest(
  page: Page,
  method: string,
  path: string,
  options: { body?: unknown; headers?: Record<string, string> } = {},
): Promise<PageResponse> {
  return page.evaluate(
    async ({ method, path, body, headers }) => {
      const response = await fetch(path, {
        method,
        credentials: "same-origin",
        headers: {
          ...(body === undefined ? {} : { "Content-Type": "application/json" }),
          ...headers,
        },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      });
      const all: Record<string, string> = {};
      response.headers.forEach((value, key) => {
        all[key] = value;
      });
      return { status: response.status, text: await response.text(), headers: all };
    },
    { method, path, body: options.body, headers: options.headers ?? {} },
  );
}
