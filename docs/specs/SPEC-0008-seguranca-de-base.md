---
id: SPEC-0008
title: Segurança de base
tier: full
type: foundation
user_facing: false
status: implemented
created: 2026-10-13
parent: SPEC-0001
depends_on: [SPEC-0006]
consumes_contract: [SPEC-0006@1]
contract_version: 4
touches: [src/Api/Security/**, src/Api/Program.cs, src/Api/Vitrine.Api.csproj, src/Api/appsettings*.json, src/ServiceDefaults/**, src/Common/Vitrine.Common.Security/**, src/Modules/Identity/Vitrine.Identity.Presentation/**, src/Modules/Identity/Vitrine.Identity.Infrastructure/**, Vitrine.slnx, Directory.Packages.props, "**/packages.lock.json", deploy/compose.yaml, deploy/compose.override.yaml, deploy/Caddyfile, deploy/local/.env.example, tests/Vitrine.UnitTests/Security/**, tests/Vitrine.UnitTests/Vitrine.UnitTests.csproj, tests/Vitrine.IntegrationTests/Security/**, tests/Vitrine.IntegrationTests/Support/**, tests/Vitrine.IntegrationTests/Vitrine.IntegrationTests.csproj, tests/Vitrine.ArchitectureTests/**, docs/runbooks/**, .gitleaksignore, web/vite.config.ts]
adrs: [ADR-0007, ADR-0006]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-13
---

# SPEC-0008 — Segurança de base

## 1. Visão Geral
Fecha a base de segurança antes de qualquer dado de cliente: autorização negada por padrão verificada por teste de arquitetura, cabeçalhos de segurança e HTTPS estrito, limite de requisições nos endpoints de autenticação, serviço de cifra de campos sensíveis (usado pelos tokens do Mercado Livre nos épicos seguintes), persistência das chaves de Data Protection e verificação de que segredos e dados sensíveis não vazam.

## 2. Motivação & Escopo
**Motivação:** o Vitrine vai guardar tokens que alteram anúncios de terceiros; as proteções transversais precisam existir antes do primeiro token.

**Objetivos (dentro do escopo):**
- `FallbackPolicy = RequireAuthenticatedUser` e lista explícita de endpoints anônimos; teste de arquitetura que falha se surgir endpoint anônimo fora da lista.
- Cabeçalhos: `Strict-Transport-Security` (produção), `Content-Security-Policy` restritiva para a SPA, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, `X-Frame-Options: DENY`; remoção de `Server`/`X-Powered-By`.
- Rate limiting (`Microsoft.AspNetCore.RateLimiting`) nos POST de cadastro e login: 10 req/min por IP e 10 req/min por e-mail no login (acima do bloqueio de 5 falhas da SPEC-0006, que responde 423 antes); resposta 429 com `Retry-After`.
- `IFieldProtector` em `src/Common` (AES-256-GCM via Data Protection com propósito por campo) + conversor EF Core `Protected<T>`; chaves do Data Protection persistidas no PostgreSQL e protegidas por chave mestra vinda do ambiente.
- Configuração do `.env`/Environment para a chave mestra; procedimento de rotação no runbook.
- Atrás do Caddy: `UseForwardedHeaders` (X-Forwarded-Proto/For só da rede do Compose) para `Request.IsHttps` correto; cookie antiforgery volta a `Secure=Always`; HSTS passa a valer atrás do proxy.
- Health checks anônimos por metadado (`AllowAnonymous` nos endpoints de ServiceDefaults), removendo a exceção por caminho `/health/*` da política de fallback. O teste de integração correspondente sobe o host com um `index.html` de teste no web root, provando que o fallback da SPA não engole `/health/*` nem `/api/*`.
- `security_scan` completo (gitleaks + dependências + Trivy) já no CI (SPEC-0004) — aqui só os ajustes de regra se necessário.

**Não-objetivos (fora do escopo):**
- OAuth com o Mercado Livre (épico "Contas e anúncios"); 2FA; WAF.

## 3. Dependências
- **Implementações necessárias:** SPEC-0006 — os testes de integração exercitam os endpoints de autenticação reais.
- **Contratos consumidos:** SPEC-0006@1 — endpoints de auth (rate limiting, antiforgery) e telas da SPA (CSP).
- **Pré-requisitos externos:** chave mestra gerada pelo responsável, guardada como secret dos Environments e em cofre pessoal fora do servidor (ADR-0006).

## 4. Decisão Arquitetural
**Contexto:** Projeto novo. Padrão definido em ADR-0007 (autorização negada por padrão, cookie na mesma origem) e ADR-0006 (segredos e cifra de campos).

**Decisão:** proteções nativas do ASP.NET Core 10 (autorização, rate limiting, Data Protection) configuradas no host; cifra de campo como building block em `src/Common`, sem dependência de módulos.

**Justificativa:** só uma biblioteca nova, oficial da Microsoft (`Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` 10.0.13, MIT, para persistir as chaves no PostgreSQL — ADR-0006); tudo verificável por testes de integração.

**Desvio do padrão existente:** Nenhum (projeto novo).

**Alternativas descartadas:** cifra no banco (pgcrypto) — a chave trafegaria para o banco; cofre externo (ADR-0006 descartou).

**ADRs:** ADR-0007, ADR-0006.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** cifra/decifra de um campo < 1 ms (p95) — UT-03 com medição simples; rate limiting sem impacto perceptível.
- **Segurança:** OWASP Top 10 aplicáveis: A01 (controle de acesso — IT-01, UT-01), A02 (falhas criptográficas — UT-02, IT-04), A05 (configuração — IT-02), A07 (autenticação — IT-03) — checklist no G4.
- **Privacidade e dados pessoais:** valores protegidos nunca são logados (o conversor não implementa `ToString` com conteúdo) — UT-02.
- **Disponibilidade e resiliência:** reinício do container não invalida sessões nem dados cifrados (chaves persistidas) — IT-05.
- **Acessibilidade (UI):** N/A — sem tela nova.
- **Custo:** N/A

## 6. Artefato A — Contrato
**Interface:** política de autorização, cabeçalhos HTTP, limites de requisição e `IFieldProtector`

```text
Autorização: todo endpoint exige usuário autenticado, exceto a lista AnonymousEndpoints:
  POST /api/v1/auth/register, POST /api/v1/auth/login, GET /api/v1/auth/antiforgery, GET /health/live, GET /health/ready, arquivos estáticos da SPA
Cabeçalhos em toda resposta: Content-Security-Policy (default-src 'self'; script-src 'self'; connect-src 'self' <sentry>; frame-ancestors 'none'),
  X-Content-Type-Options: nosniff, Referrer-Policy: strict-origin-when-cross-origin, Permissions-Policy: camera=(), microphone=(), geolocation=(),
  X-Frame-Options: DENY; em produção também Strict-Transport-Security: max-age=31536000; includeSubDomains
Rate limit: POST /api/v1/auth/register e POST /api/v1/auth/login, 10/min por IP (IP real do cliente, via forwarded headers confiáveis;
  GET /antiforgery e logout não contam — evitam bloquear quem divide IP por CGNAT); login 10/min por e-mail -> 429 + Retry-After
  Em Development, conexões de loopback não contam no limite por IP (AppHost/Vite locais)
  A decisão de limitar usa o endpoint roteado (vale para `/login/`, `/LOGIN` etc.); o e-mail da partição é o mesmo que o endpoint usa
  (JSON com `email` repetido ou ilegível para o limitador -> 400, falha fechada; BOM aceito; e-mail normalizado como no Identity, FormC + maiúsculas); corpo do login/cadastro acima de 4 KB -> 413 sem ser lido por inteiro
  Ordem: o bloqueio da SPEC-0006 (5 falhas -> 423) vem antes do limite por e-mail; o limite por e-mail protege e-mails inexistentes e o custo do hash
src/Common/Security:
  interface IFieldProtector { string Protect(string plaintext, string purpose); string Unprotect(string ciphertext, string purpose); }
  EF Core: builder.Property(x => x.Secret).IsProtected("<purpose>")   // grava cifrado, lê decifrado
  Erro: CryptographicException -> nunca devolve texto parcial
Config: DataProtection__MasterKey (secret do Environment; 32 bytes aleatórios em Base64; ausente fora de Development -> a API não sobe)
  DataProtection__PreviousMasterKey (opcional, só durante a rotação: a API lê com as duas e `--migrate` recifra o anel de chaves)
  Fora de Development, se o anel tem chaves e nenhuma decifra com a chave mestra (ou a anterior) -> a API não sobe (mensagem clara)
  RateLimiting__AuthPerIp__PermitLimit (padrão 10), RateLimiting__LoginPerEmail__PermitLimit (padrão 10)
  ForwardedHeaders__KnownNetworks__0 (CIDR da rede do Compose onde está o Caddy)
  CSP connect-src inclui a origem de ingestão do Sentry derivada de Sentry__Dsn quando configurado (backend e SPA usam o mesmo projeto do Sentry)
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches` no frontmatter.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Endpoint sem política | novo endpoint sem `RequireAuthorization` nem na lista anônima | teste de arquitetura falha | UT-01 |
| Acesso anônimo negado | GET de rota protegida sem cookie | 401 | IT-01 |
| Cabeçalhos | qualquer resposta | todos os cabeçalhos do contrato presentes; `Server` ausente | IT-02 |
| Rate limit | 11º POST de cadastro/login por minuto do mesmo IP, ou 11º login/min do mesmo e-mail | 429 + Retry-After; GET /antiforgery não conta | IT-03 |
| Variações de rota e corpo | `POST /api/v1/auth/login/`, caixa diferente, `email` duplicado no JSON, corpo > 4 KB | mesmos limites (429 no 11º); `email` duplicado -> 400; corpo grande -> 413 | IT-03 |
| Chave mestra | rotação com chave anterior; chave ausente ou errada fora de Development | rotação preserva sessão e campos; ausente/errada -> API não sobe | IT-08 |
| Bloqueio antes do limite | 6ª tentativa após 5 falhas de um e-mail existente | 423 (contrato SPEC-0006), não 429 | IT-03 |
| Cifra de campo | valor protegido gravado | coluna no banco não contém o texto claro; leitura devolve o original | UT-02, IT-04 |
| Propósito errado / adulteração | decifrar com propósito diferente ou texto alterado | CryptographicException | UT-03 |
| Reinício | container reiniciado | sessão e dados cifrados continuam válidos | IT-05 |
| Atrás do proxy | requisição com `X-Forwarded-Proto: https` vinda da rede do Compose | `Request.IsHttps` verdadeiro; cookie antiforgery com `Secure` | IT-06 |
| Rotas da SPA vs. API | `GET /health/*` e `GET /api/*` inexistente com `index.html` no web root | health responde 200 sem login; API inexistente dá 404 JSON, nunca o `index.html` | IT-07 |
| Contrato de auth | endpoints SPEC-0006@1 | login/registro funcionam sob CSP, antiforgery e rate limit | CT-01 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — projeto novo.

### 7.2 Testes Unitários
- **UT-01** — Dado a lista de endpoints do host (`EndpointDataSource`) e uma rota de amostra sem política, quando a regra "autorização por padrão" roda, então falha apontando a rota; sem a rota de amostra, passa.
- **UT-02** — Dado um `IFieldProtector`, quando `Protect("token-123","ml-token")` é chamado, então o resultado difere do texto claro, `Unprotect` devolve `"token-123"`, e o valor protegido não aparece em `ToString()` da entidade.
- **UT-03** — Dado um texto protegido, quando decifrado com propósito diferente ou com um byte alterado, então lança `CryptographicException`; e 1.000 operações de cifra+decifra têm p95 < 1 ms.

### 7.3 Testes de Integração
- **IT-01** — Dado a API, quando cada rota protegida conhecida é chamada sem cookie, então 401; e cada rota da lista anônima responde sem exigir login.
- **IT-02** — Dado a API em ambiente `Production` simulado, quando `GET /` e `GET /api/v1/me`, então as respostas trazem todos os cabeçalhos do contrato (incluindo HSTS) e não trazem `Server`.
- **IT-03** — Dado a API, quando 11 POST de cadastro/login do mesmo IP chegam em um minuto, então o 11º recebe 429 com `Retry-After` e `GET /antiforgery` do mesmo IP não é limitado; e 11 tentativas de login para o mesmo e-mail (inexistente) de IPs diferentes resultam em 429 na 11ª; e, para um e-mail existente, a 6ª tentativa após 5 falhas continua 423 (contrato da SPEC-0006). Regressões: o mesmo vale com `/login/`, `/register/` e caixa diferente; JSON com `email` duplicado responde 400; corpo acima de 4 KB responde 413; corpo com BOM UTF-8 conta no mesmo limite por e-mail; JSON que o limitador não consegue ler responde 400 (falha fechada); e-mails em formas Unicode equivalentes (composta/decomposta) caem na mesma partição.
- **IT-04** — Dado uma entidade de teste com campo `IsProtected`, quando gravada no Postgres real, então a coluna lida por SQL cru não contém o texto claro e a leitura via EF devolve o original.
- **IT-05** — Dado sessão ativa e um campo cifrado gravado, quando o host é reiniciado com a mesma chave mestra, então a sessão continua válida e o campo é decifrado.
- **IT-06** — Dado a API configurada para confiar só na rede do proxy, quando chega `GET /api/v1/auth/antiforgery` com `X-Forwarded-Proto: https` de um IP confiável, então o cookie antiforgery sai com `Secure`; e o mesmo cabeçalho vindo de IP não confiável é ignorado.
- **IT-08** — Dado chaves do Data Protection gravadas com a chave mestra A, quando o host sobe com A como anterior e B como atual e `--migrate` roda, então campos e sessão antigos continuam válidos só com B; e fora de Development, sem chave mestra ou com uma chave que não decifra nenhuma chave do anel, o host não sobe e a mensagem diz o motivo sem expor a chave.
- **IT-07** — Dado um `index.html` de teste no web root, quando `GET /health/live`, `GET /health/ready` (anônimos) e `GET /api/v1/nao-existe`, então os health checks respondem 200 sem login pelo metadado `AllowAnonymous` e a rota de API inexistente responde 404 sem devolver o `index.html`.

### 7.4 Testes de Contrato
- **CT-01** — Dado o contrato SPEC-0006@1, quando cadastro e login são executados pelo cliente gerado da SPA sob as novas políticas (CSP, rate limit, antiforgery), então os códigos de resposta continuam os do contrato (201/204/401/409/423).

### 7.5 Testes E2E
- N/A — `user_facing: false`: nenhuma jornada muda; o E2E de acesso da SPEC-0006 roda sob a CSP nova no CI e no deploy e falha se a CSP quebrar a SPA.

### 7.6 Outros
- Varredura de cabeçalhos com `curl -I` scriptado no ambiente local prod-like (G6); Mozilla Observatory em staging na SPEC-0009.

**Dublês e dados de teste:** entidade de teste só no projeto de testes; chave mestra de teste gerada por execução.

**Ambiente de execução:** local + CI (Testcontainers); varredura de cabeçalhos em staging.

## 8. Plano de Rollout
- **Estratégia:** deploy direto — ainda sem clientes.
- **Dados/schema:** nova tabela de chaves do Data Protection (expand); sem dado prévio.
- **Compatibilidade:** CSP pode quebrar scripts de terceiros na SPA — coberto pelo E2E da SPEC-0006 rodando sob a CSP.
- **Observabilidade:** contagem de respostas 429 e 401 nas métricas de requisição (SPEC-0007).
- **Rollback:** redeploy da tag anterior; a tabela de chaves permanece (sem efeito na versão anterior).
- **Etapas de migração/coexistência:** N/A

## 9. Questões em Aberto
Nenhuma

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Cifra de campos e autorização por padrão**
- [x] Red: escrever UT-01, UT-02, UT-03 com a tag `SPEC-0008:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: `IFieldProtector` + conversor EF `IsProtected`, chaves do Data Protection no PostgreSQL, `FallbackPolicy` e lista de endpoints anônimos
- [x] Refactor mantendo tudo verde

**Fase 2: Cabeçalhos, limites e contrato**
- [x] Red: escrever IT-01..IT-07 e CT-01 e confirmar que falham pelo motivo certo
- [x] Green: cabeçalhos de segurança, HSTS em produção, rate limiting de auth, chave mestra por ambiente + runbook de rotação
- [x] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração + CI verde (G5) e aprovação (H2)
- [x] Deploy no ambiente local prod-like com smoke; varredura de cabeçalhos em staging na SPEC-0009 (G6)
- [x] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 4602cbe (árvore suja) | 2026-10-13 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — 890a444 | 2026-10-28 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  370ms (environment 59%, import 18%, transform 17%, tests 4%, worker 2%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (Coverage: 92%) — 1458fee | 2026-10-28 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 170ms) — 1458fee | 2026-10-28 |
| G4 Review | PASS | MANUAL: Re-review em 1458fee: M1 (barra final), M2 (email duplicado), M3 (BOM) e m1–m4 fechados; probes repetidos com variações de caminho, charset, Content-Type e forma do corpo — 0 contornos; nits aceitos como pendência (email nulo sem partição por e-mail; elementos <revocation> no check do anel); verify PASS 12/12; dotnet 139/139; lint/build/audit/vulnerable ok; gitleaks 0 | 2026-10-28 |
| G5 Integração & CI | PASS | MANUAL: PR #33 (sdd/int-SPEC-0008 @ d793184): CI 7/7 verde + CodeQL (runs/40806468786); local no integrado: build 0 avisos, format/lint ok, dotnet 139/139, vitest 6/6, e2e 30/30, npm audit high 0 | 2026-10-28 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-28 |
| G6 Deploy | PASS | smoke_test exit 0 (2 passed (3.5s)) — d793184 (árvore suja) | 2026-10-29 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — 443e943 (árvore suja) | 2026-10-29 |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
<!-- comportamento entregue do ponto de vista do usuário/sistema -->
Base de segurança antes do primeiro dado de cliente: autorização negada por padrão (só cadastro, login, token antiforgery, health checks e a SPA são anônimos), guardada por teste de arquitetura; cabeçalhos de segurança em toda resposta (CSP estrita, HSTS fora de Development, X-Frame-Options, nosniff, Referrer-Policy, Permissions-Policy, sem `Server`); limite de 10/min por IP nos POST de cadastro/login e de 10/min por e-mail no login (429 + `Retry-After`, depois do bloqueio 423 da SPEC-0006), fechado contra variações de rota, caixa, corpo e codificação; `IFieldProtector` (AES-256-GCM via Data Protection, propósito por campo) com conversor EF `IsProtected`, chaves persistidas no PostgreSQL e cifradas pela chave mestra do ambiente, com rotação (`PreviousMasterKey` + `--migrate`) e recusa de subida com chave ausente ou errada; forwarded headers só da rede do Caddy; health checks anônimos por metadado.

### Como foi feito
<!-- decisões de implementação, módulos/arquivos principais, desvios e emendas (com versão), dívidas assumidas -->
Projeto novo `src/Common/Vitrine.Common.Security` (`IFieldProtector`, `IsProtected`); host em `src/Api/Security/` (`AuthRateLimiting` com middleware que lê até 4 KB do corpo e decide pelo endpoint roteado, `SecurityHeaders`, `DataProtectionMasterKey`/`MasterKeyXml`, `DataProtectionKeyRingCheck`, forwarded headers); `FallbackPolicy` + `AllowAnonymous` nos endpoints de ServiceDefaults e Identity. Pacote novo: `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` 10.0.13 (ADR-0006). Compose/`.env.example` com `DATA_PROTECTION_MASTER_KEY` (valor sintético local), `PROXY_SUBNET` e `RATE_LIMIT_*`; runbook `docs/runbooks/rotacao-chave-mestra.md`; `web/vite.config.ts` sem fontes `data:` para a CSP estrita. Emendas v1, v1.1, v2, v3, v4 e v4.1 (ver seção 15). Review: FAIL, FAIL e APPROVED na 3ª rodada (três contornos do limite reproduzidos e fechados). Alerta CodeQL `cs/sensitive-data-transmission` no teste `AuthRateLimitBypassTests` (senha falsa contra o TestServer) dispensado como "used in tests" a pedido do dono do repositório.

### Prova de Correção
<!-- type fix: o teste de regressão falhou antes da correção (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->
N/A — type foundation.

### Verificação
<!-- Uma linha por teste do plano (todos os IDs da seção 7). Resultado: PASS. Evidência: execução de CI, commit ou relatório. -->
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| UT-01 | Endpoint sem política nem na lista anônima falha o teste de arquitetura | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943) |
| UT-02 | Protect/Unprotect com propósito; texto claro fora de ToString() | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943) |
| UT-03 | Propósito errado ou byte alterado → CryptographicException; p95 < 1 ms em 1.000 operações | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943) |
| IT-01 | Rotas protegidas sem cookie → 401; lista anônima sem login | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943) |
| IT-02 | Todos os cabeçalhos do contrato (com HSTS em Production) e sem Server | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943); G6: `curl -I` no ambiente local com CSP, HSTS, X-Frame-Options, nosniff, Referrer/Permissions-Policy |
| IT-03 | 429 + Retry-After no 11º POST por IP e no 11º login por e-mail; 423 antes; regressões de barra final, caixa, email duplicado (400), corpo > 4 KB (413), BOM, JSON ilegível (400) e NFC/NFD | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943) |
| IT-04 | Coluna cifrada no Postgres sem o texto claro; EF devolve o original | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943) |
| IT-05 | Reinício com a mesma chave mestra mantém sessão e campo cifrado | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943) |
| IT-06 | X-Forwarded-Proto só da rede confiável; antiforgery com Secure | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943) |
| IT-07 | /health/* 200 anônimo por metadado; /api inexistente 404 sem index.html | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943) |
| IT-08 | Rotação com PreviousMasterKey + --migrate; chave ausente/errada impede a subida sem expor a chave | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943) |
| CT-01 | Cadastro/login pelo cliente gerado mantêm 201/204/401/409/423 sob CSP, antiforgery e rate limit | PASS | CI PR #33 run 40806468786 (d793184); main run 40933777578 (443e943); G6: smoke @smoke 2/2 (login sob CSP) |

### Definição de Pronto
- [x] Todos os testes do plano passando e listados na Verificação
- [x] Todo comportamento do Mapa de Comportamentos coberto e verificado
- [x] Suíte completa, arquitetura e CI verdes no resultado integrado (G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado
- [x] Requisitos não-funcionais medidos com evidência (ou N/A justificado) — cifra p95 < 1 ms (UT-03), OWASP A01/A02/A05/A07 (IT-01..03, UT-01..02, G4), reinício (IT-05)
- [x] Disponível no ambiente-alvo via pipeline, com smoke/E2E passando no ambiente (G6)
- [x] Observabilidade e rollback prontos conforme o Plano de Rollout — 401/429 nas métricas e logs da SPEC-0007; rollback = redeploy da tag anterior (o anel no banco é compatível)
- [x] Documentação raiz e CHANGELOG atualizados (G7)
- [x] Pendências registradas como novas specs (ou nenhuma)

### Deploy
<!-- ambiente(s), versão/tag, data, estratégia, estado da feature flag, execução do pipeline -->
Ambiente local prod-like: imagem `ghcr.io/thomasravache/vitrine-api:443e943` (commit de merge do PR #33) da CI da `main` (run 40933777578) via `deploy/deploy.sh`, health 200 em 3 s, chave mestra sintética do `.env.example`, smoke `@smoke` 2/2 e `curl -I` com todos os cabeçalhos do contrato (2026-10-29). Staging/produção com chave mestra real na SPEC-0009.

### Pendências
<!-- specs criadas para o que ficou de fora, ou "Nenhuma" -->
- SPEC-0014 — login sem e-mail responde 400 antes do Identity; revogações no anel não impedem a subida (nits do G4).
- SPEC-0009 (emenda v1.2 pendente, a revisar juntos no go-live) — `DATA_PROTECTION_MASTER_KEY` real no `DEPLOY_ENV_FILE` de staging/produção com cópia no cofre; `PROXY_SUBNET`/`RATE_LIMIT_*` por ambiente; Mozilla Observatory em staging.

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0008`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v4.1 (contrato v4, esclarecimento) | 2026-10-29 | Limitador falha fechado (JSON ilegível -> 400), aceita BOM e normaliza o e-mail como o Identity; regressões no IT-03 | Re-review do G4: BOM UTF-8 escapava do limite por e-mail (reproduzido) | nenhuma | thomas (2026-10-29, piloto automático) |
| v4 | 2026-10-29 | Limite decidido pelo endpoint roteado; `email` duplicado -> 400; teto de 4 KB no corpo de login/cadastro (413); chave mestra errada fora de Development impede a subida; IT-03 com regressões e IT-08 novo (rotação, chave ausente/errada); runbook de rotação sem corrida | G4 CHANGES_REQUESTED: barra final e `email` duplicado contornavam os limites (reproduzido), corpo lido sem teto, rotação/chave sem teste, chave errada subia em silêncio, corrida no runbook | nenhuma | thomas (2026-10-29, piloto automático) |
| v3 | 2026-10-29 | Limite por IP só nos POST de cadastro/login (GET /antiforgery e logout fora); isenção de loopback em Development; rotação com `DataProtection__PreviousMasterKey`; chave mestra obrigatória fora de Development; `web/vite.config.ts` no escopo para não embutir fontes como `data:` (CSP estrita mantida) | Implementer: o smoke do deploy (2 projetos Playwright) estourava 10/min porque a SPA busca o token antiforgery antes de cada POST — mesmo risco para usuários atrás de CGNAT; fonte embutida bloqueada pela CSP; rotação sem chave anterior perderia os dados cifrados | nenhuma | thomas (2026-10-29, piloto automático) |
| v2 | 2026-10-29 | Limite de login por e-mail 5/min → 10/min, com o bloqueio da SPEC-0006 (423) antes dele; chaves de configuração formalizadas (`DataProtection__MasterKey` Base64 de 32 bytes, `RateLimiting__*`, `ForwardedHeaders__KnownNetworks__0`, origem do Sentry na CSP a partir de `Sentry__Dsn`); IT-03 ajustado | SPEC_DEFECT do Test-writer: 5/min por e-mail e o bloqueio de 5 falhas disparavam juntos na 6ª tentativa (429 × 423), quebrando o contrato SPEC-0006@1 | nenhuma (SPEC-0006 mantém o 423) | thomas (2026-10-29, piloto automático) |
| v1.1 (escopo, sem mudança de contrato) | 2026-10-29 | `touches` alinhado ao código real: projeto novo `src/Common/Vitrine.Common.Security` (+ `Vitrine.slnx`), rate limit e antiforgery onde já estão (`Identity.Presentation`/`Infrastructure`, `Api/Program.cs`), csproj/appsettings da API, suporte e csproj de testes, `.env.example`/override do Compose (chave mestra), lockfiles e `.gitleaksignore` (valores falsos de teste); pacote `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` 10.0.13 registrado no ADR-0006; seção 7.5 deixa de citar ID de teste de outra spec | A spec foi escrita antes do módulo Identity e de qualquer projeto em `src/Common` existirem | — | thomas (2026-10-29, piloto automático) |
| v1 (escopo, sem mudança de contrato) | 2026-10-14 | Inclui forwarded headers atrás do Caddy com antiforgery `Secure=Always`, e health checks anônimos por metadado; touches ganham `src/ServiceDefaults/**` e `deploy/compose.yaml` | Review G4 da SPEC-0006 (desvios aceitos lá para serem fechados aqui); testes IT-06 e IT-07 | — | thomas (2026-10-14) |
