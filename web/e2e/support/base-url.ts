// SPEC-0006: o ambiente local prod-like usa a CA interna do Caddy, que o navegador do Playwright não conhece.
// `localhost` e `*.localhost` são sempre loopback (RFC 6761), então só nesses hosts o certificado é ignorado;
// staging e produção seguem validando.
export function acceptsLocalCa(baseUrl: string | undefined): boolean {
  if (!baseUrl) return false;
  let hostname: string;
  try {
    hostname = new URL(baseUrl).hostname;
  } catch {
    return false;
  }
  return hostname === "localhost" || hostname.endsWith(".localhost");
}
