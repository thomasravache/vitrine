// Transporte do cliente gerado (orval): mesma origem, cookie de sessão HttpOnly (ADR-0007) e o token
// antiforgery buscado em GET /api/v1/auth/antiforgery e devolvido no cabeçalho de mesmo nome nos POST.

const ANTIFORGERY_PATH = "/api/v1/auth/antiforgery";
const ANTIFORGERY_HEADER = "X-XSRF-TOKEN";
const SAFE_METHODS = new Set(["GET", "HEAD", "OPTIONS"]);

// O token acompanha a identidade da sessão (muda ao entrar/sair): busca um novo antes de cada POST.
async function antiforgeryToken(): Promise<string> {
  const response = await fetch(ANTIFORGERY_PATH, { credentials: "same-origin" });
  const token = response.headers.get(ANTIFORGERY_HEADER);
  if (!response.ok || !token) {
    throw new Error("Não foi possível obter o token antiforgery.");
  }
  return token;
}

export async function http<T>(url: string, options: RequestInit = {}): Promise<T> {
  const method = (options.method ?? "GET").toUpperCase();
  const headers = new Headers(options.headers);
  if (!SAFE_METHODS.has(method)) {
    headers.set(ANTIFORGERY_HEADER, await antiforgeryToken());
  }

  const response = await fetch(url, { ...options, headers, credentials: "same-origin" });
  const text = [204, 205, 304].includes(response.status) ? "" : await response.text();
  const data: unknown = text ? JSON.parse(text) : undefined;
  return { data, status: response.status, headers: response.headers } as T;
}
